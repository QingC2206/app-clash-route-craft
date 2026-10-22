using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed class EditableDocument
{
    public string FilePath { get; }
    public string Baseline { get; private set; }
    public string Text { get; private set; }
    public bool IsReadOnly { get; }
    public bool Dirty => Text != Baseline;
    public EditableDocument(string path) { FilePath = path; Baseline = Text = File.ReadAllText(path); }
    public JsonNode? Parse() => YamlData.Parse(Text);
    public EditableDocument(string path, string text, bool readOnly) { FilePath = path; Baseline = Text = text; IsReadOnly = readOnly; }
    void CheckWritable() { if (IsReadOnly) throw new BuildException("Example 是只读示例，请先新增或复制项目"); }
    public void Set(JsonNode? data) { CheckWritable(); Text = YamlEditing.Apply(Text, data); }
    public void SetText(string text) { CheckWritable(); Text = text; }
    public void Accept() => Baseline = Text;
}

public enum OutputFreshness { Missing, InputsChanged, ValidationChanged, Current }

public sealed class WorkspaceSession
{
    public const string ExampleName = "Example";
    public static bool IsExample(string name) => name.Equals(ExampleName, StringComparison.OrdinalIgnoreCase);
    public bool IsReadOnly => IsExample(ProjectName);
    public string Workspace { get; }
    public string ProjectName { get; }
    public string ProjectPath => Path.Combine(Workspace, "config", ProjectName);
    public string OutputPath => Path.Combine(Workspace, "output", ProjectName);
    public Dictionary<string, EditableDocument> Documents { get; } = [];
    public SubscriptionState Subscriptions { get; private set; }
    bool pendingSourceReferences;
    public bool Dirty => Documents.Values.Any(x => x.Dirty);
    public WorkspaceSession(string workspace, string project)
    {
        Workspace = Path.GetFullPath(workspace); ProjectName = FileName(project);
        foreach (string name in new[] { "settings.yaml", "residential.yaml", "policy.yaml" })
        {
            string path = Path.Combine(ProjectPath, name);
            if (IsReadOnly) { Documents[name] = new(path, ExampleDocuments(Workspace)[name], true); continue; }
            if (!File.Exists(path)) throw new BuildException($"项目缺少 {name}");
            Documents[name] = new(path);
        }
        JsonObject? settings = null; try { settings = Settings; } catch (BuildException) { }
        Subscriptions = IsReadOnly ? SubscriptionState.Empty(ProjectPath, settings!) : SubscriptionState.Load(ProjectPath, settings, true);
    }
    public string SnapshotPath(string name) => Subscriptions.SnapshotPath(name);
    public void ReloadSubscriptionState() => Subscriptions = SubscriptionState.Load(ProjectPath, Settings);
    public void RenameSource(int index, string newName)
    {
        newName = FileName(newName.Trim(), "订阅名称"); var settings = Settings; var sources = L(settings, "sources");
        var source = Map(sources[index], "订阅"); string oldName = S(source, "name");
        if (sources.Where((_, i) => i != index).Any(x => S(Map(x, "订阅"), "name").Equals(newName, StringComparison.OrdinalIgnoreCase))) throw new BuildException("订阅名称不能重复");
        var policy = ProjectReferences.RewritePolicy(Policy, x => ProjectReferences.SourceReference(x, oldName, newName));
        string policyText = YamlEditing.Apply(Documents["policy.yaml"].Text, policy);
        source["name"] = newName;
        string settingsText = YamlEditing.Apply(Documents["settings.yaml"].Text, settings);
        if (IsReadOnly) throw new BuildException("Example 是只读示例，请先新增或复制项目");
        pendingSourceReferences |= policyText != Documents["policy.yaml"].Text;
        Documents["settings.yaml"].SetText(settingsText); Documents["policy.yaml"].SetText(policyText); Subscriptions.Rename(oldName, newName);
    }
    public void RenameResidence(int index, string newName)
    {
        newName = Name(JsonValue.Create(newName.Trim()), "住宅名称");
        var nodes = Residences; string oldName = S(Map(nodes[index], "住宅"), "name");
        if (nodes.Where((_, i) => i != index).Any(x => S(Map(x, "住宅"), "name") == newName)) throw new BuildException("住宅名称不能重复");
        var policy = ProjectReferences.RewritePolicy(Policy, x => x == oldName ? newName : x);
        string policyText = YamlEditing.Apply(Documents["policy.yaml"].Text, policy);
        Map(nodes[index], "住宅")["name"] = newName;
        SetResidences(nodes); Documents["policy.yaml"].SetText(policyText);
    }
    public void RemoveSource(int index)
    {
        var settings = Settings; string name = S(Map(L(settings, "sources")[index], "订阅"), "name");
        ProjectReferences.RewritePolicy(Policy, x => ProjectReferences.SourceReference(x, name, "__removed__") != x ? throw new BuildException("规则或 DNS 仍引用此订阅，请先调整引用后再删除") : x);
        L(settings, "sources").RemoveAt(index); ProjectOrdering.NumberSources(settings); Documents["settings.yaml"].Set(settings);
    }
    public void RemoveResidence(int index)
    {
        var nodes = Residences; string name = S(Map(nodes[index], "住宅"), "name");
        ProjectReferences.RewritePolicy(Policy, x => x == name ? throw new BuildException("规则或 DNS 仍引用此住宅，请先调整引用后再删除") : x);
        nodes.RemoveAt(index); SetResidences(nodes);
    }
    public string NextResidenceName()
    {
        var names = Residences.Select(x => S(Map(x, "住宅"), "name")).ToHashSet();
        int number = 1; while (names.Contains("住宅-" + number)) number++;
        return "住宅-" + number;
    }
    public JsonObject Settings => Map(Documents["settings.yaml"].Parse(), "settings");
    public JsonObject Policy => Map(Documents["policy.yaml"].Parse(), "policy");
    public JsonArray Residences
    {
        get { var value = Documents["residential.yaml"].Parse(); return List(value is JsonObject map ? map["proxies"] : value, "住宅节点"); }
    }
    public void SetResidences(JsonArray residences)
    {
        var value = Documents["residential.yaml"].Parse();
        if (value is JsonObject map) { map["proxies"] = residences.DeepClone(); Documents["residential.yaml"].Set(map); }
        else Documents["residential.yaml"].Set(residences);
    }
    public void Save(IReadOnlyList<(string Path, byte[] Bytes)>? importedSnapshots = null)
        => SaveDocuments(Documents.Values.ToArray(), importedSnapshots);
    internal void SaveSubscriptions(IReadOnlyList<(string Path, byte[] Bytes)> snapshots, IReadOnlyDictionary<string, string> expectedSnapshots, CancellationToken cancellation)
    {
        CheckSubscriptionSave(); SaveDocuments([Documents["settings.yaml"]], snapshots, expectedSnapshots, cancellation);
    }
    internal void CheckSubscriptionSave()
    {
        if (pendingSourceReferences) throw new BuildException("订阅重命名已同步规则/DNS 引用，请先保存项目后再拉取订阅");
    }
    void SaveDocuments(IReadOnlyList<EditableDocument> documents, IReadOnlyList<(string Path, byte[] Bytes)>? importedSnapshots, IReadOnlyDictionary<string, string>? expectedSnapshots = null, CancellationToken cancellation = default)
    {
        if (IsReadOnly) throw new BuildException("Example 是只读示例，请先新增或复制项目");
        using var lease = WorkspaceLease.Acquire(Workspace, true);
        foreach (var doc in documents)
        {
            doc.Parse();
            if (File.ReadAllText(doc.FilePath) != doc.Baseline) throw new BuildException($"{Path.GetFileName(doc.FilePath)} 已被外部修改，请重新载入项目后再保存");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var settings = Settings; ProjectOrdering.NumberSources(settings); Documents["settings.yaml"].Set(settings);
        foreach (var raw in L(Settings, "sources"))
        {
            var source = Map(raw, "订阅"); string name = FileName(Name(source["name"], "订阅"));
            if (!names.Add(name)) throw new BuildException("订阅名称不能重复");
            I(source, "priority", 1);
        }
        foreach (var expected in expectedSnapshots ?? new Dictionary<string, string>())
            if (DependencySnapshot.FileHash(expected.Key) != expected.Value) throw new BuildException("拉取期间节点缓存已被外部修改，本次结果未保存，请重试");
        var changed = documents.Where(x => x.Dirty).Select(x => (x.FilePath, Encoding.UTF8.GetBytes(x.Text))).ToList();
        Subscriptions.CheckExternalChanges(); Subscriptions.Reconcile(settings);
        if (Subscriptions.Text != Subscriptions.Baseline) changed.Add((Path.Combine(ProjectPath, SubscriptionState.FileName), Subscriptions.Bytes));
        if (importedSnapshots is not null) changed.AddRange(importedSnapshots);
        FileBundle.Commit(changed, cancellation);
        Subscriptions.Accept();
        foreach (var doc in documents) doc.Accept();
        if (documents.Contains(Documents["policy.yaml"])) pendingSourceReferences = false;
    }
    public JsonObject? ReadReport()
    {
        string path = Path.Combine(OutputPath, "HomeChain.report.json");
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }
    public bool OutputIsCurrent() => OutputStatus() == OutputFreshness.Current;
    public string ReadValidatedOutput(string filename)
    {
        if (filename is not "HomeChain.yaml" and not "HomeChain.MC2DNS.yaml") throw new BuildException("未知输出文件");
        var report = ReadReport();
        if (report is null || S(report, "core_validation") != "passed" || !OutputIsCurrent()) throw new BuildException("配置或输出已有变化，请重新生成并校验后再复制");
        byte[] bytes = File.ReadAllBytes(Path.Combine(OutputPath, filename));
        string expected = filename == "HomeChain.yaml" ? S(report, "main_sha256") : S(M(report, "dns"), "mc2_sha256");
        if (expected.Length == 0 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != expected || !OutputIsCurrent())
            throw new BuildException("输出文件已变化，本次未复制，请重新生成");
        return Encoding.UTF8.GetString(bytes);
    }
    public OutputFreshness OutputStatus()
    {
        if (Dirty) return OutputFreshness.InputsChanged;
        var saved = ReadReport(); string main = Path.Combine(OutputPath, "HomeChain.yaml");
        if (saved is null || !File.Exists(main)) return OutputFreshness.Missing;
        if (I(saved, "dns_artifacts_version", 0) != DnsPrivacy.ArtifactVersion) return OutputFreshness.InputsChanged;
        if (I(saved, "hosts_compiler_version", 0) != SubscriptionHosts.Version) return OutputFreshness.InputsChanged;
        if (I(saved, "project_ordering_version", 0) != ProjectOrdering.Version || I(saved, "geo_rules_version", 0) != GeoRules.Version || I(saved, "rule_compiler_version", 0) != Rules.Version) return OutputFreshness.InputsChanged;
        if (SubscriptionBaseline.Enabled(Policy) && I(saved, "subscription_baseline_version", 0) != SubscriptionBaseline.Version) return OutputFreshness.InputsChanged;
        string mainHash = S(saved, "main_sha256"), dnsHash = S(M(saved, "dns"), "mc2_sha256");
        if (mainHash.Length == 0 || DependencySnapshot.FileHash(main) != mainHash || dnsHash.Length > 0 && DependencySnapshot.FileHash(Path.Combine(OutputPath, "HomeChain.MC2DNS.yaml")) != dnsHash) return OutputFreshness.InputsChanged;
        if (S(saved, "input_fingerprint") != InputFingerprint.Compute(ProjectPath) || !DependencySnapshot.RuleCachesCurrent(ProjectPath, saved)) return OutputFreshness.InputsChanged;
        return DependencySnapshot.ValidationCurrent(this, saved) ? OutputFreshness.Current : OutputFreshness.ValidationChanged;
    }
    public static void CreateProject(string workspace, string name, string? copyFrom = null)
    {
        name = ProjectStore.ValidateName(name);
        workspace = Path.GetFullPath(workspace);
        using var lease = WorkspaceLease.Acquire(workspace, true);
        ProjectStore.CheckDestination(workspace, name);
        string config = Path.Combine(Path.GetFullPath(workspace), "config"), destination = Path.Combine(config, name);
        if (Directory.Exists(destination) || Directory.Exists(config) && Directory.GetDirectories(config).Any(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase))) throw new BuildException("同名项目已存在");
        Directory.CreateDirectory(config);
        string staging = Path.Combine(config, ".new-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "subscrip"));
            if (copyFrom is not null && !IsExample(copyFrom))
            {
                var source = new WorkspaceSession(workspace, copyFrom);
                foreach (var (file, doc) in source.Documents) File.WriteAllText(Path.Combine(staging, file), doc.Text);
                string stateFile = Path.Combine(source.ProjectPath, SubscriptionState.FileName);
                if (File.Exists(stateFile)) File.Copy(stateFile, Path.Combine(staging, SubscriptionState.FileName));
                string snapshots = Path.Combine(source.ProjectPath, "subscrip");
                if (Directory.Exists(snapshots)) foreach (string file in Directory.GetFiles(snapshots, "*.yaml")) File.Copy(file, Path.Combine(staging, "subscrip", Path.GetFileName(file)));
                ProjectReferences.CopyLocalRules(source, staging);
            }
            else
            {
                foreach (var (file, text) in ExampleDocuments(workspace)) File.WriteAllText(Path.Combine(staging, file), text);
            }
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static IReadOnlyDictionary<string, string> ExampleDocuments(string? workspace = null)
    {
        var files = new Dictionary<string, string>(BuiltInDefaults.Documents());
        if (workspace is not null && File.Exists(RuleDefaults.FilePath(workspace)))
            files["policy.yaml"] = YamlData.Write(RuleDefaults.Apply(Map(YamlData.Parse(files["policy.yaml"]), "policy"), RuleDefaults.Read(workspace)));
        return files;
    }

}

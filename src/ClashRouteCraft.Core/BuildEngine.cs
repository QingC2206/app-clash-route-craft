using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed class BuildEngine
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static IReadOnlyList<string> DiscoverProjects(string workspace)
    {
        string directory = Path.Combine(Path.GetFullPath(workspace), "config");
        return !Directory.Exists(directory) ? [] : Directory.GetDirectories(directory).Where(x => !WorkspaceSession.IsExample(Path.GetFileName(x)) && !Path.GetFileName(x).StartsWith('.') && !Path.GetFileName(x).Equals("examples", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(x, "settings.yaml"))).Select(x => Path.GetFileName(x)!).Order(StringComparer.Ordinal).ToArray();
    }
    public static JsonObject ProjectSettings(string project, string workspace, ValidationPaths? resources = null)
    {
        var settings = ConfigurationValidation.EffectiveSettings(Map(YamlData.Read(Path.Combine(project, "settings.yaml")), "settings"), workspace, resources);
        ProjectOrdering.NumberSources(settings);
        settings["http"] = Merge(M(settings, "http"), O("cache_dir", Path.Combine(workspace, ".cache", Path.GetFileName(project))));
        settings["_project_ordering_version"] = ProjectOrdering.Version;
        return settings;
    }
    public async Task<BuildResult> BuildProjectAsync(string workspace, string projectName, BuildOptions? options = null, IProgress<BuildProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (WorkspaceSession.IsExample(projectName)) throw new BuildException("Example 是只读示例，请先新增或复制项目");
        options ??= new(); workspace = Path.GetFullPath(workspace); FileName(projectName, "项目名称");
        cancellation.ThrowIfCancellationRequested();
        using var workspaceLease = WorkspaceLease.Acquire(workspace);
        if (projectName.Equals("examples", StringComparison.OrdinalIgnoreCase)) throw new BuildException("examples 目录不作为项目生成");
        string project = Path.Combine(workspace, "config", projectName);
        if (!File.Exists(Path.Combine(project, "settings.yaml"))) throw new BuildException("项目 settings.yaml 不存在");
        var gate = ProjectLocks.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellation)) throw new BuildException("当前项目已有生成任务，请等待完成");
        string temporary = Path.Combine(Path.GetTempPath(), "homechain-subscriptions-" + Guid.NewGuid().ToString("N"));
        try
        {
            string cache = Path.Combine(workspace, ".cache", projectName); Directory.CreateDirectory(cache);
            FileStream fileLock;
            try { fileLock = new FileStream(Path.Combine(cache, ".build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new BuildException("当前项目已在另一个进程中生成"); }
            using var heldLock = fileLock;
            Directory.CreateDirectory(temporary);
            progress?.Report(new("检查配置", "检查项目、筛选表达式及依赖", 0));
            string initialFingerprint = InputFingerprint.Compute(project);
            var settings = ProjectSettings(project, workspace, options.Validation);
            var rawSettings = Map(YamlData.Read(Path.Combine(project, "settings.yaml")), "settings");
            var subscriptionState = SubscriptionState.Load(project, rawSettings);
            settings["_input_fingerprint"] = initialFingerprint;
            var currentSession = new WorkspaceSession(workspace, projectName);
            var failures = ParameterInspection.Inspect(currentSession, cancellation, options, checkDependencies: !options.InspectAsnOnly).Where(x => x.State == "fail").ToArray();
            if (failures.Length > 0) throw new BuildException(string.Join("；", failures.Select(x => x.Title + "：" + x.Message)));
            M(settings, "http")["prefer_cache"] = !options.RefreshRules;
            var fetchOptions = Merge(M(settings, "http"), O("cache_dir", Path.Combine(temporary, "cache"), "allow_stale", false, "prefer_cache", false, "strict_subscriptions", true));
            using var fetch = new Fetcher(project, fetchOptions, options.Offline, cancellation, useCache: false);
            var extra = new List<(string, byte[])>(); var snapshots = new JsonArray();
            progress?.Report(new("订阅快照", options.Force ? "刷新在线订阅，保留本地导入节点" : "使用已有缓存，仅获取缺少缓存的订阅", 1));
            foreach (var raw in L(settings, "sources"))
            {
                cancellation.ThrowIfCancellationRequested(); var source = Map(raw, "source"); if (!B(source, "enabled", true)) continue;
                string name = S(source, "name"), snapshot = subscriptionState.SnapshotPath(name);
                if (ConfigurationValidation.Download(currentSession, source, options))
                {
                    try
                    {
                        var document = await SubscriptionUpdater.DownloadDocumentAsync(fetch, source, name, !SubscriptionBaseline.Enabled(currentSession.Policy) || SubscriptionBaseline.IsPrimary(settings, name));
                        if (SubscriptionBaseline.Enabled(currentSession.Policy) && SubscriptionBaseline.IsPrimary(settings, name)) SubscriptionBaseline.RequireRules(document, name);
                        var nodes = L(document, "proxies");
                        if (SubscriptionBaseline.Enabled(currentSession.Policy)) SubscriptionBaseline.Eligible(nodes, source, M(settings, "selection"));
                        else Nodes.Eligible(Nodes.Namespace(nodes, name), source, M(settings, "selection"));
                        byte[] payload = SubscriptionUpdater.Payload(document);
                        string staged = Path.Combine(temporary, name + ".yaml"); FileBundle.AtomicWrite(staged, payload); extra.Add((snapshot, payload)); source["file"] = staged;
                        subscriptionState.MarkUpdated(Map(L(rawSettings, "sources").First(x => S(Map(x, "订阅"), "name") == name), "订阅"), "download");
                    }
                    catch (BuildException ex)
                    {
                        if (!File.Exists(snapshot)) throw new BuildException($"{name} 拉取失败且没有缓存：{ex.Message}");
                        try { SubscriptionUpdater.Cached(snapshot, name); }
                        catch (BuildException) { throw new BuildException($"{name} 拉取失败且现有缓存不可用：{ex.Message}"); }
                        source["file"] = snapshot;
                        snapshots.Push(O("item", name, "status", "snapshot_fallback", "reason", ex.Message));
                        progress?.Report(new("订阅 · " + name, "拉取失败，已使用现有缓存；" + ex.Message, 1, State: "warning"));
                    }
                }
                else
                {
                    SubscriptionUpdater.Cached(snapshot, name);
                    source["file"] = snapshot; snapshots.Push(O("item", name, "status", "snapshot"));
                    if (currentSession.Subscriptions.UrlChanged(source)) progress?.Report(new("订阅 · " + name, "链接或请求选项已变更，本次使用原有缓存；需要新节点时请拉取订阅或开启强制拉取", 1, State: "warning"));
                }
                source.Remove("url");
            }
            settings["_subscription_fetches"] = A(fetch.Events.Concat(snapshots).Select(x => x?.DeepClone()));
            if (extra.Count > 0 || subscriptionState.Text != subscriptionState.Baseline) extra.Add((Path.Combine(project, SubscriptionState.FileName), subscriptionState.Bytes));
            return await GenerateAsync(Path.Combine(project, "settings.yaml"), Path.Combine(workspace, "output", projectName, "HomeChain.yaml"), settings, extra, options.Offline, progress, cancellation, options.InspectAsnOnly);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); gate.Release(); }
    }
    public async Task<BuildResult> GenerateAsync(string settingsPath, string? output = null, JsonObject? settingsOverride = null, IReadOnlyList<(string Path, byte[] Bytes)>? extraOutputs = null, bool offline = false, IProgress<BuildProgress>? progress = null, CancellationToken cancellation = default, bool inspectAsnOnly = false)
    {
        settingsPath = Path.GetFullPath(settingsPath); string root = Path.GetDirectoryName(settingsPath)!;
        var settings = settingsOverride ?? Map(YamlData.Read(settingsPath), "settings");
        string initialFingerprint = S(settings, "_input_fingerprint", InputFingerprint.Compute(root));
        progress?.Report(new("编译配置", "读取订阅基线，添加住宅出口与附加规则", 2));
        var compiled = await ProfileCompiler.CompileAsync(settings, root, offline, cancellation);
        var configuration = compiled.Configuration; var report = compiled.Report;
        string buildTarget = ConfigurationValidation.Target(settings); report["target"] = buildTarget;
        var asn = M(report, "asn");
        if (inspectAsnOnly) return new("", report);
        AsnPolicy.Enforce(asn);
        var targetConfig = (JsonObject)configuration.DeepClone();
        if (buildTarget == "mc2") targetConfig["geodata-mode"] = true;
        report["target_dependencies"] = O("status", S(asn, "status"), "databases", GeoRules.RequiredDatabases(targetConfig),
            "potential_databases", I(asn, "uninspected_providers", 0) > 0 ? A("ASN.mmdb") : A(),
            "uninspected_providers", I(asn, "uninspected_providers", 0), "router_runtime_tested", false,
            "scope", "静态依赖检查；本机内核校验不验证目标设备是否已安装数据库");
        report["dns_artifacts_version"] = DnsPrivacy.ArtifactVersion;
        report["dependency_version"] = DependencySnapshot.Version;
        var validationInputs = DependencySnapshot.Validation(settings, root, configuration, asn);
        report["validation_inputs"] = validationInputs.DeepClone();
        report["project_ordering_version"] = I(settings, "_project_ordering_version", 0);
        report["input_fingerprint"] = InputFingerprint.Compute(root, extraOutputs);
        if (settings.ContainsKey("_subscription_fetches")) report["subscription_fetches"] = settings["_subscription_fetches"]?.DeepClone();
        string target = Path.GetFullPath(output ?? S(settings, "output", "output/HomeChain.yaml"), root);
        string stem = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target));
        var dns = buildTarget == "mc2" ? ProfileCompiler.Mc2Dns(configuration) : null; string dnsTarget = stem + ".MC2DNS.yaml";
        M(report, "dns")["mc2_file"] = dns is null ? null : dnsTarget;
        string dnsMode = dns is null ? "" : S(M(dns, "dns"), "enhanced-mode");
        M(report, "dns")["mc2_mode"] = dnsMode;
        byte[]? defaultDns = buildTarget == "mc2" ? DnsPrivacy.DefaultBytes() : null;
        byte[]? dnsGuide = buildTarget == "mc2" ? Encoding.UTF8.GetBytes(DnsPrivacy.Guide(Path.GetFileName(stem), dnsMode)) : null;
        if (defaultDns is not null)
        {
            M(report, "dns")["mc2_default_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(defaultDns));
            M(report, "dns")["mc2_default_source"] = DnsPrivacy.OfficialVersion;
        }
        var generator = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(BuildEngine).Assembly;
        string generatorVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(generator)?.InformationalVersion.Split('+')[0]
            ?? generator.GetName().Version?.ToString(3) ?? "unknown";
        string generatedAt = DateTimeOffset.Parse(S(report, "built_at"), System.Globalization.CultureInfo.InvariantCulture)
            .ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        byte[] blob = YamlData.Bytes(configuration, $"# Generated at: {generatedAt}\n# Generated by: ClashRouteCraft {generatorVersion}\n# Generated for Mihomo. Contains private proxy credentials.\n# Edit project inputs, then rebuild.\n");
        byte[]? dnsBlob = dns is null ? null : YamlData.Mc2DnsBytes(dns, $"# MC2 {dnsMode} DNS fragment. Paste the ENTIRE file including dns:.\n# Import the matching main profile first; apply to the matching MC2 DNS mode and restart.\n# Turning the app switch off does NOT restore DNS on the router.\n# Recovery: {Path.GetFileName(stem)}{DnsPrivacy.DefaultSuffix}; instructions: {Path.GetFileName(stem)}{DnsPrivacy.GuideSuffix}\n");
        report["main_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(blob));
        if (dnsBlob is not null) M(report, "dns")["mc2_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(dnsBlob));
        Nodes.CheckGraph(Map(YamlData.Parse(blob), "生成配置"));
        var validation = M(settings, "validation"); string exe = S(validation, "mihomo"); if (exe.Length > 0) exe = Path.GetFullPath(exe, root);
        if (exe.Length == 0 || !File.Exists(exe))
        {
            if (B(validation, "required")) throw new BuildException("需要有效的 Mihomo 校验程序");
            report["core_validation"] = "not_run; structure checked only";
            if (dns is not null) M(report, "dns")["mc2_core_validation"] = "not_run; structure checked only";
        }
        else
        {
            string cache = Path.GetFullPath(S(M(settings, "http"), "cache_dir", Path.Combine(root, ".cache")), root);
            string[] requiredData = AsnPolicy.ValidationFiles(configuration, asn);
            string source = S(validation, "data_dir");
            string dataDirectory = source.Length > 0 ? Path.GetFullPath(source, root) : Path.Combine(cache, "core-data-unconfigured");
            foreach (string file in requiredData)
                if (source.Length == 0 || !File.Exists(Path.Combine(Path.GetFullPath(source, root), file))) throw new BuildException($"内核校验缺少 {file}，请补齐校验数据库；客户端使用原生 GEO 也需要对应数据库。");
            progress?.Report(new("主配置校验", "使用 Mihomo 检查生成配置", 3));
            await CoreValidation.ValidateAsync(configuration, exe, dataDirectory, Path.Combine(cache, "core-check.log"), cancellation, requiredData); report["core_validation"] = "passed";
            if (buildTarget == "mc2") progress?.Report(new("DNS 兼容性", dns is null ? "DNS 由 MC2 管理，本次不生成或覆盖 DNS" : "检查主配置与 MC2 DNS 的组合配置", 4));
            if (dns is not null)
            {
                await CoreValidation.ValidateAsync(Merge(configuration, dns), exe, dataDirectory, Path.Combine(cache, "mc2-dns-check.log"), cancellation, requiredData);
                M(report, "dns")["mc2_core_validation"] = "passed";
            }
            if (buildTarget == "mc2" && S(validation, "mc2_package") is { Length: > 0 } package)
                report["mc2_package_validation"] = await CoreValidation.ValidatePackageAsync(configuration, dns, exe, Path.GetFullPath(package, root), Path.Combine(cache, "mc2-package-check.log"), cancellation, S(report, "geo_mode") == "native" || B(asn, "allow_native") ? dataDirectory : null, requiredData.Where(x => x == "ASN.mmdb").ToArray());
        }
        cancellation.ThrowIfCancellationRequested(); progress?.Report(new("提交输出", "校验结束，统一替换本项目文件", 5));
        if (InputFingerprint.Compute(root) != initialFingerprint) throw new BuildException("生成期间输入配置或快照已被修改，本次结果未提交，请重新生成");
        if (Canonical(validationInputs) != Canonical(DependencySnapshot.Validation(settings, root, configuration, asn)) || !DependencySnapshot.RuleCachesCurrent(root, report, Path.GetFullPath(S(M(settings, "http"), "cache_dir", Path.Combine(root, ".cache")), root))) throw new BuildException("生成期间名单或校验资源已改变，本次结果未提交，请重试");
        foreach (var raw in L(report, "fetches"))
        {
            var fetched = Map(raw, "名单读取");
            if (B(fetched, "stale")) progress?.Report(new("名单缓存", S(fetched, "item") + (S(fetched, "reason") == "offline" ? "：当前离线，使用过期缓存" : "：在线更新失败，使用过期缓存；" + S(fetched, "reason")), 5, State: "warning"));
        }
        var outputs = extraOutputs?.ToList() ?? [];
        if (dnsBlob is not null) outputs.Add((dnsTarget!, dnsBlob));
        if (defaultDns is not null) outputs.Add((stem + DnsPrivacy.DefaultSuffix, defaultDns));
        if (dnsGuide is not null) outputs.Add((stem + DnsPrivacy.GuideSuffix, dnsGuide));
        var tools = RouterAsnTools.Outputs(Path.GetDirectoryName(target)!);
        outputs.AddRange(tools);
        report["router_asn_tools"] = O("version", RouterAsnTools.Version, "directory", RouterAsnTools.DirectoryName,
            "files", A(tools.Select(x => (JsonNode?)O("name", Path.GetFileName(x.Path), "sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(x.Bytes))))));
        outputs.Add((stem + ".report.json", Encoding.UTF8.GetBytes(report.ToJsonString(JsonOptions) + "\n"))); outputs.Add((target, blob));
        var removals = new List<string>();
        if (dnsBlob is null) removals.Add(dnsTarget);
        if (buildTarget != "mc2") { removals.Add(stem + DnsPrivacy.DefaultSuffix); removals.Add(stem + DnsPrivacy.GuideSuffix); }
        FileBundle.Commit(outputs, cancellation, removals: removals);
        progress?.Report(new("生成完成", $"节点 {I(report, "proxy_count", 0)}，规则 {I(report, "rule_count", 0)}；ASN 安装工具：{RouterAsnTools.DirectoryName}/", 6));
        return new(target, report);
    }
}

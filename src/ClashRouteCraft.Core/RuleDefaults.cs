using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Workspace-level routing template; never copies DNS, subscriptions or credentials.</summary>
public static class RuleDefaults
{
    static readonly string[] Keys = ["rule_source", "extra_rules_enabled", "first", "order", "direct", "reject", "proxy", "remote_rules", "supplemental_rules", "rule_sets", "domestic_direct", "final", "geo_mode", "geosite_sources", "asn_sources"];
    public static string FilePath(string workspace) => Path.Combine(Path.GetFullPath(workspace), "settings", "rule-defaults.yaml");
    public static JsonObject Extract(JsonObject policy)
    {
        policy = ImportedRuleSets.Migrate(policy); var result = new JsonObject();
        foreach (string key in Keys) if (policy.ContainsKey(key)) result[key] = policy[key]?.DeepClone();
        return result;
    }
    public static JsonObject Apply(JsonObject policy, JsonObject defaults)
    {
        defaults = Extract(defaults);
        var result = (JsonObject)policy.DeepClone();
        foreach (string key in Keys)
        {
            // Older saved templates never selected a compiler mode or an extras switch.
            if (key is "rule_source" or "extra_rules_enabled" && !defaults.ContainsKey(key)) continue;
            result.Remove(key); if (defaults.ContainsKey(key)) result[key] = defaults[key]?.DeepClone();
        }
        return result;
    }
    public static void Save(string workspace, JsonObject policy)
    {
        var defaults = Extract(policy);
        var portable = new HashSet<string>(Nodes.Reserved) { "默认代理", "住宅出口", "机场入口", "订阅优先", "机场手选" };
        try { ParameterInspection.ValidateRules(defaults, portable); }
        catch (BuildException ex) { throw new BuildException("默认分流配置不可跨项目使用：" + ex.Message + "；请使用默认代理、住宅出口等通用分组"); }
        void CheckSource(JsonObject source)
        {
            if (S(source, "file").Length > 0 || S(source, "fallback_file").Length > 0 || !ConfigurationValidation.HttpUrl(S(source, "url"))) throw new BuildException("默认分流配置不能引用项目本地名单文件，请改用 HTTP(S) 来源");
        }
        foreach (var source in Rules.RemoteSpecs(defaults["remote_rules"])) CheckSource(source);
        foreach (string key in new[] { "geosite_sources", "asn_sources" })
            foreach (var (_, value) in M(defaults, key)) CheckSource(IsString(value) ? O("url", value) : Map(value, key));
        using var lease = WorkspaceLease.Acquire(workspace, true);
        string path = FilePath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileBundle.AtomicWrite(path, Encoding.UTF8.GetBytes(YamlData.Write(defaults)));
    }
    public static JsonObject Read(string workspace)
    {
        string path = FilePath(workspace);
        return Extract(File.Exists(path) ? Map(YamlData.Read(path), "默认分流规则") : Map(YamlData.Parse(WorkspaceSession.ExampleDocuments()["policy.yaml"]), "默认分流规则"));
    }
}

using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>A subscription owns its routing semantics. The builder only adds explicit user extensions.</summary>
public static class SubscriptionBaseline
{
    public const int Version = 4;
    public const string JumpInclude = "(?i)(香港|港|HK|Hong Kong|HongKong|🇭🇰|日本|东京|大阪|JP|Japan|Tokyo|Osaka|🇯🇵|台湾|台灣|台|台北|TW|Taiwan|Taipei|🇹🇼|新加坡|狮城|獅城|SG|Singapore|🇸🇬)";
    public const string JumpExclude = "(?i)官网|流量|过期|到期|重置|优选|群|TG|Static|静态|直连";
    public static string ChainedName(string name) => "[链式] " + name;
    public static string DirectName(string name) => "[直连] " + name;
    public static bool Enabled(JsonObject policy)
    {
        string mode = S(policy, "rule_source", "custom");
        if (mode is not "subscription" and not "custom") throw new BuildException("rule_source 只支持 subscription 或 custom");
        return mode == "subscription";
    }
    public static JsonObject? Primary(JsonObject settings) => L(settings, "sources").OfType<JsonObject>()
        .Where(x => B(x, "enabled", true)).OrderBy(x => I(x, "priority", 100)).FirstOrDefault();
    public static bool IsPrimary(JsonObject settings, string name)
    {
        var ordered = (JsonObject)settings.DeepClone(); ProjectOrdering.NumberSources(ordered);
        return Primary(ordered) is { } primary && S(primary, "name") == name;
    }
    public static int RuleCount(JsonObject document) => L(document, "rules").Count(x => IsString(x) && !string.IsNullOrWhiteSpace(S(x)));
    public static JsonObject ExpandPolicy(JsonObject policy)
    {
        if (!Enabled(policy)) return Map(ExpandEnvironment(policy), "policy");
        var effective = O("rule_source", "subscription", "extra_rules_enabled", B(policy, "extra_rules_enabled", true));
        if (policy.ContainsKey("dns_privacy")) effective["dns_privacy"] = policy["dns_privacy"]?.DeepClone();
        if (B(effective, "extra_rules_enabled"))
            foreach (string key in new[] { "first", "order", "direct", "reject", "proxy", "remote_rules", "supplemental_rules", "rule_sets" })
                if (policy.ContainsKey(key)) effective[key] = policy[key]?.DeepClone();
        return Map(ExpandEnvironment(effective), "policy");
    }

    public static void RequireRules(JsonObject document, string name)
    {
        if (L(document, "rules").Any(x => x is not null && !IsString(x))) throw new BuildException($"{name} 的 rules 应为文本规则列表");
        if (RuleCount(document) == 0)
            throw new BuildException($"主订阅“{name}”的缓存只有节点、没有规则。请拉取该订阅或导入完整 Clash/Mihomo 配置；不会改用其他订阅的规则。");
    }

    // Materialize node providers at pull time, but retain the original rule/group graph.
    // Groups using providers get explicit node lists, so the snapshot remains portable.
    public static async Task<JsonObject> SnapshotAsync(JsonObject input, Fetcher fetch, string? baseDirectory, string label)
    {
        var doc = (JsonObject)input.DeepClone();
        var nodes = (JsonArray)L(doc, "proxies").DeepClone();
        var providerNodes = new Dictionary<string, List<string>>();
        var names = nodes.Select(x => Name(Map(x, "节点")["name"], "节点")).ToHashSet();
        if (names.Count != nodes.Count) throw new BuildException($"{label} 存在重名节点，无法保留订阅策略引用");
        foreach (var (key, value) in M(doc, "proxy-providers"))
        {
            var loaded = await Nodes.CollectAsync(O("proxy-providers", O(key, value)), fetch, baseDirectory, label);
            var members = new List<string>();
            foreach (var raw in loaded)
            {
                var node = Map(raw, "节点"); string original = Name(node["name"], "节点");
                if (!names.Add(original)) throw new BuildException($"{label} 的节点来源存在重名节点；请先在订阅中设置不同的节点前后缀");
                members.Add(original); nodes.Push(node.DeepClone());
            }
            providerNodes.Add(key, members);
        }
        doc["proxies"] = nodes; doc.Remove("proxy-providers");
        foreach (var raw in L(doc, "proxy-groups"))
        {
            var group = Map(raw, "订阅策略组"); var members = L(group, "proxies").Select(x => S(x)).ToList();
            if (L(group, "use").Count == 0 && !B(group, "include-all") && !B(group, "include-all-proxies") && !B(group, "include-all-providers")) continue;
            var expanded = new List<string>();
            foreach (string provider in L(group, "use").Select(x => S(x)))
            {
                if (!providerNodes.TryGetValue(provider, out var entries)) throw new BuildException($"{label} 的策略组引用了不存在的节点来源");
                expanded.AddRange(entries);
            }
            if (B(group, "include-all") || B(group, "include-all-proxies")) expanded.AddRange(names);
            if (B(group, "include-all") || B(group, "include-all-providers")) expanded.AddRange(providerNodes.Values.SelectMany(x => x));
            if (expanded.Count > 0)
            {
                var include = Pattern(S(group, "filter", ".*"), "策略组筛选");
                var exclude = S(group, "exclude-filter") is { Length: > 0 } filter ? Pattern(filter, "策略组筛选") : null;
                var excludedTypes = S(group, "exclude-type").Split('|').ToHashSet(StringComparer.OrdinalIgnoreCase);
                var byName = nodes.OfType<JsonObject>().ToDictionary(x => S(x, "name"));
                members.AddRange(expanded.Where(x => Match(include, x) && (exclude is null || !Match(exclude, x)) && !excludedTypes.Contains(S(byName[x], "type"))));
            }
            group["proxies"] = A(members.Distinct());
            if (members.Count == 0) throw new BuildException($"{label} 的策略组筛选后没有节点");
            foreach (string key in new[] { "use", "include-all", "include-all-proxies", "include-all-providers", "filter", "exclude-filter", "exclude-type" }) group.Remove(key);
        }
        // A remote rule provider is part of the subscription's own configuration. Local
        // file references cannot be copied to a router, so materialize text providers.
        foreach (var (_, raw) in M(doc, "rule-providers"))
        {
            var provider = Map(raw, "订阅规则来源");
            if (S(provider, "type") != "file") continue;
            if (baseDirectory is null) throw new BuildException($"{label} 的远程配置引用了本地规则文件，无法迁移");
            if (S(provider, "format", "yaml") == "mrs") throw new BuildException("本地 MRS 规则请改为 HTTP 来源后再导入完整订阅");
            string path = fetch.Resolve(S(provider, "path"), baseDirectory);
            string relative = Path.GetRelativePath(baseDirectory, path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)) throw new BuildException("订阅规则文件不能超出配置所在目录");
            byte[] bytes = await File.ReadAllBytesAsync(path);
            JsonArray payload = S(provider, "format", "yaml") == "text"
                ? A(System.Text.Encoding.UTF8.GetString(bytes).Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')))
                : L(Map(YamlData.Parse(bytes), "规则文件"), "payload");
            provider["type"] = "inline"; provider["payload"] = payload.DeepClone(); provider.Remove("path"); provider.Remove("format");
        }
        ConfigurationValidation.CacheNodes(nodes);
        return doc;
    }

    public static List<string> Eligible(JsonArray nodes, JsonObject source, JsonObject selection, string prefix = "")
    {
        var include = Pattern(S(source, "include", S(selection, "include", JumpInclude)), "机场入口筛选");
        var exclude = S(source, "exclude", S(selection, "exclude", JumpExclude)) is { Length: > 0 } pattern ? Pattern(pattern, "机场入口筛选") : null;
        return nodes.OfType<JsonObject>().Select(x => S(x, "name")).Where(x =>
        {
            string candidate = prefix.Length > 0 && x.StartsWith(prefix, StringComparison.Ordinal) ? x[prefix.Length..] : x;
            return Match(include, candidate) && (exclude is null || !Match(exclude, candidate));
        }).ToList();
    }
}

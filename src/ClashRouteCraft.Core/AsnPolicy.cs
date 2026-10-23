using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed class AsnDependencyException(JsonObject analysis) : BuildException(
    "ASN 依赖尚未处理：" + string.Join("；", L(analysis, "sources").OfType<JsonObject>()
        .Where(x => I(x, "detected", 0) > I(x, "excluded", 0) || L(x, "uninspected").Count > 0)
        .Select(x => S(x, "label") + $"（ASN {I(x, "detected", 0) - I(x, "excluded", 0)} 条，未检查规则集 {L(x, "uninspected").Count} 个）"))
    + "。请在规则页按来源排除，或明确选择保留 ASN（高级）；旧输出已保留。")
{
    public JsonObject Analysis { get; } = (JsonObject)analysis.DeepClone();
}

/// <summary>Explicit, per-project decisions. IDs never expose subscription tokens.</summary>
public sealed class AsnPolicy
{
    public const int Version = 1;
    readonly JsonObject policy;
    readonly Dictionary<string, JsonObject> sources = new(StringComparer.Ordinal);
    public AsnPolicy(JsonObject policy) { Validate(policy); this.policy = policy; }
    public static string SourceId(string kind, string identity) => kind + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    public static string SubscriptionId(string name) => SourceId("subscription", name);
    public static string RemoteId(JsonObject spec) => SourceId("remote", Canonical(O("url", spec["url"], "file", spec["file"], "headers", spec["headers"], "user_agent", spec["user_agent"])));
    public static bool AllowsNative(JsonObject policy) => B(M(policy, "asn_policy"), "allow_native");
    public static bool Excludes(JsonObject policy, string id) => L(M(policy, "asn_policy"), "exclude_sources").Any(x => S(x) == id);
    public static void Validate(JsonObject policy)
    {
        var options = M(policy, "asn_policy"); B(options, "allow_native");
        foreach (var raw in L(options, "exclude_sources"))
            if (!IsString(raw) || string.IsNullOrWhiteSpace(S(raw))) throw new BuildException("asn_policy.exclude_sources 应为来源 ID 列表");
    }
    public static void SetExcluded(JsonObject policy, string id, bool excluded)
    {
        policy["asn_policy"] ??= new JsonObject(); var options = M(policy, "asn_policy");
        var ids = L(options, "exclude_sources").Select(x => S(x)).ToHashSet(StringComparer.Ordinal);
        if (excluded) ids.Add(id); else ids.Remove(id);
        options["exclude_sources"] = A(ids.Order(StringComparer.Ordinal));
    }
    public static void RenameSubscription(JsonObject policy, string before, string after)
    {
        if (!Excludes(policy, SubscriptionId(before))) return;
        SetExcluded(policy, SubscriptionId(before), false); SetExcluded(policy, SubscriptionId(after), true);
    }
    JsonObject Source(string id, string label)
    {
        if (!sources.TryGetValue(id, out var value)) sources.Add(id, value = O("id", id, "label", label, "exclude_selected", Excludes(policy, id),
            "detected", 0, "excluded", 0, "rules", new JsonArray(), "uninspected", new JsonArray(), "snapshots", new JsonArray()));
        return value;
    }
    public bool Include(string id, string label, string rule, string location = "")
    {
        var source = Source(id, label);
        if (!RuleKinds(rule).Any(x => x is "IP-ASN" or "SRC-IP-ASN")) return true;
        bool excluded = Excludes(policy, id);
        source["detected"] = I(source, "detected", 0) + 1;
        if (excluded) source["excluded"] = I(source, "excluded", 0) + 1;
        if (L(source, "rules").Count < 100) L(source, "rules").Push(O("rule", rule, "location", location, "excluded", excluded));
        else source["details_truncated"] = true;
        return !excluded;
    }
    void Filter(JsonArray rules, string id, string label, string location)
    {
        // Remove the entire outer rule. Never weaken an AND/OR/NOT by deleting a child.
        for (int i = 0, originalPosition = 0; i < rules.Count; originalPosition++)
        {
            if (Include(id, label, S(rules[i]), $"{location}[{originalPosition + 1}]")) i++;
            else rules.RemoveAt(i);
        }
    }
    public async Task InspectDocumentAsync(JsonObject document, string id, string label, Fetcher fetch, string? baseDirectory)
    {
        Source(id, label); Filter(L(document, "rules"), id, label, "rules");
        foreach (var (name, value) in M(document, "sub-rules")) Filter(List(value, "子规则"), id, label, "sub-rules/" + name);
        foreach (var (name, value) in M(document, "rule-providers"))
        {
            var provider = Map(value, "规则集"); string behavior = S(provider, "behavior");
            if (behavior is "domain" or "ipcidr") continue;
            if (S(provider, "type") == "inline") { Filter(L(provider, "payload"), id, label, "rule-providers/" + name); continue; }
            var source = Source(id, label);
            if (!Excludes(policy, id)) { L(source, "uninspected").Push(name); continue; }
            if (S(provider, "type") != "http" || S(provider, "format") == "mrs")
                throw new BuildException($"{label} 的规则集 {name} 无法按来源排除 ASN：仅支持可读取的 HTTP 文本规则集或内嵌规则集；旧输出已保留。");
            var spec = O("url", provider["url"], "format", S(provider, "format", "yaml"), "behavior", "classical");
            if (provider.ContainsKey("header")) spec["headers"] = provider["header"]?.DeepClone();
            var loaded = await fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec), label + " / " + name, baseDirectory);
            var payload = new JsonArray(); int position = 0;
            foreach (var raw in List(loaded.Value, "远程规则集"))
            {
                position++; string rule = S(raw);
                if (!Include(id, label, rule, $"rule-providers/{name}[{position}]")) continue;
                var parts = Rules.SplitFull(rule); parts.RemoveAt(2); payload.Push(string.Join(',', parts));
            }
            // A filtered live provider must become a snapshot, otherwise the router
            // would download the original ASN rules again on its next update.
            provider.Clear(); provider["type"] = "inline"; provider["behavior"] = "classical"; provider["payload"] = payload;
            L(source, "snapshots").Push(name);
        }
    }
    public JsonObject Report(JsonObject configuration, string target)
    {
        bool required = GeoRules.RequiredDatabases(configuration).Contains("ASN.mmdb");
        int unknown = sources.Values.Sum(x => L(x, "uninspected").Count);
        bool blocked = target == "mc2" && !AllowsNative(policy) && (required || unknown > 0);
        return O("version", Version, "native_required", required, "uninspected_providers", unknown, "allow_native", AllowsNative(policy),
            "status", blocked ? "blocked" : required || unknown > 0 ? "requires_target_setup" : "no_asn_dependency",
            "excluded_rules", sources.Values.Sum(x => I(x, "excluded", 0)), "sources", A(sources.Values.Select(x => x.DeepClone())), "router_runtime_tested", false);
    }
    public static void Enforce(JsonObject analysis) { if (S(analysis, "status") == "blocked") throw new AsnDependencyException(analysis); }
    public static string[] ValidationFiles(JsonObject configuration, JsonObject? analysis = null)
    {
        var files = GeoRules.RequiredDatabases(configuration).ToHashSet(StringComparer.Ordinal);
        // Explicit retention of opaque classical providers may require ASN when
        // the core downloads them. This is a declared potential dependency.
        if (analysis is not null && I(analysis, "uninspected_providers", 0) > 0) files.Add("ASN.mmdb");
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    // Parse rule boundaries, not serialized JSON: regex/domain text may contain
    // the literal "IP-ASN," without representing an ASN predicate.
    public static IEnumerable<string> RuleKinds(string rule, int depth = 0)
    {
        if (depth > 32) throw new BuildException("依赖检查：逻辑规则嵌套过深");
        int comma = rule.IndexOf(','); string kind = (comma < 0 ? rule : rule[..comma]).Trim().ToUpperInvariant();
        yield return kind;
        if (kind is not "AND" and not "OR" and not "NOT" and not "SUB-RULE") yield break;
        var parts = Rules.Split(rule);
        if (parts.Count < 2 || parts[1].Length < 2 || parts[1][0] != '(' || parts[1][^1] != ')') throw new BuildException("依赖检查：逻辑规则括号无效");
        string body = parts[1][1..^1];
        if (kind == "SUB-RULE")
        {
            foreach (string childKind in RuleKinds(body, depth + 1)) yield return childKind;
        }
        else foreach (string child in Rules.Split(body))
        {
            if (child.Length < 2 || child[0] != '(' || child[^1] != ')') throw new BuildException("依赖检查：逻辑子条件括号无效");
            foreach (string childKind in RuleKinds(child[1..^1], depth + 1)) yield return childKind;
        }
    }
}

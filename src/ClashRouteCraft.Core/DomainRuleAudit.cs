using System.Globalization;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed class InvalidDomainRuleException(string message) : BuildException(message);

/// <summary>Reject only invalid domain conditions; never rewrite a guessed hostname.</summary>
public sealed class DomainRuleAudit
{
    readonly HashSet<string> warningKeys = new(StringComparer.Ordinal);
    public JsonArray Skipped { get; } = new();
    public JsonArray Unchecked { get; } = new();
    public static void Domain(string value, bool wildcard = false)
    {
        if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new InvalidDomainRuleException("域名为空或含空白、控制字符、不可见字符");
        string check = wildcard ? value.Replace("*", "wildcard").Replace("?", "x") : value;
        if (check.IndexOfAny(['*', '?', '+', '/', ':', ',', '\\']) >= 0 || Uri.CheckHostName(check) != UriHostNameType.Dns)
            throw new InvalidDomainRuleException("域名格式无效，不能包含协议、路径或非法域名字符");
    }
    public static void Condition(string rule, int depth = 0)
    {
        if (depth > 32) throw new BuildException("逻辑规则嵌套过深");
        int comma = rule.IndexOf(','); string kind = (comma < 0 ? rule : rule[..comma]).Trim().ToUpperInvariant();
        if (kind is "DOMAIN-REGEX" or "DOMAIN-KEYWORD" || kind.StartsWith("PROCESS-")) return;
        if (kind is "DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-WILDCARD")
        {
            var fields = Rules.Split(rule);
            Domain(fields.Count > 1 ? fields[1] : "", kind == "DOMAIN-WILDCARD"); return;
        }
        if (kind is not ("AND" or "OR" or "NOT" or "SUB-RULE")) return;
        var parts = Rules.Split(rule);
        if (parts.Count < 2 || !parts[1].StartsWith('(') || !parts[1].EndsWith(')')) throw new BuildException("逻辑规则括号无效");
        string body = parts[1][1..^1];
        if (kind == "SUB-RULE") { Condition(body, depth + 1); return; }
        foreach (string child in Rules.Split(body))
        {
            if (!child.StartsWith('(') || !child.EndsWith(')')) throw new BuildException("逻辑子条件括号无效");
            Condition(child[1..^1], depth + 1);
        }
    }
    public void Record(string source, string position, string rule, string reason)
    {
        var warning = O("source", source, "position", position, "rule", rule, "reason", reason, "action", "skipped");
        if (warningKeys.Add(Canonical(warning))) Skipped.Push(warning);
    }
    public static string Message(JsonObject warning) => $"{S(warning, "source")} · {S(warning, "position")}：这条规则因异常而跳过（{S(warning, "reason")}），未自动修正。原规则：{S(warning, "rule")}";
    public string? Check(Func<string> make, string source, string position, string raw)
    {
        try { string rule = make(); Condition(rule); return rule; }
        catch (InvalidDomainRuleException ex) { Record(source, position, raw, ex.Message); return null; }
    }
    public JsonObject FilterPolicy(JsonObject input)
    {
        var policy = (JsonObject)input.DeepClone();
        if (SubscriptionBaseline.Enabled(policy) && !B(policy, "extra_rules_enabled", true)) return policy;
        void Filter(JsonArray entries, string source, string field, Func<JsonNode?, string> make)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
                if (Check(() => make(entries[i]), source, $"{field}[{i + 1}]", IsString(entries[i]) ? S(entries[i]) : Canonical(entries[i])) is null) entries.RemoveAt(i);
        }
        foreach (string group in new[] { "direct", "proxy", "reject" }) Filter(L(policy, group), "个人规则 · " + group, group, raw => Rules.WithAction(raw, "DIRECT"));
        foreach (string field in new[] { "first", "supplemental_rules" }) Filter(L(policy, field), "个人规则", field, raw => Rules.WithAction(Map(raw, field)["match"], "DIRECT", inputFormat: "match"));
        foreach (var set in L(policy, "rule_sets").OfType<JsonObject>().Where(x => B(x, "enabled", true)))
        {
            foreach (var warning in L(set, "domain_warnings").OfType<JsonObject>()) Record(S(warning, "source"), S(warning, "position"), S(warning, "rule"), S(warning, "reason"));
            var entries = L(set, "rules");
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = Map(entries[i], "导入规则"); if (!ImportedRuleSets.Included(set, entry)) continue;
                if (Check(() => ImportedRuleSets.Rule(entry, "DIRECT"), "导入规则集 · " + S(set, "name"), $"rules[{i + 1}]", S(entry, "match")) is null) entries.RemoveAt(i);
            }
            if (entries.Count == 0) set["enabled"] = false;
        }
        var sets = L(policy, "rule_sets");
        for (int i = sets.Count - 1; i >= 0; i--)
            if (sets[i] is JsonObject set && L(set, "rules").Count == 0) sets.RemoveAt(i);
        return policy;
    }
    public void FilterRules(JsonArray rules, string source, string field, bool matches = false, bool domain = false)
    {
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            string raw = S(rules[i]);
            if (Check(() => domain ? Rules.DomainProviderRule(raw) : matches ? Rules.WithAction(JsonValue.Create(raw), "DIRECT", inputFormat: "match") : raw, source, $"{field}[{i + 1}]", raw) is null) rules.RemoveAt(i);
        }
    }
    public void FilterDocument(JsonObject doc, string source)
    {
        FilterRules(L(doc, "rules"), source, "rules");
        foreach (var (key, value) in M(doc, "sub-rules")) FilterRules(List(value, "子规则"), source, "sub-rules/" + key);
        foreach (var (key, value) in M(doc, "rule-providers"))
        {
            var provider = Map(value, "规则来源"); if (S(provider, "type") != "inline" || S(provider, "behavior") == "ipcidr") continue;
            FilterRules(L(provider, "payload"), source, "rule-providers/" + key + "/payload", true, S(provider, "behavior") == "domain");
        }
    }
    public async Task InspectProvidersAsync(JsonObject doc, Fetcher fetch, string source, string? baseDirectory)
    {
        foreach (var (key, value) in M(doc, "rule-providers"))
        {
            var provider = Map(value, "规则来源");
            if (S(provider, "type") == "inline" || S(provider, "behavior") == "ipcidr") continue;
            if (S(provider, "type") != "http" || S(provider, "format") == "mrs")
            { Unchecked.Push(O("source", source, "position", "rule-providers/" + key, "reason", "此规则集不是可读取的 HTTP 文本，域名内容未检查")); continue; }
            var spec = O("url", provider["url"], "format", S(provider, "format", "yaml"), "behavior", S(provider, "behavior", "classical"));
            if (provider.ContainsKey("header")) spec["headers"] = provider["header"]?.DeepClone();
            var providerAudit = new DomainRuleAudit();
            var loaded = await fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec, providerAudit, source + " / rule-providers/" + key), source + " / " + key, baseDirectory);
            foreach (var warning in providerAudit.Skipped.OfType<JsonObject>()) Record(S(warning, "source"), S(warning, "position"), S(warning, "rule"), S(warning, "reason"));
            if (providerAudit.Skipped.Count == 0) continue;
            var payload = new JsonArray();
            foreach (var raw in List(loaded.Value, "规则来源")) { var fields = Rules.SplitFull(S(raw)); fields.RemoveAt(2); payload.Push(string.Join(',', fields)); }
            provider.Clear(); provider["type"] = "inline"; provider["behavior"] = "classical"; provider["payload"] = payload;
        }
    }
    public void AddReport(JsonObject report) { report["invalid_domain_rules"] = Skipped.DeepClone(); report["unchecked_domain_sources"] = Unchecked.DeepClone(); }
}

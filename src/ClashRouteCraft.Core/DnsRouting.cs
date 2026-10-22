using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Ordered domain-only projection of user routing rules, before DNS fallback policies.</summary>
public static class DnsRouting
{
    public const string Prefix = "HomeChain-DNS-";
    static readonly HashSet<string> DomainTypes = ["DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD", "GEOSITE"];

    // Never strip connection-dependent children from a logical rule: doing so broadens its scope.
    static bool DomainOnly(string condition)
    {
        var parts = Rules.Split(condition);
        string type = parts[0];
        if (DomainTypes.Contains(type)) return true;
        if (type is not "AND" and not "OR" and not "NOT" || parts.Count != 2) return false;
        string body = parts[1];
        if (!body.StartsWith('(') || !body.EndsWith(')')) return false;
        var children = Rules.Split(body[1..^1]);
        return children.Count > 0 && children.All(child => child.StartsWith('(') && child.EndsWith(')') && DomainOnly(child[1..^1]));
    }

    public static JsonObject? Apply(JsonObject configuration, JsonObject policy, JsonArray candidates)
    {
        if (!B(policy, "dns_follow_rules")) return null;
        var dns = M(configuration, "dns");
        if (!B(dns, "enable")) return O("enabled", false, "reason", "dns_disabled");
        var direct = L(dns, "direct-nameserver");
        var proxy = A(L(dns, "nameserver").Concat(L(dns, "fallback")).Select(x => x?.DeepClone()));
        if (direct.Count == 0 || proxy.Count == 0) throw new BuildException("dns_follow_rules 需要 direct-nameserver 和 nameserver/fallback，分别指定直连与代理域名的 DNS");
        var providers = M(configuration, "rule-providers");
        if (providers.Any(x => x.Key.StartsWith(Prefix, StringComparison.Ordinal))) throw new BuildException("rule-providers 的 HomeChain-DNS- 前缀由 DNS 规则生成器保留");
        var ordered = new JsonObject(); var skipped = new JsonArray();
        JsonArray? payload = null; string previous = ""; int groups = 0, projected = 0, pass = 0;
        foreach (var raw in candidates)
        {
            var entry = Map(raw, "DNS route");
            if (S(entry, "stage") is "baseline" or "final") continue;
            var parts = Rules.SplitFull(S(entry, "rule"));
            string action = parts[0] == "MATCH" ? parts[1] : parts[2];
            if (action == "PASS") { pass++; continue; }
            string condition = parts[0] == "MATCH" ? "DOMAIN-REGEX,^.*$" : string.Join(',', parts.Take(2));
            if (!DomainOnly(condition))
            {
                skipped.Push(O("rule", S(entry, "rule"), "source", S(entry, "source"), "reason", "requires_connection_context"));
                continue;
            }
            // REJECT remains a traffic action. Its DNS lookup uses the ordinary resolver pool,
            // preserving its position so a later overlapping DIRECT rule cannot take precedence.
            string category = action == "DIRECT" ? "direct" : "proxy";
            if (category != previous)
            {
                string name = Prefix + (++groups).ToString("D4") + "-" + category;
                providers[name] = O("type", "inline", "behavior", "classical", "payload", new JsonArray());
                payload = L(Map(providers[name], "DNS provider"), "payload");
                ordered["rule-set:" + name] = (category == "direct" ? direct : proxy).DeepClone();
                previous = category;
            }
            payload!.Push(JsonValue.Create(condition)); projected++;
        }
        // Existing DNS policies (including geosite:cn) are fallback after user routing rules.
        foreach (var (key, value) in M(dns, "nameserver-policy"))
        {
            if (key.StartsWith("rule-set:" + Prefix, StringComparison.Ordinal)) throw new BuildException("nameserver-policy 的 HomeChain-DNS- 引用由生成器管理，请勿复制生成结果回输入");
            ordered[key] = value?.DeepClone();
        }
        if (groups > 0 && !configuration.ContainsKey("rule-providers")) configuration["rule-providers"] = providers;
        dns["nameserver-policy"] = ordered;
        return O("enabled", true, "precedence", "user_domain_rules_then_dns_policy_then_default", "projected_rules", projected,
            "provider_count", groups, "pass_skipped", pass, "connection_rules_skipped", skipped,
            "scope", "Domain-only conditions in first/manual/remote/imported/supplemental order. Connection-dependent rules stay in traffic routing; DIRECT re-resolves via direct-nameserver.");
    }
}

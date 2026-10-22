using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class DnsSettings
{
    const string DomesticKey = "geosite:cn";
    public static JsonArray DomesticResolvers() => A("https://223.5.5.5/dns-query#DIRECT");
    public static JsonObject CreateDefault() => Map(YamlData.Parse(BuiltInDefaults.Text("dns.yaml")), "内置 DNS");
    static JsonArray Endpoints(JsonNode? value) => IsString(value) ? A(S(value)) : value is JsonArray list ? (JsonArray)list.DeepClone() : new();
    static bool ExplicitDirect(JsonArray values) => values.Count > 0 && values.All(x => S(x).Contains('#') && S(x).Split('#')[1].Split('&').Contains("DIRECT"));
    static string? DomesticPolicyKey(JsonObject dns) => M(dns, "nameserver-policy").Select(x => x.Key).FirstOrDefault(x => x.Equals(DomesticKey, StringComparison.OrdinalIgnoreCase));
    public static bool DomesticEnabled(JsonObject policy)
    {
        var dns = M(policy, "dns"); string? key = DomesticPolicyKey(dns);
        return key is not null && ExplicitDirect(Endpoints(M(dns, "nameserver-policy")[key]));
    }
    static JsonObject EnsureDns(JsonObject policy)
    {
        if (policy["dns"] is null) policy["dns"] = CreateDefault();
        return M(policy, "dns");
    }
    static void SetDomesticPolicy(JsonObject dns, JsonArray? servers)
    {
        var policies = M(dns, "nameserver-policy");
        foreach (string key in policies.Select(x => x.Key).Where(x => x.Equals(DomesticKey, StringComparison.OrdinalIgnoreCase)).ToArray()) policies.Remove(key);
        if (servers is not null) policies[DomesticKey] = servers.DeepClone();
        if (!dns.ContainsKey("nameserver-policy")) dns["nameserver-policy"] = policies;
    }
    public static void SetDomesticEnabled(JsonObject policy, bool enabled)
    {
        var dns = EnsureDns(policy);
        if (!enabled) { SetDomesticPolicy(dns, null); return; }
        var current = L(dns, "direct-nameserver");
        var servers = ExplicitDirect(current) ? current : DomesticResolvers();
        dns["direct-nameserver"] = servers.DeepClone();
        dns["direct-nameserver-follow-policy"] = false;
        SetDomesticPolicy(dns, servers);
    }
    public static void SetDirectResolvers(JsonObject policy, JsonArray values)
    {
        bool domestic = DomesticPolicyKey(M(policy, "dns")) is not null;
        var dns = EnsureDns(policy); dns["direct-nameserver"] = values.DeepClone();
        if (domestic) SetDomesticPolicy(dns, values);
    }
    public static JsonObject ApplyRecommended(JsonObject policy)
    {
        var result = (JsonObject)policy.DeepClone(); var dns = EnsureDns(result); var defaults = CreateDefault();
        // Preserve listener/mode, IPv6, QUIC-related flags and unrelated custom DNS settings.
        foreach (var (key, value) in defaults) if (!dns.ContainsKey(key)) dns[key] = value?.DeepClone();
        foreach (string key in new[] { "enable", "respect-rules", "direct-nameserver-follow-policy", "default-nameserver", "proxy-server-nameserver", "nameserver", "fallback", "direct-nameserver" }) dns[key] = defaults[key]?.DeepClone();
        SetDomesticPolicy(dns, DomesticResolvers());
        result["dns_follow_rules"] = true;
        return result;
    }
}

using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class GeoRules
{
    public const int Version = 2;
    public static string Mode(JsonObject policy)
    {
        string mode = S(policy, "geo_mode", "native");
        if (mode is not "native" and not "mc2-inline") throw new BuildException("geo_mode 只支持 native 或 mc2-inline");
        return mode;
    }
    public static string[] RequiredDatabases(JsonObject configuration)
    {
        var rules = L(configuration, "rules").Select(x => S(x))
            .Concat(M(configuration, "sub-rules").SelectMany(x => List(x.Value, "子规则").Select(v => S(v))))
            .Concat(M(configuration, "rule-providers").Select(x => Map(x.Value, "规则集")).Where(x => S(x, "behavior") is not "domain" and not "ipcidr").SelectMany(x => L(x, "payload").Select(v => S(v))));
        var kinds = rules.SelectMany(x => AsnPolicy.RuleKinds(x)).ToHashSet(StringComparer.Ordinal);
        string dns = configuration["dns"]?.ToJsonString() ?? "";
        var files = new SortedSet<string>(StringComparer.Ordinal);
        if (kinds.Contains("GEOSITE") || dns.Contains("geosite:", StringComparison.OrdinalIgnoreCase)) files.Add("GeoSite.dat");
        if (kinds.Overlaps(["GEOIP", "SRC-GEOIP"]) || B(M(M(configuration, "dns"), "fallback-filter"), "geoip")) files.Add(B(configuration, "geodata-mode") ? "GeoIP.dat" : "country.mmdb");
        if (kinds.Overlaps(["IP-ASN", "SRC-IP-ASN"])) files.Add("ASN.mmdb");
        return files.ToArray();
    }
}

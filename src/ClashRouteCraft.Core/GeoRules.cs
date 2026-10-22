using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class GeoRules
{
    public const int Version = 1;
    public static string Mode(JsonObject policy)
    {
        string mode = S(policy, "geo_mode", "native");
        if (mode is not "native" and not "mc2-inline") throw new BuildException("geo_mode 只支持 native 或 mc2-inline");
        return mode;
    }
    public static string[] RequiredDatabases(JsonObject configuration)
    {
        var rules = new[] { configuration["rules"], configuration["sub-rules"], configuration["rule-providers"] };
        string text = string.Join('\n', rules.Select(x => x?.ToJsonString() ?? ""));
        string dns = configuration["dns"]?.ToJsonString() ?? "";
        var files = new SortedSet<string>(StringComparer.Ordinal);
        if (Regex.IsMatch(text, @"\bGEOSITE,", RegexOptions.IgnoreCase) || dns.Contains("geosite:", StringComparison.OrdinalIgnoreCase)) files.Add("GeoSite.dat");
        if (Regex.IsMatch(text, @"\b(?:SRC-)?GEOIP,", RegexOptions.IgnoreCase) || B(M(M(configuration, "dns"), "fallback-filter"), "geoip")) files.Add(B(configuration, "geodata-mode") ? "GeoIP.dat" : "country.mmdb");
        if (Regex.IsMatch(text, @"\b(?:SRC-)?IP-ASN,", RegexOptions.IgnoreCase)) files.Add("ASN.mmdb");
        return files.ToArray();
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class InputFingerprint
{
    public static string Compute(string project, IReadOnlyList<(string Path, byte[] Bytes)>? overrides = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new SortedDictionary<string, byte[]>(comparer);
        foreach (string name in new[] { "settings.yaml", "residential.yaml", "policy.yaml", SubscriptionState.FileName })
        { string path = Path.Combine(project, name); if (File.Exists(path)) files[name] = File.ReadAllBytes(path); }
        var staged = new Dictionary<string, byte[]>(comparer);
        if (overrides is not null)
            foreach (var (path, bytes) in overrides)
            {
                string relative = Path.GetRelativePath(project, path).Replace('\\', '/');
                if (relative == SubscriptionState.FileName) files[relative] = bytes;
                else if (relative.StartsWith("subscrip/", StringComparison.OrdinalIgnoreCase) && !relative.Contains("../")) staged[relative] = bytes;
            }
        if (files.TryGetValue("settings.yaml", out var settingsBytes))
            try
            {
                var settings = Map(YamlData.Parse(settingsBytes), "settings");
                var state = files.TryGetValue(SubscriptionState.FileName, out var stateBytes) ? Map(JsonNode.Parse(stateBytes), "订阅关联") : new JsonObject();
                foreach (var raw in L(settings, "sources"))
                {
                    var source = Map(raw, "订阅"); if (!B(source, "enabled", true)) continue;
                    string name = FileName(S(source, "name"));
                    var entry = state.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value as JsonObject ?? new();
                    string file = FileName(S(entry, "snapshot", name + ".yaml"), "订阅快照文件名");
                    string relative = "subscrip/" + file, path = Path.Combine(project, "subscrip", file);
                    if (staged.TryGetValue(relative, out var bytes)) files[relative] = bytes;
                    else if (File.Exists(path)) files[relative] = File.ReadAllBytes(path);
                }
            }
            catch (Exception ex) when (ex is BuildException or System.Text.Json.JsonException) { files["invalid-subscriptions"] = Encoding.UTF8.GetBytes("invalid"); }
        // Expanded environment values are hashed, never stored in the report.
        foreach (string name in new[] { "settings.yaml", "residential.yaml", "policy.yaml" })
            if (files.TryGetValue(name, out var raw))
                try { files["expanded/" + name] = Encoding.UTF8.GetBytes(Canonical(name == "settings.yaml" ? ConfigurationValidation.ExpandSettings(Map(YamlData.Parse(raw), "settings")) : ExpandEnvironment(YamlData.Parse(raw)))) ; }
                catch (BuildException) { files["expanded/" + name] = Encoding.UTF8.GetBytes("invalid-or-missing-environment"); }
        if (files.TryGetValue("policy.yaml", out var policyBytes))
            try
            {
                var policy = Map(ExpandEnvironment(YamlData.Parse(policyBytes)), "policy");
                var sources = Rules.RemoteSpecs(policy["remote_rules"]).Where(x => B(x, "enabled", true)).ToList();
                if (GeoRules.Mode(policy) == "mc2-inline")
                    foreach (string key in new[] { "geosite_sources", "asn_sources" })
                        foreach (var (_, value) in M(policy, key)) if (value is JsonObject map) sources.Add(map);
                foreach (var source in sources)
                    foreach (string key in new[] { "file", "fallback_file" })
                    {
                        string file = S(source, key); if (file.Length == 0) continue;
                        if (file.StartsWith('~')) file = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + file[1..];
                        string path = Path.GetFullPath(file, project);
                        string identity = "local-rule/" + Path.GetRelativePath(project, path).Replace('\\', '/');
                        files[identity] = File.Exists(path) ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes("missing");
                    }
            }
            catch (BuildException) { }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, bytes) in files) { string identity = OperatingSystem.IsWindows() ? name.ToLowerInvariant() : name; hash.AppendData(Encoding.UTF8.GetBytes(identity + "\0" + bytes.Length + "\0")); hash.AppendData(bytes); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

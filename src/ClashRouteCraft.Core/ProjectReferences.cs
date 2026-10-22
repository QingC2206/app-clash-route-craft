using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Only proxy/group references are rewritten; domain rules, URLs and credentials are preserved.</summary>
public static class ProjectReferences
{
    public static JsonObject RewritePolicy(JsonObject original, Func<string, string> replace)
    {
        var policy = (JsonObject)original.DeepClone();
        string Value(JsonNode? raw, Func<string, string> rewrite)
        {
            string text = S(raw), expanded = S(ExpandEnvironment(raw)), updated = rewrite(expanded);
            if (expanded != text && updated != expanded) throw new BuildException("规则或 DNS 通过环境变量引用此名称，请先调整该引用后再重命名或删除");
            return updated == expanded ? text : updated;
        }
        void Action(JsonObject map, string key) { if (map.ContainsKey(key)) map[key] = Value(map[key], replace); }
        Action(policy, "final");
        foreach (var raw in L(policy, "first")) Action(Map(raw, "前置规则"), "action");
        foreach (var raw in L(policy, "supplemental_rules")) Action(Map(raw, "补充规则"), "action");
        foreach (var raw in L(policy, "rule_sets"))
        {
            var mappings = M(Map(raw, "规则集"), "mappings");
            foreach (string key in mappings.Select(x => x.Key).ToArray()) Action(mappings, key);
        }
        foreach (var raw in L(policy, "remote_rules")) Action(Map(raw, "远程名单"), "action");
        string Endpoint(string text)
        {
            int marker = text.IndexOf('#'); if (marker < 0) return text;
            var parts = text[(marker + 1)..].Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                string decoded = Uri.UnescapeDataString(parts[i]);
                if (decoded.Contains('=')) continue;
                string updated = replace(decoded);
                if (updated != decoded) parts[i] = Uri.EscapeDataString(updated);
            }
            return text[..(marker + 1)] + string.Join('&', parts);
        }
        JsonNode? Dns(JsonNode? node) => node switch
        {
            JsonObject map => new JsonObject(map.Select(x => new KeyValuePair<string, JsonNode?>(x.Key, Dns(x.Value)))),
            JsonArray list => new JsonArray(list.Select(Dns).ToArray()),
            _ when IsString(node) => JsonValue.Create(Value(node, Endpoint)),
            _ => node?.DeepClone()
        };
        if (policy.ContainsKey("dns")) policy["dns"] = Dns(policy["dns"]);
        return policy;
    }
    public static string SourceReference(string reference, string oldName, string newName)
        => reference == "订阅·" + oldName ? "订阅·" + newName
            : reference.StartsWith("[" + oldName + "] ", StringComparison.Ordinal) ? "[" + newName + "] " + reference[(oldName.Length + 3)..] : reference;

    public static void CopyLocalRules(WorkspaceSession source, string destination)
    {
        var policy = source.Policy;
        var specs = L(policy, "remote_rules").OfType<JsonObject>().ToList();
        foreach (string key in new[] { "geosite_sources", "asn_sources" })
            specs.AddRange(M(policy, key).Select(x => x.Value).OfType<JsonObject>());
        bool changed = false;
        foreach (var spec in specs)
            foreach (string key in new[] { "file", "fallback_file" })
            {
                string value = S(ExpandEnvironment(spec[key])); if (value.Length == 0) continue;
                if (value.StartsWith('~')) value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..];
                string path = Path.GetFullPath(value, source.ProjectPath);
                string relative = Path.GetRelativePath(source.ProjectPath, path);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) continue;
                // Preserve missing fallback/disabled references; required missing files still fail preflight.
                if (File.Exists(path))
                {
                    string target = Path.GetFullPath(relative, destination);
                    if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new BuildException("本地规则路径超出项目目录");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Copy(path, target);
                }
                if (S(spec[key]) != relative.Replace('\\', '/')) { spec[key] = relative.Replace('\\', '/'); changed = true; }
            }
        if (changed) File.WriteAllText(Path.Combine(destination, "policy.yaml"), YamlEditing.Apply(source.Documents["policy.yaml"].Text, policy));
    }
}

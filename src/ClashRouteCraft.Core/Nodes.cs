using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class Nodes
{
    public static readonly HashSet<string> Reserved = ["DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "GLOBAL"];
    public const string Asia = @"(?i)(香港|港|HK|Hong Kong|HongKong|日本|东京|大阪|JP|Japan|Tokyo|Osaka|台湾|台|台北|TW|Taiwan|Taipei|新加坡|狮城|SG|Singapore)";
    public static JsonObject Document(byte[] raw) => Document(YamlData.Parse(raw));
    public static JsonObject Document(JsonNode? document)
    {
        if (document is JsonArray array && array.All(x => x is JsonObject)) document = O("proxies", array);
        if (document is JsonObject map)
        {
            if (map.ContainsKey("proxies") || map.ContainsKey("proxy-providers"))
            {
                if (map.ContainsKey("proxies"))
                    foreach (var node in List(map["proxies"], "proxies"))
                    {
                        var item = Map(node, "节点"); Name(item["name"], "节点");
                        if (!IsString(item["type"]) || S(item, "type").Length == 0) throw new BuildException("节点缺少 type");
                    }
                if (map.ContainsKey("proxy-providers")) Map(map["proxy-providers"], "proxy-providers");
                return (JsonObject)map.DeepClone();
            }
            if (map["payload"] is JsonArray payload && payload.All(x => x is JsonObject)) return Document(O("proxies", payload));
        }
        throw new BuildException("未找到节点列表；支持完整 Clash/Mihomo YAML、proxies/payload 文件或节点列表");
    }
    public static async Task<JsonArray> CollectAsync(JsonObject document, Fetcher fetch, string? baseDirectory, string label, int depth = 0, HashSet<string>? ancestry = null)
    {
        if (depth > 4) throw new BuildException($"{label} 的 providers 嵌套过深或存在循环");
        ancestry ??= [];
        var result = (JsonArray)L(document, "proxies").DeepClone();
        foreach (var (_, raw) in M(document, "proxy-providers"))
        {
            var provider = Map(raw, "provider");
            string childLabel = $"{label}/provider-{result.Count}";
            string type = S(provider, "type", S(provider, "url").Length > 0 ? "http" : "file");
            JsonObject child; string? childBase = null;
            var childAncestry = new HashSet<string>(ancestry);
            if (type == "inline") child = O("proxies", L(provider, "payload"));
            else
            {
                JsonObject resource;
                if (type == "http") resource = O("url", provider["url"], "headers", M(provider, "header"));
                else if (type == "file" && baseDirectory is not null)
                {
                    string target = fetch.Resolve(S(provider, "path"), baseDirectory);
                    string relative = Path.GetRelativePath(baseDirectory, target);
                    if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)) throw new BuildException($"{label} 的 file provider 超出所在配置目录");
                    resource = O("file", target);
                }
                else if (type == "file") throw new BuildException($"{label} 的远程 YAML 引用了本地 file provider");
                else throw new BuildException($"{label} 的 provider 类型不支持");
                string id = Canonical(resource);
                if (!childAncestry.Add(id)) throw new BuildException($"{label} 的 providers 有循环引用");
                try
                {
                    var loaded = await fetch.ReadAsync(resource, bytes => Document(bytes), childLabel, baseDirectory);
                    child = Map(loaded.Value, childLabel); childBase = loaded.Base;
                }
                catch (BuildException) when (provider["payload"] is JsonArray { Count: > 0 } && !B(fetch.Options, "strict_subscriptions"))
                { child = O("proxies", L(provider, "payload")); fetch.Events.Push(O("item", childLabel, "status", "inline_fallback")); }
            }
            var children = await CollectAsync(child, fetch, childBase, childLabel, depth + 1, childAncestry);
            var include = Pattern(S(provider, "filter", ".*"), label);
            var exclude = S(provider, "exclude-filter") is { Length: > 0 } exclusion ? Pattern(exclusion, label) : null;
            var changes = M(provider, "override");
            string[] permitted = ["additional-prefix", "additional-suffix", "udp", "tfo", "mptcp", "skip-cert-verify", "dialer-proxy", "ip-version"];
            if (changes.Any(x => !permitted.Contains(x.Key))) throw new BuildException($"{label} 使用了尚未支持的 provider override 字段");
            var excludedTypes = S(provider, "exclude-type").Split('|').ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rawNode in children)
            {
                var node = (JsonObject)Map(rawNode, "node").DeepClone();
                string original = Name(node["name"], "节点");
                if (!Match(include, original) || exclude is not null && Match(exclude, original) || excludedTypes.Contains(S(node, "type"))) continue;
                node["name"] = S(changes, "additional-prefix") + original + S(changes, "additional-suffix");
                foreach (var (key, value) in changes.Where(x => x.Key is not "additional-prefix" and not "additional-suffix")) node[key] = value?.DeepClone();
                result.Push(node);
            }
        }
        if (result.Count > 20000) throw new BuildException($"{label} 节点数超过 20000");
        return result;
    }
    public static JsonArray Namespace(JsonArray nodes, string source)
    {
        var output = new JsonArray(); var used = new HashSet<string>(); var fingerprints = new Dictionary<string, string>(); var originals = new Dictionary<string, HashSet<string>>();
        foreach (var raw in nodes.OrderBy(x => Regex.IsMatch(S(Map(x, "节点"), "name"), @"(?:套餐到期日期|套餐重置日期|订阅获取时间)\s*[:：]")))
        {
            var node = (JsonObject)Map(raw, "节点").DeepClone(); string original = Name(node["name"], "节点");
            if (!IsString(node["type"])) throw new BuildException($"{source} 存在没有 type 的节点");
            var signature = (JsonObject)node.DeepClone(); signature.Remove("name");
            string fingerprint = Canonical(signature);
            if (fingerprints.TryGetValue(fingerprint, out string? retained))
            {
                if (!originals.TryGetValue(original, out var aliases)) originals[original] = aliases = [];
                aliases.Add(retained);
                continue;
            }
            string stem = $"[{source}] {original}", renamed = stem; int suffix = 2;
            while (!used.Add(renamed)) renamed = $"{stem} ({suffix++})";
            if (!originals.TryGetValue(original, out var names)) originals[original] = names = [];
            names.Add(renamed); node["name"] = renamed; output.Push(node);
            fingerprints[fingerprint] = renamed;
        }
        foreach (var raw in output)
        {
            var node = Map(raw, "节点"); string dialer = S(node, "dialer-proxy");
            if (dialer.Length == 0 || Reserved.Contains(dialer)) continue;
            if (!originals.TryGetValue(dialer, out var names) || names.Count != 1) throw new BuildException($"{source} 节点的 dialer-proxy 引用了分组或不唯一的节点");
            node["dialer-proxy"] = names.Single();
        }
        return output;
    }
    public static List<string> Eligible(JsonArray namespaced, JsonObject source, JsonObject selection)
    {
        string name = S(source, "name");
        var include = Pattern(S(source, "include", S(selection, "include", Asia)), name);
        string exclusion = S(source, "exclude", S(selection, "exclude"));
        var exclude = exclusion.Length > 0 ? Pattern(exclusion, name) : null;
        int prefix = $"[{name}] ".Length;
        var eligible = namespaced.Select(x => S(Map(x, "节点"), "name"))
            .Where(n => Match(include, n[prefix..]) && (exclude is null || !Match(exclude, n[prefix..]))).ToList();
        if (eligible.Count == 0) throw new BuildException($"{name} 没有符合筛选条件的节点，请调整保留/排除条件");
        return eligible;
    }
    public static void CheckGraph(JsonObject configuration)
    {
        var elements = L(configuration, "proxies").Concat(L(configuration, "proxy-groups")).Select(x => Map(x, "节点/分组")).ToList();
        var graph = new Dictionary<string, List<string>>();
        foreach (var element in elements)
        {
            string name = Name(element["name"], "节点/分组");
            if (Reserved.Contains(name) || !graph.TryAdd(name, [])) throw new BuildException("生成结果中节点或分组重名，或使用了保留名称");
        }
        foreach (var element in elements)
            foreach (string reference in L(element, "proxies").Select(x => S(x)).Concat(S(element, "dialer-proxy").Length > 0 ? [S(element, "dialer-proxy")] : []))
                if (graph.ContainsKey(reference)) graph[S(element, "name")].Add(reference);
                else if (!Reserved.Contains(reference)) throw new BuildException("生成配置存在未知的节点/分组引用");
        var visited = new HashSet<string>(); var active = new HashSet<string>();
        void Walk(string name)
        {
            if (active.Contains(name)) throw new BuildException("生成配置存在代理链循环");
            if (!visited.Add(name)) return;
            active.Add(name); foreach (string target in graph[name]) Walk(target); active.Remove(name);
        }
        foreach (string name in graph.Keys) Walk(name);
    }
}

using System.Net;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Carry node resolution dependencies without importing another subscription's DNS policy.</summary>
public static class SubscriptionHosts
{
    public const int Version = 1;
    static string Domain(string value) => value.TrimEnd('.').ToLowerInvariant();
    static bool IsIp(string value) => IPAddress.TryParse(value, out _);
    static bool IsDomain(string value) => value.Length > 0 && value == value.Trim() && Uri.CheckHostName(value) == UriHostNameType.Dns;

    public static JsonObject Normalize(JsonObject hosts, string label)
    {
        var result = new JsonObject();
        foreach (var (key, value) in hosts)
        {
            string name = Domain(key), body = name.StartsWith("+.") ? name[2..] : name.TrimStart('.');
            if (name.Length == 0 || !IsDomain(string.Join('.', body.Split('.').Select(x => x == "*" ? "wildcard" : x))))
                throw new BuildException($"{label} 的 hosts 域名格式无效：{key}");
            bool valid = value is JsonArray list ? list.Count > 0 && list.All(x => IsString(x) && IsIp(S(x)))
                : IsString(value) && (IsIp(S(value)) || IsDomain(S(value)));
            if (!valid) throw new BuildException($"{label} 的 hosts 值应为 IP、域名或非空 IP 列表：{key}");
            if (result.TryGetPropertyValue(name, out var existing) && !Same(existing, value)) Conflict(label, name);
            result[name] = value!.DeepClone();
        }
        return result;
    }

    static bool Same(JsonNode? left, JsonNode? right) => IsString(left) && IsString(right)
        ? Domain(S(left)) == Domain(S(right)) : JsonNode.DeepEquals(left, right);
    static void Conflict(string label, string name) => throw new BuildException($"{label} 的 hosts 映射冲突（{name}）；不同来源无法同时使用不同解析，请修正来源配置后重试。");

    // Match mihomo's DNS wildcards, not DOMAIN-WILDCARD: '*' is one label,
    // '.' is descendants only, '+.' includes the root. Rightmost literals win.
    static JsonNode? Find(JsonObject hosts, string domain, string label)
    {
        string[] parts = Domain(domain).Split('.').Reverse().ToArray();
        string? best = null; JsonNode? found = null;
        foreach (var (key, value) in hosts)
        {
            bool inclusive = key.StartsWith("+."), suffix = inclusive || key.StartsWith('.');
            string body = inclusive ? key[2..] : suffix ? key[1..] : key;
            string[] pattern = body.Split('.').Reverse().ToArray();
            if (parts.Length < pattern.Length || !suffix && parts.Length != pattern.Length || suffix && !inclusive && parts.Length == pattern.Length) continue;
            var rank = new System.Text.StringBuilder(); bool matches = true;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == "*") rank.Append('1');
                else if (pattern[i] == parts[i]) rank.Append('2');
                else { matches = false; break; }
            }
            if (!matches) continue;
            rank.Append(parts.Length == pattern.Length ? '3' : '0');
            string score = rank.ToString(); int order = best is null ? 1 : string.CompareOrdinal(score, best);
            if (order > 0) { best = score; found = value; }
            else if (order == 0 && !Same(found, value)) Conflict(label, domain);
        }
        return found;
    }

    static IEnumerable<string> Servers(JsonNode? node)
    {
        if (node is JsonObject map)
            foreach (var (key, value) in map)
            {
                if (key == "server" && IsString(value) && IsDomain(S(value))) yield return Domain(S(value));
                else foreach (string server in Servers(value)) yield return server;
            }
        else if (node is JsonArray list)
            foreach (var value in list) foreach (string server in Servers(value)) yield return server;
    }

    public static JsonObject Required(JsonObject hosts, JsonArray nodes, string label)
    {
        var normalized = Normalize(hosts, label); var required = new JsonObject();
        var done = new HashSet<string>(); var active = new HashSet<string>();
        void Walk(string name)
        {
            name = Domain(name);
            if (IsIp(name) || done.Contains(name)) return;
            if (active.Count >= 128 || !active.Add(name)) throw new BuildException($"{label} 的 hosts 域名别名存在循环或链路过深：{name}");
            if (Find(normalized, name, label) is { } value)
            {
                // Scope wildcard dependencies to the concrete node/alias hostname.
                required[name] = value.DeepClone();
                if (IsString(value) && !IsIp(S(value))) Walk(S(value));
            }
            active.Remove(name); done.Add(name);
        }
        foreach (string server in Servers(nodes).Distinct()) Walk(server);
        return required;
    }

    public static JsonObject NodeDocument(JsonObject document, JsonArray nodes, string label)
    {
        var result = O("proxies", nodes);
        var hosts = Required(M(document, "hosts"), nodes, label);
        if (hosts.Count > 0) result["hosts"] = hosts;
        return result;
    }

    public static JsonObject Merge(JsonObject current, JsonObject incoming, string label)
    {
        var left = Normalize(current, label); var right = Normalize(incoming, label);
        // Exact records must not be silently shadowed by another source's wildcard,
        // or override a wildcard that resolves the same hostname differently.
        foreach (var (key, value) in right)
        {
            if (left.TryGetPropertyValue(key, out var previous) && !Same(previous, value)) Conflict(label, key);
            if (!key.Contains('*') && !key.StartsWith('.') && !key.StartsWith("+."))
                if (Find(left, key, label) is { } effective && !Same(effective, value)) Conflict(label, key);
        }
        foreach (var (key, value) in left)
            if (!key.Contains('*') && !key.StartsWith('.') && !key.StartsWith("+."))
                if (Find(right, key, label) is { } effective && !Same(effective, value)) Conflict(label, key);
        foreach (var (key, value) in right) left[key] = value?.DeepClone();
        return left;
    }

    public static void Apply(JsonObject configuration, JsonObject incoming, string label)
    {
        var hosts = Merge(M(configuration, "hosts"), incoming, label);
        if (hosts.Count > 0) configuration["hosts"] = hosts;
    }
}

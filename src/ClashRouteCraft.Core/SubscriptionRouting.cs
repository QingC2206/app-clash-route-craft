using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Keep subscription matches, but reduce routing decisions to direct, proxy and reject.</summary>
public sealed class SubscriptionRouting
{
    public const string ProxyGroup = "代理出口";
    readonly JsonObject document;
    readonly Dictionary<string, JsonObject> nodes;
    readonly Dictionary<string, JsonObject> groups;
    readonly Dictionary<string, string> resolved = new(StringComparer.Ordinal);
    readonly HashSet<string> active = new(StringComparer.Ordinal);

    public SubscriptionRouting(JsonObject document)
    {
        this.document = document;
        Nodes.CheckGraph(document);
        nodes = L(document, "proxies").OfType<JsonObject>().ToDictionary(x => S(x, "name"));
        groups = L(document, "proxy-groups").OfType<JsonObject>().ToDictionary(x => S(x, "name"));
    }

    public bool Contains(string name) => nodes.ContainsKey(name) || groups.ContainsKey(name) || Nodes.Reserved.Contains(name);
    public static bool IsProxyNode(JsonObject node) => S(node, "type") is not ("direct" or "reject" or "dns" or "loopback" or "rematch");

    public string Kind(string name)
    {
        if (name is "DIRECT" or "COMPATIBLE") return "DIRECT";
        // PASS is control flow, not a fourth user-facing policy. In a sub-rule it
        // must remain intact so the core can return to the parent rule list.
        if (name is "REJECT" or "REJECT-DROP" or "PASS" or "PASS-RULE") return name;
        if (resolved.TryGetValue(name, out string? cached)) return cached;
        if (!active.Add(name)) throw new BuildException($"订阅策略组“{name}”存在循环引用，无法归并");
        try
        {
            string kind;
            if (nodes.TryGetValue(name, out var node))
                kind = S(node, "type") switch
                {
                    "direct" => "DIRECT", "reject" => "REJECT",
                    "dns" or "loopback" or "rematch" => throw new BuildException($"订阅节点“{name}”是特殊转发动作，不能归并为直连、代理或拦截"),
                    _ => "PROXY"
                };
            else if (groups.TryGetValue(name, out var group))
            {
                var members = L(group, "proxies").Select(x => S(x)).ToArray();
                if (members.Length == 0) throw new BuildException($"订阅策略组“{name}”没有候选，无法确定默认动作");
                string type = S(group, "type");
                if (type == "select")
                {
                    string selected = S(group, "default-selected", members[0]);
                    if (!members.Contains(selected)) throw new BuildException($"订阅策略组“{name}”的 default-selected 不在候选列表中");
                    kind = Kind(selected);
                }
                else
                {
                    var kinds = members.Select(Kind).Distinct().ToArray();
                    if (type == "relay" && kinds.All(x => x is "PROXY" or "DIRECT")) kind = kinds.Contains("PROXY") ? "PROXY" : "DIRECT";
                    else if (type is "url-test" or "fallback" or "load-balance" && kinds.Length == 1) kind = kinds[0];
                    else throw new BuildException($"订阅策略组“{name}”无法确定唯一动作；自动组需使用同类候选，混合直连/代理/拦截时请改为有明确默认项的 select 组");
                }
            }
            else throw new BuildException($"订阅规则引用了无法归并的动作“{name}”");
            resolved.Add(name, kind);
            return kind;
        }
        finally { active.Remove(name); }
    }

    public string Action(string name, string proxyGroup)
    {
        string kind = Kind(name);
        return kind == "PROXY" ? proxyGroup : kind;
    }

    public JsonArray Rewrite(JsonArray rules, string proxyGroup)
    {
        var result = new JsonArray();
        foreach (var raw in rules)
        {
            if (raw is null || IsString(raw) && string.IsNullOrWhiteSpace(S(raw))) continue;
            if (!IsString(raw)) throw new BuildException("订阅规则应为文本列表");
            var parts = Rules.SplitFull(S(raw));
            string type = parts[0].ToUpperInvariant();
            int index = type is "MATCH" or "FINAL" ? 1 : 2;
            if (parts.Count <= index || parts[index].Length == 0) throw new BuildException("订阅规则缺少目标动作");
            // SUB-RULE's third field is a rule-block name, never an outbound.
            if (type == "SUB-RULE")
            {
                if (M(document, "sub-rules")[parts[index]] is not JsonArray) throw new BuildException("订阅引用了不存在的子规则");
            }
            else parts[index] = Action(parts[index], proxyGroup);
            result.Push(string.Join(',', parts));
        }
        return result;
    }

    public JsonObject RewriteSubRules(string proxyGroup)
    {
        var result = new JsonObject();
        foreach (var (name, raw) in M(document, "sub-rules")) result[name] = Rewrite(List(raw, "订阅子规则"), proxyGroup);
        return result;
    }

    // A node may itself need a subscription group as its transport. Keep only
    // that dependency closure, hidden from the normal dashboard group list.
    // Never redirect those groups back to the unified exit (which creates cycles).
    public JsonArray TransportGroups()
    {
        var retained = new HashSet<string>(); var visited = new HashSet<string>();
        void Walk(string name)
        {
            if (!visited.Add(name)) return;
            if (groups.TryGetValue(name, out var group))
            {
                retained.Add(name);
                foreach (string member in L(group, "proxies").Select(x => S(x))) Walk(member);
                if (S(group, "dialer-proxy") is { Length: > 0 } dialer) Walk(dialer);
            }
            else if (nodes.TryGetValue(name, out var node) && S(node, "dialer-proxy") is { Length: > 0 } dialer) Walk(dialer);
        }
        foreach (var node in nodes.Values) if (S(node, "dialer-proxy") is { Length: > 0 } dialer) Walk(dialer);
        return A(groups.Where(x => retained.Contains(x.Key)).Select(x => (JsonNode?)Merge(x.Value, O("hidden", true))));
    }

    public JsonArray Mapping(string proxyGroup) => A(resolved.Where(x => groups.ContainsKey(x.Key))
        .Select(x => (JsonNode?)O("source", x.Key, "action", x.Value == "PROXY" ? proxyGroup : x.Value)));
}

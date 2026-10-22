using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class ProjectOrdering
{
    public const int Version = 3;
    public static void NumberSources(JsonObject settings)
    {
        int index = 0;
        foreach (var raw in L(settings, "sources")) Map(raw, "订阅")["priority"] = ++index;
    }
    public static string[] RuleOrder(JsonObject policy)
    {
        var order = policy["order"] is null ? ["direct", "reject", "proxy"] : L(policy, "order").Select(x => S(x)).ToArray();
        if (order.Length != 3 || !order.ToHashSet().SetEquals(["direct", "reject", "proxy"])) throw new BuildException("policy.order 必须恰好包含 direct、reject、proxy 各一次");
        return order;
    }
    // Apply the same group priority to every custom source, after actions have
    // been resolved. Stable sorting preserves the relative order within a group.
    public static JsonArray OrderCustomRules(JsonObject policy, JsonArray entries, Func<JsonNode?, string>? ruleText = null)
    {
        var order = RuleOrder(policy);
        int Rank(JsonNode? entry)
        {
            var parts = Rules.SplitFull(ruleText is null ? S(entry) : ruleText(entry));
            string action = parts[parts[0] is "MATCH" or "FINAL" ? 1 : 2];
            // Custom rules are top-level rules (SUB-RULE is not accepted here).
            // PASS only continues matching; retain it without treating it as an
            // outbound choice. Subscription control flow is never reordered.
            if (action is "PASS" or "PASS-RULE") return -1;
            string group = action switch { "DIRECT" or "COMPATIBLE" => "direct", "REJECT" or "REJECT-DROP" => "reject", _ => "proxy" };
            return Array.IndexOf(order, group);
        }
        return A(entries.OrderBy(Rank));
    }
    public static string? RemoteGroup(JsonObject spec) => S(spec, "action", "PROXY") switch
    {
        "DIRECT" => "direct", "REJECT" => "reject", "PROXY" or "默认代理" => "proxy", _ => null
    };
    public static bool IsUrlEntry(JsonObject spec) => RemoteGroup(spec) is not null && B(spec, "enabled", true) && spec.ContainsKey("url") && S(spec, "file").Length == 0;
    public static IEnumerable<JsonObject> OrderedRemotes(JsonObject policy)
    {
        var order = RuleOrder(policy);
        return Rules.RemoteSpecs(policy["remote_rules"]).OrderBy(x => RemoteGroup(x) is { } group ? Array.IndexOf(order, group) : order.Length);
    }
    public static void SetRemoteUrls(JsonObject policy, string group, string text)
    {
        if (!RuleOrder(policy).Contains(group)) throw new BuildException("未知名单分组");
        var existing = Rules.RemoteSpecs(policy["remote_rules"]).ToList();
        var editable = existing.Where(x => IsUrlEntry(x) && RemoteGroup(x) == group).ToList();
        var urls = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        var replacements = new JsonObject?[urls.Length];
        var available = editable.ToList();
        var names = existing.Select(x => S(x, "name")).ToHashSet();
        // Match retained/reordered lines first so inserting a URL cannot steal another
        // line's request options. Editing a line in place retains its options as well.
        for (int i = 0; i < urls.Length; i++)
        {
            int match = available.FindIndex(x => S(x, "url") == urls[i]);
            if (match >= 0) { replacements[i] = available[match]; available.RemoveAt(match); }
        }
        for (int i = 0; i < urls.Length; i++)
        {
            if (replacements[i] is not null) continue;
            if (i < editable.Count && available.Remove(editable[i]))
            {
                replacements[i] = (JsonObject)editable[i].DeepClone(); replacements[i]!["url"] = urls[i];
            }
            else
            {
                int number = 1; string name;
                do { name = group + "-URL-" + number++; } while (!names.Add(name));
                replacements[i] = O("name", name, "url", urls[i], "action", group.ToUpperInvariant());
            }
        }
        // Unchanged URLs keep names, parsing options and request headers. Other groups,
        // local files, disabled entries and custom actions remain available in YAML.
        policy["remote_rules"] = A(existing.Where(x => !IsUrlEntry(x) || RemoteGroup(x) != group).Concat(replacements.Select(x => x!)).Select(x => (JsonNode?)x));
        policy["remote_rules"] = A(OrderedRemotes(policy).Select(x => (JsonNode?)x));
    }
}

using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class SubscriptionBaselineCompiler
{
    public static async Task<CompiledProfile> CompileAsync(JsonObject settings, JsonObject policy, string policyPath, Fetcher fetch, CancellationToken cancellation)
    {
        ParameterInspection.ValidateRules(policy);
        var primary = SubscriptionBaseline.Primary(settings) ?? throw new BuildException("至少启用一个订阅");
        string primaryName = Name(primary["name"], "订阅");
        var sources = L(settings, "sources").OfType<JsonObject>().Where(x => B(x, "enabled", true)).OrderBy(x => I(x, "priority", 100)).ToArray();
        var selection = M(settings, "selection"); var summaries = new JsonArray();
        var read = await fetch.ReadAsync(primary, Nodes.Document, primaryName);
        var original = await SubscriptionBaseline.SnapshotAsync(Map(read.Value, "订阅"), fetch, read.Base, primaryName);
        SubscriptionBaseline.RequireRules(original, primaryName);
        var configuration = O("mixed-port", 7890, "allow-lan", false, "mode", "rule", "log-level", "info");
        var runtime = M(settings, "runtime");
        if (runtime.Any(x => new[] { "dns", "proxies", "proxy-providers", "proxy-groups", "rules", "rule-providers", "sub-rules" }.Contains(x.Key))) throw new BuildException("runtime 不能覆盖订阅基线管理的字段");
        configuration = Merge(configuration, runtime);
        foreach (string key in new[] { "proxies", "proxy-groups", "rules", "rule-providers", "sub-rules", "geodata-mode", "geodata-loader", "geox-url", "profile" })
            if (original.ContainsKey(key)) configuration[key] = original[key]?.DeepClone();
        if (!configuration.ContainsKey("proxy-groups")) configuration["proxy-groups"] = new JsonArray();
        var proxies = L(configuration, "proxies"); var groups = L(configuration, "proxy-groups");
        var originalGroups = groups.OfType<JsonObject>().ToArray();
        var originalRules = (JsonArray)L(original, "rules").DeepClone();
        var aliases = new Dictionary<string, string>();
        foreach (var node in proxies.OfType<JsonObject>()) aliases[$"[{primaryName}] {S(node, "name")}"] = S(node, "name");
        var used = proxies.Concat(groups).Select(x => Name(Map(x, "节点/分组")["name"], "节点/分组")).Concat(Nodes.Reserved).ToHashSet();
        if (used.Count != proxies.Count + groups.Count + Nodes.Reserved.Count) throw new BuildException("订阅节点或策略组名称重复，无法保留原始引用");
        string Unique(string name) { string result = name; int i = 2; while (!used.Add(result)) result = name + " · " + i++; return result; }
        var jumpCandidates = new List<string>();
        var sourceCandidates = new List<(string Name, List<string> Members)>();
        void CollectCandidates(JsonObject source, JsonArray nodes, string prefix = "")
        {
            string name = S(source, "name");
            var eligible = SubscriptionBaseline.Eligible(nodes, source, selection, prefix);
            jumpCandidates.AddRange(eligible);
            sourceCandidates.Add((name, eligible));
            summaries.Push(O("source", name, "priority", I(source, "priority", 100), "nodes", nodes.Count, "eligible", eligible.Count, "rules_used", source == primary));
        }
        CollectCandidates(primary, proxies);
        foreach (var source in sources.Skip(1))
        {
            cancellation.ThrowIfCancellationRequested(); string name = Name(source["name"], "订阅");
            var loaded = await fetch.ReadAsync(source, Nodes.Document, name);
            // Deliberately ignore all routing/group/DNS content from secondary sources.
            var nodes = await Nodes.CollectAsync(Map(loaded.Value, "订阅"), fetch, loaded.Base, name);
            var namespaced = Nodes.Namespace(nodes, name);
            foreach (var node in namespaced.OfType<JsonObject>())
            {
                if (!used.Add(S(node, "name"))) throw new BuildException("其他订阅节点名称与主订阅冲突，请重命名订阅");
                proxies.Push(node.DeepClone());
            }
            CollectCandidates(source, namespaced, $"[{name}] ");
        }
        var residenceDoc = ExpandEnvironment(YamlData.Read(fetch.Resolve(S(settings, "residential_file", "residential.yaml"))));
        var residences = (JsonArray)List(residenceDoc is JsonObject map ? map["proxies"] : residenceDoc, "住宅节点").DeepClone();
        if (residences.Count > 0) ProfileCompiler.ValidateResidences(residences);
        // Reserve old residential action names before allocating generated groups.
        // Existing user rules can keep referring to the unprefixed residential name.
        foreach (var raw in residences)
            if (!used.Add(S(Map(raw, "住宅"), "name"))) throw new BuildException("住宅名称与订阅节点/策略组冲突，请重命名住宅");
        string airport = Unique("机场入口"), aggregate = Unique("聚合组"), residential = Unique("住宅出口");
        var sourceGroups = new JsonArray();
        if (residences.Count > 0 && jumpCandidates.Count == 0) throw new BuildException("没有符合筛选条件的机场入口节点，无法连接链式住宅。请在订阅页面调整入口筛选，不会自动改成直连。");
        if (jumpCandidates.Count > 0)
        {
            JsonObject Pool(string name, List<string> members)
            {
                var group = O("name", name, "type", members.Count > 1 ? "url-test" : "select", "proxies", members);
                if (members.Count == 1) return group;
                int interval = I(selection, "interval", 300), timeout = I(selection, "timeout", 5000), tolerance = I(selection, "tolerance", 50);
                if (interval < 1 || timeout < 1 || tolerance < 0) throw new BuildException("入口健康检查 interval/timeout 应为正数，tolerance 不能为负数");
                return Merge(group, O("url", S(selection, "test_url", "https://www.gstatic.com/generate_204"), "interval", interval, "timeout", timeout, "tolerance", tolerance, "lazy", true));
            }
            var choices = A(aggregate); var pools = new JsonArray(Pool(aggregate, jumpCandidates));
            foreach (var (name, members) in sourceCandidates.Where(x => x.Members.Count > 0))
            {
                string group = Unique(name); choices.Push(group); pools.Push(Pool(group, members));
                sourceGroups.Push(O("source", name, "group", group, "candidates", members.Count));
            }
            groups.Push(O("name", airport, "type", "select", "proxies", choices));
            foreach (var pool in pools) groups.Push(pool?.DeepClone());
        }
        var chainedNames = new List<string>(); var directNames = new List<string>();
        foreach (var raw in residences)
        {
            var node = Map(raw, "住宅"); string name = S(node, "name");
            string chainedName = Unique(SubscriptionBaseline.ChainedName(name)), directName = Unique(SubscriptionBaseline.DirectName(name));
            aliases[name] = chainedName;
            string oldDirect = "直连住宅 · " + name;
            if (!used.Contains(oldDirect)) aliases[oldDirect] = directName;
            var direct = (JsonObject)node.DeepClone();
            direct["name"] = directName; direct.Remove("dialer-proxy"); proxies.Push(direct); directNames.Add(directName);
            var chained = (JsonObject)node.DeepClone(); chained["name"] = chainedName; chained["dialer-proxy"] = airport; proxies.Push(chained); chainedNames.Add(chainedName);
        }
        if (residences.Count > 0) groups.Push(O("name", residential, "type", "select", "proxies", chainedNames.Concat(directNames)));
        var mainSelector = originalGroups.FirstOrDefault(x => S(x, "type") == "select" && L(x, "proxies").Count > 0);
        string originalProxy = originalRules.Select(x => Rules.SplitFull(S(x))).Where(x => x.Count >= 2)
            .Select(x => x[0] is "MATCH" or "FINAL" ? x[1] : x.Count >= 3 ? x[2] : "")
            .FirstOrDefault(x => used.Contains(x) && !Nodes.Reserved.Contains(x)) ?? (jumpCandidates.Count > 0 ? airport : S(Map(proxies[0], "节点"), "name"));
        string defaultProxy = mainSelector is not null ? S(mainSelector, "name") : residences.Count > 0 ? residential : originalProxy;
        var injected = new List<string>();
        if (mainSelector is not null && residences.Count > 0)
        {
            L(mainSelector, "proxies").Insert(0, JsonValue.Create(residential)); injected.Add(S(mainSelector, "name"));
        }
        else if (mainSelector is not null && sources.Length > 1 && jumpCandidates.Count > 0) L(mainSelector, "proxies").Push(airport);
        Nodes.CheckGraph(configuration);
        var actions = proxies.Concat(groups).Select(x => S(Map(x, "节点/分组"), "name")).Concat(Nodes.Reserved).ToHashSet();
        string Action(JsonNode? raw)
        {
            string value = S(raw, "PROXY");
            value = value switch { "PROXY" => defaultProxy, "默认代理" when !actions.Contains(value) => defaultProxy,
                "AIRPORT" or "机场入口" => jumpCandidates.Count > 0 ? airport : originalProxy,
                "订阅跳板" when !actions.Contains(value) => jumpCandidates.Count > 0 ? airport : originalProxy,
                "住宅出口" => residential, _ => aliases.GetValueOrDefault(value, value) };
            if (!actions.Contains(value)) throw new BuildException("附加规则引用了不存在的节点或策略组");
            return value;
        }
        var rules = new JsonArray(); var remoteSummaries = new JsonArray();
        if (B(policy, "extra_rules_enabled", true))
        {
            foreach (var raw in L(policy, "first")) { var entry = Map(raw, "前置规则"); rules.Push(Rules.WithAction(entry["match"], Action(entry["action"]), inputFormat: "match")); }
            foreach (string group in ProjectOrdering.RuleOrder(policy))
                foreach (var rule in L(policy, group)) rules.Push(Rules.WithAction(rule, Action(JsonValue.Create(group switch { "direct" => "DIRECT", "reject" => "REJECT", _ => "PROXY" }))));
            foreach (var remote in ProjectOrdering.OrderedRemotes(policy).Where(x => B(x, "enabled", true)))
            {
                var loaded = await fetch.ReadAsync(remote, bytes => Rules.ParseRemote(bytes, remote), S(remote, "name"), Path.GetDirectoryName(policyPath));
                var entries = List(loaded.Value, "远程附加规则"); string action = Action(remote["action"]);
                foreach (var rule in entries) rules.Push(Rules.WithAction(rule, action, inputFormat: "rule"));
                remoteSummaries.Push(O("name", S(remote, "name"), "rules", entries.Count, "action", action));
            }
            foreach (var set in ImportedRuleSets.Sets(policy).OfType<JsonObject>().Where(x => B(x, "enabled", true)))
                foreach (var entry in L(set, "rules").OfType<JsonObject>().Where(x => ImportedRuleSets.Included(set, x)))
                {
                    if (S(entry, "match") == "MATCH") throw new BuildException("附加规则集不能使用 MATCH 覆盖整个订阅，请关闭该规则集的兜底规则");
                    rules.Push(ImportedRuleSets.Rule(entry, Action(JsonValue.Create(ImportedRuleSets.Target(set, entry)))));
                }
        }
        int extraCount = rules.Count;
        if (rules.Any(x => Rules.SplitFull(S(x))[0] is "MATCH" or "FINAL")) throw new BuildException("附加规则不能使用 MATCH / FINAL 覆盖整个订阅，请删除或停用附加兜底规则");
        foreach (var rule in originalRules) rules.Add(rule?.DeepClone());
        configuration["rules"] = rules;
        string dnsExit = DnsPrivacy.Apply(configuration, policy, residences.Count > 0 ? residential : "", jumpCandidates.Count > 0 ? airport : "");
        var report = O("built_at", DateTimeOffset.UtcNow.ToString("O"), "sources", summaries, "fetches", fetch.Events,
            "rule_source", "subscription", "subscription_baseline_version", SubscriptionBaseline.Version, "baseline_source", primaryName,
            "baseline_rule_count", SubscriptionBaseline.RuleCount(original), "baseline_entry_count", originalRules.Count, "extra_rule_count", extraCount, "residential_selectors", injected,
            "jump_group", jumpCandidates.Count > 0 ? airport : "", "jump_candidates", jumpCandidates.Count, "residential_group", residences.Count > 0 ? residential : "",
            "aggregate_group", jumpCandidates.Count > 0 ? aggregate : "", "source_groups", sourceGroups,
            "residential_paths", O("chained", chainedNames, "direct", directNames), "main_selector", mainSelector is null ? "" : S(mainSelector, "name"),
            "remote_rules", remoteSummaries, "rule_compiler_version", Rules.Version, "geo_rules_version", GeoRules.Version,
            "geo_mode", "subscription", "required_geodata", GeoRules.RequiredDatabases(configuration),
            "proxy_count", proxies.Count, "rule_count", SubscriptionBaseline.RuleCount(configuration), "contains_subscription_urls", false,
            "dns", O("configured", dnsExit.Length > 0, "enabled", dnsExit.Length > 0, "source", dnsExit.Length > 0 ? "privacy" : "client", "proxy_exit", dnsExit, "proxy_references", ProfileCompiler.ValidateDns(configuration)),
            "rule_merge", O("precedence", "user_extensions_then_primary_subscription", "input_count", rules.Count, "duplicates_removed", 0));
        return new(configuration, report);
    }
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class ProfileCompiler
{
    public static async Task<CompiledProfile> CompileAsync(JsonObject rawSettings, string root, bool offline = false, CancellationToken cancellation = default)
    {
        var settings = ConfigurationValidation.ExpandSettings(rawSettings);
        if (settings.ContainsKey("remote_rules")) throw new BuildException("规则入口已统一到 policy.yaml，请迁移 settings 中的 remote_rules");
        if (M(settings, "runtime").ContainsKey("dns")) throw new BuildException("DNS 统一由 policy.yaml 的顶层 dns 维护");
        using var fetch = new Fetcher(root, M(settings, "http"), offline, cancellation);
        string policyPath = fetch.Resolve(S(settings, "policy_file", "policy.yaml"));
        var policy = SubscriptionBaseline.ExpandPolicy(Map(YamlData.Read(policyPath), "policy"));
        if (SubscriptionBaseline.Enabled(policy)) return await SubscriptionBaselineCompiler.CompileAsync(settings, policy, policyPath, fetch, cancellation);
        string geoMode = GeoRules.Mode(policy);
        ParameterInspection.ValidateRules(policy);
        var residenceDoc = ExpandEnvironment(YamlData.Read(fetch.Resolve(S(settings, "residential_file", "residential.yaml"))));
        var residences = (JsonArray)List(residenceDoc is JsonObject residenceMap ? residenceMap["proxies"] : residenceDoc, "住宅节点").DeepClone();
        ValidateResidences(residences);
        foreach (var raw in residences) Map(raw, "住宅节点")["dialer-proxy"] = "机场入口";
        var selection = M(settings, "selection"); var sources = L(settings, "sources");
        var proxies = new JsonArray(); var sourceGroups = new JsonArray(); var summaries = new JsonArray(); var allEligible = new List<string>();
        var hosts = new JsonObject();
        var tiers = new SortedDictionary<int, List<string>>(); var seenSources = new HashSet<string>();
        int interval = I(selection, "interval", 300), timeout = I(selection, "timeout", 5000), tolerance = I(selection, "tolerance", 50);
        if (interval < 1 || timeout < 1 || tolerance < 0) throw new BuildException("健康检查 interval/timeout 应为正数，tolerance 不能为负数");
        var health = O("url", S(selection, "test_url", "https://www.gstatic.com/generate_204"), "interval", interval, "timeout", timeout, "lazy", false, "expected-status", 204);
        foreach (var raw in sources)
        {
            cancellation.ThrowIfCancellationRequested(); var source = Map(raw, "source");
            if (!B(source, "enabled", true)) continue;
            string name = Name(source["name"], "订阅"); if (!seenSources.Add(name)) throw new BuildException("订阅名称不能重复");
            int priority = I(source, "priority", 100); JsonArray nodes;
            try
            {
                var read = await fetch.ReadAsync(source, bytes => Nodes.Document(bytes), name);
                var collected = await Nodes.CollectDocumentAsync(Map(read.Value, "订阅"), fetch, read.Base, name);
                nodes = Nodes.Namespace(L(collected, "proxies"), name);
                hosts = SubscriptionHosts.Merge(hosts, M(collected, "hosts"), name);
            }
            catch (BuildException ex) when (S(source, "on_error", "abort") == "skip")
            { summaries.Push(O("source", name, "status", "skipped", "reason", ex.Message)); continue; }
            ConfigurationValidation.CacheNodes(nodes);
            var eligible = Nodes.Eligible(nodes, source, selection);
            foreach (var node in nodes) proxies.Push(node?.DeepClone());
            allEligible.AddRange(eligible); if (!tiers.TryGetValue(priority, out var tier)) tiers[priority] = tier = []; tier.AddRange(eligible);
            sourceGroups.Push(Merge(O("name", "订阅·" + name, "type", "url-test", "proxies", eligible, "tolerance", tolerance), health));
            summaries.Push(O("source", name, "priority", priority, "nodes", nodes.Count, "eligible", eligible.Count));
        }
        if (proxies.Count == 0) throw new BuildException("没有可用的已启用订阅");
        var tierGroups = new JsonArray();
        foreach (var (level, members) in tiers) tierGroups.Push(Merge(O("name", $"优先级·{level}", "type", "url-test", "proxies", members, "tolerance", tolerance), health));
        var groups = new JsonArray();
        groups.Push(O("name", "默认代理", "type", "select", "proxies", A("住宅出口", "机场入口", "DIRECT")));
        groups.Push(O("name", "住宅出口", "type", "select", "proxies", residences.Select(x => S(Map(x, "住宅"), "name"))));
        groups.Push(O("name", "机场入口", "type", "select", "proxies", new[] { "订阅优先", "机场手选" }.Concat(sourceGroups.Select(x => S(Map(x, "分组"), "name")))));
        groups.Push(Merge(O("name", "订阅优先", "type", "fallback", "proxies", tierGroups.Select(x => S(Map(x, "分组"), "name"))), health));
        groups.Push(O("name", "机场手选", "type", "select", "proxies", allEligible));
        foreach (var group in tierGroups.Concat(sourceGroups)) groups.Push(group?.DeepClone());
        var validActions = proxies.Concat(residences).Concat(groups).Select(x => S(Map(x, "节点/分组"), "name")).Concat(Nodes.Reserved).ToHashSet();
        string Action(JsonNode? value, string defaultValue = "PROXY")
        {
            string action = S(value, defaultValue); action = action switch { "PROXY" => "默认代理", "AIRPORT" => "机场入口", _ => action };
            if (!validActions.Contains(action)) throw new BuildException("规则 action 引用了不存在的策略");
            return action;
        }
        ParameterInspection.ValidateRules(policy, validActions);
        ParameterInspection.ValidateDnsParameters(policy, validActions);
        var candidates = new JsonArray();
        void AddRule(string rule, string source, string stage) => candidates.Push(O("rule", rule, "source", source, "stage", stage));
        int number = 0;
        foreach (var raw in L(policy, "first")) { number++; var entry = Map(raw, "first rule"); AddRule(Rules.WithAction(entry["match"], Action(entry["action"]), inputFormat: "match"), $"first[{number}]", "first"); }
        var orderNames = ProjectOrdering.RuleOrder(policy);
        foreach (string group in orderNames)
        {
            string target = Action(JsonValue.Create(group switch { "direct" => "DIRECT", "reject" => "REJECT", _ => "PROXY" }));
            foreach (var rule in L(policy, group)) AddRule(Rules.WithAction(rule, target), "manual." + group, "manual");
        }
        var remoteSummaries = new JsonArray(); var remoteNames = new HashSet<string>();
        foreach (var remote in ProjectOrdering.OrderedRemotes(policy))
        {
            cancellation.ThrowIfCancellationRequested(); if (!B(remote, "enabled", true)) continue;
            string name = Name(remote["name"], "远程规则集"); if (!remoteNames.Add(name)) throw new BuildException("启用的远程规则集名称不能重复");
            string target = Action(remote["action"]);
            var loaded = await fetch.ReadAsync(remote, bytes => Rules.ParseRemote(bytes, remote), "规则·" + name, Path.GetDirectoryName(policyPath));
            var entries = List(loaded.Value, "远程规则");
            foreach (var entry in entries) AddRule(Rules.WithAction(entry, target, inputFormat: "rule"), name, "remote");
            remoteSummaries.Push(O("name", name, "rules", entries.Count, "action", target));
        }
        foreach (var raw in L(policy, "rule_sets"))
        {
            var set = Map(raw, "规则集"); if (!B(set, "enabled", true)) continue; number = 0;
            foreach (var item in L(set, "rules"))
            {
                number++; var entry = Map(item, "导入规则"); if (!ImportedRuleSets.Included(set, entry)) continue;
                AddRule(ImportedRuleSets.Rule(entry, Action(JsonValue.Create(ImportedRuleSets.Target(set, entry)))), $"rule-set:{S(set, "id")}[{number}]", "imported");
            }
        }
        number = 0;
        foreach (var raw in L(policy, "supplemental_rules")) { number++; var entry = Map(raw, "补充规则"); AddRule(Rules.WithAction(entry["match"], Action(entry["action"]), inputFormat: "match"), $"supplemental[{number}]", "supplemental"); }
        if (B(policy, "domestic_direct", true))
            foreach (string rule in new[] { "GEOSITE,geolocation-!cn," + Action(JsonValue.Create("PROXY")), "GEOSITE,CN,DIRECT", "GEOIP,CN,DIRECT" }) AddRule(rule, "domestic", "baseline");
        AddRule("MATCH," + Action(policy["final"]), "final", "final");
        var (rules, mergeReport) = Rules.MergeRules(candidates);
        var runtime = M(settings, "runtime");
        if (runtime.Any(x => new[] { "proxies", "proxy-providers", "proxy-groups", "rules", "rule-providers" }.Contains(x.Key))) throw new BuildException("runtime 不能覆盖生成器管理的代理、分组和规则字段");
        var configuration = Merge(O("mixed-port", 7890, "allow-lan", false, "mode", "rule", "log-level", "info", "ipv6", false, "geodata-mode", true, "geodata-loader", "memconservative", "profile", O("store-selected", true)), runtime);
        foreach (var residence in residences) proxies.Push(residence?.DeepClone());
        configuration["proxies"] = proxies; configuration["proxy-groups"] = groups; configuration["rules"] = rules;
        SubscriptionHosts.Apply(configuration, hosts, "订阅");
        SubscriptionHosts.Required(M(configuration, "hosts"), proxies, "合并订阅");
        string dnsExit = DnsPrivacy.Apply(configuration, policy, "住宅出口", "机场入口");
        if (dnsExit.Length == 0 && policy["dns"] is not null) configuration["dns"] = Map(policy["dns"], "policy.dns").DeepClone();
        var dnsRouting = dnsExit.Length > 0 ? null : DnsRouting.Apply(configuration, policy, candidates);
        var embeddedGeosite = geoMode == "mc2-inline" ? await EmbedGeositeAsync(configuration, M(policy, "geosite_sources"), fetch, Path.GetDirectoryName(policyPath)!) : new JsonArray();
        var embeddedAsn = geoMode == "mc2-inline" ? await EmbedAsnAsync(configuration, M(policy, "asn_sources"), fetch, Path.GetDirectoryName(policyPath)!) : new JsonArray();
        Nodes.CheckGraph(configuration); var references = ValidateDns(configuration);
        var report = O("built_at", DateTimeOffset.UtcNow.ToString("O"), "sources", summaries, "remote_rules", remoteSummaries, "fetches", fetch.Events,
            "rule_merge", mergeReport, "rule_compiler_version", Rules.Version, "hosts_compiler_version", SubscriptionHosts.Version, "embedded_geosite", embeddedGeosite, "embedded_asn", embeddedAsn,
            "geo_mode", geoMode, "geo_rules_version", GeoRules.Version, "required_geodata", GeoRules.RequiredDatabases(configuration),
            "embedded_rule_count", embeddedGeosite.Concat(embeddedAsn).Sum(x => I(Map(x, "embedded"), "entries", 0)),
            "proxy_count", proxies.Count, "rule_count", rules.Count, "priority_order", A(tiers.Keys.Select(x => (JsonNode?)JsonValue.Create(x))),
            "selection", "priority tiers: url-test; tiers: fallback", "contains_subscription_urls", false,
            "dns", O("configured", configuration.ContainsKey("dns"), "enabled", B(M(configuration, "dns"), "enable"), "source", dnsExit.Length > 0 ? "privacy" : "policy.yaml", "proxy_exit", dnsExit, "proxy_references", references));
        if (dnsRouting is not null) report["dns_routing"] = dnsRouting;
        return new(configuration, report);
    }
    public static void ValidateResidences(JsonArray residences)
    {
        if (residences.Count == 0) throw new BuildException("住宅列表不能为空");
        foreach (var raw in residences)
        {
            var node = Map(raw, "住宅节点"); Name(node["name"], "住宅节点");
            if (HasPlaceholder(node)) throw new BuildException("住宅节点仍有 FILL_/YOUR_/脱敏占位符，请先填写");
            if (S(node, "type").Length == 0 || !IsString(node["server"]) || string.IsNullOrWhiteSpace(S(node, "server"))) throw new BuildException("住宅节点需要 type 和 server");
            string server = S(node, "server");
            if (Uri.CheckHostName(server.Trim('[', ']')) == UriHostNameType.Unknown || server != server.Trim()) throw new BuildException("服务器应填写域名或 IP，不包含协议、路径或空格");
            int port = I(node, "port", 0); if (port is < 1 or > 65535) throw new BuildException("住宅节点端口必须在 1 至 65535");
        }
    }
    public static JsonArray ValidateDns(JsonObject configuration, ISet<string>? deferredPrefixes = null)
    {
        if (!configuration.ContainsKey("dns")) return new();
        var dns = Map(configuration["dns"], "policy.dns"); var endpoints = new List<JsonNode?>();
        foreach (string key in new[] { "default-nameserver", "proxy-server-nameserver", "nameserver", "fallback", "direct-nameserver" }) if (dns.ContainsKey(key)) endpoints.AddRange(List(dns[key], "policy.dns." + key));
        foreach (string key in new[] { "nameserver-policy", "proxy-server-nameserver-policy" })
            foreach (var (_, value) in M(dns, key)) if (IsString(value)) endpoints.Add(value); else endpoints.AddRange(List(value, "policy.dns." + key));
        var names = L(configuration, "proxies").Concat(L(configuration, "proxy-groups")).Select(x => S(Map(x, "节点/分组"), "name")).Concat(Nodes.Reserved).ToHashSet();
        var references = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            if (!IsString(endpoint) || string.IsNullOrWhiteSpace(S(endpoint))) throw new BuildException("DNS 服务器地址必须是非空字符串");
            string text = S(endpoint); int marker = text.IndexOf('#'); if (marker < 0) continue;
            foreach (string rawOption in text[(marker + 1)..].Split('&'))
            {
                string option = Uri.UnescapeDataString(rawOption);
                if (option.Length == 0 || option.Contains('=') || new[] { "h3", "skip-cert-verify", "ecs-override", "disable-ipv4", "disable-ipv6" }.Contains(option) || Regex.IsMatch(option, @"^disable-qtype-\d+$")) continue;
                if (!names.Contains(option) && option != "RULES" && !(deferredPrefixes?.Any(option.StartsWith) ?? false)) throw new BuildException($"DNS 引用了不存在的代理/分组：{option}；请核对 policy.yaml 中 # 后的名称");
                references.Add(option);
            }
        }
        if (B(dns, "enable") && (references.Any(x => x != "DIRECT") || B(dns, "respect-rules")) && L(dns, "proxy-server-nameserver").Count == 0) throw new BuildException("DNS 经代理发送时，需要配置 proxy-server-nameserver");
        return A(references);
    }
    public static JsonObject? Mc2Dns(JsonObject configuration)
    {
        if (!configuration.ContainsKey("dns")) return null;
        var dns = (JsonObject)Map(configuration["dns"], "dns").DeepClone(); dns["listen"] = "0.0.0.0:23453";
        if (!dns.ContainsKey("enhanced-mode")) dns["enhanced-mode"] = "redir-host";
        return O("dns", dns);
    }
    public static async Task<JsonArray> EmbedGeositeAsync(JsonObject configuration, JsonObject sources, Fetcher fetch, string baseDirectory)
    {
        var summaries = new JsonArray(); var rules = L(configuration, "rules");
        foreach (var (tag, rawSpec) in sources)
        {
            Name(JsonValue.Create(tag), "geosite tag"); var spec = IsString(rawSpec) ? O("url", rawSpec) : Map(rawSpec, "GeoSite 来源");
            var positions = Enumerable.Range(0, rules.Count).Where(i => S(rules[i]).StartsWith("GEOSITE,") && Rules.Split(S(rules[i]))[1].Equals(tag, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (positions.Length == 0) continue;
            var loaded = await fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec), "GeoSite·" + tag, baseDirectory);
            var payload = new List<string>();
            foreach (var entry in List(loaded.Value, "GeoSite"))
            {
                var parts = Rules.SplitFull(S(entry)); if (!new[] { "DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD" }.Contains(parts[0])) throw new BuildException("geosite_sources 只接受域名类规则");
                payload.Add(string.Join(',', parts.Take(2)));
            }
            payload = payload.Distinct().ToList(); string name = "HomeChain-GeoSite-" + tag;
            if (configuration["rule-providers"] is null) configuration["rule-providers"] = new JsonObject();
            M(configuration, "rule-providers")[name] = O("type", "inline", "behavior", "classical", "payload", payload);
            foreach (int i in positions) rules[i] = string.Join(',', new[] { "RULE-SET", name }.Concat(Rules.Split(S(rules[i])).Skip(2)));
            summaries.Push(O("tag", tag, "provider", name, "entries", payload.Count, "positions", A(positions.Select(x => (JsonNode?)JsonValue.Create(x)))));
        }
        return summaries;
    }
    public static async Task<JsonArray> EmbedAsnAsync(JsonObject configuration, JsonObject sources, Fetcher fetch, string baseDirectory)
    {
        var summaries = new JsonArray(); var rules = L(configuration, "rules");
        foreach (var (number, rawSpec) in sources)
        {
            if (!Regex.IsMatch(number, @"^\d+$")) throw new BuildException("asn_sources 的键必须是 ASN 编号");
            var positions = Enumerable.Range(0, rules.Count).Where(i => S(rules[i]).StartsWith("IP-ASN," + number + ",")).ToArray(); if (positions.Length == 0) continue;
            var spec = IsString(rawSpec) ? O("url", rawSpec) : (JsonObject)Map(rawSpec, "ASN 来源").DeepClone(); spec["behavior"] = "ipcidr";
            var loaded = await fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec), "ASN·" + number, baseDirectory);
            var payload = List(loaded.Value, "ASN").Select(x => string.Join(',', Rules.Split(S(x)).Take(2))).Distinct().ToList();
            string name = "HomeChain-ASN-" + number;
            if (configuration["rule-providers"] is null) configuration["rule-providers"] = new JsonObject();
            M(configuration, "rule-providers")[name] = O("type", "inline", "behavior", "ipcidr", "payload", payload.Select(x => Rules.Split(x)[1]));
            foreach (int i in positions)
            {
                var parts = Rules.Split(S(rules[i])); if (parts.Skip(3).Contains("src")) throw new BuildException("asn_sources 暂不支持 src 标志");
                rules[i] = string.Join(',', new[] { "RULE-SET", name }.Concat(parts.Skip(2)));
            }
            summaries.Push(O("asn", number, "provider", name, "entries", payload.Count, "positions", A(positions.Select(x => (JsonNode?)JsonValue.Create(x)))));
        }
        return summaries;
    }
}

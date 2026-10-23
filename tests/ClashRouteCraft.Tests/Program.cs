using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

if (args.Length == 3 && args[0] == "--bridge")
{
    var request = Map(JsonNode.Parse(File.ReadAllText(args[1])), "request");
    JsonObject response;
    try
    {
        var engine = new BuildEngine(); string root = S(request, "root");
        JsonNode? result = S(request, "op") switch
        {
            "yaml" => YamlData.Parse(S(request, "text")),
            "namespace" => Nodes.Namespace(L(request, "nodes"), S(request, "source", "A")),
            "rule" => JsonValue.Create(Rules.WithAction(request["rule"], S(request, "action", "DIRECT"), B(request, "bare", true), S(request, "input", "auto"))),
            "remote" => Rules.ParseRemote(Encoding.UTF8.GetBytes(S(request, "text")), M(request, "spec")),
            "merge" => MergeBridge(L(request, "candidates")),
            "compile" => await CompileBridge(M(request, "settings"), root, B(request, "offline", true)),
            "inspect" => A(ParameterInspection.Inspect(new WorkspaceSession(root, S(request, "project"))).Select(x => (JsonNode?)O("title", x.Title, "state", x.State, "message", x.Message))),
            "build" => await BuildBridge(engine, root, S(request, "project"), B(request, "force"), B(request, "offline", true)),
            "validate-mc2" => await CoreValidation.ValidatePackageAsync(Map(YamlData.Read(S(request, "configuration")), "configuration"), request["dns"] as JsonObject, S(request, "executable"), S(request, "package"), S(request, "log")),
            "save-defaults" => SaveDefaultsBridge(root, M(request, "policy")),
            "migrate-rule-sets" => ImportedRuleSets.Migrate(M(request, "policy")),
            _ => throw new BuildException("unknown operation")
        };
        response = O("ok", true, "result", result);
    }
    catch (Exception ex) { response = O("ok", false, "error", ex.Message, "type", ex.GetType().Name); }
    File.WriteAllText(args[2], response.ToJsonString(JsonOptions)); return;
}
int passed = 0, failed = 0;
async Task Test(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + " :: " + ex); failed++; }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
void Equal(JsonNode? actual, JsonNode? expected) => Check(Canonical(actual) == Canonical(expected), "expected " + Canonical(expected) + " actual " + Canonical(actual));
void Throws(Action action) { try { action(); } catch (BuildException) { return; } throw new Exception("expected BuildException"); }
async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch (BuildException) { return; } throw new Exception("expected BuildException"); }
JsonArray Candidates(params string[] rules) => A(rules.Select((rule, i) => (JsonNode?)O("rule", rule, "source", "list-" + (i + 1), "stage", "remote")));

await Test("YAML text patch preserves comments and accepts new numeric fields", () => Sync(() =>
{
    string text = "# 配置订阅后首次执行刷新。\nsources:\n  - name: AirportA\n    url: https://example.invalid/FILL_SUBSCRIPTION_URL\n";
    var data = Map(YamlData.Parse(text), "settings"); Map(L(data, "sources")[0], "source")["name"] = "AirportRenamed";
    text = YamlEditing.Apply(text, data); Map(L(data, "sources")[0], "source")["priority"] = 10;
    string updated = YamlEditing.Apply(text, data); Check(updated.StartsWith("# 配置")); Check(I(Map(L(Map(YamlData.Parse(updated), "settings"), "sources")[0], "source"), "priority", 0) == 10);
}));
await Test("YAML preserves typed values and credentials", () => Sync(() =>
{
    var data = Map(YamlData.Parse("text: 'on'\nflag: false\nport: 12345\npassword: 'p#a:ss\\word'\n"), "test");
    Check(S(data, "text") == "on" && !B(data, "flag") && I(data, "port", 0) == 12345 && S(data, "password") == "p#a:ss\\word");
    Equal(YamlData.Parse(YamlData.Bytes(data)), data);
}));
await Test("YAML flow collection edits retain closing delimiters", () => Sync(() =>
{
    string text = "# header\n" + O("sources", A(new JsonNode?[] { O("name", "AirportA", "url", "https://example.invalid", "include", Nodes.Asia) }), "selection", O("include", Nodes.Asia)).ToJsonString();
    var data = Map(YamlData.Parse(text), "settings"); Map(L(data, "sources")[0], "source")["priority"] = 10;
    string updated = YamlEditing.Apply(text, data); Check(I(Map(L(Map(YamlData.Parse(updated), "settings"), "sources")[0], "source"), "priority", 0) == 10);
}));
await Test("YAML aliases, explicit merge override and duplicate rejection", () => Sync(() =>
{
    var data = Map(YamlData.Parse("base: &node {type: http, port: 80}\nproxies:\n- {<<: *node, name: 香港, port: 81}\n"), "test");
    Check(I(Map(L(data, "proxies")[0], "node"), "port", 0) == 81); Throws(() => YamlData.Parse("x: 1\nx: 2\n"));
}));
await Test("YAML rejects unsafe tags and cyclic aliases", () => Sync(() => { Throws(() => YamlData.Parse("x: !!python/object:bad {}")); Throws(() => YamlData.Parse("x: &x [*x]")); }));
await Test("Nested logical rule splitting and preserved flags", () => Sync(() =>
{
    Check(Rules.WithAction(JsonValue.Create("AND,((NETWORK,UDP),(DST-PORT,443))"), "REJECT") == "AND,((NETWORK,UDP),(DST-PORT,443)),REJECT");
    Check(Rules.WithAction(JsonValue.Create("IP-CIDR,10.0.0.0/8,OLD,no-resolve"), "REJECT") == "IP-CIDR,10.0.0.0/8,REJECT,no-resolve");
    Throws(() => Rules.WithAction(JsonValue.Create("MATCH,DIRECT"), "REJECT"));
}));
await Test("Duplicate/conflict precedence and normalization", () => Sync(() =>
{
    var (rules, report) = Rules.MergeRules(Candidates("DOMAIN-SUFFIX,Example.COM,DIRECT", "DOMAIN-SUFFIX,example.com,DIRECT", "DOMAIN-SUFFIX,example.com,REJECT"));
    Equal(rules, A("DOMAIN-SUFFIX,example.com,DIRECT", "DOMAIN-SUFFIX,example.com,REJECT")); Check(I(report, "duplicates_removed", 0) == 1 && I(report, "conflicts_resolved", 0) == 0 && I(report, "conflicts_preserved", 0) == 1);
}));
await Test("Domain overlap boundaries and exact exceptions", () => Sync(() =>
{
    var (_, report) = Rules.MergeRules(Candidates("DOMAIN-SUFFIX,example.com,REJECT", "DOMAIN,notexample.com,DIRECT", "DOMAIN,api.example.com,DIRECT", "DOMAIN-SUFFIX,sub.example.com,默认代理", "DOMAIN,sub.api.example.com,默认代理"));
    Check(I(report, "overlap_notices", 0) == 3);
    var (_, exact) = Rules.MergeRules(Candidates("DOMAIN,example.com,REJECT", "DOMAIN,api.example.com,DIRECT")); Check(I(exact, "overlap_notices", 0) == 0);
}));
await Test("IPv4/IPv6 CIDR normalization and flag identity", () => Sync(() =>
{
    var (rules, report) = Rules.MergeRules(Candidates("IP-CIDR,10.1.2.3/8,DIRECT,no-resolve", "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve", "IP-CIDR,10.0.0.0/8,REJECT", "IP-CIDR,10.2.0.0/16,REJECT,no-resolve", "IP-CIDR,2001:db8::1/32,DIRECT", "IP-CIDR6,2001:0db8::/32,REJECT"));
    Check(I(report, "duplicates_removed", 0) == 0 && I(report, "duplicates_preserved", 0) == 1 && I(report, "conflicts_preserved", 0) == 1 && I(report, "overlap_notices", 0) == 1); Check(rules.Any(x => S(x) == "IP-CIDR6,2001:db8::/32,DIRECT"));
}));
await Test("Bounded report details and trie performance", () => Sync(() =>
{
    var candidates = Candidates("DOMAIN-SUFFIX,example.com,DIRECT");
    for (int i = 0; i < 110; i++) candidates.Push(O("rule", "DOMAIN-SUFFIX,example.com,Proxy-" + i, "source", "test", "stage", "remote"));
    var (_, report) = Rules.MergeRules(candidates); Check(L(report, "conflicts").Count == 100 && B(report, "details_truncated"));
    var large = Candidates(Enumerable.Range(0, 10000).Select(i => $"DOMAIN,host{i}.example.com,DIRECT").ToArray()); Check(Rules.MergeRules(large).Rules.Count == 10000);
}));
await Test("Remote list content detection and domain behavior", () => Sync(() =>
{
    foreach (string text in new[] { "# x\npayload: ['DOMAIN-SUFFIX,claude.ai']", "# x\nDOMAIN-SUFFIX,claude.ai\n", "{\"payload\":[\"DOMAIN-SUFFIX,claude.ai\"]}" })
        Equal(Rules.ParseRemote(Encoding.UTF8.GetBytes(text), O()), A("DOMAIN-SUFFIX,claude.ai,DIRECT"));
    Equal(Rules.ParseRemote(Encoding.UTF8.GetBytes("payload: [example.com, '+.example.net']"), O("behavior", "domain")), A("DOMAIN,example.com,DIRECT", "DOMAIN-SUFFIX,example.net,DIRECT"));
    Throws(() => Rules.ParseRemote(Encoding.UTF8.GetBytes("<html>error</html>"), O()));
}));
await Test("Remote shorthand ordering and invalid priority", () => Sync(() =>
{
    var entries = Map(YamlData.Parse("remote_rules:\n- reject: https://example.com/a\n- direct: https://example.com/b\n- reject: https://example.com/c"), "test");
    Equal(A(Rules.RemoteSpecs(entries["remote_rules"]).Select(x => S(x, "action"))), A("REJECT", "DIRECT", "REJECT"));
    Throws(() => Rules.RemoteSpecs(A(new JsonNode?[] { O("url", "https://example.com", "priority", 1) })).ToArray());
}));
await Test("Node dedup prefers actual route over notice alias", () => Sync(() =>
{
    JsonObject Node(string name, int port = 80) => O("name", name, "type", "http", "server", "localhost", "port", port);
    var nodes = A(new JsonNode?[] { Node("套餐到期日期：2027"), Node("香港01"), Node("香港01", 81), Node("别名", 81) });
    Equal(A(Nodes.Namespace(nodes, "A").Select(x => S(Map(x, "node"), "name"))), A("[A] 香港01", "[A] 香港01 (2)"));
}));
await Test("Dependency names, unknown groups and cycles", () => Sync(() =>
{
    Throws(() => Nodes.Namespace(A(new JsonNode?[] { O("name", "HK", "type", "http", "dialer-proxy", "missing") }), "A"));
    Throws(() => Nodes.CheckGraph(O("proxies", A(new JsonNode?[] { O("name", "a", "dialer-proxy", "b") }), "proxy-groups", A(new JsonNode?[] { O("name", "b", "proxies", A("a")) }))));
}));
await Test("Project output, priorities and paired DNS", async () =>
{
    using var fixture = new Fixture(); var result = await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    var config = Map(YamlData.Read(result.OutputPath), "cfg"); var groups = L(config, "proxy-groups").Select(x => Map(x, "g")).ToDictionary(x => S(x, "name"));
    Equal(groups["订阅优先"]["proxies"], A("优先级·1", "优先级·2")); Check(S(Map(L(config, "proxies")[^1], "res"), "dialer-proxy") == "机场入口");
    string dnsPath = Path.Combine(fixture.Root, "output", "SampleOne", "HomeChain.MC2DNS.yaml");
    var dns = Map(YamlData.Read(dnsPath), "dns"); Check(S(M(dns, "dns"), "listen") == "0.0.0.0:23453"); Check(!File.ReadAllText(result.OutputPath).Contains("token=private"));
    // Exercise the emitted text consumed by MC2's awk/xargs port extraction, not only YAML parsing.
    string[] portFields = File.ReadAllLines(dnsPath).Where(line => line.Contains("listen")).Select(line => line.Split(':').ElementAtOrDefault(2) ?? "").ToArray();
    Check(portFields.Length == 1 && portFields[0].Trim() == "23453", "MC2 dnsmasq port extraction would fail");
});
await Test("Project source order overrides legacy numeric priorities", async () =>
{
    using var fixture = new Fixture(); var settings = Map(YamlData.Read(fixture.Settings), "settings"); Map(L(settings, "sources")[1], "source")["priority"] = 10; fixture.Write("settings.yaml", settings);
    var engine = new BuildEngine(); var result = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); Equal(result.Report["priority_order"], A(new JsonNode?[] { JsonValue.Create(1), JsonValue.Create(2) }));
    var list = L(settings, "sources"); var second = list[1]!.DeepClone(); list.RemoveAt(1); list.Insert(0, second); Map(list[0], "source")["priority"] = 999; fixture.Write("settings.yaml", settings);
    result = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); Check(S(Map(L(result.Report, "sources")[0], "source"), "source") == "B");
    var cfg = Map(YamlData.Read(result.OutputPath), "cfg"); var top = L(cfg, "proxy-groups").Select(x => Map(x, "g")).Single(x => S(x, "name") == "优先级·1"); Check(L(top, "proxies").All(x => S(x).StartsWith("[B]")));
    Map(list[0], "source")["enabled"] = false; fixture.Write("settings.yaml", settings);
    result = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); Check(L(result.Report, "sources").Count == 1 && S(Map(L(result.Report, "sources")[0], "s"), "source") == "A");
});
await Test("Remote URL groups preserve options and other sources", () => Sync(() =>
{
    var policy = O("order", A("proxy", "reject", "direct"), "remote_rules", A(new JsonNode?[]
    {
        O("name", "custom", "url", "https://example.com/a", "action", "PROXY", "headers", O("User-Agent", "custom"), "behavior", "domain"),
        O("reject", A("https://example.com/reject")), O("name", "local", "file", "local.list", "action", "DIRECT"),
        O("name", "disabled", "url", "https://example.com/off", "action", "PROXY", "enabled", false),
        O("name", "special", "url", "https://example.com/special", "action", "AIRPORT")
    }));
    ProjectOrdering.SetRemoteUrls(policy, "proxy", " https://example.com/new\r\nhttps://example.com/a\n\n");
    var all = Rules.RemoteSpecs(policy["remote_rules"]).ToArray(); var urls = all.Where(x => ProjectOrdering.IsUrlEntry(x) && ProjectOrdering.RemoteGroup(x) == "proxy").ToArray();
    Check(urls.Select(x => S(x, "url")).SequenceEqual(["https://example.com/new", "https://example.com/a"])); Check(S(M(urls[1], "headers"), "User-Agent") == "custom");
    Check(all.Any(x => S(x, "file") == "local.list") && all.Any(x => !B(x, "enabled", true)) && all.Any(x => S(x, "action") == "AIRPORT"));
    ProjectOrdering.SetRemoteUrls(policy, "proxy", "https://example.com/new\nhttps://example.com/changed");
    var changed = Rules.RemoteSpecs(policy["remote_rules"]).Single(x => S(x, "url") == "https://example.com/changed"); Check(S(M(changed, "headers"), "User-Agent") == "custom" && S(changed, "name") == "custom");
    ProjectOrdering.SetRemoteUrls(policy, "proxy", ""); all = Rules.RemoteSpecs(policy["remote_rules"]).ToArray();
    Check(!all.Any(x => ProjectOrdering.IsUrlEntry(x) && ProjectOrdering.RemoteGroup(x) == "proxy") && all.Length == 4);
}));
await Test("Remote conflicts follow manual group order with stable URL order", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer(); server.Body = "DOMAIN-SUFFIX,conflict.example\n";
    var policy = Map(YamlData.Read(Path.Combine(fixture.Project, "policy.yaml")), "policy"); policy["order"] = A("direct", "reject", "proxy");
    policy["remote_rules"] = A(new JsonNode?[] { O("proxy", A(server.Url + "p1", server.Url + "p2")), O("reject", server.Url + "r"), O("direct", server.Url + "d") }); fixture.Write("policy.yaml", policy);
    var engine = new BuildEngine(); var result = await engine.BuildProjectAsync(fixture.Root, "SampleOne");
    Check(L(Map(YamlData.Read(result.OutputPath), "cfg"), "rules").Any(x => S(x) == "DOMAIN-SUFFIX,conflict.example,DIRECT"));
    policy["order"] = A("proxy", "reject", "direct"); fixture.Write("policy.yaml", policy); result = await engine.BuildProjectAsync(fixture.Root, "SampleOne");
    Check(L(Map(YamlData.Read(result.OutputPath), "cfg"), "rules").Any(x => S(x) == "DOMAIN-SUFFIX,conflict.example,默认代理"));
    var sources = L(result.Report, "remote_rules"); Check(S(Map(sources[0], "r"), "name").EndsWith("proxy[1]") && S(Map(sources[1], "r"), "name").EndsWith("proxy[2]"));
});
await Test("Reports from numeric-priority releases require regeneration", async () =>
{
    using var fixture = new Fixture(); var result = await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    var session = new WorkspaceSession(fixture.Root, "SampleOne"); Check(session.OutputIsCurrent());
    result.Report.Remove("project_ordering_version"); File.WriteAllText(Path.Combine(session.OutputPath, "HomeChain.report.json"), result.Report.ToJsonString(JsonOptions));
    Check(!session.OutputIsCurrent());
});
await Test("Missing snapshot never downloads", async () =>
{
    using var fixture = new Fixture(); File.Delete(Path.Combine(fixture.Project, "subscrip", "A.yaml")); await ThrowsAsync(() => new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)));
});
await Test("Failure preserves old output bundle", async () =>
{
    using var fixture = new Fixture(); var engine = new BuildEngine(); var good = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); byte[] old = File.ReadAllBytes(good.OutputPath);
    var policy = Map(YamlData.Read(Path.Combine(fixture.Project, "policy.yaml")), "policy"); M(policy, "dns")["nameserver"] = A("https://1.1.1.1/dns-query#missing"); fixture.Write("policy.yaml", policy);
    await ThrowsAsync(() => engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true))); Check(File.ReadAllBytes(good.OutputPath).SequenceEqual(old));
});
await Test("Output freshness detects snapshot and output edits", async () =>
{
    using var fixture = new Fixture(); var built = await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); var session = new WorkspaceSession(fixture.Root, "SampleOne");
    Check(session.OutputIsCurrent()); byte[] main = File.ReadAllBytes(built.OutputPath); File.AppendAllText(built.OutputPath, "\n# manual edit\n"); Check(!session.OutputIsCurrent()); File.WriteAllBytes(built.OutputPath, main); Check(session.OutputIsCurrent());
    File.AppendAllText(Path.Combine(fixture.Project, "subscrip", "A.yaml"), "\n# snapshot changed\n"); Check(!session.OutputIsCurrent());
});
await Test("External configuration edits during generation prevent commit", async () =>
{
    using var fixture = new Fixture(); var engine = new BuildEngine(); var built = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); byte[] old = File.ReadAllBytes(built.OutputPath);
    var progress = new TestProgress(stage => { if (stage.Completed == 5) File.AppendAllText(fixture.Settings, "\n# external edit during generation\n"); });
    await ThrowsAsync(() => engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true), progress)); Check(File.ReadAllBytes(built.OutputPath).SequenceEqual(old));
});
await Test("MC2 DNS requires bootstrap resolver", async () =>
{
    using var fixture = new Fixture(); var policy = Map(YamlData.Read(Path.Combine(fixture.Project, "policy.yaml")), "policy"); M(policy, "dns").Remove("proxy-server-nameserver"); fixture.Write("policy.yaml", policy);
    await ThrowsAsync(() => new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)));
});
await Test("Output commit rollback restores every old file", () => Sync(() =>
{
    using var fixture = new Fixture(); string a = Path.Combine(fixture.Root, "a"), b = Path.Combine(fixture.Root, "b"); File.WriteAllText(a, "old-a"); File.WriteAllText(b, "old-b");
    Throws(() => FileBundle.Commit([(a, Encoding.UTF8.GetBytes("new-a")), (b, Encoding.UTF8.GetBytes("new-b"))], replace: (src, dst) => { if (dst == b) throw new IOException("locked"); File.Move(src, dst, true); }));
    Check(File.ReadAllText(a) == "old-a" && File.ReadAllText(b) == "old-b" && !Directory.GetFiles(fixture.Root, "*.building-*").Any());
}));
await Test("Provider expansion and directory containment", async () =>
{
    using var fixture = new Fixture(); using var fetch = new Fetcher(fixture.Project, O(), true);
    var document = O("proxy-providers", O("local", O("type", "file", "path", "subscrip/A.yaml", "override", O("additional-prefix", "供应-", "udp", true))));
    var nodes = await Nodes.CollectAsync(document, fetch, fixture.Project, "A"); Check(S(Map(nodes[0], "node"), "name").StartsWith("供应-"));
    await ThrowsAsync(() => Nodes.CollectAsync(document, fetch, null, "A"));
    await ThrowsAsync(() => Nodes.CollectAsync(O("proxy-providers", O("escape", O("type", "file", "path", "../outside.yaml"))), fetch, fixture.Project, "A"));
});
await Test("Environment references expand without changing source", () => Sync(() =>
{
    Environment.SetEnvironmentVariable("HOMECHAIN_TEST_SECRET", "example-secret"); var source = O("password", "${HOMECHAIN_TEST_SECRET}");
    Check(S(Map(ExpandEnvironment(source), "env"), "password") == "example-secret"); Check(S(source, "password").Contains("${")); Environment.SetEnvironmentVariable("HOMECHAIN_TEST_SECRET", null);
}));
await Test("Invalid names and examples are rejected", () => Sync(() =>
{
    foreach (string name in new[] { "CON", "a/b", "x.", "..", "LPT1.yaml" }) Throws(() => FileName(name));
    using var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "config", "Examples")); File.WriteAllText(Path.Combine(fixture.Root, "config", "Examples", "settings.yaml"), "bad"); Check(BuildEngine.DiscoverProjects(fixture.Root).SequenceEqual(["SampleOne"]));
}));
await Test("HTTP cache fallback preserves good data and redacts tokens", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer(); using var fetch = new Fetcher(fixture.Project, O("trust_env", false));
    var spec = O("url", server.Url + "?token=private"); server.Body = "proxies: [{name: 香港, type: http, server: localhost, port: 80}]";
    var first = await fetch.ReadAsync(spec, bytes => Nodes.Document(bytes), "A"); server.Status = 403;
    var second = await fetch.ReadAsync(spec, bytes => Nodes.Document(bytes), "A"); Equal(first.Value, second.Value); server.Status = 200; server.Body = "<html>bad</html>";
    Equal((await fetch.ReadAsync(spec, bytes => Nodes.Document(bytes), "A")).Value, first.Value); Check(!fetch.Events.ToJsonString().Contains("private"));
});
await Test("Forced subscription failure falls back per source with a warning and old provenance", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer(); var engine = new BuildEngine(); await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    string a = Path.Combine(fixture.Project, "subscrip", "A.yaml"), b = Path.Combine(fixture.Project, "subscrip", "B.yaml"); byte[] oldA = File.ReadAllBytes(a), oldB = File.ReadAllBytes(b);
    var settings = Map(YamlData.Read(fixture.Settings), "settings"); int i = 0; foreach (var source in L(settings, "sources")) Map(source, "s")["url"] = server.Url + (++i == 1 ? "good" : "fail"); fixture.Write("settings.yaml", settings);
    server.FailPath = "/fail"; server.Body = "proxies: [{name: 香港新节点, type: http, server: localhost, port: 8080}]";
    var progress = new List<BuildProgress>(); var built = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Force: true), new TestProgress(progress.Add));
    Check(!File.ReadAllBytes(a).SequenceEqual(oldA) && File.ReadAllBytes(b).SequenceEqual(oldB));
    Check(progress.Any(x => x.State == "warning" && x.Message.Contains("已使用现有缓存") && x.Message.Contains("403") && !x.Message.Contains("没有可用缓存")));
    Check(L(built.Report, "subscription_fetches").Any(x => S(Map(x, "f"), "status") == "snapshot_fallback"));
    var session = new WorkspaceSession(fixture.Root, "SampleOne"); Check(!session.Subscriptions.UrlChanged(Map(L(session.Settings, "sources")[0], "s")) && session.Subscriptions.UrlChanged(Map(L(session.Settings, "sources")[1], "s")));
});
await Test("Forced failure without a usable cache preserves the entire old bundle", async () =>
{
    using var f = new Fixture(); using var server = new LocalServer(); var engine = new BuildEngine(); var old = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    byte[] output = File.ReadAllBytes(old.OutputPath), snapshot = File.ReadAllBytes(Path.Combine(f.Project, "subscrip", "A.yaml")), state = File.ReadAllBytes(Path.Combine(f.Project, SubscriptionState.FileName));
    var settings = Map(YamlData.Read(f.Settings), "s"); int i = 0; foreach (var source in L(settings, "sources")) Map(source, "s")["url"] = server.Url + (++i == 1 ? "good" : "fail"); f.Write("settings.yaml", settings);
    server.FailPath = "/fail"; server.Body = "proxies: [{name: 香港新节点, type: http, server: localhost, port: 8080}]"; File.Delete(Path.Combine(f.Project, "subscrip", "B.yaml"));
    await ThrowsAsync(() => engine.BuildProjectAsync(f.Root, "SampleOne", new(Force: true)));
    Check(File.ReadAllBytes(old.OutputPath).SequenceEqual(output) && File.ReadAllBytes(Path.Combine(f.Project, "subscrip", "A.yaml")).SequenceEqual(snapshot) && File.ReadAllBytes(Path.Combine(f.Project, SubscriptionState.FileName)).SequenceEqual(state));
});
await Test("Successful refresh commits snapshots and default reuses them", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer(); var settings = Map(YamlData.Read(fixture.Settings), "settings"); foreach (var source in L(settings, "sources")) Map(source, "source")["url"] = server.Url; fixture.Write("settings.yaml", settings);
    server.Body = "proxies: [{name: 香港新节点, type: http, server: localhost, port: 8080}]"; var engine = new BuildEngine(); await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Force: true)); server.Status = 403;
    Check(new WorkspaceSession(fixture.Root, "SampleOne").OutputIsCurrent());
    var result = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); Check(I(result.Report, "proxy_count", 0) == 3);
});
await Test("Cancellation leaves previous outputs unchanged", async () =>
{
    using var fixture = new Fixture(); var engine = new BuildEngine(); var result = await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); var old = File.ReadAllBytes(result.OutputPath);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true), cancellation: cancellation.Token); throw new Exception("expected cancellation"); } catch (OperationCanceledException) { }
    Check(File.ReadAllBytes(result.OutputPath).SequenceEqual(old));
});
await Test("Project create and copy preserve inputs and isolate changes", () => Sync(() =>
{
    using var fixture = new Fixture(); WorkspaceSession.CreateProject(fixture.Root, "中文项目", "SampleOne");
    var copy = new WorkspaceSession(fixture.Root, "中文项目"); Check(copy.Documents["settings.yaml"].Text == File.ReadAllText(fixture.Settings));
    Check(File.ReadAllBytes(Path.Combine(copy.ProjectPath, "subscrip", "A.yaml")).SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Project, "subscrip", "A.yaml"))));
    copy.Documents["settings.yaml"].SetText(copy.Documents["settings.yaml"].Text + "\n# isolated\n"); copy.Save(); Check(!File.ReadAllText(fixture.Settings).Contains("isolated"));
    Check(BuildEngine.DiscoverProjects(fixture.Root).Count == 2);
}));
await Test("Project rename migrates config outputs cache and report path", async () =>
{
    using var fixture = new Fixture(); var result = await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    byte[] output = File.ReadAllBytes(result.OutputPath); File.WriteAllText(Path.Combine(fixture.Root, ".cache", "SampleOne", "keep.txt"), "cached");
    ProjectStore.Rename(fixture.Root, "SampleOne", "新项目");
    var renamed = new WorkspaceSession(fixture.Root, "新项目"); Check(renamed.OutputIsCurrent());
    Check(File.ReadAllBytes(Path.Combine(renamed.OutputPath, "HomeChain.yaml")).SequenceEqual(output));
    Check(S(M(renamed.ReadReport()!, "dns"), "mc2_file") == Path.Combine(renamed.OutputPath, "HomeChain.MC2DNS.yaml"));
    Check(File.ReadAllText(Path.Combine(fixture.Root, ".cache", "新项目", "keep.txt")) == "cached");
    Check(!Directory.Exists(fixture.Project) && BuildEngine.DiscoverProjects(fixture.Root).SequenceEqual(["新项目"]));
    await new BuildEngine().BuildProjectAsync(fixture.Root, "新项目", new(Offline: true));
});
await Test("Project names and all destination areas reject collisions", () => Sync(() =>
{
    using var fixture = new Fixture(); foreach (string name in new[] { "examples", "EXAMPLES", ".new-private", "../escape", "CON", "x.", "SampleOne", "sampleone" }) Throws(() => WorkspaceSession.CreateProject(fixture.Root, name));
    WorkspaceSession.CreateProject(fixture.Root, "SampleTwo"); Throws(() => ProjectStore.Rename(fixture.Root, "SampleOne", "SampleTwo"));
    Directory.CreateDirectory(Path.Combine(fixture.Root, "output", "Orphan")); Throws(() => WorkspaceSession.CreateProject(fixture.Root, "Orphan")); Throws(() => ProjectStore.Rename(fixture.Root, "SampleOne", "Orphan"));
    Check(File.Exists(fixture.Settings)); Throws(() => ProjectStore.Delete(fixture.Root, "examples")); Throws(() => ProjectStore.Delete(fixture.Root, "../escape"));
}));
await Test("Project case-only rename and hidden staging discovery", () => Sync(() =>
{
    using var fixture = new Fixture(); ProjectStore.Rename(fixture.Root, "SampleOne", "sampleone");
    Check(BuildEngine.DiscoverProjects(fixture.Root).SequenceEqual(["sampleone"]));
    string staging = Path.Combine(fixture.Root, "config", ".new-test"); Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "settings.yaml"), "{}");
    Check(BuildEngine.DiscoverProjects(fixture.Root).SequenceEqual(["sampleone"]));
}));
await Test("Project delete archives every area and permits reuse of name", async () =>
{
    using var fixture = new Fixture(); var result = await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); byte[] output = File.ReadAllBytes(result.OutputPath);
    string inputs = File.ReadAllText(fixture.Settings); string archive = ProjectStore.Delete(fixture.Root, "SampleOne");
    Check(BuildEngine.DiscoverProjects(fixture.Root).Count == 0 && !Directory.Exists(fixture.Project));
    Check(File.ReadAllText(Path.Combine(archive, "config", "settings.yaml")) == inputs && Directory.Exists(Path.Combine(archive, "cache")));
    Check(File.ReadAllBytes(Path.Combine(archive, "output", "HomeChain.yaml")).SequenceEqual(output));
    WorkspaceSession.CreateProject(fixture.Root, "SampleOne"); Check(new WorkspaceSession(fixture.Root, "SampleOne").Settings is not null);
    Check(File.ReadAllText(Path.Combine(archive, "config", "settings.yaml")) == inputs);
});
await Test("Project changes are blocked during builds and by another instance", async () =>
{
    using var fixture = new Fixture(); bool checkedBusy = false;
    await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true), new TestProgress(_ =>
    {
        if (checkedBusy) return; checkedBusy = true;
        Throws(() => ProjectStore.Rename(fixture.Root, "SampleOne", "Renamed")); Throws(() => ProjectStore.Delete(fixture.Root, "SampleOne")); Throws(() => WorkspaceSession.CreateProject(fixture.Root, "New"));
    })); Check(checkedBusy);
    using var held = new FileStream(Path.Combine(fixture.Root, ".cache", ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Throws(() => new WorkspaceSession(fixture.Root, "SampleOne").Save());
    await ThrowsAsync(() => new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)));
});
await Test("Project failed migration rolls back all directories", async () =>
{
    using var fixture = new Fixture(); var result = await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    byte[] original = File.ReadAllBytes(result.OutputPath); string report = Path.Combine(Path.GetDirectoryName(result.OutputPath)!, "HomeChain.report.json");
    using (var held = new FileStream(report, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try { ProjectStore.Rename(fixture.Root, "SampleOne", "Renamed"); throw new Exception("expected occupied-file failure"); } catch (IOException) { }
    }
    Check(File.Exists(fixture.Settings) && !Directory.Exists(Path.Combine(fixture.Root, "config", "Renamed")));
    Check(File.ReadAllBytes(result.OutputPath).SequenceEqual(original) && new WorkspaceSession(fixture.Root, "SampleOne").OutputIsCurrent());
});
await Test("Subscription rename preserves snapshot identity without downloading or duplicating", async () =>
{
    using var fixture = new Fixture(); var session = new WorkspaceSession(fixture.Root, "SampleOne"); string original = session.SnapshotPath("A");
    int originalCount = I((await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true))).Report, "proxy_count", 0);
    session.ReloadSubscriptionState(); byte[] stateBeforeRename = File.ReadAllBytes(Path.Combine(fixture.Project, SubscriptionState.FileName)); session.RenameSource(0, "New name"); Check(session.SnapshotPath("New name") == original && session.Dirty, "rename changed the pending snapshot binding or failed to mark dirty");
    Check(File.ReadAllBytes(Path.Combine(fixture.Project, SubscriptionState.FileName)).SequenceEqual(stateBeforeRename), "rename wrote state before save"); Check(S(Map(L(new WorkspaceSession(fixture.Root, "SampleOne").Settings, "sources")[0], "s"), "name") == "A", "pending rename changed persisted config");
    session.Save(); var reopened = new WorkspaceSession(fixture.Root, "SampleOne"); Check(reopened.SnapshotPath("New name") == original, "binding lost on reload");
    var result = await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    Check(reopened.OutputIsCurrent(), "renamed snapshot produced a stale output"); Check(I(result.Report, "proxy_count", 0) == originalCount, "rename changed node count");
    Check(Directory.GetFiles(Path.Combine(fixture.Project, "subscrip"), "*.yaml").Length == 2);
    Throws(() => reopened.RenameSource(0, "B")); Check(S(Map(L(reopened.Settings, "sources")[0], "s"), "name") == "New name");
});
await Test("Changed subscription URL survives saving until successful refresh", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer(); var session = new WorkspaceSession(fixture.Root, "SampleOne"); var settings = session.Settings;
    foreach (var raw in L(settings, "sources")) Map(raw, "s")["url"] = server.Url;
    session.Documents["settings.yaml"].Set(settings); Check(session.Subscriptions.UrlChanged(Map(L(settings, "sources")[0], "s"))); session.Save();
    session = new(fixture.Root, "SampleOne"); Check(session.Subscriptions.UrlChanged(Map(L(session.Settings, "sources")[0], "s")));
    var engine = new BuildEngine(); await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    Check(session.Subscriptions.UrlChanged(Map(L(session.Settings, "sources")[0], "s")));
    server.Body = "proxies: [{name: 香港新节点, type: http, server: localhost, port: 8080}]";
    await engine.BuildProjectAsync(fixture.Root, "SampleOne", new(Force: true)); session.ReloadSubscriptionState();
    Check(!session.Subscriptions.UrlChanged(Map(L(session.Settings, "sources")[0], "s")) && session.OutputIsCurrent());
});
await Test("Reusing renamed subscription display name cannot inherit another source snapshot", () => Sync(() =>
{
    using var fixture = new Fixture(); var session = new WorkspaceSession(fixture.Root, "SampleOne"); session.RenameSource(0, "Renamed");
    var settings = session.Settings; var source = O("name", "A", "url", "https://new.example", "enabled", true); session.Subscriptions.Add(source); L(settings, "sources").Push(source); session.Documents["settings.yaml"].Set(settings); session.Save();
    session = new(fixture.Root, "SampleOne"); Check(session.SnapshotPath("A") != session.SnapshotPath("Renamed")); Check(!File.Exists(session.SnapshotPath("A")) && File.Exists(session.SnapshotPath("Renamed")));
}));
await Test("Copied and restored projects retain renamed snapshot associations", async () =>
{
    using var fixture = new Fixture(); var session = new WorkspaceSession(fixture.Root, "SampleOne"); session.RenameSource(0, "Renamed"); session.Save();
    await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    WorkspaceSession.CreateProject(fixture.Root, "Copy", "SampleOne"); await new BuildEngine().BuildProjectAsync(fixture.Root, "Copy", new(Offline: true));
    string archive = ProjectStore.Delete(fixture.Root, "Copy"); Check(ProjectStore.DeletedProjects(fixture.Root).Count == 1);
    Throws(() => ProjectStore.Restore(fixture.Root, archive, "SampleOne")); Check(File.Exists(Path.Combine(archive, "config", "settings.yaml")));
    ProjectStore.Restore(fixture.Root, archive, "Restored"); var restored = new WorkspaceSession(fixture.Root, "Restored");
    Check(restored.OutputIsCurrent() && File.Exists(restored.SnapshotPath("Renamed")) && ProjectStore.DeletedProjects(fixture.Root).Count == 0);
    Check(S(M(restored.ReadReport()!, "dns"), "mc2_file") == Path.Combine(restored.OutputPath, "HomeChain.MC2DNS.yaml"));
});
await Test("Readiness locates missing fields before generation and permits existing local nodes", () => Sync(() =>
{
    using var fixture = new Fixture(); WorkspaceSession.CreateProject(fixture.Root, "New"); var session = new WorkspaceSession(fixture.Root, "New");
    var issues = ProjectReadiness.Inspect(session); Check(issues.Any(x => x.Page == "sources" && x.Field == "SourceUrl0") && !issues.Any(x => x.Field == "ResidenceServer0"));
    Check(!session.Documents.Values.Any(x => x.Text.Contains("FILL_")));
    Check(ProjectReadiness.Inspect(new(fixture.Root, "SampleOne")).Count == 0);
}));
await Test("External snapshot identity changes prevent partial save", () => Sync(() =>
{
    using var fixture = new Fixture(); var session = new WorkspaceSession(fixture.Root, "SampleOne"); session.Save(); string old = File.ReadAllText(fixture.Settings);
    session.RenameSource(0, "Renamed"); File.AppendAllText(Path.Combine(fixture.Project, SubscriptionState.FileName), " "); Throws(() => session.Save());
    Check(File.ReadAllText(fixture.Settings) == old && File.Exists(Path.Combine(fixture.Project, "subscrip", "A.yaml")));
}));
await Test("Project restore rejects archives outside workspace and rolls back occupied reports", async () =>
{
    using var fixture = new Fixture(); await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true)); string archive = ProjectStore.Delete(fixture.Root, "SampleOne");
    Throws(() => ProjectStore.Restore(fixture.Root, fixture.Root, "Escaped"));
    using (var held = new FileStream(Path.Combine(archive, "output", "HomeChain.report.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    { try { ProjectStore.Restore(fixture.Root, archive, "Restored"); throw new Exception("expected occupied-file failure"); } catch (IOException) { } }
    Check(File.Exists(Path.Combine(archive, "config", "settings.yaml")) && !Directory.Exists(Path.Combine(fixture.Root, "config", "Restored")));
});
await Test("Broken settings remain available for raw YAML repair without rewriting", () => Sync(() =>
{
    using var fixture = new Fixture(); File.WriteAllText(fixture.Settings, "sources: [broken"); var session = new WorkspaceSession(fixture.Root, "SampleOne");
    Check(ProjectReadiness.Inspect(session).Any(x => x.Page == "yaml")); Check(!session.Dirty && !File.Exists(Path.Combine(fixture.Project, SubscriptionState.FileName)));
}));
await Test("Portable relocation preserves project snapshots, output, cache and trash", async () =>
{
    using var fixture = new Fixture(); var session = new WorkspaceSession(fixture.Root, "SampleOne"); session.Save();
    await new BuildEngine().BuildProjectAsync(fixture.Root, "SampleOne", new(Offline: true));
    WorkspaceSession.CreateProject(fixture.Root, "Deleted", "SampleOne"); ProjectStore.Delete(fixture.Root, "Deleted");
    string fingerprint = InputFingerprint.Compute(fixture.Project), destination = Path.Combine(fixture.Root, "app with spaces");
    PortableWorkspace.Move(fixture.Root, destination);
    var relocated = new WorkspaceSession(destination, "SampleOne");
    Check(InputFingerprint.Compute(relocated.ProjectPath) == fingerprint && relocated.OutputIsCurrent());
    Check(File.Exists(relocated.SnapshotPath("A")) && ProjectStore.DeletedProjects(destination).Count == 1);
    Check(!Directory.Exists(Path.Combine(fixture.Root, "config")) && !Directory.Exists(Path.Combine(fixture.Root, "output")));
    Check(BuildEngine.DiscoverProjects(destination).SequenceEqual(["SampleOne"]));
    var rebuilt = await new BuildEngine().BuildProjectAsync(destination, "SampleOne", new(Offline: true));
    Check(rebuilt.OutputPath.StartsWith(destination + Path.DirectorySeparatorChar));
    PortableWorkspace.Move(destination, destination);
});
await Test("Portable relocation refuses collisions before moving any input", () => Sync(() =>
{
    using var fixture = new Fixture(); string target = Path.Combine(fixture.Root, "app");
    Directory.CreateDirectory(Path.Combine(target, "output")); File.WriteAllText(Path.Combine(target, "output", "keep.txt"), "keep");
    Directory.CreateDirectory(Path.Combine(fixture.Root, "output"));
    Throws(() => PortableWorkspace.Move(fixture.Root, target));
    Check(File.Exists(fixture.Settings) && File.ReadAllText(Path.Combine(target, "output", "keep.txt")) == "keep" && !Directory.Exists(Path.Combine(target, "config")));
    Throws(() => PortableWorkspace.Move(fixture.Root, Path.Combine(fixture.Project, "app")));
}));
await Test("Portable relocation rolls back when an occupied cache file cannot move", () => Sync(() =>
{
    using var fixture = new Fixture(); string cache = Path.Combine(fixture.Root, ".cache"); Directory.CreateDirectory(cache);
    string locked = Path.Combine(cache, "occupied.data"), target = Path.Combine(fixture.Root, "app"); File.WriteAllText(locked, "keep");
    using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try { PortableWorkspace.Move(fixture.Root, target); throw new Exception("expected occupied-file failure"); }
        catch (IOException) { }
    }
    Check(File.Exists(fixture.Settings) && File.Exists(locked) && !Directory.Exists(Path.Combine(target, "config")));
}));
await Test("Portable relocation honors a running workspace operation", () => Sync(() =>
{
    using var fixture = new Fixture(); string cache = Path.Combine(fixture.Root, ".cache"); Directory.CreateDirectory(cache);
    using var held = new FileStream(Path.Combine(cache, ".workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Throws(() => PortableWorkspace.Move(fixture.Root, Path.Combine(fixture.Root, "app")));
    Check(File.Exists(fixture.Settings));
}));
await Test("Manual rule fields preserve interleaving, raw spelling and advanced flags", () => Sync(() =>
{
    var original = A("DOMAIN,api.example.com", "GEOIP,LAN,no-resolve", "bare.example", "DOMAIN-KEYWORD,uuyc", "DOMAIN-SUFFIX,example.com", "DOMAIN,flagged.example,no-resolve");
    var editor = new ManualRuleEditor(original, true); Equal(editor.Rules, original);
    Check(editor.LanEnabled && editor.Text(ManualRuleKind.Suffix) == "bare.example\nexample.com" && editor.Text(ManualRuleKind.Exact) == "api.example.com");
    Check(editor.Text(ManualRuleKind.Other) == "DOMAIN-KEYWORD,uuyc\nDOMAIN,flagged.example,no-resolve");
    editor.Replace(ManualRuleKind.Suffix, "bare.example\nDOMAIN-SUFFIX,new.example\nthird.example");
    Equal(editor.Rules, A("DOMAIN,api.example.com", "GEOIP,LAN,no-resolve", "bare.example", "DOMAIN-KEYWORD,uuyc", "DOMAIN-SUFFIX,new.example", "DOMAIN-SUFFIX,third.example", "DOMAIN,flagged.example,no-resolve"));
    editor.Replace(ManualRuleKind.Exact, "DOMAIN,api.example.com");
    Check(S(editor.Rules[0]) == "DOMAIN,api.example.com");
}));
await Test("Advanced rule editing and LAN toggle do not duplicate or resurrect typed entries", () => Sync(() =>
{
    var editor = new ManualRuleEditor(A("DOMAIN-SUFFIX,example.com"), true); Check(!editor.LanEnabled);
    editor.Replace(ManualRuleKind.Other, "GEOIP,LAN,no-resolve"); Check(editor.LanEnabled);
    editor.Replace(ManualRuleKind.Other, "GEOIP,LAN,no-resolve\nDOMAIN-KEYWORD,uuyc"); Check(editor.Rules.Count == 3);
    editor.SetLan(true); Check(editor.Rules.Count == 3);
    editor.SetLan(false); Equal(editor.Rules, A("DOMAIN-SUFFIX,example.com", "DOMAIN-KEYWORD,uuyc"));
    editor.SetLan(true); Check(S(editor.Rules[0]) == "GEOIP,LAN,no-resolve");
    var legacy = new ManualRuleEditor(A("geoip,lan,DIRECT", "GEOIP,LAN,no-resolve", "GEOIP,LAN,src", "IP-CIDR,10.0.0.0/8,no-resolve"), true);
    Check(legacy.LanEnabled); legacy.SetLan(false); Equal(legacy.Rules, A("GEOIP,LAN,src", "IP-CIDR,10.0.0.0/8,no-resolve"));
    var proxy = new ManualRuleEditor(A("GEOIP,LAN,no-resolve"), false); Check(!proxy.LanEnabled && proxy.Count(ManualRuleKind.Other) == 1);
}));
await Test("New projects use subscription rules without an added LAN rule; copies retain their settings", () => Sync(() =>
{
    using var fixture = new Fixture(); WorkspaceSession.CreateProject(fixture.Root, "New");
    var fresh = new WorkspaceSession(fixture.Root, "New"); Check(SubscriptionBaseline.Enabled(fresh.Policy) && !new ManualRuleEditor(L(fresh.Policy, "direct"), true).LanEnabled);
    var policy = fresh.Policy; policy["direct"] = new JsonArray(); fresh.Documents["policy.yaml"].Set(policy); fresh.Save(); Check(L(fresh.Policy, "direct").Count == 0);
    WorkspaceSession.CreateProject(fixture.Root, "Copy", "SampleOne");
    Check(!new ManualRuleEditor(L(new WorkspaceSession(fixture.Root, "Copy").Policy, "direct"), true).LanEnabled);
}));
await Test("Remote URL validation reads fresh YAML without writing or accepting stale caches", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer { Body = "payload:\n  - DOMAIN-SUFFIX,example.com\n  - DOMAIN,api.example.com\n" };
    var spec = O("url", server.Url); var http = O("trust_env", false, "cache_dir", Path.Combine(fixture.Root, "probe-cache"));
    var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray();
    var valid = await RemoteRuleValidation.CheckAsync(fixture.Root, spec, http); Check(valid.Success && valid.RuleCount == 2);
    Check(before.SequenceEqual(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order()));
    using (var fetch = new Fetcher(fixture.Root, http)) await fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec), "seed");
    var cached = Directory.GetFiles(Path.Combine(fixture.Root, "probe-cache")).Single(); string hash = Hash(File.ReadAllText(cached));
    server.Status = 403; var failed = await RemoteRuleValidation.CheckAsync(fixture.Root, spec, Merge(http, O("prefer_cache", true, "allow_stale", true)));
    Check(!failed.Success && failed.Message.Contains("403") && Hash(File.ReadAllText(cached)) == hash);
});
await Test("Remote URL validation rejects non-rules, invalid conditions and unsupported formats", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer(); var http = O("trust_env", false);
    foreach (string body in new[] { "<html>login</html>", "# empty\n", "{\"error\":\"denied\"}", "payload:\n - 123", "UNKNOWN,example.com", "IP-CIDR,999.0.0.0/8", "DOMAIN,https://example.com/path", "DOMAIN-SUFFIX,bad domain", "AND,(DOMAIN,example.com)", "DOMAIN,example.com,unexpected,extra" })
    { server.Body = body; Check(!(await RemoteRuleValidation.CheckAsync(fixture.Root, O("url", server.Url), http)).Success, "Accepted invalid rule content"); }
    server.Body = "DOMAIN-KEYWORD,uuyc\nGEOIP,LAN,no-resolve\nAND,((DOMAIN,example.com),(NETWORK,UDP))\n";
    Check((await RemoteRuleValidation.CheckAsync(fixture.Root, O("url", server.Url), http)).RuleCount == 3);
    server.Body = "DOMAIN-REGEX,(?U)example.*\\.com"; Check((await RemoteRuleValidation.CheckAsync(fixture.Root, O("url", server.Url), http)).Success, "Target-core regex dialect was incorrectly checked as .NET syntax");
    Check(!(await RemoteRuleValidation.CheckAsync(fixture.Root, O("url", server.Url, "format", "mrs"), http)).Success);
    Check(!(await RemoteRuleValidation.CheckAsync(fixture.Root, O("url", "file:///example.list"), http)).Success);
});
await Test("Remote URL validation preserves request options and follows HTTP redirects", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer { Body = "payload:\n - '+.example.com'\n - 'api.example.com'" };
    server.RedirectPath = "/redirect";
    var result = await RemoteRuleValidation.CheckAsync(fixture.Root, O("url", server.Url + "redirect", "behavior", "domain", "user_agent", "RuleCheckTest", "headers", O("X-Rule-Test", "present")), O("trust_env", false));
    Check(result.Success && result.RuleCount == 2 && server.RequestHeaders.Contains("X-Rule-Test: present") && server.RequestHeaders.Contains("User-Agent: RuleCheckTest"));
});
await Test("Remote URL validation bounds body size, body time and supports cancellation", async () =>
{
    using var fixture = new Fixture(); using var server = new LocalServer { Body = "DOMAIN,example.com" }; var spec = O("url", server.Url);
    var large = await RemoteRuleValidation.CheckAsync(fixture.Root, spec, O("trust_env", false, "max_bytes", 8)); Check(!large.Success && large.Message.Contains("大小"));
    server.BodyDelayMs = 1500;
    var timed = await RemoteRuleValidation.CheckAsync(fixture.Root, spec, O("trust_env", false, "timeout", 1)); Check(!timed.Success && timed.Message.Contains("超时"));
    using var cancellation = new CancellationTokenSource(100);
    try { await RemoteRuleValidation.CheckAsync(fixture.Root, spec, O("trust_env", false), cancellation.Token); throw new Exception("Cancellation was swallowed"); }
    catch (OperationCanceledException) { }
    Check(!Directory.Exists(Path.Combine(fixture.Root, ".cache")));
});
await Test("Example is immutable, reserved, excluded from builds and is the new project template", async () =>
{
    using var fixture = new Fixture();
    var example = new WorkspaceSession(fixture.Root, "Example");
    Check(example.IsReadOnly && !example.Dirty && !Directory.Exists(example.ProjectPath));
    foreach (var document in example.Documents.Values) { Throws(() => document.SetText("{}")); Throws(() => document.Set(O())); }
    Throws(() => example.Save()); Throws(() => WorkspaceSession.CreateProject(fixture.Root, "example"));
    Throws(() => ProjectStore.Delete(fixture.Root, "Example")); Throws(() => ProjectStore.Rename(fixture.Root, "Example", "Other"));
    try { await new BuildEngine().BuildProjectAsync(fixture.Root, "Example"); throw new Exception("Example build was allowed"); } catch (BuildException) { }
    WorkspaceSession.CreateProject(fixture.Root, "FromExample", "Example");
    var created = new WorkspaceSession(fixture.Root, "FromExample");
    foreach (var (file, document) in example.Documents) Check(created.Documents[file].Text == document.Text);
    Check(!BuildEngine.DiscoverProjects(fixture.Root).Contains("Example") && !created.IsReadOnly);
});
await Test("Native GEO is the default and never reads configured embedding sources", async () =>
{
    using var f = new Fixture(); var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
    policy["domestic_direct"] = true; policy["proxy"] = A("IP-ASN,20473,no-resolve");
    policy["asn_policy"] = O("allow_native", true);
    policy["geosite_sources"] = O("geolocation-!cn", O("file", "missing-geosite.list"));
    policy["asn_sources"] = O("20473", O("url", "http://127.0.0.1:1/must-not-fetch")); f.Write("policy.yaml", policy);
    var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    var config = Map(YamlData.Read(built.OutputPath), "config");
    Check(!config.ContainsKey("rule-providers") && L(config, "rules").Any(x => S(x) == "IP-ASN,20473,默认代理,no-resolve"));
    Check(L(config, "rules").Any(x => S(x) == "GEOSITE,geolocation-!cn,默认代理"));
    Check(S(built.Report, "geo_mode") == "native" && I(built.Report, "embedded_rule_count", -1) == 0);
    Equal(built.Report["required_geodata"], A("ASN.mmdb", "GeoIP.dat", "GeoSite.dat"));
    Check(!L(built.Report, "fetches").Any(x => S(Map(x, "fetch"), "item").StartsWith("GeoSite") || S(Map(x, "fetch"), "item").StartsWith("ASN")));
});
await Test("Explicit MC2 embedding keeps rule order flags and accurate payload counts", async () =>
{
    using var f = new Fixture(); var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
    policy["geo_mode"] = "mc2-inline"; policy["domestic_direct"] = true; policy["proxy"] = A("IP-ASN,20473,no-resolve");
    policy["geosite_sources"] = O("geolocation-!cn", O("file", "geo.list")); policy["asn_sources"] = O("20473", O("file", "asn.list"));
    File.WriteAllText(Path.Combine(f.Project, "geo.list"), "DOMAIN-SUFFIX,foreign.example\nDOMAIN,other.example\nDOMAIN,other.example\n");
    File.WriteAllText(Path.Combine(f.Project, "asn.list"), "1.2.3.0/24\n2001:db8::/32\n"); f.Write("policy.yaml", policy);
    var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    var config = Map(YamlData.Read(built.OutputPath), "config");
    Check(I(built.Report, "embedded_rule_count", 0) == 4 && M(config, "rule-providers").Count == 2);
    Check(L(config, "rules").Any(x => S(x) == "RULE-SET,HomeChain-ASN-20473,默认代理,no-resolve"));
    Check(S(L(config, "rules")[^4]) == "RULE-SET,HomeChain-GeoSite-geolocation-!cn,默认代理");
    Check(!L(built.Report, "required_geodata").Any(x => S(x) == "ASN.mmdb"));
});
await Test("Invalid GEO mode fails instead of selecting an implicit fallback", async () =>
{
    using var f = new Fixture(); var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
    policy["geo_mode"] = "typo"; f.Write("policy.yaml", policy);
    try { await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); throw new Exception("Invalid mode accepted"); }
    catch (BuildException ex) { Check(ex.Message.Contains("geo_mode")); }
});
await Test("GEO dependencies include nested expressions DNS and MMDB mode", () => Sync(() =>
{
    Equal(A(GeoRules.RequiredDatabases(O("rules", A("AND,((GEOSITE,cn),(SRC-IP-ASN,20473)),DIRECT"), "dns", O("fallback-filter", O("geoip", true))))), A("ASN.mmdb", "GeoSite.dat", "country.mmdb"));
    Equal(A(GeoRules.RequiredDatabases(O("rules", A("MATCH,DIRECT")))), A());
}));
await Test("Missing native databases abort before core launch and preserve old output", async () =>
{
    using var f = new Fixture(); var engine = new BuildEngine();
    var previous = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); var original = File.ReadAllBytes(previous.OutputPath);
    var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy"); policy["domestic_direct"] = true; f.Write("policy.yaml", policy);
    string fakeCore = Path.Combine(f.Root, "unlaunchable.exe"); File.WriteAllText(fakeCore, "not an executable; must never run");
    var settings = Map(YamlData.Read(f.Settings), "settings"); settings["validation"] = O("mihomo", fakeCore, "required", true); f.Write("settings.yaml", settings);
    try { await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); throw new Exception("Missing data accepted"); }
    catch (BuildException ex) { Check(ex.Message.Contains("内核校验缺少")); }
    Check(original.SequenceEqual(File.ReadAllBytes(previous.OutputPath)));
});
await Test("Old GEO generation reports are marked stale", async () =>
{
    using var f = new Fixture(); var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    var session = new WorkspaceSession(f.Root, "SampleOne"); Check(session.OutputIsCurrent());
    built.Report.Remove("geo_rules_version"); File.WriteAllText(Path.Combine(session.OutputPath, "HomeChain.report.json"), built.Report.ToJsonString());
    Check(!session.OutputIsCurrent());
});
await Test("Python cache-key JSON compatibility", () => Sync(() => Check(PythonJson(new JsonArray("https://example.com/?a=1&b=2", O("User-Agent", "clash-verge/v2.4.7"))) == "[\"https://example.com/?a=1&b=2\", {\"User-Agent\": \"clash-verge/v2.4.7\"}]")));
await Test("Routing defaults persist in Example and new projects without private inputs or DNS", () => Sync(() =>
{
    using var f = new Fixture(); var session = new WorkspaceSession(f.Root, "SampleOne"); var policy = session.Policy;
    policy["proxy"] = A("DOMAIN,play.example"); policy["custom"] = "not-a-routing-default";
    RuleDefaults.Save(f.Root, policy);
    var defaults = Map(YamlData.Read(RuleDefaults.FilePath(f.Root)), "defaults");
    Check(!defaults.ContainsKey("dns") && !defaults.ContainsKey("custom"));
    var example = new WorkspaceSession(f.Root, "Example");
    Equal(example.Policy["proxy"], policy["proxy"]); Check(example.IsReadOnly && !example.Dirty);
    Throws(() => example.Documents["policy.yaml"].Set(policy));
    WorkspaceSession.CreateProject(f.Root, "FromDefault"); var created = new WorkspaceSession(f.Root, "FromDefault");
    Equal(created.Policy["proxy"], policy["proxy"]);
    Check(S(Map(L(created.Settings, "sources")[0], "source"), "url") == "" && created.Residences.Count == 0);
    Equal(created.Policy["dns"], Map(YamlData.Parse(WorkspaceSession.ExampleDocuments()["policy.yaml"]), "factory")["dns"]);
    var applied = RuleDefaults.Apply(session.Policy, defaults); Equal(applied["dns"], session.Policy["dns"]);
    Check(File.ReadAllText(Path.Combine(f.Project, "policy.yaml")) == session.Documents["policy.yaml"].Baseline);
}));
await Test("Explicit legacy DNS recommendation splits domestic queries and retains overseas proxy DNS", () => Sync(() =>
{
    using var f = new Fixture();
    // Saved routing defaults must not resurrect the old overseas direct resolver.
    RuleDefaults.Save(f.Root, new WorkspaceSession(f.Root, "SampleOne").Policy);
    WorkspaceSession.CreateProject(f.Root, "DnsDefault");
    foreach (var policy in new[] { DnsSettings.ApplyRecommended(new WorkspaceSession(f.Root, "Example").Policy), DnsSettings.ApplyRecommended(new WorkspaceSession(f.Root, "DnsDefault").Policy) })
    {
        Check(B(policy, "dns_follow_rules"));
        var dns = M(policy, "dns");
        Equal(dns["direct-nameserver"], A("https://223.5.5.5/dns-query#DIRECT"));
        Check(!B(dns, "direct-nameserver-follow-policy"));
        Equal(M(dns, "nameserver-policy"), O("geosite:cn", A("https://223.5.5.5/dns-query#DIRECT")));
        Equal(dns["nameserver"], A("https://1.1.1.1/dns-query#住宅出口"));
        Equal(dns["fallback"], A("https://8.8.8.8/dns-query#住宅出口"));
        Equal(dns["proxy-server-nameserver"], A("https://223.5.5.5/dns-query#DIRECT"));
        Equal(A(GeoRules.RequiredDatabases(O("rules", A("MATCH,DIRECT"), "dns", dns.DeepClone()))), A("GeoSite.dat"));
    }
}));
await Test("Invalid routing default cannot replace the saved template", () => Sync(() =>
{
    using var f = new Fixture(); var policy = new WorkspaceSession(f.Root, "SampleOne").Policy;
    RuleDefaults.Save(f.Root, policy); string before = File.ReadAllText(RuleDefaults.FilePath(f.Root));
    policy["order"] = A("direct", "direct", "proxy"); Throws(() => RuleDefaults.Save(f.Root, policy));
    Check(File.ReadAllText(RuleDefaults.FilePath(f.Root)) == before);
    policy.Remove("order"); policy["proxy"] = A("DOMAIN-SUFFIX,https://invalid.example/path"); Throws(() => RuleDefaults.Save(f.Root, policy));
}));
await Test("Parameter preflight reads unsaved inputs without network, saves or output changes", () => Sync(() =>
{
    using var f = new Fixture(); using var server = new LocalServer(); var session = new WorkspaceSession(f.Root, "SampleOne");
    var settings = session.Settings; Map(L(settings, "sources")[0], "source")["url"] = server.Url;
    session.Documents["settings.yaml"].Set(settings);
    var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
    var result = ParameterInspection.Inspect(session).ToArray();
    Check(result.All(x => x.State != "fail"), string.Join(";", result.Where(x => x.State == "fail").Select(x => x.Title + ":" + x.Message)));
    Check(result.Any(x => x.Title.Contains("缓存") && x.Message.Contains("1 个符合筛选")));
    Check(result.Any(x => x.Title.Contains("DNS") && x.State == "pass"));
    Check(session.Dirty && server.RequestHeaders == "");
    Check(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Length == before.Count);
    foreach (var file in before) Check(file.Value.SequenceEqual(File.ReadAllBytes(file.Key)));
}));
await Test("Parameter preflight finds independent cache residence rule and DNS errors", () => Sync(() =>
{
    using var f = new Fixture(); var session = new WorkspaceSession(f.Root, "SampleOne");
    File.WriteAllText(session.SnapshotPath("A"), "invalid: cache");
    var residences = session.Residences; Map(residences[0], "residence")["port"] = 0; session.SetResidences(residences);
    var policy = session.Policy; policy["proxy"] = A("DOMAIN-SUFFIX,https://bad.example/path"); M(policy, "dns")["nameserver"] = A("not-a-dns-address"); session.Documents["policy.yaml"].Set(policy);
    var checks = ParameterInspection.Inspect(session).ToArray();
    foreach (string part in new[] { "缓存", "连接参数", "代理 · 手工", "DNS" }) Check(checks.Any(x => x.Title.Contains(part) && x.State == "fail"), part);
    Check(checks.Where(x => x.State == "fail").All(x => x.Issue is not null));
    Check(checks.Single(x => x.Title == "代理 · 手工名单").Issue?.Field == "Suffix_proxy", "Invalid domain rule does not locate its actual input field");
}));
await Test("Preflight distinguishes missing caches and catches DNS references and disabled sources", () => Sync(() =>
{
    using var f = new Fixture(); var session = new WorkspaceSession(f.Root, "SampleOne");
    File.Delete(session.SnapshotPath("A"));
    var result = ParameterInspection.Inspect(session).ToArray(); Check(result.Any(x => x.Title.Contains("缓存") && x.State == "warning"));
    var policy = session.Policy; M(policy, "dns")["nameserver"] = A("https://1.1.1.1/dns-query#MissingGroup"); session.Documents["policy.yaml"].Set(policy);
    Check(ParameterInspection.Inspect(session).Any(x => x.Title.Contains("DNS") && x.State == "fail"));
    M(policy, "dns")["nameserver"] = A("https://1.1.1.1/dns-query#PROXY"); session.Documents["policy.yaml"].Set(policy);
    Check(ParameterInspection.Inspect(session).Any(x => x.Title.Contains("DNS") && x.State == "fail"), "DNS accepted a rule-only alias as an actual proxy group");
    var settings = session.Settings; foreach (var raw in L(settings, "sources")) Map(raw, "source")["enabled"] = false; session.Documents["settings.yaml"].Set(settings);
    Check(ParameterInspection.Inspect(session).Any(x => x.Title == "订阅配置" && x.State == "fail"));
    Map(L(settings, "sources")[0], "source")["enabled"] = "invalid"; session.Documents["settings.yaml"].Set(settings);
    Check(ParameterInspection.Inspect(session).Last().Title == "本地校验资源");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { ParameterInspection.Inspect(session, canceled.Token).ToArray(); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
}));
await Test("Cached subscription ports accept quoted decimal values without rewriting", () => Sync(() =>
{
    using var f = new Fixture(); var session = new WorkspaceSession(f.Root, "SampleOne");
    string path = session.SnapshotPath("A"); var document = Nodes.Document(File.ReadAllBytes(path));
    var nodes = L(document, "proxies"); Map(nodes[0], "node")["port"] = "443"; Map(nodes[1], "node")["port"] = "65535";
    File.WriteAllBytes(path, YamlData.Bytes(document)); byte[] original = File.ReadAllBytes(path);
    var checks = ParameterInspection.Inspect(session).ToArray();
    Check(checks.Single(x => x.Title == "订阅 1 · 缓存与筛选").State == "pass");
    Check(original.SequenceEqual(File.ReadAllBytes(path)), "Preflight rewrote quoted ports");
    foreach (JsonNode? invalid in new JsonNode?[] { JsonValue.Create("0"), JsonValue.Create("65536"), JsonValue.Create("https"), JsonValue.Create("443.5"), JsonValue.Create(true), JsonValue.Create(-1), null })
    {
        Map(nodes[0], "node")["port"] = invalid?.DeepClone(); File.WriteAllBytes(path, YamlData.Bytes(document));
        var failed = ParameterInspection.Inspect(session).Single(x => x.Title == "订阅 1 · 缓存与筛选");
        Check(failed.State == "fail" && failed.Message.Contains("缓存节点 #1") && failed.Message.Contains("1～65535"), "Invalid cache port accepted or unlocatable");
    }
    Throws(() => Integer(JsonValue.Create("443"), "strict field"));
}));
await Test("Preflight and build reject local errors before any subscription request", async () =>
{
    using var server = new LocalServer(); server.Body = "proxies: [{name: 香港, type: http, server: localhost, port: 8080}]";
    for (int item = 0; item < 4; item++)
    {
        using var f = new Fixture(); var settings = Map(YamlData.Read(f.Settings), "settings");
        foreach (var source in L(settings, "sources")) Map(source, "s")["url"] = server.Url;
        var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
        if (item == 0) Map(L(settings, "sources")[0], "s")["on_error"] = "skip";
        if (item == 1) settings["runtime"] = O("rules", A("MATCH,DIRECT"));
        if (item == 2) M(policy, "dns")["nameserver"] = A("https://1.1.1.1/dns-query#absent");
        if (item == 3) policy["remote_rules"] = A(new JsonNode?[] { O("name", "invalid", "url", server.Url, "file", "local.list") });
        f.Write("settings.yaml", settings); f.Write("policy.yaml", policy);
        Check(ParameterInspection.Inspect(new WorkspaceSession(f.Root, "SampleOne")).Any(x => x.State == "fail"));
        await ThrowsAsync(() => new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Force: true)));
    }
    Check(server.Requests == 0, "Invalid local inputs consumed a subscription URL");
});
await Test("Generation reuses existing caches until force is selected and fills missing online sources", async () =>
{
    using var f = new Fixture(); using var online = new LocalServer(); using var changed = new LocalServer();
    online.Body = changed.Body = "proxies: [{name: 香港新节点, type: http, server: localhost, port: 8080}]";
    var settings = Map(YamlData.Read(f.Settings), "settings"); var sources = L(settings, "sources");
    Map(sources[0], "s")["url"] = ""; Map(sources[1], "s")["url"] = online.Url; f.Write("settings.yaml", settings);
    string imported = Path.Combine(f.Project, "subscrip", "A.yaml"), downloaded = Path.Combine(f.Project, "subscrip", "B.yaml");
    byte[] original = File.ReadAllBytes(imported); File.Delete(downloaded);
    var engine = new BuildEngine(); await engine.BuildProjectAsync(f.Root, "SampleOne");
    Check(online.Requests == 1 && File.ReadAllBytes(imported).SequenceEqual(original));
    await engine.BuildProjectAsync(f.Root, "SampleOne", new(Force: true)); Check(online.Requests == 2);
    Map(sources[1], "s")["url"] = changed.Url; f.Write("settings.yaml", settings);
    await engine.BuildProjectAsync(f.Root, "SampleOne"); Check(changed.Requests == 0 && online.Requests == 2);
    await engine.BuildProjectAsync(f.Root, "SampleOne", new(Force: true)); Check(changed.Requests == 1);
    File.WriteAllText(downloaded, "broken: cache");
    await engine.BuildProjectAsync(f.Root, "SampleOne", new(Force: true));
    Check(changed.Requests == 2 && File.ReadAllBytes(imported).SequenceEqual(original), "Refresh tried to parse a cache scheduled for replacement");
});
await Test("Rule refresh and cache expiry never refresh subscription URLs", async () =>
{
    using var f = new Fixture(); using var subscriptions = new LocalServer(); using var rules = new LocalServer();
    var settings = Map(YamlData.Read(f.Settings), "settings"); foreach (var source in L(settings, "sources")) Map(source, "s")["url"] = subscriptions.Url;
    f.Write("settings.yaml", settings); var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "p");
    policy["remote_rules"] = A(new JsonNode?[] { O("proxy", A(rules.Url)) }); f.Write("policy.yaml", policy);
    rules.Body = "DOMAIN,old.example"; var engine = new BuildEngine(); await engine.BuildProjectAsync(f.Root, "SampleOne", new(AutoFetch: false));
    rules.Body = "DOMAIN,new.example";
    var cached = await engine.BuildProjectAsync(f.Root, "SampleOne", new(AutoFetch: false));
    Check(rules.Requests == 1 && File.ReadAllText(cached.OutputPath).Contains("old.example"));
    var updated = await engine.BuildProjectAsync(f.Root, "SampleOne", new(RefreshRules: true, AutoFetch: false));
    Check(rules.Requests == 2 && subscriptions.Requests == 0 && File.ReadAllText(updated.OutputPath).Contains("new.example"));
    string cache = Directory.GetFiles(Path.Combine(f.Root, ".cache", "SampleOne"), "*.data").Single();
    File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-2)); rules.Status = 403;
    var progress = new List<BuildProgress>();
    var stale = await engine.BuildProjectAsync(f.Root, "SampleOne", new(AutoFetch: false), new TestProgress(progress.Add));
    Check(rules.Requests == 3 && subscriptions.Requests == 0 && progress.Any(x => x.State == "warning"));
    Check(L(stale.Report, "fetches").Any(x => B(Map(x, "fetch"), "stale")), "Stale fallback was not reported");
    Check(new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent());
    File.WriteAllText(cache, "DOMAIN,changed-cache.example");
    Check(!new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent(), "Changed rule cache retained current output status");
});
await Test("Routing defaults reject project-specific actions and local dependencies atomically", () => Sync(() =>
{
    using var f = new Fixture(); var policy = new WorkspaceSession(f.Root, "SampleOne").Policy;
    RuleDefaults.Save(f.Root, policy); byte[] before = File.ReadAllBytes(RuleDefaults.FilePath(f.Root));
    foreach (var invalid in new[] { O("final", "住宅"), O("first", A(new JsonNode?[] { O("match", "DOMAIN,a.example", "action", "[A] 香港同名") })), O("remote_rules", A(new JsonNode?[] { O("file", "local.list", "action", "PROXY") })), O("geosite_sources", O("cn", O("file", "cn.list"))) })
    {
        Throws(() => RuleDefaults.Save(f.Root, Merge(policy, invalid)));
        Check(File.ReadAllBytes(RuleDefaults.FilePath(f.Root)).SequenceEqual(before));
    }
    RuleDefaults.Save(f.Root, Merge(policy, O("final", "住宅出口")));
    WorkspaceSession.CreateProject(f.Root, "Portable"); Check(S(new WorkspaceSession(f.Root, "Portable").Policy, "final") == "住宅出口");
}));
await Test("Clash target does not require MC2 and removes obsolete DNS on successful commit", async () =>
{
    using var f = new Fixture(); var engine = new BuildEngine(); await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    string dns = Path.Combine(f.Root, "output", "SampleOne", "HomeChain.MC2DNS.yaml"); Check(File.Exists(dns));
    var settings = Map(YamlData.Read(f.Settings), "s"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
    var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "p"); policy.Remove("dns"); f.Write("policy.yaml", policy);
    var result = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    Check(!File.Exists(dns) && !M(result.Report, "dns").ContainsKey("mc2_sha256") && S(result.Report, "target") == "clash");
    string fake = Path.Combine(f.Root, "core.exe"); File.WriteAllText(fake, "validation path exists");
    ConfigurationValidation.Resources(O("target", "clash", "validation", O("required", true, "mihomo", fake, "mc2_package", "missing")), f.Project, policy);
    settings["target"] = "mc2"; f.Write("settings.yaml", settings);
    Check(ParameterInspection.Inspect(new WorkspaceSession(f.Root, "SampleOne")).Any(x => x.State == "fail" && x.Message.Contains("MC2 目标需要 DNS")));
    await ThrowsAsync(() => engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)));
});
await Test("Failed obsolete DNS removal rolls back the complete output bundle", () => Sync(() =>
{
    using var f = new Fixture(); string main = Path.Combine(f.Root, "main.yaml"), dns = Path.Combine(f.Root, "dns.yaml");
    File.WriteAllText(main, "old-main"); File.WriteAllText(dns, "old-dns");
    using (var held = new FileStream(dns, FileMode.Open, FileAccess.Read, FileShare.Read))
        Throws(() => FileBundle.Commit([(main, Encoding.UTF8.GetBytes("new-main"))], removals: [dns]));
    Check(File.ReadAllText(main) == "old-main" && File.ReadAllText(dns) == "old-dns");
}));
await Test("Local rule content and environment changes invalidate existing outputs", async () =>
{
    using var f = new Fixture(); string file = Path.Combine(f.Project, "local.list"); File.WriteAllText(file, "DOMAIN,one.example");
    var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "p"); policy["remote_rules"] = A(new JsonNode?[] { O("file", "local.list", "action", "PROXY") }); f.Write("policy.yaml", policy);
    var engine = new BuildEngine(); await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    Check(new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent());
    var timestamp = File.GetLastWriteTimeUtc(file); File.WriteAllText(file, "DOMAIN,two.example"); File.SetLastWriteTimeUtc(file, timestamp);
    Check(!new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent());
    await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); Check(new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent());
    try
    {
        Environment.SetEnvironmentVariable("HOMECHAIN_DEPENDENCY_TEST", "first");
        var session = new WorkspaceSession(f.Root, "SampleOne"); var residences = session.Residences; Map(residences[0], "r")["password"] = "${HOMECHAIN_DEPENDENCY_TEST}"; session.SetResidences(residences); session.Save();
        await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); Check(new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent());
        Environment.SetEnvironmentVariable("HOMECHAIN_DEPENDENCY_TEST", "second"); Check(!new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent());
    }
    finally { Environment.SetEnvironmentVariable("HOMECHAIN_DEPENDENCY_TEST", null); }
});
await Test("Validation fingerprints and GeoData copying compare content instead of timestamps", () => Sync(() =>
{
    using var f = new Fixture(); string assets = Path.Combine(f.Root, "assets"), cache = Path.Combine(f.Root, "cache"); Directory.CreateDirectory(assets);
    string data = Path.Combine(assets, "GeoSite.dat"), core = Path.Combine(assets, "core.exe"); File.WriteAllText(data, "aaaa"); File.WriteAllText(core, "core-1");
    var settings = O("target", "clash", "validation", O("mihomo", core, "data_dir", assets)); var config = O("rules", A("GEOSITE,cn,DIRECT"));
    var first = DependencySnapshot.Validation(settings, f.Project, config); CoreValidation.CopyGeoData(assets, cache);
    var timestamp = File.GetLastWriteTimeUtc(data); File.WriteAllText(data, "bbbb"); File.SetLastWriteTimeUtc(data, timestamp); CoreValidation.CopyGeoData(assets, cache);
    Check(File.ReadAllText(Path.Combine(cache, "GeoSite.dat")) == "bbbb" && Canonical(first) != Canonical(DependencySnapshot.Validation(settings, f.Project, config)));
    first = DependencySnapshot.Validation(settings, f.Project, config); File.WriteAllText(core, "core-2"); Check(Canonical(first) != Canonical(DependencySnapshot.Validation(settings, f.Project, config)));
}));
await Test("Wildcard DNS listen syntax and local rule errors use shared checks", async () =>
{
    using var f = new Fixture(); var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "p"); M(policy, "dns")["listen"] = ":1053"; f.Write("policy.yaml", policy);
    Check(!ParameterInspection.Inspect(new WorkspaceSession(f.Root, "SampleOne")).Any(x => x.State == "fail"));
    await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    policy["remote_rules"] = A(new JsonNode?[] { O("file", "missing.list", "action", "PROXY") }); f.Write("policy.yaml", policy);
    Check(ParameterInspection.Inspect(new WorkspaceSession(f.Root, "SampleOne")).Any(x => x.State == "fail"));
});
await Test("Output status separates modified inputs from changed validation resources", async () =>
{
    using var f = new Fixture(); var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    string core = Path.Combine(f.Root, "core.exe"); File.WriteAllText(core, "version-1");
    var settings = Map(YamlData.Read(f.Settings), "s"); settings["validation"] = O("mihomo", core); f.Write("settings.yaml", settings);
    // Synthetic resource metadata isolates status classification; it does not claim this mock core ran.
    var report = built.Report; report["input_fingerprint"] = InputFingerprint.Compute(f.Project);
    report["validation_inputs"] = DependencySnapshot.Validation(settings, f.Project, Map(YamlData.Read(built.OutputPath), "output"));
    File.WriteAllText(Path.Combine(f.Root, "output", "SampleOne", "HomeChain.report.json"), report.ToJsonString(JsonOptions));
    Check(new WorkspaceSession(f.Root, "SampleOne").OutputStatus() == OutputFreshness.Current);
    File.WriteAllText(core, "version-2"); Check(new WorkspaceSession(f.Root, "SampleOne").OutputStatus() == OutputFreshness.ValidationChanged);
    File.AppendAllText(f.Settings, "\n# changed input\n"); Check(new WorkspaceSession(f.Root, "SampleOne").OutputStatus() == OutputFreshness.InputsChanged);
});
await Test("Invalid remote rule syntax cannot overwrite a valid rule cache", async () =>
{
    using var f = new Fixture(); using var server = new LocalServer(); var spec = O("url", server.Url);
    using var fetch = new Fetcher(f.Project, O("trust_env", false, "allow_stale", false));
    server.Body = "DOMAIN,valid.example"; await fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec), "rules");
    string file = Directory.GetFiles(Path.Combine(f.Project, ".cache"), "*.data").Single(); byte[] before = File.ReadAllBytes(file);
    server.Body = "DOMAIN,https://invalid.example/path";
    await ThrowsAsync(() => fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec), "rules"));
    Check(File.ReadAllBytes(file).SequenceEqual(before));
});
await Test("Cached generation persists provenance across restarts and header changes", async () =>
{
    using var f = new Fixture(); using var server = new LocalServer(); server.Body = "proxies: [{name: 香港, type: http, server: localhost, port: 8080}]";
    var settings = Map(YamlData.Read(f.Settings), "s"); foreach (var source in L(settings, "sources")) Map(source, "s")["url"] = server.Url; f.Write("settings.yaml", settings);
    await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    Check(File.Exists(Path.Combine(f.Project, SubscriptionState.FileName)), "Cached generation did not persist source identity");
    Map(L(settings, "sources")[0], "s")["headers"] = O("X-Test", "changed"); f.Write("settings.yaml", settings);
    var restarted = new WorkspaceSession(f.Root, "SampleOne"); Check(restarted.Subscriptions.UrlChanged(Map(L(restarted.Settings, "sources")[0], "s")));
    await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne"); Check(server.Requests == 0, "Default generation fetched a source that already had cache");
    await SubscriptionUpdater.PullAsync(new WorkspaceSession(f.Root, "SampleOne"), 0); Check(server.Requests == 1, "Direct pull did not fetch just the selected source");
    Check(!new WorkspaceSession(f.Root, "SampleOne").Subscriptions.UrlChanged(Map(L(settings, "sources")[0], "s")));
});
await Test("Parameter button inspection downloads each enabled rule URL without subscriptions or writes", async () =>
{
    using var f = new Fixture(); using var good = new LocalServer(); using var denied = new LocalServer(); using var invalid = new LocalServer(); using var subscriptions = new LocalServer(); using var disabled = new LocalServer();
    good.Body = "payload: ['DOMAIN,valid.example']"; denied.Status = 403; invalid.Body = "<html>Not a rule list</html>";
    string cache = Path.Combine(f.Root, "rule-cache");
    var spec = O("url", good.Url); var http = O("trust_env", false, "cache_dir", cache, "prefer_cache", true, "allow_stale", true);
    using (var fetch = new Fetcher(f.Project, http)) await fetch.ReadAsync(spec, bytes => Rules.ParseRemote(bytes, spec), "seed");
    var session = new WorkspaceSession(f.Root, "SampleOne"); var settings = session.Settings; settings["http"] = http;
    foreach (var source in L(settings, "sources")) Map(source, "s")["url"] = subscriptions.Url;
    session.Documents["settings.yaml"].Set(settings);
    var policy = session.Policy; policy["order"] = A("reject", "proxy", "direct");
    policy["remote_rules"] = A(new JsonNode?[] { O("direct", A(good.Url)), O("proxy", A(denied.Url)), O("reject", A(invalid.Url)), O("name", "disabled", "url", disabled.Url, "enabled", false) });
    session.Documents["policy.yaml"].Set(policy);
    var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
    var rows = new List<ParameterCheck>(); await foreach (var row in ParameterInspection.InspectAsync(session)) rows.Add(row);
    var completed = rows.Where(x => x.Key is not null && x.State != "pending").ToArray();
    Check(completed.Length == 3 && completed.Select(x => x.State).SequenceEqual(["fail", "fail", "pass"]));
    Check(completed.Select(x => x.Title).SequenceEqual(["拒绝 URL · 1", "代理 URL · 1", "直连 URL · 1"]));
    Check(completed[0].Message.Contains(invalid.Url) && completed[1].Message.Contains("403") && completed[1].Issue?.Field == "RemoteUrls_proxy");
    Check(completed[2].Message.Contains(good.Url) && completed[2].Message.Contains("1 条规则"));
    Check(rows.Count(x => x.State == "pending") == 3 && good.Requests == 2 && denied.Requests == 1 && invalid.Requests == 1 && subscriptions.Requests == 0 && disabled.Requests == 0);
    good.Status = 403; rows.Clear(); await foreach (var row in ParameterInspection.InspectAsync(session)) rows.Add(row);
    Check(rows.Single(x => x.Key is not null && x.Title == "直连 URL · 1" && x.State != "pending").State == "fail", "Button accepted stale cached content instead of a live HTTP failure");
    Check(before.Count == Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Length && before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)) && session.Dirty, "Parameter inspection saved inputs or modified caches");
});
await Test("Parameter URL inspection preserves advanced request options", async () =>
{
    using var f = new Fixture(); using var server = new LocalServer(); server.Body = "payload: ['+.example.org']";
    var session = new WorkspaceSession(f.Root, "SampleOne"); var policy = session.Policy;
    policy["remote_rules"] = A(new JsonNode?[] { O("name", "custom", "url", server.Url, "format", "yaml", "behavior", "domain", "user_agent", "ParameterCheck", "headers", O("X-Rule-Check", "present"), "action", "PROXY") });
    session.Documents["policy.yaml"].Set(policy);
    var rows = new List<ParameterCheck>(); await foreach (var row in ParameterInspection.InspectAsync(session)) rows.Add(row);
    Check(rows.Any(x => x.Key is not null && x.State == "pass") && server.RequestHeaders.Contains("ParameterCheck") && server.RequestHeaders.Contains("X-Rule-Check: present"));
});
await Test("Parameter URL inspection cancels in-flight downloads and stops remaining URLs", async () =>
{
    using var f = new Fixture(); using var slow = new LocalServer(); using var remaining = new LocalServer(); using var cancellation = new CancellationTokenSource();
    slow.Body = "DOMAIN,slow.example"; slow.BodyDelayMs = 3000;
    var session = new WorkspaceSession(f.Root, "SampleOne"); var policy = session.Policy; policy["remote_rules"] = A(new JsonNode?[] { O("proxy", A(slow.Url, remaining.Url)) }); session.Documents["policy.yaml"].Set(policy);
    var rows = new List<ParameterCheck>();
    try
    {
        await foreach (var row in ParameterInspection.InspectAsync(session, cancellation.Token)) { rows.Add(row); if (row.State == "pending") cancellation.CancelAfter(100); }
        throw new Exception("Parameter check ignored cancellation");
    }
    catch (OperationCanceledException) { }
    Check(rows.Count(x => x.State == "pending") == 1 && !rows.Any(x => x.Key is not null && x.State == "pass") && remaining.Requests == 0);
});
await Test("Direct subscription pull saves only subscription settings and cache", async () =>
{
    using var f = new Fixture(); using var server = new LocalServer(); server.Body = "proxies: [{name: 香港独立拉取, type: http, server: localhost, port: '8080'}]";
    var old = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); byte[] output = File.ReadAllBytes(old.OutputPath), other = File.ReadAllBytes(Path.Combine(f.Project, "subscrip", "B.yaml"));
    var session = new WorkspaceSession(f.Root, "SampleOne"); var settings = session.Settings; Map(L(settings, "sources")[0], "s")["url"] = server.Url; session.Documents["settings.yaml"].Set(settings);
    string originalPolicy = File.ReadAllText(Path.Combine(f.Project, "policy.yaml")), originalResidence = File.ReadAllText(Path.Combine(f.Project, "residential.yaml"));
    session.Documents["policy.yaml"].SetText("dns: [unfinished"); session.Documents["residential.yaml"].SetText("proxies: []\n");
    var pulled = await SubscriptionUpdater.PullAsync(session, 0);
    Check(pulled.NodeCount == 1 && server.Requests == 1 && !session.Documents["settings.yaml"].Dirty && session.Dirty);
    Check(File.ReadAllText(Path.Combine(f.Project, "policy.yaml")) == originalPolicy && File.ReadAllText(Path.Combine(f.Project, "residential.yaml")) == originalResidence);
    Check(File.ReadAllBytes(old.OutputPath).SequenceEqual(output) && File.ReadAllBytes(Path.Combine(f.Project, "subscrip", "B.yaml")).SequenceEqual(other));
    Check(!session.Subscriptions.UrlChanged(Map(L(session.Settings, "sources")[0], "s")) && session.Documents["policy.yaml"].Dirty && session.Documents["residential.yaml"].Dirty);
});
await Test("Direct pull failure and cancellation preserve pending edits and all cached data", async () =>
{
    using var f = new Fixture(); using var server = new LocalServer(); await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    var session = new WorkspaceSession(f.Root, "SampleOne"); var settings = session.Settings; Map(L(settings, "sources")[0], "s")["url"] = server.Url; session.Documents["settings.yaml"].Set(settings);
    var before = Directory.GetFiles(f.Project, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes); string state = session.Subscriptions.Text;
    server.Status = 403; await ThrowsAsync(() => SubscriptionUpdater.PullAsync(session, 0));
    Check(session.Dirty && session.Subscriptions.Text == state && before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)));
    server.Status = 200; server.Body = "proxies: [{name: 香港, type: http, server: localhost, port: 8080}]"; server.BodyDelayMs = 3000;
    using var cancellation = new CancellationTokenSource(100);
    try { await SubscriptionUpdater.PullAsync(session, 0, cancellation.Token); throw new Exception("Direct pull ignored cancellation"); } catch (OperationCanceledException) { }
    Check(before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)) && session.Subscriptions.Text == state);
});
await Test("Forced invalid downloads fall back but cancellation never does", async () =>
{
    using var f = new Fixture(); using var server = new LocalServer(); var engine = new BuildEngine(); var old = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    var settings = Map(YamlData.Read(f.Settings), "s"); foreach (var source in L(settings, "sources")) Map(source, "s")["url"] = server.Url; f.Write("settings.yaml", settings);
    var before = Directory.GetFiles(Path.Combine(f.Project, "subscrip")).ToDictionary(x => x, File.ReadAllBytes);
    foreach (string body in new[] { "<html>failure</html>", "proxies: []", "proxies: [{name: 香港, type: http, server: localhost, port: bad}]", "proxies: [{name: 美国不筛选, type: http, server: localhost, port: 80}]" })
    {
        server.Body = body; var progress = new List<BuildProgress>();
        await engine.BuildProjectAsync(f.Root, "SampleOne", new(Force: true), new TestProgress(progress.Add));
        Check(progress.Count(x => x.State == "warning" && x.Message.Contains("已使用现有缓存")) == 2 && before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)));
    }
    byte[] output = File.ReadAllBytes(old.OutputPath); server.Body = "proxies: [{name: 香港, type: http, server: localhost, port: 80}]"; server.BodyDelayMs = 3000;
    using var cancellation = new CancellationTokenSource(100);
    try { await engine.BuildProjectAsync(f.Root, "SampleOne", new(Force: true), cancellation: cancellation.Token); throw new Exception("Canceled force generation fell back to cache"); } catch (OperationCanceledException) { }
    Check(File.ReadAllBytes(old.OutputPath).SequenceEqual(output) && before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)));
});
await Test("Direct pull detects external cache edits and expands HTTP providers without output generation", async () =>
{
    using var f = new Fixture(); using var source = new LocalServer(); using var provider = new LocalServer();
    provider.Body = "proxies: [{name: 香港provider, type: http, server: localhost, port: 8080}]";
    source.Body = "proxy-providers: {remote: {type: http, url: '" + provider.Url + "'}}";
    var session = new WorkspaceSession(f.Root, "SampleOne"); var settings = session.Settings; Map(L(settings, "sources")[0], "s")["url"] = source.Url; session.Documents["settings.yaml"].Set(settings);
    await SubscriptionUpdater.PullAsync(session, 0); Check(source.Requests == 1 && provider.Requests == 1 && !Directory.Exists(session.OutputPath));
    string snapshot = session.SnapshotPath("A"); source.BodyDelayMs = 400;
    var pulling = SubscriptionUpdater.PullAsync(session, 0);
    for (int i = 0; i < 100 && source.Requests < 2; i++) await Task.Delay(5);
    File.WriteAllText(snapshot, "proxies: []"); await ThrowsAsync(() => pulling);
    Check(File.ReadAllText(snapshot) == "proxies: []", "Direct pull overwrote an external edit");
});
await FunctionalRegression.Run(Test);
await DnsRoutingRegression.Run(Test);
await DnsSettingsRegression.Run(Test);
await ImportedRuleSetsRegression.Run(Test);
await SubscriptionRuleImportRegression.Run(Test);
await BuiltInResourcesRegression.Run(Test);
await SubscriptionBaselineRegression.Run(Test);
await UnifiedRoutingRegression.Run(Test);
await GroupPriorityRegression.Run(Test);
await SubscriptionHostsRegression.Run(Test);
await DnsPrivacyRegression.Run(Test);
await RuleSemanticsRegression.Run(Test);
await AsnPolicyRegression.Run(Test);
await RouterAsnToolsRegression.Run(Test);
Console.WriteLine($"RESULT {passed} passed, {failed} failed"); Environment.ExitCode = failed == 0 ? 0 : 1;

static JsonObject MergeBridge(JsonArray input) { var (rules, report) = Rules.MergeRules(input); return O("rules", rules, "report", report); }
static async Task<JsonObject> CompileBridge(JsonObject settings, string root, bool offline) { var profile = await ProfileCompiler.CompileAsync(settings, root, offline); return O("configuration", profile.Configuration, "report", profile.Report); }
static async Task<JsonObject> BuildBridge(BuildEngine engine, string root, string project, bool force, bool offline) { var result = await engine.BuildProjectAsync(root, project, new(force, offline)); return O("path", result.OutputPath, "report", result.Report); }
static JsonObject SaveDefaultsBridge(string root, JsonObject policy)
{
    RuleDefaults.Save(root, policy);
    var example = new WorkspaceSession(root, "Example");
    return O("example_documents", WorkspaceSession.ExampleDocuments(root).Aggregate(new JsonObject(), (all, entry) => { all[entry.Key] = entry.Value; return all; }), "supplemental_count", L(example.Policy, "supplemental_rules").Count, "default_dns", DnsSettings.DomesticEnabled(example.Policy));
}

sealed class TestProgress(Action<BuildProgress> callback) : IProgress<BuildProgress> { public void Report(BuildProgress value) => callback(value); }

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "homechain-test-" + Guid.NewGuid().ToString("N"));
    public string Project => Path.Combine(Root, "config", "SampleOne");
    public string Settings => Path.Combine(Project, "settings.yaml");
    public Fixture()
    {
        Directory.CreateDirectory(Path.Combine(Project, "subscrip"));
        for (int i = 0; i < 2; i++) Write("subscrip/" + (i == 0 ? "A" : "B") + ".yaml", O("proxies", A(new JsonNode?[] { O("name", "香港同名", "type", "http", "server", "127.0.0.1", "port", 1081 + i), O("name", "美国", "type", "http", "server", "127.0.0.1", "port", 1181 + i) })));
        Write("settings.yaml", O("sources", A(new JsonNode?[] { O("name", "A", "priority", 10, "url", "https://example.invalid/?token=private"), O("name", "B", "priority", 20, "url", "https://example.invalid/b") }), "selection", O("include", "香港", "interval", 300, "timeout", 1000, "tolerance", 0), "http", O("trust_env", false), "validation", O()));
        Write("residential.yaml", O("proxies", A(new JsonNode?[] { O("name", "住宅", "type", "socks5", "server", "127.0.0.1", "port", 1090, "username", "local", "password", "p#a:ss\\word", "udp", true) })));
        Write("policy.yaml", O("direct", A("example.cn"), "reject", A("bad.example"), "proxy", A("DOMAIN-SUFFIX,proxy.example"), "domestic_direct", false,
            "dns", O("enable", true, "listen", "127.0.0.1:1053", "nameserver", A("https://1.1.1.1/dns-query#住宅出口"), "proxy-server-nameserver", A("https://223.5.5.5/dns-query#DIRECT"))));
    }
    public void Write(string relative, JsonNode value) { string path = Path.Combine(Project, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, YamlData.Bytes(value)); }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
sealed class LocalServer : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0); readonly CancellationTokenSource stop = new();
    public int Requests; public string Body = ""; public int Status = 200; public string? FailPath, RedirectPath;
    public int BodyDelayMs; public string RequestHeaders = "";
    public string Url => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
    public LocalServer() { listener.Start(); _ = Run(); }
    async Task Run()
    {
        while (!stop.IsCancellationRequested)
            try { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Respond(client); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { break; }
    }
    async Task Respond(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true); string first = await reader.ReadLineAsync() ?? ""; Interlocked.Increment(ref Requests);
            var requestHeaders = new StringBuilder(); while (await reader.ReadLineAsync() is { Length: > 0 } headerLine) requestHeaders.AppendLine(headerLine); RequestHeaders = requestHeaders.ToString();
            if (RedirectPath is not null && first.Contains(" " + RedirectPath + " "))
            { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + Url + "final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); return; }
            int status = FailPath is not null && first.Contains(" " + FailPath + " ") ? 403 : Status;
            byte[] data = Encoding.UTF8.GetBytes(Body); byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
            try { await stream.WriteAsync(header); if (BodyDelayMs > 0) await Task.Delay(BodyDelayMs, stop.Token); await stream.WriteAsync(data); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); stop.Dispose(); }
}

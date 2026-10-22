using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class SubscriptionHostsRegression
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static JsonObject Node(string name, string server) => O("name", name, "type", "trojan", "server", server, "port", 443, "password", "demo", "sni", "tls.example", "udp", false);
    static JsonObject Source(string server, JsonObject hosts) => O("proxies", A(new JsonNode?[] { Node("香港", server) }), "hosts", hosts,
        "proxy-groups", A(new JsonNode?[] { O("name", "Original", "type", "select", "proxies", A("香港")) }), "rules", A("MATCH,Original"));
    static void Baseline(Fixture f) => f.Write("policy.yaml", O("rule_source", "subscription", "extra_rules_enabled", false));
    static async Task<JsonObject> Build(Fixture f) => Map(YamlData.Read((await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true))).OutputPath), "output");
    static void Fails(Action action, string reason)
    {
        try { action(); throw new Exception("Expected hosts failure"); }
        catch (BuildException ex) { Check(ex.Message.Contains(reason), "Unclear hosts diagnostic: " + ex.Message); }
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Primary hosts and secondary node aliases survive offline generation without secondary DNS/rules", async () =>
        {
            using var f = new Fixture(); Baseline(f);
            var first = Source("first.example", O("first.example", "edge.example", "edge.example", A("192.0.2.1", "2001:db8::1"), "business.example", "192.0.2.2"));
            var second = Source("second.example", O("+.example", "cdn.example.net", "cdn.example.net", "192.0.2.3", "unrelated.invalid", "192.0.2.4"));
            second["rules"] = A("unsupported secondary rule"); second["dns"] = O("nameserver", A("https://dns.invalid"));
            f.Write("subscrip/A.yaml", first); f.Write("subscrip/B.yaml", second);
            var output = await Build(f); var hosts = M(output, "hosts");
            Check(hosts.Count == 5 && Canonical(hosts["edge.example"]) == Canonical(A("192.0.2.1", "2001:db8::1")), "Primary aliases/IP list lost");
            Check(S(hosts, "second.example") == "cdn.example.net" && !hosts.ContainsKey("+.example") && !hosts.ContainsKey("unrelated.invalid"), "Secondary hosts were lost or over-imported");
            Check(Canonical(output["rules"]) == Canonical(A("MATCH,代理出口")) && !output.ContainsKey("dns"), "Secondary routing or DNS imported");
            var node = L(output, "proxies").OfType<JsonObject>().Single(x => S(x, "name") == "[B] 香港");
            Check(S(node, "server") == "second.example" && S(node, "sni") == "tls.example" && !B(node, "udp"), "Hosts carry changed transport fields");
        });
        await test("Secondary subscription pull caches hosts for later offline generation", async () =>
        {
            using var f = new Fixture(); Baseline(f); f.Write("subscrip/A.yaml", Source("127.0.0.1", O()));
            using var server = new LocalServer();
            server.Body = YamlData.Write(Source("node.example", O("node.example", "alias.example", "alias.example", "192.0.2.5", "unrelated.example", "192.0.2.6")));
            var session = new WorkspaceSession(f.Root, "SampleOne"); var settings = session.Settings;
            Map(L(settings, "sources")[1], "source")["url"] = server.Url; session.Documents["settings.yaml"].Set(settings);
            await SubscriptionUpdater.PullAsync(session, 1);
            var cached = Map(YamlData.Read(session.SnapshotPath("B")), "snapshot");
            Check(cached.Count == 2 && M(cached, "hosts").Count == 2 && !cached.ContainsKey("rules"), "Secondary pull dropped hosts or kept routing");
            Check(Canonical(M(await Build(f), "hosts")) == Canonical(M(cached, "hosts")), "Offline read dropped cached hosts");
        });
        await test("Nested provider hosts and parent alias dependencies survive primary and secondary snapshots", async () =>
        {
            using var f = new Fixture();
            f.Write("child.yaml", Source("provider.example", O("provider.example", "parent.example")));
            f.Write("middle.yaml", O("proxy-providers", O("child", O("type", "file", "path", "child.yaml"))));
            var doc = O("hosts", O("parent.example", "192.0.2.7"), "proxy-providers", O("middle", O("type", "file", "path", "middle.yaml")),
                "proxy-groups", A(new JsonNode?[] { O("name", "Group", "type", "select", "use", A("middle")) }), "rules", A("MATCH,Group"));
            using var fetch = new Fetcher(f.Project, O(), offline: true);
            var primary = await SubscriptionBaseline.SnapshotAsync(doc, fetch, f.Project, "A");
            var secondary = await Nodes.CollectDocumentAsync(doc, fetch, f.Project, "B");
            foreach (var snapshot in new[] { primary, secondary })
                Check(S(M(snapshot, "hosts"), "provider.example") == "parent.example" && S(M(snapshot, "hosts"), "parent.example") == "192.0.2.7", "Nested/parent provider alias lost");
            Check(Canonical(L(Map(L(primary, "proxy-groups")[0], "group"), "proxies")) == Canonical(A("香港")), "Provider members changed");
        });
        await test("DNS wildcards use exact and one-label precedence and include complete alias chains", () =>
        {
            var hosts = O("exact.edge.example", "192.0.2.1", "*.edge.example", "192.0.2.2", ".edge.example", "192.0.2.3",
                "+.zone.example", "alias.example", "alias.example", "final.example", "final.example", A("192.0.2.4", "2001:db8::4"), "unused.example", "192.0.2.9");
            var nodes = A(new JsonNode?[] { Node("1", "exact.edge.example"), Node("2", "x.edge.example"), Node("3", "x.y.edge.example"), Node("4", "zone.example"), Node("5", "x.y.zone.example") });
            var result = SubscriptionHosts.Required(hosts, nodes, "test");
            Check(S(result, "exact.edge.example") == "192.0.2.1" && S(result, "x.edge.example") == "192.0.2.2" && S(result, "x.y.edge.example") == "192.0.2.3", "Hosts wildcard precedence differs from DNS semantics");
            Check(S(result, "zone.example") == "alias.example" && S(result, "x.y.zone.example") == "alias.example" && result.ContainsKey("final.example") && !result.ContainsKey("unused.example"), "Root/deep alias closure incorrect");
            var payload = Nodes.Document(O("payload", nodes, "hosts", hosts));
            Check(M(SubscriptionHosts.NodeDocument(payload, L(payload, "proxies"), "import"), "hosts").Count == 7, "Payload/local import lost hosts");
            return Task.CompletedTask;
        });
        await test("Conflicting hosts stop generation and retain the previous output", async () =>
        {
            using var f = new Fixture(); Baseline(f);
            f.Write("subscrip/A.yaml", Source("node.example", O("node.example", "192.0.2.1")));
            f.Write("subscrip/B.yaml", Source("node.example", O("node.example", "192.0.2.1")));
            var output = await Build(f); var session = new WorkspaceSession(f.Root, "SampleOne"); string path = Path.Combine(session.OutputPath, "HomeChain.yaml"); string before = DependencySnapshot.FileHash(path);
            f.Write("subscrip/B.yaml", Source("node.example", O("NODE.EXAMPLE", "192.0.2.2")));
            try { await Build(f); throw new Exception("Conflicting hosts accepted"); }
            catch (BuildException ex) { Check(ex.Message.Contains("hosts") && ex.Message.Contains("冲突"), "Conflict not explained"); }
            Check(DependencySnapshot.FileHash(path) == before, "Conflict overwrote old output");
            Fails(() => SubscriptionHosts.Merge(O("+.example", "192.0.2.1"), O("node.example", "192.0.2.2"), "B"), "冲突");
            Fails(() => SubscriptionHosts.Merge(O("node.example", "192.0.2.2"), O("+.example", "192.0.2.1"), "B"), "冲突");
        });
        await test("Cross-subscription alias cycles and invalid host values fail explicitly", async () =>
        {
            using var f = new Fixture(); Baseline(f);
            f.Write("subscrip/A.yaml", Source("a.example", O("a.example", "b.example")));
            f.Write("subscrip/B.yaml", Source("b.example", O("b.example", "a.example")));
            try { await Build(f); throw new Exception("Cross-source cycle accepted"); }
            catch (BuildException ex) { Check(ex.Message.Contains("循环"), "Alias cycle not explained"); }
            Fails(() => SubscriptionHosts.Normalize(O("node.example", A("alias.example")), "source"), "IP 列表");
            Fails(() => SubscriptionHosts.Required(O("+.example", "alias.example"), A(new JsonNode?[] { Node("香港", "node.example") }), "source"), "循环");
        });
        await test("Custom rule mode also preserves node hosts and marks older output stale", async () =>
        {
            using var f = new Fixture();
            f.Write("subscrip/A.yaml", Source("a.example", O("a.example", "192.0.2.1")));
            f.Write("subscrip/B.yaml", Source("b.example", O("b.example", "192.0.2.2")));
            var output = await Build(f); Check(M(output, "hosts").Count == 2, "Custom compiler dropped node hosts");
            var session = new WorkspaceSession(f.Root, "SampleOne"); Check(session.OutputStatus() == OutputFreshness.Current, "Fresh output unexpectedly stale");
            string path = Path.Combine(session.OutputPath, "HomeChain.report.json"); var report = Map(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)), "report");
            report.Remove("hosts_compiler_version"); File.WriteAllText(path, report.ToJsonString(JsonOptions));
            Check(session.OutputStatus() == OutputFreshness.InputsChanged, "Old output considered current after hosts fix");
        });
    }
}

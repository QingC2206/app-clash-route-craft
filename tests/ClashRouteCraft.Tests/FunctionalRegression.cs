using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class FunctionalRegression
{
    static void Check(bool value, string message = "Functional regression") { if (!value) throw new Exception(message); }
    static async Task Reject(Func<Task> run)
    {
        try { await run(); } catch (BuildException) { return; }
        throw new Exception("Expected rejection");
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Direct pull rejects zero eligible nodes and preserves cache, provenance and drafts", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "proxies: [{name: 美国, type: http, server: localhost, port: 8080}]" };
            var s = new WorkspaceSession(f.Root, "SampleOne"); s.Save();
            var before = Directory.GetFiles(f.Project, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
            var settings = s.Settings; Map(L(settings, "sources")[0], "s")["url"] = server.Url; s.Documents["settings.yaml"].Set(settings);
            string draft = s.Documents["settings.yaml"].Text, state = s.Subscriptions.Text;
            await Reject(() => SubscriptionUpdater.PullAsync(s, 0));
            Check(server.Requests == 1 && s.Documents["settings.yaml"].Text == draft && s.Dirty && s.Subscriptions.Text == state);
            Check(before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)));
            server.Body = "proxies: [{name: 香港, type: http, server: localhost, port: 8080}, {name: 美国, type: http, server: localhost, port: 8081}]";
            var pulled = await SubscriptionUpdater.PullAsync(s, 0); Check(pulled.NodeCount == 2 && pulled.EligibleCount == 1);
        });
        await test("Disabled source invalid regex and missing environment cannot block active generation", async () =>
        {
            using var f = new Fixture(); var s = new WorkspaceSession(f.Root, "SampleOne"); var settings = s.Settings;
            var disabled = Map(L(settings, "sources")[1], "s"); disabled["enabled"] = false; disabled["include"] = "["; disabled["url"] = "${HOMECHAIN_TEST_MISSING_91CB}"; disabled["headers"] = O("X-Token", "${HOMECHAIN_TEST_MISSING_91CB}");
            s.Documents["settings.yaml"].Set(settings); s.Save();
            Check(!ParameterInspection.Inspect(s).Any(x => x.State == "fail"));
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); Check(I(result.Report, "proxy_count", 0) > 0);
            disabled["enabled"] = true; s.Documents["settings.yaml"].Set(settings);
            Check(ParameterInspection.Inspect(s).Any(x => x.State == "fail"));
        });
        await test("Rename rewrites group, namespaced node and DNS references and keeps unrelated strings", async () =>
        {
            using var f = new Fixture(); var s = new WorkspaceSession(f.Root, "SampleOne"); var p = s.Policy;
            p["final"] = "订阅·A"; p["first"] = A(new JsonNode?[] { O("match", "DOMAIN,rename.example", "action", "[A] 香港同名") });
            p["remote_rules"] = A(new JsonNode?[] { O("name", "disabled", "url", "https://example.invalid/A", "action", "订阅·A", "enabled", false) });
            M(p, "dns")["nameserver"] = A("https://1.1.1.1/dns-query#" + Uri.EscapeDataString("住宅") + "&h3");
            s.Documents["policy.yaml"].Set(p); s.Save();
            s.RenameSource(0, "Renamed"); s.RenameResidence(0, "住宅改名");
            Check(S(s.Policy, "final") == "订阅·Renamed" && S(Map(L(s.Policy, "first")[0], "r"), "action") == "[Renamed] 香港同名");
            Check(S(Map(L(s.Policy, "remote_rules")[0], "r"), "url") == "https://example.invalid/A");
            Check(Uri.UnescapeDataString(S(L(M(s.Policy, "dns"), "nameserver")[0])).EndsWith("#住宅改名&h3"));
            s.Save(); await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            await Reject(() => Task.Run(() => s.RemoveSource(0))); await Reject(() => Task.Run(() => s.RemoveResidence(0)));
        });
        await test("Reference-changing rename cannot partially persist through subscription pull", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer();
            var s = new WorkspaceSession(f.Root, "SampleOne"); var settings = s.Settings; Map(L(settings, "sources")[0], "s")["url"] = server.Url;
            s.Documents["settings.yaml"].Set(settings); var p = s.Policy; p["final"] = "订阅·A"; s.Documents["policy.yaml"].Set(p); s.Save();
            s.RenameSource(0, "Renamed");
            await Reject(() => SubscriptionUpdater.PullAsync(s, 0)); Check(server.Requests == 0);
            Check(S(Map(L(new WorkspaceSession(f.Root, "SampleOne").Settings, "sources")[0], "s"), "name") == "A" && S(s.Policy, "final") == "订阅·Renamed");
        });
        await test("Project copy includes relative and absolute internal rules and local fallbacks", async () =>
        {
            using var f = new Fixture(); var s = new WorkspaceSession(f.Root, "SampleOne");
            string folder = Path.Combine(f.Project, "lists"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "direct.txt"), "DOMAIN,copy.example\n");
            File.WriteAllText(Path.Combine(folder, "backup.txt"), "DOMAIN,backup.example\n");
            File.WriteAllText(Path.Combine(folder, "asn.txt"), "IP-CIDR,192.0.2.0/24\n");
            var p = s.Policy; p["remote_rules"] = A(new JsonNode?[] {
                O("name", "relative", "file", "lists/direct.txt", "action", "DIRECT"),
                O("name", "absolute", "file", Path.Combine(folder, "backup.txt"), "action", "PROXY"),
                O("name", "fallback", "url", "https://example.invalid", "fallback_file", "lists/backup.txt", "enabled", false) });
            p["asn_sources"] = O("123", O("file", "lists/asn.txt")); s.Documents["policy.yaml"].Set(p); s.Save();
            WorkspaceSession.CreateProject(f.Root, "Copy", "SampleOne");
            var copy = new WorkspaceSession(f.Root, "Copy");
            foreach (string file in new[] { "direct.txt", "backup.txt", "asn.txt" }) Check(File.ReadAllBytes(Path.Combine(copy.ProjectPath, "lists", file)).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, file))));
            Check(!Path.IsPathRooted(S(Map(L(copy.Policy, "remote_rules")[1], "rule"), "file")));
            await new BuildEngine().BuildProjectAsync(f.Root, "Copy", new(Offline: true));
        });
        await test("Fingerprint ignores orphan and disabled snapshots but tracks active renamed and staged ones", async () =>
        {
            using var f = new Fixture(); var s = new WorkspaceSession(f.Root, "SampleOne"); var settings = s.Settings;
            Map(L(settings, "sources")[1], "s")["enabled"] = false; s.Documents["settings.yaml"].Set(settings); s.RenameSource(0, "Renamed"); s.Save();
            await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); Check(s.OutputIsCurrent());
            File.AppendAllText(s.SnapshotPath("B"), "\n# inactive"); File.WriteAllText(Path.Combine(f.Project, "subscrip", "orphan.yaml"), "ignored");
            Check(s.OutputIsCurrent());
            string active = s.SnapshotPath("Renamed"), initial = InputFingerprint.Compute(f.Project);
            Check(InputFingerprint.Compute(f.Project, [(active, Encoding.UTF8.GetBytes("new"))]) != initial);
            File.AppendAllText(active, "\n# changed"); Check(!s.OutputIsCurrent());
        });
        await test("Node deduplication retains dialer aliases but rejects genuinely ambiguous aliases", async () =>
        {
            JsonObject Node(string name, int port) => O("name", name, "type", "http", "server", "localhost", "port", port);
            var c = Node("C", 81); c["dialer-proxy"] = "B";
            var nodes = Nodes.Namespace(A(new JsonNode?[] { Node("A", 80), Node("B", 80), c }), "source");
            Check(nodes.Count == 2 && S(Map(nodes[1], "node"), "dialer-proxy") == "[source] A");
            await Reject(() => Task.Run(() => Nodes.Namespace(A(new JsonNode?[] { Node("A", 80), Node("B", 80), Node("B", 82), c }), "source")));
        });
        await test("Residence names reuse free slots and never collide", () =>
        {
            using var f = new Fixture(); var s = new WorkspaceSession(f.Root, "SampleOne"); var nodes = s.Residences;
            Map(nodes[0], "r")["name"] = "住宅-1"; var third = nodes[0]!.DeepClone(); Map(third, "r")["name"] = "住宅-3"; nodes.Push(third); s.SetResidences(nodes);
            Check(s.NextResidenceName() == "住宅-2"); return Task.CompletedTask;
        });
        await test("Output copy revalidates exact bytes and refuses external edits or dirty inputs", async () =>
        {
            using var f = new Fixture(); var s = new WorkspaceSession(f.Root, "SampleOne"); var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            built.Report["core_validation"] = "passed"; File.WriteAllText(Path.Combine(s.OutputPath, "HomeChain.report.json"), built.Report.ToJsonString());
            string text = s.ReadValidatedOutput("HomeChain.yaml"); Check(text.Contains("proxies:"));
            File.AppendAllText(built.OutputPath, "# external edit"); await Reject(() => Task.Run(() => s.ReadValidatedOutput("HomeChain.yaml")));
            await Reject(() => Task.Run(() => s.ReadValidatedOutput("../settings.yaml")));
        });
        await test("Manual rules remain ahead of remote rules regardless of group order", async () =>
        {
            using var f = new Fixture(); var s = new WorkspaceSession(f.Root, "SampleOne"); var p = s.Policy;
            File.WriteAllText(Path.Combine(f.Project, "direct.txt"), "DOMAIN-SUFFIX,ordering.example\n");
            p["order"] = A("direct", "reject", "proxy"); p["proxy"] = A("DOMAIN-SUFFIX,ordering.example");
            p["remote_rules"] = A(new JsonNode?[] { O("name", "direct-list", "file", "direct.txt", "action", "DIRECT") }); s.Documents["policy.yaml"].Set(p); s.Save();
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var rules = L(Map(YamlData.Read(result.OutputPath), "config"), "rules");
            Check(rules.Where(x => S(x).Contains("ordering.example")).Select(x => S(x)).SequenceEqual(["DOMAIN-SUFFIX,ordering.example,默认代理", "DOMAIN-SUFFIX,ordering.example,DIRECT"]));
        });
        await test("Residence HTTP probe uses explicit proxy and bounds failures, body and timeout", async () =>
        {
            using var server = new LocalServer { Body = "ip=203.0.113.9\n" };
            var node = O("name", "probe", "type", "http", "server", "127.0.0.1", "port", new Uri(server.Url).Port);
            var destination = new Uri("http://test.invalid/trace");
            var result = await ResidenceProbe.CheckAsync(node, endpoint: destination); Check(result.State == "pass" && result.ExitIp == "203.0.113.9" && server.Requests == 1);
            server.Status = 407; Check((await ResidenceProbe.CheckAsync(node, endpoint: destination)).Message.Contains("鉴权"));
            server.Status = 200; server.Body = "<html>Unexpected response</html>"; Check((await ResidenceProbe.CheckAsync(node, endpoint: destination)).State == "warning");
            server.Body = new string('x', 9000); Check((await ResidenceProbe.CheckAsync(node, endpoint: destination)).State == "fail");
            server.BodyDelayMs = 1500; Check((await ResidenceProbe.CheckAsync(node, endpoint: destination, timeout: TimeSpan.FromMilliseconds(100))).Message.Contains("超时"));
            using var canceled = new CancellationTokenSource(80);
            try { await ResidenceProbe.CheckAsync(node, canceled.Token, destination); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            node["type"] = "ss"; Check((await ResidenceProbe.CheckAsync(node)).State == "warning");
        });
        await test("Residence SOCKS5 probe authenticates and rejects bad credentials without disclosing them", async () =>
        {
            using var proxy = new SocksProbeServer();
            var node = O("name", "probe", "type", "socks5", "server", "127.0.0.1", "port", proxy.Port, "username", "demo", "password", "test-secret");
            var result = await ResidenceProbe.CheckAsync(node, endpoint: new Uri("http://test.invalid/trace"));
            Check(result.State == "pass" && result.ExitIp == "203.0.113.10" && proxy.Authenticated);
            node["password"] = "wrong-secret"; result = await ResidenceProbe.CheckAsync(node, endpoint: new Uri("http://test.invalid/trace"));
            Check(result.State == "fail" && !result.Message.Contains("secret"));
        });
    }
}

sealed class SocksProbeServer : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool Authenticated;
    public SocksProbeServer() { listener.Start(); _ = Run(); }
    async Task Run()
    {
        while (!stop.IsCancellationRequested)
            try { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Respond(client); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { break; }
    }
    async Task Respond(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            async Task<byte[]> Read(int count) { var b = new byte[count]; await stream.ReadExactlyAsync(b, stop.Token); return b; }
            var greeting = await Read(2); await Read(greeting[1]); await stream.WriteAsync(new byte[] { 5, 2 }, stop.Token);
            var auth = await Read(2); string user = Encoding.UTF8.GetString(await Read(auth[1]));
            int length = (await Read(1))[0]; string password = Encoding.UTF8.GetString(await Read(length));
            bool accepted = user == "demo" && password == "test-secret"; Authenticated |= accepted;
            await stream.WriteAsync(new byte[] { 1, accepted ? (byte)0 : (byte)1 }, stop.Token); if (!accepted) return;
            var connect = await Read(4);
            if (connect[3] == 3) await Read((await Read(1))[0]); else await Read(connect[3] == 1 ? 4 : 16);
            await Read(2); await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, stop.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
            byte[] body = Encoding.ASCII.GetBytes("ip=203.0.113.10\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
            await stream.WriteAsync(body, stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

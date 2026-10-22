using System.Text;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class SubscriptionRuleImportRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Fails(Func<Task> action) { try { await action(); } catch (BuildException) { return; } throw new Exception("Expected failure"); }
    static void Configure(WorkspaceSession session, string url)
    {
        var settings = session.Settings; var source = Map(L(settings, "sources")[0], "source");
        source["url"] = url; source["headers"] = O("X-Rule-Test", "present"); source["user_agent"] = "SubscriptionRulesTest";
        settings["http"] = O("trust_env", false); session.Documents["settings.yaml"].Set(settings);
        session.Subscriptions.MarkUpdated(source, "test");
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Subscription rule import reuses renamed full snapshot without fetching or saving project", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer(); var session = new WorkspaceSession(f.Root, "SampleOne"); Configure(session, server.Url);
            string path = session.SnapshotPath("A"); File.WriteAllText(path, "rules: ['DOMAIN,local.example,DIRECT']\nproxies: [{password: PRIVATE}]");
            session.RenameSource(0, "Renamed"); var before = session.Documents.ToDictionary(x => x.Key, x => x.Value.Text);
            var info = SubscriptionRuleImport.Inspect(session, 0); Check(info.File == path, "Renamed snapshot identity lost");
            var set = await SubscriptionRuleImport.ReadAsync(session, 0, false);
            Check(S(set, "name") == "Renamed 规则" && !Canonical(set).Contains("PRIVATE") && server.RequestHeaders == "", "Local import fetched or copied credentials");
            Check(before.All(x => session.Documents[x.Key].Text == x.Value), "Import changed editors");
        });
        await test("Subscription rule import reuses matching HTTP cache and rejects changed request identity", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "rules: ['DOMAIN,http-cache.example,DIRECT']" }; var session = new WorkspaceSession(f.Root, "SampleOne"); Configure(session, server.Url);
            using var fetch = new Fetcher(session.ProjectPath, M(session.Settings, "http"));
            var source = Map(L(session.Settings, "sources")[0], "source"); await fetch.ReadAsync(source, b => YamlData.Parse(Encoding.UTF8.GetString(b)), "fixture");
            Check(SubscriptionRuleImport.Inspect(session, 0).File == fetch.CachedPath(source), "HTTP cache not discovered");
            server.RequestHeaders = ""; await SubscriptionRuleImport.ReadAsync(session, 0, false); Check(server.RequestHeaders == "", "Cached import fetched");
            var settings = session.Settings; M(Map(L(settings, "sources")[0], "source"), "headers")["X-Rule-Test"] = "changed"; session.Documents["settings.yaml"].Set(settings);
            Check(SubscriptionRuleImport.Inspect(session, 0).File is null, "Old authenticated request cache reused");
            await Fails(() => SubscriptionRuleImport.ReadAsync(session, 0, false)); Check(server.RequestHeaders == "", "Missing source fetched without explicit action");
        });
        await test("Missing subscription rules fetch once with configured headers and persist only rules", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "rules: ['DOMAIN,remote.example,External', 'MATCH,External']\nproxies: [{password: PRIVATE}]\ndns: {nameserver: [9.9.9.9]}" };
            var session = new WorkspaceSession(f.Root, "SampleOne"); Configure(session, server.Url);
            var files = Directory.GetFiles(f.Project, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
            Check(SubscriptionRuleImport.Inspect(session, 0).File is null, "Node-only snapshot counted as rules");
            var set = await SubscriptionRuleImport.ReadAsync(session, 0, true);
            Check(server.RequestHeaders.Contains("X-Rule-Test: present") && server.RequestHeaders.Contains("SubscriptionRulesTest"), "Source request settings lost");
            Check(!Canonical(set).Contains("PRIVATE") && S(M(set, "mappings"), "External") == "", "Credentials copied or mapping guessed");
            var info = SubscriptionRuleImport.Inspect(session, 0); Check(info.File is not null, "Sanitized snapshot missing");
            string cached = File.ReadAllText(info.File!); Check(!cached.Contains("PRIVATE") && !cached.Contains("dns:") && !cached.Contains(server.Url), "Sensitive source persisted");
            server.RequestHeaders = ""; var again = await SubscriptionRuleImport.ReadAsync(session, 0, false);
            Check(Canonical(set["rules"]) == Canonical(again["rules"]) && server.RequestHeaders == "", "Second import changed rules or fetched again");
            Check(files.All(x => x.Value.SequenceEqual(File.ReadAllBytes(x.Key))), "Project inputs or node snapshot modified");
        });
        await test("Node-only subscription response fails clearly without caching or modifying nodes", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "proxies: [{password: PRIVATE}]" }; var session = new WorkspaceSession(f.Root, "SampleOne"); Configure(session, server.Url);
            await Fails(() => SubscriptionRuleImport.ReadAsync(session, 0, true));
            Check(SubscriptionRuleImport.Inspect(session, 0).File is null && !Directory.Exists(Path.Combine(f.Project, ".cache", "subscription-rules")), "Failed parse wrote rule cache");
        });
        await test("Cancelling subscription rule download leaves no partial snapshot", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "rules: ['DOMAIN,cancel.example,DIRECT']", BodyDelayMs = 400 };
            var session = new WorkspaceSession(f.Root, "SampleOne"); Configure(session, server.Url); using var cancellation = new CancellationTokenSource(80);
            bool cancelled = false;
            try { await SubscriptionRuleImport.ReadAsync(session, 0, true, cancellation: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !Directory.Exists(Path.Combine(f.Project, ".cache", "subscription-rules")), "Cancellation saved partial rules");
        });
    }
}

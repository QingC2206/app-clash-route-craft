using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class DnsRoutingRegression
{
    static void Check(bool value, string message = "DNS routing assertion failed") { if (!value) throw new Exception(message); }
    static JsonObject Dns() => O("enable", true, "nameserver", A("https://1.1.1.1/dns-query#住宅出口"), "fallback", A("https://8.8.8.8/dns-query#住宅出口"), "direct-nameserver", A("https://223.5.5.5/dns-query#DIRECT"), "proxy-server-nameserver", A("223.5.5.5"), "nameserver-policy", O("geosite:cn", A("223.5.5.5")));
    static JsonArray Candidates(params string[] rules) => A(rules.Select(x => (JsonNode?)O("rule", x, "source", "test", "stage", "first")));
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("DNS ordered providers preserve broad-before-specific and alternating actions", () =>
        {
            var config = O("dns", Dns());
            var report = DnsRouting.Apply(config, O("dns_follow_rules", true), Candidates("DOMAIN-SUFFIX,example,默认代理", "DOMAIN,child.example,DIRECT", "DOMAIN-SUFFIX,other.example,默认代理", "DOMAIN,blocked.example,REJECT", "DOMAIN,blocked.example,DIRECT"))!;
            var providers = M(config, "rule-providers").ToArray();
            Check(providers.Length == 4 && I(report, "projected_rules", 0) == 5);
            Check(S(L(Map(providers[0].Value, "provider"), "payload")[0]) == "DOMAIN-SUFFIX,example");
            Check(S(L(Map(providers[1].Value, "provider"), "payload")[0]) == "DOMAIN,child.example");
            Check(L(Map(providers[2].Value, "provider"), "payload").Count == 2);
            Check(M(M(config, "dns"), "nameserver-policy").Last().Key == "geosite:cn");
            return Task.CompletedTask;
        });
        await test("DNS projection retains regex and domain logic without widening connection conditions or PASS", () =>
        {
            var config = O("dns", Dns());
            string regex = @"DOMAIN-REGEX,^api[0-9]{1,3}\.example$|^foo,bar$";
            string logic = "AND,((DOMAIN-SUFFIX,example),(NOT,((DOMAIN,skip.example))))";
            var report = DnsRouting.Apply(config, O("dns_follow_rules", true), Candidates("DOMAIN,pass.example,PASS", "DOMAIN,pass.example,DIRECT", regex + ",DIRECT", logic + ",DIRECT", "DOMAIN-WILDCARD,*.wild.example,DIRECT", "AND,((DOMAIN,x.example),(NETWORK,UDP)),默认代理", "DST-PORT,443,DIRECT", "GEOIP,CN,DIRECT", "PROCESS-NAME,app.exe,DIRECT"))!;
            var payload = L(Map(M(config, "rule-providers").First().Value, "provider"), "payload");
            Check(Canonical(payload) == Canonical(A("DOMAIN,pass.example", regex, logic, "DOMAIN-WILDCARD,*.wild.example")));
            Check(I(report, "pass_skipped", 0) == 1 && L(report, "connection_rules_skipped").Count == 4);
            return Task.CompletedTask;
        });
        await test("DNS generation follows edited manual and remote priority and refreshes paired output", async () =>
        {
            using var f = new Fixture(); var policy = new WorkspaceSession(f.Root, "SampleOne").Policy;
            policy["dns_follow_rules"] = true; policy["dns"] = Dns();
            policy["order"] = A("proxy", "direct", "reject");
            f.Write("list.yaml", O("payload", A("*.remote.example", "exact.remote.example")));
            policy["remote_rules"] = A(new JsonNode?[] { O("name", "domains", "file", "list.yaml", "action", "DIRECT") });
            f.Write("policy.yaml", policy);
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var config = Map(YamlData.Read(built.OutputPath), "config");
            Check(S(L(Map(M(config, "rule-providers").First().Value, "provider"), "payload")[0]) == "DOMAIN-SUFFIX,proxy.example");
            Check(M(config, "rule-providers").SelectMany(x => L(Map(x.Value, "provider"), "payload")).Any(x => S(x) == @"DOMAIN-REGEX,(?i)^[^.]+\.remote\.example$"));
            var fragment = Map(YamlData.Read(Path.ChangeExtension(built.OutputPath, ".MC2DNS.yaml")), "fragment");
            Check(Canonical(M(M(fragment, "dns"), "nameserver-policy")) == Canonical(M(M(config, "dns"), "nameserver-policy")));
            policy["order"] = A("direct", "proxy", "reject"); f.Write("policy.yaml", policy);
            built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            config = Map(YamlData.Read(built.OutputPath), "config");
            Check(S(L(Map(M(config, "rule-providers").First().Value, "provider"), "payload")[0]) == "DOMAIN-SUFFIX,example.cn");
            Check(B(M(built.Report, "dns_routing"), "enabled"));
        });
        await test("DNS projection is opt-in and rejects missing resolver pools without replacing outputs", async () =>
        {
            using var f = new Fixture(); var engine = new BuildEngine();
            var built = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var before = File.ReadAllBytes(built.OutputPath);
            Check(!built.Report.ContainsKey("dns_routing"));
            var policy = new WorkspaceSession(f.Root, "SampleOne").Policy; policy["dns_follow_rules"] = true; f.Write("policy.yaml", policy);
            try { await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); throw new Exception("Missing direct DNS accepted"); }
            catch (BuildException ex) { Check(ex.Message.Contains("direct-nameserver")); }
            Check(before.SequenceEqual(File.ReadAllBytes(built.OutputPath)));
        });
    }
}

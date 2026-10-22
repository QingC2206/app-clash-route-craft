using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class UnifiedRoutingRegression
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static JsonObject Document() => O("proxies", A(new JsonNode?[] {
        O("name", "香港", "type", "http", "server", "127.0.0.1", "port", 8080),
        O("name", "日本", "type", "http", "server", "127.0.0.1", "port", 8081) }),
        "proxy-groups", A(new JsonNode?[] {
            O("name", "Ad-looking-name", "type", "select", "proxies", A("DIRECT", "香港")),
            O("name", "Local-looking-name", "type", "select", "proxies", A("REJECT", "DIRECT")),
            O("name", "Auto", "type", "url-test", "url", "https://example.com/", "proxies", A("香港", "日本")),
            O("name", "Service", "type", "select", "proxies", A("Auto", "DIRECT")),
            O("name", "Final", "type", "select", "proxies", A("Service", "DIRECT")) }));

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Three-action routing follows default paths, not group names", () =>
        {
            var doc = Document();
            var rules = A("DOMAIN,local.example,Ad-looking-name", "DOMAIN,ad.example,Local-looking-name", "DOMAIN,proxy.example,Service", "MATCH,Final");
            var result = new SubscriptionRouting(doc).Rewrite(rules, "代理出口");
            Check(Canonical(result) == Canonical(A("DOMAIN,local.example,DIRECT", "DOMAIN,ad.example,REJECT", "DOMAIN,proxy.example,代理出口", "MATCH,代理出口")), "Default nested actions incorrectly mapped");
            Check(S(L(Map(L(doc, "proxy-groups")[0], "group"), "proxies")[0]) == "DIRECT", "Source group was mutated");
            Map(L(doc, "proxy-groups")[4], "group")["default-selected"] = "DIRECT";
            Check(S(new SubscriptionRouting(doc).Rewrite(A("MATCH,Final"), "代理出口")[0]) == "MATCH,DIRECT", "Explicit default-selected or direct final lost");
            return Task.CompletedTask;
        });
        await test("Normalization preserves regex, logical flags, PASS and UDP fallback rules", () =>
        {
            var doc = Document();
            var input = A(@"DOMAIN-REGEX,^a[0-9]{1,3},end$,Service", "AND,((NETWORK,UDP),(DST-PORT,443)),REJECT-DROP",
                "IP-CIDR,203.0.113.0/24,PASS,no-resolve", "IP-CIDR,203.0.113.0/24,Service,no-resolve", "IP-CIDR,203.0.113.0/24,DIRECT,no-resolve",
                "SUB-RULE,(NETWORK,TCP),nested", "MATCH,Final");
            doc["sub-rules"] = O("nested", A("DOMAIN,x.example,PASS", "DOMAIN,y.example,PASS-RULE", "MATCH,Service"));
            var routing = new SubscriptionRouting(doc);
            Check(Canonical(routing.Rewrite(input, "代理出口")) == Canonical(A(@"DOMAIN-REGEX,^a[0-9]{1,3},end$,代理出口", "AND,((NETWORK,UDP),(DST-PORT,443)),REJECT-DROP",
                "IP-CIDR,203.0.113.0/24,PASS,no-resolve", "IP-CIDR,203.0.113.0/24,代理出口,no-resolve", "IP-CIDR,203.0.113.0/24,DIRECT,no-resolve",
                "SUB-RULE,(NETWORK,TCP),nested", "MATCH,代理出口")), "Continuation, flags, regex or sub-rule reference changed");
            Check(Canonical(routing.RewriteSubRules("代理出口")["nested"]) == Canonical(A("DOMAIN,x.example,PASS", "DOMAIN,y.example,PASS-RULE", "MATCH,代理出口")), "Nested control flow altered");
            return Task.CompletedTask;
        });
        await test("Mixed automatic groups and unknown actions fail instead of guessing", () =>
        {
            foreach (string mode in new[] { "url-test", "fallback", "load-balance" })
            {
                var doc = Document(); var auto = Map(L(doc, "proxy-groups")[2], "group"); auto["type"] = mode; L(auto, "proxies").Push("DIRECT");
                try { new SubscriptionRouting(doc).Kind("Service"); throw new Exception("Mixed automatic group guessed"); }
                catch (BuildException ex) { Check(ex.Message.Contains("唯一动作"), "Unclear ambiguous-group diagnostic"); }
            }
            try { new SubscriptionRouting(Document()).Kind("unknown"); throw new Exception("Unknown action guessed"); }
            catch (BuildException ex) { Check(ex.Message.Contains("无法归并"), "Unclear unknown-action diagnostic"); }
            var invalid = Document(); Map(L(invalid, "proxy-groups")[3], "group")["default-selected"] = "missing";
            try { new SubscriptionRouting(invalid).Kind("Service"); throw new Exception("Invalid default accepted"); } catch (BuildException) { }
            return Task.CompletedTask;
        });
        await test("Typed direct and reject outbounds retain their action", () =>
        {
            var doc = Document();
            L(doc, "proxies").Push(O("name", "D", "type", "direct")); L(doc, "proxies").Push(O("name", "R", "type", "reject"));
            var routing = new SubscriptionRouting(doc);
            Check(SubscriptionBaseline.Eligible(L(doc, "proxies"), O("include", ".*", "exclude", ""), O()).SequenceEqual(["香港", "日本"]), "Typed built-ins leaked into the unified proxy candidate list");
            Check(Canonical(routing.Rewrite(A("DOMAIN,a.example,D", "DOMAIN,b.example,R", "MATCH,COMPATIBLE"), "代理出口")) ==
                Canonical(A("DOMAIN,a.example,DIRECT", "DOMAIN,b.example,REJECT", "MATCH,DIRECT")), "Typed built-ins reclassified as proxy");
            return Task.CompletedTask;
        });
        await test("User rules override opposite subscription actions and provider downloads use unified exit", async () =>
        {
            using var f = new Fixture();
            f.Write("policy.yaml", O("rule_source", "subscription", "extra_rules_enabled", true, "direct", A("local.example"), "proxy", A("proxy.example"), "reject", A("block.example")));
            var doc = Document();
            doc["rules"] = A("DOMAIN-SUFFIX,local.example,Service", "DOMAIN-SUFFIX,proxy.example,Ad-looking-name", "DOMAIN-SUFFIX,block.example,Service", "RULE-SET,domains,Service", "MATCH,Final");
            doc["rule-providers"] = O("domains", O("type", "inline", "behavior", "domain", "proxy", "Service", "payload", A("+.provider.example")));
            f.Write("subscrip/A.yaml", doc);
            string snapshotBefore = File.ReadAllText(Path.Combine(f.Project, "subscrip/A.yaml"));
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var result = Map(YamlData.Read(built.OutputPath), "output");
            var rules = L(result, "rules").Select(x => S(x)).ToArray();
            Check(rules.Take(3).SequenceEqual(["DOMAIN-SUFFIX,local.example,DIRECT", "DOMAIN-SUFFIX,block.example,REJECT", "DOMAIN-SUFFIX,proxy.example,代理出口"]), "Custom rules do not precede subscription matches");
            Check(rules[3] == "DOMAIN-SUFFIX,local.example,代理出口" && rules[4] == "DOMAIN-SUFFIX,proxy.example,DIRECT", "Subscription matches reordered or deleted");
            Check(S(M(M(result, "rule-providers"), "domains"), "proxy") == "代理出口", "Provider references a removed business group");
            Check(File.ReadAllText(Path.Combine(f.Project, "subscrip/A.yaml")) == snapshotBefore, "Normalization rewrote subscription cache");
            Check(!L(result, "proxy-groups").OfType<JsonObject>().Any(x => S(x, "name") is "Service" or "Auto" or "Final" or "Ad-looking-name" or "Local-looking-name"), "Old policy groups remain");
            Check(M(result, "dns").Count == 0 && GeoRules.RequiredDatabases(result).Length == 0, "Normalization introduced DNS or databases");
        });
    }
}

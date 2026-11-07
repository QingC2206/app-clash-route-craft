using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class StrictRoutingRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (string mode in new[] { "subscription", "custom", "standalone" })
        await test("Strict rules override inherited direct fallback: " + mode, async () =>
        {
            using var f = new Fixture();
            var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
            if (mode == "standalone") { settings["sources"] = A(); f.Write("settings.yaml", settings); }
            var primary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            primary["rules"] = A("DOMAIN-SUFFIX,subscription.example,DIRECT", "MATCH,DIRECT"); f.Write("subscrip/A.yaml", primary);
            var policy = O("rule_source", mode == "custom" ? "custom" : "subscription", "strict_mode", true,
                "order", A("direct", "reject", "proxy"), "direct", A("allowed.example"), "reject", A("blocked.example"),
                "proxy", A("proxy.example"), "final", "DIRECT", "domestic_direct", true);
            f.Write("policy.yaml", policy);
            string before = File.ReadAllText(Path.Combine(f.Project, "subscrip/A.yaml"));
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var config = Map(YamlData.Read(result.OutputPath), "config"); string proxy = mode == "custom" ? "默认代理" : "代理出口";
            Check(Canonical(config["rules"]) == Canonical(A("DOMAIN-SUFFIX,allowed.example,DIRECT", "DOMAIN-SUFFIX,blocked.example,REJECT", "DOMAIN-SUFFIX,proxy.example," + proxy, "MATCH," + proxy)), "Strict rule ordering or fallback wrong");
            Check(GeoRules.RequiredDatabases(config).Length == 0 && config["dns"] is null, "Strict introduced DNS or databases");
            Check(!L(L(config, "proxy-groups").OfType<JsonObject>().Single(x => S(x, "name") == proxy), "proxies").Any(x => S(x) == "DIRECT"), "Proxy selector allows direct fallback");
            Check(B(result.Report, "strict_mode") && S(M(result.Report, "rule_merge"), "precedence") == "user_groups_then_proxy" && before == File.ReadAllText(Path.Combine(f.Project, "subscrip/A.yaml")), "Strict lost cache or report flag");
            policy["strict_mode"] = false; policy["domestic_direct"] = false; f.Write("policy.yaml", policy);
            var restored = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var restoredConfig = Map(YamlData.Read(restored.OutputPath), "config");
            Check(S(L(restoredConfig, "rules").Last()) == "MATCH,DIRECT", "Turning off strict did not restore prior fallback");
            if (mode == "subscription") Check(Canonical(restoredConfig["rules"]).Contains("subscription.example"), "Subscription rules not restored");
        });
        await test("Strict skips baseline ASN/providers but keeps hosts and transport dependencies", async () =>
        {
            using var f = new Fixture();
            var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            var node = Map(L(doc, "proxies")[0], "node"); node["server"] = "node.example"; node["dialer-proxy"] = "Transport";
            doc["hosts"] = O("node.example", "127.0.0.1");
            doc["proxy-groups"] = A(new JsonNode?[] { O("name", "Transport", "type", "select", "proxies", A("美国")) });
            doc["rules"] = A("IP-ASN,64512,DIRECT", "RULE-SET,Missing,DIRECT", "MATCH,DIRECT");
            doc["rule-providers"] = O("Missing", O("type", "file", "behavior", "classical", "path", "does-not-exist.yaml"));
            doc["sub-rules"] = O("Unused", A("IP-ASN,64513,DIRECT"));
            f.Write("subscrip/A.yaml", doc);
            f.Write("policy.yaml", O("rule_source", "subscription", "strict_mode", true, "extra_rules_enabled", false, "direct", A("ignored.example")));
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); var config = Map(YamlData.Read(result.OutputPath), "config");
            Check(Canonical(config["rules"]) == Canonical(A("MATCH,代理出口")), "Disabled extras applied");
            Check(config["rule-providers"] is null && config["sub-rules"] is null && !B(M(result.Report, "asn"), "native_required"), "Inherited rule dependencies retained");
            Check(S(M(config, "hosts"), "node.example") == "127.0.0.1" && L(config, "proxy-groups").OfType<JsonObject>().Any(x => S(x, "name") == "Transport"), "Transport dependencies lost");
            Check(L(result.Report, "sources").OfType<JsonObject>().All(x => !B(x, "rules_used")), "Report claims inherited rules used");
        });
        await test("Strict supports node-only subscriptions and retains explicitly chosen ASN checks", async () =>
        {
            using var f = new Fixture();
            var policy = O("rule_source", "subscription", "strict_mode", true, "proxy", A("IP-ASN,64512,no-resolve")); f.Write("policy.yaml", policy);
            bool blocked = false;
            try { await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); } catch (AsnDependencyException) { blocked = true; }
            Check(blocked, "Strict suppressed user ASN dependency check");
            policy["proxy"] = A(); f.Write("policy.yaml", policy);
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            Check(B(result.Report, "strict_mode"), "Node-only build failed");
        });
        await test("Strict rejects runtime direct mode and catchall overrides", async () =>
        {
            using var f = new Fixture(); var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; settings["runtime"] = O("mode", "direct"); f.Write("settings.yaml", settings);
            f.Write("policy.yaml", O("rule_source", "subscription", "strict_mode", true));
            bool blocked = false;
            try { await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); } catch (BuildException) { blocked = true; }
            Check(blocked, "Direct runtime bypassed strict mode");
            settings.Remove("runtime"); f.Write("settings.yaml", settings);
            foreach (string mode in new[] { "custom", "subscription" })
            {
                var imported = ImportedRuleSets.Parse(System.Text.Encoding.UTF8.GetBytes("MATCH,DIRECT"), "Catchall");
                imported["include_fallback"] = true; imported["mappings"] = O("DIRECT", "DIRECT");
                f.Write("policy.yaml", O("rule_source", mode, "strict_mode", true, "rule_sets", A(new JsonNode?[] { imported }), "domestic_direct", false)); blocked = false;
                try { await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); } catch (BuildException) { blocked = true; }
                Check(blocked, "User MATCH bypassed strict: " + mode);
            }
        });
        await test("Strict template roundtrip preserves DNS and old-template compatibility", () =>
        {
            var policy = O("strict_mode", true, "dns", O("enable", true));
            Check(B(RuleDefaults.Apply(policy, O("direct", A())), "strict_mode"), "Old template resets strict setting");
            var template = RuleDefaults.Extract(policy); Check(B(template, "strict_mode") && template["dns"] is null, "Template loses strict or copies DNS");
            return Task.CompletedTask;
        });
        await test("Strict does not offer direct pseudo-nodes as proxy exits", async () =>
        {
            using var f = new Fixture(); var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
            var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            L(doc, "proxies").Push(O("name", "香港伪代理", "type", "direct")); f.Write("subscrip/A.yaml", doc);
            foreach (string mode in new[] { "custom", "subscription" })
            {
                f.Write("policy.yaml", O("rule_source", mode, "strict_mode", true));
                var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); var config = Map(YamlData.Read(result.OutputPath), "config");
                Check(!Canonical(config["proxy-groups"]).Contains("香港伪代理"), "Direct pseudo-node offered as proxy exit");
            }
            f.Write("residential.yaml", O("proxies", A(new JsonNode?[] { O("name", "Fake", "type", "direct", "server", "127.0.0.1", "port", 1080) })));
            bool blocked = false;
            try { await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); } catch (BuildException) { blocked = true; }
            Check(blocked, "Special residence bypassed strict proxy fallback");
        });
        await test("Strict pulls nodes-only subscriptions and preserves dormant rule snapshots", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer();
            var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            server.Body = System.Text.Encoding.UTF8.GetString(YamlData.Bytes(doc));
            var settings = Map(YamlData.Read(f.Settings), "settings"); Map(L(settings, "sources")[0], "source")["url"] = server.Url; f.Write("settings.yaml", settings);
            f.Write("policy.yaml", O("rule_source", "subscription", "strict_mode", true));
            await SubscriptionUpdater.PullAsync(new WorkspaceSession(f.Root, "SampleOne"), 0);
            doc["rules"] = A("RULE-SET,Missing,DIRECT", "MATCH,DIRECT");
            doc["rule-providers"] = O("Missing", O("type", "file", "behavior", "classical", "path", "does-not-exist.yaml"));
            server.Body = System.Text.Encoding.UTF8.GetString(YamlData.Bytes(doc));
            await SubscriptionUpdater.PullAsync(new WorkspaceSession(f.Root, "SampleOne"), 0);
            var cached = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "cache");
            Check(Canonical(cached["rules"]) == Canonical(doc["rules"]), "Pull discarded rules required after turning off strict");
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            Check(B(result.Report, "strict_mode"), "Fresh strict snapshot did not compile");
        });
        if (Environment.GetEnvironmentVariable("CRC_TEST_MIHOMO") is { Length: > 0 } core)
        foreach (bool standalone in new[] { false, true })
        await test("Real Mihomo validates strict output, standalone=" + standalone, async () =>
        {
            using var f = new Fixture(); var settings = Map(YamlData.Read(f.Settings), "settings");
            if (standalone) { settings["sources"] = A(); f.Write("settings.yaml", settings); }
            f.Write("policy.yaml", O("rule_source", "subscription", "strict_mode", true, "direct", A("allowed.example"), "reject", A("blocked.example")));
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true, Validation: new(core, "", "")));
            Check(S(result.Report, "core_validation") == "passed", "Real kernel rejected strict output");
        });
    }
}

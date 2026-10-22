using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class SubscriptionBaselineRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Enable(Fixture f, bool extras = false)
    {
        var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
        policy["rule_source"] = "subscription"; policy["extra_rules_enabled"] = extras;
        // These dormant legacy settings must not contaminate or block the new baseline.
        policy["domestic_direct"] = true; policy["dns_follow_rules"] = true;
        policy["dns"] = O("enable", true, "nameserver", A("https://1.1.1.1/dns-query#obsolete-group"));
        f.Write("policy.yaml", policy);
    }
    static JsonObject Subscription(Fixture f, string source = "A")
    {
        var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip", source + ".yaml")), "snapshot");
        doc["proxy-groups"] = A(new JsonNode?[] { O("name", "Original", "type", "select", "proxies", A("Auto", "香港同名", "美国", "DIRECT")),
            O("name", "Auto", "type", "url-test", "url", "https://example.com/", "interval", 600, "proxies", A("香港同名", "美国")) });
        doc["rules"] = A(@"DOMAIN-REGEX,^api[0-9]{1,3}\.example$,REJECT", "DOMAIN,same.example,PASS", "DOMAIN,same.example,Original", "DOMAIN,same.example,DIRECT", "MATCH,Original");
        doc["dns"] = O("nameserver", A("1.2.3.4"));
        return doc;
    }
    static Task<BuildResult> Build(Fixture f) => new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Old rule templates preserve the chosen mode and new projects use subscription baseline", () =>
        {
            using var f = new Fixture(); var legacy = new WorkspaceSession(f.Root, "SampleOne").Policy;
            RuleDefaults.Save(f.Root, legacy); WorkspaceSession.CreateProject(f.Root, "NewBaseline");
            Check(SubscriptionBaseline.Enabled(new WorkspaceSession(f.Root, "NewBaseline").Policy), "Old template reverted new project to custom compiler");
            Check(!SubscriptionBaseline.Enabled(RuleDefaults.Apply(legacy, RuleDefaults.Read(f.Root))), "Applying old rules silently migrated an existing project");
            return Task.CompletedTask;
        });
        await test("Subscription baseline keeps only primary rules, regex and continuation order", async () =>
        {
            using var f = new Fixture(); Enable(f); var primary = Subscription(f); f.Write("subscrip/A.yaml", primary);
            var other = Subscription(f, "B"); other["rules"] = A("DOMAIN,same.example,REJECT", "MATCH,DIRECT");
            other["rule-providers"] = O("unusable-ignored", O("type", "file", "path", "missing.mrs", "format", "mrs")); f.Write("subscrip/B.yaml", other);
            var built = await Build(f); var output = Map(YamlData.Read(built.OutputPath), "output");
            Check(Canonical(output["rules"]) == Canonical(A(@"DOMAIN-REGEX,^api[0-9]{1,3}\.example$,REJECT", "DOMAIN,same.example,PASS", "DOMAIN,same.example,代理出口", "DOMAIN,same.example,DIRECT", "MATCH,代理出口")), "Matches, regex or continuation order changed");
            Check(output["dns"] is null && output["rule-providers"] is null && GeoRules.RequiredDatabases(output).Length == 0, "Generated extra DNS or geodata dependency");
            Check(L(output, "proxies").Count == 6 && !L(output, "proxy-groups").OfType<JsonObject>().Any(x => S(x, "name") is "Original" or "Auto"), "Subscription business groups survived normalization or nodes lost");
            var unified = L(output, "proxy-groups").OfType<JsonObject>().Single(x => S(x, "name") == "代理出口");
            Check(Canonical(unified["proxies"]) == Canonical(A("住宅出口", "机场入口")), "Unified exit does not default to residence or permits direct bypass");
            var entrance = L(output, "proxy-groups").OfType<JsonObject>().Single(x => S(x, "name") == "机场入口");
            Check(S(entrance, "type") == "select" && Canonical(entrance["proxies"]) == Canonical(A("聚合组", "A", "B")), "Entrance must select aggregate or actual subscription groups");
            var aggregate = L(output, "proxy-groups").OfType<JsonObject>().Single(x => S(x, "name") == "聚合组");
            Check(S(aggregate, "type") == "url-test" && L(aggregate, "proxies").Select(x => S(x)).SequenceEqual(["香港同名", "[B] 香港同名"]), "Subscription nodes not pooled for automatic testing");
            foreach (string name in new[] { "A", "B" })
            {
                var sourceGroup = L(output, "proxy-groups").OfType<JsonObject>().Single(x => S(x, "name") == name);
                Check(S(sourceGroup, "type") == "select" && Canonical(sourceGroup["proxies"]) == Canonical(A(name == "A" ? "香港同名" : "[B] 香港同名")), "Subscription group mixed nodes from another source");
            }
            Check(L(output, "proxy-groups").Count == 6 && !L(output, "proxy-groups").OfType<JsonObject>().Any(x => S(x, "type") == "fallback" || S(x, "name").StartsWith("优先级")), "Unexpected priority/fallback groups");
            Nodes.CheckGraph(output); Check(new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent(), "Baseline result not current");
        });
        await test("Priority reorder and disabled sources choose exactly one baseline", async () =>
        {
            using var f = new Fixture(); Enable(f); var a = Subscription(f); f.Write("subscrip/A.yaml", a);
            var b = Subscription(f, "B"); b["rules"] = A("DOMAIN,b-only.example,REJECT", "MATCH,Original"); f.Write("subscrip/B.yaml", b);
            var settings = Map(YamlData.Read(f.Settings), "settings"); var sources = L(settings, "sources").Select(x => x!.DeepClone()).Reverse(); settings["sources"] = A(sources); f.Write("settings.yaml", settings);
            Check(S((await Build(f)).Report, "baseline_source") == "B", "Visible priority order ignored");
            Map(L(settings, "sources")[0], "source")["enabled"] = false; f.Write("settings.yaml", settings);
            Check(S((await Build(f)).Report, "baseline_source") == "A", "Disabled primary still supplied rules");
        });
        await test("Node-only primary errors without taking rules from secondary", async () =>
        {
            using var f = new Fixture(); Enable(f); f.Write("subscrip/B.yaml", Subscription(f, "B"));
            try { await Build(f); throw new Exception("Unexpected fallback"); }
            catch (BuildException ex) { Check(ex.Message.Contains("没有规则") && ex.Message.Contains("不会改用"), "Unclear primary snapshot error"); }
        });
        await test("Baseline extras prefix original rules and stale MC2 DNS is removed", async () =>
        {
            using var f = new Fixture(); var old = await Build(f); string stale = Path.Combine(Path.GetDirectoryName(old.OutputPath)!, "HomeChain.MC2DNS.yaml"); Check(File.Exists(stale), "Legacy fixture did not output DNS");
            Enable(f, true); var primary = Subscription(f); f.Write("subscrip/A.yaml", primary);
            var built = await Build(f); var output = Map(YamlData.Read(built.OutputPath), "output");
            Check(S(L(output, "rules")[0]) == "DOMAIN-SUFFIX,example.cn,DIRECT", "Extras not first");
            Check(L(output, "rules").Count == 8 && S(L(output, "rules")[3]) == @"DOMAIN-REGEX,^api[0-9]{1,3}\.example$,REJECT" && S(L(output, "rules")[7]) == "MATCH,代理出口", "Subscription rules not appended after extras");
            Check(!File.Exists(stale) && !B(M(built.Report, "dns"), "configured"), "Stale DNS retained");
        });
        await test("Original rule providers and sub-rules survive baseline build", async () =>
        {
            using var f = new Fixture(); Enable(f); var primary = Subscription(f);
            primary["rule-providers"] = O("list", O("type", "inline", "behavior", "domain", "payload", A("+.example.org")));
            primary["sub-rules"] = O("nested", A("DOMAIN,nested.example,DIRECT", "MATCH,Original"));
            primary["rules"] = A("RULE-SET,list,Original", "SUB-RULE,(NETWORK,TCP),nested", "GEOIP,CN,DIRECT", "MATCH,Original");
            f.Write("subscrip/A.yaml", primary); var built = await Build(f); var output = Map(YamlData.Read(built.OutputPath), "output");
            Check(Canonical(primary["rule-providers"]) == Canonical(output["rule-providers"]), "Provider payload changed");
            Check(Canonical(output["rules"]) == Canonical(A("RULE-SET,list,代理出口", "SUB-RULE,(NETWORK,TCP),nested", "GEOIP,CN,DIRECT", "MATCH,代理出口")), "RULE-SET or SUB-RULE reference broken");
            Check(Canonical(M(output, "sub-rules")["nested"]) == Canonical(A("DOMAIN,nested.example,DIRECT", "MATCH,代理出口")), "Sub-rule actions not normalized");
            Check(GeoRules.RequiredDatabases(output).SequenceEqual(["country.mmdb"]), "Introduced new database requirements");
        });
        await test("Pull stores complete primary while secondary rules are never evaluated", async () =>
        {
            using var f = new Fixture(); Enable(f); using var server = new LocalServer(); var primary = Subscription(f); server.Body = YamlData.Write(primary);
            var session = new WorkspaceSession(f.Root, "SampleOne"); var settings = session.Settings; Map(L(settings, "sources")[0], "source")["url"] = server.Url; session.Documents["settings.yaml"].Set(settings);
            await SubscriptionUpdater.PullAsync(session, 0); var saved = Map(YamlData.Read(session.SnapshotPath("A")), "saved");
            Check(Canonical(saved["rules"]) == Canonical(primary["rules"]) && Canonical(saved["proxy-groups"]) == Canonical(primary["proxy-groups"]), "Pull discarded routing");
            primary["rule-providers"] = O("bad", O("type", "file", "format", "mrs", "path", "unreachable")); server.Body = YamlData.Write(primary);
            settings = session.Settings; Map(L(settings, "sources")[1], "source")["url"] = server.Url + "b"; session.Documents["settings.yaml"].Set(settings);
            await SubscriptionUpdater.PullAsync(session, 1);
            Check(Map(YamlData.Read(session.SnapshotPath("B")), "secondary").Count == 1, "Secondary routing imported");
        });
        await test("Force generation saves complete primary snapshot atomically", async () =>
        {
            using var f = new Fixture(); Enable(f); using var server = new LocalServer { Body = YamlData.Write(Subscription(f)) };
            var settings = Map(YamlData.Read(f.Settings), "settings"); Map(L(settings, "sources")[0], "source")["url"] = server.Url; Map(L(settings, "sources")[1], "source")["enabled"] = false; f.Write("settings.yaml", settings);
            await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Force: true));
            Check(L(Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "snapshot"), "rules").Count == 5, "Build stored node-only snapshot");
        });
        await test("Node-only refresh cannot overwrite a working complete primary snapshot", async () =>
        {
            using var f = new Fixture(); Enable(f); f.Write("subscrip/A.yaml", Subscription(f));
            using var server = new LocalServer { Body = "proxies: [{name: 香港, type: http, server: localhost, port: 80}]" };
            var settings = Map(YamlData.Read(f.Settings), "settings"); Map(L(settings, "sources")[0], "source")["url"] = server.Url; Map(L(settings, "sources")[1], "source")["enabled"] = false; f.Write("settings.yaml", settings);
            var session = new WorkspaceSession(f.Root, "SampleOne"); string before = DependencySnapshot.FileHash(session.SnapshotPath("A"));
            try { await SubscriptionUpdater.PullAsync(session, 0); throw new Exception("Node-only pull accepted"); }
            catch (BuildException ex) { Check(ex.Message.Contains("没有规则"), "Wrong pull failure"); }
            Check(DependencySnapshot.FileHash(session.SnapshotPath("A")) == before, "Working snapshot overwritten");
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Force: true));
            Check(I(built.Report, "baseline_rule_count", 0) == 5 && DependencySnapshot.FileHash(session.SnapshotPath("A")) == before, "Force refresh discarded working baseline");
        });
        await test("Disabled extras ignore old rule and geo options; active catch-all cannot swallow baseline", async () =>
        {
            using var f = new Fixture(); Enable(f); f.Write("subscrip/A.yaml", Subscription(f));
            var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy"); policy["geo_mode"] = "unused-old-value"; policy["order"] = A("invalid-old-order"); f.Write("policy.yaml", policy);
            await Build(f);
            policy["extra_rules_enabled"] = true; policy["order"] = A("direct", "reject", "proxy"); policy["first"] = A(new JsonNode?[] { O("match", "MATCH,DIRECT", "action", "DIRECT") }); f.Write("policy.yaml", policy);
            try { await Build(f); throw new Exception("Catch-all swallowed baseline"); }
            catch (BuildException ex) { Check(ex.Message.Contains("MATCH"), "Wrong extra fallback failure"); }
        });
        await test("Inactive DNS and old extras do not resolve their environment variables", async () =>
        {
            using var f = new Fixture(); Enable(f); var primary = Subscription(f); L(primary, "rules").Push(null); f.Write("subscrip/A.yaml", primary);
            var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
            policy["dns"] = O("nameserver", A("${CLASH_ROUTE_CRAFT_UNSET_LEGACY_DNS}")); policy["direct"] = A("${CLASH_ROUTE_CRAFT_UNSET_OLD_EXTRA}"); f.Write("policy.yaml", policy);
            var built = await Build(f); Check(I(built.Report, "baseline_rule_count", 0) == 5 && I(built.Report, "baseline_entry_count", 0) == 6, "Empty subscription entry counted as a rule");
            primary["rules"] = A(new JsonNode?[] { null }); f.Write("subscrip/A.yaml", primary);
            try { await Build(f); throw new Exception("Empty primary rules accepted"); } catch (BuildException ex) { Check(ex.Message.Contains("没有规则"), "Empty primary not explained"); }
        });
        await test("Provider groups are materialized with original filtering and order", async () =>
        {
            using var f = new Fixture(); using var fetch = new Fetcher(f.Project, O(), true);
            var input = O("proxy-providers", O("provider", O("type", "file", "path", "subscrip/A.yaml")),
                "proxy-groups", A(new JsonNode?[] { O("name", "Choose", "type", "select", "use", A("provider"), "filter", "香港", "proxies", A("DIRECT")) }), "rules", A("MATCH,Choose"));
            var snapshot = await SubscriptionBaseline.SnapshotAsync(input, fetch, f.Project, "A");
            Check(snapshot["proxy-providers"] is null && L(snapshot, "proxies").Count == 2, "Provider nodes lost");
            Check(Canonical(L(Map(L(snapshot, "proxy-groups")[0], "group"), "proxies")) == Canonical(A("DIRECT", "香港同名")), "Group filter or order changed");
            Nodes.CheckGraph(snapshot);
        });
        await test("Residential is optional and unified proxy defaults to airport", async () =>
        {
            using var f = new Fixture(); Enable(f); f.Write("residential.yaml", O("proxies", A())); f.Write("subscrip/A.yaml", Subscription(f));
            var built = await Build(f); Check(I(built.Report, "proxy_count", 0) == 4, "Optional residence required");
            var groups = L(Map(YamlData.Read(built.OutputPath), "output"), "proxy-groups").OfType<JsonObject>();
            Check(Canonical(groups.Single(x => S(x, "name") == "代理出口")["proxies"]) == Canonical(A("机场入口")), "No residence must use airport, never direct");
        });
        await test("Residential direct and chained copies preserve explicit UDP and TLS settings", async () =>
        {
            using var f = new Fixture(); Enable(f); f.Write("subscrip/A.yaml", Subscription(f));
            var residence = Map(L(Map(YamlData.Read(Path.Combine(f.Project, "residential.yaml")), "residence"), "proxies")[0], "node");
            residence["udp"] = false; residence["skip-cert-verify"] = false; f.Write("residential.yaml", O("proxies", A(new JsonNode?[] { residence })));
            var output = Map(YamlData.Read((await Build(f)).OutputPath), "output");
            var direct = (JsonObject)L(output, "proxies").OfType<JsonObject>().Single(x => S(x, "name") == "[直连] 住宅").DeepClone();
            var chain = (JsonObject)L(output, "proxies").OfType<JsonObject>().Single(x => S(x, "name") == "[链式] 住宅").DeepClone();
            Check(!direct.ContainsKey("dialer-proxy") && S(chain, "dialer-proxy") == "机场入口", "Direct and chained paths not distinct");
            direct["name"] = "住宅"; chain["name"] = "住宅"; chain.Remove("dialer-proxy");
            Check(Canonical(direct) == Canonical(residence) && Canonical(chain) == Canonical(residence), "Residential options or credentials changed");
            var selector = L(output, "proxy-groups").OfType<JsonObject>().Single(x => S(x, "name") == "住宅出口");
            Check(Canonical(selector["proxies"]) == Canonical(A("[链式] 住宅", "[直连] 住宅")), "Chain is not the default residential path");
        });
        await test("Empty jump filters never silently route residential traffic directly", async () =>
        {
            using var f = new Fixture(); Enable(f); f.Write("subscrip/A.yaml", Subscription(f));
            var settings = Map(YamlData.Read(f.Settings), "settings"); settings["selection"] = O("include", "^no-match$"); f.Write("settings.yaml", settings);
            try { await Build(f); throw new Exception("Empty jump became DIRECT"); }
            catch (BuildException ex) { Check(ex.Message.Contains("机场入口"), "Missing entrance not explained"); }
            f.Write("residential.yaml", O("proxies", A()));
            try { await Build(f); throw new Exception("Empty unified exit accepted without residence"); }
            catch (BuildException ex) { Check(ex.Message.Contains("机场入口"), "Missing entrance not explained"); }
        });
        await test("Residential injection never creates a dialer cycle", async () =>
        {
            using var f = new Fixture(); Enable(f); var primary = Subscription(f);
            Map(L(primary, "proxies")[0], "node")["dialer-proxy"] = "Upstream";
            L(primary, "proxy-groups").Push(O("name", "Upstream", "type", "select", "proxies", A("美国"))); f.Write("subscrip/A.yaml", primary);
            var output = Map(YamlData.Read((await Build(f)).OutputPath), "output"); Nodes.CheckGraph(output);
            var upstream = L(output, "proxy-groups").OfType<JsonObject>().Single(x => S(x, "name") == "Upstream");
            Check(Canonical(upstream["proxies"]) == Canonical(A("美国")) && B(upstream, "hidden"), "Transport group must be preserved and hidden without injecting a circular exit");
        });
        await test("Entrance defaults include four regions and honor explicit filters", () =>
        {
            string[] included = ["香港 01", "HongKong 02", "🇭🇰 03", "日本 01", "Tokyo 02", "🇯🇵 03", "台湾 01", "Taipei 02", "🇹🇼 03", "新加坡 01", "Singapore 02", "🇸🇬 03"];
            var nodes = A(included.Concat(["美国", "德国", "香港 剩余流量", "日本 到期", "新加坡 Static"]).Select(x => (JsonNode?)O("name", x)));
            Check(SubscriptionBaseline.Eligible(nodes, O(), O()).SequenceEqual(included), "Default regions or informational-node exclusion incorrect");
            Check(SubscriptionBaseline.Eligible(nodes, O(), O("include", "^德国$", "exclude", "")).SequenceEqual(["德国"]), "Global custom filter replaced by defaults");
            Check(SubscriptionBaseline.Eligible(nodes, O("include", "^美国$"), O("include", "^德国$")).SequenceEqual(["美国"]), "Source custom filter did not override global filter");
            return Task.CompletedTask;
        });
        await test("Aggregate and per-source pools use the same filters without priority tiers", async () =>
        {
            using var f = new Fixture(); Enable(f);
            var settings = Map(YamlData.Read(f.Settings), "settings"); M(settings, "selection").Remove("include");
            L(settings, "sources").Push(O("name", "Disabled", "enabled", false)); f.Write("settings.yaml", settings);
            foreach (string name in new[] { "A", "B" })
            {
                var doc = Subscription(f, name); int port = 2000;
                foreach (string nodeName in new[] { "日本", "台湾", "新加坡", "香港 到期" })
                    L(doc, "proxies").Push(O("name", nodeName, "type", "http", "server", "127.0.0.1", "port", port++));
                f.Write("subscrip/" + name + ".yaml", doc);
            }
            var output = Map(YamlData.Read((await Build(f)).OutputPath), "output");
            var groups = L(output, "proxy-groups").OfType<JsonObject>().ToDictionary(x => S(x, "name"));
            Check(L(groups["聚合组"], "proxies").Count == 8 && L(groups["A"], "proxies").Count == 4 && L(groups["B"], "proxies").Count == 4, "Aggregate or per-source pools lost eligible nodes");
            Check(Canonical(groups["聚合组"]["proxies"]) == Canonical(A(L(groups["A"], "proxies").Concat(L(groups["B"], "proxies")).Select(x => x?.DeepClone()))), "Aggregate differs from union of source pools");
            Check(new[] { "聚合组", "A", "B" }.All(x => S(groups[x], "type") == "url-test") && !groups.ContainsKey("Disabled"), "Source groups do not use automatic testing or include disabled sources");
            Check(L(output, "proxies").Any(x => S(Map(x, "node"), "name") == "美国") && !groups.ContainsKey("Auto"), "Unselected node removed or original business group retained");
            Map(L(settings, "sources")[1], "source")["include"] = "^no-match$"; f.Write("settings.yaml", settings);
            output = Map(YamlData.Read((await Build(f)).OutputPath), "output"); groups = L(output, "proxy-groups").OfType<JsonObject>().ToDictionary(x => S(x, "name"));
            Check(Canonical(groups["机场入口"]["proxies"]) == Canonical(A("聚合组", "A")) && !groups.ContainsKey("B") && L(groups["聚合组"], "proxies").Count == 4, "Empty source produced an invalid group or unfiltered aggregate");
            Nodes.CheckGraph(output);
        });
        await test("Existing custom proxy targets all use the unified exit", async () =>
        {
            using var f = new Fixture(); Enable(f, true); f.Write("subscrip/A.yaml", Subscription(f));
            var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
            string[] actions = ["住宅", "直连住宅 · 住宅", "[链式] 住宅", "[直连] 住宅", "订阅跳板", "AIRPORT", "A", "聚合组"];
            policy["first"] = A(actions.Select((action, index) => (JsonNode?)O("match", "DOMAIN,r" + index + ".example", "action", action))); f.Write("policy.yaml", policy);
            string inputBefore = File.ReadAllText(Path.Combine(f.Project, "residential.yaml"));
            var output = Map(YamlData.Read((await Build(f)).OutputPath), "output");
            Check(L(output, "rules").Take(actions.Length).All(x => Rules.SplitFull(S(x))[2] == "代理出口"), "Old explicit proxy action bypasses the unified exit");
            Check(File.ReadAllText(Path.Combine(f.Project, "residential.yaml")) == inputBefore, "Output labels changed residential input");
        });
        await test("Generated entrance and residential labels do not overwrite subscription names", async () =>
        {
            using var f = new Fixture(); Enable(f); var primary = Subscription(f);
            string[] existing = ["机场入口", "聚合组", "A", "住宅出口", "[链式] 住宅", "[直连] 住宅", "代理出口"];
            foreach (string name in existing) L(primary, "proxy-groups").Push(O("name", name, "type", "select", "proxies", A("香港同名")));
            f.Write("subscrip/A.yaml", primary); var built = await Build(f); var output = Map(YamlData.Read(built.OutputPath), "output");
            var groups = L(output, "proxy-groups").OfType<JsonObject>().ToDictionary(x => S(x, "name"));
            Check(S(built.Report, "jump_group") == "机场入口 · 2" && S(built.Report, "aggregate_group") == "聚合组 · 2", "Generated group collision unresolved");
            Check(Canonical(groups["机场入口 · 2"]["proxies"]) == Canonical(A("聚合组 · 2", "A · 2", "B")), "Entrance chose original colliding groups");
            foreach (string name in existing) Check(!groups.ContainsKey(name), "Unneeded subscription group survived");
            Check(S(built.Report, "main_selector") == "代理出口 · 2" && S(L(output, "rules").Last()) == "MATCH,代理出口 · 2", "Unified exit collision changed rule references");
            Check(Canonical(groups["住宅出口 · 2"]["proxies"]) == Canonical(A("[链式] 住宅 · 2", "[直连] 住宅 · 2")), "Residential collision unresolved");
            Nodes.CheckGraph(output);
        });
    }
}

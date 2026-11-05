using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class ResidentialOnlyRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (string mode in new[] { "subscription", "custom" })
        await test("Residential only builds without subscriptions: " + mode, async () =>
        {
            using var f = new Fixture();
            var settings = Map(YamlData.Read(f.Settings), "settings"); settings["sources"] = A(); f.Write("settings.yaml", settings);
            var policy = O("rule_source", mode, "order", A("reject", "direct", "proxy"), "direct", A("exception.example"), "reject", A("bad.example"), "proxy", A("proxy.example"), "final", "DIRECT");
            f.Write("policy.yaml", policy);
            var before = Directory.GetFiles(f.Project, "*.yaml").ToDictionary(x => x, File.ReadAllText);
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var config = Map(YamlData.Read(built.OutputPath), "config");
            Check(S(built.Report, "rule_source") == "residential", "wrong baseline");
            Check(L(config, "proxies").Count == 1 && S(Map(L(config, "proxies")[0], "node"), "name") == "[直连] 住宅", "Unexpected nodes");
            Check(!Canonical(config).Contains("dialer-proxy") && !Canonical(config).Contains("机场入口"), "Airport dependency retained");
            Check(Canonical(config["rules"]) == Canonical(A("DOMAIN-SUFFIX,bad.example,REJECT", "DOMAIN-SUFFIX,exception.example,DIRECT", "DOMAIN-SUFFIX,proxy.example,代理出口", "MATCH,DIRECT")), "Custom precedence or fallback wrong");
            Check(config["dns"] is null && GeoRules.RequiredDatabases(config).Length == 0, "Default added DNS or database");
            Check(before.All(x => File.ReadAllText(x.Key) == x.Value), "Build changed project inputs");
        });
        await test("Disabled subscriptions need no cache; residential DNS and defaults work", async () =>
        {
            using var f = new Fixture();
            var settings = Map(YamlData.Read(f.Settings), "settings");
            foreach (var source in L(settings, "sources").OfType<JsonObject>()) { source["enabled"] = false; File.Delete(Path.Combine(f.Project, "subscrip", S(source, "name") + ".yaml")); }
            f.Write("settings.yaml", settings);
            f.Write("policy.yaml", O("rule_source", "subscription", "extra_rules_enabled", false, "direct", A("ignored.example"), "dns_privacy", O("enabled", true, "proxy_exit", "residential")));
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); var config = Map(YamlData.Read(built.OutputPath), "config");
            Check(Canonical(config["rules"]) == Canonical(A("MATCH,代理出口")), "Disabled rules or default changed");
            Check(L(M(config, "dns"), "nameserver").All(x => S(x).EndsWith("#住宅出口")), "DNS exit wrong");
            Check(File.Exists(Path.Combine(Path.GetDirectoryName(built.OutputPath)!, "HomeChain.MC2DNS.yaml")), "Missing companion DNS");
        });
        await test("Residential only rejects missing nodes, invalid fallback and airport DNS", () =>
        {
            using var f = new Fixture(); var settings = Map(YamlData.Read(f.Settings), "settings"); settings["sources"] = A(); f.Write("settings.yaml", settings);
            f.Write("policy.yaml", O("rule_source", "subscription", "final", "AIRPORT", "dns_privacy", O("enabled", true, "proxy_exit", "airport")));
            f.Write("residential.yaml", O("proxies", A()));
            var failures = ParameterInspection.Inspect(new WorkspaceSession(f.Root, "SampleOne"), checkDependencies: false).Where(x => x.State == "fail").ToArray();
            Check(failures.Any(x => x.Title == "住宅出口 · 名称") && failures.Any(x => x.Title == "订阅规则基线") && failures.Any(x => x.Title == "DNS · 地址与出口"), "Preflight missed dependencies");
            return Task.CompletedTask;
        });
        await test("Rule presets load, reload and append without changing unrelated policy", () =>
        {
            using var f = new Fixture(); BuiltInDefaults.Ensure(f.Root);
            Check(RulePresets.Load(f.Root).Count >= 4, "Missing bundled catalogue");
            var catalogue = O("version", 1, "presets", A(new JsonNode?[] { O("id", "test", "name", "Test", "category", "Custom", "urls", A("https://example.com/rules.yaml"), "domains", A("example.com"), "rules", A("DOMAIN,api.example.org", @"DOMAIN-REGEX,^a[0-9]{1,3}\.example$")) }));
            File.WriteAllBytes(RulePresets.FilePath(f.Root), YamlData.Bytes(catalogue)); BuiltInDefaults.Ensure(f.Root);
            var presets = RulePresets.Load(f.Root); Check(presets.Single().Name == "Test", "Catalogue overwritten or not reloaded");
            var original = O("order", A("reject", "proxy", "direct"), "direct", A("example.com"), "proxy", A("example.com"), "dns", O("nameserver", A("1.1.1.1")), "extra_rules_enabled", false);
            string before = Canonical(original);
            var appended = RulePresets.Append(original, presets, "direct");
            Check(appended.UrlsAdded == 1 && appended.RulesAdded == 2 && appended.Skipped == 1, "Wrong appended counts");
            Check(Canonical(original) == before && Canonical(appended.Policy["dns"]) == Canonical(original["dns"]) && Canonical(appended.Policy["order"]) == Canonical(original["order"]) && !B(appended.Policy, "extra_rules_enabled"), "Append changed existing settings");
            var again = RulePresets.Append(appended.Policy, presets, "direct"); Check(again.UrlsAdded + again.RulesAdded == 0 && again.Skipped == 4, "Repeated append duplicates entries");
            var other = RulePresets.Append(again.Policy, presets, "reject"); Check(other.CrossGroupMatches == 4 && other.UrlsAdded == 1 && other.RulesAdded == 3, "Cross-group entries lost");
            ParameterInspection.ValidateRules(other.Policy);
            return Task.CompletedTask;
        });
        await test("Rule preset malformed domains, URLs and catchalls rejected", () =>
        {
            foreach (var entry in new[] { O("domains", A("https://example.com/path")), O("urls", A("file:///private")), O("rules", A("MATCH")), O("rules", A("DOMAIN,a.example,REJECT")) })
            {
                bool failed = false;
                try { RulePresets.Parse(O("version", 1, "presets", A(new JsonNode?[] { Merge(O("id", "bad", "name", "Bad"), entry) }))); }
                catch (BuildException) { failed = true; }
                Check(failed, "Invalid catalogue accepted: " + Canonical(entry));
            }
            return Task.CompletedTask;
        });
        if (Environment.GetEnvironmentVariable("CRC_TEST_PRESETS_DIR") is { Length: > 0 } downloads)
        await test("Downloaded public preset URLs parse using the unchanged rule compiler", () =>
        {
            var manifest = List(JsonNode.Parse(File.ReadAllText(Path.Combine(downloads, "manifest.json"))), "manifest");
            foreach (var item in manifest.OfType<JsonObject>())
            {
                var rules = Rules.ParseRemote(File.ReadAllBytes(Path.Combine(downloads, S(item, "file"))), O("url", S(item, "url")));
                Check(rules.Count > 0, "Empty preset URL");
                foreach (var rule in rules) Rules.ValidateSyntax(S(rule));
                Check(GeoRules.RequiredDatabases(O("rules", rules)).Length == 0, "Catalogue introduced database dependency");
            }
            return Task.CompletedTask;
        });
        if (Environment.GetEnvironmentVariable("CRC_TEST_MIHOMO") is { Length: > 0 } core)
        foreach (bool privacy in new[] { false, true })
        await test("Real Mihomo validates residential-only output, privacy=" + privacy, async () =>
        {
            using var f = new Fixture(); var settings = Map(YamlData.Read(f.Settings), "settings"); settings["sources"] = A(); f.Write("settings.yaml", settings);
            f.Write("policy.yaml", O("rule_source", "subscription", "dns_privacy", O("enabled", privacy, "proxy_exit", "residential")));
            var validation = new ValidationPaths(core, "", "");
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true, Validation: validation));
            Check(S(result.Report, "core_validation") == "passed", "Real kernel did not pass");
            if (privacy) Check(S(M(result.Report, "dns"), "mc2_core_validation") == "passed", "MC2 DNS fragment did not pass");
        });
    }
}

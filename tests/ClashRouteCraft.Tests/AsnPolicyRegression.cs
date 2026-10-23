using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class AsnPolicyRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static JsonObject Policy(Fixture f) => new WorkspaceSession(f.Root, "SampleOne").Policy;
    static Task<BuildResult> Build(Fixture f, bool offline = true, bool inspect = false) => new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: offline, AutoFetch: false, InspectAsnOnly: inspect));
    static async Task<AsnDependencyException> Blocked(Func<Task> run)
    {
        try { await run(); } catch (AsnDependencyException ex) { return ex; }
        throw new Exception("Expected ASN dependency gate");
    }
    static async Task Fails(Func<Task> run)
    {
        try { await run(); } catch (BuildException) { return; }
        throw new Exception("Expected build failure");
    }
    static JsonObject Primary(Fixture f, params string[] rules)
    {
        var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
        doc["proxy-groups"] = A(new JsonNode?[] { O("name", "Original", "type", "select", "proxies", A("香港同名", "美国")) });
        doc["rules"] = A(rules); return doc;
    }
    static void Baseline(Fixture f)
    {
        var p = Policy(f); p["rule_source"] = "subscription"; p["extra_rules_enabled"] = false; f.Write("policy.yaml", p);
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("ASN dependency scanner parses predicates without matching regex or domain text", () =>
        {
            var harmless = O("rules", A(@"DOMAIN-REGEX,^(?:IP-ASN,123|GEOSITE,cn){1,3}$,REJECT", "DOMAIN-KEYWORD,IP-ASN,DIRECT", "MATCH,DIRECT"),
                "rule-providers", O("IP-ASN,123", O("type", "inline", "behavior", "domain", "payload", A("+.example.com"))));
            Check(GeoRules.RequiredDatabases(harmless).Length == 0, "Literal text introduced a database");
            var nested = O("rules", A("SUB-RULE,(AND,((NETWORK,TCP),(NOT,((SRC-IP-ASN,64512))))),sub"),
                "sub-rules", O("sub", A("OR,((IP-ASN,64513),(DOMAIN,keep.example)),DIRECT")));
            Check(GeoRules.RequiredDatabases(nested).SequenceEqual(["ASN.mmdb"]), "Nested source or destination ASN missing");
            return Task.CompletedTask;
        });
        await test("MC2 blocks new ASN dependencies atomically even when local validation is optional", async () =>
        {
            using var f = new Fixture(); var original = await Build(f); byte[] before = File.ReadAllBytes(original.OutputPath);
            var p = Policy(f); p["proxy"] = A("IP-ASN,64512,no-resolve"); f.Write("policy.yaml", p);
            var failure = await Blocked(() => Build(f));
            Check(B(failure.Analysis, "native_required") && L(failure.Analysis, "sources").OfType<JsonObject>().Any(x => S(x, "id") == "manual:proxy" && I(x, "detected", 0) == 1), "Origin lost");
            Check(File.ReadAllBytes(original.OutputPath).SequenceEqual(before), "Blocked build replaced main output");
            Check(!AsnPolicy.AllowsNative(Policy(f)), "Implicitly enabled advanced mode");
        });
        await test("ASN inspection returns sources without committing output or policy", async () =>
        {
            using var f = new Fixture(); var p = Policy(f); p["reject"] = A("IP-ASN,64512"); f.Write("policy.yaml", p);
            byte[] input = File.ReadAllBytes(Path.Combine(f.Project, "policy.yaml"));
            var result = await Build(f, inspect: true);
            Check(S(M(result.Report, "asn"), "status") == "blocked" && result.OutputPath.Length == 0, "Inspection should return unresolved analysis");
            Check(!File.Exists(Path.Combine(f.Root, "output/SampleOne/HomeChain.yaml")) && File.ReadAllBytes(Path.Combine(f.Project, "policy.yaml")).SequenceEqual(input), "Inspection wrote project/output");
        });
        await test("Per-source ASN exclusion removes whole logical rules and preserves other sources and priorities", async () =>
        {
            using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Project, "one.list"), "AND,((DOMAIN,guard.example),(IP-ASN,64512))\nDOMAIN,allowed.example\n");
            File.WriteAllText(Path.Combine(f.Project, "two.list"), "IP-ASN,64512,no-resolve\nDOMAIN,other.example\n");
            var one = O("name", "One", "file", "one.list", "action", "REJECT"); var two = O("name", "Two", "file", "two.list", "action", "PROXY");
            var p = Policy(f); p["remote_rules"] = A(new JsonNode?[] { one, two }); p["asn_policy"] = O("allow_native", true); AsnPolicy.SetExcluded(p, AsnPolicy.RemoteId(one), true); f.Write("policy.yaml", p);
            var built = await Build(f); var c = Map(YamlData.Read(built.OutputPath), "configuration");
            Check(!L(c, "rules").Any(x => S(x).Contains("guard.example")), "Logical condition was weakened instead of removing outer rule");
            Check(L(c, "rules").Any(x => S(x) == "IP-ASN,64512,默认代理,no-resolve"), "Another source was filtered");
            Check(L(c, "rules").Any(x => S(x) == "DOMAIN,allowed.example,REJECT"), "Non-ASN rule lost");
            Check(I(M(built.Report, "asn"), "excluded_rules", 0) == 1 && S(M(built.Report, "target_dependencies"), "status") == "requires_target_setup", "Exclusion or retained dependency not reported");
            string id = AsnPolicy.RemoteId(one); one["name"] = "Renamed";
            Check(AsnPolicy.RemoteId(one) == id, "Renaming/reordering resets URL/file exclusion");
            one["file"] = "new.list"; Check(AsnPolicy.RemoteId(one) != id, "Different source inherited exclusion");
        });
        await test("Primary ASN, sub-rules and inline providers are checked while secondary rules are ignored", async () =>
        {
            using var f = new Fixture(); Baseline(f);
            var doc = Primary(f, "IP-ASN,64512,Original", "DOMAIN,still.example,DIRECT", "MATCH,Original");
            doc["sub-rules"] = O("unused", A("SRC-IP-ASN,64513,DIRECT", "MATCH,DIRECT"));
            doc["rule-providers"] = O("inline", O("type", "inline", "behavior", "classical", "payload", A("NOT,((IP-ASN,64514))", "DOMAIN,keep.example")));
            f.Write("subscrip/A.yaml", doc);
            var failure = await Blocked(() => Build(f));
            var origin = L(failure.Analysis, "sources").OfType<JsonObject>().Single(x => S(x, "id") == AsnPolicy.SubscriptionId("A"));
            Check(I(origin, "detected", 0) == 3, "Did not inspect all primary rule locations");
            var p = Policy(f); AsnPolicy.SetExcluded(p, AsnPolicy.SubscriptionId("A"), true); f.Write("policy.yaml", p);
            var result = await Build(f); var c = Map(YamlData.Read(result.OutputPath), "configuration");
            Check(!GeoRules.RequiredDatabases(c).Contains("ASN.mmdb") && S(L(c, "rules")[0]) == "DOMAIN,still.example,DIRECT", "Source exclusion changed other primary rules");
            Check(L(M(M(c, "rule-providers"), "inline"), "payload").Count == 1, "Inline provider not filtered");
            var secondary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/B.yaml")), "secondary"); secondary["rules"] = A("IP-ASN,64515,REJECT"); f.Write("subscrip/B.yaml", secondary);
            var again = await Build(f); Check(!B(M(again.Report, "asn"), "native_required"), "Secondary subscription rules affected baseline");
            var session = new WorkspaceSession(f.Root, "SampleOne"); session.RenameSource(0, "Renamed"); Check(AsnPolicy.Excludes(session.Policy, AsnPolicy.SubscriptionId("Renamed")), "Source rename dropped exclusion choice");
        });
        await test("Uninspected classical providers cannot report no ASN and explicit exclusion snapshots them", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "payload:\n- IP-ASN,64512\n- DOMAIN,keep.example\n" }; Baseline(f);
            var doc = Primary(f, "RULE-SET,remote,Original", "MATCH,Original");
            doc["rule-providers"] = O("remote", O("type", "http", "behavior", "classical", "url", server.Url, "path", "./rules/remote.yaml", "interval", 86400)); f.Write("subscrip/A.yaml", doc);
            var failure = await Blocked(() => Build(f)); Check(I(failure.Analysis, "uninspected_providers", 0) == 1 && server.Requests == 0, "Unknown provider should be explicit");
            var p = Policy(f); p["asn_policy"] = O("allow_native", true); f.Write("policy.yaml", p);
            var retained = await Build(f); Check(S(M(retained.Report, "asn"), "status") == "requires_target_setup", "Unknown provider incorrectly claimed safe");
            p["asn_policy"] = O(); AsnPolicy.SetExcluded(p, AsnPolicy.SubscriptionId("A"), true); f.Write("policy.yaml", p);
            var result = await Build(f, offline: false); var output = Map(YamlData.Read(result.OutputPath), "output"); var provider = M(M(output, "rule-providers"), "remote");
            Check(server.Requests == 1 && S(provider, "type") == "inline" && !provider.ContainsKey("url") && !provider.ContainsKey("interval"), "Live provider could download filtered rules again");
            Check(Canonical(provider["payload"]) == Canonical(A("DOMAIN,keep.example")) && S(L(output, "rules")[0]) == "RULE-SET,remote,代理出口", "Provider matches/actions changed");
            Check(S(M(result.Report, "asn"), "status") == "no_asn_dependency", "Snapshot retained unknown status");
        });
        await test("Unreadable ASN exclusion fails instead of dropping an entire remote ruleset", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Status = 503 }; Baseline(f);
            var doc = Primary(f, "MATCH,Original"); f.Write("subscrip/A.yaml", doc); var previous = await Build(f); var bytes = File.ReadAllBytes(previous.OutputPath);
            doc["rule-providers"] = O("remote", O("type", "http", "behavior", "classical", "url", server.Url, "path", "./rules/remote.yaml")); f.Write("subscrip/A.yaml", doc);
            var p = Policy(f); AsnPolicy.SetExcluded(p, AsnPolicy.SubscriptionId("A"), true); f.Write("policy.yaml", p);
            await Fails(() => Build(f, offline: false)); Check(File.ReadAllBytes(previous.OutputPath).SequenceEqual(bytes), "Failed exclusion replaced old output");
        });
        await test("Explicit native ASN retention preserves flags and exposes target requirements", async () =>
        {
            using var f = new Fixture(); var p = Policy(f); p["proxy"] = A("IP-ASN,64512,no-resolve"); p["asn_policy"] = O("allow_native", true); f.Write("policy.yaml", p);
            var result = await Build(f); var c = Map(YamlData.Read(result.OutputPath), "output");
            Check(L(c, "rules").Any(x => S(x) == "IP-ASN,64512,默认代理,no-resolve"), "Native match/flags changed");
            Check(S(M(result.Report, "target_dependencies"), "status") == "requires_target_setup" && !B(M(result.Report, "target_dependencies"), "router_runtime_tested"), "Local success pretended router readiness");
        });
        await test("Imported ASN exclusions retain non-ASN rules in both compiler modes", async () =>
        {
            foreach (bool baseline in new[] { false, true })
            {
                using var f = new Fixture();
                if (baseline) { Baseline(f); f.Write("subscrip/A.yaml", Primary(f, "DOMAIN,base.example,DIRECT", "MATCH,Original")); }
                var p = Policy(f); p["extra_rules_enabled"] = true;
                var imported = ImportedRuleSets.Parse(System.Text.Encoding.UTF8.GetBytes("IP-ASN,64512,REJECT\nDOMAIN,keep.example,REJECT"), "Imported");
                p["rule_sets"] = new JsonArray(imported); f.Write("policy.yaml", p);
                var failure = await Blocked(() => Build(f)); string id = "imported:" + S(imported, "id");
                Check(L(failure.Analysis, "sources").OfType<JsonObject>().Any(x => S(x, "id") == id && I(x, "detected", 0) == 1), "Imported source was not identified");
                AsnPolicy.SetExcluded(p, id, true); f.Write("policy.yaml", p);
                var built = await Build(f); var c = Map(YamlData.Read(built.OutputPath), "output");
                Check(!GeoRules.RequiredDatabases(c).Contains("ASN.mmdb") && L(c, "rules").Any(x => S(x) == "DOMAIN,keep.example,REJECT"), "Imported exclusion changed unrelated rules");
            }
        });
        await test("Ordinary Clash targets retain ASN without inheriting the MC2 compatibility gate", async () =>
        {
            using var f = new Fixture(); var settings = Map(YamlData.Read(Path.Combine(f.Project, "settings.yaml")), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
            var p = Policy(f); p["proxy"] = A("IP-ASN,64512,no-resolve"); f.Write("policy.yaml", p);
            var built = await Build(f);
            Check(S(M(built.Report, "asn"), "status") == "requires_target_setup" && !AsnPolicy.AllowsNative(Policy(f)), "Clash target was silently opted into MC2 advanced mode");
            Check(L(Map(YamlData.Read(built.OutputPath), "output"), "rules").Any(x => S(x).StartsWith("IP-ASN,64512,")), "Clash ASN lost");
        });
        await test("Real core validation uses clean files and never repairs a corrupt ASN from the network", async () =>
        {
            string? core = Environment.GetEnvironmentVariable("CRC_TEST_MIHOMO");
            if (string.IsNullOrEmpty(core)) { Console.WriteLine("SKIP real core: set CRC_TEST_MIHOMO"); return; }
            using var f = new Fixture(); using var server = new LocalServer { Body = "unexpected download" };
            string data = Path.Combine(f.Root, "validation-data"); Directory.CreateDirectory(data);
            string asn = Path.Combine(data, "ASN.mmdb"); File.WriteAllText(asn, "corrupt ASN must stay unchanged");
            var simple = O("rules", A("MATCH,DIRECT"));
            await CoreValidation.ValidateAsync(simple, core, data, Path.Combine(f.Root, "no-asn.log"));
            var needsAsn = O("rules", A("IP-ASN,64512,DIRECT", "MATCH,DIRECT"), "geox-url", O("asn", server.Url)); string input = Canonical(needsAsn);
            await Fails(() => CoreValidation.ValidateAsync(needsAsn, core, data, Path.Combine(f.Root, "bad-asn.log")));
            Check(server.Requests == 0 && File.ReadAllText(asn) == "corrupt ASN must stay unchanged" && Canonical(needsAsn) == input, "Validation mutated input/assets or silently downloaded");
            File.Delete(asn); await Fails(() => CoreValidation.ValidateAsync(needsAsn, core, data, Path.Combine(f.Root, "missing-asn.log")));
            string? geo = Environment.GetEnvironmentVariable("CRC_TEST_GEODATA");
            if (!string.IsNullOrEmpty(geo))
            {
                File.Copy(Path.Combine(geo, "ASN.mmdb"), asn);
                await CoreValidation.ValidateAsync(needsAsn, core, data, Path.Combine(f.Root, "valid-asn.log"));
                // A valid old core cache cannot satisfy missing declared input data.
                string oldCache = Path.Combine(f.Project, ".cache", "core-data"); Directory.CreateDirectory(oldCache); File.Copy(asn, Path.Combine(oldCache, "ASN.mmdb"));
                var settings = Map(YamlData.Read(Path.Combine(f.Project, "settings.yaml")), "settings"); settings["validation"] = O("mihomo", core, "required", true); f.Write("settings.yaml", settings);
                var policy = Policy(f); policy["proxy"] = A("IP-ASN,64512"); policy["asn_policy"] = O("allow_native", true); f.Write("policy.yaml", policy);
                await Fails(() => Build(f));
            }
            var emptyProvider = O("rules", A("RULE-SET,empty,DIRECT", "MATCH,DIRECT"), "rule-providers", O("empty", O("type", "inline", "behavior", "classical", "payload", A())));
            await CoreValidation.ValidateAsync(emptyProvider, core, data, Path.Combine(f.Root, "empty-provider.log"));
        });
    }
}

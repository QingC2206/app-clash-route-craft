using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class ImportedRuleSetsRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Fails(Action action) { try { action(); } catch (BuildException) { return; } throw new Exception("Expected validation failure"); }
    static JsonObject OfflineSettings(WorkspaceSession session)
    {
        var settings = session.Settings;
        foreach (var raw in L(settings, "sources")) { var source = Map(raw, "source"); source.Remove("url"); source["file"] = session.SnapshotPath(S(source, "name")); }
        return settings;
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Generic import extracts ordered rules without proxy credentials and requires unknown policy mappings", () =>
        {
            var set = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("proxies: [{name: secret-node, password: SUPERSECRET}]\ndns: {nameserver: [9.9.9.9]}\nrules:\n - 'DOMAIN-REGEX,^api[0-9]{1,3}\\.example$,ExternalGroup'\n - DOMAIN,api.example,PASS\n - DOMAIN,api.example,DIRECT\n - IP-CIDR,10.0.0.0/8,DIRECT,no-resolve\n - GEOIP,CN,DIRECT\n - MATCH,ExternalGroup\n"), "Base");
            Check(!Canonical(set).Contains("SUPERSECRET") && !Canonical(set).Contains("secret-node") && !Canonical(set).Contains("9.9.9.9"), "Credentials leaked into imported snapshot");
            Check(L(set, "rules").Count == 6 && S(Map(L(set, "rules")[0], "rule"), "match").Contains("{1,3}"), "Regex or rules were truncated");
            Fails(() => ParameterInspection.ValidateRules(O("rule_sets", new JsonArray(set.DeepClone()))));
            M(set, "mappings")["ExternalGroup"] = "PROXY";
            ParameterInspection.ValidateRules(O("rule_sets", new JsonArray(set.DeepClone())));
            Check(L(set, "rules").OfType<JsonObject>().Count(e => ImportedRuleSets.Included(set, e)) == 4, "Source fallback was silently imported");
            Check(ImportedRuleSets.Rule(Map(L(set, "rules")[3], "rule"), "DIRECT").EndsWith(",DIRECT,no-resolve"), "Rule flags lost");
            Fails(() => ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("rules: [RULE-SET,foo]"), "bad"));
            Fails(() => ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("rules: ['RULE-SET,external,DIRECT']"), "bad"));
            return Task.CompletedTask;
        });
        await test("Domain lists and full text import preserve wildcard and logical semantics", () =>
        {
            var set = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("*.example.com\n+.example.net\nexact.example.org"), "domains");
            var matches = L(set, "rules").OfType<JsonObject>().Select(e => S(e, "match")).ToArray();
            Check(matches[0].StartsWith("DOMAIN-REGEX,") && matches[1] == "DOMAIN-SUFFIX,example.net" && matches[2] == "DOMAIN,exact.example.org", "Domain list semantics changed");
            var logic = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("AND,((DOMAIN,api.example),(NETWORK,UDP)),DIRECT\nDOMAIN,api.example,PASS\nDOMAIN,api.example,REJECT"), "logic");
            Check(L(logic, "rules").Count == 3 && S(Map(L(logic, "rules")[0], "r"), "source_action") == "DIRECT", "Logical rule action misparsed");
            return Task.CompletedTask;
        });
        await test("Legacy migration preserves generated traffic and DNS rules and is idempotent", async () =>
        {
            using var f = new Fixture(); var session = new WorkspaceSession(f.Root, "SampleOne"); var policy = session.Policy;
            policy["dns"] = DnsSettings.CreateDefault(); policy["dns_follow_rules"] = true;
            policy["supplemental_rules"] = new JsonArray(O("match", "DOMAIN,a.example", "action", "PASS"), O("match", "DOMAIN,a.example", "action", "DIRECT"), O("match", "DOMAIN-SUFFIX,example", "action", "PROXY"));
            f.Write("policy.yaml", policy); var before = await ProfileCompiler.CompileAsync(OfflineSettings(session), f.Project, true);
            var migrated = ImportedRuleSets.Migrate(policy);
            Check(!migrated.ContainsKey("supplemental_rules") && Canonical(ImportedRuleSets.Migrate(migrated)) == Canonical(migrated), "Migration duplicates entries");
            f.Write("policy.yaml", migrated); var after = await ProfileCompiler.CompileAsync(OfflineSettings(session), f.Project, true);
            Check(Canonical(before.Configuration) == Canonical(after.Configuration), "Migration changes generated rules or DNS");
            RuleDefaults.Save(f.Root, migrated); var applied = RuleDefaults.Apply(policy, RuleDefaults.Read(f.Root));
            Check(Canonical(applied["dns"]) == Canonical(policy["dns"]) && B(applied, "dns_follow_rules"), "Applying template changes DNS");
            WorkspaceSession.CreateProject(f.Root, "Inherited"); Check(ImportedRuleSets.Sets(new WorkspaceSession(f.Root, "Inherited").Policy).Count == 1, "New project lost imported rules");
        });
        await test("Imported sets obey source order and enabled state and reference renaming", async () =>
        {
            using var f = new Fixture(); var session = new WorkspaceSession(f.Root, "SampleOne"); var policy = session.Policy;
            var set = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("DOMAIN,same.example,PASS\nDOMAIN,same.example,DIRECT\nMATCH,DIRECT"), "generic");
            var second = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("DOMAIN,disabled.example,REJECT"), "disabled"); second["enabled"] = false;
            policy["rule_sets"] = new JsonArray(set, second); f.Write("policy.yaml", policy);
            var built = await ProfileCompiler.CompileAsync(OfflineSettings(session), f.Project, true); var rules = L(built.Configuration, "rules").Select(x => S(x)).ToList();
            Check(rules.IndexOf("DOMAIN,same.example,PASS") < rules.IndexOf("DOMAIN,same.example,DIRECT") && !rules.Contains("MATCH,DIRECT") && !rules.Any(x => x.Contains("disabled.example")), "Enabled state, fallback or PASS order lost");
            set["include_fallback"] = true; policy["dns_follow_rules"] = true; policy["dns"] = DnsSettings.CreateDefault(); f.Write("policy.yaml", policy);
            var included = await ProfileCompiler.CompileAsync(OfflineSettings(session), f.Project, true);
            Check(M(included.Configuration, "rule-providers").Any(x => L(Map(x.Value, "provider"), "payload").Any(y => S(y) == "DOMAIN-REGEX,^.*$")), "Included MATCH did not participate in DNS priority");
            M(set, "mappings")["DIRECT"] = "old-group";
            var renamed = ProjectReferences.RewritePolicy(policy, x => x == "old-group" ? "new-group" : x);
            Check(S(M(Map(L(renamed, "rule_sets")[0], "set"), "mappings"), "DIRECT") == "new-group", "Mapped group references not renamed");
            var update = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("DOMAIN,same.example,REJECT"), "generic");
            Check(ImportedRuleSets.Diff(set, update).Contains("新增 1 条，删除 3 条"), "Update diff omitted action changes");
        });
        await test("URL import reads without persisting a full subscription or using stale cache", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "rules: ['DOMAIN,one.example,DIRECT']\nproxies: [{password: SECRET}]" };
            using var fetch = new Fetcher(f.Project, O("trust_env", false), useCache: false);
            var read = await fetch.ReadAsync(O("url", server.Url), bytes => ImportedRuleSets.Parse(bytes, "URL", "url", "localhost"), "rules");
            Check(!Canonical(read.Value).Contains("SECRET") && !Directory.Exists(Path.Combine(f.Project, ".cache")), "Import persisted subscription contents");
        });
    }
}

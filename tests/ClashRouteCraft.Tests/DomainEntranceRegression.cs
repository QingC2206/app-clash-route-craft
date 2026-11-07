using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class DomainEntranceRegression
{
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static Task Sync(Action action) { action(); return Task.CompletedTask; }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Domain audit skips malformed hostnames, never repairs them or damages regex", () => Sync(() =>
        {
            var audit = new DomainRuleAudit();
            string[] bad = ["DOMAIN-SUFFIX,smartdnsBitz Net.com", "DOMAIN,bad\u200b.example", "DOMAIN,https://bad.example/path", "DOMAIN-WILDCARD,*.bad name.example", "DOMAIN,,DIRECT", "AND,((DOMAIN,bad name.example),(NETWORK,TCP))"];
            foreach (string raw in bad) Check(audit.Check(() => Rules.WithAction(JsonValue.Create(raw), "DIRECT", inputFormat: "match"), "手工", "proxy[1]", raw) is null, "Bad domain retained: " + raw);
            foreach (string raw in new[] { @"DOMAIN-REGEX,^api[0-9]{1,3}\.example$", "PROCESS-PATH,C:\\Program Files\\App.exe", "DOMAIN-KEYWORD,contains space", "DOMAIN,valid.example" })
                Check(audit.Check(() => Rules.WithAction(JsonValue.Create(raw), "DIRECT", inputFormat: "match"), "手工", "proxy[2]", raw) is not null, "Valid non-domain condition rejected");
            Check(audit.Skipped.Count == bad.Length && audit.Skipped.OfType<JsonObject>().All(x => DomainRuleAudit.Message(x).Contains("未自动修正")), "Missing explicit warnings");
        }));
        await test("Text and YAML audits preserve original locations and valid wildcard semantics", () => Sync(() =>
        {
            var audit = new DomainRuleAudit();
            var rules = Rules.ParseRemote(Encoding.UTF8.GetBytes("# comment\n\n+.good.example\n*.bad name.example\n*.good.example"), O(), audit, "List A");
            Check(rules.Count == 2 && S(Map(audit.Skipped[0], "warning"), "position") == "第 4 行", "Text position shifted");
            var yaml = Rules.ParseRemote(Encoding.UTF8.GetBytes("payload: ['DOMAIN,good.example', 'DOMAIN,bad name.example']"), O(), audit, "List B");
            Check(yaml.Count == 1 && S(Map(audit.Skipped[1], "warning"), "position") == "payload[2]", "YAML position shifted");
            Check(S(rules[1]) == @"DOMAIN-REGEX,(?i)^[^.]+\.good\.example$,DIRECT", "Wildcard changed");
            int count = audit.Skipped.Count;
            try { Rules.ParseRemote(Encoding.UTF8.GetBytes("DOMAIN,bad name.example\nUNKNOWN,value"), O(), audit); throw new Exception("Structural errors accepted"); } catch (BuildException) { }
            Check(count == audit.Skipped.Count, "Failed parse leaked warnings into fallback result");
        }));
        await test("Imported rules skip invalid domains and expose warning with original position", () => Sync(() =>
        {
            var imported = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("# comment\nDOMAIN-SUFFIX,bad name.example,DIRECT\nDOMAIN,good.example,DIRECT"), "Portable");
            Check(L(imported, "rules").Count == 1 && L(imported, "domain_warnings").Count == 1, "Import failed to skip");
            Check(S(Map(L(imported, "domain_warnings")[0], "w"), "position") == "第 2 行", "Import line number lost");
        }));
        await test("Editable presets expose skipped domains instead of hiding the whole catalogue", () => Sync(() =>
        {
            var presets = RulePresets.Parse(O("version", 1, "presets", A(new JsonNode?[] { O("id", "generic", "name", "Generic", "domains", A("bad name.example", "good.example"), "rules", A("DOMAIN,https://bad.example/path")) })));
            Check(presets.Single().Domains.SequenceEqual(["good.example"]) && presets.Single().DomainWarnings?.Count == 2, "Preset did not share domain validation");
            var result = RulePresets.Append(O("direct", A("existing bad.example")), presets, "direct");
            Check(result.RulesAdded == 1 && result.DomainWarnings.Count == 2 && L(result.Policy, "direct").Count == 2, "Append lost diagnostics or rewrote old rules");
        }));
        foreach (bool baseline in new[] { false, true })
        await test("Every compiled rule source is audited without changing project or snapshot: " + baseline, async () =>
        {
            using var f = new Fixture();
            var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
            var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            doc["rules"] = A("DOMAIN-SUFFIX,smartdnsBitz Net.com,DIRECT", "DOMAIN,base.example,DIRECT", "SUB-RULE,(NETWORK,TCP),children", "RULE-SET,inline,DIRECT", "MATCH,DIRECT");
            doc["sub-rules"] = O("children", A("DOMAIN,child bad.example,DIRECT", "DOMAIN,child.example,DIRECT"));
            doc["rule-providers"] = O("inline", O("type", "inline", "behavior", "domain", "payload", A("+.bad name.example", "+.provider.example")));
            f.Write("subscrip/A.yaml", doc);
            var imported = O("id", "test", "name", "Imported", "enabled", true, "rules", A(new JsonNode?[] { O("match", "DOMAIN,import bad.example", "source_action", "DIRECT") }), "mappings", O("DIRECT", "DIRECT"));
            f.Write("list.txt", JsonValue.Create("unused")!); File.WriteAllText(Path.Combine(f.Project, "list.txt"), "DOMAIN,url bad.example\nDOMAIN,url.example\n");
            f.Write("policy.yaml", O("rule_source", baseline ? "subscription" : "custom", "direct", A("bad name.example", "safe.example"), "proxy", A("DOMAIN,proxy bad.example"),
                "rule_sets", A(new JsonNode?[] { imported }), "remote_rules", A(new JsonNode?[] { O("name", "Local", "file", "list.txt", "action", "DIRECT") }), "domestic_direct", false));
            var before = Directory.GetFiles(f.Project, "*", SearchOption.AllDirectories).ToDictionary(x => x, DependencySnapshot.FileHash);
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            string output = File.ReadAllText(result.OutputPath);
            Check(!output.Contains("bad name") && !output.Contains("Bitz Net") && !output.Contains(" bad.example") && output.Contains("safe.example"), "Invalid rule leaked");
            Check(L(result.Report, "invalid_domain_rules").Count == (baseline ? 7 : 4), "Incomplete source coverage: " + Canonical(result.Report["invalid_domain_rules"]));
            foreach (var file in before) Check(file.Value == DependencySnapshot.FileHash(file.Key), "Input rewritten: " + Path.GetFileName(file.Key));
            Check(L(result.Report, "fetches").OfType<JsonObject>().Where(x => S(x, "status") == "local").All(x => S(x, "sha256").Length == 64), "Local read lacks SHA-256");
            if (baseline && Environment.GetEnvironmentVariable("CRC_TEST_MIHOMO") is { Length: > 0 } core)
                await CoreValidation.ValidateAsync(Map(YamlData.Read(result.OutputPath), "output"), core, "", Path.Combine(f.Root, "check.log"), default, []);
        });
        await test("HTTP provider sanitization pins output while preserving raw response cache", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "payload: ['+.bad name.example', '+.safe.example']" };
            var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            doc["rules"] = A("RULE-SET,domains,DIRECT", "MATCH,DIRECT");
            doc["rule-providers"] = O("domains", O("type", "http", "behavior", "domain", "url", server.Url, "path", "./rules/list.yaml")); f.Write("subscrip/A.yaml", doc);
            var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
            f.Write("policy.yaml", O("rule_source", "subscription"));
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(AutoFetch: false));
            var provider = M(M(Map(YamlData.Read(result.OutputPath), "output"), "rule-providers"), "domains");
            Check(S(provider, "type") == "inline" && !provider.ContainsKey("url") && Canonical(provider["payload"]) == Canonical(A("DOMAIN-SUFFIX,safe.example")), "Router can re-download invalid rules");
            using var fetch = new Fetcher(f.Project, Merge(M(settings, "http"), O("cache_dir", Path.Combine(f.Root, ".cache", "SampleOne"))));
            Check(File.ReadAllText(fetch.CachedPath(O("url", server.Url))) == server.Body, "Raw cached response modified");
            var check = await RemoteRuleValidation.CheckAsync(f.Project, O("url", server.Url), M(settings, "http"));
            Check(check.Success && check.RuleCount == 1 && check.SkippedCount == 1 && check.Message.Contains("payload[1]"), "URL warning lost");
        });
        await test("Sanitized classical provider has no false ASN dependency; unreadable provider preserves output", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "payload: ['AND,((DOMAIN,bad name.example),(IP-ASN,64512))', 'DOMAIN,safe.example']" };
            var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            doc["rules"] = A("RULE-SET,text,DIRECT", "MATCH,DIRECT");
            doc["rule-providers"] = O("text", O("type", "http", "behavior", "classical", "url", server.Url, "path", "./rules/text.yaml")); f.Write("subscrip/A.yaml", doc);
            f.Write("policy.yaml", O("rule_source", "subscription"));
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(AutoFetch: false));
            Check(S(M(result.Report, "asn"), "status") == "no_asn_dependency", "Invalid logical rule left an ASN dependency");
            string before = DependencySnapshot.FileHash(result.OutputPath);
            Map(M(doc, "rule-providers")["text"], "provider")["url"] = server.Url + "missing"; server.Status = 503; f.Write("subscrip/A.yaml", doc);
            try { await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(AutoFetch: false)); throw new Exception("Unreadable provider silently omitted"); } catch (BuildException) { }
            Check(DependencySnapshot.FileHash(result.OutputPath) == before, "Failure replaced output");
        });
        await test("Entrance presets cover names and flags without substring country collisions", () => Sync(() =>
        {
            string[] names = ["香港", "澳门", "🇭🇰 HK01", "Macao", "日本", "Japan JP01", "台灣", "🇸🇬 SG01", "韩国", "Korea KR01", "美国", "US01", "🇺🇸", "AUS", "RUS", "Japan via Hong Kong"];
            var nodes = A(names.Select((name, i) => (JsonNode?)O("name", name, "type", "http", "server", "127.0.0.1", "port", 8080 + i)));
            var source = O("name", "Airport", "include", "香港");
            var nearby = SubscriptionBaseline.Eligible(nodes, source, O(EntranceFilters.Key, "nearby"));
            var noHk = SubscriptionBaseline.Eligible(nodes, source, O(EntranceFilters.Key, "nearby-no-hk-mo"));
            var us = SubscriptionBaseline.Eligible(nodes, source, O(EntranceFilters.Key, "us"));
            Check(nearby.Contains("澳门") && nearby.Contains("韩国") && !nearby.Contains("美国"), "Nearby list incomplete");
            Check(noHk.Count == 6 && !noHk.Contains("Japan via Hong Kong"), "HK/MO exclusion bypassed");
            Check(us.SequenceEqual(["美国", "US01", "🇺🇸"]), "US matched unrelated names");
            Check(SubscriptionBaseline.Eligible(nodes, source, O()).SequenceEqual(["香港"]), "Legacy include lost");
            foreach (string mode in new[] { "nearby", "nearby-no-hk-mo", "us" })
                Check(Nodes.Eligible(Nodes.Namespace(nodes, "Airport"), source, O(EntranceFilters.Key, mode)).Select(x => x[10..]).SequenceEqual(SubscriptionBaseline.Eligible(nodes, source, O(EntranceFilters.Key, mode))), "Compiler modes disagree");
        }));
        await test("US preset changes all airport pools and preserves old include exclusions", async () =>
        {
            using var f = new Fixture(); var settings = Map(YamlData.Read(f.Settings), "s"); settings["target"] = "clash"; M(settings, "selection")[EntranceFilters.Key] = "us"; f.Write("settings.yaml", settings);
            var doc = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "s"); doc["rules"] = A("MATCH,DIRECT"); f.Write("subscrip/A.yaml", doc);
            f.Write("policy.yaml", O("rule_source", "subscription"));
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var groups = L(Map(YamlData.Read(built.OutputPath), "config"), "proxy-groups").OfType<JsonObject>().ToArray();
            foreach (string name in new[] { "聚合组", "A", "B" }) Check(L(groups.Single(x => S(x, "name") == name), "proxies").All(x => S(x).Contains("美国")), "Non-US candidate in pool");
            Check(S(M(Map(YamlData.Read(f.Settings), "s"), "selection"), "include") == "香港", "Existing include overwritten");
            M(settings, "selection")["exclude"] = "美国"; f.Write("settings.yaml", settings);
            try { await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); throw new Exception("No candidates silently fell back"); }
            catch (BuildException ex) { Check(ex.Message.Contains("没有符合筛选条件"), "Wrong empty-pool error"); }
        });
    }
}

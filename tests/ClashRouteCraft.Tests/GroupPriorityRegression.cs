using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class GroupPriorityRegression
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static readonly string[][] Orders = [
        ["direct", "reject", "proxy"], ["direct", "proxy", "reject"],
        ["reject", "direct", "proxy"], ["reject", "proxy", "direct"],
        ["proxy", "direct", "reject"], ["proxy", "reject", "direct"]];

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (bool baseline in new[] { true, false })
            foreach (var order in Orders)
                await test($"Four-layer persona precedence governs all sources ({(baseline ? "subscription" : "custom")}: {string.Join('/', order)})", async () =>
                {
                    using var f = new Fixture();
                    var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
                    using var server = new LocalServer { Body = "DOMAIN,priority-vs-url.example\nDOMAIN,url-vs-manual.example\nDOMAIN,url-cross.example\nDOMAIN-REGEX,^api[0-9]{1,3}\\.example$\n" };
                    var primary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "primary");
                    primary["proxy-groups"] = new JsonArray(O("name", "SourceProxy", "type", "select", "proxies", A("香港同名")));
                    primary["rules"] = A("DOMAIN,user-vs-sub.example,REJECT", "DOMAIN,continue.example,PASS",
                        "AND,((DOMAIN,continue.example),(NETWORK,UDP)),SourceProxy", "DOMAIN,continue.example,DIRECT", "MATCH,SourceProxy");
                    f.Write("subscrip/A.yaml", primary);
                    var secondary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/B.yaml")), "secondary");
                    secondary["rules"] = A("DOMAIN,secondary-only.example,REJECT", "MATCH,REJECT"); f.Write("subscrip/B.yaml", secondary);
                    var imported = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("DOMAIN,imported.example,REJECT-DROP\nDOMAIN,imported.example,PROXY\nDOMAIN,imported.example,DIRECT\nDOMAIN,manual-vs-imported.example,DIRECT"), "Mixed");
                    M(imported, "mappings")["PROXY"] = "PROXY";
                    var disabled = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("DOMAIN,disabled.example,DIRECT"), "Disabled"); disabled["enabled"] = false;
                    var urlOrder = order.Reverse().ToArray();
                    var policy = O("rule_source", baseline ? "subscription" : "custom", "extra_rules_enabled", true,
                        "url_order", A(urlOrder), "order", A(order), "domestic_direct", false,
                        "first", new JsonArray(
                            O("match", "DOMAIN,priority-vs-url.example", "action", "PROXY"),
                            O("match", "DOMAIN,priority-order.example", "action", "REJECT"),
                            O("match", "DOMAIN,priority-order.example", "action", "DIRECT"),
                            JsonValue.Create("DOMAIN,priority-string.example,DIRECT"),
                            O("match", "DOMAIN,pass.example", "action", "PASS")),
                        "direct", A("DOMAIN,shared.example", "DOMAIN,user-vs-sub.example", "DOMAIN,pass.example", "DOMAIN,url-vs-manual.example"),
                        "reject", A("DOMAIN,shared.example", "DOMAIN,priority-vs-url.example", "DOMAIN,manual-vs-imported.example", "DOMAIN-SUFFIX,example.org"),
                        "proxy", A("DOMAIN,shared.example", "DOMAIN,specific.example.org"),
                        "remote_rules", new JsonArray(
                            O("name", "direct-url", "url", server.Url + "d", "action", "DIRECT"),
                            O("name", "reject-url", "url", server.Url + "r", "action", "REJECT"),
                            O("name", "proxy-url", "url", server.Url + "p", "action", "PROXY")),
                        "rule_sets", new JsonArray(imported, disabled),
                        "supplemental_rules", new JsonArray(O("match", "DOMAIN,supplemental.example", "action", "DIRECT"), O("match", "DOMAIN,supplemental.example", "action", "PROXY")));
                    f.Write("policy.yaml", policy);
                    string[] inputs = ["policy.yaml", "settings.yaml", "residential.yaml", "subscrip/A.yaml", "subscrip/B.yaml"];
                    var before = inputs.ToDictionary(x => x, x => File.ReadAllBytes(Path.Combine(f.Project, x)));
                    var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(AutoFetch: false));
                    var output = Map(YamlData.Read(built.OutputPath), "output");
                    var rules = L(output, "rules").Select(x => S(x)).ToArray();
                    string proxy = baseline ? "代理出口" : "默认代理";
                    string Expected(string group) => group switch { "direct" => "DIRECT", "reject" => "REJECT", _ => proxy };
                    string First(string domain) => rules.First(x => x.StartsWith("DOMAIN," + domain + ","));

                    // Layer 1 (priority rules) beats Layer 2 (URL) and Layer 3 (manual) regardless of group order, and preserves exact line order.
                    Check(First("priority-vs-url.example") == "DOMAIN,priority-vs-url.example," + proxy, "Layer 1 priority rule did not beat URL and manual rules");
                    Check(First("priority-order.example") == "DOMAIN,priority-order.example,REJECT", "Layer 1 did not preserve exact line order across actions");
                    Check(Array.IndexOf(rules, "DOMAIN,priority-order.example,REJECT") < Array.IndexOf(rules, "DOMAIN,priority-order.example,DIRECT"), "Layer 1 internal order changed");
                    Check(Array.IndexOf(rules, "DOMAIN,priority-string.example,DIRECT") < Array.IndexOf(rules, "DOMAIN,url-cross.example," + Expected(urlOrder[0])), "Layer 1 string rule did not precede Layer 2 URL rules");
                    Check(Array.IndexOf(rules, "DOMAIN,pass.example,PASS") < Array.IndexOf(rules, "DOMAIN,pass.example,DIRECT"), "PASS continuation was dropped");

                    // Layer 2 (URL rules) beats Layer 3 (manual/imported) and follows url_order independently of manual order.
                    Check(First("url-cross.example") == "DOMAIN,url-cross.example," + Expected(urlOrder[0]), "Layer 2 URL conflicts did not follow url_order");
                    Check(First("url-vs-manual.example") == "DOMAIN,url-vs-manual.example," + Expected(urlOrder[0]), "Layer 2 URL did not beat Layer 3 manual rule");
                    Check(rules.Contains(@"DOMAIN-REGEX,^api[0-9]{1,3}\.example$,DIRECT"), "Regex quantifier was changed");

                    // Layer 3 (manual groups follow order, followed by imported rule sets preserving internal order) beats Layer 4 (subscription).
                    Check(First("shared.example") == "DOMAIN,shared.example," + Expected(order[0]), "Layer 3 manual conflict winner did not follow policy.order");
                    Check(rules.Count(x => x.StartsWith("DOMAIN,shared.example,")) == 3, "Conflicting manual actions were removed");
                    Check((Array.IndexOf(rules, "DOMAIN,specific.example.org," + proxy) < Array.IndexOf(rules, "DOMAIN-SUFFIX,example.org,REJECT"))
                        == (Array.IndexOf(order, "proxy") < Array.IndexOf(order, "reject")), "Manual exact matches escaped Layer 3 group order");
                    Check(First("manual-vs-imported.example") == "DOMAIN,manual-vs-imported.example,REJECT", "Manual rule did not precede imported rule set in Layer 3");
                    Check(First("imported.example") == "DOMAIN,imported.example,REJECT-DROP", "Imported rule set did not preserve internal order");
                    Check(First("user-vs-sub.example") == "DOMAIN,user-vs-sub.example,DIRECT", "Subscription overrode a Layer 3 custom rule");
                    Check(!rules.Any(x => x.Contains("disabled.example") || x.Contains("secondary-only.example")), "Disabled or secondary rules leaked into the output");

                    // Verify strict layer boundaries in report and output.
                    var merge = M(built.Report, "rule_merge");
                    int pCount = I(merge, "priority_rule_count", -1), uCount = I(merge, "url_rule_count", -1), mCount = I(merge, "manual_rule_count", -1);
                    Check(pCount == 5 && uCount == 12 && mCount > 0, $"Unexpected layer counts: p={pCount}, u={uCount}, m={mCount}");
                    Check(Canonical(merge["url_order"]) == Canonical(A(urlOrder)) && Canonical(merge["group_order"]) == Canonical(A(order)), "Report does not describe effective url_order and group_order");
                    if (baseline)
                    {
                        int boundary = I(built.Report, "extra_rule_count", -1);
                        Check(boundary == pCount + uCount + mCount, "extra_rule_count does not equal sum of layers 1-3");
                        Check(rules.Skip(boundary).SequenceEqual(["DOMAIN,user-vs-sub.example,REJECT", "DOMAIN,continue.example,PASS",
                            "AND,((DOMAIN,continue.example),(NETWORK,UDP)),代理出口", "DOMAIN,continue.example,DIRECT", "MATCH,代理出口"]), "Subscription order or continuation changed");
                    }
                    Check(inputs.All(x => before[x].SequenceEqual(File.ReadAllBytes(Path.Combine(f.Project, x)))), "Generation rewrote project inputs");
                    Check(server.Requests == 3, "Each URL source should be loaded once");
                });

        await test("PriorityRuleEditor roundtrips full rules, flags, regex and incomplete drafts safely", () =>
        {
            var initial = new JsonArray(
                O("match", "DOMAIN-SUFFIX,todesk.com", "action", "DIRECT"),
                O("match", "GEOIP,LAN,no-resolve", "action", "DIRECT"),
                O("match", "AND,((NETWORK,UDP),(DST-PORT,443))", "action", "REJECT"),
                O("match", @"DOMAIN-REGEX,^foo,bar$|^blocked\.example$", "action", "REJECT"),
                O("match", "DOMAIN,pass.example", "action", "PASS"));
            var editor = new PriorityRuleEditor(initial);
            Check(editor.Text.Split('\n').SequenceEqual([
                "DOMAIN-SUFFIX,todesk.com,DIRECT",
                "GEOIP,LAN,DIRECT,no-resolve",
                "AND,((NETWORK,UDP),(DST-PORT,443)),REJECT",
                @"DOMAIN-REGEX,^foo,bar$|^blocked\.example$,REJECT",
                "DOMAIN,pass.example,PASS"]), "PriorityRuleEditor display mismatch: " + editor.Text);
            editor.Replace("DOMAIN-SUFFIX,claude.ai,PROXY\nGEOIP,LAN,DIRECT,no-resolve\nDOMAIN-SUFFIX,incomplete");
            Check(editor.Count == 3 && S(Map(editor.Rules[0], "r"), "action") == "PROXY"
                && S(Map(editor.Rules[1], "r"), "match") == "GEOIP,LAN,no-resolve"
                && S(editor.Rules[2]) == "DOMAIN-SUFFIX,incomplete", "PriorityRuleEditor edit or draft preservation failed");
            bool rejected = false;
            try { PriorityRuleEditor.ParseEntry(editor.Rules[2]); } catch (BuildException) { rejected = true; }
            Check(rejected, "Incomplete priority rule without action was accepted");
            bool matchRejected = false;
            try { PriorityRuleEditor.ParseRuleLine("MATCH,DIRECT"); } catch (BuildException ex) { matchRejected = ex.Message.Contains("MATCH"); }
            Check(matchRejected, "MATCH rule in priority layer was accepted");
            return Task.CompletedTask;
        });

        await test("RuleDefaults preserves priority rules and independent url_order", () =>
        {
            using var f = new Fixture();
            var policy = O("rule_source", "subscription", "extra_rules_enabled", true,
                "first", new JsonArray(O("match", "DOMAIN-SUFFIX,todesk.com", "action", "DIRECT")),
                "url_order", A("proxy", "direct", "reject"),
                "order", A("direct", "reject", "proxy"));
            RuleDefaults.Save(f.Root, policy);
            var read = RuleDefaults.Read(f.Root);
            Check(S(Map(L(read, "first")[0], "f"), "match") == "DOMAIN-SUFFIX,todesk.com"
                && ProjectOrdering.UrlOrder(read).SequenceEqual(["proxy", "direct", "reject"])
                && ProjectOrdering.RuleOrder(read).SequenceEqual(["direct", "reject", "proxy"]), "RuleDefaults lost first or url_order");
            return Task.CompletedTask;
        });

        await test("Changing group ordering version invalidates an existing generated output", async () =>
        {
            using var f = new Fixture(); var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            built.Report["project_ordering_version"] = 3;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(built.OutputPath)!, "HomeChain.report.json"), built.Report.ToJsonString(JsonOptions));
            Check(!new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent(), "Old v3 whole-group output still appears current");
        });
    }
}

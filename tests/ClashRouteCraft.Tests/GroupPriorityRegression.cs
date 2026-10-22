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
                await test($"Whole custom groups govern all sources ({(baseline ? "subscription" : "custom")}: {string.Join('/', order)})", async () =>
                {
                    using var f = new Fixture();
                    var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
                    using var server = new LocalServer { Body = "DOMAIN,cross.example\nDOMAIN,url-only.example\nDOMAIN-REGEX,^api[0-9]{1,3}\\.example$\n" };
                    var primary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "primary");
                    primary["proxy-groups"] = new JsonArray(O("name", "SourceProxy", "type", "select", "proxies", A("香港同名")));
                    primary["rules"] = A("DOMAIN,user-vs-sub.example,REJECT", "DOMAIN,continue.example,PASS",
                        "AND,((DOMAIN,continue.example),(NETWORK,UDP)),SourceProxy", "DOMAIN,continue.example,DIRECT", "MATCH,SourceProxy");
                    f.Write("subscrip/A.yaml", primary);
                    var secondary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/B.yaml")), "secondary");
                    secondary["rules"] = A("DOMAIN,secondary-only.example,REJECT", "MATCH,REJECT"); f.Write("subscrip/B.yaml", secondary);
                    var imported = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("DOMAIN,imported.example,REJECT-DROP\nDOMAIN,imported.example,PROXY\nDOMAIN,imported.example,DIRECT\nDOMAIN,cross.example,DIRECT"), "Mixed");
                    M(imported, "mappings")["PROXY"] = "PROXY";
                    var disabled = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes("DOMAIN,disabled.example,DIRECT"), "Disabled"); disabled["enabled"] = false;
                    var policy = O("rule_source", baseline ? "subscription" : "custom", "extra_rules_enabled", true,
                        "order", A(order), "domestic_direct", false,
                        "first", new JsonArray(O("match", "DOMAIN,cross.example", "action", "PROXY"),
                            O("match", "DOMAIN,legacy-one.example", "action", "PROXY"), O("match", "DOMAIN,legacy-two.example", "action", "PROXY"),
                            O("match", "DOMAIN,pass.example", "action", "PASS")),
                        "direct", A("DOMAIN,shared.example", "DOMAIN,user-vs-sub.example", "DOMAIN,pass.example"),
                        "reject", A("DOMAIN,shared.example", "DOMAIN,cross.example", "DOMAIN-SUFFIX,example.org"),
                        "proxy", A("DOMAIN,shared.example", "DOMAIN,specific.example.org"),
                        "remote_rules", new JsonArray(O("name", "direct-url", "url", server.Url, "action", "DIRECT")),
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
                    Check(First("cross.example") == "DOMAIN,cross.example," + Expected(order[0]), "URL, first and manual conflicts did not follow whole-group order");
                    Check(First("shared.example") == "DOMAIN,shared.example," + Expected(order[0]), "Manual conflict winner changed");
                    Check(rules.Count(x => x.StartsWith("DOMAIN,shared.example,")) == 3, "Conflicting actions were removed");
                    string importedAction = order[0] == "reject" ? "REJECT-DROP" : Expected(order[0]);
                    Check(First("imported.example") == "DOMAIN,imported.example," + importedAction, "Mixed imported actions did not join their groups");
                    Check(rules.Contains(@"DOMAIN-REGEX,^api[0-9]{1,3}\.example$,DIRECT"), "Regex quantifier was changed");
                    Check(!rules.Any(x => x.Contains("disabled.example") || x.Contains("secondary-only.example")), "Disabled or secondary rules leaked into the output");
                    Check(First("user-vs-sub.example") == "DOMAIN,user-vs-sub.example,DIRECT", "Subscription overrode a custom action");
                    Check(Array.IndexOf(rules, "DOMAIN,pass.example,PASS") < Array.IndexOf(rules, "DOMAIN,pass.example,DIRECT"), "PASS continuation was dropped");
                    Check(Array.IndexOf(rules, "DOMAIN,legacy-one.example," + proxy) < Array.IndexOf(rules, "DOMAIN,legacy-two.example," + proxy), "Within-group order changed");
                    Check((Array.IndexOf(rules, "DOMAIN,specific.example.org," + proxy) < Array.IndexOf(rules, "DOMAIN-SUFFIX,example.org,REJECT"))
                        == (Array.IndexOf(order, "proxy") < Array.IndexOf(order, "reject")), "Exact matches were promoted outside the group priority");
                    int boundary = baseline ? I(built.Report, "extra_rule_count", -1) : rules.Length - 1;
                    int previous = -1;
                    foreach (string rule in rules.Take(boundary))
                    {
                        var parts = Rules.SplitFull(rule); string action = parts[2]; if (action is "PASS" or "PASS-RULE") continue;
                        int rank = Array.IndexOf(order, action == "DIRECT" ? "direct" : action is "REJECT" or "REJECT-DROP" ? "reject" : "proxy");
                        Check(rank >= previous, "An earlier group resumed after a later group started"); previous = rank;
                    }
                    if (baseline)
                        Check(rules.Skip(boundary).SequenceEqual(["DOMAIN,user-vs-sub.example,REJECT", "DOMAIN,continue.example,PASS",
                            "AND,((DOMAIN,continue.example),(NETWORK,UDP)),代理出口", "DOMAIN,continue.example,DIRECT", "MATCH,代理出口"]), "Subscription order or continuation changed");
                    Check(Canonical(M(built.Report, "rule_merge")["group_order"]) == Canonical(A(order)), "Report does not describe the effective group order");
                    Check(inputs.All(x => before[x].SequenceEqual(File.ReadAllBytes(Path.Combine(f.Project, x)))), "Generation rewrote project inputs");
                    Check(server.Requests == 1, "A URL list was loaded more than once while grouping");
                });

        await test("Changing group ordering version invalidates an existing generated output", async () =>
        {
            using var f = new Fixture(); var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            built.Report["project_ordering_version"] = 2;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(built.OutputPath)!, "HomeChain.report.json"), built.Report.ToJsonString(JsonOptions));
            Check(!new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent(), "Old stage-based output still appears current");
        });
    }
}

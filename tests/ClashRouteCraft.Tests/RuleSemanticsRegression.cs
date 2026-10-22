using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class RuleSemanticsRegression
{
    static void Check(bool value, string message = "Rule semantics regression") { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Regex commas, classes, escapes and quoted literals survive action replacement", () =>
        {
            string[] patterns = [@"^api[0-9]{1,3}\.example$", @"^(foo|bar)[a-z]{1,}\.example$", @"^api[,)]?\.example$",
                @"^[[:digit:],]{1,3}\.example$", @"^\(api\){1,3}\.example$", @"^\Q(api,{1,3})\E\.example$"];
            foreach (string type in new[] { "DOMAIN-REGEX", "PROCESS-NAME-REGEX", "PROCESS-PATH-REGEX" })
                foreach (string pattern in patterns)
                {
                    string raw = type + "," + pattern, expected = raw + ",REJECT";
                    Check(Rules.WithAction(JsonValue.Create(raw), "REJECT") == expected, raw);
                    Check(Rules.Canonicalize(expected).Value == pattern && Rules.Canonicalize(expected).Text == expected, expected);
                    Check(Rules.WithAction(O("rule", raw + ",OLD"), "DIRECT") == raw + ",DIRECT", raw);
                }
            string path = @"PROCESS-PATH,C:\Program Files (x86)\Example\app.exe";
            Check(Rules.WithAction(JsonValue.Create(path), "REJECT") == path + ",REJECT");
            return Task.CompletedTask;
        });
        await test("Logic rules preserve regex punctuation and surrounding condition boundaries", () =>
        {
            foreach (string pattern in new[] { @"^api[0-9]{1,3}\.example$", @"^(api|cdn)[,)]?\.example$", @"^\(api\)\.example$", @"^foo,bar$|^blocked\.example$" })
            {
                string raw = "AND,((DOMAIN-REGEX," + pattern + "),(NETWORK,TCP))";
                string rule = Rules.WithAction(JsonValue.Create(raw), "REJECT");
                Check(rule == raw + ",REJECT"); Rules.ValidateSyntax(rule);
            }
            return Task.CompletedTask;
        });
        await test("Remote regex lists preserve payloads through both parse and action override", () =>
        {
            string raw = @"DOMAIN-REGEX,^api[0-9]{1,3}\.example$";
            foreach (string content in new[] { raw, YamlData.Write(O("rules", A(raw + ",OLD"))), YamlData.Write(O("payload", A(raw))) })
            {
                string parsed = S(Rules.ParseRemote(Encoding.UTF8.GetBytes(content), O())[0]);
                Check(parsed == raw + ",DIRECT");
                Check(Rules.WithAction(JsonValue.Create(parsed), "REJECT", inputFormat: "rule") == raw + ",REJECT");
            }
            return Task.CompletedTask;
        });
        await test("Same-condition PASS and UDP fallback actions remain ordered with exact duplicates removed", () =>
        {
            foreach (string action in new[] { "PASS", "TCP_ONLY", "代理组" })
            {
                string[] input = ["DOMAIN,test.example," + action, "DOMAIN,test.example,REJECT", "DOMAIN,test.example," + action, "DOMAIN,test.example,DIRECT"];
                var (rules, report) = Rules.MergeRules(A(input.Select((rule, i) => (JsonNode?)O("rule", rule, "source", "test-" + i, "stage", "manual"))));
                Check(rules.Select(x => S(x)).SequenceEqual(action == "PASS" ? [input[0], input[1], input[3]] : input));
                Check(I(report, "duplicates_removed", 0) == (action == "PASS" ? 1 : 0) && I(report, "conflicts_preserved", 0) == 2 && I(report, "conflicts_resolved", 0) == 0);
            }
            return Task.CompletedTask;
        });
        await test("Project generation preserves manual PASS exceptions and remote regex rules", async () =>
        {
            using var f = new Fixture(); var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
            string regex = @"DOMAIN-REGEX,^api[0-9]{1,3}\.example$";
            f.Write("regex.list", O("payload", A(regex)));
            policy["first"] = A(new JsonNode?[] { O("match", "DOMAIN,test.example", "action", "PASS") });
            policy["reject"] = A("DOMAIN,test.example");
            policy["remote_rules"] = A(new JsonNode?[] { O("name", "regex", "file", "regex.list", "action", "REJECT") });
            f.Write("policy.yaml", policy);
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var rules = L(Map(YamlData.Read(built.OutputPath), "output"), "rules").Select(x => S(x)).ToArray();
            Check(Array.IndexOf(rules, "DOMAIN,test.example,PASS") < Array.IndexOf(rules, "DOMAIN,test.example,REJECT"));
            Check(rules.Contains(regex + ",REJECT") && I(built.Report, "rule_compiler_version", 0) == Rules.Version);
        });
        await test("Pre-fix output reports require regeneration before being considered current", async () =>
        {
            using var f = new Fixture(); var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var session = new WorkspaceSession(f.Root, "SampleOne"); Check(session.OutputIsCurrent());
            built.Report.Remove("rule_compiler_version"); File.WriteAllText(Path.Combine(session.OutputPath, "HomeChain.report.json"), built.Report.ToJsonString(JsonOptions));
            Check(session.OutputStatus() == OutputFreshness.InputsChanged);
            built.Report["rule_compiler_version"] = Rules.Version - 1; File.WriteAllText(Path.Combine(session.OutputPath, "HomeChain.report.json"), built.Report.ToJsonString(JsonOptions));
            Check(session.OutputStatus() == OutputFreshness.InputsChanged);
        });
        await test("Literal regex commas use explicit boundaries and ambiguous legacy input fails safely", () =>
        {
            foreach (string type in new[] { "DOMAIN-REGEX", "PROCESS-NAME-REGEX", "PROCESS-PATH-REGEX" })
                foreach (string pattern in new[] { @"^foo,bar$|^blocked\.example$", @"^foo,bar,baz$", @"^foo,DIRECT", @"^foo,bar\(x\)$", @"^foo,bar{1,3}$", @"^foo,bar[,)]$" })
                {
                    string raw = type + "," + pattern;
                    Check(Rules.WithAction(O("match", raw), "REJECT") == raw + ",REJECT");
                    Check(Rules.WithAction(O("rule", raw + ",Old Group"), "REJECT") == raw + ",REJECT");
                    Check(Rules.Canonicalize(raw + ",REJECT").Value == pattern);
                    bool rejected = false; try { Rules.WithAction(JsonValue.Create(raw), "REJECT"); } catch (BuildException) { rejected = true; }
                    Check(rejected, "Ambiguous input was silently reinterpreted");
                }
            return Task.CompletedTask;
        });
        await test("Remote match and full formats preserve comma payloads through action replacement", () =>
        {
            string raw = @"DOMAIN-REGEX,^foo,bar$|^blocked\.example$";
            foreach (var (content, spec) in new[] { (raw, O("rule_format", "match")), (raw + ",OLD", O("rule_format", "rule")),
                (YamlData.Write(O("payload", A(raw))), O()), (YamlData.Write(O("rules", A(raw + ",OLD"))), O()) })
            {
                string parsed = S(Rules.ParseRemote(Encoding.UTF8.GetBytes(content), spec)[0]);
                Check(parsed == raw + ",DIRECT");
                Check(Rules.WithAction(JsonValue.Create(parsed), "REJECT", inputFormat: "rule") == raw + ",REJECT");
            }
            return Task.CompletedTask;
        });
        await test("DNS-sensitive duplicates and dynamic actions remain even when adjacent", () =>
        {
            foreach (string raw in new[] { "IP-CIDR,127.0.0.0/8,REJECT,no-resolve", "GEOIP,CN,REJECT,no-resolve",
                "OR,((IP-CIDR,127.0.0.0/8,no-resolve),(IP-CIDR,192.0.2.0/24)),REJECT", "DOMAIN,test.example,DynamicGroup" })
            {
                var entries = A(new[] { raw, raw }.Select(rule => (JsonNode?)O("rule", rule, "source", "test", "stage", "first")));
                var (rules, report) = Rules.MergeRules(entries);
                Check(rules.Count == 2 && I(report, "duplicates_preserved", 0) == 1 && I(report, "conflicts_preserved", 0) == 0);
            }
            return Task.CompletedTask;
        });
        await test("Domain-provider label wildcards and dot prefixes keep their original scope", () =>
        {
            string Parsed(string entry) => S(Rules.ParseRemote(Encoding.UTF8.GetBytes(entry), O("behavior", "domain"))[0]);
            Check(Parsed("*.example.com") == @"DOMAIN-REGEX,(?i)^[^.]+\.example\.com$,DIRECT");
            Check(Parsed(".example.com") == @"DOMAIN-REGEX,(?i)^(?:[^.]+\.)+example\.com$,DIRECT");
            Check(Parsed("+.example.com") == "DOMAIN-SUFFIX,example.com,DIRECT");
            Check(Parsed("a.*.example.com") == @"DOMAIN-REGEX,(?i)^a\.[^.]+\.example\.com$,DIRECT");
            Check(Parsed("+.*.example.com") == @"DOMAIN-REGEX,(?i)^(?:[^.]+\.)*[^.]+\.example\.com$,DIRECT");
            return Task.CompletedTask;
        });
        await test("IP lists resolve by default and allow explicit no-resolve", () =>
        {
            var raw = Encoding.UTF8.GetBytes("127.1.2.3/8\n2001:db8::1/32");
            Check(Rules.ParseRemote(raw, O("behavior", "ipcidr")).Select(x => S(x)).SequenceEqual(["IP-CIDR,127.0.0.0/8,DIRECT", "IP-CIDR6,2001:db8::/32,DIRECT"]));
            Check(Rules.ParseRemote(raw, O("behavior", "ipcidr", "no-resolve", true)).All(x => S(x).EndsWith(",no-resolve")));
            return Task.CompletedTask;
        });
        await test("URL-only domain lists preserve exact, suffix and wildcard scope across formats", () =>
        {
            string[] entries = ["*.wechat.com", "btrace.qq.com", "+.example.org", ".example.net", "a.*.example.com"];
            string[] expected = [@"DOMAIN-REGEX,(?i)^[^.]+\.wechat\.com$,DIRECT", "DOMAIN,btrace.qq.com,DIRECT", "DOMAIN-SUFFIX,example.org,DIRECT",
                @"DOMAIN-REGEX,(?i)^(?:[^.]+\.)+example\.net$,DIRECT", @"DOMAIN-REGEX,(?i)^a\.[^.]+\.example\.com$,DIRECT"];
            foreach (string text in new[] { "\uFEFF# header\n\n" + string.Join('\n', entries) + "\n; end\n// end",
                YamlData.Write(O("payload", A(entries))), YamlData.Write(A(entries)), O("payload", A(entries)).ToJsonString() })
                foreach (var spec in new[] { O("url", "https://example.com/download?id=1"), O("behavior", "auto") })
                    Check(Rules.ParseRemote(Encoding.UTF8.GetBytes(text), spec).Select(x => S(x)).SequenceEqual(expected));
            return Task.CompletedTask;
        });
        await test("Automatic domain detection respects explicit behavior and inspects the complete list", () =>
        {
            var raw = Encoding.UTF8.GetBytes("*.wechat.com\nbtrace.qq.com");
            Check(Rules.ParseRemote(raw, O("behavior", "classical")).Select(x => S(x)).SequenceEqual(["DOMAIN-WILDCARD,*.wechat.com,DIRECT", "DOMAIN-SUFFIX,btrace.qq.com,DIRECT"]));
            foreach (string text in new[] { "example.com\nDOMAIN,exact.example", "DOMAIN,exact.example\nexample.com", "127.0.0.1\n2001:db8::/32", "example.com\n127.0.0.0/8" })
                Check(JsonNode.DeepEquals(Rules.ParseRemote(Encoding.UTF8.GetBytes(text), O()), Rules.ParseRemote(Encoding.UTF8.GetBytes(text), O("behavior", "classical"))));
            foreach (string text in new[] { "payload: ['*.example.com', 123]", "payload: []", "# empty", "bad*.example.com", "*.example.com\nbad*.example.net" })
            {
                bool rejected = false; try { Rules.ParseRemote(Encoding.UTF8.GetBytes(text), O()); } catch (BuildException) { rejected = true; }
                Check(rejected, "Automatic detection accepted invalid domain content");
            }
            return Task.CompletedTask;
        });
        await test("URL editor and shorthand detect fetched and cached domain lists through project generation", async () =>
        {
            using var f = new Fixture(); using var server = new LocalServer { Body = "payload: ['*.wechat.com', 'btrace.qq.com', '+.example.org']" };
            var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
            ProjectOrdering.SetRemoteUrls(policy, "direct", server.Url + "download?list=wechat");
            f.Write("policy.yaml", policy);
            var source = Rules.RemoteSpecs(policy["remote_rules"]).Single();
            Check(!source.ContainsKey("behavior"));
            var checkedUrl = await RemoteRuleValidation.CheckAsync(f.Project, source, O("trust_env", false));
            Check(checkedUrl.Success && checkedUrl.RuleCount == 3);
            using (var fetch = new Fetcher(f.Project, M(BuildEngine.ProjectSettings(f.Project, f.Root), "http")))
                await fetch.ReadAsync(source, bytes => Rules.ParseRemote(bytes, source), "WeChat");
            server.Status = 403;
            var engine = new BuildEngine(); var built = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var rules = L(Map(YamlData.Read(built.OutputPath), "output"), "rules").Select(x => S(x)).ToArray();
            Check(rules.Contains(@"DOMAIN-REGEX,(?i)^[^.]+\.wechat\.com$,DIRECT") && rules.Contains("DOMAIN,btrace.qq.com,DIRECT") && rules.Contains("DOMAIN-SUFFIX,example.org,DIRECT"));
            policy["remote_rules"] = A(new JsonNode?[] { O("direct", A(S(source, "url"))) }); f.Write("policy.yaml", policy);
            var shorthand = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            Check(L(Map(YamlData.Read(shorthand.OutputPath), "output"), "rules").Select(x => S(x)).SequenceEqual(rules));
        });
        await test("Kernel-supported port and process wildcard types pass manual and remote parsing", () =>
        {
            foreach (string raw in new[] { "IN-PORT,7890", "PROCESS-NAME-WILDCARD,*chrome*", @"PROCESS-PATH-WILDCARD,C:\Apps\*" })
            {
                Check(Rules.WithAction(JsonValue.Create(raw), "REJECT") == raw + ",REJECT");
                Check(S(Rules.ParseRemote(Encoding.UTF8.GetBytes(raw), O())[0]) == raw + ",DIRECT");
            }
            return Task.CompletedTask;
        });
        await test("Explicit manual regex objects survive editor changes and project generation", async () =>
        {
            using var f = new Fixture(); string raw = @"DOMAIN-REGEX,^foo,bar$|^blocked\.example$";
            var draft = new ManualRuleEditor(new JsonArray(), false);
            draft.Replace(ManualRuleKind.Other, "{match:"); Check(S(draft.Rules[0]) == "{match:");
            draft.Replace(ManualRuleKind.Other, "{match: 'DOMAIN-REGEX,^foo,bar$'}"); Check(draft.Rules[0] is JsonObject);
            var editor = new ManualRuleEditor(A(new JsonNode?[] { O("match", raw) }), false);
            editor.Replace(ManualRuleKind.Other, editor.Text(ManualRuleKind.Other) + "\n{rule: 'DOMAIN-REGEX,^second,third$,OLD'}");
            Check(editor.Rules.Count == 2 && editor.Rules.All(x => x is JsonObject));
            var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy"); policy["reject"] = editor.Rules;
            policy["first"] = A(new JsonNode?[] { O("match", "IP-CIDR,127.0.0.0/8,no-resolve", "action", "REJECT"),
                O("match", "IP-CIDR,192.0.2.0/24", "action", "REJECT"), O("match", "IP-CIDR,127.0.0.0/8,no-resolve", "action", "REJECT") });
            f.Write("policy.yaml", policy);
            var result = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var rules = L(Map(YamlData.Read(result.OutputPath), "output"), "rules").Select(x => S(x)).ToArray();
            Check(rules.Contains(raw + ",REJECT") && rules.Contains("DOMAIN-REGEX,^second,third$,REJECT"));
            Check(rules.Count(x => x == "IP-CIDR,127.0.0.0/8,REJECT,no-resolve") == 2);
        });
    }
}

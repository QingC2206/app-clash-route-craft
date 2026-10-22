using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class DnsSettingsRegression
{
    static void Check(bool value, string message = "DNS settings assertion failed") { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Recommended DNS migrates legacy inputs without changing routing listeners modes or QUIC", () =>
        {
            var old = O("direct", A("local.example"), "dns", O("enable", false, "listen", "127.0.0.1:9953", "enhanced-mode", "fake-ip", "ipv6", true, "prefer-h3", true,
                "respect-rules", true, "direct-nameserver", A("https://1.1.1.1/dns-query#住宅出口"), "nameserver-policy", O("private.example", A("192.168.1.53")), "hosts-extra", "preserved"));
            string before = Canonical(old); var updated = DnsSettings.ApplyRecommended(old); var dns = M(updated, "dns");
            Check(Canonical(old) == before && Canonical(updated["direct"]) == Canonical(old["direct"]));
            Check(B(updated, "dns_follow_rules") && DnsSettings.DomesticEnabled(updated) && B(dns, "enable") && !B(dns, "respect-rules"));
            foreach (string key in new[] { "listen", "enhanced-mode", "ipv6", "prefer-h3", "hosts-extra" }) Check(Canonical(dns[key]) == Canonical(M(old, "dns")[key]));
            Check(M(dns, "nameserver-policy").ContainsKey("private.example") && !B(dns, "direct-nameserver-follow-policy"));
            Check(Canonical(DnsSettings.ApplyRecommended(updated)) == Canonical(updated));
            Check(DnsSettings.DomesticEnabled(DnsSettings.ApplyRecommended(O())));
            return Task.CompletedTask;
        });
        await test("Domestic DNS toggle removes only CN fallback and resolver edits stay synchronized through empty drafts", () =>
        {
            var policy = O("dns", DnsSettings.CreateDefault(), "dns_follow_rules", true);
            M(M(policy, "dns"), "nameserver-policy")["custom.example"] = A("9.9.9.9");
            DnsSettings.SetDomesticEnabled(policy, false);
            Check(!DnsSettings.DomesticEnabled(policy) && M(M(policy, "dns"), "nameserver-policy").ContainsKey("custom.example"));
            Check(B(policy, "dns_follow_rules") && L(M(policy, "dns"), "direct-nameserver").Count == 1);
            DnsSettings.SetDomesticEnabled(policy, true);
            DnsSettings.SetDirectResolvers(policy, new JsonArray());
            DnsSettings.SetDirectResolvers(policy, A("https://doh.pub/dns-query#DIRECT"));
            Check(DnsSettings.DomesticEnabled(policy));
            Check(Canonical(M(M(policy, "dns"), "nameserver-policy")["geosite:cn"]) == Canonical(M(policy, "dns")["direct-nameserver"]));
            return Task.CompletedTask;
        });
        await test("Supplemental rules preserve imported order behind project remotes and ahead of domestic fallback", async () =>
        {
            using var f = new Fixture(); var policy = new WorkspaceSession(f.Root, "SampleOne").Policy;
            policy["dns"] = DnsSettings.CreateDefault(); policy["dns_follow_rules"] = true;
            f.Write("remote.yaml", O("payload", A("DOMAIN,from-remote.example")));
            policy["remote_rules"] = A(new JsonNode?[] { O("name", "remote", "file", "remote.yaml", "action", "DIRECT") });
            policy["supplemental_rules"] = A(new JsonNode?[] { O("match", "DOMAIN-SUFFIX,import.example", "action", "PROXY"), O("match", "DOMAIN,child.import.example", "action", "DIRECT"), O("match", "DOMAIN,from-remote.example", "action", "REJECT") });
            policy["domestic_direct"] = true; f.Write("policy.yaml", policy);
            var built = await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            var cfg = Map(YamlData.Read(built.OutputPath), "config"); var rules = L(cfg, "rules").Select(x => S(x)).ToList();
            Check(rules.IndexOf("DOMAIN,from-remote.example,DIRECT") < rules.IndexOf("DOMAIN-SUFFIX,import.example,默认代理"), "Supplemental rule precedes remote rule");
            Check(rules.IndexOf("DOMAIN-SUFFIX,import.example,默认代理") < rules.IndexOf("GEOSITE,CN,DIRECT"), "Supplemental rule follows domestic fallback");
            Check(!rules.Contains("DOMAIN,from-remote.example,REJECT") || rules.IndexOf("DOMAIN,from-remote.example,DIRECT") < rules.IndexOf("DOMAIN,from-remote.example,REJECT"), "Supplemental conflict overrides earlier rule");
            policy["remote_rules"] = new JsonArray(); RuleDefaults.Save(f.Root, policy);
            WorkspaceSession.CreateProject(f.Root, "Imported");
            Check(Canonical(ImportedRuleSets.Sets(new WorkspaceSession(f.Root, "Imported").Policy)) == Canonical(ImportedRuleSets.Sets(policy)));
            Check(Canonical(ImportedRuleSets.Sets(new WorkspaceSession(f.Root, "Example").Policy)) == Canonical(ImportedRuleSets.Sets(policy)));
        });
    }
}

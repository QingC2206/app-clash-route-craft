using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class DnsPrivacyRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Prepare(Fixture f)
    {
        var primary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
        primary["proxy-groups"] = A(new JsonNode?[] { O("name", "Original", "type", "select", "proxies", A("香港同名", "美国")) });
        primary["rules"] = A("DOMAIN,local.example,DIRECT", "DOMAIN,passthrough.example,PASS", "DOMAIN,passthrough.example,Original",
            "AND,((NETWORK,UDP),(DST-PORT,443)),REJECT", "MATCH,Original");
        f.Write("subscrip/A.yaml", primary);
        f.Write("policy.yaml", O("rule_source", "subscription", "extra_rules_enabled", false,
            "dns", O("nameserver", A("https://1.1.1.1/dns-query#dormant-legacy")), "dns_follow_rules", true, "domestic_direct", true));
    }
    static void Set(Fixture f, bool enabled, string exit = "residential")
    {
        var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
        policy["dns_privacy"] = O("enabled", enabled, "proxy_exit", exit); f.Write("policy.yaml", policy);
    }
    static Task<BuildResult> Build(Fixture f) => new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
    static JsonObject Read(BuildResult result) => Map(YamlData.Read(result.OutputPath), "generated");
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Both stock DNS modes use fixed filenames independent of the main output name", async () =>
        {
            using var f = new Fixture(); Prepare(f);
            var settings = Map(YamlData.Read(f.Settings), "settings");
            foreach (var raw in L(settings, "sources")) { var source = Map(raw, "source"); source["file"] = "subscrip/" + S(source, "name") + ".yaml"; source.Remove("url"); }
            string directory = Path.Combine(f.Root, "renamed-output");
            var result = await new BuildEngine().GenerateAsync(f.Settings, Path.Combine(directory, "DifferentName.yaml"), settings, offline: true);
            Check(Directory.GetFiles(Path.Combine(directory, "default")).Select(Path.GetFileName).Order().SequenceEqual(new[] { "Fake-IP.yaml", "Redir-Host.yaml" }), "Default filenames depend on main name");
            foreach (string mode in new[] { "redir-host", "fake-ip" })
            {
                string relative = DnsPrivacy.DefaultRelativePath(mode), path = Path.Combine(directory, relative);
                var original = Map(YamlData.Read(path), "original");
                Check(original.Count == 1 && S(M(original, "dns"), "enhanced-mode") == mode, "Default contains unrelated project settings or wrong mode");
                Check(DependencySnapshot.FileHash(path) == S(M(M(result.Report, "dns"), "mc2_defaults"), relative), "Default checksum missing");
                Check(!File.ReadAllText(path).Contains("DifferentName"), "Fixed template contains main name");
            }
            var redir = M(Map(YamlData.Parse(DnsPrivacy.DefaultText("redir-host")), "redir"), "dns");
            Check(B(redir, "respect-rules") && M(redir, "nameserver-policy").ContainsKey("geosite: gfw") && L(redir, "proxy-server-nameserver").Select(x => S(x)).SequenceEqual(new[] { "119.29.29.29", "223.5.5.5" }), "Stock Redir-Host semantics changed");
        });
        await test("Default migration restores missing files and preserves edited legacy backups and unrelated files", async () =>
        {
            using var f = new Fixture(); Prepare(f); var result = await Build(f);
            string directory = Path.GetDirectoryName(result.OutputPath)!;
            string legacy = Path.Combine(directory, "HomeChain" + DnsPrivacy.LegacyDefaultSuffix);
            File.WriteAllBytes(legacy, DnsPrivacy.DefaultBytes());
            string note = Path.Combine(directory, "default", "keep.txt"); File.WriteAllText(note, "user note");
            File.Delete(Path.Combine(directory, DnsPrivacy.DefaultRelativePath("redir-host")));
            await Build(f);
            Check(!File.Exists(legacy) && DnsPrivacy.DefaultOutputs(directory).All(x => File.ReadAllBytes(x.Path).SequenceEqual(x.Bytes)), "Migration failed");
            string edited = DnsPrivacy.DefaultText() + "# user modification\n"; File.WriteAllText(legacy, edited);
            await Build(f);
            Check(File.ReadAllText(legacy) == edited && File.ReadAllText(note) == "user note", "Migration destroyed a personal file");
            var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
            await Build(f);
            Check(File.ReadAllText(legacy) == edited && File.ReadAllText(note) == "user note", "Target change destroyed a personal file");
        });
        await test("Blocked default export leaves the last configuration report and legacy recovery intact", async () =>
        {
            using var f = new Fixture(); Prepare(f); var result = await Build(f);
            string directory = Path.GetDirectoryName(result.OutputPath)!;
            string legacy = Path.Combine(directory, "HomeChain" + DnsPrivacy.LegacyDefaultSuffix);
            File.WriteAllBytes(legacy, DnsPrivacy.DefaultBytes());
            var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
            using (var held = File.Open(Path.Combine(directory, DnsPrivacy.DefaultRelativePath("fake-ip")), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { await Build(f); throw new Exception("Blocked default write was accepted"); }
                catch (BuildException) { }
            }
            Check(before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)), "Failed export partially replaced the bundle");
        });
        await test("Privacy toggle preserves all non-DNS content and returns to the exact baseline", async () =>
        {
            using var f = new Fixture(); Prepare(f);
            var initial = await Build(f); var before = Read(initial); string originalMain = Canonical(Read(initial));
            string directory = Path.GetDirectoryName(initial.OutputPath)!;
            Check(!before.ContainsKey("dns") && DnsPrivacy.DefaultOutputs(directory).All(x => File.Exists(x.Path)), "Default mode changed or rollback missing");
            var rawPolicy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
            Set(f, true); string inputs = InputFingerprint.Compute(f.Project);
            var enhanced = await Build(f); var output = Read(enhanced); var dns = M(output, "dns");
            Check(S(dns, "enhanced-mode") == "fake-ip" && S(dns, "fake-ip-filter-mode") == "blacklist", "Wrong DNS mode");
            Check(L(dns, "nameserver").All(x => S(x).EndsWith("#住宅出口")) && L(dns, "nameserver").Count == 2, "Proxy DNS leaked to direct or wrong group");
            Check(L(dns, "proxy-server-nameserver").All(x => S(x).EndsWith("#DIRECT")) && L(dns, "direct-nameserver").All(x => S(x).EndsWith("#DIRECT")), "Bootstrap/direct DNS loops through proxy");
            Check(!dns.ContainsKey("fallback") && !dns.ContainsKey("nameserver-policy") && GeoRules.RequiredDatabases(output).Length == 0, "Introduced DNS geodata dependencies");
            output.Remove("dns"); Check(Canonical(output) == Canonical(before), "Rules, QUIC, nodes or groups changed");
            Check(InputFingerprint.Compute(f.Project) == inputs, "Build changed project inputs");
            string fragment = File.ReadAllText(Path.Combine(directory, "HomeChain.MC2DNS.yaml"));
            Check(S(M(Map(YamlData.Parse(fragment), "fragment"), "dns"), "enhanced-mode") == "fake-ip" && fragment.Contains("listen: 0.0.0.0:23453") && !fragment.Contains("listen: \""), "MC2 mode or port incompatible");
            Check(new WorkspaceSession(f.Root, "SampleOne").OutputIsCurrent(), "Enhanced output not current");
            Set(f, false); await Build(f);
            Check(Canonical(YamlData.Read(initial.OutputPath)) == originalMain && !File.Exists(Path.Combine(directory, "HomeChain.MC2DNS.yaml")), "Default mode not fully restored");
            Check(DnsPrivacy.DefaultOutputs(directory).All(x => File.ReadAllBytes(x.Path).SequenceEqual(x.Bytes)), "Default recovery differs from built-in original");
            var restored = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy"); restored.Remove("dns_privacy");
            Check(Canonical(restored) == Canonical(rawPolicy), "Toggle destroyed dormant DNS or rules");
        });
        await test("Privacy DNS follows allocated groups when subscription names collide", async () =>
        {
            using var f = new Fixture(); Prepare(f);
            var primary = Map(YamlData.Read(Path.Combine(f.Project, "subscrip/A.yaml")), "subscription");
            foreach (string name in new[] { "住宅出口", "机场入口" }) L(primary, "proxy-groups").Push(O("name", name, "type", "select", "proxies", A("DIRECT")));
            f.Write("subscrip/A.yaml", primary); Set(f, true);
            var home = await Build(f); string residential = S(home.Report, "residential_group");
            Check(residential != "住宅出口" && L(M(Read(home), "dns"), "nameserver").All(x => S(x).EndsWith("#" + residential)), "DNS selected colliding subscription group");
            Set(f, true, "airport"); var airport = await Build(f); string entry = S(airport.Report, "jump_group");
            Check(entry != "机场入口" && L(M(Read(airport), "dns"), "nameserver").All(x => S(x).EndsWith("#" + entry)), "Airport alias not resolved");
        });
        await test("Missing residential DNS exit fails without overwriting working outputs; airport is explicit", async () =>
        {
            using var f = new Fixture(); Prepare(f); f.Write("residential.yaml", O("proxies", A()));
            var old = await Build(f); string before = File.ReadAllText(old.OutputPath); Set(f, true);
            try { await Build(f); throw new Exception("Missing residence accepted"); }
            catch (BuildException ex) { Check(ex.Message.Contains("住宅"), "Unclear exit error"); }
            Check(File.ReadAllText(old.OutputPath) == before, "Failure overwrote main");
            Set(f, true, "airport"); var built = await Build(f);
            Check(L(M(Read(built), "dns"), "nameserver").All(x => S(x).EndsWith("#机场入口")), "Explicit airport unavailable");
            Set(f, true, "DIRECT");
            try { await Build(f); throw new Exception("Direct fallback accepted"); } catch (BuildException) { }
        });
        await test("Enhanced Clash export embeds DNS and removes only MC2 artifacts", async () =>
        {
            using var f = new Fixture(); Prepare(f); Set(f, true); var mc2 = await Build(f); string originalDns = Canonical(M(Read(mc2), "dns"));
            string directory = Path.GetDirectoryName(mc2.OutputPath)!;
            var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = "clash"; f.Write("settings.yaml", settings);
            var clash = await Build(f);
            Check(originalDns == Canonical(M(Read(clash), "dns")), "Target changed DNS routing");
            foreach (string suffix in new[] { ".MC2DNS.yaml", DnsPrivacy.LegacyDefaultSuffix, DnsPrivacy.GuideSuffix }) Check(!File.Exists(Path.Combine(directory, "HomeChain" + suffix)), "Obsolete MC2 artifact retained");
            Check(DnsPrivacy.DefaultOutputs(directory).All(x => !File.Exists(x.Path)), "Clash export retained MC2 defaults");
        });
        await test("Legacy custom DNS survives an enhanced round trip and MC2 preserves explicit Fake-IP", async () =>
        {
            using var f = new Fixture(); var original = await Build(f); string before = Canonical(Read(original));
            Set(f, true); var privacy = await Build(f); Check(S(M(Read(privacy), "dns"), "enhanced-mode") == "fake-ip", "Legacy could not opt in");
            Check(!M(Read(privacy), "dns").ContainsKey("nameserver-policy"), "Legacy DNS policies contaminated preset");
            Set(f, false); await Build(f); Check(Canonical(YamlData.Read(original.OutputPath)) == before, "Legacy DNS lost on disable");
            var fragment = ProfileCompiler.Mc2Dns(O("dns", O("enhanced-mode", "fake-ip")))!;
            Check(S(M(fragment, "dns"), "enhanced-mode") == "fake-ip", "Explicit Fake-IP forced to Redir-Host");
        });
        await test("Recovery template is independent of private settings and contains actionable steps", async () =>
        {
            using var f = new Fixture(); Prepare(f); Set(f, true); var result = await Build(f);
            string directory = Path.GetDirectoryName(result.OutputPath)!;
            var original = Map(YamlData.Parse(DnsPrivacy.DefaultText()), "official");
            Check(original.Count == 1 && original.ContainsKey("dns") && !DnsPrivacy.DefaultText().Contains("#住宅出口"), "Recovery depends on private project");
            Check(S(M(original, "dns"), "fake-ip-filter-mode") == "whitelist" && L(M(original, "dns"), "nameserver").Count == 3, "Official semantics modified");
            string guide = File.ReadAllText(Path.Combine(directory, "HomeChain" + DnsPrivacy.GuideSuffix));
            Check(guide.Contains("编辑 Fake-IP") && guide.Contains("编辑 Redir-Host") && guide.Contains("default/Redir-Host.yaml") && guide.Contains("default/Fake-IP.yaml") && guide.Contains("仅重新导入主 YAML 不会") && guide.Contains("1.2.2"), "Missing MC2 application/recovery/version boundary");
            var policy = Map(YamlData.Read(Path.Combine(f.Project, "policy.yaml")), "policy");
            Check(!RuleDefaults.Extract(policy).ContainsKey("dns_privacy") && DnsPrivacy.Enabled(RuleDefaults.Apply(policy, O())), "Applying rules changed DNS privacy");
        });
    }
}

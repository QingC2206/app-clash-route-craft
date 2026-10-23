using System.Text;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class RouterAsnToolsRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Generation exports portable embedded ASN tools and restores missing tools", async () =>
        {
            using var f = new Fixture(); var engine = new BuildEngine();
            foreach (string target in new[] { "mc2", "clash" })
            {
                var settings = Map(YamlData.Read(f.Settings), "settings"); settings["target"] = target; f.Write("settings.yaml", settings);
                var built = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
                string directory = Path.GetDirectoryName(built.OutputPath)!;
                var tools = RouterAsnTools.Outputs(directory);
                Check(tools.Count == 5 && L(M(built.Report, "router_asn_tools"), "files").Count == 5, "Incomplete standalone tools");
                foreach (var (path, bytes) in tools)
                {
                    Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Embedded resource not exported");
                    Check(!Encoding.UTF8.GetString(bytes).Contains('\r'), "Tool contains Windows newlines");
                }
                string readme = File.ReadAllText(Path.Combine(directory, "router-asn", "README.md"));
                Check(readme.Contains("sh ./install-router-asn.sh") && !readme.Contains("sh scripts/"), "Guide requires source checkout");
                byte[] main = File.ReadAllBytes(built.OutputPath);
                File.Delete(tools[0].Path);
                string extra = Path.Combine(directory, "router-asn", "router-login.json"); File.WriteAllText(extra, "{\"password\":\"fixture-private-password\"}");
                string hosts = Path.Combine(directory, "router-asn", "router-known_hosts"); File.WriteAllText(hosts, "fixture-project-host-key");
                await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
                Check(File.Exists(tools[0].Path) && File.ReadAllText(extra) == "{\"password\":\"fixture-private-password\"}", "Recovery changed private login file");
                Check(File.ReadAllText(hosts) == "fixture-project-host-key", "Recovery changed project SSH trust records");
                Check(File.ReadAllBytes(built.OutputPath).SequenceEqual(main), "Tools changed generated YAML");
            }
        });
        await test("ASN inspection produces no installation tools or configuration", async () =>
        {
            using var f = new Fixture();
            await new BuildEngine().BuildProjectAsync(f.Root, "SampleOne", new(Offline: true, InspectAsnOnly: true));
            Check(!Directory.Exists(Path.Combine(f.Root, "output", "SampleOne")), "Inspection wrote output files");
        });
        await test("Blocked tool write preserves configuration and all previous output", async () =>
        {
            using var f = new Fixture(); var engine = new BuildEngine();
            var built = await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true));
            string directory = Path.GetDirectoryName(built.OutputPath)!;
            var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
            using (var locked = new FileStream(Path.Combine(directory, "router-asn", "install-router-asn.py"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { await engine.BuildProjectAsync(f.Root, "SampleOne", new(Offline: true)); throw new Exception("Locked tool did not prevent commit"); }
                catch (BuildException) { }
            }
            Check(before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)), "Failed generation changed old outputs");
            Check(!Directory.GetFiles(directory, "*.building-*", SearchOption.AllDirectories).Any(), "Staging files leaked");
        });
    }
}

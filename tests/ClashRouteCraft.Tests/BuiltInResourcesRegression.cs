using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class BuiltInResourcesRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Embedded defaults restore missing files and preserve personal inputs", () =>
        {
            using var f = new Fixture(); var original = Directory.GetFiles(f.Project, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
            Check(BuiltInDefaults.Ensure(f.Root).Count == 4 && BuiltInDefaults.Ensure(f.Root).Count == 0, "Initialization not idempotent");
            Check(BuildEngine.DiscoverProjects(f.Root).SequenceEqual(["SampleOne"]), "Example became a normal project");
            var example = new WorkspaceSession(f.Root, "Example"); Check(example.IsReadOnly && SubscriptionBaseline.Enabled(example.Policy) && !example.Policy.ContainsKey("dns") && example.Residences.Count == 0 && ImportedRuleSets.Sets(example.Policy).Count == 0, "Factory default incorrect");
            var custom = example.Policy; custom["direct"] = A("DOMAIN,my.example"); RuleDefaults.Save(f.Root, custom); string saved = File.ReadAllText(RuleDefaults.FilePath(f.Root));
            string deleted = Path.Combine(f.Root, "config", "Example", "settings.yaml"); File.Delete(deleted);
            Check(BuiltInDefaults.Ensure(f.Root).Count == 1 && File.Exists(deleted) && File.ReadAllText(RuleDefaults.FilePath(f.Root)) == saved, "Custom defaults overwritten");
            string userPolicy = Path.Combine(f.Project, "policy.yaml"); File.Delete(userPolicy); BuiltInDefaults.Ensure(f.Root);
            Check(!File.Exists(userPolicy) && original.Where(x => x.Key != userPolicy).All(x => x.Value.SequenceEqual(File.ReadAllBytes(x.Key))), "Personal inputs changed");
            File.Delete(RuleDefaults.FilePath(f.Root)); BuiltInDefaults.Ensure(f.Root); Check(!Canonical(RuleDefaults.Read(f.Root)).Contains("my.example"), "Personal template restored as factory"); return Task.CompletedTask;
        });
        await test("Default recovery preserves corrupt files and rejects directory conflicts", () =>
        {
            using var f = new Fixture(); BuiltInDefaults.Ensure(f.Root); string path = RuleDefaults.FilePath(f.Root);
            File.WriteAllText(path, "invalid: ["); BuiltInDefaults.Ensure(f.Root); Check(File.ReadAllText(path) == "invalid: [", "Corrupt config overwritten");
            File.Delete(path); Directory.CreateDirectory(path); bool failed = false;
            try { BuiltInDefaults.Ensure(f.Root); } catch (BuildException) { failed = true; }
            Check(failed && Directory.Exists(path), "Directory overwritten"); return Task.CompletedTask;
        });
        await test("Resource download verifies hashes and preserves existing resources", async () =>
        {
            using var f = new Fixture(); using var handler = new ResourceHandler(); using var client = new HttpClient(handler);
            string path = RuntimeResources.PathFor(f.Root, RuntimeResources.Catalog[1]); FileBundle.AtomicWrite(path, Encoding.UTF8.GetBytes("custom-data"));
            Check(await RuntimeResources.DownloadMissingAsync(f.Root, client: client) == 4 && RuntimeResources.Missing(f.Root).Count == 0 && File.ReadAllText(path) == "custom-data", "Existing resource replaced");
            Check(handler.Calls == 6 && !File.Exists(Path.Combine(f.Root, "escape.exe")), "Unexpected request count or archive escape");
            Check(await RuntimeResources.DownloadMissingAsync(f.Root, client: client) == 0 && handler.Calls == 6, "Complete resources fetched");
            Check(CoreValidation.FindResources(f.Root).Mc2Package == "", "MC2 package remains a default dependency");
        });
        await test("Resource checksum failure commits no partial downloads", async () =>
        {
            using var f = new Fixture(); using var handler = new ResourceHandler { BadDigest = true }; using var client = new HttpClient(handler); bool failed = false;
            try { await RuntimeResources.DownloadMissingAsync(f.Root, client: client); } catch (BuildException) { failed = true; }
            Check(failed && RuntimeResources.Missing(f.Root).Count == 5 && !Directory.Exists(Path.Combine(f.Root, "resources")), "Partial download committed");
        });
        await test("Resource cancellation commits no partial files", async () =>
        {
            using var f = new Fixture(); using var handler = new ResourceHandler { Slow = true }; using var client = new HttpClient(handler); using var cancellation = new CancellationTokenSource(50); bool cancelled = false;
            try { await RuntimeResources.DownloadMissingAsync(f.Root, cancellation: cancellation.Token, client: client); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !Directory.Exists(Path.Combine(f.Root, "resources")), "Cancelled download committed");
        });
        await test("MC2 configuration validation requires no installer archive", () =>
        {
            using var f = new Fixture(); string exe = RuntimeResources.PathFor(f.Root, RuntimeResources.Catalog[0]); FileBundle.AtomicWrite(exe, Encoding.UTF8.GetBytes("test"));
            ConfigurationValidation.Resources(O("target", "mc2", "validation", O("required", true, "mihomo", exe)), f.Project, O("geo_mode", "mc2-inline")); return Task.CompletedTask;
        });
    }
    sealed class ResourceHandler : HttpMessageHandler
    {
        public bool BadDigest, Slow; public int Calls;
        readonly Dictionary<string, byte[]> data = new();
        public ResourceHandler()
        {
            foreach (var item in RuntimeResources.Catalog)
            {
                byte[] bytes = Encoding.UTF8.GetBytes("test-data-" + item.Name);
                if (item.Zip)
                {
                    using var buffer = new MemoryStream();
                    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)) { using var stream = zip.CreateEntry("../../escape.exe").Open(); stream.Write(Encoding.UTF8.GetBytes("MZ-test")); }
                    bytes = buffer.ToArray();
                }
                data[item.Asset] = bytes;
            }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string url = request.RequestUri!.ToString(); Calls++;
            if (Slow) await Task.Delay(1000, cancellation);
            if (request.RequestUri.Host == "api.github.com")
            {
                var assets = new JsonArray();
                foreach (var item in RuntimeResources.Catalog.Where(x => x.Release == url))
                {
                    string digest = BadDigest && item.Asset == "country.mmdb" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(data[item.Asset]));
                    string path = item.Zip ? "MetaCubeX/mihomo" : "MetaCubeX/meta-rules-dat";
                    assets.Push(O("name", item.Asset, "digest", "sha256:" + digest, "browser_download_url", "https://github.com/" + path + "/releases/download/test/" + item.Asset));
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(O("assets", assets).ToJsonString()) };
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data[Path.GetFileName(request.RequestUri.AbsolutePath)]) };
        }
    }
}

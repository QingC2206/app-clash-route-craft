using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class CoreValidation
{
    public static ValidationPaths FindResources(string workspace)
    {
        string bundled = Path.Combine(Path.GetFullPath(workspace), "resources");
        string app = Path.Combine(AppContext.BaseDirectory, "resources");
        return new(
            File.Exists(Path.Combine(bundled, "validation", "mihomo.exe")) || !File.Exists(Path.Combine(app, "validation", "mihomo.exe")) ? Path.Combine(bundled, "validation", "mihomo.exe") : Path.Combine(app, "validation", "mihomo.exe"),
            Directory.Exists(Path.Combine(bundled, "geodata")) || !Directory.Exists(Path.Combine(app, "geodata")) ? Path.Combine(bundled, "geodata") : Path.Combine(app, "geodata"), "");
    }
    public static async Task ValidateAsync(JsonObject configuration, string executable, string dataDirectory, string logPath, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(dataDirectory);
        string pending = Path.Combine(dataDirectory, "config-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllBytesAsync(pending, YamlData.Bytes(configuration), cancellation);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (string arg in new[] { "-t", "-d", Path.GetFullPath(dataDirectory), "-f", pending }) process.StartInfo.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            if (!process.Start()) throw new BuildException("Mihomo 校验程序未能启动");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string log = await stdout + await stderr;
            FileBundle.AtomicWrite(logPath, Encoding.UTF8.GetBytes(log));
            if (process.ExitCode != 0) throw new BuildException($"Mihomo 校验失败，旧输出已保留；详细日志：{Path.GetFileName(logPath)}");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            cancellation.ThrowIfCancellationRequested(); throw new BuildException("Mihomo 校验超时，旧输出已保留");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { throw new BuildException("Mihomo 校验未完成，请检查可执行文件与目录权限"); }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    public static async Task<JsonObject> ValidatePackageAsync(JsonObject configuration, JsonObject? dns, string executable, string package, string logPath, CancellationToken cancellation = default, string? nativeDataDirectory = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "homechain-mc2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var content = new Dictionary<string, byte[]>();
            using (var file = File.OpenRead(package))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new TarReader(gzip))
            {
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(false, cancellation)) is not null)
                {
                    string name = entry.Name.TrimStart('.', '/');
                    if (name is not "merlinclash/clash/GeoIP.dat" and not "merlinclash/clash/GeoSite.dat" and not "merlinclash/yaml_basic/head.yaml" and not "merlinclash/yaml_dns/fakeip.yaml" and not "merlinclash/yaml_dns/redirhost.yaml") continue;
                    if (entry.DataStream is null || entry.Length > 64 * 1024 * 1024) throw new BuildException("MC2 安装包文件无效或过大");
                    using var data = new MemoryStream(); await entry.DataStream.CopyToAsync(data, cancellation); content[name] = data.ToArray();
                }
            }
            var assets = new JsonObject();
            foreach (string name in new[] { "GeoIP.dat", "GeoSite.dat" })
            {
                if (!content.TryGetValue("merlinclash/clash/" + name, out var raw)) throw new BuildException("MC2 安装包缺少必要数据库");
                await File.WriteAllBytesAsync(Path.Combine(directory, name), raw, cancellation);
                assets[name] = O("bytes", raw.Length, "sha256", Convert.ToHexStringLower(SHA256.HashData(raw)));
            }
            if (!content.TryGetValue("merlinclash/yaml_basic/head.yaml", out var header)) throw new BuildException("MC2 安装包缺少 head.yaml");
            var merged = Map(YamlData.Parse(header), "MC2 head");
            foreach (string key in new[] { "proxies", "proxy-providers", "proxy-groups", "rule-providers", "rules", "sub-rules" }) if (configuration.ContainsKey(key)) merged[key] = configuration[key]?.DeepClone();
            if (dns is not null) merged = Merge(merged, dns);
            var requiredAssets = new JsonObject();
            if (nativeDataDirectory is not null)
                foreach (string name in GeoRules.RequiredDatabases(merged))
                {
                    string source = Path.Combine(nativeDataDirectory, name);
                    if (!File.Exists(source)) throw new BuildException($"原生 GEO 的 MC2 校验需要 {name}；请补齐校验数据库，不会自动改为内嵌名单。");
                    byte[] raw = await File.ReadAllBytesAsync(source, cancellation);
                    await File.WriteAllBytesAsync(Path.Combine(directory, name), raw, cancellation);
                    requiredAssets[name] = O("bytes", raw.Length, "sha256", Convert.ToHexStringLower(SHA256.HashData(raw)));
                }
            var dnsModes = new JsonArray();
            if (dns is null)
            {
                foreach (string mode in new[] { "fakeip", "redirhost" })
                {
                    if (!content.TryGetValue($"merlinclash/yaml_dns/{mode}.yaml", out var template)) throw new BuildException("MC2 安装包缺少默认 DNS 模板：" + mode);
                    await ValidateAsync(Merge(merged, Map(YamlData.Parse(template), "MC2 默认 DNS")), executable, directory, logPath + "." + mode, cancellation);
                    dnsModes.Push(mode);
                }
            }
            else await ValidateAsync(merged, executable, directory, logPath, cancellation);
            var downloaded = Directory.GetFiles(directory).Select(Path.GetFileName).Where(x => x is not null && !assets.ContainsKey(x) && !requiredAssets.ContainsKey(x)).ToArray();
            if (downloaded.Length > 0) throw new BuildException("MC2 校验触发未声明的数据库下载，请补齐数据库依赖；旧输出已保留");
            bool external = requiredAssets.Count > 0;
            return O("status", external ? "passed_with_geodata" : "passed", "scope", external ? "MC2 package header + external databases + imported fields + DNS; install/update these databases on the router" : "MC2 package header + stock databases + imported fields + DNS",
                "database_mode", external ? "external" : "stock", "dns_source", dns is null ? "mc2_default" : "generated", "stock_dns_validated", dnsModes, "required_assets", requiredAssets, "stock_assets", assets, "additional_core_downloads", A(), "router_runtime_tested", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new BuildException("MC2 安装包读取失败，请检查 validation.mc2_package"); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    public static void CopyGeoData(string source, string target, IReadOnlyCollection<string>? required = null)
    {
        if (!Directory.Exists(source)) throw new BuildException("GeoData 目录不存在，请在应用设置中配置");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            if (required is not null && !required.Contains(Path.GetFileName(file))) continue;
            if (!new[] { ".dat", ".mmdb", ".mrs" }.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination) || DependencySnapshot.FileHash(file) != DependencySnapshot.FileHash(destination)) File.Copy(file, destination, true);
        }
    }
}

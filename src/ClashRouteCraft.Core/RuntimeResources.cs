using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record RuntimeResource(string Name, string RelativePath, string Release, string Asset, bool Zip = false);
public sealed record ResourceProgress(string Message, int Completed, int Total);

/// <summary>Explicit, checksum-verified downloads of missing validation assets. No client installers.</summary>
public static class RuntimeResources
{
    const string MihomoRelease = "https://api.github.com/repos/MetaCubeX/mihomo/releases/tags/v1.19.28";
    const string GeoRelease = "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/latest";
    public static readonly IReadOnlyList<RuntimeResource> Catalog = new[]
    {
        new RuntimeResource("Mihomo v1.19.28", "validation/mihomo.exe", MihomoRelease, "mihomo-windows-amd64-compatible-v1.19.28.zip", true),
        new RuntimeResource("GeoIP.dat", "geodata/GeoIP.dat", GeoRelease, "geoip.dat"),
        new RuntimeResource("GeoSite.dat", "geodata/GeoSite.dat", GeoRelease, "geosite.dat"),
        new RuntimeResource("ASN.mmdb", "geodata/ASN.mmdb", GeoRelease, "GeoLite2-ASN.mmdb"),
        new RuntimeResource("country.mmdb", "geodata/country.mmdb", GeoRelease, "country.mmdb")
    };
    public static string PathFor(string workspace, RuntimeResource item) => Path.Combine(Path.GetFullPath(workspace), "resources", item.RelativePath);
    public static bool Present(string workspace, RuntimeResource item) => File.Exists(PathFor(workspace, item)) && new FileInfo(PathFor(workspace, item)).Length > 0;
    public static IReadOnlyList<RuntimeResource> Missing(string workspace) => Catalog.Where(x => !Present(workspace, x)).ToArray();
    public static async Task<int> DownloadMissingAsync(string workspace, IProgress<ResourceProgress>? progress = null, CancellationToken cancellation = default, HttpClient? client = null)
    {
        workspace = Path.GetFullPath(workspace); var missing = Missing(workspace);
        if (missing.Count == 0) return 0;
        var caller = cancellation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromMinutes(10)); cancellation = deadline.Token;
        foreach (var item in missing) BuiltInDefaults.CheckPath(PathFor(workspace, item));
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) } : null;
        client ??= owned!;
        var releases = new Dictionary<string, JsonObject>(); var outputs = new List<(string Path, byte[] Bytes)>();
        int complete = 0;
        try
        {
            foreach (var item in missing)
            {
                cancellation.ThrowIfCancellationRequested(); progress?.Report(new("正在读取 " + item.Name + " 的官方发布信息…", complete, missing.Count));
                if (!releases.TryGetValue(item.Release, out var release))
                {
                    release = Map(JsonNode.Parse(await Get(item.Release, 8 * 1024 * 1024)), "发布信息"); releases[item.Release] = release;
                }
                var asset = L(release, "assets").OfType<JsonObject>().SingleOrDefault(x => S(x, "name") == item.Asset)
                    ?? throw new BuildException(item.Name + " 的官方发布资源未找到，请稍后重试");
                string digest = S(asset, "digest"), url = S(asset, "browser_download_url");
                if (!digest.StartsWith("sha256:") || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new BuildException(item.Name + " 缺少官方 SHA-256 校验值，未下载");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith(item.Zip ? "/MetaCubeX/mihomo/releases/download/" : "/MetaCubeX/meta-rules-dat/releases/download/", StringComparison.Ordinal))
                    throw new BuildException("发布资源地址不在指定官方仓库内，未下载");
                progress?.Report(new("正在下载 " + item.Name + "…", complete, missing.Count));
                byte[] bytes = await Get(url, 128 * 1024 * 1024);
                if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new BuildException(item.Name + " 校验失败，未安装；官方资源可能刚刚更新，请重试");
                if (item.Zip)
                {
                    using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                    var entries = archive.Entries.Where(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (entries.Length != 1 || entries[0].Length is <= 0 or > 128 * 1024 * 1024) throw new BuildException("Mihomo 压缩包结构异常，未安装");
                    await using var stream = entries[0].Open(); bytes = await ReadLimited(stream, 128 * 1024 * 1024);
                    if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new BuildException("Mihomo 可执行文件格式异常，未安装");
                }
                if (bytes.Length == 0) throw new BuildException(item.Name + " 内容为空，未安装");
                outputs.Add((PathFor(workspace, item), bytes)); complete++;
                progress?.Report(new(item.Name + " 已校验", complete, missing.Count));
            }
            cancellation.ThrowIfCancellationRequested();
            using var lease = WorkspaceLease.Acquire(workspace, true);
            // Keep any resources another process installed while downloads were in progress.
            outputs = outputs.Where(x => !File.Exists(x.Path) || new FileInfo(x.Path).Length == 0).ToList();
            foreach (var output in outputs) BuiltInDefaults.CheckPath(output.Path);
            FileBundle.Commit(outputs, cancellation);
            return outputs.Count;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new BuildException("资源下载超时；现有文件保持不变，请重试"); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        { throw new BuildException("无法下载或读取官方资源；请检查网络后重试，也可手动放入 resources 对应目录"); }

        async Task<byte[]> Get(string url, int maximum)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("ClashRouteCraft/1.12.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) throw new BuildException($"官方资源请求失败（HTTP {(int)response.StatusCode}）；请稍后重试");
            if (response.Content.Headers.ContentLength > maximum) throw new BuildException("资源超过大小限制，未安装");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation); return await ReadLimited(stream, maximum);
        }
        async Task<byte[]> ReadLimited(Stream stream, int maximum)
        {
            using var buffer = new MemoryStream(); byte[] block = new byte[65536]; int count;
            while ((count = await stream.ReadAsync(block, cancellation)) > 0)
            {
                if (buffer.Length + count > maximum) throw new BuildException("资源超过大小限制，未安装");
                buffer.Write(block, 0, count);
            }
            return buffer.ToArray();
        }
    }
}

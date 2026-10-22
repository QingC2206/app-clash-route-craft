using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed class Fetcher : IDisposable
{
    public string Root { get; }
    public JsonObject Options { get; }
    public JsonArray Events { get; } = new();
    readonly bool offline;
    readonly string cache;
    readonly HttpClient client;
    readonly CancellationToken cancellation;
    readonly bool useCache;

    public Fetcher(string root, JsonObject options, bool offline = false, CancellationToken cancellation = default, bool useCache = true)
    {
        Root = Path.GetFullPath(root); Options = options; this.offline = offline; this.cancellation = cancellation; this.useCache = useCache;
        cache = Resolve(S(options, "cache_dir", Path.Combine(Root, ".cache")));
        var handler = new SocketsHttpHandler
        {
            UseProxy = B(options, "trust_env", true), ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 10
        };
        if (S(options, "proxy") is { Length: > 0 } proxy)
        {
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri) || proxyUri.Scheme is not "http" and not "https" and not "socks5") throw new BuildException("HTTP 代理地址无效");
            handler.Proxy = new WebProxy(proxyUri); handler.UseProxy = true;
        }
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(I(options, "timeout", 30), 1, 300)) };
    }
    public string Resolve(string path, string? baseDirectory = null)
    {
        if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory ?? Root, path));
    }
    JsonObject RequestHeaders(JsonObject spec)
    {
        var headers = O("User-Agent", S(spec, "user_agent", S(Options, "user_agent", "clash-verge/v2.4.7")));
        foreach (var (key, value) in M(spec, "headers")) headers[key] = value is JsonArray list ? string.Join(", ", list.Select(SValue)) : S(value);
        return headers;
    }
    public string CachedPath(JsonObject spec) => Path.Combine(cache, Hash(PythonJson(new JsonArray(S(spec, "url"), RequestHeaders(spec)))) + ".data");
    public async Task<(JsonNode? Value, string? Base)> ReadAsync(JsonObject spec, Func<byte[], JsonNode?> parser, string label, string? baseDirectory = null)
    {
        cancellation.ThrowIfCancellationRequested();
        string url = S(spec, "url"), file = S(spec, "file");
        if ((url.Length > 0) == (file.Length > 0)) throw new BuildException($"{label} 必须且只能指定 url 或 file 之一");
        if (file.Length > 0)
        {
            string path = Resolve(file, baseDirectory);
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(path, cancellation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BuildException($"{label} 的本地文件不可读"); }
            var value = parser(bytes);
            Events.Push(O("item", label, "status", "local", "bytes", bytes.Length));
            return (value, Path.GetDirectoryName(path));
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https") throw new BuildException($"{label} 的 URL 协议必须为 HTTP(S)");
        var headers = RequestHeaders(spec);
        string cacheFile = CachedPath(spec);
        int maximumAge = Math.Max(0, I(Options, "cache_max_age", 86400));
        bool Expired() => !File.Exists(cacheFile) || maximumAge == 0 || DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) >= TimeSpan.FromSeconds(maximumAge);
        void Record(string status, byte[] raw, string? why = null, bool stale = false)
        {
            var entry = O("item", label, "status", status, "bytes", raw.Length, "sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)));
            if (useCache) { entry["cache_file"] = Path.GetFileName(cacheFile); entry["cached_at"] = File.GetLastWriteTimeUtc(cacheFile).ToString("O"); }
            if (why is not null) entry["reason"] = why;
            if (stale) entry["stale"] = true;
            Events.Push(entry);
        }
        if (useCache && B(Options, "prefer_cache") && File.Exists(cacheFile) && !Expired())
        {
            try { byte[] raw = await File.ReadAllBytesAsync(cacheFile, cancellation); var value = parser(raw); Record("cache", raw); return (value, null); }
            catch (Exception ex) when (ex is BuildException or IOException) { }
        }
        string reason = "offline";
        if (!offline)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(client.Timeout);
                var downloadToken = timeout.Token;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                foreach (var (key, headerValue) in headers)
                {
                    string content = S(headerValue);
                    request.Headers.Remove(key);
                    if (key.IndexOfAny(['\r', '\n']) >= 0 || content.IndexOfAny(['\r', '\n']) >= 0 || !request.Headers.TryAddWithoutValidation(key, content)) throw new BuildException("请求头格式无效");
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken);
                if (!response.IsSuccessStatusCode) throw new BuildException($"HTTP {(int)response.StatusCode}");
                int maximum = Math.Clamp(I(Options, "max_bytes", 16 * 1024 * 1024), 1, 128 * 1024 * 1024);
                await using var stream = await response.Content.ReadAsStreamAsync(downloadToken);
                using var buffer = new MemoryStream();
                byte[] block = new byte[65536]; int count;
                while ((count = await stream.ReadAsync(block, downloadToken)) > 0)
                {
                    if (buffer.Length + count > maximum) throw new BuildException("响应超过大小限制");
                    buffer.Write(block, 0, count);
                }
                byte[] raw = buffer.ToArray();
                var value = parser(raw); // Invalid responses never replace a valid cache.
                if (useCache) FileBundle.AtomicWrite(cacheFile, raw);
                Record("downloaded", raw);
                return (value, null);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { reason = "请求超时"; }
            catch (BuildException ex) { reason = ex.Message; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) { reason = "网络连接或缓存写入失败"; }
        }
        if (useCache && (offline || B(spec, "allow_stale", B(Options, "allow_stale", true))))
        {
            if (File.Exists(cacheFile))
                try
                {
                    byte[] raw = await File.ReadAllBytesAsync(cacheFile, cancellation); var value = parser(raw);
                    Record("cache", raw, reason, Expired());
                    return (value, null);
                }
                catch (Exception ex) when (ex is BuildException or IOException) { }
            if (S(spec, "fallback_file") is { Length: > 0 } fallback)
            {
                string path = Resolve(fallback, baseDirectory);
                try
                {
                    var value = parser(await File.ReadAllBytesAsync(path, cancellation));
                    Events.Push(O("item", label, "status", "fallback_file", "reason", reason, "stale", true));
                    return (value, Path.GetDirectoryName(path));
                }
                catch (Exception ex) when (ex is BuildException or IOException) { throw new BuildException($"{label} 下载失败，备用文件也不可用"); }
            }
        }
        throw new BuildException(useCache ? $"{label} 无法读取且没有可用缓存（{reason}）" : reason);
    }
    static string SValue(JsonNode? node) => S(node);
    public void Dispose() => client.Dispose();
}

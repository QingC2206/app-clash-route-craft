using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record ResidenceProbeResult(string State, string Message, string? ExitIp = null);

/// <summary>Explicit computer-to-residence probe. Never changes global proxy settings or project files.</summary>
public static class ResidenceProbe
{
    public static readonly Uri Endpoint = new("https://www.cloudflare.com/cdn-cgi/trace");
    public static async Task<ResidenceProbeResult> CheckAsync(JsonObject raw, CancellationToken cancellation = default, Uri? endpoint = null, TimeSpan? timeout = null)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var node = Map(ExpandEnvironment(raw.DeepClone()), "住宅");
            ProfileCompiler.ValidateResidences(A(new JsonNode?[] { node }));
            string type = S(node, "type");
            if (type is not "http" and not "socks5" || type == "socks5" && B(node, "tls"))
                return new("warning", "此协议暂不支持独立检测，请通过客户端验证");
            if (S(node, "sni").Length > 0 || B(node, "skip-cert-verify"))
                return new("warning", "此节点使用自定义 TLS 参数，请通过客户端验证；检测不会忽略证书错误");
            var address = new UriBuilder(type == "http" && B(node, "tls") ? "https" : type, S(node, "server").Trim('[', ']'), I(node, "port", 0)).Uri;
            var proxy = new WebProxy(address, false);
            if (S(node, "username").Length > 0 || S(node, "password").Length > 0)
                proxy.Credentials = new NetworkCredential(S(node, "username"), S(node, "password"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
            using var handler = new SocketsHttpHandler { Proxy = proxy, UseProxy = true, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = timeout ?? TimeSpan.FromSeconds(12) };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var timer = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint ?? Endpoint);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) return new("fail", "代理鉴权失败，请检查账号和密码");
            if (!response.IsSuccessStatusCode) return new("fail", $"检测服务返回 HTTP {(int)response.StatusCode}，未确认出口");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] buffer = new byte[8193]; int count = 0;
            while (count < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
                if (read == 0) break;
                count += read;
            }
            if (count == buffer.Length) return new("fail", "检测服务返回内容过大，未确认出口");
            string text = Encoding.UTF8.GetString(buffer, 0, count);
            string ip = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("ip=", StringComparison.Ordinal))?[3..] ?? "";
            if (!IPAddress.TryParse(ip, out _)) return new("warning", "代理请求已完成，但检测服务没有返回有效出口 IP");
            cancellation.ThrowIfCancellationRequested();
            return new("pass", $"代理连接与访问通过 · 出口 {ip} · 总耗时 {timer.ElapsedMilliseconds} ms", ip);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new("fail", "检测超时，未确认住宅出口连通性"); }
        catch (BuildException ex) { return new("fail", ex.Message); }
        catch (HttpRequestException ex)
        {
            return new("fail", ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired ? "代理鉴权失败，请检查账号和密码"
                : ex.HttpRequestError == HttpRequestError.SecureConnectionError ? "TLS 握手或证书校验失败"
                : ex.HttpRequestError == HttpRequestError.ProxyTunnelError ? "代理握手或鉴权失败，请检查协议、账号和服务状态"
                : "无法通过该住宅访问检测服务，请检查地址、端口、凭据和网络");
        }
        catch (IOException) { return new("fail", "代理连接中断，未确认出口"); }
    }
}

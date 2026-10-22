using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record RemoteRuleCheck(bool Success, int RuleCount, string Message);

public static class RemoteRuleValidation
{
    public static async Task<RemoteRuleCheck> CheckAsync(string root, JsonObject source, JsonObject http, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var spec = Map(ExpandEnvironment(source.DeepClone()), "远程名单");
            if (!Uri.TryCreate(S(spec, "url"), UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https")
                return new(false, 0, "需要完整的 HTTP(S) URL");
            spec.Remove("file"); spec["allow_stale"] = false;
            var options = Map(ExpandEnvironment(http.DeepClone()), "HTTP 设置");
            options["timeout"] = Math.Clamp(I(options, "timeout", 15), 1, 30);
            using var fetch = new Fetcher(root, options, cancellation: cancellation, useCache: false);
            var result = await fetch.ReadAsync(spec, bytes =>
            {
                var rules = Rules.ParseRemote(bytes, spec);
                for (int i = 0; i < rules.Count; i++)
                    try { cancellation.ThrowIfCancellationRequested(); Rules.ValidateSyntax(S(rules[i])); }
                    catch (BuildException ex) { throw new BuildException($"第 {i + 1} 条规则：{ex.Message}"); }
                return rules;
            }, "URL");
            cancellation.ThrowIfCancellationRequested();
            int count = List(result.Value, "规则").Count;
            return new(true, count, $"可访问 · 格式通过 · {count} 条规则");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (BuildException ex) { return new(false, 0, ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { return new(false, 0, "URL、请求选项或规则格式无效"); }
    }
}

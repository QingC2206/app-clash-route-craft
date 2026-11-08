using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record RemoteRuleCheck(bool Success, int RuleCount, string Message, int SkippedCount = 0);

public static class RemoteRuleValidation
{
    public static async Task<RemoteRuleCheck> CheckAsync(string root, JsonObject source, JsonObject http, CancellationToken cancellation = default)
        => (await CheckBatchAsync(root, [source], http, cancellation: cancellation))[0];

    /// <summary>Fresh checks, four requests at most. Share connections and identical downloads only within this batch.</summary>
    public static async Task<RemoteRuleCheck[]> CheckBatchAsync(string root, IReadOnlyList<JsonObject> sources, JsonObject http,
        Func<int, RemoteRuleCheck, Task>? completed = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var results = new RemoteRuleCheck[sources.Count];
        async Task Report(int index, RemoteRuleCheck result)
        {
            cancellation.ThrowIfCancellationRequested();
            results[index] = result;
            if (completed is not null) await completed(index, result);
        }
        JsonObject options; HttpClient client;
        try
        {
            options = Map(ExpandEnvironment(http.DeepClone()), "HTTP 设置");
            options["timeout"] = Math.Clamp(I(options, "timeout", 15), 1, 30);
            client = Fetcher.CreateClient(options);
        }
        catch (Exception ex) when (Expected(ex))
        {
            for (int i = 0; i < sources.Count; i++) await Report(i, Failure(ex));
            return results;
        }
        using (client)
        using (var keys = new Fetcher(root, options, false, cancellation, false, client))
        {
            var groups = new Dictionary<string, List<(int Index, JsonObject Spec)>>();
            for (int i = 0; i < sources.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var spec = Map(ExpandEnvironment(sources[i].DeepClone()), "远程名单");
                    if (!Uri.TryCreate(S(spec, "url"), UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https")
                        throw new BuildException("需要完整的 HTTP(S) URL");
                    spec.Remove("file"); spec["allow_stale"] = false;
                    // Include URL, User-Agent and headers; parsing options remain per row.
                    string key = keys.CachedPath(spec);
                    if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                    group.Add((i, spec));
                }
                catch (Exception ex) when (Expected(ex)) { await Report(i, Failure(ex)); }
            }
            await Parallel.ForEachAsync(groups.Values, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation }, async (group, token) =>
            {
                byte[]? raw = null; RemoteRuleCheck? downloadFailure = null;
                try
                {
                    using var fetch = new Fetcher(root, options, false, token, false, client);
                    await fetch.ReadAsync(group[0].Spec, bytes => { raw = bytes; return null; }, "URL");
                }
                catch (Exception ex) when (Expected(ex)) { downloadFailure = Failure(ex); }
                foreach (var (index, spec) in group)
                {
                    token.ThrowIfCancellationRequested();
                    RemoteRuleCheck result;
                    try
                    {
                        if (downloadFailure is not null) result = downloadFailure;
                        else
                        {
                            var audit = new DomainRuleAudit();
                            var rules = Rules.ParseRemote(raw!, spec, audit, "URL 名单");
                            for (int i = 0; i < rules.Count; i++)
                                try { token.ThrowIfCancellationRequested(); Rules.ValidateSyntax(S(rules[i])); }
                                catch (BuildException ex) { throw new BuildException($"第 {i + 1} 条规则：{ex.Message}"); }
                            result = new(true, rules.Count, $"可访问 · {rules.Count} 条有效规则" + (audit.Skipped.Count == 0 ? " · 格式通过" : "\n" + string.Join("\n", audit.Skipped.OfType<JsonObject>().Select(DomainRuleAudit.Message))), audit.Skipped.Count);
                        }
                    }
                    catch (Exception ex) when (Expected(ex)) { result = Failure(ex); }
                    await Report(index, result);
                }
            });
        }
        return results;
    }
    static bool Expected(Exception ex) => ex is BuildException or ArgumentException or InvalidOperationException or FormatException;
    static RemoteRuleCheck Failure(Exception ex) => new(false, 0, ex is BuildException ? ex.Message : "URL、请求选项或规则格式无效");
}

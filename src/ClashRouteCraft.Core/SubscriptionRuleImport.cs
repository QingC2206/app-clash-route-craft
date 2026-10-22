using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record SubscriptionRuleSource(string Name, string? File, bool CanFetch, string Status);

/// <summary>Reuses request-bound local subscription rules without changing node snapshots or project inputs.</summary>
public static class SubscriptionRuleImport
{
    static JsonObject Source(WorkspaceSession session, int index) => Map(ExpandEnvironment(L(session.Settings, "sources")[index]), "订阅");
    static JsonObject Http(WorkspaceSession session) => Map(ExpandEnvironment(M(session.Settings, "http")), "http");
    static string RuleFile(WorkspaceSession session, Fetcher fetch, JsonObject source) =>
        Path.Combine(session.ProjectPath, ".cache", "subscription-rules", Path.GetFileNameWithoutExtension(fetch.CachedPath(source)) + ".yaml");
    static bool ContainsRules(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return false;
            return YamlData.Parse(File.ReadAllText(path)) is JsonObject doc && (doc["rules"] is JsonArray { Count: > 0 } || doc["payload"] is JsonArray { Count: > 0 });
        }
        catch (Exception ex) when (ex is BuildException or IOException or UnauthorizedAccessException) { return false; }
    }
    public static SubscriptionRuleSource Inspect(WorkspaceSession session, int index)
    {
        var source = Source(session, index); string name = S(source, "name"), snapshot = session.SnapshotPath(name);
        using var fetch = new Fetcher(session.ProjectPath, Http(session));
        var candidates = new List<string> { RuleFile(session, fetch, source), fetch.CachedPath(source) };
        bool changed = session.Subscriptions.UrlChanged(source);
        if (!changed) candidates.Add(snapshot);
        string? file = candidates.Where(ContainsRules).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        bool canFetch = ConfigurationValidation.HttpUrl(S(source, "url"));
        string status = file is not null ? $"已有规则文件 · {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}，可直接读取，无需联网。"
            : changed ? "订阅请求已变更，没有与当前请求匹配的规则文件。"
            : File.Exists(snapshot) ? "已有节点文件，但未包含可导入的规则。" : "尚未找到已拉取的规则文件。";
        return new(name, file, canFetch, status + (file is null ? canFetch ? " 点击“拉取规则并预览”，从此订阅拉取一次。" : " 请先在订阅页面填写有效 URL，或选择本地文件。" : ""));
    }
    public static async Task<JsonObject> ReadAsync(WorkspaceSession session, int index, bool fetchMissing, string? name = null, string mode = "auto", CancellationToken cancellation = default)
    {
        var info = Inspect(session, index); string title = name ?? info.Name + " 规则";
        if (info.File is not null)
            return ImportedRuleSets.Parse(await File.ReadAllBytesAsync(info.File, cancellation), title, "subscription", info.Name, mode);
        if (!fetchMissing) throw new BuildException(info.Status);
        if (!info.CanFetch) throw new BuildException("此订阅没有有效的 HTTP(S) URL");
        var source = Source(session, index);
        using var fetch = new Fetcher(session.ProjectPath, Http(session), cancellation: cancellation, useCache: false);
        // Always parse source rules in auto mode before caching; user input format affects only the preview.
        var loaded = await fetch.ReadAsync(source, bytes => ImportedRuleSets.Parse(bytes, title, "subscription", info.Name), "订阅规则");
        var set = Map(loaded.Value, "规则集");
        var bytes = YamlData.Bytes(O("rules", A(L(set, "rules").OfType<JsonObject>().Select(e => ImportedRuleSets.Rule(e, S(e, "source_action"))))));
        var preview = mode == "auto" ? set : ImportedRuleSets.Parse(bytes, title, "subscription", info.Name, mode);
        cancellation.ThrowIfCancellationRequested();
        FileBundle.AtomicWrite(RuleFile(session, fetch, source), bytes);
        return preview;
    }
}

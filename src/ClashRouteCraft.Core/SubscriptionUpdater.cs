using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record SubscriptionPullResult(string Name, int NodeCount, int EligibleCount);

/// <summary>Fetches a single source without generating output or saving unrelated project editors.</summary>
public static class SubscriptionUpdater
{
    public static async Task<JsonObject> DownloadDocumentAsync(Fetcher fetch, JsonObject source, string name, bool routing = true)
    {
        var loaded = await fetch.ReadAsync(source, bytes => Nodes.Document(bytes), name);
        var document = routing ? await SubscriptionBaseline.SnapshotAsync(Map(loaded.Value, name), fetch, loaded.Base, name)
            : O("proxies", await Nodes.CollectAsync(Map(loaded.Value, name), fetch, loaded.Base, name));
        var nodes = L(document, "proxies");
        if (nodes.Count == 0) throw new BuildException($"{name} 下载结果没有节点");
        ConfigurationValidation.CacheNodes(nodes);
        return document;
    }
    public static async Task<JsonArray> DownloadAsync(Fetcher fetch, JsonObject source, string name) => L(await DownloadDocumentAsync(fetch, source, name), "proxies");
    public static byte[] Payload(JsonArray nodes) => YamlData.Bytes(O("proxies", nodes), "# Local subscription snapshot; contains private credentials.\n");
    public static byte[] Payload(JsonObject document) => YamlData.Bytes(document, "# Complete local subscription snapshot; contains private credentials.\n");
    public static JsonArray Cached(string path, string name)
    {
        if (!File.Exists(path)) throw new BuildException("没有订阅缓存");
        var document = Nodes.Document(File.ReadAllBytes(path));
        if (M(document, "proxy-providers").Count > 0) throw new BuildException("缓存仍引用外部节点来源");
        var nodes = L(document, "proxies");
        if (nodes.Count == 0) throw new BuildException("订阅缓存没有节点");
        ConfigurationValidation.CacheNodes(nodes);
        return nodes;
    }
    public static async Task<SubscriptionPullResult> PullAsync(WorkspaceSession session, int index, CancellationToken cancellation = default)
    {
        if (session.IsReadOnly) throw new BuildException("Example 是只读示例，请先新增或复制项目");
        session.CheckSubscriptionSave();
        var raw = Map(L(session.Settings, "sources")[index], "订阅");
        var source = Map(ExpandEnvironment(raw), "订阅"); string name = FileName(Name(source["name"], "订阅"), "订阅名称");
        if (!ConfigurationValidation.HttpUrl(S(source, "url"))) throw new BuildException("请先填写完整的 HTTP(S) 订阅链接");
        var http = Map(ExpandEnvironment(M(session.Settings, "http")), "http");
        var selection = Map(ExpandEnvironment(M(session.Settings, "selection")), "selection");
        var selected = (JsonObject)source.DeepClone(); selected["enabled"] = true;
        ConfigurationValidation.Settings(O("sources", A(new JsonNode?[] { selected }), "selection", selection.DeepClone(), "http", http.DeepClone(), "validation", O()));
        string temporary = Path.Combine(Path.GetTempPath(), "homechain-pull-" + Guid.NewGuid().ToString("N"));
        string snapshot = session.SnapshotPath(name), expected = DependencySnapshot.FileHash(snapshot);
        string editor = session.Documents["settings.yaml"].Text, state = session.Subscriptions.Text;
        bool baseline = false;
        // A standalone pull must remain available while unrelated policy YAML is being edited.
        try { baseline = SubscriptionBaseline.Enabled(session.Policy); }
        catch (BuildException) { try { baseline = SubscriptionBaseline.Enabled(Map(YamlData.Parse(session.Documents["policy.yaml"].Baseline), "policy")); } catch (BuildException) { } }
        JsonObject document;
        try
        {
            using (var lease = WorkspaceLease.Acquire(session.Workspace))
            using (var fetch = new Fetcher(session.ProjectPath, Merge(http, O("cache_dir", temporary, "allow_stale", false, "prefer_cache", false, "strict_subscriptions", true)), cancellation: cancellation, useCache: false))
                document = await DownloadDocumentAsync(fetch, source, name, !baseline || SubscriptionBaseline.IsPrimary(session.Settings, name));
            cancellation.ThrowIfCancellationRequested();
            if (baseline && SubscriptionBaseline.IsPrimary(session.Settings, name)) SubscriptionBaseline.RequireRules(document, name);
            var nodes = L(document, "proxies");
            int eligible = baseline ? SubscriptionBaseline.Eligible(nodes, source, selection).Count : Nodes.Eligible(Nodes.Namespace(nodes, name), source, selection).Count;
            if (session.Documents["settings.yaml"].Text != editor || session.Subscriptions.Text != state) throw new BuildException("拉取期间订阅设置已修改，本次结果未保存，请重试");
            session.Subscriptions.MarkUpdated(raw, "download");
            try { session.SaveSubscriptions([(snapshot, Payload(document))], new Dictionary<string, string> { [snapshot] = expected }, cancellation); }
            catch { session.Subscriptions.RestorePending(state); throw; }
            return new(name, nodes.Count, eligible);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}

using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Local snapshot identity and provenance, independent of the subscription's display name.</summary>
public sealed class SubscriptionState
{
    public const string FileName = ".subscription-state.json";
    readonly JsonObject entries;
    public string? Baseline { get; private set; }
    readonly string project;
    SubscriptionState(string project, JsonObject entries, string? baseline) { this.project = project; this.entries = entries; Baseline = baseline; }
    public static SubscriptionState Load(string project, JsonObject? settings, bool tolerateInvalid = false)
    {
        string path = Path.Combine(project, FileName); string? text = File.Exists(path) ? File.ReadAllText(path) : null;
        JsonObject entries;
        try { entries = text is null ? new() : Map(JsonNode.Parse(text), "订阅关联记录"); }
        catch (System.Text.Json.JsonException) { throw new BuildException("订阅关联记录损坏，请从备份恢复 .subscription-state.json"); }
        var state = new SubscriptionState(project, entries, text);
        if (settings is not null) try { state.Reconcile(settings); } catch (BuildException) when (tolerateInvalid) { }
        return state;
    }
    public static SubscriptionState Empty(string project, JsonObject settings) { var state = new SubscriptionState(project, new(), null); state.Reconcile(settings); return state; }
    static JsonNode? RequestValue(JsonObject source, string key)
    {
        try { return ExpandEnvironment(source[key]); }
        catch (BuildException) when (!B(source, "enabled", true)) { return source[key]?.DeepClone(); }
    }
    static string UrlHash(JsonObject source) => Hash(S(RequestValue(source, "url")));
    static string RequestHash(JsonObject source) => Hash(Canonical(O("url", RequestValue(source, "url"), "user_agent", RequestValue(source, "user_agent"), "headers", RequestValue(source, "headers"))));
    public void Reconcile(JsonObject settings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in L(settings, "sources"))
        {
            var source = Map(raw, "订阅"); string name = S(source, "name"); Data.FileName(name, "订阅名称"); names.Add(name);
            if (Find(name) is null)
            {
                string file = name + ".yaml";
                if (entries.Any(x => S(x.Value as JsonObject ?? new(), "snapshot").Equals(file, StringComparison.OrdinalIgnoreCase))) file = "source-" + Guid.NewGuid().ToString("N") + ".yaml";
                entries[name] = O("snapshot", file, "url_hash", UrlHash(source), "request_hash", RequestHash(source), "origin", "existing");
            }
            else if (Find(name) is { } existing && !existing.ContainsKey("request_hash")) existing["request_hash"] = RequestHash(source);
        }
        foreach (string key in entries.Select(x => x.Key).Where(x => !names.Contains(x)).ToArray()) entries.Remove(key);
    }
    JsonObject? Find(string name) => entries.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
    public string SnapshotPath(string name)
    {
        string file = S(Find(name) ?? new(), "snapshot", Data.FileName(name) + ".yaml");
        Data.FileName(file, "订阅快照文件名");
        if (!file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) throw new BuildException("订阅快照文件名必须以 .yaml 结尾");
        return Path.Combine(project, "subscrip", file);
    }
    public bool UrlChanged(JsonObject source) => File.Exists(SnapshotPath(S(source, "name"))) && Find(S(source, "name")) is { } entry && (S(entry, "url_hash") != UrlHash(source) || entry.ContainsKey("request_hash") && S(entry, "request_hash") != RequestHash(source));
    public void Rename(string oldName, string newName)
    {
        var value = Find(oldName)?.DeepClone() ?? O("snapshot", oldName + ".yaml");
        string? key = entries.Select(x => x.Key).FirstOrDefault(x => x.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (key is not null) entries.Remove(key);
        entries[newName] = value;
    }
    public void Add(JsonObject source)
    {
        entries[S(source, "name")] = O("snapshot", "source-" + Guid.NewGuid().ToString("N") + ".yaml", "url_hash", UrlHash(source), "request_hash", RequestHash(source), "origin", "new");
    }
    public void MarkUpdated(JsonObject source, string origin)
    {
        string name = S(source, "name"); var entry = Find(name) ?? throw new BuildException("订阅关联不存在");
        entry["url_hash"] = UrlHash(source); entry["request_hash"] = RequestHash(source); entry["origin"] = origin; entry["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
    }
    public string Text => entries.ToJsonString(JsonOptions) + "\n";
    public byte[] Bytes => Encoding.UTF8.GetBytes(Text);
    public void CheckExternalChanges()
    {
        string path = Path.Combine(project, FileName);
        if ((File.Exists(path) ? File.ReadAllText(path) : null) != Baseline) throw new BuildException("订阅关联已被其他进程修改，请重新载入项目后保存");
    }
    public void Accept() => Baseline = Text;
    public void RestorePending(string text)
    {
        var restored = Map(JsonNode.Parse(text), "订阅关联记录"); entries.Clear();
        foreach (var entry in restored) entries[entry.Key] = entry.Value?.DeepClone();
    }
}

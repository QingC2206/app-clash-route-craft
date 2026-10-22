using System.Text;
using System.Text.Json.Nodes;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed class AppPreferences
{
    readonly string path;
    readonly JsonObject settings;
    public bool IsPortable { get; }
    public string ApplicationDirectory { get; }
    public AppPreferences(string? path = null, string? applicationDirectory = null)
    {
        IsPortable = path is null;
        ApplicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        this.path = path ?? Path.Combine(ApplicationDirectory, "settings", "preferences.json");
        string readPath = this.path;
        try { settings = File.Exists(readPath) ? JsonNode.Parse(File.ReadAllText(readPath)) as JsonObject ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { settings = new(); }
    }
    public string Workspace { get => S(settings, "workspace"); set => settings["workspace"] = value; }
    public string Theme { get => S(settings, "theme", "system"); set => settings["theme"] = value; }
    public bool FlowCollapsed { get => B(settings, "flow_collapsed"); set { settings["flow_collapsed"] = value; Save(); } }
    public string RecentProject(string workspace) => S(M(settings, "recent_projects"), Hash(Path.GetFullPath(workspace).ToUpperInvariant()));
    public void RememberProject(string workspace, string project)
    {
        if (settings["recent_projects"] is not JsonObject) settings["recent_projects"] = new JsonObject();
        M(settings, "recent_projects")[Hash(Path.GetFullPath(workspace).ToUpperInvariant())] = project; Save();
    }
    public string RecentPage { get => S(settings, "recent_page", "overview"); set { settings["recent_page"] = value; Save(); } }
    public bool OverrideValidation { get => B(settings, "override_validation"); set => settings["override_validation"] = value; }
    public ValidationPaths Paths(string workspace)
    {
        var defaults = CoreValidation.FindResources(workspace);
        return new(S(settings, "mihomo", defaults.Mihomo), S(settings, "geodata", defaults.DataDirectory), S(settings, "mc2_package", defaults.Mc2Package));
    }
    public void SetPaths(ValidationPaths paths) { settings["mihomo"] = paths.Mihomo; settings["geodata"] = paths.DataDirectory; settings["mc2_package"] = paths.Mc2Package; }
    JsonObject Confirmations(string scope)
    {
        if (settings["confirmations"] is not JsonObject) settings["confirmations"] = new JsonObject();
        var all = M(settings, "confirmations"); if (all[scope] is not JsonObject) all[scope] = new JsonObject(); return M(all, scope);
    }
    public bool MainConfirmed(string scope, string fingerprint) => fingerprint.Length > 0 && S(Confirmations(scope), "main") == fingerprint;
    public bool DnsConfirmed(string scope, string fingerprint) => fingerprint.Length > 0 && S(Confirmations(scope), "dns") == fingerprint;
    public void ConfirmMain(string scope, string fingerprint, bool confirmed) { Confirmations(scope)["main"] = confirmed ? fingerprint : ""; Save(); }
    public void ConfirmDns(string scope, string fingerprint, bool confirmed) { Confirmations(scope)["dns"] = confirmed ? fingerprint : ""; Save(); }
    public void Save() => FileBundle.AtomicWrite(path, Encoding.UTF8.GetBytes(settings.ToJsonString(JsonOptions)));
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using ClashRouteCraft.Core;
using ClashRouteCraft.Desktop;
using static ClashRouteCraft.Core.Data;

static class EntranceUiRegression
{
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
    static T Named<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(x => x.Name == name);
    public static void Run(string workspace, string output, Action<bool, string> check)
    {
        WorkspaceSession.CreateProject(workspace, "EntranceUi");
        var session = new WorkspaceSession(workspace, "EntranceUi"); var settings = session.Settings;
        settings["selection"] = O("include", "香港");
        Map(L(settings, "sources")[0], "s")["include"] = "Japan";
        session.Documents["settings.yaml"].Set(settings); session.Save();
        var window = new MainWindow(new AppPreferences(Path.Combine(workspace, "entrance-preferences.json")), workspace) { Width = 1220, Height = 850 };
        window.Show(); window.SelectProject("EntranceUi"); window.Navigate("sources"); Pump();
        check(Named<ComboBox>(window, "EntranceRegionPreset").SelectedIndex == 0 && !window.Session!.Dirty, "Existing filter changed on page load");
        string policy = Canonical(window.Session!.Policy), residence = Canonical(window.Session.Residences);
        Named<ComboBox>(window, "EntranceRegionPreset").SelectedIndex = 3; Pump();
        check(S(M(window.Session.Settings, "selection"), EntranceFilters.Key) == "us", "US option not stored");
        Named<Button>(window, "EditSource0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        check(!Named<TextBox>(window, "Sourceinclude0").IsEnabled && Named<TextBox>(window, "Sourceinclude0").Text == "Japan", "Preset destroyed original include or leaves dormant editor active");
        check(Canonical(window.Session.Policy) == policy && Canonical(window.Session.Residences) == residence, "Entrance change mutated rules or residences");
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "entrance-presets.png"));
        Named<ComboBox>(window, "EntranceRegionPreset").SelectedIndex = 2; Pump();
        check(S(M(window.Session.Settings, "selection"), EntranceFilters.Key) == "nearby-no-hk-mo", "HK exclusion option missing");
        Named<ComboBox>(window, "EntranceRegionPreset").SelectedIndex = 1; Pump();
        check(S(M(window.Session.Settings, "selection"), EntranceFilters.Key) == "nearby", "Nearby option missing");
        Named<ComboBox>(window, "EntranceRegionPreset").SelectedIndex = 0; Pump();
        check(Named<TextBox>(window, "Sourceinclude0").IsEnabled && Named<TextBox>(window, "Sourceinclude0").Text == "Japan", "Custom include not restored");
        window.Session.Save(); window.Close(); Pump();
        check(S(M(new WorkspaceSession(workspace, "EntranceUi").Settings, "selection"), EntranceFilters.Key) == "custom", "Selection did not persist");
    }
}

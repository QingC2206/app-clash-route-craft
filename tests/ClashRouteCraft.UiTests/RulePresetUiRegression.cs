using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClashRouteCraft.Core;
using ClashRouteCraft.Desktop;
using static ClashRouteCraft.Core.Data;

static class RulePresetUiRegression
{
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
    static T Named<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);
    static void Click(Window owner, string name) { Named<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    public static void Run(string workspace, string output, Action<bool, string> check)
    {
        WorkspaceSession.CreateProject(workspace, "ResidentialUi");
        var initial = new WorkspaceSession(workspace, "ResidentialUi");
        var settings = initial.Settings; settings["sources"] = A(); settings["validation"] = O(); initial.Documents["settings.yaml"].Set(settings);
        initial.SetResidences(A(new JsonNode?[] { O("name", "Home", "type", "socks5", "server", "127.0.0.1", "port", 1080) }));
        var policy = initial.Policy; policy["extra_rules_enabled"] = false; initial.Documents["policy.yaml"].Set(policy); initial.Save();
        var window = new MainWindow(new AppPreferences(Path.Combine(workspace, "presets-preferences.json")), workspace) { Width = 1220, Height = 850 };
        window.Show(); Pump(); window.SelectProject("ResidentialUi"); window.Navigate("policy"); Pump();
        check(!window.Session!.Dirty && Named<ComboBox>(window, "FinalAction").SelectedIndex == 0, "Standalone page changed project or lacks fallback");
        string original = Canonical(window.Session.Policy), originalSettings = Canonical(window.Session.Settings), residences = Canonical(window.Session.Residences);
        Click(window, "AddRulePresets"); var dialog = window.OwnedWindows.Single(x => x.IsVisible && x.Title == "添加规则预设"); Pump();
        check(!Named<Button>(dialog, "ConfirmRulePresets").IsEnabled, "Empty selection can apply");
        Named<CheckBox>(dialog, "Preset_ai-common").IsChecked = true; Pump();
        check(Named<TextBlock>(dialog, "PresetStatus").Text!.Contains("4 个 URL"), "Preset preview missing URLs");
        using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "rule-presets.png"));
        Click(dialog, "CancelRulePresets"); check(Canonical(window.Session.Policy) == original, "Cancel mutated policy");
        Click(window, "AddRulePresets"); dialog = window.OwnedWindows.Single(x => x.IsVisible && x.Title == "添加规则预设");
        var file = RulePresets.FilePath(workspace); string factory = File.ReadAllText(file);
        var custom = O("version", 1, "presets", A(new JsonNode?[] { O("id", "local", "category", "文件更新", "name", "本地预设", "urls", A("https://example.com/rules.yaml"), "domains", A("local.example"), "rules", A("DOMAIN,api.example")) }));
        File.WriteAllBytes(file, YamlData.Bytes(custom)); Click(dialog, "ReloadRulePresets");
        Named<TextBox>(dialog, "PresetSearch").Text = "本地"; Pump();
        Named<CheckBox>(dialog, "Preset_local").IsChecked = true; Named<ComboBox>(dialog, "PresetTarget").SelectedIndex = 1;
        Named<CheckBox>(dialog, "PresetEnableExtras").IsChecked = true; Pump(); Click(dialog, "ConfirmRulePresets");
        check(L(window.Session.Policy, "direct").Count == 2 && Rules.RemoteSpecs(window.Session.Policy["remote_rules"]).Count() == 1 && B(window.Session.Policy, "extra_rules_enabled"), "Confirmed append did not apply to direct group");
        check(Canonical(window.Session.Settings) == originalSettings && Canonical(window.Session.Residences) == residences && window.Session.Dirty, "Append changed nodes/settings or auto-saved");
        Click(window, "AddRulePresets"); dialog = window.OwnedWindows.Single(x => x.IsVisible && x.Title == "添加规则预设");
        Named<CheckBox>(dialog, "Preset_local").IsChecked = true; Named<ComboBox>(dialog, "PresetTarget").SelectedIndex = 1; Pump();
        check(Named<TextBlock>(dialog, "PresetStatus").Text!.Contains("跳过 3 项"), "Duplicate preview missing");
        File.WriteAllText(file, "version: 999\npresets: []\n"); Click(dialog, "ReloadRulePresets");
        check(!Named<Button>(dialog, "ConfirmRulePresets").IsEnabled && Named<TextBlock>(dialog, "PresetStatus").Text!.Contains("加载失败"), "Invalid catalogue could apply stale selection");
        Click(dialog, "CancelRulePresets"); File.WriteAllText(file, factory);
        window.Session.Save(); window.Navigate("residential"); Pump();
        check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("仅生成“[直连]") == true), "Standalone residence instructions missing");
        window.Close(); Pump();
    }
}

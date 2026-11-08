using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ClashRouteCraft.Core;
using ClashRouteCraft.Desktop;
using static ClashRouteCraft.Core.Data;

static class ResidenceCopyUiRegression
{
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
    static T Named<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(x => x.Name == name);
    public static void Run(string workspace, string output, Action<bool, string> check)
    {
        WorkspaceSession.CreateProject(workspace, "ResidenceCopy");
        var session = new WorkspaceSession(workspace, "ResidenceCopy");
        var original = O("name", "Example", "type", "socks5", "server", "proxy.example.com", "port", 12345,
            "username", "demo", "password", "synthetic-secret", "udp", true, "tls", true, "custom", O("nested", "kept"));
        var collision = original.DeepClone(); collision["name"] = "example-copy-2";
        session.SetResidences(A(new[] { original, collision })); session.Save();
        var window = new MainWindow(new AppPreferences(Path.Combine(workspace, "copy-preferences.json")), workspace) { Width = 1220, Height = 850 };
        window.Show(); window.SelectProject("ResidenceCopy"); window.Navigate("residential"); Pump();
        string policy = Canonical(window.Session!.Policy), settings = Canonical(window.Session.Settings);
        check(Named<Button>(window, "EditResidence0").Content is Avalonia.Controls.Shapes.Path && AutomationProperties.GetName(Named<Button>(window, "EditResidence0")) == "编辑住宅", "Edit icon lacks accessible description");
        check(Named<Button>(window, "CopyResidence0").Content is Avalonia.Controls.Shapes.Path && AutomationProperties.GetName(Named<Button>(window, "CopyResidence0")) == "复制住宅", "Copy icon lacks accessible description");
        void Copy(int index) { Named<Button>(window, "CopyResidence" + index).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        Copy(0);
        var cloned = Map(window.Session.Residences[1]!.DeepClone(), "copy");
        check(S(cloned, "name") == "Example-Copy", "First copy name incorrect");
        cloned["name"] = "Example";
        check(Canonical(cloned) == Canonical(original), "Copy lost protocol fields or credentials");
        Copy(1);
        check(S(Map(window.Session.Residences[2], "copy"), "name") == "Example-Copy-3", "Copy suffix nesting or case-insensitive collision");
        Copy(2);
        check(S(Map(window.Session.Residences[3], "copy"), "name") == "Example-Copy-4", "Numbered copy suffix nesting");
        Named<TextBox>(window, "ResidenceServer3").Text = "changed.example.com"; Pump();
        check(S(Map(window.Session.Residences[0], "original"), "server") == "proxy.example.com" && S(Map(window.Session.Residences[2], "copy"), "server") == "proxy.example.com", "Editing clone changed original or sibling");
        check(Canonical(window.Session.Policy) == policy && Canonical(window.Session.Settings) == settings, "Copy changed rules, subscriptions or DNS");
        check(window.Session.Dirty && new WorkspaceSession(workspace, "ResidenceCopy").Residences.Count == 2, "Copy wrote project before save");
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "residence-copy.png"));
        window.Session.Save(); window.Close(); Pump();
        check(new WorkspaceSession(workspace, "ResidenceCopy").Residences.Count == 5, "Copies did not persist after save");
    }
}

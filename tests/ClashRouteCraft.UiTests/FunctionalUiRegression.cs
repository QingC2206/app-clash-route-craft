using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClashRouteCraft.Core;
using ClashRouteCraft.Desktop;
using static ClashRouteCraft.Core.Data;

static class FunctionalUiRegression
{
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); }
    static void Until(Func<bool> done)
    {
        for (int i = 0; i < 800 && !done(); i++) { Pump(); Thread.Sleep(5); }
        Pump(); if (!done()) throw new Exception("Functional UI operation timed out");
    }
    static Button Named(MainWindow window, string name) => window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == name);
    static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    public static void Run(string workspace, string output, Action<bool, string> check)
    {
        WorkspaceSession.CreateProject(workspace, "Functional");
        var session = new WorkspaceSession(workspace, "Functional");
        var settings = session.Settings; settings["validation"] = O(); session.Documents["settings.yaml"].Set(settings);
        var residences = A(new JsonNode?[] { O("name", "住宅-1", "type", "socks5", "server", "127.0.0.1", "port", 1) });
        session.SetResidences(residences);
        var policy = DnsSettings.ApplyRecommended(session.Policy); policy["rule_source"] = "custom"; policy["domestic_direct"] = false; policy["geosite_sources"] = O(); policy["first"] = A(new JsonNode?[] { O("match", "DOMAIN,rename.example", "action", "住宅-1") });
        M(policy, "dns")["nameserver"] = A("https://1.1.1.1/dns-query#住宅-1"); session.Documents["policy.yaml"].Set(policy); session.Save();
        File.WriteAllBytes(session.SnapshotPath("AirportA"), YamlData.Bytes(O("proxies", A(new JsonNode?[] { O("name", "香港", "type", "http", "server", "localhost", "port", 8080) }))));
        var built = Task.Run(() => new BuildEngine().BuildProjectAsync(workspace, "Functional", new(Offline: true))).GetAwaiter().GetResult();
        built.Report["core_validation"] = "passed"; File.WriteAllText(Path.Combine(session.OutputPath, "HomeChain.report.json"), built.Report.ToJsonString());
        var window = new MainWindow(new AppPreferences(Path.Combine(workspace, "functional-preferences.json")), workspace) { Width = 1220, Height = 850 };
        window.Show(); Pump(); window.SelectProject("Functional"); window.Navigate("report"); Pump();
        var copy = window.GetVisualDescendants().OfType<Button>().Single(x => x.Content?.ToString() == "复制主配置全文");
        check(copy.IsEnabled, "Valid output copy was disabled");
        window.Clipboard!.SetTextAsync("unchanged-clipboard").GetAwaiter().GetResult();
        File.AppendAllText(built.OutputPath, "# external change");
        Click(copy);
        check(Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard).GetAwaiter().GetResult() == "unchanged-clipboard", "Copy read externally modified unvalidated output");
        check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("重新生成并校验后再复制") == true), "Blocked copy did not explain how to recover");
        window.Navigate("residential"); Pump(); Click(Named(window, "EditResidence0")); Click(Named(window, "ResidenceName0"));
        Until(() => window.OwnedWindows.Any(x => x.Title == "重命名住宅"));
        var dialog = window.OwnedWindows.Single(x => x.Title == "重命名住宅");
        dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "改名住宅";
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Content?.ToString() == "保存名称"));
        check(S(Map(window.Session!.Residences[0], "r"), "name") == "改名住宅", "Residence rename did not reach the model");
        check(S(Map(L(window.Session.Policy, "first")[0], "r"), "action") == "改名住宅"
            && Uri.UnescapeDataString(S(L(M(window.Session.Policy, "dns"), "nameserver")[0])).EndsWith("#改名住宅"), "Residence rename did not update rules and DNS");
        var nodes = window.Session.Residences; Map(nodes[0], "r")["name"] = "住宅-1";
        var third = nodes[0]!.DeepClone(); Map(third, "r")["name"] = "住宅-3"; nodes.Push(third); window.Session.SetResidences(nodes);
        window.Navigate("residential"); Pump(); Click(Named(window, "AddResidence"));
        check(window.Session.Residences.Select(x => S(Map(x, "r"), "name")).SequenceEqual(["住宅-1", "住宅-3", "住宅-2"]), "Residence add reused an occupied name");
        // A reserved loopback port provides a deterministic connection failure without requesting a real service.
        using var held = new ProbeHoldServer();
        nodes = window.Session.Residences; var first = Map(nodes[0], "r"); first["type"] = "http"; first["port"] = held.Port; window.Session.SetResidences(nodes);
        window.Navigate("residential"); Pump();
        var probe = Named(window, "CheckResidence0"); var flyout = (Flyout)FlyoutBase.GetAttachedFlyout(probe)!;
        string Text() => string.Join(" ", ((Control)flyout.Content!).GetLogicalDescendants().OfType<TextBlock>().Select(x => x.Text));
        Click(probe); Until(() => held.Requests > 0);
        check(Text().Contains("正在检测") && probe.IsEnabled, "Probe has no pending feedback or cancel action");
        Click(probe); Until(() => Text().Contains("已取消"));
        check(!((Control)flyout.Content!).GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "pass")), "Canceled probe reported success");
        Click(probe); Until(() => held.Requests > 1);
        nodes = window.Session.Residences; Map(nodes[0], "r")["port"] = 1; window.Session.SetResidences(nodes);
        Click(probe); Until(() => Text().Contains("配置已修改"));
        check(Text().Contains("重新检测"), "Probe kept a result for edited parameters");
        // Use an unsupported protocol to render the same deterministic warning in both themes.
        nodes = window.Session.Residences; Map(nodes[0], "r")["type"] = "ss"; window.Session.SetResidences(nodes);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme; window.Navigate("residential"); Pump();
            var edit = Named(window, "EditResidence2"); if (edit.Content?.ToString() == "收起") Click(edit);
            probe = Named(window, "CheckResidence0"); flyout = (Flyout)FlyoutBase.GetAttachedFlyout(probe)!;
            Click(probe); Until(() => Text().Contains("暂不支持"));
            check(((Control)flyout.Content!).GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "warning")), "Unsupported probe did not use warning state");
            check(!((Control)flyout.Content!).GetLogicalDescendants().OfType<ScrollViewer>().Any(), "Probe results added a nested scrollbar");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, theme == ThemeVariant.Dark ? "dark-residence-probe.png" : "light-residence-probe.png"));
            // Native popup has its own host; record it separately as well.
            foreach (var popup in window.GetVisualDescendants().OfType<Popup>())
                if (popup.IsOpen && popup.Child is Control child)
                {
                    var top = TopLevel.GetTopLevel(child);
                    if (top is not null) { using var popupFrame = top.CaptureRenderedFrame(); popupFrame?.Save(Path.Combine(output, theme == ThemeVariant.Dark ? "dark-residence-probe-popup.png" : "light-residence-probe-popup.png")); }
                }
            flyout.Hide();
        }
        window.Navigate("policy"); Pump();
        var moreRules = window.GetVisualDescendants().OfType<Expander>().Single(x => x.Name == "MoreRules_reject");
        moreRules.IsExpanded = true; Pump();
        var ruleInput = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "Manual_reject");
        ruleInput.Text = "{match:"; Pump();
        check(L(window.Session.Policy, "reject").Any(x => S(x) == "{match:"), "Incomplete explicit rule edit was lost or threw");
        ruleInput.Text = @"{match: 'DOMAIN-REGEX,^foo,bar$|^blocked\.example$'}"; Pump();
        var explicitRule = L(window.Session.Policy, "reject").OfType<JsonObject>().Single();
        check(Rules.WithAction(explicitRule, "REJECT") == @"DOMAIN-REGEX,^foo,bar$|^blocked\.example$,REJECT", "Explicit regex input was not parsed correctly");
        window.Navigate("residential"); window.Navigate("policy"); Pump();
        check(L(window.Session.Policy, "reject").OfType<JsonObject>().Count() == 1, "Navigation rewrote the explicit regex object");
        window.Session.Save(); window.Close(); Pump();
        check(!window.IsVisible, "Functional test window did not close");
    }
}

sealed class ProbeHoldServer : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    public int Requests;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public ProbeHoldServer() { listener.Start(); _ = Run(); }
    async Task Run()
    {
        while (!stop.IsCancellationRequested)
            try { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Hold(client); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { break; }
    }
    async Task Hold(TcpClient client)
    {
        using (client)
            try { Interlocked.Increment(ref Requests); await Task.Delay(15000, stop.Token); }
            catch (OperationCanceledException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClashRouteCraft.Core;
using ClashRouteCraft.Desktop;
using static ClashRouteCraft.Core.Data;

static class AsnUiRegression
{
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); }
    static Button Button(MainWindow window, string name) => window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == name);
    static CheckBox Box(MainWindow window, string name) => window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == name);
    static void Click(MainWindow window, string name) { Button(window, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump(); }
    static void Wait(MainWindow window)
    {
        for (int i = 0; i < 1600 && window.IsBusy; i++) { Pump(); Thread.Sleep(5); }
        Pump(); if (window.IsBusy) throw new Exception("ASN UI task timed out");
    }
    public static void Run(string workspace, string output, Action<bool, string> check)
    {
        WorkspaceSession.CreateProject(workspace, "AsnUi"); var session = new WorkspaceSession(workspace, "AsnUi");
        var settings = session.Settings; settings["validation"] = O(); session.Documents["settings.yaml"].Set(settings);
        session.Documents["policy.yaml"].Set(O("rule_source", "subscription", "extra_rules_enabled", true));
        session.SetResidences(A(new JsonNode?[] { O("name", "Home", "type", "socks5", "server", "127.0.0.1", "port", 9000) })); session.Save();
        File.WriteAllBytes(session.SnapshotPath("AirportA"), YamlData.Bytes(O("proxies", A(new JsonNode?[] { O("name", "香港", "type", "http", "server", "127.0.0.1", "port", 8080) }), "proxy-groups", A(new JsonNode?[] { O("name", "Original", "type", "select", "proxies", A("香港")) }), "rules", A("MATCH,Original"))));
        var original = Task.Run(() => new BuildEngine().BuildProjectAsync(workspace, "AsnUi", new(Offline: true))).GetAwaiter().GetResult(); byte[] before = File.ReadAllBytes(original.OutputPath);
        var window = new MainWindow(new AppPreferences(Path.Combine(workspace, "asn-preferences.json")), workspace) { Width = 1220, Height = 850 };
        window.Show(); Pump(); window.SelectProject("AsnUi");
        try
        {
            string savedSettings = session.Documents["settings.yaml"].Text, savedNodes = session.Documents["residential.yaml"].Text;
            var source = O("name", "通用规则来源", "file", "asn.list", "action", "REJECT"); string id = AsnPolicy.RemoteId(source);
            File.WriteAllText(Path.Combine(session.ProjectPath, "asn.list"), "IP-ASN,64512\nDOMAIN,ad.example\n");
            var policy = window.Session!.Policy; policy["remote_rules"] = A(new JsonNode?[] { source }); window.Session.Documents["policy.yaml"].Set(policy);
            window.Navigate("build"); Pump(); Click(window, "GeneratePrimary"); Wait(window);
            check(File.ReadAllBytes(original.OutputPath).SequenceEqual(before), "Blocked ASN build changed output");
            check(Button(window, "ResolveAsnDependencies").IsVisible, "Blocked generation has no action to resolve sources");
            Click(window, "ResolveAsnDependencies");
            check(!Box(window, "AllowNativeAsn").IsChecked.GetValueOrDefault(), "Native ASN enabled by default");
            check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("通用规则来源") == true), "Source provenance missing from UI");
            if (window.GetVisualDescendants().OfType<Button>().Any(x => x.Name == "ToggleFlow")) Click(window, "ToggleFlow");
            window.GetVisualDescendants().OfType<Control>().Single(x => x.Name == "AsnPolicyCard").BringIntoView(); Pump();
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "asn-blocked-sources.png"));
            Box(window, "ExcludeAsnSource").IsChecked = true; Pump();
            check(AsnPolicy.Excludes(window.Session.Policy, id), "Per-source checkbox did not update draft");
            Click(window, "InspectAsnSources"); Wait(window);
            check(File.ReadAllBytes(original.OutputPath).SequenceEqual(before), "Inspection replaced previous output");
            check(S(window.Session.LastAsnAnalysis!, "status") == "no_asn_dependency", "Excluded source still blocked");
            check(AsnPolicy.Excludes(new WorkspaceSession(workspace, "AsnUi").Policy, id), "Source exclusion was not persisted");
            window.Navigate("build"); Pump(); Click(window, "GeneratePrimary"); Wait(window);
            check(!GeoRules.RequiredDatabases(Map(YamlData.Read(original.OutputPath), "output")).Contains("ASN.mmdb"), "Excluded ASN reached output");
            window.Navigate("policy"); Pump(); Box(window, "ExcludeAsnSource").IsChecked = false; Pump(); Box(window, "AllowNativeAsn").IsChecked = true; Pump();
            window.Navigate("build"); Pump(); Click(window, "GeneratePrimary"); Wait(window);
            check(B(M(window.Session.ReadReport()!, "asn"), "native_required"), "Advanced retention lost ASN");
            window.Navigate("report"); Pump();
            check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("目标 ASN：需准备 ASN.mmdb") == true), "Output hides target dependency behind local success");
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "asn-retained-output.png"));
            check(File.ReadAllText(Path.Combine(session.ProjectPath, "settings.yaml")) == savedSettings && File.ReadAllText(Path.Combine(session.ProjectPath, "residential.yaml")) == savedNodes, "ASN controls modified subscriptions or nodes");
        }
        finally { window.Close(); }
    }
}

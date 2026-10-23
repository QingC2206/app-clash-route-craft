using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClashRouteCraft.Core;
using ClashRouteCraft.Desktop;
using static ClashRouteCraft.Core.Data;

// Render real application views from disposable, wholly synthetic data.
// No user workspace, credentials, network requests or simulated validation success.
static class ReadmeScreenshots
{
    public static void Run(string workspace, string output)
    {
        const string project = "演示项目 Demo";
        WorkspaceSession.CreateProject(workspace, project);
        var session = new WorkspaceSession(workspace, project);
        var settings = session.Settings;
        settings["validation"] = O();
        settings["sources"] = A(new JsonNode?[] {
            O("name", "示例订阅 A", "url", "https://subscription-a.example.invalid/clash", "enabled", true),
            O("name", "示例订阅 B", "url", "https://subscription-b.example.invalid/clash", "enabled", true) });
        session.Documents["settings.yaml"].Set(settings);
        session.SetResidences(A(new JsonNode?[] {
            O("name", "住宅示例", "type", "socks5", "server", "192.0.2.10", "port", 1080, "username", "demo-user", "password", "example-only", "udp", true) }));
        var policy = session.Policy;
        policy["rule_source"] = "subscription";
        policy["order"] = A("direct", "proxy", "reject");
        policy["direct"] = A("DOMAIN-SUFFIX,local.example", "DOMAIN-SUFFIX,media.example");
        policy["proxy"] = A("DOMAIN-SUFFIX,global.example");
        policy["reject"] = A("DOMAIN-SUFFIX,ads.example");
        policy["remote_rules"] = A();
        policy["dns_privacy"] = O("enabled", false, "proxy_exit", "residential");
        session.Documents["policy.yaml"].Set(policy);
        session.Save();
        foreach (string source in new[] { "示例订阅 A", "示例订阅 B" })
        {
            var nodes = A(new JsonNode?[] {
                O("name", "香港示例", "type", "http", "server", "192.0.2.20", "port", 8080),
                O("name", "日本示例", "type", "http", "server", "192.0.2.21", "port", 8080),
                O("name", "新加坡示例", "type", "http", "server", "192.0.2.22", "port", 8080) });
            var doc = O("proxies", nodes, "proxy-groups", A(new JsonNode?[] {
                O("name", "订阅代理", "type", "select", "proxies", A("香港示例", "日本示例", "新加坡示例")) }),
                "rules", A("DOMAIN-SUFFIX,domestic.example,DIRECT", "DOMAIN-SUFFIX,blocked.example,REJECT", "MATCH,订阅代理"));
            File.WriteAllBytes(session.SnapshotPath(source), YamlData.Bytes(doc));
        }
        Task.Run(() => new BuildEngine().BuildProjectAsync(workspace, project, new(Offline: true))).GetAwaiter().GetResult();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var window = new MainWindow(new AppPreferences(Path.Combine(workspace, "demo-preferences.json")), workspace) { Width = 1280, Height = 900 };
        window.Show(); Pump(); window.SelectProject(project); Pump();
        try
        {
            void Click(string name) { window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
            void Capture(string name)
            {
                Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                using var frame = window.CaptureRenderedFrame();
                if (frame is null) throw new Exception("Missing README screenshot: " + name);
                frame.Save(Path.Combine(output, name + ".png"));
            }
            window.Navigate("overview"); Capture("overview");
            window.Navigate("sources"); Pump(); Click("EditSource0"); Capture("subscriptions");
            window.Navigate("residential"); Pump(); Click("EditResidence0"); Capture("residential");
            window.Navigate("policy"); Pump(); Capture("routing");
            Click("ToggleFlow");
            window.Height = 1100; Pump();
            // Keep real controls and their ordering intact; scroll within the tab only.
            var groups = window.GetVisualDescendants().OfType<StackPanel>().Single(x => x.Name == "RemoteRuleGroups");
            foreach (var expander in groups.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = false;
            groups.GetVisualDescendants().OfType<Expander>().First(x => x.Header?.ToString()?.StartsWith("域名及其子域名") == true).IsExpanded = true;
            foreach (string group in new[] { "direct", "proxy", "reject" }) Click("CollapseUrls_" + group);
            var extra = window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "EnableExtraRules");
            Pump();
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "PageScroll");
            var offset = extra.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
            scroll.Offset = new Vector(0, scroll.Offset.Y + offset);
            Pump(); Capture("custom-rules");
            window.Height = 900; Pump();
            Click("PolicyDnsTab");
            window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "DnsPrivacyEnabled").IsChecked = true;
            Pump(); Capture("dns-privacy");
            window.Session!.Save();
            Task.Run(() => new BuildEngine().BuildProjectAsync(workspace, project, new(Offline: true))).GetAwaiter().GetResult();
            window.SelectProject(project); window.Navigate("report"); Capture("output");
        }
        finally { window.Close(); Pump(); }
        Console.WriteLine("PASS 7 README screenshots rendered from isolated synthetic fixtures; no router or subscription contacted.");
    }
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); }
}

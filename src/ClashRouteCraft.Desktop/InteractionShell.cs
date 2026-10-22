using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    int expandedSource = -1, expandedResidence = -1;
    bool sourceAdvanced, dnsAdvanced;
    string OutputState()
    {
        if (session is null) return "未选择项目";
        if (IsBusy) return pullingSource >= 0 ? "正在拉取订阅" : "正在处理";
        if (session.Dirty) return "未保存 · 输出待更新";
        try { return session.OutputStatus() switch { OutputFreshness.Current => "已生成 · 可查看输出", OutputFreshness.Missing => "尚未生成", OutputFreshness.ValidationChanged => "校验资源已变化 · 待重新校验", _ => "配置已保存 · 输出待更新" }; }
        catch (Exception ex) when (ex is IOException or BuildException) { return "输出状态需要检查"; }
    }
    IReadOnlyList<ConfigurationIssue> Issues(bool refresh = false, bool dependencies = false, bool autoFetch = true)
    {
        if (session is null) return [];
        try { return ProjectReadiness.Inspect(session, refresh, null, dependencies, autoFetch); }
        catch (Exception ex) when (ex is IOException or BuildException or ArgumentException) { return [new("yaml", "", SafeMessage(ex))]; }
    }
    bool MissingSnapshots()
    {
        if (session is null) return false;
        try { return L(session.Settings, "sources").Select(x => Map(x, "订阅")).Any(x => B(x, "enabled", true) && !File.Exists(session.SnapshotPath(S(x, "name")))); }
        catch (BuildException) { return false; }
    }
    bool ChangedSourceUrls()
    {
        if (session is null) return false;
        try { return L(session.Settings, "sources").Select(x => Map(x, "订阅")).Any(x => B(x, "enabled", true) && session.Subscriptions.UrlChanged(x)); }
        catch (BuildException) { return false; }
    }
    Control GenerationActions()
    {
        var primary = AsyncButton("生成配置", StartBuildAsync, true); primary.Name = "GeneratePrimary";
        primary.IsEnabled = !IsBusy && session?.IsReadOnly != true;
        return primary;
    }
    Control IssuesCard(IReadOnlyList<ConfigurationIssue> issues)
    {
        if (issues.Count == 0) return new Border { IsVisible = false };
        var rows = new StackPanel { Spacing = 8 };
        foreach (var issue in issues)
        {
            var fix = Button("去修复", () => OpenIssue(issue)); fix.Tag = "fix:" + issue.Field;
            var line = new Grid { ColumnDefinitions = new("*,Auto") }; line.Children.Add(Label(issue.Message)); line.Children.Add(fix); Grid.SetColumn(fix, 1); rows.Children.Add(line);
        }
        return Card($"还有 {issues.Count} 项需要处理", rows);
    }
    void OpenIssue(ConfigurationIssue issue)
    {
        if (IsBusy && issue.Page is not "build" and not "report") { SetStatus("生成结束后可修复配置"); return; }
        var match = System.Text.RegularExpressions.Regex.Match(issue.Field, @"\d+$");
        if (match.Success)
        {
            int index = int.Parse(match.Value);
            if (issue.Page == "sources") { expandedSource = index; sourceAdvanced = true; }
            if (issue.Page == "residential") expandedResidence = index;
        }
        if (issue.Page == "policy") { policyTab = issue.Field.StartsWith("Dns_") ? "dns" : "rules"; dnsAdvanced = issue.Field.StartsWith("Dns_"); policyFocusField = issue.Field; }
        if (issue.Page == "yaml" && session?.Documents.ContainsKey(issue.Field) == true) yamlFile = issue.Field;
        Navigate(issue.Page);
        Dispatcher.UIThread.Post(() =>
        {
            var field = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.Name == issue.Field);
            field?.BringIntoView(); field?.Focus();
        }, DispatcherPriority.Loaded);
    }
    void NavigateOutput(string file)
    {
        previewFile = file; Navigate("report");
    }
    void RefreshFlow()
    {
        bool restoreFocus = (FocusManager?.GetFocusedElement() as Control)?.Name?.StartsWith("Flow_") == true;
        bool shown = session is not null && CurrentPage is not "projects" and not "settings" and not "yaml";
        flowHost.IsVisible = shown;
        if (!shown) { flowHost.Content = null; return; }
        var title = new Grid { ColumnDefinitions = new("*,Auto") };
        title.Children.Add(Label("配置生成关系 · 点击节点进入对应页面", true, 12));
        var toggle = Button(preferences.FlowCollapsed ? "展开关系图" : "收起", () => { preferences.FlowCollapsed = !preferences.FlowCollapsed; RefreshFlow(); }); toggle.Name = "ToggleFlow"; toggle.MinHeight = 24; toggle.Padding = new(8, 2);
        title.Children.Add(toggle); Grid.SetColumn(toggle, 1);
        if (preferences.FlowCollapsed) { flowHost.Content = title; return; }
        var issues = Issues();
        string Summary(string page, string ready) => issues.Count(x => x.Page == page) is > 0 and var count ? $"! {count} 项待填写" : ready;
        string countSources = "待配置", countResidences = "待配置";
        try { countSources = L(session!.Settings, "sources").Count(x => B(Map(x, "订阅"), "enabled", true)) + " 个已启用"; countResidences = session.Residences.Count + " 个出口"; } catch (BuildException) { }
        var diagram = new FlowGrid { ColumnDefinitions = new("*,22,1.25*,22,1.1*,22,1.15*"), RowDefinitions = new("44,4,44,4,44"), LineBrush = Line, Name = "ConfigurationFlow" };
        Button Node(string id, string name, string detail, int column, int row, int span, bool selected, Action click, string page)
        {
            var content = new StackPanel { Spacing = 2, Children = { Label(name, size: 13), Label(detail, true, 11) } };
            var button = Button("", click); button.Content = content; button.Name = "Flow_" + id; button.Tag = selected ? "selected" : "idle";
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.VerticalAlignment = VerticalAlignment.Center;
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Padding = new(8, 4); button.MinHeight = 42;
            button.Background = selected ? Active : Surface; button.BorderBrush = selected ? Accent : Line; button.BorderThickness = new(selected ? 2 : 1);
            button.IsEnabled = !IsBusy || page is "build" or "report";
            AutomationProperties.SetName(button, name + "，" + detail + (selected ? "，当前页面" : ""));
            Grid.SetColumn(button, column); Grid.SetRow(button, row); Grid.SetRowSpan(button, span); diagram.Children.Add(button); return button;
        }
        var project = Node("overview", "项目概览", issues.Count == 0 ? "基础配置就绪" : "待完善", 0, 0, 5, CurrentPage == "overview", () => Navigate("overview"), "overview");
        var sources = Node("sources", "机场订阅", Summary("sources", ChangedSourceUrls() ? "链接已改 · 待更新" : countSources), 2, 0, 1, CurrentPage == "sources", () => Navigate("sources"), "sources");
        var residence = Node("residential", "住宅出口", Summary("residential", countResidences), 2, 2, 1, CurrentPage == "residential", () => Navigate("residential"), "residential");
        var policy = Node("policy", "规则与 DNS", Summary("policy", "可选调整"), 2, 4, 1, CurrentPage == "policy", () => Navigate("policy"), "policy");
        var build = Node("build", "校验与生成", IsBusy ? taskStage : issues.Count > 0 ? "先完成基础配置" : "可以生成", 4, 0, 5, CurrentPage == "build", () => Navigate("build"), "build");
        bool outputSelected = CurrentPage == "report";
        string output = session!.Dirty ? "待更新" : session.OutputIsCurrent() ? "已生成" : session.ReadReport() is null ? "尚未生成" : "待更新";
        var main = Node("main", "主配置输出", output, 6, 0, 2, outputSelected && previewFile != "HomeChain.MC2DNS.yaml", () => NavigateOutput("HomeChain.yaml"), "report");
        var dns = Node("dns", "DNS配置输出(MC2专用)", UsesDnsPrivacy() ? "增强 DNS · 查看应用与恢复" : UsesSubscriptionBaseline() ? "由客户端管理 · 可恢复默认" : ConfigurationValidation.Target(session.Settings) == "clash" ? "Clash 无需此文件" : output, 6, 3, 2, outputSelected && previewFile == "HomeChain.MC2DNS.yaml", () => NavigateOutput("HomeChain.MC2DNS.yaml"), "report");
        foreach (var input in new[] { sources, residence, policy }) { diagram.Connections.Add((project, input)); diagram.Connections.Add((input, build)); }
        diagram.Connections.Add((build, main)); diagram.Connections.Add((build, dns));
        flowHost.Content = new StackPanel { Spacing = 6, Children = { title, diagram } };
        if (restoreFocus) Dispatcher.UIThread.Post(() => diagram.Children.OfType<Button>().FirstOrDefault(x => Equals(x.Tag, "selected"))?.Focus(), DispatcherPriority.Loaded);
    }
    void ValidateProjectName(string name) => ProjectStore.ValidateDestination(preferences.Workspace, name);
    static string LocalTime(string value) => DateTimeOffset.TryParse(value, out var date) ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : value;
    sealed class FlowGrid : Grid
    {
        public List<(Control From, Control To)> Connections { get; } = [];
        public IBrush LineBrush { get; set; } = Brushes.Gray;
        public FlowGrid()
        {
            var wires = new FlowWires(this) { IsHitTestVisible = false }; Grid.SetColumnSpan(wires, 7); Grid.SetRowSpan(wires, 5); Children.Add(wires);
            LayoutUpdated += (_, _) => wires.InvalidateVisual();
        }
        sealed class FlowWires(FlowGrid owner) : Control
        {
            public override void Render(DrawingContext context)
            {
                var pen = new Pen(owner.LineBrush, 1.4);
                foreach (var (from, to) in owner.Connections)
                {
                    var a = new Point(from.Bounds.Right, from.Bounds.Center.Y); var b = new Point(to.Bounds.Left, to.Bounds.Center.Y); double mid = (a.X + b.X) / 2;
                    context.DrawLine(pen, a, new(mid, a.Y)); context.DrawLine(pen, new(mid, a.Y), new(mid, b.Y)); context.DrawLine(pen, new(mid, b.Y), b);
                    context.DrawLine(pen, b, new(b.X - 4, b.Y - 3)); context.DrawLine(pen, b, new(b.X - 4, b.Y + 3));
                }
            }
        }
    }
}

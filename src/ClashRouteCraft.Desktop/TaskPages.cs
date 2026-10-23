using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    string checkedInputs = "";
    bool forceSubscriptions;
    string CurrentCheckInputs() => session is null ? "" : string.Join("\n", session.Documents.Values.Select(x => x.Text)) + "\n" + session.Subscriptions.Text;
    Control BuildPage()
    {
        liveStage = Label(taskStage, size: UiMetrics.FieldTitle);
        liveStage.Name = "TaskState";
        liveProgress = new ProgressBar { Minimum = 0, Maximum = 100, Value = taskPercent, Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        var check = AsyncButton("参数检查", StartParameterCheckAsync); check.Name = "CheckParameters"; check.IsEnabled = !IsBusy;
        ToolTip.SetTip(check, "检查当前编辑、本地节点和远程名单的可访问性及规则内容；不更新订阅或生成缓存");
        var force = Check("强制拉取最新的订阅", forceSubscriptions, value => forceSubscriptions = value); force.Name = "ForceSubscriptions"; force.IsEnabled = !IsBusy;
        ToolTip.SetTip(force, "默认使用已有缓存；开启后强制拉取，失败时使用有效缓存并在日志中警告");
        var controls = Row(check, GenerationActions(), force);
        var operation = new Grid { ColumnDefinitions = new("*,Auto") };
        operation.Children.Add(SectionTitle("生成当前项目"));
        if (IsBusy)
        {
            var cancel = Button("取消", () => jobCancellation?.Cancel()); cancel.Name = "CancelTask";
            operation.Children.Add(cancel); Grid.SetColumn(cancel, 1);
        }
        var targets = new[] { new ComboBoxItem { Content = "MC2（主配置 + DNS）", Tag = "mc2" }, new ComboBoxItem { Content = "Clash（主配置）", Tag = "clash" } };
        var target = new ComboBox { Name = "BuildTarget", ItemsSource = targets, SelectedItem = targets.Single(x => Equals(x.Tag, ConfigurationValidation.Target(session!.Settings))), MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !IsBusy && !session!.IsReadOnly };
        target.SelectionChanged += (_, _) => { if (target.SelectedItem is ComboBoxItem { Tag: string value } && value != ConfigurationValidation.Target(session!.Settings)) Mutate("settings.yaml", doc => doc["target"] = value); };
        var content = Stack(Card("", operation, Field("生成目标", target), IsBusy ? Stack(liveStage, liveProgress) : controls));
        liveLog = new StackPanel { Name = "DetailedLog", Spacing = 2 };
        RenderTaskLog();
        var logHeader = new Grid { ColumnDefinitions = new("*,Auto") }; logHeader.Children.Add(SectionTitle("详细日志"));
        if (!IsBusy && taskStage == "生成完成")
        {
            var output = Button("查看主配置", () => NavigateOutput("HomeChain.yaml"));
            logHeader.Children.Add(output); Grid.SetColumn(output, 1);
        }
        content.Children.Add(Card("", logHeader, liveLog));
        if (session?.LastAsnAnalysis is { } asn && S(asn, "status") == "blocked")
        {
            var resolve = Button("处理 ASN 依赖", () => { policyTab = "rules"; Navigate("policy"); }); resolve.Name = "ResolveAsnDependencies";
            content.Children.Add(Card("ASN 依赖需要处理", Label("旧输出已保留。请在规则页查看来源，选择排除或高级保留。"), resolve));
        }
        return content;
    }
    void RenderTaskLog()
    {
        if (liveLog is null) return;
        liveLog.Children.Clear();
        if (taskLog.Count == 0) { liveLog.Children.Add(Label("检查或生成后，结果会逐项显示在这里。", true, UiMetrics.Supporting)); return; }
        IEnumerable<ParameterCheck> entries = taskLog;
        if (!IsBusy && checkedInputs.Length > 0 && checkedInputs != CurrentCheckInputs())
            entries = new[] { new ParameterCheck("配置已修改", "下面是上次检查结果，请重新执行参数检查", "warning") }.Concat(entries);
        foreach (var entry in entries)
        {
            var row = new Grid { Name = "TaskLogRow", ColumnDefinitions = new("*,20,Auto"), ColumnSpacing = UiMetrics.Gap, MinHeight = 30 };
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = UiMetrics.Body, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines!.Add(new Avalonia.Controls.Documents.Run(entry.Title + "  ") { FontWeight = FontWeight.SemiBold });
            text.Inlines.Add(new Avalonia.Controls.Documents.Run(entry.Message) { Foreground = Muted });
            row.Children.Add(text);
            var icon = CheckStatusIcon(entry.State, entry.Title + "：" + entry.Message); icon.Name = "TaskStatus";
            row.Children.Add(icon); Grid.SetColumn(icon, 1);
            if (entry.Issue is { } issue)
            {
                var fix = Button("去修复", () => OpenIssue(issue)); fix.Tag = "fix:" + issue.Field; fix.IsEnabled = !IsBusy;
                fix.MinHeight = 24; fix.Height = 24; fix.Padding = new(8, 1); fix.FontSize = UiMetrics.Supporting;
                row.Children.Add(fix); Grid.SetColumn(fix, 2);
            }
            liveLog.Children.Add(row);
        }
    }
    Avalonia.Controls.Shapes.Path CheckStatusIcon(string state, string description)
    {
        string geometry = state switch
        {
            "pass" => "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M4,8 L7,11 L12,5",
            "fail" => "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M5,5 L11,11 M11,5 L5,11",
            "warning" => "M8,1 L15,14 L1,14 Z M8,5 L8,9 M8,11 L8,12",
            _ => "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M8,4 L8,9 M8,11 L8,12"
        };
        string color = state switch { "pass" => Dark ? "#6FD79B" : "#157347", "fail" => Dark ? "#FF9A9A" : "#B02A37", "warning" => Dark ? "#E9B949" : "#8A6400", _ => Dark ? "#97A2B0" : "#687585" };
        var icon = new Avalonia.Controls.Shapes.Path { Tag = state, Data = Geometry.Parse(geometry), Stroke = Brush.Parse(color), StrokeThickness = 2, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(icon, description); Avalonia.Automation.AutomationProperties.SetName(icon, state + " · " + description);
        return icon;
    }
    async Task StartParameterCheckAsync()
    {
        if (session is null || IsBusy) return;
        var current = session;
        jobCancellation?.Dispose(); jobCancellation = new(); var cancellation = jobCancellation.Token;
        taskLog.Clear(); checkedInputs = CurrentCheckInputs(); lastError = ""; taskStage = "正在检查参数"; taskPercent = 0;
        job = Run(); SetBusy(true); RenderPage();
        try { await job; }
        finally { SetBusy(false); RenderPage(); UpdateFooter(); }
        async Task Run()
        {
            await Task.Yield();
            try
            {
                await Task.Run(async () =>
                {
                    await foreach (var check in ParameterInspection.InspectAsync(current, cancellation))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            int existing = check.Key is null ? -1 : taskLog.FindIndex(x => x.Key == check.Key);
                            if (existing >= 0) taskLog[existing] = check; else taskLog.Add(check);
                            if (liveStage is not null) liveStage.Text = check.Title;
                            RenderTaskLog();
                        });
                    }
                }, cancellation);
                int failed = taskLog.Count(x => x.State == "fail"), warned = taskLog.Count(x => x.State == "warning"), passed = taskLog.Count(x => x.State == "pass");
                taskStage = "参数检查完成";
                taskLog.Add(new("检查完成", $"{passed} 项通过 · {warned} 项警告 · {failed} 项错误", failed > 0 ? "fail" : warned > 0 ? "warning" : "pass"));
            }
            catch (OperationCanceledException) { EndPendingChecks("warning", "已取消，尚未完成校验"); taskStage = "检查已取消"; taskLog.Add(new("检查已取消", "已完成的结果保留，剩余项目未检查", "warning")); }
            catch (Exception ex) { EndPendingChecks("fail", "检查中断，未完成校验"); taskStage = "检查中断"; taskLog.Add(new("检查中断", SafeMessage(ex), "fail")); }
        }
    }
    void EndPendingChecks(string state, string message)
    {
        for (int i = 0; i < taskLog.Count; i++)
            if (taskLog[i].State == "pending") taskLog[i] = taskLog[i] with { State = state, Message = taskLog[i].Message.Replace("正在检查可访问性和规则内容", message) };
    }
    void FinishPendingLog(string state)
    {
        for (int i = 0; i < taskLog.Count; i++) if (taskLog[i].State == "pending") taskLog[i] = taskLog[i] with { State = state };
    }
    async Task StartBuildAsync()
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        checkedInputs = "";
        bool refresh = forceSubscriptions;
        var issues = Issues(refresh, true);
        if (issues.Count > 0)
        {
            taskLog.Clear(); taskLog.AddRange(issues.Select(x => new ParameterCheck("配置待修复", x.Message, "fail", x))); taskStage = "配置待修复"; CurrentPage = "build"; RenderPage(); return;
        }
        if (session.Dirty) session.Save();
        var workspace = session.Workspace;
        IReadOnlyList<string> projects = [session.ProjectName];
        if (projects.Count == 0) throw new BuildException("工作区中没有项目");
        jobCancellation?.Dispose(); jobCancellation = new(); var cancellation = jobCancellation.Token;
        taskLog.Clear(); lastError = ""; taskPercent = 0; taskStage = "准备生成"; taskMessage = "检查已保存输入"; CurrentPage = "build";
        job = RunJobsAsync(); SetBusy(true); RenderPage();
        try { await job; }
        finally { SetBusy(false); Try(() => session.ReloadSubscriptionState()); RenderPage(); UpdateFooter(); }

        async Task RunJobsAsync()
        {
            await Task.Yield();
            try
            {
                int current = 0;
                foreach (string project in projects)
                {
                    cancellation.ThrowIfCancellationRequested(); int projectIndex = current;
                    var progress = new CallbackProgress(value => Dispatcher.UIThread.Post(() =>
                    {
                        taskStage = project + " · " + value.Stage; taskMessage = value.Message;
                        taskPercent = (projectIndex * 6 + value.Completed) * 100.0 / (projects.Count * 6);
                        FinishPendingLog("pass"); taskLog.Add(new(value.Stage, value.Message, value.State));
                        if (liveStage is not null) liveStage.Text = taskStage;
                        if (liveProgress is not null) liveProgress.Value = taskPercent;
                        RenderTaskLog();
                        RefreshFlow();
                    }));
                    var options = new BuildOptions(Force: refresh);
                    var built = await Task.Run(() => engine.BuildProjectAsync(workspace, project, options, progress, cancellation), cancellation);
                    session.LastAsnAnalysis = (JsonObject)M(built.Report, "asn").DeepClone(); current++;
                }
                await Dispatcher.UIThread.InvokeAsync(() => { }); FinishPendingLog("pass"); session.ReloadSubscriptionState(); taskStage = "生成完成"; taskMessage = "输出已保存在本地，可查看配置文件"; taskPercent = 100; SetStatus(taskMessage);
            }
            catch (OperationCanceledException) { FinishPendingLog("warning"); taskLog.Add(new("已取消", "未提交的项目保留旧输出", "warning")); taskStage = "已取消"; taskMessage = "未提交的项目保留旧输出"; SetStatus(taskMessage); }
            catch (AsnDependencyException ex) { session.LastAsnAnalysis = ex.Analysis; lastError = SafeMessage(ex); taskStage = "ASN 依赖待处理"; taskMessage = lastError; FinishPendingLog("fail"); taskLog.Add(new("ASN 依赖待处理", lastError, "fail")); SetStatus(lastError, true); }
            catch (Exception ex) { lastError = SafeMessage(ex); taskStage = "生成失败"; taskMessage = lastError; FinishPendingLog("fail"); taskLog.Add(new("生成失败", lastError, "fail")); SetStatus(lastError, true); }
        }
    }
    void SetBusy(bool busy)
    {
        projectPicker.IsEnabled = !busy; saveButton.IsEnabled = !busy && session?.Dirty == true;
        for (int i = 0; i < navButtons.Count; i++) navButtons[i].IsEnabled = !busy || PageIds[i] is "build" or "report";
    }
    sealed class CallbackProgress(Action<BuildProgress> callback) : IProgress<BuildProgress> { public void Report(BuildProgress value) => callback(value); }
    Control ReportPage()
    {
        if (previewFile == "HomeChain.MC2DNS.yaml") return DnsOutputPage();
        var report = session!.ReadReport();
        if (report is null) return Stack(Card("尚未生成", Label("完成基础配置后生成，即可获取主配置和 DNS 文件。"), IssuesCard(Issues())));
        string Status(JsonNode? node) => S(node) == "passed" ? "通过" : S(node) == "passed_with_geodata" ? "需配套数据库" : node is null ? "未运行" : S(node).StartsWith("not_run") ? "未运行，仅检查结构" : S(node);
        Control badges = StatusChips("本机内核：" + Status(report["core_validation"]) + " · 目标 ASN：" + AsnTargetStatus(report) + (S(report, "rule_source") == "subscription" ? $" · 规则来源：{S(report, "baseline_source")}" : "")
            + (S(M(report, "dns"), "source") == "privacy" ? " · DNS：Fake-IP 隐私增强" : S(M(report, "dns"), "source") == "client" ? " · DNS：由客户端管理" : "")
            + (S(report, "target", "mc2") == "mc2" && B(M(report, "dns"), "configured") ? " · 本机 DNS 组合：" + Status(M(report, "dns")["mc2_core_validation"]) : ""));
        string path = Path.Combine(session.OutputPath, previewFile); string text = PreviewText(path, previewSecrets); var editor = Editor(text, true);
        var outputStatus = session.OutputStatus(); bool current = outputStatus == OutputFreshness.Current;
        var copy = AsyncButton("复制主配置全文", async () => { if (Clipboard is not null && !IsBusy) { await Clipboard.SetTextAsync(session.ReadValidatedOutput("HomeChain.yaml")); SetStatus("已复制原始全文，包含真实连接参数"); } });
        copy.IsEnabled = OutputValidated();
        var openOutput = Button("打开输出目录", () => OpenPath(session.OutputPath));
        return Stack(IsBusy ? Note(taskStage + "，下面显示上一次成功输出；请等待当前任务结束后再使用。") : current ? Note("这是当前配置的生成结果。文件校验通过后，仍需在设备上验收联网。") : Card("当前显示上一次成功输出", Label(outputStatus == OutputFreshness.ValidationChanged ? "校验程序或数据库已有变化，请重新生成以完成校验后再导入。" : "配置或名单已有变化，请重新生成后再导入。")),
            Card("主配置", Label("将 HomeChain.yaml 导入 Clash 客户端并启用。"),
                Row(openOutput, copy), badges,
                Label($"生成于 {LocalTime(S(report, "built_at"))} · {I(report, "proxy_count", 0)} 个节点 · {I(report, "rule_count", 0)} 条路由规则", true, UiMetrics.Supporting),
                Label(GeoDependencyNotice(report).Trim(), true, UiMetrics.Supporting)),
            new Expander { Header = "查看文件内容", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Stack(Check("显示敏感字段", previewSecrets, show => { previewSecrets = show; RenderPage(); }), CodeCard(editor), Label("预览默认遮盖凭据；复制使用原始文件全文。", true, UiMetrics.Supporting)) });
    }
    Border CodeCard(TextEditor editor, params Control[] actions)
    {
        var stack = new StackPanel { Spacing = 8 };
        if (actions.Length > 0)
        {
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var action in actions) toolbar.Children.Add(action);
            stack.Children.Add(toolbar);
        }
        stack.Children.Add(editor);
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new(1), CornerRadius = new(UiMetrics.CardRadius), Padding = new(14, 10), Child = stack };
    }
    Control StatusChips(string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (string part in value.Split('·'))
        {
            string text = part.Trim();
            if (text.Length == 0) continue;
            int colon = text.IndexOf('：');
            string label = colon > 0 ? text[..colon] : text;
            string state = colon > 0 ? text[(colon + 1)..] : "";
            bool ok = state.Contains("通过");
            bool warn = !ok && (state.Contains("需") || state.Contains("未"));
            string dot = ok ? (Dark ? "#6FD79B" : "#157347") : warn ? (Dark ? "#E9B949" : "#8A6400") : (Dark ? "#97A2B0" : "#687585");
            string tint = ok ? (Dark ? "#1B3328" : "#EAF7F0") : warn ? (Dark ? "#3A2F16" : "#FFF7E6") : (Dark ? "#242E3A" : "#F2F5F9");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brush.Parse(dot), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label + (state.Length > 0 ? "：" + state : ""), FontSize = UiMetrics.Supporting, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new Border { Background = Brush.Parse(tint), BorderBrush = Brush.Parse(dot), BorderThickness = new(1), CornerRadius = new(11), Padding = new(9, 2), Child = content });
        }
        return row;
    }
    string PreviewText(string path, bool showSecrets)
    {
        if (!File.Exists(path)) return "此文件未生成。";
        try
        {
            string text = File.ReadAllText(path); if (showSecrets) return text;
            var document = path.EndsWith(".json") ? JsonNode.Parse(text) : YamlData.Parse(text);
            void Redact(JsonNode? node)
            {
                if (node is JsonObject map)
                    foreach (string key in map.Select(x => x.Key).ToArray())
                        if (new[] { "password", "username", "uuid", "private-key", "token", "secret", "authorization" }.Contains(key.ToLowerInvariant())) map[key] = "••••••";
                        else Redact(map[key]);
                else if (node is JsonArray array) foreach (var child in array) Redact(child);
            }
            Redact(document); return path.EndsWith(".json") ? document?.ToJsonString(JsonOptions) ?? "null" : YamlData.Write(document);
        }
        catch (Exception ex) when (ex is IOException or BuildException or System.Text.Json.JsonException) { return "无法安全预览此文件，请检查生成报告。"; }
    }
    static string GeoDependencyNotice(JsonObject? report)
    {
        if (report is null) return "";
        var target = M(report, "target_dependencies");
        var files = (target.ContainsKey("databases") ? L(target, "databases") : L(report, "required_geodata")).Select(x => S(x))
            .Concat(M(M(report, "mc2_package_validation"), "required_assets").Select(x => x.Key)).Distinct().ToArray();
        string text = files.Length == 0 ? "" : "\n规则引用数据库：" + string.Join("、", files) + "，需包含所引用的分类。";
        var asn = M(report, "asn");
        if (I(asn, "uninspected_providers", 0) > 0) text += "\n仍有远程规则集未检查，可能需要 ASN.mmdb；不能认定没有额外依赖。";
        if (I(asn, "excluded_rules", 0) > 0) text += $"\n按项目选择排除了 {I(asn, "excluded_rules", 0)} 条 ASN 规则；匹配结果可能改变，详情见规则页。";
        return text + "\n本机校验通过不代表目标设备已安装数据库或能够联网；路由器运行状态尚未验证。";
    }
    bool OutputValidated()
    {
        if (IsBusy) return false;
        var report = session!.ReadReport();
        return report is not null && session.OutputIsCurrent() && S(report, "core_validation") == "passed";
    }
}

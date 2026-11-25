using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    string policyFocusField = "";

    Control PriorityRuleField(JsonObject policy)
    {
        var editor = new PriorityRuleEditor(L(policy, "first"));
        var countLabel = Label("", true, 12); countLabel.Name = "PriorityRulesCount";
        var error = Label("", size: 12); error.Name = "PriorityRulesError"; error.Foreground = Danger; error.IsVisible = false;
        void RefreshSummary()
        {
            countLabel.Text = editor.Count == 0
                ? "当前 0 条（留空则直接进入第 2 层 URL 规则名单）"
                : $"当前 {editor.Count} 条（不按动作重新分组，直接按行顺序生效）";
            error.IsVisible = false;
            int position = 0;
            foreach (var raw in editor.Rules)
            {
                position++;
                try
                {
                    var (match, _) = PriorityRuleEditor.ParseEntry(raw);
                    Rules.ValidateSyntax(Rules.WithAction(match, "DIRECT", inputFormat: "match"));
                }
                catch (InvalidDomainRuleException ex)
                {
                    error.Text = $"优先自定义规则 · 第 {position} 行：这条规则因异常将在生成时跳过（{ex.Message}），未自动修正。原规则：{PriorityRuleEditor.Display(raw)}";
                    error.IsVisible = true; break;
                }
                catch (BuildException ex)
                {
                    error.Text = $"优先自定义规则 · 第 {position} 行：{ex.Message}";
                    error.IsVisible = true; break;
                }
            }
        }
        var input = TextInput(editor.Text, text =>
        {
            editor.Replace(text);
            Mutate("policy.yaml", doc => doc["first"] = editor.Rules);
            RefreshSummary();
        }, multiline: true);
        input.Name = "PriorityRules";
        input.Watermark = "每行一条完整规则（含动作，按行顺序生效），例如：\nDOMAIN-SUFFIX,todesk.com,DIRECT\nGEOIP,LAN,DIRECT,no-resolve\nAND,((NETWORK,UDP),(DST-PORT,443)),REJECT\nDOMAIN-SUFFIX,claude.ai,PROXY";
        FitListInput(input);
        ToolTip.SetTip(input, "支持动作：DIRECT（直连）、PROXY（统一代理出口）、REJECT / REJECT-DROP（拦截）、PASS（继续匹配后续规则）。支持逻辑规则与 no-resolve / src 标志；不能包含 MATCH / FINAL。");
        RefreshSummary();
        var card = Card("",
            countLabel,
            input,
            error,
            Label("支持动作：DIRECT · PROXY · REJECT · REJECT-DROP · PASS（按行顺序生效；不能包含 MATCH / FINAL）。", true, 12));
        card.Name = "PriorityRulesSection";
        return card;
    }

    Control ManualRuleFields(string group, JsonObject policy, out Control? lanOption)
    {
        lanOption = null;
        var editor = new ManualRuleEditor(L(policy, group), group == "direct");
        var error = Label("", size: 12); error.Foreground = Danger; error.IsVisible = false;
        bool syncing = false;
        CheckBox? lan = null; Expander? exactSection = null, moreSection = null;
        void RefreshSummary()
        {
            if (lan is not null) { syncing = true; lan.IsChecked = editor.LanEnabled; syncing = false; }
            if (exactSection is not null) exactSection.Header = $"精确域名 · DOMAIN（{editor.Count(ManualRuleKind.Exact)} 条）";
            if (moreSection is not null) moreSection.Header = $"更多匹配方式（{editor.Count(ManualRuleKind.Other)} 条）";
            error.IsVisible = false;
            int position = 0;
            foreach (var rule in editor.Rules)
                try { position++; Rules.ValidateSyntax(Rules.WithAction(rule, "DIRECT")); }
                catch (InvalidDomainRuleException ex) { error.Text = $"个人规则 · {group}[{position}]：这条规则因异常将在生成时跳过（{ex.Message}），未自动修正。原规则：{S(rule, Canonical(rule))}"; error.IsVisible = true; break; }
                catch (BuildException ex) { error.Text = ex.Message; error.IsVisible = true; break; }
        }
        void Apply() { Mutate("policy.yaml", doc => doc[group] = editor.Rules); RefreshSummary(); }
        TextBox Input(ManualRuleKind kind, string name, string hint)
        {
            var input = TextInput(editor.Text(kind), text => { if (syncing) return; editor.Replace(kind, text); Apply(); }, multiline: true);
            input.Name = name + "_" + group; input.Watermark = hint; FitListInput(input);
            // Pasted prefixed rules remain compatible, and are simplified after editing.
            input.LostFocus += (_, _) => { if (kind != ManualRuleKind.Other) input.Text = editor.Text(kind); };
            return input;
        }
        var suffix = Input(ManualRuleKind.Suffix, "Suffix", "每行一个域名，例如 example.com；包含其所有子域名");
        var exact = Input(ManualRuleKind.Exact, "Exact", "每行一个完整域名，例如 api.example.com；仅匹配此域名");
        var more = Input(ManualRuleKind.Other, "Manual", "每行完整匹配条件，例如 DOMAIN-KEYWORD,uuyc");
        ToolTip.SetTip(more, "正则含普通逗号时，用 {match: 'DOMAIN-REGEX,^foo,bar$'} 明确为匹配条件；带旧动作的完整规则使用 {rule: '...'}。");
        exactSection = new Expander { Name = "ExactSection_" + group, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = editor.Count(ManualRuleKind.Exact) > 0 || policyFocusField == exact.Name, Content = exact };
        moreSection = new Expander { Name = "MoreRules_" + group, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = policyFocusField == more.Name, Content = more };
        var content = new StackPanel { Spacing = UiMetrics.FieldGap };
        if (group == "direct")
        {
            lan = Check("局域网及回环地址直连", editor.LanEnabled, enabled =>
            {
                if (syncing) return;
                editor.SetLan(enabled); syncing = true; more.Text = editor.Text(ManualRuleKind.Other); syncing = false; Apply();
            });
            lan.Name = "DirectLan";
            ToolTip.SetTip(lan, "添加 GEOIP,LAN,DIRECT,no-resolve：匹配内网和回环目的 IP，不为这条规则额外发起 DNS 查询。关闭只移除本条附加规则，主订阅中的同类规则仍可生效。");
            var explanation = Label("让路由器、NAS、打印机等内网地址直连，避免误走代理。它属于第 3 层直连规则，与 URL 名单无关；如需高于 URL 名单放行，可写入第 1 层优先自定义规则。", true, 12); explanation.Name = "DirectLanHelp";
            var lanContent = Stack(lan, explanation, Label("例如 192.168.x.x、10.x.x.x、127.0.0.1；回环地址指运行 Mihomo 的设备，MC2 中即路由器。", true, 12)); lanContent.Spacing = 4;
            if (SubscriptionBaseline.Enabled(policy)) lanContent.Children.Add(Label("主订阅若已有同类规则，无需重复补充；关闭个人附加规则时此项也不生效。", true, 12));
            lanOption = lanContent;
        }
        content.Children.Add(new Expander { Header = "域名及其子域名 · DOMAIN-SUFFIX", IsExpanded = true, Content = suffix });
        content.Children.Add(exactSection); content.Children.Add(moreSection); content.Children.Add(error);
        RefreshSummary(); return content;
    }

    Control RemoteUrlField(string group, string title, IReadOnlyList<JsonObject> remotes, int ordinal, Action<int> move, bool canMoveUp, bool canMoveDown)
    {
        CancellationTokenSource? running = null;
        int generation = 0;
        var results = new ContentControl { Name = "UrlCheckResults_" + group, Width = 320 };
        var flyout = new Flyout { Content = results, Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway };
        flyout.FlyoutPresenterClasses.Add("rule-check");
        var messages = new List<(string Url, int Line, RemoteRuleCheck? Check)>();
        var summary = Label("", true, 12);
        void RenderResults()
        {
            if (summary.Parent is Panel previousRows) previousRows.Children.Remove(summary);
            var rows = Stack(summary);
            rows.Spacing = 8; double widestUrl = 0;
            foreach (var item in messages)
            {
                string state = item.Check is null ? "pending" : item.Check.Success ? item.Check.SkippedCount > 0 ? "warning" : "pass" : "fail";
                string detail = item.Check?.Message ?? "尚未执行";
                var statusIcon = CheckStatusIcon(state, $"第 {item.Line} 行：{detail}");
                statusIcon.Name = "UrlStatus_" + group + "_" + item.Line;
                ToolTip.SetTip(statusIcon, detail);
                var url = new TextBlock { Text = item.Url, TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                url.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); widestUrl = Math.Max(widestUrl, url.DesiredSize.Width);
                var row = new Grid { Name = "UrlCheckRow_" + item.Line, ColumnDefinitions = new("*,16"), ColumnSpacing = 12, MinHeight = 24 };
                row.Children.Add(url); row.Children.Add(statusIcon); Grid.SetColumn(statusIcon, 1); rows.Children.Add(row);
            }
            results.Width = Math.Clamp(widestUrl + 28, 320, Math.Max(320, Math.Min(960, Bounds.Width - 88)));
            results.Content = rows;
        }
        void Pending(string text)
        {
            messages.Clear();
            messages.AddRange(text.Split('\n').Select((url, index) => (Url: url.Trim(), Line: index + 1, Check: (RemoteRuleCheck?)null)).Where(x => x.Url.Length > 0));
        }
        var urls = TextInput(string.Join('\n', remotes.Where(x => ProjectOrdering.IsUrlEntry(x) && ProjectOrdering.RemoteGroup(x) == group).Select(x => S(x, "url"))), text =>
        {
            generation++; running?.Cancel();
            Pending(text); summary.Text = "链接已修改，请重新校验。"; RenderResults(); flyout.Hide();
            Mutate("policy.yaml", doc => ProjectOrdering.SetRemoteUrls(doc, group, text));
        }, multiline: true);
        urls.Name = "RemoteUrls_" + group; urls.Margin = new(UiMetrics.DisclosureInset, 0, 0, 0); FitListInput(urls); urls.Watermark = "每行一个 HTTP(S) URL，可批量粘贴";
        Pending(urls.Text ?? ""); summary.Text = "尚未执行校验，点击按钮开始。"; RenderResults();
        Button? validate = null;
        async Task Validate()
        {
            if (running is { IsCancellationRequested: false }) { running.Cancel(); summary.Text = "正在取消…"; ToolTip.SetTip(validate!, null); flyout.ShowAt(validate!); return; }
            var currentSession = session; if (currentSession is null) return;
            var lines = (urls.Text ?? "").Split('\n').Select((url, index) => (Url: url.Trim(), Line: index + 1)).Where(x => x.Url.Length > 0).ToArray();
            if (lines.Length == 0) { Notify("请先填写至少一个 URL。"); return; }
            var cancellation = new CancellationTokenSource(); running = cancellation; int revision = ++generation;
            bool Current() => revision == generation && ReferenceEquals(session, currentSession);
            messages.Clear(); messages.AddRange(lines.Select(line => (line.Url, line.Line, (RemoteRuleCheck?)null))); summary.Text = "正在校验…再次点击图标可取消。"; summary.Foreground = Muted; RenderResults(); ToolTip.SetTip(validate!, null); flyout.ShowAt(validate!);
             AutomationProperties.SetName(validate!, "取消 " + title + " URL 校验");
            int passed = 0, skippedRules = 0, checkedCount = 0;
            try
            {
                var available = Rules.RemoteSpecs(currentSession.Policy["remote_rules"]).Where(x => ProjectOrdering.IsUrlEntry(x) && ProjectOrdering.RemoteGroup(x) == group).ToList();
                var specs = lines.Select(line =>
                {
                    int match = available.FindIndex(x => S(x, "url") == line.Url);
                    if (match < 0) return O("url", line.Url);
                    var spec = (JsonObject)available[match].DeepClone(); available.RemoveAt(match); return spec;
                }).ToArray();
                var http = (JsonObject)M(currentSession.Settings, "http").DeepClone();
                summary.Text = $"并发校验中：已完成 0/{lines.Length}（最多 4 条同时请求）…再次点击可取消。";
                await Task.Run(() => RemoteRuleValidation.CheckBatchAsync(currentSession.ProjectPath, specs, http, async (i, result) =>
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!Current() || cancellation.IsCancellationRequested) return;
                        if (result.Success) passed++; skippedRules += result.SkippedCount; checkedCount++;
                        summary.Text = $"并发校验中：已完成 {checkedCount}/{lines.Length}…再次点击可取消。";
                        messages[i] = (lines[i].Url, lines[i].Line, result); RenderResults();
                    });
                }, cancellation.Token));
                if (!Current()) return;
                summary.Text = $"校验完成：{passed}/{lines.Length} 通过。" + (skippedRules > 0 ? $"{skippedRules} 条异常域名规则将跳过，请查看明细。" : "");
            }
            catch (OperationCanceledException) { if (Current()) summary.Text = "校验已取消。"; }
            catch (Exception ex) { if (Current()) { summary.Text = SafeMessage(ex); summary.Foreground = Danger; } }
            finally
            {
                if (ReferenceEquals(running, cancellation))
                {
                    running = null;  AutomationProperties.SetName(validate!, "校验 " + title + " URL 组");
                }
                cancellation.Dispose();
            }
        }
        validate = IconButton("ValidateUrls_" + group, "校验 " + title + " URL 组", "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M4,8 L7,11 L12,5", Validate);
        flyout.OverlayInputPassThroughElement = validate;
        var label = SectionTitle($"2.{ordinal} {title} URL 组"); label.FontSize = UiMetrics.FieldTitle;
        Button? collapse = null;
        collapse = IconButton("CollapseUrls_" + group, "收起 URL 名单", "M3,6 L8,11 L13,6", () =>
        {
            urls.IsVisible = !urls.IsVisible;
            ToolTip.SetTip(collapse!, urls.IsVisible ? "收起 URL 名单" : "展开 URL 名单");
            AutomationProperties.SetName(collapse!, urls.IsVisible ? "收起 URL 名单" : "展开 URL 名单");
            ((Avalonia.Controls.Shapes.Path)collapse!.Content!).RenderTransform = new RotateTransform(urls.IsVisible ? 180 : 0);
            return Task.CompletedTask;
        });
        var chevron = (Avalonia.Controls.Shapes.Path)collapse.Content!;
        chevron.Data = (Geometry)Application.Current!.FindResource("DisclosureChevronGeometry")!;
        chevron.RenderTransform = new RotateTransform(180);
        var up = IconButton("UrlUp_" + group, "提高" + title + " URL 组优先级", "M3,7 L8,2 L13,7 M8,2 L8,14", () => { move(-1); return Task.CompletedTask; }); up.Name = "UrlUp_" + group; up.IsEnabled = canMoveUp;
        var down = IconButton("UrlDown_" + group, "降低" + title + " URL 组优先级", "M3,9 L8,14 L13,9 M8,14 L8,2", () => { move(1); return Task.CompletedTask; }); down.Name = "UrlDown_" + group; down.IsEnabled = canMoveDown;
        var header = new Grid { ColumnDefinitions = new("30,*,Auto") };
        header.Children.Add(collapse);
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, validate } };
        header.Children.Add(caption); Grid.SetColumn(caption, 1);
        var moves = Row(up, down);
        header.Children.Add(moves); Grid.SetColumn(moves, 2);
        FlyoutBase.SetAttachedFlyout(validate, flyout);
        validate.PointerEntered += (_, _) => { if (results.Content is not null) { ToolTip.SetTip(validate, null); ToolTip.SetIsOpen(validate, false); flyout.ShowAt(validate); } };
        urls.DetachedFromVisualTree += (_, _) => { generation++; running?.Cancel(); flyout.Hide(); };
        var content = Stack(header, urls); content.Spacing = UiMetrics.FieldGap;
        return Card("", content);
    }
}

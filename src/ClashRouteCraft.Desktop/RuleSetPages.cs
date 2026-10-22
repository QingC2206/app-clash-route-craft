using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    void ChangeRuleSets(Action<JsonArray> change)
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        var policy = ImportedRuleSets.Migrate(session.Policy); var sets = L(policy, "rule_sets"); change(sets);
        if (!policy.ContainsKey("rule_sets")) policy["rule_sets"] = sets;
        session.Documents["policy.yaml"].Set(policy); RenderPage();
    }
    static string RuleSetSummary(JsonObject set) => string.Join(" · ", L(set, "rules").OfType<JsonObject>()
        .Where(e => ImportedRuleSets.Included(set, e)).GroupBy(e => ImportedRuleSets.Target(set, e))
        .Select(g => $"{(g.Key == "DIRECT" ? "直连" : g.Key.StartsWith("REJECT") ? "拒绝" : g.Key == "PROXY" ? "默认代理" : g.Key.Length == 0 ? "待映射" : g.Key)} {g.Count():N0}"));
    Control RuleSetsForm()
    {
        var sets = ImportedRuleSets.Sets(session!.Policy);
        bool baseline = UsesSubscriptionBaseline();
        var content = Stack();
        if (baseline)
        {
            var back = Button("返回分流规则", () => { policyTab = "rules"; RenderPage(); }); back.Name = "BackToRules";
            content.Children.Add(Row(back));
        }
        else
        {
            var import = AsyncButton("导入规则集", () => ImportRuleSetAsync()); import.Name = "ImportRuleSet";
            content.Children.Add(Row(import));
        }
        content.Children.Add(Note(baseline ? "这里只管理旧项目保留的附加规则集，主订阅规则无需重新导入。匹配顺序：个人前置规则 → 手工与 URL 名单 → 下列附加规则集 → 主订阅规则。所有代理目标生成时均归并到统一的“代理出口”。规则集使用导入快照，不会自动下载或更新。" : "匹配顺序：自定义前置规则 → 手工与 URL 名单 → 下列启用的规则集 → 国内库 → 最终动作。规则集内部保留原始顺序；生成使用已导入的快照，不会自动下载或更新。"));
        if (UsesSubscriptionBaseline() && !B(session.Policy, "extra_rules_enabled", true)) content.Children.Add(Note("个人附加规则总开关已关闭，以下规则集都不参与本次生成；内容与各自的开关状态仍保留。"));
        if (sets.Count == 0) content.Children.Add(Card("尚未导入规则集", Label("可直接选择项目订阅，或从完整配置、独立规则文件、URL、粘贴内容提取规则。节点和 DNS 不会导入。")));
        for (int i = 0; i < sets.Count; i++)
        {
            int index = i; var set = Map(sets[i], "规则集");
            var enabled = Check("启用", B(set, "enabled", true), value => ChangeRuleSets(list => Map(list[index], "规则集")["enabled"] = value)); enabled.Name = "EnableRuleSet" + i;
            var view = AsyncButton("查看规则", () => ViewRuleSetAsync(set)); view.Name = "ViewRuleSet" + i;
            var map = AsyncButton("策略映射", () => MapRuleSetAsync(index)); map.Name = "MapRuleSet" + i;
            var update = AsyncButton("更新 / 重新导入", () => ImportRuleSetAsync(index)); update.Name = "UpdateRuleSet" + i;
            var up = Button("↑", () => ChangeRuleSets(list => { var item = list[index]!.DeepClone(); list.RemoveAt(index); list.Insert(index - 1, item); })); up.Name = "RuleSetUp" + i; up.IsEnabled = i > 0;
            var down = Button("↓", () => ChangeRuleSets(list => { var item = list[index]!.DeepClone(); list.RemoveAt(index); list.Insert(index + 1, item); })); down.Name = "RuleSetDown" + i; down.IsEnabled = i < sets.Count - 1;
            var remove = AsyncButton("移除", async () => { if (await ConfirmAsync("移除规则集", "从当前编辑内容中移除此规则集，保存前可撤销文件修改。", "移除")) ChangeRuleSets(list => list.RemoveAt(index)); }); remove.Name = "RemoveRuleSet" + i;
            int omitted = L(set, "rules").OfType<JsonObject>().Count(e => !ImportedRuleSets.Included(set, e));
            content.Children.Add(Card($"{i + 1}. {S(set, "name")} · {L(set, "rules").Count:N0} 条", Label(RuleSetSummary(set)),
                Label($"来源：{S(M(set, "source"), "label", "导入快照")} · {LocalTime(S(set, "imported_at", "旧版迁移"))}" + (omitted > 0 ? $" · {omitted} 条源兜底已跳过" : ""), true, 12),
                Row(enabled, view, map, update, up, down, remove)));
        }
        return content;
    }
    Window RuleSetWindow(string title, int width = 850, int height = 650) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = 620, MinHeight = 430,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = WindowSurface, Foreground = Ink
    };
    async Task ViewRuleSetAsync(JsonObject set, Window? owner = null)
    {
        var dialog = RuleSetWindow("查看规则集");
        var search = new TextBox { Name = "RuleSetSearch", Watermark = "搜索域名、规则类型或源策略" };
        var choices = new[] { "全部动作" }.Concat(L(set, "rules").OfType<JsonObject>().Select(e => ImportedRuleSets.Target(set, e)).Distinct()).ToArray();
        var filter = new ComboBox { Name = "RuleSetActionFilter", ItemsSource = choices, SelectedIndex = 0, MinWidth = 160 };
        var rows = new StackPanel { Spacing = 6 }; var info = Label(""); int page = 0;
        Button previous = null!, next = null!;
        previous = Button("上一页", () => { page--; Refresh(); }); previous.Name = "RuleSetPrevious";
        next = Button("下一页", () => { page++; Refresh(); }); next.Name = "RuleSetNext";
        void Refresh()
        {
            string query = search.Text?.Trim() ?? "", action = filter.SelectedItem as string ?? "全部动作";
            var selected = L(set, "rules").OfType<JsonObject>().Select((e, i) => (Entry: e, Index: i + 1))
                .Where(x => (action == "全部动作" || ImportedRuleSets.Target(set, x.Entry) == action)
                    && (S(x.Entry, "match") + " " + S(x.Entry, "source_action")).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            int pages = Math.Max(1, (selected.Length + 49) / 50); page = Math.Clamp(page, 0, pages - 1);
            rows.Children.Clear();
            foreach (var (entry, ordinal) in selected.Skip(page * 50).Take(50))
            {
                var row = new Grid { ColumnDefinitions = new("55,*,190"), ColumnSpacing = 10 };
                row.Children.Add(Label(ordinal.ToString(), true, 12));
                var match = Label(S(entry, "match"), size: 12); row.Children.Add(match); Grid.SetColumn(match, 1);
                var mapped = Label(S(entry, "source_action") + " → " + (ImportedRuleSets.Target(set, entry) is { Length: > 0 } target ? target : "待映射")
                    + (ImportedRuleSets.Included(set, entry) ? "" : "（兜底已跳过）"), true, 12); row.Children.Add(mapped); Grid.SetColumn(mapped, 2);
                rows.Children.Add(row);
            }
            info.Text = $"{selected.Length:N0} 条结果 · 第 {page + 1} / {pages} 页 · 按源顺序显示";
            previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < pages;
        }
        search.TextChanged += (_, _) => { page = 0; Refresh(); }; filter.SelectionChanged += (_, _) => { page = 0; Refresh(); };
        var close = Button("关闭", () => dialog.Close()); close.Name = "CloseRuleSetViewer";
        var layout = new DockPanel { Margin = new Thickness(20) };
        var columns = new Grid { ColumnDefinitions = new("55,*,190"), ColumnSpacing = 10 };
        columns.Children.Add(Label("序号", true, 12)); var matchTitle = Label("匹配条件", true, 12); columns.Children.Add(matchTitle); Grid.SetColumn(matchTitle, 1);
        var actionTitle = Label("源策略 → 项目动作", true, 12); columns.Children.Add(actionTitle); Grid.SetColumn(actionTitle, 2);
        var header = Stack(SectionTitle(S(set, "name")), Label(RuleSetSummary(set), true), search, Row(filter), columns);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var footer = Stack(info, Row(previous, next, close)); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = rows, Margin = new Thickness(0, 12) }); dialog.Content = layout; Refresh();
        await dialog.ShowDialog(owner ?? this);
    }
    Control MappingForm(JsonObject set, Action? changed = null)
    {
        var name = new TextBox { Name = "RuleSetName", Text = S(set, "name") }; name.TextChanged += (_, _) => { set["name"] = name.Text?.Trim() ?? ""; changed?.Invoke(); };
        int fallbacks = L(set, "rules").OfType<JsonObject>().Count(ImportedRuleSets.IsFallback);
        var include = new CheckBox { Name = "RuleSetIncludeFallback", Content = $"包含来源中的兜底规则（{fallbacks} 条）", IsChecked = B(set, "include_fallback") };
        include.IsCheckedChanged += (_, _) => { set["include_fallback"] = include.IsChecked == true; changed?.Invoke(); };
        var content = Stack(Label("规则集名称"), name, Label($"共 {L(set, "rules").Count:N0} 条，保持原始顺序。仅明确的内置动作自动映射，其他策略请手动选择。", true), include,
            Label("默认跳过来源中的 MATCH / CN / geolocation-!cn 兜底，使用项目兜底。勾选后可能遮挡后续规则集与项目兜底。", true, 12),
            Label("PROXY：默认代理 · DIRECT：直连 · REJECT：拒绝 · PASS：继续匹配后续规则", true, 12));
        var actions = ImportedRuleSets.PortableActions.Concat(Nodes.Reserved).Concat(new[] { "默认代理", "订阅优先", "机场手选" })
            .Concat(session!.Residences.OfType<JsonObject>().Select(x => S(x, "name")))
            .Concat(L(session.Settings, "sources").OfType<JsonObject>().Select(x => "订阅·" + S(x, "name"))).Distinct().ToArray();
        int i = 0;
        foreach (string key in M(set, "mappings").Select(x => x.Key).ToArray())
        {
            string current = S(M(set, "mappings"), key); var options = new[] { "请选择目标动作" }.Concat(actions).Concat(current.Length > 0 ? [current] : Array.Empty<string>()).Distinct().ToArray();
            var picker = new ComboBox { Name = "RuleSetMapping" + i++, ItemsSource = options, SelectedItem = current.Length > 0 ? current : options[0], HorizontalAlignment = HorizontalAlignment.Stretch };
            picker.SelectionChanged += (_, _) => { M(set, "mappings")[key] = picker.SelectedIndex <= 0 ? "" : picker.SelectedItem?.ToString() ?? ""; changed?.Invoke(); };
            content.Children.Add(Field(key, picker));
        }
        return content;
    }
    void ValidateRuleSet(JsonObject set)
    {
        var checkedSet = (JsonObject)set.DeepClone(); checkedSet["enabled"] = true;
        var known = ImportedRuleSets.PortableActions.Concat(Nodes.Reserved).Concat(new[] { "默认代理", "订阅优先", "机场手选", "机场入口" })
                .Concat(session!.Residences.OfType<JsonObject>().Select(x => S(x, "name")))
                .Concat(L(session.Settings, "sources").OfType<JsonObject>().Select(x => "订阅·" + S(x, "name"))).ToHashSet();
        ImportedRuleSets.Validate(O("rule_sets", new JsonArray(checkedSet)), action =>
        {
            if (!known.Contains(action)) throw new BuildException("目标策略不在当前项目中，请重新选择映射");
        });
    }
    async Task MapRuleSetAsync(int index)
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        var target = session; var set = (JsonObject)Map(ImportedRuleSets.Sets(target.Policy)[index], "规则集").DeepClone();
        var dialog = RuleSetWindow("规则集策略映射", 720); var error = Label("", true);
        var save = Button("应用映射", () => { try { ValidateRuleSet(set); dialog.Close(true); } catch (BuildException ex) { error.Text = ex.Message; } }); save.Name = "ApplyRuleSetMapping";
        var cancel = Button("取消", () => dialog.Close(false));
        var layout = new DockPanel { Margin = new Thickness(20) }; var footer = Stack(error, Row(save, cancel)); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = MappingForm(set, () => error.Text = "") }); dialog.Content = layout;
        if (await dialog.ShowDialog<bool>(this) && session == target) ChangeRuleSets(list => list[index] = set.DeepClone());
    }
    async Task ImportRuleSetAsync(int? replace = null)
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        var target = session; JsonObject? old = replace is { } pos ? Map(ImportedRuleSets.Sets(target.Policy)[pos], "规则集") : null;
        var dialog = RuleSetWindow(old is null ? "导入规则集" : "更新规则集");
        using var cancellation = new CancellationTokenSource(); dialog.Closed += (_, _) => cancellation.Cancel();
        var sources = L(target.Settings, "sources").OfType<JsonObject>().ToArray();
        var kind = new ComboBox { Name = "RuleSetSourceKind", ItemsSource = new[] { "粘贴内容", "本地文件", "URL", "项目订阅" }, SelectedIndex = sources.Length > 0 ? 3 : 0, MinWidth = 160 };
        var subscription = new ComboBox { Name = "RuleSetSubscription", ItemsSource = sources.Select(s => S(s, "name") + (B(s, "enabled", true) ? "" : "（已停用）")).ToArray(), SelectedIndex = sources.Length > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var subscriptionStatus = Label("", true); subscriptionStatus.Name = "RuleSetSubscriptionStatus";
        var subscriptionPanel = Stack(Label("选择当前项目的订阅"), subscription, subscriptionStatus);
        bool fetchOffered = false;
        var mode = new ComboBox { Name = "RuleSetInputFormat", ItemsSource = new[] { "自动识别", "完整规则（包含动作）", "匹配名单（不含动作）" }, SelectedIndex = 0, MinWidth = 220 };
        var input = new TextBox { Name = "RuleSetSourceInput", AcceptsReturn = true, MinHeight = 180, MaxHeight = 260, Watermark = "粘贴规则 / 输入文件路径 / 输入 HTTP(S) URL" };
        var error = Label("", true); var preview = new ContentControl(); JsonObject? pending = null; bool reading = false; string? loadedSignature = null;
        var sourcePanel = new Expander { Header = "选择来源与输入内容", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        string SourceSignature() => kind.SelectedIndex + ":" + mode.SelectedIndex + ":" + subscription.SelectedIndex + ":" + input.Text;
        var apply = Button(old is null ? "导入到当前项目" : "替换规则集", () =>
        {
            try { if (pending is null) return; ValidateRuleSet(pending); dialog.Close(true); }
            catch (BuildException ex) { error.Text = ex.Message; }
        }); apply.Name = "ConfirmRuleSetImport"; apply.IsEnabled = false;
        var read = AsyncButton("读取并预览", async () =>
        {
            if (cancellation.IsCancellationRequested || reading) return; reading = true; loadedSignature = SourceSignature();
            apply.IsEnabled = false; error.Text = "正在读取…"; preview.Content = null; pending = null;
            kind.IsEnabled = mode.IsEnabled = input.IsEnabled = subscription.IsEnabled = false;
            try
            {
                string value = input.Text?.Trim() ?? "", format = mode.SelectedIndex == 1 ? "rule" : mode.SelectedIndex == 2 ? "match" : "auto";
                string name = old is null ? "导入规则集" : S(old, "name");
                if (kind.SelectedIndex == 3)
                {
                    if (subscription.SelectedIndex < 0) throw new BuildException("当前项目没有订阅，请先添加订阅，或选择其他导入来源");
                    pending = await SubscriptionRuleImport.ReadAsync(target, subscription.SelectedIndex, fetchOffered, old is null ? null : name, cancellation: cancellation.Token);
                }
                else if (kind.SelectedIndex == 2)
                {
                    if (!ConfigurationValidation.HttpUrl(value)) throw new BuildException("请输入有效的 HTTP(S) URL");
                    using var fetch = new Fetcher(target.ProjectPath, M(target.Settings, "http"), cancellation: cancellation.Token, useCache: false);
                    var loaded = await fetch.ReadAsync(O("url", value), bytes => ImportedRuleSets.Parse(bytes, name, "url", new Uri(value).Host, format), "导入规则集"); pending = Map(loaded.Value, "规则集");
                }
                else if (kind.SelectedIndex == 1)
                {
                    if (new FileInfo(value).Length > 16 * 1024 * 1024) throw new BuildException("规则文件不能超过 16 MiB");
                    var bytes = await File.ReadAllBytesAsync(value, cancellation.Token); pending = ImportedRuleSets.Parse(bytes, name, "file", Path.GetFileName(value), format);
                }
                else pending = ImportedRuleSets.Parse(Encoding.UTF8.GetBytes(value), name, mode: format);
                cancellation.Token.ThrowIfCancellationRequested();
                if (old is not null)
                {
                    pending["id"] = S(old, "id"); pending["enabled"] = B(old, "enabled", true); pending["include_fallback"] = B(old, "include_fallback");
                    foreach (string key in M(pending, "mappings").Select(x => x.Key).ToArray()) if (M(old, "mappings").ContainsKey(key)) M(pending, "mappings")[key] = M(old, "mappings")[key]?.DeepClone();
                }
                preview.Content = Stack(Label(old is null ? "预览完成：确认策略映射后导入。" : ImportedRuleSets.Diff(old, pending)), MappingForm(pending, () => error.Text = ""),
                    AsyncButton("查看导入内容", () => ViewRuleSetAsync(pending, dialog)));
                sourcePanel.IsExpanded = false;
                error.Text = "只导入规则与来源说明，不复制订阅 URL、节点和 DNS。更新时可再次选择项目订阅或其他来源。"; apply.IsEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error.Text = ex is BuildException ? ex.Message : "读取失败，请检查来源是否可访问、编码和文件大小。"; }
            finally { reading = false; kind.IsEnabled = mode.IsEnabled = input.IsEnabled = subscription.IsEnabled = true; }
        }); read.Name = "PreviewRuleSetImport";
        var pick = AsyncButton("选择文件", async () =>
        {
            if (reading) return;
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "选择规则或配置文件", AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) { kind.SelectedIndex = 1; input.Text = path; }
        }); pick.Name = "PickRuleSetFile";
        void RefreshSource()
        {
            bool selected = kind.SelectedIndex == 3;
            subscriptionPanel.IsVisible = selected; input.IsVisible = mode.IsVisible = !selected; pick.IsVisible = kind.SelectedIndex == 1;
            fetchOffered = false; read.Content = "读取并预览"; read.IsEnabled = true;
            if (!selected) return;
            if (subscription.SelectedIndex < 0) { subscriptionStatus.Text = "当前项目没有订阅，请先添加订阅，或选择其他导入来源。"; read.IsEnabled = false; return; }
            try
            {
                var info = SubscriptionRuleImport.Inspect(target, subscription.SelectedIndex);
                subscriptionStatus.Text = info.Status; fetchOffered = info.File is null && info.CanFetch;
                read.Content = fetchOffered ? "拉取规则并预览" : "读取并预览"; read.IsEnabled = info.File is not null || info.CanFetch;
            }
            catch (BuildException) { subscriptionStatus.Text = "订阅配置无法读取，请先检查该订阅及 HTTP 设置。"; read.IsEnabled = false; }
        }
        void Invalidate() { if (SourceSignature() == loadedSignature) return; pending = null; preview.Content = null; apply.IsEnabled = false; }
        input.TextChanged += (_, _) => Invalidate(); kind.SelectionChanged += (_, _) => { Invalidate(); RefreshSource(); }; mode.SelectionChanged += (_, _) => Invalidate();
        subscription.SelectionChanged += (_, _) => { Invalidate(); RefreshSource(); };
        var cancel = Button("取消", () => dialog.Close(false));
        var layout = new DockPanel { Margin = new Thickness(20) }; var footer = Stack(error, Row(apply, cancel)); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        sourcePanel.Content = Stack(Row(kind, mode, pick), subscriptionPanel, input, read); RefreshSource();
        layout.Children.Add(new ScrollViewer { Content = Stack(Label("支持完整配置的 rules、规则 payload 或逐行文本；不跟随外部 RULE-SET 引用。", true), sourcePanel, preview) }); dialog.Content = layout;
        if (await dialog.ShowDialog<bool>(this) && pending is not null && session == target)
        {
            ChangeRuleSets(list => { if (replace is { } index) list[index] = pending.DeepClone(); else list.Push(pending.DeepClone()); });
            SetStatus("规则集已应用到编辑内容；请保存并生成。订阅、节点和 DNS 设置保持不变。");
        }
    }
}

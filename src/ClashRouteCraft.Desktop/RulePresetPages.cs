using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    async Task AddRulePresetsAsync()
    {
        var target = session;
        if (target is null || target.IsReadOnly || IsBusy) return;
        var dialog = RuleSetWindow("添加规则预设", 900, 710);
        var selected = new HashSet<string>();
        IReadOnlyList<RulePreset> catalogue = [];
        var rows = new StackPanel { Spacing = 10 };
        var status = Label("", true, 12); status.Name = "PresetStatus";
        var search = new TextBox { Name = "PresetSearch", Watermark = "搜索分类、名称、域名或 URL" };
        var action = new ComboBox { Name = "PresetTarget", ItemsSource = new[] { "代理", "直连", "拒绝" }, SelectedIndex = 0, MinWidth = 140 };
        string Group() => action.SelectedIndex switch { 1 => "direct", 2 => "reject", _ => "proxy" };
        bool extrasDisabled = UsesSubscriptionBaseline() && !B(target.Policy, "extra_rules_enabled", true);
        var enable = Check("同时启用个人附加规则（也会启用项目中原有个人规则）", false, _ => { });
        enable.Name = "PresetEnableExtras"; enable.IsVisible = extrasDisabled;
        Button confirm = null!;
        void Preview()
        {
            try
            {
                var preview = RulePresets.Append(target.Policy, catalogue.Where(x => selected.Contains(x.Id)), Group());
                status.Text = $"已选 {selected.Count} 个预设；追加 {preview.UrlsAdded} 个 URL、{preview.RulesAdded} 条规则；同组重复跳过 {preview.Skipped} 项。"
                    + (preview.CrossGroupMatches > 0 ? $"\n另有 {preview.CrossGroupMatches} 项也出现在其他组：保留双方，按现有组顺序决定优先级。" : "")
                    + (extrasDisabled ? "\n个人附加规则目前关闭；可勾选启用，或仅添加并保持停用。" : "");
                confirm.IsEnabled = selected.Count > 0;
            }
            catch (Exception ex) { status.Text = SafeMessage(ex); confirm.IsEnabled = false; }
        }
        void Render()
        {
            rows.Children.Clear(); string query = search.Text?.Trim() ?? "";
            var filtered = catalogue.Where(x => (x.Category + " " + x.Name + " " + x.Description + " " + string.Join(' ', x.Urls.Concat(x.Domains).Concat(x.Rules))).Contains(query, StringComparison.OrdinalIgnoreCase));
            foreach (var category in filtered.GroupBy(x => x.Category))
            {
                rows.Children.Add(SectionTitle(category.Key));
                foreach (var preset in category)
                {
                    var pick = Check(preset.Name, selected.Contains(preset.Id), value => { if (value) selected.Add(preset.Id); else selected.Remove(preset.Id); Preview(); });
                    pick.Name = "Preset_" + preset.Id;
                    var detail = string.Join('\n', preset.Urls.Select(x => "URL · " + x).Concat(preset.Domains.Select(x => "域名及子域名 · " + x)).Concat(preset.Rules.Select(x => "规则 · " + x)));
                    rows.Children.Add(Card("", pick, Label(preset.Description, true, 12), new Expander { Header = $"查看内容 · {preset.Urls.Length} 个 URL / {preset.Domains.Length + preset.Rules.Length} 条规则", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Label(detail, true, 12) }));
                }
            }
            if (rows.Children.Count == 0) rows.Children.Add(Label("没有匹配的预设。可修改配置文件后重新加载。", true));
            Preview();
        }
        void Reload()
        {
            try
            {
                catalogue = RulePresets.Load(target.Workspace);
                selected.IntersectWith(catalogue.Select(x => x.Id)); Render();
            }
            catch (Exception ex)
            {
                catalogue = []; selected.Clear(); rows.Children.Clear(); confirm.IsEnabled = false;
                status.Text = "预设文件加载失败，未修改项目：" + SafeMessage(ex);
            }
        }
        confirm = Button("确认追加", () => dialog.Close(true)); confirm.Name = "ConfirmRulePresets"; confirm.Classes.Add("primary");
        var cancel = Button("取消", () => dialog.Close(false)); cancel.Name = "CancelRulePresets";
        var reload = Button("重新加载", Reload); reload.Name = "ReloadRulePresets";
        var open = Button("打开预设配置", () => OpenPath(RulePresets.FilePath(target.Workspace))); open.Name = "OpenRulePresetsFile";
        var header = Stack(Label("选择预设 → 指定直连 / 代理 / 拒绝 → 确认追加。不会覆盖现有规则或改变组顺序。", true, 13),
            Label("预设来自本地配置文件；更新文件后重新加载即可。URL 内容在校验或生成时读取，添加后与原有 URL 名单使用相同逻辑。", true, 12),
            Label(RulePresets.FilePath(target.Workspace), true, 12), Row(open, reload), search);
        var footer = Stack(status, enable, Row(Label("添加到"), action, confirm, cancel));
        var layout = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(header); layout.Children.Add(footer); layout.Children.Add(new ScrollViewer { Content = rows, Margin = new Thickness(0, 12) });
        dialog.Content = layout;
        search.TextChanged += (_, _) => Render(); action.SelectionChanged += (_, _) => Preview(); Reload();
        if (await dialog.ShowDialog<bool>(this) && ReferenceEquals(session, target) && !IsBusy)
        {
            var appended = RulePresets.Append(target.Policy, catalogue.Where(x => selected.Contains(x.Id)), Group());
            if (extrasDisabled && enable.IsChecked == true) appended.Policy["extra_rules_enabled"] = true;
            Mutate("policy.yaml", doc => { doc.Clear(); foreach (var (key, value) in appended.Policy) doc[key] = value?.DeepClone(); });
            policyTab = "rules"; RenderPage();
            SetStatus($"已追加 {appended.UrlsAdded} 个 URL、{appended.RulesAdded} 条规则，跳过 {appended.Skipped} 项重复；请保存并生成。");
        }
    }
}

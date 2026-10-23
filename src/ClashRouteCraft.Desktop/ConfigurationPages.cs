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
    bool UsesSubscriptionBaseline()
    {
        try { return session is not null && SubscriptionBaseline.Enabled(session.Policy); }
        catch (BuildException) { try { return SubscriptionBaseline.Enabled(Map(YamlData.Parse(session!.Documents["policy.yaml"].Baseline), "policy")); } catch (BuildException) { return false; } }
    }
    Control OverviewPage()
    {
        if (session?.IsReadOnly == true) return Card("Example · 只读示例", Label("这是新项目的原始配置。通过左侧添加或复制创建可编辑项目。"), Label("可从左侧查看订阅、住宅出口、规则和 DNS 的默认设置。", true, 13));
        var issues = Issues();
        return Stack(Card(session!.ProjectName, Label(OutputState(), true), IssuesCard(issues), Button("前往生成配置", () => Navigate("build"))),
            Card("配置准备", Row(Button("填写订阅", () => Navigate("sources")), Button("填写住宅出口", () => Navigate("residential")), Button("调整分流规则（可选）", () => Navigate("policy"))),
                Label("首次使用：填写并拉取完整订阅即可生成，住宅出口可选。默认使用第一个启用订阅的规则，DNS 由客户端管理。", true, 13)),
            Card("使用生成结果", Label(UsesDnsPrivacy() ? "已选择 DNS 隐私增强。普通客户端导入主配置；MC2 还需应用配套 Fake-IP DNS，可从 DNS 输出页恢复官方默认。" : UsesSubscriptionBaseline() ? "只需导入生成的主配置；MC2 / 客户端继续管理 DNS。若曾应用增强 DNS，还需在 MC2 恢复。" : session.ReadReport() is null ? "生成成功后，可查看主配置和 MC2 专用 DNS 文件。" : "文件已保存在本地，可导入使用。", true),
                Row(Button("主配置输出", () => NavigateOutput("HomeChain.yaml")), Button("DNS 配置", () => NavigateOutput("HomeChain.MC2DNS.yaml")))));
    }
    Control SourcesPage()
    {
        var settings = session!.Settings; var sources = L(settings, "sources"); var content = new StackPanel { Spacing = 10 };
        var add = Button("＋ 添加并启用订阅", () =>
        {
            MutateSources(list =>
            {
                int suffix = list.Count + 1; string name = "订阅" + suffix;
                while (list.Any(x => S(Map(x, "订阅"), "name").Equals(name, StringComparison.OrdinalIgnoreCase))) name = "订阅" + (++suffix);
                var source = O("name", name, "url", "", "enabled", true, "priority", list.Count + 1); session.Subscriptions.Add(source); list.Push(source); expandedSource = list.Count - 1;
            }); RenderPage();
        }); add.Name = "AddSource"; add.IsEnabled = !IsBusy;
        content.Children.Add(Row(add)); content.Children.Add(Label(UsesSubscriptionBaseline() ? "第一个启用的订阅提供分流规则，归并为本地直连、代理、拦截；其余订阅仅提供节点。上下移动只更换规则来源。机场入口可选聚合组或任一订阅组，组内自动测速；默认筛选香港、日本、台湾、新加坡。所有代理业务使用同一个“代理出口”。" : "上方优先，下方备用。勾选的订阅参与生成；点“编辑”填写链接或调整筛选。", true, 13));
        for (int i = 0; i < sources.Count; i++)
        {
            int index = i; var source = Map(sources[i], "订阅"); string name = S(source, "name"); var snapshot = Label(SnapshotInfo(source), true, 12); Action updateProblems = () => { }; Button? pull = null;
            void Change(string key, JsonNode? value)
            {
                MutateSources(list => Map(list[index], "订阅")[key] = value?.DeepClone()); snapshot.Text = SnapshotInfo(Map(L(session.Settings, "sources")[index], "订阅"));
                if (pull is not null) pull.IsEnabled = !IsBusy && !string.IsNullOrWhiteSpace(S(Map(L(session.Settings, "sources")[index], "订阅"), "url"));
                updateProblems();
            }
            var up = Button("↑", () => MoveSource(index, -1)); up.Name = "SourceUp" + index; up.IsEnabled = index > 0; ToolTip.SetTip(up, UsesSubscriptionBaseline() ? "上移；第一个启用订阅提供规则" : "提高优先级");
            var down = Button("↓", () => MoveSource(index, 1)); down.Name = "SourceDown" + index; down.IsEnabled = index < sources.Count - 1; ToolTip.SetTip(down, UsesSubscriptionBaseline() ? "下移；第一个启用订阅提供规则" : "降低优先级");
            var edit = Button(expandedSource == index ? "收起" : "编辑", () => { expandedSource = expandedSource == index ? -1 : index; RenderPage(); }); edit.Name = "EditSource" + index;
            up.IsEnabled &= !IsBusy; down.IsEnabled &= !IsBusy; edit.IsEnabled = !IsBusy;
            pull = AsyncButton(pullingSource == index ? "取消拉取" : "拉取订阅", () => PullSubscriptionAsync(index)); pull.Name = "PullSource" + index;
            pull.IsEnabled = pullingSource == index || !IsBusy && !string.IsNullOrWhiteSpace(S(source, "url"));
            ToolTip.SetTip(pull, "仅拉取此订阅并保存订阅设置，不生成配置；失败保留原有缓存");
            var summary = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
            summary.Children.Add(SectionTitle($"{index + 1}. {name}")); summary.Children.Add(snapshot); Grid.SetColumn(snapshot, 1);
            var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; header.Children.Add(summary);
            var enabled = Check("启用订阅", B(source, "enabled", true), value => Change("enabled", JsonValue.Create(value))); enabled.IsEnabled = !IsBusy;
            var actions = Row(enabled, up, down, pull, edit); header.Children.Add(actions); Grid.SetColumn(actions, 1);
            var card = Stack(header);
            if (expandedSource == index)
            {
                var rename = AsyncButton("重命名", async () =>
                {
                    if (IsBusy) return;
                    string? value = await PromptAsync("重命名订阅", "订阅名称", name, "保存名称", candidate =>
                    {
                        if (L(session.Settings, "sources").Where((_, j) => j != index).Any(x => S(Map(x, "订阅"), "name").Equals(candidate, StringComparison.OrdinalIgnoreCase))) throw new BuildException("订阅名称不能重复");
                    });
                    if (value is null || value == name || IsBusy) return; session.RenameSource(index, value); RenderPage();
                }); rename.Name = "SourceName" + index;
                var url = TextInput(S(source, "url"), text => Change("url", JsonValue.Create(text)), true); url.Name = "SourceUrl" + index; url.Watermark = "粘贴 HTTP(S) 订阅链接，或导入已有节点文件"; url.IsEnabled = !IsBusy;
                card.Children.Add(Field("订阅链接", url));
                var deleteSource = AsyncButton("删除订阅", async () =>
                {
                    if (IsBusy) return;
                    if (!await ConfirmAsync("删除订阅", "从项目中移除此订阅；原有节点文件保留。保存后生效。", "删除")) return;
                    if (IsBusy) return; session.RemoveSource(index); expandedSource = -1; RenderPage();
                }); deleteSource.Classes.Add("danger");
                card.Children.Add(Row(Check("显示链接", false, show => url.PasswordChar = show ? '\0' : '●'), rename, AsyncButton("导入订阅文件…", () => ImportSnapshotAsync(index)), deleteSource));
                var include = TextInput(S(source, "include", S(M(settings, "selection"), "include", UsesSubscriptionBaseline() ? SubscriptionBaseline.JumpInclude : Nodes.Asia)), value => Change("include", JsonValue.Create(value))); include.Name = "Sourceinclude" + index;
                var exclude = TextInput(S(source, "exclude", S(M(settings, "selection"), "exclude", UsesSubscriptionBaseline() ? SubscriptionBaseline.JumpExclude : "")), value => Change("exclude", JsonValue.Create(value))); exclude.Name = "Sourceexclude" + index;
                card.Children.Add(new Expander { Header = "此订阅的筛选与请求选项", IsExpanded = sourceAdvanced, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = Stack(Pair(Field("保留节点（正则，可留空）", include), Field("排除节点（正则）", exclude)), Field("请求客户端标识", TextInput(S(source, "user_agent", "clash-verge/v2.4.7"), value => Change("user_agent", JsonValue.Create(value))))) });
                var problems = Label("", true, 12); problems.Name = "SourceProblems" + index;
                updateProblems = () => { problems.Text = string.Join('\n', Issues().Where(x => x.Page == "sources" && x.Field.EndsWith(index.ToString())).Select(x => x.Message)); problems.IsVisible = problems.Text.Length > 0; };
                updateProblems(); card.Children.Add(problems);
            }
            content.Children.Add(ItemCard(card));
        }
        var selection = M(settings, "selection");
        void SetSelection(string key, JsonNode value) => Mutate("settings.yaml", doc => { if (doc["selection"] is null) doc["selection"] = new JsonObject(); M(doc, "selection")[key] = value.DeepClone(); });
        var interval = new NumericUpDown { Value = I(selection, "interval", 300), Minimum = 1, Maximum = 86400, Increment = 30, FormatString = "0" }; interval.ValueChanged += (_, _) => { if (interval.Value is { } value) SetSelection("interval", JsonValue.Create((int)value)!); };
        var tolerance = new NumericUpDown { Value = I(selection, "tolerance", 50), Minimum = 0, Maximum = 10000, Increment = 10, FormatString = "0" }; tolerance.ValueChanged += (_, _) => { if (tolerance.Value is { } value) SetSelection("tolerance", JsonValue.Create((int)value)!); };
        var globalFilters = Stack(Pair(Field("保留节点（正则）", TextInput(S(selection, "include", UsesSubscriptionBaseline() ? SubscriptionBaseline.JumpInclude : Nodes.Asia), value => SetSelection("include", JsonValue.Create(value)!))), Field("排除节点（正则）", TextInput(S(selection, "exclude", UsesSubscriptionBaseline() ? SubscriptionBaseline.JumpExclude : ""), value => SetSelection("exclude", JsonValue.Create(value)!)))));
        globalFilters.Children.Add(Pair(Field("健康检查间隔（秒）", interval), Field("延迟容差（毫秒）", tolerance)));
        content.Children.Add(new Expander { Name = "SourceGlobalFilters", Header = UsesSubscriptionBaseline() ? "机场入口的默认筛选与测速（高级）" : "所有订阅的默认筛选与健康检查（高级）", HorizontalAlignment = HorizontalAlignment.Stretch, Content = globalFilters });
        return content;
    }
    string SnapshotInfo(JsonObject source)
    {
        try
        {
            string name = S(source, "name"), path = session!.SnapshotPath(name);
            if (!File.Exists(path)) return "尚无节点 · 填写链接后获取，或导入文件";
            string prefix = session.Subscriptions.UrlChanged(source) ? "链接已修改，当前仍为旧节点 · " : "";
            var document = Nodes.Document(File.ReadAllBytes(path)); int count = L(document, "proxies").Count;
            string routing = UsesSubscriptionBaseline() && SubscriptionBaseline.IsPrimary(session.Settings, name) ? SubscriptionBaseline.RuleCount(document) > 0 ? $" · 主订阅，{SubscriptionBaseline.RuleCount(document):N0} 条规则" : " · 主订阅缺少规则，请重新拉取完整订阅" : "";
            return prefix + $"{count} 个节点{routing} · 更新于 {File.GetLastWriteTime(path):MM-dd HH:mm}";
        }
        catch (Exception ex) when (ex is BuildException or IOException) { return "节点文件需要检查"; }
    }
    void MoveSource(int index, int offset)
    {
        MutateSources(list => { int target = index + offset; if (target < 0 || target >= list.Count) return; var item = list[index]?.DeepClone(); list.RemoveAt(index); list.Insert(target, item); if (expandedSource == index) expandedSource = target; else if (expandedSource == target) expandedSource = index; }); RenderPage();
    }
    void MutateSources(Action<JsonArray> change) => Mutate("settings.yaml", doc => { if (doc["sources"] is null) doc["sources"] = new JsonArray(); change(L(doc, "sources")); ProjectOrdering.NumberSources(doc); });
    async Task ImportSnapshotAsync(int index)
    {
        if (IsBusy) return;
        var source = Map(L(session!.Settings, "sources")[index], "订阅"); string name = S(source, "name");
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择完整订阅配置或节点文件", AllowMultiple = false, FileTypeFilter = [new("YAML / JSON") { Patterns = ["*.yaml", "*.yml", "*.json"] }] });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var document = Nodes.Document(await File.ReadAllBytesAsync(path));
        if (M(document, "proxy-providers").Count > 0) throw new BuildException("此文件仍需下载外部节点，请使用更新订阅生成，或选择包含完整节点的文件");
        if (L(document, "proxies").Count == 0) throw new BuildException("所选文件没有可用节点");
        ConfigurationValidation.CacheNodes(L(document, "proxies"));
        bool nodesOnly = UsesSubscriptionBaseline() && !SubscriptionBaseline.IsPrimary(session.Settings, name);
        if (nodesOnly) document = SubscriptionHosts.NodeDocument(document, L(document, "proxies"), name);
        using (var fetch = new Fetcher(Path.GetDirectoryName(path)!, O("cache_dir", Path.Combine(session.Workspace, ".cache")), offline: true))
            document = await SubscriptionBaseline.SnapshotAsync(document, fetch, Path.GetDirectoryName(path), name);
        if (UsesSubscriptionBaseline() && SubscriptionBaseline.IsPrimary(session.Settings, name)) SubscriptionBaseline.RequireRules(document, name);
        var effective = ConfigurationValidation.ExpandSettings(session.Settings);
        if (UsesSubscriptionBaseline()) SubscriptionBaseline.Eligible(L(document, "proxies"), Map(L(effective, "sources")[index], "订阅"), M(effective, "selection"));
        else Nodes.Eligible(Nodes.Namespace(L(document, "proxies"), name), Map(L(effective, "sources")[index], "订阅"), M(effective, "selection"));
        string retained = nodesOnly ? "保留节点及其 hosts 解析依赖" : "保留规则、策略组及 hosts 解析依赖";
        if (!await ConfirmAsync("导入订阅并保存项目", "将所选文件关联到“" + name + "”，" + retained + "，并保存当前项目修改。已有缓存将被替换。", "导入并保存")) return;
        string pending = session.Subscriptions.Text;
        try { session.Subscriptions.MarkUpdated(source, "import"); session.Save([(session.SnapshotPath(name), SubscriptionUpdater.Payload(document))]); }
        catch { session.Subscriptions.RestorePending(pending); throw; }
        SetStatus("订阅已导入，项目已保存；生成后更新输出"); RenderPage();
    }
    Control ResidencesPage()
    {
        var values = session!.Residences; var content = new StackPanel { Spacing = 10 };
        var add = Button("＋ 添加住宅", () => { string name = session!.NextResidenceName(); MutateResidence(list => { list.Push(O("name", name, "type", "socks5", "server", "", "port", 12345, "username", "", "password", "", "udp", true)); expandedResidence = list.Count - 1; }); RenderPage(); }); add.Name = "AddResidence";
        content.Children.Add(Row(add)); content.Children.Add(Label(UsesSubscriptionBaseline() ? "每个住宅生成“[链式] 节点名”和“[直连] 节点名”两条路径，链式路径经机场入口连接住宅。在客户端的“代理出口”统一选择住宅出口或机场入口；有住宅时默认使用第一个链式住宅。此处的“[直连]”表示直接连接住宅代理，业务仍从住宅 IP 出站。" : "填写服务商提供的住宅代理信息。上方节点作为首次默认出口；实际使用以设备面板选择为准。", true, 13));
        for (int i = 0; i < values.Count; i++)
        {
            int index = i; var node = Map(values[i], "住宅"); void Change(string key, JsonNode? value) => MutateResidence(list => Map(list[index], "住宅")[key] = value?.DeepClone());
            var up = Button("↑", () => MoveResidence(index, -1)); up.IsEnabled = index > 0; var down = Button("↓", () => MoveResidence(index, 1)); down.IsEnabled = index < values.Count - 1;
            var edit = Button(expandedResidence == index ? "收起" : "编辑", () => { expandedResidence = expandedResidence == index ? -1 : index; RenderPage(); }); edit.Name = "EditResidence" + index;
            var head = new Grid { ColumnDefinitions = new("*,Auto") }; head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { SectionTitle($"{index + 1}. {S(node, "name")}"), Label(S(node, "server").Length > 0 ? S(node, "server") + ":" + I(node, "port", 0) : "服务器待填写", true, 12) } });
            var actions = Row(up, down, ResidenceCheckButton(index), edit); head.Children.Add(actions); Grid.SetColumn(actions, 1); var card = Stack(head);
            if (expandedResidence == index)
            {
                TextBox Input(string key, string field, bool secret = false) { var input = TextInput(S(node, key), value => Change(key, JsonValue.Create(value)), secret); input.Name = "Residence" + field + index; return input; }
                string type = S(node, "type", "socks5"); var protocol = new ComboBox { ItemsSource = new[] { "socks5", "http", type }.Distinct().ToArray(), SelectedItem = type, HorizontalAlignment = HorizontalAlignment.Stretch }; protocol.SelectionChanged += (_, _) => Change("type", JsonValue.Create(protocol.SelectedItem?.ToString() ?? "socks5"));
                var port = new NumericUpDown { Name = "ResidencePort" + index, Value = I(node, "port", 12345), Minimum = 1, Maximum = 65535, FormatString = "0" }; port.ValueChanged += (_, _) => { if (port.Value is { } value) Change("port", JsonValue.Create((int)value)); };
                var server = Input("server", "Server"); server.Watermark = "例如 proxy.example.com"; var password = Input("password", "password", true);
                var rename = AsyncButton("重命名", async () =>
                {
                    if (IsBusy) return;
                    string? value = await PromptAsync("重命名住宅", "住宅名称", S(node, "name"), "保存名称", candidate =>
                    {
                        if (session.Residences.Where((_, j) => j != index).Any(x => S(Map(x, "住宅"), "name") == candidate)) throw new BuildException("住宅名称不能重复");
                    });
                    if (value is null || IsBusy) return; session.RenameResidence(index, value); RenderPage();
                }); rename.Name = "ResidenceName" + index;
                card.Children.Add(Field("协议", protocol)); card.Children.Add(Pair(Field("服务器", server), Field("端口", port)));
                card.Children.Add(Pair(Field("用户名（按服务商要求）", Input("username", "username")), Field("密码", password)));
                var deleteResidence = AsyncButton("删除住宅", async () =>
                { if (IsBusy) return; if (await ConfirmAsync("删除住宅节点", "从项目中移除此节点，保存并生成后更新输出。", "删除") && !IsBusy) { session.RemoveResidence(index); expandedResidence = -1; RenderPage(); } }); deleteResidence.Classes.Add("danger");
                card.Children.Add(Row(Check("允许 UDP 转发", B(node, "udp"), value => Change("udp", JsonValue.Create(value))), Check("显示密码", false, show => password.PasswordChar = show ? '\0' : '●'), rename, deleteResidence));
                card.Children.Add(Label("UDP 是否可用还取决于服务商与实际链路；凭据也支持环境变量引用。", true, 12));
            }
            content.Children.Add(ItemCard(card));
        }
        return content;
    }
    void MoveResidence(int index, int offset)
    {
        MutateResidence(list => { int target = index + offset; if (target < 0 || target >= list.Count) return; var value = list[index]?.DeepClone(); list.RemoveAt(index); list.Insert(target, value); if (expandedResidence == index) expandedResidence = target; else if (expandedResidence == target) expandedResidence = index; }); RenderPage();
    }
    Control PolicyPage()
    {
        bool baseline = SubscriptionBaseline.Enabled(session!.Policy);
        int setCount = ImportedRuleSets.Sets(session.Policy).Count;
        if (baseline && setCount == 0 && policyTab == "sets") policyTab = "rules";
        Button Tab(string text, Action clicked, bool selected) { var b = new Button { Content = text }; b.Classes.Add("tab"); if (selected) b.Classes.Add("selected"); b.Click += (_, _) => Try(clicked); return b; }
        var rulesTab = Tab("分流规则", () => { policyTab = "rules"; RenderPage(); }, policyTab == "rules" || baseline && policyTab == "sets"); rulesTab.Name = "PolicyRulesTab";
        var dnsTab = Tab("DNS 策略", () => { policyTab = "dns"; RenderPage(); }, policyTab == "dns"); dnsTab.Name = "PolicyDnsTab";
        var tabs = Row(rulesTab);
        if (!baseline)
        {
            var setsTab = Tab($"导入规则集 ({setCount})", () => { policyTab = "sets"; RenderPage(); }, policyTab == "sets"); setsTab.Name = "PolicyRuleSetsTab";
            tabs.Children.Add(setsTab);
        }
        tabs.Children.Add(dnsTab);
        var toolbar = new DockPanel { LastChildFill = true };
        if (policyTab is "rules" or "sets")
        {
            var saveDefault = AsyncButton("保存为 Example 规则", SaveRuleDefaultsAsync); saveDefault.Name = "SaveRuleDefaults";
            var restoreDefault = AsyncButton("应用 Example 规则", ApplyExampleRulesAsync); restoreDefault.Name = "ApplyExampleRules";
            saveDefault.IsEnabled = restoreDefault.IsEnabled = session?.IsReadOnly != true && !IsBusy;
            ToolTip.SetTip(saveDefault, "将当前分流规则写入 Example；不复制订阅、住宅或 DNS");
            ToolTip.SetTip(restoreDefault, "将 Example 的全部规则（含导入规则集）应用到当前项目；保留订阅、节点、住宅出口和 DNS 配置。应用后保存并生成。");
            var actions = Row(saveDefault, restoreDefault); DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        }
        toolbar.Children.Add(tabs);
        policyTabsHost.Content = toolbar; policyTabsHost.IsVisible = true;
        if (policyTab == "dns") return DnsForm();
        var useSubscription = Check("使用第一个启用订阅的规则（推荐）", baseline, value => { Mutate("policy.yaml", doc => doc["rule_source"] = value ? "subscription" : "custom"); RenderPage(); }); useSubscription.Name = "UseSubscriptionBaseline";
        var ordered = (JsonObject)session!.Settings.DeepClone(); ProjectOrdering.NumberSources(ordered);
        string source = SubscriptionBaseline.Primary(ordered) is { } primary ? S(primary, "name") : "尚未启用订阅";
        return Stack(Card("规则来源", useSubscription, Label(baseline ? $"当前来源：{source}。使用该订阅的匹配条件和顺序，动作归并为三类；其他订阅只提供节点。自定义规则优先于订阅规则，无需再次导入规则集。" : "当前使用旧的自定义编排。勾选上方即可切换到订阅基线；保留现有配置内容，保存并重新生成后生效。", true, 13)), AsnPolicyCard(), policyTab == "sets" ? RuleSetsForm() : RuleForms());
    }
    Control UnifiedRoutingOverview()
    {
        var help = Label("优先顺序：自定义规则 → 主订阅规则。订阅内部保持原顺序，只归并动作。代理业务统一使用“代理出口”，有住宅时默认住宅，否则使用机场入口。", true, 12); help.Name = "UnifiedRoutingHelp";
        var card = Card("三类分流", help,
            Label("本地直连 · 使用本机网络，吸收订阅的直连规划。", true, 12),
            Label("代理 · 统一从“代理出口”出站，在客户端面板切换住宅或机场。", true, 12),
            Label("拦截 · 合并拒绝、广告拦截等规则，不再分别设置开关。", true, 12),
            Label("未命中时沿用主订阅兜底规则的动作，不再单独创建“漏网之鱼”分组。", true, 12));
        var details = Stack(Label("手选组沿 default-selected 或首个候选递归归类，不按分组名称猜测。以下是订阅文件的默认规划，不读取路由器保存的选择。", true, 12));
        try
        {
            var ordered = (JsonObject)session!.Settings.DeepClone(); ProjectOrdering.NumberSources(ordered);
            var source = SubscriptionBaseline.Primary(ordered);
            string path = source is null ? "" : session.SnapshotPath(S(source, "name"));
            if (!File.Exists(path)) details.Children.Add(Label("拉取主订阅后可查看归并结果。", true, 12));
            else
            {
                var document = Nodes.Document(File.ReadAllBytes(path));
                var routing = new SubscriptionRouting(document);
                routing.Rewrite(L(document, "rules"), SubscriptionRouting.ProxyGroup); routing.RewriteSubRules(SubscriptionRouting.ProxyGroup);
                var mapping = routing.Mapping(SubscriptionRouting.ProxyGroup);
                foreach (var item in mapping.OfType<JsonObject>())
                {
                    string action = S(item, "action") switch { "DIRECT" => "本地直连", "REJECT" or "REJECT-DROP" => "拦截", "PASS" or "PASS-RULE" => "继续匹配", _ => "代理（统一出口）" };
                    details.Children.Add(Label(S(item, "source") + " → " + action, true, 12));
                }
                if (mapping.Count == 0) details.Children.Add(Label("订阅直接使用节点或内置动作，按同样的三类方式归并。", true, 12));
            }
        }
        catch (Exception ex) when (ex is BuildException or IOException or UnauthorizedAccessException)
        { details.Children.Add(Label("暂不能预览归并，请检查主订阅：" + SafeMessage(ex), true, 12)); }
        return Stack(card, new Expander { Name = "SubscriptionRoutingPreview", Header = "查看订阅策略归并", HorizontalAlignment = HorizontalAlignment.Stretch, Content = details });
    }
    async Task SaveRuleDefaultsAsync()
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        var policy = session.Policy; ParameterInspection.ValidateRules(policy);
        if (!await ConfirmAsync("保存分流默认配置", "将当前分流规则（包括尚未保存的编辑）覆盖到 Example，之后新建项目会使用它。订阅、住宅和 DNS 不受影响。", "覆盖默认")) return;
        RuleDefaults.Save(session.Workspace, policy);
    }
    async Task ApplyExampleRulesAsync()
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        var target = session;
        var defaults = RuleDefaults.Read(target.Workspace);
        var sets = ImportedRuleSets.Sets(defaults); int count = sets.OfType<JsonObject>().Sum(x => L(x, "rules").Count);
        if (!await ConfirmAsync("应用 Example 规则", $"将 Example 的全部分流规则（含 {sets.Count} 个规则集、{count:N0} 条导入规则）、规则顺序和兜底动作替换到当前项目。当前尚未保存的规则编辑也会被替换。\n\n保留订阅、节点、住宅出口、DNS 配置及 DNS 开关。应用后仍需保存、生成；若已开启 DNS 按规则分流，生成的 DNS 域名规则集会随新规则更新。", "应用规则")) return;
        if (session != target || target.IsReadOnly || IsBusy) return;
        var restored = RuleDefaults.Apply(target.Policy, defaults);
        target.Documents["policy.yaml"].Set(restored); RenderPage();
        SetStatus($"已应用 Example 规则（含 {sets.Count} 个规则集、{count:N0} 条导入规则）；订阅、节点和 DNS 配置已保留。请保存并重新生成。");
    }
    Control RuleForms()
    {
        var policy = session!.Policy; var order = ProjectOrdering.RuleOrder(policy).ToList(); var content = new StackPanel { Spacing = 10 };
        bool baseline = SubscriptionBaseline.Enabled(policy);
        if (baseline)
        {
            content.Children.Add(UnifiedRoutingOverview());
            var extras = Check("启用个人附加规则", B(policy, "extra_rules_enabled", true), value => { Mutate("policy.yaml", doc => doc["extra_rules_enabled"] = value); RenderPage(); }); extras.Name = "EnableExtraRules";
            content.Children.Add(extras);
            if (!B(policy, "extra_rules_enabled", true)) content.Children.Add(Note("附加规则已停用，内容仍保留。当前完全使用主订阅规则；可随时重新启用。"));
        }
        string GroupName(string group) => group switch { "direct" => baseline ? "本地直连" : "直连", "reject" => "拦截", _ => "代理" };
        var orderHint = Label("按组从上到下匹配：上一组的全部规则优先于下一组，包含手工、URL 和已导入规则。", true, 12); orderHint.Name = "RuleOrderHint";
        ToolTip.SetTip(orderHint, baseline ? "全部自定义规则按下方三组排序，随后匹配主订阅规则。旧 first 和导入规则按实际动作归组，不再单独置顶。组内保留原有相对顺序，冲突时先命中的规则生效。" : "全部自定义规则按下方三组排序，随后匹配国内分流和默认动作。旧 first 和导入规则按实际动作归组；组内保留原有相对顺序，冲突时先命中的规则生效。"); content.Children.Add(orderHint);
        var imported = ImportedRuleSets.Sets(policy);
        if (!baseline || imported.Count > 0)
        {
            var browse = Button(baseline ? "管理已有规则集" : "查看导入规则集", () => { policyTab = "sets"; RenderPage(); }); browse.Name = "BrowseRuleSets";
            string hint = baseline ? "保留旧项目已有的附加规则集，可查看、停用或移除。" + (B(policy, "extra_rules_enabled", true) ? "启用的规则集仍优先于主订阅匹配。" : "个人附加规则已关闭，这些规则集暂不生效。") : "以下个人名单优先匹配，导入规则集随后按顺序匹配。分类与搜索不改变执行顺序。";
            content.Children.Add(Card($"{(baseline ? "已有附加规则集" : "导入规则集")} · {imported.Count} 个 · {imported.OfType<JsonObject>().Sum(x => L(x, "rules").Count):N0} 条", Label(hint, true, 12), browse));
        }
        var groups = new StackPanel { Name = "RemoteRuleGroups", Spacing = 10 }; var remotes = Rules.RemoteSpecs(policy["remote_rules"]).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int index = i; string group = order[i];
            void Move(int offset) { var current = ProjectOrdering.RuleOrder(session.Policy).ToList(); int target = index + offset; if (target < 0 || target >= current.Count) return; (current[index], current[target]) = (current[target], current[index]); Mutate("policy.yaml", doc => doc["order"] = A(current)); RenderPage(); }
            var up = IconButton("RuleUp_" + group, "提高" + GroupName(group) + "组优先级", "M3,7 L8,2 L13,7 M8,2 L8,14", () => { Move(-1); return Task.CompletedTask; }); up.Name = "RuleUp_" + group; up.IsEnabled = i > 0; var down = IconButton("RuleDown_" + group, "降低" + GroupName(group) + "组优先级", "M3,9 L8,14 L13,9 M8,14 L8,2", () => { Move(1); return Task.CompletedTask; }); down.Name = "RuleDown_" + group; down.IsEnabled = i < order.Count - 1;
            var groupHeader = new Grid { ColumnDefinitions = new("*,Auto") }; groupHeader.Children.Add(SectionTitle($"{i + 1}. {GroupName(group)}")); var moves = Row(up, down); groupHeader.Children.Add(moves); Grid.SetColumn(moves, 1);
            var manual = ManualRuleFields(group, policy, out var lanOption);
            var fields = Stack();
            if (lanOption is not null) fields.Children.Add(lanOption);
            fields.Children.Add(RemoteUrlField(group, GroupName(group), remotes)); fields.Children.Add(manual);
            fields.Margin = new(UiMetrics.Inset, 0, 0, 0);
            groups.Children.Add(Card("", groupHeader, fields));
        }
        policyFocusField = "";
        content.Children.Add(groups); int advanced = remotes.Count(x => !ProjectOrdering.IsUrlEntry(x)); if (advanced > 0) content.Children.Add(Label($"另有 {advanced} 条高级来源，保留在高级 YAML 中维护。", true, 12));
        if (baseline) { groups.IsEnabled = B(policy, "extra_rules_enabled", true); content.Children.Add(Note("以上自定义规则优先于订阅规则；所有代理动作共用“代理出口”。订阅的规则依赖和兜底动作保留，DNS 策略单独设置。")); return content; }
        string currentAction = S(policy, "final", "PROXY");
        var options = new[] { ("PROXY", "默认代理 · 使用面板选择的代理路径"), ("DIRECT", "直连 · 不经过代理"), ("REJECT", "拒绝 · 阻止连接"), ("AIRPORT", "机场入口 · 仅经过机场"), ("住宅出口", "住宅出口 · 经机场连接住宅") }.Select(x => new ComboBoxItem { Tag = x.Item1, Content = x.Item2 }).ToList();
        if (!options.Any(x => Equals(x.Tag, currentAction))) options.Add(new() { Tag = currentAction, Content = "已有自定义动作 · " + currentAction });
        var final = new ComboBox { Name = "FinalAction", ItemsSource = options, SelectedItem = options.Single(x => Equals(x.Tag, currentAction)), HorizontalAlignment = HorizontalAlignment.Stretch };
        final.SelectionChanged += (_, _) => { if (final.SelectedItem is ComboBoxItem { Tag: string value } && value != S(session.Policy, "final", "PROXY")) Mutate("policy.yaml", doc => doc["final"] = value); };
        content.Children.Add(Card("未命中名单时", Check("国内网站默认直连", B(policy, "domestic_direct", true), value => Mutate("policy.yaml", doc => doc["domestic_direct"] = value)), Field("其余流量的默认动作", final)));
        var embedGeo = Check("兼容 MC2 原始数据库：内嵌 GEO / ASN 名单", GeoRules.Mode(policy) == "mc2-inline", value => Mutate("policy.yaml", doc => doc["geo_mode"] = value ? "mc2-inline" : "native"));
        embedGeo.Name = "EmbedGeoForMc2";
        content.Children.Add(new Expander { Header = "GEO 数据（高级）", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Stack(embedGeo, Label("默认保留原生 GEO / ASN 规则，由客户端数据库处理。仅在无法更新 MC2 数据库时启用内嵌；这会显著增大 YAML，名单随重新生成更新。", true, 12)) });
        return content;
    }
    static IEnumerable<string> Lines(string text) => text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0);
    static void FitListInput(TextBox input)
    {
        input.MinHeight = UiMetrics.ListMinimum; input.MaxHeight = double.PositiveInfinity; input.VerticalAlignment = VerticalAlignment.Top;
        ScrollViewer.SetHorizontalScrollBarVisibility(input, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(input, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
    }
    Control DnsForm()
    {
        if (SubscriptionBaseline.Enabled(session!.Policy) || DnsPrivacy.Enabled(session.Policy)) return DnsPrivacyForm();
        var dns = M(session!.Policy, "dns"); void Set(string key, JsonNode value) => Mutate("policy.yaml", doc => { if (doc["dns"] is null) doc["dns"] = new JsonObject(); M(doc, "dns")[key] = value.DeepClone(); });
        var apply = Button("一键应用推荐 DNS 策略", () => { if (session.IsReadOnly || IsBusy) return; session.Documents["policy.yaml"].Set(DnsSettings.ApplyRecommended(session.Policy)); RenderPage(); SetStatus("推荐 DNS 策略已填入；请保存并重新生成，再更新主配置与 MC2 DNS。"); }); apply.Name = "ApplyRecommendedDns";
        var follow = Check("DNS 解析按自定义域名规则分流", B(session.Policy, "dns_follow_rules"), value => Mutate("policy.yaml", doc => doc["dns_follow_rules"] = value)); follow.Name = "DnsFollowDomainRules";
        var domestic = Check("国内域名使用国内直连 DNS", DnsSettings.DomesticEnabled(session.Policy), value => { Mutate("policy.yaml", doc => DnsSettings.SetDomesticEnabled(doc, value)); RenderPage(); }); domestic.Name = "DnsDomesticDirect";
        var content = Stack(DnsPrivacyForm(), Card("当前 DNS 策略", Label(B(dns, "enable") ? "DNS 已启用。修改后保存并重新生成，再同步主配置与 MC2 DNS。" : "DNS 已关闭，可一键应用推荐策略。"),
            apply, Label("将 DNS 服务器设为国内直连 / 海外住宅出口，并开启以下两项。保留监听端口、解析模式、IPv6、QUIC 和其他自定义 DNS 条目；不立即保存或修改路由器。", true, 13)),
            Card("解析策略", follow, Label("决定域名由哪组 DNS 解析：按自定义规则原顺序，直连域名用直连 DNS，代理域名用海外 DNS，随后才进入兜底。", true, 13),
                domestic, Label("国内域名库作为兜底，应用推荐 DNS 时开启；使用下方直连 DNS 服务器。关闭仅移除国内库兜底，已指定直连的域名仍按直连策略解析。", true, 13)),
            Button(dnsAdvanced ? "收起自定义 DNS" : "自定义 DNS（高级）", () => { dnsAdvanced = !dnsAdvanced; RenderPage(); }));
        if (!dnsAdvanced) return content;
        var transport = Check("DNS 请求的连接路径跟随流量规则（高级）", B(dns, "respect-rules"), value => Set("respect-rules", JsonValue.Create(value)!)); transport.Name = "DnsTransportRespectRules";
        content.Children.Add(Card("DNS 连接行为", Row(Check("启用 DNS", B(dns, "enable"), value => Set("enable", JsonValue.Create(value)!)), Check("启用 IPv6", B(dns, "ipv6"), value => Set("ipv6", JsonValue.Create(value)!))), transport,
            Label("只控制访问 DNS 服务器的网络路径，不决定域名选哪组 DNS。推荐关闭；当前 DNS 地址已用 #DIRECT / #住宅出口 指定路径。IP、进程、端口条件仍由流量规则处理。", true, 13)));
        foreach (var (key, title, hint) in new[] { ("default-nameserver", "引导解析", "解析 DNS 服务器自身域名"), ("proxy-server-nameserver", "代理服务器域名解析", "通常通过直连解析，避免循环"), ("nameserver", "普通网站查询", "未命中 nameserver-policy 时使用；默认经住宅出口查询海外 DNS"), ("direct-nameserver", "直连网站查询", "直连连接时重新解析；默认使用国内 DNS 并指定 #DIRECT"), ("fallback", "后备查询", "按内核后备筛选条件启用") })
        { string field = key; var input = TextInput(string.Join('\n', L(dns, key).Select(x => S(x))), text => { if (field == "direct-nameserver") Mutate("policy.yaml", doc => DnsSettings.SetDirectResolvers(doc, A(Lines(text)))); else Set(field, A(Lines(text))); }, multiline: true); input.Name = "Dns_" + key; FitListInput(input); ToolTip.SetTip(input, hint); content.Children.Add(Card(title, input)); }
        return content;
    }
    Control YamlPage()
    {
        var picker = new ComboBox { ItemsSource = session!.Documents.Keys, SelectedItem = yamlFile, MinWidth = 220 }; picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is string file && file != yamlFile) { yamlFile = file; RenderPage(); } };
        var document = session.Documents[yamlFile]; var editor = Editor(document.Text, session.IsReadOnly);  var validation = Label("未修改的文本和注释会保留。", true, 12); var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); try { document.Parse(); validation.Text = "语法通过；生成时继续检查字段与兼容性。"; validation.Foreground = Muted; } catch (Exception ex) { validation.Text = SafeMessage(ex); validation.Foreground = Danger; } };
        editor.TextChanged += (_, _) => { document.SetText(editor.Text); UpdateFooter(); timer.Stop(); timer.Start(); }; editor.DetachedFromVisualTree += (_, _) => timer.Stop();
        return Stack(Row(picker, AsyncButton("撤销当前文件修改", async () => { if (await ConfirmAsync("撤销编辑", "恢复当前文件到上次载入或保存的内容？", "恢复")) { document.SetText(document.Baseline); RenderPage(); } })), Card("", editor, validation));
    }
}

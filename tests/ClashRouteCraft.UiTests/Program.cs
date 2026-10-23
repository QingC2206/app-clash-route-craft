using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using ClashRouteCraft.Core;
using ClashRouteCraft.Desktop;
using static ClashRouteCraft.Core.Data;

string output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/ui"); Directory.CreateDirectory(output);
string workspace = Path.Combine(Path.GetTempPath(), "homechain-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
AppBuilder.Configure<App>().UseSkia().UseHeadless(new() { UseHeadlessDrawing = false }).SetupWithoutStarting();
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
try
{
    if (args.Contains("--readme")) { ReadmeScreenshots.Run(workspace, output); return; }
    // A fresh installation must never discover or relocate another workspace.
    var externalWorkspace = Path.Combine(workspace, "external-owner");
    Directory.CreateDirectory(externalWorkspace);
    WorkspaceSession.CreateProject(externalWorkspace, "PrivateFixture");
    var externalInput = Path.Combine(externalWorkspace, "config", "PrivateFixture", "settings.yaml");
    var originalInput = File.ReadAllBytes(externalInput);
    var cleanInstallation = Path.Combine(externalWorkspace, "fresh-install");
    Directory.CreateDirectory(cleanInstallation);
    var cleanPreferences = new AppPreferences(applicationDirectory: cleanInstallation);
    Check(cleanPreferences.Workspace.Length == 0 && cleanPreferences.RecentPage == "overview", "Fresh installation inherited machine-wide preferences");
    cleanPreferences.Workspace = externalWorkspace;
    cleanPreferences.Save();
    var previousCurrentDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = externalWorkspace;
        var cleanWindow = new MainWindow(new AppPreferences(applicationDirectory: cleanInstallation));
        Check(BuildEngine.DiscoverProjects(cleanInstallation).Count == 0 && File.Exists(Path.Combine(cleanInstallation, "config", "Example", "settings.yaml")), "Fresh startup imported an external project instead of restoring generic Example");
        Check(File.ReadAllBytes(externalInput).SequenceEqual(originalInput), "Fresh startup moved or changed external data");
        Check(new AppPreferences(applicationDirectory: cleanInstallation).Workspace == cleanInstallation, "Portable startup followed an external saved workspace");
        cleanWindow.Close();
    }
    finally { Environment.CurrentDirectory = previousCurrentDirectory; }
    WorkspaceSession.CreateProject(workspace, "SampleOne"); WorkspaceSession.CreateProject(workspace, "SampleTwo");
    var fixture = new WorkspaceSession(workspace, "SampleOne"); var settings = fixture.Settings; settings["validation"] = O(); fixture.Documents["settings.yaml"].Set(settings);
    L(settings, "sources").Push(O("name", "AirportB", "url", "https://example.invalid/b", "enabled", false, "priority", -100)); fixture.Documents["settings.yaml"].Set(settings);
    var residences = A(new System.Text.Json.Nodes.JsonNode?[] { O("name", "住宅-1", "type", "socks5", "server", "127.0.0.1", "port", 12345, "username", "demo", "password", "demo", "udp", true) }); fixture.SetResidences(residences);
    var policy = DnsSettings.ApplyRecommended(fixture.Policy); policy["rule_source"] = "custom"; policy["domestic_direct"] = false; policy["geosite_sources"] = O(); fixture.Documents["policy.yaml"].Set(policy); fixture.Save();
    File.WriteAllBytes(Path.Combine(fixture.ProjectPath, "subscrip", "AirportA.yaml"), YamlData.Bytes(O("proxies", A(new System.Text.Json.Nodes.JsonNode?[] { O("name", "香港-示例", "type", "http", "server", "127.0.0.1", "port", 8080) }))));
    var generated = Task.Run(() => new BuildEngine().BuildProjectAsync(workspace, "SampleOne", new(Offline: true))).GetAwaiter().GetResult();
    // Synthetic passed flags exercise output UI only, never a router.
    var fixtureReport = generated.Report; fixtureReport["core_validation"] = "passed"; M(fixtureReport, "dns")["mc2_core_validation"] = "passed"; fixtureReport["mc2_package_validation"] = O("status", "passed_with_geodata", "required_assets", O("GeoSite.dat", O(), "ASN.mmdb", O()));
    File.WriteAllText(Path.Combine(fixture.OutputPath, "HomeChain.report.json"), fixtureReport.ToJsonString(JsonOptions));
    var preferences = new AppPreferences(Path.Combine(workspace, "preferences.json"));
    var window = new MainWindow(preferences, workspace) { Width = 1220, Height = 850 }; window.Show();
    Dispatcher.UIThread.RunJobs();
    Check(File.Exists(Path.Combine(workspace, "config", "Example", "policy.yaml")) && File.Exists(RuleDefaults.FilePath(workspace)), "Startup did not restore embedded defaults");
    window.Navigate("settings"); Pump(); ClickNamed("DownloadRuntimeResources");
    var resourceDialog = Dialog("下载校验资源");
    Check(resourceDialog.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("缺少：") == true), "Missing resource prompt absent");
    Check(resourceDialog.GetVisualDescendants().OfType<Button>().Any(x => x.Name == "ConfirmRuntimeResourceDownload" && x.IsEnabled), "Explicit resource download unavailable");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = resourceDialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "missing-runtime-resources.png"));
    Click(resourceDialog, "稍后"); Check(!Directory.Exists(Path.Combine(workspace, "resources")), "Dismissing prompt installed resources");
    File.Delete(Path.Combine(workspace, "config", "Example", "residential.yaml")); ClickNamed("RestoreBuiltInDefaults");
    Check(File.Exists(Path.Combine(workspace, "config", "Example", "residential.yaml")), "Manual missing-default recovery failed");
    string projectBeforeRecovery = window.Session!.ProjectName;
    var temporaryDefaults = window.Session.Policy; temporaryDefaults["direct"] = A("DOMAIN,temporary.example"); RuleDefaults.Save(workspace, temporaryDefaults);
    window.SelectProject("Example"); File.Delete(RuleDefaults.FilePath(workspace)); ClickNamed("RestoreBuiltInDefaults");
    Check(!Canonical(window.Session!.Policy).Contains("temporary.example"), "Recovery left a stale Example view");
    window.SelectProject(projectBeforeRecovery);
    foreach (string theme in new[] { "light", "dark" })
    {
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        // Recreate pages to use product colors for the active appearance.
        foreach (string page in MainWindow.PageIds)
        {
            window.Navigate(page); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Check(window.CurrentPage == page, "Page navigation failed: " + page);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
            Check(!texts.Contains("配置需要修复"), "Page failed to construct: " + page);
            if (theme == "dark" && page == "policy")
            {
                var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "PageScroll");
                var disclosure = window.GetVisualDescendants().OfType<Expander>().First().GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First();
                File.WriteAllText(Path.Combine(output, "layout-metrics.json"), System.Text.Json.JsonSerializer.Serialize(new { width = window.Width, height = window.Height, viewportWidth = scroll.Viewport.Width, viewportHeight = scroll.Viewport.Height, viewportRatio = scroll.Viewport.Width * scroll.Viewport.Height / (window.Width * window.Height), disclosureHeight = disclosure.Bounds.Height }));
            }
            using var frame = window.CaptureRenderedFrame();
            Check(frame is not null, "No rendered frame: " + page); frame!.Save(Path.Combine(output, theme + "-" + page + ".png"));
        }
    }
    // DNS migration uses real controls, remains an editable draft, and never changes routing/QUIC.
    window.SelectProject("SampleTwo"); window.Navigate("policy"); Pump();
    var dnsSession = window.Session!; string originalDnsPolicy = dnsSession.Documents["policy.yaml"].Text;
    var legacyDnsPolicy = dnsSession.Policy; legacyDnsPolicy["rule_source"] = "custom"; legacyDnsPolicy["dns_follow_rules"] = false;
    legacyDnsPolicy["dns"] = O("enable", true, "listen", "127.0.0.1:9953", "enhanced-mode", "fake-ip", "ipv6", true, "prefer-h3", true, "respect-rules", true,
        "nameserver", A("9.9.9.9"), "direct-nameserver", A("https://1.1.1.1/dns-query#住宅出口"), "nameserver-policy", O("private.example", A("192.168.1.53")));
    dnsSession.Documents["policy.yaml"].Set(legacyDnsPolicy); dnsSession.Save();
    string legacyDisk = File.ReadAllText(Path.Combine(dnsSession.ProjectPath, "policy.yaml"));
    window.Navigate("policy"); ClickNamed("PolicyDnsTab"); Pump();
    Check(((CheckBox)NamedControl("DnsDomesticDirect")).IsChecked == false && ((CheckBox)NamedControl("DnsFollowDomainRules")).IsChecked == false, "Legacy DNS was silently migrated");
    ClickNamed("ApplyRecommendedDns"); Pump();
    Check(dnsSession.Dirty && File.ReadAllText(Path.Combine(dnsSession.ProjectPath, "policy.yaml")) == legacyDisk, "DNS preset saved implicitly");
    Check(DnsSettings.DomesticEnabled(dnsSession.Policy) && B(dnsSession.Policy, "dns_follow_rules"), "One-click DNS migration did not enable both strategies");
    Check(B(M(dnsSession.Policy, "dns"), "prefer-h3") && B(M(dnsSession.Policy, "dns"), "ipv6") && S(M(dnsSession.Policy, "dns"), "listen") == "127.0.0.1:9953", "DNS migration changed transport or listener settings");
    ((CheckBox)NamedControl("DnsDomesticDirect")).IsChecked = false; Pump();
    Check(!M(M(dnsSession.Policy, "dns"), "nameserver-policy").ContainsKey("geosite:cn") && B(dnsSession.Policy, "dns_follow_rules"), "Domestic toggle changed the rule-priority switch");
    ((CheckBox)NamedControl("DnsDomesticDirect")).IsChecked = true; Pump();
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Application.Current!.RequestedThemeVariant = theme; window.Navigate("policy"); Pump();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, theme == ThemeVariant.Dark ? "dark-dns-strategy.png" : "light-dns-strategy.png"));
    }
    Click(window, "自定义 DNS（高级）"); Pump();
    Check(((CheckBox)NamedControl("DnsTransportRespectRules")).IsChecked == false, "Recommended DNS incorrectly enabled transport respect-rules");
    ((TextBox)NamedControl("Dns_direct-nameserver")).Text = ""; Pump();
    ((TextBox)NamedControl("Dns_direct-nameserver")).Text = "https://doh.pub/dns-query#DIRECT"; Pump();
    Check(Canonical(M(M(dnsSession.Policy, "dns"), "nameserver-policy")["geosite:cn"]) == Canonical(M(dnsSession.Policy, "dns")["direct-nameserver"]), "Domestic fallback did not follow resolver edits");
    dnsSession.Save(); Check(DnsSettings.DomesticEnabled(new WorkspaceSession(workspace, "SampleTwo").Policy), "DNS settings did not survive save/reload");
    dnsSession.Documents["policy.yaml"].SetText(originalDnsPolicy); dnsSession.Save();
    window.SelectProject("Example"); window.Navigate("policy"); ClickNamed("PolicyDnsTab"); Pump();
    Check(NamedControl("ClientManagedDns").IsEffectivelyVisible && !NamedControl("DnsPrivacyEnabled").IsEnabled && !window.Session!.Policy.ContainsKey("dns"), "Example does not use client-managed DNS");
    window.SelectProject("SampleOne"); window.Navigate("policy"); ClickNamed("PolicyRulesTab"); Pump();
    string beforeBaseline = window.Session!.Documents["policy.yaml"].Text, preservedDns = Canonical(window.Session.Policy["dns"]);
    var preservedDocs = window.Session.Documents.Where(x => x.Key != "policy.yaml").ToDictionary(x => x.Key, x => x.Value.Text);
    ((CheckBox)NamedControl("UseSubscriptionBaseline")).IsChecked = true; Pump();
    Check(SubscriptionBaseline.Enabled(window.Session.Policy) && Canonical(window.Session.Policy["dns"]) == preservedDns && preservedDocs.All(x => window.Session.Documents[x.Key].Text == x.Value), "Baseline switch changed subscription/residence/DNS");
    Check(!window.GetVisualDescendants().OfType<Control>().Any(x => x.Name is "PolicyRuleSetsTab" or "BrowseRuleSets"), "Empty subscription baseline retained redundant rule-set import navigation");
    Check(((TextBlock)NamedControl("UnifiedRoutingHelp")).Text!.Contains("自定义规则 → 主订阅规则") && ((TextBlock)NamedControl("UnifiedRoutingHelp")).Text!.Contains("代理出口"), "Unified routing priority or exit unexplained");
    Check(NamedControl("SubscriptionRoutingPreview") is Expander { IsExpanded: false }, "Subscription normalization preview must be available but collapsed");
    ((CheckBox)NamedControl("EnableExtraRules")).IsChecked = false; Pump();
    Check(!B(window.Session.Policy, "extra_rules_enabled") && L(window.Session.Policy, "direct").Count == L(Map(YamlData.Parse(beforeBaseline), "before"), "direct").Count, "Disabling extras deleted rules");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "subscription-baseline.png"));
    ClickNamed("PolicyDnsTab"); Pump(); Check(NamedControl("ClientManagedDns").IsEffectivelyVisible, "Baseline did not retain client-managed default");
    string dnsDiskBeforeToggle = File.ReadAllText(Path.Combine(window.Session.ProjectPath, "policy.yaml"));
    var beforePrivacyPolicy = window.Session.Policy; beforePrivacyPolicy.Remove("dns_privacy");
    ((CheckBox)NamedControl("DnsPrivacyEnabled")).IsChecked = true; Pump();
    Check(DnsPrivacy.Enabled(window.Session.Policy) && ((ComboBox)NamedControl("DnsPrivacyExit")).SelectedIndex == 0, "Privacy switch did not default to residential");
    Check(File.ReadAllText(Path.Combine(window.Session.ProjectPath, "policy.yaml")) == dnsDiskBeforeToggle && preservedDocs.All(x => window.Session.Documents[x.Key].Text == x.Value), "Privacy switch wrote files or changed subscriptions/residences");
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Application.Current!.RequestedThemeVariant = theme; window.Navigate("policy"); Pump();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, theme == ThemeVariant.Dark ? "dark-dns-privacy.png" : "light-dns-privacy.png"));
    }
    window.Width = 940; window.Height = 640; Pump();
    var dnsPaneScroll = (ScrollViewer)NamedControl("PageScroll");
    double dnsTabY = NamedControl("PolicyDnsTab").TranslatePoint(default, window)!.Value.Y;
    Check(dnsPaneScroll.Extent.Height > dnsPaneScroll.Viewport.Height, "Small window did not allow DNS content to scroll");
    dnsPaneScroll.ScrollToEnd(); Pump();
    Check(dnsPaneScroll.Offset.Y > 0 && Math.Abs(NamedControl("PolicyDnsTab").TranslatePoint(default, window)!.Value.Y - dnsTabY) < 1, "DNS tab header moved with content scrolling");
    Check(!NamedControl("PolicyDnsTab").GetVisualAncestors().Contains(dnsPaneScroll), "DNS tab remains inside the content scrollbar");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "dns-tab-scrolled-small.png"));
    window.Width = 1220; window.Height = 850; dnsPaneScroll.Offset = default; Pump();
    ((ComboBox)NamedControl("DnsPrivacyExit")).SelectedIndex = 1; Pump();
    Check(DnsPrivacy.Exit(window.Session.Policy) == "airport", "Airport DNS selection not saved in draft");
    ((CheckBox)NamedControl("DnsPrivacyEnabled")).IsChecked = false; Pump();
    var afterPrivacyPolicy = window.Session.Policy; afterPrivacyPolicy.Remove("dns_privacy");
    Check(Canonical(beforePrivacyPolicy) == Canonical(afterPrivacyPolicy) && Canonical(window.Session.Policy["dns"]) == preservedDns, "DNS toggle changed legacy DNS or rules");
    ClickNamed("Nav_dns"); Pump();
    Check(!NamedControl("PolicyTabBar").IsVisible, "Policy tabs leaked onto output pages");
    ((Expander)NamedControl("DefaultDnsPreview")).IsExpanded = true; Pump();
    Check(NamedControl("CopyDefaultDns").IsEnabled && ((AvaloniaEdit.TextEditor)NamedControl("DefaultDnsOutput")).Text == DnsPrivacy.DefaultText(), "Recovery unavailable with dirty project");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "dns-recovery.png"));
    window.Navigate("sources"); Pump();
    var entranceFilters = (Expander)NamedControl("SourceGlobalFilters"); entranceFilters.IsExpanded = true; Pump();
    Check(entranceFilters.Header?.ToString() == "机场入口的默认筛选与测速（高级）" && entranceFilters.GetVisualDescendants().OfType<TextBox>().Any(x => x.Text == SubscriptionBaseline.JumpInclude), "Entrance defaults missing from UI");
    Check(ToolTip.GetTip(NamedControl("SourceUp0"))?.ToString() == "上移；第一个启用订阅提供规则", "Rule-source ordering still describes entrance priority");
    window.Navigate("residential"); Pump();
    Check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("[链式] 节点名") == true && x.Text.Contains("[直连] 节点名")), "Residential path names missing from UI");
    Check(preservedDocs.All(x => window.Session.Documents[x.Key].Text == x.Value), "Viewing entrance defaults modified project settings");
    window.Session.Documents["policy.yaml"].SetText(beforeBaseline); window.Navigate("policy"); ClickNamed("PolicyRulesTab"); Pump();
    // Real production controls: defaults round-trip and read-only preflight with all result states.
    WorkspaceSession.CreateProject(workspace, "Review", "SampleOne"); window.OpenWorkspace(workspace); window.SelectProject("Review"); window.Navigate("policy"); Pump();
    var reviewSession = window.Session!; string initialPolicy = reviewSession.Documents["policy.yaml"].Text;
    var reviewPolicy = reviewSession.Policy; reviewPolicy["proxy"] = A("DOMAIN,default.example");
    reviewPolicy["supplemental_rules"] = new System.Text.Json.Nodes.JsonArray(O("match", "DOMAIN-SUFFIX,supplement.example", "action", "DIRECT"), O("match", "DOMAIN,blocked.example", "action", "REJECT"));
    M(reviewPolicy, "dns")["nameserver"] = A("https://9.9.9.9/dns-query#住宅出口"); reviewSession.Documents["policy.yaml"].Set(reviewPolicy); window.Navigate("policy"); Pump();
    string defaultsBeforeCancel = File.ReadAllText(RuleDefaults.FilePath(workspace));
    ClickNamed("SaveRuleDefaults"); Click(Dialog("保存分流默认配置"), "取消");
    Check(File.ReadAllText(RuleDefaults.FilePath(workspace)) == defaultsBeforeCancel, "Cancel wrote default configuration");
    ClickNamed("SaveRuleDefaults"); Click(Dialog("保存分流默认配置"), "覆盖默认");
    Check(reviewSession.Dirty && L(RuleDefaults.Read(workspace), "proxy").Select(x => S(x)).Contains("DOMAIN,default.example"), "Save defaults lost unsaved rules or saved the project implicitly");
    window.SelectProject("Example"); window.Navigate("policy"); Pump();
    Check(!NamedButton("SaveRuleDefaults").IsEnabled && !NamedButton("ApplyExampleRules").IsEnabled && L(window.Session!.Policy, "proxy").Count == 1, "Example failed to display the saved read-only template");
    WorkspaceSession.CreateProject(workspace, "DefaultCopy"); var defaultCopy = new WorkspaceSession(workspace, "DefaultCopy");
    Check(L(defaultCopy.Policy, "proxy").Select(x => S(x)).Contains("DOMAIN,default.example"), "New project did not use saved routing defaults");
    window.SelectProject("Review"); reviewSession = window.Session!; reviewPolicy = reviewSession.Policy;
    reviewPolicy["proxy"] = A("DOMAIN,current.example"); reviewPolicy["supplemental_rules"] = new System.Text.Json.Nodes.JsonArray(O("match", "DOMAIN,old.example", "action", "PROXY"));
    reviewPolicy["dns_follow_rules"] = false; reviewPolicy["custom-extension"] = O("keep", true);
    M(reviewPolicy, "dns")["nameserver"] = A("https://9.9.9.9/dns-query#住宅出口"); reviewSession.Documents["policy.yaml"].Set(reviewPolicy);
    string beforeApply = reviewSession.Documents["policy.yaml"].Text, dnsBeforeApply = Canonical(reviewPolicy["dns"]);
    var otherDocuments = reviewSession.Documents.Where(x => x.Key != "policy.yaml").ToDictionary(x => x.Key, x => x.Value.Text);
    var filesBeforeApply = Directory.GetFiles(reviewSession.ProjectPath, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
    window.Navigate("policy"); Pump(); ClickNamed("ApplyExampleRules");
    var applyDialog = Dialog("应用 Example 规则");
    Check(applyDialog.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("2 条导入规则") == true), "Apply confirmation did not identify imported rules");
    Click(applyDialog, "取消");
    Check(reviewSession.Documents["policy.yaml"].Text == beforeApply, "Cancel changed current rules");
    ClickNamed("ApplyExampleRules"); Click(Dialog("应用 Example 规则"), "应用规则"); Pump();
    Check(reviewSession.Dirty && Canonical(RuleDefaults.Extract(reviewSession.Policy)) == Canonical(RuleDefaults.Read(workspace)), "Apply Example omitted or reordered template rules");
    Check(Canonical(reviewSession.Policy["dns"]) == dnsBeforeApply && !B(reviewSession.Policy, "dns_follow_rules") && B(M(reviewSession.Policy, "custom-extension"), "keep"), "Apply Example changed DNS or unrelated policy settings");
    Check(otherDocuments.All(x => reviewSession.Documents[x.Key].Text == x.Value) && filesBeforeApply.All(x => x.Value.SequenceEqual(File.ReadAllBytes(x.Key))), "Apply Example changed subscription/node documents or saved implicitly");
    Check(NamedButton("BrowseRuleSets").IsEnabled && L(Map(ImportedRuleSets.Sets(reviewSession.Policy)[0], "set"), "rules").Count == 2, "Applied imported rules are missing from the UI");
    reviewSession.Save();
    var reloadedRules = new WorkspaceSession(workspace, "Review");
    Check(Canonical(RuleDefaults.Extract(reloadedRules.Policy)) == Canonical(RuleDefaults.Read(workspace)) && Canonical(reloadedRules.Policy["dns"]) == dnsBeforeApply && !B(reloadedRules.Policy, "dns_follow_rules"), "Applied rules or preserved DNS failed save/reload");
    Check(filesBeforeApply.Where(x => x.Key != Path.Combine(reviewSession.ProjectPath, "policy.yaml")).All(x => x.Value.SequenceEqual(File.ReadAllBytes(x.Key))), "Saving applied rules modified subscriptions or nodes");
    // Generic importer: real paste/file/URL controls, explicit mapping, paged viewer and atomic update.
    string savedRulePolicy = reviewSession.Documents["policy.yaml"].Text;
    Control InDialog(Window d, string name) => d.GetVisualDescendants().OfType<Control>().Single(x => x.Name == name);
    void PreviewImport(Window d)
    {
        ((Button)InDialog(d, "PreviewRuleSetImport")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int spin = 0; spin < 500 && !InDialog(d, "ConfirmRuleSetImport").IsEnabled; spin++) { Pump(); Thread.Sleep(5); }
        Pump(); Check(InDialog(d, "ConfirmRuleSetImport").IsEnabled, "Import failed to preview: " + string.Join(" | ", d.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text)));
    }
    ClickNamed("PolicyRuleSetsTab"); ClickNamed("ImportRuleSet"); var importDialog = Dialog("导入规则集");
    ((ComboBox)InDialog(importDialog, "RuleSetSourceKind")).SelectedIndex = 0;
    ((TextBox)InDialog(importDialog, "RuleSetSourceInput")).Text = "proxies: [{password: SECRET}]\nrules:\n" + string.Join("\n", Enumerable.Range(0, 73).Select(i => " - DOMAIN,site" + i + ".example,External")) + "\n - MATCH,External";
    PreviewImport(importDialog); Click(importDialog, "导入到当前项目");
    Check(importDialog.IsVisible && ImportedRuleSets.Sets(reviewSession.Policy).Count == 1, "Unmapped policy imported silently");
    ((TextBox)InDialog(importDialog, "RuleSetName")).Text = "通用基础";
    ((ComboBox)InDialog(importDialog, "RuleSetMapping0")).SelectedItem = "PROXY";
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var image = importDialog.CaptureRenderedFrame()) image!.Save(Path.Combine(output, "rule-set-import-preview.png"));
    Click(importDialog, "导入到当前项目"); Pump();
    var importedSet = Map(ImportedRuleSets.Sets(reviewSession.Policy)[1], "set");
    Check(L(importedSet, "rules").Count == 74 && !B(importedSet, "include_fallback") && !Canonical(importedSet).Contains("SECRET"), "Importer copied credentials or silently adopted MATCH");
    Check(Canonical(reviewSession.Policy["dns"]) == dnsBeforeApply && otherDocuments.All(x => reviewSession.Documents[x.Key].Text == x.Value), "Import changed unrelated project inputs");
    ClickNamed("ViewRuleSet1"); var viewer = Dialog("查看规则集");
    Check(InDialog(viewer, "RuleSetNext").IsEnabled, "Viewer did not paginate"); Click(viewer, "下一页");
    ((TextBox)InDialog(viewer, "RuleSetSearch")).Text = "site72.example"; Pump();
    Check(viewer.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "DOMAIN,site72.example") && !InDialog(viewer, "RuleSetNext").IsEnabled, "Viewer search failed");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var image = viewer.CaptureRenderedFrame()) image!.Save(Path.Combine(output, "rule-set-viewer-search.png"));
    Click(viewer, "关闭"); ClickNamed("MapRuleSet1"); var mappingDialog = Dialog("规则集策略映射");
    ((ComboBox)InDialog(mappingDialog, "RuleSetMapping0")).SelectedItem = "DIRECT"; Click(mappingDialog, "应用映射");
    Check(S(M(Map(ImportedRuleSets.Sets(reviewSession.Policy)[1], "set"), "mappings"), "External") == "DIRECT", "Mapping edit failed");
    string importedId = S(Map(ImportedRuleSets.Sets(reviewSession.Policy)[1], "set"), "id");
    var localImportPath = Path.Combine(workspace, "rules-import.yaml"); File.WriteAllText(localImportPath, "rules: ['DOMAIN,replacement.example,External']");
    ClickNamed("UpdateRuleSet1"); var updateDialog = Dialog("更新规则集");
    ((ComboBox)InDialog(updateDialog, "RuleSetSourceKind")).SelectedIndex = 1;
    ((TextBox)InDialog(updateDialog, "RuleSetSourceInput")).Text = localImportPath; PreviewImport(updateDialog);
    Check(updateDialog.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("新增 1 条，删除 74 条") == true), "Update did not preview changes");
    Click(updateDialog, "替换规则集");
    var replacedSet = Map(ImportedRuleSets.Sets(reviewSession.Policy)[1], "set");
    Check(S(replacedSet, "id") == importedId && L(replacedSet, "rules").Count == 1 && S(M(replacedSet, "mappings"), "External") == "DIRECT", "Update lost identity or mapping");
    ClickNamed("RuleSetUp1"); Check(S(Map(ImportedRuleSets.Sets(reviewSession.Policy)[0], "set"), "id") == importedId, "Rule set reorder failed");
    ((CheckBox)NamedControl("EnableRuleSet0")).IsChecked = false; Pump();
    Check(!B(Map(ImportedRuleSets.Sets(reviewSession.Policy)[0], "set"), "enabled", true), "Rule set disable failed");
    using (var genericServer = new UiRuleServer())
    {
        ClickNamed("ImportRuleSet"); var urlDialog = Dialog("导入规则集");
        ((ComboBox)InDialog(urlDialog, "RuleSetSourceKind")).SelectedIndex = 2;
        ((TextBox)InDialog(urlDialog, "RuleSetSourceInput")).Text = genericServer.Url; PreviewImport(urlDialog);
        ((ComboBox)InDialog(urlDialog, "RuleSetMapping0")).SelectedItem = "DIRECT"; Click(urlDialog, "导入到当前项目");
        Check(genericServer.Requests == 1 && ImportedRuleSets.Sets(reviewSession.Policy).Count == 3, "URL import failed");
    }
    // Project subscription import: local snapshot, explicit missing-file fetch, then cache reuse.
    using (var subscriptionServer = new UiRuleServer())
    {
        string originalSettings = reviewSession.Documents["settings.yaml"].Text, originalState = reviewSession.Subscriptions.Text;
        var subscriptionSettings = reviewSession.Settings; var source = Map(L(subscriptionSettings, "sources")[0], "source");
        source["url"] = subscriptionServer.Url + "subscription-rules"; source["enabled"] = true;
        subscriptionSettings["http"] = O("trust_env", false); reviewSession.Documents["settings.yaml"].Set(subscriptionSettings); reviewSession.Subscriptions.MarkUpdated(source, "test");
        string snapshotPath = reviewSession.SnapshotPath(S(source, "name")); byte[]? originalSnapshot = File.Exists(snapshotPath) ? File.ReadAllBytes(snapshotPath) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!); File.WriteAllText(snapshotPath, "rules: ['DOMAIN,existing-subscription.example,DIRECT']\nproxies: [{password: PRIVATE}]");
        ClickNamed("ImportRuleSet"); var localSubscription = Dialog("导入规则集");
        Check(((ComboBox)InDialog(localSubscription, "RuleSetSourceKind")).SelectedIndex == 3 && InDialog(localSubscription, "RuleSetSubscription").IsVisible, "Project subscription source not offered by default");
        Check(((TextBlock)InDialog(localSubscription, "RuleSetSubscriptionStatus")).Text!.Contains("已有规则文件") && subscriptionServer.Requests == 0, "Existing source not detected or selection fetched silently");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = localSubscription.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "subscription-rule-local.png"));
        PreviewImport(localSubscription); Click(localSubscription, "导入到当前项目");
        Check(subscriptionServer.Requests == 0 && ImportedRuleSets.Sets(reviewSession.Policy).Count == 4, "Local subscription import failed or fetched");
        File.WriteAllText(snapshotPath, "proxies: [{name: local-node, password: PRIVATE}]");
        ClickNamed("ImportRuleSet"); var missingSubscription = Dialog("导入规则集");
        Check(((Button)InDialog(missingSubscription, "PreviewRuleSetImport")).Content?.ToString() == "拉取规则并预览" && subscriptionServer.Requests == 0, "Missing rules did not require explicit pull");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = missingSubscription.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "subscription-rule-missing.png"));
        Click(missingSubscription, "取消"); Check(subscriptionServer.Requests == 0, "Cancel initiated download");
        ClickNamed("ImportRuleSet"); var fetchedSubscription = Dialog("导入规则集"); PreviewImport(fetchedSubscription);
        Check(subscriptionServer.Requests == 1 && !fetchedSubscription.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("PRIVATE") == true), "Subscription fetch failed or exposed credentials");
        Click(fetchedSubscription, "取消"); ClickNamed("ImportRuleSet"); var cachedSubscription = Dialog("导入规则集"); PreviewImport(cachedSubscription);
        Check(subscriptionServer.Requests == 1, "Pulled rules were not reusable"); Click(cachedSubscription, "取消");
        Check(Canonical(reviewSession.Policy["dns"]) == dnsBeforeApply, "Subscription rule import modified DNS");
        reviewSession.Documents["settings.yaml"].SetText(originalSettings); reviewSession.Subscriptions.RestorePending(originalState);
        if (originalSnapshot is not null) File.WriteAllBytes(snapshotPath, originalSnapshot); else File.Delete(snapshotPath);
    }
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Application.Current!.RequestedThemeVariant = theme; window.Navigate("policy"); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var image = window.CaptureRenderedFrame(); image!.Save(Path.Combine(output, theme == ThemeVariant.Dark ? "dark-rule-sets.png" : "light-rule-sets.png"));
    }
    Check(filesBeforeApply.Where(x => x.Key != Path.Combine(reviewSession.ProjectPath, "policy.yaml")).All(x => x.Value.SequenceEqual(File.ReadAllBytes(x.Key))), "Import wrote subscription or node files");
    // Existing imported data remains manageable after switching to the subscription baseline.
    string beforeLegacyNavigation = reviewSession.Documents["policy.yaml"].Text;
    var legacyNavigation = reviewSession.Policy; string legacySets = Canonical(ImportedRuleSets.Sets(legacyNavigation));
    legacyNavigation["rule_source"] = "subscription"; reviewSession.Documents["policy.yaml"].Set(legacyNavigation); window.Navigate("policy"); Pump();
    Check(!window.GetVisualDescendants().OfType<Control>().Any(x => x.Name is "PolicyRuleSetsTab" or "ImportRuleSet") && NamedButton("BackToRules").IsEnabled, "Legacy rule-set view still offers a duplicate import flow");
    ClickNamed("BackToRules"); Pump(); ClickNamed("BrowseRuleSets"); Pump();
    Check(NamedButton("ViewRuleSet0").IsEnabled && NamedControl("EnableRuleSet0").IsEnabled, "Existing rule sets cannot be viewed or disabled in subscription mode");
    Check(Canonical(ImportedRuleSets.Sets(reviewSession.Policy)) == legacySets, "Hiding import navigation changed stored rule sets");
    reviewSession.Documents["policy.yaml"].SetText(beforeLegacyNavigation);
    reviewSession.Documents["policy.yaml"].SetText(savedRulePolicy); reviewSession.Save();
    window.SelectProject("Example"); window.Navigate("policy"); ClickNamed("PolicyRuleSetsTab"); Pump();
    Check(NamedButton("ViewRuleSet0").IsEnabled && !NamedButton("ImportRuleSet").IsEnabled && !NamedButton("MapRuleSet0").IsEnabled, "Read-only Example cannot view or can mutate rule sets");
    ClickNamed("ViewRuleSet0"); Click(Dialog("查看规则集"), "关闭");
    window.SelectProject("Review"); reviewSession = window.Session!; window.Navigate("policy"); ClickNamed("PolicyRulesTab"); Pump();
    reviewPolicy = reviewSession.Policy; M(reviewPolicy, "dns")["nameserver"] = A("invalid-dns"); reviewSession.Documents["policy.yaml"].Set(reviewPolicy);
    var filesBeforeCheck = Directory.GetFiles(reviewSession.ProjectPath, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
    window.Navigate("build"); Pump(); NamedButton("CheckParameters").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(window.IsBusy && !NamedControl("CurrentProject").IsEnabled, "Preflight did not protect the selected project while running");
    for (int spin = 0; spin < 600 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
    Check(!window.IsBusy, "Preflight failed to finish");
    var log = NamedControl("DetailedLog");
    foreach (string state in new[] { "pass", "warning", "fail" }) Check(log.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, state)), "Preflight missing status icon " + state);
    Check(!log.GetVisualAncestors().OfType<Expander>().Any() && !log.GetVisualDescendants().OfType<ScrollViewer>().Any(), "Detailed log is collapsed or has a nested scrollbar");
    foreach (var row in log.GetVisualDescendants().OfType<Grid>().Where(x => x.Name == "TaskLogRow"))
    {
        var icon = row.Children.OfType<Avalonia.Controls.Shapes.Path>().Single();
        Check(Grid.GetColumn(icon) == 1 && icon.Bounds.Width > 0, "Log status is not in the right column");
    }
    Check(reviewSession.Dirty && filesBeforeCheck.All(x => x.Value.SequenceEqual(File.ReadAllBytes(x.Key))), "Preflight wrote unsaved configuration");
    foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
    {
        Application.Current!.RequestedThemeVariant = theme;
        foreach (int width in new[] { 1220, 960 })
        {
            window.Width = width; window.Navigate("build"); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, $"{width}-{theme}-parameter-check.png"));
        }
    }
    window.Width = 1220; Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; window.Navigate("build"); Pump();
    window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Tag, "fix:Dns_nameserver")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    Check(window.CurrentPage == "policy" && NamedControl("Dns_nameserver").IsEffectivelyVisible, "DNS repair link did not expand DNS settings");
    reviewSession.Documents["policy.yaml"].SetText(initialPolicy); window.Navigate("build"); Pump();
    Check(NamedControl("DetailedLog").GetVisualDescendants().OfType<TextBlock>().Any(x => x.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text?.Contains("配置已修改") == true) == true), "Edited inputs left preflight results looking current");
    ClickNamed("GeneratePrimary");
    for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
    Check(!window.IsBusy && reviewSession.OutputIsCurrent(), "Generation failed after preflight");
    Check(NamedControl("DetailedLog").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().All(x => Equals(x.Tag, "pass")), "Completed generation retained pending/failed stages");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "generation-log.png"));
    Check(((CheckBox)NamedControl("ForceSubscriptions")).IsChecked != true, "Force selection leaked across projects");
    var targetPicker = (ComboBox)NamedControl("BuildTarget");
    Check(targetPicker.Bounds.Width > 300, "Build target selector does not fill its available editor width");
    targetPicker.SelectedItem = targetPicker.Items.OfType<ComboBoxItem>().Single(x => Equals(x.Tag, "clash")); Pump();
    Check(ConfigurationValidation.Target(reviewSession.Settings) == "clash" && reviewSession.Dirty, "Target change was not saved in the current project editor");
    ClickNamed("GeneratePrimary");
    for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
    Check(!window.IsBusy && !File.Exists(Path.Combine(reviewSession.OutputPath, "HomeChain.MC2DNS.yaml")) && reviewSession.OutputIsCurrent(), "Clash target retained obsolete MC2 DNS");
    ClickNamed("Nav_dns"); Pump();
    Check(!window.GetVisualDescendants().OfType<Control>().Any(x => x.Name == "CopyDns") && NamedControl("CopyDefaultDns").IsEnabled, "DNS output exposes an obsolete file or hides recovery after target switch");
    window.Navigate("build"); Pump(); targetPicker = (ComboBox)NamedControl("BuildTarget");
    targetPicker.SelectedItem = targetPicker.Items.OfType<ComboBoxItem>().Single(x => Equals(x.Tag, "mc2")); Pump();
    ClickNamed("GeneratePrimary");
    for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
    Check(File.Exists(Path.Combine(reviewSession.OutputPath, "HomeChain.MC2DNS.yaml")), "Switching back to MC2 did not restore its paired DNS artifact");
    using (var remoteCheckServer = new UiRuleServer())
    {
        var remoteCheckPolicy = reviewSession.Policy; remoteCheckPolicy["remote_rules"] = A(new System.Text.Json.Nodes.JsonNode?[] { O("proxy", A(remoteCheckServer.Url + "valid", remoteCheckServer.Url + "invalid")) });
        reviewSession.Documents["policy.yaml"].Set(remoteCheckPolicy);
        var checkFiles = new[] { reviewSession.ProjectPath, reviewSession.OutputPath, Path.Combine(workspace, ".cache", "Review") }.SelectMany(x => Directory.GetFiles(x, "*", SearchOption.AllDirectories)).ToDictionary(x => x, File.ReadAllBytes);
        window.Navigate("build"); Pump(); ClickNamed("CheckParameters");
        for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
        var remoteRows = NamedControl("DetailedLog").GetVisualDescendants().OfType<Grid>().Where(x => x.Name == "TaskLogRow" && x.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text?.StartsWith("代理 URL ·") == true) == true)).ToArray();
        Check(!window.IsBusy && remoteCheckServer.Requests == 2 && remoteRows.Length == 2, "Parameter button did not request every URL or retained duplicate pending rows");
        Check(remoteRows.SelectMany(x => x.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()).Select(x => x.Tag?.ToString()).SequenceEqual(["pass", "fail"]), "Parameter URL results are missing their final pass/fail icons");
        Check(remoteRows.All(x => x.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text?.Contains(remoteCheckServer.Url) == true) == true)), "Parameter URL check hid the full URL");
        Check(checkFiles.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)) && reviewSession.Dirty, "Parameter URL check wrote unsaved inputs, output or cache");
        ((ScrollViewer)NamedControl("PageScroll")).ScrollToEnd(); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "parameter-remote-check.png"));
        window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Tag, "fix:RemoteUrls_proxy")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(window.CurrentPage == "policy" && NamedControl("RemoteUrls_proxy").IsEffectivelyVisible, "Failed parameter URL check cannot navigate to its URL group");
        remoteCheckPolicy = reviewSession.Policy; remoteCheckPolicy["remote_rules"] = A(new System.Text.Json.Nodes.JsonNode?[] { O("proxy", A(remoteCheckServer.Url + "slow", remoteCheckServer.Url + "unvisited")) }); reviewSession.Documents["policy.yaml"].Set(remoteCheckPolicy);
        window.Navigate("build"); Pump(); ClickNamed("CheckParameters");
        for (int spin = 0; spin < 300 && remoteCheckServer.Requests < 3; spin++) { Pump(); Thread.Sleep(5); } Pump();
        Check(window.IsBusy && NamedControl("DetailedLog").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "pending")), "In-flight parameter URL check lacks a pending status and cancel action");
        ClickNamed("CancelTask"); for (int spin = 0; spin < 400 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
        Check(!window.IsBusy && remoteCheckServer.Requests == 3 && !NamedControl("DetailedLog").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "pending")), "Cancel did not stop URL checking or left a pending result behind");
        Check(NamedControl("DetailedLog").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text?.Contains("已取消，尚未完成校验") == true) == true), "Canceled URL looked successfully checked");
        reviewSession.Documents["policy.yaml"].SetText(initialPolicy);
    }
    ProjectStore.Delete(workspace, "Review"); ProjectStore.Delete(workspace, "DefaultCopy");
    RuleDefaults.Save(workspace, Map(YamlData.Parse(WorkspaceSession.ExampleDocuments()["policy.yaml"]), "factory"));
    WorkspaceSession.CreateProject(workspace, "PullReview", "SampleOne"); window.OpenWorkspace(workspace); window.SelectProject("PullReview"); window.Navigate("sources"); Pump();
    using (var sourceServer = new UiRuleServer())
    {
        var pullSession = window.Session!; string originalPolicy = pullSession.Documents["policy.yaml"].Text, originalResidences = pullSession.Documents["residential.yaml"].Text;
        Check(!NamedButton("PullSource0").IsEnabled && !NamedControl("PageScroll").GetVisualDescendants().OfType<Button>().Any(x => x.Name == "GeneratePrimary"), "Source page lacks a disabled pull action for an empty URL or retained generation controls");
        ClickNamed("EditSource0"); ((TextBox)NamedControl("SourceUrl0")).Text = sourceServer.Url + "subscription"; Pump();
        pullSession.Documents["policy.yaml"].SetText("dns: [unfinished"); pullSession.Documents["residential.yaml"].SetText("proxies: []\n");
        NamedButton("PullSource0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.IsBusy && NamedButton("PullSource0").Content?.ToString() == "取消拉取" && !NamedControl("CurrentProject").IsEnabled && !window.OwnedWindows.Any(x => x.IsVisible), "Direct pull did not start immediately with a cancel action");
        for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
        Check(!window.IsBusy && sourceServer.Requests == 1 && !Directory.Exists(pullSession.OutputPath) && !pullSession.Documents["settings.yaml"].Dirty && pullSession.Documents["policy.yaml"].Dirty, "Direct pull generated output or saved unrelated editors");
        Check(File.ReadAllText(Path.Combine(pullSession.ProjectPath, "policy.yaml")) == originalPolicy && File.ReadAllText(Path.Combine(pullSession.ProjectPath, "residential.yaml")) == originalResidences, "Direct pull persisted unrelated policy or residence edits");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "subscription-pull.png"));
        string snapshot = pullSession.SnapshotPath("AirportA"); byte[] cached = File.ReadAllBytes(snapshot);
        ((TextBox)NamedControl("SourceUrl0")).Text = sourceServer.Url + "subscription-fail"; Pump(); ClickNamed("PullSource0");
        for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
        Check(sourceServer.Requests == 2 && File.ReadAllBytes(snapshot).SequenceEqual(cached) && pullSession.Documents["settings.yaml"].Dirty, "Failed pull changed cache or accepted pending URL settings");
        ((TextBox)NamedControl("SourceUrl0")).Text = sourceServer.Url + "subscription-slow"; Pump(); ClickNamed("PullSource0");
        for (int spin = 0; spin < 300 && sourceServer.Requests < 3; spin++) { Pump(); Thread.Sleep(5); } Pump();
        window.GetLogicalDescendants().OfType<Expander>().Single(x => Equals(x.Header, "此订阅的筛选与请求选项")).IsExpanded = true; Pump();
        var busyFilter = (TextBox)NamedControl("Sourceinclude0"); string busyValue = busyFilter.Text ?? "";
        Check(busyFilter.IsReadOnly && !NamedButton("SourceName0").IsEnabled, "Busy pull left filters or rename editable");
        busyFilter.Focus(); window.KeyTextInput("discarded-edit"); Pump();
        Check(busyFilter.Text == busyValue && S(Map(L(pullSession.Settings, "sources")[0], "s"), "include", Nodes.Asia) == busyValue, "Busy input accepted an edit that would be discarded");
        Check(NamedControl("PageScroll").GetLogicalDescendants().OfType<NumericUpDown>().All(x => !x.IsEnabled), "Busy pull left global options enabled");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var busyFrame = window.CaptureRenderedFrame()) busyFrame!.Save(Path.Combine(output, "subscription-busy-protected.png"));
        ClickNamed("PullSource0");
        for (int spin = 0; spin < 400 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
        Check(!window.IsBusy && sourceServer.Requests == 3 && File.ReadAllBytes(snapshot).SequenceEqual(cached), "Source pull cancellation failed to preserve the old cache");
        ((TextBox)NamedControl("SourceUrl0")).Text = sourceServer.Url + "subscription-fail"; Pump(); pullSession.Documents["policy.yaml"].SetText(originalPolicy); pullSession.Documents["residential.yaml"].SetText(originalResidences);
        window.Navigate("build"); Pump();
        Check(((CheckBox)NamedControl("ForceSubscriptions")).IsChecked != true && NamedButton("GeneratePrimary").Content?.ToString() == "生成配置", "Force was enabled by default or generation retained changing labels");
        Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<Button>().Any(x => x.Content?.ToString()?.StartsWith("更新订阅") == true || x.Content?.ToString()?.StartsWith("更新规则") == true || x.Content?.ToString()?.StartsWith("仍使用") == true), "Build page retained legacy generation choices");
        ClickNamed("GeneratePrimary"); for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
        Check(!window.IsBusy && sourceServer.Requests == 3 && pullSession.OutputIsCurrent(), "Default generation fetched a cached subscription after its URL changed");
        ((CheckBox)NamedControl("ForceSubscriptions")).IsChecked = true; Pump(); ClickNamed("GeneratePrimary");
        for (int spin = 0; spin < 800 && window.IsBusy; spin++) { Pump(); Thread.Sleep(5); } Pump();
        Check(!window.IsBusy && sourceServer.Requests == 4 && pullSession.OutputIsCurrent() && File.ReadAllBytes(snapshot).SequenceEqual(cached), "Forced generation did not fall back to the existing cache");
        Check(NamedControl("DetailedLog").GetVisualDescendants().OfType<TextBlock>().Any(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text?.Contains("已使用现有缓存") == true) == true) && NamedControl("DetailedLog").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "warning")), "Forced cache fallback was not visibly warned in the detailed log");
        while (window.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == "DismissNotice") is { } notice) { notice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "force-cache-fallback.png"));
        foreach (string page in new[] { "sources", "residential", "report" })
        {
            window.Navigate(page); Pump(); Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<Button>().Any(x => x.Name == "GeneratePrimary" || x.Content?.ToString()?.StartsWith("更新订阅") == true || x.Content?.ToString()?.StartsWith("更新规则") == true), "Generation controls leaked into " + page);
        }
    }
    ProjectStore.Delete(workspace, "PullReview");
    window.SelectProject("SampleOne"); window.Navigate("policy"); Pump(); Click(window, "分流规则");
    foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
    {
        Application.Current!.RequestedThemeVariant = theme; window.Navigate("policy"); Pump();
        var arrow = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(x => x.Name == "DisclosureChevron");
        Check(arrow.Width == 16 && arrow.Height == 16 && arrow.TranslatePoint(default, window)!.Value.X < 310, "Disclosure arrow is not a consistent left-side vector");
        Check(!window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Name == "DisclosureChevron"), "Text glyph disclosure arrow remains");
        var urlToggle = NamedButton("CollapseUrls_direct"); var urlArrow = (Avalonia.Controls.Shapes.Path)urlToggle.Content!;
        Check(urlArrow.Width == arrow.Width && urlArrow.Height == arrow.Height && urlArrow.Data!.ToString() == arrow.Data!.ToString(), "URL and rule disclosure arrows differ");
        double toggleX = urlToggle.TranslatePoint(default, window)!.Value.X;
        Check(toggleX < 300, "URL collapse action is still beside priority actions");
        ClickNamed("CollapseUrls_direct"); Check(!window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "RemoteUrls_direct").IsVisible, "URL collapse failed");
        Check(((RotateTransform)urlArrow.RenderTransform!).Angle == 0, "Collapsed URL arrow orientation is incorrect");
        ClickNamed("CollapseUrls_direct"); Check(((RotateTransform)urlArrow.RenderTransform!).Angle == 180, "Expanded URL arrow orientation is incorrect");
        var disabledPriority = NamedButton("RuleUp_direct").GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        Check(disabledPriority.Background is ISolidColorBrush disabledBrush && disabledBrush.Color.A == 0, "Disabled priority button still has a grey background");
        var hoverInput = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "Suffix_direct");
        hoverInput.BringIntoView(); Pump();
        window.MouseMove(hoverInput.TranslatePoint(new Point(30, 20), window)!.Value); Pump();
        var border = hoverInput.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_BorderElement");
        Check((border.Background as ISolidColorBrush)?.Color == Color.Parse(theme == ThemeVariant.Dark ? "#24303F" : "#EAEFF7"), "Text input hover uses an unrelated background");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using(var hoverFrame = window.CaptureRenderedFrame()) hoverFrame!.Save(Path.Combine(output, theme == ThemeVariant.Dark ? "dark-policy-hover.png" : "light-policy-hover.png"));
        hoverInput.Focus(); Pump();
        Check((border.Background as ISolidColorBrush)?.Color == Color.Parse(theme == ThemeVariant.Dark ? "#151D27" : "#FFFFFF"), "Focused input does not preserve the editor surface");
        window.MouseMove(new Point(10, 10));
    }
    Check(window.Session?.Dirty == false, "Opening views modified the project");
    window.GetLogicalDescendants().OfType<Expander>().Single(x => Equals(x.Header, "GEO 数据（高级）")).IsExpanded = true; Pump();
    var geoToggle = window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "EmbedGeoForMc2");
    Check(geoToggle.IsChecked != true, "GEO embedding is enabled by default");
    window.GetLogicalDescendants().OfType<Expander>().Single(x => Equals(x.Header, "GEO 数据（高级）")).BringIntoView(); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "light-geo-mode.png"));
    geoToggle.IsChecked = true; Check(S(window.Session!.Policy, "geo_mode") == "mc2-inline", "Compatibility toggle did not save explicit mode");
    geoToggle.IsChecked = false; Check(S(window.Session.Policy, "geo_mode") == "native", "Compatibility toggle did not restore native GEO");
    window.Session.Documents["policy.yaml"].SetText(window.Session.Documents["policy.yaml"].Baseline);
    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; window.Navigate("policy"); Pump();
    var geoSection = window.GetLogicalDescendants().OfType<Expander>().Single(x => Equals(x.Header, "GEO 数据（高级）"));
    geoSection.IsExpanded = true; Pump(); geoSection.BringIntoView(); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "dark-geo-mode.png"));
    window.Width = 960; Pump(); geoSection.BringIntoView(); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "960-geo-mode.png"));
    window.Width = 1220; Application.Current.RequestedThemeVariant = ThemeVariant.Light; window.Navigate("policy"); Pump();


    void ClickNamed(string name)
    { window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    Button Flow(string name) => window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "Flow_" + name);
    Button NamedButton(string name) => window.GetVisualDescendants().OfType<Button>().SingleOrDefault(x => x.Name == name) ?? throw new Exception("Missing button: " + name + " on " + window.CurrentPage);
    Control NamedControl(string name) => window.GetVisualDescendants().OfType<Control>().SingleOrDefault(x => x.Name == name) ?? throw new Exception("Missing control: " + name + " on " + window.CurrentPage);
    window.Navigate("sources"); Pump(); Check(Equals(Flow("sources").Tag, "selected"), "Sidebar route did not highlight graph node");
    Check(!window.GetVisualDescendants().OfType<Button>().Any(x => x.Name is "Nav_projects" or "Nav_yaml" or "Nav_import"), "Sidebar retained pages unrelated to the relationship map");
    var map = NamedControl("RelationshipMap"); var header = NamedControl("PageHeader");
    Check(map.TranslatePoint(default, window)!.Value.Y + map.Bounds.Height <= header.TranslatePoint(default, window)!.Value.Y, "Relationship map is not above the page title");
    Check(NamedButton("Nav_settings").TranslatePoint(default, window)!.Value.Y > window.Height - 110, "Application settings are not anchored to the sidebar bottom");
    Check(NamedButton("NewProject").TranslatePoint(default, window)!.Value.Y >= NamedControl("CurrentProject").TranslatePoint(default, window)!.Value.Y + NamedControl("CurrentProject").Bounds.Height && NamedButton("DeleteProject").IsEnabled, "Project actions are not below the selector");
    ClickNamed("Nav_dns"); Check(Equals(Flow("dns").Tag, "selected") && Equals(NamedButton("Nav_dns").Tag, "selected"), "DNS sidebar selection is not linked to the graph");
    var dnsOutput = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single(x => x.Name == "DnsOutput");
    Check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("GeoSite.dat") == true && x.Text.Contains("不代表目标设备")), "DNS output hides client GEO dependencies");
    Check(dnsOutput.IsReadOnly && dnsOutput.Text == File.ReadAllText(Path.Combine(fixture.OutputPath, "HomeChain.MC2DNS.yaml")) && dnsOutput.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, "DNS output is not the complete read-only file");
    Check(!dnsOutput.GetVisualAncestors().OfType<Expander>().Any(), "Generated DNS output is still collapsed");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using(var dnsFrame = window.CaptureRenderedFrame()) dnsFrame!.Save(Path.Combine(output, "dns-output.png"));
    ClickNamed("Nav_report"); Check(Equals(Flow("main").Tag, "selected") && Equals(NamedButton("Nav_report").Tag, "selected"), "Main output sidebar selection is not linked to the graph");
    ClickNamed("Flow_residential"); Check(window.CurrentPage == "residential" && Equals(Flow("residential").Tag, "selected"), "Graph node did not navigate");
    ClickNamed("Flow_dns"); Check(window.CurrentPage == "report" && Equals(Flow("dns").Tag, "selected"), "DNS graph node did not select output tab");
    ClickNamed("Flow_build"); Check(!window.IsBusy && window.CurrentPage == "build", "Graph navigation executed generation");
    Flow("sources").Focus(); window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Pump();
    Check(window.CurrentPage == "sources" && Equals(Flow("sources").Tag, "selected"), "Keyboard could not activate graph navigation");
    Check((window.FocusManager?.GetFocusedElement() as Control)?.Name == "Flow_sources", "Graph navigation lost keyboard focus");
    window.Navigate("build"); Pump();
    window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "GeneratePrimary").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(window.IsBusy, "Generate action did not start background task");
    Check(!Flow("sources").IsEnabled, "Graph editing allowed during generation");
    Check(!NamedButton("NewProject").IsEnabled && !NamedButton("DeleteProject").IsEnabled && !NamedControl("CurrentProject").IsEnabled, "Project actions remained enabled while generating");
    window.Navigate("sources"); Check(window.CurrentPage == "build", "Navigation bypassed generation protection");
    for (int i = 0; i < 1000 && window.IsBusy; i++) { Pump(); Thread.Sleep(5); } Pump();
    Check(!window.IsBusy && window.Session!.OutputIsCurrent(), "Local generation failed or did not finish");
    if (window.GetVisualDescendants().OfType<Button>().Any(x => x.Name == "DismissNotice")) ClickNamed("DismissNotice");
    var latest = window.Session!.ReadReport()!; latest["core_validation"] = "passed"; M(latest, "dns")["mc2_core_validation"] = "passed"; latest["mc2_package_validation"] = O("status", "passed"); File.WriteAllText(Path.Combine(fixture.OutputPath, "HomeChain.report.json"), latest.ToJsonString(JsonOptions));
    ClickNamed("ToggleFlow"); Check(preferences.FlowCollapsed && !window.GetVisualDescendants().OfType<Button>().Any(x => x.Name == "Flow_sources"), "Graph collapse failed");
    window.Navigate("sources"); Pump(); Check(preferences.FlowCollapsed, "Navigation forgot collapsed preference");
    ClickNamed("ToggleFlow"); Check(Equals(Flow("sources").Tag, "selected"), "Expanded graph selected the wrong node");
    window.Navigate("settings"); Pump(); Check(!window.GetVisualDescendants().OfType<Button>().Any(x => x.Name?.StartsWith("Flow_") == true), "Global settings retained graph selection");
    Check(!window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SaveProject").IsVisible, "Project save shown in application settings");
    Check(!window.GetVisualDescendants().OfType<TextBox>().Any(x => x.Name?.StartsWith("Validation") == true), "Internal resource paths remain exposed");
    var themePicker = window.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name != "CurrentProject"); themePicker.SelectedIndex = 1; Pump();
    Check(new AppPreferences(Path.Combine(workspace, "preferences.json")).Theme == "light" && !window.Session!.Dirty, "Appearance setting did not save independently");
    Check(!MainWindow.PageIds.Contains("import"), "Removed import page is still registered");
    ClickNamed("Nav_dns"); Pump();
    Check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "DNS配置输出(MC2专用)"), "MC2-specific DNS title is missing");
    Check(NamedControl("PageScroll").GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("先导入主配置") == true && x.Text.Contains("普通 Mihomo 客户端从主 YAML")), "MC2 two-stage explanation is not on DNS output");
    Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<CheckBox>().Any(), "DNS output still requires manual confirmation");
    ClickNamed("Nav_report"); Pump();
    Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<Button>().Any(x => x.Content?.ToString() is "主配置输出" or "DNS 配置输出" or "导入步骤" or "查看导入步骤"), "Output page retained duplicate navigation");
    Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<Expander>().Any(x => x.Header?.ToString()?.StartsWith("规则解释") == true), "Rule explanation was retained");
    window.Navigate("build"); Pump();
    Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<Expander>().Any(x => x.Header?.ToString()?.Contains("批量") == true), "Batch generation was retained");
    window.Navigate("sources"); Dispatcher.UIThread.RunJobs();
    ClickNamed("EditSource0");
    var changedUrl = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "SourceUrl0"); string previousUrl = changedUrl.Text ?? "";
    changedUrl.Text = "https://changed.example/subscription"; Pump();
    Check(!window.GetVisualDescendants().OfType<Button>().Any(x => x.Name == "GeneratePrimary") && NamedButton("PullSource0").IsEnabled, "Source editing retained generation commands or did not enable direct pull");
    Check(window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("当前仍为旧节点") == true), "Changed URL did not explain old-node provenance");
    changedUrl.Text = previousUrl; Pump();
    ClickNamed("SourceName0"); Submit("重命名订阅", "AirportRenamed", "保存名称");
    Check(S(Map(L(window.Session!.Settings, "sources")[0], "source"), "name") == "AirportRenamed", "Source name editing did not update document");
    Check(window.Session.Dirty, "Editing did not set dirty state");
    Check(!window.GetVisualDescendants().OfType<NumericUpDown>().Any(), "Source priority exposed a numeric editor");
    window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SourceDown0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    Check(S(Map(L(window.Session.Settings, "sources")[0], "s"), "name") == "AirportB" && I(Map(L(window.Session.Settings, "sources")[0], "s"), "priority", 0) == 1 && I(Map(L(window.Session.Settings, "sources")[1], "s"), "priority", 0) == 2, "Moving sources did not update priority order");
    Check(!window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SourceUp0").IsEnabled && !window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SourceDown1").IsEnabled, "Source move boundaries are enabled");
    window.GetVisualDescendants().OfType<CheckBox>().First(x => x.Content?.ToString() == "启用订阅").IsChecked = true; Pump();
    Check(B(Map(L(window.Session.Settings, "sources")[0], "s"), "enabled") && !window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "订阅 1 · 已停用"), "Enabling a source left its disabled label stale");
    Click(window, "删除订阅"); Click(Dialog("删除订阅"), "删除");
    Check(L(window.Session.Settings, "sources").Count == 1 && I(Map(L(window.Session.Settings, "sources")[0], "s"), "priority", 0) == 1, "Removing a source did not renumber priorities");
    Click(window, "＋ 添加并启用订阅"); Check(I(Map(L(window.Session.Settings, "sources")[1], "s"), "priority", 0) == 2 && B(Map(L(window.Session.Settings, "sources")[1], "s"), "enabled"), "New source not explicitly enabled at lowest priority");
    window.Session.Save(); Check(!window.Session.Dirty, "Save did not clear dirty state");
    window.SelectProject("SampleTwo"); Check(S(Map(L(window.Session.Settings, "sources")[0], "source"), "name") == "AirportA", "Projects leaked edits");
    Check(new AppPreferences(Path.Combine(workspace, "preferences.json")).RecentProject(workspace) == "SampleTwo", "Recently selected project not remembered");
    window.Navigate("yaml"); Dispatcher.UIThread.RunJobs();
    var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First(); editor.Text += "\n# preserved editor comment\n";
    Check(window.Session.Dirty && window.Session.Documents["settings.yaml"].Text.Contains("preserved editor comment"), "YAML editor did not update document");
    string shortYaml = editor.Text; editor.Text = string.Join('\n', Enumerable.Range(0, 180).Select(i => "# line " + i)); Pump();
    Check(editor.Bounds.Height > 2000 && editor.GetVisualDescendants().OfType<ScrollViewer>().All(x => x.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && x.Extent.Height <= x.Viewport.Height + 1), "Long YAML retained a nested vertical scroll region");
    editor.Text = shortYaml; Pump(); window.Session.Save();
    var groupPolicy = window.Session.Policy;
    groupPolicy["proxy"] = A("DOMAIN-SUFFIX,example.com", "DOMAIN,api.example.com", "DOMAIN-KEYWORD,uuyc");
    groupPolicy["remote_rules"] = A(new System.Text.Json.Nodes.JsonNode?[] { O("proxy", A("https://example.com/proxy-a.list", "https://example.com/proxy-b.list")), O("direct", "https://example.com/direct.list"), O("reject", "https://example.com/reject.list"), O("name", "local", "file", "local.list", "action", "DIRECT") });
    window.Session.Documents["policy.yaml"].Set(groupPolicy); window.Session.Save(); window.Navigate("policy"); Pump();
    Check(!window.Session.Dirty && Canonical(window.Session.Policy["proxy"]) == Canonical(groupPolicy["proxy"]), "Classifying rules rewrote or reordered existing inputs");
    TextBox RemoteBox(string group) => window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "RemoteUrls_" + group);
    Check(window.GetVisualDescendants().OfType<TextBox>().Count(x => x.Name?.StartsWith("RemoteUrls_") == true) == 3, "Expected exactly three remote URL group fields");
    Check(RemoteBox("proxy").PasswordChar == '\0' && RemoteBox("proxy").Text!.Contains("proxy-a.list") && RemoteBox("proxy").Text!.Contains("proxy-b.list"), "Remote URLs were masked or not aggregated");
    var manualBox = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "Suffix_proxy");
    Check(manualBox.Text == "example.com", "Suffix field retained raw rule prefixes");
    var exactBox = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "Exact_proxy");
    Check(exactBox.Text == "api.example.com", "Exact field did not classify existing rules");
    var moreSection = window.GetVisualDescendants().OfType<Expander>().Single(x => x.Name == "MoreRules_proxy");
    Check(!moreSection.IsExpanded, "Advanced rule field was not collapsed by default"); var moreToggle = moreSection.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(); moreToggle.Focus(); window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Pump(); Check(moreSection.IsExpanded, "Disclosure did not expand by keyboard");
    var moreBox = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "Manual_proxy");
    Check(moreBox.Text == "DOMAIN-KEYWORD,uuyc", "Advanced field stripped rule types");
    moreBox.Text += "\nGEOIP,US,no-resolve"; Pump();
    Check(L(window.Session.Policy, "proxy").Select(x => S(x)).Contains("GEOIP,US,no-resolve"), "Advanced edit lost additional rule parameters");
    manualBox.Focus(); manualBox.Text = "DOMAIN-SUFFIX,example.com\nDOMAIN-SUFFIX,new.example"; Pump(); exactBox.Focus(); Pump();
    Check(manualBox.Text == "example.com\nnew.example" && L(window.Session.Policy, "proxy").Select(x => S(x)).Contains("DOMAIN-KEYWORD,uuyc"), "Pasted prefixes did not simplify or erased advanced entries");
    exactBox.Text = "api.example.com\ncdn.example.com"; Pump();
    Check(L(window.Session.Policy, "proxy").Select(x => S(x)).Contains("DOMAIN,cdn.example.com"), "Exact entries were saved as suffixes");
    var lanCheck = window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Name == "DirectLan");
    Check(lanCheck.TranslatePoint(default, window)!.Value.Y < RemoteBox("direct").TranslatePoint(default, window)!.Value.Y && NamedControl("DirectLanHelp").IsEffectivelyVisible, "LAN option is still mixed into the URL list without an explanation");
    Check(lanCheck.IsChecked == false, "New project unexpectedly adds its own LAN rule"); lanCheck.IsChecked = true; Pump(); lanCheck.IsChecked = false; Pump();
    Check(!L(window.Session.Policy, "direct").Select(x => S(x)).Any(x => x.Contains("GEOIP,LAN")), "Unchecking LAN left an active LAN rule");
    lanCheck.IsChecked = true; Pump(); Check(L(window.Session.Policy, "direct").Select(x => S(x)).Count(x => x == "GEOIP,LAN,no-resolve") == 1, "Rechecking LAN duplicated its rule");
    Check(Math.Abs(RemoteBox("proxy").TranslatePoint(default, window)!.Value.X - manualBox.TranslatePoint(default, window)!.Value.X) < 1 && RemoteBox("proxy").TranslatePoint(default, window)!.Value.Y + RemoteBox("proxy").Bounds.Height < manualBox.TranslatePoint(default, window)!.Value.Y && Math.Abs(RemoteBox("proxy").Bounds.Width - manualBox.Bounds.Width) < 1, "URL and manual lists are not vertically stacked with URLs above manual entries");
    string oldManual = manualBox.Text ?? ""; double shortHeight = RemoteBox("proxy").Bounds.Height;
    RemoteBox("proxy").Text = string.Join('\n', Enumerable.Range(0, 30).Select(i => "https://example.com/" + new string('a', 150) + i + ".list"));
    manualBox.Text = string.Join('\n', Enumerable.Range(0, 40).Select(i => "manual" + i + ".example")); Pump();
    Check(RemoteBox("proxy").Bounds.Height > shortHeight * 3 && manualBox.Bounds.Height > 500, "Long lists did not expand the controls");
    foreach (var box in new[] { RemoteBox("proxy"), manualBox })
        Check(box.GetVisualDescendants().OfType<ScrollViewer>().All(x => x.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && x.Extent.Height <= x.Viewport.Height + 1), "A list retained an inner vertical scroll region");
    var pageScroll = (ScrollViewer)NamedControl("PageScroll"); Check(pageScroll.Extent.Height > pageScroll.Viewport.Height, "Long lists did not extend the page scrollbar");
    double rulesTabY = NamedControl("PolicyRulesTab").TranslatePoint(default, window)!.Value.Y;
    pageScroll.Offset = default; manualBox.Focus(); Pump();
    double beforeCaretScroll = pageScroll.Offset.Y;
    window.KeyPressQwerty(PhysicalKey.End, Avalonia.Input.RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.End, Avalonia.Input.RawInputModifiers.Control); Pump();
    Check(manualBox.CaretIndex == manualBox.Text!.Length && pageScroll.Offset.Y > beforeCaretScroll, "Keyboard movement to the end of a long list did not scroll the outer page");
    Check(Math.Abs(NamedControl("PolicyRulesTab").TranslatePoint(default, window)!.Value.Y - rulesTabY) < 1 && !NamedControl("PolicyRulesTab").GetVisualAncestors().Contains(pageScroll), "Rule tabs moved when editing a long list");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "rules-tab-scrolled.png"));
    RemoteBox("proxy").BringIntoView(); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "auto-height-rule-lists.png"));
    manualBox.Text = oldManual;
    RemoteBox("proxy").Text = "https://example.com/proxy-b.list\r\nhttps://example.com/new.list\n"; Pump();
    var urls = Rules.RemoteSpecs(window.Session.Policy["remote_rules"]).Where(x => ProjectOrdering.IsUrlEntry(x) && ProjectOrdering.RemoteGroup(x) == "proxy").Select(x => S(x, "url")).ToArray();
    Check(urls.SequenceEqual(["https://example.com/proxy-b.list", "https://example.com/new.list"]), "Pasting URL lines did not update their group");
    Check(Rules.RemoteSpecs(window.Session.Policy["remote_rules"]).Any(x => S(x, "file") == "local.list"), "URL edit discarded a local rule source");
    ClickNamed("RuleUp_proxy"); ClickNamed("RuleUp_proxy"); ClickNamed("RuleUp_reject");
    Check(window.GetVisualDescendants().OfType<TextBox>().Where(x => x.Name?.StartsWith("RemoteUrls_") == true).Select(x => x.Name).SequenceEqual(["RemoteUrls_proxy", "RemoteUrls_reject", "RemoteUrls_direct"]), "Remote groups did not follow the shared rule order");
    Check(RemoteBox("proxy").Text!.Contains("new.list"), "Changing group order lost edited URLs");
    window.Session.Save(); window.SelectProject("SampleTwo"); window.Navigate("policy"); Pump();
    Check(RemoteBox("proxy").Text!.Contains("new.list") && ProjectOrdering.RuleOrder(window.Session!.Policy)[0] == "proxy", "Grouped URLs or order did not survive save/reload");
    Check(window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "Exact_proxy").Text!.Contains("cdn.example.com"), "Classified fields did not survive reload");
    Check(window.GetVisualDescendants().OfType<Button>().Count(x => x.Name?.StartsWith("ValidateUrls_") == true) == 3, "Each URL group needs a validation icon");
    using (var server = new UiRuleServer())
    {
        string savedUrls = RemoteBox("proxy").Text ?? "";
        RemoteBox("proxy").Text = server.Url + "valid\n" + server.Url + "invalid"; Pump();
        var httpSettings = window.Session!.Settings; httpSettings["http"] = O("trust_env", false); window.Session.Documents["settings.yaml"].Set(httpSettings); window.Session.Save();
        string beforePolicy = window.Session.Documents["policy.yaml"].Text;
        var beforeFiles = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Order().ToArray();
        var checkFlyout = (Flyout)Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(NamedButton("ValidateUrls_proxy"))!;
        string CheckText() => string.Join('\n', ((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<TextBlock>().Select(x => x.Text));
        void WaitCheck(Func<bool> done) { for (int i = 0; i < 1200 && !done(); i++) { Pump(); Thread.Sleep(5); } Pump(); Check(done(), "URL check did not complete in time"); }
        NamedButton("ValidateUrls_proxy").BringIntoView(); Pump();
        double beforeCheckExtent = ((ScrollViewer)NamedControl("PageScroll")).Extent.Height;
        var startPoint = NamedButton("ValidateUrls_proxy").TranslatePoint(new Point(15, 15), window)!.Value;
        window.MouseMove(startPoint); Pump();
        Check(checkFlyout.IsOpen && ((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().All(x => Equals(x.Tag, "pending")), "Hover did not show unexecuted URL statuses");
        window.MouseDown(startPoint, MouseButton.Left); window.MouseUp(startPoint, MouseButton.Left);
        WaitCheck(() => CheckText().Contains("校验完成"));
        Check(Math.Abs(((ScrollViewer)NamedControl("PageScroll")).Extent.Height - beforeCheckExtent) < 1, "URL check shifted page layout");
        checkFlyout.Hide(); window.MouseMove(new Point(10, 10)); Pump();
        var checkPoint = NamedButton("ValidateUrls_proxy").TranslatePoint(new Point(15, 15), window)!.Value;
        window.MouseMove(checkPoint); Pump(); Check(checkFlyout.IsOpen, "Hover did not reopen URL result flyout");
        Check(CheckText().Contains("1/2 通过") && ((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "pass") && ToolTip.GetTip(x)?.ToString()?.Contains("2 条规则") == true) && ((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "fail") && ToolTip.GetTip(x)?.ToString()?.Contains("HTML/XML") == true) && CheckText().Contains(server.Url + "invalid"), "URL results did not identify each URL and its parsing outcome");
        Check(!window.Session.Dirty && window.Session.Documents["policy.yaml"].Text == beforePolicy && beforeFiles.SequenceEqual(Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Order()), "URL validation changed config, outputs or cache files");
        Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<ContentControl>().Any(x => x.Name == "UrlCheckResults_proxy"), "URL results occupied page layout"); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "rule-url-validation.png"));
        checkFlyout.Hide(); RemoteBox("proxy").Text = string.Join('\n', Enumerable.Range(1, 5).Select(i => server.Url + "valid?" + i + "&source=remote-rules/" + new string('a', 90) + ".list")); Pump();
        ClickNamed("ValidateUrls_proxy"); WaitCheck(() => CheckText().Contains("校验完成"));
        var resultRows = ((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<Grid>().Where(x => x.Name?.StartsWith("UrlCheckRow_") == true).ToArray();
        Check(CheckText().Contains("5/5 通过") && resultRows.Length == 5 && CheckText().Contains("valid?5"), "Validation results are not fully displayed");
        Check(!((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<Button>().Any(), "Validation still has pagination");
        foreach (var row in resultRows)
        {
            var label = row.Children.OfType<TextBlock>().Single(); var icon = row.Children.OfType<Avalonia.Controls.Shapes.Path>().Single();
            Check(icon.Bounds.X > label.Bounds.Right && Math.Abs(icon.Bounds.Center.Y - label.Bounds.Center.Y) < 1, "URL and status are not aligned left/right");
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using(var fullResults = window.CaptureRenderedFrame()) fullResults!.Save(Path.Combine(output, "url-full-results.png"));
        checkFlyout.Hide();
        RemoteBox("proxy").Text = server.Url + "slow"; Pump(); Check(CheckText().Contains("请重新校验") && !CheckText().Contains("1/2 通过"), "Editing URLs retained stale passing results: " + CheckText());
        ClickNamed("ValidateUrls_proxy");
        Check(((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(x => Equals(x.Tag, "pending")), "Pending URL does not have a neutral status icon");
        ClickNamed("ValidateUrls_proxy"); WaitCheck(() => CheckText().Contains("已取消"));
        ClickNamed("ValidateUrls_proxy"); RemoteBox("proxy").Text = savedUrls; Pump();
        for (int i = 0; i < 20; i++) { Pump(); Thread.Sleep(5); }
        Check(CheckText().Contains("请重新校验") && !CheckText().Contains("校验完成"), "Cancelled check overwrote edited-URL status");
        RemoteBox("proxy").Text = server.Url + "slow"; Pump(); ClickNamed("ValidateUrls_proxy"); window.Navigate("sources"); Pump();
        window.Navigate("policy"); Pump(); checkFlyout = (Flyout)Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(NamedButton("ValidateUrls_proxy"))!; Check(CheckText().Contains("尚未执行校验") && ((Control)checkFlyout.Content!).GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().All(x => Equals(x.Tag, "pending")), "Leaving the page retained another request's result");
        RemoteBox("proxy").Text = ""; Pump(); ClickNamed("ValidateUrls_proxy");
        Check(NamedControl("Notifications").GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "请先填写至少一个 URL。"), "Empty URL warning was not a shared overlay");
        Check(!NamedControl("PageScroll").GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "请先填写至少一个 URL。"), "Warning was embedded in the page");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using(var notificationFrame = window.CaptureRenderedFrame()) notificationFrame!.Save(Path.Combine(output, "notification-overlay.png"));
        window.MouseMove(new Point(10, 10)); RemoteBox("proxy").Focus();
        for (int i = 0; i < 1600 && NamedControl("Notifications").GetVisualDescendants().OfType<Border>().Any(x => x.Name == "Notice"); i++) { Pump(); Thread.Sleep(5); }
        Check(!NamedControl("Notifications").GetVisualDescendants().OfType<Border>().Any(x => x.Name == "Notice"), "Notification did not automatically disappear");
        RemoteBox("proxy").Text = savedUrls; Pump(); window.Session.Save();
    }
    foreach (int width in new[] { 1220, 960 })
    {
        window.Width = width;
        foreach (string page in MainWindow.PageIds)
        {
            window.Navigate(page); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, width + "-" + page + ".png"));
        }
        foreach (string theme in new[] { "light", "dark" })
        {
            Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            window.Navigate("policy"); Pump(); window.GetVisualDescendants().OfType<StackPanel>().Single(x => x.Name == "RemoteRuleGroups").BringIntoView(); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, width + "-" + theme + "-remote-groups.png"));
        }
    }
    window.Width = 960; window.Height = 640; window.Navigate("sources"); Pump(); ClickNamed("EditSource0");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    foreach (var node in window.GetVisualDescendants().OfType<Button>().Where(x => x.Name?.StartsWith("Flow_") == true))
    { var position = node.TranslatePoint(new Point(0, 0), window)!.Value; Check(position.X >= 216 && position.X + node.Bounds.Width <= 961 && position.Y + node.Bounds.Height < 320, "Flow node clipped in minimum viewport: " + node.Name); }
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "960x640-source-editor.png"));
    var normalContent = (Control)window.Content!; window.Content = null;
    window.Width = 1220; window.Height = 850; var scaled = new LayoutTransformControl { Child = normalContent, LayoutTransform = new ScaleTransform(1.25, 1.25) }; window.Content = scaled; Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    foreach (var node in window.GetVisualDescendants().OfType<Button>().Where(x => x.Name?.StartsWith("Flow_") == true))
    { var position = node.TranslatePoint(new Point(0, 0), window)!.Value; Check(position.X >= 0 && position.X + node.Bounds.Width * 1.25 <= 1221, "Flow node clipped with 125% layout scaling: " + node.Name); }
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "125-percent-source-editor.png"));
    window.Content = null; scaled.Child = null; window.Content = normalContent; window.Height = 850; Pump();
    void Pump() { Dispatcher.UIThread.RunJobs(); }
    void Click(Control parent, string text)
    {
        parent.GetVisualDescendants().OfType<Button>().First(x => x.Content?.ToString() == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    }
    Window Dialog(string title)
    {
        for (int i = 0; i < 50; i++) { Pump(); var dialog = window.OwnedWindows.FirstOrDefault(x => x.IsVisible && x.Title == title); if (dialog is not null) return dialog; Thread.Sleep(5); }
        throw new Exception("Dialog missing: " + title);
    }
    void Submit(string title, string name, string confirm)
    {
        var dialog = Dialog(title); dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = name; Pump(); Click(dialog, confirm); Pump();
    }
    void ProjectAction(string action, string name)
    {
        if (action == "restore") Dialog("项目回收站").GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Tag, "restore-project:" + name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else { if (window.Session?.ProjectName != name) window.SelectProject(name); ClickNamed(action switch { "rename" => "RenameProject", "copy" => "CopyProject", _ => "DeleteProject" }); }
        Pump();
    }
    Check(!MainWindow.PageIds.Contains("projects"), "Project management page was retained");
    window.SelectProject("Example"); window.Navigate("policy"); Pump();
    Check(window.Session!.IsReadOnly && !window.Session.Dirty && !NamedButton("DeleteProject").IsEnabled && !NamedButton("RenameProject").IsEnabled && NamedButton("CopyProject").IsEnabled, "Example actions violate read-only state");
    Check(window.GetVisualDescendants().OfType<TextBox>().All(x => x.IsReadOnly || !x.IsEffectivelyEnabled || !x.IsEffectivelyVisible), "Example has writable rule fields: " + string.Join(",", window.GetVisualDescendants().OfType<TextBox>().Where(x => !x.IsReadOnly).Select(x => x.Name)));
    window.Navigate("overview"); Pump(); ClickNamed("NewProject");
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using (var promptFrame = Dialog("新建项目").CaptureRenderedFrame()) promptFrame!.Save(Path.Combine(output, "new-project-dialog.png"));
    Check(Dialog("新建项目").GetVisualDescendants().OfType<TextBox>().Single().Text == "", "New project name is not an empty modal input");
    Submit("新建项目", "ProjectC", "创建");
    Check(window.Session?.ProjectName == "ProjectC" && window.CurrentPage == "sources", "Create did not select new project and open its settings");
    window.Navigate("build"); Pump(); ClickNamed("GeneratePrimary"); Check(!window.IsBusy && window.CurrentPage == "build" && !window.OwnedWindows.Any(x => x.IsVisible), "Incomplete configuration started work or requested confirmation before validation");
    var repair = window.GetVisualDescendants().OfType<Button>().First(x => Equals(x.Tag, "fix:SourceUrl0")); repair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    Check(window.CurrentPage == "sources" && window.GetVisualDescendants().OfType<TextBox>().Any(x => x.Name == "SourceUrl0"), "Repair action failed to locate missing subscription field");
    window.Navigate("overview"); Pump(); ProjectAction("rename", "ProjectC"); Submit("重命名项目", "Renamed", "保存名称");
    Check(window.Session?.ProjectName == "Renamed" && !Directory.Exists(Path.Combine(workspace, "config", "ProjectC")), "Rename left selection or files at the old name");
    ProjectAction("copy", "Renamed"); Submit("复制项目", "Copy", "创建"); Check(window.Session?.ProjectName == "Copy", "Copy did not select copied project");
    ProjectAction("delete", "Copy"); Click(Dialog("删除项目"), "取消"); Check(window.Session?.ProjectName == "Copy", "Cancelling deletion changed selection");
    window.Session!.Documents["settings.yaml"].SetText(window.Session.Documents["settings.yaml"].Text + "\n# unsaved\n");
    ProjectAction("delete", "Copy"); Click(Dialog("有未保存的修改"), "取消"); Check(window.Session.Dirty && window.Session.ProjectName == "Copy", "Dirty deletion cancelled incorrectly"); window.Session.Save();
    window.Navigate("sources"); Pump(); ClickNamed("DeleteProject"); Click(Dialog("删除项目"), "移入回收站");
    Check(window.Session?.ProjectName != "Copy" && BuildEngine.DiscoverProjects(workspace).Count == 3, "Delete did not refresh selection and project list");
    Check(Directory.EnumerateDirectories(ProjectStore.TrashPath(workspace)).Any(x => File.Exists(Path.Combine(x, "config", "settings.yaml"))), "Deleted inputs were not recoverable");
    Check(window.CurrentPage == "overview", "Deleting from the project selector did not select the next project overview");
    ClickNamed("ProjectTrash");
    ProjectAction("restore", "Copy"); Submit("恢复项目", "Restored", "恢复"); Check(window.Session?.ProjectName == "Restored", "Restore did not select recovered project");
    foreach (string name in BuildEngine.DiscoverProjects(workspace).ToArray())
    {
        window.SelectProject(name); window.Navigate("overview"); Pump(); ProjectAction("delete", name); Click(Dialog("删除项目"), "移入回收站");
    }
    Check(window.Session?.ProjectName == "Example" && window.CurrentPage == "overview", "Deleting the last project did not return to Example");
    Check(((ComboBox)NamedControl("CurrentProject")).Items.Cast<string>().SequenceEqual(["Example"]), "Example was not the sole remaining project");
    Check(!NamedButton("DeleteProject").IsEnabled, "Delete remains enabled with no current project");
    ClickNamed("NewProject"); Submit("新建项目", "Restart", "创建"); Check(window.Session?.ProjectName == "Restart", "Could not create after deleting the last project");
    string broken = Path.Combine(workspace, "config", "Broken"); Directory.CreateDirectory(broken); File.WriteAllText(Path.Combine(broken, "settings.yaml"), "{}");
    window.OpenWorkspace(workspace); window.SelectProject("Broken"); Pump();
    Check(window.Session is null && NamedButton("DeleteProject").IsEnabled, "Incomplete project cannot be managed from the selector");
    ClickNamed("DeleteProject"); Click(Dialog("删除项目"), "移入回收站");
    Check(!Directory.Exists(broken) && window.Session is not null, "Incomplete project deletion failed");
    window.Navigate("settings"); Pump(); Click(window, "通知记录");
    Check(Dialog("通知记录").GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "请先填写至少一个 URL。"), "Expired notification is not available in shared history"); Dialog("通知记录").Close(); Pump();
    window.Close(); Dispatcher.UIThread.RunJobs(); Check(!window.IsVisible, "Clean window did not close on the first attempt");
    FunctionalUiRegression.Run(workspace, output, Check);
    AsnUiRegression.Run(workspace, output, Check);
    Console.WriteLine($"RESULT {checks} UI checks passed; screenshots: {output}");
}

finally
{
    // App state and fixtures were created only in this dedicated temporary workspace.
    if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
}

sealed class UiRuleServer : IDisposable
{
    readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    public int Requests;
    public string Url => "http://127.0.0.1:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port + "/";
    public UiRuleServer() { listener.Start(); _ = Task.Run(Run); }
    async Task Run()
    {
        while (!stop.IsCancellationRequested)
            try { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Reply(client); }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException) { break; }
    }
    async Task Reply(System.Net.Sockets.TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream); string first = await reader.ReadLineAsync() ?? ""; Interlocked.Increment(ref Requests);
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
            if (first.Contains(" /slow ") || first.Contains(" /subscription-slow ")) await Task.Delay(3000, stop.Token);
            string body = first.Contains(" /subscription-rules ") ? "rules: ['DOMAIN,fetched-subscription.example,External']\nproxies: [{password: PRIVATE}]" : first.Contains(" /subscription") ? "proxies: [{name: 香港独立订阅, type: http, server: localhost, port: 8080}]" : first.Contains(" /invalid ") ? "<html>Login required</html>" : "DOMAIN-SUFFIX,example.com\nDOMAIN,api.example.com\n";
            int status = first.Contains(" /subscription-fail ") ? 403 : 200;
            byte[] data = System.Text.Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n")); await stream.WriteAsync(data);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

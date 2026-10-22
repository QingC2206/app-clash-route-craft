using Avalonia.Controls;
using Avalonia.Layout;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    bool UsesDnsPrivacy()
    {
        try { return session is not null && DnsPrivacy.Enabled(session.Policy); }
        catch (BuildException) { return false; }
    }
    Control DnsPrivacyForm()
    {
        bool enabled = DnsPrivacy.Enabled(session!.Policy), baseline = UsesSubscriptionBaseline();
        var toggle = Check("启用 DNS 隐私增强（Fake-IP）", enabled, value =>
        {
            Mutate("policy.yaml", policy => DnsPrivacy.SetEnabled(policy, value)); RenderPage();
            SetStatus(value ? "已切换为隐私增强，请保存并重新生成；MC2 还需应用配套 DNS。" : "已关闭隐私增强，请保存并重新生成；已应用过增强的 MC2 仍需恢复 DNS。");
        }); toggle.Name = "DnsPrivacyEnabled";
        var card = Card("DNS 模式", toggle,
            Label(enabled ? "已选择隐私增强：普通域名使用 Fake-IP；代理 DNS 经所选出口，直连连接使用国内直连 DNS。" : baseline ? "当前由客户端管理 DNS，保持现有默认行为；原有 DNS 设置保留但不应用。" : "当前使用项目原有 DNS；启用增强会临时替代它，关闭后恢复原有设置。", true, 13),
            Label("使用建议：访问 Claude 等服务、希望减少 DNS 出口不一致时，建议开启；日常场景通常保持默认。", true, 13),
            Label("收益：加密查询 · 减少代理域名的本地 DNS 暴露。\n代价：首次查询或连接可能稍慢 · 依赖所选出口 · 少数应用的 Fake-IP 兼容性。", true, 12),
            Label("缓存和连接复用可减轻后续开销。此功能不能保证避免平台风控，也不会接管浏览器自带的独立 DoH。", true, 12),
            Label("切换只修改当前项目的草稿。保存并重新生成后使用；不会自动改路由器、订阅、节点或 QUIC。", true, 12));
        card.Name = enabled ? "PrivacyDns" : "ClientManagedDns";
        var content = Stack(card);
        if (enabled)
        {
            var exit = new ComboBox { Name = "DnsPrivacyExit", ItemsSource = new[] { "住宅出口（默认）", "机场入口" }, SelectedIndex = DnsPrivacy.Exit(session.Policy) == "airport" ? 1 : 0, MinWidth = 230, IsEnabled = !session.IsReadOnly };
            exit.SelectionChanged += (_, _) =>
            {
                if (exit.SelectedIndex < 0) return;
                Mutate("policy.yaml", policy => M(policy, "dns_privacy")["proxy_exit"] = exit.SelectedIndex == 0 ? "residential" : "airport");
                SetStatus("代理 DNS 出口已修改，请保存并重新生成。");
            };
            content.Children.Add(Card("代理 DNS 出口", exit,
                Label("跟随对应策略组在客户端的选择。没有住宅节点时请先添加住宅，或明确改选机场入口；不会自动降级为直连。", true, 13),
                Label("不额外添加域名库或 DNS 规则集。末尾 GEOIP 判断可能先查询代理 DNS，再在直连时重新解析。", true, 12)));
        }
        content.Children.Add(Card("MC2 的应用与恢复", Label("MC2 不采用主 YAML 的 DNS，需在“编辑 Fake-IP”粘贴配套文件，选择 Fake-IP，保存并重启。关闭增强后也需恢复 MC2 DNS，仅重新导入主配置不会撤销。", true, 13),
            Button("查看 DNS 输出与官方默认恢复配置", () => NavigateOutput("HomeChain.MC2DNS.yaml"))));
        return content;
    }
    Control DnsOutputPage()
    {
        var report = session!.ReadReport(); var dnsReport = M(report ?? new(), "dns");
        string path = Path.Combine(session.OutputPath, "HomeChain.MC2DNS.yaml"), expected = S(dnsReport, "mc2_sha256");
        bool available = expected.Length > 0 && DependencySnapshot.FileHash(path) == expected;
        bool current = false;
        try { current = available && session.OutputIsCurrent(); } catch (BuildException) { }
        var content = Stack();
        if (available)
        {
            string mode = S(dnsReport, "mc2_mode", "redir-host"), title = mode == "fake-ip" ? "Fake-IP" : "Redir-Host";
            content.Children.Add(Note((current ? "" : "这是上次成功输出，配置已有变化，请重新生成后再应用。\n")
                + $"先导入主配置并保留默认 DNS 确认可以启动，再将下方全文（含 dns:）粘贴到 MC2 的“编辑 {title}”。选择 {title}，保存并重启。普通 Mihomo 客户端从主 YAML 读取 DNS。" + GeoDependencyNotice(report)));
            var editor = Editor(File.ReadAllText(path), true); editor.Name = "DnsOutput";
            var copy = IconButton("CopyDns", "复制 DNS 配置全文", "M5,2 L13,2 L13,12 L5,12 Z M2,5 L2,15 L10,15", async () =>
            {
                if (Clipboard is not null && !IsBusy) { await Clipboard.SetTextAsync(session.ReadValidatedOutput("HomeChain.MC2DNS.yaml")); SetStatus("已复制配套 DNS，仍需在 MC2 应用并重启"); }
            }); copy.IsEnabled = current && OutputValidated();
            content.Children.Add(CodeCard(editor, copy));
        }
        else content.Children.Add(Card("未生成配套 DNS", Label(S(report ?? new(), "target") == "clash" ? "当前生成目标是普通 Mihomo，DNS 如有配置则包含在主 YAML 中，无需配套文件。" : "默认模式由客户端管理 DNS；启用隐私增强后请保存并重新生成。若之前已在 MC2 应用增强配置，请按下方步骤恢复。", true, 13)));
        var copyDefault = AsyncButton("复制官方默认 DNS（用于恢复）", async () =>
        {
            if (Clipboard is not null && !IsBusy) { await Clipboard.SetTextAsync(DnsPrivacy.DefaultText()); SetStatus("已复制 MC2 1.2.2 原装 Fake-IP DNS，请在 MC2 的“编辑 Fake-IP”应用并重启"); }
        }); copyDefault.Name = "CopyDefaultDns"; copyDefault.IsEnabled = !IsBusy;
        var defaultEditor = Editor(DnsPrivacy.DefaultText(), true); defaultEditor.Name = "DefaultDnsOutput";
        content.Children.Insert(1, Card("恢复 MC2 官方默认 DNS", Label("内置 MC2 1.2.2 ARM64 原装 Fake-IP 模板。生成失败时仍可复制；仅恢复 DNS。", true, 13),
            Row(copyDefault, Button("打开输出目录", () => OpenPath(session.OutputPath))),
            new Expander { Header = "恢复步骤与适用版本", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Stack(
                Label("1. 关闭软件的隐私增强，保存并重新生成。\n2. 复制默认 DNS 全文，粘贴到 MC2 的“DNS 设置 → 编辑 Fake-IP”。\n3. 选择 Fake-IP，保存并重启。仅重新导入主配置不会恢复 DNS。", true, 13),
                Label("MC2 输出同时保存 HomeChain.MC2DNS.Default.yaml 和 HomeChain.DNS-README.txt。如曾修改过原 DNS，请优先恢复自己的备份。原装模板引用 MC2 已有的 GeoSite.dat；增强方案不新增域名库。此文件不是节点或规则备份。", true, 12)) },
            new Expander { Name = "DefaultDnsPreview", Header = "查看官方默认 DNS", HorizontalAlignment = HorizontalAlignment.Stretch, Content = CodeCard(defaultEditor) }));
        return content;
    }
}

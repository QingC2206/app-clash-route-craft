using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>An optional DNS preset; does not translate routing rules or introduce rule providers.</summary>
public static class DnsPrivacy
{
    public const int ArtifactVersion = 2;
    public const string LegacyDefaultSuffix = ".MC2DNS.Default.yaml";
    public const string DefaultDirectory = "default";
    public const string GuideSuffix = ".DNS-README.txt";
    public const string OfficialVersion = "MC2 1.2.2 ARM64";
    public const string OfficialSourceHash = "22c47f81d7f55960bc9400f58ab1c9a4741bc14d2aaadd743b633c2e727c3577";
    public const string RedirHostSourceHash = "38f92c2b3111b89b00c48caaa55c1be852cb086a8622d6543b1a6c1425f38323";
    public static string DefaultRelativePath(string mode) => DefaultDirectory + "/" + (mode switch
    {
        "fake-ip" => "Fake-IP.yaml",
        "redir-host" => "Redir-Host.yaml",
        _ => throw new BuildException("未知的默认 DNS 模式")
    });
    public static IReadOnlyList<(string Path, byte[] Bytes)> DefaultOutputs(string directory) =>
        new[] { "redir-host", "fake-ip" }.Select(mode => (Path.Combine(directory, DefaultRelativePath(mode)), DefaultBytes(mode))).ToArray();
    public static bool Enabled(JsonObject policy) => B(M(policy, "dns_privacy"), "enabled");
    public static string Exit(JsonObject policy)
    {
        string value = S(M(policy, "dns_privacy"), "proxy_exit", "residential");
        if (value is not "residential" and not "airport") throw new BuildException("DNS 隐私增强的代理出口只能选择住宅出口或机场入口");
        return value;
    }
    public static void SetEnabled(JsonObject policy, bool enabled)
    {
        if (policy["dns_privacy"] is null) policy["dns_privacy"] = O("proxy_exit", "residential");
        M(policy, "dns_privacy")["enabled"] = enabled;
    }
    public static string Apply(JsonObject configuration, JsonObject policy, string residential, string airport)
    {
        if (!Enabled(policy)) return "";
        string exit = Exit(policy) == "residential" ? residential : airport;
        if (exit.Length == 0) throw new BuildException(Exit(policy) == "residential"
            ? "DNS 隐私增强默认使用住宅出口，请先添加住宅节点，或将代理 DNS 出口改为机场入口；不会自动改成直连"
            : "没有可用的机场入口，请调整订阅节点筛选或改选住宅出口");
        configuration["dns"] = Create(exit);
        ProfileCompiler.ValidateDns(configuration);
        return exit;
    }
    public static JsonObject Create(string exit) => O(
        "enable", true, "listen", "127.0.0.1:1053", "enhanced-mode", "fake-ip", "fake-ip-range", "198.18.0.1/16",
        "fake-ip-filter-mode", "blacklist", "fake-ip-filter", A("localhost", "+.localhost"),
        "ipv6", false, "prefer-h3", false, "respect-rules", false, "direct-nameserver-follow-policy", false,
        "default-nameserver", A("https://223.5.5.5/dns-query"),
        "proxy-server-nameserver", A("https://223.5.5.5/dns-query#DIRECT"),
        "direct-nameserver", A("https://223.5.5.5/dns-query#DIRECT"),
        "nameserver", A("https://1.1.1.1/dns-query#" + exit, "https://8.8.8.8/dns-query#" + exit));

    // Recovery remains available from the binary even when project inputs or generated files are broken.
    public static string DefaultText(string mode = "fake-ip")
    {
        _ = DefaultRelativePath(mode);
        string title = mode == "fake-ip" ? "Fake-IP" : "Redir-Host";
        string source = mode == "fake-ip" ? "fakeip" : "redirhost";
        string hash = mode == "fake-ip" ? OfficialSourceHash : RedirHostSourceHash;
        return $"# MC2 1.2.2 ARM64 原装 {title} DNS；仅恢复 DNS，不恢复节点或规则。\n"
            + $"# 来源：MC2_1.2.2_ARM64.tar.gz / merlinclash/yaml_dns/{source}.yaml\n"
            + "# 原始文件 SHA-256: " + hash + "\n"
            + $"# 将全文（含 dns:）粘贴到 MC2 的“编辑 {title}”，选择 {title}，保存并重启。\n"
            + "# 保留原装 geosite:gfw 引用，使用 MC2 自带 GeoSite.dat；不是新增下载的域名库。\n"
            + BuiltInDefaults.Text($"mc2-{source}-default.yaml");
    }
    public static byte[] DefaultBytes(string mode = "fake-ip") => Encoding.UTF8.GetBytes(DefaultText(mode));
    public static string Guide(string stem, string mode) => $"""
        DNS 应用与恢复说明

        主配置：{stem}.yaml
        本次 DNS：{(mode == "fake-ip" ? "Fake-IP 隐私增强 / 自定义 Fake-IP" : mode.Length > 0 ? "自定义 " + mode : "由客户端管理（没有生成增强 DNS）")}

        普通 Mihomo 客户端
        {(mode.Length == 0 ? "当前主 YAML 不添加 DNS，沿用客户端现有设置。启用增强并重新生成后，DNS 才会写入主 YAML。" : "DNS 已包含在主 YAML 中。导入后仍需确认客户端没有用自己的 DNS 覆盖配置。")}

        MC2 应用
        MC2 1.2.2 导入会丢弃主 YAML 的 dns 字段，启动时使用自己的 DNS 模板。
        1. 先保存当前可联网的主配置及 MC2 DNS 文本，便于恢复个人修改。
        2. 导入并选择 {stem}.yaml，先保留现有默认 DNS，确认主配置可以启动。
        3. 若本次生成了 {stem}.MC2DNS.yaml，将其中全文（包括 dns:）粘贴到“DNS 设置”中与 enhanced-mode 对应的编辑窗口（Fake-IP 或 Redir-Host）。
        4. DNS 方案选择与该文件 enhanced-mode 一致的模式，保存并重启 MC2。
        5. 在 MC2 当前配置中核对 enhanced-mode、nameserver 和出口组名称。软件中的开关不会自动修改路由器。

        恢复 MC2 原装默认 DNS
        1. 在软件关闭“DNS 隐私增强”，保存并重新生成；订阅基线的主配置不再添加 dns。
        2. 根据要恢复的模式选择固定文件：{DefaultRelativePath("redir-host")} 或 {DefaultRelativePath("fake-ip")}。
        3. 将所选文件全文（含 dns:）粘贴到“DNS 设置 → 编辑 Redir-Host”或“编辑 Fake-IP”，两者必须对应。
        4. 选择对应的 Redir-Host 或 Fake-IP 模式，保存并重启 MC2；必要时重连设备或清理设备 DNS 缓存。
        仅重新导入主 YAML 不会撤销 MC2 已保存的增强 DNS。关闭增强后仍需完成上述恢复。
        若此前自行修改过 MC2 默认 DNS，应恢复步骤 1 中自己保存的文本。
        此恢复文件是 {OfficialVersion} 原装模板，不是任意版本通用的“官方默认”，也不是完整路由器或订阅配置备份。
        原装模板引用 geosite:gfw，依赖 MC2 原有 GeoSite.dat；增强方案没有增加域名库。
        两份固定默认文件始终一起输出，与主配置文件名和增强开关无关；不要把两份内容合并使用。
        即使生成失败或文件被删除，也可在软件 DNS 输出页选择模式并复制内置官方默认 DNS。

        增强方案边界
        代理 DNS 默认经过住宅出口；住宅选择链式路径时会经过机场入口。
        直连连接使用国内直连 DNS；代理节点域名使用独立引导解析，避免循环。
        末尾 GEOIP 判断可能先进行代理 DNS 查询，最终直连时再用国内 DNS 解析。
        普通域名使用 Fake-IP，localhost 使用本地 hosts；局域网自定义域名依赖现有本地 DNS / hosts 设置。
        此选项不改变订阅规则、节点协议或 QUIC，不自动接管浏览器独立 DoH、IPv6 绕行或路由器自身 DNS。
        """ + "\n";
}

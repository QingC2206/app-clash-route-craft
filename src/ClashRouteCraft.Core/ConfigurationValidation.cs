using System.Net;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Local checks shared by preflight, readiness and generation.</summary>
public static class ConfigurationValidation
{
    public static JsonObject ExpandSettings(JsonObject raw)
    {
        var expanded = new JsonObject();
        foreach (var (key, value) in raw)
            expanded[key] = key == "sources"
                ? new JsonArray(List(value, "sources").Select(x => !B(Map(x, "订阅"), "enabled", true) ? x!.DeepClone() : ExpandEnvironment(x)).ToArray())
                : ExpandEnvironment(value);
        return expanded;
    }
    public static string Target(JsonObject settings)
    {
        string value = S(settings, "target", "mc2");
        if (value is not "mc2" and not "clash") throw new BuildException("生成目标只支持 mc2 或 clash");
        return value;
    }
    public static void Settings(JsonObject settings)
    {
        string[] allowed = ["sources", "selection", "runtime", "http", "validation", "target"];
        if (settings.Any(x => !allowed.Contains(x.Key))) throw new BuildException("项目 settings 含未知字段；只支持 sources/selection/runtime/http/validation/target");
        Target(settings);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int enabled = 0;
        foreach (var raw in L(settings, "sources"))
        {
            var source = Map(raw, "订阅");
            string[] permitted = ["name", "url", "priority", "enabled", "include", "exclude", "user_agent", "headers"];
            if (source.Any(x => !permitted.Contains(x.Key))) throw new BuildException("订阅含旧版或未知字段；不再使用 file/fallback_file/allow_stale/on_error");
            string name = FileName(Name(source["name"], "订阅"), "订阅名称");
            if (!names.Add(name)) throw new BuildException("订阅名称不能重复（文件名不区分大小写）");
            if (!B(source, "enabled", true)) continue;
            enabled++;
            Pattern(S(source, "include", S(M(settings, "selection"), "include", Nodes.Asia)), name);
            string exclude = S(source, "exclude", S(M(settings, "selection"), "exclude")); if (exclude.Length > 0) Pattern(exclude, name);
            Headers(source);
        }
        if (enabled == 0) throw new BuildException("至少启用一个订阅");
        var selection = M(settings, "selection");
        if (I(selection, "interval", 300) < 1 || I(selection, "timeout", 5000) < 1 || I(selection, "tolerance", 50) < 0) throw new BuildException("健康检查间隔、超时或延迟容差无效");
        if (!HttpUrl(S(selection, "test_url", "https://www.gstatic.com/generate_204"))) throw new BuildException("健康检查地址需要 HTTP(S) URL");
        if (M(settings, "runtime").Any(x => new[] { "proxies", "proxy-providers", "proxy-groups", "rules", "rule-providers" }.Contains(x.Key))) throw new BuildException("runtime 不能覆盖生成器管理的代理、分组和规则字段");
        if (M(settings, "runtime").ContainsKey("dns")) throw new BuildException("DNS 统一由 policy.yaml 的顶层 dns 维护");
        var http = M(settings, "http");
        I(http, "timeout", 30); I(http, "max_bytes", 16 * 1024 * 1024); I(http, "cache_max_age", 86400);
        B(http, "trust_env", true); B(http, "allow_stale", true); B(http, "prefer_cache");
        if (I(http, "cache_max_age", 86400) < 0) throw new BuildException("规则缓存有效期不能为负数");
        Headers(http);
        if (S(http, "proxy") is { Length: > 0 } proxy && (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https" and not "socks5")) throw new BuildException("HTTP 代理地址无效");
        B(M(settings, "validation"), "required");
    }
    public static bool HttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !HasPlaceholder(JsonValue.Create(value));
    public static void Headers(JsonObject spec)
    {
        if (S(spec, "user_agent").IndexOfAny(['\r', '\n']) >= 0) throw new BuildException("请求头格式无效");
        foreach (var (key, value) in M(spec, "headers"))
        {
            string text = value is JsonArray list ? string.Join(", ", list.Select(x => S(x))) : S(value);
            if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) || text.IndexOfAny(['\r', '\n']) >= 0) throw new BuildException("请求头格式无效");
        }
    }
    public static bool Download(WorkspaceSession session, JsonObject source, BuildOptions options)
    {
        if (options.Offline || S(source, "url").Length == 0) return false;
        return options.Force || options.AutoFetch && !File.Exists(session.SnapshotPath(S(source, "name")));
    }
    public static JsonObject EffectiveSettings(JsonObject raw, string workspace, ValidationPaths? resources = null)
    {
        var settings = ExpandSettings(raw); Settings(settings);
        if (!settings.ContainsKey("validation") || resources is not null)
        {
            resources ??= CoreValidation.FindResources(workspace);
            settings["validation"] = O("mihomo", resources.Mihomo, "data_dir", resources.DataDirectory, "mc2_package", resources.Mc2Package, "required", true);
        }
        return settings;
    }
    public static void Resources(JsonObject settings, string project, JsonObject? policy = null)
    {
        var validation = M(settings, "validation"); if (!B(validation, "required")) return;
        string Resolve(string key) => Path.GetFullPath(S(validation, key), project);
        if (S(validation, "mihomo").Length == 0 || !File.Exists(Resolve("mihomo"))) throw new BuildException("缺少 Mihomo 校验程序；请在应用设置中下载缺失资源，或检查高级 YAML 中的 validation");
        if (policy is not null && !SubscriptionBaseline.Enabled(policy) && GeoRules.Mode(policy) == "native")
        {
            var known = new List<string>();
            foreach (string group in new[] { "direct", "reject", "proxy" }) known.AddRange(L(policy, group).Select(x => Rules.WithAction(x, "DIRECT")));
            foreach (var first in L(policy, "first")) known.Add(Rules.WithAction(Map(first, "前置规则")["match"], "DIRECT", inputFormat: "match"));
            if (B(policy, "domestic_direct", true)) known.AddRange(["GEOSITE,CN,DIRECT", "GEOIP,CN,DIRECT"]);
            var config = Merge(O("geodata-mode", true), M(settings, "runtime")); config["rules"] = A(known);
            config["dns"] = DnsPrivacy.Enabled(policy) ? DnsPrivacy.Create("住宅出口") : policy["dns"]?.DeepClone();
            foreach (string file in GeoRules.RequiredDatabases(config))
                if (S(validation, "data_dir").Length == 0 || !File.Exists(Path.Combine(Resolve("data_dir"), file))) throw new BuildException($"内核校验缺少 {file}，请补齐校验数据库；客户端使用原生 GEO 也需要对应数据库。");
        }
        if (Target(settings) == "mc2" && S(validation, "mc2_package").Length > 0 && !File.Exists(Resolve("mc2_package"))) throw new BuildException("显式指定的 validation.mc2_package 不存在；可移除此可选配置，仅校验生成的主配置和 DNS");
    }
    public static void CacheNodes(JsonArray nodes)
    {
        int index = 0;
        foreach (var raw in nodes)
        {
            index++; var node = Map(raw, "缓存节点");
            if (HasPlaceholder(node)) throw new BuildException("缓存节点仍包含占位符");
            if (!node.ContainsKey("server")) continue;
            string server = S(node, "server");
            if (Uri.CheckHostName(server.Trim('[', ']')) == UriHostNameType.Unknown || server != server.Trim()) throw new BuildException($"缓存节点 #{index} 的服务器地址无效");
            string port = S(node, "port");
            if (node["port"] is not JsonValue || port.Length == 0 || port.Any(c => c is < '0' or > '9') || !int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) || number is < 1 or > 65535)
                throw new BuildException($"缓存节点 #{index} 的 port 应为 1～65535 的整数或数字字符串；请更新订阅或重新导入节点文件");
        }
    }
}

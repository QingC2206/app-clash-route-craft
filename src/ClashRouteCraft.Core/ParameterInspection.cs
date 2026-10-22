using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record ParameterCheck(string Title, string Message, string State, ConfigurationIssue? Issue = null, string? Key = null);

/// <summary>Read-only preflight of the current editor state. Never consumes subscription URLs.</summary>
public static class ParameterInspection
{
    const string RemoteContentTitle = "远程名单 · 内容检查";
    /// <summary>The user-triggered check also downloads and validates rule URLs without writing caches.</summary>
    public static async IAsyncEnumerable<ParameterCheck> InspectAsync(WorkspaceSession session, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        foreach (var local in Inspect(session, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            if (local.Title != RemoteContentTitle) { yield return local; continue; }
            JsonObject http = new(); JsonObject[] urls = [];
            var preparation = Evaluate(RemoteContentTitle, "policy", "RemoteUrls_proxy", () =>
            {
                var policy = SubscriptionBaseline.ExpandPolicy(session.Policy);
                if (SubscriptionBaseline.Enabled(policy) && !B(policy, "extra_rules_enabled", true)) return ("附加规则已停用", false);
                urls = ProjectOrdering.OrderedRemotes(policy).Where(x => B(x, "enabled", true) && S(x, "url").Length > 0).ToArray();
                http = Map(ExpandEnvironment(M(session.Settings, "http")), "http");
                return ("", false);
            });
            if (preparation.State == "fail") { yield return preparation; continue; }
            if (local.State == "fail" || urls.Length == 0) yield return local;
            var counts = new Dictionary<string, int>(); int sequence = 0;
            foreach (var source in urls)
            {
                cancellation.ThrowIfCancellationRequested();
                string? group = ProjectOrdering.RemoteGroup(source);
                string label = group switch { "direct" => "直连", "reject" => "拒绝", "proxy" => "代理", _ => "远程名单" };
                counts.TryGetValue(label, out int number); counts[label] = ++number;
                string title = label + " URL · " + number, url = S(source, "url"), key = "remote-url:" + ++sequence;
                yield return new(title, url + " · 正在检查可访问性和规则内容", "pending", Key: key);
                var result = await RemoteRuleValidation.CheckAsync(session.ProjectPath, source, http, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var issue = result.Success ? null : new ConfigurationIssue(group is null ? "yaml" : "policy", group is null ? "policy.yaml" : "RemoteUrls_" + group, result.Message);
                yield return new(title, url + " · " + result.Message, result.Success ? "pass" : "fail", issue, key);
            }
        }
    }
    static bool HttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !HasPlaceholder(JsonValue.Create(value));
    static ParameterCheck Evaluate(string title, string page, string field, Func<(string Message, bool Warning)> check)
    {
        try { var result = check(); return new(title, result.Message, result.Warning ? "warning" : "pass"); }
        catch (Exception ex) when (ex is BuildException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
        {
            string message = ex is BuildException ? ex.Message : "配置格式或本地文件无效，请检查此项";
            return new(title, message, "fail", new(page, field, message));
        }
    }
    public static void ValidateRules(JsonObject policy, ISet<string>? actions = null, ISet<string>? deferredPrefixes = null)
    {
        bool baseline = SubscriptionBaseline.Enabled(policy);
        if (baseline && !B(policy, "extra_rules_enabled", true)) return;
        ProjectOrdering.RuleOrder(policy); if (!baseline) GeoRules.Mode(policy);
        void Action(string action)
        {
            string resolved = action switch { "PROXY" => "默认代理", "AIRPORT" => "机场入口", _ => action };
            if (actions is not null && !actions.Contains(resolved) && !(deferredPrefixes?.Any(resolved.StartsWith) ?? false)) throw new BuildException("规则动作引用了不存在的代理或分组");
        }
        ImportedRuleSets.Validate(policy, Action);
        foreach (string group in new[] { "direct", "reject", "proxy" })
            foreach (var rule in L(policy, group)) Rules.ValidateSyntax(Rules.WithAction(rule, "DIRECT"));
        foreach (var raw in L(policy, "first").Concat(L(policy, "supplemental_rules")))
        {
            var entry = Map(raw, "前置或补充规则"); Action(S(entry, "action", "PROXY"));
            Rules.ValidateSyntax(Rules.WithAction(entry["match"], "DIRECT", inputFormat: "match"));
        }
        var names = new HashSet<string>();
        foreach (var remote in Rules.RemoteSpecs(policy["remote_rules"]))
        {
            if (!B(remote, "enabled", true)) continue;
            Rules.InputFormat(remote); B(remote, "no-resolve");
            if (!names.Add(Name(remote["name"], "远程名单"))) throw new BuildException("启用的远程名单名称重复");
            Action(S(remote, "action", "PROXY"));
            if (S(remote, "url") is { Length: > 0 } url && !HttpUrl(S(ExpandEnvironment(JsonValue.Create(url))))) throw new BuildException("远程名单需要完整的 HTTP(S) URL");
            if ((S(remote, "url").Length > 0) == (S(remote, "file").Length > 0)) throw new BuildException("远程名单必须且只能指定 URL 或文件之一");
            ConfigurationValidation.Headers(remote);
        }
        if (!baseline) { B(policy, "domestic_direct", true); Action(S(policy, "final", "PROXY")); }
    }
    public static IEnumerable<ParameterCheck> Inspect(WorkspaceSession session, CancellationToken cancellation = default, BuildOptions? build = null, bool checkDependencies = true)
    {
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new Dictionary<string, JsonNode?>();
        foreach (var (name, doc) in session.Documents)
        {
            cancellation.ThrowIfCancellationRequested();
            yield return Evaluate("配置文件 · " + name, "yaml", name, () => { var value = name == "settings.yaml" ? ConfigurationValidation.ExpandSettings(Map(doc.Parse(), name)) : name == "policy.yaml" ? SubscriptionBaseline.ExpandPolicy(Map(doc.Parse(), name)) : ExpandEnvironment(doc.Parse()); if (name == "residential.yaml") List(value is JsonObject map ? map["proxies"] : value, "住宅节点"); else Map(value, name); parsed[name] = value; return ("语法与有效配置的环境变量通过", false); });
        }
        JsonObject settings = parsed.GetValueOrDefault("settings.yaml") as JsonObject ?? new();
        JsonObject policy = parsed.GetValueOrDefault("policy.yaml") as JsonObject ?? new();
        bool subscriptionBaseline = S(policy, "rule_source", "custom") == "subscription";
        bool checkExtras = !subscriptionBaseline || B(policy, "extra_rules_enabled", true);
        JsonNode? residenceDoc = parsed.GetValueOrDefault("residential.yaml");
        var residences = residenceDoc is JsonArray a ? a : (residenceDoc as JsonObject)?["proxies"] as JsonArray ?? new();
        var sources = settings["sources"] as JsonArray ?? new();
        var actions = new HashSet<string>(Nodes.Reserved) { "默认代理", "住宅出口", "机场入口", "订阅优先", "机场手选" };
        if (subscriptionBaseline) { actions.Add("聚合组"); actions.Add("订阅跳板"); }
        yield return Evaluate("订阅配置", "sources", "AddSource", () =>
        {
            ConfigurationValidation.Settings(settings);
            int enabled = 0;
            foreach (var raw in L(settings, "sources"))
            {
                var source = Map(raw, "订阅"); if (!B(source, "enabled", true)) continue;
                enabled++; string name = S(source, "name");
                if (subscriptionBaseline) actions.Add(name);
                else { actions.Add("订阅·" + name); actions.Add("优先级·" + (sources.IndexOf(raw) + 1)); }
            }
            return ($"{enabled} 个已启用订阅；名称和健康检查参数通过", false);
        });
        for (int i = 0; i < sources.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); int index = i;
            if (sources[i] is not JsonObject source || source["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var enabled) && !enabled) continue;
            string title = "订阅 " + (i + 1);
            bool downloading = build is not null && ConfigurationValidation.Download(session, source, build);
            if (downloading || !File.Exists(session.SnapshotPath(S(source, "name")))) deferred.Add("[" + S(source, "name") + "] ");
            yield return Evaluate(title + " · 链接", "sources", "SourceUrl" + i, () =>
            {
                string url = S(source, "url");
                if (url.Length == 0)
                {
                    if (!File.Exists(session.SnapshotPath(S(source, "name")))) throw new BuildException("未填写链接，也没有导入节点缓存");
                    return ("使用导入的节点；未填写链接，不能在线更新", true);
                }
                if (!HttpUrl(url))
                {
                    if (build is not null && File.Exists(session.SnapshotPath(S(source, "name")))) return ("订阅链接无效，生成时可使用已有缓存；请修复链接后再拉取", true);
                    throw new BuildException("请填写完整的 HTTP(S) 订阅链接");
                }
                return ("链接格式通过（未请求订阅）", false);
            });
            yield return Evaluate(title + " · 缓存与筛选", "sources", "Sourceinclude" + i, () =>
            {
                if (downloading) return ("此来源将在生成时下载；下载后检查节点与筛选，失败保留旧数据", true);
                string path = session.SnapshotPath(S(source, "name")); var selection = M(settings, "selection");
                if (!File.Exists(path)) { if (build is not null) throw new BuildException("尚无节点缓存；离线或使用已有节点时不能生成，请先获取订阅或导入节点文件"); return ("尚无节点缓存；生成前需获取订阅或导入节点文件", true); }
                var document = Nodes.Document(File.ReadAllBytes(path));
                if (M(document, "proxy-providers").Count > 0) throw new BuildException("缓存仍引用外部节点来源，请更新订阅取得完整节点");
                var nodes = subscriptionBaseline ? L(document, "proxies") : Nodes.Namespace(L(document, "proxies"), S(source, "name"));
                ConfigurationValidation.CacheNodes(nodes);
                foreach (var raw in nodes) actions.Add(S(Map(raw, "节点"), "name"));
                if (subscriptionBaseline)
                {
                    foreach (var group in L(document, "proxy-groups")) actions.Add(S(Map(group, "策略组"), "name"));
                    foreach (var node in nodes) actions.Add($"[{S(source, "name")}] {S(Map(node, "节点"), "name")}");
                }
                int count = subscriptionBaseline ? SubscriptionBaseline.Eligible(nodes, source, selection).Count : Nodes.Eligible(nodes, source, selection).Count;
                bool changed = session.Subscriptions.UrlChanged(Map(L(session.Settings, "sources")[index], "订阅"));
                return ($"{nodes.Count} 个缓存节点，{count} 个符合筛选" + (changed ? "；链接已变更，缓存仍来自旧链接" : ""), changed);
            });
        }
        if (subscriptionBaseline) yield return Evaluate("订阅规则基线", "sources", "AddSource", () =>
        {
            var ordered = (JsonObject)settings.DeepClone(); ProjectOrdering.NumberSources(ordered);
            var primary = SubscriptionBaseline.Primary(ordered) ?? throw new BuildException("至少启用一个订阅");
            string name = S(primary, "name"), path = session.SnapshotPath(name);
            if (build is not null && ConfigurationValidation.Download(session, primary, build)) return ($"将拉取“{name}”的完整规则；其他订阅仅提供节点", false);
            if (!File.Exists(path)) return ($"规则来源：{name}；请先拉取完整订阅", true);
            var document = Nodes.Document(File.ReadAllBytes(path)); SubscriptionBaseline.RequireRules(document, name);
            return ($"{name} · {SubscriptionBaseline.RuleCount(document)} 条原始规则；其他订阅仅提供节点", false);
        });
        yield return Evaluate("住宅出口 · 名称", "residential", "AddResidence", () =>
        {
            if (residences.Count == 0) { if (subscriptionBaseline) return ("未添加住宅出口，保留订阅的代理路径", false); throw new BuildException("请添加住宅出口"); }
            var names = new HashSet<string>();
            foreach (var raw in residences)
            {
                string name = Name(Map(raw, "住宅")["name"], "住宅");
                if (!names.Add(name) || !subscriptionBaseline && !actions.Add(name)) throw new BuildException("住宅名称重复或与代理分组冲突");
                if (subscriptionBaseline)
                {
                    actions.Add(name);
                    actions.Add(SubscriptionBaseline.ChainedName(name)); actions.Add(SubscriptionBaseline.DirectName(name));
                    actions.Add("直连住宅 · " + name);
                }
            }
            return ($"{residences.Count} 个住宅出口，名称通过", false);
        });
        for (int i = 0; i < residences.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var raw = residences[i];
            yield return Evaluate("住宅 " + (i + 1) + " · 连接参数", "residential", "ResidenceServer" + i, () =>
            {
                var node = Map(raw, "住宅"); ProfileCompiler.ValidateResidences(A(new JsonNode?[] { node }));
                string server = S(node, "server");
                if (Uri.CheckHostName(server.Trim('[', ']')) == UriHostNameType.Unknown || server != server.Trim()) throw new BuildException("服务器应填写域名或 IP，不包含协议、路径或空格");
                string protocol = S(node, "type");
                return (protocol is "socks5" or "http" ? "服务器、端口与凭据格式通过；未测试连通性" : "基础字段通过；此协议的专用参数需在生成时由内核校验", protocol is not "socks5" and not "http");
            });
        }
        foreach (string group in checkExtras ? new[] { "direct", "reject", "proxy" } : [])
        {
            string title = group == "direct" ? "直连" : group == "reject" ? "拒绝" : "代理";
            string field = "Manual_" + group;
            var check = Evaluate(title + " · 手工名单", "policy", field, () =>
            {
                var rules = L(policy, group);
                foreach (var rule in rules)
                {
                    field = (ManualRuleEditor.Classify(rule, group == "direct") switch { ManualRuleKind.Suffix => "Suffix_", ManualRuleKind.Exact => "Exact_", _ => "Manual_" }) + group;
                    Rules.ValidateSyntax(Rules.WithAction(rule, "DIRECT"));
                }
                return ($"{rules.Count} 条匹配条件，格式通过", false);
            });
            yield return check.State == "fail" ? check with { Issue = new("policy", field, check.Message) } : check;
        }
        yield return Evaluate("分流规则 · 顺序与动作", "policy", "FinalAction", () =>
        {
            ValidateRules(policy, actions, deferred);
            return ("规则优先级、前置规则、默认动作和远程来源格式通过", false);
        });
        yield return Evaluate(RemoteContentTitle, "policy", "RemoteUrls_proxy", () =>
        {
            if (!checkExtras) return ("附加规则已停用，本次不读取 URL 名单", false);
            var sources = Rules.RemoteSpecs(policy["remote_rules"]).Where(x => B(x, "enabled", true)).ToArray();
            foreach (var source in sources)
                if (S(source, "file").Length > 0)
                {
                    using var fetch = new Fetcher(session.ProjectPath, M(settings, "http"), offline: true);
                    string file = fetch.Resolve(S(source, "file"));
                    foreach (var rule in Rules.ParseRemote(File.ReadAllBytes(file), source)) Rules.ValidateSyntax(S(rule));
                }
            int count = sources.Count(x => S(x, "url").Length > 0);
            return (count == 0 ? sources.Length == 0 ? "未配置远程名单" : $"{sources.Length} 个本地来源，内容检查通过" : $"{count} 个 URL 来源；在线内容检查将在参数检查或生成时执行", count > 0);
        });
        yield return Evaluate("DNS · 地址与出口", "policy", "Dns_nameserver", () =>
        {
            if (DnsPrivacy.Enabled(policy))
            {
                string exit = DnsPrivacy.Exit(policy);
                if (exit == "residential" && residences.Count == 0) throw new BuildException("DNS 隐私增强需要住宅节点，请添加住宅或改选机场入口");
                return ($"Fake-IP 隐私增强；代理 DNS 经{(exit == "residential" ? "住宅出口" : "机场入口")}；生成时检查实际组引用，MC2 需另行应用 DNS", false);
            }
            if (subscriptionBaseline) return ("DNS 由客户端 / MC2 管理；本次忽略项目中保留的旧 DNS 设置，不生成配套 DNS 文件", false);
            if (ConfigurationValidation.Target(settings) == "mc2" && !policy.ContainsKey("dns")) throw new BuildException("MC2 目标需要 DNS 配置；仅导出普通 Clash 时请选择 Clash 目标");
            int endpoints = ValidateDnsParameters(policy, actions, deferred);
            bool enabled = B(M(policy, "dns"), "enable");
            return (enabled ? $"{endpoints} 个解析地址，格式与代理分组引用通过" : "DNS 未启用，客户端需自行提供解析", !enabled);
        });
        if (checkDependencies) yield return Evaluate("本地校验资源", "settings", "", () =>
        {
            var effective = ConfigurationValidation.EffectiveSettings(settings, session.Workspace, build?.Validation);
            ConfigurationValidation.Resources(effective, session.ProjectPath, policy);
            bool required = B(M(effective, "validation"), "required");
            return (required ? "本地校验资源路径存在；生成时执行内核校验" : "内核校验未启用，生成时无法确认完整兼容性", !required);
        });
    }
    public static int ValidateDnsParameters(JsonObject policy, ISet<string> actions, ISet<string>? deferred = null)
    {
        bool follow = B(policy, "dns_follow_rules");
        var dns = M(policy, "dns");
        bool enabled = B(dns, "enable");
        if (enabled && follow && (L(dns, "direct-nameserver").Count == 0 || L(dns, "nameserver").Count + L(dns, "fallback").Count == 0)) throw new BuildException("dns_follow_rules 需要 direct-nameserver 和 nameserver/fallback");
        if (enabled && L(dns, "nameserver").Count == 0 && M(dns, "nameserver-policy").Count == 0) throw new BuildException("启用 DNS 时需要 nameserver 或 nameserver-policy");
        string listen = S(dns, "listen");
        string normalized = listen.StartsWith(':') ? "0.0.0.0" + listen : listen;
        if (listen.Length > 0 && (!IPEndPoint.TryParse(normalized, out var binding) || binding.Port is < 1 or > 65535)) throw new BuildException("DNS 监听地址应为 IP:端口，端口范围 1～65535");
        var endpoints = new List<JsonNode?>();
        foreach (string key in new[] { "default-nameserver", "proxy-server-nameserver", "nameserver", "direct-nameserver", "fallback" }) endpoints.AddRange(L(dns, key));
        foreach (string key in new[] { "nameserver-policy", "proxy-server-nameserver-policy" })
            foreach (var (_, value) in M(dns, key)) { if (IsString(value)) endpoints.Add(value); else endpoints.AddRange(List(value, key)); }
        foreach (var endpoint in endpoints) { if (!IsString(endpoint)) throw new BuildException("DNS 服务器地址必须是字符串"); ValidateDnsEndpoint(S(endpoint)); }
        foreach (string flag in new[] { "enable", "ipv6", "prefer-h3", "respect-rules", "direct-nameserver-follow-policy" }) B(dns, flag);
        ProfileCompiler.ValidateDns(O("dns", dns.DeepClone(), "proxy-groups", A(actions.Select(name => (JsonNode?)O("name", name)))), deferred);
        return endpoints.Count;
    }
    static void ValidateDnsEndpoint(string text)
    {
        string address = text.Split('#')[0];
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace) || HasPlaceholder(JsonValue.Create(address))) throw new BuildException("DNS 地址为空、包含空格或仍是占位符");
        if (address is "system" or "dhcp://system") return;
        if (IPAddress.TryParse(address, out _)) return;
        if (address.Contains("://"))
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && new[] { "https", "tls", "tcp", "udp", "quic", "h3", "dhcp" }.Contains(uri.Scheme) && uri.Host.Length > 0 && uri.Port != 0) return;
        }
        else if (Uri.TryCreate("udp://" + address, UriKind.Absolute, out var uri) && IPAddress.TryParse(uri.Host.Trim('[', ']'), out _) && uri.Port is > 0 and <= 65535 && uri.AbsolutePath is "" or "/") return;
        throw new BuildException("DNS 地址格式无效，请填写 IP 或支持的 DNS URL");
    }
}

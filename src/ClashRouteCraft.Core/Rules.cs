using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public record NetworkPrefix(byte[] Address, int Prefix)
{
    public bool IsV6 => Address.Length == 16;
    public string Bits => string.Concat(Address.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')))[..Prefix];
    public override string ToString() => new IPAddress(Address).ToString().ToLowerInvariant() + "/" + Prefix;
    public static bool TryParse(string input, out NetworkPrefix prefix)
    {
        prefix = null!; var parts = input.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily is not AddressFamily.InterNetwork and not AddressFamily.InterNetworkV6) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork && (parts[0].Split('.').Length != 4 || parts[0].Split('.').Any(x => x.Length > 1 && x[0] == '0'))) return false;
        var bytes = address.GetAddressBytes(); int length = bytes.Length * 8, bits = length;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > length)) return false;
        for (int bit = bits; bit < length; bit++) bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
        prefix = new NetworkPrefix(bytes, bits); return true;
    }
}

public static class Rules
{
    public const int Version = 3;
    static readonly HashSet<string> Types = ["DOMAIN", "DOMAIN-SUFFIX", "DOMAIN-KEYWORD", "DOMAIN-REGEX", "DOMAIN-WILDCARD", "GEOSITE", "GEOIP", "IP-CIDR", "IP-CIDR6", "IP-ASN", "IP-SUFFIX", "SRC-GEOIP", "SRC-IP-ASN", "SRC-IP-CIDR", "SRC-IP-SUFFIX", "SRC-PORT", "DST-PORT", "IN-PORT", "PROCESS-NAME", "PROCESS-PATH", "PROCESS-NAME-REGEX", "PROCESS-PATH-REGEX", "PROCESS-NAME-WILDCARD", "PROCESS-PATH-WILDCARD", "NETWORK", "DSCP", "IN-TYPE", "IN-USER", "IN-NAME", "REMATCH-NAME", "AND", "OR", "NOT", "UID"];
    static bool RegexType(ReadOnlySpan<char> value) => value.Trim().Equals("DOMAIN-REGEX", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("PROCESS-NAME-REGEX", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("PROCESS-PATH-REGEX", StringComparison.OrdinalIgnoreCase);
    public static List<string> Split(string text)
    {
        var parts = new List<string>(); int depth = 0, start = 0, tokenStart = 0, regexDepth = 0;
        bool regexPayload = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (regexPayload)
            {
                // Regex punctuation belongs to the payload, including inside logic rules.
                // Do not apply escape handling to ordinary Windows process paths.
                if (c == '\\' && i + 1 < text.Length)
                {
                    if (text[i + 1] == 'Q')
                    {
                        int end = text.IndexOf(@"\E", i + 2, StringComparison.Ordinal);
                        if (end < 0) throw new BuildException(@"正则表达式的 \Q 必须用 \E 闭合，避免规则动作进入表达式");
                        i = end + 1;
                    }
                    else i++;
                    continue;
                }
                if (c == '[') { i = RegexClassEnd(text, i); continue; }
                if (c == '{')
                {
                    int end = i + 1;
                    while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
                    if (end > i + 1)
                    {
                        if (end < text.Length && text[end] == ',') { end++; while (end < text.Length && char.IsAsciiDigit(text[end])) end++; }
                        if (end < text.Length && text[end] == '}') { i = end; continue; }
                    }
                }
                if (c == '(') { regexDepth++; continue; }
                if (c == ')' && regexDepth > 0) { regexDepth--; continue; }
                if (c == ',' && regexDepth == 0)
                {
                    // A logic child has no action; all its commas belong to the regex.
                    // At the top level retain boundaries only to detect ambiguous input.
                    if (depth == 0) { parts.Add(text[start..i].Trim()); start = i + 1; }
                    continue;
                }
                if (c != ')' || regexDepth > 0) continue;
                regexPayload = false;
            }
            if (c == '(') { depth++; tokenStart = i + 1; }
            else if (c == ')') { depth--; tokenStart = i + 1; }
            else if (c == ',')
            {
                regexPayload = RegexType(text.AsSpan(tokenStart, i - tokenStart)); regexDepth = 0; tokenStart = i + 1;
                if (depth == 0) { parts.Add(text[start..i].Trim()); start = i + 1; }
            }
            if (depth < 0) throw new BuildException("规则括号不匹配");
        }
        if (depth != 0) throw new BuildException("规则括号不匹配");
        parts.Add(text[start..].Trim()); return parts;
    }
    static int RegexClassEnd(string text, int start)
    {
        int i = start + 1;
        if (i < text.Length && text[i] == '^') i++;
        if (i < text.Length && text[i] == ']') i++; // A leading ] is a class member.
        for (; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) { i++; continue; }
            if (text[i] == '[' && i + 1 < text.Length && text[i + 1] == ':')
            {
                int end = text.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (end >= 0) { i = end + 1; continue; }
            }
            if (text[i] == ']') return i;
        }
        throw new BuildException("正则表达式字符组未闭合");
    }
    // Full regex rules have no flags: Mihomo takes the last comma as the action boundary.
    public static List<string> SplitFull(string text)
    {
        int first = text.IndexOf(',');
        if (first >= 0 && RegexType(text.AsSpan(0, first)))
        {
            int last = text.LastIndexOf(',');
            if (last == first || last == text.Length - 1) throw new BuildException("规则缺少目标动作");
            return [text[..first].Trim().ToUpperInvariant(), text[(first + 1)..last].Trim(), text[(last + 1)..].Trim()];
        }
        return Split(text);
    }
    public static string InputFormat(JsonObject spec, string fallback = "auto")
    {
        string format = S(spec, "rule_format", fallback);
        if (format is not "auto" and not "match" and not "rule") throw new BuildException("rule_format 只支持 auto、match 或 rule");
        return format;
    }
    public static string WithAction(JsonNode? value, string action, bool bareSuffix = true, string inputFormat = "auto")
    {
        if (value is JsonObject explicitRule)
        {
            if (explicitRule.Count != 1 || !explicitRule.ContainsKey("match") && !explicitRule.ContainsKey("rule"))
                throw new BuildException("规则对象应只包含 match（匹配条件）或 rule（带动作的完整规则）");
            inputFormat = explicitRule.ContainsKey("match") ? "match" : "rule";
            value = explicitRule[inputFormat];
        }
        if (!IsString(value) || string.IsNullOrWhiteSpace(S(value)) || S(value).IndexOfAny(['\r', '\n']) >= 0) throw new BuildException("规则必须是非空单行字符串");
        string text = S(value).Trim();
        if (inputFormat is not "auto" and not "match" and not "rule") throw new BuildException("规则输入格式无效");
        if (!text.Contains(','))
        {
            if (NetworkPrefix.TryParse(text, out var network)) return $"{(network.IsV6 ? "IP-CIDR6" : "IP-CIDR")},{network},{action},no-resolve";
            string kind = bareSuffix || text.StartsWith("+.") || text.StartsWith('.') ? "DOMAIN-SUFFIX" : "DOMAIN";
            if (text.StartsWith("*.")) kind = "DOMAIN-WILDCARD";
            string domain = text.StartsWith("+.") ? text[2..] : text.TrimStart('.');
            if (domain.Length == 0 || Regex.IsMatch(domain, @"[\s/:]")) throw new BuildException("域名规则格式不正确");
            return $"{kind},{domain},{action}";
        }
        int first = text.IndexOf(','); string type = text[..first].Trim().ToUpperInvariant();
        if (!Types.Contains(type)) throw new BuildException("不支持的规则类型；名单不接受 MATCH/FINAL/RULE-SET");
        if (RegexType(type))
        {
            if (inputFormat == "auto" && Split(text).Count > 2)
                throw new BuildException("正则含顶层逗号，无法区分表达式与动作；请使用 {match: '匹配条件'} 或 {rule: '完整规则'}，远程名单可指定 rule_format");
            string payload = inputFormat == "rule" ? SplitFull(text)[1] : text[(first + 1)..].Trim();
            if (payload.Length == 0) throw new BuildException("规则缺少匹配条件");
            return string.Join(',', type, payload, action);
        }
        var parts = Split(text);
        if (parts.Count < 2 || parts[1].Length == 0) throw new BuildException("规则缺少匹配条件");
        var flags = parts.Skip(2).Where(x => x is "no-resolve" or "src").ToArray();
        if (parts.Skip(2).Count(x => x is not "no-resolve" and not "src") > 1) throw new BuildException("规则字段过多，请检查逗号或条件格式");
        return string.Join(',', new[] { type, parts[1], action }.Concat(flags));
    }
    static string DomainProviderRule(string text)
    {
        string[] labels = text.ToLowerInvariant().Split('.');
        bool suffix = labels[0] == "+", subdomains = labels[0].Length == 0;
        var body = labels.Skip(suffix || subdomains ? 1 : 0).ToArray();
        if (body.Length == 0 || body.Any(x => x.Length == 0 || x != "*" && (x.IndexOfAny(['*', '+', '?', ',', '/', ':']) >= 0 || x.Any(char.IsWhiteSpace)))
            || Uri.CheckHostName(string.Join('.', body.Select(x => x == "*" ? "wildcard" : x))) != UriHostNameType.Dns)
            throw new BuildException("domain 名单格式无效；* 必须独占一个域名层级，+ 或空前缀只能位于开头");
        if (!body.Contains("*") && !subdomains) return $"{(suffix ? "DOMAIN-SUFFIX" : "DOMAIN")},{string.Join('.', body)},DIRECT";
        static string Escape(string value) => string.Concat(value.Select(c => @"\.+*?()|[]{}^$".Contains(c) ? "\\" + c : c.ToString()));
        string prefix = suffix ? @"(?:[^.]+\.)*" : subdomains ? @"(?:[^.]+\.)+" : "";
        string pattern = "(?i)^" + prefix + string.Join(@"\.", body.Select(x => x == "*" ? "[^.]+" : Escape(x))) + "$";
        return "DOMAIN-REGEX," + pattern + ",DIRECT";
    }
    public static JsonArray ParseRemote(byte[] raw, JsonObject spec)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(raw).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new BuildException("远程规则不是 UTF-8 文本"); }
        string format = S(spec, "format", "auto");
        if (format == "auto")
        {
            string significant = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !Comment(x)) ?? "";
            if (significant.StartsWith('<')) throw new BuildException("远程规则返回了 HTML/XML");
            format = Regex.IsMatch(significant, @"^(payload\s*:|rules\s*:|---(?:\s|$)|[\[{]|-\s)") ? "yaml" : "text";
        }
        JsonArray lines; string inputFormat = InputFormat(spec);
        if (format == "yaml")
        {
            var document = YamlData.Parse(text);
            lines = List(document is JsonObject map ? map["payload"] ?? map["rules"] : document, "远程规则 payload");
            if (!spec.ContainsKey("rule_format")) inputFormat = document is JsonObject doc && !doc.ContainsKey("payload") && doc.ContainsKey("rules") ? "rule" : "match";
        }
        else if (format == "text") lines = A(text.Split('\n'));
        else throw new BuildException("电脑端内嵌规则支持 yaml/text，不直接解码 mrs");
        string behavior = S(spec, "behavior", "auto");
        if (behavior is not "auto" and not "classical" and not "domain" and not "ipcidr") throw new BuildException("远程规则 behavior 只支持 auto、classical、domain 或 ipcidr");
        var entries = new List<string>();
        foreach (var rawLine in lines)
        {
            if (!IsString(rawLine)) throw new BuildException("远程规则包含非字符串项");
            string line = S(rawLine).Trim();
            if (line.Length > 0 && !Comment(line)) entries.Add(line);
        }
        if (entries.Count == 0) throw new BuildException("远程规则集为空");
        // Inspect the complete list, not the URL or just its first entry. Explicit
        // behavior wins; typed rules and IP/mixed lists keep classical handling.
        if (behavior == "auto") behavior = entries.All(line => !line.Contains(',') && !NetworkPrefix.TryParse(line, out _)) ? "domain" : "classical";
        var result = new JsonArray();
        foreach (string line in entries)
        {
            if (behavior == "ipcidr" && !NetworkPrefix.TryParse(line, out _)) throw new BuildException("ipcidr 规则包含非 IP 网段");
            if (behavior == "domain" && line.Contains(',')) throw new BuildException("domain 规则应只包含域名");
            string rule;
            if (behavior == "domain") rule = DomainProviderRule(line);
            else if (behavior == "ipcidr")
            {
                NetworkPrefix.TryParse(line, out var network);
                rule = $"{(network.IsV6 ? "IP-CIDR6" : "IP-CIDR")},{network},DIRECT" + (B(spec, "no-resolve") ? ",no-resolve" : "");
            }
            else rule = WithAction(JsonValue.Create(line), "DIRECT", inputFormat: inputFormat);
            ValidateSyntax(rule); result.Push(JsonValue.Create(rule));
        }
        return result;
    }
    static bool Comment(string text) => text.StartsWith('#') || text.StartsWith("//") || text.StartsWith(';');
    public static IEnumerable<JsonObject> RemoteSpecs(JsonNode? entries)
    {
        if (entries is null) yield break;
        var aliases = new Dictionary<string, string> { ["direct"] = "DIRECT", ["reject"] = "REJECT", ["proxy"] = "PROXY" };
        int number = 0;
        foreach (var raw in List(entries, "remote_rules（使用带 - 的 YAML 列表）"))
        {
            number++; var entry = Map(raw, "remote rule"); var keys = entry.Select(x => x.Key).Where(aliases.ContainsKey).ToList();
            if (keys.Count > 0)
            {
                if (entry.Count != 1) throw new BuildException("每个远程规则列表项只能有一个 direct/reject/proxy 动作");
                string key = keys[0]; var urls = entry[key] is JsonArray list ? list : new JsonArray(entry[key]?.DeepClone()); int offset = 0;
                foreach (var url in urls)
                {
                    offset++;
                    if (!IsString(url) || !Uri.TryCreate(S(url), UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https") throw new BuildException($"remote_rules 第 {number} 项应填写 HTTP(S) URL");
                    yield return O("name", $"remote[{number}].{key}[{offset}]", "url", url, "action", aliases[key], "enabled", true);
                }
            }
            else
            {
                if (entry.ContainsKey("priority")) throw new BuildException("远程规则不使用 priority，请调整上下顺序");
                yield return Merge(O("name", $"remote[{number}]"), entry);
            }
        }
    }
    public record CanonicalRule(string Text, string Key, string Action, string Kind, string Value, string[] Flags);
    public static void ValidateSyntax(string rule) => ValidateSyntax(rule, 0);
    static void ValidateSyntax(string rule, int depth)
    {
        if (depth > 32) throw new BuildException("逻辑规则嵌套过深");
        var parsed = Canonicalize(rule);
        if (parsed.Kind is "DOMAIN" or "DOMAIN-SUFFIX")
        {
            if (parsed.Value.IndexOfAny(['*', '/', ':', ',']) >= 0 || Uri.CheckHostName(parsed.Value) != UriHostNameType.Dns)
                throw new BuildException("域名格式无效，请只填域名，不带协议、路径或通配符");
        }
        // Regex payloads use the target core's dialect, not .NET's regex grammar.
        // Their compilation is intentionally left to the complete core check.
        if (parsed.Kind is "AND" or "OR" or "NOT")
        {
            string value = parsed.Value;
            if (value.Length < 4 || value[0] != '(' || value[^1] != ')') throw new BuildException("逻辑规则需要括号包裹的匹配条件");
            var children = Split(value[1..^1]);
            if (parsed.Kind == "NOT" && children.Count != 1) throw new BuildException("NOT 规则只能有一个条件");
            foreach (string child in children)
            {
                if (child.Length < 3 || child[0] != '(' || child[^1] != ')') throw new BuildException("逻辑子条件需要括号");
                ValidateSyntax(WithAction(JsonValue.Create(child[1..^1]), "DIRECT", inputFormat: "match"), depth + 1);
            }
        }
    }
    public static CanonicalRule Canonicalize(string rule)
    {
        var parts = SplitFull(rule);
        if (parts[0] == "MATCH") return new(rule, "MATCH", parts[1], "MATCH", "", []);
        if (parts.Count < 3) throw new BuildException("规则缺少目标动作");
        string kind = parts[0], value = parts[1], action = parts[2];
        if (kind is "DOMAIN" or "DOMAIN-SUFFIX") value = value.ToLowerInvariant();
        else if (kind is "IP-CIDR" or "IP-CIDR6" or "SRC-IP-CIDR")
        {
            if (!NetworkPrefix.TryParse(value, out var network)) throw new BuildException("IP 网段规则格式不正确");
            value = network.ToString(); if (kind != "SRC-IP-CIDR") kind = network.IsV6 ? "IP-CIDR6" : "IP-CIDR";
        }
        var flags = parts.Skip(3).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new(string.Join(',', new[] { kind, value, action }.Concat(flags)), string.Join('\u001f', new[] { kind, value }.Concat(flags)), action, kind, value, flags);
    }
    public static (JsonArray Rules, JsonObject Report) MergeRules(JsonArray candidates)
    {
        var report = O("precedence", "first_listed", "input_count", candidates.Count, "duplicates_removed", 0, "duplicates_preserved", 0, "conflicts_resolved", 0, "conflicts_preserved", 0, "overlap_notices", 0,
            "duplicates", new JsonArray(), "preserved_duplicates", new JsonArray(), "conflicts", new JsonArray(), "overlaps", new JsonArray(), "detail_limit", 100,
            "overlap_detection", "DOMAIN/DOMAIN-SUFFIX and CIDR with identical flags; other expressions are not expanded");
        var seen = new Dictionary<string, Dictionary<string, JsonObject>>(); var output = new JsonArray(); var index = new OverlapIndex(); int position = 0;
        static JsonObject Brief(JsonObject item) => O("source", item["source"], "stage", item["stage"], "rule", item["rule"], "position", item["position"]);
        void Note(string kind, string counter, JsonObject earlier, JsonObject later, string resolution)
        {
            report[counter] = I(report, counter, 0) + 1;
            if (L(report, kind).Count < 100) L(report, kind).Push(O("earlier", Brief(earlier), "later", Brief(later), "resolution", resolution));
        }
        foreach (var raw in candidates)
        {
            position++; var original = Map(raw, "候选规则"); var canonical = Canonicalize(S(original, "rule"));
            var entry = Merge(original, O("rule", canonical.Text, "action", canonical.Action, "position", position));
            if (!seen.TryGetValue(canonical.Key, out var actions)) seen[canonical.Key] = actions = new(StringComparer.Ordinal);
            if (actions.TryGetValue(canonical.Action, out var previous))
            {
                // DNS/process state and proxy-group selections can change during matching.
                // Only fixed domain predicates with fixed built-in actions are proven redundant.
                bool stable = canonical.Kind is "DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD" or "DOMAIN-REGEX" or "DOMAIN-WILDCARD" or "GEOSITE"
                    && canonical.Action is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS";
                if (stable)
                {
                    Note("duplicates", "duplicates_removed", previous, entry, "same_condition_and_action; keep_earlier");
                    continue;
                }
                Note("preserved_duplicates", "duplicates_preserved", previous, entry, "matching_or_action_may_change; keep_both");
            }
            // PASS and UDP-incompatible outbounds continue matching in the core.
            // A different action is therefore not a redundant condition.
            else if (actions.Count > 0) Note("conflicts", "conflicts_preserved", actions.Values.First(), entry, "same_condition; keep_both; core_decides");
            foreach (var other in index.Overlaps(canonical))
                if (S(other, "action") != canonical.Action && Canonicalize(S(other, "rule")).Key != canonical.Key)
                    Note("overlaps", "overlap_notices", other, entry, "keep_both; earlier_rule_first_on_overlap");
            index.Add(canonical, entry); actions[canonical.Action] = entry; output.Push(JsonValue.Create(canonical.Text));
        }
        report["output_count"] = output.Count;
        report["details_truncated"] = I(report, "duplicates_removed", 0) > L(report, "duplicates").Count || I(report, "duplicates_preserved", 0) > L(report, "preserved_duplicates").Count || I(report, "conflicts_preserved", 0) > L(report, "conflicts").Count || I(report, "overlap_notices", 0) > L(report, "overlaps").Count;
        return (output, report);
    }
    sealed class OverlapIndex
    {
        sealed class Node
        {
            public Dictionary<string, Node> Children { get; } = [];
            public Dictionary<string, JsonObject> Cover { get; } = [];
            public Dictionary<string, JsonObject> Exact { get; } = [];
            public Dictionary<string, JsonObject> Subtree { get; } = [];
        }
        readonly Dictionary<string, Node> roots = [];
        static (string Family, string[] Path, bool Broad)? Descriptor(CanonicalRule rule)
        {
            string flags = string.Join(',', rule.Flags);
            if (rule.Kind is "DOMAIN" or "DOMAIN-SUFFIX") return ("domain:" + flags, rule.Value.Split('.').Reverse().ToArray(), rule.Kind == "DOMAIN-SUFFIX");
            if ((rule.Kind is "IP-CIDR" or "IP-CIDR6" or "SRC-IP-CIDR") && NetworkPrefix.TryParse(rule.Value, out var net)) return ($"ip:{net.Address.Length}:{rule.Kind == "SRC-IP-CIDR"}:{flags}", net.Bits.Select(c => c.ToString()).ToArray(), true);
            return null;
        }
        static void Collect(Dictionary<string, JsonObject> found, Dictionary<string, JsonObject> entries)
        {
            foreach (var (action, entry) in entries) if (!found.TryGetValue(action, out var old) || I(entry, "position", 0) < I(old, "position", 0)) found[action] = entry;
        }
        public IEnumerable<JsonObject> Overlaps(CanonicalRule rule)
        {
            var descriptor = Descriptor(rule); if (descriptor is null || !roots.TryGetValue(descriptor.Value.Family, out var node)) return [];
            var found = new Dictionary<string, JsonObject>(); Collect(found, node.Cover);
            foreach (string part in descriptor.Value.Path)
            {
                if (!node.Children.TryGetValue(part, out node)) return found.Values;
                Collect(found, node.Cover);
            }
            Collect(found, descriptor.Value.Broad ? node.Subtree : node.Exact); return found.Values;
        }
        public void Add(CanonicalRule rule, JsonObject entry)
        {
            var descriptor = Descriptor(rule); if (descriptor is null) return;
            var (family, path, broad) = descriptor.Value;
            if (!roots.TryGetValue(family, out var node)) roots[family] = node = new();
            node.Subtree.TryAdd(rule.Action, entry);
            foreach (string part in path) { if (!node.Children.TryGetValue(part, out var child)) node.Children[part] = child = new(); node = child; node.Subtree.TryAdd(rule.Action, entry); }
            (broad ? node.Cover : node.Exact).TryAdd(rule.Action, entry);
        }
    }
}

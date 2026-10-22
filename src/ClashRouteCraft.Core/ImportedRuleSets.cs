using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Portable, ordered rule snapshots. Source configuration and credentials are never retained.</summary>
public static class ImportedRuleSets
{
    public static readonly string[] PortableActions = ["DIRECT", "REJECT", "REJECT-DROP", "PASS", "PROXY", "AIRPORT", "住宅出口"];
    public static JsonObject Migrate(JsonObject original)
    {
        var policy = (JsonObject)original.DeepClone();
        var legacy = L(policy, "supplemental_rules");
        if (legacy.Count > 0)
        {
            var entries = new JsonArray(); var mapping = new JsonObject();
            foreach (var raw in legacy)
            {
                var entry = Map(raw, "补充规则"); string action = S(entry, "action", "PROXY");
                entries.Push(O("match", entry["match"], "source_action", action)); mapping[action] = action;
            }
            var sets = L(policy, "rule_sets");
            sets.Push(O("id", "legacy-" + Hash(Canonical(legacy))[..12], "name", "迁移的补充规则", "enabled", true,
                "source", O("kind", "migration", "label", "旧版补充规则"), "include_fallback", true, "mappings", mapping, "rules", entries));
            if (!policy.ContainsKey("rule_sets")) policy["rule_sets"] = sets;
        }
        policy.Remove("supplemental_rules"); return policy;
    }
    public static JsonArray Sets(JsonObject policy) => L(policy.ContainsKey("supplemental_rules") ? Migrate(policy) : policy, "rule_sets");
    public static bool IsFallback(JsonObject entry)
    {
        string match = S(entry, "match"); var p = Rules.Split(match);
        return match == "MATCH" || p.Count >= 2 && (p[0] == "GEOIP" && p[1].Equals("CN", StringComparison.OrdinalIgnoreCase)
            || p[0] == "GEOSITE" && (p[1].Equals("CN", StringComparison.OrdinalIgnoreCase) || p[1] == "geolocation-!cn"));
    }
    public static bool Included(JsonObject set, JsonObject entry) => !IsFallback(entry) || B(set, "include_fallback");
    public static string Target(JsonObject set, JsonObject entry) => S(M(set, "mappings"), S(entry, "source_action"));
    public static string Rule(JsonObject entry, string action) => S(entry, "match") == "MATCH" ? "MATCH," + action : Rules.WithAction(entry["match"], action, inputFormat: "match");
    public static void Validate(JsonObject policy, Action<string> validateAction)
    {
        var ids = new HashSet<string>();
        foreach (var raw in Sets(policy))
        {
            var set = Map(raw, "规则集"); Name(set["name"], "规则集");
            string id = Name(set["id"], "规则集 ID"); if (!ids.Add(id)) throw new BuildException("规则集 ID 重复");
            var entries = L(set, "rules"); if (entries.Count == 0) throw new BuildException("规则集不能为空");
            if (!B(set, "enabled", true)) continue;
            foreach (var rawEntry in entries)
            {
                var entry = Map(rawEntry, "导入规则"); if (!Included(set, entry)) continue;
                string action = Target(set, entry);
                if (string.IsNullOrWhiteSpace(action)) throw new BuildException($"规则集 {S(set, "name")} 尚有未映射策略：{S(entry, "source_action")}");
                validateAction(action);
                if (S(entry, "match") != "MATCH") Rules.ValidateSyntax(Rule(entry, "DIRECT"));
            }
        }
    }
    public static JsonObject Parse(byte[] bytes, string name, string kind = "paste", string label = "粘贴内容", string mode = "auto")
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new BuildException("规则文件不能超过 16 MiB");
        Name(JsonValue.Create(name.Trim()), "规则集");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new BuildException("请导入 UTF-8 文本规则文件"); }
        JsonArray? lines = null; bool full = mode == "rule";
        string first = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !x.StartsWith('#')) ?? "";
        bool yaml = System.Text.RegularExpressions.Regex.IsMatch(first, @"^(---(?:\s|$)|[\[{]|-\s|[A-Za-z0-9_-]+\s*:)");
        var doc = yaml ? YamlData.Parse(text) : null;
        if (doc is JsonObject map)
        {
            if (map["rules"] is JsonArray rules) { lines = rules; full = true; }
            else if (map["payload"] is JsonArray payload) lines = payload;
            else throw new BuildException("配置中没有 rules 或 payload；只提取规则，不导入节点或 DNS");
        }
        else if (doc is JsonArray array) lines = array;
        else lines = A(text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#') && !x.StartsWith(';') && !x.StartsWith("//")));
        if (mode == "match") full = false;
        var entries = new JsonArray(); var mappings = new JsonObject(); int index = 0;
        foreach (var raw in lines)
        {
            index++; if (raw is null) continue;
            if (!IsString(raw)) throw new BuildException($"第 {index} 条规则不是文本");
            string line = S(raw).Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            try
            {
                var parts = Rules.Split(line); string type = parts[0].ToUpperInvariant();
                bool isFull = full || mode == "auto" && (type is "MATCH" or "FINAL" || parts.Count >= 3 && parts[2] is not "no-resolve" and not "src");
                string action = "未指定策略", match;
                if (type is "MATCH" or "FINAL")
                {
                    if (parts.Count != 2 || string.IsNullOrWhiteSpace(parts[1])) throw new BuildException("最终兜底必须包含动作");
                    action = parts[1]; match = "MATCH";
                }
                else if (isFull)
                {
                    var p = Rules.SplitFull(line); if (p.Count < 3 || p[2] is "no-resolve" or "src") throw new BuildException("完整规则缺少动作");
                    action = p[2]; var canonical = Rules.SplitFull(Rules.WithAction(JsonValue.Create(line), "DIRECT", inputFormat: "rule"));
                    match = string.Join(',', canonical.Take(2).Concat(canonical.Skip(3)));
                }
                else
                {
                    // Reuse domain-list parsing to preserve *. / +. semantics.
                    var parsed = Rules.ParseRemote(Encoding.UTF8.GetBytes(line), O("format", "text", "rule_format", "match"));
                    var p = Rules.SplitFull(S(parsed[0])); match = string.Join(',', p.Take(2).Concat(p.Skip(3)));
                }
                if (match != "MATCH") Rules.ValidateSyntax(Rules.WithAction(JsonValue.Create(match), "DIRECT", inputFormat: "match"));
                entries.Push(O("match", match, "source_action", action));
                if (!mappings.ContainsKey(action)) mappings[action] = Nodes.Reserved.Contains(action) ? action : "";
            }
            catch (BuildException ex) { throw new BuildException($"第 {index} 条规则：{ex.Message}；RULE-SET 等外部引用请先导出为展开的规则文本"); }
        }
        if (entries.Count == 0) throw new BuildException("没有可导入的规则");
        return O("id", Guid.NewGuid().ToString("N"), "name", name.Trim(), "enabled", true,
            "source", O("kind", kind, "label", label, "sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))),
            "imported_at", DateTimeOffset.UtcNow.ToString("O"), "include_fallback", false, "mappings", mappings, "rules", entries);
    }
    public static string Diff(JsonObject old, JsonObject updated)
    {
        var a = L(old, "rules").Select(Canonical).ToList(); var b = L(updated, "rules").Select(Canonical).ToList();
        var ac = a.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count()); var bc = b.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        int added = bc.Sum(x => Math.Max(0, x.Value - ac.GetValueOrDefault(x.Key))), removed = ac.Sum(x => Math.Max(0, x.Value - bc.GetValueOrDefault(x.Key)));
        return $"新增 {added} 条，删除 {removed} 条；顺序{(a.SequenceEqual(b) ? "未变" : "或内容已变化")}。动作变化计入新增与删除。";
    }
}

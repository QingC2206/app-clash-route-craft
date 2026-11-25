using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Ordered top-priority custom rules (Layer 1, matching persona-route's priority-rules.yaml).</summary>
public sealed class PriorityRuleEditor
{
    readonly List<JsonNode?> entries;

    public PriorityRuleEditor(JsonArray rules)
    {
        entries = rules.Select(x => x?.DeepClone()).ToList();
    }

    public JsonArray Rules => A(entries);
    public int Count => entries.Count;
    public string Text => string.Join('\n', entries.Select(Display));

    public void Replace(string text)
    {
        var existing = entries.Select(x => (Raw: x, Display: Display(x))).ToList();
        var updated = new List<JsonNode?>();
        foreach (string line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            int match = existing.FindIndex(x => x.Display == line);
            if (match >= 0)
            {
                updated.Add(existing[match].Raw);
                existing.RemoveAt(match);
                continue;
            }
            if (line.StartsWith('{'))
            {
                try
                {
                    if (YamlData.Parse(line) is JsonObject parsed) { updated.Add(parsed); continue; }
                }
                catch (BuildException) { }
                updated.Add(JsonValue.Create(line));
                continue;
            }
            if (TryParseRuleLine(line, out string parsedMatch, out string parsedAction))
                updated.Add(O("match", parsedMatch, "action", parsedAction));
            else
                updated.Add(JsonValue.Create(line));
        }
        entries.Clear();
        entries.AddRange(updated);
    }

    public static (JsonNode? Match, string Action) ParseEntry(JsonNode? raw)
    {
        if (raw is JsonObject map)
        {
            if (!map.ContainsKey("match")) throw new BuildException("优先自定义规则缺少 match 字段");
            string action = map.ContainsKey("action") ? S(map, "action") : "PROXY";
            if (string.IsNullOrWhiteSpace(action)) throw new BuildException("优先自定义规则缺少动作（如 DIRECT / PROXY / REJECT / PASS）");
            return (map["match"], action.Trim());
        }
        if (IsString(raw))
        {
            string line = S(raw).Trim();
            if (line.Length == 0 || line.IndexOfAny(['\r', '\n']) >= 0) throw new BuildException("优先自定义规则必须是非空单行规则");
            var (match, action) = ParseRuleLine(line);
            return (JsonValue.Create(match), action);
        }
        throw new BuildException("优先自定义规则应为完整规则字符串或 {match, action} 对象");
    }

    public static (string Match, string Action) ParseRuleLine(string line)
    {
        int firstComma = line.IndexOf(',');
        if (firstComma < 0) throw new BuildException("优先自定义规则须包含类型、匹配条件与动作，例如 DOMAIN-SUFFIX,example.com,DIRECT");
        string kind = line[..firstComma].Trim().ToUpperInvariant();
        if (kind is "MATCH" or "FINAL") throw new BuildException("优先自定义规则不能包含 MATCH / FINAL，请将兜底留在最后一层");
        var parts = ClashRouteCraft.Core.Rules.SplitFull(line);
        if (parts.Count < 3 || string.IsNullOrWhiteSpace(parts[2]) || parts[2] is "no-resolve" or "src")
            throw new BuildException("优先自定义规则须包含动作（如 DIRECT / PROXY / REJECT / PASS），例如 DOMAIN-SUFFIX,example.com,DIRECT");
        string action = parts[2].Trim();
        var flags = parts.Skip(3).ToArray();
        if (flags.Any(x => x is not "no-resolve" and not "src"))
            throw new BuildException("优先自定义规则字段过多或标志无效，仅支持 no-resolve / src");
        string match = string.Join(',', new[] { parts[0].ToUpperInvariant(), parts[1] }.Concat(flags));
        return (match, action);
    }

    static bool TryParseRuleLine(string line, out string match, out string action)
    {
        try
        {
            (match, action) = ParseRuleLine(line);
            // Verify syntax except domain audit (invalid domains should still parse into match+action so audit can report them cleanly).
            ClashRouteCraft.Core.Rules.WithAction(JsonValue.Create(match), "DIRECT", inputFormat: "match");
            return true;
        }
        catch (InvalidDomainRuleException)
        {
            // Still split structurally valid rules with bad domain characters so audit warnings point to the domain.
            try { (match, action) = ParseRuleLine(line); return true; }
            catch (BuildException) { match = ""; action = ""; return false; }
        }
        catch (BuildException) { match = ""; action = ""; return false; }
    }

    public static string Display(JsonNode? raw)
    {
        if (IsString(raw)) return S(raw).Trim();
        if (raw is JsonObject map)
        {
            string action = map.ContainsKey("action") ? S(map, "action").Trim() : "PROXY";
            if (action.Length > 0 && !action.Contains(',') && map.ContainsKey("match") && IsString(map["match"]))
            {
                try
                {
                    string rendered = ClashRouteCraft.Core.Rules.WithAction(map["match"], action, inputFormat: "match");
                    var (roundMatch, roundAction) = ParseRuleLine(rendered);
                    if (roundAction == action && roundMatch.Length > 0) return rendered;
                }
                catch (BuildException) { }
            }
            return YamlData.Write(map).Trim();
        }
        return Canonical(raw);
    }
}

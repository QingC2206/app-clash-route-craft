using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public enum ManualRuleKind { Suffix, Exact, Other, Lan }

/// <summary>A view over an existing ordered list. Editing one field preserves the other slots.</summary>
public sealed class ManualRuleEditor
{
    sealed record Entry(JsonNode? Raw, ManualRuleKind Kind);
    readonly List<Entry> entries;
    readonly bool direct;
    public ManualRuleEditor(JsonArray rules, bool direct)
    {
        this.direct = direct;
        entries = rules.Select(raw => new Entry(raw?.DeepClone(), Classify(raw, direct))).ToList();
    }
    public JsonArray Rules => A(entries.Select(x => x.Raw));
    public bool LanEnabled => direct && entries.Any(x => IsLan(x.Raw));
    public string Text(ManualRuleKind kind) => string.Join('\n', entries.Where(x => x.Kind == kind).Select(x => Display(x.Raw, kind)));
    public int Count(ManualRuleKind kind) => entries.Count(x => x.Kind == kind);
    public void Replace(ManualRuleKind kind, string text)
    {
        var existing = entries.Where(x => x.Kind == kind).ToList();
        var replacements = new List<Entry>();
        foreach (string line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            string display = StripPrefix(line, kind);
            int match = existing.FindIndex(x => Display(x.Raw, kind) == display);
            if (match >= 0) { replacements.Add(existing[match]); existing.RemoveAt(match); }
            else
            {
                string raw = kind switch { ManualRuleKind.Suffix => "DOMAIN-SUFFIX," + display, ManualRuleKind.Exact => "DOMAIN," + display, _ => line };
                JsonNode? value = JsonValue.Create(raw);
                if (kind == ManualRuleKind.Other && raw.StartsWith('{'))
                    try { if (YamlData.Parse(raw) is JsonObject parsed) value = parsed; }
                    catch (BuildException) { } // Keep an incomplete edit as text; normal validation displays its error.
                replacements.Add(new(value, kind));
            }
        }
        int last = entries.FindLastIndex(x => x.Kind == kind), next = 0;
        var updated = new List<Entry>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind != kind) updated.Add(entries[i]);
            else if (next < replacements.Count) updated.Add(replacements[next++]);
            if (i == last) while (next < replacements.Count) updated.Add(replacements[next++]);
        }
        if (last < 0) updated.AddRange(replacements);
        entries.Clear(); entries.AddRange(updated);
    }
    public void SetLan(bool enabled)
    {
        if (!direct) throw new BuildException("局域网直连选项只属于直连组");
        if (!enabled) entries.RemoveAll(x => IsLan(x.Raw));
        else if (!LanEnabled) entries.Insert(0, new(JsonValue.Create("GEOIP,LAN,no-resolve"), ManualRuleKind.Lan));
    }
    public static ManualRuleKind Classify(JsonNode? raw, bool direct)
    {
        if (!IsString(raw)) return ManualRuleKind.Other;
        if (direct && IsLan(raw)) return ManualRuleKind.Lan;
        try
        {
            string text = S(raw).Trim(); var parts = ClashRouteCraft.Core.Rules.Split(text);
            if (parts.Count == 2 && parts[0].Equals("DOMAIN", StringComparison.OrdinalIgnoreCase)) return ManualRuleKind.Exact;
            if (parts.Count == 2 && parts[0].Equals("DOMAIN-SUFFIX", StringComparison.OrdinalIgnoreCase)) return ManualRuleKind.Suffix;
            if (parts.Count == 1 && ClashRouteCraft.Core.Rules.WithAction(raw, "DIRECT").StartsWith("DOMAIN-SUFFIX,")) return ManualRuleKind.Suffix;
        }
        catch (BuildException) { }
        return ManualRuleKind.Other;
    }
    static bool IsLan(JsonNode? raw)
    {
        if (!IsString(raw)) return false;
        try
        {
            var parts = ClashRouteCraft.Core.Rules.Split(ClashRouteCraft.Core.Rules.WithAction(raw, "DIRECT"));
            return parts[0] == "GEOIP" && parts[1].Equals("LAN", StringComparison.OrdinalIgnoreCase) && parts.Skip(3).All(x => x == "no-resolve");
        }
        catch (BuildException) { return false; }
    }
    static string StripPrefix(string text, ManualRuleKind kind)
    {
        string prefix = kind switch { ManualRuleKind.Suffix => "DOMAIN-SUFFIX,", ManualRuleKind.Exact => "DOMAIN,", _ => "" };
        return prefix.Length > 0 && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..].Trim() : text;
    }
    static string Display(JsonNode? raw, ManualRuleKind kind)
    {
        string text = S(raw).Trim();
        if (kind is ManualRuleKind.Other or ManualRuleKind.Lan) return text;
        try { return ClashRouteCraft.Core.Rules.Split(ClashRouteCraft.Core.Rules.WithAction(raw, "DIRECT"))[1]; }
        catch (BuildException) { return StripPrefix(text, kind); }
    }
}

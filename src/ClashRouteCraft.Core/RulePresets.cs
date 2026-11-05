using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record RulePreset(string Id, string Category, string Name, string Description, string[] Urls, string[] Domains, string[] Rules);
public sealed record PresetAppend(JsonObject Policy, int UrlsAdded, int RulesAdded, int Skipped, int CrossGroupMatches);

/// <summary>A local catalogue of suggestions, copied into existing policy fields on explicit confirmation.</summary>
public static class RulePresets
{
    public static string FilePath(string workspace) => Path.Combine(workspace, "settings", "rule-presets.yaml");
    public static IReadOnlyList<RulePreset> Load(string workspace)
    {
        string path = FilePath(workspace);
        if (!File.Exists(path)) BuiltInDefaults.Ensure(workspace);
        return Parse(Map(YamlData.Read(path), "规则预设"));
    }
    public static IReadOnlyList<RulePreset> Parse(JsonObject document)
    {
        if (I(document, "version", 0) != 1) throw new BuildException("规则预设 version 必须为 1");
        if (document.Any(x => x.Key is not ("version" or "presets"))) throw new BuildException("规则预设仅支持 version 和 presets");
        var ids = new HashSet<string>(); var result = new List<RulePreset>();
        foreach (var raw in L(document, "presets"))
        {
            var item = Map(raw, "预设项目");
            if (item.Any(x => x.Key is not ("id" or "category" or "name" or "description" or "urls" or "domains" or "rules"))) throw new BuildException("预设项目含未知字段");
            string id = Name(item["id"], "预设 id"), name = Name(item["name"], "预设名称");
            if (!ids.Add(id)) throw new BuildException("规则预设 id 重复：" + id);
            string[] Texts(string key) => L(item, key).Select(x => IsString(x) && !string.IsNullOrWhiteSpace(S(x)) ? S(x).Trim() : throw new BuildException(name + " 的 " + key + " 应为非空文本列表")).Distinct().ToArray();
            var urls = Texts("urls"); var domains = Texts("domains"); var rules = Texts("rules");
            foreach (string url in urls) if (!ConfigurationValidation.HttpUrl(url)) throw new BuildException(name + " 的 URL 无效");
            foreach (string domain in domains)
            {
                if (domain.Contains(',') || Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.IndexOfAny(['/', ':', '*', '+']) >= 0) throw new BuildException(name + " 的域名无效；填写裸域名，包含子域名；精确匹配请使用 rules");
                RulesSyntax("DOMAIN-SUFFIX," + domain);
            }
            foreach (string rule in rules) RulesSyntax(rule);
            if (urls.Length + domains.Length + rules.Length == 0) throw new BuildException(name + " 没有 URL、域名或规则");
            result.Add(new(id, S(item, "category", "其他"), name, S(item, "description"), urls, domains, rules));
        }
        return result;
    }
    static void RulesSyntax(string match)
    {
        if (match.Equals("MATCH", StringComparison.OrdinalIgnoreCase) || match.Equals("FINAL", StringComparison.OrdinalIgnoreCase)) throw new BuildException("预设不能包含兜底规则");
        var parts = ClashRouteCraft.Core.Rules.Split(match);
        if (!parts[0].EndsWith("-REGEX", StringComparison.OrdinalIgnoreCase) && parts.Skip(2).Any(x => x is not ("no-resolve" or "src")))
            throw new BuildException("预设 rules 只填写匹配条件，不附带直连 / 代理 / 拒绝动作");
        string full = Rules.WithAction(JsonValue.Create(match), "DIRECT", inputFormat: "match");
        Rules.ValidateSyntax(full);
        if (Rules.SplitFull(full)[0] is "MATCH" or "FINAL") throw new BuildException("预设不能包含 MATCH / FINAL，请在项目中设置兜底动作");
    }
    public static PresetAppend Append(JsonObject policy, IEnumerable<RulePreset> selected, string group)
    {
        if (!ProjectOrdering.RuleOrder(policy).Contains(group)) throw new BuildException("请选择直连、代理或拒绝");
        var output = (JsonObject)policy.DeepClone();
        var manual = L(output, group); output[group] ??= manual;
        var remotes = Rules.RemoteSpecs(output["remote_rules"]).ToList();
        var names = remotes.Select(x => S(x, "name")).ToHashSet();
        string Key(JsonNode? raw) => ClashRouteCraft.Core.Rules.Canonicalize(ClashRouteCraft.Core.Rules.WithAction(raw, "DIRECT")).Text;
        var matches = manual.Select(Key).ToHashSet(StringComparer.Ordinal);
        var otherMatches = new HashSet<string>(StringComparer.Ordinal);
        foreach (string other in ProjectOrdering.RuleOrder(policy).Where(x => x != group))
            foreach (var rule in L(policy, other)) otherMatches.Add(Key(rule));
        int addedUrls = 0, addedRules = 0, skipped = 0, cross = 0;
        foreach (var preset in selected)
        {
            // Validate even callers that construct records without loading the catalogue.
            Parse(O("version", 1, "presets", A(new JsonNode?[] { O("id", preset.Id, "name", preset.Name, "urls", preset.Urls, "domains", preset.Domains, "rules", preset.Rules) })));
            foreach (string url in preset.Urls)
            {
                if (remotes.Any(x => ProjectOrdering.IsUrlEntry(x) && ProjectOrdering.RemoteGroup(x) == group && S(x, "url") == url)) { skipped++; continue; }
                if (remotes.Any(x => B(x, "enabled", true) && ProjectOrdering.RemoteGroup(x) != group && S(x, "url") == url)) cross++;
                int number = 1; string name;
                do { name = "预设-" + preset.Name + "-" + number++; } while (!names.Add(name));
                remotes.Add(O("name", name, "url", url, "action", group.ToUpperInvariant())); addedUrls++;
            }
            foreach (string match in preset.Domains.Select(x => "DOMAIN-SUFFIX," + x).Concat(preset.Rules))
            {
                var entry = O("match", match); string key = Key(entry);
                if (!matches.Add(key)) { skipped++; continue; }
                if (otherMatches.Contains(key)) cross++;
                // Simple domain rules remain visible in the normal text fields.
                manual.Add(match.StartsWith("DOMAIN-SUFFIX,") || match.StartsWith("DOMAIN,") ? JsonValue.Create(match) : entry);
                addedRules++;
            }
        }
        output["remote_rules"] = A(remotes);
        return new(output, addedUrls, addedRules, skipped, cross);
    }
}

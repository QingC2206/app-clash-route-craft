using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace ClashRouteCraft.Desktop;

internal sealed class YamlHighlighting : IHighlightingDefinition
{
    readonly Dictionary<string, HighlightingColor> colors = [];
    public string Name => "YAML";
    public HighlightingRuleSet MainRuleSet { get; } = new();
    public IEnumerable<HighlightingColor> NamedHighlightingColors => colors.Values;
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
    public YamlHighlighting(bool dark)
    {
        HighlightingColor ColorFor(string name, string light, string night)
        {
            var color = new HighlightingColor { Name = name, Foreground = new SimpleHighlightingBrush(Color.Parse(dark ? night : light)) }; colors[name] = color; return color;
        }
        MainRuleSet.Rules.Add(new() { Regex = new Regex(@"""(?:[^""\\]|\\.)*""|'(?:[^']|'')*'"), Color = ColorFor("String", "#8C4B18", "#E6BC84") });
        MainRuleSet.Rules.Add(new() { Regex = new Regex(@"#[^\r\n]*"), Color = ColorFor("Comment", "#5D7B59", "#83C99B") });
        MainRuleSet.Rules.Add(new() { Regex = new Regex(@"[\p{L}\p{N}_-]+(?=\s*:)"), Color = ColorFor("Key", "#285CC8", "#A5C4FF") });
        MainRuleSet.Rules.Add(new() { Regex = new Regex(@"\b(?:true|false|null|\d+)\b"), Color = ColorFor("Value", "#7045A1", "#C1A8F5") });
    }
    public HighlightingColor GetNamedColor(string name) => colors.GetValueOrDefault(name)!;
    public HighlightingRuleSet GetNamedRuleSet(string name) => name == "YAML" ? MainRuleSet : null!;
}

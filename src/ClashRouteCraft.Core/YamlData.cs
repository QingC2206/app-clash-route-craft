using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class YamlData
{
    public static JsonNode? Parse(string text)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1) throw new BuildException("需要且只能包含一个 YAML 文档");
            return ConvertNode(stream.Documents[0].RootNode, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), 0);
        }
        catch (BuildException) { throw; }
        catch (YamlException ex) { throw new BuildException($"YAML 格式错误，行 {ex.Start.Line}、列 {ex.Start.Column}；请检查重复键、缩进或标点"); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { throw new BuildException("不是有效的 YAML/JSON 配置"); }
    }
    public static JsonNode? Parse(byte[] raw)
    {
        try { return Parse(new UTF8Encoding(false, true).GetString(raw).TrimStart('\uFEFF')); }
        catch (DecoderFallbackException) { throw new BuildException("配置不是有效的 UTF-8 文本"); }
    }
    public static JsonNode? Read(string path)
    {
        try { return Parse(File.ReadAllBytes(path)); }
        catch (IOException) { throw new BuildException("本地输入文件无法读取，请检查配置路径"); }
        catch (UnauthorizedAccessException) { throw new BuildException("没有权限读取本地配置文件"); }
    }
    static JsonNode? ConvertNode(YamlNode node, HashSet<YamlNode> active, int depth)
    {
        if (depth > 80 || !active.Add(node)) throw new BuildException("YAML 别名存在循环或嵌套过深");
        try
        {
            string tag = node.Tag.IsEmpty || node.Tag.IsNonSpecific ? "" : node.Tag.Value;
            if (tag.Length > 0 && tag is not "tag:yaml.org,2002:str" and not "tag:yaml.org,2002:int" and not "tag:yaml.org,2002:bool" and not "tag:yaml.org,2002:float" and not "tag:yaml.org,2002:null" and not "tag:yaml.org,2002:map" and not "tag:yaml.org,2002:seq" and not "tag:yaml.org,2002:merge")
                throw new BuildException("YAML 不接受自定义对象标签");
            if (node is YamlSequenceNode sequence) return new JsonArray(sequence.Children.Select(x => ConvertNode(x, active, depth + 1)).ToArray());
            if (node is YamlMappingNode mapping)
            {
                var result = new JsonObject();
                foreach (var (key, value) in mapping.Children.Where(x => (x.Key as YamlScalarNode)?.Value == "<<"))
                {
                    var merged = ConvertNode(value, active, depth + 1);
                    var maps = merged is JsonArray list ? list : new JsonArray(merged);
                    foreach (var source in maps)
                        foreach (var (name, item) in Map(source, "YAML 合并键")) if (!result.ContainsKey(name)) result[name] = item?.DeepClone();
                }
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode scalar) throw new BuildException("YAML 的映射键必须是简单值");
                    if (scalar.Value == "<<") continue;
                    result[scalar.Value ?? ""] = ConvertNode(value, active, depth + 1);
                }
                return result;
            }
            var text = ((YamlScalarNode)node).Value ?? "";
            var scalarNode = (YamlScalarNode)node;
            if (scalarNode.Style is not ScalarStyle.Plain && scalarNode.Style is not ScalarStyle.Any || tag == "tag:yaml.org,2002:str") return JsonValue.Create(text);
            if (text is "" or "~" || text.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (Regex.IsMatch(text, @"^(true|yes|on)$", RegexOptions.IgnoreCase)) return JsonValue.Create(true);
            if (Regex.IsMatch(text, @"^(false|no|off)$", RegexOptions.IgnoreCase)) return JsonValue.Create(false);
            string number = text.Replace("_", "");
            if (Regex.IsMatch(number, @"^[-+]?\d+$") && long.TryParse(number, CultureInfo.InvariantCulture, out var integer))
            {
                if (number.Length > 1 && number[0] == '0' && number.All(c => c is >= '0' and <= '7')) integer = System.Convert.ToInt64(number, 8);
                return JsonValue.Create(integer);
            }
            if (Regex.IsMatch(number, @"^[-+]?0x[0-9a-fA-F]+$")) return JsonValue.Create(System.Convert.ToInt64(number.TrimStart('+', '-')[2..], 16) * (number.StartsWith('-') ? -1L : 1L));
            if (Regex.IsMatch(number, @"^[-+]?0b[01]+$")) return JsonValue.Create(System.Convert.ToInt64(number.TrimStart('+', '-')[2..], 2) * (number.StartsWith('-') ? -1L : 1L));
            if (number.Contains('.') && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating)) return JsonValue.Create(floating);
            return JsonValue.Create(text);
        }
        finally { active.Remove(node); }
    }
    static YamlNode ToNode(JsonNode? value) => value switch
    {
        JsonObject map => new YamlMappingNode(map.Select(x => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(x.Key), ToNode(x.Value)))),
        JsonArray list => new YamlSequenceNode(list.Select(ToNode)),
        null => new YamlScalarNode("null"),
        _ when IsString(value) => new YamlScalarNode(S(value)) { Style = ScalarStyle.DoubleQuoted },
        _ => new YamlScalarNode(value.ToJsonString()) { Style = ScalarStyle.Plain }
    };
    static string WriteNode(YamlNode node, string comment)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, false);
        return comment + writer.ToString().Replace("\r\n", "\n");
    }
    public static string Write(JsonNode? document, string comment = "") => WriteNode(ToNode(document), comment);
    public static byte[] Bytes(JsonNode? document, string comment = "") => Encoding.UTF8.GetBytes(Write(document, comment));
    public static byte[] Mc2DnsBytes(JsonObject document, string comment = "")
    {
        var root = (YamlMappingNode)ToNode(document);
        var dns = (YamlMappingNode)root.Children[new YamlScalarNode("dns")];
        var listen = (YamlScalarNode)dns.Children[new YamlScalarNode("listen")];
        // MC2 1.2.2 dnsmasq.postconf uses awk -F: '/listen/{print $3}' | xargs.
        // A quoted scalar leaves an unmatched closing quote and loses the DNS port.
        if (listen.Value != "0.0.0.0:23453") throw new BuildException("MC2 DNS 监听地址不兼容");
        listen.Style = ScalarStyle.Plain;
        return Encoding.UTF8.GetBytes(WriteNode(root, comment));
    }
}

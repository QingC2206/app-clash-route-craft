using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Keep authoritative source text. Scalar edits preserve comments and untouched fields.</summary>
public static class YamlEditing
{
    public static string Apply(string text, JsonNode? changed)
    {
        var original = YamlData.Parse(text);
        if (Canonical(original) == Canonical(changed)) return text;
        var stream = new YamlStream(); stream.Load(new StringReader(text));
        // Representation-model End marks for collections describe the start token.
        // Pair parser collection events to obtain the actual replacement extent.
        var collectionEnds = new Dictionary<(int Start, char Kind), int>();
        var collectionStack = new Stack<(int Start, char Kind)>();
        var parser = new Parser(new StringReader(text));
        while (parser.MoveNext())
        {
            if (parser.Current is MappingStart mappingStart) collectionStack.Push(((int)mappingStart.Start.Index, 'm'));
            else if (parser.Current is SequenceStart sequenceStart) collectionStack.Push(((int)sequenceStart.Start.Index, 's'));
            else if (parser.Current is MappingEnd or SequenceEnd) collectionEnds[collectionStack.Pop()] = (int)parser.Current.End.Index;
        }
        var patches = new List<(int Start, int End, string Text)>();
        void Visit(YamlNode node, JsonNode? before, JsonNode? after)
        {
            if (Canonical(before) == Canonical(after)) return;
            if (node is YamlMappingNode { Style: MappingStyle.Block } blockMapping && before is JsonObject blockBefore && after is JsonObject blockAfter
                && blockBefore.All(x => blockAfter.ContainsKey(x.Key)) && blockAfter.Count > blockBefore.Count && !blockMapping.Children.Keys.Any(x => (x as YamlScalarNode)?.Value == "<<"))
            {
                foreach (var (key, child) in blockMapping.Children) { string name = ((YamlScalarNode)key).Value ?? ""; Visit(child, blockBefore[name], blockAfter[name]); }
                var added = new JsonObject(); foreach (var (key, value) in blockAfter.Where(x => !blockBefore.ContainsKey(x.Key))) added[key] = value?.DeepClone();
                int insertionEnd = collectionEnds[((int)node.Start.Index, 'm')];
                int lineStart = insertionEnd == 0 ? 0 : text.LastIndexOf('\n', Math.Min(insertionEnd - 1, text.Length - 1)) + 1;
                int position = text[lineStart..insertionEnd].All(char.IsWhiteSpace) ? lineStart : insertionEnd;
                string indent = new(' ', (int)Math.Max(0, node.Start.Column - 1));
                string addition = string.Join('\n', BlockText(added).Split('\n').Select(line => indent + line)) + "\n";
                if (position > 0 && text[position - 1] != '\n') addition = "\n" + addition;
                patches.Add((position, position, addition)); return;
            }
            if (node is YamlMappingNode mapping && before is JsonObject oldMap && after is JsonObject newMap && oldMap.Select(x => x.Key).ToHashSet().SetEquals(newMap.Select(x => x.Key)) && !mapping.Children.Keys.Any(x => (x as YamlScalarNode)?.Value == "<<"))
            {
                foreach (var (key, child) in mapping.Children) { string name = ((YamlScalarNode)key).Value ?? ""; Visit(child, oldMap[name], newMap[name]); }
                return;
            }
            if (node is YamlSequenceNode sequence && before is JsonArray oldList && after is JsonArray newList && oldList.Count == newList.Count)
            { for (int i = 0; i < oldList.Count; i++) Visit(sequence.Children[i], oldList[i], newList[i]); return; }
            int start = (int)node.Start.Index;
            int end = node is YamlMappingNode ? collectionEnds[(start, 'm')] : node is YamlSequenceNode ? collectionEnds[(start, 's')] : (int)node.End.Index;
            if (node is YamlMappingNode { Style: MappingStyle.Flow } || node is YamlSequenceNode { Style: SequenceStyle.Flow }) end = FlowEnd(text, start);
            string replacement = after?.ToJsonString() ?? "null";
            if (node is YamlMappingNode { Style: MappingStyle.Block } || node is YamlSequenceNode { Style: SequenceStyle.Block })
            {
                string indent = new(' ', (int)Math.Max(0, node.Start.Column - 1));
                replacement = string.Join('\n', BlockText(after).Split('\n').Select((line, index) => index == 0 ? line : indent + line));
                // YAML permits an indentless block sequence below a mapping key,
                // but its empty flow replacement ([]) must be indented as a value.
                if (node is YamlSequenceNode && after is JsonArray { Count: 0 }) replacement = "  " + replacement;
            }
            // A block collection may own the newline before the following sibling.
            // Keep that boundary when replacing it with a JSON/YAML flow collection.
            var boundary = System.Text.RegularExpressions.Regex.Match(text[start..end], @"\r?\n[ \t]*$");
            if (boundary.Success) replacement += boundary.Value;
            patches.Add((start, end, replacement));
        }
        Visit(stream.Documents[0].RootNode, original, changed);
        int lastStart = text.Length + 1;
        foreach (var patch in patches.Select((patch, index) => (Patch: patch, Index: index)).OrderByDescending(x => x.Patch.Start).ThenByDescending(x => x.Index).Select(x => x.Patch))
        {
            if (patch.End > lastStart || patch.Start < 0 || patch.End > text.Length) throw new BuildException("字段使用了共享 YAML 别名，请在高级 YAML 编辑中修改");
            text = text[..patch.Start] + patch.Text + text[patch.End..]; lastStart = patch.Start;
        }
        if (Canonical(YamlData.Parse(text)) != Canonical(changed)) throw new BuildException("此配置包含 YAML 合并或别名，请在高级 YAML 编辑中修改对应字段");
        return text;
    }
    static string BlockText(JsonNode? value) => System.Text.RegularExpressions.Regex.Replace(YamlData.Write(value), @"(?m)^(?:---|\.\.\.)[ \t]*(?:\r?\n|$)", "").TrimEnd('\r', '\n');
    static int FlowEnd(string text, int start)
    {
        int depth = 0; char quote = '\0'; bool comment = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (comment) { if (c is '\r' or '\n') comment = false; continue; }
            if (quote != '\0')
            {
                if (quote == '"' && c == '\\') { i++; continue; }
                if (c == quote) { if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') i++; else quote = '\0'; }
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]))) { comment = true; continue; }
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') { depth--; if (depth == 0) return i + 1; }
        }
        throw new BuildException("无法定位 YAML 集合，请在高级编辑中修改");
    }
}

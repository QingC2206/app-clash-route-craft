using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClashRouteCraft.Core;

public sealed class BuildException(string message) : Exception(message);
public record BuildProgress(string Stage, string Message, int Completed = 0, int Total = 6, string State = "pending");
public record BuildResult(string OutputPath, JsonObject Report);
public record CompiledProfile(JsonObject Configuration, JsonObject Report);
public record ValidationPaths(string Mihomo, string DataDirectory, string Mc2Package);
public record BuildOptions(bool Force = false, bool Offline = false, ValidationPaths? Validation = null, bool RefreshRules = false, bool AutoFetch = true);

public static class Data
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = JsonContext.Default };
    public static JsonObject Map(JsonNode? value, string label) => value as JsonObject ?? throw new BuildException($"{label} 应为 YAML 映射");
    public static JsonArray List(JsonNode? value, string label) => value as JsonArray ?? throw new BuildException($"{label} 应为列表");
    public static JsonObject M(JsonObject value, string key) => value[key] is null ? new() : Map(value[key], key);
    public static JsonArray L(JsonObject value, string key) => value[key] is null ? new() : List(value[key], key);
    public static string S(JsonNode? value, string fallback = "") => value is JsonValue scalar && scalar.TryGetValue<string>(out var result) ? result : value is null ? fallback : value.ToJsonString();
    public static string S(JsonObject value, string key, string fallback = "") => value[key] is null ? fallback : S(value[key]);
    public static bool IsString(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out _);
    public static bool B(JsonObject value, string key, bool fallback = false) => value[key] is null ? fallback : value[key] is JsonValue scalar && scalar.TryGetValue<bool>(out var result) ? result : throw new BuildException($"{key} 必须为布尔值");
    public static int I(JsonObject value, string key, int fallback) => value[key] is null ? fallback : Integer(value[key], key);
    public static int Integer(JsonNode? value, string label) => value is JsonValue scalar && scalar.TryGetValue<long>(out var number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : value is JsonValue v && v.TryGetValue<int>(out var i) ? i : throw new BuildException($"{label} 必须为整数");
    public static JsonObject O(params object?[] items)
    {
        var result = new JsonObject();
        for (int i = 0; i < items.Length; i += 2) result[(string)items[i]!] = N(items[i + 1]);
        return result;
    }
    public static JsonNode? N(object? value) => value switch
    {
        null => null, JsonNode node => node.DeepClone(), string s => JsonValue.Create(s), bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i), long l => JsonValue.Create(l), double d => JsonValue.Create(d),
        IEnumerable<string> strings => A(strings.Select(x => (JsonNode?)JsonValue.Create(x))),
        IEnumerable<JsonNode?> nodes => A(nodes),
        _ => throw new ArgumentException($"Unsupported data type: {value.GetType().Name}")
    };
    public static JsonArray A(IEnumerable<JsonNode?> values) => new(values.Select(v => v?.DeepClone()).ToArray());
    public static JsonArray A(params string[] values) => A(values.Select(v => (JsonNode?)JsonValue.Create(v)));
    public static JsonArray A(IEnumerable<string> values) => A(values.Select(v => (JsonNode?)JsonValue.Create(v)));
    public static void Push(this JsonArray array, JsonNode? node) => ((ICollection<JsonNode?>)array).Add(node);
    public static JsonObject Merge(JsonObject first, JsonObject second)
    {
        var result = (JsonObject)first.DeepClone();
        foreach (var (key, value) in second) result[key] = value?.DeepClone();
        return result;
    }
    public static string Name(JsonNode? value, string label)
    {
        if (!IsString(value) || string.IsNullOrWhiteSpace(S(value)) || S(value).IndexOfAny(['\r', '\n', ',']) >= 0)
            throw new BuildException($"{label} 需要非空名称，不能有逗号或换行");
        return S(value);
    }
    public static string FileName(string name, string label = "名称")
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.IndexOfAny("<>:\"/\\|?*\r\n,".ToCharArray()) >= 0 || name.EndsWith(' ') || name.EndsWith('.') || name is "." or ".." || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new BuildException($"{label} 不能包含文件名非法字符或系统保留名称");
        return name;
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Canonical(JsonNode? value) => value switch
    {
        JsonObject map => "{" + string.Join(",", map.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => PythonString(x.Key) + ":" + Canonical(x.Value))) + "}",
        JsonArray list => "[" + string.Join(",", list.Select(Canonical)) + "]",
        null => "null", _ => value.ToJsonString()
    };
    // Match Python json.dumps(sort_keys=True, ensure_ascii=True) to reuse existing cache keys.
    public static string PythonJson(JsonNode? value) => value switch
    {
        JsonObject map => "{" + string.Join(", ", map.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => PythonString(x.Key) + ": " + PythonJson(x.Value))) + "}",
        JsonArray list => "[" + string.Join(", ", list.Select(PythonJson)) + "]",
        null => "null", _ when IsString(value) => PythonString(S(value)), _ => value.ToJsonString()
    };
    static string PythonString(string text)
    {
        var output = new StringBuilder("\"");
        foreach (char c in text) output.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when c < 32 || c > 126 => "\\u" + ((int)c).ToString("x4"), _ => c.ToString() });
        return output.Append('"').ToString();
    }
    public static Regex Pattern(string pattern, string label)
    {
        // Named-group syntax is the common Python-specific form used in local filters.
        pattern = Regex.Replace(pattern, @"\(\?P<([A-Za-z_]\w*)>", "(?<$1>");
        pattern = Regex.Replace(pattern, @"\(\?P=([A-Za-z_]\w*)\)", @"\k<$1>");
        try { return new Regex(string.IsNullOrEmpty(pattern) ? ".*" : pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException) { throw new BuildException($"{label} 的筛选正则无效"); }
    }
    public static bool Match(Regex pattern, string value)
    {
        try { return pattern.IsMatch(value); }
        catch (RegexMatchTimeoutException) { throw new BuildException("筛选正则运行超时，请简化表达式"); }
    }
    public static bool HasPlaceholder(JsonNode? node) => node switch
    {
        JsonObject map => map.Any(x => HasPlaceholder(x.Value)), JsonArray list => list.Any(HasPlaceholder),
        _ when IsString(node) => new[] { "FILL_", "YOUR_", "脱敏" }.Any(S(node).Contains), _ => false
    };
    public static JsonNode? ExpandEnvironment(JsonNode? node)
    {
        if (node is JsonObject map) { var result = new JsonObject(); foreach (var (k, v) in map) result[k] = ExpandEnvironment(v); return result; }
        if (node is JsonArray array) return new JsonArray(array.Select(ExpandEnvironment).ToArray());
        if (IsString(node) && Regex.Match(S(node), @"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$") is { Success: true } match)
            return JsonValue.Create(Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? throw new BuildException($"缺少环境变量 {match.Groups[1].Value}"));
        return node?.DeepClone();
    }
}

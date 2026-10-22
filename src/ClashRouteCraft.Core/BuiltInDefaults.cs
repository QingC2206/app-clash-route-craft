using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Public factory templates embedded in the binary, independent of personal workspaces.</summary>
public static class BuiltInDefaults
{
    public static string Text(string name)
    {
        using var stream = typeof(BuiltInDefaults).Assembly.GetManifestResourceStream("ClashRouteCraft.Defaults." + name)
            ?? throw new BuildException("内置配置模板缺失，请重新安装程序");
        using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
    }
    public static IReadOnlyDictionary<string, string> Documents()
    {
        var policy = Map(YamlData.Parse(Text("policy.yaml")), "内置规则");
        return new Dictionary<string, string> { ["settings.yaml"] = Text("settings.yaml"), ["residential.yaml"] = Text("residential.yaml"), ["policy.yaml"] = YamlData.Write(policy) };
    }
    public static IReadOnlyList<string> Ensure(string workspace)
    {
        workspace = Path.GetFullPath(workspace); var restored = new List<string>();
        CheckPath(Path.Combine(workspace, ".cache", ".workspace.lock"));
        using var lease = WorkspaceLease.Acquire(workspace, true);
        void Create(string relative, string contents)
        {
            string path = Path.Combine(workspace, relative); CheckPath(path);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".building-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, contents, new UTF8Encoding(false));
                try { File.Move(temp, path, false); restored.Add(relative); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        var documents = Documents();
        Create(Path.Combine("settings", "rule-defaults.yaml"), YamlData.Write(RuleDefaults.Extract(Map(YamlData.Parse(documents["policy.yaml"]), "默认规则"))));
        foreach (var (name, text) in documents) Create(Path.Combine("config", "Example", name), text);
        return restored;
    }
    internal static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new BuildException("默认文件路径包含符号链接或目录联接，未写入；请检查数据目录");
        if (Directory.Exists(path)) throw new BuildException("默认文件路径被同名目录占用，请检查数据目录");
    }
}

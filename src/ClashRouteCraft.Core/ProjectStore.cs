using System.Text;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Coordinates project changes with saves and builds in every app instance.</summary>
internal static class WorkspaceLease
{
    public static FileStream Acquire(string workspace, bool exclusive = false)
    {
        string cache = Path.Combine(Path.GetFullPath(workspace), ".cache");
        Directory.CreateDirectory(cache);
        try { return new(Path.Combine(cache, ".workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite); }
        catch (IOException) { throw new BuildException("工作区正在生成、保存或管理项目，请等待操作完成后重试"); }
    }
}

public static class ProjectStore
{
    public sealed record DeletedProject(string Archive, string Name, string DeletedAt);
    static readonly string[] Areas = ["config", "output", ".cache"];
    public static string TrashPath(string workspace) => Path.Combine(Path.GetFullPath(workspace), ".project-trash");
    public static void ValidateDestination(string workspace, string name, string? except = null) => CheckDestination(Path.GetFullPath(workspace), name, except);
    internal static string ValidateName(string name)
    {
        name = FileName(name, "项目名称");
        if (WorkspaceSession.IsExample(name)) throw new BuildException("Example 是保留的只读示例名称");
        if (name.StartsWith('.') || name.Equals("examples", StringComparison.OrdinalIgnoreCase)) throw new BuildException("项目名不能以点开头，也不能使用保留名称 examples");
        return name;
    }
    internal static void CheckDestination(string workspace, string name, string? except = null)
    {
        ValidateName(name);
        foreach (string area in Areas)
        {
            string root = Path.Combine(workspace, area);
            CheckDirectory(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).Equals(except, StringComparison.OrdinalIgnoreCase)))
                throw new BuildException($"{area} 中已有同名文件或目录，请使用其他项目名称");
        }
    }
    static void CheckDirectory(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new BuildException("项目管理目录不能是文件、符号链接或目录联接");
    }
    static void CheckSource(string workspace, string name)
    {
        ValidateName(name);
        if (!File.Exists(Path.Combine(workspace, "config", name, "settings.yaml"))) throw new BuildException("项目不存在，请刷新项目列表");
        foreach (string area in Areas) { CheckDirectory(Path.Combine(workspace, area)); CheckDirectory(Path.Combine(workspace, area, name)); }
        // Also detect a build held by older desktop releases.
        string legacyLock = Path.Combine(workspace, ".cache", name, ".build.lock");
        if (File.Exists(legacyLock))
            try { using var held = new FileStream(legacyLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new BuildException("当前项目正在另一个进程中生成，请等待完成"); }
    }
    public static void Rename(string workspace, string oldName, string newName)
    {
        workspace = Path.GetFullPath(workspace); ValidateName(oldName); ValidateName(newName);
        using var lease = WorkspaceLease.Acquire(workspace, true);
        CheckSource(workspace, oldName); CheckDestination(workspace, newName, oldName);
        if (oldName == newName) return;
        var moves = new List<(string From, string To)>();
        try
        {
            foreach (string area in Areas)
            {
                string from = Path.Combine(workspace, area, oldName), to = Path.Combine(workspace, area, newName);
                if (!Directory.Exists(from)) continue;
                // A temporary name also supports changing only the letter case on Windows.
                string temporary = Path.Combine(workspace, area, ".rename-" + Guid.NewGuid().ToString("N"));
                Move(from, temporary, moves); Move(temporary, to, moves);
            }
            string reportPath = Path.Combine(workspace, "output", newName, "HomeChain.report.json");
            if (File.Exists(reportPath))
            {
                JsonObject? report;
                try { report = JsonNode.Parse(File.ReadAllText(reportPath)) as JsonObject; }
                catch (System.Text.Json.JsonException) { report = null; }
                if (report?["dns"] is JsonObject dns && S(dns, "mc2_file").Length > 0)
                {
                    dns["mc2_file"] = Path.Combine(workspace, "output", newName, "HomeChain.MC2DNS.yaml");
                    FileBundle.AtomicWrite(reportPath, Encoding.UTF8.GetBytes(report.ToJsonString(JsonOptions)));
                }
            }
        }
        catch { Rollback(moves); throw; }
    }
    /// <returns>The recoverable archive directory, containing config, output and cache.</returns>
    public static string Delete(string workspace, string name)
    {
        workspace = Path.GetFullPath(workspace); ValidateName(name);
        using var lease = WorkspaceLease.Acquire(workspace, true);
        CheckSource(workspace, name);
        string trash = TrashPath(workspace); CheckDirectory(trash); Directory.CreateDirectory(trash);
        string archive = Path.Combine(trash, name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        var moves = new List<(string From, string To)>();
        try
        {
            File.WriteAllText(Path.Combine(archive, "project.json"), O("name", name, "deleted_utc", DateTime.UtcNow.ToString("O")).ToJsonString(JsonOptions));
            foreach (string area in Areas)
            {
                string from = Path.Combine(workspace, area, name);
                if (Directory.Exists(from)) Move(from, Path.Combine(archive, area == ".cache" ? "cache" : area), moves);
            }
            return archive;
        }
        catch { Rollback(moves); throw; }
    }
    public static IReadOnlyList<DeletedProject> DeletedProjects(string workspace)
    {
        string trash = TrashPath(workspace); CheckDirectory(trash);
        if (!Directory.Exists(trash)) return [];
        var result = new List<DeletedProject>();
        foreach (string archive in Directory.GetDirectories(trash))
        {
            if (File.GetAttributes(archive).HasFlag(FileAttributes.ReparsePoint) || !File.Exists(Path.Combine(archive, "config", "settings.yaml"))) continue;
            try
            {
                var info = Map(JsonNode.Parse(File.ReadAllText(Path.Combine(archive, "project.json"))), "回收项目");
                result.Add(new(archive, S(info, "name"), S(info, "deleted_utc")));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or BuildException) { }
        }
        return result.OrderByDescending(x => x.DeletedAt).ToArray();
    }
    public static void Restore(string workspace, string archive, string name)
    {
        workspace = Path.GetFullPath(workspace); archive = Path.GetFullPath(archive); ValidateName(name);
        string trash = TrashPath(workspace);
        if (!string.Equals(Path.GetDirectoryName(archive), trash, StringComparison.OrdinalIgnoreCase)) throw new BuildException("只能恢复当前工作区回收站中的项目");
        using var lease = WorkspaceLease.Acquire(workspace, true);
        CheckDirectory(trash); CheckDirectory(archive); CheckDestination(workspace, name);
        if (!File.Exists(Path.Combine(archive, "config", "settings.yaml"))) throw new BuildException("回收项目不存在或已恢复");
        foreach (string area in Areas) CheckDirectory(Path.Combine(archive, area == ".cache" ? "cache" : area));
        var moves = new List<(string From, string To)>();
        try
        {
            foreach (string area in Areas)
            {
                string from = Path.Combine(archive, area == ".cache" ? "cache" : area);
                if (!Directory.Exists(from)) continue;
                Directory.CreateDirectory(Path.Combine(workspace, area)); Move(from, Path.Combine(workspace, area, name), moves);
            }
            string path = Path.Combine(workspace, "output", name, "HomeChain.report.json");
            if (File.Exists(path))
            {
                JsonObject? report;
                try { report = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch (System.Text.Json.JsonException) { report = null; }
                if (report?["dns"] is JsonObject dns) { dns["mc2_file"] = Path.Combine(workspace, "output", name, "HomeChain.MC2DNS.yaml"); FileBundle.AtomicWrite(path, Encoding.UTF8.GetBytes(report.ToJsonString(JsonOptions))); }
            }
        }
        catch { Rollback(moves); throw; }
        // Preserve unknown archive contents; remove only our own now-empty archive.
        try
        {
            if (Directory.GetFileSystemEntries(archive).All(x => Path.GetFileName(x) == "project.json"))
            { File.Delete(Path.Combine(archive, "project.json")); Directory.Delete(archive); }
        }
        catch (IOException) { }
    }
    static void Move(string from, string to, List<(string From, string To)> moves) { Directory.Move(from, to); moves.Add((from, to)); }
    static void Rollback(List<(string From, string To)> moves)
    {
        try { foreach (var (from, to) in moves.AsEnumerable().Reverse()) Directory.Move(to, from); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BuildException("文件被占用，项目迁移未能完全回滚；请检查 config、output、.cache 及 .project-trash 中的数据后重试"); }
    }
}

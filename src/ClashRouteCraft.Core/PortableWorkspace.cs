namespace ClashRouteCraft.Core;

/// <summary>Relocates owned workspace data without merging or overwriting existing projects.</summary>
public static class PortableWorkspace
{
    public static readonly string[] DataDirectories = ["config", "output", ".cache", ".project-trash", "settings"];

    public static void Move(string source, string destination)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(source)) throw new BuildException("原工作区不存在，未迁移数据");
        CheckAncestors(source); CheckAncestors(destination);
        if (source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BuildException("目标目录不能包含原工作区");
        // Application settings are managed separately; only project data is moved here.
        var moves = new List<(string From, string To)>();
        foreach (string area in DataDirectories.Where(x => x != "settings"))
        {
            string from = Path.Combine(source, area), to = Path.Combine(destination, area);
            if (destination.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || destination.Equals(from, StringComparison.OrdinalIgnoreCase))
                throw new BuildException("目标目录不能位于待迁移数据内");
            CheckTree(from); CheckTree(to);
            if (!Directory.Exists(from)) continue;
            if (area == ".cache")
                foreach (string entry in Directory.EnumerateFileSystemEntries(from).Where(x => Path.GetFileName(x) != ".workspace.lock"))
                    moves.Add((entry, Path.Combine(to, Path.GetFileName(entry))));
            else moves.Add((from, to));
        }
        foreach (var move in moves)
            if (Path.Exists(move.To)) throw new BuildException($"EXE 目录已存在 {Path.GetRelativePath(destination, move.To)}，为避免覆盖，未迁移任何项目数据");
        Directory.CreateDirectory(destination);
        using var migration = AcquireMigration(destination);
        using var sourceLease = WorkspaceLease.Acquire(source, true);
        using var targetLease = WorkspaceLease.Acquire(destination, true);
        // Older releases can hold a project lock without taking the workspace lock.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, ".cache"), ".build.lock", SearchOption.AllDirectories))
            try { using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new BuildException("原工作区仍有生成任务，请完成后再迁移"); }
        var completed = new List<(string From, string To)>();
        try
        {
            foreach (var move in moves)
            {
                CheckAncestors(move.To); Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                MoveEntry(move.From, move.To); completed.Add(move);
            }
        }
        catch
        {
            try { foreach (var move in completed.AsEnumerable().Reverse()) MoveEntry(move.To, move.From); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new BuildException("迁移未能完全回滚；数据仍保留在原工作区和 EXE 目录，请检查后重试"); }
            throw;
        }
    }
    static FileStream AcquireMigration(string directory)
    {
        try { return new(Path.Combine(directory, ".migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new BuildException("另一个迁移任务正在运行，请稍后重试"); }
    }
    static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
    }
    static void CheckAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new BuildException("数据迁移路径不能包含符号链接或目录联接");
    }
    static void CheckTree(string path)
    {
        if (!Path.Exists(path)) return;
        CheckAncestors(path);
        if (File.Exists(path)) throw new BuildException("数据目录被同名文件占用");
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) throw new BuildException("数据目录内存在符号链接或目录联接，未迁移");
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
}

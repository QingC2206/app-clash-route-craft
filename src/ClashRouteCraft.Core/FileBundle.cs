namespace ClashRouteCraft.Core;

public static class FileBundle
{
    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string staged = path + ".building-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(staged, bytes); File.Move(staged, path, true); }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    public static void Commit(IReadOnlyList<(string Path, byte[] Bytes)> outputs, CancellationToken cancellation = default, Action<string, string>? replace = null, IReadOnlyList<string>? removals = null)
    {
        var staged = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var previous = new Dictionary<string, byte[]?>();
        var replaced = new List<string>();
        replace ??= (src, dst) => File.Move(src, dst, true);
        try
        {
            foreach (var (path, bytes) in outputs)
            {
                cancellation.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(path);
                if (staged.ContainsKey(target)) throw new BuildException("输出路径不能重复");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                previous[target] = File.Exists(target) ? File.ReadAllBytes(target) : null;
                string temporary = target + ".building-" + Guid.NewGuid().ToString("N");
                staged[target] = temporary;
                File.WriteAllBytes(temporary, bytes);
            }
            foreach (string path in removals ?? [])
            {
                string target = Path.GetFullPath(path);
                if (staged.ContainsKey(target)) throw new BuildException("输出路径不能重复");
                previous[target] = File.Exists(target) ? File.ReadAllBytes(target) : null; staged[target] = null;
            }
            cancellation.ThrowIfCancellationRequested();
            // Once commit begins, finish or roll back; cancellation cannot split the bundle.
            foreach (var (target, temporary) in staged) { if (temporary is null) File.Delete(target); else replace(temporary, target); replaced.Add(target); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bool restored = true;
            foreach (string target in replaced.AsEnumerable().Reverse())
                try { if (previous[target] is { } bytes) AtomicWrite(target, bytes); else File.Delete(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { restored = false; }
            throw new BuildException(restored ? "输出写入失败；已保留原有主配置、DNS、报告和订阅快照" : "输出写入与回滚未全部成功，请核对文件后再导入");
        }
        finally { foreach (string? path in staged.Values) if (path is not null && File.Exists(path)) File.Delete(path); }
    }
}

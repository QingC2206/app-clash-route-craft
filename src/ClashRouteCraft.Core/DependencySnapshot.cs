using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class DependencySnapshot
{
    public const int Version = 5;
    public static string FileHash(string path) => File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    public static JsonObject Validation(JsonObject settings, string project, JsonObject configuration, JsonObject? asn = null)
    {
        var validation = M(settings, "validation"); var result = new JsonObject();
        string exe = S(validation, "mihomo");
        result["mihomo"] = exe.Length == 0 ? "not_configured" : FileHash(Path.GetFullPath(exe, project));
        string data = S(validation, "data_dir");
        foreach (string file in AsnPolicy.ValidationFiles(configuration, asn)) result[file] = data.Length == 0 ? "not_configured" : FileHash(Path.Combine(Path.GetFullPath(data, project), file));
        if (ConfigurationValidation.Target(settings) == "mc2")
        {
            string package = S(validation, "mc2_package"); result["mc2_package"] = package.Length == 0 ? "not_configured" : FileHash(Path.GetFullPath(package, project));
        }
        return result;
    }
    public static bool ValidationCurrent(WorkspaceSession session, JsonObject report)
    {
        if (I(report, "dependency_version", 0) != Version) return false;
        var settings = ConfigurationValidation.EffectiveSettings(session.Settings, session.Workspace);
        string main = Path.Combine(session.OutputPath, "HomeChain.yaml");
        return File.Exists(main) && Canonical(M(report, "validation_inputs")) == Canonical(Validation(settings, session.ProjectPath, Map(YamlData.Read(main), "生成配置"), M(report, "asn")));
    }
    public static bool RuleCachesCurrent(string project, JsonObject report, string? cacheDirectory = null)
    {
        string workspace = Directory.GetParent(project)?.Parent?.FullName ?? project;
        string cache = cacheDirectory ?? Path.Combine(workspace, ".cache", Path.GetFileName(project));
        foreach (var raw in L(report, "fetches"))
        {
            var entry = Map(raw, "名单读取"); string file = S(entry, "cache_file");
            if (file.Length == 0) continue;
            FileName(file, "名单缓存文件名");
            if (FileHash(Path.Combine(cache, file)) != S(entry, "sha256")) return false;
        }
        return true;
    }
}

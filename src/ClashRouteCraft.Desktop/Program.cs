using Avalonia;
using ClashRouteCraft.Core;
using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-check"))
        {
            int result = Array.IndexOf(args, "--result");
            return result >= 0 && result + 1 < args.Length ? NativeSmoke.Run(Path.GetFullPath(args[result + 1])) : 2;
        }
        if (args.Contains("--build") || args.Contains("--compile") || args.Contains("--initialize") || args.Contains("--download-resources")) return CommandLine(args).GetAwaiter().GetResult();
        if (args.Contains("--relocate-workspace"))
        {
            int source = Array.IndexOf(args, "--relocate-workspace");
            if (source + 1 >= args.Length) return 2;
            try { PortableWorkspace.Move(args[source + 1], AppContext.BaseDirectory); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        int workspace = Array.IndexOf(args, "--workspace"); if (workspace >= 0 && workspace + 1 < args.Length) App.InitialWorkspace = args[workspace + 1];
        return BuildApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildApp() => AppBuilder.Configure<App>().UsePlatformDetect();
    static async Task<int> CommandLine(string[] args)
    {
        string Get(string key, string fallback = "") { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        string resultPath = Get("--result");
        try
        {
            JsonObject result;
            if (args.Contains("--initialize") || args.Contains("--download-resources"))
            {
                string workspace = Path.GetFullPath(Get("--workspace", AppContext.BaseDirectory));
                var restored = BuiltInDefaults.Ensure(workspace);
                int downloaded = args.Contains("--download-resources") ? await RuntimeResources.DownloadMissingAsync(workspace) : 0;
                result = O("ok", true, "restored", A(restored), "downloaded", downloaded, "missing_resources", A(RuntimeResources.Missing(workspace).Select(x => x.Name)));
            }
            else if (args.Contains("--compile"))
            {
                string file = Path.GetFullPath(Get("--settings"));
                var profile = await ProfileCompiler.CompileAsync(Map(YamlData.Read(file), "settings"), Path.GetDirectoryName(file)!, args.Contains("--offline"));
                result = O("ok", true, "configuration", profile.Configuration, "report", profile.Report);
            }
            else
            {
                string workspace = Path.GetFullPath(Get("--workspace", AppContext.BaseDirectory)); string project = Get("--project");
                var projects = project.Length > 0 ? new[] { project } : BuildEngine.DiscoverProjects(workspace);
                if (projects.Count == 0) throw new BuildException("工作区中没有项目");
                var reports = new JsonArray(); var engine = new BuildEngine();
                foreach (string name in projects)
                {
                    var built = await engine.BuildProjectAsync(workspace, name, new(args.Contains("--refresh"), args.Contains("--offline"), RefreshRules: args.Contains("--refresh-rules"), AutoFetch: !args.Contains("--cached-subscriptions")));
                    reports.Push(O("project", name, "output", built.OutputPath, "report", built.Report));
                }
                result = O("ok", true, "projects", reports);
            }
            if (resultPath.Length > 0) FileBundle.AtomicWrite(resultPath, System.Text.Encoding.UTF8.GetBytes(result.ToJsonString(JsonOptions)));
            return 0;
        }
        catch (Exception ex)
        {
            string message = ex is BuildException ? ex.Message : ex is OperationCanceledException ? "操作已取消" : "操作失败，请检查路径、配置或依赖";
            if (resultPath.Length > 0) FileBundle.AtomicWrite(resultPath, System.Text.Encoding.UTF8.GetBytes(O("ok", false, "error", message).ToJsonString(JsonOptions)));
            return 1;
        }
    }
}

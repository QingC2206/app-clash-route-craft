using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public sealed record ConfigurationIssue(string Page, string Field, string Message);

public static class ProjectReadiness
{
    public static IReadOnlyList<ConfigurationIssue> Inspect(WorkspaceSession session, bool refresh = false, ValidationPaths? resources = null, bool checkDependencies = false, bool autoFetch = true)
        => ParameterInspection.Inspect(session, build: new BuildOptions(Force: refresh, Validation: resources, AutoFetch: autoFetch), checkDependencies: checkDependencies)
            .Where(x => x.State == "fail").Select(x => x.Issue ?? new ConfigurationIssue("yaml", "", x.Message)).ToArray();
}

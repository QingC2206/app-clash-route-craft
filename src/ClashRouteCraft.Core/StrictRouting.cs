using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

/// <summary>Explicit user rules followed by a proxy-only fallback.</summary>
public static class StrictRouting
{
    public static bool Enabled(JsonObject policy) => B(policy, "strict_mode");

    // Work on a clone: turning strict mode off must restore the cached subscription.
    // Keep node groups and hosts, including dialer-proxy dependencies.
    public static JsonObject WithoutSubscriptionRules(JsonObject document)
    {
        var result = (JsonObject)document.DeepClone();
        foreach (string key in new[] { "rules", "sub-rules", "rule-providers", "geodata-mode", "geodata-loader", "geox-url" }) result.Remove(key);
        return result;
    }

    public static void RequireRuleMode(JsonObject configuration, JsonObject policy)
    {
        if (Enabled(policy) && S(configuration, "mode", "rule") != "rule")
            throw new BuildException("严格模式需要 runtime.mode 为 rule，不能使用 direct / global 绕过分流规则");
    }

    public static void RequireProxyResidences(JsonArray nodes, JsonObject policy)
    {
        if (Enabled(policy) && nodes.OfType<JsonObject>().Any(x => !SubscriptionRouting.IsProxyNode(x)))
            throw new BuildException("严格模式的住宅出口必须是真实代理节点，不能使用 direct / reject 等特殊动作节点");
    }
}

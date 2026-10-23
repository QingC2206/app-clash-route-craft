using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    Control AsnPolicyCard()
    {
        var policy = session!.Policy;
        var inspection = session.LastAsnAnalysis ?? session.ReadReport()?["asn"] as JsonObject;
        var check = AsyncButton("保存并检查 ASN 来源", InspectAsnAsync); check.Name = "InspectAsnSources";
        check.IsEnabled = !IsBusy && !session.IsReadOnly;
        var retain = Check("保留 ASN（高级，目标设备需准备数据库）", AsnPolicy.AllowsNative(policy), value =>
        {
            Mutate("policy.yaml", doc => { doc["asn_policy"] ??= new JsonObject(); M(doc, "asn_policy")["allow_native"] = value; }); RenderPage();
        });
        retain.Name = "AllowNativeAsn"; retain.IsEnabled = !IsBusy && !session.IsReadOnly;
        string summary = inspection is null ? "尚未检查。生成时会检查主订阅、手工及附加规则；MC2 默认阻止未经处理的 ASN 依赖。"
            : $"最近检查：ASN {L(inspection, "sources").OfType<JsonObject>().Sum(x => I(x, "detected", 0))} 条，已排除 {I(inspection, "excluded_rules", 0)} 条，未检查远程规则集 {I(inspection, "uninspected_providers", 0)} 个。修改后请重新检查或生成。";
        var rows = Stack(Label(summary, true, UiMetrics.Supporting), Row(check), retain,
            Label("检查会保存草稿，不替换生成文件。排除会移除整条含 ASN 的规则，可能改变直连、代理或拦截结果；其他规则顺序不变。选择只在保存、生成后生效。", true, UiMetrics.Supporting));
        if (AsnPolicy.AllowsNative(policy)) rows.Children.Add(Note("已选择保留：本机校验通过不代表路由器已安装 ASN.mmdb。未检查的远程规则集仍会标为待核对，不能认定没有 ASN 依赖。"));
        if (inspection is not null)
        {
            var sources = L(inspection, "sources").OfType<JsonObject>().Where(x => I(x, "detected", 0) > 0 || L(x, "uninspected").Count > 0 || B(x, "exclude_selected")).ToArray();
            foreach (var source in sources)
            {
                string id = S(source, "id"), label = S(source, "label");
                var exclude = Check("排除此来源的 ASN 规则", AsnPolicy.Excludes(policy, id), value => { Mutate("policy.yaml", doc => AsnPolicy.SetExcluded(doc, id, value)); RenderPage(); });
                exclude.Name = "ExcludeAsnSource"; exclude.Tag = id; exclude.IsEnabled = !IsBusy && !session.IsReadOnly;
                var details = Stack(Label(label, true), Label($"ASN {I(source, "detected", 0)} 条 · 未检查规则集 {L(source, "uninspected").Count} 个", true, UiMetrics.Supporting), exclude);
                if (L(source, "uninspected").Count > 0 || L(source, "snapshots").Count > 0)
                    details.Children.Add(Label("远程完整规则集选择排除后，会读取并内嵌过滤后的快照；以后更新需重新生成，避免路由器重新下载原始 ASN 规则。", true, UiMetrics.Supporting));
                var preview = Stack();
                foreach (var raw in L(source, "rules").OfType<JsonObject>()) preview.Children.Add(Label(S(raw, "location") + "  " + S(raw, "rule"), true, UiMetrics.Supporting));
                foreach (var name in L(source, "uninspected")) preview.Children.Add(Label("待核对规则集：" + S(name), true, UiMetrics.Supporting));
                if (B(source, "details_truncated")) preview.Children.Add(Label("仅显示前 100 条；计数包括全部规则。", true, UiMetrics.Supporting));
                details.Children.Add(new Expander { Header = "查看受影响规则", HorizontalAlignment = HorizontalAlignment.Stretch, Content = preview });
                rows.Children.Add(Card("", details));
            }
        }
        var card = Card("ASN 依赖与兼容性", rows); card.Name = "AsnPolicyCard"; return card;
    }
    async Task InspectAsnAsync()
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        var inspected = session;
        if (inspected.Dirty) inspected.Save();
        jobCancellation?.Dispose(); jobCancellation = new(); var cancellation = jobCancellation.Token;
        taskStage = "检查 ASN 来源";
        job = Inspect(); SetBusy(true); RenderPage();
        try { await job; }
        finally { SetBusy(false); RenderPage(); UpdateFooter(); }
        async Task Inspect()
        {
            await Task.Yield();
            try
            {
                var result = await Task.Run(() => engine.BuildProjectAsync(inspected.Workspace, inspected.ProjectName,
                    new(AutoFetch: false, InspectAsnOnly: true), cancellation: cancellation), cancellation);
                inspected.LastAsnAnalysis = (JsonObject)M(result.Report, "asn").DeepClone();
                SetStatus(S(inspected.LastAsnAnalysis, "status") == "blocked" ? "发现尚未处理的 ASN 依赖，请按来源排除或选择高级保留" : "ASN 来源检查完成，生成文件保持不变");
            }
            catch (OperationCanceledException) { SetStatus("检查已取消，生成文件保持不变"); }
            catch (Exception ex) { SetStatus(SafeMessage(ex), true); }
        }
    }
    static string AsnTargetStatus(JsonObject report)
    {
        var asn = M(report, "asn");
        if (asn.Count == 0) return "未检查";
        if (B(asn, "native_required") || M(M(report, "mc2_package_validation"), "required_assets").ContainsKey("ASN.mmdb")) return "需准备 ASN.mmdb";
        if (I(asn, "uninspected_providers", 0) > 0) return "远程规则待核对";
        return "无 ASN 依赖";
    }
}

using ClashRouteCraft.Core;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    int pullingSource = -1;
    async Task PullSubscriptionAsync(int index)
    {
        if (IsBusy) { if (pullingSource == index) jobCancellation?.Cancel(); return; }
        if (session is null || session.IsReadOnly) return;
        var current = session;
        jobCancellation?.Dispose(); jobCancellation = new(); var cancellation = jobCancellation.Token;
        pullingSource = index; taskLog.Clear(); checkedInputs = ""; taskStage = "正在拉取订阅";
        job = Run(); SetBusy(true); RenderPage();
        try { await job; }
        finally { pullingSource = -1; SetBusy(false); RenderPage(); UpdateFooter(); }
        async Task Run()
        {
            await Task.Yield();
            try
            {
                var result = await Task.Run(() => SubscriptionUpdater.PullAsync(current, index, cancellation), cancellation);
                string message = result.Name + "：已拉取 " + result.NodeCount + " 个节点，" + result.EligibleCount + " 个符合筛选";
                taskStage = "订阅已拉取"; taskLog.Add(new("拉取订阅", message, "pass"));
                SetStatus(message); Notify(message);
            }
            catch (OperationCanceledException) { taskStage = "拉取已取消"; taskLog.Add(new("拉取已取消", "原有缓存保持不变", "warning")); SetStatus("拉取已取消，原有缓存保持不变"); }
            catch (Exception ex) { taskStage = "拉取失败"; taskLog.Add(new("拉取失败", SafeMessage(ex) + "；原有缓存保持不变", "fail")); SetStatus(SafeMessage(ex) + "；原有缓存保持不变", true); }
        }
    }
}

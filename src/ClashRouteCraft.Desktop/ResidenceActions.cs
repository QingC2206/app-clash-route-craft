using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    Button ResidenceCheckButton(int index)
    {
        var owner = session!;
        CancellationTokenSource? running = null;
        string checkedValue = "", state = "pending", message = "点击检测当前电脑到此住宅代理的连接与出口";
        bool attached = true;
        var results = new ContentControl { Name = "ResidenceCheckResults" + index, Width = UiMetrics.ProbeFlyoutWidth };
        var flyout = new Flyout { Content = results, Placement = PlacementMode.BottomEdgeAlignedRight, ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway };
        flyout.FlyoutPresenterClasses.Add("rule-check");
        Button? button = null;
        string Current()
        {
            if (session != owner || index >= owner.Residences.Count) return "";
            var node = owner.Residences[index];
            try { return Canonical(ExpandEnvironment(node)); }
            catch (BuildException) { return Canonical(node) + ":missing-environment"; }
        }
        void Render()
        {
            if (running is null && checkedValue.Length > 0 && checkedValue != Current())
            { state = "pending"; message = "配置已修改，请重新检测"; checkedValue = ""; }
            var row = new Grid { ColumnDefinitions = new($"*,{UiMetrics.StatusColumn}"), ColumnSpacing = UiMetrics.Gap };
            row.Children.Add(Label(message)); var icon = CheckStatusIcon(state, message); row.Children.Add(icon); Grid.SetColumn(icon, 1);
            results.Content = Stack(row, Label("通过此住宅代理访问 Cloudflare 检测服务；不保存配置。未测试机场中转、UDP 或手机/路由器链路。", true, UiMetrics.Supporting));
        }
        async Task Run()
        {
            if (running is not null) { running.Cancel(); message = "正在取消…"; Render(); flyout.ShowAt(button!); return; }
            if (IsBusy || session != owner) return;
            checkedValue = Current();
            var node = Map(owner.Residences[index]!.DeepClone(), "住宅");
            running = new(); var cancellation = running.Token;
            state = "pending"; message = "正在检测连接、代理握手与出口…再次点击可取消";
            AutomationProperties.SetName(button!, "取消住宅连通性检测"); ToolTip.SetTip(button!, null); Render(); flyout.ShowAt(button!);
            try
            {
                var result = await ResidenceProbe.CheckAsync(node, cancellation);
                state = result.State; message = result.Message;
            }
            catch (OperationCanceledException) { state = "warning"; message = "检测已取消"; }
            catch (Exception ex) { state = "fail"; message = SafeMessage(ex); }
            finally
            {
                running.Dispose(); running = null;
                if (attached)
                {
                    AutomationProperties.SetName(button!, "检测住宅连通性"); Render();
                }
            }
        }
        button = IconButton("CheckResidence" + index, "检测住宅连通性", "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M4,8 L7,11 L12,5", Run);
        Render(); FlyoutBase.SetAttachedFlyout(button, flyout);
        button.PointerEntered += (_, _) => { Render(); ToolTip.SetTip(button, null); ToolTip.SetIsOpen(button, false); flyout.ShowAt(button); };
        button.DetachedFromVisualTree += (_, _) => { attached = false; running?.Cancel(); flyout.Hide(); };
        return button;
    }
}

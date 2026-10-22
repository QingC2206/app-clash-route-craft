using Avalonia;
using Avalonia.Controls;
using ClashRouteCraft.Core;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    Control ResourceSettings()
    {
        var rows = Stack();
        foreach (var item in RuntimeResources.Catalog)
            rows.Children.Add(Label(item.Name + (RuntimeResources.Present(preferences.Workspace, item) ? " · 已就绪" : " · 缺失"), true, 12));
        var download = AsyncButton("下载缺失资源…", () => ShowResourcesAsync()); download.Name = "DownloadRuntimeResources";
        var restore = Button("恢复缺失默认配置", () => Try(() =>
        {
            int count = BuiltInDefaults.Ensure(preferences.Workspace).Count;
            if (session?.IsReadOnly == true) session = new WorkspaceSession(preferences.Workspace, WorkspaceSession.ExampleName);
            RenderPage(); SetStatus(count > 0 ? $"已恢复 {count} 个默认文件；已有配置未覆盖。" : "默认配置完整，无需恢复。");
        })); restore.Name = "RestoreBuiltInDefaults";
        return Card("默认配置与校验资源", Label("内置通用 Example，默认沿用主订阅规则，DNS 由客户端管理。启动时补齐缺失模板，保留现有项目与自定义默认规则。", true, 12),
            rows, Label("Mihomo 与 GeoData 按需从官方仓库下载；不提供 MC2 安装包。稍后下载不影响编辑，生成校验时需要这些资源。", true, 12), Row(download, restore));
    }
    async Task ShowResourcesAsync()
    {
        if (IsBusy) return;
        string workspace = preferences.Workspace;
        var dialog = new Window { Title = "下载校验资源", Width = 680, Height = 520, MinWidth = 550, MinHeight = 420, Background = WindowSurface, Foreground = Ink, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        using var cancellation = new CancellationTokenSource(); bool downloading = false;
        dialog.Closed += (_, _) => cancellation.Cancel();
        var summary = Label("", true); summary.Name = "RuntimeResourcesSummary";
        var state = Label("选择下载后才会联网。下载完成会校验完整性；已有资源不会覆盖。", true); state.Name = "RuntimeResourcesStatus";
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 5, IsVisible = false };
        Button start = null!, close = null!;
        void Refresh()
        {
            var missing = RuntimeResources.Missing(workspace);
            summary.Text = missing.Count == 0 ? "资源已完整，可以开始生成和校验。" : "缺少：" + string.Join("、", missing.Select(x => x.Name));
            start.IsEnabled = missing.Count > 0 && !downloading;
        }
        start = AsyncButton("下载缺失资源", async () =>
        {
            if (downloading || cancellation.IsCancellationRequested) return;
            downloading = true; start.IsEnabled = false; bar.IsVisible = true; bar.IsIndeterminate = true; close.Content = "取消下载";
            try
            {
                var progress = new Progress<ResourceProgress>(p => { state.Text = p.Message; bar.Maximum = p.Total; bar.Value = p.Completed; });
                int count = await RuntimeResources.DownloadMissingAsync(workspace, progress, cancellation.Token);
                state.Text = $"已安装 {count} 个缺失资源。配置和节点未修改。"; close.Content = "完成";
            }
            catch (OperationCanceledException) { state.Text = "已取消；现有资源保持不变。"; }
            catch (Exception ex) { state.Text = SafeMessage(ex); close.Content = "关闭"; }
            finally { downloading = false; bar.IsVisible = false; Refresh(); }
        }); start.Name = "ConfirmRuntimeResourceDownload";
        close = Button("稍后", () => dialog.Close()); close.Name = "CloseRuntimeResources";
        dialog.Content = Stack(summary,
            Label("来源：MetaCubeX/mihomo（固定版本）与 MetaCubeX/meta-rules-dat（当前 GeoData）。使用系统网络设置；下载失败可检查网络或手动放入下方目录。", true, 12),
            Label(Path.Combine(workspace, "resources"), true, 12), state, bar, Row(start, close));
        DialogShell.Apply(dialog, "补齐本地校验资源", Ink, Line, BackgroundBrush); Refresh();
        await dialog.ShowDialog(this); if (CurrentPage == "settings") RenderPage();
    }
}

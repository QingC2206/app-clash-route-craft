using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AvaloniaEdit;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    readonly StackPanel notifications = new() { Name = "Notifications", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(16), Spacing = 8, Width = 320 };
    readonly List<(DateTime Time, string Message)> notificationHistory = [];
    void InitializeNotifications() { Closed += (_, _) => notifications.Children.Clear(); }
    void Notify(string message)
    {
        if (notificationHistory.LastOrDefault().Message == message && notificationHistory.LastOrDefault().Time > DateTime.Now.AddSeconds(-2)) return;
        notificationHistory.Add((DateTime.Now, message));
        if (notificationHistory.Count > 100) notificationHistory.RemoveAt(0);
        var close = IconButton("DismissNotice", "关闭提示", "M3,3 L13,13 M13,3 L3,13", () => Task.CompletedTask);
        var row = new Grid { ColumnDefinitions = new("*,Auto") }; row.Children.Add(Label(message)); row.Children.Add(close); Grid.SetColumn(close, 1);
        var notice = Card("", row); notice.Name = "Notice";
        while (notifications.Children.Count >= 3) notifications.Children.RemoveAt(0);
        notifications.Children.Add(notice);
        bool dismissed = false;
        void Dismiss() { dismissed = true; notifications.Children.Remove(notice); }
        close.Click += (_, _) => Dismiss(); notice.DetachedFromVisualTree += (_, _) => dismissed = true;
        _ = ExpireAsync();
        async Task ExpireAsync()
        {
            int ticks = 0;
            while (!dismissed)
            {
                await Task.Delay(250);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!dismissed && !notice.IsPointerOver && !notice.IsKeyboardFocusWithin && ++ticks >= 20) Dismiss();
                });
            }
        }
    }
    async void ShowNotificationHistory()
    {
        var rows = new StackPanel { Spacing = 12 };
        foreach (var item in notificationHistory.AsEnumerable().Reverse())
        {
            var copy = AsyncButton("复制", async () => { if (Clipboard is not null) await Clipboard.SetTextAsync(item.Message); });
            // Row command, not a dialog action: TextBlock content keeps DialogShell from hoisting it into the bottom bar.
            copy.Content = new TextBlock { Text = "复制" };
            rows.Children.Add(Stack(Label(item.Time.ToString("HH:mm:ss"), true, 12), Label(item.Message), copy));
        }
        var dialog = new Window { Title = "通知记录", Width = 560, Height = 460, Foreground = Ink, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (notificationHistory.Count == 0)
            dialog.Content = DialogEmptyState("通知记录", "暂无通知。操作失败的提示会保留在通知记录中。");
        else
            // The shell stacks title above content, so bound the list instead of letting it grow past the window.
            dialog.Content = new ScrollViewer { Content = rows, MaxHeight = dialog.Height - 120, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        DialogShell.Apply(dialog, notificationHistory.Count == 0 ? null : "通知记录", Ink, Line, BackgroundBrush);
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) dialog.Close(); };
        await dialog.ShowDialog(this);
    }
    void ApplyReadOnlyState()
    {
        if (session is null || (!session.IsReadOnly && !IsBusy) || CurrentPage == "settings" || body.Content is not Control content) return;
        foreach (var control in content.GetLogicalDescendants().OfType<Control>())
            switch (control)
            {
                case TextBox text: text.IsReadOnly = true; break;
                case TextEditor editor: editor.IsReadOnly = true; break;
                case ComboBox or CheckBox or NumericUpDown: control.IsEnabled = false; break;
                case Button button when IsBusy:
                    if (button is not Avalonia.Controls.Primitives.ToggleButton && button.Name != "CancelTask" && button.Name != "PullSource" + pullingSource) button.IsEnabled = false;
                    break;
                case Button button when button.Name is not "PolicyRulesTab" and not "PolicyDnsTab" and not "PolicyRuleSetsTab" and not "BrowseRuleSets" and not "BackToRules" && button.Name?.StartsWith("ViewRuleSet") != true && button is not Avalonia.Controls.Primitives.ToggleButton && button.Name?.StartsWith("EditSource") != true && button.Name?.StartsWith("EditResidence") != true && button.Name?.StartsWith("CollapseUrls_") != true:
                    button.IsEnabled = false; break;
            }
    }
}

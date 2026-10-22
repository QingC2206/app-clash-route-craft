using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ClashRouteCraft.Desktop;

// Shared dialog chrome used by every modal in the app:
//   [ title ]
//   content
//   ---------------------------------
//                   [ secondary actions ] [ danger / primary ] [ 取消 ]   <- cancel rightmost (CHROME-009)
// The caller keeps its own title, size, button labels and click handlers; this only
// rebuilds the visual order and adds the title line / action bar. Empty-state dialogs
// can pass title: null to let their own composed empty state carry the heading.
//
// Apply() may be called before ShowDialog(): the window template (and therefore the
// buttons) only exists after the window is opened, so the rebuild is deferred to Opened.
internal static class DialogShell
{
    public static void Apply(Window dialog, string? title, IBrush ink, IBrush line, IBrush background)
    {
        dialog.Background = background;
        if (dialog.GetVisualDescendants().OfType<Button>().Any())
        {
            Rebuild(dialog, title, ink, line);
            return;
        }
        void OnOpened(object? sender, EventArgs e)
        {
            dialog.Opened -= OnOpened;
            Rebuild(dialog, title, ink, line);
        }
        dialog.Opened += OnOpened;
    }

    static void Rebuild(Window dialog, string? title, IBrush ink, IBrush line)
    {
        var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string).ToArray();
        var cancel = buttons.FirstOrDefault(b => (b.Content?.ToString() ?? "").Contains("取消"));
        var body = dialog.Content as Control;
        if (body is null) return;
        if (dialog.Content is DockPanel existing && existing.Children.OfType<Border>().Any(b => Equals(b.Tag, "dialog-bar"))) return;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        bool primaryAssigned = false;
        foreach (var button in buttons)
        {
            string text = button.Content?.ToString() ?? "";
            if (ReferenceEquals(button, cancel)) continue;
            bool danger = text.Contains("删除") || text.Contains("移入回收站") || text.Contains("清空") || text.Contains("移除");
            if (danger)
            {
                button.Classes.Add("danger");
                button.Classes.Remove("primary");
            }
            else if (!primaryAssigned)
            {
                button.Classes.Add("primary");
                primaryAssigned = true;
            }
            if (button.Parent is Panel owner) owner.Children.Remove(button);
            actions.Children.Add(button);
        }
        if (cancel is not null)
        {
            if (cancel.Parent is Panel owner) owner.Children.Remove(cancel);
            actions.Children.Add(cancel);
        }

        dialog.Content = null;
        var content = new StackPanel();
        if (!string.IsNullOrWhiteSpace(title))
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Foreground = ink,
                Margin = new Thickness(0, 0, 0, 10),
            });
        content.Children.Add(body);

        var bodyBorder = new Border { Padding = new Thickness(20, 16, 20, 0), Child = content };
        var root = new DockPanel { LastChildFill = true };
        if (actions.Children.Count > 0)
        {
            var bar = new Border
            {
                Tag = "dialog-bar",
                BorderBrush = line,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 12, 0, 0),
                Margin = new Thickness(20, 14, 20, 16),
                Child = actions,
            };
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
        }
        else
        {
            bodyBorder.Padding = new Thickness(20);
        }
        root.Children.Add(bodyBorder);
        dialog.Content = root;
    }
}

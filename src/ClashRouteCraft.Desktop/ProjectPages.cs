using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClashRouteCraft.Core;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow
{
    void RefreshProjects(string? preferred = null)
    {
        lastError = ""; taskStage = "待生成"; taskLog.Clear(); expandedSource = expandedResidence = -1;
        var projects = new[] { WorkspaceSession.ExampleName }.Concat(BuildEngine.DiscoverProjects(preferences.Workspace)).ToArray();
        string? chosen = preferred ?? session?.ProjectName;
        chosen = projects.FirstOrDefault(x => x == chosen) ?? projects.FirstOrDefault(x => x != WorkspaceSession.ExampleName) ?? WorkspaceSession.ExampleName;
        selectedProjectName = chosen; session = null;
        if (chosen is not null)
            try { session = new(preferences.Workspace, chosen); }
            catch (BuildException ex) { SetStatus(ex.Message, true); }
        if (session is not null) preferences.RememberProject(preferences.Workspace, session.ProjectName);
        changingProject = true; projectPicker.ItemsSource = projects; projectPicker.SelectedItem = selectedProjectName; changingProject = false;
        UpdateChrome();
    }
    async Task ShowTrashAsync()
    {
        if (IsBusy) return;
        var dialog = new Window { Title = "项目回收站", Width = 520, Height = 420, Foreground = Ink, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var rows = new StackPanel { Spacing = 8 };
        var deleted = ProjectStore.DeletedProjects(preferences.Workspace);
        foreach (var item in deleted)
        {
            var restore = AsyncButton("恢复", async () =>
            {
                dialog.Close();
                if (!await CanLeaveAsync()) return;
                string? name = await PromptAsync("恢复项目", "恢复后的项目名称", item.Name, "恢复", ValidateProjectName);
                if (name is null) return;
                ProjectStore.Restore(preferences.Workspace, item.Archive, name); RefreshProjects(name); Navigate("overview");
            }); restore.Tag = "restore-project:" + item.Name;
            // Row command, not a dialog action: TextBlock content keeps DialogShell from hoisting it into the bottom bar.
            restore.Content = new TextBlock { Text = "恢复" };
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            row.Children.Add(Stack(Label(item.Name), Label(LocalTime(item.DeletedAt), true, 12))); row.Children.Add(restore); Grid.SetColumn(restore, 1); rows.Children.Add(row);
        }
        if (deleted.Count == 0)
        {
            dialog.Content = DialogEmptyState("项目回收站", "这里没有可恢复的项目。删除项目后可在回收站恢复。");
        }
        else
        {
            // The shell stacks title above content, so bound the list instead of letting it grow past the window.
            dialog.Content = new ScrollViewer { Content = rows, MaxHeight = dialog.Height - 120, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        }
        DialogShell.Apply(dialog, deleted.Count == 0 ? null : "项目回收站", Ink, Line, BackgroundBrush);
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) dialog.Close(); };
        await dialog.ShowDialog(this);
    }
    Task RenameProjectAsync() => RenameProjectAsync(selectedProjectName);
    async Task RenameProjectAsync(string? oldName)
    {
        if (IsBusy || oldName is null || WorkspaceSession.IsExample(oldName) || !await CanLeaveAsync()) return;
        string? name = await PromptAsync("重命名项目", "新项目名称", oldName, "保存名称", value => ProjectStore.ValidateDestination(preferences.Workspace, value, oldName));
        if (name is null || name == oldName) return;
        Try(() => { ProjectStore.Rename(preferences.Workspace, oldName, name); RefreshProjects(name); RenderPage(); SetStatus($"已将 {oldName} 重命名为 {name}"); });
    }
    Task DeleteProjectAsync() => DeleteProjectAsync(selectedProjectName);
    async Task DeleteProjectAsync(string? name)
    {
        if (IsBusy || name is null || WorkspaceSession.IsExample(name) || !await CanLeaveAsync()) return;
        if (!await ConfirmAsync("删除项目", $"将项目“{name}”及其生成结果移入回收站。可从选择框下方的回收站恢复。", "移入回收站")) return;
        Try(() => { ProjectStore.Delete(preferences.Workspace, name); RefreshProjects(); Navigate("overview"); SetStatus("项目已移入回收站，可从回收站恢复"); });
    }

    // Standard composed empty state (EMPTY-001/002): icon base + title + one-line explanation, centered.
    Control DialogEmptyState(string title, string description) => new StackPanel
    {
        Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new Border
            {
                Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = Active, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M2,6 L14,6 M6,3 L10,3 M4,6 L5,14 L11,14 L12,6"), Stroke = Muted, StrokeThickness = 1.6,
                    Width = 24, Height = 24, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            },
            new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ink, Margin = new Thickness(0, 6, 0, 0) },
            new TextBlock { Text = description, FontSize = 12, MaxWidth = 380, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Muted }
        }
    };
}

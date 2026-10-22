using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Desktop;

public sealed partial class MainWindow : Window
{
    public static readonly string[] PageIds = ["overview", "sources", "residential", "policy", "yaml", "build", "report", "settings"];
    static readonly string[] PageNames = ["项目概览", "订阅与入口", "住宅出口", "规则与 DNS", "高级 YAML", "生成配置", "结果与使用", "应用设置"];
    readonly AppPreferences preferences;
    readonly BuildEngine engine = new();
    readonly ContentControl body = new();
    readonly ContentControl flowHost = new(), feedbackHost = new(), policyTabsHost = new();
    readonly TextBlock heading = new(), subtitle = new(), status = new(), projectStatus = new();
    readonly ComboBox projectPicker = new() { Name = "CurrentProject", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly Button addProject, deleteProject, copyProject, renameProject, trashProject, dnsNavigation;
    readonly StackPanel navigation = new() { Spacing = 4 };
    readonly List<Button> navButtons = [];
    readonly Button saveButton;
    readonly Border sidebar = new(), mainSurface = new(), footer = new();
    readonly List<ParameterCheck> taskLog = [];
    CancellationTokenSource? jobCancellation;
    Task? job;
    WorkspaceSession? session;
    string? selectedProjectName;
    bool changingProject, allowClose, handlingClose;
    string policyTab = "rules", yamlFile = "settings.yaml", previewFile = "HomeChain.yaml";
    string taskStage = "待生成", taskMessage = "", lastError = "";
    double taskPercent;
    bool previewSecrets;
    TextBlock? liveStage;
    ProgressBar? liveProgress;
    StackPanel? liveLog;
    public string CurrentPage { get; private set; } = "overview";
    public WorkspaceSession? Session => session;
    public bool IsBusy => job is { IsCompleted: false };
    bool Dark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
    IBrush WindowSurface => Brush.Parse(Dark ? "#12181F" : "#F1F4F9");
    IBrush Panel => Brush.Parse(Dark ? "#1B2330" : "#FFFFFF");
    IBrush Surface => Brush.Parse(Dark ? "#202A38" : "#FFFFFF");
    IBrush BackgroundBrush => WindowSurface;
    IBrush Line => Brush.Parse(Dark ? "#33404F" : "#DFE6EF");
    IBrush InputLine => Brush.Parse(Dark ? "#7D869C" : "#8B95A5");
    IBrush Separator => Brush.Parse(Dark ? "#2A3542" : "#D7DFE9");
    IBrush Muted => Brush.Parse(Dark ? "#AEBACB" : "#5B6979");
    IBrush Ink => Brush.Parse(Dark ? "#E8EEF6" : "#1C2735");
    IBrush Accent => Brush.Parse(Dark ? "#9CBDFF" : "#285CC8");
    IBrush PrimaryFill => Brush.Parse("#285CC8");
    IBrush Active => Brush.Parse(Dark ? "#25395C" : "#E8EFFB");
    IBrush NoteSurface => Brush.Parse(Dark ? "#1C2941" : "#EEF3FC");
    IBrush IconInk => Brush.Parse(Dark ? "#C3CEE0" : "#3D4B5F");
    IBrush Danger => Brush.Parse(Dark ? "#FF9A9A" : "#B02A37");
    IBrush DangerLine => Brush.Parse(Dark ? "#7A4A4E" : "#D9A7AC");

    public MainWindow(AppPreferences? preferences = null, string? initialWorkspace = null)
    {
        this.preferences = preferences ?? new();
        Title = "ClashRouteCraft · 配置工作台"; Width = 1220; Height = 850; MinWidth = 940; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyTheme();
        heading.FontSize = UiMetrics.PageTitle; heading.FontWeight = FontWeight.SemiBold; heading.TextWrapping = TextWrapping.Wrap;
        subtitle.FontSize = UiMetrics.Supporting; subtitle.TextWrapping = TextWrapping.Wrap; subtitle.Foreground = Muted;
        saveButton = Button("保存", SaveAll, true); saveButton.Name = "SaveProject"; saveButton.Classes.Remove("primary"); saveButton.Classes.Add("primary");
        var logo = new Border { Tag = "accent-surface", Width = 34, Height = 34, CornerRadius = new(9), Background = Active, Child = new TextBlock { Tag = "accent", Text = "C", Foreground = Accent, FontSize = 21, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var brand = new StackPanel { Spacing = 1, Children = { Label("ClashRouteCraft", size: 17), Label("桌面配置工作台", true, UiMetrics.Supporting) } };
        var brandGrid = new Grid { ColumnDefinitions = new("44,*"), Children = { logo, brand } }; Grid.SetColumn(brand, 1);
        var sideContent = new DockPanel { LastChildFill = true, Margin = new(14, 20) };
        addProject = IconButton("NewProject", "新增项目", "M8,1 L8,15 M1,8 L15,8", NewProjectAsync);
        deleteProject = IconButton("DeleteProject", "删除当前项目", "M2,4 L14,4 M6,1 L10,1 L11,4 M4,4 L5,15 L11,15 L12,4 M7,7 L7,12 M9,7 L9,12", DeleteProjectAsync);
        copyProject = IconButton("CopyProject", "复制当前项目", "M5,5 L15,5 L15,15 L5,15 Z M1,11 L1,1 L11,1", CopyProjectAsync);
        renameProject = IconButton("RenameProject", "重命名当前项目", "M2,11 L11,2 L14,5 L5,14 L1,15 Z M9,4 L12,7", RenameProjectAsync);
        trashProject = IconButton("ProjectTrash", "项目回收站", "M2,5 A6,6 0 1 1 2,11 M1,1 L1,6 L6,6 M8,4 L8,8 L11,10", ShowTrashAsync);
        var projectControls = new StackPanel { Name = "ProjectActions", Orientation = Orientation.Horizontal, Spacing = 6, Children = { addProject, copyProject, renameProject, deleteProject, trashProject } };
        var top = Stack(brandGrid, Gap(12), Label("当前项目", true, 12), projectPicker, projectControls); top.Spacing = 8;
        DockPanel.SetDock(top, Dock.Top); sideContent.Children.Add(top);
        for (int i = 0; i < PageIds.Length; i++)
        {
            string id = PageIds[i]; var button = Button(PageNames[i], () => { if (id == "report") NavigateOutput("HomeChain.yaml"); else Navigate(id); }); button.Name = "Nav_" + id; button.Classes.Add("nav"); navButtons.Add(button);
        }
        navButtons[6].Content = "主配置输出";
        dnsNavigation = Button("DNS配置输出(MC2专用)", () => NavigateOutput("HomeChain.MC2DNS.yaml")); dnsNavigation.Name = "Nav_dns"; dnsNavigation.Classes.Add("nav");
        var settingsIcon = new PathIcon { Data = Geometry.Parse("M7,0 L9,0 L10,3 L12,4 L15,4 L16,6 L14,8 L14,10 L16,12 L15,14 L12,14 L10,15 L9,18 L7,18 L6,15 L4,14 L1,14 L0,12 L2,10 L2,8 L0,6 L1,4 L4,4 L6,3 Z M8,6 A3,3 0 1 0 8,12 A3,3 0 1 0 8,6"), Width = 16, Height = 16, Foreground = IconInk };
        navButtons[7].Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { settingsIcon, new TextBlock { Text = "应用设置", VerticalAlignment = VerticalAlignment.Center } } };
        var bottom = Stack(navButtons[7]); bottom.Margin = new(0, 12, 0, 0); DockPanel.SetDock(bottom, Dock.Bottom); sideContent.Children.Add(bottom);
        navigation.Children.Add(Label("配置项目", true, UiMetrics.Caption));
        foreach (int i in new[] { 0, 1, 2, 3 }) navigation.Children.Add(navButtons[i]);
        navigation.Children.Add(Gap()); navigation.Children.Add(Label("生成与使用", true, UiMetrics.Caption));
        navigation.Children.Add(navButtons[5]); navigation.Children.Add(navButtons[6]); navigation.Children.Add(dnsNavigation);
        navigation.Margin = new(0, 16, 0, 0); sideContent.Children.Add(new ScrollViewer { Content = navigation, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        sidebar.Child = sideContent; sidebar.Background = Panel; sidebar.BorderBrush = Separator; sidebar.BorderThickness = new(0, 0, 1, 0);
        mainSurface.Background = WindowSurface;
        var header = new Grid { Name = "PageHeader", ColumnDefinitions = new("*,Auto"), Margin = new(24, 14, 24, 10) };
        header.Children.Add(Stack(heading, subtitle)); header.Children.Add(saveButton); Grid.SetColumn(saveButton, 1); saveButton.VerticalAlignment = VerticalAlignment.Top;
        var right = new DockPanel();
        flowHost.Name = "RelationshipMap"; flowHost.Margin = new(24, 14, 24, 0); DockPanel.SetDock(flowHost, Dock.Top); right.Children.Add(flowHost);
        DockPanel.SetDock(header, Dock.Top); right.Children.Add(header);
        policyTabsHost.Name = "PolicyTabBar"; policyTabsHost.Margin = new(24, 0, 24, 10); policyTabsHost.IsVisible = false;
        DockPanel.SetDock(policyTabsHost, Dock.Top); right.Children.Add(policyTabsHost);

        body.Margin = new(24, 0, 24, 20);
        right.Children.Add(new ScrollViewer { Name = "PageScroll", Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); mainSurface.Child = right;
        var columns = new Grid { ColumnDefinitions = new("216,*"), Children = { sidebar, mainSurface } }; Grid.SetColumn(mainSurface, 1);
        var footerGrid = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new(16, 8) }; projectStatus.FontSize = UiMetrics.Supporting; projectStatus.Foreground = Muted; status.FontSize = UiMetrics.Supporting; status.Foreground = Muted; status.TextWrapping = TextWrapping.Wrap; status.HorizontalAlignment = HorizontalAlignment.Right;
        footerGrid.Children.Add(projectStatus); footerGrid.Children.Add(status); Grid.SetColumn(status, 1); footer.Child = footerGrid; footer.Background = Panel; footer.BorderBrush = Separator; footer.BorderThickness = new(0, 1, 0, 0);
        var shell = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer); shell.Children.Add(columns); var overlay = new Grid { Children = { shell, notifications } }; Content = overlay; InitializeNotifications();
        projectPicker.SelectionChanged += async (_, _) =>
        {
            if (changingProject || projectPicker.SelectedItem is not string selected || selected == session?.ProjectName) return;
            if (await CanLeaveAsync()) Try(() => SelectProject(selected)); else UpdateProjectPicker();
        };
        Closing += async (_, e) =>
        {
            if (allowClose || (!IsBusy && session?.Dirty != true)) return;
            e.Cancel = true; if (handlingClose) return; handlingClose = true;
            await Task.Yield();
            try
            {
                if (IsBusy)
                {
                    if (await ConfirmAsync("生成任务仍在运行", "取消任务并等待文件保护处理完成后关闭？", "取消任务并关闭") != true) return;
                    jobCancellation?.Cancel(); if (job is not null) await job;
                }
                if (!await CanLeaveAsync()) return; allowClose = true; Close();
            }
            finally { handlingClose = false; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SaveAll(); e.Handled = true; } };
        ActualThemeVariantChanged += (_, _) => { UpdateChrome(); RenderPage(); };
        string candidate = initialWorkspace ?? (this.preferences.IsPortable ? this.preferences.ApplicationDirectory : this.preferences.Workspace);
        string? startupError = null;
        try
        {
            if (candidate.Length > 0 && Directory.Exists(candidate)) OpenWorkspace(candidate);
        }
        catch (Exception ex)
        {
            startupError = SafeMessage(ex);
            if (initialWorkspace is null && this.preferences.IsPortable)
            {
                this.preferences.Workspace = this.preferences.ApplicationDirectory;
                try { RefreshProjects(); } catch (Exception) { session = null; }
            }
        }
        UpdateChrome(); Navigate(session is null ? "overview" : PageIds.Contains(this.preferences.RecentPage) ? this.preferences.RecentPage : "overview");
        if (startupError is not null) SetStatus(startupError, true);
        if (startupError is null && this.preferences.IsPortable && initialWorkspace is null)
            Opened += async (_, _) =>
            {
                try { if (RuntimeResources.Missing(this.preferences.Workspace).Count > 0) await ShowResourcesAsync(); }
                catch (Exception ex) { SetStatus(SafeMessage(ex), true); }
            };
    }
    public void OpenWorkspace(string workspace)
    {
        if (IsBusy) throw new BuildException("生成结束后才能切换工作区");
        workspace = Path.GetFullPath(workspace); if (!Directory.Exists(workspace)) throw new BuildException("工作区目录不存在");
        var restored = BuiltInDefaults.Ensure(workspace);
        preferences.Workspace = workspace; preferences.Save(); RefreshProjects(preferences.RecentProject(workspace));
        UpdateChrome(); RenderPage(); SetStatus(restored.Count > 0 ? $"已打开工作区，补齐 {restored.Count} 个默认配置文件。" : "已打开工作区");
    }
    public void SelectProject(string project)
    {
        if (IsBusy) throw new BuildException("生成结束后才能切换项目");
        selectedProjectName = project; session = null; forceSubscriptions = false;
        try { session = new(preferences.Workspace, project); } catch (BuildException ex) { Notify(ex.Message); }
        preferences.RememberProject(preferences.Workspace, project); lastError = ""; checkedInputs = ""; taskStage = "待生成"; taskLog.Clear(); expandedSource = expandedResidence = -1; UpdateProjectPicker(); UpdateChrome(); RenderPage(); SetStatus("已切换到 " + project);
    }
    void UpdateProjectPicker() { changingProject = true; projectPicker.SelectedItem = selectedProjectName; changingProject = false; }
    public void Navigate(string page)
    {
        if (!PageIds.Contains(page)) return;
        if (IsBusy && page is not "build" and not "report") { SetStatus("当前生成完成后可继续编辑", true); return; }
        CurrentPage = page; feedbackHost.Content = null;
        if (page is not "settings") preferences.RecentPage = page;
        RenderPage();
    }
    void RenderPage()
    {
        liveStage = null; liveProgress = null; liveLog = null;
        policyTabsHost.IsVisible = false; policyTabsHost.Content = null;
        int index = Array.IndexOf(PageIds, CurrentPage); heading.Text = PageNames[index]; subtitle.Text = session is null ? "选择工作区，或创建第一个配置项目。" : session.ProjectName + (session.IsReadOnly ? " · 只读示例" : "") + "  /  " + PageNames[index];
        bool outputPage = CurrentPage == "report";
        if (outputPage && CurrentPage == "report") { heading.Text = previewFile == "HomeChain.MC2DNS.yaml" ? "DNS配置输出(MC2专用)" : "主配置输出"; subtitle.Text = session?.ProjectName + "  /  " + heading.Text; }
        foreach (var (button, i) in navButtons.Select((b, i) => (b, i))) { bool selected = i == 6 ? outputPage && previewFile != "HomeChain.MC2DNS.yaml" : i == index; StyleNav(button, selected); }
        bool dnsSelected = outputPage && previewFile == "HomeChain.MC2DNS.yaml"; StyleNav(dnsNavigation, dnsSelected);
        try
        {
            if (session is null && CurrentPage is not "settings") { body.Content = selectedProjectName is null ? Welcome() : Card("项目配置不完整", Label("可通过左侧项目操作重命名或移入回收站，恢复后再选择此项目。")); UpdateFooter(); return; }
            body.Content = CurrentPage switch { "overview" => OverviewPage(), "sources" => SourcesPage(), "residential" => ResidencesPage(), "policy" => PolicyPage(), "yaml" => YamlPage(), "build" => BuildPage(), "report" => ReportPage(), "settings" => SettingsPage(), _ => Welcome() };
        }
        catch (Exception ex) { policyTabsHost.IsVisible = false; body.Content = Stack(Card("配置需要修复", Label(SafeMessage(ex)), Button("打开高级 YAML", () => Navigate("yaml"))), Note("原始文件保持不变，修复后保存再生成。")); }
        UpdateFooter(); ApplyReadOnlyState(); Dispatcher.UIThread.Post(ApplyReadOnlyState, DispatcherPriority.Loaded);
    }
    Control Welcome() => Stack(Card("接入现有配置", Label("选择包含 config/ 的旧工作目录，将项目配置、输出和缓存迁入 EXE 所在目录。同名数据不会覆盖。"), Gap(8), AsyncButton("迁入已有工作区…", PickWorkspaceAsync, true)), Card("开始新项目", Label("通过左侧当前项目旁的 ＋ 创建住宅链配置项目。"), Gap(8), AsyncButton("创建项目…", NewProjectAsync)));
    void UpdateChrome()
    {
        Background = WindowSurface; Foreground = Ink; sidebar.Background = Panel; sidebar.BorderBrush = Separator; mainSurface.Background = WindowSurface; footer.Background = Panel; footer.BorderBrush = Separator;
        subtitle.Foreground = Muted; projectStatus.Foreground = Muted; UpdateFooter();
        foreach (var label in this.GetLogicalDescendants().OfType<TextBlock>())
            if (label.Tag is string role) label.Foreground = role switch { "muted" => Muted, "accent" => Accent, "ink" => Ink, _ => label.Foreground };
        foreach (var notice in notifications.Children.OfType<Border>()) { notice.Background = Surface; notice.BorderBrush = Line; }
        foreach (var icon in this.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(x => Equals(x.Tag, "accent-stroke"))) icon.Stroke = IconInk;
        foreach (var border in this.GetLogicalDescendants().OfType<Border>().Where(x => Equals(x.Tag, "accent-surface"))) border.Background = Active;
    }
    void StyleNav(Button button, bool selected)
    {
        button.Tag = selected ? "selected" : "idle";
        button.Background = selected ? Active : Brushes.Transparent;
        button.Foreground = selected ? Ink : Ink;
        button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        button.BorderThickness = new Thickness(selected ? 2 : 0, 0, 0, 0);
        button.BorderBrush = Accent;
        button.Padding = new Thickness(12, 7);
        button.MinHeight = 34;
        button.CornerRadius = new CornerRadius(4);
        foreach (var path in button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()) path.Stroke = selected ? Accent : IconInk;
        foreach (var icon in button.GetLogicalDescendants().OfType<PathIcon>()) icon.Foreground = selected ? Accent : IconInk;
    }
    void UpdateFooter()
    {
        projectStatus.Text = session is null ? "尚未选择项目" : session.ProjectName + " · " + OutputState();
        bool editablePage = CurrentPage is "overview" or "sources" or "residential" or "policy" or "yaml";
        bool dirty = session is { IsReadOnly: false, Dirty: true } && editablePage && !IsBusy;
        saveButton.IsVisible = dirty;
        saveButton.IsEnabled = dirty;
        saveButton.Content = "保存";
        if (string.IsNullOrEmpty(status.Text))
            status.Text = session is { IsReadOnly: false, Dirty: true } ? "有未提交的修改 · Ctrl+S 保存" : "已保存 · 无未提交修改";
        addProject.IsEnabled = !IsBusy; deleteProject.IsEnabled = renameProject.IsEnabled = selectedProjectName is not null && !WorkspaceSession.IsExample(selectedProjectName) && !IsBusy; copyProject.IsEnabled = session is not null && !IsBusy; trashProject.IsEnabled = !IsBusy; projectPicker.IsEnabled = !IsBusy;
        RefreshFlow();
    }
    void SetStatus(string message, bool error = false)
    {
        status.Text = message;
        if (error) Notify(message);
        UpdateFooter();
    }
    static string SafeMessage(Exception exception) => exception is BuildException ? exception.Message : exception is OperationCanceledException ? "操作已取消，未提交的输出保持原样" : exception is IOException or UnauthorizedAccessException ? "文件操作失败，请检查路径、权限或文件是否被占用" : "操作失败，请检查配置与依赖";
    void Try(Action action) { try { action(); } catch (Exception ex) { SetStatus(SafeMessage(ex), true); } }
    void SaveAll()
    {
        if (CurrentPage == "settings") { SetStatus("应用设置已自动保存"); return; }
        if (session is null || session.IsReadOnly || IsBusy) return;
        if (!session.Dirty) { SetStatus("项目配置已保存，无需重复保存"); return; }
        Try(() => { session.Save(); SetStatus("项目配置已保存；生成后更新输出文件。"); });
    }
    void Mutate(string document, Action<JsonObject> change)
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        Try(() => { var value = Map(session.Documents[document].Parse(), document); change(value); session.Documents[document].Set(value); UpdateFooter(); });
    }
    void MutateResidence(Action<JsonArray> change)
    {
        if (session is null || session.IsReadOnly || IsBusy) return;
        Try(() => { var values = session.Residences; change(values); session.SetResidences(values); UpdateFooter(); });
    }
    async Task<bool> CanLeaveAsync()
    {
        if (session?.Dirty != true) return true;
        int choice = await ChoiceAsync("有未保存的修改", "保存当前项目后继续，或放弃本次未保存修改。", ["取消", "放弃修改", "保存并继续"]);
        if (choice == 1) return true;
        if (choice != 2) return false;
        try { session.Save(); UpdateFooter(); return true; } catch (Exception ex) { SetStatus(SafeMessage(ex), true); return false; }
    }
    async Task PickWorkspaceAsync()
    {
        if (IsBusy || !await CanLeaveAsync()) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "选择 ClashRouteCraft 工作目录", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) Try(() =>
        {
            if (preferences.IsPortable) { PortableWorkspace.Move(path, preferences.ApplicationDirectory); OpenWorkspace(preferences.ApplicationDirectory); SetStatus("项目数据已迁入 EXE 目录"); }
            else OpenWorkspace(path);
        });
    }
    async Task NewProjectAsync()
    {
        if (IsBusy || !await CanLeaveAsync()) return;
        if (preferences.Workspace.Length == 0) { await PickWorkspaceAsync(); if (preferences.Workspace.Length == 0) return; }
        string? name = await PromptAsync("新建项目", "项目名称", "", validate: ValidateProjectName); if (name is null) return;
        Try(() => { WorkspaceSession.CreateProject(preferences.Workspace, name); RefreshProjects(name); expandedSource = expandedResidence = 0; Navigate("sources"); SetStatus("已创建项目 " + name); });
    }
    async Task CopyProjectAsync()
    {
        if (IsBusy || session is null || !await CanLeaveAsync()) return;
        string? name = await PromptAsync("复制项目", "新项目名称", session.ProjectName + "-副本", validate: ValidateProjectName); if (name is null) return;
        Try(() => { WorkspaceSession.CreateProject(session.Workspace, name, session.ProjectName); RefreshProjects(name); RenderPage(); SetStatus("已复制项目 " + name); });
    }
    void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = preferences.Theme switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
    }
    TextBlock Label(string text, bool muted = false, double size = UiMetrics.Body) => new() { Tag = muted ? "muted" : "ink", Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = muted ? Muted : Ink };
    static Border Gap(double height = 4) => new() { Height = height };
    static StackPanel Stack(params Control[] controls) { var stack = new StackPanel { Spacing = UiMetrics.Gap }; foreach (var control in controls) stack.Children.Add(control); return stack; }
    static WrapPanel Row(params Control[] controls) { var panel = new WrapPanel { Orientation = Orientation.Horizontal }; foreach (var control in controls) { control.Margin = new(0, 0, UiMetrics.Gap, 0); panel.Children.Add(control); } return panel; }
    Border Card(string title, params Control[] controls)
    {
        var stack = new StackPanel { Spacing = UiMetrics.Gap }; if (title.Length > 0) stack.Children.Add(SectionTitle(title));
        foreach (var control in controls) stack.Children.Add(control);
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new(1), CornerRadius = new(UiMetrics.CardRadius), Padding = new(UiMetrics.CardPadding, UiMetrics.CardPaddingVertical), Child = stack };
    }
    Border Note(string text) => new() { Background = NoteSurface, BorderBrush = Accent, BorderThickness = new(2, 0, 0, 0), CornerRadius = new(6), Padding = new(12, 9), Child = Label(text, true, UiMetrics.Supporting) };
    // Compact card for one list item (subscription/residence): same roles as Card, tighter vertical padding.
    Border ItemCard(params Control[] controls)
    {
        var stack = new StackPanel { Spacing = UiMetrics.FieldGap };
        foreach (var control in controls) stack.Children.Add(control);
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new(1), CornerRadius = new(UiMetrics.CardRadius), Padding = new(14, 8), Child = stack };
    }
    Button Button(string text, Action clicked, bool primary = false)
    {
        var button = new Button { Content = text }; if (primary) button.Classes.Add("primary"); button.Click += (_, _) => Try(clicked); return button;
    }
    Button AsyncButton(string text, Func<Task> clicked, bool primary = false)
    {
        var button = new Button { Content = text }; if (primary) button.Classes.Add("primary"); button.Click += async (_, _) => { try { await clicked(); } catch (Exception ex) { SetStatus(SafeMessage(ex), true); } }; return button;
    }
    Button IconButton(string name, string description, string geometry, Func<Task> clicked)
    {
        var button = AsyncButton("", clicked); button.Name = name; button.Width = 30; button.Height = 30; button.MinHeight = 30; button.Padding = new(6); button.Classes.Add("icon");
        button.Content = new Avalonia.Controls.Shapes.Path { Tag = "accent-stroke", Data = Geometry.Parse(geometry), Stroke = IconInk, StrokeThickness = 1.6, Width = 16, Height = 16, Stretch = Stretch.Uniform };
        ToolTip.SetTip(button, description); Avalonia.Automation.AutomationProperties.SetName(button, description); return button;
    }
    new TextBox TextInput(string value, Action<string> changed, bool secret = false, bool multiline = false)
    {
        var box = new TextBox { IsReadOnly = session?.IsReadOnly == true && CurrentPage != "settings", Text = value, PasswordChar = secret ? '●' : '\0', AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 95 : UiMetrics.InputHeight };
        string previous = value;
        box.TextChanged += (_, _) => { string current = box.Text ?? ""; if (current == previous) return; previous = current; changed(current); }; return box;
    }
    TextBlock SectionTitle(string text) => new() { Text = text, FontSize = UiMetrics.SectionTitle, FontWeight = FontWeight.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
    Control Field(string name, Control input, string? description = null)
    {
        var title = Label(name, size: UiMetrics.FieldTitle); title.FontWeight = FontWeight.Medium;
        var content = Stack(title, input); content.Spacing = UiMetrics.FieldGap;
        if (description is not null) ToolTip.SetTip(input, description);
        return content;
    }
    static Grid Pair(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new("*,16,*") }; grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 2); return grid;
    }
    CheckBox Check(string label, bool value, Action<bool> changed)
    {
        var check = new CheckBox { Content = label, IsEnabled = session?.IsReadOnly != true, IsChecked = value }; bool previous = value;
        check.IsCheckedChanged += (_, _) => { bool current = check.IsChecked == true; if (current == previous) return; previous = current; changed(current); }; return check;
    }
    TextEditor Editor(string text, bool readOnly = false)
    {
        var editor = new TextEditor { Text = text, SyntaxHighlighting = new YamlHighlighting(Dark), IsReadOnly = readOnly || session?.IsReadOnly == true, ShowLineNumbers = true, FontFamily = new FontFamily("Consolas,Microsoft YaHei,monospace"), FontSize = 13, Background = BackgroundBrush, Foreground = Ink, MinHeight = 320, Height = double.NaN, WordWrap = true, Padding = new(10), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false; editor.Options.ConvertTabsToSpaces = true; editor.Options.IndentationSize = 2;
        return editor;
    }
    async Task<int> ChoiceAsync(string title, string message, string[] choices)
    {
        var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = WindowSurface, Foreground = Ink };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; int number = 0;
        foreach (string choice in choices) { int value = number++; var button = new Button { Content = choice, Margin = new(8, 0, 0, 0) }; button.Click += (_, _) => dialog.Close(value); buttons.Children.Add(button); }
        dialog.Content = new StackPanel { Spacing = 16, Children = { Label(message), buttons } }; dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(0); };
        DialogShell.Apply(dialog, title, Ink, Separator, WindowSurface);
        return await dialog.ShowDialog<int>(this);
    }
    async Task<bool> ConfirmAsync(string title, string message, string confirm) => await ChoiceAsync(title, message, ["取消", confirm]) == 1;
    async Task<string?> PromptAsync(string title, string field, string initial, string confirm = "创建", Action<string>? validate = null)
    {
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = WindowSurface, Foreground = Ink };
        var input = new TextBox { Text = initial, Watermark = "输入项目名称" }; var error = Label("", true, UiMetrics.Supporting); error.IsVisible = false;
        var ok = new Button { Content = confirm }; ok.Classes.Add("primary"); ok.Click += (_, _) => { try { string name = FileName(input.Text?.Trim() ?? "", field); validate?.Invoke(name); dialog.Close(name); } catch (BuildException ex) { error.Text = ex.Message; error.Foreground = Danger; error.IsVisible = true; } };
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel { Spacing = 8, Children = { Label(field), input, error, Row(cancel, ok) } }; dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(null); };
        DialogShell.Apply(dialog, title, Ink, Separator, WindowSurface);
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); e.Handled = true; } };
        return await dialog.ShowDialog<string?>(this);
    }
    static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new BuildException("文件或目录尚未生成");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

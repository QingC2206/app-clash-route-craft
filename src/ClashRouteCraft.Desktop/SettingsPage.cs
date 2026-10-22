using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using ClashRouteCraft.Core;
namespace ClashRouteCraft.Desktop;
public sealed partial class MainWindow
{
    Control SettingsPage()
    {
        var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = preferences.Theme switch { "light" => 1, "dark" => 2, _ => 0 }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        theme.SelectionChanged += (_, _) => { preferences.Theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" }; preferences.Save(); ApplyTheme(); UpdateChrome(); RenderPage(); };
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        int plus = version.IndexOf('+'); if (plus > 0) version = version[..plus];
        var advancedRow = new Grid { ColumnDefinitions = new("*,Auto") };
        advancedRow.Children.Add(SectionTitle("高级编辑"));
        var openYaml = Button("打开高级 YAML", () => Navigate("yaml"));
        openYaml.VerticalAlignment = VerticalAlignment.Center;
        advancedRow.Children.Add(openYaml);
        Grid.SetColumn(openYaml, 1);
        return Stack(Card("外观", Field("主题", theme)),
            ResourceSettings(),
            Card("", advancedRow),
            Card("关于 ClashRouteCraft", Label(version), Label("配置与校验在本机完成，校验资源由程序自动查找。", true, 12), Button("通知记录", ShowNotificationHistory)));
    }
}

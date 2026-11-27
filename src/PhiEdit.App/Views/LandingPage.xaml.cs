using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhiEdit.App.Views;

/// <summary>
/// 主界面特色功能导航页。左侧栏上组为谱面库/开始/渲染，下组为设置/关于/退出，
/// 顶部无搜索栏。具体动作以事件形式抛给主窗口处理，便于复用与解耦。
/// </summary>
public sealed partial class LandingPage : UserControl
{
    public event EventHandler? StartRequested;
    public event EventHandler? NewChartRequested;
    public event EventHandler? OpenChartRequested;
    public event EventHandler? PreviewRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public LandingPage()
    {
        InitializeComponent();
        Select("start");
    }

    /// <summary>设置关于页显示的版本文本。</summary>
    public void SetVersion(string version) => AboutVersion.Text = version;

    /// <summary>直接显示指定页面：library / start / render / about。</summary>
    public void Select(string tag)
    {
        StartPanel.Visibility = tag == "start" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = tag == "library" ? Visibility.Visible : Visibility.Collapsed;
        RenderPanel.Visibility = tag == "render" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        Highlight(NavLibrary, tag == "library");
        Highlight(NavStart, tag == "start");
        Highlight(NavRender, tag == "render");
        Highlight(NavSettings, tag == "settings");
        Highlight(NavAbout, tag == "about");
    }

    private static void Highlight(Button button, bool active) =>
        button.Background = active
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentSoftBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void OnNavigateClick(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        switch (tag)
        {
            case "settings":
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "exit":
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                Select(tag);
                break;
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);

    private void OnNewChartClick(object sender, RoutedEventArgs e) => NewChartRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenChartClick(object sender, RoutedEventArgs e) => OpenChartRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenPreviewClick(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke(this, EventArgs.Empty);
}

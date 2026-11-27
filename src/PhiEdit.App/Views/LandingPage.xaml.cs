using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhiEdit.Core.Charts;

namespace PhiEdit.App.Views;

/// <summary>
/// 主界面特色功能导航页，基于 WinUI 3 原生 NavigationView（与系统设置应用同款侧栏）。
/// MenuItems：谱面库 / 开始 / 渲染；FooterMenuItems：设置 / 关于 / 退出。
/// 设置页采用系统设置风格卡片布局，并内置搜索框。
/// 具体动作以事件形式抛给主窗口处理，保持编辑器逻辑集中。
/// </summary>
public sealed partial class LandingPage : UserControl
{
    public event EventHandler? StartRequested;
    public event EventHandler? NewChartRequested;
    public event EventHandler? OpenChartRequested;
    public event EventHandler? PreviewRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ImportSkinRequested;
    public event EventHandler? ApplySkinRequested;
    public event EventHandler? DeleteSkinRequested;
    public event EventHandler? LandingSkinSelectionChanged;
    public event EventHandler<ChartLibraryItem>? LibraryOpenRequested;
    public event EventHandler? LibraryImportRequested;
    public event EventHandler? LibraryNewRequested;
    public event EventHandler<ChartLibraryItem>? LibraryExportRequested;

    public LandingPage()
    {
        InitializeComponent();
        Select("library");
    }

    /// <summary>填充谱面库列表。</summary>
    public void SetLibraryItems(IReadOnlyList<ChartLibraryItem> items)
    {
        LibraryList.ItemsSource = items;
    }

    /// <summary>设置关于页与设置页显示的版本文本。</summary>
    public void SetVersion(string version)
    {
        AboutVersion.Text = version;
        SettingsVersionText.Text = version;
    }

    /// <summary>设置页状态文本（供主窗口反馈导入/应用结果）。</summary>
    public string SettingsStatus
    {
        get => LandingSettingsStatus.Text;
        set => LandingSettingsStatus.Text = value;
    }

    /// <summary>设置页资源包列表，供主窗口填充数据。</summary>
    public ListView SkinListView => SkinList;

    /// <summary>设置页资源包预览信息。</summary>
    public string SkinPreview
    {
        set => SkinPreviewInfo.Text = value;
    }

    public string SkinDescriptionText
    {
        set => SkinDescription.Text = value;
    }

    /// <summary>直接显示指定页面：library / start / render / settings / about。</summary>
    public void Select(string tag)
    {
        StartPanel.Visibility = tag == "start" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = tag == "library" ? Visibility.Visible : Visibility.Collapsed;
        RenderPanel.Visibility = tag == "render" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        var selectedItem = tag switch
        {
            "library" => NavLibrary,
            "render" => NavRender,
            "settings" => NavSettings,
            "about" => NavAbout,
            _ => NavStart,
        };
        if (!ReferenceEquals(RootNavigation.SelectedItem, selectedItem))
        {
            RootNavigation.SelectedItem = selectedItem;
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var tag = item.Tag as string ?? string.Empty;
        switch (tag)
        {
            case "exit":
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "settings":
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                Select("settings");
                break;
            default:
                Select(tag);
                break;
        }
    }

    /// <summary>设置页搜索：按关键词过滤设置卡片。</summary>
    private void OnSettingsSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text?.Trim() ?? string.Empty;
        bool Matches(string text) => string.IsNullOrEmpty(query) ||
            text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        var skin = Matches("音符资源包 皮肤 skin 导入 删除");
        var preview = Matches("实时预览 渲染 preview");
        var about = Matches("关于 版本 about");
        SkinSettingsCard.Visibility = skin ? Visibility.Visible : Visibility.Collapsed;
        PreviewSettingsCard.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        AboutSettingsCard.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        SettingsNoResult.Visibility = skin || preview || about ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);
    private void OnNewChartClick(object sender, RoutedEventArgs e) => NewChartRequested?.Invoke(this, EventArgs.Empty);
    private void OnOpenChartClick(object sender, RoutedEventArgs e) => OpenChartRequested?.Invoke(this, EventArgs.Empty);
    private void OnOpenPreviewClick(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke(this, EventArgs.Empty);

    private void OnImportSkinClick(object sender, RoutedEventArgs e) => ImportSkinRequested?.Invoke(this, EventArgs.Empty);
    private void OnApplySkinClick(object sender, RoutedEventArgs e) => ApplySkinRequested?.Invoke(this, EventArgs.Empty);
    private void OnDeleteSkinClick(object sender, RoutedEventArgs e) => DeleteSkinRequested?.Invoke(this, EventArgs.Empty);
    private void OnLandingSkinSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LandingSkinSelectionChanged?.Invoke(this, EventArgs.Empty);

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LibraryExportButton.IsEnabled = LibraryList.SelectedItem is ChartLibraryItem;

    private void OnLibraryImportClick(object sender, RoutedEventArgs e) => LibraryImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnLibraryNewClick(object sender, RoutedEventArgs e) => LibraryNewRequested?.Invoke(this, EventArgs.Empty);

    private void OnLibraryOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ChartLibraryItem item)
        {
            LibraryOpenRequested?.Invoke(this, item);
        }
    }

    private void OnLibraryExportClick(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is ChartLibraryItem item)
        {
            LibraryExportRequested?.Invoke(this, item);
        }
    }
}

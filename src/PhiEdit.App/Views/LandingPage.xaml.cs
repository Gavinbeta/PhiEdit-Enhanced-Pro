using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhiEdit.Core.Charts;
using Windows.System;
using Windows.UI.Core;

namespace PhiEdit.App.Views;

/// <summary>谱面库卡片的展示模型，携带背景图与原始条目。</summary>
public sealed class LibraryCard
{
    public LibraryCard(ChartLibraryItem item, string? backgroundPath)
    {
        Item = item;
        Name = item.Name;
        Subtitle = item.Subtitle;
        SourceLabel = item.Source switch
        {
            ChartLibrarySource.Builtin => "示例",
            ChartLibrarySource.Created => "新建",
            _ => "导入",
        };
        if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
        {
            try
            {
                Background = new BitmapImage(new Uri(backgroundPath));
            }
            catch
            {
                Background = null;
            }
        }
    }

    public ChartLibraryItem Item { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string SourceLabel { get; }
    public BitmapImage? Background { get; }
}

/// <summary>
/// 主界面特色功能导航页，基于 WinUI 3 原生 NavigationView（与系统设置应用同款侧栏）。
/// MenuItems：谱面库 / 开始 / 渲染；FooterMenuItems：设置 / 关于 / 退出。
/// 设置页采用系统设置风格卡片布局，并内置搜索框。
/// 谱面库支持按钮进入多选、Ctrl+A 全选自动进入多选、退出多选。
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
    public event EventHandler<IReadOnlyList<ChartLibraryItem>>? LibraryExportRequested;

    private readonly List<LibraryCard> _libraryCards = new();

    private const double DefaultCardWidth = 210;
    private const double CardAspect = 150.0 / 210.0;

    public LandingPage()
    {
        InitializeComponent();
        LibraryList.KeyDown += OnLibraryKeyDown;

        // 读取持久化的卡片尺寸；在 ItemsSource 设置后应用。
        var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
        var width = settings.Values.TryGetValue("LibraryCardWidth", out var value) && value is double d
            ? d
            : DefaultCardWidth;
        width = Math.Clamp(width, 150, 360);
        LibraryCardSizeSlider.Value = width;
        LibraryList.Loaded += (_, _) => ApplyCardSize(width);

        Select("library");
    }

    private void ApplyCardSize(double width)
    {
        if (LibraryList.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
        {
            wrapGrid.ItemWidth = width;
            wrapGrid.ItemHeight = width * CardAspect;
        }
        LibraryCardSizeValue.Text = $"{(int)width} × {(int)(width * CardAspect)}";
    }

    /// <summary>填充谱面库列表（背景由 backgroundResolver 提供绝对路径）。</summary>
    public void SetLibraryItems(IReadOnlyList<ChartLibraryItem> items,
        Func<ChartLibraryItem, string?> backgroundResolver)
    {
        _libraryCards.Clear();
        foreach (var item in items)
        {
            _libraryCards.Add(new LibraryCard(item, backgroundResolver(item)));
        }
        LibraryList.ItemsSource = _libraryCards;
        UpdateLibrarySelectionUI();
    }

    /// <summary>设置关于页与设置页显示的版本文本。</summary>
    public void SetVersion(string version)
    {
        AboutVersion.Text = version;
        SettingsVersionText.Text = version;
    }

    public string SettingsStatus
    {
        get => LandingSettingsStatus.Text;
        set => LandingSettingsStatus.Text = value;
    }

    public ListView SkinListView => SkinList;

    public string SkinPreview
    {
        set => SkinPreviewInfo.Text = value;
    }

    public string SkinDescriptionText
    {
        set => SkinDescription.Text = value;
    }

    /// <summary>是否处于谱面库多选模式。</summary>
    public bool IsMultiSelect { get; private set; }

    /// <summary>直接显示指定页面：library / start / render / settings / about。</summary>
    public void Select(string tag)
    {
        StartPanel.Visibility = tag == "start" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = tag == "library" ? Visibility.Visible : Visibility.Collapsed;
        RenderPanel.Visibility = tag == "render" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        ContentRoot.MaxWidth = tag == "library" ? double.PositiveInfinity : 1000;
        ContentRoot.Padding = tag == "library"
            ? new Thickness(24, 28, 24, 28)
            : new Thickness(44, 40, 44, 40);
        ContentRoot.HorizontalAlignment = tag == "library"
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        if (tag != "library" && IsMultiSelect)
        {
            ExitMultiSelect();
        }

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

    private void OnSettingsSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text?.Trim() ?? string.Empty;
        bool Matches(string text) => string.IsNullOrEmpty(query) ||
            text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        var skin = Matches("音符资源包 皮肤 skin 导入 删除");
        var library = Matches("谱面库 卡片 大小 尺寸 library card");
        var preview = Matches("实时预览 渲染 preview");
        var about = Matches("关于 版本 about");
        SkinSettingsCard.Visibility = skin ? Visibility.Visible : Visibility.Collapsed;
        LibrarySettingsCard.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        PreviewSettingsCard.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        AboutSettingsCard.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        SettingsNoResult.Visibility = skin || library || preview || about ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- 谱面库多选

    private void OnLibraryMultiSelectClick(object sender, RoutedEventArgs e) => EnterMultiSelect();

    private void OnLibraryCancelMultiSelectClick(object sender, RoutedEventArgs e) => ExitMultiSelect();

    private void EnterMultiSelect()
    {
        if (IsMultiSelect)
        {
            return;
        }
        IsMultiSelect = true;
        LibraryList.SelectionMode = ListViewSelectionMode.Multiple;
        UpdateLibrarySelectionUI();
    }

    private void ExitMultiSelect()
    {
        IsMultiSelect = false;
        LibraryList.SelectionMode = ListViewSelectionMode.Single;
        LibraryList.SelectedItems.Clear();
        UpdateLibrarySelectionUI();
    }

    private void UpdateLibrarySelectionUI()
    {
        LibraryMultiSelectButton.Visibility = IsMultiSelect ? Visibility.Collapsed : Visibility.Visible;
        LibraryCancelMultiSelectButton.Visibility = IsMultiSelect ? Visibility.Visible : Visibility.Collapsed;
        LibraryExportButton.IsEnabled = LibraryList.SelectedItems.Count > 0;
        LibraryExportButton.Content = IsMultiSelect
            ? $"导出选中 ({LibraryList.SelectedItems.Count})"
            : "导出选中";
    }

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLibrarySelectionUI();

    /// <summary>双击谱面卡片进入编辑（多选模式下不触发）。</summary>
    private void OnLibraryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsMultiSelect)
        {
            return;
        }

        // 从命中测试的原始元素向上找 DataContext 为 LibraryCard 的控件。
        FrameworkElement? element = e.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not LibraryCard)
        {
            element = element.Parent as FrameworkElement;
        }

        var card = element?.DataContext as LibraryCard ?? LibraryList.SelectedItem as LibraryCard;
        if (card is not null)
        {
            LibraryOpenRequested?.Invoke(this, card.Item);
        }
    }

    private void OnLibraryCardSizeChanged(object sender, RoutedEventArgs e)
    {
        var width = LibraryCardSizeSlider.Value;
        ApplyCardSize(width);
        Windows.Storage.ApplicationData.Current.LocalSettings.Values["LibraryCardWidth"] = width;
    }

    private void OnLibraryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+A：自动进入多选并全选；Esc：退出多选。
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.A)
        {
            e.Handled = true;
            EnterMultiSelect();
            foreach (var card in _libraryCards)
            {
                if (!LibraryList.SelectedItems.Contains(card))
                {
                    LibraryList.SelectedItems.Add(card);
                }
            }
            UpdateLibrarySelectionUI();
        }
        else if (e.Key == VirtualKey.Escape && IsMultiSelect)
        {
            e.Handled = true;
            ExitMultiSelect();
        }
    }

    private void OnLibraryImportClick(object sender, RoutedEventArgs e) => LibraryImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnLibraryNewClick(object sender, RoutedEventArgs e) => LibraryNewRequested?.Invoke(this, EventArgs.Empty);

    private void OnLibraryOpenClick(object sender, RoutedEventArgs e)
    {
        if (IsMultiSelect)
        {
            return;
        }
        if ((sender as Button)?.Tag is ChartLibraryItem item)
        {
            LibraryOpenRequested?.Invoke(this, item);
        }
    }

    private void OnLibraryExportClick(object sender, RoutedEventArgs e)
    {
        var items = LibraryList.SelectedItems
            .OfType<LibraryCard>()
            .Select(card => card.Item)
            .ToList();
        if (items.Count > 0)
        {
            LibraryExportRequested?.Invoke(this, items);
        }
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
}

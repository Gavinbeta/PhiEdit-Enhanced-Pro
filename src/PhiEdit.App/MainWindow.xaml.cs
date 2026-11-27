using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PhiEdit.App.Controls;
using PhiEdit.App.Services;
using PhiEdit.App.Views;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.IO;
using PhiEdit.Core.Playback;
using PhiEdit.Core.Preview;
using PhiEdit.Core.Rendering;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace PhiEdit.App;

public sealed partial class MainWindow : Window
{
    private readonly EditorSession _session = new();

    private readonly ChartEditor _editor;

    private readonly PlaybackClock _clock = new();

    /// <summary>编辑区底层实时预览的时间源，只读跟随编辑播放头。</summary>
    private readonly FollowClockSource _embeddedClock;

    private readonly EditorPalette _palette = new();

    private readonly NoteSkinRenderer _skinRenderer = new();

    private readonly DispatcherQueueTimer _timer;

    private PreviewWindow? _previewWindow;

    private string? _currentFilePath;

    private bool _refreshingInspector;

    private Note? _noteEditSnapshot;
    private Note? _noteEditSource;

    private readonly HashSet<VirtualKey> _heldPlacementKeys = new();

    public MainWindow()
    {
        _editor = new ChartEditor(_session);
        _embeddedClock = new FollowClockSource(_clock);
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        ApplyInitialWindowPlacement();

        _clock.Tempo = _session.Document.BuildTempoMap();
        EditorCanvas.Session = _session;
        EditorCanvas.Editor = _editor;
        EditorCanvas.Palette = _palette;
        EditorCanvas.Renderer = _skinRenderer;

        // 嵌入编辑区的实时预览：与独立窗口复用同一个 PreviewCanvas 渲染代码，
        // 时间源只读跟随编辑播放头。画布自身锁定比例、居中并以黑边填充多余空间。
        EmbeddedPreview.Session = _session;
        EmbeddedPreview.Clock = _embeddedClock;

        EditorCanvas.SkinLoadFailed += (_, message) => StatusReadout.Text = message;
        EditorCanvas.SelectionChanged += (_, _) => OnSelectionChanged();
        _session.Viewport.ViewChanged += (_, _) => OnViewChanged();
        Scrubber.ValueChanged += (sender, args) => OnScrubberChanged(sender, args);
        EditorSplitter.Dragged += (_, delta) => ApplyEditorSplit(delta);
        InspectorSplitter.Dragged += (_, delta) => ApplyInspectorSplit(delta);

        _session.Changed += OnSessionChanged;

        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTimerTick;

        RegisterShortcuts();
        RootLayout.KeyDown += OnRootKeyDown;
        RootLayout.KeyUp += OnRootKeyUp;

        // 滚轮统一在窗口根节点处理：无论焦点在哪个控件，只要指针位于编辑区就应滚动时间轴。
        RootLayout.PointerWheelChanged += OnRootPointerWheelChanged;
        AttachAspectRatioLock();

        for (var index = 0; index < PhiEdit.Core.Easing.Easing.Count; index++)
        {
            EventEasingBox.Items.Add($"{index + 1} · {PhiEdit.Core.Easing.Easing.DisplayName(index + 1)}");
        }

        EventEasingBox.PointerWheelChanged += OnEventEasingWheel;

        DivisionBox.Value = _session.Division;
        GridCountBox.Value = _session.GridCount;
        SnapToggle.IsOn = _session.SnapX;

        for (var layer = 0; layer <= JudgeLine.SpecialLayer; layer++)
        {
            var button = new ToggleButton
            {
                Content = layer == JudgeLine.SpecialLayer
                    ? "特殊"
                    : layer.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Tag = layer,
                MinWidth = 32,
                IsChecked = layer == _session.CurrentLayer,
                Style = Application.Current.Resources["ToolbarToggleStyle"] as Style,
            };
            button.Click += OnLayerButtonClick;
            LayerButtons.Children.Add(button);
        }

        LoadDefaultChart();
        RefreshAll();

        // 资源包必须在预览窗口打开前装载，否则命中音与皮肤都会是空。
        InitializeSkins();

        // 主界面特色功能导航页：事件委托回主窗口处理，保持编辑器逻辑集中。
        Landing.SetVersion(BuildVersion.Text.Replace("v", string.Empty));
        Landing.StartRequested += (_, _) => EnterEditor();
        Landing.NewChartRequested += (_, _) => { EnterEditor(); OnNewClick(this, new RoutedEventArgs()); };
        Landing.OpenChartRequested += (_, _) => { EnterEditor(); OnOpenClick(this, new RoutedEventArgs()); };
        Landing.PreviewRequested += (_, _) => OpenPreviewForStartup();
        Landing.SettingsRequested += (_, _) => OnSettingsClick(this, new RoutedEventArgs());
        Landing.ExitRequested += (_, _) => Close();
    }

    /// <summary>隐藏主界面导航页，进入编辑工作区。</summary>
    public void EnterEditor()
    {
        if (LandingOverlay.Visibility == Visibility.Collapsed)
        {
            return;
        }
        LandingOverlay.Visibility = Visibility.Collapsed;
        RefreshAll();
        EditorCanvas.Invalidate();
        EmbeddedPreview.Invalidate();
    }

    // ---------------------------------------------------------------- 初始化

    /// <summary>默认加载内置的示例 PEZ；缺失时退回到最小示例谱面。</summary>
    private void LoadDefaultChart()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "SampleCharts", "16266666.pez");
        if (File.Exists(path))
        {
            LoadChartPath(path);
            _session.Viewport.SetPosition(71.81);
            _clock.SeekToBeat(71.81);
            return;
        }

        BuildSampleChart();
    }

    /// <summary>建立一个含示例内容的谱面，便于首次打开即可观察编辑界面。</summary>
    private void BuildSampleChart()
    {
        var document = _session.Document;
        document.SongName = "Sample";
        document.SongArtist = "PhiEdit";
        document.ChartName = "Default";
        document.Difficulty = "5";
        document.BpmList.Clear();
        document.BpmList.Add(new TempoPoint(Beat.Zero, 120));

        var line = document.Lines.Count > 0 ? document.Lines[0] : document.AddLine();
        line.X = 0;
        line.Y = 0;

        var editor = _editor;
        var notes = new[]
        {
            (NoteType.Tap, 4.0, -420.0),
            (NoteType.Tap, 4.25, -135.0),
            // 同一时刻的两个音符 = 多押，其中一个会以高亮贴图提示
            (NoteType.Tap, 4.5, -270.0),
            (NoteType.Flick, 4.5, 90.0),
            (NoteType.Drag, 5.0, 300.0),
            (NoteType.Tap, 5.5, -675.0),
        };

        foreach (var (type, beat, x) in notes)
        {
            editor.PlaceNote(line, type, Beat.FromValue(beat), x);
        }

        // Hold 样例：带长度的音符，身体整体可点击、可拖动
        var hold = editor.PlaceNote(line, NoteType.Hold, Beat.FromValue(3.75), 200);
        hold.EndBeat = Beat.FromValue(4.5);

        // 起点 4:1/2、终点 5:1/2 的事件应绘制成覆盖整段的长条
        editor.AddEvent(line, EventTrackKind.X, 0, Beat.FromValue(4.5), -120, 60, Beat.FromValue(5.5));
        editor.AddEvent(line, EventTrackKind.Y, 0, Beat.FromValue(4.5), 0, 40, Beat.FromValue(5.5));
        editor.AddEvent(line, EventTrackKind.Rotate, 0, Beat.FromValue(4.5), 0, 0, Beat.FromValue(5.5));
        editor.AddEvent(line, EventTrackKind.Alpha, 0, Beat.FromValue(4.5), 255, 255, Beat.FromValue(5.5));
        editor.AddEvent(line, EventTrackKind.Speed, 0, Beat.FromValue(4.5), 1, 1, Beat.FromValue(5.5));

        _session.Undo.Clear();
        document.IsDirty = false;
        _session.Viewport.SetPosition(2.5);
        _clock.SeekToBeat(2.5);
    }

    private void RegisterShortcuts()
    {
        void Add(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, _) =>
            {
                if (LandingOverlay.Visibility == Visibility.Visible)
                {
                    return;
                }
                action();
            };
            RootLayout.KeyboardAccelerators.Add(accelerator);
        }

        Add(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, () => _ = SaveChart());
        Add(Windows.System.VirtualKey.O, Windows.System.VirtualKeyModifiers.Control, () => _ = OpenChart());
        Add(Windows.System.VirtualKey.P, Windows.System.VirtualKeyModifiers.Control, OpenPreviewForStartup);
        Add(Windows.System.VirtualKey.Z, Windows.System.VirtualKeyModifiers.Control, Undo);
        Add(Windows.System.VirtualKey.Y, Windows.System.VirtualKeyModifiers.Control, Redo);
        Add(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, () => _ = ExportChart());
        Add(Windows.System.VirtualKey.C, Windows.System.VirtualKeyModifiers.Control, CopySelection);
        Add(Windows.System.VirtualKey.X, Windows.System.VirtualKeyModifiers.Control, CutSelection);
        Add(Windows.System.VirtualKey.V, Windows.System.VirtualKeyModifiers.Control, PasteSelection);
        Add(Windows.System.VirtualKey.M, Windows.System.VirtualKeyModifiers.Control, MirrorSelection);
        Add(Windows.System.VirtualKey.A, Windows.System.VirtualKeyModifiers.Control, SelectAll);
    }

    // ---------------------------------------------------------------- 全局滚轮

    /// <summary>
    /// 窗口级滚轮处理：无论当前焦点在哪个控件，只要指针位于编辑区，滚轮都应滚动时间轴。
    /// 向下滚动 = 拍号变小（与原版一致），Ctrl+滚轮 = 纵向缩放。
    /// </summary>
    private void OnRootPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (LandingOverlay.Visibility == Visibility.Visible)
        {
            return;
        }
        var point = e.GetCurrentPoint(RootLayout);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var position = point.Position;
        if (!IsOverEditorArea(position))
        {
            return;
        }

        var viewport = _session.Viewport;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            viewport.ZoomAt(EditorLocalY(position), delta > 0 ? 1.15 : 1 / 1.15);
        }
        else
        {
            // 与原版一致：滚轮向下 = 拍号变小（回看更早的拍），一格滚动一个细分单位。
            var step = 1.0 / Math.Max(1, _session.Division);
            viewport.ScrollByBeats(delta > 0 ? step : -step);
        }

        e.Handled = true;
    }

    /// <summary>判断指针是否位于编辑画布之内。</summary>
    private bool IsOverEditorArea(Point position)
    {
        var bounds = EditorCanvas.TransformToVisual(RootLayout).TransformBounds(
            new Rect(0, 0, EditorCanvas.ActualWidth, EditorCanvas.ActualHeight));
        return bounds.Contains(position);
    }

    /// <summary>把窗口坐标转换为编辑区内部的纵坐标。</summary>
    private double EditorLocalY(Point position)
    {
        var bounds = EditorCanvas.TransformToVisual(RootLayout).TransformBounds(
            new Rect(0, 0, EditorCanvas.ActualWidth, EditorCanvas.ActualHeight));
        return bounds.Contains(position) ? position.Y - bounds.Top : 0;
    }

    // ---------------------------------------------------------------- 窗口比例

    /// <summary>锁定主窗口为 16:9，避免调整大小时编辑区被右侧面板遮挡。</summary>
    private void AttachAspectRatioLock()
    {
        try
        {
            var handle = WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.Changed += OnAppWindowChanged;
        }
        catch (Exception)
        {
            // 窗口 API 不可用时保持默认行为。
        }
    }

    private SizeInt32 _lastWindowSize;

    private bool _adjustingSize;

    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (_adjustingSize || !args.DidSizeChange)
        {
            return;
        }

        var size = sender.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        const double ratio = 16.0 / 9.0;
        var last = _lastWindowSize;
        _lastWindowSize = size;
        if (last.Width <= 0 || last.Height <= 0)
        {
            return;
        }

        // 以变化更明显的那条边为准，另一条边按 16:9 回推。
        var widthChanged = Math.Abs(size.Width - last.Width) >= Math.Abs(size.Height - last.Height);
        var targetWidth = widthChanged ? size.Width : (int)Math.Round(size.Height * ratio);
        var targetHeight = widthChanged ? (int)Math.Round(size.Width / ratio) : size.Height;

        try
        {
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(sender.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            targetWidth = Math.Clamp(targetWidth, work.X + 900, work.X + work.Width);
            targetHeight = Math.Clamp(targetHeight, work.Y + 560, work.Y + work.Height);
        }
        catch (Exception)
        {
            // 取不到工作区时直接使用推算值。
        }

        _adjustingSize = true;
        sender.Resize(new SizeInt32(targetWidth, targetHeight));
        _adjustingSize = false;
    }

    // ---------------------------------------------------------------- 剪贴板

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelection();

    private void OnCutClick(object sender, RoutedEventArgs e) => CutSelection();

    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteSelection();

    private void OnMirrorClick(object sender, RoutedEventArgs e) => MirrorSelection();

    private void OnClearClipboardClick(object sender, RoutedEventArgs e)
    {
        _session.ClearClipboard();
        RefreshSelectionInfo();
        StatusReadout.Text = "剪贴板已清空";
    }

    private void OnDeleteSelectionClick(object sender, RoutedEventArgs e) => DeleteSelection();

    private void CopySelection()
    {
        var count = _editor.CopySelection();
        RefreshSelectionInfo();
        StatusReadout.Text = count > 0 ? $"已复制 {count} 个对象" : "没有可复制的内容";
    }

    private void CutSelection()
    {
        var count = _editor.CutSelection();
        RefreshSelectionInfo();
        RefreshAll();
        StatusReadout.Text = count > 0 ? $"已剪切 {count} 个对象" : "没有可剪切的内容";
    }

    private void PasteSelection()
    {
        var beat = _session.Viewport.Position;
        var count = _editor.PasteSelection(beat);
        RefreshAll();
        StatusReadout.Text = count > 0 ? $"已粘贴 {count} 个对象" : "剪贴板为空";
    }

    private void MirrorSelection()
    {
        var count = _editor.MirrorSelection();
        RefreshAll();
        StatusReadout.Text = count > 0 ? $"已镜像 {count} 个对象" : "没有可镜像的内容";
    }

    /// <summary>全选当前判定线上的音符。</summary>
    private void SelectAll()
    {
        var line = _session.CurrentLine;
        if (line is null)
        {
            return;
        }

        _session.SelectedNotes.Clear();
        foreach (var note in line.Notes)
        {
            _session.SelectedNotes.Add(note);
        }

        RefreshAll();
        StatusReadout.Text = $"已选中 {_session.SelectedNotes.Count} 个音符";
    }

    /// <summary>更新底栏的选中信息。</summary>
    private void RefreshSelectionInfo()
    {
        var notes = _session.SelectedNotes.Count;
        var events = _session.SelectedEvents.Count;
        SelectionInfo.Text = notes + events == 0
            ? "未选中"
            : $"已选 {notes} 音符 / {events} 事件";
    }

    // ---------------------------------------------------------------- 设置与资源包

    /// <summary>装载内置资源包，并把首个资源包设为当前生效的皮肤。</summary>
    private void InitializeSkins()
    {
        var failures = BuiltinSkins.Load(_session);
        foreach (var failure in failures)
        {
            StatusReadout.Text = failure;
        }

        _session.Skins.Changed += OnSkinsChanged;

        if (_session.Skins.Active is not null)
        {
            ApplySkinAsync(_session.Skins.Active);
        }

        RefreshSkinList();
    }

    private void OnSkinsChanged(object? sender, EventArgs e)
    {
        RefreshSkinList();
        if (_session.Skins.Active is not null)
        {
            ApplySkinAsync(_session.Skins.Active);
        }
    }

    /// <summary>应用资源包：贴图由画布在设备就绪后加载。</summary>
    private void ApplySkinAsync(PhiEdit.Core.Skins.NoteSkin skin)
    {
        EditorCanvas.RequestSkinReload();
        StatusReadout.Text = $"已应用皮肤：{skin.Info.Name}";
    }

    /// <summary>列表项与资源包的对应关系。</summary>
    private readonly List<PhiEdit.Core.Skins.NoteSkin> _skinItems = new();

    /// <summary>刷新设置页的资源包列表。</summary>
    private void RefreshSkinList()
    {
        _skinItems.Clear();
        SkinList.Items.Clear();
        foreach (var skin in _session.Skins.Skins)
        {
            _skinItems.Add(skin);
            SkinList.Items.Add($"{skin.Info.Name} · {skin.Info.Author}");
        }

        if (_session.Skins.Active is not null)
        {
            for (var index = 0; index < _skinItems.Count; index++)
            {
                if (_skinItems[index].Id == _session.Skins.Active.Id)
                {
                    SkinList.SelectedIndex = index;
                    break;
                }
            }
        }
    }

    private void OnSkinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkinList.SelectedIndex < 0 || SkinList.SelectedIndex >= _skinItems.Count)
        {
            return;
        }

        var skin = _skinItems[SkinList.SelectedIndex];

        SkinPreviewInfo.Text = $"名称：{skin.Info.Name}\n作者：{skin.Info.Author}";
        SkinDescription.Text = string.IsNullOrWhiteSpace(skin.Info.Description)
            ? "（无说明）"
            : skin.Info.Description;

        foreach (var warning in skin.Warnings)
        {
            SettingsStatus.Text = warning;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        RefreshSkinList();
        SettingsStatus.Text = "准备就绪。";
    }

    private void OnCloseSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnImportSkinClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var archive = await File.ReadAllBytesAsync(file.Path);

            var skin = PhiEdit.Core.Skins.NoteSkin.FromZip(archive, PhiEdit.Core.Skins.NoteSkin.ComputeId(
                PhiEdit.Core.Skins.NoteSkin.ReadArchive(archive)));
            _session.Skins.Add(skin);
            _session.Skins.Activate(skin.Id);
            ApplySkinAsync(skin);
            RefreshSkinList();
            SettingsStatus.Text = $"已导入资源包：{skin.Info.Name}";
        }
        catch (Exception exception)
        {
            SettingsStatus.Text = $"资源包导入失败：{exception.Message}";
        }
    }

    private void OnApplySkinClick(object sender, RoutedEventArgs e)
    {
        if (SkinList.SelectedIndex < 0 || SkinList.SelectedIndex >= _skinItems.Count)
        {
            SettingsStatus.Text = "请先选择一个资源包。";
            return;
        }

        var skin = _skinItems[SkinList.SelectedIndex];

        _session.Skins.Activate(skin.Id);
        ApplySkinAsync(skin);
        SettingsStatus.Text = $"已应用皮肤：{skin.Info.Name}";
    }

    private void OnDeleteSkinClick(object sender, RoutedEventArgs e)
    {
        if (SkinList.SelectedIndex < 0 || SkinList.SelectedIndex >= _skinItems.Count)
        {
            SettingsStatus.Text = "请先选择一个资源包。";
            return;
        }

        var skin = _skinItems[SkinList.SelectedIndex];

        if (skin.IsBuiltin)
        {
            SettingsStatus.Text = "内置资源包不可删除。";
            return;
        }

        _session.Skins.Remove(skin.Id);
        RefreshSkinList();
        SettingsStatus.Text = $"已删除资源包：{skin.Info.Name}";
    }

    // ---------------------------------------------------------------- 分栏

    /// <summary>把窗口放到工作区中央并按屏幕尺寸设置初始大小。</summary>
    private void ApplyInitialWindowPlacement()
    {
        try
        {
            var handle = WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            var work = display.WorkArea;

            var width = Math.Max(1024, (int)(work.Width * 0.86));
            var height = Math.Max(680, (int)(work.Height * 0.88));
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            appWindow.Move(new Windows.Graphics.PointInt32(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2));
        }
        catch (Exception)
        {
            // 窗口初始化失败时保持默认位置，不影响使用。
        }
    }

    /// <summary>编辑区中音符区与判定线区的分配比例。</summary>
    private double _editorSplitRatio = 0.5;

    /// <summary>属性面板宽度。</summary>
    private double _inspectorWidth = 360;

    private void OnEditorAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySplitWidths();
    }

    private void ApplyEditorSplit(double deltaX)
    {
        var total = Math.Max(1, EditorArea.ActualWidth);
        _editorSplitRatio = Math.Clamp(_editorSplitRatio + deltaX / total, 0.15, 0.85);
        ApplySplitWidths();
    }

    private void ApplyInspectorSplit(double deltaX)
    {
        _inspectorWidth = Math.Clamp(_inspectorWidth - deltaX, 260, 640);
        ApplySplitWidths();
    }

    private void ApplySplitWidths()
    {
        // 左右半区现在由同一块画布按 SplitRatio 绘制，不再依赖两列布局。
        EditorCanvas.SplitRatio = _editorSplitRatio;

        if (WorkspaceArea.ColumnDefinitions.Count >= 3)
        {
            WorkspaceArea.ColumnDefinitions[2].Width = new GridLength(_inspectorWidth);
        }
    }

    // ---------------------------------------------------------------- 刷新

    /// <summary>选择变化：刷新属性面板并让两个编辑区同时重绘。</summary>
    private void OnSelectionChanged()
    {
        RefreshInspector();
        EditorCanvas.Invalidate();
    }


    /// <summary>
    /// 纵向位置（视口唯一真源）变化时的统一响应：
    /// 同步播放头位置、刷新底栏读数，并让两个编辑区同时重绘。
    /// 滚轮、缩放、底部进度条、播放推进等所有入口最终都只修改视口位置，
    /// 因此两侧永远同步，不存在某一侧滞后的情况。
    /// </summary>
    private void OnViewChanged()
    {
        if (!_clock.IsPlaying)
        {
            _clock.SeekToBeat(_session.Viewport.Position);
        }

        RefreshTimeline();
        EditorCanvas.Invalidate();
        EmbeddedPreview.Invalidate();
        _previewWindow?.SyncFromEditor(_session.Viewport.Position);
    }


    private void RefreshAll()
    {
        RefreshSelectionInfo();
        RefreshLineSelector();
        RefreshLineList();
        RefreshBpmList();
        RefreshInspector();
        RefreshTimeline();
        EditorCanvas.Invalidate();
        EmbeddedPreview.Invalidate();
    }

    private void RefreshTimeline()
    {
        var tempo = _session.Document.BuildTempoMap();
        _clock.Tempo = tempo;
        var last = Math.Max(1.0, _session.Document.LastBeat + 4);
        Scrubber.Maximum = last;
        if (!_clock.IsPlaying)
        {
            Scrubber.Value = Math.Clamp(_clock.CurrentBeat, Scrubber.Minimum, Scrubber.Maximum);
        }

        TimeReadout.Text = $"{_clock.CurrentSeconds:0.00}s / {tempo.DurationToBeat(_session.Document.LastBeat):0.00}s";
        BeatReadout.Text = $"拍 {_clock.CurrentBeat:0.00}";
        BpmReadout.Text = $"{tempo.BpmAt(_clock.CurrentBeat):0.##} BPM";
        DocumentTitle.Text = _session.Document.ChartName;
    }

    private void RefreshLineSelector()
    {
        LineSelector.Items.Clear();
        for (var index = 0; index < _session.Document.Lines.Count; index++)
        {
            var line = _session.Document.Lines[index];
            LineSelector.Items.Add($"判定线 {line.Number}（#{line.Id}）");
        }

        if (_session.Document.Lines.Count > 0)
        {
            LineSelector.SelectedIndex = Math.Clamp(_session.CurrentLineIndex, 0, LineSelector.Items.Count - 1);
        }
    }

    private void RefreshLineList()
    {
        LineList.Items.Clear();
        foreach (var line in _session.Document.Lines)
        {
            LineList.Items.Add($"判定线 {line.Number} · 音符 {line.Notes.Count} · 事件 {line.Events.Count}");
        }

        if (_session.Document.Lines.Count > 0)
        {
            LineList.SelectedIndex = Math.Clamp(_session.CurrentLineIndex, 0, LineList.Items.Count - 1);
        }
    }

    private void RefreshBpmList()
    {
        BpmList.Items.Clear();
        foreach (var point in _session.Document.BpmList)
        {
            BpmList.Items.Add($"拍 {point.StartBeat} → {point.Bpm:0.##} BPM");
        }

        BpmBox.Value = _session.Document.BpmList.Count > 0 ? _session.Document.BpmList[0].Bpm : 120;
        SongNameBox.Text = _session.Document.SongName;
        SongArtistBox.Text = _session.Document.SongArtist;
        ChartNameBox.Text = _session.Document.ChartName;
        DifficultyBox.Text = _session.Document.Difficulty;
    }

    private void RefreshInspector()
    {
        _refreshingInspector = true;
        try
        {
            UpdateInspectorTab();
            RefreshSelectionInfo();

            var note = _session.SelectedNotes.FirstOrDefault();
            if (note is not null)
            {
                if (!ReferenceEquals(_noteEditSource, note))
                {
                    _noteEditSource = note;
                    _noteEditSnapshot = note.Clone();
                }

                NotesPanel.Visibility = Visibility.Visible;
                NoteTypeBox.SelectedIndex = (int)note.Type - 1;
                NoteStartBeatBox.Text = note.StartBeat.ToString();
                NoteEndBeatBox.Text = note.EndBeat.ToString();
                // 只有 Hold 是有长度的音符，其余为瞬时音符，不显示结束拍。
                NoteEndBeatPanel.Visibility = note.Type == NoteType.Hold ? Visibility.Visible : Visibility.Collapsed;
                NotePositionXBox.Value = note.PositionX;
                NoteFallDirectionBox.SelectedIndex = note.IsUp ? 0 : 1;
                NoteSpeedBox.Value = note.Speed;
                NotePositionYBox.Value = note.PositionY;
                NoteTruthBox.SelectedIndex = note.IsFake ? 1 : 0;
                NoteWidthBox.Value = note.Size;
                NoteVisibleTimeBox.Value = note.VisibleTime;
                NoteAlphaBox.Value = note.Alpha;
            }
            else
            {
                _noteEditSource = null;
                _noteEditSnapshot = null;
                NoteStartBeatBox.Text = string.Empty;
                NoteEndBeatBox.Text = string.Empty;
            }

            var item = _session.SelectedEvents.FirstOrDefault();
            if (item is not null)
            {
                var valueName = item.Kind switch
                {
                    EventTrackKind.X => "X 坐标",
                    EventTrackKind.Y => "Y 坐标",
                    EventTrackKind.Rotate => "旋转",
                    EventTrackKind.Alpha => "透明度",
                    EventTrackKind.Speed => "速度",
                    EventTrackKind.ScaleX => "缩放 X",
                    EventTrackKind.ScaleY => "缩放 Y",
                    EventTrackKind.Color => "颜色",
                    EventTrackKind.Paint => "着色器",
                    EventTrackKind.Text => "文本",
                    _ => "数值",
                };
                var isColor = item.Kind == EventTrackKind.Color;
                var isText = item.Kind == EventTrackKind.Text;
                var isShader = item.Kind == EventTrackKind.Paint;
                var specialValue = isColor || isText || isShader;
                EventInspectorTitle.Text = $"{valueName}事件编辑";
                EventStartValueLabel.Text = $"{valueName}起点";
                EventEndValueLabel.Text = $"{valueName}终点";
                EventStartValuePanel.Visibility = specialValue ? Visibility.Collapsed : Visibility.Visible;
                EventEndValuePanel.Visibility = specialValue ? Visibility.Collapsed : Visibility.Visible;
                EventColorValuePanel.Visibility = isColor ? Visibility.Visible : Visibility.Collapsed;
                EventTextValuePanel.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
                EventShaderPanel.Visibility = isShader ? Visibility.Visible : Visibility.Collapsed;
                EventEasingSection.Visibility = isShader ? Visibility.Collapsed : Visibility.Visible;
                EventActionGrid.Visibility = isShader ? Visibility.Collapsed : Visibility.Visible;
                EventHookedCheck.Visibility = isShader ? Visibility.Collapsed : Visibility.Visible;
                EventStartColorBox.Text = item.Color ?? "255, 255, 255";
                EventEndColorBox.Text = item.EndColor ?? item.Color ?? "255, 255, 255";
                EventStartTextBox.Text = item.StartText ?? string.Empty;
                EventEndTextBox.Text = item.EndText ?? string.Empty;
                EventShaderTypeBox.SelectedIndex = item.ShaderType switch
                {
                    "blur" => 1,
                    "glitch" => 2,
                    "custom" => 3,
                    _ => 0,
                };
                EventShaderGlobalBox.IsChecked = item.ShaderGlobal;
                EventShaderAutoAlignBox.IsChecked = item.ShaderAutoAlign;
                EventShaderOrderBox.Value = item.ShaderOrder;
                EventShaderParameterBox.SelectedIndex = item.ShaderParameter switch
                {
                    "intensity" => 1,
                    "speed" => 2,
                    _ => 0,
                };
                EventStartBeatBox.Text = item.StartBeat.ToString();
                EventEndBeatBox.Text = item.EndBeat.ToString();
                EventStartValueBox.Value = item.StartValue;
                EventEndValueBox.Value = item.EndValue;
                EventEasingBox.SelectedIndex = Math.Clamp(Math.Max(1, item.Easing) - 1, 0,
                    PhiEdit.Core.Easing.Easing.Count - 1);
                EventEasingLeftBox.Value = item.EasingLeft;
                EventEasingRightBox.Value = item.EasingRight;
                EventLinkGroupBox.Value = item.LinkedGroup;
                EventHookedCheck.IsChecked = item.IsHooked;
                EventCurvePreview.Event = isShader ? null : item;
            }
            else
            {
                EventInspectorTitle.Text = "事件属性";
                EventStartValueLabel.Text = "起点值";
                EventEndValueLabel.Text = "终点值";
                EventStartValuePanel.Visibility = Visibility.Visible;
                EventEndValuePanel.Visibility = Visibility.Visible;
                EventColorValuePanel.Visibility = Visibility.Collapsed;
                EventTextValuePanel.Visibility = Visibility.Collapsed;
                EventShaderPanel.Visibility = Visibility.Collapsed;
                EventEasingSection.Visibility = Visibility.Visible;
                EventActionGrid.Visibility = Visibility.Visible;
                EventHookedCheck.Visibility = Visibility.Visible;
                EventHookedCheck.IsChecked = false;
                EventStartBeatBox.Text = string.Empty;
                EventEndBeatBox.Text = string.Empty;
                EventEasingLeftBox.Value = 0;
                EventEasingRightBox.Value = 1;
                EventCurvePreview.Event = null;
            }
        }
        finally
        {
            _refreshingInspector = false;
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        RefreshLineSelector();
        RefreshLineList();
        RefreshInspector();
        EditorCanvas.Invalidate();
    }

    // ---------------------------------------------------------------- 播放

    private void OnTimerTick(object? sender, object e)
    {
        if (!_clock.IsPlaying)
        {
            return;
        }

        _clock.Advance(0);
        var beat = _clock.CurrentBeat;
        // 判定线固定在底部，播放时视口位置跟随播放头，内容自上而下流动。
        _session.Viewport.SetPosition(beat);
        Scrubber.Value = Math.Clamp(beat, Scrubber.Minimum, Scrubber.Maximum);
        RefreshTimeline();
        EmbeddedPreview.Invalidate();
        _previewWindow?.SyncFromEditor(beat);
    }

    // ---------------------------------------------------------------- 工具栏

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _session.Document = new ChartDocument();
        _session.Assets = new ChartAssetStore();
        _session.Document.AddLine();
        _session.CurrentLineIndex = 0;
        _currentFilePath = null;
        _session.Undo.Clear();
        _session.ClearSelection();
        _clock.Reset();
        _session.Viewport.ScrollToBeat(0);
        RefreshAll();
        StatusReadout.Text = "已新建谱面";
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenChart();

    private async void OnImportPezClick(object sender, RoutedEventArgs e) => await OpenChart(pezOnly: true);

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveChart();

    private async void OnExportClick(object sender, RoutedEventArgs e) => await ExportChart();

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _clock.Reset();
        _timer.Stop();
        _session.Viewport.ScrollToBeat(0);
        PlayGlyph.Glyph = "\uE768";
        RefreshTimeline();
    }

    /// <summary>打开独立实时预览窗口。预览内容始终来自整份谱面。</summary>
    public void OpenPreviewForStartup()
    {
        if (_previewWindow is null)
        {
            _previewWindow = new PreviewWindow(_session);
            _previewWindow.Closed += (_, _) => _previewWindow = null;
        }

        _previewWindow.SyncFromEditor(_session.Viewport.Position);
        _previewWindow.Activate();
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e) => OpenPreviewForStartup();

    /// <summary>
    /// 切换编辑网格叠加层的显示。隐藏后编辑区只剩纯实时预览画布；
    /// 显示时网格为半透明，可透过它直接看到下层的实时预览。
    /// </summary>
    private void OnGridOverlayClick(object sender, RoutedEventArgs e)
    {
        var show = EditorCanvas.Visibility == Visibility.Collapsed;
        EditorCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EditorSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GridOverlayGlyph.Glyph = show ? "\uE8A4" : "\uE7B3";
        StatusReadout.Text = show ? "已显示编辑网格" : "已隐藏编辑网格（纯预览）";
        EmbeddedPreview.Invalidate();
    }

    private void OnLineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LineSelector.SelectedIndex >= 0)
        {
            _session.CurrentLineIndex = LineSelector.SelectedIndex;
            RefreshInspector();
        }
    }

    private void OnLineListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LineList.SelectedIndex >= 0)
        {
            _session.CurrentLineIndex = LineList.SelectedIndex;
            LineSelector.SelectedIndex = LineList.SelectedIndex;
            RefreshInspector();
        }
    }

    private void OnDivisionChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (double.IsNaN(e.NewValue))
        {
            return;
        }

        _session.Division = (int)e.NewValue;
        EditorCanvas.Invalidate();
        EditorCanvas.Invalidate();
    }

    private void OnGridCountChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (double.IsNaN(e.NewValue))
        {
            return;
        }

        _session.GridCount = (int)e.NewValue;
        EditorCanvas.Invalidate();
    }

    private void OnSnapToggled(object sender, RoutedEventArgs e)
    {
        _session.SnapX = SnapToggle.IsOn;
    }

    private void OnScrubberChanged(object sender, object e)
    {
        if (_clock.IsPlaying)
        {
            return;
        }

        var value = Scrubber.Value;
        if (double.IsNaN(value))
        {
            return;
        }

        _clock.SeekToBeat(value);
        // 纵向位置唯一真源：进度条只改它，其余刷新由视口事件驱动。
        _session.Viewport.SetPosition(value);
        RefreshTimeline();
    }

    /// <summary>切换基础事件层（0–3），事件与音符选择同时清空。</summary>
    private void OnLayerButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not int layer)
        {
            return;
        }

        foreach (var child in LayerButtons.Children.OfType<ToggleButton>())
        {
            child.IsChecked = ReferenceEquals(child, button);
        }

        _session.CurrentLayer = layer;
        _session.SelectedEvents.Clear();
        _session.SelectedNotes.Clear();
        EditorCanvas.CancelPendingPlacement();
        StatusReadout.Text = $"事件层 {layer}";
        RefreshAll();
    }

    // ---------------------------------------------------------------- 属性面板

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        var tag = button.Tag as string ?? "edit";
        foreach (var other in new[] { TabEdit, TabLines, TabChart })
        {
            if (!ReferenceEquals(other, button))
            {
                other.IsChecked = false;
            }
        }

        button.IsChecked = true;
        // 回到“音符/事件”页签时必须按当前选择重建面板，
        // 否则从判定线页签切回后只会显示空白的“编辑”提示。
        if (ReferenceEquals(button, TabEdit))
        {
            RefreshInspector();
            return;
        }

        ShowInspectorTab(tag);
    }

    /// <summary>显示指定页签的内容。</summary>
    private void ShowInspectorTab(string tag)
    {
        EditHintPanel.Visibility = Visibility.Collapsed;
        NotesPanel.Visibility = Visibility.Collapsed;
        EventsPanel.Visibility = Visibility.Collapsed;
        LinesPanel.Visibility = Visibility.Collapsed;
        ChartPanel.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "notes":
                NotesPanel.Visibility = Visibility.Visible;
                break;
            case "events":
                EventsPanel.Visibility = Visibility.Visible;
                break;
            case "lines":
                LinesPanel.Visibility = Visibility.Visible;
                break;
            case "chart":
                ChartPanel.Visibility = Visibility.Visible;
                break;
            default:
                EditHintPanel.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>
    /// 同步属性页签：未选中任何对象时页签显示「编辑」且不可选；
    /// 选中音符时自动切到该页签并改名为「音符」；选中事件时改名为「事件」。
    /// </summary>
    private void UpdateInspectorTab()
    {
        var hasNote = _session.SelectedNotes.Count > 0;
        var hasEvent = _session.SelectedEvents.Count > 0;

        if (hasNote)
        {
            TabEdit.Content = "音符";
            TabEdit.IsEnabled = true;
            TabEdit.IsChecked = true;
            TabLines.IsChecked = false;
            TabChart.IsChecked = false;
            ShowInspectorTab("notes");
            return;
        }

        if (hasEvent)
        {
            TabEdit.Content = "事件";
            TabEdit.IsEnabled = true;
            TabEdit.IsChecked = true;
            TabLines.IsChecked = false;
            TabChart.IsChecked = false;
            ShowInspectorTab("events");
            return;
        }

        TabEdit.Content = "编辑";
        TabEdit.IsEnabled = false;
        if (TabEdit.IsChecked == true)
        {
            ShowInspectorTab("edit");
        }
    }

    private void OnNoteTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null || NoteTypeBox.SelectedIndex < 0)
        {
            return;
        }

        note.Type = (NoteType)(NoteTypeBox.SelectedIndex + 1);
        if (note.Type == NoteType.Hold && note.EndBeat.Value <= note.StartBeat.Value)
        {
            note.EndBeat = Beat.FromValue(note.StartBeat.Value + 1);
        }

        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
        RefreshInspector();
    }

    private void OnNotePositionXChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null || double.IsNaN(e.NewValue))
        {
            return;
        }

        note.PositionX = e.NewValue;
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    private void OnNotePositionYChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null || double.IsNaN(e.NewValue))
        {
            return;
        }

        note.PositionY = e.NewValue;
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    private void OnNoteFallDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingInspector || NoteFallDirectionBox.SelectedIndex < 0)
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        note.IsUp = NoteFallDirectionBox.SelectedIndex == 0;
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    private void OnNoteSpeedChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector || double.IsNaN(e.NewValue))
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        note.Speed = e.NewValue;
        _session.Document.IsDirty = true;
    }

    private void OnNoteTruthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingInspector || NoteTruthBox.SelectedIndex < 0)
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        note.IsFake = NoteTruthBox.SelectedIndex == 1;
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    private void OnNoteWidthChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector || double.IsNaN(e.NewValue))
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        note.Size = e.NewValue;
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    private void OnNoteVisibleTimeChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector || double.IsNaN(e.NewValue))
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        note.VisibleTime = e.NewValue;
        _session.Document.IsDirty = true;
    }

    private void OnNoteAlphaChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector || double.IsNaN(e.NewValue))
        {
            return;
        }

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        note.Alpha = Math.Clamp(e.NewValue, 0, 255);
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    private void OnCancelNoteClick(object sender, RoutedEventArgs e)
    {
        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null || _noteEditSnapshot is null)
        {
            return;
        }

        note.Type = _noteEditSnapshot.Type;
        note.StartBeat = _noteEditSnapshot.StartBeat;
        note.EndBeat = _noteEditSnapshot.EndBeat;
        note.PositionX = _noteEditSnapshot.PositionX;
        note.PositionY = _noteEditSnapshot.PositionY;
        note.IsFake = _noteEditSnapshot.IsFake;
        note.IsUp = _noteEditSnapshot.IsUp;
        note.Speed = _noteEditSnapshot.Speed;
        note.Size = _noteEditSnapshot.Size;
        note.VisibleTime = _noteEditSnapshot.VisibleTime;
        note.Alpha = _noteEditSnapshot.Alpha;
        EditorCanvas.Invalidate();
        RefreshInspector();
        StatusReadout.Text = "已取消修改";
    }

    private void OnSaveNoteClick(object sender, RoutedEventArgs e)
    {
        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        _noteEditSnapshot = note.Clone();
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
        StatusReadout.Text = "已保存音符";
    }

    /// <summary>编辑音符的起止拍。</summary>
    private void OnNoteBeatEdited(object sender, RoutedEventArgs e)
    {
        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(NoteStartBeatBox.Text))
            {
                var start = Beat.Parse(NoteStartBeatBox.Text);
                var delta = start.Value - note.StartBeat.Value;
                note.StartBeat = start;
                note.EndBeat = Beat.FromValue(note.EndBeat.Value + delta);
            }

            if (note.Type == NoteType.Hold && !string.IsNullOrWhiteSpace(NoteEndBeatBox.Text))
            {
                note.EndBeat = Beat.Parse(NoteEndBeatBox.Text);
            }
        }
        catch (FormatException exception)
        {
            StatusReadout.Text = $"拍格式不正确：{exception.Message}";
            RefreshInspector();
            return;
        }

        if (note.Type == NoteType.Hold && note.EndBeat.Value <= note.StartBeat.Value)
        {
            StatusReadout.Text = "Hold 终点必须晚于起点";
            RefreshInspector();
            return;
        }

        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
        StatusReadout.Text = "已更新音符时间";
    }

    /// <summary>编辑事件的起止拍。</summary>
    private void OnEventBeatEdited(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(EventStartBeatBox.Text))
            {
                item.StartBeat = Beat.Parse(EventStartBeatBox.Text);
            }

            if (!string.IsNullOrWhiteSpace(EventEndBeatBox.Text))
            {
                item.EndBeat = Beat.Parse(EventEndBeatBox.Text);
            }
        }
        catch (FormatException exception)
        {
            StatusReadout.Text = $"拍格式不正确：{exception.Message}";
            RefreshInspector();
            return;
        }

        if (item.EndBeat.Value <= item.StartBeat.Value)
        {
            StatusReadout.Text = "事件终点必须晚于起点";
            RefreshInspector();
            return;
        }

        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
        StatusReadout.Text = "已更新事件时间";
    }

    /// <summary>缓动下拉框支持鼠标滚轮切换选项。</summary>
    private void OnEventEasingWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(EventEasingBox).Properties.MouseWheelDelta;
        if (delta == 0 || EventEasingBox.Items.Count == 0)
        {
            return;
        }

        var next = Math.Clamp(EventEasingBox.SelectedIndex + (delta > 0 ? -1 : 1),
            0, EventEasingBox.Items.Count - 1);
        if (next != EventEasingBox.SelectedIndex)
        {
            EventEasingBox.SelectedIndex = next;
        }

        e.Handled = true;
    }

    /// <summary>编辑事件的缓动类型。</summary>
    private void OnEventEasingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || EventEasingBox.SelectedIndex < 0)
        {
            return;
        }

        item.Easing = EventEasingBox.SelectedIndex + 1;
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
        RefreshInspector();
        StatusReadout.Text = "已更新缓动类型";
    }

    /// <summary>编辑事件的绑定组。</summary>
    private void OnEventColorEdited(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || item.Kind != EventTrackKind.Color)
        {
            return;
        }

        CommitEventChange("修改颜色", item, target =>
        {
            target.Color = EventStartColorBox.Text;
            target.EndColor = EventEndColorBox.Text;
        });
    }

    private void OnEventTextEdited(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || item.Kind != EventTrackKind.Text)
        {
            return;
        }

        CommitEventChange("修改文字", item, target =>
        {
            target.StartText = EventStartTextBox.Text;
            target.EndText = EventEndTextBox.Text;
        });
    }

    private void OnEventHookedChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var hooked = EventHookedCheck.IsChecked == true;
        CommitEventChange(hooked ? "钩定事件" : "取消钩定", item, target => target.IsHooked = hooked);
    }

    private void OnEventSwapClick(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        CommitEventChange("首尾交换", item, target =>
        {
            (target.StartBeat, target.EndBeat) = (target.EndBeat, target.StartBeat);
            (target.StartValue, target.EndValue) = (target.EndValue, target.StartValue);
            (target.StartText, target.EndText) = (target.EndText, target.StartText);
            (target.Color, target.EndColor) = (target.EndColor, target.Color);
        });
    }

    private void OnEventShaderTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || item.Kind != EventTrackKind.Paint || EventShaderTypeBox.SelectedIndex < 0)
        {
            return;
        }

        var value = EventShaderTypeBox.SelectedIndex switch { 1 => "blur", 2 => "glitch", 3 => "custom", _ => "chromatic" };
        CommitEventChange("修改着色器类型", item, target => target.ShaderType = value);
    }

    private void OnEventShaderParameterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || item.Kind != EventTrackKind.Paint || EventShaderParameterBox.SelectedIndex < 0)
        {
            return;
        }

        var value = EventShaderParameterBox.SelectedIndex switch { 1 => "intensity", 2 => "speed", _ => "sampleCount" };
        CommitEventChange("修改着色器参数", item, target => target.ShaderParameter = value);
    }

    private void OnEventShaderFlagChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || item.Kind != EventTrackKind.Paint)
        {
            return;
        }

        var global = EventShaderGlobalBox.IsChecked == true;
        var autoAlign = EventShaderAutoAlignBox.IsChecked == true;
        CommitEventChange("修改着色器选项", item, target =>
        {
            target.ShaderGlobal = global;
            target.ShaderAutoAlign = autoAlign;
        });
    }

    private void OnEventShaderNumberChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector || double.IsNaN(e.NewValue))
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || item.Kind != EventTrackKind.Paint)
        {
            return;
        }

        var order = (int)e.NewValue;
        CommitEventChange("修改着色器顺序", item, target => target.ShaderOrder = order);
    }

    private void OnEventShaderCodeClick(object sender, RoutedEventArgs e) =>
        StatusReadout.Text = "着色器代码编辑器暂未接入，字段已保存在谱面中";

    private void OnEventLinkGroupChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null || double.IsNaN(e.NewValue))
        {
            return;
        }

        item.LinkedGroup = (int)e.NewValue;
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    private void OnEventValueChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector)
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        if (!double.IsNaN(EventStartValueBox.Value))
        {
            item.StartValue = EventStartValueBox.Value;
        }

        if (!double.IsNaN(EventEndValueBox.Value))
        {
            item.EndValue = EventEndValueBox.Value;
        }

        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
    }

    /// <summary>编辑缓动左右边界。</summary>
    private void OnEventEasingBoundChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_refreshingInspector || double.IsNaN(e.NewValue))
        {
            return;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var value = Math.Clamp(e.NewValue, 0, 1);
        var isLeft = ReferenceEquals(sender, EventEasingLeftBox);
        CommitEventChange(isLeft ? "修改缓动左边界" : "修改缓动右边界", item,
            target =>
            {
                if (isLeft)
                {
                    target.EasingLeft = value;
                }
                else
                {
                    target.EasingRight = value;
                }
            });
    }

    /// <summary>粘合：首值接到同类上一条事件的尾值。</summary>
    private void OnEventStickClick(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var previous = _session.CurrentLine?
            .Track(item.Kind, item.Layer)
            .Where(candidate => candidate != item && candidate.EndBeat.Value <= item.StartBeat.Value + 1e-9)
            .OrderByDescending(candidate => candidate.EndBeat.Value)
            .FirstOrDefault();

        if (previous is null)
        {
            StatusReadout.Text = "当前轨道没有可粘合的前一事件";
            return;
        }

        CommitEventChange("粘合前一事件", item, target =>
        {
            target.StartValue = previous.EndValue;
            if (target.IsHooked)
            {
                target.EndValue = previous.EndValue;
            }
        });
    }

    /// <summary>切割：按横线细分密度采样为线性小段。</summary>
    private void OnEventCutClick(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var count = _editor.CutEvent(item, Math.Max(2, _session.Division * 4));
        _session.SelectedEvents.Clear();
        StatusReadout.Text = count > 0 ? $"已切割为 {count} 段" : "零长度事件不能切割";
        RefreshAll();
    }

    /// <summary>随机缓动类型（与原版属性页的随机按钮对应）。</summary>
    private void OnEventRandomClick(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var next = 1 + Random.Shared.Next(PhiEdit.Core.Easing.Easing.Count);
        if (next == item.Easing)
        {
            next = next % PhiEdit.Core.Easing.Easing.Count + 1;
        }

        CommitEventChange("随机缓动", item, target => target.Easing = next);
    }

    /// <summary>递推：把当前事件的尾值接到同类下一条事件的首值。</summary>
    private void OnEventRecurrenceClick(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var next = _session.CurrentLine?
            .Track(item.Kind, item.Layer)
            .Where(candidate => candidate != item && candidate.StartBeat.Value >= item.EndBeat.Value - 1e-9)
            .OrderBy(candidate => candidate.StartBeat.Value)
            .FirstOrDefault();

        if (next is null)
        {
            StatusReadout.Text = "当前轨道没有可递推的后一事件";
            return;
        }

        CommitEventChange("向后递推事件", next, target =>
        {
            target.StartValue = item.EndValue;
            if (target.IsHooked)
            {
                target.EndValue = item.EndValue;
            }
        });
    }

    /// <summary>拆分：在当前判定线所在拍把事件切成两段。</summary>
    private void OnEventSplitClick(object sender, RoutedEventArgs e)
    {
        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        var split = _editor.SplitEvent(item, Beat.FromValue(_session.Viewport.Position));
        if (!split)
        {
            StatusReadout.Text = "拆分拍必须位于事件内部";
            return;
        }

        _session.SelectedEvents.Clear();
        StatusReadout.Text = "已拆分事件";
        RefreshAll();
    }

    /// <summary>把一次属性修改包装成可撤销操作。</summary>
    private void CommitEventChange(string label, LineEvent item, Action<LineEvent> mutate)
    {
        var before = item.Clone();
        _session.Undo.Push(new DelegateAction(label, () => mutate(item), () => RestoreEvent(item, before)));
        _session.Document.IsDirty = true;
        EditorCanvas.Invalidate();
        RefreshInspector();
        StatusReadout.Text = label;
    }

    private static void RestoreEvent(LineEvent target, LineEvent before)
    {
        target.Kind = before.Kind;
        target.Layer = before.Layer;
        target.StartBeat = before.StartBeat;
        target.EndBeat = before.EndBeat;
        target.StartValue = before.StartValue;
        target.EndValue = before.EndValue;
        target.Easing = before.Easing;
        target.EasingLeft = before.EasingLeft;
        target.EasingRight = before.EasingRight;
        target.LinkedGroup = before.LinkedGroup;
        target.IsHooked = before.IsHooked;
        target.Text = before.Text;
        target.Color = before.Color;
        target.EndColor = before.EndColor;
        target.StartText = before.StartText;
        target.EndText = before.EndText;
        target.ShaderType = before.ShaderType;
        target.ShaderGlobal = before.ShaderGlobal;
        target.ShaderAutoAlign = before.ShaderAutoAlign;
        target.ShaderOrder = before.ShaderOrder;
        target.ShaderParameter = before.ShaderParameter;
        target.ShaderCode = before.ShaderCode;
    }

    private void OnBpmChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (double.IsNaN(e.NewValue) || e.NewValue <= 0 || _session.Document.BpmList.Count == 0)
        {
            return;
        }

        _session.Document.BpmList[0] = _session.Document.BpmList[0] with { Bpm = e.NewValue };
        _session.Document.IsDirty = true;
        RefreshTimeline();
    }

    private void OnDeleteNoteClick(object sender, RoutedEventArgs e) => DeleteSelection();

    private void OnDeleteEventClick(object sender, RoutedEventArgs e) => DeleteSelection();

    private void OnAddLineClick(object sender, RoutedEventArgs e)
    {
        _editor.AddLine();
        _session.Document.IsDirty = true;
        RefreshAll();
    }

    private void OnRemoveLineClick(object sender, RoutedEventArgs e)
    {
        var line = _session.CurrentLine;
        if (line is null)
        {
            return;
        }

        _editor.RemoveLine(line);
        _session.CurrentLineIndex = 0;
        RefreshAll();
    }

    // ---------------------------------------------------------------- 快捷键与命令

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsTextInputFocused())
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case VirtualKey.Delete:
            case VirtualKey.D:
                DeleteSelection();
                e.Handled = true;
                break;
            case VirtualKey.Q:
            case VirtualKey.W:
            case VirtualKey.E:
            case VirtualKey.R:
                // 按下即放置；同一按键在松开前产生的重复 KeyDown 全部忽略。
                if (_heldPlacementKeys.Add(e.Key))
                {
                    StatusReadout.Text = e.Key switch
                    {
                        VirtualKey.Q => EditorCanvas.PlaceNoteAtPointer(NoteType.Tap),
                        VirtualKey.W => EditorCanvas.PlaceNoteAtPointer(NoteType.Drag),
                        VirtualKey.E => EditorCanvas.PlaceNoteAtPointer(NoteType.Flick),
                        _ => EditorCanvas.HandleRKey(),
                    };
                }

                e.Handled = true;
                break;
        }
    }

    /// <summary>松开后释放按键锁，下一次按下才能再次放置。</summary>
    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        _heldPlacementKeys.Remove(e.Key);
        e.Handled = e.Key is VirtualKey.Q or VirtualKey.W or VirtualKey.E or VirtualKey.R;
    }

    private static bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement() is TextBox or ComboBox or Microsoft.UI.Xaml.Controls.NumberBox;

    private void TogglePlay()
    {
        _clock.Toggle();
        if (_clock.IsPlaying)
        {
            PlayGlyph.Glyph = "\uE769";
            _timer.Start();
            StatusReadout.Text = "播放中";
        }
        else
        {
            PlayGlyph.Glyph = "\uE768";
            _timer.Stop();
            StatusReadout.Text = "已暂停";
        }
    }

    private void Undo()
    {
        if (!_session.Undo.CanUndo)
        {
            StatusReadout.Text = "没有可撤销的操作";
            return;
        }

        _session.Undo.Undo();
        RefreshAll();
        StatusReadout.Text = $"已撤销：{_session.Undo.RedoLabel}";
    }

    private void Redo()
    {
        if (!_session.Undo.CanRedo)
        {
            StatusReadout.Text = "没有可重做的操作";
            return;
        }

        _session.Undo.Redo();
        RefreshAll();
        StatusReadout.Text = "已重做";
    }

    private void DeleteSelection()
    {
        var count = _session.SelectedNotes.Count + _session.SelectedEvents.Count;
        if (count == 0)
        {
            StatusReadout.Text = "没有选中的对象";
            return;
        }

        _editor.DeleteSelection();
        RefreshAll();
        StatusReadout.Text = $"已删除 {count} 个对象";
    }

    // ---------------------------------------------------------------- 文件

    private IntPtr GetWindowHandle() => WindowNative.GetWindowHandle(this);

    private async Task OpenChart(bool pezOnly = false)
    {
        var picker = new FileOpenPicker();
        if (!pezOnly)
        {
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add(".pec");
        }
        picker.FileTypeFilter.Add(".pez");
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        LoadChartPath(file.Path);
    }

    /// <summary>供命令行或自动化测试直接打开谱面文件。</summary>
    public void OpenStartupFile(string path) => LoadChartPath(path);

    /// <summary>供命令行或自动化测试跳转到指定拍。</summary>
    public void SetStartupBeat(double beat)
    {
        _session.Viewport.SetPosition(beat);
        _clock.SeekToBeat(beat);
        RefreshTimeline();
    }

    private void LoadChartPath(string path)
    {
        try
        {
            ChartAssetStore assets;
            string text;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".pez" or ".zip")
            {
                var archive = File.ReadAllBytes(path);
                assets = ChartAssetStore.FromZip(archive);
                var chartName = assets.FindChart();
                var chartBytes = chartName is null ? null : assets.Resolve(chartName);
                if (chartBytes is null)
                {
                    StatusReadout.Text = "打开失败：压缩包内没有谱面 JSON";
                    return;
                }

                text = System.Text.Encoding.UTF8.GetString(chartBytes);
            }
            else
            {
                text = File.ReadAllText(path);
                assets = ChartAssetStore.FromDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            }

            var result = RpeJson.ReadWithWarnings(text);
            var document = result.Chart;
            var warnings = result.Warnings.ToList();
            var info = assets.ReadInfo();
            if ((string.IsNullOrWhiteSpace(document.SongName) || document.SongName == "Untitled") &&
                (info.TryGetValue("Name", out var infoName) || info.TryGetValue("name", out infoName)))
            {
                document.SongName = infoName;
            }

            if (string.IsNullOrWhiteSpace(document.SongArtist) &&
                (info.TryGetValue("Composer", out var infoComposer) || info.TryGetValue("composer", out infoComposer)))
            {
                document.SongArtist = infoComposer;
            }

            if ((string.IsNullOrWhiteSpace(document.ChartName) || document.ChartName == "Default") &&
                (info.TryGetValue("Charter", out var infoCharter) || info.TryGetValue("charter", out infoCharter)))
            {
                document.ChartName = infoCharter;
            }

            if (string.IsNullOrWhiteSpace(document.Difficulty) &&
                (info.TryGetValue("Level", out var infoLevel) || info.TryGetValue("level", out infoLevel)))
            {
                document.Difficulty = infoLevel;
            }

            if (string.IsNullOrWhiteSpace(document.SongFilename) &&
                (info.TryGetValue("Song", out var infoSong) || info.TryGetValue("music", out infoSong)))
            {
                document.SongFilename = infoSong;
            }

            if (string.IsNullOrWhiteSpace(document.Background) &&
                (info.TryGetValue("Picture", out var infoPicture) || info.TryGetValue("illustration", out infoPicture)))
            {
                document.Background = infoPicture;
            }

            if (document.Lines.Count == 0)
            {
                warnings.Add("谱面内没有可识别的判定线");
            }

            _session.Document = document;
            _session.Assets = assets;
            _session.CurrentLineIndex = 0;
            _session.Undo.Clear();
            _session.ClearSelection();
            _clock.Reset();
            _session.Viewport.ScrollToBeat(0);
            _currentFilePath = path;
            RefreshAll();
            StatusReadout.Text = warnings.Count == 0
                ? $"已打开 {Path.GetFileName(path)}"
                : $"已打开 {Path.GetFileName(path)} · 已忽略 {warnings.Count} 项：{string.Join("；", warnings.Take(2))}";
        }
        catch (Exception exception)
        {
            StatusReadout.Text = $"打开失败：{exception.Message}";
        }
    }

    private async Task SaveChart()
    {
        var path = _currentFilePath;
        if (string.IsNullOrEmpty(path) || Path.GetExtension(path).ToLowerInvariant() is ".pez" or ".zip")
        {
            await ExportChart();
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await FileIO.WriteTextAsync(file, RpeJson.Write(_session.Document));
            _session.Document.IsDirty = false;
            StatusReadout.Text = $"已保存 {file.Name}";
        }
        catch (Exception exception)
        {
            StatusReadout.Text = $"保存失败：{exception.Message}";
        }
    }

    private async Task ExportChart()
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(new KeyValuePair<string, IList<string>>("谱面文件", new[] { ".json" }));
        picker.SuggestedFileName = string.IsNullOrWhiteSpace(_session.Document.ChartName)
            ? "chart"
            : _session.Document.ChartName;
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await FileIO.WriteTextAsync(file, RpeJson.Write(_session.Document));
            _currentFilePath = file.Path;
            _session.Document.IsDirty = false;
            StatusReadout.Text = $"已导出 {file.Name}";
        }
        catch (Exception exception)
        {
            StatusReadout.Text = $"导出失败：{exception.Message}";
        }
    }
}

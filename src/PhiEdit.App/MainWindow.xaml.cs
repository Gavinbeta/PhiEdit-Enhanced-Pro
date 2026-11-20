using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PhiEdit.App.Controls;
using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using PhiEdit.Core.IO;
using PhiEdit.Core.Playback;
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

    private readonly EditorPalette _palette = new();

    private readonly DispatcherQueueTimer _timer;

    private string? _currentFilePath;

    public MainWindow()
    {
        _editor = new ChartEditor(_session);
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        ApplyInitialWindowPlacement();

        _clock.Tempo = _session.Document.BuildTempoMap();

        NoteSurface.Session = _session;
        NoteSurface.Editor = _editor;
        NoteSurface.Palette = _palette;
        EventSurface.Session = _session;
        EventSurface.Editor = _editor;
        EventSurface.Palette = _palette;

        NoteSurface.SelectionChanged += (_, _) => RefreshInspector();
        EventSurface.SelectionChanged += (_, _) => RefreshInspector();
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

        // 滚轮统一在窗口根节点处理：无论焦点在哪个控件，只要指针位于编辑区就应滚动时间轴。
        RootLayout.PointerWheelChanged += OnRootPointerWheelChanged;
        AttachAspectRatioLock();

        DivisionBox.Value = _session.Division;
        GridCountBox.Value = _session.GridCount;
        SnapToggle.IsOn = _session.SnapX;

        BuildSampleChart();
        RefreshAll();
    }

    // ---------------------------------------------------------------- 初始化

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
            (NoteType.Flick, 4.5, 90.0),
            (NoteType.Drag, 5.0, 300.0),
            (NoteType.Tap, 5.5, -675.0),
        };

        foreach (var (type, beat, x) in notes)
        {
            editor.PlaceNote(line, type, Beat.FromValue(beat), x);
        }

        editor.AddEvent(line, EventTrackKind.X, 0, Beat.FromValue(0), 0);
        editor.AddEvent(line, EventTrackKind.X, 0, Beat.FromValue(4), -120, 60);
        editor.AddEvent(line, EventTrackKind.Y, 0, Beat.FromValue(2), 0, 40);
        editor.AddEvent(line, EventTrackKind.Rotate, 0, Beat.FromValue(0), 0);
        editor.AddEvent(line, EventTrackKind.Alpha, 0, Beat.FromValue(0), 1);
        editor.AddEvent(line, EventTrackKind.Speed, 0, Beat.FromValue(0), 1);

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
            accelerator.Invoked += (_, _) => action();
            RootLayout.KeyboardAccelerators.Add(accelerator);
        }

        Add(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, () => _ = SaveChart());
        Add(Windows.System.VirtualKey.O, Windows.System.VirtualKeyModifiers.Control, () => _ = OpenChart());
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

    /// <summary>判断指针是否位于两个编辑区之内。</summary>
    private bool IsOverEditorArea(Point position)
    {
        foreach (var surface in new FrameworkElement[] { NoteSurface, EventSurface })
        {
            var bounds = surface.TransformToVisual(RootLayout).TransformBounds(
                new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
            if (bounds.Contains(position))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把窗口坐标转换为编辑区内部的纵坐标。</summary>
    private double EditorLocalY(Point position)
    {
        var surface = NoteSurface;
        var bounds = surface.TransformToVisual(RootLayout).TransformBounds(
            new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
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
        // 使用星号单位：窗口变窄时两个编辑区等比收缩，绝不会被右侧面板遮挡。
        EditorArea.ColumnDefinitions[0].Width = new GridLength(_editorSplitRatio, GridUnitType.Star);
        EditorArea.ColumnDefinitions[1].Width = new GridLength(6);
        EditorArea.ColumnDefinitions[2].Width = new GridLength(1 - _editorSplitRatio, GridUnitType.Star);

        if (WorkspaceArea.ColumnDefinitions.Count >= 3)
        {
            WorkspaceArea.ColumnDefinitions[2].Width = new GridLength(_inspectorWidth);
        }
    }

    // ---------------------------------------------------------------- 刷新

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
        NoteSurface.Invalidate();
        EventSurface.Invalidate();
    }


    private void RefreshAll()
    {
        RefreshSelectionInfo();
        RefreshLineSelector();
        RefreshLineList();
        RefreshBpmList();
        RefreshInspector();
        RefreshTimeline();
        NoteSurface.Invalidate();
        EventSurface.Invalidate();
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
        UpdateInspectorTab();

        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is not null)
        {
            NotesPanel.Visibility = Visibility.Visible;
            NoteTypeBox.SelectedIndex = (int)note.Type - 1;
            NoteStartBeatBox.Text = note.StartBeat.ToString();
            NoteEndBeatBox.Text = note.EndBeat.ToString();
            NotePositionXBox.Value = note.PositionX;
            NotePositionYBox.Value = note.PositionY;
        }
        else
        {
            NoteStartBeatBox.Text = string.Empty;
            NoteEndBeatBox.Text = string.Empty;
        }

        var item = _session.SelectedEvents.FirstOrDefault();
        if (item is not null)
        {
            EventTrackBox.Text = item.Kind.DisplayName();
            EventStartBeatBox.Text = item.StartBeat.ToString();
            EventEndBeatBox.Text = item.EndBeat.ToString();
            EventStartValueBox.Value = item.StartValue;
            EventEndValueBox.Value = item.EndValue;
        }
        else
        {
            EventTrackBox.Text = string.Empty;
            EventStartBeatBox.Text = string.Empty;
            EventEndBeatBox.Text = string.Empty;
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        RefreshLineSelector();
        RefreshLineList();
        RefreshInspector();
        NoteSurface.Invalidate();
        EventSurface.Invalidate();
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
    }

    // ---------------------------------------------------------------- 工具栏

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _session.Document = new ChartDocument();
        _session.Document.AddLine();
        _session.Undo.Clear();
        _session.ClearSelection();
        _clock.Reset();
        _session.Viewport.ScrollToBeat(0);
        RefreshAll();
        StatusReadout.Text = "已新建谱面";
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenChart();

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

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        var tag = button.Tag as string ?? "select";
        if (tag == "select")
        {
            // 选择模式：不放置音符，空白处拖动即框选。
            _session.Tool = null;
            SelectToolButton.IsChecked = true;
            TapToolButton.IsChecked = false;
            DragToolButton.IsChecked = false;
            FlickToolButton.IsChecked = false;
            HoldToolButton.IsChecked = false;
            StatusReadout.Text = "工具：选择（框选 / 移动）";
            return;
        }

        SelectToolButton.IsChecked = false;
        _session.Tool = tag switch
        {
            "tap" => NoteType.Tap,
            "drag" => NoteType.Drag,
            "flick" => NoteType.Flick,
            "hold" => NoteType.Hold,
            _ => NoteType.Tap,
        };

        StatusReadout.Text = $"工具：{_session.Tool}";
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
        NoteSurface.Invalidate();
        EventSurface.Invalidate();
    }

    private void OnGridCountChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (double.IsNaN(e.NewValue))
        {
            return;
        }

        _session.GridCount = (int)e.NewValue;
        NoteSurface.Invalidate();
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
        _session.Document.IsDirty = true;
        NoteSurface.Invalidate();
    }

    private void OnNotePositionXChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null || double.IsNaN(e.NewValue))
        {
            return;
        }

        note.PositionX = e.NewValue;
        _session.Document.IsDirty = true;
        NoteSurface.Invalidate();
    }

    private void OnNotePositionYChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        var note = _session.SelectedNotes.FirstOrDefault();
        if (note is null || double.IsNaN(e.NewValue))
        {
            return;
        }

        note.PositionY = e.NewValue;
        _session.Document.IsDirty = true;
        NoteSurface.Invalidate();
    }

    private void OnEventValueChanged(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
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
        EventSurface.Invalidate();
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
                SetTool(TapToolButton, NoteType.Tap);
                e.Handled = true;
                break;
            case VirtualKey.W:
                SetTool(HoldToolButton, NoteType.Hold);
                e.Handled = true;
                break;
            case VirtualKey.E:
                SetTool(FlickToolButton, NoteType.Flick);
                e.Handled = true;
                break;
            case VirtualKey.R:
                SetTool(DragToolButton, NoteType.Drag);
                e.Handled = true;
                break;
            case VirtualKey.V:
                SetTool(SelectToolButton, null);
                e.Handled = true;
                break;
        }
    }

    private static bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement() is TextBox or ComboBox or Microsoft.UI.Xaml.Controls.NumberBox;

    private void SetTool(ToggleButton button, NoteType? type)
    {
        _session.Tool = type;
        foreach (var other in new[] { SelectToolButton, TapToolButton, DragToolButton, FlickToolButton, HoldToolButton })
        {
            other.IsChecked = ReferenceEquals(other, button);
        }

        StatusReadout.Text = type is null ? "工具：选择（框选 / 移动）" : $"工具：{type}";
    }

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

    private async Task OpenChart()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".pec");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var text = await FileIO.ReadTextAsync(file);
            var document = RpeJson.Read(text);
            _session.Document = document;
            _session.Undo.Clear();
            _session.ClearSelection();
            _clock.Reset();
            _currentFilePath = file.Path;
            RefreshAll();
            StatusReadout.Text = $"已打开 {file.Name}";
        }
        catch (Exception exception)
        {
            StatusReadout.Text = $"打开失败：{exception.Message}";
        }
    }

    private async Task SaveChart()
    {
        var path = _currentFilePath;
        if (string.IsNullOrEmpty(path))
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

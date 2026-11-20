using PhiEdit.Core.Charts;
using PhiEdit.Core.Layout;
using PhiEdit.Core.Skins;

namespace PhiEdit.Core.Editing;

/// <summary>
/// 编辑会话：保存当前谱面、视图状态、选择集与工具。
/// 界面层只读取并调用这里的方法，所有改动都经由撤销栈。
/// </summary>
public sealed class EditorSession
{
    private int _currentLineIndex;

    public EditorSession(ChartDocument? document = null)
    {
        Document = document ?? new ChartDocument();
        Undo = new UndoStack();
        Viewport = new EditViewport();
        if (Document.Lines.Count == 0)
        {
            Document.AddLine();
        }
    }

    public ChartDocument Document { get; set; }

    public UndoStack Undo { get; }

    /// <summary>已安装的音符资源包，当前生效的资源包决定音符贴图。</summary>
    public NoteSkinLibrary Skins { get; } = new();

    public EditViewport Viewport { get; }

    /// <summary>每拍细分数：相邻整拍线之间的细线数量。</summary>
    public int Division { get; set; } = 4;

    /// <summary>竖线（音符横向定位网格）数量。</summary>
    public int GridCount { get; set; } = 21;

    /// <summary>是否吸附到竖线。</summary>
    public bool SnapX { get; set; } = true;

    /// <summary>是否吸附到拍细分线。</summary>
    public bool SnapTime { get; set; } = true;

    /// <summary>当前放置工具；为 null 表示处于「选择」模式（框选 / 移动，不放置音符）。</summary>
    public NoteType? Tool { get; set; } = NoteType.Tap;

    /// <summary>当前事件层。</summary>
    public int CurrentLayer { get; set; }

    /// <summary>选中的音符。</summary>
    public HashSet<Note> SelectedNotes { get; } = new();

    /// <summary>选中的事件。</summary>
    public HashSet<LineEvent> SelectedEvents { get; } = new();

    /// <summary>剪贴板中的音符（连同所属判定线）。</summary>
    public List<ClipboardNote> ClipboardNotes { get; } = new();

    /// <summary>剪贴板中的事件（连同所属判定线与轨道）。</summary>
    public List<ClipboardEvent> ClipboardEvents { get; } = new();

    /// <summary>剪贴板中最早的拍，粘贴时以此为对齐基准。</summary>
    public double ClipboardStartBeat { get; set; } = double.PositiveInfinity;

    public int CurrentLineIndex
    {
        get => _currentLineIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, Document.Lines.Count - 1));
            if (_currentLineIndex == clamped)
            {
                return;
            }

            _currentLineIndex = clamped;
            ClearSelection();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public JudgeLine? CurrentLine =>
        _currentLineIndex >= 0 && _currentLineIndex < Document.Lines.Count ? Document.Lines[_currentLineIndex] : null;

    public VerticalGrid Grid => new(GridCount);

    /// <summary>数据或选择发生变化时触发。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 选中一个音符：先取消此前的事件选择，再选中该音符。
    /// 音符与事件的选择互斥，避免出现「删掉了两个对象」的情况。
    /// </summary>
    public void SelectNote(Note note, bool additive = false)
    {
        SelectedEvents.Clear();
        if (!additive)
        {
            SelectedNotes.Clear();
        }

        SelectedNotes.Add(note);
    }

    /// <summary>选中一个事件：先取消此前的音符选择，再选中该事件。</summary>
    public void SelectEvent(LineEvent item, bool additive = false)
    {
        SelectedNotes.Clear();
        if (!additive)
        {
            SelectedEvents.Clear();
        }

        SelectedEvents.Add(item);
    }

    /// <summary>清空剪贴板。</summary>
    public void ClearClipboard()
    {
        ClipboardNotes.Clear();
        ClipboardEvents.Clear();
        ClipboardStartBeat = double.PositiveInfinity;
    }

    public void ClearSelection()
    {
        var had = SelectedNotes.Count > 0 || SelectedEvents.Count > 0;
        SelectedNotes.Clear();
        SelectedEvents.Clear();
        if (had)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}


/// <summary>剪贴板中的音符条目。</summary>
public sealed class ClipboardNote
{
    public required Note Note { get; init; }

    public required int LineId { get; init; }
}

/// <summary>剪贴板中的事件条目。</summary>
public sealed class ClipboardEvent
{
    public required LineEvent Event { get; init; }

    public required EventTrackKind Kind { get; init; }

    public required int Layer { get; init; }

    public required int LineId { get; init; }
}

using PhiEdit.Core.Charts;
using PhiEdit.Core.Layout;

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

    public EditViewport Viewport { get; }

    /// <summary>每拍细分数：相邻整拍线之间的细线数量。</summary>
    public int Division { get; set; } = 4;

    /// <summary>竖线（音符横向定位网格）数量。</summary>
    public int GridCount { get; set; } = 21;

    /// <summary>是否吸附到竖线。</summary>
    public bool SnapX { get; set; } = true;

    /// <summary>是否吸附到拍细分线。</summary>
    public bool SnapTime { get; set; } = true;

    /// <summary>当前放置工具。</summary>
    public NoteType Tool { get; set; } = NoteType.Tap;

    /// <summary>当前事件层。</summary>
    public int CurrentLayer { get; set; }

    /// <summary>选中的音符。</summary>
    public HashSet<Note> SelectedNotes { get; } = new();

    /// <summary>选中的事件。</summary>
    public HashSet<LineEvent> SelectedEvents { get; } = new();

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

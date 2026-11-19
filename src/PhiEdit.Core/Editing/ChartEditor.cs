using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.Editing;

/// <summary>
/// 谱面编辑命令集合。所有写入都通过撤销栈，保证可回退。
/// </summary>
public sealed class ChartEditor
{
    private readonly EditorSession _session;

    public ChartEditor(EditorSession session)
    {
        _session = session;
    }

    /// <summary>撤销 / 重做栈。</summary>
    public UndoStack Undo => _session.Undo;

    /// <summary>在指定判定线上放置音符。</summary>
    public Note PlaceNote(JudgeLine line, NoteType type, Beat startBeat, double positionX, double positionY = 0)
    {
        var note = new Note
        {
            Type = type,
            StartBeat = startBeat,
            EndBeat = type == NoteType.Hold ? Beat.FromValue(startBeat.Value + 1) : startBeat,
            PositionX = positionX,
            PositionY = positionY,
        };

        _session.Undo.Push(new DelegateAction(
            $"放置{type}音符",
            () => line.Notes.Add(note),
            () => line.Notes.Remove(note)));

        _session.Document.IsDirty = true;
        return note;
    }

    /// <summary>删除一组音符。</summary>
    public void RemoveNotes(IEnumerable<Note> notes)
    {
        var list = notes.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _session.Undo.Push(new DelegateAction(
            $"删除 {list.Count} 个音符",
            () => list.ForEach(note => note.OwningLine?.Notes.Remove(note)),
            () => list.ForEach(note => note.OwningLine?.Notes.Add(note))));

        foreach (var note in list)
        {
            note.OwningLine?.Notes.Remove(note);
        }

        _session.SelectedNotes.Clear();
        _session.Document.IsDirty = true;
    }

    /// <summary>移动音符到新的拍与横坐标。</summary>
    public void MoveNote(Note note, Beat newBeat, double newPositionX, double newPositionY)
    {
        var oldBeat = note.StartBeat;
        var oldX = note.PositionX;
        var oldY = note.PositionY;

        _session.Undo.Push(new DelegateAction(
            "移动音符",
            () => Apply(note, newBeat, newPositionX, newPositionY),
            () => Apply(note, oldBeat, oldX, oldY)));

        Apply(note, newBeat, newPositionX, newPositionY);
        _session.Document.IsDirty = true;
    }

    /// <summary>在判定线上新建事件。</summary>
    public LineEvent AddEvent(JudgeLine line, EventTrackKind kind, int layer, Beat startBeat, double startValue, double? endValue = null)
    {
        var item = new LineEvent
        {
            Kind = kind,
            Layer = layer,
            StartBeat = startBeat,
            EndBeat = startBeat,
            StartValue = startValue,
            EndValue = endValue ?? startValue,
        };

        _session.Undo.Push(new DelegateAction(
            $"新增{kind.DisplayName()}事件",
            () => line.AddEvent(item),
            () => line.RemoveEvent(item)));

        _session.Document.IsDirty = true;
        return item;
    }

    /// <summary>删除事件。</summary>
    public void RemoveEvents(IEnumerable<LineEvent> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _session.Undo.Push(new DelegateAction(
            $"删除 {list.Count} 个事件",
            () => list.ForEach(item => item.OwningLine?.RemoveEvent(item)),
            () => list.ForEach(item => item.OwningLine?.AddEvent(item))));

        foreach (var item in list)
        {
            item.OwningLine?.RemoveEvent(item);
        }

        _session.SelectedEvents.Clear();
        _session.Document.IsDirty = true;
    }

    /// <summary>新增判定线。</summary>
    public JudgeLine AddLine()
    {
        JudgeLine? created = null;
        _session.Undo.Push(new DelegateAction(
            "新增判定线",
            () => created = _session.Document.AddLine(),
            () =>
            {
                if (created is not null)
                {
                    _session.Document.Lines.Remove(created);
                }
            }));

        created = _session.Document.AddLine();
        _session.Document.IsDirty = true;
        return created;
    }

    /// <summary>删除判定线及其全部音符与事件。</summary>
    public void RemoveLine(JudgeLine line)
    {
        _session.Undo.Push(new DelegateAction(
            "删除判定线",
            () => _session.Document.Lines.Add(line),
            () => _session.Document.Lines.Remove(line)));

        _session.Document.Lines.Remove(line);
        _session.Document.IsDirty = true;
    }

    /// <summary>修改谱面元信息（歌曲名、作者、难度等）。</summary>
    public void UpdateMetadata(Action<ChartDocument> mutate, string label = "修改谱面信息")
    {
        var snapshot = (Document: _session.Document.SongName,
            Artist: _session.Document.SongArtist,
            Chart: _session.Document.ChartName,
            Difficulty: _session.Document.Difficulty);

        _session.Undo.Push(new DelegateAction(
            label,
            () => mutate(_session.Document),
            () =>
            {
                _session.Document.SongName = snapshot.Document;
                _session.Document.SongArtist = snapshot.Artist;
                _session.Document.ChartName = snapshot.Chart;
                _session.Document.Difficulty = snapshot.Difficulty;
            }));

        mutate(_session.Document);
        _session.Document.IsDirty = true;
    }

    private static void Apply(Note note, Beat beat, double x, double y)
    {
        var delta = beat.Value - note.StartBeat.Value;
        note.StartBeat = beat;
        note.EndBeat = Beat.FromValue(note.EndBeat.Value + delta);
        note.PositionX = x;
        note.PositionY = y;
    }
}

using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Easing;

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

    /// <summary>复制选中的音符与事件到剪贴板，返回复制数量。</summary>
    public int CopySelection()
    {
        var notes = _session.SelectedNotes.ToList();
        var events = _session.SelectedEvents.ToList();
        if (notes.Count == 0 && events.Count == 0)
        {
            return 0;
        }

        _session.ClearClipboard();
        var earliest = double.PositiveInfinity;

        foreach (var note in notes)
        {
            var line = note.OwningLine;
            if (line is null)
            {
                continue;
            }

            _session.ClipboardNotes.Add(new ClipboardNote { Note = note.Clone(), LineId = line.Id });
            earliest = Math.Min(earliest, note.StartBeat.Value);
        }

        foreach (var item in events)
        {
            var line = item.OwningLine;
            if (line is null)
            {
                continue;
            }

            _session.ClipboardEvents.Add(new ClipboardEvent
            {
                Event = item.Clone(),
                Kind = item.Kind,
                Layer = item.Layer,
                LineId = line.Id,
            });

            earliest = Math.Min(earliest, item.StartBeat.Value);
        }

        _session.ClipboardStartBeat = earliest;
        return notes.Count + events.Count;
    }

    /// <summary>剪切选中的音符与事件。</summary>
    public int CutSelection()
    {
        var count = CopySelection();
        if (count > 0)
        {
            DeleteSelection();
        }

        return count;
    }

    /// <summary>删除选中的音符与事件。</summary>
    public void DeleteSelection()
    {
        var notes = _session.SelectedNotes.ToList();
        var events = _session.SelectedEvents.ToList();
        if (notes.Count == 0 && events.Count == 0)
        {
            return;
        }

        // 约定：Redo = 执行后的状态，Undo = 执行前的状态；Push 内部会执行一次 Redo。
        _session.Undo.Push(new DelegateAction(
            $"删除 {notes.Count + events.Count} 个对象",
            () =>
            {
                foreach (var note in notes)
                {
                    note.OwningLine?.Notes.Remove(note);
                }

                foreach (var item in events)
                {
                    item.OwningLine?.RemoveEvent(item);
                }
            },
            () =>
            {
                foreach (var note in notes)
                {
                    note.OwningLine?.Notes.Add(note);
                }

                foreach (var item in events)
                {
                    item.OwningLine?.AddEvent(item);
                }
            }));

        _session.SelectedNotes.Clear();
        _session.SelectedEvents.Clear();
        _session.Document.IsDirty = true;
    }

    /// <summary>在指定拍粘贴剪贴板内容；mirror 为真时以判定线为轴镜像。</summary>
    public int PasteSelection(double beat, bool mirror = false)
    {
        if (_session.ClipboardNotes.Count == 0 && _session.ClipboardEvents.Count == 0)
        {
            return 0;
        }

        var delta = beat - _session.ClipboardStartBeat;
        var targetLine = _session.CurrentLine;
        var createdNotes = new List<Note>();
        var createdEvents = new List<LineEvent>();

        var noteTargets = new List<(Note Note, JudgeLine Line)>();
        foreach (var entry in _session.ClipboardNotes)
        {
            var line = _session.Document.FindLine(entry.LineId) ?? targetLine;
            if (line is null)
            {
                continue;
            }

            var note = entry.Note.Clone();
            note.StartBeat = Beat.FromValue(entry.Note.StartBeat.Value + delta);
            note.EndBeat = Beat.FromValue(entry.Note.EndBeat.Value + delta);
            note.PositionX = mirror ? -entry.Note.PositionX : entry.Note.PositionX;
            noteTargets.Add((note, line));
            createdNotes.Add(note);
        }

        var eventTargets = new List<(LineEvent Event, JudgeLine Line)>();
        foreach (var entry in _session.ClipboardEvents)
        {
            var line = _session.Document.FindLine(entry.LineId) ?? targetLine;
            if (line is null)
            {
                continue;
            }

            var item = entry.Event.Clone();
            item.StartBeat = Beat.FromValue(entry.Event.StartBeat.Value + delta);
            item.EndBeat = Beat.FromValue(entry.Event.EndBeat.Value + delta);
            if (mirror && entry.Kind is not EventTrackKind.Alpha)
            {
                item.StartValue = -entry.Event.StartValue;
                item.EndValue = -entry.Event.EndValue;
            }

            eventTargets.Add((item, line));
            createdEvents.Add(item);
        }

        if (createdNotes.Count == 0 && createdEvents.Count == 0)
        {
            return 0;
        }

        _session.Undo.Push(new DelegateAction(
            "粘贴",
            () =>
            {
                foreach (var (note, line) in noteTargets)
                {
                    line.Notes.Add(note);
                }

                foreach (var (item, line) in eventTargets)
                {
                    line.AddEvent(item);
                }
            },
            () =>
            {
                foreach (var (note, line) in noteTargets)
                {
                    line.Notes.Remove(note);
                }

                foreach (var (item, line) in eventTargets)
                {
                    line.RemoveEvent(item);
                }
            }));

        _session.SelectedNotes.Clear();
        _session.SelectedEvents.Clear();
        foreach (var note in createdNotes)
        {
            _session.SelectedNotes.Add(note);
        }

        foreach (var item in createdEvents)
        {
            _session.SelectedEvents.Add(item);
        }

        _session.Document.IsDirty = true;
        return createdNotes.Count + createdEvents.Count;
    }

    /// <summary>以判定线为轴镜像选中的音符与事件。</summary>
    public int MirrorSelection()
    {
        var notes = _session.SelectedNotes.ToList();
        var events = _session.SelectedEvents.ToList();
        if (notes.Count == 0 && events.Count == 0)
        {
            return 0;
        }

        var noteStates = notes
            .Select(note => (note, x: note.PositionX))
            .ToList();
        var eventStates = events
            .Select(item => (item, start: item.StartValue, end: item.EndValue))
            .ToList();

        void Apply(bool mirrored)
        {
            foreach (var (note, x) in noteStates)
            {
                note.PositionX = mirrored ? -x : x;
            }

            foreach (var (item, start, end) in eventStates)
            {
                if (item.Kind == EventTrackKind.Alpha)
                {
                    continue;
                }

                item.StartValue = mirrored ? -start : start;
                item.EndValue = mirrored ? -end : end;
            }
        }

        _session.Undo.Push(new DelegateAction(
            "镜像",
            () => Apply(true),
            () => Apply(false)));

        _session.Document.IsDirty = true;
        return notes.Count + events.Count;
    }

    /// <summary>整体平移一组音符（用于多选拖动）。</summary>
    public void MoveNotes(IEnumerable<Note> notes, double deltaBeat, double deltaX)
    {
        var list = notes.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var origins = list
            .Select(note => (note, beat: note.StartBeat.Value, end: note.EndBeat.Value, x: note.PositionX))
            .ToList();

        void Apply(double beatDelta, double xDelta)
        {
            foreach (var (note, beat, end, x) in origins)
            {
                note.StartBeat = Beat.FromValue(beat + beatDelta);
                note.EndBeat = Beat.FromValue(end + beatDelta);
                note.PositionX = x + xDelta;
            }
        }

        _session.Undo.Push(new DelegateAction(
            $"移动 {list.Count} 个音符",
            () => Apply(deltaBeat, deltaX),
            () => Apply(-deltaBeat, -deltaX)));

        _session.Document.IsDirty = true;
    }

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

    /// <summary>
    /// 在判定线上新建事件。endBeat 省略时事件为零长度（瞬时事件）；
    /// 给出 endBeat 则事件覆盖整段区间，在编辑区绘制为一条长条。
    /// </summary>
    public LineEvent AddEvent(JudgeLine line, EventTrackKind kind, int layer, Beat startBeat, double startValue,
        double? endValue = null, Beat? endBeat = null)
    {
        var end = endBeat ?? Beat.FromValue(startBeat.Value + 1);
        if (end.Value <= startBeat.Value)
        {
            end = Beat.FromValue(startBeat.Value + 1);
        }

        var item = new LineEvent
        {
            Kind = kind,
            Layer = layer,
            StartBeat = startBeat,
            EndBeat = end,
            StartValue = startValue,
            EndValue = endValue ?? startValue,
            Easing = 1,
            EasingLeft = 0,
            EasingRight = 1,
        };

        _session.Undo.Push(new DelegateAction(
            $"新增{kind.DisplayName()}事件",
            () => line.AddEvent(item),
            () => line.RemoveEvent(item)));

        _session.Document.IsDirty = true;
        return item;
    }

    /// <summary>
    /// 切割事件：按细分密度采样原缓动，生成若干线性小段（原版“切割”）。
    /// </summary>
    public int CutEvent(LineEvent item, int pieces)
    {
        if (item.DurationBeats <= 1e-9)
        {
            return 0;
        }

        var count = Math.Clamp(pieces, 2, 256);
        var start = item.StartBeat.Value;
        var duration = item.DurationBeats;
        var parts = new List<LineEvent>(count);
        for (var index = 0; index < count; index++)
        {
            var pieceStart = start + duration * index / count;
            var pieceEnd = start + duration * (index + 1) / count;
            parts.Add(CloneSegment(item, pieceStart, pieceEnd,
                ValueAt(item, pieceStart), ValueAt(item, pieceEnd)));
        }

        return ReplaceWithParts(item, parts, "切割事件");
    }

    /// <summary>
    /// 在指定拍把事件拆成两段，拆点数值由原缓动采样得到（原版“拆分”）。
    /// </summary>
    public bool SplitEvent(LineEvent item, Beat at)
    {
        if (item.DurationBeats <= 1e-9 || at.Value <= item.StartBeat.Value + 1e-9 ||
            at.Value >= item.EndBeat.Value - 1e-9)
        {
            return false;
        }

        var value = ValueAt(item, at.Value);
        var parts = new[]
        {
            CloneSegment(item, item.StartBeat.Value, at.Value, item.StartValue, value),
            CloneSegment(item, at.Value, item.EndBeat.Value, value, item.EndValue),
        };

        return ReplaceWithParts(item, parts, "拆分事件") == parts.Length;
    }

    private static LineEvent CloneSegment(LineEvent source, double start, double end, double startValue, double endValue) =>
        new()
        {
            Kind = source.Kind,
            Layer = source.Layer,
            StartBeat = Beat.FromValue(start),
            EndBeat = Beat.FromValue(end),
            StartValue = startValue,
            EndValue = endValue,
            Easing = 1,
            EasingLeft = 0,
            EasingRight = 1,
            LinkedGroup = 0,
            IsHooked = false,
            Text = source.Text,
            Color = source.Color,
        };

    private static double ValueAt(LineEvent item, double beat)
    {
        var duration = item.DurationBeats;
        if (duration <= 1e-9)
        {
            return item.EndValue;
        }

        var progress = Math.Clamp((beat - item.StartBeat.Value) / duration, 0, 1);
        var eased = PhiEdit.Core.Easing.Easing.Evaluate(progress, item.Easing, item.EasingLeft, item.EasingRight);
        return item.StartValue + (item.EndValue - item.StartValue) * eased;
    }

    private int ReplaceWithParts(LineEvent original, IReadOnlyList<LineEvent> parts, string label)
    {
        var line = original.OwningLine;
        if (line is null || parts.Count == 0)
        {
            return 0;
        }

        var list = EventListFor(original, line);
        var index = Math.Max(0, list.IndexOf(original));
        var before = list.ToList();
        var partList = parts.ToList();

        _session.Undo.Push(new DelegateAction(
            label,
            () =>
            {
                list.Remove(original);
                for (var offset = 0; offset < partList.Count; offset++)
                {
                    list.Insert(index + offset, partList[offset]);
                }
            },
            () =>
            {
                foreach (var part in partList)
                {
                    list.Remove(part);
                }

                list.Clear();
                list.AddRange(before);
            }));

        _session.Document.IsDirty = true;
        return partList.Count;
    }

    private static LineEventList EventListFor(LineEvent item, JudgeLine line)
    {
        if (item.Layer == 0)
        {
            return line.Events;
        }

        if (line.Layers.TryGetValue(item.Layer, out var tracks) && tracks.TryGetValue(item.Kind, out var list))
        {
            return list;
        }

        throw new InvalidOperationException("事件所在层不存在。");
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

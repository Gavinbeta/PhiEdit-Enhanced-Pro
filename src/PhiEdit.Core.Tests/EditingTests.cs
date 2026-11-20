using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using Xunit;

namespace PhiEdit.Core.Tests;

public class EditingTests
{
    private static (EditorSession Session, ChartEditor Editor) CreateSession()
    {
        var session = new EditorSession();
        var editor = new ChartEditor(session);
        var line = session.Document.AddLine();
        session.CurrentLineIndex = session.Document.Lines.IndexOf(line);
        return (session, editor);
    }

    [Fact]
    public void PlaceNote_AddsToLineAndIsUndoable()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;

        var note = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(1), 0);

        Assert.Single(line.Notes);
        Assert.Contains(note, session.SelectedNotes is null ? Array.Empty<Note>() : line.Notes);

        session.Undo.Undo();
        Assert.Empty(line.Notes);

        session.Undo.Redo();
        Assert.Single(line.Notes);
    }

    [Fact]
    public void RemoveNotes_CanBeRestored()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;
        var note = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(1), 0);

        editor.RemoveNotes(new[] { note });
        Assert.Empty(line.Notes);

        session.Undo.Undo();
        Assert.Single(line.Notes);
    }

    [Fact]
    public void AddEvent_IsUndoable()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;

        editor.AddEvent(line, EventTrackKind.Alpha, 0, Beat.FromValue(0), 1);
        Assert.Single(line.Track(EventTrackKind.Alpha));

        session.Undo.Undo();
        Assert.Empty(line.Track(EventTrackKind.Alpha));
    }

    [Fact]
    public void CutEvent_SamplesCurveAndIsUndoable()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;
        var item = editor.AddEvent(line, EventTrackKind.Y, 0, Beat.FromValue(0), 0, -100, Beat.FromValue(1));
        item.Easing = 2;

        var count = editor.CutEvent(item, 4);
        var parts = line.Track(EventTrackKind.Y);
        Assert.Equal(4, count);
        Assert.Equal(4, parts.Count);
        Assert.All(parts, part => Assert.Equal(1, part.Easing));
        Assert.Equal(0, parts[0].StartValue, 6);
        Assert.Equal(-100, parts[^1].EndValue, 6);

        session.Undo.Undo();
        var restored = line.Track(EventTrackKind.Y);
        Assert.Single(restored);
        Assert.Same(item, restored[0]);
    }

    [Fact]
    public void SplitEvent_UsesEasedValueAtCut()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;
        var item = editor.AddEvent(line, EventTrackKind.X, 0, Beat.FromValue(0), 0, 100, Beat.FromValue(2));

        Assert.True(editor.SplitEvent(item, Beat.FromValue(1)));
        var parts = line.Track(EventTrackKind.X).OrderBy(part => part.StartBeat.Value).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(50, parts[0].EndValue, 6);
        Assert.Equal(50, parts[1].StartValue, 6);
        Assert.Equal(2, parts[1].EndBeat.Value, 6);
    }

    [Fact]
    public void AddLine_UndoAndRedoCreateExactlyOneLine()
    {
        var (session, editor) = CreateSession();
        var before = session.Document.Lines.Count;

        var added = editor.AddLine();

        Assert.Equal(before + 1, session.Document.Lines.Count);
        session.Undo.Undo();
        Assert.Equal(before, session.Document.Lines.Count);
        Assert.DoesNotContain(added, session.Document.Lines);
        session.Undo.Redo();
        Assert.Equal(before + 1, session.Document.Lines.Count);
    }

    [Fact]
    public void RemoveLine_IsUndoableAndRedoable()
    {
        var (session, editor) = CreateSession();
        var line = session.Document.Lines[0];

        editor.RemoveLine(line);
        Assert.DoesNotContain(line, session.Document.Lines);

        session.Undo.Undo();
        Assert.Contains(line, session.Document.Lines);

        session.Undo.Redo();
        Assert.DoesNotContain(line, session.Document.Lines);
    }

    [Fact]
    public void UndoStack_ClearsRedoOnNewAction()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;

        editor.AddEvent(line, EventTrackKind.Speed, 0, Beat.FromValue(0), 1);
        session.Undo.Undo();
        Assert.True(session.Undo.CanRedo);

        editor.AddEvent(line, EventTrackKind.Speed, 0, Beat.FromValue(1), 2);
        Assert.False(session.Undo.CanRedo);
    }
}

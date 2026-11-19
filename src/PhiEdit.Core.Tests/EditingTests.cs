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

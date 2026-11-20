using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;
using PhiEdit.Core.Editing;
using Xunit;

namespace PhiEdit.Core.Tests;

public class ClipboardTests
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
    public void CopyPaste_KeepsRelativeTiming()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;

        var first = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(4), -100);
        var second = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(5), 200);
        session.SelectedNotes.Clear();
        session.SelectedNotes.Add(first);
        session.SelectedNotes.Add(second);

        Assert.Equal(2, editor.CopySelection());

        session.SelectedNotes.Clear();
        var pasted = editor.PasteSelection(10);
        Assert.Equal(2, pasted);
        Assert.Equal(4, line.Notes.Count);

        // 剪贴板最早拍为 4，粘贴到 10 拍 → 时间整体后移 6 拍
        var moved = line.Notes.Where(note => note.StartBeat.Value is > 6).ToList();
        Assert.Equal(2, moved.Count);
        Assert.Contains(moved, note => Math.Abs(note.StartBeat.Value - 10) < 1e-6);
        Assert.Contains(moved, note => Math.Abs(note.StartBeat.Value - 11) < 1e-6);
    }

    [Fact]
    public void Mirror_NegatesHorizontalPosition()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;
        var note = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(2), 300);
        session.SelectedNotes.Add(note);

        Assert.Equal(1, editor.MirrorSelection());
        Assert.Equal(-300, note.PositionX, 6);

        // 镜像同样可以撤销
        session.Undo.Undo();
        Assert.Equal(300, note.PositionX, 6);
    }

    [Fact]
    public void DeleteSelection_RemovesNotesAndEvents()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;
        var note = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(1), 0);
        var item = editor.AddEvent(line, EventTrackKind.Alpha, 0, Beat.FromValue(0), 1);
        session.SelectedNotes.Add(note);
        session.SelectedEvents.Add(item);

        editor.DeleteSelection();
        Assert.Empty(line.Notes);
        Assert.Empty(line.Track(EventTrackKind.Alpha));

        session.Undo.Undo();
        Assert.Single(line.Notes);
        Assert.Single(line.Track(EventTrackKind.Alpha));
    }

    [Fact]
    public void CutSelection_CopiesThenRemoves()
    {
        var (session, editor) = CreateSession();
        var line = session.CurrentLine!;
        var note = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(1), 0);
        session.SelectedNotes.Add(note);

        Assert.Equal(1, editor.CutSelection());
        Assert.Empty(line.Notes);
        Assert.Single(session.ClipboardNotes);

        session.Undo.Undo();
        Assert.Single(line.Notes);
    }
}

public class SelectionTests
{
    [Fact]
    public void SelectingEvent_ClearsNoteSelection()
    {
        var session = new EditorSession();
        var editor = new ChartEditor(session);
        var line = session.Document.AddLine();
        session.CurrentLineIndex = session.Document.Lines.IndexOf(line);

        var note = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(1), 0);
        var item = editor.AddEvent(line, EventTrackKind.Alpha, 0, Beat.FromValue(0), 1);

        session.SelectNote(note);
        Assert.Single(session.SelectedNotes);
        Assert.Empty(session.SelectedEvents);

        session.SelectEvent(item);
        Assert.Empty(session.SelectedNotes);
        Assert.Single(session.SelectedEvents);
    }

    [Fact]
    public void SelectingNote_ClearsEventSelection()
    {
        var session = new EditorSession();
        var editor = new ChartEditor(session);
        var line = session.Document.AddLine();
        session.CurrentLineIndex = session.Document.Lines.IndexOf(line);

        var note = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(1), 0);
        var item = editor.AddEvent(line, EventTrackKind.Speed, 0, Beat.FromValue(0), 1);

        session.SelectEvent(item);
        Assert.Single(session.SelectedEvents);

        session.SelectNote(note);
        Assert.Empty(session.SelectedEvents);
        Assert.Single(session.SelectedNotes);
    }

    [Fact]
    public void AdditiveSelection_KeepsSameKindOnly()
    {
        var session = new EditorSession();
        var editor = new ChartEditor(session);
        var line = session.Document.AddLine();
        session.CurrentLineIndex = session.Document.Lines.IndexOf(line);

        var first = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(1), 0);
        var second = editor.PlaceNote(line, NoteType.Tap, Beat.FromValue(2), 0);

        session.SelectNote(first);
        session.SelectNote(second, additive: true);

        Assert.Equal(2, session.SelectedNotes.Count);
        Assert.Empty(session.SelectedEvents);
    }
}

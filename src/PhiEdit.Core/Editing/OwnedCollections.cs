using PhiEdit.Core.Charts;

namespace PhiEdit.Core.Editing;

/// <summary>会自动回填所属判定线的音符集合。</summary>
public sealed class NoteList : List<Note>
{
    public JudgeLine? Owner { get; internal set; }

    public new void Add(Note item)
    {
        if (Owner is not null)
        {
            item.OwningLine = Owner;
        }

        base.Add(item);
    }

    public new void Insert(int index, Note item)
    {
        if (Owner is not null)
        {
            item.OwningLine = Owner;
        }

        base.Insert(index, item);
    }

    public new void AddRange(IEnumerable<Note> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }
}

/// <summary>会自动回填所属判定线的事件集合。</summary>
public sealed class LineEventList : List<LineEvent>
{
    public JudgeLine? Owner { get; internal set; }

    public new void Add(LineEvent item)
    {
        if (Owner is not null)
        {
            item.OwningLine = Owner;
        }

        base.Add(item);
    }

    public new void Insert(int index, LineEvent item)
    {
        if (Owner is not null)
        {
            item.OwningLine = Owner;
        }

        base.Insert(index, item);
    }

    public new void AddRange(IEnumerable<LineEvent> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }
}

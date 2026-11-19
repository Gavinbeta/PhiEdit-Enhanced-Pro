namespace PhiEdit.Core.Editing;

/// <summary>可撤销的操作。</summary>
public interface IUndoableAction
{
    string Label { get; }

    void Redo();

    void Undo();
}

/// <summary>
/// 撤销 / 重做栈。保留上限由历史条设置控制。
/// </summary>
public sealed class UndoStack
{
    private readonly List<IUndoableAction> _actions = new();

    private readonly List<IUndoableAction> _redo = new();

    public int Limit { get; set; } = 200;

    public bool CanUndo => _actions.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoLabel => _actions.Count > 0 ? _actions[^1].Label : null;

    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public event EventHandler? Changed;

    public void Push(IUndoableAction action)
    {
        action.Redo();
        _actions.Add(action);
        _redo.Clear();
        while (_actions.Count > Limit)
        {
            _actions.RemoveAt(0);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_actions.Count == 0)
        {
            return;
        }

        var action = _actions[^1];
        _actions.RemoveAt(_actions.Count - 1);
        action.Undo();
        _redo.Add(action);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var action = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        action.Redo();
        _actions.Add(action);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _actions.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>把一段委托包装成可撤销操作。</summary>
public sealed class DelegateAction : IUndoableAction
{
    private readonly Action _redo;
    private readonly Action _undo;

    public DelegateAction(string label, Action redo, Action undo)
    {
        Label = label;
        _redo = redo;
        _undo = undo;
    }

    public string Label { get; }

    public void Redo() => _redo();

    public void Undo() => _undo();
}

namespace PhiEdit.Core.Skins;

/// <summary>
/// 已安装的音符资源包集合。内置资源包与用户导入的资源包统一管理，
/// 当前生效的资源包通过 <see cref="Active"/> 暴露，界面只读取它来绘制音符。
/// </summary>
public sealed class NoteSkinLibrary
{
    private readonly List<NoteSkin> _skins = new();

    public IReadOnlyList<NoteSkin> Skins => _skins;

    /// <summary>当前生效的资源包。</summary>
    public NoteSkin? Active { get; private set; }

    /// <summary>资源包列表或当前选择发生变化时触发。</summary>
    public event EventHandler? Changed;

    public void Add(NoteSkin skin)
    {
        if (_skins.Any(item => item.Id == skin.Id))
        {
            return;
        }

        _skins.Add(skin);
        if (Active is null)
        {
            Active = skin;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        var skin = _skins.FirstOrDefault(item => item.Id == id);
        if (skin is null || skin.IsBuiltin)
        {
            return false;
        }

        _skins.Remove(skin);
        if (Active?.Id == id)
        {
            Active = _skins.FirstOrDefault();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Activate(string id)
    {
        var skin = _skins.FirstOrDefault(item => item.Id == id);
        if (skin is null)
        {
            return false;
        }

        Active = skin;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public NoteSkin? Find(string id) => _skins.FirstOrDefault(item => item.Id == id);
}

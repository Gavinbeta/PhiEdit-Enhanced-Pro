using PhiEdit.Core.Charts;

namespace PhiEdit.Core.Extensions;

/// <summary>
/// 谱面文件格式适配器。新增格式（JSON / PEC / PEZ 等）只需实现本接口。
/// </summary>
public interface IChartSerializer
{
    /// <summary>格式标识，例如 "rpe-json"。</summary>
    string Id { get; }

    /// <summary>界面显示名。</summary>
    string DisplayName { get; }

    /// <summary>支持读入。</summary>
    bool CanRead { get; }

    /// <summary>支持写出。</summary>
    bool CanWrite { get; }

    ChartDocument? Read(string content);

    string Write(ChartDocument chart);
}

/// <summary>
/// 放置工具提供器。新增工具（打点器、曲线笔刷等）只需实现本接口。
/// </summary>
public interface IToolProvider
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>工具对应的音符类型。</summary>
    NoteType? NoteType { get; }
}

/// <summary>
/// 编辑器扩展入口。扩展可以注册序列化器、工具以及界面面板。
/// </summary>
public interface IEditorExtension
{
    string Id { get; }

    string DisplayName { get; }

    void Register(IExtensionRegistry registry);
}

/// <summary>扩展注册表。</summary>
public interface IExtensionRegistry
{
    void AddSerializer(IChartSerializer serializer);

    void AddTool(IToolProvider tool);
}

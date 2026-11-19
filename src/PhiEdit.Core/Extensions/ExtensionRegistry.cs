using PhiEdit.Core.Extensions;

namespace PhiEdit.Core.Extensions;

/// <summary>扩展注册表的默认实现。</summary>
public sealed class ExtensionRegistry : IExtensionRegistry
{
    private readonly List<IChartSerializer> _serializers = new();

    private readonly List<IToolProvider> _tools = new();

    public IReadOnlyList<IChartSerializer> Serializers => _serializers;

    public IReadOnlyList<IToolProvider> Tools => _tools;

    public void AddSerializer(IChartSerializer serializer)
    {
        if (!_serializers.Any(item => item.Id == serializer.Id))
        {
            _serializers.Add(serializer);
        }
    }

    public void AddTool(IToolProvider tool)
    {
        if (!_tools.Any(item => item.Id == tool.Id))
        {
            _tools.Add(tool);
        }
    }

    /// <summary>装载全部扩展。</summary>
    public void Load(IEnumerable<IEditorExtension> extensions)
    {
        foreach (var extension in extensions)
        {
            extension.Register(this);
        }
    }
}

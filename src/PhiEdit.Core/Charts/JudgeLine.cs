using PhiEdit.Core.Beats;
using PhiEdit.Core.Editing;
using System.Text.Json.Nodes;

namespace PhiEdit.Core.Charts;

/// <summary>
/// 判定线。包含基础属性、按层组织的事件轨道与音符。
/// </summary>
public sealed class JudgeLine
{
    /// <summary>RPE 特殊事件层在内部使用层号 4，对应文件中的 extended。</summary>
    public const int SpecialLayer = 4;

    public int Id { get; init; }

    /// <summary>判定线编号（用于线号列表显示）。</summary>
    public int Number { get; set; }

    /// <summary>判定线所在拍。</summary>
    public Beat Time { get; set; } = Beat.Zero;

    public double X { get; set; }

    public double Y { get; set; }

    public double Rotate { get; set; }

    public double Alpha { get; set; } = 1;

    public double Speed { get; set; } = 1;

    /// <summary>父判定线索引，-1 表示无父线。</summary>
    public int Father { get; set; } = -1;

    /// <summary>绘制层级，数值越大越靠上。</summary>
    public int ZOrder { get; set; }

    /// <summary>该线事件时间缩放；不改变音频播放速度。</summary>
    public double BpmFactor { get; set; } = 1;

    /// <summary>是否继承父线旋转；null 时按 RPE 版本决定。</summary>
    public bool? RotateWithFather { get; set; } = true;

    /// <summary>是否隐藏越过判定线的音符。</summary>
    public int IsCover { get; set; } = 1;

    /// <summary>判定线锚点，默认居中。</summary>
    public double AnchorX { get; set; } = 0.5;

    public double AnchorY { get; set; } = 0.5;

    /// <summary>是否绑定到游戏 UI 而非直接绘制。</summary>
    public bool AttachUI { get; set; }

    /// <summary>控制曲线：透明度、横向位置、尺寸、斜切、纵向位置。</summary>
    public List<ControlPoint> AlphaControl { get; } = new();
    public List<ControlPoint> PosControl { get; } = new();
    public List<ControlPoint> SizeControl { get; } = new();
    public List<ControlPoint> SkewControl { get; } = new();
    public List<ControlPoint> YControl { get; } = new();

    /// <summary>读入时保留的原始判定线节点，用于透传未知扩展字段。</summary>
    public JsonObject? RawNode { get; set; }

    /// <summary>是否为占位判定线。</summary>
    public bool IsFake { get; set; }

    /// <summary>线纹理素材路径。</summary>
    public string? Texture { get; set; }

    /// <summary>线颜色。</summary>
    public string? Color { get; set; }

    /// <summary>自定义背景素材路径。</summary>
    public string? CustomBackground { get; set; }

    /// <summary>判定线底部垫底事件。</summary>
    public LineEventList Events { get; } = new();

    /// <summary>附加层事件：层号 → 轨道种类 → 事件列表。</summary>
    public Dictionary<int, Dictionary<EventTrackKind, LineEventList>> Layers { get; } = new();

    public NoteList Notes { get; } = new();

    public JudgeLine()
    {
        Events.Owner = this;
        Notes.Owner = this;
    }

    /// <summary>按轨道取出指定层的事件（保持文件顺序）。</summary>
    public List<LineEvent> Track(EventTrackKind kind, int layer = 0)
    {
        if (layer == 0)
        {
            return Events.Where(item => item.Kind == kind).ToList();
        }

        if (Layers.TryGetValue(layer, out var tracks) && tracks.TryGetValue(kind, out var list))
        {
            return list;
        }

        return new List<LineEvent>();
    }

    /// <summary>向指定层与轨道追加事件。</summary>
    public void AddEvent(LineEvent item)
    {
        if (item.Layer == 0)
        {
            Events.Add(item);
            return;
        }

        if (!Layers.TryGetValue(item.Layer, out var tracks))
        {
            tracks = new Dictionary<EventTrackKind, LineEventList>();
            Layers[item.Layer] = tracks;
        }

        if (!tracks.TryGetValue(item.Kind, out var list))
        {
            list = new LineEventList { Owner = this };
            tracks[item.Kind] = list;
        }

        list.Add(item);
    }

    public void RemoveEvent(LineEvent item)
    {
        if (item.Layer == 0)
        {
            Events.Remove(item);
            return;
        }

        if (Layers.TryGetValue(item.Layer, out var tracks) &&
            tracks.TryGetValue(item.Kind, out var list))
        {
            list.Remove(item);
        }
    }
}

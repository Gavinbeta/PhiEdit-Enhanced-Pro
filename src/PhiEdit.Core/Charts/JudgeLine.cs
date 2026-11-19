using PhiEdit.Core.Beats;
using PhiEdit.Core.Editing;

namespace PhiEdit.Core.Charts;

/// <summary>
/// 判定线。包含基础属性、按层组织的事件轨道与音符。
/// </summary>
public sealed class JudgeLine
{
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

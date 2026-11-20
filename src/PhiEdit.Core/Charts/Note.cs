using PhiEdit.Core.Beats;

namespace PhiEdit.Core.Charts;

/// <summary>
/// 音符。坐标系沿用 RPE：舞台 1350×900，原点在判定线中心，X 向右，Y 向上。
/// </summary>
public sealed class Note
{
    public NoteType Type { get; set; } = NoteType.Tap;

    /// <summary>起始拍。</summary>
    public Beat StartBeat { get; set; } = Beat.Zero;

    /// <summary>结束拍（仅 Hold 有意义）。</summary>
    public Beat EndBeat { get; set; } = Beat.Zero;

    /// <summary>相对判定线的横向坐标。</summary>
    public double PositionX { get; set; }

    /// <summary>相对判定线的纵向偏移。</summary>
    public double PositionY { get; set; }

    /// <summary>是否为占位音符。</summary>
    public bool IsFake { get; set; }

    /// <summary>音符尺寸倍率（原版 note.size），影响绘制宽度。</summary>
    public double Size { get; set; } = 1;

    /// <summary>音效素材相对路径。</summary>
    public string? HitSound { get; set; }

    /// <summary>所属判定线（由判定线在加入集合时回填）。</summary>
    public JudgeLine? OwningLine { get; internal set; }

    public Note Clone() => (Note)MemberwiseClone();

    /// <summary>Hold 的持续拍数。</summary>
    public double HoldBeats => EndBeat.Value - StartBeat.Value;

    public override string ToString() => $"{Type} @ {StartBeat}";
}

/// <summary>
/// 判定线事件（X/Y/旋转/透明度/速度等轨道上的一条事件）。
/// </summary>
public sealed class LineEvent
{
    public EventTrackKind Kind { get; set; }

    /// <summary>事件所在层，0 为基础层。</summary>
    public int Layer { get; set; }

    public Beat StartBeat { get; set; } = Beat.Zero;

    public Beat EndBeat { get; set; } = Beat.Zero;

    public double StartValue { get; set; }

    public double EndValue { get; set; }

    /// <summary>缓动编号，0 表示线性。</summary>
    public int Easing { get; set; }

    /// <summary>被同组事件共享（联动）的标识。</summary>
    public int LinkedGroup { get; set; }

    /// <summary>是否钩定（与本类型上一条事件连续）。</summary>
    public bool IsHooked { get; set; }

    /// <summary>事件附带的自定义文本（文本轨道使用）。</summary>
    public string? Text { get; set; }

    /// <summary>事件附带的颜色（颜色轨道使用）。</summary>
    public string? Color { get; set; }

    /// <summary>所属判定线（由判定线在加入集合时回填）。</summary>
    public JudgeLine? OwningLine { get; internal set; }

    public double DurationBeats => EndBeat.Value - StartBeat.Value;

    public LineEvent Clone() => (LineEvent)MemberwiseClone();
}

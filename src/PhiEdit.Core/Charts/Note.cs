using PhiEdit.Core.Beats;
using System.Text.Json.Nodes;

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

    /// <summary>下落朝向：true 为 Up（从下往上）。</summary>
    public bool IsUp { get; set; } = true;

    /// <summary>音符自身速度倍率。</summary>
    public double Speed { get; set; } = 1;

    /// <summary>可视时间（秒）。</summary>
    public double VisibleTime { get; set; } = 999999;

    /// <summary>透明度（0–255）。</summary>
    public double Alpha { get; set; } = 255;

    /// <summary>音符自身颜色覆盖（RGB，0–255）。</summary>
    public double[]? Color { get; set; }

    /// <summary>音效素材相对路径。</summary>
    public string? HitSound { get; set; }

    /// <summary>所属判定线（由判定线在加入集合时回填）。</summary>
    public JudgeLine? OwningLine { get; internal set; }

    /// <summary>读入时保留的原始音符节点，用于透传未知扩展字段。</summary>
    public JsonObject? RawNode { get; set; }

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

    /// <summary>缓动采样左边界，0 表示从曲线起点开始。</summary>
    public double EasingLeft { get; set; }

    /// <summary>缓动采样右边界，1 表示到曲线终点结束。</summary>
    public double EasingRight { get; set; } = 1;

    /// <summary>是否使用贝塞尔缓动。</summary>
    public bool Bezier { get; set; }

    /// <summary>贝塞尔控制点 [x1,y1,x2,y2]。</summary>
    public double[] BezierPoints { get; set; } = { 0, 0, 1, 1 };

    /// <summary>被同组事件共享（联动）的标识。</summary>
    public int LinkedGroup { get; set; }

    /// <summary>是否钩定（与本类型上一条事件连续）。</summary>
    public bool IsHooked { get; set; }

    /// <summary>事件附带的自定义文本（文本轨道使用）。</summary>
    public string? Text { get; set; }

    /// <summary>事件附带的颜色（颜色轨道使用）。</summary>
    public string? Color { get; set; }

    /// <summary>颜色事件的终点颜色。</summary>
    public string? EndColor { get; set; }

    /// <summary>文字事件的起止文字。</summary>
    public string? StartText { get; set; }

    public string? EndText { get; set; }

    /// <summary>着色器事件字段。</summary>
    public string? ShaderType { get; set; } = "chromatic";

    public bool ShaderGlobal { get; set; }

    public bool ShaderAutoAlign { get; set; } = true;

    public int ShaderOrder { get; set; }

    public string? ShaderParameter { get; set; } = "sampleCount";

    public string? ShaderCode { get; set; }

    /// <summary>所属判定线（由判定线在加入集合时回填）。</summary>
    public JudgeLine? OwningLine { get; internal set; }

    /// <summary>读入时保留的原始事件节点，用于透传未知扩展字段。</summary>
    public JsonObject? RawNode { get; set; }

    public double DurationBeats => EndBeat.Value - StartBeat.Value;

    public LineEvent Clone() => (LineEvent)MemberwiseClone();
}

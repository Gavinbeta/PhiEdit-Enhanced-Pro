using PhiEdit.Core.Beats;

namespace PhiEdit.Core.Charts;

/// <summary>二维坐标点，参考 noise-areas.mjs 的 {x, y}。</summary>
public readonly record struct NoisePoint(double X, double Y);

/// <summary>噪域激活区间，参考 createNoiseArea.activeIntervals 元素。</summary>
public sealed class NoiseInterval
{
    public Beat StartTime { get; set; }
    public Beat EndTime { get; set; }
    /// <summary>激活前闪烁时长（秒）。</summary>
    public double ReadyFlash { get; set; } = 0.5;
}

/// <summary>
/// 噪域事件。参考 official-noise.mjs createNoiseEvent 与 noise-areas.mjs 的 transform 扩展字段。
/// </summary>
public sealed class NoiseEvent
{
    public Beat StartTime { get; set; }
    public Beat EndTime { get; set; }

    /// <summary>事件起止值（move 为坐标，scale 为倍率，rotate 为角度）。</summary>
    public double Start { get; set; }
    public double End { get; set; }

    public int EasingType { get; set; } = 1;
    public double EasingLeft { get; set; }
    public double EasingRight { get; set; } = 1;
    public bool Bezier { get; set; }
    public double[] BezierPoints { get; set; } = { 0, 0, 1, 1 };

    /// <summary>缩放/旋转事件的锚点（逻辑坐标）。</summary>
    public NoisePoint? Anchor { get; set; }

    /// <summary>scale/rotate 的瞬时变换基准值，用于累积变换。</summary>
    public double? TransformStart { get; set; }
    public double? TransformEnd { get; set; }
    public bool TransformStep { get; set; }
}

/// <summary>
/// 噪域（旧格式 blockAreaList）。参考 rpe-next core/noise-areas.mjs 的 createNoiseArea。
/// 噪域是覆盖在音符之上的遮罩/反色区域，isInvert 为真时挖空区域内画面。
/// </summary>
public sealed class NoiseArea
{
    public string Name { get; set; } = string.Empty;

    public NoisePoint BottomLeft { get; set; } = new(-250, -150);
    public NoisePoint TopRight { get; set; } = new(250, 150);

    public Beat AppearTime { get; set; }
    public Beat DisappearTime { get; set; } = Beat.FromValue(4);

    public List<NoiseInterval> ActiveIntervals { get; } = new();

    /// <summary>是否为反色/挖空区域（旧格式 isSubtract）。</summary>
    public bool IsInvert { get; set; }

    public List<NoiseEvent> MoveXEvents { get; } = new();
    public List<NoiseEvent> MoveYEvents { get; } = new();
    public List<NoiseEvent> RotateEvents { get; } = new();
    public List<NoiseEvent> ScaleXEvents { get; } = new();
    public List<NoiseEvent> ScaleYEvents { get; } = new();

    public NoisePoint Center => new((BottomLeft.X + TopRight.X) / 2, (BottomLeft.Y + TopRight.Y) / 2);
}

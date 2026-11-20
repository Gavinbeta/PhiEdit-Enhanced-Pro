namespace PhiEdit.Core.Charts;

/// <summary>音符种类，数值与 RPE 一致。</summary>
public enum NoteType
{
    Tap = 1,
    Hold = 2,
    Flick = 3,
    Drag = 4,
}

/// <summary>判定线的事件轨道种类。</summary>
public enum EventTrackKind
{
    X,
    Y,
    Rotate,
    Alpha,
    Speed,
    ScaleX,
    ScaleY,
    Color,
    Paint,
    Text,
}

/// <summary>轨道类别归类，便于界面分组显示。</summary>
public static class EventTrackKinds
{
    /// <summary>标准轨道：判定线行为控制区默认显示的五条。</summary>
    public static readonly EventTrackKind[] Standard =
    {
        EventTrackKind.X,
        EventTrackKind.Y,
        EventTrackKind.Rotate,
        EventTrackKind.Alpha,
        EventTrackKind.Speed,
    };

    /// <summary>扩展轨道：附加在标准轨道之后。</summary>
    public static readonly EventTrackKind[] Extended =
    {
        EventTrackKind.ScaleX,
        EventTrackKind.ScaleY,
        EventTrackKind.Color,
        EventTrackKind.Paint,
        EventTrackKind.Text,
    };

    public static string Key(this EventTrackKind kind) => kind switch
    {
        EventTrackKind.X => "moveXEvents",
        EventTrackKind.Y => "moveYEvents",
        EventTrackKind.Rotate => "rotateEvents",
        EventTrackKind.Alpha => "alphaEvents",
        EventTrackKind.Speed => "speedEvents",
        EventTrackKind.ScaleX => "scaleXEvents",
        EventTrackKind.ScaleY => "scaleYEvents",
        EventTrackKind.Color => "colorEvents",
        EventTrackKind.Paint => "paintEvents",
        EventTrackKind.Text => "textEvents",
        _ => kind.ToString(),
    };

    public static EventTrackKind? FromKey(string key) => key switch
    {
        "x" or "moveXEvents" => EventTrackKind.X,
        "y" or "moveYEvents" => EventTrackKind.Y,
        "rotate" or "rotateEvents" => EventTrackKind.Rotate,
        "alpha" or "alphaEvents" => EventTrackKind.Alpha,
        "speed" or "speedEvents" => EventTrackKind.Speed,
        "scaleXEvents" => EventTrackKind.ScaleX,
        "scaleYEvents" => EventTrackKind.ScaleY,
        "colorEvents" => EventTrackKind.Color,
        "paintEvents" => EventTrackKind.Paint,
        "textEvents" => EventTrackKind.Text,
        _ => null,
    };

    public static string DisplayName(this EventTrackKind kind) => kind switch
    {
        EventTrackKind.X => "X 移动",
        EventTrackKind.Y => "Y 移动",
        EventTrackKind.Rotate => "旋转",
        EventTrackKind.Alpha => "透明度",
        EventTrackKind.Speed => "速度",
        EventTrackKind.ScaleX => "缩放 X",
        EventTrackKind.ScaleY => "缩放 Y",
        EventTrackKind.Color => "颜色",
        EventTrackKind.Paint => "着色器",
        EventTrackKind.Text => "文字",
        _ => kind.ToString(),
    };

    /// <summary>轨道默认数值格式精度。</summary>
    public static string FormatValue(this EventTrackKind kind, double value) => kind switch
    {
        EventTrackKind.Alpha => value.ToString("0.00"),
        EventTrackKind.Rotate => value.ToString("0.0"),
        _ => value.ToString("0.##"),
    };
}

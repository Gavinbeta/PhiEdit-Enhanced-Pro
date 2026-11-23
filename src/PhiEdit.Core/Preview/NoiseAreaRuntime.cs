using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.Preview;

/// <summary>
/// 噪域在某一时刻的采样结果。points 为变换后的四角（逻辑坐标 -675..675 / -450..450），
/// 顺序：左下、右下、右上、左上。参考 noise-areas.mjs NoiseAreaRuntime.sample。
/// </summary>
public sealed record NoiseSample(
    NoisePoint[] Points,
    NoisePoint Center,
    string State,
    float Alpha,
    float ActivationMix,
    bool IsInvert,
    double Rotation);

/// <summary>
/// 噪域运行时。按秒采样激活区间、移动/缩放/旋转轨道，输出变换后的多边形。
/// 移植自 rpe-next core/noise-areas.mjs 的 NoiseAreaRuntime（简化累积锚点变换）。
/// </summary>
public sealed class NoiseAreaRuntime
{
    private static readonly string[] StateRank = { "disabled", "ready", "active" };

    private readonly NoiseArea _area;
    private readonly TempoMap _tempo;
    private readonly double _appear;
    private readonly double _disappear;
    private readonly List<(double Start, double End, double ReadyFlash)> _intervals;
    private readonly EventTrack _moveX;
    private readonly EventTrack _moveY;
    private readonly EventTrack _rotate;
    private readonly EventTrack _scaleX;
    private readonly EventTrack _scaleY;
    private readonly NoisePoint _center;
    private readonly NoisePoint[] _corners;

    public NoiseAreaRuntime(NoiseArea area, TempoMap tempo)
    {
        _area = area;
        _tempo = tempo;
        _appear = tempo.SecondsAt(area.AppearTime.Value);
        _disappear = tempo.SecondsAt(area.DisappearTime.Value);
        _intervals = area.ActiveIntervals
            .Select(interval =>
            {
                var start = Math.Max(_appear, tempo.SecondsAt(interval.StartTime.Value));
                var end = Math.Min(_disappear, tempo.SecondsAt(interval.EndTime.Value));
                return (Start: start, End: end, interval.ReadyFlash);
            })
            .Where(item => item.End > item.Start)
            .OrderBy(item => item.Start)
            .ToList();

        _moveX = new EventTrack(area.MoveXEvents.Select(ToLineEvent), tempo, 1, area.Center.X);
        _moveY = new EventTrack(area.MoveYEvents.Select(ToLineEvent), tempo, 1, area.Center.Y);
        _rotate = new EventTrack(area.RotateEvents.Select(ToLineEvent), tempo, 1, 0.0);
        _scaleX = new EventTrack(area.ScaleXEvents.Select(ToLineEvent), tempo, 1, 1.0);
        _scaleY = new EventTrack(area.ScaleYEvents.Select(ToLineEvent), tempo, 1, 1.0);

        _center = area.Center;
        var left = Math.Min(area.BottomLeft.X, area.TopRight.X);
        var right = Math.Max(area.BottomLeft.X, area.TopRight.X);
        var bottom = Math.Min(area.BottomLeft.Y, area.TopRight.Y);
        var top = Math.Max(area.BottomLeft.Y, area.TopRight.Y);
        _corners = new[]
        {
            new NoisePoint(left, bottom), new NoisePoint(right, bottom),
            new NoisePoint(right, top), new NoisePoint(left, top),
        };
    }

    private static LineEvent ToLineEvent(NoiseEvent item) => new()
    {
        Kind = EventTrackKind.X,
        StartBeat = item.StartTime,
        EndBeat = item.EndTime,
        StartValue = item.Start,
        EndValue = item.End,
        Easing = item.EasingType,
        EasingLeft = item.EasingLeft,
        EasingRight = item.EasingRight,
        Bezier = item.Bezier,
        BezierPoints = item.BezierPoints,
    };

    public NoiseSample? Sample(double seconds)
    {
        if (seconds + 1e-9 < _appear || seconds >= _disappear)
        {
            return null;
        }

        // 激活状态：active / ready（激活前闪烁）/ disabled
        var active = _intervals.LastOrDefault(item => seconds + 1e-9 >= item.Start && seconds + 1e-9 < item.End);
        var next = _intervals.FirstOrDefault(item => item.Start > seconds);
        var state = "disabled";
        var activationMix = 0.0;
        if (active.Start < active.End && seconds >= active.Start && seconds < active.End)
        {
            state = "active";
            activationMix = 1;
        }
        else if (next.Start > 0 && next.ReadyFlash > 0 && seconds >= next.Start - next.ReadyFlash)
        {
            state = "ready";
            var phase = (seconds - (next.Start - next.ReadyFlash)) / next.ReadyFlash;
            activationMix = (1 + Math.Cos(Math.Clamp(phase, 0, 1) * Math.PI * 6)) / 2;
        }

        var alpha = (float)(0.4 + (0.667 - 0.4) * activationMix);

        var moveX = _moveX.Number(seconds);
        var moveY = _moveY.Number(seconds);
        var rotation = _rotate.Number(seconds) * Math.PI / 180;
        var scaleX = _scaleX.Number(seconds);
        var scaleY = _scaleY.Number(seconds);
        if (Math.Abs(scaleX) < 1e-9 || Math.Abs(scaleY) < 1e-9)
        {
            return null;
        }

        var move = new NoisePoint(moveX - _center.X, moveY - _center.Y);
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);

        NoisePoint Transform(NoisePoint point)
        {
            var dx = scaleX * (point.X - _center.X);
            var dy = scaleY * (point.Y - _center.Y);
            var rx = cos * dx + sin * dy;
            var ry = -sin * dx + cos * dy;
            return new NoisePoint(_center.X + rx + move.X, _center.Y + ry + move.Y);
        }

        var points = _corners.Select(Transform).ToArray();
        return new NoiseSample(points, Transform(_center), state, alpha, (float)activationMix, _area.IsInvert, rotation);
    }
}

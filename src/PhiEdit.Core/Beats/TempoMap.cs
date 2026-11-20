namespace PhiEdit.Core.Beats;

/// <summary>
/// 一条 BPM 断点。
/// </summary>
public sealed record TempoPoint(Beat StartBeat, double Bpm)
{
    public double SecondsPerBeat => 60.0 / Bpm;
}

/// <summary>
/// 速度映射表：拍与秒之间的双向换算，支持变速点。
/// </summary>
public sealed class TempoMap
{
    private readonly List<TempoPoint> _points;
    private readonly List<double> _cumulativeSeconds;

    public TempoMap(IEnumerable<TempoPoint> points)
    {
        _points = points.OrderBy(point => point.StartBeat.Value).ToList();
        if (_points.Count == 0)
        {
            _points.Add(new TempoPoint(Beat.Zero, 120));
        }

        if (_points[0].StartBeat.Value > 0)
        {
            _points.Insert(0, new TempoPoint(Beat.Zero, _points[0].Bpm));
        }

        _cumulativeSeconds = new List<double>(_points.Count);
        var seconds = 0.0;
        for (var index = 0; index < _points.Count; index++)
        {
            _cumulativeSeconds.Add(seconds);
            if (index + 1 < _points.Count)
            {
                seconds += (_points[index + 1].StartBeat.Value - _points[index].StartBeat.Value) * _points[index].SecondsPerBeat;
            }
        }
    }

    public IReadOnlyList<TempoPoint> Points => _points;

    public static TempoMap Default { get; } = new(new[] { new TempoPoint(Beat.Zero, 120) });

    /// <summary>拍 → 秒。</summary>
    public double SecondsAt(double beat) => SecondsAt(beat, 1);

    /// <summary>
    /// 拍 → 秒，并应用判定线的 bpmfactor。与参考源码 tempo.seconds(beat, factor) 一致。
    /// </summary>
    public double SecondsAt(double beat, double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor))
        {
            factor = 1;
        }

        return SecondsAtCore(beat) * factor;
    }

    private double SecondsAtCore(double beat)
    {
        var index = IndexForBeat(beat);
        return _cumulativeSeconds[index] + (beat - _points[index].StartBeat.Value) * _points[index].SecondsPerBeat;
    }

    /// <summary>秒 → 拍。</summary>
    public double BeatAt(double seconds) => BeatAt(seconds, 1);

    /// <summary>秒 → 拍，并应用判定线的 bpmfactor。</summary>
    public double BeatAt(double seconds, double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor))
        {
            factor = 1;
        }

        return BeatAtCore(seconds / factor);
    }

    private double BeatAtCore(double seconds)
    {
        var index = 0;
        for (var i = 0; i < _points.Count; i++)
        {
            if (_cumulativeSeconds[i] <= seconds)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        var point = _points[index];
        return point.StartBeat.Value + (seconds - _cumulativeSeconds[index]) / point.SecondsPerBeat;
    }

    public double BpmAt(double beat) => _points[IndexForBeat(beat)].Bpm;

    /// <summary>给定结束拍时的谱面时长（秒）。</summary>
    public double DurationToBeat(double lastBeat) => SecondsAt(lastBeat);

    private int IndexForBeat(double beat)
    {
        var index = 0;
        for (var i = 0; i < _points.Count; i++)
        {
            if (_points[i].StartBeat.Value <= beat)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        return index;
    }
}

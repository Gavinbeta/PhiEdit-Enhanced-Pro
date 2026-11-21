using PhiEdit.Core.Beats;
using PhiEdit.Core.Charts;

namespace PhiEdit.Core.Preview;

/// <summary>某一时刻的判定线状态，字段与参考 scene.lineRuntime.state() 一一对应。</summary>
public sealed record LineState(
    double X,
    double Y,
    double Rotation,
    double Alpha,
    double ScaleX,
    double ScaleY,
    double[] Color,
    string Text,
    double Incline,
    double Floor);

/// <summary>某一时刻的音符状态，字段与参考 scene.lineRuntime.noteState() 一一对应。</summary>
public sealed record NoteState(
    double X,
    double Y,
    double Tail,
    double Size,
    double SizeY,
    double Alpha,
    double Skew,
    bool ShowHead);

/// <summary>编译后的音符时间条目。</summary>
public sealed record NoteEntry(
    Note Note,
    int Index,
    double Start,
    double End,
    double StartFloor,
    double EndFloor,
    double Floor,
    double Tail);

/// <summary>一次打击特效触发。</summary>
public sealed record HitMoment(NoteEntry Entry, double Time, int Seed)
{
    public const double HoldInterval = 20.0 / 120.0;
}

/// <summary>
/// 单条判定线的运行时状态计算。移植自参考源码 core/scene.mjs 的 LineRuntime。
/// </summary>
public sealed class LineRuntime
{
    private readonly JudgeLine _line;
    private readonly TempoMap _tempo;
    private readonly double _factor;
    private readonly List<EventTrack> _xTracks;
    private readonly List<EventTrack> _yTracks;
    private readonly List<EventTrack> _rotateTracks;
    private readonly List<EventTrack> _alphaTracks;
    private readonly List<SpeedIntegral> _speeds;
    private readonly EventTrack _scaleX;
    private readonly EventTrack _scaleY;
    private readonly EventTrack _color;
    private readonly EventTrack _text;
    private readonly EventTrack _incline;
    private readonly ControlCurve _alphaControl;
    private readonly ControlCurve _posControl;
    private readonly ControlCurve _sizeControl;
    private readonly ControlCurve _skewControl;
    private readonly ControlCurve _yControl;
    private readonly List<NoteEntry> _notes;
    private readonly IntervalIndex<NoteEntry> _index;
    private readonly IntervalIndex<NoteEntry> _staticIndex;
    private readonly IntervalIndex<NoteEntry> _holdIndex;
    private readonly IReadOnlyList<NoteEntry> _hitTimes;
    private readonly double _minSpeed;
    private readonly bool _hasYControl;
    private readonly bool _hasAlphaEvents;
    private readonly bool[] _legacyAlpha;

    public LineRuntime(JudgeLine line, TempoMap tempo)
    {
        _line = line;
        _tempo = tempo;
        _factor = line.BpmFactor > 0 ? line.BpmFactor : 1;

        _xTracks = StandardTracks(EventTrackKind.X, 0);
        _yTracks = StandardTracks(EventTrackKind.Y, 0);
        _rotateTracks = StandardTracks(EventTrackKind.Rotate, 0);
        _alphaTracks = StandardTracks(EventTrackKind.Alpha, 0);
        _hasAlphaEvents = _alphaTracks.Any(track => track.HasEvents);
        _legacyAlpha = _alphaTracks.Select(track => track.IsLegacyAlpha).ToArray();
        _speeds = Layers().Select(layer => new SpeedIntegral(EventsOf(layer, EventTrackKind.Speed), tempo, _factor)).ToList();
        if (_speeds.Count == 0)
        {
            _speeds.Add(new SpeedIntegral(Array.Empty<LineEvent>(), tempo, _factor));
        }

        var defaultColor = line.AttachUI ||
                           line.Layers.TryGetValue(JudgeLine.SpecialLayer, out var special) && special.ContainsKey(EventTrackKind.Text) ||
                           !string.IsNullOrEmpty(line.Texture) && line.Texture != "line.png"
            ? new[] { 255.0, 255.0, 255.0 }
            : new[] { 241.0, 216.0, 148.0 };

        _scaleX = Extended(EventTrackKind.ScaleX, 1);
        _scaleY = Extended(EventTrackKind.ScaleY, 1);
        _color = Extended(EventTrackKind.Color, defaultColor);
        _text = Extended(EventTrackKind.Text, string.Empty);
        _incline = Extended(EventTrackKind.Incline, 0);

        _alphaControl = new ControlCurve(line.AlphaControl, "alpha");
        _posControl = new ControlCurve(line.PosControl, "pos");
        _sizeControl = new ControlCurve(line.SizeControl, "size");
        _skewControl = new ControlCurve(line.SkewControl, "skew");
        _yControl = new ControlCurve(line.YControl, "y");

        _notes = line.Notes.Select((note, index) => NoteEntry(note, index)).ToList();
        _index = new IntervalIndex<NoteEntry>(
            _notes.Where(entry => entry.Note.Speed != 0),
            entry => Math.Min(entry.Floor, entry.Tail),
            entry => Math.Max(entry.Floor, entry.Tail));
        _staticIndex = new IntervalIndex<NoteEntry>(
            _notes.Where(entry => entry.Note.Speed == 0),
            entry => entry.Start - (entry.Note.VisibleTime > 0 ? entry.Note.VisibleTime : 999999),
            entry => entry.End);
        _holdIndex = new IntervalIndex<NoteEntry>(
            _notes.Where(entry => entry.Note.Type == NoteType.Hold),
            entry => entry.Start,
            entry => entry.End);
        _hitTimes = _notes.OrderBy(entry => entry.Start).ToList();
        _minSpeed = _notes.Aggregate(1.0, (minimum, entry) =>
            Math.Min(minimum, Math.Abs(double.IsFinite(entry.Note.Speed) && entry.Note.Speed != 0 ? entry.Note.Speed : 1)));
        _hasYControl = line.YControl.Count > 1 && line.YControl.Any(point => Math.Abs(point.Y - 1) > 1e-6);
    }

    public JudgeLine Line => _line;

    public IReadOnlyList<NoteEntry> Notes => _notes;

    public bool HasScaleYEvents => AllEvents(EventTrackKind.ScaleY).Any();

    public bool HasColorEvents => AllEvents(EventTrackKind.Color).Any();

    public bool HasTextEvents => AllEvents(EventTrackKind.Text).Any();

    public double Floor(double seconds) => _speeds.Sum(speed => speed.Distance(seconds));

    public LineState State(double seconds)
    {
        // 参考 scene.mjs：判定线 alpha 只由 alphaEvents 求和得到；
        // 没有事件时才沿用判定线的基础 alpha，兼容本程序早期写出的谱面。
        var alpha = _hasAlphaEvents
            ? _alphaTracks.Select((track, index) =>
                track.Number(seconds) * (_legacyAlpha[index] ? 255 : 1)).Sum()
            : _line.Alpha * 255;
        var colorValue = _color.Value(seconds);
        var color = colorValue as double[] ?? (EventTrack.ToDouble(colorValue) is var gray && gray != 0
            ? new[] { gray, gray, gray }
            : EventTrack.ParseRgb(null));
        return new LineState(
            _xTracks.Sum(track => track.Number(seconds)),
            _yTracks.Sum(track => track.Number(seconds)),
            _rotateTracks.Sum(track => track.Number(seconds)),
            alpha,
            _scaleX.Number(seconds),
            _scaleY.Number(seconds),
            color,
            _text.Value(seconds) as string ?? string.Empty,
            _incline.Number(seconds),
            Floor(seconds));
    }

    public IReadOnlyList<NoteEntry> VisibleNotes(double seconds, LineState state, double radius = 1600)
    {
        if (state.Alpha < 0)
        {
            return Array.Empty<NoteEntry>();
        }

        var reach = radius / Math.Max(0.01, _minSpeed);
        var entries = _hasYControl
            ? (IEnumerable<NoteEntry>)_notes
            : _index.Query(state.Floor - reach, state.Floor + reach)
                .Select(item => item.Item)
                .Concat(_staticIndex.Query(seconds, seconds)
                    .OrderBy(item => item.Index)
                    .Select(item => item.Item));

        return entries.Where(entry => IsVisible(entry, seconds, state)).ToList();
    }

    private bool IsVisible(NoteEntry entry, double seconds, LineState state)
    {
        if (entry.End < seconds || entry.Start - seconds > entry.Note.VisibleTime)
        {
            return false;
        }

        return !(_line.IsCover == 1 && entry.Note.Speed != 0 &&
                 (entry.Note.Type == NoteType.Hold ? entry.Tail : entry.Floor) < state.Floor);
    }

    /// <summary>
    /// 参考 core/hit-effects.mjs recentHits：只取时间窗内的音符与 Hold 脉冲。
    /// </summary>
    public IReadOnlyList<HitMoment> RecentHits(double seconds, double lifetime = 2.0 / 3.0, double since = double.NegativeInfinity)
    {
        var lower = Math.Max(seconds - lifetime, since);
        var result = new List<HitMoment>();

        if (double.IsFinite(lower))
        {
            var first = UpperBound(_hitTimes, lower, entry => entry.Start);
            var last = UpperBound(_hitTimes, seconds, entry => entry.Start);
            for (var index = first; index < last; index++)
            {
                var entry = _hitTimes[index];
                if (entry.Note.Type != NoteType.Hold && !entry.Note.IsFake)
                {
                    result.Add(new HitMoment(entry, entry.Start, index));
                }
            }
        }

        foreach (var item in _holdIndex.Query(seconds - lifetime, seconds))
        {
            var entry = item.Item;
            if (entry.Note.IsFake)
            {
                continue;
            }

            var firstPulse = Math.Max(0,
                (int)Math.Floor((Math.Max(seconds - lifetime, since) - entry.Start) / HitMoment.HoldInterval) + 1);
            var lastPulse = Math.Min(
                (int)Math.Floor((seconds - entry.Start) / HitMoment.HoldInterval),
                (int)Math.Ceiling((entry.End - entry.Start) / HitMoment.HoldInterval) - 1);
            for (var pulse = firstPulse; pulse <= lastPulse; pulse++)
            {
                result.Add(new HitMoment(entry, entry.Start + pulse * HitMoment.HoldInterval, item.Index * 65537 + pulse));
            }
        }

        return result;
    }

    private static int UpperBound<T>(IReadOnlyList<T> values, double value, Func<T, double> selector)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (value < selector(values[middle]))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    public NoteState NoteState(NoteEntry entry, LineState state, double seconds)
    {
        var note = entry.Note;
        var activeHold = note.Type == NoteType.Hold && entry.Start <= seconds;
        var offset = note.PositionY;
        var distance = activeHold ? offset : entry.StartFloor - state.Floor + offset;
        var speed = note.Speed == 0 ? 0 : note.Speed;
        var side = note.IsUp ? 1 : -1;
        var isHold = note.Type == NoteType.Hold;
        var sizeY = isHold ? 1 : _sizeControl.Value(distance);
        var horizontal = note.PositionX * (isHold ? 1 : _posControl.Value(distance));
        var vertical = distance * speed * side * (isHold ? 1 : _yControl.Value(distance));
        if (!isHold && Math.Abs(state.Incline) > 0.01)
        {
            horizontal -= Math.Tan(note.PositionX / 675 * state.Incline * Math.PI / 180) * distance * speed * side;
        }

        return new NoteState(
            horizontal,
            vertical,
            (entry.EndFloor - state.Floor + offset) * speed * side,
            note.Size * sizeY,
            sizeY,
            note.Alpha / 255 * _alphaControl.Value(distance),
            isHold ? 0 : note.PositionX * _skewControl.Value(distance),
            !activeHold);
    }

    private NoteEntry NoteEntry(Note note, int index)
    {
        var start = _tempo.SecondsAt(note.StartBeat.Value, _factor);
        var end = _tempo.SecondsAt(note.EndBeat.Value, _factor);
        var startFloor = Floor(start);
        var endFloor = Floor(end);
        return new NoteEntry(note, index, start, end, startFloor, endFloor,
            startFloor + note.PositionY, endFloor + note.PositionY);
    }

    private List<EventTrack> StandardTracks(EventTrackKind kind, double fallback) =>
        Layers().Select(layer => new EventTrack(EventsOf(layer, kind), _tempo, _factor, fallback)).ToList();

    private EventTrack Extended(EventTrackKind kind, object fallback) =>
        new(AllEvents(kind), _tempo, _factor, fallback);

    private IEnumerable<int> Layers() => Enumerable.Range(0, 4).Concat(_line.Layers.Keys.Where(index => index != 0));

    private IEnumerable<LineEvent> EventsOf(int layer, EventTrackKind kind)
    {
        if (layer == 0)
        {
            return _line.Events.Where(item => item.Kind == kind);
        }

        return _line.Layers.TryGetValue(layer, out var tracks) && tracks.TryGetValue(kind, out var list)
            ? list
            : Enumerable.Empty<LineEvent>();
    }

    private IEnumerable<LineEvent> AllEvents(EventTrackKind kind) =>
        EventsOf(0, kind).Concat(_line.Layers.Values.SelectMany(tracks =>
            tracks.TryGetValue(kind, out var list) ? list : Enumerable.Empty<LineEvent>()));

}

/// <summary>
/// 整谱面运行时。状态计算与当前编辑器选中的判定线无关，始终包含所有判定线。
/// </summary>
public sealed class SceneRuntime
{
    private ChartDocument? _chart;
    private TempoMap? _tempo;
    private long _generation;
    private long _compiledGeneration = -1;

    public IReadOnlyList<LineRuntime> Lines { get; private set; } = Array.Empty<LineRuntime>();

    public IReadOnlyList<int> Order { get; private set; } = Array.Empty<int>();

    /// <summary>同一时刻有多个音符时，这些音符使用高亮贴图，与参考 simultaneousNotes 一致。</summary>
    public HashSet<Note> Simultaneous { get; private set; } = new();

    /// <summary>谱面被编辑后调用，下一帧重新编译运行时。</summary>
    public void MarkDirty() => _generation++;

    public void Compile(ChartDocument chart, TempoMap tempo)
    {
        if (ReferenceEquals(_chart, chart) && ReferenceEquals(_tempo, tempo) && _generation == _compiledGeneration)
        {
            return;
        }

        _chart = chart;
        _tempo = tempo;
        Lines = chart.Lines.Select(line => new LineRuntime(line, tempo)).ToList();
        Order = Enumerable.Range(0, Lines.Count)
            .OrderBy(index => Lines[index].Line.ZOrder)
            .ThenBy(index => index)
            .ToList();

        var counts = new Dictionary<long, int>();
        var times = new Dictionary<Note, long>();
        foreach (var runtime in Lines)
        {
            foreach (var entry in runtime.Notes)
            {
                times[entry.Note] = (long)Math.Round(entry.Start * 1_000_000);
                var key = times[entry.Note];
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        }

        Simultaneous = times.Where(pair => counts[pair.Value] > 1).Select(pair => pair.Key).ToHashSet();
        _compiledGeneration = _generation;
    }

    /// <summary>
    /// 参考 scene.mjs sampler：返回按需解析的采样器，只在真正需要某条线时才计算其状态。
    /// 打击特效等场景只需少数几条线的历史状态，避免每个时间点都重算整份谱面。
    /// </summary>
    public Func<int, LineState?> Sampler(double seconds, (double X, double Y) positionScale = default)
    {
        if (_chart is null || Lines.Count == 0)
        {
            return _ => null;
        }

        var scaleX = positionScale.X == 0 ? 1 : positionScale.X;
        var scaleY = positionScale.Y == 0 ? 1 : positionScale.Y;
        var states = new LineState?[Lines.Count];
        var resolving = new HashSet<int>();

        LineState? Resolve(int index)
        {
            if (index < 0 || index >= Lines.Count || states[index] is not null)
            {
                return index >= 0 && index < Lines.Count ? states[index] : null;
            }

            if (!resolving.Add(index))
            {
                // 与参考 scene.mjs 一致：父线成环时返回当前线已应用 positionScale 的局部状态。
                return states[index];
            }

            var state = Lines[index].State(seconds);
            state = state with { X = state.X * scaleX, Y = state.Y * scaleY };
            states[index] = state;
            var father = Lines[index].Line.Father;
            if (father >= 0 && father < Lines.Count)
            {
                var parent = Resolve(father);
                if (parent is not null)
                {
                    var angle = -parent.Rotation * Math.PI / 180;
                    var cosine = Math.Cos(angle);
                    var sine = Math.Sin(angle);
                    var x = state.X;
                    var y = state.Y;
                    state = state with
                    {
                        X = parent.X + x * cosine - y * sine,
                        Y = parent.Y + x * sine + y * cosine,
                        Rotation = state.Rotation + (Lines[index].Line.RotateWithFather ?? (_chart.RpeVersion >= 163) ? parent.Rotation : 0),
                    };
                }
            }

            resolving.Remove(index);
            states[index] = state;
            return state;
        }

        return Resolve;
    }

    public IReadOnlyList<LineState> Sample(double seconds, (double X, double Y) positionScale = default)
    {
        if (Lines.Count == 0)
        {
            return Array.Empty<LineState>();
        }

        var sampler = Sampler(seconds, positionScale);
        var result = new LineState[Lines.Count];
        for (var index = 0; index < Lines.Count; index++)
        {
            result[index] = sampler(index)!;
        }

        return result;
    }
}

/// <summary>预览舞台坐标换算，移植自参考 core/editor-display.mjs。</summary>
public readonly record struct PreviewViewport(double Scale, double Width, double Height, double Left, double Top)
{
    public static PreviewViewport Create(double width, double height, double ratio = 1.5)
    {
        var logicalWidth = Math.Min(1350, 900 * ratio);
        var logicalHeight = Math.Min(900, 1350 / ratio);
        var scale = Math.Min(width / Math.Max(1, logicalWidth), height / Math.Max(1, logicalHeight));
        var actualWidth = logicalWidth * scale;
        var actualHeight = logicalHeight * scale;
        return new PreviewViewport(scale, actualWidth, actualHeight,
            (width - actualWidth) / 2, (height - actualHeight) / 2);
    }

    public static (double X, double Y) PositionScale(double ratio = 1.5) =>
        (Math.Min(1, ratio / 1.5), Math.Min(1, 1.5 / ratio));
}

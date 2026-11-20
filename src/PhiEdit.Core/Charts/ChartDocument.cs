using PhiEdit.Core.Beats;
using System.Text.Json.Nodes;

namespace PhiEdit.Core.Charts;

/// <summary>
/// 谱面文档。承载元信息、速度表与全部判定线。
/// </summary>
public sealed class ChartDocument
{
    public string SongName { get; set; } = "Untitled";

    public string SongArtist { get; set; } = "";

    public string ChartName { get; set; } = "Default";

    public string Difficulty { get; set; } = "";

    public string SongFilename { get; set; } = "";

    public double SongDuration { get; set; }

    /// <summary>文件格式版本，写出时使用。</summary>
    public int RpeVersion { get; set; } = 240;

    /// <summary>读入时保留的原始根节点，用于写回时原样透传未知扩展字段。</summary>
    public JsonObject? RawRoot { get; set; }

    public List<TempoPoint> BpmList { get; } = new()
    {
        new TempoPoint(Beat.Zero, 120),
    };

    public List<JudgeLine> Lines { get; } = new();

    /// <summary>文档被修改后置位，供界面提示与自动保存使用。</summary>
    public bool IsDirty { get; set; }

    public TempoMap BuildTempoMap() => new(BpmList);

    /// <summary>谱面最后一个有内容的拍。</summary>
    public double LastBeat
    {
        get
        {
            var last = 0.0;
            foreach (var line in Lines)
            {
                last = Math.Max(last, line.Time.Value);
                foreach (var note in line.Notes)
                {
                    last = Math.Max(last, note.EndBeat.Value);
                }

                foreach (var item in line.Events)
                {
                    last = Math.Max(last, item.EndBeat.Value);
                }
            }

            return last;
        }
    }

    public JudgeLine? FindLine(int id) => Lines.FirstOrDefault(line => line.Id == id);

    /// <summary>
    /// 找出「多押」的音符：与其它音符落在同一时刻的音符集合。
    /// 原版用它来提示同时按下的音符（高亮贴图），与选中状态无关。
    /// </summary>
    public HashSet<Note> SimultaneousNotes(TempoMap? tempo = null)
    {
        var map = tempo ?? BuildTempoMap();
        var times = new Dictionary<Note, long>();
        var counts = new Dictionary<long, int>();

        foreach (var line in Lines)
        {
            foreach (var note in line.Notes)
            {
                var time = (long)Math.Round(map.SecondsAt(note.StartBeat.Value) * 1_000_000);
                times[note] = time;
                counts[time] = counts.GetValueOrDefault(time) + 1;
            }
        }

        return times.Where(pair => counts[pair.Value] > 1).Select(pair => pair.Key).ToHashSet();
    }

    public JudgeLine AddLine()
    {
        var line = new JudgeLine
        {
            Id = Lines.Count == 0 ? 0 : Lines.Max(item => item.Id) + 1,
            Number = Lines.Count,
        };
        Lines.Add(line);
        return line;
    }
}

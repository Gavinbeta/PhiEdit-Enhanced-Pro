using PhiEdit.Core.Beats;

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

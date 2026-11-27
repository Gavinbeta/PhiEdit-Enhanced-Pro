namespace PhiEdit.Core.Charts;

/// <summary>谱面库中条目的来源。</summary>
public enum ChartLibrarySource
{
    /// <summary>随程序分发的示例谱面。</summary>
    Builtin,
    /// <summary>用户从文件导入的谱面。</summary>
    Imported,
    /// <summary>在程序内新建并保存的谱面。</summary>
    Created,
}

/// <summary>
/// 谱面库中的一条记录。谱面文件副本存放在库目录下，索引以 JSON 持久化。
/// </summary>
public sealed class ChartLibraryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>库目录内的谱面文件名（含扩展名）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>来源标识：示例谱面的源文件名 / 导入时的原始文件名，用于去重。</summary>
    public string SourceKey { get; set; } = string.Empty;

    public string Name { get; set; } = "未命名";

    public string Artist { get; set; } = string.Empty;

    public string Charter { get; set; } = string.Empty;

    public string Difficulty { get; set; } = string.Empty;

    public ChartLibrarySource Source { get; set; } = ChartLibrarySource.Imported;

    /// <summary>加入库的时间（本地日期时间字符串）。</summary>
    public string AddedAt { get; set; } = string.Empty;

    /// <summary>显示用的副标题（作者 / 谱师 / 难度）。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
            if (!string.IsNullOrWhiteSpace(Charter)) parts.Add(Charter);
            if (!string.IsNullOrWhiteSpace(Difficulty)) parts.Add(Difficulty);
            return string.Join(" · ", parts);
        }
    }
}

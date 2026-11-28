using System.Text.Json;
using PhiEdit.Core.IO;

namespace PhiEdit.Core.Charts;

/// <summary>
/// 谱面库持久化服务。把谱面文件副本与索引存放在用户本地数据目录，
/// 记录导入过、创建过的谱面以及随程序分发的示例谱面。
/// </summary>
public sealed class ChartLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root;
    private readonly string _chartsDirectory;
    private readonly string _indexPath;
    private List<ChartLibraryItem> _items = new();

    public ChartLibraryStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhiEditEnhancedPro", "Library");
        _chartsDirectory = Path.Combine(_root, "Charts");
        _indexPath = Path.Combine(_root, "Index.json");
    }

    /// <summary>库根目录（供诊断/展示）。</summary>
    public string Root => _root;

    public IReadOnlyList<ChartLibraryItem> Items => _items;

    /// <summary>读取索引；文件不存在时创建空库。</summary>
    public void Load()
    {
        Directory.CreateDirectory(_chartsDirectory);
        if (!File.Exists(_indexPath))
        {
            _items = new List<ChartLibraryItem>();
            return;
        }

        try
        {
            var json = File.ReadAllText(_indexPath);
            _items = JsonSerializer.Deserialize<List<ChartLibraryItem>>(json, JsonOptions) ?? new List<ChartLibraryItem>();
            // 清理索引中已不存在文件的条目。
            _items = _items.Where(item => File.Exists(GetPath(item))).ToList();
            // 旧索引条目可能没有背景图，从已存谱面补提取一次。
            foreach (var item in _items)
            {
                if (string.IsNullOrWhiteSpace(item.BackgroundFile) && File.Exists(GetPath(item)))
                {
                    ReadMeta(GetPath(item), item);
                }
            }
            Save();
        }
        catch
        {
            _items = new List<ChartLibraryItem>();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_chartsDirectory);
        var json = JsonSerializer.Serialize(_items, JsonOptions);
        File.WriteAllText(_indexPath, json);
    }

    /// <summary>
    /// 把示例谱面目录中的谱面登记入库（已登记过的跳过）。返回新增条目数。
    /// </summary>
    public int EnsureSeeded(string builtinDirectory)
    {
        if (!Directory.Exists(builtinDirectory))
        {
            return 0;
        }

        var added = 0;
        foreach (var path in Directory.EnumerateFiles(builtinDirectory)
                     .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".pez" or ".zip" or ".json")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var key = Path.GetFileName(path);
            if (_items.Any(item => item.Source == ChartLibrarySource.Builtin &&
                                   string.Equals(item.SourceKey, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var storedName = CopyIntoLibrary(path);
            var item = new ChartLibraryItem
            {
                FileName = storedName,
                SourceKey = key,
                Source = ChartLibrarySource.Builtin,
                AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };
            ReadMeta(path, item);
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name == "未命名")
            {
                item.Name = Path.GetFileNameWithoutExtension(path);
            }

            _items.Add(item);
            added++;
        }

        if (added > 0)
        {
            Save();
        }
        return added;
    }

    /// <summary>从任意路径导入谱面到库（复制文件副本），返回新条目。</summary>
    public ChartLibraryItem Import(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("谱面文件不存在", path);
        }

        var storedName = CopyIntoLibrary(path);
        var item = new ChartLibraryItem
        {
            FileName = storedName,
            SourceKey = Path.GetFileName(path),
            Source = ChartLibrarySource.Imported,
            AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
        ReadMeta(path, item);
        if (string.IsNullOrWhiteSpace(item.Name) || item.Name == "未命名")
        {
            item.Name = Path.GetFileNameWithoutExtension(path);
        }

        _items.Add(item);
        Save();
        return item;
    }

    /// <summary>新建谱面存入库：把当前会话的 JSON 写到库目录并登记。</summary>
    public ChartLibraryItem Create(string chartJson, string? name = null)
    {
        var storedName = $"created-{Guid.NewGuid():N}.json";
        var path = Path.Combine(_chartsDirectory, storedName);
        File.WriteAllText(path, chartJson);

        var item = new ChartLibraryItem
        {
            FileName = storedName,
            Source = ChartLibrarySource.Created,
            AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Name = string.IsNullOrWhiteSpace(name) ? "未命名谱面" : name,
        };
        _items.Add(item);
        Save();
        return item;
    }

    /// <summary>更新已创建谱面的内容（保存时调用）。</summary>
    public void UpdateContent(ChartLibraryItem item, string chartJson)
    {
        File.WriteAllText(GetPath(item), chartJson);
        Save();
    }

    public void Remove(string id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            return;
        }
        _items.Remove(item);
        try
        {
            File.Delete(GetPath(item));
            if (!string.IsNullOrWhiteSpace(item.BackgroundFile))
            {
                File.Delete(Path.Combine(_chartsDirectory, item.BackgroundFile));
            }
        }
        catch
        {
            // 文件删除失败时仍从索引移除，避免卡住库。
        }
        Save();
    }

    /// <summary>库内谱面文件的绝对路径。</summary>
    public string GetPath(ChartLibraryItem item) => Path.Combine(_chartsDirectory, item.FileName);

    /// <summary>库内谱面背景图的绝对路径（无背景时返回 null）。</summary>
    public string? GetBackgroundPath(ChartLibraryItem item) =>
        string.IsNullOrWhiteSpace(item.BackgroundFile) ? null : Path.Combine(_chartsDirectory, item.BackgroundFile);

    /// <summary>导出谱面到目标路径（当前仅支持单条，多选导出后续实现）。</summary>
    public void Export(ChartLibraryItem item, string targetPath)
    {
        File.Copy(GetPath(item), targetPath, overwrite: true);
    }

    private string CopyIntoLibrary(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        var storedName = $"{Guid.NewGuid():N}{extension}";
        File.Copy(sourcePath, Path.Combine(_chartsDirectory, storedName), overwrite: true);
        return storedName;
    }

    private void ReadMeta(string path, ChartLibraryItem item)
    {
        try
        {
            ChartAssetStore assets;
            string text;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".pez" or ".zip")
            {
                assets = ChartAssetStore.FromZip(File.ReadAllBytes(path));
                var chartName = assets.FindChart();
                var bytes = chartName is null ? null : assets.Resolve(chartName);
                if (bytes is null)
                {
                    return;
                }
                text = System.Text.Encoding.UTF8.GetString(bytes);
            }
            else
            {
                text = File.ReadAllText(path);
                assets = ChartAssetStore.FromDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            }

            var chart = RpeJson.ReadWithWarnings(text).Chart;
            item.Name = string.IsNullOrWhiteSpace(chart.SongName) ? chart.ChartName : chart.SongName;
            item.Artist = chart.SongArtist;
            item.Charter = chart.ChartName;
            item.Difficulty = chart.Difficulty;

            // 提取谱面背景图作为卡片背景，随条目一起持久化。
            var backgroundName = assets.FindBackground(chart.Background);
            if (backgroundName is not null)
            {
                var image = assets.Resolve(backgroundName);
                if (image is not null)
                {
                    var imageExtension = Path.GetExtension(backgroundName);
                    if (string.IsNullOrWhiteSpace(imageExtension))
                    {
                        imageExtension = ".png";
                    }
                    var backgroundFile = $"bg-{item.Id}{imageExtension}";
                    File.WriteAllBytes(Path.Combine(_chartsDirectory, backgroundFile), image);
                    item.BackgroundFile = backgroundFile;
                }
            }

            var info = assets.ReadInfo();
            if (string.IsNullOrWhiteSpace(item.Name) && info.TryGetValue("Name", out var name))
            {
                item.Name = name;
            }
            if (string.IsNullOrWhiteSpace(item.Artist) && info.TryGetValue("Composer", out var composer))
            {
                item.Artist = composer;
            }
            if (string.IsNullOrWhiteSpace(item.Charter) && info.TryGetValue("Charter", out var charter))
            {
                item.Charter = charter;
            }
            if (string.IsNullOrWhiteSpace(item.Difficulty) && info.TryGetValue("Level", out var level))
            {
                item.Difficulty = level;
            }
        }
        catch
        {
            // 元数据解析失败不影响条目登记，显示文件名即可。
        }
    }
}

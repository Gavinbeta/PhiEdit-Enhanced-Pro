using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PhiEdit.Core.IO;

/// <summary>
/// 谱面资源集合。参考 rpe-next 的 platform/files.mjs：
/// 支持同目录散文件与 PEZ/ZIP 包，按文件名或相对路径解析背景、判定线贴图等资源。
/// </summary>
public sealed class ChartAssetStore
{
    public const int MaxFiles = 20000;
    public const long MaxBytes = 1024L * 1024 * 1024;
    public const long MaxFileBytes = 512L * 1024 * 1024;
    public const double MaxCompressionRatio = 500;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".pec", ".pez", ".zip", ".txt", ".yml", ".yaml",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".avif",
        ".ogg", ".mp3", ".wav",
    };

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    private long _totalBytes;

    public static ChartAssetStore Empty { get; } = new();

    public IReadOnlyCollection<string> Names => _files.Keys;

    public byte[]? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = Normalize(name);
        if (_files.TryGetValue(normalized, out var direct))
        {
            return direct;
        }

        var fileName = Path.GetFileName(normalized);
        if (_byFileName.TryGetValue(fileName, out var candidates))
        {
            if (candidates.Count == 1)
            {
                return _files[candidates[0]];
            }

            var relative = _files.Keys.FirstOrDefault(name => name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                                                              name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            return relative is null ? null : _files[relative];
        }

        return null;
    }

    public void Add(string name, byte[] bytes)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0 || bytes.LongLength > MaxFileBytes)
        {
            return;
        }

        if (!_files.TryGetValue(normalized, out var previous))
        {
            if (_files.Count >= MaxFiles || _totalBytes + bytes.LongLength > MaxBytes)
            {
                return;
            }

            _totalBytes += bytes.LongLength;
        }
        else
        {
            _totalBytes += bytes.LongLength - previous.LongLength;
        }

        _files[normalized] = bytes;
        var fileName = Path.GetFileName(normalized);
        if (!_byFileName.TryGetValue(fileName, out var candidates))
        {
            candidates = new List<string>();
            _byFileName[fileName] = candidates;
        }
        if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalized);
        }
    }

    public void Clear()
    {
        _files.Clear();
        _byFileName.Clear();
        _totalBytes = 0;
    }

    /// <summary>
    /// 按原版优先级寻找背景：META.background → info.txt 的 Picture → 目录内唯一图片。
    /// </summary>
    public string? FindBackground(string? metaBackground)
    {
        if (Resolve(metaBackground) is not null)
        {
            return Normalize(metaBackground!);
        }

        var infoDirectory = string.Empty;
        foreach (var infoName in _files.Keys.Where(name => Path.GetFileName(name).Equals("info.txt", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Encoding.UTF8.GetString(_files[infoName]);
            infoDirectory = Path.GetDirectoryName(infoName) ?? string.Empty;
            foreach (var line in text.Split('\n'))
            {
                var separator = line.IndexOf(':');
                if (separator < 0 || !line[..separator].Trim().Equals("Picture", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                var relative = infoDirectory.Length > 0 && !value.Contains('/')
                    ? infoDirectory + "/" + value
                    : value;
                if (Resolve(relative) is not null)
                {
                    return Normalize(relative);
                }

                if (Resolve(value) is not null)
                {
                    return Normalize(value);
                }
            }
        }

        var directory = string.IsNullOrEmpty(metaBackground) ? string.Empty : Path.GetDirectoryName(Normalize(metaBackground)) ?? string.Empty;
        var images = _files.Keys.Where(name => IsImage(name) &&
            (directory.Length == 0 || name.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        return images.Count == 1 ? images[0] : null;
    }

    public static ChartAssetStore FromDirectory(string directory)
    {
        var store = new ChartAssetStore();
        if (!Directory.Exists(directory))
        {
            return store;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!AllowedExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            var name = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (name.Count(character => character == '/') >= 6)
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes || store._totalBytes + info.Length > MaxBytes)
                {
                    continue;
                }

                store.Add(name, File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                // 单个不可读资源不影响谱面打开。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return store;
    }

    public static ChartAssetStore FromZip(byte[] bytes)
    {
        var store = new ChartAssetStore();
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
        if (entries.Count > MaxFiles || entries.Sum(entry => entry.Length) > MaxBytes ||
            entries.Any(entry => entry.Length > MaxFileBytes) ||
            entries.Any(entry => entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > MaxCompressionRatio))
        {
            throw new InvalidDataException("PEZ/ZIP 资源超出安全限制。");
        }

        foreach (var entry in entries)
        {
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            store.Add(entry.FullName, buffer.ToArray());
        }

        return store;
    }

    /// <summary>
    /// 按原版 info.txt 的 Chart 字段寻找谱面 JSON，找不到时回退到包内唯一 JSON。
    /// </summary>
    public string? FindChart()
    {
        foreach (var infoName in _files.Keys.Where(name => Path.GetFileName(name).Equals("info.txt", StringComparison.OrdinalIgnoreCase)))
        {
            var directory = Path.GetDirectoryName(infoName) ?? string.Empty;
            var text = Encoding.UTF8.GetString(_files[infoName]);
            foreach (var line in text.Split('\n'))
            {
                var separator = line.IndexOf(':');
                if (separator < 0 || !line[..separator].Trim().Equals("Chart", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                var relative = directory.Length > 0 && !value.Contains('/') ? directory + "/" + value : value;
                if (Resolve(relative) is not null)
                {
                    return Normalize(relative);
                }

                if (Resolve(value) is not null)
                {
                    return Normalize(value);
                }
            }
        }

        var charts = _files.Keys.Where(name =>
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pec", StringComparison.OrdinalIgnoreCase)).ToList();
        return charts.Count == 1 ? charts[0] : null;
    }

    /// <summary>按 META.song、info.txt 的 Song 与包内唯一音频解析背景音乐。</summary>
    public string? FindSong(string? metaSong)
    {
        if (Resolve(metaSong) is not null)
        {
            return Normalize(metaSong!);
        }

        var infoDirectory = string.Empty;
        foreach (var infoName in _files.Keys.Where(name => Path.GetFileName(name).Equals("info.txt", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Encoding.UTF8.GetString(_files[infoName]);
            infoDirectory = Path.GetDirectoryName(infoName) ?? string.Empty;
            foreach (var line in text.Split('\n'))
            {
                var separator = line.IndexOf(':');
                if (separator < 0 || !line[..separator].Trim().Equals("Song", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                var relative = infoDirectory.Length > 0 && !value.Contains('/') ? infoDirectory + "/" + value : value;
                if (Resolve(relative) is not null)
                {
                    return Normalize(relative);
                }

                if (Resolve(value) is not null)
                {
                    return Normalize(value);
                }
            }
        }

        var directory = string.IsNullOrEmpty(metaSong) ? string.Empty : Path.GetDirectoryName(Normalize(metaSong)) ?? string.Empty;
        var audios = _files.Keys.Where(name => IsAudio(name) &&
            (directory.Length == 0 || name.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        return audios.Count == 1 ? audios[0] : null;
    }

    public static bool IsImage(string name) =>
        Regex.IsMatch(Path.GetExtension(name), @"^\.(png|jpe?g|webp|gif|bmp|avif)$", RegexOptions.IgnoreCase);

    public static bool IsAudio(string name) =>
        Regex.IsMatch(Path.GetExtension(name), @"^\.(mp3|wav|ogg|m4a|aac|flac)$", RegexOptions.IgnoreCase);

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');
}

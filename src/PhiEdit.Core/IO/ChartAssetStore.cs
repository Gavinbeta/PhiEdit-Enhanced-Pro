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
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

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
        if (_files.TryGetValue(fileName, out var byName))
        {
            return byName;
        }

        var matches = _files.Where(pair => pair.Key.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Value : null;
    }

    public void Add(string name, byte[] bytes)
    {
        var normalized = Normalize(name);
        if (normalized.Length > 0)
        {
            _files[normalized] = bytes;
        }
    }

    public void Clear() => _files.Clear();

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
            var name = Path.GetRelativePath(directory, path).Replace('\\', '/');
            try
            {
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
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

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

    public static bool IsImage(string name) =>
        Regex.IsMatch(Path.GetExtension(name), @"^\.(png|jpe?g|webp|gif|bmp|avif)$", RegexOptions.IgnoreCase);

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PhiEdit.Core.Skins;

/// <summary>贴图尺寸。</summary>
public readonly record struct NoteImageSize(int Width, int Height);

/// <summary>
/// 音符资源包。结构与原版一致：<c>info.yml</c> 清单 + 9 张贴图 + 可选音效。
/// </summary>
public sealed class NoteSkin
{
    /// <summary>必须提供的贴图。</summary>
    public static readonly string[] RequiredImages =
    {
        "click.png", "click_mh.png",
        "drag.png", "drag_mh.png",
        "flick.png", "flick_mh.png",
        "hold.png", "hold_mh.png",
        "hit_fx.png",
    };

    /// <summary>可选音效：类型 → 文件名。</summary>
    public static readonly IReadOnlyDictionary<string, string> OptionalSounds =
        new Dictionary<string, string>
        {
            ["tap"] = "click.ogg",
            ["drag"] = "drag.ogg",
            ["flick"] = "flick.ogg",
        };

    public const int MaxFiles = 256;
    public const long MaxBytes = 192L * 1024 * 1024;
    public const long MaxFileBytes = 48L * 1024 * 1024;
    public const long MaxPixels = 96L * 1024 * 1024;
    public const int MaxDimension = 16384;

    public required string Id { get; init; }

    public required NoteSkinInfo Info { get; init; }

    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }

    public required IReadOnlyDictionary<string, NoteImageSize> Images { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool IsBuiltin { get; init; }

    /// <summary>按文件名取贴图字节。</summary>
    public byte[]? GetFile(string name) => Files.TryGetValue(name, out var bytes) ? bytes : null;

    /// <summary>是否为内置资源包。</summary>
    public static NoteSkin FromZip(byte[] archive, string id, bool builtin = false)
    {
        var files = ReadArchive(archive);
        return Parse(files, id, builtin);
    }

    /// <summary>解析资源包文件集合。</summary>
    public static NoteSkin Parse(IReadOnlyDictionary<string, byte[]> source, string id, bool builtin = false)
    {
        var files = Normalize(source);
        var manifest = files.TryGetValue("info.yml", out var manifestBytes)
            ? manifestBytes
            : throw new FormatException("资源包缺少 info.yml。");

        string yaml;
        try
        {
            yaml = new UTF8Encoding(false, true).GetString(manifest);
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("info.yml 不是合法的 UTF-8 文本。");
        }

        var info = NoteSkinInfo.Parse(yaml);
        var images = new Dictionary<string, NoteImageSize>(StringComparer.OrdinalIgnoreCase);
        long pixels = 0;

        foreach (var name in RequiredImages)
        {
            if (!files.TryGetValue(name, out var bytes))
            {
                throw new FormatException($"资源包缺少贴图 {name}。");
            }

            var size = ReadPngSize(bytes, name);
            images[name] = size;
            pixels += (long)size.Width * size.Height;
        }

        if (pixels > MaxPixels)
        {
            throw new FormatException("资源包贴图总像素过大。");
        }

        foreach (var (name, key) in new[] { ("hold.png", "holdAtlas"), ("hold_mh.png", "holdAtlasMH") })
        {
            var pair = key == "holdAtlas" ? info.HoldAtlas : info.HoldAtlasMH;
            if (pair[0] + pair[1] >= images[name].Height)
            {
                throw new FormatException($"资源包的 {key} 与 {name} 的尺寸不匹配。");
            }
        }

        if (info.HitFx[0] > images["hit_fx.png"].Width || info.HitFx[1] > images["hit_fx.png"].Height)
        {
            throw new FormatException("打击特效图集尺寸与 hitFx 设置不匹配。");
        }

        var warnings = new List<string>();
        if (images["hit_fx.png"].Width % info.HitFx[0] != 0 || images["hit_fx.png"].Height % info.HitFx[1] != 0)
        {
            warnings.Add("打击特效图集无法被格子数整除。");
        }

        if (files.ContainsKey("ending.ogg"))
        {
            warnings.Add("资源包包含结算音效，当前版本暂未使用。");
        }

        return new NoteSkin
        {
            Id = id,
            Info = info,
            Files = files,
            Images = images,
            Warnings = warnings,
            IsBuiltin = builtin,
        };
    }

    /// <summary>按内容生成稳定标识（与文件路径无关）。</summary>
    public static string ComputeId(IReadOnlyDictionary<string, byte[]> files)
    {
        var ordered = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        using var hash = SHA256.Create();
        var buffer = new List<byte>();
        foreach (var (name, bytes) in ordered)
        {
            buffer.AddRange(Encoding.UTF8.GetBytes(name));
            buffer.AddRange(bytes);
        }

        var digest = hash.ComputeHash(buffer.ToArray());
        return "pack-" + Convert.ToHexString(digest[..6]).ToLowerInvariant();
    }

    /// <summary>读取 ZIP 内的全部文件。</summary>
    public static Dictionary<string, byte[]> ReadArchive(byte[] archive)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        if (zip.Entries.Count > MaxFiles)
        {
            throw new FormatException("资源包文件数量过多。");
        }

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            if (entry.Length > MaxFileBytes)
            {
                throw new FormatException($"资源包中的文件过大：{entry.FullName}");
            }

            total += entry.Length;
            if (total > MaxBytes)
            {
                throw new FormatException("资源包总体积过大。");
            }

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            files[entry.FullName] = buffer.ToArray();
        }

        return files;
    }

    /// <summary>规范化文件集合：剥离目录前缀并校验清单位置。</summary>
    private static Dictionary<string, byte[]> Normalize(IReadOnlyDictionary<string, byte[]> source)
    {
        if (source.Count > MaxFiles)
        {
            throw new FormatException("资源包文件数量过多。");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;

        foreach (var (path, bytes) in source)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
                path.Split('/').Any(part => part is "." or ".."))
            {
                throw new FormatException($"资源包包含非法路径：{path}");
            }

            if (path.EndsWith('/') || path.StartsWith("__MACOSX/", StringComparison.Ordinal) ||
                path.Split('/')[^1].StartsWith('.'))
            {
                continue;
            }

            if (bytes.LongLength > MaxFileBytes)
            {
                throw new FormatException($"资源包中的文件过大：{path}");
            }

            total += bytes.LongLength;
            if (total > MaxBytes)
            {
                throw new FormatException("资源包总体积过大。");
            }

            files[path] = bytes;
        }

        var manifests = files.Keys.Where(path =>
            path.EndsWith("info.yml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("info.yaml", StringComparison.OrdinalIgnoreCase)).ToList();

        if (manifests.Count != 1)
        {
            throw new FormatException("资源包必须且只能包含一个 info.yml。");
        }

        var prefix = manifests[0][..(manifests[0].LastIndexOf('/') + 1)];
        if (prefix.Length > 0 && files.Keys.Any(path => !path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new FormatException("资源包中的文件必须全部位于 info.yml 所在目录内。");
        }

        var normalized = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in files)
        {
            var name = path[prefix.Length..];
            var lower = name.ToLowerInvariant();
            if (RequiredImages.Contains(lower) || OptionalSounds.Values.Contains(lower) || lower == "ending.ogg")
            {
                name = lower;
            }

            if (path == manifests[0])
            {
                name = "info.yml";
            }

            if (normalized.ContainsKey(name))
            {
                throw new FormatException($"资源包中存在重名文件：{name}");
            }

            normalized[name] = bytes;
        }

        return normalized;
    }

    /// <summary>读取 PNG 尺寸（仅校验 IHDR 头）。</summary>
    public static NoteImageSize ReadPngSize(byte[] bytes, string name)
    {
        if (bytes.Length < 33 ||
            bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 ||
            bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10 ||
            Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR")
        {
            throw new FormatException($"贴图不是合法的 PNG：{name}");
        }

        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
        {
            throw new FormatException($"贴图尺寸超出限制：{name}");
        }

        return new NoteImageSize(width, height);
    }
}

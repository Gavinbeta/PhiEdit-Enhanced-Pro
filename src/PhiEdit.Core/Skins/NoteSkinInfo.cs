using System.Globalization;

namespace PhiEdit.Core.Skins;

/// <summary>资源包清单（info.yml）的数据。</summary>
public sealed class NoteSkinInfo
{
    public const int NameLimit = 100;
    public const int AuthorLimit = 200;
    public const int DescriptionLimit = 4000;

    public string Name { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>打击特效图集的列数与行数。</summary>
    public int[] HitFx { get; set; } = { 1, 1 };

    /// <summary>Hold 贴图的尾部像素与头部像素。</summary>
    public int[] HoldAtlas { get; set; } = { 0, 0 };

    /// <summary>Hold 贴图（命中高亮）的尾部像素与头部像素。</summary>
    public int[] HoldAtlasMH { get; set; } = { 0, 0 };

    public double HitFxDuration { get; set; } = 0.5;

    public double HitFxScale { get; set; } = 1;

    public bool HitFxRotate { get; set; }

    public bool HitFxTinted { get; set; } = true;

    public bool HideParticles { get; set; }

    public bool HoldKeepHead { get; set; }

    public bool HoldRepeat { get; set; }

    public bool HoldCompact { get; set; }

    /// <summary>Perfect 判定颜色（ARGB）。</summary>
    public uint ColorPerfect { get; set; } = 0xE1FFEC9F;

    /// <summary>Good 判定颜色（ARGB）。</summary>
    public uint ColorGood { get; set; } = 0xEBB4E1FF;

    /// <summary>从 YAML 文本解析清单。</summary>
    public static NoteSkinInfo Parse(string yaml)
    {
        if (yaml.Length > 65536)
        {
            throw new FormatException("资源包清单过大。");
        }

        var source = MiniYaml.Parse(yaml.TrimStart('\uFEFF'));
        var info = new NoteSkinInfo();

        if (source.TryGetValue("name", out var name))
        {
            info.Name = name;
        }

        if (source.TryGetValue("author", out var author))
        {
            info.Author = author;
        }

        if (source.TryGetValue("description", out var description))
        {
            info.Description = description;
        }

        if (source.TryGetValue("hitFx", out var hitFx))
        {
            info.HitFx = MiniYaml.ParseIntArray(hitFx);
        }

        if (source.TryGetValue("holdAtlas", out var holdAtlas))
        {
            info.HoldAtlas = MiniYaml.ParseIntArray(holdAtlas);
        }

        if (source.TryGetValue("holdAtlasMH", out var holdAtlasMh))
        {
            info.HoldAtlasMH = MiniYaml.ParseIntArray(holdAtlasMh);
        }

        if (source.TryGetValue("hitFxDuration", out var duration) && double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
        {
            info.HitFxDuration = parsedDuration;
        }

        if (source.TryGetValue("hitFxScale", out var scale) && double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScale))
        {
            info.HitFxScale = parsedScale;
        }

        foreach (var (key, target) in new (string, Action<bool>)[]
                 {
                     ("hitFxRotate", value => info.HitFxRotate = value),
                     ("hitFxTinted", value => info.HitFxTinted = value),
                     ("hideParticles", value => info.HideParticles = value),
                     ("holdKeepHead", value => info.HoldKeepHead = value),
                     ("holdRepeat", value => info.HoldRepeat = value),
                     ("holdCompact", value => info.HoldCompact = value),
                 })
        {
            if (source.TryGetValue(key, out var raw))
            {
                target(MiniYaml.ParseBool(raw));
            }
        }

        if (source.TryGetValue("colorPerfect", out var perfect))
        {
            info.ColorPerfect = ParseColor(perfect);
        }

        if (source.TryGetValue("colorGood", out var good))
        {
            info.ColorGood = ParseColor(good);
        }

        info.Validate();
        return info;
    }

    private static uint ParseColor(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }
        else if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed[1..];
        }

        return uint.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>按原版规则校验清单。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new FormatException("资源包缺少名称（name）。");
        }

        if (Name.Length > NameLimit)
        {
            throw new FormatException($"资源包名称超过 {NameLimit} 字符。");
        }

        if (Author.Length > AuthorLimit)
        {
            throw new FormatException($"资源包作者超过 {AuthorLimit} 字符。");
        }

        if (Description.Length > DescriptionLimit)
        {
            throw new FormatException($"资源包说明超过 {DescriptionLimit} 字符。");
        }

        ValidatePair(HitFx, 1, "hitFx");
        ValidatePair(HoldAtlas, 0, "holdAtlas");
        ValidatePair(HoldAtlasMH, 0, "holdAtlasMH");

        if (HitFx[0] * HitFx[1] > 10240)
        {
            throw new FormatException("打击特效图集格子过多。");
        }

        if (HitFxDuration <= 0 || HitFxDuration > 120)
        {
            throw new FormatException("打击特效时长超出范围。");
        }

        if (HitFxScale <= 0 || HitFxScale > 64)
        {
            throw new FormatException("打击特效缩放超出范围。");
        }
    }

    private static void ValidatePair(int[] pair, int minimum, string key)
    {
        if (pair.Length != 2 || pair.Any(value => value < minimum || value > 65535))
        {
            throw new FormatException($"资源包的 {key} 设置不合法。");
        }
    }
}

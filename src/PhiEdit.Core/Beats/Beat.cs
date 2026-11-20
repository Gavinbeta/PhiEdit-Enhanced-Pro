using System.Globalization;

namespace PhiEdit.Core.Beats;

/// <summary>
/// 拍（Beat）。沿用 RPE 的 [整数, 分子, 分母] 三元组表示，保证与原谱面文件无损互转。
/// </summary>
public readonly struct Beat : IComparable<Beat>, IEquatable<Beat>
{
    public static readonly Beat Zero = new(0, 0, 1);

    public Beat(int integer, int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "分母必须为正数。");
        }

        Integer = integer;
        Numerator = numerator;
        Denominator = denominator;
    }

    public int Integer { get; }

    public int Numerator { get; }

    public int Denominator { get; }

    /// <summary>拍的小数值形式，用于计算。</summary>
    public double Value => Integer + (double)Numerator / Denominator;

    public bool IsZero => Integer == 0 && Numerator == 0;

    /// <summary>约分后的最简分数形式，Denominator 为 1 时退化为整数拍。</summary>
    public Beat Simplified()
    {
        var divisor = Gcd(Math.Abs(Numerator), Denominator);
        if (divisor <= 1)
        {
            return this;
        }

        return new Beat(Integer, Numerator / divisor, Denominator / divisor);
    }

    /// <summary>由小数值构造拍，默认使用 19200 分母以保持 RPE 精度。</summary>
    public static Beat FromValue(double value, int denominator = 19200)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var total = (long)Math.Round(value * denominator, MidpointRounding.AwayFromZero);
        var whole = (int)Math.Floor(total / (double)denominator);
        var numerator = (int)(total - (long)whole * denominator);
        var divisor = Gcd(Math.Abs(numerator), denominator);
        if (divisor <= 1)
        {
            return new Beat(whole, numerator, denominator);
        }

        return new Beat(whole, numerator / divisor, denominator / divisor);
    }

    /// <summary>把拍吸附到指定的每拍细分数。</summary>
    public static Beat Snap(double value, int division)
    {
        division = Math.Max(1, division);
        return FromValue(Math.Round(value * division, MidpointRounding.AwayFromZero) / division, division);
    }

    /// <summary>解析 "整数:分子/分母"、"分子/分母" 或纯数字文本。</summary>
    public static Beat Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("拍文本为空。");
        }

        var trimmed = text.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
            var head = trimmed[..colon].Trim();
            var tail = trimmed[(colon + 1)..].Trim();
            if (int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
            {
                var (numerator, denominator) = ParseFraction(tail);
                return new Beat(whole, numerator, denominator);
            }
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            return FromValue(real);
        }

        var (num, den) = ParseFraction(trimmed);
        if (den == 1)
        {
            return new Beat(num, 0, 1);
        }

        return new Beat(0, num, den);
    }

    private static (int Numerator, int Denominator) ParseFraction(string text)
    {
        var slash = text.IndexOf('/');
        if (slash > 0)
        {
            var left = text[..slash].Trim();
            var right = text[(slash + 1)..].Trim();
            if (int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0)
            {
                return (n, d);
            }

            throw new FormatException($"无法解析分数：{text}");
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return (whole, 1);
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            var beat = FromValue(real);
            return (beat.Numerator, beat.Denominator);
        }

        throw new FormatException($"无法解析拍：{text}");
    }

    public static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    public int CompareTo(Beat other) => Value.CompareTo(other.Value);

    public bool Equals(Beat other) => Math.Abs(Value - other.Value) < 1e-9;

    public override bool Equals(object? obj) => obj is Beat other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Denominator == 1
        ? Integer.ToString(CultureInfo.InvariantCulture)
        : $"{Integer}:{Numerator}/{Denominator}";

    public static bool operator ==(Beat left, Beat right) => left.Equals(right);

    public static bool operator !=(Beat left, Beat right) => !left.Equals(right);

    public static bool operator <(Beat left, Beat right) => left.Value < right.Value;

    public static bool operator >(Beat left, Beat right) => left.Value > right.Value;

    public static bool operator <=(Beat left, Beat right) => left.Value <= right.Value;

    public static bool operator >=(Beat left, Beat right) => left.Value >= right.Value;
}

using System.Globalization;

namespace PhiEdit.Core.Skins;

/// <summary>
/// 极简 YAML 子集解析器：只支持资源包清单需要的语法
/// —— <c>key: value</c>、行内数组 <c>[a, b]</c>、引号字符串、数字、布尔值、
/// 以及块标量 <c>|</c> / <c>&gt;</c>。足以读取 info.yml，且不引入第三方依赖。
/// </summary>
public static class MiniYaml
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            // 块标量：把后续缩进行合并成一段文本。
            if (trimmed.EndsWith('|') || trimmed.EndsWith('>'))
            {
                var separator = trimmed[^1];
                var parts = new List<string>();
                var cursor = index + 1;
                while (cursor < lines.Length)
                {
                    var candidate = lines[cursor];
                    if (candidate.Trim().Length == 0)
                    {
                        cursor++;
                        continue;
                    }

                    if (!char.IsWhiteSpace(candidate[0]))
                    {
                        break;
                    }

                    parts.Add(candidate.Trim());
                    cursor++;
                }

                index = cursor - 1;
                var joiner = separator == '|' ? "\n" : " ";
                var key = trimmed[..^1].Trim();
                result[key] = string.Join(joiner, parts).TrimEnd();
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();

            // 去掉行尾注释（引号内的 # 不算）。
            if (value.Length > 0 && value[0] is not ('"' or '\''))
            {
                var hash = value.IndexOf('#');
                if (hash >= 0)
                {
                    value = value[..hash].Trim();
                }
            }

            result[name] = Unquote(value);
        }

        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Replace("\\\"", "\"").Replace("''", "'");
        }

        return value;
    }

    /// <summary>解析行内数组，如 <c>[7, 6]</c>。</summary>
    public static int[] ParseIntArray(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
        {
            throw new FormatException($"不是合法的数组：{value}");
        }

        var body = trimmed[1..^1].Trim();
        if (body.Length == 0)
        {
            throw new FormatException("数组为空");
        }

        var parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[index]))
            {
                throw new FormatException($"数组元素不是整数：{parts[index]}");
            }
        }

        return result;
    }

    /// <summary>解析布尔值。</summary>
    public static bool ParseBool(string value)
    {
        var trimmed = value.Trim();
        if (bool.TryParse(trimmed, out var parsed))
        {
            return parsed;
        }

        return trimmed is "1" or "yes" or "on";
    }
}

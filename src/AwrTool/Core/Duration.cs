using System.Globalization;

namespace AwrTool.Core;

/// <summary>
/// 解析 CLI 的相对时长字面量，如 <c>30s</c> / <c>90m</c> / <c>2h</c> / <c>1d</c>。
/// </summary>
/// <remarks>
/// 单位是必填的。故意不支持裸数字（<c>--last 2</c>）和复合写法（<c>1h30m</c>）：
/// 这个工具主要给 AI 调用，含糊的输入应该立刻失败并给出示例，而不是靠猜。
/// </remarks>
public static class Duration
{
    /// <summary>解析时长。非法输入抛 <see cref="FormatException"/>，消息里带正确写法示例。</summary>
    public static TimeSpan Parse(string? text)
    {
        if (!TryParse(text, out var value))
        {
            throw new FormatException(
                $"无法解析时长 '{text}'。请带单位书写，例如 30m、2h、1d " +
                "(s=秒 m=分 h=时 d=天，支持小数如 1.5h)。");
        }

        return value;
    }

    /// <summary>尝试解析时长，失败不抛异常。</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var unit = trimmed[^1];
        var numberPart = trimmed[..^1];

        if (numberPart.Length == 0)
        {
            return false;
        }

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        if (amount <= 0)
        {
            return false;
        }

        var parsed = char.ToLowerInvariant(unit) switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero,
        };

        if (parsed <= TimeSpan.Zero)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}

using System.Globalization;
using AwrTool.Cli;

namespace AwrTool.Core;

/// <summary>
/// 解析 <c>--begin-time</c> / <c>--end-time</c> 的时间戳。
/// </summary>
/// <remarks>
/// 只接受 ISO 风格的日期，分隔符不放宽（不吃 <c>2026/10/16</c>）：
/// 这个工具主要给 AI 调用，模棱两可的输入宁可直接失败，也不要猜错月份和日。
/// <para>
/// 注意：这里<b>不做时区换算</b>。时间戳按字面值与 <c>dba_hist_snapshot</c> 里的时间比较，
/// 所以要求调用方按数据库服务器的时区书写。想完全绕开时区问题就用 <c>--last</c>，
/// 它锚定在数据库自己的时间上。
/// </para>
/// </remarks>
public static class Timestamp
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd",
    ];

    /// <summary>尝试解析时间戳。</summary>
    public static bool TryParse(string? text, out DateTime value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTime.TryParseExact(
            text.Trim(),
            Formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    /// <summary>解析时间戳，失败抛用法错误。</summary>
    public static DateTime Parse(string? text, string optionName = "--begin-time")
    {
        if (!TryParse(text, out var value))
        {
            throw AwrToolException.Usage(
                $"{optionName} 无法解析 '{text}'。" +
                "支持 yyyy-MM-dd、yyyy-MM-dd HH:mm、yyyy-MM-dd HH:mm:ss（也接受 T 分隔）。");
        }

        return value;
    }
}

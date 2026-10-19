using System.Globalization;

namespace AwrTool.Core;

/// <summary>
/// 规范化后的 Oracle 实例号列表。
/// </summary>
/// <remarks>
/// 存在的理由是踩过的坑：<c>awr_global_report_text</c> 有两个重载
/// （<c>L_INST_NUM</c> 分别是 VARCHAR2 和嵌套 TABLE），传 <c>null</c> 会触发
/// <c>ORA-06553: PLS-307 有太多的声明与此次调用相匹配</c>；而传空格分隔的
/// <c>'1 2'</c> 不报错，却会在结果里返回一行 <c>*** ORA-20019</c> 正文。
/// 所以这里统一收口成「逗号分隔、已排序、已去重、非空」。
/// </remarks>
public sealed class InstanceList
{
    private readonly int[] _numbers;

    private InstanceList(int[] numbers) => _numbers = numbers;

    /// <summary>已排序去重的实例号。</summary>
    public IReadOnlyList<int> Numbers => _numbers;

    /// <summary>构造单个实例的列表。</summary>
    public static InstanceList Single(int instanceNumber)
    {
        if (instanceNumber <= 0)
        {
            throw new FormatException($"实例号必须是正整数，收到 '{instanceNumber}'。");
        }

        return new InstanceList([instanceNumber]);
    }

    /// <summary>
    /// 解析实例号列表。接受逗号与空白混合分隔（<c>1</c> / <c>1,2</c> / <c>1, 2</c> / <c>1 2</c>），
    /// 输出一律归一化为逗号分隔。
    /// </summary>
    public static InstanceList Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "实例号列表不能为空。单实例用 --inst-num 2，RAC 全局报告用 --inst-num 1,2。",
                nameof(text));
        }

        var numbers = new SortedSet<int>();

        // 先按逗号切段，段内再按空白切；空段说明输入里有连续分隔符，属于错误，不静默吞掉。
        foreach (var segment in text.Split(','))
        {
            var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                throw new FormatException($"实例号列表 '{text}' 含空项，请检查分隔符。");
            }

            foreach (var token in tokens)
            {
                if (!token.All(char.IsAsciiDigit)
                    || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || number <= 0)
                {
                    throw new FormatException($"'{token}' 不是合法的实例号（必须是正整数）。");
                }

                numbers.Add(number);
            }
        }

        return new InstanceList([.. numbers]);
    }

    /// <summary>渲染成 Oracle 需要的逗号分隔字符串，如 <c>1,2</c>。</summary>
    public string ToCommaSeparated() => string.Join(",", _numbers);

    /// <inheritdoc />
    public override string ToString() => ToCommaSeparated();
}

using AwrTool.Cli;

namespace AwrTool.Core;

/// <summary>报告类型。</summary>
public enum ReportKind
{
    /// <summary>单实例 AWR 报告（<c>awr_report_*</c>）。</summary>
    Awr,

    /// <summary>RAC 全局报告（<c>awr_global_report_*</c>）。</summary>
    Global,
}

/// <summary>报告格式。</summary>
public enum ReportFormat
{
    /// <summary>纯文本（Oracle 函数后缀 <c>text</c>，文件扩展名 <c>txt</c>）。</summary>
    Text,

    /// <summary>HTML。</summary>
    Html,
}

/// <summary>报告格式的名字换算。</summary>
public static class ReportFormatExtensions
{
    /// <summary>Oracle 报告函数的后缀，如 <c>awr_report_<b>text</b></c>。</summary>
    public static string ToOracleSuffix(this ReportFormat format)
        => format == ReportFormat.Html ? "html" : "text";

    /// <summary>CLI 取值与文件扩展名，如 <c>txt</c> / <c>html</c>。</summary>
    public static string ToExtension(this ReportFormat format)
        => format == ReportFormat.Html ? "html" : "txt";
}

/// <summary>解析 <c>--type</c>。</summary>
public static class ReportKindParser
{
    /// <summary>解析报告类型，缺省为 <see cref="ReportKind.Awr"/>。</summary>
    public static ReportKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReportKind.Awr;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "awr" => ReportKind.Awr,
            "global" => ReportKind.Global,
            "ash" => throw new AwrToolException(
                ExitCode.Usage,
                "--type ash 暂未实现。ASH 报告用的是时间窗而不是快照号，参数语义与 AWR 不同，尚未验证通过。"),
            _ => throw AwrToolException.Usage($"--type 只支持 awr 或 global，收到 '{value}'。"),
        };
    }
}

/// <summary>解析 <c>--format</c>。</summary>
public static class ReportFormatParser
{
    /// <summary>解析报告格式，缺省为 <see cref="ReportFormat.Html"/>。</summary>
    public static ReportFormat Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReportFormat.Html;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "html" => ReportFormat.Html,
            "txt" or "text" => ReportFormat.Text,
            _ => throw AwrToolException.Usage($"--format 只支持 html 或 txt，收到 '{value}'。"),
        };
    }
}

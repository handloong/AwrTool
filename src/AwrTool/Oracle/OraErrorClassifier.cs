using System.Text.RegularExpressions;
using AwrTool.Cli;
using Oracle.ManagedDataAccess.Client;

namespace AwrTool.Oracle;

/// <summary>
/// 把 Oracle 错误映射成退出码，以及识别「成功查询里夹带的错误正文」。
/// </summary>
/// <remarks>
/// 已知错误号这张表是刻意枚举的，不是按前缀猜。遇到表里没有的错误号一律回退到
/// 调用方给的兜底码 —— 因为同一个错误号在不同阶段（连接 / 快照 / 取报告）含义不同，
/// 由上下文决定比在这里瞎猜更准。
/// </remarks>
public static partial class OraErrorClassifier
{
    private static readonly HashSet<int> SnapshotProblems = [20019, 20020, 20021];

    private static readonly HashSet<int> PrivilegeProblems = [942, 1031, 1036, 1039, 6550];

    private static readonly HashSet<int> ConnectProblems =
        [1017, 12154, 12170, 12514, 12541, 12545, 28000, 28001, 28002, 28003];

    private static readonly HashSet<int> ReportProblems = [50071];

    /// <summary>按错误号分类，表里没有的返回 <paramref name="fallback"/>。</summary>
    public static ExitCode Classify(int oraNumber, ExitCode fallback)
    {
        if (SnapshotProblems.Contains(oraNumber))
        {
            return ExitCode.SnapshotNotFound;
        }

        if (PrivilegeProblems.Contains(oraNumber))
        {
            return ExitCode.PrivilegeDenied;
        }

        if (ConnectProblems.Contains(oraNumber))
        {
            return ExitCode.ConnectFailed;
        }

        if (ReportProblems.Contains(oraNumber))
        {
            return ExitCode.ReportFailed;
        }

        return fallback;
    }

    /// <summary>从异常链里找出 Oracle 错误号后分类，找不到就返回 <paramref name="fallback"/>。</summary>
    public static ExitCode Classify(Exception ex, ExitCode fallback)
        => FindOraNumber(ex) is int number ? Classify(number, fallback) : fallback;

    /// <summary>从异常链里提取 ORA 错误号。</summary>
    public static int? FindOraNumber(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OracleException oracle)
            {
                return oracle.Number;
            }
        }

        return null;
    }

    // 对应 awrrpt.sql 自己的判错方式：结果正文里以 *** ORA- 开头的行即失败。
    [GeneratedRegex(@"^\s*\*\*\*\s*ORA-(\d{1,5})\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedErrorPattern();

    /// <summary>
    /// 判断一行结果正文是否是内嵌的错误行（如 <c>*** ORA-20019: ...</c>）。
    /// </summary>
    /// <remarks>
    /// 必须做这个检查：Global 报告传空格分隔的实例列表时不会抛异常，
    /// 而是在结果里返回一行错误正文。不拦就把错误当报告写出去了。
    /// </remarks>
    public static bool TryParseEmbeddedError(string? line, out int oraNumber)
    {
        oraNumber = 0;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = EmbeddedErrorPattern().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var parsed))
        {
            return false;
        }

        oraNumber = parsed;
        return true;
    }
}

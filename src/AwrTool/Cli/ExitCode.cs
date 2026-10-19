namespace AwrTool.Cli;

/// <summary>
/// 进程退出码。这是 AI 判定失败原因的**唯一权威依据**，数值不可随意变更。
/// </summary>
public enum ExitCode
{
    /// <summary>成功。</summary>
    Ok = 0,

    /// <summary>未预期错误（未分类异常）。</summary>
    Internal = 1,

    /// <summary>参数或用法错误。</summary>
    Usage = 2,

    /// <summary>网络不可达、登录失败、服务名错误。</summary>
    ConnectFailed = 3,

    /// <summary>快照区间无效或不存在。</summary>
    SnapshotNotFound = 4,

    /// <summary>权限不足。</summary>
    PrivilegeDenied = 5,

    /// <summary>报告生成失败（含结果正文里嵌的错误）。</summary>
    ReportFailed = 6,

    /// <summary>输出文件写入失败。</summary>
    OutputFailed = 7,
}

/// <summary>退出码的稳定字符串形式，用于 JSON 信封里的 <c>error.code</c>。</summary>
public static class ErrorCodes
{
    /// <summary>把退出码转成稳定的 wire 名称。</summary>
    public static string ToWireName(ExitCode code) => code switch
    {
        ExitCode.Ok => "OK",
        ExitCode.Internal => "INTERNAL",
        ExitCode.Usage => "USAGE",
        ExitCode.ConnectFailed => "CONNECT_FAILED",
        ExitCode.SnapshotNotFound => "SNAPSHOT_NOT_FOUND",
        ExitCode.PrivilegeDenied => "PRIVILEGE_DENIED",
        ExitCode.ReportFailed => "REPORT_FAILED",
        ExitCode.OutputFailed => "OUTPUT_FAILED",
        _ => "INTERNAL",
    };
}

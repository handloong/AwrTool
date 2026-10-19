namespace AwrTool.Cli;

/// <summary>
/// 带退出码语义的工具异常。所有可预期的失败都用它抛出，
/// 由 <c>Program</c> 统一翻译成退出码 + JSON 失败信封。
/// </summary>
public sealed class AwrToolException : Exception
{
    /// <summary>该失败对应的进程退出码。</summary>
    public ExitCode Code { get; }

    /// <summary>相关 Oracle 错误号（如有），会出现在 JSON 信封里方便排查。</summary>
    public int? OraNumber { get; }

    /// <summary>构造。</summary>
    public AwrToolException(ExitCode code, string message, int? oraNumber = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        OraNumber = oraNumber;
    }

    /// <summary>参数/用法错误。</summary>
    public static AwrToolException Usage(string message, Exception? inner = null)
        => new(ExitCode.Usage, message, null, inner);
}

using AwrTool.Core;

namespace AwrTool.Cli;

/// <summary>
/// 解析后的命令行选项。所有取值在这一层就已经完成类型转换与校验，
/// 越靠后越不可能出现「参数写错了但没报错」的情况。
/// </summary>
public sealed record CliOptions
{
    /// <summary>子命令。</summary>
    public CliCommand Command { get; init; } = CliCommand.Help;

    /// <summary>是否请求帮助。</summary>
    public bool Help { get; init; }

    // ---- 连接 ----

    /// <summary>完整连接串。</summary>
    public string? ConnectString { get; init; }

    /// <summary>Data Source。</summary>
    public string? Dsn { get; init; }

    /// <summary>用户名。</summary>
    public string? User { get; init; }

    /// <summary>密码。</summary>
    public string? Password { get; init; }

    /// <summary>数据库 ID，缺省自动探测。</summary>
    public long? Dbid { get; init; }

    /// <summary>命令超时秒数。</summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>静默模式。</summary>
    public bool Quiet { get; init; }

    /// <summary>详细输出。</summary>
    public bool Verbose { get; init; }

    // ---- report ----

    /// <summary>报告类型。</summary>
    public ReportKind Type { get; init; } = ReportKind.Awr;

    /// <summary>报告格式。</summary>
    public ReportFormat Format { get; init; } = ReportFormat.Html;

    /// <summary>相对时间窗口。</summary>
    public TimeSpan? Last { get; init; }

    /// <summary>起始快照号。</summary>
    public int? BeginSnap { get; init; }

    /// <summary>结束快照号。</summary>
    public int? EndSnap { get; init; }

    /// <summary>起始时刻。</summary>
    public DateTime? BeginTime { get; init; }

    /// <summary>结束时刻。</summary>
    public DateTime? EndTime { get; init; }

    /// <summary>实例号列表。</summary>
    public InstanceList? Instances { get; init; }

    /// <summary>输出路径，<c>-</c> 表示 stdout。</summary>
    public string? Out { get; init; }

    /// <summary>
    /// 未指定 <see cref="Out"/> 时的输出目录。<c>null</c> 表示当前工作目录。
    /// MCP 场景下当前目录不可预测，会显式指定一个稳定目录。
    /// </summary>
    public string? OutputDirectory { get; init; }

    /// <summary>允许覆盖已存在的输出文件。</summary>
    public bool Overwrite { get; init; }

    // ---- snapshots ----

    /// <summary>最多返回条数。</summary>
    public int Limit { get; init; } = 24;

    /// <summary>回溯窗口。</summary>
    public TimeSpan? Since { get; init; }
}

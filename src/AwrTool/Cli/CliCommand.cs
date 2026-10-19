namespace AwrTool.Cli;

/// <summary>子命令。</summary>
public enum CliCommand
{
    /// <summary>生成 AWR 报告。</summary>
    Report,

    /// <summary>列出可用快照区间。</summary>
    Snapshots,

    /// <summary>自检。</summary>
    Doctor,

    /// <summary>启动 MCP stdio 服务器。</summary>
    Mcp,

    /// <summary>版本信息。</summary>
    Version,

    /// <summary>帮助。</summary>
    Help,
}

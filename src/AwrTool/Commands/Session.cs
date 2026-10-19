using AwrTool.Cli;
using AwrTool.Oracle;

namespace AwrTool.Commands;

/// <summary>命令共用的连接建立流程。</summary>
public static class Session
{
    /// <summary>按选项解析连接信息并建立连接。</summary>
    /// <param name="options">命令行选项。</param>
    /// <param name="pooling">是否启用连接池。CLI 单次运行关掉更利落，MCP 长驻开着更省。</param>
    /// <param name="cancellationToken">取消标志。</param>
    public static Task<AwrRepository> OpenAsync(
        CliOptions options,
        bool pooling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolved = ConnectionResolver.Resolve(
            options.ConnectString,
            options.Dsn,
            options.User,
            options.Password,
            Environment.GetEnvironmentVariable);

        return AwrRepository.OpenAsync(resolved, options.TimeoutSeconds, pooling, cancellationToken);
    }

    /// <summary>解析连接信息（不建连），供 doctor 单独报告配置来源。</summary>
    public static ResolvedConnection Resolve(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ConnectionResolver.Resolve(
            options.ConnectString,
            options.Dsn,
            options.User,
            options.Password,
            Environment.GetEnvironmentVariable);
    }
}

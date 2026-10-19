using System.Text;
using AwrTool.Cli;

namespace AwrTool.Mcp;

/// <summary>把 MCP 服务器接到真实的 stdin / stdout / stderr。</summary>
public static class McpLauncher
{
    /// <summary>启动 MCP stdio 服务器，直到 stdin 关闭。</summary>
    /// <remarks>
    /// stdout 只允许出现 JSON-RPC 消息，所以这里把日志全部导向 <c>Console.Error</c>。
    /// 输入单独包一层 UTF-8 reader，避免 Windows 控制台代码页破坏中文。
    /// </remarks>
    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var input = new StreamReader(
            Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var host = new OracleToolHost(options);
        var server = new McpServer(input, Console.Out, Console.Error, host);

        Console.Error.WriteLine(
            $"[mcp] awrtool {ToolInfo.Version} MCP stdio 服务器已启动，" +
            $"工具：{string.Join(", ", host.ListTools().Select(t => t.Name))}");

        await server.RunAsync(cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine("[mcp] stdin 已关闭，退出。");
        return (int)ExitCode.Ok;
    }
}

using System.Diagnostics;
using AwrTool.Cli;
using AwrTool.Commands;
using AwrTool.Mcp;
using AwrTool.Output;

namespace AwrTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // stdout / stderr 统一 UTF-8 无 BOM，并把控制台代码页切到 65001。
        // 少了后者，简体中文 cmd 会用 936(GBK) 解码 UTF-8 字节，中文变乱码。
        ConsoleOutput.Configure();

        var stopwatch = Stopwatch.StartNew();
        var commandName = "unknown";
        CliOptions? options = null;

        try
        {
            options = CliParser.Parse(args);
            commandName = CommandName(options.Command);

            if (options.Help || options.Command == CliCommand.Help)
            {
                Console.Out.WriteLine(HelpText.Full);
                return (int)ExitCode.Ok;
            }

            switch (options.Command)
            {
                case CliCommand.Version:
                    WriteEnvelope(
                        new ResultEnvelope
                        {
                            Ok = true,
                            Command = "version",
                            ElapsedMs = stopwatch.ElapsedMilliseconds,
                        },
                        options);

                    return (int)ExitCode.Ok;

                case CliCommand.Mcp:
                    // MCP 服务器自行管理 stdout，绝不走信封输出
                    return await McpLauncher.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
            }

            using var cancellation = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var envelope = options.Command switch
            {
                CliCommand.Report => await ReportCommand.RunAsync(options, cancellation.Token).ConfigureAwait(false),
                CliCommand.Snapshots => await SnapshotsCommand.RunAsync(options, cancellation.Token).ConfigureAwait(false),
                CliCommand.Doctor => await DoctorCommand.RunAsync(options, cancellation.Token).ConfigureAwait(false),
                _ => throw AwrToolException.Usage("该命令尚未实现。"),
            };

            WriteEnvelope(envelope, options);

            if (!envelope.Ok && !options.Quiet)
            {
                Console.Error.WriteLine($"[{envelope.Command}] 失败：{envelope.Error?.Message}");
            }

            return (int)envelope.ExitCode;
        }
        catch (AwrToolException ex)
        {
            return Fail(ex.Code, ex.Message, ex.OraNumber, commandName, options, stopwatch);
        }
        catch (OperationCanceledException)
        {
            return Fail(ExitCode.Internal, "操作被取消。", null, commandName, options, stopwatch);
        }
        catch (Exception ex)
        {
            return Fail(ExitCode.Internal, ex.Message, null, commandName, options, stopwatch);
        }
    }

    private static int Fail(
        ExitCode code,
        string message,
        int? ora,
        string command,
        CliOptions? options,
        Stopwatch stopwatch)
    {
        var envelope = new ResultEnvelope
        {
            Ok = false,
            ExitCode = code,
            Command = command,
            Error = new ErrorSummary(ErrorCodes.ToWireName(code), message, ora),
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };

        WriteEnvelope(envelope, options);

        if (options?.Quiet != true)
        {
            Console.Error.WriteLine($"[{command}] {ErrorCodes.ToWireName(code)}: {message}");
        }

        return (int)code;
    }

    private static void WriteEnvelope(ResultEnvelope envelope, CliOptions? options)
    {
        var json = envelope.ToJson();

        // -o - 时 stdout 被报告正文占用，信封改走 stderr，保证管道里拿到的只有报告
        if (options?.Out == "-")
        {
            Console.Error.WriteLine(json);
            return;
        }

        Console.Out.WriteLine(json);
    }

    private static string CommandName(CliCommand command) => command switch
    {
        CliCommand.Report => "report",
        CliCommand.Snapshots => "snapshots",
        CliCommand.Doctor => "doctor",
        CliCommand.Mcp => "mcp",
        CliCommand.Version => "version",
        _ => "help",
    };
}

using System.Text.Json;
using AwrTool.Cli;
using AwrTool.Commands;
using AwrTool.Core;
using AwrTool.Oracle;
using AwrTool.Output;

namespace AwrTool.Mcp;

/// <summary>
/// 把 AwrTool 的能力暴露成三个 MCP 工具。
/// </summary>
/// <remarks>
/// 连接信息在启动时解析一次，每次工具调用按需建连（开启池化，开销很小），
/// 这样可以避免长驻进程里持有陈旧连接。配置有问题时服务照常启动 ——
/// 只是每次调用都返回明确错误，这样 <c>tools/list</c> 仍可用，模型也能把问题报出来，
/// 比进程直接退出更好排查。
/// </remarks>
public sealed class OracleToolHost : IMcpToolHost
{
    /// <summary>工具名：执行只读查询。</summary>
    public const string QueryTool = "oracle_query";

    /// <summary>工具名：统计表行数。</summary>
    public const string TableRowCountTool = "oracle_table_rowcount";

    /// <summary>工具名：拉取 AWR 报告。</summary>
    public const string AwrReportTool = "awr_report";

    private const int DefaultQueryRows = 200;
    private const int MaxQueryRows = 5000;

    private readonly CliOptions _options;
    private readonly ResolvedConnection? _connection;
    private readonly string? _configurationError;

    /// <summary>按命令行选项构造。</summary>
    public OracleToolHost(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        try
        {
            _connection = Session.Resolve(options);
        }
        catch (AwrToolException ex)
        {
            _configurationError = ex.Message;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<McpToolDefinition> ListTools() =>
    [
        new(
            QueryTool,
            "执行 Oracle 查询",
            "在目标 Oracle 库上执行单条只读查询（SELECT / WITH）并返回结果集。参数：sql 必填；maxRows 限制返回行数（默认 200，上限 5000）；timeoutSeconds 控制语句超时。返回列名、行数据与 truncated 标记。",
            McpToolSchemas.Query),
        new(
            TableRowCountTool,
            "统计表行数",
            "统计指定表的总行数，可用 where 条件做过滤计数。参数：table 必填（可带 schema，如 SCOTT.EMP）；where 可选（不要写 WHERE 关键字）。返回精确的 COUNT(*) 结果。",
            McpToolSchemas.TableRowCount),
        new(
            AwrReportTool,
            "拉取 AWR 报告",
            "生成指定时间范围的 Oracle AWR 报告并写入本机文件，返回绝对路径与元数据。参数：last 相对窗口（如 2h，推荐）或 beginSnap/endSnap/beginTime/endTime；type 选 awr 或 global；format 选 html 或 txt。拿到路径后请读取该文件再分析。",
            McpToolSchemas.AwrReport),
    ];

    /// <inheritdoc />
    public async Task<McpToolResult> CallAsync(
        string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        return name switch
        {
            QueryTool => await RunQueryAsync(arguments, cancellationToken).ConfigureAwait(false),
            TableRowCountTool => await RunTableRowCountAsync(arguments, cancellationToken).ConfigureAwait(false),
            AwrReportTool => await RunAwrReportAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => throw AwrToolException.Usage($"未知工具：{name}"),
        };
    }

    private void EnsureConfigured()
    {
        if (_configurationError is not null)
        {
            throw new AwrToolException(
                ExitCode.ConnectFailed,
                "数据库连接信息未配置好，无法执行任何工具。" + _configurationError);
        }
    }

    private async Task<McpToolResult> RunQueryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var sql = McpArguments.RequireString(arguments, "sql");
        var maxRows = McpArguments.OptionalInt(arguments, "maxRows", DefaultQueryRows, 1, MaxQueryRows);
        var timeout = McpArguments.OptionalInt(
            arguments, "timeoutSeconds", _options.TimeoutSeconds, 1, 600);

        await using var repository = await AwrRepository
            .OpenAsync(_connection!, timeout, pooling: true, cancellationToken)
            .ConfigureAwait(false);

        var result = await repository.QueryAsync(sql, maxRows, cancellationToken).ConfigureAwait(false);
        var columns = Disambiguate(result.Columns);

        var text = JsonOut.Serialize(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("rowCount", result.RowCount);
            writer.WriteBoolean("truncated", result.Truncated);
            writer.WriteNumber("maxRows", maxRows);

            writer.WriteStartArray("columns");
            foreach (var column in columns)
            {
                writer.WriteStringValue(column);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("rows");
            foreach (var row in result.Rows)
            {
                writer.WriteStartObject();
                for (var i = 0; i < columns.Count; i++)
                {
                    writer.WritePropertyName(columns[i]);
                    JsonOut.WriteCell(writer, row[i]);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }, indented: false);

        return new McpToolResult(text, IsError: false);
    }

    private async Task<McpToolResult> RunTableRowCountAsync(
        JsonElement arguments, CancellationToken cancellationToken)
    {
        var table = McpArguments.RequireString(arguments, "table");
        var where = McpArguments.OptionalString(arguments, "where");
        var timeout = McpArguments.OptionalInt(
            arguments, "timeoutSeconds", _options.TimeoutSeconds, 1, 600);

        await using var repository = await AwrRepository
            .OpenAsync(_connection!, timeout, pooling: true, cancellationToken)
            .ConfigureAwait(false);

        var count = await repository.CountRowsAsync(table, where, cancellationToken).ConfigureAwait(false);

        var text = JsonOut.Serialize(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("table", table.Trim());

            if (string.IsNullOrWhiteSpace(where))
            {
                writer.WriteNull("where");
            }
            else
            {
                writer.WriteString("where", where.Trim());
            }

            writer.WriteNumber("count", count);
            writer.WriteBoolean("exact", true);
            writer.WriteEndObject();
        }, indented: false);

        return new McpToolResult(text, IsError: false);
    }

    private async Task<McpToolResult> RunAwrReportAsync(
        JsonElement arguments, CancellationToken cancellationToken)
    {
        var options = BuildReportOptions(arguments);

        // 直接复用 CLI 的 report 实现：一条路径，不维护第二套逻辑
        var envelope = await ReportCommand.RunAsync(options, cancellationToken, pooling: true)
            .ConfigureAwait(false);

        return new McpToolResult(envelope.ToJson(indented: false), IsError: false);
    }

    private CliOptions BuildReportOptions(JsonElement arguments) => _options with
    {
        Command = CliCommand.Report,
        Type = ReportKindParser.Parse(McpArguments.OptionalString(arguments, "type")),
        Format = ReportFormatParser.Parse(McpArguments.OptionalString(arguments, "format")),
        Last = McpArguments.OptionalDuration(arguments, "last"),
        BeginSnap = McpArguments.OptionalIntOrNull(arguments, "beginSnap"),
        EndSnap = McpArguments.OptionalIntOrNull(arguments, "endSnap"),
        BeginTime = McpArguments.OptionalTimestamp(arguments, "beginTime"),
        EndTime = McpArguments.OptionalTimestamp(arguments, "endTime"),
        Instances = McpArguments.OptionalInstances(arguments, "instNum"),
        Out = McpArguments.OptionalString(arguments, "outPath"),
        OutputDirectory = AwrToolPaths.DefaultReportDirectory(),
        Overwrite = McpArguments.OptionalBool(arguments, "overwrite", fallback: true),
    };

    /// <summary>列名重名时加后缀，避免 JSON 里出现重复键。</summary>
    private static IReadOnlyList<string> Disambiguate(IReadOnlyList<string> columns)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(columns.Count);

        foreach (var column in columns)
        {
            var name = string.IsNullOrWhiteSpace(column) ? "COLUMN" : column;

            if (seen.TryGetValue(name, out var count))
            {
                count++;
                seen[name] = count;
                result.Add($"{name}_{count}");
                continue;
            }

            seen[name] = 1;
            result.Add(name);
        }

        return result;
    }
}

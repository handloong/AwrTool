using System.Data;
using System.Globalization;
using AwrTool.Cli;
using AwrTool.Core;
using Oracle.ManagedDataAccess.Client;

namespace AwrTool.Oracle;

/// <summary>
/// Oracle 数据访问。所有 SQL 都在这里，命令层不直接拼 SQL。
/// </summary>
/// <remarks>
/// 连接、报告取数都遵守 <see cref="ReportSql"/> 与 <see cref="SnapshotSql"/> 里记录的实测约束。
/// 生命周期绑定一个连接，用完即 <see cref="DisposeAsync"/>。
/// </remarks>
public sealed class AwrRepository : IAsyncDisposable
{
    private readonly OracleConnection _connection;
    private readonly int _timeoutSeconds;

    private AwrRepository(OracleConnection connection, int timeoutSeconds)
    {
        _connection = connection;
        _timeoutSeconds = timeoutSeconds;
    }

    /// <summary>建立连接。</summary>
    /// <param name="resolved">已解析的连接信息。</param>
    /// <param name="timeoutSeconds">命令超时秒数。</param>
    /// <param name="pooling">是否启用连接池。CLI 单次运行不需要，MCP 长驻需要。</param>
    /// <param name="cancellationToken">取消标志。</param>
    public static async Task<AwrRepository> OpenAsync(
        ResolvedConnection resolved,
        int timeoutSeconds,
        bool pooling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        OracleConnectionStringBuilder builder;
        try
        {
            builder = new OracleConnectionStringBuilder(resolved.ConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new AwrToolException(
                ExitCode.ConnectFailed,
                $"连接串格式不合法（{resolved.ToSafeString()}）：{ex.Message}",
                null,
                ex);
        }

        // 覆盖（而不是拼接）超时与池化设置，避免用户手写的重复键导致行为不确定。
        builder.ConnectionTimeout = 15;
        builder.Pooling = pooling;

        var connection = new OracleConnection(builder.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            var code = OraErrorClassifier.Classify(ex, ExitCode.ConnectFailed);
            var ora = OraErrorClassifier.FindOraNumber(ex);

            throw new AwrToolException(
                code,
                $"连接数据库失败（{resolved.ToSafeString()}）：{FirstLine(ex.Message)}",
                ora,
                ex);
        }

        return new AwrRepository(connection, timeoutSeconds);
    }

    /// <summary>探测 dbid、库名、当前实例与版本。</summary>
    /// <remarks>
    /// 优先取 <c>version_full</c>：19c 上 <c>version</c> 只给 <c>19.0.0.0.0</c>，
    /// 而实际补丁版本（如 <c>19.31.0.0.0</c>）在 <c>version_full</c> 里。
    /// 该列是 18c 才有的，老库会报 ORA-00904，此时退回 <c>version</c>。
    /// </remarks>
    public async Task<DatabaseInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadProbeAsync(ProbeWithFullVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException ex) when (ex.Number == 904)
        {
            try
            {
                return await ReadProbeAsync(ProbeLegacyVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException inner)
            {
                throw Translate(inner, ExitCode.PrivilegeDenied, "探测数据库信息失败");
            }
        }
        catch (OracleException ex)
        {
            throw Translate(ex, ExitCode.PrivilegeDenied, "探测数据库信息失败");
        }
    }

    private const string ProbeWithFullVersion = """
        select d.dbid,
               d.name,
               i.instance_number,
               i.instance_name,
               i.host_name,
               i.version_full
        from   v$database d, v$instance i
        """;

    private const string ProbeLegacyVersion = """
        select d.dbid,
               d.name,
               i.instance_number,
               i.instance_name,
               i.host_name,
               i.version
        from   v$database d, v$instance i
        """;

    private async Task<DatabaseInfo> ReadProbeAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new AwrToolException(
                ExitCode.PrivilegeDenied,
                "读不到 v$database / v$instance，账号可能缺少权限。");
        }

        return new DatabaseInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    /// <summary>列出快照。</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        long dbid,
        int? instanceNumber,
        TimeSpan lookback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = CreateCommand(SnapshotSql.Build(dbid, instanceNumber, lookback));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var snapshots = new List<SnapshotInfo>();

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                snapshots.Add(new SnapshotInfo(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    ReadTime(reader, 2),
                    ReadTime(reader, 3)));
            }

            return snapshots;
        }
        catch (OracleException ex)
        {
            throw Translate(ex, ExitCode.SnapshotNotFound, "查询快照失败");
        }
    }

    /// <summary>流式生成报告并写入 <paramref name="writer"/>。</summary>
    public async Task<ReportStats> StreamReportAsync(
        ReportRequest request,
        TextWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);

        try
        {
            await using var command = CreateCommand(ReportSql.Build(request));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var rows = 0;
            long chars = 0;
            var inspectedHead = 0;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var line = reader.GetString(0);

                // 只在开头几行内查内嵌错误：函数失败时错误横幅就在第一行，
                // 全文扫描会因为正常报告里可能出现类似字样而误判。
                if (inspectedHead < 5)
                {
                    inspectedHead++;

                    if (OraErrorClassifier.TryParseEmbeddedError(line, out var ora))
                    {
                        throw new AwrToolException(
                            ExitCode.ReportFailed,
                            $"报告生成失败：{line.Trim()}",
                            ora);
                    }
                }

                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                rows++;
                chars += line.Length;
            }

            if (rows == 0)
            {
                throw new AwrToolException(ExitCode.ReportFailed, "报告生成返回了空结果。");
            }

            return new ReportStats(rows, chars);
        }
        catch (OracleException ex)
        {
            throw Translate(ex, ExitCode.ReportFailed, "生成报告失败");
        }
    }

    /// <summary>执行只读查询。</summary>
    /// <param name="sql">查询语句，会先过 <see cref="SqlGuard.EnsureReadOnly"/>。</param>
    /// <param name="maxRows">最多返回行数。</param>
    /// <param name="cancellationToken">取消标志。</param>
    public async Task<QueryResult> QueryAsync(
        string sql,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        var guarded = SqlGuard.EnsureReadOnly(sql);

        try
        {
            await using var command = CreateCommand(guarded);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var columns = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns[i] = reader.GetName(i);
            }

            var rows = new List<IReadOnlyList<object?>>();
            var truncated = false;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var values = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(values);
            }

            return new QueryResult(columns, rows, truncated);
        }
        catch (OracleException ex)
        {
            throw Translate(ex, ExitCode.ReportFailed, "查询失败");
        }
    }

    /// <summary>统计表行数。</summary>
    public async Task<long> CountRowsAsync(
        string table,
        string? where,
        CancellationToken cancellationToken = default)
    {
        var name = SqlGuard.EnsureTableName(table);

        var sql = $"select count(*) from {name}";
        if (!string.IsNullOrWhiteSpace(where))
        {
            var condition = where.Trim();

            if (condition.Contains(';', StringComparison.Ordinal))
            {
                throw AwrToolException.Usage("where 条件里不能出现分号。");
            }

            sql += " where " + condition;
        }

        try
        {
            await using var command = CreateCommand(sql);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return value is null or DBNull
                ? 0L
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (OracleException ex)
        {
            throw Translate(ex, ExitCode.ReportFailed, "统计行数失败");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    private OracleCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _timeoutSeconds;
        command.BindByName = true;
        return command;
    }

    private static DateTime ReadTime(OracleDataReader reader, int ordinal)
        => reader.GetFieldType(ordinal) == typeof(DateTime)
            ? reader.GetDateTime(ordinal)
            : reader.GetOracleTimeStamp(ordinal).Value;

    private static AwrToolException Translate(OracleException ex, ExitCode fallback, string context)
        => new(
            OraErrorClassifier.Classify(ex, fallback),
            $"{context}：{FirstLine(ex.Message)}",
            ex.Number,
            ex);

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }
}

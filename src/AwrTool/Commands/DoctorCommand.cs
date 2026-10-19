using System.Diagnostics;
using AwrTool.Cli;
using AwrTool.Core;
using AwrTool.Oracle;
using AwrTool.Output;

namespace AwrTool.Commands;

/// <summary>自检：配置、连通性、身份、快照可见性、报告权限。</summary>
public static class DoctorCommand
{
    private const string OkStatus = "ok";
    private const string FailStatus = "fail";
    private const string WarnStatus = "warn";

    /// <summary>执行 doctor 命令。</summary>
    public static async Task<ResultEnvelope> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stopwatch = Stopwatch.StartNew();
        var checks = new List<DoctorCheck>();
        DatabaseInfo? db = null;
        var failureCode = ExitCode.Internal;

        // 先报输出编码：中文乱码是纯显示问题，跟数据库无关，但排查时最容易被误判。
        checks.Add(new DoctorCheck("console", OkStatus, ConsoleOutput.Describe()));

        ResolvedConnection? resolved = null;

        try
        {
            resolved = Session.Resolve(options);
            checks.Add(new DoctorCheck(
                "config", OkStatus, $"来源 {DescribeSource(resolved.Source)}：{resolved.ToSafeString()}"));
        }
        catch (AwrToolException ex)
        {
            failureCode = ex.Code;
            checks.Add(new DoctorCheck("config", FailStatus, ex.Message));
        }

        if (resolved is not null)
        {
            await using var repository = await TryOpenAsync(
                resolved, options, checks, cancellationToken, code => failureCode = code).ConfigureAwait(false);

            if (repository is not null)
            {
                db = await TryProbeAsync(repository, checks, cancellationToken, code => failureCode = code)
                    .ConfigureAwait(false);

                if (db is not null)
                {
                    await CheckSnapshotsAndReportAsync(
                        repository, options, db, checks, cancellationToken, code => failureCode = code)
                        .ConfigureAwait(false);
                }
            }
        }

        stopwatch.Stop();

        var failed = checks.FirstOrDefault(c => c.Status == FailStatus);

        return new ResultEnvelope
        {
            Ok = failed is null,
            ExitCode = failed is null ? ExitCode.Ok : failureCode,
            Command = "doctor",
            Db = db,
            Checks = checks,
            Error = failed is null ? null : new ErrorSummary(ErrorCodes.ToWireName(failureCode), failed.Detail, null),
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static async Task<AwrRepository?> TryOpenAsync(
        ResolvedConnection resolved,
        CliOptions options,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken,
        Action<ExitCode> onFailure)
    {
        try
        {
            var repository = await AwrRepository
                .OpenAsync(resolved, options.TimeoutSeconds, pooling: false, cancellationToken)
                .ConfigureAwait(false);

            checks.Add(new DoctorCheck("login", OkStatus, "登录成功"));
            return repository;
        }
        catch (AwrToolException ex)
        {
            onFailure(ex.Code);
            checks.Add(new DoctorCheck("login", FailStatus, ex.Message));
            return null;
        }
    }

    private static async Task<DatabaseInfo?> TryProbeAsync(
        AwrRepository repository,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken,
        Action<ExitCode> onFailure)
    {
        try
        {
            var db = await repository.ProbeAsync(cancellationToken).ConfigureAwait(false);

            checks.Add(new DoctorCheck(
                "identity",
                OkStatus,
                $"{db.Name}（dbid={db.Dbid}，实例 {db.InstanceNumber}/{db.InstanceName}，{db.Version}，host {db.HostName}）"));

            return db;
        }
        catch (AwrToolException ex)
        {
            onFailure(ex.Code);
            checks.Add(new DoctorCheck("identity", FailStatus, ex.Message));
            return null;
        }
    }

    private static async Task CheckSnapshotsAndReportAsync(
        AwrRepository repository,
        CliOptions options,
        DatabaseInfo db,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken,
        Action<ExitCode> onFailure)
    {
        var dbid = options.Dbid ?? db.Dbid;
        IReadOnlyList<SnapshotInfo> snapshots;

        try
        {
            snapshots = await repository
                .ListSnapshotsAsync(dbid, null, TimeSpan.FromHours(48), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AwrToolException ex)
        {
            onFailure(ex.Code);
            checks.Add(new DoctorCheck("snapshots", FailStatus, ex.Message));
            return;
        }

        var instances = snapshots.Select(s => s.InstanceNumber).Distinct().Order().ToArray();

        if (snapshots.Count == 0)
        {
            checks.Add(new DoctorCheck("snapshots", WarnStatus, "48 小时内没有快照，AWR 可能未启用或保留期过短。"));
            return;
        }

        checks.Add(new DoctorCheck(
            "snapshots",
            OkStatus,
            $"48 小时内 {snapshots.Count} 条，实例 {string.Join(",", instances)}，最新 {snapshots[^1].EndTime:yyyy-MM-dd HH:mm:ss}"));

        // 端到端验证报告权限：真的生成一次，但直接丢弃内容，不落盘。
        var mine = snapshots.Where(s => s.InstanceNumber == db.InstanceNumber).ToArray();

        if (mine.Length < 2)
        {
            checks.Add(new DoctorCheck("report", WarnStatus, "该实例快照不足以构成区间，跳过报告生成验证。"));
            return;
        }

        try
        {
            var range = SnapshotSelector.Select(mine);
            var request = new ReportRequest(
                dbid,
                InstanceList.Single(db.InstanceNumber),
                range.BeginSnapId,
                range.EndSnapId,
                ReportKind.Awr,
                ReportFormat.Text);

            var sink = new DiscardWriter();
            var stats = await repository.StreamReportAsync(request, sink, cancellationToken).ConfigureAwait(false);

            checks.Add(new DoctorCheck(
                "report",
                OkStatus,
                $"{range.BeginSnapId}→{range.EndSnapId} 生成成功（{stats.Rows} 行 / {stats.Chars} 字符）"));
        }
        catch (AwrToolException ex)
        {
            onFailure(ex.Code);
            checks.Add(new DoctorCheck("report", FailStatus, ex.Message));
        }
    }

    private static string DescribeSource(ConnectionSource source) => source switch
    {
        ConnectionSource.ArgumentConnectString => "--connect",
        ConnectionSource.EnvironmentConnectString => ConnectionResolver.EnvConn,
        _ => "分项参数 / 环境变量",
    };
}

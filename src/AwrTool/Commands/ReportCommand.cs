using System.Diagnostics;
using AwrTool.Cli;
using AwrTool.Core;
using AwrTool.Oracle;
using AwrTool.Output;

namespace AwrTool.Commands;

/// <summary>生成 AWR 报告并落盘。</summary>
public static class ReportCommand
{
    // 指定快照号时不知道用户要多久以前的，给一个足够宽的窗口。
    // AWR 默认保留 8 天，30 天能覆盖绝大多数现场；查不到会明确报出可用范围。
    private static readonly TimeSpan SnapshotLookback = TimeSpan.FromDays(30);

    /// <summary>执行 report 命令。</summary>
    /// <param name="options">命令行选项。</param>
    /// <param name="cancellationToken">取消标志。</param>
    /// <param name="pooling">是否启用连接池。MCP 长驻场景开，CLI 单次运行关。</param>
    public static async Task<ResultEnvelope> RunAsync(
        CliOptions options,
        CancellationToken cancellationToken,
        bool pooling = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();

        await using var repository = await Session.OpenAsync(options, pooling, cancellationToken)
            .ConfigureAwait(false);

        var db = await repository.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var dbid = options.Dbid ?? db.Dbid;

        // 用 --last 时窗口可以算出来，但要多留余量：快照间隔是实例相关的配置
        // （现场常见 30m / 1h，也可能更长），窗口取太紧会漏掉起点快照。
        // 多查几十行快照的成本可以忽略，选区间的准确性更重要。
        var lookback = options.Last is { } last ? last + TimeSpan.FromHours(24) : SnapshotLookback;

        // 不带实例过滤拉一次，用来：(1) 判断有哪些实例 (2) 按参考实例选区间。
        var allSnapshots = await repository.ListSnapshotsAsync(dbid, null, lookback, cancellationToken)
            .ConfigureAwait(false);

        if (allSnapshots.Count == 0)
        {
            throw new AwrToolException(
                ExitCode.SnapshotNotFound,
                $"最近 {lookback.TotalDays:0.#} 天内没有可用快照。请用 snapshots 命令确认，或放宽 --last。");
        }

        var instances = options.Instances ?? DefaultInstances(options, db, allSnapshots);

        // RAC 下 snap_id 在所有实例间是一致的，用列表里第一个实例当参考即可。
        var referenceInstance = instances.Numbers[0];
        var referenceSnapshots = allSnapshots
            .Where(s => s.InstanceNumber == referenceInstance)
            .ToArray();

        if (referenceSnapshots.Length == 0)
        {
            var available = string.Join(", ", allSnapshots.Select(s => s.InstanceNumber).Distinct().Order());
            throw new AwrToolException(
                ExitCode.SnapshotNotFound,
                $"实例 {referenceInstance} 在最近 {lookback.TotalDays:0.#} 天内没有快照。可用实例：{available}。");
        }

        var range = SnapshotSelector.Select(
            referenceSnapshots,
            options.BeginSnap,
            options.EndSnap,
            options.Last,
            options.BeginTime,
            options.EndTime);

        var request = new ReportRequest(
            dbid, instances, range.BeginSnapId, range.EndSnapId, options.Type, options.Format);

        var target = options.Out
            ?? Path.Combine(options.OutputDirectory ?? ".", DefaultFileName(request, range, db));

        var artifact = await ReportSink.WriteAsync(
            target,
            options.Overwrite,
            async writer => await repository.StreamReportAsync(request, writer, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        return new ResultEnvelope
        {
            Ok = true,
            Command = "report",
            Db = db,
            Report = new ReportSummary(
                options.Type,
                options.Format,
                instances.Numbers,
                range.BeginSnapId,
                range.EndSnapId,
                range.BeginTime,
                range.EndTime),
            Output = artifact,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static InstanceList DefaultInstances(
        CliOptions options, DatabaseInfo db, IReadOnlyList<SnapshotInfo> snapshots)
    {
        if (options.Type != ReportKind.Global)
        {
            return InstanceList.Single(db.InstanceNumber);
        }

        // Global 报告默认覆盖「有历史数据」的全部实例，而不是当前连接的这一个。
        var numbers = snapshots.Select(s => s.InstanceNumber).Distinct().Order().ToArray();
        return InstanceList.Parse(string.Join(",", numbers));
    }

    private static string DefaultFileName(ReportRequest request, SnapshotRange range, DatabaseInfo db)
    {
        var type = request.Kind == ReportKind.Global ? "global" : "awr";
        var instances = string.Join("-", request.Instances.Numbers);

        return $"awr_{db.Name}_{type}_{instances}_{range.BeginSnapId}_{range.EndSnapId}.{request.Format.ToExtension()}";
    }
}

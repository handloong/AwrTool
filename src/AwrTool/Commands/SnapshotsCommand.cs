using System.Diagnostics;
using AwrTool.Cli;
using AwrTool.Core;
using AwrTool.Output;

namespace AwrTool.Commands;

/// <summary>列出可用快照区间。</summary>
public static class SnapshotsCommand
{
    /// <summary>执行 snapshots 命令。</summary>
    public static async Task<ResultEnvelope> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();

        await using var repository = await Session.OpenAsync(options, pooling: false, cancellationToken)
            .ConfigureAwait(false);

        var db = await repository.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var dbid = options.Dbid ?? db.Dbid;
        var since = options.Since ?? TimeSpan.FromHours(48);

        // 默认只看当前连接实例：RAC 下同一 snap_id 每个实例各一行，
        // 全部列出来会让区间列表出现重复，反而不好挑。
        var instance = options.Instances?.Numbers[0] ?? db.InstanceNumber;

        var all = await repository.ListSnapshotsAsync(dbid, instance, since, cancellationToken)
            .ConfigureAwait(false);

        if (all.Count == 0)
        {
            throw new AwrToolException(
                ExitCode.SnapshotNotFound,
                $"最近 {since.TotalHours:0.#} 小时内没有可用快照（实例 {instance}）。");
        }

        // 只回最近的 limit 条，但保持升序，方便直接拿来当 begin/end。
        var selected = all.Count > options.Limit
            ? all.Skip(all.Count - options.Limit).ToArray()
            : all.ToArray();

        stopwatch.Stop();

        return new ResultEnvelope
        {
            Ok = true,
            Command = "snapshots",
            Db = db,
            Snapshots = selected,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }
}

using AwrTool.Cli;

namespace AwrTool.Core;

/// <summary>
/// 从一个实例的快照列表里挑出报告区间。纯函数，不碰数据库，便于单测。
/// </summary>
/// <remarks>
/// 覆盖语义见 <see cref="SnapshotRange"/>：报告覆盖 <c>[end_interval_time(bid), end_interval_time(eid)]</c>。
/// 所有「按时间对齐」的选择器都统一用「取 <c>end_interval_time &lt;= T</c> 中最大的那个快照」，
/// 这样结果不会晚于用户给的时间点，行为可预测。
/// </remarks>
public static class SnapshotSelector
{
    /// <summary>选出报告区间。</summary>
    /// <param name="snapshots">该实例的快照列表，顺序任意。</param>
    /// <param name="beginSnapId">显式起始快照号。与 <paramref name="last"/>、<paramref name="beginTime"/> 互斥。</param>
    /// <param name="endSnapId">显式结束快照号。与 <paramref name="endTime"/> 互斥。</param>
    /// <param name="last">相对窗口，如 2h。与显式起始选择器互斥。</param>
    /// <param name="beginTime">起始时刻，对齐到不晚于它的快照。</param>
    /// <param name="endTime">结束时刻，对齐到不晚于它的快照。</param>
    public static SnapshotRange Select(
        IReadOnlyList<SnapshotInfo> snapshots,
        int? beginSnapId = null,
        int? endSnapId = null,
        TimeSpan? last = null,
        DateTime? beginTime = null,
        DateTime? endTime = null)
    {
        ValidateExclusive(beginSnapId, endSnapId, last, beginTime, endTime);

        if (last is { } span && span <= TimeSpan.Zero)
        {
            throw AwrToolException.Usage("--last 必须大于零。");
        }

        // 防御性去重：RAC 下同一 snap_id 可能重复出现。
        var ordered = snapshots
            .GroupBy(s => s.SnapId)
            .Select(g => g.First())
            .OrderBy(s => s.SnapId)
            .ToArray();

        if (ordered.Length == 0)
        {
            throw new AwrToolException(
                ExitCode.SnapshotNotFound,
                "该实例在指定范围内没有任何可用快照。请用 snapshots 命令查看可用区间，或确认账号有 dba_hist_snapshot 的查询权限。");
        }

        var end = ResolveEnd(ordered, endSnapId, endTime);
        var (begin, beginExplicit) = ResolveBegin(ordered, end, beginSnapId, last, beginTime);

        if (begin.SnapId == end.SnapId)
        {
            // 显式指定同一对快照 = 用户写错了；自动推出来重合 = 可用数据不足。
            throw beginExplicit
                ? AwrToolException.Usage($"起始与结束快照不能是同一个（{begin.SnapId}）。")
                : new AwrToolException(
                    ExitCode.SnapshotNotFound,
                    "可用快照不足以构成一个区间（至少需要两个）。请用 snapshots 命令查看可用区间。");
        }

        if (begin.SnapId > end.SnapId)
        {
            throw AwrToolException.Usage(
                $"起始快照 {begin.SnapId} 晚于结束快照 {end.SnapId}，区间为空。");
        }

        // 覆盖区间取 end_interval_time 两端，与报告头一致。
        return new SnapshotRange(begin.SnapId, end.SnapId, begin.EndTime, end.EndTime);
    }

    private static void ValidateExclusive(
        int? beginSnapId, int? endSnapId, TimeSpan? last, DateTime? beginTime, DateTime? endTime)
    {
        if (last is not null && beginSnapId is not null)
        {
            throw AwrToolException.Usage("--last 与 --begin-snap 互斥，只能给一个。");
        }

        if (last is not null && beginTime is not null)
        {
            throw AwrToolException.Usage("--last 与 --begin-time 互斥，只能给一个。");
        }

        if (beginSnapId is not null && beginTime is not null)
        {
            throw AwrToolException.Usage("--begin-snap 与 --begin-time 互斥，只能给一个。");
        }

        if (endSnapId is not null && endTime is not null)
        {
            throw AwrToolException.Usage("--end-snap 与 --end-time 互斥，只能给一个。");
        }
    }

    private static SnapshotInfo ResolveEnd(
        SnapshotInfo[] ordered, int? endSnapId, DateTime? endTime)
    {
        if (endSnapId is int explicitId)
        {
            return Require(ordered, explicitId);
        }

        if (endTime is DateTime time)
        {
            return AlignToNotLater(ordered, time)
                ?? throw new AwrToolException(
                    ExitCode.SnapshotNotFound,
                    $"没有不晚于 {time:yyyy-MM-dd HH:mm:ss} 的快照。可用范围：" +
                    $"{ordered[0].EndTime:yyyy-MM-dd HH:mm:ss} ~ {ordered[^1].EndTime:yyyy-MM-dd HH:mm:ss}。");
        }

        return ordered[^1];
    }

    private static (SnapshotInfo Snapshot, bool Explicit) ResolveBegin(
        SnapshotInfo[] ordered, SnapshotInfo end, int? beginSnapId, TimeSpan? last, DateTime? beginTime)
    {
        if (beginSnapId is int explicitId)
        {
            return (Require(ordered, explicitId), true);
        }

        if (last is { } span)
        {
            var target = end.EndTime - span;

            // 快照粒度不够细时，退到最小的可用区间；跨出可见历史时，退到最早的快照。
            return (AlignToNotLater(ordered, target) ?? ordered[0], false);
        }

        if (beginTime is DateTime time)
        {
            return (AlignToNotLater(ordered, time)
                ?? throw new AwrToolException(
                    ExitCode.SnapshotNotFound,
                    $"没有不晚于 {time:yyyy-MM-dd HH:mm:ss} 的快照。可用范围：" +
                    $"{ordered[0].EndTime:yyyy-MM-dd HH:mm:ss} ~ {ordered[^1].EndTime:yyyy-MM-dd HH:mm:ss}。"),
                true);
        }

        // 没给起始选择器：取结束快照的前一个。
        // index <= 0 说明结束快照就是最早的那个，此时返回自身，交给调用处的
        // 「起始与结束相同」检查报 SnapshotNotFound（可用数据不足，不是用法错误）。
        var index = Array.FindIndex(ordered, s => s.SnapId == end.SnapId);
        return (index <= 0 ? end : ordered[index - 1], false);
    }

    private static SnapshotInfo? AlignToNotLater(SnapshotInfo[] ordered, DateTime time)
    {
        SnapshotInfo? best = null;

        foreach (var snapshot in ordered)
        {
            if (snapshot.EndTime <= time && (best is null || snapshot.EndTime > best.EndTime))
            {
                best = snapshot;
            }
        }

        return best;
    }

    private static SnapshotInfo Require(SnapshotInfo[] ordered, int snapId)
    {
        foreach (var snapshot in ordered)
        {
            if (snapshot.SnapId == snapId)
            {
                return snapshot;
            }
        }

        throw new AwrToolException(
            ExitCode.SnapshotNotFound,
            $"快照 {snapId} 在该实例下不存在。可用范围：" +
            $"{ordered[0].SnapId} ~ {ordered[^1].SnapId}。");
    }
}

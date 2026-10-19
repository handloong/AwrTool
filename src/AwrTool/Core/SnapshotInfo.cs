namespace AwrTool.Core;

/// <summary>
/// 一条 AWR 快照记录。
/// </summary>
/// <remarks>
/// RAC 环境下 <c>dba_hist_snapshot</c> 每个实例各有一行、<c>snap_id</c> 相同，
/// 所以查询时必须按 <see cref="InstanceNumber"/> 过滤。
/// </remarks>
/// <param name="SnapId">快照号。</param>
/// <param name="InstanceNumber">实例号。</param>
/// <param name="BeginTime">快照区间起点（<c>begin_interval_time</c>）。</param>
/// <param name="EndTime">快照区间终点（<c>end_interval_time</c>）。</param>
public sealed record SnapshotInfo(int SnapId, int InstanceNumber, DateTime BeginTime, DateTime EndTime);

/// <summary>
/// 选定的一段报告区间。
/// </summary>
/// <remarks>
/// <see cref="BeginTime"/> / <see cref="EndTime"/> 取的是 **<c>end_interval_time</c>**，
/// 而不是 <c>begin_interval_time</c>。这是实测结论：对 <c>awr_report_text(dbid, 2, 36819, 36820)</c>，
/// 报告头写的是 <c>Begin Snap: 36819 ... 16:00:28</c>、<c>Elapsed: 60.10 (mins)</c>，
/// 而 36819 的 <c>end_interval_time</c> 正是 16:00:28。
/// 也就是说报告覆盖的是 <c>[end_interval_time(bid), end_interval_time(eid)]</c>。
/// </remarks>
public sealed record SnapshotRange(int BeginSnapId, int EndSnapId, DateTime BeginTime, DateTime EndTime)
{
    /// <summary>报告实际覆盖的时长。</summary>
    public TimeSpan Elapsed => EndTime - BeginTime;
}

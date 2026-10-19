using System.Globalization;

namespace AwrTool.Oracle;

/// <summary>
/// 拼查询 <c>dba_hist_snapshot</c> 的 SQL。
/// </summary>
/// <remarks>
/// <para>
/// RAC 环境下 <c>dba_hist_snapshot</c> 每个实例各有一行、<c>snap_id</c> 相同。
/// 不按 <c>instance_number</c> 过滤就会拿到重复行，区间会选歪。
/// </para>
/// <para>
/// <b>窗口过滤必须用 <c>end_interval_time</c></b>，不能用 <c>begin_interval_time</c>。
/// 这是踩过的坑：<see cref="AwrTool.Core.SnapshotSelector"/> 是按 <c>end_interval_time</c>
/// 对齐的，而一个快照的 <c>begin_interval_time</c> 比它的 <c>end_interval_time</c>
/// 整整早一个快照间隔。用 begin 过滤时，只要窗口短于一个快照间隔，
/// 最近那个快照的 begin 就会落在窗口之外而被漏掉，导致「快照不足一条区间」。
/// </para>
/// </remarks>
public static class SnapshotSql
{
    /// <summary>拼查询快照列表的 SQL。</summary>
    /// <param name="dbid">数据库 ID。</param>
    /// <param name="instanceNumber">实例号；<c>null</c> 表示不过滤（列出全部实例）。</param>
    /// <param name="lookback">回溯窗口。</param>
    public static string Build(long dbid, int? instanceNumber, TimeSpan lookback)
    {
        // 换算成分钟做字面量；不足一分钟按一分钟算，避免出现恒假的 0/1440。
        var minutes = Math.Max(1, (long)Math.Ceiling(lookback.TotalMinutes));

        var instanceFilter = instanceNumber is int number
            ? $"\nand    instance_number = {number.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        return $"""
            select snap_id,
                   instance_number,
                   begin_interval_time,
                   end_interval_time
            from   dba_hist_snapshot
            where  dbid = {dbid.ToString(CultureInfo.InvariantCulture)}
            and    end_interval_time > sysdate - {minutes.ToString(CultureInfo.InvariantCulture)}/1440{instanceFilter}
            order by snap_id
            """;
    }
}

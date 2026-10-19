using AwrTool.Core;
using AwrTool.Oracle;
using Xunit;

namespace AwrTool.Tests;

public class SnapshotSqlTests
{
    [Fact]
    public void 不带实例过滤()
    {
        var sql = SnapshotSql.Build(2295037378, instanceNumber: null, TimeSpan.FromHours(48));

        Assert.Contains("dba_hist_snapshot", sql);
        Assert.Contains("dbid = 2295037378", sql);
        // 不过滤实例：只选列，不出现过滤条件
        Assert.DoesNotContain("instance_number =", sql);
    }

    [Fact]
    public void 带上实例过滤()
    {
        // RAC 下不加这个过滤，同一个 snap_id 每个实例各返回一行，区间会选歪
        var sql = SnapshotSql.Build(2295037378, 2, TimeSpan.FromHours(48));

        Assert.Contains("instance_number = 2", sql);
    }

    [Fact]
    public void 回溯窗口换算成分钟()
    {
        Assert.Contains("2880/1440", SnapshotSql.Build(1, null, TimeSpan.FromHours(48)));
        Assert.Contains("30/1440", SnapshotSql.Build(1, null, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void 窗口过滤必须用end_interval_time()
    {
        // 用 begin_interval_time 过滤会漏掉起点快照：它的 begin 比 end 早整整一个
        // 快照间隔，窗口短于一个间隔时就会落在窗口外，导致「快照不足以构成区间」。
        var sql = SnapshotSql.Build(1, null, TimeSpan.FromHours(1));
        Assert.Contains("end_interval_time >", sql);
        Assert.DoesNotContain("begin_interval_time >", sql);
    }

    [Fact]
    public void 返回需要的四列()
    {
        var sql = SnapshotSql.Build(1, 2, TimeSpan.FromHours(1));

        Assert.Contains("snap_id", sql);
        Assert.Contains("instance_number", sql);
        Assert.Contains("begin_interval_time", sql);
        Assert.Contains("end_interval_time", sql);
        Assert.Contains("order by snap_id", sql);
    }

    [Fact]
    public void 回溯窗口不足一分钟时至少按一分钟算()
    {
        // 避免出现 0/1440 这种恒假条件
        Assert.Contains("1/1440", SnapshotSql.Build(1, null, TimeSpan.FromSeconds(5)));
    }
}

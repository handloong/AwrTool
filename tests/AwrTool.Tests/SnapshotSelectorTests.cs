using AwrTool.Cli;
using AwrTool.Core;
using Xunit;

namespace AwrTool.Tests;

public class SnapshotSelectorTests
{
    // 实测自 192.168.1.7:1521/demo（instance 2），每小时一个快照。
    // 注意 begin_interval_time(N) == end_interval_time(N-1)。
    private static DateTime At(string hhmmss)
        => new(2026, 10, 16, int.Parse(hhmmss[..2]), int.Parse(hhmmss[3..5]), int.Parse(hhmmss[6..8]));

    private static readonly SnapshotInfo[] Hourly =
    [
        new(36817, 2, At("13:00:09"), At("14:00:16")),
        new(36818, 2, At("14:00:16"), At("15:00:22")),
        new(36819, 2, At("15:00:22"), At("16:00:28")),
        new(36820, 2, At("16:00:28"), At("17:00:34")),
    ];

    [Fact]
    public void 无参数取最近两个快照()
    {
        var range = SnapshotSelector.Select(Hourly);

        Assert.Equal(36819, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
        // 覆盖区间是 [end_interval_time(begin), end_interval_time(end)]，实测报告头就是这么写的
        Assert.Equal(At("16:00:28"), range.BeginTime);
        Assert.Equal(At("17:00:34"), range.EndTime);
        // 真实报告头写的是 "Elapsed: 60.10 (mins)"，即 3606 秒 —— 不是整 3600
        Assert.Equal(3606, range.Elapsed.TotalSeconds, 0);
    }

    [Fact]
    public void last_1h_落在快照粒度上()
    {
        var range = SnapshotSelector.Select(Hourly, last: TimeSpan.FromHours(1));
        Assert.Equal(36819, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
    }

    [Fact]
    public void last_2h_起点往前推一个快照()
    {
        var range = SnapshotSelector.Select(Hourly, last: TimeSpan.FromHours(2));
        Assert.Equal(36818, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
        // 15:00:22 -> 17:00:34 = 7212 秒，对应报告头的 "Elapsed: 120.20 (mins)"
        Assert.Equal(7212, range.Elapsed.TotalSeconds, 0);
    }

    [Fact]
    public void last_小于快照间隔时退化成最小可用区间()
    {
        // 快照是一小时一个，要 10 分钟也只能给一小时。不能报错，也不能装作给了 10 分钟。
        var range = SnapshotSelector.Select(Hourly, last: TimeSpan.FromMinutes(10));
        Assert.Equal(36819, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
    }

    [Fact]
    public void last_超出可见历史时退到最早的快照()
    {
        var range = SnapshotSelector.Select(Hourly, last: TimeSpan.FromHours(48));
        Assert.Equal(36817, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
    }

    [Fact]
    public void last_刚好等于边界时优先选中该快照()
    {
        // anchor=17:00:34，减 1h 得 16:00:34；end_interval_time <= 16:00:34 中最大的是 36819(16:00:28)
        var range = SnapshotSelector.Select(Hourly, last: TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(6));
        Assert.Equal(36819, range.BeginSnapId);
    }

    [Fact]
    public void 显式指定起止快照()
    {
        var range = SnapshotSelector.Select(Hourly, beginSnapId: 36817, endSnapId: 36819);
        Assert.Equal(36817, range.BeginSnapId);
        Assert.Equal(36819, range.EndSnapId);
        Assert.Equal(At("14:00:16"), range.BeginTime);
        Assert.Equal(At("16:00:28"), range.EndTime);
    }

    [Fact]
    public void 只给开始快照时结束取最新()
    {
        var range = SnapshotSelector.Select(Hourly, beginSnapId: 36818);
        Assert.Equal(36818, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
    }

    [Fact]
    public void 只给结束快照时开始取前一个()
    {
        var range = SnapshotSelector.Select(Hourly, endSnapId: 36819);
        Assert.Equal(36818, range.BeginSnapId);
        Assert.Equal(36819, range.EndSnapId);
    }

    [Fact]
    public void 起止快照相同时报用法错误()
    {
        var ex = Assert.Throws<AwrToolException>(() => SnapshotSelector.Select(Hourly, beginSnapId: 36819, endSnapId: 36819));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Fact]
    public void 开始大于结束时报用法错误()
    {
        var ex = Assert.Throws<AwrToolException>(() => SnapshotSelector.Select(Hourly, beginSnapId: 36820, endSnapId: 36818));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Fact]
    public void 快照号不存在时报快照错误()
    {
        var ex = Assert.Throws<AwrToolException>(() => SnapshotSelector.Select(Hourly, beginSnapId: 999999, endSnapId: 36820));
        Assert.Equal(ExitCode.SnapshotNotFound, ex.Code);
        Assert.Contains("999999", ex.Message);
    }

    [Fact]
    public void 快照列表为空时报快照错误()
    {
        var ex = Assert.Throws<AwrToolException>(() => SnapshotSelector.Select([]));
        Assert.Equal(ExitCode.SnapshotNotFound, ex.Code);
    }

    [Fact]
    public void 只有一个快照时无法构成区间()
    {
        SnapshotInfo[] single = [new(36820, 2, At("16:00:28"), At("17:00:34"))];
        var ex = Assert.Throws<AwrToolException>(() => SnapshotSelector.Select(single));
        Assert.Equal(ExitCode.SnapshotNotFound, ex.Code);
    }

    [Fact]
    public void RAC重复行会被去重()
    {
        // RAC 下 dba_hist_snapshot 每个实例各一行，snap_id 相同。同一实例内理论上不会重复，
        // 但防御性去重，避免区间选歪。
        SnapshotInfo[] withDupes =
        [
            new(36819, 2, At("15:00:22"), At("16:00:28")),
            new(36819, 2, At("15:00:22"), At("16:00:28")),
            new(36820, 2, At("16:00:28"), At("17:00:34")),
        ];

        var range = SnapshotSelector.Select(withDupes);
        Assert.Equal(36819, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
    }

    [Fact]
    public void begin_time_按覆盖窗口对齐()
    {
        // --begin-time 取「end_interval_time <= T 中最大的那个」
        var range = SnapshotSelector.Select(Hourly, beginTime: At("15:30:00"), endSnapId: 36820);
        Assert.Equal(36818, range.BeginSnapId);
    }

    [Fact]
    public void end_time_按覆盖窗口对齐()
    {
        // --end-time 取「end_interval_time >= T 中最小的那个」
        var range = SnapshotSelector.Select(Hourly, endTime: At("16:30:00"), beginSnapId: 36818);
        Assert.Equal(36819, range.EndSnapId);
    }

    [Fact]
    public void 互斥的区间参数同时给出时报用法错误()
    {
        var ex = Assert.Throws<AwrToolException>(
            () => SnapshotSelector.Select(Hourly, beginSnapId: 36819, last: TimeSpan.FromHours(1)));
        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains("--last", ex.Message);
    }

    [Fact]
    public void 乱序输入也能正确选择()
    {
        SnapshotInfo[] shuffled = [Hourly[2], Hourly[0], Hourly[3], Hourly[1]];
        var range = SnapshotSelector.Select(shuffled, last: TimeSpan.FromHours(2));
        Assert.Equal(36818, range.BeginSnapId);
        Assert.Equal(36820, range.EndSnapId);
    }
}

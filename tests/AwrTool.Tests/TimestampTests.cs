using AwrTool.Core;
using Xunit;

namespace AwrTool.Tests;

public class TimestampTests
{
    [Theory]
    [InlineData("2026-10-16 15:00:00", 2026, 10, 16, 15, 0, 0)]
    [InlineData("2026-10-16T15:00:00", 2026, 10, 16, 15, 0, 0)]
    [InlineData("2026-10-16 15:00", 2026, 10, 16, 15, 0, 0)]
    [InlineData("2026-10-16T15:00", 2026, 10, 16, 15, 0, 0)]
    [InlineData("2026-10-16", 2026, 10, 16, 0, 0, 0)]
    [InlineData("  2026-10-16 15:00:00  ", 2026, 10, 16, 15, 0, 0)]
    public void 解析多种写法(string input, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.True(Timestamp.TryParse(input, out var parsed));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), parsed);
    }

    [Theory]
    [InlineData("2026/10/16 15:00:00")]  // 分隔符不放宽，避免歧义
    [InlineData("15:00:00")]             // 缺日期
    [InlineData("16-10-2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void 拒绝非法写法(string? input)
    {
        Assert.False(Timestamp.TryParse(input, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Fact]
    public void Parse失败时抛用法错误()
    {
        var ex = Assert.Throws<Cli.AwrToolException>(() => Timestamp.Parse("2026/10/16"));
        Assert.Equal(Cli.ExitCode.Usage, ex.Code);
        Assert.Contains("yyyy-MM-dd", ex.Message);
    }
}

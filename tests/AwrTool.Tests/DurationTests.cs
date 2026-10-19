using AwrTool.Core;
using Xunit;

namespace AwrTool.Tests;

public class DurationTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("90m", 5400)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    [InlineData("1.5h", 5400)]
    [InlineData("0.5d", 43200)]
    [InlineData("2H", 7200)]
    [InlineData("15M", 900)]
    [InlineData(" 2h ", 7200)]
    public void 解析合法时长(string input, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Duration.Parse(input));
    }

    [Theory]
    [InlineData("2")]        // 缺单位：必须显式，不能靠猜
    [InlineData("h")]        // 缺数值
    [InlineData("-1h")]      // 负数
    [InlineData("0m")]       // 零
    [InlineData("0s")]
    [InlineData("2x")]       // 单位非法
    [InlineData("1h30m")]    // 复合写法不支持，必须报错而不是误解
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1..5h")]
    public void 拒绝非法时长(string input)
    {
        Assert.Throws<FormatException>(() => Duration.Parse(input));
    }

    [Fact]
    public void 报错消息给出正确示例()
    {
        var ex = Assert.Throws<FormatException>(() => Duration.Parse("2"));
        Assert.Contains("2h", ex.Message);
    }

    [Fact]
    public void TryParse_失败不抛异常()
    {
        Assert.False(Duration.TryParse("nope", out var v));
        Assert.Equal(TimeSpan.Zero, v);
        Assert.True(Duration.TryParse("3h", out var ok));
        Assert.Equal(TimeSpan.FromHours(3), ok);
    }
}

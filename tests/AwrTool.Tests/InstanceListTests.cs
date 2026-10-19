using AwrTool.Core;
using Xunit;

namespace AwrTool.Tests;

public class InstanceListTests
{
    [Theory]
    [InlineData("1", new[] { 1 })]
    [InlineData("1,2", new[] { 1, 2 })]
    [InlineData("1, 2", new[] { 1, 2 })]
    [InlineData("1 2", new[] { 1, 2 })]      // 空格分隔也要能吃进去
    [InlineData("1,2,3", new[] { 1, 2, 3 })]
    [InlineData(" 2 ", new[] { 2 })]
    [InlineData("2,1", new[] { 1, 2 })]      // 归一化时排序
    [InlineData("1,1,2", new[] { 1, 2 })]    // 去重
    public void 解析并归一化(string input, int[] expected)
    {
        var list = InstanceList.Parse(input);
        Assert.Equal(expected, list.Numbers);
        Assert.Equal(string.Join(",", expected), list.ToCommaSeparated());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 拒绝空值(string? input)
    {
        // 坑 3：Global 报告传 null 会触发 PLS-307 重载歧义，必须提前拦掉
        Assert.Throws<ArgumentException>(() => InstanceList.Parse(input));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,x")]
    [InlineData("0")]        // 实例号从 1 开始
    [InlineData("-1")]
    [InlineData("1,,2")]
    public void 拒绝非法实例号(string input)
    {
        Assert.Throws<FormatException>(() => InstanceList.Parse(input));
    }

    [Fact]
    public void 单个实例号也能构造()
    {
        var list = InstanceList.Single(2);
        Assert.Equal(new[] { 2 }, list.Numbers);
        Assert.Equal("2", list.ToCommaSeparated());
    }
}

using System.Text;
using AwrTool.Cli;
using Xunit;

namespace AwrTool.Tests;

/// <summary>
/// 输出编码的回归测试。乱码是"字节对但解码方式不对"造成的，
/// 所以这里把两个不变量钉住：字节必须是 UTF-8，且不能带 BOM。
/// </summary>
public class ConsoleOutputTests
{
    [Fact]
    public void 输出编码是UTF8且不带BOM()
    {
        Assert.Equal(65001, ConsoleOutput.Utf8NoBom.CodePage);
        Assert.Empty(ConsoleOutput.Utf8NoBom.GetPreamble());
    }

    [Fact]
    public void 中文往返无损()
    {
        const string text = "awrtool —— 给 AI 工具用的 Oracle AWR 报告生成器";

        var bytes = ConsoleOutput.Utf8NoBom.GetBytes(text);

        Assert.Empty(ConsoleOutput.Utf8NoBom.GetPreamble());
        Assert.Equal(text, ConsoleOutput.Utf8NoBom.GetString(bytes));

        // UTF-8 里中文是 3 字节，破折号也是 3 字节；被 GBK 误解就会变成「鈥斺€」
        Assert.Equal(Encoding.UTF8.GetBytes(text), bytes);
    }

    [Fact]
    public void 帮助文本能被UTF8编码且无替换字符()
    {
        var bytes = ConsoleOutput.Utf8NoBom.GetBytes(HelpText.Full);
        var roundTrip = ConsoleOutput.Utf8NoBom.GetString(bytes);

        Assert.Equal(HelpText.Full, roundTrip);
        Assert.DoesNotContain('\uFFFD', roundTrip);
        Assert.Contains("Oracle AWR 报告生成器", roundTrip);
    }

    [Fact]
    public void 诊断描述包含重定向状态与编码()
    {
        // 不主动改测试宿主的控制台代码页，只验证诊断本身可用
        var description = ConsoleOutput.Describe();

        Assert.Contains("输出编码=", description);
        Assert.Contains("控制台代码页=", description);
        Assert.True(
            description.Contains("已重定向", StringComparison.Ordinal)
            || description.Contains("直接输出到控制台", StringComparison.Ordinal));
    }
}

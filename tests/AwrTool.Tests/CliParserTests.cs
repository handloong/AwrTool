using AwrTool.Cli;
using AwrTool.Core;
using Xunit;

namespace AwrTool.Tests;

public class CliParserTests
{
    [Fact]
    public void 无参数时报用法错误并给出帮助()
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse([]));
        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains("awrtool", ex.Message);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void 显式请求帮助(string arg)
    {
        var options = CliParser.Parse([arg]);
        Assert.True(options.Help);
    }

    [Fact]
    public void report_上的help标志()
    {
        var options = CliParser.Parse(["report", "--help"]);
        Assert.True(options.Help);
    }

    [Fact]
    public void 默认值为_awr_html()
    {
        var options = CliParser.Parse(["report"]);
        Assert.Equal(CliCommand.Report, options.Command);
        Assert.Equal(ReportKind.Awr, options.Type);
        Assert.Equal(ReportFormat.Html, options.Format);
        Assert.Equal(600, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData("--format", "html", ReportFormat.Html)]
    [InlineData("--format", "txt", ReportFormat.Text)]
    [InlineData("--format", "TEXT", ReportFormat.Text)]
    public void 解析格式(string name, string value, ReportFormat expected)
    {
        Assert.Equal(expected, CliParser.Parse(["report", name, value]).Format);
    }

    [Fact]
    public void 支持等号写法()
    {
        var options = CliParser.Parse(["report", "--format=txt", "--type=global", "--out=a.html"]);
        Assert.Equal(ReportFormat.Text, options.Format);
        Assert.Equal(ReportKind.Global, options.Type);
        Assert.Equal("a.html", options.Out);
    }

    [Fact]
    public void 解析report的全部区间选项()
    {
        var options = CliParser.Parse([
            "report",
            "--type", "global",
            "--format", "txt",
            "--last", "2h",
            "--begin-snap", "36819",
            "--end-snap", "36820",
            "--begin-time", "2026-10-16 15:00:00",
            "--end-time", "2026-10-16T16:00",
            "--inst-num", "1,2",
            "--out", "r.txt",
            "--overwrite",
        ]);

        Assert.Equal(ReportKind.Global, options.Type);
        Assert.Equal(TimeSpan.FromHours(2), options.Last);
        Assert.Equal(36819, options.BeginSnap);
        Assert.Equal(36820, options.EndSnap);
        Assert.Equal(new DateTime(2026, 10, 16, 15, 0, 0), options.BeginTime);
        Assert.Equal(new DateTime(2026, 10, 16, 16, 0, 0), options.EndTime);
        Assert.Equal(new[] { 1, 2 }, options.Instances!.Numbers);
        Assert.Equal("r.txt", options.Out);
        Assert.True(options.Overwrite);
    }

    [Fact]
    public void 解析snapshots选项()
    {
        var options = CliParser.Parse(["snapshots", "--limit", "48", "--since", "1d", "--inst-num", "2"]);
        Assert.Equal(CliCommand.Snapshots, options.Command);
        Assert.Equal(48, options.Limit);
        Assert.Equal(TimeSpan.FromDays(1), options.Since);
        Assert.Equal(new[] { 2 }, options.Instances!.Numbers);
    }

    [Fact]
    public void 解析连接选项()
    {
        var options = CliParser.Parse([
            "doctor",
            "--connect", "User Id=a;Password=b;Data Source=c",
            "--dbid", "2295037378",
            "--timeout", "120",
            "--quiet",
        ]);

        Assert.Equal("User Id=a;Password=b;Data Source=c", options.ConnectString);
        Assert.Equal(2295037378L, options.Dbid);
        Assert.Equal(120, options.TimeoutSeconds);
        Assert.True(options.Quiet);
    }

    [Fact]
    public void 短选项别名()
    {
        var options = CliParser.Parse(["report", "-o", "x.html", "-v"]);
        Assert.Equal("x.html", options.Out);
        Assert.True(options.Verbose);
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--formats")]
    [InlineData("-z")]
    public void 未知选项报用法错误(string bad)
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["report", bad]));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("--out")]
    [InlineData("--limit")]
    public void 缺少取值报用法错误(string name)
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["report", name]));
        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void 布尔选项不接受取值()
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["report", "--overwrite=yes"]));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Fact]
    public void 多余的位置参数报用法错误()
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["report", "36819"]));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Fact]
    public void 未知子命令报用法错误()
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["frobnicate"]));
        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains("report", ex.Message);
    }

    [Fact]
    public void report_不接受snapshots专属选项()
    {
        // 静默忽略会让 AI 以为自己指定的窗口生效了
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["report", "--limit", "5"]));
        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains("snapshots", ex.Message);
    }

    [Fact]
    public void snapshots_不接受report专属选项()
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["snapshots", "--format", "html"]));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Theory]
    [InlineData("--last", "2")]
    [InlineData("--since", "abc")]
    [InlineData("--begin-snap", "abc")]
    [InlineData("--end-snap", "1.5")]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "-5")]
    [InlineData("--dbid", "x")]
    [InlineData("--limit", "0")]
    [InlineData("--begin-time", "2026/10/16")]
    [InlineData("--inst-num", "abc")]
    [InlineData("--inst-num", "0")]
    [InlineData("--type", "ash")]
    [InlineData("--format", "pdf")]
    public void 取值非法时报用法错误(string name, string value)
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["report", name, value]));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Fact]
    public void snapshots的since取值非法时报用法错误()
    {
        var ex = Assert.Throws<AwrToolException>(() => CliParser.Parse(["snapshots", "--since", "abc"]));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }
}

using AwrTool.Cli;
using AwrTool.Core;
using AwrTool.Oracle;
using Xunit;

namespace AwrTool.Tests;

/// <summary>
/// 这些测试的作用是把三个实测踩到的坑钉死在代码里。
/// 任何一个断言被改掉，都会让工具在真库上以难以诊断的方式失败。
/// </summary>
public class ReportSqlTests
{
    private static ReportRequest Request(
        ReportKind kind = ReportKind.Awr,
        ReportFormat format = ReportFormat.Text,
        string instances = "2") =>
        new(2295037378, InstanceList.Parse(instances), 36819, 36820, kind, format);

    [Fact]
    public void 单实例TXT报告()
    {
        var sql = ReportSql.Build(Request());
        Assert.Equal(
            "select output from table(dbms_workload_repository.awr_report_text(2295037378, 2, 36819, 36820, 0))",
            sql);
    }

    [Fact]
    public void 单实例HTML报告()
    {
        var sql = ReportSql.Build(Request(format: ReportFormat.Html));
        Assert.Contains("awr_report_html(2295037378, 2, 36819, 36820, 0)", sql);
    }

    [Fact]
    public void 坑1_必须用TABLE展开而不能直接取嵌套表对象()
    {
        // 用 `select dbms_...awr_report_text(...) from dual` 在裁剪后会抛
        // ORA-50071: Custom type mapping for 'AWRRPT_TEXT_TYPE_TABLE' is not specified or is invalid
        foreach (var kind in new[] { ReportKind.Awr, ReportKind.Global })
        {
            foreach (var format in new[] { ReportFormat.Text, ReportFormat.Html })
            {
                // AWR 只接受单实例，Global 才是多实例
                var instances = kind == ReportKind.Awr ? "2" : "1,2";
                var sql = ReportSql.Build(Request(kind, format, instances));

                Assert.StartsWith("select output from table(", sql);
                Assert.EndsWith("))", sql);
            }
        }
    }

    [Fact]
    public void 坑2_Global的实例列表必须是逗号分隔的带引号字面量()
    {
        var sql = ReportSql.Build(Request(ReportKind.Global, instances: "1,2"));
        Assert.Contains("awr_global_report_text(2295037378, '1,2', 36819, 36820, 0)", sql);
    }

    [Fact]
    public void 坑2_空格分隔输入会被归一化成逗号()
    {
        // 用户写 '1 2' 时，Oracle 不报错，而是在结果里返回 *** ORA-20019。
        // 我们在拼 SQL 之前就归一化，从根上避免。
        var sql = ReportSql.Build(Request(ReportKind.Global, instances: "1 2"));
        Assert.Contains("'1,2'", sql);
        Assert.DoesNotContain("'1 2'", sql);
    }

    [Fact]
    public void 坑3_实例列表永远不能是null字面量()
    {
        // awr_global_report_text(dbid, null, ...) 会触发 ORA-06553 PLS-307 重载歧义
        var sql = ReportSql.Build(Request(ReportKind.Global, instances: "1,2"));
        Assert.DoesNotContain("null", sql);

        // 空列表在构造 ReportRequest 之前就被 InstanceList 拦住了
        Assert.Throws<ArgumentException>(() => InstanceList.Parse(""));
    }

    [Fact]
    public void 单实例AWR不接受多个实例号()
    {
        var ex = Assert.Throws<AwrToolException>(() => ReportSql.Build(Request(instances: "1,2")));
        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains("global", ex.Message);
    }

    [Fact]
    public void 报告类型与格式的名字映射正确()
    {
        // Oracle 函数名后缀是 text/html，而 CLI 与文件扩展名用 txt/html
        Assert.Equal("text", ReportFormat.Text.ToOracleSuffix());
        Assert.Equal("html", ReportFormat.Html.ToOracleSuffix());
        Assert.Equal("txt", ReportFormat.Text.ToExtension());
        Assert.Equal("html", ReportFormat.Html.ToExtension());
    }

    [Fact]
    public void 报告类型解析()
    {
        Assert.Equal(ReportKind.Awr, ReportKindParser.Parse("awr"));
        Assert.Equal(ReportKind.Global, ReportKindParser.Parse("GLOBAL"));
        Assert.Equal(ReportKind.Awr, ReportKindParser.Parse(null));

        Assert.Throws<AwrToolException>(() => ReportKindParser.Parse("ash"));

        Assert.Equal(ReportFormat.Html, ReportFormatParser.Parse("html"));
        Assert.Equal(ReportFormat.Text, ReportFormatParser.Parse("TXT"));
        Assert.Equal(ReportFormat.Text, ReportFormatParser.Parse("text"));
        Assert.Equal(ReportFormat.Html, ReportFormatParser.Parse(null));
        Assert.Throws<AwrToolException>(() => ReportFormatParser.Parse("pdf"));
    }
}

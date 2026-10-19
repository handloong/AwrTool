using AwrTool.Cli;
using AwrTool.Oracle;
using Xunit;

namespace AwrTool.Tests;

/// <summary>
/// SqlGuard 是 MCP 侧的安全边界。给 AI 开放任意 SQL 执行是不负责任的，
/// 这些测试就是那条线。
/// </summary>
public class SqlGuardTests
{
    [Theory]
    [InlineData("select * from dual")]
    [InlineData("  SELECT 1 from dual  ")]
    [InlineData("select 1 from dual;")]
    [InlineData("with x as (select 1 a from dual) select a from x")]
    [InlineData("/* hint */ select count(*) from dba_hist_snapshot")]
    public void 放行只读查询(string sql)
    {
        var guarded = SqlGuard.EnsureReadOnly(sql);
        Assert.False(string.IsNullOrWhiteSpace(guarded));
        Assert.DoesNotContain(";", guarded);
    }

    [Theory]
    [InlineData("delete from t")]
    [InlineData("update t set a = 1")]
    [InlineData("insert into t values (1)")]
    [InlineData("drop table t")]
    [InlineData("alter session set x = 1")]
    [InlineData("create table t (a number)")]
    [InlineData("truncate table t")]
    [InlineData("merge into t using s on (1=1) when matched then update set a=1")]
    [InlineData("grant select on t to u")]
    [InlineData("begin null; end;")]
    [InlineData("declare x number; begin null; end;")]
    [InlineData("call dbms_stats.gather_schema_stats('S')")]
    [InlineData("explain plan for select 1 from dual")]
    public void 拒绝非只读语句(string sql)
    {
        var ex = Assert.Throws<AwrToolException>(() => SqlGuard.EnsureReadOnly(sql));
        Assert.Equal(ExitCode.Usage, ex.Code);
    }

    [Theory]
    [InlineData("select 1 from dual; delete from t")]
    [InlineData("select 1 from dual;;")]
    [InlineData("select 1; select 2")]
    public void 拒绝多语句(string sql)
    {
        Assert.Throws<AwrToolException>(() => SqlGuard.EnsureReadOnly(sql));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 拒绝空SQL(string? sql)
    {
        Assert.Throws<AwrToolException>(() => SqlGuard.EnsureReadOnly(sql!));
    }

    [Theory]
    [InlineData("EMP", "EMP")]
    [InlineData("emp", "emp")]
    [InlineData("SCOTT.EMP", "SCOTT.EMP")]
    [InlineData("DBA_HIST_SNAPSHOT", "DBA_HIST_SNAPSHOT")]
    [InlineData("MY$TAB#1", "MY$TAB#1")]
    [InlineData("  SCOTT.EMP  ", "SCOTT.EMP")]
    public void 放行合法表名(string input, string expected)
    {
        Assert.Equal(expected, SqlGuard.EnsureTableName(input));
    }

    [Theory]
    [InlineData("EMP; DROP TABLE EMP")]
    [InlineData("EMP WHERE 1=1")]
    [InlineData("\"EMP\"")]
    [InlineData("1EMP")]
    [InlineData("SCOTT.EMP.EXTRA")]
    [InlineData("SCOTT.")]
    [InlineData(".EMP")]
    [InlineData("EMP--")]
    [InlineData("EMP'")]
    public void 拒绝非法表名(string input)
    {
        Assert.Throws<AwrToolException>(() => SqlGuard.EnsureTableName(input));
    }

    [Fact]
    public void 表名大小写不敏感且不带引号()
    {
        // 引号会让 Oracle 变成大小写敏感，统一剥掉引号并按原样传
        Assert.Equal("emp", SqlGuard.EnsureTableName("emp"));
    }
}

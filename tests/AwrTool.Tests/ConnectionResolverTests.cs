using AwrTool.Cli;
using AwrTool.Oracle;
using Xunit;

namespace AwrTool.Tests;

public class ConnectionResolverTests
{
    private const string Secret = "Awr#Luxshare#2O26";
    private const string Full = "User Id=a;Password=b;Data Source=c";

    private static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
        => key => pairs.FirstOrDefault(p => p.Key == key).Value;

    private static readonly Func<string, string?> NoEnv = _ => null;

    [Fact]
    public void 完整连接串原样透传且优先级最高()
    {
        var r = ConnectionResolver.Resolve(
            Full, null, null, null,
            Env(("AWRTOOL_CONN", "User Id=env;Password=x;Data Source=y")));

        Assert.Equal(ConnectionSource.ArgumentConnectString, r.Source);
        Assert.Equal(Full, r.ConnectionString);
    }

    [Fact]
    public void 回退到AWRTOOL_CONN()
    {
        const string fromEnv = "User Id=env;Password=x;Data Source=y";
        var r = ConnectionResolver.Resolve(null, null, null, null, Env(("AWRTOOL_CONN", fromEnv)));

        Assert.Equal(ConnectionSource.EnvironmentConnectString, r.Source);
        Assert.Equal(fromEnv, r.ConnectionString);
    }

    [Fact]
    public void 空白连接串视为未提供()
    {
        var r = ConnectionResolver.Resolve(
            "   ", "192.168.1.7:1521/demo", "awr_monitor", Secret, NoEnv);

        Assert.Equal(ConnectionSource.Assembled, r.Source);
    }

    [Fact]
    public void 由三个参数拼装()
    {
        var r = ConnectionResolver.Resolve(
            null, "192.168.1.7:1521/demo", "awr_monitor", Secret, NoEnv);

        Assert.Equal(ConnectionSource.Assembled, r.Source);
        Assert.Contains("User Id=awr_monitor", r.ConnectionString);
        Assert.Contains("Data Source=192.168.1.7:1521/demo", r.ConnectionString);
        Assert.Contains("Password=" + Secret, r.ConnectionString);
        Assert.Equal("192.168.1.7:1521/demo", r.Dsn);
        Assert.Equal("awr_monitor", r.User);
    }

    [Fact]
    public void 密码里的井号原样保留()
    {
        // Oracle 连接串里 # 无特殊含义，不能转义也不能截断
        var r = ConnectionResolver.Resolve(null, "d", "u", Secret, NoEnv);
        Assert.Contains("Password=Awr#Luxshare#2O26", r.ConnectionString);
    }

    [Fact]
    public void 回退到三个环境变量()
    {
        var r = ConnectionResolver.Resolve(null, null, null, null, Env(
            ("AWRTOOL_DSN", "192.168.1.7:1521/demo"),
            ("AWRTOOL_USER", "awr_monitor"),
            ("AWRTOOL_PASSWORD", Secret)));

        Assert.Equal(ConnectionSource.Assembled, r.Source);
        Assert.Contains("User Id=awr_monitor", r.ConnectionString);
        Assert.Contains("Password=" + Secret, r.ConnectionString);
        Assert.Equal("awr_monitor", r.User);
    }

    [Fact]
    public void 参数与环境变量按字段混用()
    {
        // 常见用法：环境里放账号密码，命令行只覆盖 DSN
        var r = ConnectionResolver.Resolve(null, "10.32.44.175:1521/other", null, null, Env(
            ("AWRTOOL_USER", "awr_monitor"),
            ("AWRTOOL_PASSWORD", Secret)));

        Assert.Equal(ConnectionSource.Assembled, r.Source);
        Assert.Contains("Data Source=10.32.44.175:1521/other", r.ConnectionString);
        Assert.Contains("User Id=awr_monitor", r.ConnectionString);
    }

    [Fact]
    public void 参数优先于同名环境变量()
    {
        var r = ConnectionResolver.Resolve(null, null, "arg_user", null, Env(
            ("AWRTOOL_DSN", "env_dsn"),
            ("AWRTOOL_USER", "env_user"),
            ("AWRTOOL_PASSWORD", Secret)));

        Assert.Contains("User Id=arg_user", r.ConnectionString);
        Assert.DoesNotContain("env_user", r.ConnectionString);
        Assert.Contains("Data Source=env_dsn", r.ConnectionString);
    }

    [Theory]
    [InlineData(null, "u", null, "DSN")]
    [InlineData("d", null, null, "用户名")]
    [InlineData("d", "u", null, "密码")]
    public void 缺少字段时报用法错误并指出缺哪些(string? dsn, string? user, string? password, string expectedFragment)
    {
        var ex = Assert.Throws<AwrToolException>(
            () => ConnectionResolver.Resolve(null, dsn, user, password, NoEnv));

        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void 什么都没给时报用法错误并给出可复制的示例()
    {
        var ex = Assert.Throws<AwrToolException>(
            () => ConnectionResolver.Resolve(null, null, null, null, NoEnv));

        Assert.Equal(ExitCode.Usage, ex.Code);
        Assert.Contains("AWRTOOL_CONN", ex.Message);
        Assert.Contains("--connect", ex.Message);
    }

    [Fact]
    public void 安全字符串会打码密码()
    {
        var r = ConnectionResolver.Resolve(null, "dsn", "awr_monitor", Secret, NoEnv);

        Assert.DoesNotContain(Secret, r.ToSafeString());
        Assert.DoesNotContain("Luxshare", r.ToSafeString());
        Assert.Contains("Password=***", r.ToSafeString());
        Assert.Contains("Data Source=dsn", r.ToSafeString());
    }

    [Fact]
    public void 完整连接串的安全形式也会打码()
    {
        var r = ConnectionResolver.Resolve(
            "User Id=x;Password=topsecret;Data Source=y", null, null, null, NoEnv);

        Assert.DoesNotContain("topsecret", r.ToSafeString());
    }
}

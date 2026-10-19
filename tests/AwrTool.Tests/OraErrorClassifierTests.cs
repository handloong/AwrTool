using AwrTool.Cli;
using AwrTool.Oracle;
using Xunit;

namespace AwrTool.Tests;

public class OraErrorClassifierTests
{
    [Theory]
    // 快照区间问题
    [InlineData(20019, ExitCode.SnapshotNotFound)]
    [InlineData(20020, ExitCode.SnapshotNotFound)]
    // 权限
    [InlineData(1031, ExitCode.PrivilegeDenied)]
    [InlineData(942, ExitCode.PrivilegeDenied)]
    [InlineData(1039, ExitCode.PrivilegeDenied)]
    // 连接
    [InlineData(1017, ExitCode.ConnectFailed)]   // 用户名/口令无效
    [InlineData(12154, ExitCode.ConnectFailed)]  // 无法解析服务名
    [InlineData(12514, ExitCode.ConnectFailed)]  // 监听器不认识服务
    [InlineData(12541, ExitCode.ConnectFailed)]  // 无监听器
    [InlineData(28001, ExitCode.ConnectFailed)]  // 连接超时
    // 报告生成
    [InlineData(50071, ExitCode.ReportFailed)]
    public void 已知错误号映射到退出码(int oraNumber, ExitCode expected)
    {
        Assert.Equal(expected, OraErrorClassifier.Classify(oraNumber, ExitCode.Internal));
    }

    [Theory]
    [InlineData(12345)]
    [InlineData(1)]
    public void 未知错误号回退到调用方给的兜底码(int oraNumber)
    {
        // 同一个未知错误号，在不同阶段应落到不同的退出码上，不能瞎猜
        Assert.Equal(ExitCode.ConnectFailed, OraErrorClassifier.Classify(oraNumber, ExitCode.ConnectFailed));
        Assert.Equal(ExitCode.ReportFailed, OraErrorClassifier.Classify(oraNumber, ExitCode.ReportFailed));
    }

    [Theory]
    // 坑 2：Global 报告传空格分隔的实例列表不会抛异常，而是把 ORA-20019 当正文返回
    [InlineData("*** ORA-20019: -> Error: None of the instances specified for Database 2295037378 can be used to produce requested report.", 20019)]
    [InlineData("*** ORA-01722: 无效数字", 1722)]
    [InlineData("  *** ORA-20019", 20019)]
    public void 能识别结果正文里嵌的错误(string line, int expectedOra)
    {
        Assert.True(OraErrorClassifier.TryParseEmbeddedError(line, out var ora));
        Assert.Equal(expectedOra, ora);
    }

    [Theory]
    [InlineData("WORKLOAD REPOSITORY report for")]
    [InlineData("<html lang=\"en\"><head><title>AWR Report for DB: DEMO")]
    [InlineData("WORKLOAD REPOSITORY REPORT (RAC)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("*** ORA-abc")]
    [InlineData("note: ORA-20019 出现但不在行首")]
    public void 正常正文不会被误判为错误(string? line)
    {
        Assert.False(OraErrorClassifier.TryParseEmbeddedError(line, out var ora));
        Assert.Equal(0, ora);
    }

    [Fact]
    public void 无ORA错误号的异常回退到兜底码()
    {
        Assert.Equal(ExitCode.SnapshotNotFound,
            OraErrorClassifier.Classify(new InvalidOperationException("boom"), ExitCode.SnapshotNotFound));
    }

    [Fact]
    public void 退出码数值稳定()
    {
        // 退出码是 AI 判定失败原因的唯一权威依据，不能随手改
        Assert.Equal(0, (int)ExitCode.Ok);
        Assert.Equal(1, (int)ExitCode.Internal);
        Assert.Equal(2, (int)ExitCode.Usage);
        Assert.Equal(3, (int)ExitCode.ConnectFailed);
        Assert.Equal(4, (int)ExitCode.SnapshotNotFound);
        Assert.Equal(5, (int)ExitCode.PrivilegeDenied);
        Assert.Equal(6, (int)ExitCode.ReportFailed);
        Assert.Equal(7, (int)ExitCode.OutputFailed);
    }

    [Theory]
    [InlineData(ExitCode.Ok, "OK")]
    [InlineData(ExitCode.Internal, "INTERNAL")]
    [InlineData(ExitCode.Usage, "USAGE")]
    [InlineData(ExitCode.ConnectFailed, "CONNECT_FAILED")]
    [InlineData(ExitCode.SnapshotNotFound, "SNAPSHOT_NOT_FOUND")]
    [InlineData(ExitCode.PrivilegeDenied, "PRIVILEGE_DENIED")]
    [InlineData(ExitCode.ReportFailed, "REPORT_FAILED")]
    [InlineData(ExitCode.OutputFailed, "OUTPUT_FAILED")]
    public void JSON里的错误码是稳定的字符串(ExitCode code, string expected)
    {
        Assert.Equal(expected, ErrorCodes.ToWireName(code));
    }
}

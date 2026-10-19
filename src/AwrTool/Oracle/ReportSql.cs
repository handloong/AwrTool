using AwrTool.Cli;
using AwrTool.Core;

namespace AwrTool.Oracle;

/// <summary>一次报告生成请求。</summary>
/// <param name="Dbid">数据库 ID。</param>
/// <param name="Instances">实例号列表。单实例 AWR 只能有 1 个。</param>
/// <param name="BeginSnapId">起始快照号（<c>bid</c>）。</param>
/// <param name="EndSnapId">结束快照号（<c>eid</c>）。</param>
/// <param name="Kind">报告类型。</param>
/// <param name="Format">报告格式。</param>
public sealed record ReportRequest(
    long Dbid,
    InstanceList Instances,
    int BeginSnapId,
    int EndSnapId,
    ReportKind Kind,
    ReportFormat Format);

/// <summary>
/// 拼报告生成的 SQL。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须这样拼</b>（三条都是实测踩出来的，改错任何一条都会在真库上以难诊断的方式失败）：
/// </para>
/// <list type="number">
/// <item>
/// 必须写 <c>select output from table(...)</c>。写成
/// <c>select dbms_workload_repository.awr_report_text(...) from dual</c> 拿到的是 Oracle 自定义类型
/// <c>AWRRPT_TEXT_TYPE_TABLE</c>；在裁剪后的构建里这条路会抛
/// <c>ORA-50071: Custom type mapping for 'AWRRPT_TEXT_TYPE_TABLE' is not specified or is invalid</c>。
/// 用 <c>table()</c> 展开成普通 VARCHAR2 行之后，连 fully-trimmed 构建都能跑通。
/// </item>
/// <item>
/// Global 报告的实例列表必须是<b>逗号分隔</b>的字符串字面量。写成空格分隔（<c>'1 2'</c>）
/// 不抛异常，而是在结果里返回一行 <c>*** ORA-20019</c> 正文，很容易把错误当报告写出去。
/// </item>
/// <item>
/// 实例列表<b>不能是 null</b>，否则 <c>awr_global_report_text</c> 的 VARCHAR2 / TABLE
/// 两个重载无法消解，抛 <c>ORA-06553 PLS-307</c>。这个坑由 <see cref="InstanceList"/> 从源头堵住。
/// </item>
/// </list>
/// <para>
/// <b>为什么用字面量而不是绑定变量</b>：所有取值都已经过校验 —— 数字来自
/// <see cref="InstanceList"/>（只允许 ASCII 数字）和数据库自己的 <c>dba_hist_snapshot</c>，
/// 注入面为零。而字面量能绕开 <c>awr_global_report_text</c> 的重载消解问题，
/// 行为与实测验证过的写法完全一致。
/// </para>
/// </remarks>
public static class ReportSql
{
    /// <summary>拼出发起报告生成的 SQL。</summary>
    public static string Build(ReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var function = request.Kind switch
        {
            ReportKind.Awr => "awr_report_" + request.Format.ToOracleSuffix(),
            ReportKind.Global => "awr_global_report_" + request.Format.ToOracleSuffix(),
            _ => throw AwrToolException.Usage($"不支持的报告类型 {request.Kind}。"),
        };

        var instanceArgument = request.Kind switch
        {
            ReportKind.Awr => SingleInstance(request.Instances),
            ReportKind.Global => $"'{request.Instances.ToCommaSeparated()}'",
            _ => throw AwrToolException.Usage($"不支持的报告类型 {request.Kind}。"),
        };

        return $"select output from table(dbms_workload_repository.{function}(" +
               $"{request.Dbid}, {instanceArgument}, {request.BeginSnapId}, {request.EndSnapId}, 0))";
    }

    private static string SingleInstance(InstanceList instances)
    {
        if (instances.Numbers.Count != 1)
        {
            throw AwrToolException.Usage(
                $"--type awr 一次只能处理一个实例，收到 {instances.Numbers.Count} 个（{instances.ToCommaSeparated()}）。" +
                "要覆盖 RAC 全部实例请用 --type global。");
        }

        return instances.Numbers[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

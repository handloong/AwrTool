namespace AwrTool.Oracle;

/// <summary>数据库身份与版本信息。</summary>
public sealed record DatabaseInfo(
    long Dbid,
    string Name,
    int InstanceNumber,
    string InstanceName,
    string HostName,
    string Version);

/// <summary>报告生成的统计。</summary>
public sealed record ReportStats(int Rows, long Chars);

/// <summary>
/// 一次查询的结果。行是动态列，单元格保留原始类型，由 <c>JsonOut</c> 决定怎么落成 JSON。
/// </summary>
public sealed class QueryResult
{
    /// <summary>构造。</summary>
    public QueryResult(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows, bool truncated)
    {
        Columns = columns;
        Rows = rows;
        Truncated = truncated;
    }

    /// <summary>列名。</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>行数据。</summary>
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }

    /// <summary>是否因为行数上限而被截断。</summary>
    public bool Truncated { get; }

    /// <summary>已返回的行数。</summary>
    public int RowCount => Rows.Count;
}

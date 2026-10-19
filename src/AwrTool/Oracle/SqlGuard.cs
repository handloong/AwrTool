using System.Text;
using System.Text.RegularExpressions;
using AwrTool.Cli;

namespace AwrTool.Oracle;

/// <summary>
/// SQL 安全边界。
/// </summary>
/// <remarks>
/// <para>
/// MCP 侧把 SQL 执行能力直接暴露给 AI。给 AI 开放任意写权限是不负责任的：
/// 模型可能因为理解偏差发出 DDL/DML，而这类错误是不可逆的。所以这里只放行
/// 单条只读查询（<c>SELECT</c> / <c>WITH</c>），并且拒绝多语句拼接。
/// </para>
/// <para>
/// 这是纵深防御的第一层。第二层是数据库账号本身应当是只读的 —— 如果账号有写权限，
/// 这一层只能挡住「明显不是查询」的语句，挡不住蓄意的写操作。
/// </para>
/// </remarks>
public static partial class SqlGuard
{
    // Oracle 标识符：字母开头，允许字母数字 _ $ #，可带一个 schema 前缀
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#]*(\.[A-Za-z][A-Za-z0-9_$#]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableNamePattern();

    /// <summary>
    /// 校验 SQL 是单条只读查询，返回去掉尾部分号后的语句。
    /// </summary>
    public static string EnsureReadOnly(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw AwrToolException.Usage("SQL 不能为空。");
        }

        var body = StripLeadingComments(sql).Trim();

        // 只去掉「一个」尾部分号（用户习惯带）。剩下的任何分号都说明是多条语句，
        // 所以这里不能循环剥 —— 否则 "select 1;;" 会被当成合法语句放过。
        if (body.EndsWith(';'))
        {
            body = body[..^1].TrimEnd();
        }

        if (body.Length == 0)
        {
            throw AwrToolException.Usage("SQL 不能为空。");
        }

        if (body.Contains(';', StringComparison.Ordinal))
        {
            throw AwrToolException.Usage(
                "只接受单条 SQL，检测到多个语句。请拆成多次调用。");
        }

        var keyword = FirstKeyword(body);

        if (keyword is not ("select" or "with"))
        {
            throw AwrToolException.Usage(
                $"只接受只读查询（SELECT / WITH），检测到 '{keyword}'。" +
                "写操作与 DDL 不会通过这个工具执行 —— 需要变更数据请走正式的变更流程。");
        }

        return body;
    }

    /// <summary>校验并返回表名（可带 schema 前缀）。</summary>
    public static string EnsureTableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw AwrToolException.Usage("表名不能为空。");
        }

        var trimmed = name.Trim();

        if (!TableNamePattern().IsMatch(trimmed))
        {
            throw AwrToolException.Usage(
                $"表名 '{name}' 不合法。只接受形如 EMP 或 SCOTT.EMP 的写法，" +
                "不要带引号、空格或其它符号。");
        }

        return trimmed;
    }

    private static string StripLeadingComments(string sql)
    {
        var body = sql.TrimStart();

        while (true)
        {
            if (body.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = body.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return body;
                }

                body = body[(end + 2)..].TrimStart();
                continue;
            }

            if (body.StartsWith("--", StringComparison.Ordinal))
            {
                var newline = body.IndexOf('\n', StringComparison.Ordinal);
                if (newline < 0)
                {
                    return string.Empty;
                }

                body = body[(newline + 1)..].TrimStart();
                continue;
            }

            return body;
        }
    }

    private static string FirstKeyword(string body)
    {
        var keyword = new StringBuilder();

        foreach (var ch in body)
        {
            if (char.IsLetter(ch))
            {
                keyword.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                break;
            }
        }

        return keyword.ToString();
    }
}

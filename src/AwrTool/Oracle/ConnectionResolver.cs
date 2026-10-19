using System.Text.RegularExpressions;
using AwrTool.Cli;

namespace AwrTool.Oracle;

/// <summary>连接信息的来源。</summary>
public enum ConnectionSource
{
    /// <summary>来自 <c>--connect</c>。</summary>
    ArgumentConnectString,

    /// <summary>来自环境变量 <c>AWRTOOL_CONN</c>。</summary>
    EnvironmentConnectString,

    /// <summary>由 DSN / 用户名 / 密码拼装（命令行参数与环境变量按字段回退）。</summary>
    Assembled,
}

/// <summary>解析完成的连接信息。密码只存在于 <see cref="ConnectionString"/> 里，不单独暴露。</summary>
public sealed partial record ResolvedConnection(
    string ConnectionString,
    ConnectionSource Source,
    string? Dsn,
    string? User)
{
    [GeneratedRegex(@"(?i)\bpassword\s*=\s*[^;]*", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordField();

    /// <summary>把密码打码后的连接串，用于日志和 doctor 输出。</summary>
    public string ToSafeString() => PasswordField().Replace(ConnectionString, "Password=***");
}

/// <summary>
/// 按优先级解析连接信息。凭据只从命令行参数和环境变量来，不落配置文件、不硬编码。
/// </summary>
/// <remarks>
/// 优先级：<c>--connect</c> &gt; <c>AWRTOOL_CONN</c> &gt; 按字段拼装
/// （每个字段是「命令行参数 &gt; 同名环境变量」）。
/// 字段级回退是为了支持最常见的用法：环境里放账号密码，命令行只覆盖 DSN。
/// </remarks>
public static class ConnectionResolver
{
    /// <summary>环境变量名。</summary>
    public const string EnvConn = "AWRTOOL_CONN";

    /// <summary>环境变量名。</summary>
    public const string EnvDsn = "AWRTOOL_DSN";

    /// <summary>环境变量名。</summary>
    public const string EnvUser = "AWRTOOL_USER";

    /// <summary>环境变量名。</summary>
    public const string EnvPassword = "AWRTOOL_PASSWORD";

    /// <summary>解析连接信息。无法解析时抛 <see cref="AwrToolException"/>（退出码 2）。</summary>
    /// <param name="connectString">完整的 Oracle 连接串。</param>
    /// <param name="dsn">Data Source，如 <c>192.168.1.7:1521/demo</c>。</param>
    /// <param name="user">用户名。</param>
    /// <param name="password">密码。</param>
    /// <param name="getEnvironment">环境变量读取器，注入以便测试。</param>
    public static ResolvedConnection Resolve(
        string? connectString,
        string? dsn,
        string? user,
        string? password,
        Func<string, string?> getEnvironment)
    {
        ArgumentNullException.ThrowIfNull(getEnvironment);

        if (!string.IsNullOrWhiteSpace(connectString))
        {
            return new ResolvedConnection(connectString.Trim(), ConnectionSource.ArgumentConnectString, null, null);
        }

        var fromEnv = getEnvironment(EnvConn);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new ResolvedConnection(fromEnv.Trim(), ConnectionSource.EnvironmentConnectString, null, null);
        }

        var effectiveDsn = FirstNonBlank(dsn, getEnvironment(EnvDsn));
        var effectiveUser = FirstNonBlank(user, getEnvironment(EnvUser));
        var effectivePassword = FirstNonBlank(password, getEnvironment(EnvPassword));

        var missing = new List<string>(3);
        if (effectiveDsn is null)
        {
            missing.Add($"DSN（{EnvDsn}）");
        }

        if (effectiveUser is null)
        {
            missing.Add($"用户名（{EnvUser}）");
        }

        if (effectivePassword is null)
        {
            missing.Add($"密码（{EnvPassword}）");
        }

        if (missing.Count == 3)
        {
            throw AwrToolException.Usage(
                "未提供数据库连接信息。三种方式任选其一：\n" +
                "  1) --connect \"User Id=<用户>;Password=<密码>;Data Source=<host>:<port>/<service>\"\n" +
                $"  2) 设置环境变量 {EnvConn}\n" +
                $"  3) 分别设置 {EnvDsn} / {EnvUser} / {EnvPassword}（也可用 --dsn/--user/--password 逐项覆盖）");
        }

        if (missing.Count > 0)
        {
            throw AwrToolException.Usage($"连接信息不完整，缺少：{string.Join("、", missing)}。");
        }

        var assembled = $"User Id={effectiveUser};Password={effectivePassword};Data Source={effectiveDsn}";
        return new ResolvedConnection(assembled, ConnectionSource.Assembled, effectiveDsn, effectiveUser);
    }

    private static string? FirstNonBlank(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }
}

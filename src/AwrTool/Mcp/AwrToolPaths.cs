namespace AwrTool.Mcp;

/// <summary>需要稳定位置的路径。</summary>
public static class AwrToolPaths
{
    /// <summary>环境变量名：覆盖报告输出目录。</summary>
    public const string OutputDirEnv = "AWRTOOL_OUTPUT_DIR";

    /// <summary>
    /// MCP 场景下的默认报告目录。
    /// </summary>
    /// <remarks>
    /// MCP 服务器的当前工作目录由客户端决定，不可预测，所以报告不能默认写到 CWD，
    /// 否则调用方找不到文件。这里固定到一个用户级目录，并在返回值里给出绝对路径。
    /// </remarks>
    public static string DefaultReportDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable(OutputDirEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "awrtool", "reports");
    }
}

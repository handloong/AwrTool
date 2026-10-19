using System.Reflection;

namespace AwrTool.Cli;

/// <summary>工具标识信息。</summary>
public static class ToolInfo
{
    /// <summary>工具名。</summary>
    public const string Name = "awrtool";

    /// <summary>版本号，如 <c>0.1.0</c>。</summary>
    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>用于 JSON 信封的 <c>tool</c> 字段，如 <c>awrtool/0.1.0</c>。</summary>
    public static string FullName => $"{Name}/{Version}";
}

/// <summary>帮助与用法文本。</summary>
public static class HelpText
{
    /// <summary>完整帮助。</summary>
    public const string Full = """
        awrtool —— 给 AI 工具用的 Oracle AWR 报告生成器

        用法:
          awrtool <命令> [选项]

        命令:
          report      生成 AWR 报告并落盘（stdout 出 JSON 元数据）
          snapshots   列出可用快照区间，供选择 begin/end
          doctor      自检：配置 / 网络 / 登录 / 权限 / 能力探测
          mcp         以 MCP stdio 服务器方式运行（供 AI 客户端挂载）
          version     版本信息
          help        显示本帮助

        连接（凭据只从参数或环境变量来，不落配置文件）:
          --connect <cs>        完整连接串，优先级最高
          --dsn <d>             Data Source，如 192.168.1.7:1521/demo
          --user <u>            Oracle 用户名
          --password <p>        Oracle 密码
          --dbid <n>            数据库 ID，缺省自动探测
          --timeout <sec>       命令超时，默认 600

        环境变量（与上面逐项对应，命令行优先）:
          AWRTOOL_CONN          完整连接串
          AWRTOOL_DSN           数据源
          AWRTOOL_USER          用户名
          AWRTOOL_PASSWORD      密码

        report 选项:
          --type <awr|global>   默认 awr。global 为 RAC 全局报告
          --format <html|txt>   默认 html
          --last <dur>          相对窗口，如 30m / 2h / 1d（与 --begin-snap 互斥）
          --begin-snap <id>     起始快照号
          --end-snap <id>       结束快照号，缺省为最新
          --begin-time <ts>     起始时刻，如 "2026-10-16 15:00:00"
          --end-time <ts>       结束时刻
          --inst-num <list>     实例号。awr 单个数字；global 逗号分隔如 1,2
          -o, --out <path>      输出路径，'-' 表示 stdout
          --overwrite           允许覆盖已存在的文件

        snapshots 选项:
          --inst-num <n>        只看某个实例
          --limit <n>           最多返回条数，默认 24
          --since <dur>         回溯窗口，默认 48h

        通用选项:
          --quiet               静默，stdout 只保留 JSON
          -v, --verbose         输出调试信息到 stderr
          -h, --help            帮助

        退出码:
          0 成功   1 未预期错误   2 参数错误      3 连接失败
          4 快照无效  5 权限不足   6 报告生成失败  7 输出写入失败

        示例:
          awrtool snapshots --limit 10
          awrtool report --last 2h --format txt -o awr.txt
          awrtool report --type global --inst-num 1,2 --last 1h -o rac.html
          awrtool report --begin-snap 36819 --end-snap 36820 --inst-num 2 -o awr.html
        """;

    /// <summary>简洁用法提示，用于错误消息结尾。</summary>
    public const string Brief = $"运行 '{ToolInfo.Name} --help' 查看完整用法。";
}

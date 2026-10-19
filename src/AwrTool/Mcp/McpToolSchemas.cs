namespace AwrTool.Mcp;

/// <summary>
/// 三个工具的 JSON Schema。description 是写给模型看的，直接决定它会不会用对、用错。
/// </summary>
public static class McpToolSchemas
{
    /// <summary>执行只读查询。</summary>
    public const string Query = """
        {
          "type": "object",
          "description": "在目标 Oracle 库上执行单条只读查询并返回结果集。只接受 SELECT 或 WITH 开头的语句，写操作与 DDL 会被拒绝。",
          "properties": {
            "sql": {
              "type": "string",
              "description": "单条只读 SQL。不要用分号拼接多条语句，不要带 DDL/DML。"
            },
            "maxRows": {
              "type": "integer",
              "minimum": 1,
              "maximum": 5000,
              "default": 200,
              "description": "最多返回行数。被截断时返回的 truncated 为 true，可用 where 条件缩小范围或调大此值。"
            },
            "timeoutSeconds": {
              "type": "integer",
              "minimum": 1,
              "maximum": 600,
              "description": "语句超时秒数。省略则用服务启动时的 --timeout（默认 600）。查全表统计这类慢语句时建议显式给大一点。"
            }
          },
          "required": ["sql"],
          "additionalProperties": false
        }
        """;

    /// <summary>统计表行数。</summary>
    public const string TableRowCount = """
        {
          "type": "object",
          "description": "统计指定表的行数（COUNT(*)）。可选 where 条件做过滤计数。大表上可能较慢，此时调大 timeoutSeconds。",
          "properties": {
            "table": {
              "type": "string",
              "description": "表名，可带 schema 前缀，如 SCOTT.EMP。不要带引号或空格。"
            },
            "where": {
              "type": "string",
              "description": "可选过滤条件，不要写 WHERE 关键字本身。例如 status = 'ACTIVE' AND created_at > sysdate - 7"
            },
            "timeoutSeconds": {
              "type": "integer",
              "minimum": 1,
              "maximum": 600,
              "description": "语句超时秒数。省略则用服务启动时的 --timeout（默认 600）。"
            }
          },
          "required": ["table"],
          "additionalProperties": false
        }
        """;

    /// <summary>按时间范围拉取 AWR 报告。</summary>
    public const string AwrReport = """
        {
          "type": "object",
          "description": "生成指定时间范围内的 Oracle AWR 报告并写到本机文件，返回绝对路径、字节数、sha256 与实际覆盖的快照区间。报告内容是标准 AWR 文本或 HTML，请读取该文件后再做分析。",
          "properties": {
            "last": {
              "type": "string",
              "description": "相对时间窗口，如 30m / 2h / 1d。锚定在最新快照上，不受时区影响，推荐优先使用。与 beginSnap / beginTime 互斥。省略时取最近一个快照区间。"
            },
            "beginSnap": {
              "type": "integer",
              "description": "起始快照号。可用 oracle_query 查 dba_hist_snapshot 获得。"
            },
            "endSnap": {
              "type": "integer",
              "description": "结束快照号。省略时为最新快照。"
            },
            "beginTime": {
              "type": "string",
              "description": "起始时刻，如 2026-10-16 15:00:00。按数据库服务器时区解释，不做换算。与 last 互斥。"
            },
            "endTime": {
              "type": "string",
              "description": "结束时刻，如 2026-10-16 16:00:00。"
            },
            "type": {
              "type": "string",
              "enum": ["awr", "global"],
              "default": "awr",
              "description": "awr=单实例报告；global=RAC 全局报告（覆盖多个实例）。"
            },
            "format": {
              "type": "string",
              "enum": ["html", "txt"],
              "default": "html",
              "description": "html 更易读，txt 更省 token。"
            },
            "instNum": {
              "type": "string",
              "description": "实例号。type=awr 用单个数字如 2；type=global 用逗号分隔如 1,2。省略时 awr 取当前连接实例，global 取全部实例。"
            },
            "outPath": {
              "type": "string",
              "description": "输出文件绝对路径。省略时写到默认报告目录，并在返回值里给出绝对路径。"
            },
            "overwrite": {
              "type": "boolean",
              "default": true,
              "description": "目标文件已存在时是否覆盖。"
            }
          },
          "additionalProperties": false
        }
        """;
}

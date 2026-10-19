# AwrTool

Oracle AWR 报告生成器 + 只读取数工具，给人和 AI 用。C# / .NET 8 单文件自包含——目标机器不用装 .NET，也不用装 Oracle 客户端。

三种用法，同一份代码：

| 用法 | 场景 |
|---|---|
| **CLI** | 手工跑一条命令，出报告 |
| **MCP** | 挂到 AI 客户端，让模型自己取数、出报告 |
| **Skill** | `oracle-awr` 分析方法论，指导模型怎么读 AWR 并给出整改意见 |

---

## 安装

```bash
pwsh build/publish.ps1                  # win-x64
pwsh build/publish.ps1 -Rid linux-x64   # linux
```

产物：`src/AwrTool/bin/Release/net8.0/win-x64/publish/awrtool.exe`（约 72 MB）。

开发：

```bash
dotnet build AwrTool.sln
dotnet test AwrTool.sln
```

---

## 快速开始

```bash
# 1. 配连接（凭据只从环境变量/参数来，不写文件）
export AWRTOOL_DSN="192.168.1.7:1521/demo"
export AWRTOOL_USER="awr_monitor"
export AWRTOOL_PASSWORD='<密码>'

# 2. 自检：解析配置 → 登录 → 库身份 → 快照可见性 → 真出一份报告
awrtool doctor

# 3. 出报告
awrtool report --last 2h -o awr.html
```

PowerShell 用 `$env:AWRTOOL_DSN = "..."`，其余相同。想用一个完整连接串顶替三项，用 `AWRTOOL_CONN` 或 `--connect`。

---

## 命令

```
awrtool report     生成报告
awrtool snapshots  列出可用快照
awrtool doctor     自检
awrtool mcp        MCP stdio 服务器
awrtool version | help
```

常用例子：

```bash
awrtool report --last 2h --format txt -o awr.txt          # 文本，体积 1/3
awrtool report --begin-snap 36882 --end-snap 36883 -o a.html
awrtool report --type global --inst-num 1,2 --last 1h -o rac.html   # RAC 全局
awrtool snapshots --limit 10
```

`report` 主要选项：`--type awr|global`、`--format html|txt`、`--last 30m|2h|1d`、`--begin-snap/--end-snap`、`--begin-time/--end-time`、`--inst-num`、`-o/--out`（`-` 表示 stdout）、`--overwrite`。

**输出契约**：stdout 只有一个 JSON 对象（`ok` / `exitCode` / 元数据），报告本体落文件。退出码 `0` 成功、`3` 连接失败、`4` 快照区间无效、`5` 权限不足、`6` 生成失败、`7` 写入失败——AI 靠退出码判失败原因，不用读日志。

---

## MCP

```json
{
  "mcpServers": {
    "awrtool": {
      "command": "C:\\tools\\awrtool.exe",
      "args": ["mcp"],
      "env": {
        "AWRTOOL_DSN": "192.168.1.7:1521/demo",
        "AWRTOOL_USER": "awr_monitor",
        "AWRTOOL_PASSWORD": "<密码>",
        "AWRTOOL_OUTPUT_DIR": "D:\\awr-reports"
      }
    }
  }
}
```

三个工具：`oracle_query`（单条只读 SELECT）、`oracle_table_rowcount`、`awr_report`。

`oracle_query` 只收单条 `SELECT`/`WITH`，写操作、DDL、匿名块、多语句一律拒绝。这是第一层防线——**上线前请给这个工具单独开一个只读账号**。

`AWRTOOL_OUTPUT_DIR` 可选，不设时写 `%LOCALAPPDATA%\awrtool\reports`（Linux `~/.local/share/awrtool/reports`）。

---

## Skill：`oracle-awr`

`skills/` 下是一套 AWR 分析方法论，不含任何特定库的数据。它是"怎么读报告、怎么下结论"的操作手册，配合 awrtool 的 `awr_report` / `oracle_query` 使用。

**装到** `~/.workbuddy/skills/oracle-awr/`（把 `skills/` 整个目录拷过去，`SKILL.md` 要在根层）：

```bash
cp -r skills ~/.workbuddy/skills/oracle-awr
```

**它约束什么**

- 交付必须三层：结论（一句话定性）+ 证据（章节/数字或可复现 SQL）+ 意见（P0–P3，含预期收益、验证 SQL、回退方式）。没有数字支撑的"可能存在索引问题"不允许出现。
- RAC 必须看两份报告：单实例 + 全局。只看单实例可能正好拿到最闲的节点。
- 窗口用快照号精确指定，不用 `--last`，否则不可复现。
- 涉及 SQL/索引时三件套齐全：AWR 的 `gets/exec`、执行计划 `access_predicates`、实测段大小。
- `dba_tables.num_rows` 不是真实行数，关键表一律 `count(*)` 复核。

**目录**

```
skills/
├── SKILL.md                 工作流：环境探测 → 五类瓶颈分类 → 下钻 → 出意见
├── references/              诊断模式、查询手册、建议模板、MCP 取数纪律、基线模板
└── scripts/awr_extract.py   AWR 报告按章节抽取
```

**抽取脚本**（AWR 有 90% 信息在表格里，别整份读）：

```bash
python awr_extract.py awr.txt --list                     # 章节清单
python awr_extract.py awr.txt --section "Top 10 Foreground"
python awr_extract.py awr.txt --grep "WF_AUTOFAI"        # 按表名/SQL_ID 搜
python awr_extract.py awr.txt --json awr.json
```

自适应 txt / html，输出统一为 `########## 章节名` + 该节内容。

---

## 已知限制

- **ASH 报告未实现**：`ash_report_text` 的 DATE 参数实测报 `ORA-01843`，绑定方式待排查。
- **AWR Diff 未实现**：函数可用（722 KB / 15.5 秒），暂无需求。
- **只读**：不建快照、不改保留策略。
- 中文在 cmd 里若乱码，是控制台代码页问题，`chcp 65001` 或重定向到文件即可；`awrtool doctor` 第一项会打出实际生效的编码。

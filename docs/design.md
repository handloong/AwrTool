# AwrTool 设计文档

> 面向 AI Agent 的 Oracle AWR 报告生成 CLI。C# / .NET 8。
>
> 本文档中标注「实测」的结论均来自对目标库 `192.168.1.7:1521/demo` 的真实调用，
> 不是推测。复现命令见 `docs/plan.md` 的验证步骤。

---

## 1. 目标与非目标

**目标**

- 一条命令把一个时间区间的 AWR 报告落成本地文件（HTML / TXT），供 AI 工具直接读取分析。
- 输出契约机器可读：stdout 只吐 JSON，报告本体写文件，退出码可判定失败原因。
- 单文件自包含发布，目标机器不需要装 .NET 运行时。

**非目标**

- 不做 AWR 分析（分析由 `awr-analysis` 技能负责）。
- 不做历史快照保留策略管理、不创建快照。
- 不做交互式提示。所有参数必须能一次性给全，缺参数就报错退出，绝不阻塞等输入。

---

## 2. 目标环境事实（实测）

| 项 | 值 |
|---|---|
| 数据库版本 | Oracle **19.31.0.0.0** |
| 架构 | **2 节点 RAC**，instance 1 / 2，host `ks9p-baizedb02` |
| DBID | `2295037378` |
| DB Name / Unique Name | `DEMO` / `demo` |
| 服务名 | `demo`（SCAN/单实例均走这个 Service Name） |
| 快照节奏 | **每小时一个**；48 小时内每实例各 47 个 |
| 账号权限 | `awr_monitor` 可读 `dba_hist_snapshot`，可执行 AWR / Global / Diff 报告函数 |

**报告规模与耗时（实测，快照 36819→36820 一小时窗口，instance 2）**

| 报告 | 行数 | 字符数 | 耗时 |
|---|---|---|---|
| `awr_report_text` | 8,384 | 588 KB | 2.3 s |
| `awr_report_html` | 8,571 | 1.63 MB | 5.1 s |
| `awr_global_report_text`（inst 1,2） | — | — | ~0.2 s |
| `awr_diff_report_text` | 4,246 | 722 KB | 15.5 s |

结论：报告是「一行一条记录」的形式返回的（TXT 约 70 字符/行，HTML 约 190 字符/行），
所以**逐行流式写文件**即可，不需要把整个 CLOB 攒在内存里，也不需要处理 LOB 定位。

---

## 3. CLI 契约

```
awrtool <command> [options]

命令
  report        生成 AWR 报告并落盘
  snapshots     列出可用快照区间（供 AI 挑选 begin/end）
  doctor        自检：配置 / 网络 / 登录 / 权限 / 能力探测
  version       版本与构建信息
  help          帮助

全局选项
  --connect <cs>        完整连接串，优先级最高
  --dsn <d>             Data Source，如 192.168.1.7:1521/demo
  --user <u>            Oracle 用户名
  --password <p>        Oracle 密码
  --dbid <n>            默认自动探测
  --timeout <sec>       命令超时，默认 600
  --quiet               静默，仅保留 stdout 的 JSON
  -h, --help            帮助

report 选项
  --type <awr|global>   默认 awr
                         awr    = 单实例 AWR（awr_report_*）
                         global = RAC 全局报告（awr_global_report_*）
  --format <html|txt>   默认 html
  --last <dur>          相对窗口，如 30m / 2h / 1d。与 --begin-snap 互斥
  --begin-snap <id>     起始快照号
  --end-snap <id>       结束快照号，默认最新
  --begin-time <ts>     按时刻定位（取覆盖该时刻的快照）
  --end-time <ts>
  --inst-num <list>     实例号。awr 单个数字；global 逗号分隔如 1,2。
                        省略时：awr 用当前连接实例，global 用全部实例
  -o, --out <path>      输出路径。'-' 表示写 stdout。
                        省略时自动命名 awr_<type>_<inst>_<bid>_<eid>.<ext>
  --overwrite           允许覆盖已存在的输出文件

snapshots 选项
  --inst-num <n>        过滤实例，省略则列全部实例
  --limit <n>           最多返回条数，默认 24
  --since <dur>         回溯窗口，默认 48h
```

**参数风格约定**

- `--opt value` 与 `--opt=value` 都支持。
- 短选项仅 `-o`、`-h`、`-v`。
- 未知选项、缺少取值、互斥选项同时出现 → 退出码 2，并输出用法提示。
- 不读任何交互输入。

---

## 4. 退出码

| 码 | 常量 | 含义 |
|---|---|---|
| 0 | `OK` | 成功 |
| 1 | `INTERNAL` | 未预期错误（含未分类异常） |
| 2 | `USAGE` | 参数/用法错误 |
| 3 | `CONNECT_FAILED` | 网络不可达、登录失败、服务名错误 |
| 4 | `SNAPSHOT_NOT_FOUND` | 快照区间无效或不存在（含 `ORA-20019`） |
| 5 | `PRIVILEGE_DENIED` | 权限不足（`ORA-01031` / `ORA-00942`） |
| 6 | `REPORT_FAILED` | 报告生成失败（含 `*** ORA-` 前缀探测命中） |
| 7 | `OUTPUT_FAILED` | 输出文件写入失败 |

退出码是 AI 判定失败原因的**唯一权威依据**；JSON 里的 `error.code` 与之对应。

---

## 5. JSON 信封（stdout）

成功：

```json
{
  "ok": true,
  "tool": "awrtool/0.1.0",
  "command": "report",
  "db": { "name": "DEMO", "dbid": 2295037378, "instance": 2, "version": "19.31.0.0.0" },
  "report": {
    "type": "awr",
    "format": "html",
    "instances": [2],
    "beginSnap": 36819,
    "endSnap": 36820,
    "beginTime": "2026-10-16T15:00:22",
    "endTime": "2026-10-16T16:00:28"
  },
  "output": { "path": "D:\\out\\awr_awr_2_36819_36820.html", "bytes": 1633396, "sha256": "..." },
  "elapsedMs": 5085
}
```

失败：

```json
{
  "ok": false,
  "tool": "awrtool/0.1.0",
  "command": "report",
  "error": { "code": "SNAPSHOT_NOT_FOUND", "message": "快照 99999 不存在", "ora": 20019 },
  "elapsedMs": 213
}
```

约束：

- 序列化**只用源生成** `JsonSerializerContext`，禁止反射序列化（AOT/裁剪要求）。
- `output.path` 一律给绝对路径。
- `-o -` 时报告写 stdout，此时信封改写到 stderr，保证管道里拿到的只有报告正文。
- `--quiet` 时 stdout 只保留信封（去掉人类可读日志）。

---

## 6. 凭据解析

优先级从高到低：

1. `--connect <完整连接串>`
2. `AWRTOOL_CONN`
3. 由 `--dsn` / `--user` / `--password` 拼装
4. 由 `AWRTOOL_DSN` / `AWRTOOL_USER` / `AWRTOOL_PASSWORD` 拼装

决策依据（用户选择）：**全靠环境变量，不落配置文件、不硬编码进二进制**。

- 密码不作为命令行参数透露给进程列表；优先走 `AWRTOOL_PASSWORD`。
- 密码可能含 `#`（如 `Awr#Luxshare#2O26`），拼装连接串时不做转义处理，直接原样嵌入
  `Password=...` 段，因为 Oracle 连接串中 `#` 无特殊含义。
- 全部来源都缺失时，报 `USAGE`，消息里给出可直接复制的环境变量设置示例。

---

## 7. 快照选择规则

**RAC 陷阱**：`dba_hist_snapshot` 在 RAC 下**每个实例各有一行**（实测 snap `36820` 出现两次）。
所有查询必须按 `instance_number` 过滤，否则区间会重复。

选择输入互斥，只允许一种：

| 输入 | 语义 |
|---|---|
| 无 | 最近两个快照（即最近一小时） |
| `--last 2h` | 锚定最新快照的 `end_interval_time` 为 `E`，目标起点 `B = E - 2h`；开始快照取 `end_interval_time <= B` 中最大的那个 |
| `--begin-snap` / `--end-snap` | 直接使用；`--end-snap` 省略则取最新 |
| `--begin-time` / `--end-time` | 取 `begin_interval_time <= ts < end_interval_time` 覆盖该时刻的快照 |

校验：

- `begin >= end` → `USAGE`
- 快照号在该实例下不存在 → `SNAPSHOT_NOT_FOUND`
- `--inst-num` 给了库里没有的实例号 → `SNAPSHOT_NOT_FOUND`

---

## 8. Oracle 取数实现要点

**统一取数形态**（必须照抄，见下方坑 1）：

```sql
-- 单实例
select output from table(
  dbms_workload_repository.awr_report_text(:dbid, :inst, :bid, :eid, 0))

-- RAC 全局。注意：实例列表必须是「逗号分隔」的字符串
select output from table(
  dbms_workload_repository.awr_global_report_text(:dbid, :insts, :bid, :eid, 0))
```

函数签名（实测自 `all_arguments`）：

```
AWR_REPORT_TEXT|HTML        (L_DBID, L_INST_NUM, L_BID, L_EID, L_OPTIONS)
AWR_GLOBAL_REPORT_TEXT|HTML (L_DBID, L_INST_NUM, L_BID, L_EID, L_OPTIONS)   -- L_INST_NUM 有 VARCHAR2 和 TABLE 两个重载
AWR_DIFF_REPORT_TEXT|HTML   (DBID1, INST_NUM1, BID1, EID1, DBID2, INST_NUM2, BID2, EID2, P_OPTIONS)
ASH_REPORT_TEXT             (L_DBID, L_INST_NUM, L_BTIME, L_ETIME, ... 16 个参数，用时间窗而非快照号)
```

### 三个必须绕开的坑（实测）

**坑 1 —— 不能用 `ExecuteScalar` 取嵌套表对象。**
`select dbms_workload_repository.awr_report_text(...) from dual` 返回的是一个 Oracle 自定义类型
`AWRRPT_TEXT_TYPE_TABLE`。在**裁剪后**的构建里这条路径会抛：

```
ORA-50071: Custom type mapping for 'dataSource=... typeName='AWRRPT_TEXT_TYPE_TABLE'' is not specified or is invalid
```

改用 `select output from table(...)` 后，返回值被展开成普通 `VARCHAR2` 行，fully-trimmed 下也正常。
**这一条直接决定了裁剪/AOT 可行性，不能改。**

**坑 2 —— Global 的实例列表分隔符。**
`'1,2'` 正常；写成 `'1 2'`（空格）**不抛异常**，而是在结果里返回一行正文：

```
*** ORA-20019: -> Error: None of the instances specified for Database 2295037378 can be used to produce requested report.
```

所以实现必须：把 `--inst-num` 归一化成逗号分隔；并且**扫描结果首行是否匹配 `^\*\*\* ORA-\d+`**，
命中即判 `REPORT_FAILED`，不能让这种「带错误正文的成功查询」混过去。

**坑 3 —— 传 `null` 作实例列表会重载歧义。**
`awr_global_report_text(dbid, null, bid, eid, 0)` → `ORA-06553: PLS-307: 有太多的 'AWR_GLOBAL_REPORT_TEXT' 声明与此次调用相匹配`。
实现必须始终传非空字符串，不能传 `null`。

**坑 4 —— 快照窗口过滤必须用 `end_interval_time`。**
这是集成 MCP 时抓到的真 bug。`SnapshotSelector` 是按 `end_interval_time` 对齐的，
而一个快照的 `begin_interval_time` 比它的 `end_interval_time` 早整整一个快照间隔。
用 `begin_interval_time > sysdate - N` 过滤时，只要窗口短于一个快照间隔，
最近那个快照的 `begin` 就会落在窗口之外被漏掉，表现为
「可用快照不足以构成一个区间」——`--last 30m` 在每小时快照的库上必现。
改成 `end_interval_time > sysdate - N` 后与选择器语义一致。
另外 `--last` 的回溯窗口留了 24 小时余量，因为快照间隔是实例相关配置，取太紧会漏起点。

### 报告写入策略

- 先写临时文件（同目录 `.tmp` 后缀），全部读完后 `File.Move` 覆盖到目标路径。
- 失败（异常或命中 `*** ORA-`）时删除临时文件，**不留下半成品**。
- 逐行 `WriteLine`，行分隔符统一用 `\n`。
- 写完统计字节数与 SHA-256，放进信封供 AI 校验。

---

## 9. 发布形态（已放弃 NativeAOT）

**结论：不做 NativeAOT，用单文件 + 自包含。**

**事实**

- `Oracle.ManagedDataAccess.Core` 上游不是 AOT 安全的。发布时 ILLink 会报
  `IL2104: Assembly 'Oracle.ManagedDataAccess' produced trim warnings`（伴随 `IL2105` / `IL2026`）。
- 但**实测 fully-trimmed 构建下本工具用到的全部路径均可用**：连接、`DISTINCT` 快照查询、
  `TABLE()` 展开的 TXT / HTML 报告流式读取全部成功。所以裁剪本身不是死路。
- 另一条否决理由是收益：本工具单次调用的真实耗时是**连库 + 生成报告 2～16 秒**，
  AOT 省下的约 90 ms 启动时间没有意义。

**实测对比（同一份代码，win-x64，各 6 次取样中位数）**

| 形态 | 体积 | 启动 |
|---|---|---|
| 单文件 + 自包含 + ReadyToRun | 93 MB | 142 ms |
| 单文件 + 自包含（**采用**） | 72 MB | 130 ms |

ReadyToRun 在单文件形态下是净亏：启动开销主要在程序集加载而不是 JIT，R2R 反而把镜像撑大 21 MB。
所以最终配置里刻意不写 `PublishReadyToRun`。

---

## 10. 目标环境事实（实测）

| 项 | 值 |
|---|---|
| 数据库版本 | Oracle **19.31.0.0.0**（`v$instance.version` 只给 `19.0.0.0.0`，要取 `version_full`） |
| 架构 | **2 节点 RAC**，instance 1 / 2，host `ks9p-baizedb01` / `ks9p-baizedb02` |
| DBID | `2295037378` |
| DB Name / Unique Name | `DEMO` / `demo` |
| 快照节奏 | 每小时一个 |
| 报告规模 | TXT ≈ 548 KB；单实例 HTML ≈ 1.6 MB；RAC 全局 HTML ≈ 3.1 MB |

---

## 11. MCP 服务

`awrtool mcp` 以 MCP stdio 传输提供服务。手写 JSON-RPC 2.0，不引 `ModelContextProtocol` SDK
（用到的方法只有四个，自写能把 stdout 纯净性控制死，也不引入反射式序列化依赖）。

**协议版本**：协商时回声客户端请求的版本（已知 `2026-07-28` / `2025-06-18` / `2025-03-26` /
`2024-11-05`），陌生版本回落到 `2025-06-18`。

**stdout 纯净性是硬约束**：任何非 JSON-RPC 输出都会让客户端解析失败，所以日志全部走 stderr。

**三个工具**

| 工具 | 说明 |
|---|---|
| `oracle_query` | 单条只读查询，返回列名 + 行对象 + `truncated` |
| `oracle_table_rowcount` | `COUNT(*)`，可选 `where` |
| `awr_report` | 按时间范围生成报告并落盘，**复用 `ReportCommand`**，不维护第二套逻辑 |

**错误分两类**（按 MCP 规范）：参数不合法 / 未知工具 → JSON-RPC 协议错误 `-32602`；
执行期失败（ORA 错误等）→ `result.isError = true`，正文是 `{"ok":false,"error":{...}}`。

**连接策略**：启动时解析一次连接信息，每次工具调用按需建连并开启池化。配置有问题时服务
照常启动，只是每次调用返回明确错误 —— 这样 `tools/list` 仍可用，模型能把问题报出来，
比进程直接退出好排查。

**报告落盘位置**：MCP 服务器的当前工作目录由客户端决定、不可预测，所以报告不默认写 CWD，
而是 `AWRTOOL_OUTPUT_DIR` 或 `%LOCALAPPDATA%\awrtool\reports`，并在返回值里给绝对路径。

---

## 12. 安全边界

`oracle_query` 直接把 SQL 执行能力暴露给模型。`SqlGuard` 是第一层防线：
只放行单条 `SELECT` / `WITH`，拒绝 DDL / DML / 匿名块 / 多语句；表名走白名单正则。

**这只是第一层。** 生产部署应当给这个工具单独建一个只读账号 —— 如果账号本身有写权限，
`SqlGuard` 只能挡住「明显不是查询」的语句，挡不住蓄意的写操作。

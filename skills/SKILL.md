---
name: oracle-awr-ict
description: 通用 Oracle AWR 性能诊断与优化建议流程,需配合https://github.com/handloong/AwrTool#mcp使用,生成指定时间窗口的实例级与 RAC 全局 AWR 报告，按"取证式"方法把每条结论绑定到具体查询与数字，先判五类瓶颈（CPU/IO/争用/内存/Redo），再下钻到 Top SQL、执行计划、段与 ASH，最后输出带优先级、预期收益、验证 SQL 与回退方案的整改意见。适用于任意 Oracle 11g~23ai 实例（含 RAC、CDB）。当用户提到 AWR、awr report、statspack、Oracle 性能慢、Top SQL、等待事件、全表扫描、索引、硬解析、绑定变量、RAC 负载不均，或要求"拉一份最近 N 小时的 AWR / 分析这份 AWR / 给优化建议"时使用。
agent_created: true
---

# Oracle AWR 分析（通用）

## Overview

把 AWR 从"一堆数字"变成四件事：**哪条 SQL、扫了哪张表、为什么、怎么改并验证**。

交付物是三层，缺任一层都算没做完：

1. **结论**：一句话说清这次窗口是 CPU 型 / IO 型 / 争用型，占比最高的前三项是什么
2. **证据**：每条约结论对应报告章节 + 数字，或一条可复现的 SQL + 结果
3. **意见**：按 P0–P3 排序的整改动作，每条含预期收益、验证方法、回退方式

不允许出现"可能存在索引问题"这类没有数字支撑的判断。

**本 skill 不含任何特定数据库的数据**。环境拓扑、基线数字、已整改项一律在首次分析时探测并落到
工作区（`<ws>/awr/baseline-<DBNAME>.md`），skill 自身只装方法。

---

## 一、首次使用：环境探测（新库必做）

不要假设环境。新接一个库时先跑这套探测，结果写进工作区基线文件，后续分析直接读它。

```sql
-- ① 实例与拓扑（RAC / CDB 决定要不要出全局报告）
SELECT i.instance_number, i.instance_name, i.host_name, i.version, i.status,
       (SELECT value FROM v$parameter WHERE name = 'cluster_database') rac,
       (SELECT cdb FROM v$database) cdb,
       (SELECT name FROM v$database) db_name
FROM gv$instance i ORDER BY 1;

-- ② 快照配置（决定窗口能不能精确切）
SELECT snap_interval, retention FROM dba_hist_wr_control;

-- ③ 最近快照（定窗口用）
SELECT snap_id, TO_CHAR(begin_interval_time,'YYYY-MM-DD HH24:MI:SS') beg,
       TO_CHAR(end_interval_time,'YYYY-MM-DD HH24:MI:SS') endt
FROM dba_hist_snapshot WHERE instance_number = :inst
ORDER BY snap_id DESC FETCH FIRST 12 ROWS ONLY;

-- ④ 主机规格（判断 AAS 是否饱和）
SELECT stat_name, value FROM v$osstat
WHERE stat_name IN ('NUM_CPUS','NUM_CPU_CORES','PHYSICAL_MEMORY_BYTES','LOAD');

-- ⑤ 大头 schema（定位业务 schema，避免每次问）
SELECT owner, ROUND(SUM(bytes)/1024/1024/1024, 1) gb
FROM dba_segments GROUP BY owner ORDER BY 2 DESC FETCH FIRST 10 ROWS ONLY;
```

把结果按 `references/environment-baseline-template.md` 的格式写进
`<ws>/awr/baseline-<DBNAME>.md`，并在下次分析前先读它——尤其是「已实施的整改」一节，
否则会把改造后的效果算进改造前的窗口。

---

## 二、铁律

1. **RAC 必须出两份报告**：单实例（该节点自身行为）+ 全局（真实负载）。
   只看单实例报告，很可能正好拿到最闲的节点，得出"库很闲"的错误结论。
2. **窗口精确指定**：`beginSnap = 最新快照号 - N`，`endSnap = 最新快照号`。
   快照语义：`begin_snap = N` 表示从「N 的 end_interval_time」开始。用相对时间（如 `last:2h`）
   会按快照边界取整，窗口不可复现。
3. **结论必须三件套**（涉及 SQL/索引时缺一不可）：AWR 的 `gets/exec`、执行计划的
   `access_predicates`、段/表的实测规模（`dba_segments.blocks` 或 `count(*)`）。
4. **`dba_tables.num_rows` 不是真实行数**。长期未收集统计的库偏差可达 20% 以上，
   关键表一律 `count(*)` 复核；做不到就在结论里标注"统计值 + 统计时间 + 可能偏差"。
5. **对齐时间线**：`dba_indexes.last_analyzed` 与报告窗口比对，别把改造后的效果算进改造前的窗口。
6. **意见必须可执行**：每条建议写清「动作 / 预期收益 / 验证 SQL / 回退方式」。只说"建议优化 SQL"等于没说。
7. **MCP 只读**：若通过 awrtool 之类 MCP 取数，`oracle_query` 只接受单条 `SELECT`/`WITH`，
   DDL/DML/`EXEC`/`ALTER SESSION` 一律被拒。所有变更写成可粘贴脚本交给客户端执行。

---

## 三、取数通道

| 通道 | 适用 | 报告生成 | SQL 执行 |
|---|---|---|---|
| `awrtool` MCP（如有） | 有该连接器的环境 | `awr_report` 出 txt/html | `oracle_query`（只读） |
| SQLcl / SQL*Plus | 通用兜底 | `@?/rdbms/admin/awrrpt.sql`、`awrgrpt.sql` | 任意 SQL/DDL |
| 其他只读通道 | 只有查询权限时 | 让 DBA 出报告，自己只查 `dba_hist_*` | 只读 SQL |

工具契约、取数纪律（控制返回体积）、DDL 交接方式见 `references/mcp-execution.md`。

---

## 四、工作流

### Step 0 — 定窗口

查 `dba_hist_snapshot`，取 `snap_id` 与 `begin/end_interval_time`。
「最近 N 小时」= `最新快照号 - N → 最新快照号`。
相邻两次分析通常重叠一段，对比时要说明，或改用 ADDM 的逐小时 AAS。

### Step 1 — 生成报告

**分析用 `format: "txt"`**（体积约为 HTML 的 1/3，段落边界是 `~~~`，抽取零解析坑）；
需要交付给人看时再额外生成 HTML。

```python
# 分析用
{"beginSnap": N-2, "endSnap": N, "format": "txt", "type": "awr",   "outPath": "<ws>/awr/awr_<HHMM>_<HHMM>_inst<M>.txt"}
{"beginSnap": N-2, "endSnap": N, "format": "txt", "type": "global","instNum": "1,2", "outPath": "<ws>/awr/awr_<HHMM>_<HHMM>_global.txt"}

# 交付用（仅当用户要看 / 要存档）
{"beginSnap": N-2, "endSnap": N, "format": "html", "type": "global", "instNum": "1,2", "outPath": "<ws>/awr/awr_<HHMM>_<HHMM>_global.html"}
```

核对返回值：`elapsedSeconds` 与预期窗口一致（2 小时 ≈ 7,170 秒）、`beginTime/endTime` 对得上。

**格式选择依据（同一窗口实测）**

| 项 | txt | html |
|---|---|---|
| 体积 | ~0.4–0.6 MB | ~1.4–1.7 MB |
| 章节边界 | `标题 + ~~~` 或 `SQL ordered by` | `<hN>` + `<p />标题<p />` 两套规则 |
| 抽取可靠性 | 高（行式） | 中（模板差异多） |
| 交付阅读 | 差 | 好 |

结论：**分析读 txt，交付给 html**。

### Step 2 — 结构化抽取（不要整份读）

```bash
python <skill>/scripts/awr_extract.py <report.txt> --list                  # 章节清单
python <skill>/scripts/awr_extract.py <report.txt> --dump awr.txt          # 全量落盘
python <skill>/scripts/awr_extract.py <report.txt> --section "Load Profile"
python <skill>/scripts/awr_extract.py <report.txt> --section "Top 10 Foreground"
python <skill>/scripts/awr_extract.py <report.txt> --section "SQL ordered by Elapsed" --max-rows 40
python <skill>/scripts/awr_extract.py <report.txt> --grep "<表名或SQL_ID>"
python <skill>/scripts/awr_extract.py <report.txt> --json awr.json
```

脚本自适应 txt / html，输出统一为 `########## 章节名` + 该节内容。
全局报告必抽：`Database Summary` / `Database Instances Included In Report` / `Time Model` /
`Top Timed Events` / `SQL ordered by Elapsed Time (Global)`。

### Step 3 — 先分类，再看细节

按下面「五类瓶颈」对号入座，**一次只可能有一到两类主因**。分类错，后面全错。

**10 分钟体检清单（按顺序看，逐条否掉）**

| # | 看什么 | 出处 | 判据 |
|---|---|---|---|
| 1 | DB CPU / DB Time | `Time Model` | >80% → CPU 型；否则看等待 |
| 2 | AAS vs CPU 数 | `Database Instances Included` | AAS 接近 CPU 数 → 饱和；远低 → 效率问题 |
| 3 | Top 1 SQL 占 DB Time | `SQL ordered by Elapsed` | >5% 必查；>10% 是 P0 |
| 4 | Top 1 等待类占比 | `Foreground Wait Classes` | >20% 属等待型 |
| 5 | 硬解析耗时占比 | `Time Model` 的 Hard Parse Ela | >5% 查绑定变量 |
| 6 | 物理读/秒 | `Load Profile` 或 sysstat | 突增 2 倍先查备份/大扫 |
| 7 | 两实例 AAS 比（RAC） | 全局报告实例表 | 相差 10 倍 → 负载倾斜 |
| 8 | 窗口内有无 DDL | `dba_indexes.last_analyzed` | 有则只能当"改前"或"改后"，不能混谈 |

八条走完，问题基本已经定位到 1~2 条 SQL 或 1 个配置问题。不要在这之前下结论。

### Step 4 — 取证据

按 `references/query-cookbook.md` 逐项取数：逐实例 DB time/CPU → Top SQL → 段级逻辑读 →
嫌疑 SQL 的执行计划 → 索引/统计/真实行数 → 现场 `gv$sql`。

### Step 5 — 下钻（按需）

| 现象 | 下钻对象 |
|---|---|
| 单条 SQL 占 DB Time > 5% | `DBMS_XPLAN.DISPLAY_AWR` + `dba_hist_sql_plan` |
| 某张表逻辑读畸高 | `dba_hist_seg_stat` + `dba_segments` |
| 高峰/尖刺在 1 小时窗口里被稀释 | ASH：`dba_hist_active_sess_history` 按分钟/会话下钻 |
| 有阻塞、锁 | ASH `blocking_session` 链 |
| 改动前后要对比 | 两窗口 `dba_hist_sqlstat` delta（cookbook §10） |

### Step 6 — 出意见

按 `references/recommendation-playbook.md` 的模板写。每条建议必须包含：

```
问题（数字）→ 动作（DDL/SQL/参数/排期）→ 预期收益（可量化）→ 验证 SQL → 回退方式
```

---

## 五、指标阈值表

### Load Profile

| 指标 | 关注阈值 | 说明 |
|---|---|---|
| DB Time(s) / DB CPU(s) | AAS 接近或超过 CPU 数 = 饱和 | 远低但仍慢 = 效率问题 |
| Redo size / sec | > 10 MB/s 视为写密集 | 结合 `log file sync` 看 |
| Logical reads / sec | 结合 DB CPU 看效率 | 高但命中率高 → 全扫打出来的 |
| Hard parses / sec | > 100/s 基本等于缺绑定变量 | 20~50/s 也要看硬解析耗时占比 |
| Executes / sec | 高频小 SQL 按 per-exec 看 | 看 `gets/exec` 而非总量 |
| User calls / sec | 高但 DB Time 低 = 空转 | 与事务数一起看 |

### Instance Efficiency Percentages

| 指标 | 目标 | 告警 | 说明 |
|---|---|---|---|
| Buffer Hit % | > 95% | < 90% | 99% 也可能有问题，见下 |
| Library Hit % | > 99% | < 95% | <95% 查共享池与硬解析 |
| Execute to Parse % | > 50% | 很低 | 低 = 每次执行都伴随解析 |
| Soft Parse % | > 95% | < 90% | 低 = 硬解析过多 |
| Latch Hit % | > 99% | < 98% | 低 = 并发争用 |
| In-memory Sort % | > 95% | < 90% | 低 = PGA 不足或排序量异常 |

**命中率不等于没有问题**：99% 的命中率配上 20 万块/秒的逻辑读，依然是 CPU 灾难。
永远同时看绝对量（每秒块数），不只看比率。

### 判断优先级

`DB Time 贡献` > `单次成本` > `执行次数`。一个事件 100 万次等待但占 0.1% DB Time，不是问题；
一条 SQL 执行 20 次占 15% DB Time，是最大的问题。

---

## 六、五类瓶颈判定

| 类型 | 识别信号 | 常见根因 | 不要做什么 |
|---|---|---|---|
| **CPU 型** | DB CPU / DB Time > 80%，物理读占比 < 5% | 全表扫描、低效连接、硬解析 | 别谈加内存、换存储 |
| **IO 型** | `db file sequential/scattered read` 进前二且占比 > 15%，物理读/秒持续高 | 缺索引、缓存不足、大表扫描、备份冲突 | 别急着加磁盘，先看是不是全扫打出来的 |
| **争用型** | `buffer busy waits`、`enq:`、latch 类占前几 | 热块、行锁、序列、索引分裂 | 别调 SQL，先看应用并发模型 |
| **内存型** | Library Hit 低、`free buffer waits`、`row cache lock` | 共享池/缓冲区不足、游标泄漏 | 别直接调大 SGA，先查游标膨胀 |
| **Redo/提交型** | `log file sync` 占比 > 3%，redo 量高 | 提交过频、逐行 DML、日志盘慢 | 别改 `commit_write`，先看批量 |

多数生产库的主因是 CPU 型——"CPU 在算什么"比"存储够不够快"更常见。

---

## 七、意见的分级标准

| 级别 | 判据 | 时间要求 |
|---|---|---|
| **P0** | 单项占 DB Time > 10%，或在浪费资源且无业务价值（如监控 SQL 自耗） | 当天 |
| **P1** | 单项占 3%~10%，或有低成本高收益的索引/统计修复 | 本周内 |
| **P2** | 单项占 1%~3%，或需要应用改造配合 | 下个迭代 |
| **P3** | 结构性优化（分区键改造、缓存化、负载均衡） | 排期评估 |

排序用「预期回收的 DB Time %」除以「实施成本」，不要按发现顺序排。

---

## 八、反模式

- ❌ RAC 只出单实例报告 —— 很可能正好是最闲的节点，得出"库很闲"的错误结论
- ❌ 用相对时间窗口（`last: 2h`）—— 会按快照边界取整，窗口不可复现
- ❌ 为了分析去解析 HTML —— 体积是 txt 的 3 倍，还要维护两套标题规则
- ❌ 在 AWR 的 HTML 里找执行计划 —— HTML 只有统计，计划在 `dba_hist_sql_plan`
- ❌ 把 `dba_tables.num_rows` 当真实行数 —— 统计滞后的库偏差可达 20% 以上
- ❌ 只看 `buffer_gets` 总量不看 per-exec —— 高频小 SQL 会盖住单次巨慢的 SQL
- ❌ 建议加索引却不看 `clustering_factor` —— 单列索引可能比全扫还贵
- ❌ 把 `INTERNAL_FUNCTION(col)` 当成"列类型有问题" —— 它只是占位符，可能是特定游标的解析产物
- ❌ 拿 99% 的命中率下"健康"结论 —— 要看每秒绝对块数
- ❌ 用 1 小时 AWR 解释 5 分钟的尖刺 —— 该用 ASH
- ❌ 只报问题不给验证方法 —— 对方没法确认改完有没有用

---

## 九、资源导航

| 文件 | 何时读 |
|---|---|
| `references/mcp-execution.md` | **取证前必读**：取数通道能跑什么/不能跑什么、取数纪律、DDL 交接方式 |
| `references/query-cookbook.md` | 全部验证过的取数 SQL + 常见 ORA 报错与绕法 |
| `references/diagnosis-patterns.md` | 判定阶段：八种模式的证据链与处置 |
| `references/recommendation-playbook.md` | 写意见时：十类问题的建议模板（动作/收益/验证/回退） |
| `references/environment-baseline-template.md` | 新库首次分析时：填环境基线的模板 |
| `scripts/awr_extract.py` | 报告章节抽取（txt / html 自适应） |

---

## 十、版本说明与来源

- 基线兼容 Oracle 11g~23ai。`dba_hist_*` 视图与 `DBMS_XPLAN.DISPLAY_AWR` 在 19c/23ai 通用。
- 12c 起 AWR 数据随 CDB 变化：`CON_ID` 参与主键，PDB 级分析需按 `con_id` 过滤；本 skill 的查询
  默认在根容器或单实例上执行。
- `DBMS_WORKLOAD_REPOSITORY` 的 `CREATE_SNAPSHOT` / `CREATE_BASELINE` 语法 11g 起稳定。
- 版本差异导致列缺失时，优先 `SELECT * ... WHERE ROWNUM<=1` 探测列名，不要凭记忆写列。

## Sources

- Oracle Database Performance Tuning Guide（TGDBA）：DB Time / AAS 定义与调优方法论
- Oracle Database Reference：`DBA_HIST_SNAPSHOT`、`DBA_HIST_SQLSTAT`、`DBA_HIST_SQL_PLAN`、
  `DBA_HIST_SEG_STAT`、`DBA_HIST_ACTIVE_SESS_HISTORY`、`V$SQL`、`V$SQL_PLAN`、`V$OSSTAT`
- Oracle Database PL/SQL Packages：`DBMS_WORKLOAD_REPOSITORY`、`DBMS_XPLAN`、`DBMS_STATS`
- 结构参考：`github.com/oracle-skills` 的 `db/performance/awr-reports.md`、`ash-analysis.md`、`wait-events.md`

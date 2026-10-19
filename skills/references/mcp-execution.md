# 取数通道执行契约（以 awrtool MCP 为例）

这份文件回答一个问题：**哪些事能用取数通道干，哪些必须交给 DBA 在客户端干，以及怎么干才不浪费上下文。**

> 本文档以 awrtool MCP 为基准（只读、单语句、有行数上限）。若改用 SQLcl / SQL*Plus 直连，
> 第 2、4 节的约束会放宽（可以跑 DDL），但第 3 节的取数纪律同样适用。

不带这份约束去写建议，很容易给出"CREATE INDEX"这种 MCP 根本跑不了的命令，或者一次拉 5,000 行把上下文打爆。

---

## 1. 三个工具的能力边界

| 工具 | 参数 | 能做什么 | 不能做什么 |
|---|---|---|---|
| `mcp__awrtool__awr_report` | `last` 或 `beginSnap/endSnap/beginTime/endTime`、`type`(awr/global)、`format`(html/txt)、`instNum`、`outPath` | 生成 AWR 报告到本地文件，返回路径/字节/sha256/实际覆盖区间 | 不能按任意 SQL 取数；不能出 ASH 报告 |
| `mcp__awrtool__oracle_query` | `sql`（必填）、`maxRows`（默认 200，上限 5000）、`timeoutSeconds`（默认 600，上限 600） | 执行**单条只读**查询，返回列名+行+`truncated` | 不能 DDL/DML/PL/SQL；不能多语句；不能 ALTER SESSION |
| `mcp__awrtool__oracle_table_rowcount` | `table`、`where`、`timeoutSeconds` | `COUNT(*)`，专治大表计数 | 不能带 JOIN/子查询的复杂条件 |

**判定规则**：只要你的语句不以 `SELECT` 或 `WITH` 开头，就一定会被拒。不要试，直接走第 4 节的交接流程。

---

## 2. 执行 SQL 的硬约束

| 约束 | 具体表现 | 应对 |
|---|---|---|
| 单语句 | 用 `;` 拼接多条会被拒 | 一次一个调用 |
| 只读 | `CREATE`/`ALTER`/`DROP`/`INSERT`/`UPDATE`/`DELETE`/`EXEC`/`BEGIN...END` 全拒 | 见第 4 节 |
| 不能改会话 | `ALTER SESSION SET EVENTS '10053...'`、`SET AUTOTRACE` 不可用 | 10053/trace 必须 DBA 在客户端做 |
| 不能 EXPLAIN PLAN | `EXPLAIN PLAN FOR ...` 不可执行 | 改读 `DBMS_XPLAN.DISPLAY_AWR()`、`DBA_HIST_SQL_PLAN`、`GV$SQL_PLAN` |
| 行数上限 | `maxRows` 默认 200，最大 5000；超出返回 `truncated: true` | 先聚合，再分页 |
| 超时 | 单条最长 600 秒 | 大表聚合先加过滤条件 |

**可以用的 PL/SQL 函数**：`TABLE(DBMS_XPLAN.DISPLAY_AWR(...))`、`DBMS_LOB.SUBSTR()`、
`DBMS_LOB.INSTR()`、`DBMS_RANDOM` 等**在 SELECT 里调用的函数**都可以。被禁的是过程式调用，
不是函数。

---

## 3. 控制返回体积（这是省 token 的关键）

一次 5,000 行的查询比十次 20 行的查询贵得多，而且更容易看漏。

**纪律**

1. **先聚合再明细**：能用 `GROUP BY` 就别 `SELECT *`。
2. **永远带 `FETCH FIRST N ROWS ONLY`** 或 `WHERE ROWNUM <= N`。
3. **只取需要的列**，宽表（200 列）一次就能吃掉几千 token。
4. **CLOB 一律 `DBMS_LOB.SUBSTR(col, 150, 1)`**，取前 150 字符足够识别 SQL。
5. **分段取**：按 `snap_id`、按 `inst_id`、按表名切片，多次小查询优于一次大查询。
6. **检查 `truncated`**：返回 `true` 说明你看到的不是全量，结论不能建立在被截断的数据上。

**示例：从"取全表"改成"取结论"**

```sql
-- ✗ 差：可能几千行，还带 CLOB
SELECT * FROM dba_hist_sqlstat WHERE snap_id BETWEEN 36882 AND 36884;

-- ✓ 好：一行一个结论，附带判定用的派生列
SELECT st.sql_id,
       SUM(st.executions_delta) execs,
       ROUND(SUM(st.buffer_gets_delta)/NULLIF(SUM(st.executions_delta),0)) gets_per_exec,
       ROUND(SUM(st.elapsed_time_delta)/1000000, 1) elapsed_s
FROM dba_hist_sqlstat st
WHERE st.snap_id BETWEEN 36882 AND 36884 AND st.instance_number = 1
GROUP BY st.sql_id
ORDER BY 4 DESC NULLS LAST
FETCH FIRST 15 ROWS ONLY;
```

---

## 4. DDL / DML：谁来执行

**分工原则**：只读通道（MCP）只做「改前取证 → 输出脚本 → 改后验证」；变更动作由有写权限的人
在 SQLcl / SQL*Plus 上完成。若你本身就持有可写客户端，可以直接执行，但仍要走同样的三段式
（取证 → 执行 → 验证），否则无法确认效果。

### 标准交接话术

```
下面这段在 SQLcl / SQL*Plus 里执行（MCP 只能读，跑不了 DDL）：

  CREATE INDEX ... ONLINE;
  EXEC DBMS_STATS.GATHER_TABLE_STATS(...);

执行完告诉我，我用下面这条 SQL 验证效果：
  SELECT ... FROM dba_hist_sqlstat WHERE sql_id='...';
```

### 常见变更的脚本模板

```sql
-- 建索引（ONLINE 不阻塞 DML；nologging 对已有表无效，别写）
CREATE INDEX idx_wf_rde_route_nextproc
  ON <OWNER>.<table> (routeid, nextprocessid) ONLINE;

-- 收集统计信息（先导出旧统计，便于回退）
EXEC DBMS_STATS.EXPORT_TABLE_STATS('<OWNER>','<TABLE>', NULL, 'STAT_BAK');
EXEC DBMS_STATS.GATHER_TABLE_STATS('<OWNER>','<TABLE>', CASCADE=>TRUE, DEGREE=>4);

-- 建 AWR 基线（对比期用）
EXEC DBMS_WORKLOAD_REPOSITORY.CREATE_BASELINE(36882, 36884, 'PRE_TUNING', 30);

-- 10053 跟踪（只能客户端做）
ALTER SESSION SET EVENTS '10053 trace name context forever, level 1';
-- 执行目标 SQL
ALTER SESSION SET EVENTS '10053 trace name context off';
```

### 回退脚本也一并给出

```sql
DROP INDEX <OWNER>.<index> ONLINE;
EXEC DBMS_STATS.RESTORE_TABLE_STATS('<OWNER>','<TABLE>','STAT_BAK');
```

没有回退方案的变更建议，不算建议。

---

## 5. 权限边界与降级路径

| 情况 | 表现 | 处理 |
|---|---|---|
| schema 无 SELECT | `ORA-00942` | 向用户要权限；**不要**拿 `dba_tables.num_rows` 冒充实测值 |
| 无 DDL 权限 | 拒绝执行 | 走第 4 节交接 |
| 无 ALTER SESSION | 拒绝执行 | 10053 / trace 交给 DBA |
| 视图不存在 | `ORA-00942`（如 `dba_hist_segment`） | 换等价视图（`dba_hist_seg_stat_obj`） |
| 行被截断 | `truncated: true` | 缩小范围或聚合 |

**降级时必须标注**：用统计值代替实测值时，写清「数据来自 `dba_tables.num_rows`，统计时间 X，
可能偏差 ±N%」。实测见过 20% 以上的偏差，不标注就是误导。

---

## 6. 工具层异常速查

| 现象 | 原因 | 绕法 |
|---|---|---|
| `内部错误：Specified cast is not valid` | NUMBER 高精度列（如 `dba_tab_histograms.endpoint_value`）转换失败 | `TO_CHAR()` 包一层 |
| `ORA-01722 无效数字` | `IN` 列表触发驱动层类型推断 | 拆成多个单行 `SELECT ... UNION ALL` |
| `ORA-01790 表达式类型不一致` | `UNION ALL` 两侧列类型/精度不同 | 统一 `TO_CHAR()` 或去掉 NULL 列 |
| `ORA-06502 字符串缓冲区太小` | `UPPER()` 作用在 CLOB 上 | 用 `DBMS_LOB.INSTR()` 做匹配 |
| `truncated: true` | 行数超 `maxRows` | 提高 `maxRows` 或加分页条件 |
| 返回 `rowCount: 0` 但无报错 | 条件确实没命中（常见于窗口写错） | 先查 `dba_hist_snapshot` 确认窗口 |

---

## 7. 一次完整取证的标准调用序列

以"分析最近 2 小时"为例，控制在 8~12 次调用内完成：

| # | 调用 | 目的 |
|---|---|---|
| 1 | `awr_report`（txt，inst2） | 出单实例报告 |
| 2 | `awr_report`（txt，global） | 出全局报告 |
| 3 | 脚本 `--head` / `--section` | 抽概览、Top 等待、Top SQL（不消耗 MCP 调用） |
| 4 | `oracle_query`：逐实例 DB time/CPU | 验证报告数字 |
| 5 | `oracle_query`：Top SQL + gets/exec | 定位嫌疑 SQL |
| 6 | `oracle_query`：段级逻辑读 Top 10 | 定位热点表 |
| 7 | `oracle_query`：`dba_hist_sql_plan` 取谓词 | 判断有无索引访问路径 |
| 8 | `oracle_query`：索引 + 统计 + 真实行数 | 判定可行性 |
| 9 | `oracle_query`：`gv$sql` 现场验证 | 确认改动是否已生效 |
| 10 | 输出结论 + 交接脚本 | 结束 |

**顺序原则**：报告先给全局印象 → 查询只用于验证与下钻 → 结论必须在报告数字和查询结果间对得上。
对不上时以查询为准，并在报告里注明差异（通常是快照边界或统计滞后导致）。

---

## 8. 自检清单

出结论前过一遍：

- [ ] 每个数字都能说出「哪个报告章节」或「哪条 SQL 的哪一列」
- [ ] 没有任何 DDL 被写成"MCP 会执行"
- [ ] 所有查询都带 `FETCH FIRST`，且 `truncated` 为 `false`
- [ ] 用统计值的地方都标注了统计时间
- [ ] 变更建议都配了回退脚本
- [ ] 报告窗口与 DDL 时间已对齐

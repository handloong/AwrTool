# 取数 SQL 手册（语法在 19c 验证过；示例值按实际环境替换）

所有 SQL 都通过 `mcp__awrtool__oracle_query` 执行，**只能跑单条 SELECT/WITH**；
DDL、DML、`EXEC`、`ALTER SESSION` 会被拒，必须交客户端执行。
改用 SQLcl / SQL*Plus 时直接粘贴即可（但同样建议先聚合后取数，别在客户端拉几万行）。
工具契约与取数纪律见 `mcp-execution.md`。带 ⚠ 的是踩过的坑。

---

## 0. 快照与窗口

```sql
SELECT snap_id,
       TO_CHAR(begin_interval_time,'YYYY-MM-DD HH24:MI:SS') beg,
       TO_CHAR(end_interval_time,'YYYY-MM-DD HH24:MI:SS') endt
FROM dba_hist_snapshot
WHERE instance_number = 2
  AND end_interval_time > systimestamp - INTERVAL '8' HOUR
ORDER BY snap_id;
```

⚠ `begin_snap = N` 的报告从「N 的 end_interval_time」开始。最近 2 小时 = `N-2 → N`。
⚠ `dba_hist_sqlstat` / `dba_hist_sysstat` 里 `snap_id = N` 的 delta 覆盖 `N-1 → N`，
   取 2 小时窗口用 `snap_id IN (N-1, N)`，不要用 `N-2..N`（会多算 1 小时）。
⚠ 时间差不能直接相减：`(end_interval_time - begin_interval_time)*86400` 会报
   `ORA-00932 应为 NUMBER 但却获得 INTERVAL`。要先 `CAST(... AS DATE)`。

---

## 1. 逐实例 DB time / DB CPU

```sql
SELECT s.instance_number inst,
       ROUND(SUM(CASE WHEN t.stat_name='DB time' THEN t.value/1000000 END)) db_time_s,
       ROUND(SUM(CASE WHEN t.stat_name='DB CPU'  THEN t.value/1000000 END)) db_cpu_s
FROM dba_hist_sys_time_model t
JOIN dba_hist_snapshot s
  ON s.snap_id = t.snap_id AND s.dbid = t.dbid AND s.instance_number = t.instance_number
WHERE s.snap_id BETWEEN :b AND :e
GROUP BY s.instance_number;
```

⚠ 这样算会把每个快照的累计值直接相加，只适合看量级。精确做法是先 `LAG()` 求差分。
⚠ 全局报告里 `Database Instances Included In Report` 段直接给了逐实例 DB time 与 AAS，
   优先用报告数字，查询只作交叉验证。

---

## 2. Top SQL（带文本）

```sql
SELECT * FROM (
  SELECT st.sql_id,
         ROUND(SUM(st.elapsed_time_delta)/1000000, 1) elapsed_s,
         ROUND(SUM(st.cpu_time_delta)/1000000, 1)     cpu_s,
         ROUND(SUM(st.buffer_gets_delta))             gets,
         SUM(st.executions_delta)                     execs,
         ROUND(SUM(st.buffer_gets_delta)/NULLIF(SUM(st.executions_delta),0)) gets_per_exec,
         DBMS_LOB.SUBSTR(t.sql_text, 150, 1)          txt
  FROM dba_hist_sqlstat st
  JOIN dba_hist_sqltext t ON t.sql_id = st.sql_id
  WHERE st.snap_id BETWEEN :b AND :e
    AND st.instance_number = 1
  GROUP BY st.sql_id, DBMS_LOB.SUBSTR(t.sql_text, 150, 1)
  ORDER BY 2 DESC NULLS LAST
) WHERE ROWNUM <= 15;
```

⚠ `dba_hist_sqltext.sql_text` 是 CLOB：必须 `DBMS_LOB.SUBSTR()`，用 `SUBSTR()` 或
   `UPPER()` 会 `ORA-00932`（应为 -，但却获得 CLOB）/ `ORA-06502`。
⚠ `dba_hist_sqlstat` **没有** `child_number`、`application_wait_time_delta` 列
   （`ORA-00904`）。要看子游标用 `gv$sql`。
⚠ 搜索文本用 `DBMS_LOB.INSTR(t.sql_text,'WF_XXX') > 0`，不要用 `LIKE '%...%'`。

---

## 3. 解析与负载特征

```sql
SELECT instance_number inst, stat_name,
       ROUND(SUM(dv)) delta, ROUND(SUM(dv)/7168.8) per_sec
FROM (
  SELECT instance_number, snap_id, stat_name,
         value - LAG(value) OVER (PARTITION BY instance_number, stat_id ORDER BY snap_id) dv
  FROM dba_hist_sysstat
  WHERE snap_id IN (:s1, :s2)
    AND stat_name IN ('table scan rows gotten','consistent gets','db block gets',
                      'execute count','parse count (hard)','parse count (total)',
                      'user commits','redo size','physical reads','sorts (disk)',
                      'sorts (memory)','opened cursors cumulative','session cursor cache hits')
)
GROUP BY instance_number, stat_name ORDER BY 1, 2;
```

`table scan rows gotten` 是实例级累计值，**不按 SQL 拆分**。归因只能反推：
`execs × 被扫表的行数`。验证方法：`buffer_gets_delta / executions_delta ≈ 表的块数`。

---

## 4. 等待事件（需自己求差分）

```sql
SELECT * FROM (
  SELECT event_name, wait_class,
         ROUND(SUM(dt)/1000000, 1) time_s, SUM(dc) waits
  FROM (
    SELECT event_name, wait_class,
           time_waited_micro - LAG(time_waited_micro) OVER (PARTITION BY event_id ORDER BY snap_id) dt,
           total_waits      - LAG(total_waits)      OVER (PARTITION BY event_id ORDER BY snap_id) dc
    FROM dba_hist_system_event
    WHERE snap_id IN (:s1, :s2) AND instance_number = 1 AND wait_class <> 'Idle'
  )
  GROUP BY event_name, wait_class HAVING SUM(dt) > 0
  ORDER BY 3 DESC
) WHERE ROWNUM <= 15;
```

⚠ `dba_hist_system_event` **没有** `*_delta` 列（`ORA-00904`），必须 `LAG()`。
⚠ 直接查最新一条快照的累计值只能看趋势，不能当窗口数据。

---

## 5. 段级逻辑读（谁在被反复扫）

```sql
SELECT * FROM (
  SELECT o.object_name, o.object_type,
         ROUND(SUM(st.logical_reads_delta)) logical_reads,
         ROUND(SUM(st.logical_reads_delta)/7168.8) blk_per_sec,
         ROUND(SUM(st.physical_reads_delta)) phys_reads,
         ROUND(SUM(st.db_block_changes_delta)) blk_chg
  FROM dba_hist_seg_stat st
  JOIN dba_hist_seg_stat_obj o
    ON o.dbid = st.dbid AND o.obj# = st.obj# AND o.dataobj# = st.dataobj# AND o.ts# = st.ts#
  WHERE st.snap_id BETWEEN :b AND :e AND st.instance_number = 1
  GROUP BY o.object_name, o.object_type
  ORDER BY 3 DESC NULLS LAST
) WHERE ROWNUM <= 15;
```

⚠ 对象名视图是 `DBA_HIST_SEG_STAT_OBJ`。`dba_hist_segment` 在部分环境**不存在**（`ORA-00942`）。
⚠ `table_scans_delta` 在部分环境恒为 0，**不能用它统计全扫次数**；只能按
   `logical_reads_delta / 段块数` 估算扫描次数。
⚠ 容器表（分区表）这里只给汇总行；段大小要用 `dba_segments` 按 `segment_name` 聚合。

---

## 6. 执行计划

三者等价，按可得性选：

```sql
-- ① AWR 历史计划（最常用）
SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_AWR('fq59jjnwxzzcg', NULL, NULL, 'BASIC +ALIAS +PREDICATE'));

-- ② 纯 SQL 版（不需要 PL/SQL 包）
SELECT id, operation, options, object_name,
       access_predicates, filter_predicates
FROM dba_hist_sql_plan WHERE sql_id = '<id>' ORDER BY id;

-- ③ 当前共享池里的游标（验证是否已切换计划）
SELECT id, operation, options, object_name, cost, cardinality,
       access_predicates, filter_predicates
FROM gv$sql_plan
WHERE inst_id = 1 AND sql_id = '<id>' AND child_number = 0;
```

读计划时必须同时看这几列：

| 列 | 空值含义 |
|---|---|
| `access_predicates` | **为空 = 没有任何索引访问路径**（最硬的判据） |
| `filter_predicates` | 全是内存过滤，说明条件都没走访问路径 |
| `cost` / `cardinality` | 估算行数与代价，用来判断"优化器选错"还是"确实没有更好路径" |

**`INTERNAL_FUNCTION("COL")`** = 该列被施加了隐式转换，普通 B*Tree 索引无法作为访问路径。
DBMS_XPLAN 不会告诉你真实函数名。三条排查路径：

```sql
-- 看绑定值与字符集（判断是否绑定类型问题）
SELECT id, DBMS_LOB.SUBSTR(other_xml, 1500, 1) FROM dba_hist_sql_plan
WHERE sql_id='<id>' AND id=1;

-- 看列的实际字符集/排序规则
SELECT column_name, data_type, data_length, char_used, collation
FROM dba_tab_columns WHERE owner='<OWNER>' AND table_name='<T>' AND column_name='<C>';

-- 终极：10053（需 ALTER SESSION 权限），或按原文用真实字面量重新 EXPLAIN 对比
```

⚠ 同一个 sql_id 在 AWR 里有 `INTERNAL_FUNCTION`、重放却出现 `INDEX RANGE SCAN` 时，
   说明它是**该游标解析环境的产物**，不是语句文本固有属性——先怀疑绑定/会话参数，别改表。

---

## 7. 索引、统计信息与真实规模

```sql
-- 索引清单（含可用性、可见性、聚簇因子）
SELECT table_name, index_name, index_type, uniqueness, status, visibility,
       last_analyzed, num_rows, distinct_keys, clustering_factor, leaf_blocks, blevel
FROM dba_indexes WHERE owner='<OWNER>' AND table_name IN ('T1','T2');

-- 索引列顺序
SELECT index_name, column_position, column_name
FROM dba_ind_columns WHERE index_owner='<OWNER>' AND index_name IN ('I1','I2')
ORDER BY index_name, column_position;

-- 列统计（选择性、直方图）
SELECT column_name, num_distinct, density, num_nulls, histogram, num_buckets, sample_size, last_analyzed
FROM dba_tab_col_statistics
WHERE owner='<OWNER>' AND table_name='T' AND column_name IN ('C1','C2');

-- 分区键（判断能不能剪枝）
SELECT column_name, column_position FROM dba_part_key_columns
WHERE owner='<OWNER>' AND name='T' ORDER BY column_position;

-- 段真实规模（以它为准，不要用 dba_tables.blocks）
SELECT segment_name, SUM(blocks) blocks, ROUND(SUM(bytes)/1024/1024) mb
FROM dba_segments
WHERE owner='<OWNER>' AND segment_type LIKE 'TABLE%' AND segment_name IN ('T1','T2')
GROUP BY segment_name ORDER BY 2 DESC;

-- 真实行数（需 schema 只读权限）
SELECT 'T1' tbl, COUNT(*) FROM <OWNER>.T1
UNION ALL SELECT 'T2', COUNT(*) FROM <OWNER>.T2;
```

⚠ `dba_tables.num_rows` / `blocks` 可能严重滞后（实测偏差可达 20%~35%）。判定"表有多大"一律用
   `dba_segments`；判定"全扫要读多少块"也用 `dba_segments.blocks`，它能和 AWR 的
   `gets/exec` 对上（误差 <2% 即确认全扫）。
⚠ `dba_tab_histograms.endpoint_value` 是 NUMBER，混合直方图下容易触发驱动层的
   `内部错误：Specified cast is not valid`，取数时用 `TO_CHAR()` 包一层。

---

## 8. 现场验证（改动是否生效）

```sql
SELECT sql_id, child_number, plan_hash_value, executions, parse_calls,
       buffer_gets, elapsed_time,
       ROUND(buffer_gets / NULLIF(executions,0)) gets_per_exec,
       ROUND(elapsed_time / NULLIF(executions,0) / 1000, 2) ms_per_exec
FROM gv$sql WHERE inst_id = 1 AND sql_id = '<id>';
```

⚠ `WHERE sql_id IN ('a','b','c')` 在部分环境会偶发 `ORA-01722 无效数字`（驱动层问题）。
   改为多个单行 `SELECT ... WHERE sql_id='a' UNION ALL SELECT ... WHERE sql_id='b'`。
⚠ `v$sql` 是**生命周期累计值**，不是窗口值。判断"改动后变快"看
   `buffer_gets/executions` 的比值，而不是总量；总量会被历史执行稀释。
⚠ 新计划的判据：`child_number` 变化 + `plan_hash_value` 变化。

---

## 9. 常用错误速查

| 报错 | 原因 | 绕法 |
|---|---|---|
| `ORA-00932 应为 NUMBER/-, 但却获得 INTERVAL/CLOB` | 时间差未 CAST；CLOB 用了 SUBSTR | `CAST(ts AS DATE)`；`DBMS_LOB.SUBSTR` |
| `ORA-00904` | 视图无该列（`dba_hist_sqlstat.child_number`、`dba_hist_system_event.*_delta`） | 换 `gv$sql` / `LAG()` 求差分 |
| `ORA-00942` | 无 schema 权限或视图名不对（`dba_hist_segment`） | 用 `dba_hist_seg_stat_obj` |
| `ORA-01722` | `IN` 列表 + 驱动层类型推断 | 拆成 `UNION ALL` 单行查询 |
| `ORA-01790` | `UNION ALL` 两侧列类型不一致 | 统一 `TO_CHAR` 或去掉 NULL 列 |
| `ORA-06502` | CLOB 函数链路上限 | 换 `DBMS_LOB.INSTR/SUBSTR` |
| `ORA-00918 未明确定义列` | 多表 JOIN 后引用同名列 | 加表别名前缀 |

---

## 10. 对比两个窗口（改动前后 / 环比）

### 方式 A：同一 SQL 的两窗口 delta 对比（最常用）

```sql
WITH w AS (
  SELECT 'before' tag, ROUND(SUM(elapsed_time_delta)/1e6,1) ela_s,
         ROUND(SUM(buffer_gets_delta)/NULLIF(SUM(executions_delta),0)) gets_per_exec,
         SUM(executions_delta) execs
  FROM dba_hist_sqlstat
  WHERE snap_id BETWEEN :b1 AND :e1 AND sql_id = '<id>'
  UNION ALL
  SELECT 'after', ROUND(SUM(elapsed_time_delta)/1e6,1),
         ROUND(SUM(buffer_gets_delta)/NULLIF(SUM(executions_delta),0)),
         SUM(executions_delta)
  FROM dba_hist_sqlstat
  WHERE snap_id BETWEEN :b2 AND :e2 AND sql_id = '<id>'
)
SELECT tag, execs, gets_per_exec, ela_s,
       ROUND(ela_s/NULLIF(execs,0)*1000, 2) ms_per_exec
FROM w;
```

⚠ 两窗口的 `snap_id` 区间按「N-1, N」的规则各算一次，别共用。
⚠ 若两窗口之间有 DDL（建索引），对比才有意义；先查 `dba_indexes.last_analyzed` 确认时间点。

### 方式 B：整份报告对比

- 生成 `type: "global"` 的两份 HTML，分别用 `awr_extract.py --dump` 落盘后逐节对比
- 或直接跑 `awrddrpt.sql`（AWR Compare Period Report），适合看整体而非单条 SQL
- 也可以先建基线，再对比：
  ```sql
  EXEC DBMS_WORKLOAD_REPOSITORY.CREATE_BASELINE(:b1, :e1, 'PRE_TUNING', 30);
  SELECT baseline_name, start_snap_id, end_snap_id, expiration FROM dba_hist_baseline;
  ```

### 对比时要盯的六个指标

| 指标 | 期望方向 |
|---|---|
| 集群 / 实例 DB Time | 降 |
| DB CPU 占比 | 降（说明等待回来了，或计算量真的少了） |
| 目标 SQL 的 `gets/exec` | 降一个数量级 |
| 目标 SQL 的 `plan_hash_value` | 变 |
| 热表 `logical_reads_delta` | 降 |
| 硬解析/秒 | 降 |

---

## 11. ASH 下钻（1 小时窗口解释不了尖刺时）

AWR 是区间聚合，5 分钟的尖刺会被 60 分钟稀释。定位尖刺、阻塞、单会话问题必须用 ASH。

```sql
-- ① 窗口内谁在干活（按 program / sql_id）
SELECT sql_id, program, event, COUNT(*) samples
FROM dba_hist_active_sess_history
WHERE snap_id BETWEEN :b AND :e AND instance_number = 1
GROUP BY sql_id, program, event
ORDER BY 4 DESC FETCH FIRST 15 ROWS ONLY;
```

```sql
-- ② 按分钟看活跃会话趋势（找尖刺的准确时刻）
SELECT TO_CHAR(sample_time,'HH24:MI') minute, COUNT(*) aas_samples
FROM dba_hist_active_sess_history
WHERE snap_id BETWEEN :b AND :e AND instance_number = 1
GROUP BY TO_CHAR(sample_time,'HH24:MI')
ORDER BY 1;
```

```sql
-- ③ 阻塞链（谁挡住了谁）
SELECT blocking_session, blocking_session_serial#,
       session_id, event, COUNT(*) samples
FROM dba_hist_active_sess_history
WHERE snap_id BETWEEN :b AND :e
  AND blocking_session IS NOT NULL
GROUP BY blocking_session, blocking_session_serial#, session_id, event
ORDER BY 5 DESC FETCH FIRST 10 ROWS ONLY;
```

⚠ ASH 采样率是 1 秒 1 次（活跃会话），`COUNT(*)` 近似等于「秒数」。
⚠ `event IS NULL` 表示采样时该会话在 CPU 上，不是数据缺失。
⚠ `dba_hist_active_sess_history` 同样受 Diagnostics Pack 授权约束。

---

## 12. AWR 自身健康检查

```sql
-- 快照间隔与保留期
SELECT snap_interval, retention FROM dba_hist_wr_control;

-- AWR 在 SYSAUX 的占用
SELECT occupant_name, ROUND(space_usage_kbytes/1024,2) mb
FROM v$sysaux_occupants WHERE occupant_name LIKE 'SM/%' ORDER BY 2 DESC;

-- 快照是否连续（缺快照相隔会扭曲 per-second 指标）
SELECT snap_id,
       TO_CHAR(end_interval_time,'HH24:MI') t,
       ROUND((CAST(end_interval_time AS DATE) - CAST(LAG(end_interval_time)
             OVER (ORDER BY snap_id) AS DATE)) * 86400) gap_sec
FROM dba_hist_snapshot WHERE instance_number = 2
ORDER BY snap_id DESC FETCH FIRST 12 ROWS ONLY;
```

`gap_sec` 明显偏离 3,600（如 7,200）说明中间缺快照，做 per-second 换算时要按实际 `elapsed` 算。


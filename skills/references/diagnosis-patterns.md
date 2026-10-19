# 诊断模式表（对号入座，不要自创分类）

每个模式给出：**识别信号 → 必查证据 → 处置 → 验证方法**。
判定时先看信号，符合就用该模式的标准证据链取证，避免"感觉是索引问题"这种无据结论。

---

## 模式 1：反复全表扫描（最常见）

**识别信号**
- `dba_hist_seg_stat` 里某张表的 `logical_reads_delta` 高得离谱（占实例一致性读 10%+）
- 该表物理读为 0（全在 buffer cache）→ 纯 CPU 消耗，不是 I/O 问题
- `gets/exec ≈ 表块数`

**必查证据**
```sql
-- ① 表到底多少块
SELECT segment_name, SUM(blocks), ROUND(SUM(bytes)/1024/1024) mb
FROM dba_segments WHERE owner='<OWNER>' AND segment_name='T' AND segment_type LIKE 'TABLE%'
GROUP BY segment_name;
-- ② 执行计划的 access_predicates
SELECT id, operation, options, object_name, access_predicates, filter_predicates
FROM dba_hist_sql_plan WHERE sql_id='<id>' ORDER BY id;
-- ③ 现有索引列
SELECT index_name, column_position, column_name FROM dba_ind_columns
WHERE index_owner='<OWNER>' AND index_name IN (SELECT index_name FROM dba_indexes
  WHERE owner='<OWNER>' AND table_name='T');
```

**判据**：`gets/exec` 与 `dba_segments.blocks` 误差 <2% → 全表扫描成立（这是比看计划更硬的证据）。

**处置**：按 `WHERE` 里等值条件最多的组合建复合索引，列顺序 = 等值列在前、范围列在后。

**验证**：改动后 `buffer_gets_delta / executions_delta` 应掉到索引高度级别（个位到十位块）。

---

## 模式 2：单列索引无效 / 比全扫还贵

**识别信号**
- 表上有索引，但优化器仍走全表扫描
- `dba_indexes.clustering_factor ≈ num_rows`

**必查证据**
- `clustering_factor`（接近 `num_rows` = 索引顺序与表物理顺序无关，回表≈随机单块读）
- 谓词的 `distinct_keys`（`1,000` 个值对应 `180,000` 行 → 平均 180 行/值，选择性不足）
- 索引的 `blevel` / `leaf_blocks`（估算 range scan 成本）

**判据**：`代价(索引回表) ≈ 命中行数 × 单块读成本` vs `代价(全扫) ≈ 块数 / MBRC`。
当命中行数 > 块数/4 时，全扫通常更便宜——**这不叫优化器选错**。

**处置**：不要争论"为什么不用索引"，直接给能把命中行数降到个位数的复合索引。

---

## 模式 3：隐式转换导致索引失效（`INTERNAL_FUNCTION`）

**识别信号**
- `filter_predicates` 出现 `INTERNAL_FUNCTION("COL")`
- 该列上明明有索引却没被用作 access path

**必查证据**
1. `dba_tab_columns`：列类型、`char_used`、`collation`
2. `dba_hist_sql_plan.other_xml`：`peeked_binds` 里的 `dty` / `csi`
3. 用**原文本 + 真实字面量**重新 EXPLAIN，对比是否仍出现 `INTERNAL_FUNCTION`
4. 环境：`NLS_COMP` / `NLS_SORT` / `optimizer_index_cost_adj`

**判据**：重放后 `INTERNAL_FUNCTION` 消失且出现 `INDEX RANGE SCAN` → 它是**特定游标解析环境的
产物**，不是语句文本固有属性。此时不要改表结构，应先查应用传参（类型/长度/字符集）。

**处置**
- 短期：用复合索引把该列变成**第二列**（前导列可访问即可绕开转换）
- 长期：应用侧统一绑定变量类型；必要时去掉 `N'...'` 或 `CAST(...)` 包裹

---

## 模式 4：分区表不剪枝

**识别信号**
- 计划出现 `PARTITION RANGE ALL`
- 谓词里有日期范围，但分区键是另一列

**必查证据**
```sql
SELECT column_name, column_position FROM dba_part_key_columns
WHERE owner='<OWNER>' AND name='T' ORDER BY column_position;

SELECT COUNT(*) parts, SUM(blocks) blocks, ROUND(SUM(bytes)/1024/1024/1024,1) gb
FROM dba_segments WHERE owner='<OWNER>' AND segment_name='T' AND segment_type='TABLE PARTITION';
```

**判据**：过滤列 ≠ 分区键 → 无法剪枝，每次执行都扫全部 N 个分区。
`gets/exec` 会随分区数线性放大。

**处置**
- 建 `(过滤列, 日期列)` 本地索引，让日期范围参与索引访问
- 或把高频过滤的日期列纳入分区键（需评估改造窗口）
- 不要只加全局索引：回表仍是随机读

---

## 模式 5：监控采集 SQL 反客为主

**识别信号**
- Top SQL 里出现 `v$session`、`v$sqlarea`、`gv$sesstat`、`v$parameter` 的关联查询
- `program` 为 `hekad` / `python3` / `sqlplus`，不是业务进程
- 单次执行耗时秒级到分钟级

**必查证据**
```sql
SELECT sql_id, executions_delta, parse_calls_delta,
       ROUND(elapsed_time_delta/1000000/NULLIF(executions_delta,0),2) sec_per_exec
FROM dba_hist_sqlstat WHERE sql_id='<id>' AND snap_id BETWEEN :b AND :e;
```
再看同一 SQL 在两个实例上的单次耗时差异——差异来自 `v$sqlarea` 的记录数。

**判据**：`v$sqlarea` 记录数（`SELECT COUNT(*) FROM gv$sqlarea GROUP BY inst_id`）。
实测案例中曾见 `v$sqlarea` 达 51 万条 / 36 GB，同一条 `v$session × v$sqlarea` 查询单次 288~452 秒。

**处置**
- 给 `v$session` 加状态过滤或改按 `sql_id` 等值关联
- 用 `v$resource_limit` 替代 `gv$sesstat + CROSS JOIN`
- 治理字面量 SQL（游标膨胀是这类查询变慢的根因）

---

## 模式 6：RAC 负载倾斜

**识别信号**
- 全局报告里两实例 AAS 相差一个数量级以上
- 闲置实例的 top waits 是控制文件读、LGWR 类后台等待

**必查证据**
- 全局报告 `Database Instances Included In Report`（逐实例 AAS / DB time）
- ASH 的 `program` 分布：业务进程集中在哪个实例
  ```sql
  SELECT program, COUNT(*) samples FROM dba_hist_active_sess_history
  WHERE snap_id BETWEEN :b AND :e AND instance_number = 1
  GROUP BY program ORDER BY 2 DESC FETCH FIRST 10 ROWS ONLY;
  ```

**判据**：业务进程全部集中在单实例 → 服务/连接串未做负载均衡，不是性能问题而是配置问题。

**处置**：检查服务（`srvctl config service`）与客户端 TNS 的 ADDRESS_LIST，评估迁移收益。

---

## 模式 7：备份/批处理与业务窗口重叠

**识别信号**
- 某实例 top waits 出现 `Backup Restore Throttle sleep`、`RMAN backup & recovery I/O`
- 实例 DB time 不高但某等待类占比极高（20%+）
- 物理读/秒突增 2 倍以上，`db file sequential read` 次数同步放大

**必查证据**
```sql
SELECT event_name, wait_class, SUM(dt)/1000000 time_s, SUM(dc) waits FROM (
  SELECT event_name, wait_class,
         time_waited_micro - LAG(time_waited_micro) OVER (PARTITION BY event_id ORDER BY snap_id) dt,
         total_waits - LAG(total_waits) OVER (PARTITION BY event_id ORDER BY snap_id) dc
  FROM dba_hist_system_event WHERE snap_id IN (:s1,:s2) AND instance_number = 2
) GROUP BY event_name, wait_class HAVING SUM(dt) > 0 ORDER BY 3 DESC;
```

**判据**：`Backup Restore Throttle sleep` 占该实例 DB time >10% → 备份窗口压在生产时段。

**处置**：把 RMAN 窗口移到凌晨；必要时调 `duration` / `rate`；不要通过加 CPU 解决。

---

## 模式 8：分钟级尖刺被 1 小时窗口稀释

**识别信号**
- 用户描述"某几分钟特别慢"，但 2 小时 AWR 里一切正常
- 窗口内 AAS 平稳，没有单项超过 5%
- DB Time 与用户感知的卡顿时段对不上

**必查证据**
```sql
-- 按分钟看活跃会话，定位尖刺时刻
SELECT TO_CHAR(sample_time,'HH24:MI') minute, COUNT(*) aas
FROM dba_hist_active_sess_history
WHERE snap_id BETWEEN :b AND :e AND instance_number = 1
GROUP BY TO_CHAR(sample_time,'HH24:MI') ORDER BY 1;
```

**判据**：某几分钟 `aas` 突然翻 3 倍以上，而 AWR 的 2 小时均值只有微弱抬升
（例：尖刺 5 分钟 30 AAS + 115 分钟 5 AAS → 2 小时均值 6 AAS，看不出问题）。

**处置**：把 ASH 定位到的分钟段单独取 SQL / 会话 / 事件三个维度，**不要再用 AWR**。
必要时用 `DBMS_WORKLOAD_REPOSITORY.CREATE_SNAPSHOT()` 在复现前后各打一个快照。

**验证**：改完后同一时段的分钟级 AAS 峰值应回落。

---

## 输出要求

每个结论必须写成三段：

1. **现象**：数字（来自哪个报告章节 / 哪条 SQL 的哪一列）
2. **证据**：支撑该数字的查询与结果
3. **处置**：动作 + 预期变化 + 验证用的 SQL

缺任何一段都算不合格。写"处置"时直接套 `recommendation-playbook.md` 的模板——
那里面有十类问题的标准动作、预期收益算法和回退方式，不要临场发挥。


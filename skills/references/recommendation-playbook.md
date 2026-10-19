# 建议模板库（十类问题的标准处方）

写意见时直接套模板。每条必须五段齐全：**判据 → 动作 → 预期收益 → 验证 → 回退**。
少任何一段，这条建议就不成立。预期收益一律换算成「可回收的 DB Time 百分比」，不要写"性能会提升"这种废话。

> ⚠ **下面的 `CREATE INDEX` / `DBMS_STATS` / `ALTER SESSION` 等语句，awrtool MCP 一律执行不了**
> （只接受单条 SELECT）。写建议时必须注明「在 SQLcl / SQL*Plus 执行」，并附回退脚本。
> 详见 `mcp-execution.md`。

---

## 优先级打分

```
性价比 = 预期回收 DB Time% ÷ 实施成本系数
成本系数：改 SQL 提示/参数 = 1；建索引 = 2；改应用 = 3；改分区/架构 = 5
```

| 分数 | 级别 | 时间 |
|---|---|---|
| ≥ 5 | P0 | 当天 |
| 2 ~ 5 | P1 | 本周 |
| 0.5 ~ 2 | P2 | 下个迭代 |
| < 0.5 | P3 | 排期评估 |

---

## 1. 监控/采集 SQL 空耗（P0 常客）

**判据**
- Top SQL 出现 `v$session`×`v$sqlarea`、`gv$sesstat`、`gv$sqlarea` 关联
- `program` 是 hekad / python3 / sqlplus，不是业务进程
- 占集群 DB Time > 5%

**动作**
1. 给 `v$session` 加 `status='ACTIVE'` 或改按 `sql_id` 等值关联
2. open_cursors 类改为查 `v$resource_limit`
3. 停用扫描 `gv$sqlarea` 找 `version_count` 的巡检脚本

**预期收益**：按实测占比重算。实测案例中三条监控 SQL 合计 19%~23% DB Time，改完可全部回收。

**验证**
```sql
SELECT sql_id, SUM(elapsed_time_delta)/1e6 ela_s
FROM dba_hist_sqlstat
WHERE snap_id BETWEEN :b AND :e AND sql_id IN ('<SQL_ID_1>','<SQL_ID_2>','<SQL_ID_3>')
GROUP BY sql_id;
```

**回退**：改回采集脚本原 SQL 即可，无结构变更。

**风险**：低。但若采集器依赖返回字段，改 SQL 后要核对字段名与顺序。

---

## 2. 全表扫描（缺索引）

**判据**
- `gets/exec ≈ dba_segments.blocks`（误差 <2%）
- `access_predicates` 只有部分条件，或为 NULL
- 该表 `logical_reads_delta` 占实例一致性读 >5%

**动作**
```sql
CREATE INDEX idx_<表>_<列组合> ON <owner>.<表> (等值列..., 范围列...) ONLINE;
-- 随后
EXEC DBMS_STATS.GATHER_TABLE_STATS('<owner>','<表>', CASCADE=>TRUE);
```

**预期收益**：`单次 gets` 从「表块数」降到「索引高度 + 命中行数」。
经验值：59MB 表 5,116 块 → 10 块，回收该 SQL 全部耗时。

**验证**
```sql
SELECT ROUND(buffer_gets_delta/NULLIF(executions_delta,0)) gets_per_exec,
       plan_hash_value
FROM dba_hist_sqlstat WHERE sql_id='<id>' AND snap_id BETWEEN :b AND :e;
```
`gets_per_exec` 掉到个位/十位、`plan_hash_value` 变化 = 生效。

**回退**：`DROP INDEX`。ONLINE 创建不阻塞 DML。

**风险**：索引会增加 DML 开销与日志量。写入极频繁的表（每秒千次以上）要先评估。

---

## 3. 单列索引无效 / 优化器"选错"

**判据**
- 表上有索引仍走全表扫描
- `clustering_factor ≈ num_rows`（回表≈随机读）
- 命中行数 > 表块数 / 4

**先说清楚**：这**通常不是优化器选错**，是索引本身不划算。不要用 hint 硬压。

**动作**：改为复合索引，把命中行数压到个位。
优先做实测验证：单列索引的 plan cost 与全扫 cost 相差 <20% 时，复合索引是唯一解。

**预期收益**：见模板 2。

**验证**：同模板 2，另外看 `Rows`（cardinality）是否从千级降到个位。

**回退**：`DROP INDEX`；原单列索引若已被删除，先 `CREATE ... ONLINE` 重建。

**风险**：过度索引会拖慢 DML 并占用空间。一个表的高频查询路径通常 ≤3 条。

---

## 4. 分区表不剪枝

**判据**
- 计划出现 `PARTITION RANGE ALL`
- `dba_part_key_columns` 的分区键 ≠ 谓词里的过滤列
- `gets/exec` 随分区数线性放大

**动作（二选一）**
- 轻：建 `(过滤列, 日期列)` 本地索引，让日期范围参与索引访问
- 重：把高频过滤的日期列纳入分区键（需停机窗口评估）

**预期收益**：`gets/exec` 可从「分区数 × 单分区扫描」降到一个索引 range scan。
实测案例中一张 8 分区表单次 950,800 块逻辑读，改造后预期降两个数量级。

**验证**
```sql
SELECT ROUND(buffer_gets_delta/NULLIF(executions_delta,0)) gets_per_exec
FROM dba_hist_sqlstat WHERE sql_id='<id>' AND snap_id BETWEEN :b AND :e;
-- 并确认计划里 PARTITION RANGE ALL 变成 PARTITION RANGE SINGLE/ITERATOR
```

**回退**：删索引；分区键改造需按备份还原，风险高，务必先在测试库演练。

**风险**：改分区键涉及数据重分布，是大手术，不做 P0。

---

## 5. 硬解析 / 游标膨胀

**判据**
- 硬解析/秒 > 20，且硬解析耗时占 DB Time > 5%
- `v$sqlarea` 条数异常（实测曾见 51.5 万条 / 36 GB）
- `parse_calls ≈ executions`（每次都解析）

**动作**
1. 应用侧改绑定变量（根治）
2. 会话级试 `cursor_sharing=FORCE`（只作验证，不要直接改系统参数）
3. 清理共享池前先确认 `v$sqlarea` 是否真的在涨，而不是一次性灌入

**预期收益**：硬解析耗时（实测 3,111~3,717 秒 / 2h，占 9%）可回收大部分；
同时 `v$sqlarea` 缩小会连带让监控 SQL 变快。

**验证**
```sql
SELECT stat_name, value FROM v$sysstat
WHERE stat_name IN ('parse count (hard)','parse count (total)');
-- 以及
SELECT COUNT(*) FROM gv$sqlarea WHERE inst_id = 1;
```

**回退**：`cursor_sharing` 改回 EXACT 即可。

**风险**：`FORCE` 可能让执行计划不稳定（不同字面量共享同一计划），必须先在测试环境验证。

---

## 6. 高频逐行 DML

**判据**
- 单条 INSERT/UPDATE 执行次数百万级/2h，`rows/exec ≈ 1`
- `log file sync` 占比 > 3%，`db file parallel write` 占比 > 5%
- `user commits/秒` 上百

**动作**
- 应用侧数组绑定 / 批量提交（每 500~1000 行一次）
- 必要时把单条 INSERT 改为 `INSERT ALL` 或外部表装载

**预期收益**：提交次数降一个量级，`log file sync` 与 `db file parallel write` 同比例下降。
实测这两项合计约 9% DB Time。

**验证**
```sql
SELECT ROUND(SUM(time_waited_micro_delta)/1e6,1) log_sync_s
FROM dba_hist_system_event
WHERE snap_id BETWEEN :b AND :e AND event_name='log file sync';
```

**回退**：应用配置回滚到原提交粒度。

**风险**：批量提交会放大回滚段与锁持有时间，需确认事务边界。

---

## 7. 统计信息失真

**判据**
- `dba_tables.num_rows` 与 `count(*)` 偏差 > 10%
- 关键索引 `last_analyzed` 超过 30 天
- 优化器估算 `cardinality` 与实际返回行数差一个数量级

**动作**
```sql
EXEC DBMS_STATS.GATHER_TABLE_STATS('<owner>','<表>', CASCADE=>TRUE, DEGREE=>4);
-- 大表或分区表：
EXEC DBMS_STATS.GATHER_TABLE_STATS('<owner>','<表>', PARTNAME=>'<分区>', CASCADE=>TRUE);
```

**预期收益**：不直接回收 DB Time，但**决定前六条建议能否生效**。永远排在最前面做。

**验证**
```sql
SELECT table_name, num_rows, last_analyzed FROM dba_tables WHERE table_name='<表>';
-- 与 count(*) 对比，偏差 <5% 即合格
```

**回退**：`DBMS_STATS.RESTORE_TABLE_STATS('<owner>','<表>','<as_of>')`

**风险**：统计信息变化会导致计划翻转。改前先导出旧统计（`DBMS_STATS.EXPORT_TABLE_STATS`）。

---

## 8. RAC 负载倾斜

**判据**
- 两实例 AAS 相差 10 倍以上
- 业务进程全部集中在单实例（ASH 的 `program` 分布）

**动作**：检查服务配置与客户端 TNS 的 ADDRESS_LIST，把服务分摊到两节点

**预期收益**：把单实例 CPU 压力减半，但对总 DB Time 无影响（不是效率问题）

**验证**：`srvctl config service` + 两实例 AAS 趋近

**回退**：服务重新指回原节点

**风险**：跨实例的会话粘连（temp/undo 争用）可能上升，迁移后要观察 gc 等待

---

## 9. 备份 / 批处理与业务窗口重叠

**判据**
- `Backup Restore Throttle sleep`、`RMAN backup & recovery I/O` 进入该实例前几
- 物理读/秒突增 2 倍以上，`db file sequential read` 次数同步放大

**动作**：把 RMAN 窗口移到凌晨；必要时用 `duration` + `rate` 限速

**预期收益**：回收该时段被挤占的 I/O 与 CPU（实测占该实例 DB Time 21.87%）

**验证**
```sql
SELECT event_name, SUM(time_waited_micro_delta)/1e6 s
FROM dba_hist_system_event
WHERE snap_id BETWEEN :b AND :e AND event_name LIKE 'Backup%'
GROUP BY event_name;
```

**回退**：恢复原备份策略

**风险**：挪窗口要考虑备份时长是否撞上下一个业务高峰

---

## 10. 后台进程常驻开销（LogMiner / CDC / Data Pump）

**判据**
- `LogMiner reader: buffer`、`Streams AQ:` 等稳定占 1%~3%
- ASH 中对应后台进程持续有采样

**动作**：确认业务必要性；不需要就停，需要就评估抽数频率

**预期收益**：1%~2% DB Time

**验证**：停用后看该等待是否消失

**回退**：重新启动捕获进程

**风险**：停错会中断下游同步，先确认依赖方

---

## 意见汇报模板

```markdown
## 结论
本次窗口属 {CPU/IO/争用/内存/Redo} 型。DB Time {X} 秒，其中 DB CPU 占 {Y}%。
前三位消耗：{SQL_A} {a}%（{性质}）、{SQL_B} {b}%、{等待事件} {c}%。

## 证据
| 结论 | 出处 | 数字 |
|---|---|---|
| ... | 全局报告 Top Timed Events | ... |
| ... | dba_hist_seg_stat（snap b–e） | ... |

## 建议（按性价比排序）
### P0-1 {动作标题}
- 判据：...
- 动作：...
- 预期收益：回收 {n}% DB Time
- 验证：{SQL}
- 回退：...

## 不做的事（以及为什么）
- 不加内存：DB CPU 已占 87%，瓶颈在计算不在缓存
- 不换存储：单块读 439us 属健康区间
```

最后一段「不做的事」很重要：它能挡住那些看起来合理、实际浪费预算的动作。

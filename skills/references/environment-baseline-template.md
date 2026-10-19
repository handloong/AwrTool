# 环境基线模板

**这份文件不进 skill，只进工作区。** 每个库一份，路径：`<ws>/awr/baseline-<DBNAME>.md`。

新库首次分析时按下面结构填一遍；之后每轮分析前先读它，跑完再更新「已实施的整改」与「待办」。

为什么必须分开：skill 装方法，工作区存数据。把某个库的行数、SQL_ID、索引名写进 skill，
换个库就是错的，还会误导下一次判断。

---

## 模板（复制后填空）

```markdown
# <DBNAME> 环境基线

最后更新：YYYY-MM-DD HH:MM　｜　维护人：<名字>

## 1. 拓扑与参数

| 项 | 值 |
|---|---|
| DB / DBID | <DBNAME> / <dbid> |
| 版本 | <version>，<EE/SE>，RAC <是/否>，CDB <是/否> |
| 实例 | 实例 1 <name>@<host>（职责：业务） ／ 实例 2 <name>@<host>（职责：采集/备份） |
| 主机 | <N> CPU / <M> GB；Load 常态 <x> |
| SGA / PGA | <SGA> / <PGA target> |
| 快照 | 间隔 <N> 分钟，保留 <D> 天 |
| 业务 schema | <SCHEMA>（是否有只读权限：是/否） |
| 关键参数 | `cursor_sharing`=…、`open_cursors`=…、`optimizer_features_enable`=… |

## 2. 历史窗口基线

| 指标 | <窗口 A> | <窗口 B> | 说明 |
|---|---|---|---|
| 集群 DB Time | | | |
| 实例 1 AAS | | | |
| 实例 2 AAS | | | |
| DB CPU 占比 | | | |
| 硬解析耗时 | | | |
| 一致性读/秒 | | | |
| 全表扫描行/秒 | | | |
| 物理读/秒 | | | |

**形态判断**：<CPU 型 / IO 型 / 争用型>，依据 …

## 3. 常驻问题清单

### 3.1 监控采集 SQL（若有）

| SQL_ID | 来源程序 | 窗口耗时 | 单次 | 机理 |
|---|---|---|---|---|

### 3.2 业务全表扫描 / 慢 SQL

| SQL_ID | 表 | 单次 gets | 备注 |
|---|---|---|---|

### 3.3 高频 DML

| SQL_ID | 表 | 2h 执行次数 | 段规模 |
|---|---|---|---|

## 4. 已实施的整改与实测效果

| 日期 | 变更 | 目标 SQL | 改前 | 改后 | 倍数 |
|---|---|---|---|---|---|

> 每条都要能追溯到 `dba_indexes.last_analyzed` 或变更单号。
> 这一节是判断"某窗口是改前还是改后"的唯一依据。

## 5. 关键表真实规模

| 表 | `count(*)` | 段块数 | 大小 | 统计 `num_rows` | 偏差 |
|---|---|---|---|---|---|

> 每次分析前抽查 2~3 张热点表，确认统计是否还在漂。

## 6. 典型干扰项

| 现象 | 实际原因 | 处理 |
|---|---|---|

## 7. 待办

1. …
2. …
```

---

## 填表规则

1. **只记会变的事实**。主机 CPU 数、版本这类不变信息记一次即可；行数、AAS、DB Time 每次窗口更新。
2. **数字带时间戳**。「349,415 行」要写成「349,415 行（2026-10-19 09:50 `count(*)`）」。
3. **第 4 节最重要**。没有它，下次分析无法区分"改前"和"改后"，会把收益重复计算。
4. **不要写推测**。不确定的写「待确认」，不要写成事实。
5. 换库时新建一份，不要在同一份里混多个库。

---

## 自动填充（可选）

`query-cookbook.md` §0–§1 与 SKILL.md Step 0 的探测查询可以直接跑出来填第 1、2 节。
第 3–7 节需要人判断，不要自动生成。

首次探测的最小集合：

```sql
-- 拓扑与版本
SELECT i.instance_number, i.instance_name, i.host_name, i.version,
       (SELECT value FROM v$parameter WHERE name='cluster_database') rac,
       (SELECT name FROM v$database) db_name
FROM gv$instance i ORDER BY 1;

-- 快照配置
SELECT snap_interval, retention FROM dba_hist_wr_control;

-- 主机规格
SELECT stat_name, value FROM v$osstat
WHERE stat_name IN ('NUM_CPUS','NUM_CPU_CORES','PHYSICAL_MEMORY_BYTES');

-- 大头 schema
SELECT owner, ROUND(SUM(bytes)/1024/1024/1024,1) gb
FROM dba_segments GROUP BY owner ORDER BY 2 DESC FETCH FIRST 10 ROWS ONLY;
```

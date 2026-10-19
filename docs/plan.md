# AwrTool 实施计划

每个任务控制在 2–5 分钟粒度，包含精确文件路径、要写的代码要点、验证命令。
标注 `[TDD]` 的任务必须先看到失败的测试（RED），再写实现（GREEN）。

---

## 阶段 0：骨架（已完成）

- [x] `git init` + `.gitignore`
- [x] `NuGet.config`（内部 Nexus 优先，HTTP 源需 `allowInsecureConnections`）
- [x] `Directory.Build.props`
- [x] `src/AwrTool/AwrTool.csproj`（net8.0 / IsAotCompatible / R2R 单文件 / AOT 开关）
- [x] `tests/AwrTool.Tests/AwrTool.Tests.csproj`
- [x] `AwrTool.sln`（经典格式，兼容 VS 2022）
- [x] `build/publish.ps1`

验证：`dotnet build AwrTool.sln` 无警告无错误。

---

## 阶段 1：纯逻辑层（全部 TDD，无需数据库）

### T1 `[TDD]` Duration 解析 — `src/AwrTool/Core/Duration.cs`
支持 `30s` / `90m` / `2h` / `1d`，大小写不敏感，允许小数（`1.5h`）。
无单位、单位非法、负数、零 → 抛 `FormatException`，消息里给出正确示例。

测试要点：`2h→7200s`、`90m→5400s`、`1.5h→5400s`、`1d→86400s`、`"2"`(无单位)抛异常、`"-1h"` 抛异常、`"0m"` 抛异常。

验证：`dotnet test --filter FullyQualifiedName~DurationTests`

### T2 `[TDD]` 退出码与错误码 — `src/AwrTool/Cli/ExitCode.cs`
枚举 + 从异常到退出码的映射入口。
常量：`OK=0 INTERNAL=1 USAGE=2 CONNECT_FAILED=3 SNAPSHOT_NOT_FOUND=4 PRIVILEGE_DENIED=5 REPORT_FAILED=6 OUTPUT_FAILED=7`。

### T3 `[TDD]` ORA 错误分类 — `src/AwrTool/Oracle/OraErrorClassifier.cs`
输入 Oracle 错误号/消息，输出 `ExitCode`。
- `ORA-20019` → `SNAPSHOT_NOT_FOUND`
- `ORA-01031` / `ORA-00942` / `ORA-01039` → `PRIVILEGE_DENIED`
- `ORA-12154` / `ORA-12514` / `ORA-12541` / `ORA-01017` / 超时 → `CONNECT_FAILED`
- `ORA-50071` / 其它 → `REPORT_FAILED`
另含 `IsEmbeddedError(string firstLine)`：判断是否匹配 `^\*\*\* ORA-(\d+)`，命中时抽出错误号（坑 2）。

测试要点：每条映射一条用例；`"*** ORA-20019: -> Error: None of ..."` 能抽出 `20019`；正常的
`"WORKLOAD REPOSITORY report for"` 不算错误。

### T4 `[TDD]` 连接参数解析 — `src/AwrTool/Oracle/ConnectionResolver.cs`
输入 `--connect` / `--dsn` / `--user` / `--password` 与一个可注入的环境变量读取器（便于测试）。
输出完整连接串 + 来源标记。

优先级：`--connect` > `AWRTOOL_CONN` > 三项参数拼装 > `AWRTOOL_DSN`/`AWRTOOL_USER`/`AWRTOOL_PASSWORD`。
缺失策略：`--user`/`--password` 单独给但不完整 → `USAGE`；全缺 → `USAGE`，消息带可复制的环境变量示例。
密码含 `#` 必须原样保留（用例：`Awr#Luxshare#2O26`）。

测试要点：4 条优先级各一例；密码含 `#` 的往返；全缺时的错误消息包含 `AWRTOOL_CONN`；部分提供时明确报缺哪一项。

### T5 `[TDD]` 快照选择 — `src/AwrTool/Core/SnapshotSelector.cs`
纯函数：输入 `IReadOnlyList<SnapshotInfo>`（已按实例过滤）+ 选择条件，输出 `(begin, end)` 或抛错。
- 无条件 → 最近两个
- `--last 2h` → 锚定最新 `end_interval_time`，起点取 `end_interval_time <= anchor-2h` 中最大的
- `--begin-time` / `--end-time` → 取覆盖该时刻的快照
- 已有 begin/end → 校验 `begin < end` 且都存在

测试要点：含 RAC 重复行的输入；`--last` 落在窗口内/跨多个快照；时间戳刚好等于边界；
`begin >= end`；不存在的快照号；只有 1 个快照时要求 2 个区间。

### T6 `[TDD]` 实例列表归一化 — `src/AwrTool/Core/InstanceList.cs`
`"1"`, `"1,2"`, `"1, 2"`, `"1 2"`, `"1,2,3"` → 规范化为逗号分隔无空格。
`null`/空 → 抛错（坑 3：不能传 null）。
非数字/重复/越界 → 报错。

测试要点：空格分隔也能吃进去（用户可能这么写），但输出一定归一化成 `"1,2"`；重复去重；`null` 抛错。

---

## 阶段 2：Oracle 数据访问层

### T7 `src/AwrTool/Oracle/SnapshotInfo.cs` + `AwrRepository.ProbeAsync`
探测 `dbid` / `name` / `version` / 当前 `instance_number`。
SQL：`select dbid from v$database`、`select instance_number from v$instance`、`select banner from v$version where rownum=1`。

### T8 `src/AwrTool/Oracle/AwrRepository.ListSnapshotsAsync`
```sql
select snap_id, instance_number, begin_interval_time, end_interval_time
from   dba_hist_snapshot
where  dbid = :dbid and begin_interval_time > sysdate - :hours
[and   instance_number = :inst]
```
按 `instance_number` 过滤可选，按 `snap_id` 排序。

### T9 `src/AwrTool/Oracle/AwrRepository.StreamReportAsync`
- 按 `ReportKind`（awr / global）拼函数名。
- 必须用 `select output from table(dbms_workload_repository.<fn>(...))`（坑 1）。
- global 传逗号分隔字符串，**不传 null**（坑 3）。
- 逐行 `TextWriter.WriteLine`；读取首行时先跑 `IsEmbeddedError`（坑 2）。
- 返回写入的行数与字符数。

### T10 `src/AwrTool/Output/ReportSink.cs`
临时文件 → 校验 → `File.Move(overwrite: true)`；失败删临时文件。
`-o -` 时直接写 stdout。
计算字节数与 SHA-256。

### T11 集成验证（真库）
```
snapshots
report --type awr    --format txt  --last 2h
report --type awr    --format html --begin-snap 36819 --end-snap 36820 --inst-num 2
report --type global --format txt  --last 2h --inst-num 1,2
report --type global --format txt  --last 2h --inst-num "1 2"   # 必须归一化后成功
report --begin-snap 999999                                      # 期望退出码 4
doctor
```
校验：TXT 首行含 `WORKLOAD REPOSITORY report for`；HTML 含 `<html`；global 含 `(RAC)`；
错误路径不留下半成品文件。

---

## 阶段 3：CLI 与输出契约

### T12 `[TDD]` 参数解析 — `src/AwrTool/Cli/CliParser.cs`
手写（不用 System.CommandLine，避免 AOT/依赖负担）。
支持 `--opt value` 和 `--opt=value`；子命令；未知选项/缺值/互斥 → `UsageException`。

### T13 `src/AwrTool/Output/ResultEnvelope.cs` + `JsonContext.cs`
源生成序列化。`[JsonSerializable]`，`JsonSourceGenerationOptions(WriteIndented = true)`。

### T14 `src/AwrTool/Commands/ReportCommand.cs` / `SnapshotsCommand.cs` / `DoctorCommand.cs`
编排：解析 → 解析凭据 → 连接 → 选快照 → 取报告 → 落盘 → 输出信封。

### T15 `src/AwrTool/Program.cs`
顶层异常兜底：所有异常映射到退出码 + 失败信封。保证任何路径都吐出合法 JSON。

### T16 `src/AwrTool/Cli/HelpText.cs`
`--help` 与错误时的用法提示。

---

## 阶段 4：发布与收尾

### T17 发布 R2R 单文件
`pwsh build/publish.ps1` → 实测体积与冷启动耗时。
再验证 `-p:AwrToolAot=true` 的报错路径是否给出清晰提示（本机缺工具链）。

### T18 README
安装、环境变量、AI 调用示例、退出码表、实测数据库事实。

### T19 代码评审 + 合并
对照本计划做评审，按严重度报告；确认测试全绿；给出合并选项。

---

## 已知未完成项（明确记录，不假装做了）

- **ASH 报告未实现**。`ash_report_text` 的参数是时间窗（`L_BTIME`/`L_ETIME` 为 `DATE`），
  实测传 `cast(sysdate-1/24 as date)` 报 `ORA-01843: 无效的月份`，需要进一步排查参数绑定方式。
- **AWR Diff 报告未实现**。函数可用（实测 722 KB / 15.5 s），但不在本次需求范围内。
- **NativeAOT 未经实机验证**。缺 MSVC 工具链，只验证到 fully-trimmed 可用。

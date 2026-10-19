using System.Text.Json;
using AwrTool.Cli;
using AwrTool.Core;
using AwrTool.Oracle;

namespace AwrTool.Output;

/// <summary>报告区间与生成参数。</summary>
public sealed record ReportSummary(
    ReportKind Type,
    ReportFormat Format,
    IReadOnlyList<int> Instances,
    int BeginSnap,
    int EndSnap,
    DateTime BeginTime,
    DateTime EndTime)
{
    /// <summary>报告实际覆盖的时长（秒）。</summary>
    public double ElapsedSeconds => (EndTime - BeginTime).TotalSeconds;
}

/// <summary>输出产物。<paramref name="Path"/> 为 <c>null</c> 表示写到了 stdout。</summary>
public sealed record OutputArtifact(string? Path, long Bytes, string? Sha256);

/// <summary>失败详情。</summary>
public sealed record ErrorSummary(string Code, string Message, int? Ora);

/// <summary>自检项。<paramref name="Status"/> 取 <c>ok</c> / <c>warn</c> / <c>fail</c>。</summary>
public sealed record DoctorCheck(string Name, string Status, string Detail);

/// <summary>
/// 输出给调用方的 JSON 信封。成功与失败共用同一结构，
/// 调用方只看 <c>ok</c> 和退出码即可判定。
/// </summary>
public sealed class ResultEnvelope
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }

    /// <summary>进程退出码。同时出现在 JSON 里，方便只看 stdout 的调用方判定。</summary>
    public ExitCode ExitCode { get; init; } = ExitCode.Ok;

    /// <summary>子命令名。</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>数据库身份信息。</summary>
    public DatabaseInfo? Db { get; init; }

    /// <summary>报告区间信息。</summary>
    public ReportSummary? Report { get; init; }

    /// <summary>输出产物信息。</summary>
    public OutputArtifact? Output { get; init; }

    /// <summary>失败详情。</summary>
    public ErrorSummary? Error { get; init; }

    /// <summary>快照列表（snapshots 命令）。</summary>
    public IReadOnlyList<SnapshotInfo>? Snapshots { get; init; }

    /// <summary>自检项（doctor 命令）。</summary>
    public IReadOnlyList<DoctorCheck>? Checks { get; init; }

    /// <summary>耗时毫秒。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>渲染成 JSON 文本。<paramref name="indented"/> 为 false 时输出紧凑格式（省 token）。</summary>
    public string ToJson(bool indented = true) => JsonOut.Serialize(Write, indented);

    private void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", Ok);
        writer.WriteNumber("exitCode", (int)ExitCode);
        writer.WriteString("tool", ToolInfo.FullName);
        writer.WriteString("command", Command);

        if (Db is { } db)
        {
            writer.WriteStartObject("db");
            writer.WriteString("name", db.Name);
            writer.WriteNumber("dbid", db.Dbid);
            writer.WriteNumber("instance", db.InstanceNumber);
            writer.WriteString("instanceName", db.InstanceName);
            writer.WriteString("host", db.HostName);
            writer.WriteString("version", db.Version);
            writer.WriteEndObject();
        }

        if (Report is { } report)
        {
            writer.WriteStartObject("report");
            writer.WriteString("type", report.Type == ReportKind.Global ? "global" : "awr");
            writer.WriteString("format", report.Format.ToExtension());

            writer.WriteStartArray("instances");
            foreach (var instance in report.Instances)
            {
                writer.WriteNumberValue(instance);
            }

            writer.WriteEndArray();

            writer.WriteNumber("beginSnap", report.BeginSnap);
            writer.WriteNumber("endSnap", report.EndSnap);
            JsonOut.WriteTimestamp(writer, "beginTime", report.BeginTime);
            JsonOut.WriteTimestamp(writer, "endTime", report.EndTime);
            writer.WriteNumber("elapsedSeconds", Math.Round(report.ElapsedSeconds, 3));
            writer.WriteEndObject();
        }

        if (Output is { } output)
        {
            writer.WriteStartObject("output");
            if (output.Path is null)
            {
                writer.WriteNull("path");
                writer.WriteBoolean("stdout", true);
            }
            else
            {
                writer.WriteString("path", output.Path);
                writer.WriteBoolean("stdout", false);
            }

            writer.WriteNumber("bytes", output.Bytes);

            if (output.Sha256 is not null)
            {
                writer.WriteString("sha256", output.Sha256);
            }

            writer.WriteEndObject();
        }

        if (Snapshots is { } snapshots)
        {
            writer.WriteStartArray("snapshots");
            foreach (var snapshot in snapshots)
            {
                writer.WriteStartObject();
                writer.WriteNumber("snapId", snapshot.SnapId);
                writer.WriteNumber("instance", snapshot.InstanceNumber);
                JsonOut.WriteTimestamp(writer, "beginTime", snapshot.BeginTime);
                JsonOut.WriteTimestamp(writer, "endTime", snapshot.EndTime);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (Checks is { } checks)
        {
            writer.WriteStartArray("checks");
            foreach (var check in checks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", check.Name);
                writer.WriteString("status", check.Status);
                writer.WriteString("detail", check.Detail);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (Error is { } error)
        {
            writer.WriteStartObject("error");
            writer.WriteString("code", error.Code);
            writer.WriteString("message", error.Message);

            if (error.Ora is int ora)
            {
                writer.WriteNumber("ora", ora);
            }

            writer.WriteEndObject();
        }

        writer.WriteNumber("elapsedMs", ElapsedMs);
        writer.WriteEndObject();
    }
}

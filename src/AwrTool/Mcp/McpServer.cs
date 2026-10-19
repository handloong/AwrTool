using System.Text.Json;
using AwrTool.Cli;
using AwrTool.Output;

namespace AwrTool.Mcp;

/// <summary>
/// MCP 的 stdio 传输实现：逐行读 JSON-RPC 2.0 请求，逐行写响应。
/// </summary>
/// <remarks>
/// <para>
/// 手写而不是引 <c>ModelContextProtocol</c> SDK：这里只用到 <c>initialize</c> /
/// <c>tools/list</c> / <c>tools/call</c> / <c>ping</c> 四个方法，协议面很小；
/// 自己写能把 stdio 的硬要求（stdout 只能有 JSON-RPC 消息）控制死，
/// 也避免把一整套依赖和反射式序列化拖进来。
/// </para>
/// <para>
/// <b>stdout 纯净性是硬约束</b>：任何非 JSON-RPC 的输出都会让客户端解析失败。
/// 所以日志一律走 <c>stderr</c>，协议层绝不往 <c>stdout</c> 写别的东西。
/// </para>
/// </remarks>
public sealed class McpServer
{
    /// <summary>本实现按此版本的语义编写。</summary>
    public const string DefaultProtocolVersion = "2025-06-18";

    // 声明「认识」的版本。客户端请求的版本在这个集合里就原样回声，
    // 这样新旧客户端都能接上；本工具用到的协议面在这些版本间是稳定的。
    private static readonly HashSet<string> KnownProtocolVersions = new(StringComparer.Ordinal)
    {
        "2026-07-28",
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly IMcpToolHost _host;

    /// <summary>构造。</summary>
    public McpServer(TextReader input, TextWriter output, TextWriter log, IMcpToolHost host)
    {
        _input = input;
        _output = output;
        _log = log;
        _host = host;
    }

    /// <summary>持续处理请求直到输入结束。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;

            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? reply;

            try
            {
                reply = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[mcp] 未处理的异常: {ex}");
                reply = ErrorJson(null, -32603, "内部错误：" + ex.Message);
            }

            if (reply is null)
            {
                continue;
            }

            await _output.WriteLineAsync(reply.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return ErrorJson(null, -32700, "JSON 解析失败：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return ErrorJson(null, -32600, "请求必须是 JSON 对象。");
            }

            var idJson = root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null
                ? idElement.GetRawText()
                : null;

            var method = root.TryGetProperty("method", out var methodElement)
                         && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;

            if (method is null)
            {
                return ErrorJson(idJson, -32600, "请求缺少 method。");
            }

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            switch (method)
            {
                case "initialize":
                    return ResultJson(idJson, writer => WriteInitialize(writer, parameters));

                case "notifications/initialized":
                case "notifications/cancelled":
                    // 通知没有响应体
                    return null;

                case "ping":
                    return ResultJson(idJson, _ => { });

                case "tools/list":
                    return ResultJson(idJson, writer => WriteToolList(writer));

                case "tools/call":
                    return await CallToolAsync(idJson, parameters, cancellationToken).ConfigureAwait(false);

                default:
                    return idJson is null ? null : ErrorJson(idJson, -32601, $"不支持的方法：{method}");
            }
        }
    }

    private void WriteInitialize(Utf8JsonWriter writer, JsonElement parameters)
    {
        var requested = ReadString(parameters, "protocolVersion");

        var negotiated = requested is not null && KnownProtocolVersions.Contains(requested)
            ? requested
            : DefaultProtocolVersion;

        var clientInfo = parameters.ValueKind == JsonValueKind.Object
                         && parameters.TryGetProperty("clientInfo", out var info)
            ? ReadString(info, "name") ?? "unknown"
            : "unknown";

        _log.WriteLine($"[mcp] initialize client={clientInfo} requested={requested ?? "<none>"} negotiated={negotiated}");

        writer.WriteString("protocolVersion", negotiated);

        writer.WriteStartObject("capabilities");
        writer.WriteStartObject("tools");
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject("serverInfo");
        writer.WriteString("name", ToolInfo.Name);
        writer.WriteString("title", "Oracle AWR 报告生成器");
        writer.WriteString("version", ToolInfo.Version);
        writer.WriteEndObject();

        writer.WriteString(
            "instructions",
            "提供 Oracle 性能诊断能力：执行只读查询、统计表行数、按时间范围生成 AWR 报告文件。" +
            "AWR 报告会写到本机文件，返回绝对路径，请用文件读取能力打开它再做分析。" +
            "所有 SQL 只允许单条 SELECT / WITH，写操作会被拒绝。");
    }

    private void WriteToolList(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("tools");

        foreach (var tool in _host.ListTools())
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("title", tool.Title);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");

            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            schema.RootElement.WriteTo(writer);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private async Task<string> CallToolAsync(
        string? idJson, JsonElement parameters, CancellationToken cancellationToken)
    {
        var name = ReadString(parameters, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            return ErrorJson(idJson, -32602, "tools/call 缺少 name。");
        }

        if (!_host.ListTools().Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)))
        {
            return ErrorJson(idJson, -32602, $"未知工具：{name}");
        }

        var arguments = parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("arguments", out var args)
            ? args
            : default;

        try
        {
            var result = await _host.CallAsync(name, arguments, cancellationToken).ConfigureAwait(false);

            return ResultJson(idJson, writer =>
            {
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", result.Text);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteBoolean("isError", result.IsError);
            });
        }
        catch (AwrToolException ex) when (ex.Code == ExitCode.Usage)
        {
            // 参数不合法按协议错误报，客户端能立刻纠正而不用重试
            return ErrorJson(idJson, -32602, ex.Message);
        }
        catch (AwrToolException ex)
        {
            _log.WriteLine($"[mcp] {name} 失败 code={ErrorCodes.ToWireName(ex.Code)}: {ex.Message}");
            return ToolFailure(idJson, ErrorCodes.ToWireName(ex.Code), ex.Message, ex.OraNumber);
        }
        catch (OperationCanceledException)
        {
            return ToolFailure(idJson, "CANCELLED", "调用被取消。", null);
        }
        catch (Exception ex)
        {
            _log.WriteLine($"[mcp] {name} 未预期异常: {ex}");
            return ToolFailure(idJson, "INTERNAL", "内部错误：" + ex.Message, null);
        }
    }

    private static string ResultJson(string? idJson, Action<Utf8JsonWriter> writeResult)
        => JsonOut.Serialize(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, idJson);

                writer.WriteStartObject("result");
                writeResult(writer);
                writer.WriteEndObject();

                writer.WriteEndObject();
            },
            indented: false);

    private static string ErrorJson(string? idJson, int code, string message)
        => JsonOut.Serialize(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, idJson);

                writer.WriteStartObject("error");
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();

                writer.WriteEndObject();
            },
            indented: false);

    /// <summary>工具执行期错误：按 MCP 约定用 <c>isError: true</c>，不是协议错误。</summary>
    private static string ToolFailure(string? idJson, string code, string message, int? ora)
    {
        var text = JsonOut.Serialize(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", false);
                writer.WriteStartObject("error");
                writer.WriteString("code", code);
                writer.WriteString("message", message);
                if (ora is int number)
                {
                    writer.WriteNumber("ora", number);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            indented: false);

        return ResultJson(
            idJson,
            writer =>
            {
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteBoolean("isError", true);
            });
    }

    private static void WriteId(Utf8JsonWriter writer, string? idJson)
    {
        writer.WritePropertyName("id");

        if (idJson is null)
        {
            writer.WriteNullValue();
            return;
        }

        // id 原文来自已解析的文档，本身就是合法 JSON，直接透传可保留数字/字符串类型
        writer.WriteRawValue(idJson, skipInputValidation: true);
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

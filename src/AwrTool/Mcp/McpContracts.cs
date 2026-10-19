using System.Text.Json;

namespace AwrTool.Mcp;

/// <summary>一个 MCP 工具的声明。</summary>
/// <param name="Name">工具名，唯一标识。</param>
/// <param name="Title">给人看的标题。</param>
/// <param name="Description">给模型看的说明，决定它什么时候会用这个工具。</param>
/// <param name="InputSchemaJson">JSON Schema 原文。</param>
public sealed record McpToolDefinition(string Name, string Title, string Description, string InputSchemaJson);

/// <summary>
/// 一次工具调用的结果。<see cref="Text"/> 约定为一个 JSON 对象字符串，
/// 成功与失败结构一致（<c>{"ok":true,...}</c> / <c>{"ok":false,"error":{...}}</c>），
/// 调用方不必为两种形态写两套解析。
/// </summary>
public sealed record McpToolResult(string Text, bool IsError);

/// <summary>
/// 工具执行入口。把 MCP 协议处理与实际能力分开，协议层就能脱离数据库单独测试。
/// </summary>
public interface IMcpToolHost
{
    /// <summary>声明全部可用工具。</summary>
    IReadOnlyList<McpToolDefinition> ListTools();

    /// <summary>调用一个工具。<paramref name="arguments"/> 可能是 Undefined（未传）。</summary>
    Task<McpToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}

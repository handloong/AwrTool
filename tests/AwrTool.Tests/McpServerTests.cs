using System.Text.Json;
using AwrTool.Cli;
using AwrTool.Mcp;
using Xunit;

namespace AwrTool.Tests;

public class McpServerTests
{
    private sealed class FakeHost : IMcpToolHost
    {
        public Func<string, JsonElement, Task<McpToolResult>>? Handler { get; set; }

        public List<McpToolDefinition> Tools { get; } =
        [
            new("echo", "Echo", "回显输入", """{"type":"object","properties":{"text":{"type":"string"}}}"""),
        ];

        public IReadOnlyList<McpToolDefinition> ListTools() => Tools;

        public Task<McpToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
            => Handler?.Invoke(name, arguments) ?? Task.FromResult(new McpToolResult("""{"ok":true}""", false));
    }

    private static async Task<JsonDocument[]> RunAsync(FakeHost host, params string[] lines)
    {
        var input = new StringReader(string.Join('\n', lines) + "\n");
        var output = new StringWriter();
        var log = new StringWriter();
        var server = new McpServer(input, output, log, host);

        await server.RunAsync();

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
    }

    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";

    [Fact]
    public async Task initialize_返回协议版本与工具能力()
    {
        var replies = await RunAsync(new FakeHost(), InitializeRequest);

        var result = replies[0].RootElement.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal(ToolInfo.Name, result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(1, replies[0].RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task initialize_认识旧版客户端请求的版本()
    {
        var replies = await RunAsync(
            new FakeHost(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");

        Assert.Equal(
            "2024-11-05",
            replies[0].RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task initialize_陌生版本回落到本实现版本()
    {
        var replies = await RunAsync(
            new FakeHost(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        Assert.Equal(
            McpServer.DefaultProtocolVersion,
            replies[0].RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task 通知不产生响应()
    {
        var replies = await RunAsync(
            new FakeHost(),
            InitializeRequest,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}""");

        Assert.Single(replies);
    }

    [Fact]
    public async Task ping_返回空结果()
    {
        var replies = await RunAsync(new FakeHost(), """{"jsonrpc":"2.0","id":7,"method":"ping"}""");

        Assert.Equal(7, replies[0].RootElement.GetProperty("id").GetInt32());
        Assert.True(replies[0].RootElement.GetProperty("result").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task tools_list_列出工具与schema()
    {
        var replies = await RunAsync(new FakeHost(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = replies[0].RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("echo", tools[0].GetProperty("name").GetString());
        Assert.Equal("object", tools[0].GetProperty("inputSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task tools_call_成功时返回text与isError_false()
    {
        var host = new FakeHost
        {
            Handler = (_, _) => Task.FromResult(new McpToolResult("""{"ok":true,"n":1}""", false)),
        };

        var replies = await RunAsync(
            host,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""");

        var result = replies[0].RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());

        var content = result.GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Contains("\"ok\":true", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task tools_call_未知工具是协议错误()
    {
        var replies = await RunAsync(
            new FakeHost(),
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"nope"}}""");

        Assert.Equal(-32602, replies[0].RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task tools_call_参数错误按协议错误返回()
    {
        var host = new FakeHost
        {
            Handler = (_, _) => throw AwrToolException.Usage("缺少必填参数 sql。"),
        };

        var replies = await RunAsync(
            host,
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");

        var error = replies[0].RootElement.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Contains("sql", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task tools_call_执行期错误是isError而不是协议错误()
    {
        var host = new FakeHost
        {
            Handler = (_, _) => throw new AwrToolException(ExitCode.ReportFailed, "报告生成失败：ORA-20019", 20019),
        };

        var replies = await RunAsync(
            host,
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");

        var root = replies[0].RootElement;
        Assert.False(root.TryGetProperty("error", out _));

        var result = root.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("REPORT_FAILED", text);
        Assert.Contains("20019", text);
        Assert.Contains("\"ok\":false", text);
    }

    [Fact]
    public async Task 非法JSON返回解析错误()
    {
        var replies = await RunAsync(new FakeHost(), "{not json");

        Assert.Equal(-32700, replies[0].RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 未知方法返回method_not_found()
    {
        var replies = await RunAsync(new FakeHost(), """{"jsonrpc":"2.0","id":9,"method":"resources/list"}""");

        Assert.Equal(-32601, replies[0].RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 字符串id原样保留()
    {
        var replies = await RunAsync(new FakeHost(), """{"jsonrpc":"2.0","id":"abc-1","method":"ping"}""");

        Assert.Equal(JsonValueKind.String, replies[0].RootElement.GetProperty("id").ValueKind);
        Assert.Equal("abc-1", replies[0].RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task 每行输出都是合法的JSON_RPC消息()
    {
        var replies = await RunAsync(
            new FakeHost(),
            InitializeRequest,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo"}}""",
            "",
            "   ");

        Assert.Equal(3, replies.Length);

        foreach (var reply in replies)
        {
            Assert.Equal("2.0", reply.RootElement.GetProperty("jsonrpc").GetString());
            Assert.True(reply.RootElement.TryGetProperty("id", out _));
        }
    }

    [Fact]
    public async Task 空行被忽略()
    {
        var replies = await RunAsync(new FakeHost(), "", "   ", """{"jsonrpc":"2.0","id":1,"method":"ping"}""");

        Assert.Single(replies);
    }
}

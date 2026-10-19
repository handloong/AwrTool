using System.Globalization;
using System.Text.Json;
using AwrTool.Cli;
using AwrTool.Core;

namespace AwrTool.Mcp;

/// <summary>
/// 从 <c>tools/call</c> 的 <c>arguments</c> 里取参数。
/// </summary>
/// <remarks>
/// 对类型有点宽容：整数既接受 JSON 数字也接受数字字符串，因为不同客户端
/// 在把模型输出转成 JSON 时的习惯不一致。但非法取值一律抛用法错误，
/// 不做静默兜底 —— 静默兜底会让调用方以为参数生效了。
/// </remarks>
public static class McpArguments
{
    /// <summary>取必填字符串。</summary>
    public static string RequireString(JsonElement arguments, string name)
    {
        var value = OptionalString(arguments, name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw AwrToolException.Usage($"缺少必填参数 {name}。");
        }

        return value;
    }

    /// <summary>取可选字符串。</summary>
    public static string? OptionalString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }

    /// <summary>取可选整数，缺省返回 <paramref name="fallback"/>，越界报错。</summary>
    public static int OptionalInt(JsonElement arguments, string name, int fallback, int min, int max)
        => OptionalIntOrNull(arguments, name) is int value ? EnsureRange(name, value, min, max) : fallback;

    /// <summary>取可选整数，缺省返回 <c>null</c>。</summary>
    public static int? OptionalIntOrNull(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw AwrToolException.Usage($"参数 {name} 需要一个整数。"),
        };
    }

    /// <summary>取可选布尔。</summary>
    public static bool OptionalBool(JsonElement arguments, string name, bool fallback)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw AwrToolException.Usage($"参数 {name} 需要一个布尔值。"),
        };
    }

    /// <summary>取可选时长，如 <c>2h</c>。</summary>
    public static TimeSpan? OptionalDuration(JsonElement arguments, string name)
    {
        var text = OptionalString(arguments, name);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return Duration.Parse(text);
        }
        catch (FormatException ex)
        {
            throw AwrToolException.Usage($"{name}: {ex.Message}", ex);
        }
    }

    /// <summary>取可选时间戳。</summary>
    public static DateTime? OptionalTimestamp(JsonElement arguments, string name)
    {
        var text = OptionalString(arguments, name);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Timestamp.Parse(text, name);
    }

    /// <summary>取可选实例号列表。</summary>
    public static InstanceList? OptionalInstances(JsonElement arguments, string name)
    {
        var text = OptionalString(arguments, name);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return InstanceList.Parse(text);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw AwrToolException.Usage($"{name}: {ex.Message}", ex);
        }
    }

    private static int EnsureRange(string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw AwrToolException.Usage($"参数 {name} 需要在 {min}..{max} 之间，收到 {value}。");
        }

        return value;
    }
}

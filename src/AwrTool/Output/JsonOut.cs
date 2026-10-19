using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AwrTool.Output;

/// <summary>
/// JSON 输出。全部用 <see cref="Utf8JsonWriter"/> 手写，不用 <c>JsonSerializer</c>。
/// </summary>
/// <remarks>
/// 手写而不是靠序列化框架的两个理由：
/// 一是查询结果的行是动态列，值类型千变万化，走反射序列化要么需要多态处理，
/// 要么把数字降级成字符串，两头都不讨好；
/// 二是 AI 客户端依赖这个 wire 格式，手写能把字段名、时间格式、数字精度钉死。
/// <see cref="Utf8JsonWriter"/> 负责转义，安全性不打折。
/// </remarks>
public static class JsonOut
{
    /// <summary>把写入动作渲染成 JSON 字符串。</summary>
    public static string Serialize(Action<Utf8JsonWriter> write, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(write);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = indented,

                   // 默认编码器会把中文转成 \uXXXX，既难读又白烧 token。
                   // 这里只需要能被 JSON 解析器正确读取，不往 HTML 里嵌入，
                   // 所以放宽转义（仍会正确转义引号、反斜杠和控制字符）是安全的。
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>写 ISO 风格时间戳：<c>yyyy-MM-ddTHH:mm:ss</c>。</summary>
    public static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTime value)
        => writer.WriteString(name, value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));

    /// <summary>
    /// 按运行时类型写一个单元格的值。数字保持数字，时间转 ISO 字符串，二进制转 base64。
    /// </summary>
    public static void WriteCell(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                writer.WriteNullValue();
                return;

            case string text:
                writer.WriteStringValue(text);
                return;

            case bool flag:
                writer.WriteBooleanValue(flag);
                return;

            case DateTime date:
                writer.WriteStringValue(date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                return;

            case DateTimeOffset offset:
                writer.WriteStringValue(offset.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture));
                return;

            case TimeSpan span:
                writer.WriteStringValue(span.ToString());
                return;

            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                return;

            case sbyte or byte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;

            case ulong unsigned:
                writer.WriteNumberValue(unsigned);
                return;

            case float or double or decimal:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                return;

            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }
}

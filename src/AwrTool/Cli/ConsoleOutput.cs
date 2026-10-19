using System.Security;
using System.Text;

namespace AwrTool.Cli;

/// <summary>stdout / stderr 的编码设置。</summary>
public static class ConsoleOutput
{
    /// <summary>UTF-8，不带 BOM。</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 把 stdout / stderr 固定为 UTF-8，并让 Windows 控制台按 UTF-8 解码。
    /// </summary>
    /// <remarks>
    /// <para>两个动作缺一不可：</para>
    /// <list type="number">
    /// <item>写出的字节固定成 UTF-8 —— 管道、文件、MCP 客户端都按 UTF-8 解析，这是硬要求。</item>
    /// <item>
    /// 把控制台代码页切到 65001 —— 否则简体中文 Windows 的 cmd 默认用 936(GBK) 去解码这些
    /// UTF-8 字节，中文就显示成「鈥斺€」这样的乱码。**这正是 <c>--help</c> 出乱码的原因**：
    /// 字节是对的，是控制台的解码方式不对。
    /// </item>
    /// </list>
    /// <para>
    /// 没有控制台句柄时（服务、完全重定向的环境）第 2 步会抛异常，忽略即可 —— 第 1 步仍然有效。
    /// </para>
    /// </remarks>
    public static void Configure()
    {
        TrySetConsoleEncoding(Utf8NoBom);

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true });
    }

    /// <summary>尽力把控制台编码切成指定值；失败返回 <c>false</c>，不影响主流程。</summary>
    public static bool TrySetConsoleEncoding(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        try
        {
            Console.OutputEncoding = encoding;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or PlatformNotSupportedException
                or SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>诊断用描述，出现在 <c>doctor</c> 的 console 检查项里。</summary>
    public static string Describe()
    {
        var target = Console.IsOutputRedirected ? "已重定向(管道/文件)" : "直接输出到控制台";
        var encoding = Console.Out.Encoding.WebName;
        var codePage = SafeCodePage();

        return $"{target}，输出编码={encoding}，控制台代码页={codePage}";
    }

    private static int SafeCodePage()
    {
        try
        {
            return Console.OutputEncoding.CodePage;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return 0;
        }
    }
}

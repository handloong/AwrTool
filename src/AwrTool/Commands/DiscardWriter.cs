using System.Text;

namespace AwrTool.Commands;

/// <summary>丢弃写入的 writer，只统计字符数。用于 doctor 里验证报告可生成而不落盘。</summary>
internal sealed class DiscardWriter : TextWriter
{
    public long CharsWritten { get; private set; }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => CharsWritten++;

    public override void Write(string? value) => CharsWritten += value?.Length ?? 0;

    public override void Write(ReadOnlySpan<char> buffer) => CharsWritten += buffer.Length;

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        CharsWritten += buffer.Length;
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        CharsWritten += value?.Length ?? 0;
        return Task.CompletedTask;
    }
}

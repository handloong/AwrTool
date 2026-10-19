using System.Security.Cryptography;
using System.Text;
using AwrTool.Cli;

namespace AwrTool.Output;

/// <summary>
/// 报告落盘。先写临时文件、成功后再改名，避免失败时留下半成品文件。
/// </summary>
public static class ReportSink
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>写出报告。<paramref name="targetPath"/> 为 <c>-</c> 时写 stdout。</summary>
    /// <param name="targetPath">目标路径，或 <c>-</c>。</param>
    /// <param name="overwrite">是否允许覆盖已存在文件。</param>
    /// <param name="body">实际的写入动作。</param>
    /// <param name="cancellationToken">取消标志。</param>
    public static async Task<OutputArtifact> WriteAsync(
        string targetPath,
        bool overwrite,
        Func<TextWriter, Task> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(body);

        if (targetPath == "-")
        {
            var counter = new CountingWriter(Console.Out);
            await body(counter).ConfigureAwait(false);
            await counter.FlushAsync(cancellationToken).ConfigureAwait(false);

            return new OutputArtifact(Path: null, counter.CharsWritten, Sha256: null);
        }

        var fullPath = Path.GetFullPath(targetPath);

        if (File.Exists(fullPath) && !overwrite)
        {
            throw new AwrToolException(
                ExitCode.OutputFailed,
                $"输出文件已存在：{fullPath}。加 --overwrite 允许覆盖。");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AwrToolException(ExitCode.OutputFailed, $"无法创建输出目录 {directory}：{ex.Message}", null, ex);
            }
        }

        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, Utf8NoBom, 64 * 1024))
            {
                await body(writer).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var bytes = new FileInfo(tempPath).Length;
            var sha = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);

            File.Move(tempPath, fullPath, overwrite: true);
            tempPath = string.Empty;

            return new OutputArtifact(fullPath, bytes, sha);
        }
        catch (AwrToolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AwrToolException(ExitCode.OutputFailed, $"写入输出文件失败：{ex.Message}", null, ex);
        }
        finally
        {
            // 失败路径上清掉半成品；成功时 tempPath 已被置空。
            if (tempPath.Length > 0 && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 清理失败不影响主流程的错误语义
                }
            }
        }
    }

    private static Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Task.FromResult(Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>统计写入字符数的透传 writer，用于 stdout 模式下报告体积。</summary>
    private sealed class CountingWriter(TextWriter inner) : TextWriter
    {
        public long CharsWritten { get; private set; }

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            CharsWritten++;
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            CharsWritten += value?.Length ?? 0;
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            inner.Write(buffer);
            CharsWritten += buffer.Length;
        }

        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            CharsWritten += buffer.Length;
        }

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteLineAsync(buffer, cancellationToken).ConfigureAwait(false);
            CharsWritten += buffer.Length + Environment.NewLine.Length;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    }
}

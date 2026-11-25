namespace HoleLauncher;

internal sealed class DownloadProgressFile(string path, Action<long> onProgress)
    : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true)
{
    private const int BufferSize = 64 * 1024;
    private long bytesWritten;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        bytesWritten += buffer.Length;
        onProgress(bytesWritten);
    }
}

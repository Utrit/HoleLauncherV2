using System.Buffers.Binary;
using System.Text;

namespace HoleLauncher;

internal static class EmbeddedBackend
{
    private static ReadOnlySpan<byte> Marker => "HOLELAUNCHER-BACKEND-V1"u8;
    private const int MaxAddressBytes = 2048;

    public static string? Read()
    {
        if (Environment.ProcessPath is not { } executable)
            return null;

        try
        {
            using var file = File.OpenRead(executable);
            Span<byte> footer = stackalloc byte[sizeof(int) + Marker.Length];
            if (file.Length < footer.Length)
                return null;
            file.Seek(-footer.Length, SeekOrigin.End);
            file.ReadExactly(footer);
            if (!footer[sizeof(int)..].SequenceEqual(Marker))
                return null;

            var length = BinaryPrimitives.ReadInt32LittleEndian(footer);
            if (length is <= 0 or > MaxAddressBytes || file.Length - footer.Length < length)
                throw new LauncherException("Некорректный адрес бэкенда в лаунчере.");

            file.Seek(-footer.Length - length, SeekOrigin.End);
            Span<byte> address = stackalloc byte[length];
            file.ReadExactly(address);
            return BackendConnection.ParseAddress(Encoding.UTF8.GetString(address), "бэкенда").AbsoluteUri;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

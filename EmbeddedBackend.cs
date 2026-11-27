using System.Buffers.Binary;
using System.Text;

namespace HoleLauncher;

internal static class EmbeddedBackend
{
    private static ReadOnlySpan<byte> Marker => "HOLELAUNCHER-BACKEND-V1"u8;
    private static ReadOnlySpan<byte> FingerprintMarker => "HOLELAUNCHER-SHA512-V1"u8;
    private const int MaxAddressBytes = 2048;
    private const int FingerprintBytes = 64;

    public static string? Read() => ReadPublication()?.Address;

    public static string? ReadFingerprint() => ReadPublication()?.Fingerprint;

    private static (string Address, string? Fingerprint)? ReadPublication()
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

            var addressOffset = file.Length - footer.Length - length;
            file.Position = addressOffset;
            Span<byte> address = stackalloc byte[length];
            file.ReadExactly(address);
            var origin = BackendConnection.ParseAddress(Encoding.UTF8.GetString(address), "бэкенда").AbsoluteUri;

            Span<byte> fingerprint = stackalloc byte[FingerprintMarker.Length + FingerprintBytes];
            if (addressOffset < fingerprint.Length)
                return (origin, null);

            file.Position = addressOffset - fingerprint.Length;
            file.ReadExactly(fingerprint);
            return (origin, fingerprint[..FingerprintMarker.Length].SequenceEqual(FingerprintMarker)
                ? Convert.ToHexStringLower(fingerprint[FingerprintMarker.Length..]) : null);
        }
        catch (IOException)
        {
            return null;
        }
    }
}

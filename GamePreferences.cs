using System.Runtime.InteropServices;

namespace HoleLauncher;

internal sealed record GamePreferences(int RamMiB = 0, string? JvmArguments = null,
    int ParallelDownloads = GamePreferences.DefaultParallelDownloads)
{
    public const int DefaultParallelDownloads = 5;
    public const int MaxParallelDownloads = 16;

    public int EffectiveParallelDownloads => ParallelDownloads <= 0
        ? DefaultParallelDownloads : Math.Min(ParallelDownloads, MaxParallelDownloads);

    private const int BytesPerMiB = 1024 * 1024;

    public static int AvailableRamMiB { get; } = ReadTotalRam();

    public int EffectiveRamMiB => RamMiB > 0
        ? Math.Clamp(RamMiB, 1, AvailableRamMiB)
        : Math.Clamp(AvailableRamMiB / 2, 1, 4096);

    private static int ReadTotalRam()
    {
        if (OperatingSystem.IsWindows())
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
                return (int)Math.Clamp(memory.TotalPhysical / BytesPerMiB, 1, int.MaxValue);
        }

        return (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / BytesPerMiB, 1, int.MaxValue);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

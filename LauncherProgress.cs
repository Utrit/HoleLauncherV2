using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Installers;

namespace HoleLauncher;

internal sealed record LauncherProgress(string Title, double? Fraction = null, string? Detail = null, bool ShowBar = true, BuildFile? Download = null,
    IReadOnlyList<LauncherProgress>? ActiveDownloads = null);

internal sealed class TransferProgress
{
    private static readonly TimeSpan ReportingInterval = TimeSpan.FromMilliseconds(250);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan lastUpdate;
    private long lastBytes;
    private double speed;

    public LauncherProgress? Update(string title, string file, long bytes, long? total, bool force = false)
    {
        if (bytes < lastBytes)
        {
            lastBytes = 0;
            speed = 0;
            lastUpdate = clock.Elapsed;
        }

        var now = clock.Elapsed;
        var duration = now - lastUpdate;
        if (duration < ReportingInterval && !force && bytes != total)
            return null;

        if (duration >= ReportingInterval)
        {
            speed = (bytes - lastBytes) / duration.TotalSeconds;
            lastUpdate = now;
            lastBytes = bytes;
        }

        double? fraction = total > 0 ? Math.Clamp((double)bytes / total.Value, 0, 1) : null;
        var amount = total is > 0
            ? $"{FormatBytes(bytes)} / {FormatBytes(total.Value)} · {fraction:P0}"
            : FormatBytes(bytes);

        if (speed > 0)
            amount += $" · {FormatBytes(speed)}/с";

        return new(title, fraction, string.IsNullOrEmpty(file) ? amount : $"{file} · {amount}");
    }

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000:0.#} ГБ",
        >= 1_000_000 => $"{bytes / 1_000_000:0.#} МБ",
        >= 1_000 => $"{bytes / 1_000:0.#} кБ",
        _ => $"{bytes:0} Б"
    };
}

internal sealed class InstallerProgress : IDisposable
{
    private readonly Action<LauncherProgress> report;
    private readonly string title;
    private TransferProgress transfer = new();
    private string file = "";
    private bool active = true;
    private bool showingInstallerOutput;
    private long totalBytes;

    public InstallerProgress(string title, Action<LauncherProgress> report)
    {
        this.title = title;
        this.report = report;
        Files = new Progress<InstallerProgressChangedEventArgs>(OnFilesChanged);
        Bytes = new Progress<ByteProgress>(OnBytesChanged);
        Output = new Progress<string>(OnInstallerOutput);
    }

    public IProgress<InstallerProgressChangedEventArgs> Files { get; }
    public IProgress<ByteProgress> Bytes { get; }
    public IProgress<string> Output { get; }

    private void OnFilesChanged(InstallerProgressChangedEventArgs value)
    {
        if (!active)
            return;

        file = value.Name ?? "";
        showingInstallerOutput = false;
        var fraction = value.TotalTasks > 0 ? Math.Clamp((double)value.ProgressedTasks / value.TotalTasks, 0, 1) : (double?)null;
        var details = value.TotalTasks > 0 ? $"{value.ProgressedTasks}/{value.TotalTasks} · {file} · {fraction:P0}" : file;
        report(new(title, fraction, details));
    }

    private void OnBytesChanged(ByteProgress value)
    {
        if (!active)
            return;

        if (totalBytes != value.TotalBytes)
        {
            totalBytes = value.TotalBytes;
            transfer = new();
        }

        showingInstallerOutput = false;
        if (transfer.Update(title, file, value.ProgressedBytes, value.TotalBytes) is { } update)
            report(update);
    }

    private void OnInstallerOutput(string line)
    {
        if (!active || showingInstallerOutput || string.IsNullOrWhiteSpace(line))
            return;

        showingInstallerOutput = true;
        report(new(title, Detail: "Установщик применяет изменения…"));
    }

    public void Dispose() => active = false;
}

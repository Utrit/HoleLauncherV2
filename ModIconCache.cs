using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace HoleLauncher;

internal sealed class ModIcon : INotifyPropertyChanged
{
    public Bitmap? Image { get; private set; }
    public bool HasImage => Image is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetImage(Bitmap image)
    {
        Image = image;
        PropertyChanged?.Invoke(this, new(nameof(Image)));
        PropertyChanged?.Invoke(this, new(nameof(HasImage)));
    }
}

internal sealed class ModIconCache(BackendConnection backend, HttpClient publicHttp) : IDisposable
{
    private const int IconWidth = 40;
    private const int ConcurrentDownloads = 4;
    private readonly Dictionary<string, ModIcon> icons = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim downloads = new(ConcurrentDownloads);
    private readonly CancellationTokenSource lifetime = new();

    public ModIcon? Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (icons.TryGetValue(url, out var existing))
            return existing;

        var icon = new ModIcon();
        icons.Add(url, icon);
        _ = LoadAsync(url, icon, lifetime.Token);
        return icon;
    }

    private async Task LoadAsync(string url, ModIcon icon, CancellationToken cancellation)
    {
        try
        {
            await downloads.WaitAsync(cancellation);
            try
            {
                var bytes = await backend.GetIconAsync(url, publicHttp, cancellation);
                if (bytes is null || cancellation.IsCancellationRequested)
                    return;

                using var stream = new MemoryStream(bytes, writable: false);
                icon.SetImage(Bitmap.DecodeToWidth(stream, IconWidth));
            }
            finally
            {
                downloads.Release();
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or
            ArgumentException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var icon in icons.Values)
            icon.Image?.Dispose();
        icons.Clear();
        lifetime.Dispose();
    }
}

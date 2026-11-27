using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace HoleLauncher;

internal sealed class ServerConsole : Window
{
    private const int MaxLogLines = 350;
    private const int MaxPendingLines = 700;
    private readonly ConcurrentQueue<string> incoming = new();
    private readonly Queue<string> lines = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox command = new() { PlaceholderText = "Команда сервера", IsEnabled = false };
    private volatile Process? process;

    public event Action? Started;

    public ServerConsole()
    {
        Title = "Консоль сервера";
        Width = 760;
        Height = 450;
        var send = new Button { Content = "Отправить", VerticalAlignment = VerticalAlignment.Center };
        var controls = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { command, send } };
        Grid.SetColumn(send, 1);
        var layout = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 8, Margin = new Thickness(12), Children = { log, controls } };
        Grid.SetRow(controls, 1);
        Content = layout;
        send.Click += async (_, _) => await SendCommandAsync();
        command.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter)
                return;
            args.Handled = true;
            await SendCommandAsync();
        };
        refresh.Tick += (_, _) => FlushLog();
        Closed += (_, _) => refresh.Stop();
        Closing += (_, args) =>
        {
            if (process is null || Owner?.IsVisible != true)
                return;
            args.Cancel = true;
            Hide();
        };
    }

    public void Attach(Process server)
    {
        process = server;
        server.OutputDataReceived += Capture;
        server.ErrorDataReceived += Capture;
        command.IsEnabled = true;
        refresh.Start();
        Started?.Invoke();
    }

    public void Detach()
    {
        if (process is { } server)
        {
            server.OutputDataReceived -= Capture;
            server.ErrorDataReceived -= Capture;
        }
        process = null;
        command.IsEnabled = false;
        FlushLog();
        refresh.Stop();
    }

    private void Capture(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not { } line)
            return;
        incoming.Enqueue(line);
        while (incoming.Count > MaxPendingLines)
            incoming.TryDequeue(out _);
    }

    private void FlushLog()
    {
        if (incoming.IsEmpty)
            return;
        while (incoming.TryDequeue(out var line))
        {
            lines.Enqueue(line);
            if (lines.Count > MaxLogLines)
                lines.Dequeue();
        }
        log.Text = string.Join(Environment.NewLine, lines);
        log.CaretIndex = log.Text.Length;
    }

    private async Task SendCommandAsync()
    {
        if (process is null || command.Text?.Trim() is not { Length: > 0 } value)
            return;
        command.Clear();
        try
        {
            await SendAsync(value);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            incoming.Enqueue("Сервер больше не принимает команды.");
        }
    }

    public async Task SendAsync(string value)
    {
        await writes.WaitAsync();
        try
        {
            if (process is { HasExited: false } server)
            {
                await server.StandardInput.WriteLineAsync(value);
                await server.StandardInput.FlushAsync();
            }
        }
        finally { writes.Release(); }
    }
}

using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HoleLauncher;

public sealed partial class MainWindow
{
    private bool updateAvailable;
    private ServerConsole? serverConsole;

    private async Task RefreshLauncherUpdateAsync()
    {
        if (EmbeddedBackend.Read() is not { } address)
            return;

        try
        {
            using var backend = new BackendConnection(new Uri(address));
            updateAvailable = await backend.IsLauncherUpdateAvailableAsync(lifetime.Token);
            LauncherUpdateNotice.IsVisible = updateAvailable && !CancelGameButton.IsVisible;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
    }

    private async void RegisterClicked(object? sender, RoutedEventArgs e) => await RunAsync(async cancellation =>
    {
        var user = UserName.Text?.Trim();
        if (string.IsNullOrEmpty(user))
            throw new LauncherException("Введите логин.");

        using var backend = new BackendConnection(BackendConnection.ParseAddress(BackendAddress.Text, "бэкенда"));
        ShowProgress(new("Регистрируем аккаунт…"));
        if (await backend.RegisterAsync(user, Password.Text ?? "", cancellation))
            await SignInAsync(cancellation);
        else
            ShowStatus("Аккаунт ещё готовится. Войдите через несколько секунд.");
    });

    private bool restoringTextureModel;

    private async void SkinModelChanged(object? sender, RoutedEventArgs e)
    {
        if (restoringTextureModel || SettingsLayer?.IsVisible != true || busy ||
            connection is not { } backend || session is not { } current ||
            previewProfile is not { SkinFileId: { } id } profile)
            return;

        var model = TextureModel.IsChecked == true ? "slim" : "classic";
        if (model == profile.SkinModel)
            return;

        await RunAsync(async cancellation =>
        {
            TextureSettings.IsEnabled = false;
            SettingsStatus.Text = "Сохраняем модель скина…";
            var saved = false;
            try
            {
                session = await backend.EnsureGameSessionAsync(current, settings.UserName, Password.Text ?? "", cancellation);
                await backend.SetSkinModelAsync(id, model, cancellation);
                saved = true;
                PlayerPreview.SetModel(model);
                previewProfile = profile with { SkinModel = model };
                SettingsStatus.Text = "Модель скина сохранена.";
            }
            finally
            {
                TextureSettings.IsEnabled = true;
                if (!saved)
                {
                    restoringTextureModel = true;
                    TextureModel.IsChecked = profile.SkinModel == "slim";
                    restoringTextureModel = false;
                }
            }
        });
    }

    private async void ChangeSkinClicked(object? sender, RoutedEventArgs e) => await PickTextureAsync(skin: true);

    private async void ChangeCapeClicked(object? sender, RoutedEventArgs e) => await PickTextureAsync(skin: false);

    private Task PickTextureAsync(bool skin) => RunAsync(async cancellation =>
    {
        if (connection is not { } backend || session is not { } current)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = skin ? "Выберите скин" : "Выберите плащ",
            FileTypeFilter = [FilePickerFileTypes.ImagePng]
        });
        if (files.Count == 0)
            return;

        using var file = files[0];
        await using var input = await file.OpenReadAsync();
        SettingsStatus.Text = "Загружаем текстуру…";
        session = await backend.EnsureGameSessionAsync(current, settings.UserName, Password.Text ?? "", cancellation);
        await backend.SetTextureAsync(skin, TextureModel.IsChecked == true ? "slim" : "classic", file.Name, input, cancellation);
        await RefreshProfileAsync(backend, cancellation);
        SettingsStatus.Text = skin ? "Скин сохранён." : "Плащ сохранён.";
    });

    private IReadOnlyDictionary<string, string[]>? UnmanagedFor(PublishedBuild build) =>
        settings.UnmanagedMods?.GetValueOrDefault(build.BuildId);

    private async void ModManagementClicked(object? sender, RoutedEventArgs e)
    {
        if (!settings.DeveloperMode || sender is not Avalonia.Controls.Button { DataContext: BuildFileRow { SelectionKey: { } key } } ||
            Builds.SelectedItem is not PublishedBuild build)
            return;

        await RunAsync(async cancellation =>
        {
            var overrides = new Dictionary<Guid, Dictionary<string, string[]>>(settings.UnmanagedMods ?? new());
            var files = overrides.GetValueOrDefault(build.BuildId) is { } existing
                ? new Dictionary<string, string[]>(existing, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            if (!files.Remove(key))
            {
                files[key] = catalog.Append(build).Where(version => version.BuildId == build.BuildId)
                    .SelectMany(version => version.Files)
                    .Where(file => BuildSelection.Key(file) == key).Select(file => file.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            if (files.Count == 0)
                overrides.Remove(build.BuildId);
            else
                overrides[build.BuildId] = files;

            settings = settings with { UnmanagedMods = overrides };
            await CheckBuildFilesAsync();
            if (!await SaveSettingsAsync(cancellation))
                ShowStatus("Не удалось сохранить режим управления модом.");
        });
    }

    private ServerConsole CreateServerConsole()
    {
        serverConsole?.Close();
        var window = new ServerConsole();
        window.Started += () =>
        {
            ServerConsoleButton.IsVisible = true;
            ShowServerConsoleClicked(null, new RoutedEventArgs());
        };
        window.Closed += (_, _) =>
        {
            if (serverConsole == window)
            {
                serverConsole = null;
                ServerConsoleButton.IsVisible = false;
            }
        };
        return serverConsole = window;
    }

    private void ShowServerConsoleClicked(object? sender, RoutedEventArgs e)
    {
        if (serverConsole is not { } console)
            return;
        if (!console.IsVisible)
            console.Show(this);
        console.Activate();
    }
}

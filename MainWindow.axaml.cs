using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;

namespace HoleLauncher;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource lifetime = new();
    private readonly LauncherSettingsStore settingsStore;
    private readonly GameLauncher launcher;
    private readonly HttpClient publicHttp;
    private readonly bool serverMode;
    private bool serverRunning;
    private bool closeAfterServer;
    private readonly Dictionary<BuildFile, LocalFileState> fileStates = [];
    private LauncherSettings settings = new();
    private BackendConnection? connection;
    private DraslSession? session;
    private PublishedBuild[] catalog = [];
    private string? catalogSnapshot;
    private Guid? selectedBuildId;
    private GameProfile? previewProfile;
    private CancellationTokenSource? fileCheck;
    private CancellationTokenSource? prepareGame;
    private ModIconCache? modIcons;
    private readonly Dictionary<BuildFile, (Border Card, TextBlock Detail)> downloadCards = [];
    private bool busy;

    internal MainWindow(LauncherSettingsStore settingsStore, GameLauncher launcher, HttpClient publicHttp, bool serverMode = false)
    {
        this.settingsStore = settingsStore;
        this.launcher = launcher;
        this.publicHttp = publicHttp;
        this.serverMode = serverMode;
        InitializeComponent();
        TextureSettings.IsVisible = !serverMode;
        TextureModel.ItemsSource = new[] { "Classic", "Slim" };
        if (serverMode)
        {
            Title = "HoleLauncher · Сервер";
            ProfilePreviewPanel.IsVisible = false;
            ServerPanel.IsVisible = true;
            PlayButton.Content = "Запустить сервер";
            Closing += (_, e) =>
            {
                if (prepareGame is null)
                    return;

                e.Cancel = true;
                closeAfterServer = true;
                CancelGameClicked(null, new RoutedEventArgs());
            };
        }
        Opened += (_, _) =>
        {
            InitializeAsync();
            _ = RefreshWhileOpenAsync();
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            connection?.Dispose();
            modIcons?.Dispose();
        };
        ParallelDownloads.ItemsSource = Enumerable.Range(1, GamePreferences.MaxParallelDownloads).ToArray();
        RamSlider.Maximum = GamePreferences.AvailableRamMiB;
        RamMaximum.Text = FormatRam(GamePreferences.AvailableRamMiB);
        RamSlider.ValueChanged += (_, _) => ShowRamValue();
    }

    private async void InitializeAsync() => await RunAsync(async cancellation =>
    {
        try
        {
            settings = await settingsStore.LoadAsync(cancellation);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowStatus("Не удалось прочитать настройки лаунчера.");
        }

        if (EmbeddedBackend.Read() is { } embedded)
        {
            if (!string.Equals(settings.BackendAddress, embedded, StringComparison.Ordinal))
                settings = settings with { BackendAddress = embedded, ProtectedPassword = null };
            BackendAddressLabel.IsVisible = BackendAddress.IsVisible = false;
        }

        ShowServerPaths();
        BackendAddress.Text = settings.BackendAddress;
        _ = RefreshLauncherUpdateAsync();
        UserName.Text = settings.UserName;
        if (settings.ProtectedPassword is null || !OperatingSystem.IsWindows())
            return;

        try
        {
            Password.Text = settings.ReadPassword();
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            ShowStatus("Введите пароль заново.");
            return;
        }

        await SignInAsync(cancellation);
    });

    private async void LoginClicked(object? sender, RoutedEventArgs e) => await RunAsync(SignInAsync);

    private async Task SignInAsync(CancellationToken cancellation)
    {
        var user = UserName.Text?.Trim();
        if (string.IsNullOrEmpty(user))
            throw new LauncherException("Введите логин.");

        var address = BackendConnection.ParseAddress(BackendAddress.Text, "бэкенда");
        var password = Password.Text ?? "";
        var authenticated = new BackendConnection(address);
        ShowProgress(new("Вход…"));
        try
        {
            session = await authenticated.LoginAsync(user, password, cancellation);
        }
        catch
        {
            authenticated.Dispose();
            throw;
        }

        connection = authenticated;
        modIcons = new ModIconCache(authenticated, publicHttp);
        LoginPanel.IsVisible = false;
        MainPanel.IsVisible = true;
        FullList.IsChecked = serverMode;
        TreeViewMode.IsChecked = false;
        GameName.Text = session.SelectedProfile.Name;
        settings = (settings with { BackendAddress = address.AbsoluteUri, UserName = user }).WithPassword(password);
        var saved = await SaveSettingsAsync(cancellation);

        ShowProgress(new("Загружаем профиль и сборки…"));
        if (!serverMode)
            await PlayerPreview.LoadGeometryAsync();
        await RefreshAsync(authenticated, cancellation);
        ShowStatus(!saved ? "Не удалось сохранить настройки лаунчера." :
            Builds.ItemCount == 0 ? "Нет доступных сборок." : "");
    }

    private async Task RefreshWhileOpenAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (busy)
                    continue;
                await RefreshLauncherUpdateAsync();
                if (connection is { } backend && !busy)
                    await RefreshAsync(backend, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task RefreshAsync(BackendConnection backend, CancellationToken cancellation)
    {
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        refresh.CancelAfter(RefreshTimeout);
        if (serverMode)
            await RefreshSectionAsync(RefreshCatalogAsync);
        else
            await Task.WhenAll(RefreshSectionAsync(RefreshCatalogAsync), RefreshSectionAsync(RefreshProfileAsync));

        async Task RefreshSectionAsync(Func<BackendConnection, CancellationToken, Task> update)
        {
            try { await update(backend, refresh.Token); }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                cancellation.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task RefreshCatalogAsync(BackendConnection backend, CancellationToken cancellation)
    {
        var builds = await backend.GetBuildsAsync(cancellation);
        if (cancellation.IsCancellationRequested || connection != backend || prepareGame is not null)
            return;

        var snapshot = JsonSerializer.Serialize(builds);
        if (catalogSnapshot == snapshot)
            return;

        var previousLatest = LatestVersion(catalog, selectedBuildId);
        var currentLatest = LatestVersion(builds, selectedBuildId);
        catalog = builds;
        catalogSnapshot = snapshot;
        ShowBuilds(preferLatest: currentLatest > previousLatest);
        if (Builds.ItemCount == 0)
            ShowStatus("Нет доступных сборок.");
        else if (Status.Text == "Нет доступных сборок.")
            ShowStatus("");
    }

    private async Task RefreshProfileAsync(BackendConnection backend, CancellationToken cancellation)
    {
        var profile = await backend.GetGameProfileAsync(cancellation);
        if (profile is null || cancellation.IsCancellationRequested || connection != backend || session is not { } current)
            return;

        if (!string.IsNullOrWhiteSpace(profile.GameName) && current.SelectedProfile.Name != profile.GameName)
        {
            session = current with { SelectedProfile = current.SelectedProfile with { Name = profile.GameName } };
            GameName.Text = profile.GameName;
        }

        var previous = previewProfile;
        if (previous?.SkinModel != profile.SkinModel)
            PlayerPreview.SetModel(profile.SkinModel);

        var skin = RefreshTextureAsync(profile.SkinFileId, previous?.SkinFileId, backend.GetSkinAsync, PlayerPreview.SetSkin);
        var cape = RefreshTextureAsync(profile.CapeFileId, previous?.CapeFileId, backend.GetCapeAsync, PlayerPreview.SetCape);
        await Task.WhenAll(skin, cape);
        if (!cancellation.IsCancellationRequested && connection == backend)
            previewProfile = profile with { SkinFileId = skin.Result, CapeFileId = cape.Result };

        async Task<Guid?> RefreshTextureAsync(Guid? id, Guid? applied, Func<Guid, CancellationToken, Task<byte[]?>> download,
            Action<byte[]?> apply)
        {
            if (id == applied)
                return applied;

            byte[]? bytes;
            try { bytes = id is { } fileId ? await download(fileId, cancellation) : null; }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                return applied;
            }
            if (cancellation.IsCancellationRequested || connection != backend || (id is not null && bytes is null))
                return applied;

            apply(bytes);
            return id;
        }
    }

    private async void LogoutClicked(object? sender, RoutedEventArgs e) => await RunAsync(async cancellation =>
    {
        var backend = connection;
        var currentSession = session;
        connection = null;
        session = null;
        fileCheck?.Cancel();
        catalog = [];
        catalogSnapshot = null;
        selectedBuildId = null;
        previewProfile = null;
        fileStates.Clear();
        ShowBuilds();
        SetDownloads([]);
        modIcons?.Dispose();
        modIcons = null;
        MainPanel.IsVisible = false;
        LoginPanel.IsVisible = true;
        Password.Clear();
        PlayerPreview.SetModel("classic");
        PlayerPreview.SetSkin(null);
        PlayerPreview.SetCape(null);
        settings = settings with { ProtectedPassword = null };
        var saved = await SaveSettingsAsync(cancellation);
        using (backend)
        {
            if (backend is not null && currentSession is not null)
                await backend.LogoutAsync(currentSession, cancellation);
        }
        ShowStatus(saved ? "" : "Не удалось обновить настройки лаунчера.");
    });

    private async void PlayClicked(object? sender, RoutedEventArgs e) => await RunAsync(async cancellation =>
    {
        if (Builds.SelectedItem is not PublishedBuild build || connection is not { } backend || session is not { } gameSession)
            throw new LauncherException("Выберите сборку.");

        var jar = settings.ServerJar;
        if (serverMode && jar is null)
            throw new LauncherException("Выберите JAR серверного ядра.");

        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        prepareGame = preparation;
        CancelGameButton.Content = "Отмена";
        CancelGameButton.IsVisible = CancelGameButton.IsEnabled = true;
        LauncherUpdateNotice.IsVisible = false;
        var showGameProgress = true;
        var progress = new Progress<LauncherProgress>(update =>
        {
            if (!showGameProgress || preparation.IsCancellationRequested)
                return;
            serverRunning = serverMode && !update.ShowBar;
            CancelGameButton.IsVisible = update.ShowBar || serverRunning;
            CancelGameButton.Content = serverRunning ? "Остановить" : "Отмена";
            LauncherUpdateNotice.IsVisible = updateAvailable && !CancelGameButton.IsVisible;
            ShowProgress(update);
        });
        try
        {
            var preferences = settings.Preferences ?? new();
            var optional = OptionalFor(build);
            var unmanaged = UnmanagedFor(build);
            if (!serverMode)
            {
                ShowProgress(new("Проверяем игровую сессию…"));
                gameSession = await backend.EnsureGameSessionAsync(gameSession, settings.UserName, Password.Text ?? "", preparation.Token);
                session = gameSession;
                GameName.Text = gameSession.SelectedProfile.Name;
            }
            var code = serverMode
                ? await launcher.RunServerAsync(backend, build, jar!, settings.ServerJava, preferences, progress,
                    preparation.Token, optional, catalog, unmanaged, CreateServerConsole())
                : await launcher.PlayAsync(backend, build, gameSession, preferences, progress, preparation.Token, catalog, optional, unmanaged);
            ShowStatus(serverMode
                ? code == 0 ? "Сервер остановлен." : $"Сервер завершился с кодом {code}. Проверьте консоль сервера."
                : code == 0 ? "Игра завершена." : $"Minecraft завершился с кодом {code}.");
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            ShowStatus(serverMode ? "Подготовка сервера отменена." : "Подготовка игры отменена.");
        }
        finally
        {
            showGameProgress = false;
            prepareGame = null;
            serverRunning = false;
            CancelGameButton.IsVisible = false;
            LauncherUpdateNotice.IsVisible = updateAvailable;
            SetDownloads([]);
            if (closeAfterServer)
                Close();
            else if (!cancellation.IsCancellationRequested)
                await CheckBuildFilesAsync();
        }
    });

    private void CancelGameClicked(object? sender, RoutedEventArgs e)
    {
        CancelGameButton.IsEnabled = false;
        prepareGame?.Cancel();
        ShowStatus(serverRunning ? "Останавливаем сервер…" :
            serverMode ? "Отменяем подготовку сервера…" : "Отменяем подготовку игры…");
    }

    private void ShowBuilds(bool preferLatest = false)
    {
        var previous = Builds.SelectedItem as PublishedBuild;
        var visible = VisibleBuilds(catalog).ToArray();
        var buildId = selectedBuildId ?? visible.FirstOrDefault()?.BuildId;
        var versions = visible.Where(build => build.BuildId == buildId).ToArray();
        var selection = preferLatest || previous is null ? null : versions.FirstOrDefault(build =>
            build.Build.Version == previous.Build.Version && build.Test == previous.Test);
        selection ??= versions.OrderByDescending(VersionNumber).ThenBy(build => build.Test).FirstOrDefault();

        Builds.ItemsSource = visible;
        Builds.SelectedItem = selection;
        UpdatePlayAvailability();
        ShowFiles();
    }

    private IEnumerable<PublishedBuild> VisibleBuilds(IEnumerable<PublishedBuild> builds) =>
        builds.Where(build => settings.DeveloperMode || !build.Test);

    private long LatestVersion(IEnumerable<PublishedBuild> builds, Guid? buildId) =>
        VisibleBuilds(builds).Where(build => build.BuildId == buildId).Select(VersionNumber).DefaultIfEmpty().Max();

    private static long VersionNumber(PublishedBuild build) =>
        long.TryParse(build.Build.Version, out var version) ? version : 0;

    private async void BuildSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Builds.SelectedItem is PublishedBuild build)
            selectedBuildId = build.BuildId;
        if (PlayButton is not null)
            UpdatePlayAvailability();
        await CheckBuildFilesAsync();
    }

    private async Task CheckBuildFilesAsync()
    {
        fileCheck?.Cancel();
        fileStates.Clear();
        ShowFiles();
        if (Builds.SelectedItem is not PublishedBuild build || connection is not { } backend)
            return;

        using var check = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        fileCheck = check;
        try
        {
            var instance = new InstanceFiles(publicHttp, backend, build, catalog, serverMode: serverMode);
            var states = new Dictionary<BuildFile, LocalFileState>();
            var unmanaged = UnmanagedFor(build);
            foreach (var file in build.Files.Where(file => BuildSelection.IsForSide(file, serverMode) &&
                unmanaged?.ContainsKey(BuildSelection.Key(file)) != true))
            {
                check.Token.ThrowIfCancellationRequested();
                try { states[file] = await instance.GetStateAsync(file, check.Token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or LauncherException)
                {
                    states[file] = LocalFileState.Unavailable;
                }
            }

            if (check.IsCancellationRequested)
                return;
            foreach (var (file, state) in states)
                fileStates[file] = state;
            ShowFiles();
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        catch (LauncherException)
        {
            ShowStatus("Сборка содержит некорректный путь.");
        }
        finally
        {
            if (ReferenceEquals(fileCheck, check))
                fileCheck = null;
        }
    }

    private void FileViewChanged(object? sender, RoutedEventArgs e) => ShowFiles();

    private void ShowFiles()
    {
        if (FlatFiles is null || FileTree is null || Builds is null)
            return;

        var tree = TreeViewMode.IsChecked == true;
        var full = FullList.IsChecked == true;
        var build = Builds.SelectedItem as PublishedBuild;
        var selected = build is null ? new HashSet<string>() : OptionalFor(build);
        var graph = build is null ? null : new BuildSelection(build, selected, serverMode);
        var unmanaged = build is null ? null : UnmanagedFor(build);
        var files = (graph?.Roots() ?? [])
            .Where(file => full || file.IsOptional)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file => (file.Path, Row: CreateRow(file, tree, selectable: true)))
            .ToArray();

        BuildFileRow CreateRow(BuildFile file, bool compact, bool selectable)
        {
            var key = BuildSelection.Key(file);
            var excluded = unmanaged?.ContainsKey(key) == true;
            var row = BuildFileRow.Leaf(file, fileStates.GetValueOrDefault(file), modIcons?.Get(file.Card?.IconUrl));
            return row with
            {
                CanOverride = settings.DeveloperMode,
                Unmanaged = excluded,
                Status = excluded ? "Ручное управление" : row.Status,
                Compact = compact,
                CanSelect = selectable && file.IsOptional && !excluded,
                Selected = selected.Contains(key),
                SelectionKey = key,
                Dependencies = selectable ? graph!.Dependencies(file).Select(dep => CreateRow(dep, true, false)).ToArray() : []
            };
        }

        FlatFiles.IsVisible = !tree && files.Length > 0;
        FileTree.IsVisible = tree && files.Length > 0;
        FileTree.ItemsSource = tree ? GroupFiles(files) : null;
        FileList.ItemsSource = tree ? null : files.Select(item => item.Row).ToArray();
    }

    private HashSet<string> OptionalFor(PublishedBuild build) =>
        new(settings.OptionalMods?.GetValueOrDefault(build.BuildId) ?? [], StringComparer.Ordinal);

    private async void OptionalClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: BuildFileRow { SelectionKey: { } key } row } box ||
            Builds.SelectedItem is not PublishedBuild build)
            return;

        await RunAsync(async cancellation =>
        {
            var selected = OptionalFor(build);
            if (box.IsChecked == true)
                selected.Add(key);
            else
                selected.Remove(key);
            row.Selected = box.IsChecked == true;
            var choices = new Dictionary<Guid, string[]>(settings.OptionalMods ?? new());
            choices[build.BuildId] = [.. selected];
            settings = settings with { OptionalMods = choices };
            if (!await SaveSettingsAsync(cancellation))
                ShowStatus("Не удалось сохранить выбор модов.");
        });
    }

    private static BuildFileRow[] GroupFiles(IEnumerable<(string Path, BuildFileRow Row)> files)
    {
        var materialized = files.ToArray();
        var folders = materialized.Where(item => item.Path.Contains('/'))
            .GroupBy(item => item.Path[..item.Path.IndexOf('/')], StringComparer.OrdinalIgnoreCase)
            .Select(group => new BuildFileRow(group.Key,
                GroupFiles(group.Select(item => (item.Path[(item.Path.IndexOf('/') + 1)..], item.Row)))));
        return [.. folders, .. materialized.Where(item => !item.Path.Contains('/')).Select(item => item.Row)];
    }

    private void UpdatePlayAvailability() =>
        PlayButton.IsEnabled = Builds.SelectedItem is not null && (!serverMode || settings.ServerJar is not null);

    private void ShowServerPaths()
    {
        if (!serverMode)
            return;

        ServerJarName.Text = settings.ServerJar is { } jar ? Path.GetFileName(jar) : "JAR не выбран";
        ToolTip.SetTip(ServerJarName, settings.ServerJar);
        ServerJavaName.Text = settings.ServerJava ?? "Автоматический выбор";
        UpdatePlayAvailability();
    }

    private async void ChooseServerJar(object? sender, RoutedEventArgs e) => await ChooseServerFileAsync(java: false);

    private async void ChooseServerJava(object? sender, RoutedEventArgs e) => await ChooseServerFileAsync(java: true);

    private Task ChooseServerFileAsync(bool java) => RunAsync(async cancellation =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = java ? "Выберите исполняемый файл Java" : "Выберите JAR серверного ядра",
            FileTypeFilter = [new FilePickerFileType(java ? "Java" : "JAR")
            {
                Patterns = java ? [OperatingSystem.IsWindows() ? "java.exe" : "java"] : ["*.jar"]
            }]
        });
        using var file = files.FirstOrDefault();
        if (file?.TryGetLocalPath() is not { } path)
            return;

        settings = java ? settings with { ServerJava = path } : settings with { ServerJar = path };
        await SaveServerPathsAsync(cancellation);
    });

    private async void ResetServerJava(object? sender, RoutedEventArgs e) => await RunAsync(async cancellation =>
    {
        settings = settings with { ServerJava = null };
        await SaveServerPathsAsync(cancellation);
    });

    private async Task SaveServerPathsAsync(CancellationToken cancellation)
    {
        ShowServerPaths();
        ShowStatus(await SaveSettingsAsync(cancellation) ? "" : "Не удалось сохранить настройки сервера.");
    }

    private async void OpenServerFolder(object? sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (Builds.SelectedItem is not PublishedBuild build || connection is not { } backend)
            return;

        var folder = new InstanceFiles(publicHttp, backend, build, serverMode: true).Root;
        Directory.CreateDirectory(folder);
        await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    });

    private void SettingsClicked(object? sender, RoutedEventArgs e)
    {
        var preferences = settings.Preferences ?? new();
        RamSlider.Value = Math.Clamp(preferences.RamMiB, 0, GamePreferences.AvailableRamMiB);
        JvmArguments.Text = preferences.JvmArguments;
        ParallelDownloads.SelectedItem = preferences.EffectiveParallelDownloads;
        DeveloperMode.IsChecked = settings.DeveloperMode;
        TextureModel.SelectedIndex = previewProfile?.SkinModel == "slim" ? 1 : 0;
        SettingsStatus.Text = null;
        ShowRamValue();
        MainPanel.IsEnabled = false;
        SettingsLayer.IsVisible = true;
    }

    private async void SettingsDoneClicked(object? sender, RoutedEventArgs e) => await RunAsync(async cancellation =>
    {
        var developerModeChanged = settings.DeveloperMode != (DeveloperMode.IsChecked == true);
        settings = settings with
        {
            DeveloperMode = DeveloperMode.IsChecked == true,
            Preferences = new GamePreferences((int)Math.Round(RamSlider.Value), JvmArguments.Text?.Trim(),
                ParallelDownloads.SelectedItem is int count ? count : GamePreferences.DefaultParallelDownloads)
        };

        SettingsLayer.IsVisible = false;
        if (developerModeChanged)
            ShowBuilds();
        if (!await SaveSettingsAsync(cancellation))
            ShowStatus("Не удалось сохранить настройки лаунчера.");
    });

    private void ShowRamValue()
    {
        var miB = (int)Math.Round(RamSlider.Value);
        RamValue.Text = miB == 0 ? "Авто" : FormatRam(miB);
    }

    private static string FormatRam(int miB) => miB < 1024 ? $"{miB} МиБ" : $"{miB / 1024d:0.##} ГиБ";

    private async Task<bool> SaveSettingsAsync(CancellationToken cancellation)
    {
        try
        {
            await settingsStore.SaveAsync(settings, cancellation);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy)
            return;

        busy = true;
        LoginPanel.IsEnabled = MainPanel.IsEnabled = false;
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (LauncherException error) { ShowStatus(error.Message); }
        catch (HttpRequestException error) when (error.StatusCode is { } status)
        {
            ShowStatus($"Запрос завершился с ошибкой HTTP {(int)status}.");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            ShowStatus("Не удалось получить данные с сервера. Проверьте адрес и подключение.");
        }
        catch (Exception error) { ShowStatus($"Операция не завершилась ({error.GetType().Name})."); }
        finally
        {
            LoginProgress.IsVisible = LoadProgress.IsVisible = ProgressDetail.IsVisible = false;
            LoginPanel.IsEnabled = true;
            MainPanel.IsEnabled = !SettingsLayer.IsVisible;
            busy = false;
        }
    }

    private void ShowStatus(string text)
    {
        if (SettingsLayer.IsVisible)
            SettingsStatus.Text = text;
        else if (LoginPanel.IsVisible)
            LoginStatus.Text = text;
        else
            Status.Text = text;

        LoginProgress.IsVisible = LoadProgress.IsVisible = ProgressDetail.IsVisible = false;
        SetDownloads([]);
    }

    private void ShowProgress(LauncherProgress progress)
    {
        if (LoginPanel.IsVisible)
        {
            LoginStatus.Text = progress.Title;
            LoginProgress.IsVisible = progress.ShowBar;
            return;
        }

        Status.Text = progress.Title;
        ProgressDetail.Text = progress.Detail;
        ProgressDetail.IsVisible = progress.Download is null && !string.IsNullOrWhiteSpace(progress.Detail);
        LoadProgress.IsVisible = progress.ShowBar;
        LoadProgress.IsIndeterminate = progress.Fraction is null;
        LoadProgress.Value = progress.Fraction ?? 0;
        SetDownloads(progress.ActiveDownloads ?? []);
    }

    private void SetDownloads(IReadOnlyList<LauncherProgress> downloads)
    {
        var active = downloads.Select(download => download.Download!).ToHashSet();
        foreach (var file in downloadCards.Keys.Where(file => !active.Contains(file)).ToArray())
        {
            DownloadCards.Children.Remove(downloadCards[file].Card);
            downloadCards.Remove(file);
        }

        foreach (var download in downloads)
        {
            var file = download.Download!;
            if (!downloadCards.TryGetValue(file, out var entry))
            {
                entry = CreateDownloadCard(file);
                downloadCards.Add(file, entry);
                DownloadCards.Children.Add(entry.Card);
            }

            entry.Detail.Text = download.Detail;
        }

        DownloadList.IsVisible = downloads.Count > 0;
    }

    private (Border Card, TextBlock Detail) CreateDownloadCard(BuildFile file)
    {
        var icon = new Image { Width = 26, Height = 26, Stretch = Stretch.UniformToFill,
            DataContext = modIcons?.Get(file.Card?.IconUrl) };
        icon.Bind(Image.SourceProperty, new Binding(nameof(ModIcon.Image)));

        var title = new TextBlock { Text = file.DisplayName, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { FontSize = 11, Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var labels = new StackPanel { Spacing = 2, Children = { title, detail } };
        Grid.SetColumn(labels, 1);

        var content = new Grid
        {
            ColumnDefinitions = new("Auto,*"),
            ColumnSpacing = 7,
            Children = { icon, labels }
        };
        var card = new Border
        {
            Width = 206,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(6),
            Background = Brush.Parse("#1B2432"),
            Child = content
        };
        return (card, detail);
    }

}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace HoleLauncher;

internal sealed class InstanceFiles(HttpClient http, BackendConnection backend, PublishedBuild build,
    IReadOnlyList<PublishedBuild>? knownVersions = null, IReadOnlySet<string>? optionalMods = null, bool serverMode = false,
    IReadOnlyDictionary<string, string[]>? unmanagedMods = null)
{
    private const int Sha256HexLength = 64;
    private const int Sha512HexLength = 128;
    private const string CacheFolder = ".holelauncher";

    private const string ReceiptPath = ".holelauncher/installed.json";
    private const string LegacyVersionPath = ".holelauncher/legacy-version";
    private const string PlayerDataImport = ".player-data-import";
    private static readonly string[] PlayerData = ["saves", "screenshots", "options.txt", "servers.dat", "hotbar.nbt"];

    private sealed record ManagedFile(string Path, string Sha512, string? Key = null);
    private sealed record InstallReceipt(ManagedFile[] Installed, ManagedFile[] Pending);

    public string Root { get; } = Path.Combine(AppContext.BaseDirectory,
        serverMode ? "server-instances" : "instances", build.BuildId.ToString("N"), serverMode ? "server" : "game");
    private string BuildDirectory => Path.GetDirectoryName(Root)!;
    private string? legacyForInspection;

    private sealed record RunningGame(int Id, long StartedAt);

    public FileStream PrepareForLaunch()
    {
        Directory.CreateDirectory(BuildDirectory);
        var lockPath = Path.Combine(BuildDirectory, ".active.lock");
        FileStream lease;
        try { lease = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            throw new LauncherException("Эта сборка уже запущена или обновляется.");
        }

        try
        {
            RejectActiveGame(lease);
            if (File.GetAttributes(BuildDirectory).HasFlag(FileAttributes.ReparsePoint))
                throw new LauncherException("Папка сборки является ссылкой.");

            var import = Path.Combine(BuildDirectory, PlayerDataImport);
            if (!serverMode && !Directory.Exists(Root))
            {
                var legacy = FindLegacyRoot();
                var playerData = FindLegacyPlayerData();
                if (playerData is not null && !string.Equals(playerData, legacy, StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(import, Path.GetFileName(playerData));

                if (legacy is not null)
                {
                    var marker = ResolvePath(LegacyVersionPath, internalPath: true, root: legacy);
                    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                    File.WriteAllText(marker, Path.GetFileName(legacy));
                    Directory.Move(legacy, Root);
                }
            }

            Directory.CreateDirectory(Root);
            if (File.GetAttributes(Root).HasFlag(FileAttributes.ReparsePoint))
                throw new LauncherException("Папка игры является ссылкой.");
            if (!serverMode)
                ImportLegacyPlayerData(import);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static void RecordGame(FileStream lease, Process game)
    {
        if (game.HasExited)
            return;

        try
        {
            var state = new RunningGame(game.Id, game.StartTime.ToUniversalTime().Ticks);
            lease.Position = 0;
            JsonSerializer.Serialize(lease, state);
            lease.Flush();
        }
        catch (InvalidOperationException) when (game.HasExited) { }
    }

    private static void RejectActiveGame(FileStream lease)
    {
        if (lease.Length == 0)
            return;

        RunningGame? state;
        try { state = JsonSerializer.Deserialize<RunningGame>(lease); }
        catch (JsonException) { state = null; }

        if (state is not null)
        {
            try
            {
                using var game = Process.GetProcessById(state.Id);
                if (!game.HasExited && game.StartTime.ToUniversalTime().Ticks == state.StartedAt)
                    throw new LauncherException("Эта сборка уже запущена.");
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        lease.SetLength(0);
    }

    public async Task SyncAsync(string stage, Action<LauncherProgress> progress, int parallelDownloads,
        CancellationToken cancellation)
    {
        progress(new(stage, Detail: "Проверяем установленные файлы…"));
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (unmanagedMods is not null)
        {
            protectedPaths.UnionWith(unmanagedMods.Values.SelectMany(paths => paths));
            protectedPaths.UnionWith(AvailableVersions().Append(build).SelectMany(release => release.Files)
                .Where(file => unmanagedMods.ContainsKey(BuildSelection.Key(file))).Select(file => file.Path));
        }

        var installed = await ReadInstalledFilesAsync(cancellation);
        var protectedFiles = installed.Values.Where(file => protectedPaths.Contains(file.Path) ||
            file.Key is { } key && unmanagedMods?.ContainsKey(key) == true).ToArray();
        protectedPaths.UnionWith(protectedFiles.Select(file => file.Path));

        var files = new BuildSelection(build, optionalMods, serverMode).Installation()
            .Where(file => !protectedPaths.Contains(file.Path)).ToArray();
        var current = files.Select(file => new ManagedFile(file.Path, file.Sha512, BuildSelection.Key(file))).ToArray();
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in current)
        {
            if (!destinations.TryAdd(ResolvePath(file.Path), file.Path))
                throw new LauncherException($"В манифесте повторяется путь «{file.Path}».");
        }
        foreach (var folder in build.Build.Folders)
            ResolvePath(folder);

        await SaveReceiptAsync(new([.. installed.Values], current), cancellation);
        var currentPaths = current.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var obsolete = installed.Keys.Where(path => !currentPaths.Contains(path) && !protectedPaths.Contains(path)).ToArray();

        // Remove paths that would block creation of a file or directory in the new manifest.
        var requiredDirectories = RequiredDirectories(files);
        var blocking = obsolete.Where(requiredDirectories.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveObsoleteFiles(blocking, cancellation);
        foreach (var folder in build.Build.Folders)
            Directory.CreateDirectory(ResolvePath(folder));

        // A previously managed child may occupy a path that now needs to be a file.
        var changedFolders = obsolete.Where(path => ParentFolders(path).Any(currentPaths.Contains)).ToArray();
        RemoveObsoleteFiles(changedFolders, cancellation);
        foreach (var folder in changedFolders.SelectMany(ParentFolders).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(folder => folder.Length))
        {
            if (!currentPaths.Contains(folder) && !ParentFolders(folder).Any(currentPaths.Contains))
                continue;
            var directory = ResolvePath(folder);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        var activeDownloads = new Dictionary<BuildFile, LauncherProgress>();
        var progressLock = new object();
        var completed = 0;

        void Report() => progress(new($"{stage} ({completed}/{files.Length})",
            files.Length == 0 ? 1 : (double)completed / files.Length,
            ActiveDownloads: activeDownloads.Values.ToArray()));

        Report();
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelDownloads,
            CancellationToken = cancellation
        }, async (file, token) =>
        {
            var destination = ResolvePath(file.Path);
            await SyncFileAsync(file, destination, stage, update =>
            {
                if (update.Download is null)
                    return;

                lock (progressLock)
                {
                    activeDownloads[file] = update;
                    Report();
                }
            }, token);

            lock (progressLock)
            {
                activeDownloads.Remove(file);
                completed++;
                Report();
            }
        });

        RemoveObsoleteFiles(obsolete.Except(blocking, StringComparer.OrdinalIgnoreCase)
            .Except(changedFolders, StringComparer.OrdinalIgnoreCase), cancellation);
        await SaveReceiptAsync(new([.. current, .. protectedFiles], []), cancellation);
    }

    private HashSet<string> RequiredDirectories(IEnumerable<BuildFile> files) =>
        build.Build.Folders.Concat(build.Build.Folders.SelectMany(ParentFolders))
            .Concat(files.SelectMany(file => ParentFolders(file.Path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ParentFolders(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
            yield return path[..slash];
    }

    private void RemoveObsoleteFiles(IEnumerable<string> paths, CancellationToken cancellation)
    {
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            var destination = ResolvePath(path);
            if (File.Exists(destination))
                File.Delete(destination);
            File.Delete(VersionPath(path));
        }
    }

    private async Task<Dictionary<string, ManagedFile>> ReadInstalledFilesAsync(CancellationToken cancellation)
    {
        var receiptPath = ResolvePath(ReceiptPath, internalPath: true);
        var installed = new Dictionary<string, ManagedFile>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(receiptPath))
        {
            if (serverMode)
                return installed;

            var legacyMarker = ResolvePath(LegacyVersionPath, internalPath: true);
            if (!File.Exists(legacyMarker))
                return installed;

            var legacyVersion = await File.ReadAllTextAsync(legacyMarker, cancellation);
            var legacy = AvailableVersions().FirstOrDefault(release => release.Build.Version == legacyVersion);
            if (legacy is null)
                return installed;

            foreach (var file in legacy.Files.Where(IsManagedByLauncher))
            {
                if (await GetStateAsync(file, cancellation) == LocalFileState.Current)
                    installed[file.Path] = new(file.Path, file.Sha512, BuildSelection.Key(file));
            }
            return installed;
        }

        InstallReceipt? receipt;
        try
        {
            await using var input = File.OpenRead(receiptPath);
            receipt = await JsonSerializer.DeserializeAsync<InstallReceipt>(input, cancellationToken: cancellation);
        }
        catch (JsonException)
        {
            throw new LauncherException("Не удалось прочитать список установленных файлов сборки.");
        }

        if (receipt is null)
            throw new LauncherException("Не удалось прочитать список установленных файлов сборки.");

        foreach (var file in receipt.Installed)
            installed[file.Path] = file;
        foreach (var file in receipt.Pending)
        {
            if (!installed.ContainsKey(file.Path) &&
                await MatchesAsync(ResolvePath(file.Path), file.Sha512, cancellation))
                installed[file.Path] = file;
        }
        return installed;
    }

    private async Task SaveReceiptAsync(InstallReceipt receipt, CancellationToken cancellation)
    {
        var path = ResolvePath(ReceiptPath, internalPath: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Create(partial))
                await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: cancellation);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private IEnumerable<PublishedBuild> AvailableVersions() =>
        new[] { build }.Concat((knownVersions ?? []).Where(release => release.BuildId == build.BuildId))
            .DistinctBy(release => release.Build.Version);

    private string? FindLegacyRoot() => AvailableVersions()
        .Select(release => Path.Combine(BuildDirectory, SafeSegment(release.Build.Version)))
        .Where(path => Directory.Exists(path) &&
            !string.Equals(path, Root, StringComparison.OrdinalIgnoreCase) &&
            !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        .OrderByDescending(path => Path.GetFileName(path) == build.Build.Version)
        .ThenByDescending(Directory.GetLastWriteTimeUtc)
        .FirstOrDefault();

    private string? FindLegacyPlayerData() => Directory.EnumerateDirectories(BuildDirectory)
        .Where(directory => long.TryParse(Path.GetFileName(directory), out _) &&
            !File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint) &&
            PlayerData.Any(name => Path.Exists(Path.Combine(directory, name))))
        .OrderByDescending(directory => Directory.Exists(Path.Combine(directory, "saves")))
        .ThenByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, "logs", "latest.log")))
        .ThenByDescending(Directory.GetLastWriteTimeUtc)
        .FirstOrDefault();

    private void ImportLegacyPlayerData(string import)
    {
        if (!File.Exists(import))
            return;

        var name = SafeSegment(File.ReadAllText(import));
        var source = Path.Combine(BuildDirectory, name);
        if (Directory.Exists(source) && !File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
        {
            foreach (var item in PlayerData)
            {
                var from = Path.Combine(source, item);
                var to = ResolvePath(item);
                if (!Path.Exists(from) || Path.Exists(to) || File.GetAttributes(from).HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (Directory.Exists(from))
                    Directory.Move(from, to);
                else
                    File.Move(from, to);
            }
        }

        File.Delete(import);
    }

    private bool IsManagedByLauncher(BuildFile file) => BuildSelection.IsForSide(file, serverMode) && !file.IsOptional;

    public async Task<LocalFileState> GetStateAsync(BuildFile file, CancellationToken cancellation)
    {
        var root = serverMode || Directory.Exists(Root) ? Root : legacyForInspection ??= FindLegacyRoot() ?? Root;
        var destination = ResolvePath(file.Path, root: root);
        if (!File.Exists(destination))
            return LocalFileState.Missing;

        if (file.Type == "Soft")
        {
            var versionPath = VersionPath(file.Path, root);
            if (File.Exists(versionPath) && await File.ReadAllTextAsync(versionPath, cancellation) == file.FileVersion)
                return LocalFileState.Current;
        }

        return await MatchesAsync(destination, file.Sha512, cancellation)
            ? LocalFileState.Current : LocalFileState.Outdated;
    }

    private string VersionPath(string path, string? root = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        return ResolvePath($"{CacheFolder}/{hash}.version", internalPath: true, root: root);
    }

    private async Task SyncFileAsync(BuildFile file, string destination, string title, Action<LauncherProgress> progress,
        CancellationToken cancellation)
    {
        var (client, source) = FileSource(file.Url);
        if (file.Sha512.Length != Sha512HexLength || !file.Sha512.All(Uri.IsHexDigit))
            throw new LauncherException($"Некорректный SHA-512 у файла {file.Path}.");

        if (file.Type == "Strong" || file.IsOptional)
        {
            await EnsureDownloadAsync(client, source, destination, file.Sha512, cancellation, title, progress, file);
            return;
        }

        if (file.Type != "Soft")
            throw new LauncherException($"Неизвестный тип файла {file.Path}.");

        var versionPath = VersionPath(file.Path);
        if (File.Exists(destination) && File.Exists(versionPath) &&
            await File.ReadAllTextAsync(versionPath, cancellation) == file.FileVersion)
            return;

        await EnsureDownloadAsync(client, source, destination, file.Sha512, cancellation, title, progress, file);
        Directory.CreateDirectory(Path.GetDirectoryName(versionPath)!);
        await File.WriteAllTextAsync(versionPath, file.FileVersion, cancellation);
    }

    public static async Task EnsureDownloadAsync(HttpClient http, Uri source, string destination, string checksum,
        CancellationToken cancellation, string title, Action<LauncherProgress> progress, BuildFile? download = null)
    {
        var file = Path.GetFileName(destination);
        progress(new(title, Detail: $"Проверяем {file}…"));
        if (await MatchesAsync(destination, checksum, cancellation))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            if (download is not null)
                progress(new(title, Detail: "Подключаемся…", Download: download));

            using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new LauncherException($"Файл «{file}» отсутствует на сервере (HTTP 404).");

                throw new LauncherException($"Не удалось скачать «{file}» (HTTP {(int)response.StatusCode}).");
            }
            await using var body = await response.Content.ReadAsStreamAsync(cancellation);
            var total = response.Content.Headers.ContentLength;
            var transfer = new TransferProgress();
            var label = download is null ? file : "";
            progress(transfer.Update(title, label, 0, total, force: true)! with { Download = download });
            await using (var output = new DownloadProgressFile(partial, written =>
            {
                if (transfer.Update(title, label, written, total) is { } update)
                    progress(update with { Download = download });
            }))
                await body.CopyToAsync(output, cancellation);

            progress(new(title, Detail: "Проверяем контрольную сумму…", Download: download));
            if (!await MatchesAsync(partial, checksum, cancellation))
                throw new LauncherException($"Контрольная сумма файла {Path.GetFileName(destination)} не совпадает.");

            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private static async Task<bool> MatchesAsync(string path, string checksum, CancellationToken cancellation)
    {
        if (!File.Exists(path))
            return false;

        await using var stream = File.OpenRead(path);
        var bytes = checksum.Length == Sha256HexLength
            ? await SHA256.HashDataAsync(stream, cancellation)
            : await SHA512.HashDataAsync(stream, cancellation);
        return Convert.ToHexString(bytes).Equals(checksum, StringComparison.OrdinalIgnoreCase);
    }

    private (HttpClient Client, Uri Address) FileSource(string url)
    {
        if (url.StartsWith("/api/storage/", StringComparison.Ordinal))
        {
            var sameOrigin = new Uri(backend.Address, url);
            if (sameOrigin.AbsolutePath.StartsWith("/api/storage/", StringComparison.Ordinal))
                return (backend.Http, sameOrigin);
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var cdn) && cdn.Scheme == Uri.UriSchemeHttps &&
            cdn.Host == "cdn.modrinth.com" && cdn.IsDefaultPort && cdn.UserInfo.Length == 0 &&
            cdn.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal))
            return (http, cdn);

        throw new LauncherException("Манифест содержит неподдерживаемый адрес файла.");
    }

    private string ResolvePath(string path, bool internalPath = false, string? root = null)
    {
        var segments = path.Split('/');
        if (!internalPath && segments[0].Equals(CacheFolder, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException($"Некорректный путь в манифесте · {path}");

        var location = root ?? Root;
        if (Directory.Exists(location) && File.GetAttributes(location).HasFlag(FileAttributes.ReparsePoint))
            throw new LauncherException("Папка сборки является ссылкой.");
        foreach (var segment in segments)
        {
            location = Path.Combine(location, SafeSegment(segment));
            if ((File.Exists(location) || Directory.Exists(location)) &&
                File.GetAttributes(location).HasFlag(FileAttributes.ReparsePoint))
                throw new LauncherException($"В пути файла сборки есть ссылка · {path}");
        }

        return location;
    }

    private static string SafeSegment(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        if (segment.Length is 0 or > 255 || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Any(c => char.IsControl(c) || "<>:\"/\\|?*%".Contains(c)) ||
            stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                ("123456789¹²³".Contains(stem[3]))))
            throw new LauncherException($"Некорректный путь в манифесте · {segment}");

        return segment;
    }
}
internal enum LocalFileState { Checking, Missing, Current, Outdated, Unavailable }

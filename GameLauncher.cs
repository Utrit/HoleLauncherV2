using System.ComponentModel;
using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Rules;

namespace HoleLauncher;

internal sealed class GameLauncher(HttpClient http)
{
    private const string InjectorName = "authlib-injector-1.2.8.jar";
    private const string InjectorUrl = "https://authlib-injector.yushi.moe/artifact/56/" + InjectorName;
    private const string InjectorSha256 = "9c7f4343e6c82034958ffb48c14a2cb0c85928be7283103ce17da00c6d5a7b10";

    public async Task<int> PlayAsync(BackendConnection backend, PublishedBuild build, DraslSession session, GamePreferences preferences,
        IProgress<LauncherProgress> progress, CancellationToken cancellation, IReadOnlyList<PublishedBuild>? knownVersions = null, IReadOnlySet<string>? optionalMods = null)
    {
        var instance = new InstanceFiles(http, backend, build, knownVersions, optionalMods);
        using var installLease = instance.PrepareForLaunch();
        var minecraft = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(new MinecraftPath(instance.Root), http));

        var draslArguments = await PrepareDraslAsync(backend, "1/6 · Authlib-injector",
            "2/6 · Подключаем Drasl…", progress, cancellation);
        var maxRam = preferences.EffectiveRamMiB;
        MArgument[] customArguments = string.IsNullOrWhiteSpace(preferences.JvmArguments)
            ? [] : [MArgument.FromCommandLine(preferences.JvmArguments)];
        var arguments = new MLaunchOption
        {
            MaximumRamMb = maxRam,
            MinimumRamMb = Math.Min(maxRam, 512),
            Session = new MSession
            {
                AccessToken = session.AccessToken,
                Username = session.SelectedProfile.Name,
                UUID = Guid.Parse(session.SelectedProfile.Id).ToString("N"),
                UserType = "mojang"
            },
            ExtraJvmArguments = [.. MLaunchOption.DefaultExtraJvmArguments, .. customArguments, new MArgument(draslArguments)]
        };

        var release = build.Build;
        var loaderTitle = $"3/6 · Устанавливаем {release.Loader} {release.LoaderVersion}…";
        progress.Report(new(loaderTitle));
        string version;
        using (var loader = new InstallerProgress(loaderTitle, progress.Report))
            version = await InstallLoaderAsync(minecraft, release, loader, cancellation);

        var gameTitle = $"4/6 · Устанавливаем Minecraft {release.Minecraft}…";
        progress.Report(new(gameTitle));
        using (var installer = new InstallerProgress(gameTitle, progress.Report))
            await minecraft.InstallAsync(version, installer.Files, installer.Bytes, cancellation);

        await instance.SyncAsync("5/6 · Файлы сборки", progress.Report, preferences.EffectiveParallelDownloads, cancellation);
        progress.Report(new("6/6 · Запускаем Minecraft…"));
        using var game = await minecraft.BuildProcessAsync(version, arguments, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (!game.Start())
            throw new LauncherException("Не удалось запустить Minecraft.");

        InstanceFiles.RecordGame(installLease, game);
        progress.Report(new("Minecraft запущен.", ShowBar: false));
        await game.WaitForExitAsync(cancellation);
        installLease.SetLength(0);
        return game.ExitCode;
    }

    public async Task<int> RunServerAsync(BackendConnection backend, PublishedBuild build, string jar, string? javaPath,
        GamePreferences preferences, IProgress<LauncherProgress> progress, CancellationToken cancellation,
        IReadOnlySet<string> optionalMods)
    {
        if (!File.Exists(jar) || !Path.GetExtension(jar).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("Выберите исполняемый JAR серверного ядра.");

        var instance = new InstanceFiles(http, backend, build, optionalMods: optionalMods, serverMode: true);
        var java = ResolveJava(javaPath, build.BuildId);
        using var lease = instance.PrepareForLaunch();
        await instance.SyncAsync("1/3 · Файлы сервера", progress.Report, preferences.EffectiveParallelDownloads, cancellation);
        var auth = await PrepareDraslAsync(backend, "2/3 · Authlib-injector",
            "2/3 · Подключаем Drasl…", progress, cancellation);
        progress.Report(new("3/3 · Запускаем сервер…"));

        var startInfo = new ProcessStartInfo
        {
            FileName = java,
            WorkingDirectory = instance.Root,
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        var ram = preferences.EffectiveRamMiB;
        startInfo.ArgumentList.Add($"-Xms{Math.Min(ram, 512)}M");
        startInfo.ArgumentList.Add($"-Xmx{ram}M");
        if (!string.IsNullOrWhiteSpace(preferences.JvmArguments))
            foreach (var argument in MArgument.FromCommandLine(preferences.JvmArguments).Values)
                startInfo.ArgumentList.Add(argument);
        foreach (var argument in auth)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(jar);
        startInfo.ArgumentList.Add("nogui");

        cancellation.ThrowIfCancellationRequested();
        using var server = StartServer(startInfo);
        InstanceFiles.RecordGame(lease, server);
        progress.Report(new("Процесс сервера запущен.", ShowBar: false));
        try
        {
            await server.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!server.HasExited)
            {
                try { await server.StandardInput.WriteLineAsync("stop"); }
                catch (IOException) when (server.HasExited) { }
            }
            await server.WaitForExitAsync();
        }

        lease.SetLength(0);
        return server.ExitCode;
    }

    private static Process StartServer(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new LauncherException("Не удалось запустить сервер.");
        }
        catch (Win32Exception error)
        {
            throw new LauncherException($"Не удалось запустить Java: {error.Message}");
        }
    }

    private static string ResolveJava(string? configuredPath, Guid buildId)
    {
        if (configuredPath is not null)
            return File.Exists(configuredPath) ? configuredPath : throw new LauncherException("Выбранный файл Java не найден.");

        var javaName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var runtime = Path.Combine(AppContext.BaseDirectory, "instances", buildId.ToString("N"), "game", "runtime");
        if (Directory.Exists(runtime))
        {
            var bundled = Directory.EnumerateDirectories(runtime)
                .SelectMany(platform => Directory.EnumerateDirectories(platform, "java-runtime-*"))
                .OrderDescending(StringComparer.Ordinal)
                .Select(path => Path.Combine(path, "bin", javaName))
                .FirstOrDefault(File.Exists);
            if (bundled is not null)
                return bundled;
        }

        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var java = home is null ? null : Path.Combine(home, "bin", javaName);
        return java is not null && File.Exists(java) ? java : javaName;
    }

    private async Task<string[]> PrepareDraslAsync(BackendConnection backend, string injectorStage, string draslStage,
        IProgress<LauncherProgress> progress, CancellationToken cancellation)
    {
        var injector = Path.Combine(AppContext.BaseDirectory, InjectorName);
        await InstanceFiles.EnsureDownloadAsync(http, new Uri(InjectorUrl), injector, InjectorSha256, cancellation,
            injectorStage, progress.Report);
        progress.Report(new(draslStage));

        var drasl = new Uri(backend.Address, "/drasl/authlib-injector");
        // The local Docker port is published on IPv4 loopback.
        if (drasl.Scheme == Uri.UriSchemeHttp && drasl.Host == "localhost")
            drasl = new UriBuilder(drasl) { Host = "127.0.0.1" }.Uri;
        var metadata = await http.GetByteArrayAsync(drasl, cancellation);
        string[] tls = OperatingSystem.IsWindows() && drasl.IsLoopback && drasl.Scheme == Uri.UriSchemeHttps
            ? ["-Djavax.net.ssl.trustStoreType=Windows-ROOT", "-Djavax.net.ssl.trustStore=NONE"]
            : [];
        return [.. tls, $"-Dminecraft.api.session.host={drasl.AbsoluteUri}/sessionserver",
            $"-Dminecraft.api.services.host={drasl.AbsoluteUri}/minecraftservices",
            $"-javaagent:{injector}={drasl.AbsoluteUri}",
            $"-Dauthlibinjector.yggdrasil.prefetched={Convert.ToBase64String(metadata)}"];
    }

    private Task<string> InstallLoaderAsync(MinecraftLauncher minecraft, BuildInfo release, InstallerProgress progress,
        CancellationToken cancellation) => release.Loader switch
    {
        "forge" => new ForgeInstaller(minecraft, http).Install(release.Minecraft, release.LoaderVersion, new ForgeInstallOptions
        {
            FileProgress = progress.Files,
            ByteProgress = progress.Bytes,
            InstallerOutput = progress.Output,
            CancellationToken = cancellation
        }),
        "neoforge" => new NeoForgeInstaller(minecraft).Install(release.Minecraft, release.LoaderVersion, new NeoForgeInstallOptions
        {
            FileProgress = progress.Files,
            ByteProgress = progress.Bytes,
            InstallerOutput = progress.Output,
            CancellationToken = cancellation
        }),
        "vanilla" or "" => Task.FromResult(release.Minecraft),
        _ => throw new LauncherException($"Загрузчик {release.Loader} пока не поддерживается.")
    };
}

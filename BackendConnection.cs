using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace HoleLauncher;

internal sealed class BackendConnection(Uri address) : IDisposable
{
    public Uri Address { get; } = address;

    public HttpClient Http { get; } = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = address,
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static Uri ParseAddress(string? value, string name)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new LauncherException($"Укажите адрес {name} с HTTPS или локальный HTTP-адрес.");

        return uri;
    }

    public async Task<bool> IsLauncherUpdateAvailableAsync(CancellationToken cancellation)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
            EmbeddedBackend.ReadFingerprint() is not { } localFingerprint)
            return false;

        var path = OperatingSystem.IsLinux() ? "/launcherlinux/sha512" : "/launcher/sha512";
        using var response = await Http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        return response.Headers.TryGetValues("X-Launcher-Build-Sha512", out var values) &&
            values.FirstOrDefault() is { Length: 128 } remoteFingerprint &&
            remoteFingerprint.All(Uri.IsHexDigit) &&
            !string.Equals(localFingerprint, remoteFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<PublishedBuild[]> GetBuildsAsync(CancellationToken cancellation) =>
        await Http.GetFromJsonAsync<PublishedBuild[]>("/api/auth/repository/published", cancellation) ?? [];

    public async Task<bool> RegisterAsync(string userName, string password, CancellationToken cancellation)
    {
        using var response = await Http.PostAsJsonAsync("/api/auth/register", new { UserName = userName, Password = password }, cancellation);
        return response.StatusCode switch
        {
            HttpStatusCode.Created => true,
            HttpStatusCode.Accepted => false,
            HttpStatusCode.BadRequest => throw new LauncherException("Некорректный логин или пароль."),
            HttpStatusCode.Conflict => throw new LauncherException("Имя пользователя уже занято."),
            HttpStatusCode.TooManyRequests => throw new LauncherException("Слишком много запросов. Попробуйте позже."),
            _ => throw new LauncherException("Сервер не смог завершить регистрацию.")
        };
    }

    public async Task<DraslSession> EnsureGameSessionAsync(DraslSession session, string userName, string password,
        CancellationToken cancellation)
    {
        using var account = await Http.GetAsync("/api/auth/me", cancellation);
        if (account.StatusCode == HttpStatusCode.Unauthorized)
            return await LoginAsync(userName, password, cancellation);
        account.EnsureSuccessStatusCode();

        using var validation = await Http.PostAsJsonAsync("/drasl/auth/validate",
            new { session.AccessToken, session.ClientToken }, cancellation);
        if (validation.IsSuccessStatusCode)
            return session;
        if (validation.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest))
            validation.EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/game-session");
        request.Headers.Add("X-HoleLauncher-Request", "1");
        using var renewal = await Http.SendAsync(request, cancellation);
        if (renewal.StatusCode == HttpStatusCode.Unauthorized)
            return await LoginAsync(userName, password, cancellation);
        renewal.EnsureSuccessStatusCode();
        return await renewal.Content.ReadFromJsonAsync<DraslSession>(cancellation)
            ?? throw new LauncherException("Бэкенд не выдал игровую сессию Drasl.");
    }

    public async Task SetTextureAsync(bool skin, string model, string name, Stream source, CancellationToken cancellation)
    {
        var path = "/api/auth/game-profile/" + (skin ? "skin" : "cape");
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Headers.TryAddWithoutValidation("X-Storage-File-Name", Uri.EscapeDataString(name));
        using var upload = await Http.PostAsync(path, content, cancellation);
        if (upload.StatusCode == HttpStatusCode.BadRequest)
            throw new LauncherException("Выберите подходящую PNG-текстуру.");
        upload.EnsureSuccessStatusCode();
        var stored = await upload.Content.ReadFromJsonAsync<TextureUpload>(cancellation)
            ?? throw new LauncherException("Сервер не подтвердил загрузку текстуры.");
        await WaitForOperationAsync(stored.OperationId, cancellation);
        using var change = await Http.PutAsJsonAsync(path, new { stored.FileId, SkinModel = skin ? model : null }, cancellation);
        if (change.StatusCode == HttpStatusCode.BadRequest)
            throw new LauncherException("Текстура не подходит для выбранной модели.");
        change.EnsureSuccessStatusCode();
        if (change.StatusCode == HttpStatusCode.Accepted)
        {
            var operation = await change.Content.ReadFromJsonAsync<OperationAccepted>(cancellation)
                ?? throw new LauncherException("Сервер не подтвердил изменение текстуры.");
            await WaitForOperationAsync(operation.OperationId, cancellation);
        }
    }

    private async Task WaitForOperationAsync(Guid id, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        do
        {
            var operation = await Http.GetFromJsonAsync<TextureOperation>($"/api/auth/operations/{id:D}", deadline.Token);
            if (operation?.State == "Done")
                return;
            if (operation?.State == "Failed")
                throw new LauncherException("Сервер не смог сохранить текстуру.");
        } while (await timer.WaitForNextTickAsync(deadline.Token));
    }

    private sealed record TextureUpload(Guid OperationId, Guid FileId);
    private sealed record OperationAccepted(Guid OperationId);
    private sealed record TextureOperation(string State);

    public async Task<DraslSession> LoginAsync(string userName, string password, CancellationToken cancellation)
    {
        using var reply = await Http.PostAsJsonAsync("/api/auth/login", new { UserName = userName, Password = password }, cancellation);
        if (reply.StatusCode == HttpStatusCode.Unauthorized)
            throw new LauncherException("Неверный логин или пароль.");
        if (reply.StatusCode == HttpStatusCode.TooManyRequests)
            throw new LauncherException("Слишком много попыток входа. Попробуйте позже.");
        reply.EnsureSuccessStatusCode();

        var result = await reply.Content.ReadFromJsonAsync<LoginReply>(cancellation);
        var session = result?.DraslSession;
        if (session?.SelectedProfile is not { } player || string.IsNullOrWhiteSpace(session.AccessToken) ||
            string.IsNullOrWhiteSpace(player.Name) || !Guid.TryParse(player.Id, out _))
            throw new LauncherException("Бэкенд не выдал игровую сессию Drasl.");

        return session;
    }

    public async Task LogoutAsync(DraslSession session, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("X-HoleLauncher-Request", "1");
        using var response = await Http.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();

        if (!string.IsNullOrWhiteSpace(session.ClientToken))
        {
            using var revoke = await Http.PostAsJsonAsync("/drasl/authlib-injector/authserver/invalidate",
                new { session.AccessToken, session.ClientToken }, cancellation);
            revoke.EnsureSuccessStatusCode();
        }
    }

    public Task<byte[]?> GetIconAsync(string url, HttpClient publicHttp, CancellationToken cancellation)
    {
        if (url.StartsWith("/api/storage/", StringComparison.Ordinal))
        {
            var address = new Uri(Address, url);
            if (address.AbsolutePath.StartsWith("/api/storage/", StringComparison.Ordinal))
                return ReadImageAsync(Http, address, cancellation);
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var external) && external.Scheme == Uri.UriSchemeHttps &&
            external.Host == "cdn.modrinth.com" && external.IsDefaultPort && external.UserInfo.Length == 0 &&
            external.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal))
            return ReadImageAsync(publicHttp, external, cancellation);

        return Task.FromResult<byte[]?>(null);
    }

    public Task<GameProfile?> GetGameProfileAsync(CancellationToken cancellation) =>
        Http.GetFromJsonAsync<GameProfile>("/api/auth/game-profile", cancellation);

    public Task<byte[]?> GetSkinAsync(Guid id, CancellationToken cancellation) =>
        ReadImageAsync(Http, new Uri(Address, $"/api/storage/userdata/{id:D}"), cancellation);

    public Task<byte[]?> GetCapeAsync(Guid id, CancellationToken cancellation) =>
        ReadImageAsync(Http, new Uri(Address, $"/api/auth/game-profile/cape-preview/{id:D}"), cancellation);

    private static async Task<byte[]?> ReadImageAsync(HttpClient http, Uri address, CancellationToken cancellation)
    {
        const int maxTextureBytes = 1024 * 1024;
        using var reply = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!reply.IsSuccessStatusCode || reply.Content.Headers.ContentLength > maxTextureBytes)
            return null;

        using var stream = await reply.Content.ReadAsStreamAsync(cancellation);
        var bytes = new byte[maxTextureBytes + 1];
        var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellation);
        return read <= maxTextureBytes ? bytes[..read] : null;
    }

    public void Dispose() => Http.Dispose();
}

internal sealed class LauncherException(string message) : Exception(message);

internal sealed record LoginReply(DraslSession? DraslSession);
internal sealed record DraslSession(string AccessToken, DraslPlayer SelectedProfile, string? ClientToken = null);
internal sealed record DraslPlayer(string Id, string Name);
internal sealed record GameProfile(string? GameName, Guid? SkinFileId, Guid? CapeFileId, string SkinModel);

internal sealed record PublishedBuild(Guid BuildId, BuildInfo Build, BuildFile[] Files, bool Test)
{
    public override string ToString() => $"{Build.Name} · v{Build.Version}{(Test ? " · Тест" : "")}";
}

internal sealed record BuildInfo(string Name, string Version, string Minecraft, string Loader, string LoaderVersion, string[] Folders);
internal sealed record BuildFile(string Path, string Type, string Url, string Sha512, string FileVersion, bool? ClientSide, BuildModCard? Card = null, string? ProjectId = null, string[]? Dependencies = null, bool? ServerSide = null)
{
    public bool IsOptional => string.Equals(Type, "Optional", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => string.IsNullOrWhiteSpace(Card?.Title) ? System.IO.Path.GetFileName(Path) : Card.Title;
}

internal sealed record BuildModCard(string? Title, string? Description, string? IconUrl);

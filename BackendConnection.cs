using System.Net;
using System.Net.Http.Json;

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

    public async Task<PublishedBuild[]> GetBuildsAsync(CancellationToken cancellation) =>
        await Http.GetFromJsonAsync<PublishedBuild[]>("/api/auth/repository/published", cancellation) ?? [];

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

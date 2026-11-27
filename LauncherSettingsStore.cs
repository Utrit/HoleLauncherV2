using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HoleLauncher;

internal sealed record LauncherSettings(
    string BackendAddress = "http://localhost:8080",
    string UserName = "",
    string? ProtectedPassword = null,
    bool DeveloperMode = false, GamePreferences? Preferences = null, Dictionary<Guid, string[]>? OptionalMods = null,
    string? ServerJar = null, string? ServerJava = null, Dictionary<Guid, Dictionary<string, string[]>>? UnmanagedMods = null)
{
    public string ReadPassword() =>
        OperatingSystem.IsWindows() && ProtectedPassword is not null
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword),
                Encoding.UTF8.GetBytes(BackendAddress), DataProtectionScope.CurrentUser))
            : "";

    public LauncherSettings WithPassword(string password) => this with
    {
        ProtectedPassword = OperatingSystem.IsWindows()
            ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password),
                Encoding.UTF8.GetBytes(BackendAddress), DataProtectionScope.CurrentUser))
            : null
    };
}

internal sealed class LauncherSettingsStore(bool serverMode = false)
{
    private readonly string settingsPath = Path.Combine(AppContext.BaseDirectory,
        serverMode ? "server-launcher-settings.json" : "launcher-settings.json");

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellation)
    {
        if (!File.Exists(settingsPath))
            return new();

        return JsonSerializer.Deserialize<LauncherSettings>(
            await File.ReadAllTextAsync(settingsPath, cancellation)) ?? new();
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellation)
    {
        var partial = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(settings), cancellation);
            File.Move(partial, settingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }
}

using Autofac;

namespace HoleLauncher;

internal static class Setup
{
    public static IContainer BuildContainer(bool serverMode)
    {
        var builder = new ContainerBuilder();
        builder.Register(context => new MainWindow(context.Resolve<LauncherSettingsStore>(),
            context.Resolve<GameLauncher>(), context.Resolve<HttpClient>(), serverMode)).SingleInstance();
        builder.Register(_ => new LauncherSettingsStore(serverMode)).SingleInstance();
        builder.RegisterType<GameLauncher>().SingleInstance();
        builder.Register(_ => new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })).SingleInstance();
        return builder.Build();
    }
}

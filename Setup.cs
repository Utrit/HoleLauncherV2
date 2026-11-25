using Autofac;

namespace HoleLauncher;

internal static class Setup
{
    public static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterType<MainWindow>().SingleInstance();
        return builder.Build();
    }
}

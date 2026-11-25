using Autofac;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace HoleLauncher;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var container = Setup.BuildContainer();
            desktop.Exit += (_, _) => container.Dispose();
            desktop.MainWindow = container.Resolve<MainWindow>();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

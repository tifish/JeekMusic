using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using JeekMusic.UI;
using JeekMusic.Infrastructure;

namespace JeekMusic;

public partial class App : Application
{
    public override void Initialize()
    {
        using (LifecycleTimings.Measure("startupWork.theme")) AvaloniaXamlLoader.Load(this);
        LifecycleTimings.Mark("startup.themeReady");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices { Instance = Program.Instance, Args = Program.Args };
            var shell = new Shell(services, desktop);
            shell.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

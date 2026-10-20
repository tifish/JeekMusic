using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using JeekMusic.UI;

namespace JeekMusic;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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

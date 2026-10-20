using Avalonia;
using JeekMusic.Infrastructure;
using JeekMusic.Platform;

namespace JeekMusic;

static class Program
{
    public static SingleInstance? Instance { get; private set; }
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        // 自动更新：新版本以这个参数启动，把自己复制到程序目录（设计 §15.2）
        if (args.Length >= 3 && args[0] == "--apply-update") return Update.Updater.ApplyUpdate(args[1], args[2]);

        // 单实例（需求 PC-7）：已经在运行时把参数交给它，然后退出
        Instance = new SingleInstance();
        if (!Instance.IsFirst)
        {
            SingleInstance.Send(args.Length == 0 ? ["--show"] : args);
            Instance.Dispose();
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("app", "未处理的异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("app", "未观察的任务异常", e.Exception); e.SetObserved(); };
        Log.Info("app", $"启动 {typeof(Program).Assembly.GetName().Version} {string.Join(' ', args)}");
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            Log.Flush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

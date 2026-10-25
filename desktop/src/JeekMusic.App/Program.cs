using Avalonia;
using JeekMusic.Infrastructure;
using JeekMusic.Platform;
using JeekMusic.UI;

namespace JeekMusic;

static class Program
{
    public static SingleInstance? Instance { get; private set; }
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        // GBK / Big5 / Shift-JIS（乱码修复、.lrc / .cue 编码识别）
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // 自动更新：新版本以这个参数启动，把自己复制到程序目录（设计 §15.2）
        if (args.Length >= 3 && args[0] == "--apply-update") return Update.Updater.ApplyUpdate(args[1], args[2]);
        // MCP 子进程只转发标准输入输出，不创建窗口，也不触发单实例窗口激活。
        if (args is ["--mcp"]) return Automation.McpBridge.RunAsync().GetAwaiter().GetResult();

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
        Online.TagWriteRecovery.RecoverBeforeScan(); // before library startup/scanner can delete a temporarily absent path
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
        AppBuilder.Configure<App>().UsePlatformDetect().With(Typography.FontOptions()).LogToTrace();
}

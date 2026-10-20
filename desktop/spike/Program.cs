using System;
using Avalonia;

namespace JeekMusic.Spike;

static class Program
{
    public static SpikeOptions Options { get; private set; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        Options = SpikeOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // 对比内存：默认用 GPU（ANGLE），--software 改用 CPU 渲染
        if (Options.Software) builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        return builder;
    }
}

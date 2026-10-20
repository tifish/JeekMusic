using System;
using System.Linq;
using Avalonia;

namespace JeekMusic.AudioSpike;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Any(a => a.StartsWith("--test"))) return Tests.Run(args);
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

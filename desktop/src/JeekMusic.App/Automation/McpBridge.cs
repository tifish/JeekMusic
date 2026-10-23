using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using JeekMusic.Platform;

namespace JeekMusic.Automation;

/// <summary>正式 exe 的 stdio 入口；播放器进程持有状态，每个外部客户端各有一个轻量转发进程。</summary>
internal static class McpBridge
{
    internal static string PipeName => SingleInstance.Id + "-MCP";

    public static async Task<int> RunAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(300, timeout.Token); }
            catch (TimeoutException)
            {
                // CreateProcess 会继承所有可继承句柄（不只 STARTUPINFO 中指定的三个）。
                // 清掉宿主管道的继承标记，否则 GUI 仍持有旧管道，宿主无法收到 EOF。
                foreach (var handle in new[] { -10, -11, -12 })
                    if (GetStdHandle(handle) is var native && native != 0 && native != -1 &&
                        !SetHandleInformation(native, 1 /* HANDLE_FLAG_INHERIT */, 0))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                // 新播放器不能继承 MCP 宿主的 stdio 句柄，否则转发进程退出后宿主仍等不到 EOF。
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("--tray");
                start.ArgumentList.Add("--background");
                using var process = Process.Start(start) ?? throw new IOException("无法启动 JeekMusic");
                process.StandardInput.Close();
                await pipe.ConnectAsync(timeout.Token);
            }

            using var stop = new CancellationTokenSource();
            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();
            // 关闭 stdin 或播放器退出时一起结束，不等待仍然打开的另一个方向。
            var upload = input.CopyToAsync(pipe, stop.Token);
            var download = pipe.CopyToAsync(output, stop.Token);
            var completed = await Task.WhenAny(upload, download);
            await completed;
            stop.Cancel();
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            // WinExe 没有控制台时 Console.Error 可能是空流；重定向时仍必须只向 stderr 写诊断。
            using var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            await error.WriteLineAsync("JeekMusic MCP: " + e.Message);
            return 1;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}

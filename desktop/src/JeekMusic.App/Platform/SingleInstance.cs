using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using JeekMusic.Infrastructure;

namespace JeekMusic.Platform;

/// <summary>
/// 单实例（需求 PC-7，设计 §8.1）：命名互斥量判断是否已经在运行；已经在运行时，把命令行参数通过命名管道交给它，然后退出。
/// 发送前 AllowSetForegroundWindow，正在运行的那个可以把自己的窗口带到前台（这是用户自己的操作）。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    static readonly string Id = "JeekMusic-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName) + DataSuffix();
    static string DataSuffix() => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JEEKMUSIC_DATA")) ? "" :
        "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppPaths.DataDir).TrimEnd('\\').ToUpperInvariant())))[..16];
    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    /// <summary>另一个实例发来的参数（在后台线程上触发）。</summary>
    public event Action<string[]>? Received;

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\" + Id, out var created);
        IsFirst = created;
    }

    /// <summary>把参数交给已经在运行的实例；成功返回 true。</summary>
    public static bool Send(string[] args)
    {
        try
        {
            if (!args.Contains("--background")) Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\0', args) + "\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Warn("instance", "无法连接到已经运行的实例", e);
            return false;
        }
    }

    public void Listen()
    {
        if (!IsFirst) return;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(_cts.Token) ?? "";
                    Received?.Invoke(line.Length == 0 ? [] : line.Split('\0'));
                }
                catch (OperationCanceledException) { return; }
                catch (IOException e) { Log.Warn("instance", "命名管道出错", e); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

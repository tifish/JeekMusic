using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekMusic.Infrastructure;

namespace JeekMusic.Automation;

/// <summary>仅接受同一 Windows 用户的本机连接；每条连接有独立的 MCP 初始化状态。</summary>
public sealed class McpServer(McpTools tools) : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    int _started;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = ListenAsync();
    }

    async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(McpBridge.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                _ = ServeAsync(pipe);
                pipe = null; // ServeAsync 接管生命周期；继续接收其他软件的连接。
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("mcp", "MCP 监听失败", e);
                try { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            var session = new McpSession(tools.Catalog, async (name, args) =>
            {
                _stop.Token.ThrowIfCancellationRequested();
                // 工具在 UI 线程进入；异步标签事务在后台执行，不能同步等待阻塞 UI。
                var pending = await Dispatcher.UIThread.InvokeAsync(() => tools.CallAsync(name, args),
                    DispatcherPriority.Normal, _stop.Token);
                return await pending.ConfigureAwait(false);
            });
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                await foreach (var line in McpWire.ReadLinesAsync(reader, _stop.Token).ConfigureAwait(false))
                {
                    var response = await session.HandleAsync(line).ConfigureAwait(false);
                    if (response is not null) await writer.WriteLineAsync(response.ToJsonString().AsMemory(), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or DecoderFallbackException)
            {
                Log.Info("mcp", "MCP 客户端断开：" + e.Message);
            }
            catch (Exception e) { Log.Warn("mcp", "MCP 连接失败", e); }
        }
    }

    // 不能在 UI 线程等待正在排队的工具调用；取消会阻止这些调用访问已释放的服务。
    public void Dispose() => _stop.Cancel();
}

internal static class McpWire
{
    internal const int MaxMessageLength = 1024 * 1024;

    internal static async IAsyncEnumerable<string> ReadLinesAsync(TextReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    if (line.Length >= MaxMessageLength) throw new IOException("MCP message exceeds 1 MiB character limit");
                    line.Append(buffer[i]);
                }
            }
        }
    }
}

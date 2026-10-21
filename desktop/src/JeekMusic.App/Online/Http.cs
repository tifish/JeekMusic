using System.Net;
using System.Text;

namespace JeekMusic.Online;

/// <summary>服务暂时不可用（503，多半是限流）。</summary>
public sealed class HttpUnavailableException(string url) : IOException($"HTTP 503 for {url}");

/// <summary>
/// 在线歌词、封面共用的 HTTP 工具（同移动端 data/online/Http.kt）。HttpClient 第一次用到时才创建（设计 §1）。
/// 404 当作"没有"（返回 null），503 等一下重试（MusicBrainz、LRCLIB 偶尔限流），其他错误抛出异常。
/// </summary>
public static class Http
{
    /// <summary>MusicBrainz、LRCLIB 要求标明客户端。</summary>
    public static string AppAgent { get; } =
        $"JeekMusic/{typeof(Http).Assembly.GetName().Version?.ToString(3) ?? "1.0"} (https://github.com/tifish/JeekMusic)";

    /// <summary>网易云音乐、QQ 音乐的网页接口按浏览器的请求头来。</summary>
    public const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    const int Retries = 2;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    static readonly Lazy<HttpClient> Client = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        // 每个请求自己设超时（CancellationToken），这里不限
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    });

    /// <summary>响应体文本；404 返回 null。<paramref name="form"/> 不为 null 时用 POST 发送表单。</summary>
    public static async Task<string?> TextAsync(string url, IReadOnlyDictionary<string, string>? headers = null, string? form = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var bytes = await SendAsync(url, headers, form, timeout ?? DefaultTimeout, ct);
                return bytes is null ? null : Encoding.UTF8.GetString(bytes);
            }
            catch (HttpUnavailableException) when (attempt < Retries)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>下载图片等二进制内容；失败时返回 null。</summary>
    public static async Task<byte[]?> BytesAsync(string url, IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try { return await SendAsync(url, headers, null, timeout ?? DefaultTimeout, ct); }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    static async Task<byte[]?> SendAsync(string url, IReadOnlyDictionary<string, string>? headers, string? form, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("User-Agent", AppAgent);
        if (headers is not null)
            foreach (var (k, v) in headers)
            {
                request.Headers.Remove(k);
                request.Headers.TryAddWithoutValidation(k, v);
            }
        if (form is not null) request.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        try
        {
            using var response = await Client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await ReadLimited(response.Content, cts.Token),
                HttpStatusCode.NotFound => null,
                HttpStatusCode.ServiceUnavailable => throw new HttpUnavailableException(url),
                _ => throw new IOException($"HTTP {(int)response.StatusCode} for {url}"),
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"请求超时：{url}");
        }
    }

    public static string Encode(string value) => Uri.EscapeDataString(value);

    static async Task<byte[]> ReadLimited(HttpContent content, CancellationToken ct)
    {
        const int limit = 8 * 1024 * 1024;
        if (content.Headers.ContentLength > limit) throw new IOException("响应内容超过 8 MB");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > limit) throw new IOException("响应内容超过 8 MB");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>两次请求之间至少间隔一段时间（同移动端 Http.RateLimit）。</summary>
    public sealed class RateLimit(TimeSpan interval)
    {
        readonly SemaphoreSlim _gate = new(1, 1);
        long _last;

        public async Task<T> RunAsync<T>(Func<Task<T>> block, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var wait = _last + (long)interval.TotalMilliseconds - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait, ct);
                try { return await block(); }
                finally { _last = Environment.TickCount64; }
            }
            finally { _gate.Release(); }
        }
    }
}

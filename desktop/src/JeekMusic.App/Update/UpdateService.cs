using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using JeekMusic.Infrastructure;
using JeekMusic.Settings;

namespace JeekMusic.Update;

public sealed record UpdateInfo(int Version, string VersionName, string Notes, string DownloadUrl, long Size, string? Sha256, string Tag);

/// <summary>
/// 程序内更新（需求 UP-2，设计 §15.2）：检查 GitHub Releases 里标签以 desktop-v 开头的最新版本，比较末尾的提交数；
/// 下载 zip，用资源的 digest（SHA-256）校验，解压到 update\new\，确认后由新版本替换程序目录里的文件。
/// 每天最多自动检查一次，可以关闭；手动检查不受限制。
/// </summary>
public sealed class UpdateService(SettingsStore settings)
{
    const string ReleasesUrl = "https://api.github.com/repos/tifish/JeekMusic/releases?per_page=30";
    HttpClient? _http;

    HttpClient Http => _http ??= CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("JeekMusic-Desktop/" + CurrentVersionName);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>当前版本的提交数（1.0.N 里的 N）；本地开发版是 0。</summary>
    public static int CurrentVersion => typeof(UpdateService).Assembly.GetName().Version is { } v ? v.Build : 0;

    public static string CurrentVersionName => $"1.0.{CurrentVersion}";

    /// <summary>程序目录（更新时替换这里的文件）；不可写时（放在 Program Files）只能手动更新。</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>该自动检查了：开关打开、距上次超过 24 小时。</summary>
    public bool DueForAutoCheck =>
        settings.Current.AutoUpdateCheck && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - settings.Current.LastUpdateCheck > 24 * 3600_000L;

    /// <summary>检查新版本；没有新版本时返回 null。<paramref name="manual"/> 为 true 时忽略"跳过此版本"。网络错误抛出异常。</summary>
    public async Task<UpdateInfo?> CheckAsync(bool manual, CancellationToken ct = default)
    {
        settings.Current.LastUpdateCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        settings.Save("update");
        using var response = await Http.GetAsync(ReleasesUrl, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        UpdateInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("desktop-v", StringComparison.Ordinal) || r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            if (!int.TryParse(tag[(tag.LastIndexOf('.') + 1)..], out var version)) continue;
            if (best is not null && version <= best.Version) continue;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                best = new UpdateInfo(version, tag["desktop-v".Length..], r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(),
                    digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null, tag);
                break;
            }
        }
        if (best is null || best.Version <= CurrentVersion) return null;
        if (!manual && best.Version == settings.Current.SkippedUpdateVersion) return null;
        return best;
    }

    public void Skip(UpdateInfo info)
    {
        settings.Current.SkippedUpdateVersion = info.Version;
        settings.Save("update");
    }

    /// <summary>下载并解压到 update\new\；返回新版本 exe 的路径。<paramref name="progress"/> 是 0–1。</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var dir = AppPaths.UpdateDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "update.zip");
        using (var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        if (info.Sha256 is { } expected)
        {
            await using var f = File.OpenRead(zip);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载的文件校验失败");
        }
        var target = Path.Combine(dir, "new");
        ZipFile.ExtractToDirectory(zip, target);
        File.Delete(zip);
        var exe = Directory.EnumerateFiles(target, "JeekMusic.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException("更新包里没有 JeekMusic.exe");
        Log.Info("update", $"已下载 {info.VersionName}：{exe}");
        return exe;
    }

    /// <summary>程序目录是否可写（放在 Program Files 里时不可写，只能手动更新）。</summary>
    public static bool AppDirWritable
    {
        get
        {
            try
            {
                var probe = Path.Combine(AppDir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>启动新版本去替换文件（它等这个进程退出），然后由调用方退出程序。</summary>
    public static void LaunchInstaller(string newExe)
    {
        Process.Start(new ProcessStartInfo(newExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(newExe)!,
            ArgumentList = { "--apply-update", AppDir.TrimEnd('\\'), Environment.ProcessId.ToString() },
        });
    }

    /// <summary>新版本启动后清理 update\。</summary>
    public static void CleanUp()
    {
        try { if (Directory.Exists(AppPaths.UpdateDir) && !AppContext.BaseDirectory.StartsWith(AppPaths.UpdateDir, StringComparison.OrdinalIgnoreCase)) Directory.Delete(AppPaths.UpdateDir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

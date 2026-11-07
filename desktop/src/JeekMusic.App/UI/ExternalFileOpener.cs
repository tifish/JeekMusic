using System.Text.Json.Nodes;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>Shared file-association/MCP flow. Directory I/O must never delay the playback request.</summary>
internal sealed class ExternalFileOpener(AppServices app, MainViewModel navigation, Action playbackRequested) : IDisposable
{
    CancellationTokenSource? _loading;
    int _generation;
    string _state = "idle";
    string? _directory;
    int _trackCount;
    int _failedFiles;

    internal JsonObject Diagnostics() => new()
    {
        ["state"] = _state, ["directory"] = _directory, ["trackCount"] = _trackCount,
        ["failedFiles"] = _failedFiles, ["generation"] = _generation,
        ["policy"] = "play-before-directory-read", ["recursive"] = false, ["persisted"] = false,
    };

    internal async Task OpenAsync(IReadOnlyList<string> files)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var token = (_loading = new CancellationTokenSource()).Token;
        var generation = ++_generation;
        _state = "opening";
        _directory = null;
        _trackCount = _failedFiles = 0;
        var tracks = new List<Track>();
        try
        {
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                try { tracks.Add(await Task.Run(() => app.Library.OpenExternal(path), token)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { token.ThrowIfCancellationRequested(); _failedFiles++; Log.Warn("open", "无法打开文件", e); }
            }
            token.ThrowIfCancellationRequested();
            if (tracks.Count == 0) { _state = "failed"; return; }

            playbackRequested();
            app.Player.Play(null, tracks.Select(t => t.Id).ToArray(), 0, shuffle: false);
            // Keep an explicitly opened multi-file queue intact. Clicking a song in the
            // temporary list subsequently plays that list through the normal UI service.
            var directory = _directory = Path.GetDirectoryName(app.Library.PathOf(tracks[0]))!;
            var initial = tracks.Where(t => string.Equals(Path.GetDirectoryName(app.Library.PathOf(t)), directory, StringComparison.OrdinalIgnoreCase)).ToArray();
            navigation.ShowTemporaryFolder(directory, initial, navigate: true);
            _state = "loading";
            _trackCount = initial.Length;
            Log.Info("open", "已请求播放，开始读取临时目录");
            var siblings = await Task.Run(() => app.Library.ReadExternalDirectory(directory, token), token);
            token.ThrowIfCancellationRequested();
            navigation.ShowTemporaryFolder(directory, siblings, navigate: false);
            _trackCount = siblings.Count;
            _state = "ready";
            Log.Info("open", $"临时目录已就绪：{siblings.Count} 首歌");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (generation != _generation) return;
            _state = "failed";
            Log.Warn("open", "无法读取临时目录，保留当前播放", e);
            navigation.ShowToast(L.T("无法读取同目录歌曲"));
        }
        finally
        {
            if (generation == _generation && !token.IsCancellationRequested && _failedFiles > 0)
                navigation.ShowToast(L.F($"有 {_failedFiles} 个文件无法打开"));
        }
    }

    public void Dispose()
    {
        _generation++;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }
}

using System.Net.NetworkInformation;
using Avalonia.Controls;
using Avalonia.Threading;
using JeekMusic.Covers;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Lyrics;
using JeekMusic.Settings;
using JeekMusic.UI;

namespace JeekMusic.Online;

public sealed partial class OnlineFeature
{
    readonly HashSet<string> _attempted = new();
    DispatcherTimer _backgroundTimer = null!;
    CancellationTokenSource? _backgroundRequest;
    bool _backgroundRunning, _backgroundAgain, _embeddingAttempted, _backgroundPaused;
    public string BackgroundStatus { get; private set; } = "";
    public event Action? BackgroundChanged;

    void StartLibraryWork()
    {
        _backgroundTimer = UiTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, () => { _backgroundTimer.Stop(); _ = LibraryWork(); });
        _app.Library.SnapshotChanged += ScheduleLibraryWork;
        _app.Settings.Changed += category =>
        {
            if (category != "online") return;
            _attempted.Clear(); _embeddingAttempted = false; _backgroundPaused = false; _backgroundRequest?.Cancel(); ScheduleLibraryWork();
        };
        NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
        _app.OnExit += () => { _backgroundTimer.Stop(); _backgroundRequest?.Cancel(); NetworkChange.NetworkAvailabilityChanged -= NetworkChanged; };
        ScheduleLibraryWork();
    }

    void NetworkChanged(object? sender, NetworkAvailabilityEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_stopped) return;
        _attempted.Clear(); ScheduleLibraryWork();
    });

    void ScheduleLibraryWork()
    {
        if (_stopped || _backgroundPaused) return;
        _backgroundTimer.Stop(); _backgroundTimer.Start();
    }

    void BackgroundStatusChanged(string text) { BackgroundStatus = text; BackgroundChanged?.Invoke(); }

    async Task LibraryWork()
    {
        if (_stopped || _backgroundPaused) return;
        if (_backgroundRunning) { _backgroundAgain = true; return; }
        _backgroundRunning = true;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _backgroundRequest = request;
        var ct = request.Token;
        try
        {
            var tracks = _app.Library.Snapshot.Tracks;
            if (tracks.Count == 0) return;
            await EmbedPrevious(tracks, ct);
            var settings = _app.Settings.Current;
            bool covers = settings.OnlineCovers && AutoAllowed(settings.CoverFetchMode);
            bool lyrics = settings.OnlineLyrics && AutoAllowed(settings.LyricsFetchMode);
            if (!covers && !lyrics)
            {
                BackgroundStatusChanged(settings.OnlineCovers && settings.CoverFetchMode != FetchMode.Manual || settings.OnlineLyrics && settings.LyricsFetchMode != FetchMode.Manual
                    ? L.T("等待符合设置的网络") : "");
                return;
            }
            int done = 0;
            foreach (var track in tracks)
            {
                ct.ThrowIfCancellationRequested();
                if (_app.Library.PathOf(track) is null) continue;
                var signature = track.TrackKey + "|" + track.Size + "|" + track.Mtime;
                var coverKey = "C|" + CoverService.CacheKey(track);
                bool cover = covers && _attempted.Add(coverKey);
                bool lyric = lyrics && _attempted.Add("L|" + signature);
                if (!cover && !lyric) continue;
                BackgroundStatusChanged(L.F($"后台获取 {++done}：{track.Title}"));
                try { await FetchPair(track, cover, lyric, ct); }
                catch (OperationCanceledException)
                {
                    if (cover) _attempted.Remove(coverKey);
                    if (lyric) _attempted.Remove("L|" + signature);
                    throw;
                }
                catch (Exception e) { Log.Warn("online", "后台获取失败", e); }
            }
            BackgroundStatusChanged(L.T("后台获取完成"));
        }
        catch (OperationCanceledException) { if (!_stopped) BackgroundStatusChanged(L.T("已停止后台获取")); }
        catch (Exception e) { Log.Warn("online", "处理下载内容失败", e); }
        finally
        {
            _backgroundRequest = null; _backgroundRunning = false;
            if (_backgroundAgain) { _backgroundAgain = false; ScheduleLibraryWork(); }
        }
    }

    async Task FetchPair(Track track, bool covers, bool lyrics, CancellationToken ct)
    {
        var settings = _app.Settings.Current;
        bool combined = covers && lyrics && settings.CoverSaveMode == CoverSaveMode.Embed && settings.LyricsSaveLocation == LyricsSaveLocation.Embed;
        var lyricTracks = combined ? Covers.AlbumOf(track) : new[] { track };
        try
        {
            if (lyrics) foreach (var item in lyricTracks)
            {
                await Fetch(item, DownloadKind.Lyrics, ct, stageLyrics: combined);
                if (combined) _attempted.Add("L|" + item.TrackKey + "|" + item.Size + "|" + item.Mtime);
            }
            if (covers) await Fetch(track, DownloadKind.Covers, ct);
        }
        finally
        {
            // If no artwork was found, persist the staged lyrics on their own.
            if (combined && !ct.IsCancellationRequested && !_stopped)
                foreach (var item in lyricTracks)
                    if (_app.Db.CachedLyrics(item.TrackKey)?.content is { } text) await SaveLyrics(item, text);
        }
    }

    async Task<bool> FillAlbum(Track track, CancellationToken ct)
    {
        var mode = _app.Settings.Current.CoverSaveMode;
        if (mode == CoverSaveMode.HiddenFolder) return false;
        var missing = new List<Track>();
        foreach (var t in Covers.AlbumOf(track))
        {
            ct.ThrowIfCancellationRequested();
            if (!await Covers.HasOwnCoverAsync(t, mode)) missing.Add(t);
        }
        if (missing.Count == 0) return false;
        await _writes.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                lock (_ioGate)
                {
                    ct.ThrowIfCancellationRequested(); _lifetime.Token.ThrowIfCancellationRequested();
                    if (Covers.AlbumImage(track) is not { } image) return false;
                    var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (mode == CoverSaveMode.Embed && _app.Settings.Current.LyricsSaveLocation == LyricsSaveLocation.Embed)
                        foreach (var t in missing.Where(t => !t.IsCue))
                            if (_app.Library.PathOf(t) is { } path && _app.Db.CachedLyrics(t.TrackKey)?.content is { } text) staged[path] = text;
                    var saved = Covers.Save(missing, image, mode, staged, preserveShared: true);
                    if (saved is not null)
                        foreach (var t in missing)
                            if (_app.Library.PathOf(t) is { } path && saved.AlsoDone.Contains(path)) CleanEmbeddedLyrics(t);
                    return saved is not null;
                }
            }, ct);
        }
        finally { _writes.Release(); }
    }

    async Task EmbedPrevious(IReadOnlyList<Track> tracks, CancellationToken ct)
    {
        if (_embeddingAttempted) return;
        var settings = _app.Settings.Current;
        bool covers = settings.CoverSaveMode == CoverSaveMode.Embed && !settings.CoversEmbedded;
        bool lyrics = settings.LyricsSaveLocation == LyricsSaveLocation.Embed && !settings.LyricsEmbedded;
        if (!covers && !lyrics) return;
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (_app.Library.PathOf(track) is null) continue;
            if (lyrics && !track.IsCue)
            {
                var found = await Task.Run(() => _app.Lyrics.Load(track), ct);
                ct.ThrowIfCancellationRequested();
                if (found is not null && (found.Path is not null || _app.Db.CachedLyrics(track.TrackKey)?.content is not null))
                {
                    var text = _app.Db.CachedLyrics(track.TrackKey)?.content;
                    if (found.Path is { } path) text = Core.Text.CharsetDetector.Decode(await File.ReadAllBytesAsync(path, ct));
                    if (text is not null && _app.Library.PathOf(track) is { } target)
                    {
                        await SaveLyrics(track, text, LyricsSaveLocation.AppCache, staging: true);
                        staged[target] = text;
                    }
                }
            }
        }
        if (covers)
        {
            await _writes.WaitAsync(ct);
            try { await Task.Run(() => { lock (_ioGate) { ct.ThrowIfCancellationRequested(); Covers.EmbedDownloaded(tracks, ct, staged, t =>
            {
                CleanEmbeddedLyrics(t);
                if (_app.Library.PathOf(t) is { } path) staged.Remove(path);
            }); } }, ct); }
            finally { _writes.Release(); }
        }
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (_app.Library.PathOf(track) is { } path && staged.Remove(path, out var text)) await SaveLyrics(track, text);
        }
        ct.ThrowIfCancellationRequested();
        // Retry unfinished writes on the next launch; successful ones no longer have downloaded copies.
        // In-use files are also queued for the next track boundary in this session.
        _embeddingAttempted = true;
        if (covers) settings.CoversEmbedded = !tracks.Any(t => Covers.HasDownloaded(t));
        if (lyrics) settings.LyricsEmbedded = !tracks.Where(t => !t.IsCue).Any(t =>
            _app.Db.CachedLyrics(t.TrackKey)?.content is not null ||
            _app.Library.Snapshot.Folder(t.FolderId) is { } f &&
            (File.Exists(LyricsService.HiddenPath(f, t)) || LyricsService.SidecarNames(t).Any(n => File.Exists(Path.Combine(f.Root + t.Dir, n)))));
        _app.Settings.Save("online-migration");
    }

    void PauseBackground()
    {
        _backgroundPaused = true; _backgroundAgain = false;
        _backgroundTimer.Stop(); _backgroundRequest?.Cancel();
        foreach (var batch in _batches) batch.Cancel();
        BackgroundStatusChanged(L.T("已停止后台获取"));
    }

    void CleanEmbeddedLyrics(Track track)
    {
        if (_app.Library.Snapshot.Folder(track.FolderId) is not { } folder) return;
        foreach (var side in LyricsService.SidecarNames(track).Select(n => Path.Combine(folder.Root + track.Dir, n)).Append(LyricsService.HiddenPath(folder, track)))
            TryDeleteLyrics(side);
        _app.Db.DeleteCachedLyrics(track.TrackKey);
        lock (_pendingLyrics) _pendingLyrics.Remove(track.TrackKey);
    }

    async Task<bool> EnsureOnline(Window owner, DownloadKind kind)
    {
        var settings = _app.Settings.Current;
        if (kind == DownloadKind.Covers ? settings.OnlineCovers : settings.OnlineLyrics) return true;
        if (!await Dialogs.Confirm(owner, L.T("启用在线获取"), L.T("搜索会把歌名、艺术家和专辑发送给设置中勾选的来源。是否开启？"), L.T("开启"))) return false;
        if (kind == DownloadKind.Covers) settings.OnlineCovers = true; else settings.OnlineLyrics = true;
        _app.Settings.Save("online");
        return true;
    }
}

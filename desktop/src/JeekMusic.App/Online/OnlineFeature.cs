using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JeekMusic.Covers;
using JeekMusic.Core.Lyrics;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Lyrics;
using JeekMusic.Platform;
using JeekMusic.Settings;
using JeekMusic.UI;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Online;

/// <summary>本地封面、在线获取、手动搜索和批量保存的入口。默认不发网络请求。</summary>
public sealed partial class OnlineFeature : IFeature
{
    public static OnlineFeature Current { get; private set; } = null!;
    AppServices _app = null!;
    Shell _shell = null!;
    public CoverService Covers { get; private set; } = null!;
    public CoverClient CoverClient { get; private set; } = null!;
    public OnlineLyricsClient LyricsClient { get; private set; } = null!;
    SourceCheck _check = null!;
    CancellationTokenSource? _trackRequest;
    readonly CancellationTokenSource _lifetime = new();
    readonly Lock _ioGate = new();
    volatile bool _stopped;
    readonly HashSet<string> _pendingCovers = new();
    readonly Dictionary<string, (Track track, string text)> _pendingLyrics = new();
    readonly SemaphoreSlim _writes = new(1, 1);
    readonly SemaphoreSlim _fetches = new(1, 1);
    internal ResourceBatch Batches { get; private set; } = null!;

    public OnlineFeature() { }
    internal OnlineFeature(AppServices app) => InitializeServices(app);

    void InitializeServices(AppServices app)
    {
        _app = app;
        app.Online = this;
        Covers = new CoverService(app.Library, app.Settings);
        var netEase = new NetEaseMusic(); var qq = new QqMusic(); var lrc = new LrcLibClient();
        CoverClient = new CoverClient(app.Settings, netEase, qq);
        LyricsClient = new OnlineLyricsClient(app.Settings, lrc, netEase, qq);
        _check = new SourceCheck(netEase, qq, lrc);
        Batches = new ResourceBatch((t, kind, ct) => Fetch(t, kind, ct, ignoreMiss: true), _lifetime.Token);
        app.OnExit += () => _lifetime.Cancel();
    }

    public void Start(AppServices app, Shell shell)
    {
        Current = this; _shell = shell;
        InitializeServices(app);
        shell.Main.SetCoverService(Covers);
        Covers.IsInUse = InUse;
        Covers.DeferEmbed = t => { lock (_pendingCovers) _pendingCovers.Add(t.TrackKey); };
        app.Player.TrackChanged += t => { _ = LoadTrack(t); _ = FlushPending(); };
        app.Player.StateChanged += () => { if (app.Player.IsIdle) _ = FlushPending(); };
        Covers.Changed += key => { if (app.Player.CurrentTrack is { } t && CoverService.CacheKey(t) == key) _ = LoadCover(t); };
        shell.Main.ViewModel.Navigated += () => _ = LoadAlbumCover();
        app.Library.SnapshotChanged += () => _ = LoadAlbumCover();
        Covers.Changed += key => { _ = LoadAlbumCover(); };
        shell.Main.LyricsPanelCreated += panel => panel.SearchRequested += t => _ = SearchLyrics(shell.Main, t);
        app.OnExit += () => { _lifetime.Cancel(); lock (_ioGate) _stopped = true; LibraryRefresh.Stop(); };
        SettingsWindow.Register(L.T("歌词和封面"), 60, BuildSettings);
        StartLibraryWork();
        _ = LoadTrack(app.Player.CurrentTrack);
        _ = LoadAlbumCover();
    }

    async Task LoadAlbumCover()
    {
        var vm = _shell.Main.ViewModel;
        vm.AlbumCover = null;
        if (vm.Current is not JeekMusic.UI.ViewModels.NavTarget.Album album ||
            _app.Library.Snapshot.Albums.GetValueOrDefault(album.AlbumKey)?.FirstOrDefault() is not { } track) return;
        try
        {
            var bitmap = await Covers.BitmapAsync(track, true, _lifetime.Token);
            if (!_stopped && vm.Current == album) vm.AlbumCover = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Warn("cover", "加载专辑封面失败", e); }
    }

    bool InUse(Track t) => _app.Player.IsFileInUse(t);

    async Task LoadCover(Track t)
    {
        try
        {
            var bitmap = await Covers.BitmapAsync(t, large: true);
            if (_stopped || _app.Player.CurrentTrack?.TrackKey != t.TrackKey) return;
            _shell.Main.ViewModel.Player.Cover = bitmap;
            _shell.RefreshMediaCover();
        }
        catch (Exception e) { Log.Warn("cover", "加载封面失败", e); }
    }

    async Task LoadTrack(Track? t)
    {
        if (_stopped) return;
        _trackRequest?.Cancel(); _trackRequest?.Dispose();
        _trackRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _trackRequest.Token;
        _shell.Main.ViewModel.Player.Cover = null;
        if (t is null) return;
        await LoadCover(t);
        var s = _app.Settings.Current;
        try
        {
            await FetchPair(t, s.OnlineCovers && AutoAllowed(s.CoverFetchMode), s.OnlineLyrics && AutoAllowed(s.LyricsFetchMode), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Warn("online", "自动获取失败", e); }
    }

    static bool AutoAllowed(FetchMode mode) => mode != FetchMode.Manual && SystemState.IsOnline &&
        (mode != FetchMode.Unmetered || !SystemState.IsMeteredNetwork);

    public async Task<bool> Fetch(Track t, DownloadKind kind, CancellationToken ct, bool stageLyrics = false, bool ignoreMiss = false)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = request.Token;
        await _fetches.WaitAsync(ct);
        try { return await FetchCore(t, kind, ct, stageLyrics, ignoreMiss); }
        finally { _fetches.Release(); }
    }

    async Task<bool> FetchCore(Track t, DownloadKind kind, CancellationToken ct, bool stageLyrics, bool ignoreMiss)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long retry = 7L * 86400000;
        if (kind == DownloadKind.Covers)
        {
            if (await Covers.HasCoverAsync(t)) return await FillAlbum(t, ct);
            if (string.IsNullOrWhiteSpace(t.ArtistDisplay)) return false;
            ct.ThrowIfCancellationRequested();
            var key = t.AlbumKey ?? t.TrackKey;
            if (!ignoreMiss && _app.Db.CoverMissAt(key) is { } miss && now - miss < 30L * 86400000) return false;
            var bytes = await CoverClient.FetchBestAsync(t.Title, t.ArtistDisplay, t.Album, ct);
            ct.ThrowIfCancellationRequested();
            if (bytes is null) { _app.Db.SetCoverMiss(key, true); return false; }
            await SaveCover(t, bytes);
            ct.ThrowIfCancellationRequested();
            _app.Db.SetCoverMiss(key, false);
        }
        else
        {
            if (await Task.Run(() => _app.Lyrics.Load(t), ct) is not null) return false;
            ct.ThrowIfCancellationRequested();
            if (_app.Db.CachedLyrics(t.TrackKey) is { content: null, fetchedAt: var miss } && now - miss < retry) return false;
            var found = await LyricsClient.BestAsync(t.Title, t.ArtistDisplay, t.Album, t.DurationMs / 1000, ct);
            ct.ThrowIfCancellationRequested();
            if (found?.Text is not { } text) { _app.Db.CacheLyrics(t.TrackKey, null, false, null); return false; }
            await SaveLyrics(t, text, stageLyrics ? LyricsSaveLocation.AppCache : null, staging: stageLyrics);
        }
        return true;
    }

    public async Task SaveCover(Track t, byte[] bytes)
    {
        await _writes.WaitAsync(_lifetime.Token);
        try
        {
            var tracks = Covers.AlbumOf(t);
            var mode = _app.Settings.Current.CoverSaveMode;
            var saved = await Task.Run(() =>
            {
                lock (_ioGate)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    var lyrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (mode == CoverSaveMode.Embed && _app.Settings.Current.LyricsSaveLocation == LyricsSaveLocation.Embed)
                        foreach (var track in tracks.Where(t => !t.IsCue))
                            if (_app.Library.PathOf(track) is { } path && _app.Db.CachedLyrics(track.TrackKey)?.content is { } text) lyrics[path] = text;
                    var result = Covers.Save(tracks, bytes, mode, lyrics);
                    if (result is not null)
                        foreach (var track in tracks)
                            if (_app.Library.PathOf(track) is { } path && result.AlsoDone.Contains(path)) CleanEmbeddedLyrics(track);
                    return result;
                }
            });
            if (saved is null) throw new IOException("封面保存失败");
            if (mode == CoverSaveMode.Embed && tracks.Any(Covers.HasDownloaded))
            {
                _app.Settings.Current.CoversEmbedded = false;
                _app.Settings.Save("online-migration");
            }
        }
        finally { _writes.Release(); }
    }

    public async Task SaveLyrics(Track t, string text, LyricsSaveLocation? location = null, bool staging = false)
    {
        var mode = location ?? _app.Settings.Current.LyricsSaveLocation;
        await _writes.WaitAsync(_lifetime.Token);
        LyricsSaveLocation actual;
        try { actual = await Task.Run(() => { lock (_ioGate) { _lifetime.Token.ThrowIfCancellationRequested(); return WriteLyrics(t, text, mode, staging); } }); }
        finally { _writes.Release(); }
        if (!_stopped) _app.Lyrics.Invalidate(t);
        if (!_stopped && _app.Settings.Current.LyricsSaveLocation == LyricsSaveLocation.Embed && (staging || actual != LyricsSaveLocation.Embed))
        {
            _app.Settings.Current.LyricsEmbedded = false;
            _app.Settings.Save("online-migration");
        }
        if (!_stopped && !staging && actual != mode) _shell.Main.ViewModel.ShowToast(actual == LyricsSaveLocation.HiddenFolder
            ? L.T("歌词已保存到音乐目录的隐藏文件夹") : L.T("歌词已保存到应用缓存"));
    }

    LyricsSaveLocation WriteLyrics(Track t, string text, LyricsSaveLocation mode, bool staging)
    {
        var folder = _app.Library.Snapshot.Folder(t.FolderId);
        // 先持久化缓存；介质离线、格式不支持、文件被占用时歌词仍然可用。
        _app.Db.CacheLyrics(t.TrackKey, text, LrcParser.Parse(text).Synced, "online");
        if (staging || folder is not { Available: true }) return LyricsSaveLocation.AppCache;
        var hidden = LyricsService.HiddenPath(folder, t);
        if (mode == LyricsSaveLocation.Embed && t.IsCue) mode = LyricsSaveLocation.HiddenFolder;
        string? target = mode switch
        {
            LyricsSaveLocation.Sidecar => Path.Combine(folder.Root + t.Dir, LyricsService.SidecarNames(t).First()),
            LyricsSaveLocation.HiddenFolder => hidden,
            _ => null,
        };
        if (target is not null)
        {
            if (TryWriteLyrics(target, text))
            {
                if (target != hidden) TryDeleteLyrics(hidden);
                _app.Db.DeleteCachedLyrics(t.TrackKey);
                return mode;
            }
            if (target != hidden && TryWriteLyrics(hidden, text))
            {
                _app.Db.DeleteCachedLyrics(t.TrackKey);
                return LyricsSaveLocation.HiddenFolder;
            }
            return LyricsSaveLocation.AppCache;
        }
        if (mode == LyricsSaveLocation.Embed && !t.IsCue && _app.Library.PathOf(t) is { } path)
        {
            if (InUse(t)) { lock (_pendingLyrics) _pendingLyrics[t.TrackKey] = (t, text); }
            else
            {
                _app.Library.Watcher.Ignore(path);
                if (TagWriter.CanWrite(path) && TagWriter.Write(path, null, text))
                {
                    var info = new FileInfo(path);
                    _app.Db.MarkEmbedded(t.FolderId, t.Dir, t.FileName, false, true, info.Length, info.LastWriteTimeUtc.Ticks);
                    LibraryRefresh.Soon(_app.Library);
                    CleanEmbeddedLyrics(t);
                    return LyricsSaveLocation.Embed;
                }
            }
        }
        if (mode == LyricsSaveLocation.Embed && TryWriteLyrics(hidden, text))
        {
            _app.Db.DeleteCachedLyrics(t.TrackKey);
            return LyricsSaveLocation.HiddenFolder;
        }
        TryDeleteLyrics(hidden); // App cache must take precedence after a manual replacement.
        return LyricsSaveLocation.AppCache;
    }

    static bool TryWriteLyrics(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", text, new System.Text.UTF8Encoding(false));
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("lyrics", "保存歌词文件失败", e); return false; }
    }

    static void TryDeleteLyrics(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("lyrics", "清理旧歌词失败", e); }
    }

    async Task FlushPending()
    {
        try
        {
            List<Track> covers;
            lock (_pendingCovers)
            {
                covers = _pendingCovers.Select(_app.Library.ByKey).OfType<Track>().Where(t => !InUse(t)).ToList();
                foreach (var t in covers) _pendingCovers.Remove(t.TrackKey);
            }
            if (covers.Count > 0)
            {
                await _writes.WaitAsync(_lifetime.Token);
                try { await Task.Run(() => { lock (_ioGate)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (_app.Settings.Current.LyricsSaveLocation == LyricsSaveLocation.Embed)
                        foreach (var t in covers.SelectMany(Covers.AlbumOf).Where(t => !t.IsCue))
                            if (_app.Library.PathOf(t) is { } path && _app.Db.CachedLyrics(t.TrackKey)?.content is { } text) staged[path] = text;
                    Covers.EmbedDownloaded(covers, _lifetime.Token, staged, CleanEmbeddedLyrics);
                } }); }
                finally { _writes.Release(); }
            }
            List<(Track track, string text)> lyrics;
            lock (_pendingLyrics)
            {
                lyrics = _pendingLyrics.Values.Where(x => !InUse(x.track)).ToList();
                foreach (var x in lyrics) _pendingLyrics.Remove(x.track.TrackKey);
            }
            foreach (var x in lyrics) await SaveLyrics(x.track, x.text);
        }
        catch (Exception e) { Log.Warn("online", "延迟写入失败", e); }
    }

    public async Task Batch(Window owner, IReadOnlyList<Track> tracks, DownloadKind kind, string scope = "selected", string source = "")
    {
        if (tracks.Count == 0) return;
        if (Batches.Busy) { _app.Navigation?.ShowToast(L.T("已有批量获取任务，请等待完成或取消。")); return; }
        var job = Batches.Reserve(tracks, kind, scope, source);
        Window? progress = null;
        Action? update = null;
        try
        {
            if (!await EnsureOnline(owner, kind) || job.Cancellation.IsCancellationRequested) return;
            if (job.Tracks.Length > 1 && !await Dialogs.Confirm(owner, L.T("批量获取"),
                (source.Length > 0 ? source + "\n" : "") + L.F($"将检查 {job.Tracks.Length} 首歌曲，并按所选保存位置保存缺失内容。选择写入音乐文件时会修改文件。"), L.T("开始"))) return;
            var status = new TextBlock { Text = L.T("准备获取…"), TextWrapping = TextWrapping.Wrap, MaxLines = 4 };
            var cancel = SettingsWindow.Action(L.T("取消"), () => Batches.Cancel(job));
            var window = new Window { Title = kind == DownloadKind.Covers ? L.T("获取封面") : L.T("获取歌词"), Width = 460, Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
            progress = window;
            window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { status, Row(cancel, SettingsWindow.Action(L.T("转到后台"), window.Hide)) } };
            window.Closing += (_, _) => Batches.Cancel(job);
            update = () =>
            {
                var unit = kind == DownloadKind.Covers ? L.T("专辑/歌曲") : L.T("歌曲");
                status.Text = L.F($"目标 {job.Tracks.Length} 首歌曲；已处理 {job.Done} / {job.Total} {unit}\n已保存 {job.Saved}，失败 {job.Failed}") +
                    "\n" + (job.State == "cancelling" ? L.T("正在取消…") : job.Title);
                cancel.IsEnabled = job.State != "cancelling";
                if (!window.IsVisible) BackgroundStatusChanged(status.Text);
            };
            Batches.Changed += update;
            window.Show(owner);
            await Batches.Run(job);
            var summary = (job.State == "cancelled" ? L.T("已取消") : job.State == "failed" ? L.T("批量获取失败") : L.T("批量获取完成")) +
                " · " + L.F($"已处理 {job.Done} 项，保存 {job.Saved} 项，失败 {job.Failed} 项");
            BackgroundStatusChanged(summary);
            _app.Navigation?.ShowToast(summary);
        }
        finally
        {
            if (update is not null) Batches.Changed -= update;
            if (job.State is "preparing" or "cancelling") Batches.FinishPreparation(job);
            progress?.Close();
        }
    }

    public async Task SearchLyrics(Window owner, Track t) { if (await EnsureOnline(owner, DownloadKind.Lyrics)) await SearchWindow.ShowLyrics(owner, this, t); }
    public async Task SearchCover(Window owner, Track t) { if (await EnsureOnline(owner, DownloadKind.Covers)) await SearchWindow.ShowCover(owner, this, t); }

    Control BuildSettings(SettingsContext c)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Note(L.T("在线获取默认关闭。开启后会把歌名、艺术家和专辑发送给所选来源。")));
        p.Children.Add(Toggle(L.T("启用在线歌词"), c.S.OnlineLyrics, v => { c.S.OnlineLyrics = v; c.Save("online"); }));
        p.Children.Add(Labeled(L.T("歌词获取"), Choice([(FetchMode.Manual, L.T("手动")), (FetchMode.Auto, L.T("播放时自动获取")), (FetchMode.Unmetered, L.T("仅不计流量网络自动获取"))], c.S.LyricsFetchMode, v => { c.S.LyricsFetchMode = v; c.Save("online"); })));
        p.Children.Add(Labeled(L.T("歌词保存"), Choice([(LyricsSaveLocation.Sidecar, L.T("同名 .lrc")), (LyricsSaveLocation.HiddenFolder, L.T("音乐文件夹的隐藏目录")), (LyricsSaveLocation.Embed, L.T("写入音乐文件")), (LyricsSaveLocation.AppCache, L.T("应用内缓存"))], c.S.LyricsSaveLocation, v => { c.S.LyricsSaveLocation = v; c.S.LyricsEmbedded = false; c.Save("online"); })));
        p.Children.Add(Toggle(L.T("启用在线封面"), c.S.OnlineCovers, v => { c.S.OnlineCovers = v; c.Save("online"); }));
        p.Children.Add(Labeled(L.T("封面获取"), Choice([(FetchMode.Manual, L.T("手动")), (FetchMode.Auto, L.T("播放时自动获取")), (FetchMode.Unmetered, L.T("仅不计流量网络自动获取"))], c.S.CoverFetchMode, v => { c.S.CoverFetchMode = v; c.Save("online"); })));
        p.Children.Add(Labeled(L.T("封面保存"), Choice([(CoverSaveMode.HiddenFolder, L.T("音乐文件夹的隐藏目录")), (CoverSaveMode.Sidecar, L.T("同名图片")), (CoverSaveMode.Embed, L.T("写入音乐文件"))], c.S.CoverSaveMode, v => { c.S.CoverSaveMode = v; c.S.CoversEmbedded = false; c.Save("online"); })));
        p.Children.Add(SubHeading(L.T("在线来源")));
        foreach (var source in Enum.GetValues<Settings.OnlineSource>())
        {
            var core = Sources.ToCore(source);
            p.Children.Add(Note(Sources.Name(core) + " — " + Sources.Note(core)));
            void Change(List<Settings.OnlineSource> list, bool on) { if (!on && list.Count == 1) { c.Window.Build(); return; } list.Remove(source); if (on) list.Add(source); c.Save("online"); }
            if (source is not Settings.OnlineSource.ITunes and not Settings.OnlineSource.MusicBrainz)
                p.Children.Add(Toggle(L.T("歌词"), c.S.LyricsSources.Contains(source), v => Change(c.S.LyricsSources, v)));
            if (source != Settings.OnlineSource.LrcLib)
                p.Children.Add(Toggle(L.T("封面"), c.S.CoverSources.Contains(source), v => Change(c.S.CoverSources, v)));
        }
        p.Children.Add(SettingsWindow.Action(L.T("检测来源连接"), async () =>
        {
            var result = await _check.CheckAsync(c.S.CoverSources.Concat(c.S.LyricsSources).Select(Sources.ToCore).Distinct());
            await Dialogs.Info(c.Window, L.T("来源连接"), string.Join('\n', result.Select(r => Sources.Name(r.Source) + (r.ImageHost ? L.T(" 图片服务器") : "") + ": " + (r.Millis is { } ms ? $"{ms} ms" : L.T("连接失败")))));
        }));
        p.Children.Add(Row(SettingsWindow.Action(L.T("获取曲库缺失歌词"), () => _ = Batch(c.Window, c.App.Library.Snapshot.Tracks, DownloadKind.Lyrics, "library", L.T("曲库"))),
            SettingsWindow.Action(L.T("获取曲库缺失封面"), () => _ = Batch(c.Window, c.App.Library.Snapshot.Tracks, DownloadKind.Covers, "library", L.T("曲库")))));
        var background = Note(BackgroundStatus);
        p.Children.Add(background);
        void UpdateBackground() => background.Text = BackgroundStatus;
        BackgroundChanged += UpdateBackground;
        background.DetachedFromVisualTree += (_, _) => BackgroundChanged -= UpdateBackground;
        p.Children.Add(SettingsWindow.Action(L.T("停止后台获取"), PauseBackground));
        p.Children.Add(SettingsWindow.Action(L.T("清除歌词缓存"), async () =>
        {
            if (!await Dialogs.Confirm(c.Window, L.T("清除歌词缓存"), L.T("清除应用内的歌词缓存和未找到记录？音乐文件夹中的歌词文件将保留。"))) return;
            PauseBackground(); _trackRequest?.Cancel();
            try
            {
                await _fetches.WaitAsync(_lifetime.Token);
                try
                {
                    await _writes.WaitAsync(_lifetime.Token);
                    try { lock (_ioGate)
                    {
                        _lifetime.Token.ThrowIfCancellationRequested();
                        _app.Db.ClearLyricsCache();
                        _app.Lyrics.ClearMemoryCache();
                        lock (_pendingLyrics) _pendingLyrics.Clear();
                    } }
                    finally { _writes.Release(); }
                }
                finally { _fetches.Release(); }
                if (!_stopped && _app.Player.CurrentTrack is { } track) _app.Lyrics.Invalidate(track);
            }
            catch (OperationCanceledException) { }
        }));
        p.Children.Add(SubHeading(L.T("桌面歌词")));
        var size = new Slider { Minimum = 16, Maximum = 72, Value = c.S.DesktopLyricsFontSize, Width = 240 };
        size.ValueChanged += (_, _) => { c.S.DesktopLyricsFontSize = size.Value; c.Save("lyrics-window"); };
        p.Children.Add(Labeled(L.T("字号"), size));
        var color = new TextBox { Text = c.S.DesktopLyricsColor, Width = 160 };
        color.LostFocus += (_, _) => { if (Color.TryParse(color.Text, out _)) { c.S.DesktopLyricsColor = color.Text!; c.Save("lyrics-window"); } };
        p.Children.Add(Labeled(L.T("颜色（#RRGGBB）"), color));
        p.Children.Add(Toggle(L.T("锁定桌面歌词（鼠标穿透）"), c.S.DesktopLyricsLocked, v => { c.S.DesktopLyricsLocked = v; c.Save("lyrics-window"); }));
        return p;
    }
}

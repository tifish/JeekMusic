using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using JeekMusic.Analysis;
using JeekMusic.Core.Library;
using JeekMusic.Core.Sync;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.UI;
using static JeekMusic.Core.Sync.SyncMerge;
using static JeekMusic.UI.SettingsWindow;

namespace JeekMusic.Sync;

/// <summary>每台设备仅写自己的文件；文件变化后合并，写入采用临时文件和上一版备份。</summary>
public sealed class SyncFeature : IFeature
{
    AppServices _app = null!;
    Shell _shell = null!;
    JsonObject _device = null!;
    DispatcherTimer _timer = null!;
    bool _running, _again, _applying, _stopped, _restoreDeclined;
    readonly Dictionary<string, string> _lastWritten = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _fileGate = new();
    readonly Dictionary<string, string> _lastFeatures = new(StringComparer.OrdinalIgnoreCase);
    int _referenceVersion = -1;
    // Reuse between changes in a burst, but do not pin every path for the lifetime of the app.
    WeakReference<Dictionary<string, string>>? _references;
    IReadOnlyList<JsonObject> _devices = [];
    public string Status { get; private set; } = L.T("等待曲库");
    public event Action? Changed;

    public void Start(AppServices app, Shell shell)
    {
        _app = app; _shell = shell;
        try { _device = JsonNode.Parse(File.ReadAllText(AppPaths.Device))!.AsObject(); }
        catch (Exception e) when (e is IOException or JsonException) { _device = new JsonObject(); }
        if (string.IsNullOrEmpty(Str(_device["id"]))) _device["id"] = Guid.NewGuid().ToString("N");
        if (string.IsNullOrEmpty(Str(_device["name"]))) _device["name"] = Environment.MachineName;
        _device["platform"] = "windows";
        _device["app_version"] = typeof(SyncFeature).Assembly.GetName().Version?.ToString();
        File.WriteAllText(AppPaths.Device, _device.ToJsonString());
        _timer = UiTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, () => { _timer.Stop(); _ = Synchronize(); });
        app.Library.SnapshotChanged += Schedule;
        app.Library.SyncFolderChanged += _ => Dispatcher.UIThread.Post(Schedule);
        app.Playlists.Changed += _ => Schedule();
        shell.Main.NavOrderChanged += Schedule;
        app.Lyrics.OffsetChanged += Schedule;
        app.Settings.Changed += c => { if (c == "rules") Schedule(); };
        AnalysisFeature.Current.ResultsChanged += Schedule;
        app.OnExit += Stop;
        SettingsWindow.Register(L.T("同步"), 80, BuildSettings);
        Schedule();
    }

    void Schedule()
    {
        if (_stopped || _applying) return;
        _timer.Stop(); _timer.Start();
    }

    void Stop()
    {
        _timer.Stop();
        lock (_fileGate)
        {
            _stopped = true;
            if (_app.Db.GetValue("sync_initialized") is null) return;
            try
            {
                var state = Capture(References());
                state["format"] = 2; state["device"] = _device.DeepClone();
                state["written_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var folder in _app.Library.Snapshot.Folders.Where(f => f.Available))
                    try { AtomicWrite(Path.Combine(folder.Root, ".jeekmusic", "sync", Str(_device["id"]) + ".json"), state.ToJsonString()); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("sync", "退出时写入同步文件失败", e); }
            }
            catch (Exception e) { Log.Warn("sync", "退出时保存同步数据失败", e); }
        }
    }

    internal static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".prev");
        else File.Move(tmp, path);
    }

    Dictionary<string, string> References()
    {
        if (_referenceVersion == _app.Library.Snapshot.Version && _references is not null && _references.TryGetTarget(out var cached)) return cached;
        var refs = new Dictionary<string, string>();
        if (_app.Db.GetValue("sync_references") is { } text)
            foreach (var p in JsonNode.Parse(text)!.AsObject()) refs[p.Key] = Str(p.Value);
        var snapshot = _app.Library.Snapshot;
        var candidates = snapshot.ByKey.Values.Select(t => (t, reference: t.RelativePath.Replace('\\', '/') + (t.CueTrackNo is { } n ? $"#{n:00}" : ""))).ToList();
        var duplicates = candidates.GroupBy(x => x.reference, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, reference) in candidates)
            refs[t.TrackKey] = duplicates.Contains(reference) ? snapshot.Folder(t.FolderId)!.DisplayName + "/" + reference : reference;
        _referenceVersion = snapshot.Version; _references = new(refs);
        return refs;
    }

    JsonObject Capture(Dictionary<string, string> refs)
    {
        string Reference(string key) => key.StartsWith("foreign:", StringComparison.Ordinal) ? key[8..] : refs.GetValueOrDefault(key, key);
        var state = new JsonObject();
        var s = _app.Settings.Current;
        state["rules"] = new JsonObject { ["updated_at"] = s.RulesUpdatedAt, ["value"] = new JsonObject
        {
            ["artist_separators"] = Strings(s.ArtistSeparators), ["artist_exceptions"] = Strings(s.ArtistExceptions),
            ["infer_from_file_name"] = s.InferFromFileName, ["file_name_format"] = s.FileNameFormat, ["legacy_charset"] = s.LegacyCharset,
        } };
        state["nav"] = new JsonObject { ["updated_at"] = _app.Db.NavUpdatedAt,
            ["items"] = NormalizeNavigation(new JsonArray(_app.Db.NavItems().Select(n => (JsonNode)new JsonObject { ["type"] = n.Type, ["ref"] = n.Ref }).ToArray())) };
        state["playlists"] = new JsonArray(_app.Db.Playlists().Select(p => (JsonNode)new JsonObject
        {
            ["uid"] = p.Uid, ["name"] = p.Name, ["updated_at"] = p.UpdatedAt,
            ["items"] = Strings(_app.Db.PlaylistItems(p.Id).Select(i => i.ForeignRef ?? Reference(i.TrackKey))),
        }).ToArray());
        state["deleted_playlists"] = new JsonArray(_app.Db.Tombstones("PLAYLIST").Select(d => (JsonNode)new JsonObject { ["uid"] = d.Key, ["deleted_at"] = d.Value }).ToArray());
        var overrides = new JsonObject();
        foreach (var o in _app.Db.TagOverrides())
        {
            var reference = Reference(o.TrackKey);
            if (overrides[reference] is null) overrides[reference] = new JsonObject();
            overrides[reference]![o.Dimension] = new JsonObject { ["value"] = o.Value, ["updated_at"] = o.UpdatedAt };
        }
        state["tag_overrides"] = overrides;
        var offsets = new JsonObject();
        foreach (var o in _app.Db.LyricsOffsets()) offsets[Reference(o.TrackKey)] = new JsonObject { ["offset_ms"] = o.OffsetMs, ["updated_at"] = o.UpdatedAt };
        state["lyrics_offsets"] = offsets;
        // Keep references for user data when songs later disappear; the entire library need not be duplicated in SQLite JSON.
        var retained = new JsonObject();
        var keys = _app.Db.AllPlaylistItems().Values.SelectMany(items => items.Select(i => i.TrackKey))
            .Concat(_app.Db.TagOverrides().Select(o => o.TrackKey)).Concat(_app.Db.LyricsOffsets().Select(o => o.TrackKey)).Distinct();
        foreach (var key in keys) if (!key.StartsWith("foreign:", StringComparison.Ordinal) && refs.TryGetValue(key, out var reference)) retained[key] = reference;
        var savedRefs = retained.ToJsonString();
        if (_app.Db.GetValue("sync_references") != savedRefs) _app.Db.SetValue("sync_references", savedRefs);
        return state;
    }

    static JsonArray Strings(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    static List<string> ReadStrings(JsonNode? node) => node is JsonArray a ? a.Select(Str).ToList() : [];

    static List<JsonObject> ReadFiles(IEnumerable<LibraryFolder> folders, bool features)
    {
        var result = new List<JsonObject>();
        foreach (var folder in folders.Where(f => f.Available))
        {
            var dir = Path.Combine(folder.Root, ".jeekmusic", "sync");
            try
            {
                var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
                foreach (var path in files.Where(p => p.EndsWith(".features.json", StringComparison.OrdinalIgnoreCase) == features))
                {
                    try
                    {
                        if (new FileInfo(path).Length > 32_000_000) continue;
                        var text = File.ReadAllText(path);
                        var file = features ? JsonNode.Parse(text) as JsonObject : ParseFile(text);
                        if (file is not null && Number(file["format"]) == 2) result.Add(file);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { Log.Warn("sync", "跳过无法读取的同步文件", e); }
                }
                if (!features && files.All(p => p.EndsWith(".features.json", StringComparison.OrdinalIgnoreCase)))
                {
                    var legacy = Path.Combine(folder.Root, ".jeekmusic", "backup.json");
                    if (File.Exists(legacy) && ParseLegacy(File.ReadAllText(legacy)) is { } old) result.Add(old);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Log.Warn("sync", "读取同步目录失败", e); }
        }
        return result;
    }

    public async Task Synchronize()
    {
        if (_stopped) return;
        if (_running) { _again = true; return; }
        _running = true;
        try
        {
            var folders = _app.Library.Snapshot.Folders.ToList();
            if (folders.Count == 0) return;
            var files = await Task.Run(() => ReadFiles(folders, false));
            if (_stopped) return;
            _devices = LatestPerDevice(files, Str(_device["id"]));
            var refs = References();
            var reverse = refs.Where(p => _app.Library.Snapshot.ByKey.ContainsKey(p.Key)).GroupBy(p => p.Value.ToLowerInvariant()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Key);
            var matcher = new BackupRefs.Matcher(reverse);
            string? Match(string reference) => matcher.Match(reference.Replace('\\', '/').ToLowerInvariant());
            string Canonical(string r) => Match(r) is { } key ? refs[key] : r;
            files = files.Select(f => NormalizeReferences(f, Canonical)).ToList();
            var local = NormalizeReferences(Capture(refs), Canonical);
            if (_app.Db.GetValue("sync_initialized") is null &&
                !_app.Db.Playlists().Any() && !_app.Db.NavItems().Any(n => n.Type == "ARTIST") &&
                !_app.Db.TagOverrides().Any() && !_app.Db.LyricsOffsets().Any() &&
                _devices.Count > 0)
            {
                if (_restoreDeclined || _app.Args.Contains("--background")) return;
                if (!await Dialogs.Confirm(_shell.Main, L.T("发现同步数据"), L.T("音乐文件夹中发现其他设备的用户数据。现在合并播放列表、固定艺术家、标签修正和歌词偏移？"), L.T("合并"))) { _restoreDeclined = true; return; }
                if (_stopped) return;
                local = NormalizeReferences(Capture(References()), Canonical); // 对话框打开期间本机仍可能有改动。
            }
            _app.Db.SetValue("sync_initialized", "1");
            var merged = Merge(local, files, Str(_device["id"]), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _applying = true;
            try { Apply(merged, Match); }
            finally { _applying = false; }
            var state = Capture(refs);
            var content = state.ToJsonString();
            state["format"] = 2; state["device"] = _device.DeepClone(); state["written_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int written = 0;
            foreach (var folder in folders.Where(f => f.Available))
            {
                var path = Path.Combine(folder.Root, ".jeekmusic", "sync", Str(_device["id"]) + ".json");
                if (_lastWritten.GetValueOrDefault(path) == content && File.Exists(path)) continue;
                try
                {
                    var json = state.ToJsonString();
                    await Task.Run(() => { lock (_fileGate) { if (!_stopped) AtomicWrite(path, json); } });
                    if (_stopped) return;
                    _lastWritten[path] = content; written++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("sync", "写入同步文件失败", e); }
            }
            await ShareFeatures(folders, refs, Match);
            if (_stopped) return;
            Status = L.F($"上次同步 {DateTime.Now:HH:mm:ss}；发现 {_devices.Count} 台设备");
            Changed?.Invoke();
            if (merged.Changes.Count > 0)
            {
                _app.Playlists.Invalidate();
                if (merged.Changes.Any(c => c.Kind == "rules")) await _app.Library.ApplyRulesAsync();
                AnalysisFeature.Current.Invalidate();
                foreach (var group in merged.Changes.GroupBy(c => c.DeviceName)) _shell.Main.ViewModel.ShowToast(L.F($"已同步来自「{group.Key}」的 {group.Count()} 项改动"));
            }
        }
        catch (Exception e) { Log.Error("sync", "同步失败", e); Status = L.T("同步失败：") + e.Message; Changed?.Invoke(); }
        finally { _running = false; if (_again) { _again = false; Schedule(); } }
    }

    void Apply(MergeResult result, Func<string, string?> match)
    {
        var state = result.State; var db = _app.Db;
        db.InTransaction(() =>
        {
            foreach (var old in result.Replaced) db.AddSyncHistory("PLAYLIST", Str(old.Old["uid"]), old.Old.ToJsonString(), old.DeviceName);
            var existing = db.Playlists().ToDictionary(p => p.Uid);
            foreach (var p in Objects(state["playlists"]))
            {
                var uid = Str(p["uid"]); var stamp = Number(p["updated_at"]);
                var id = existing.TryGetValue(uid, out var mine) ? mine.Id : db.CreatePlaylist(Str(p["name"]), uid, stamp);
                var items = ReadStrings(p["items"]).Select(r => match(r) is { } key ? (key, (string?)null) : ("foreign:" + r, (string?)r)).ToList();
                if (mine is null || mine.UpdatedAt != stamp || !db.PlaylistItems(id).Select(i => (i.TrackKey, i.ForeignRef)).SequenceEqual(items))
                    db.SetPlaylistItems(id, items, stamp, Str(p["name"]));
            }
            foreach (var tombstone in Objects(state["deleted_playlists"]))
            {
                var uid = Str(tombstone["uid"]); var at = Number(tombstone["deleted_at"]);
                if (existing.TryGetValue(uid, out var p) && p.UpdatedAt <= at) db.DeletePlaylist(p.Id, at);
                db.AddTombstone("PLAYLIST", uid, at);
            }
            var oldTags = db.TagOverrides().ToDictionary(o => (o.TrackKey, o.Dimension));
            if (state["tag_overrides"] is JsonObject tags)
                foreach (var item in tags)
                    foreach (var dim in item.Value!.AsObject())
                    {
                        var key = match(item.Key) ?? "foreign:" + item.Key;
                        var row = new TagOverride(key, dim.Key, dim.Value?["value"]?.GetValue<string>(), Number(dim.Value?["updated_at"]));
                        if (oldTags.GetValueOrDefault((key, dim.Key)) != row) db.SetTagOverride(key, dim.Key, row.Value, row.UpdatedAt);
                    }
            var oldOffsets = db.LyricsOffsets().ToDictionary(o => o.TrackKey);
            if (state["lyrics_offsets"] is JsonObject offsets)
                foreach (var item in offsets)
                {
                    var key = match(item.Key) ?? "foreign:" + item.Key;
                    var value = Number(item.Value?["offset_ms"]);
                    if (value is < int.MinValue or > int.MaxValue) continue;
                    var row = new LyricsOffset(key, (int)value, Number(item.Value?["updated_at"]));
                    var previous = oldOffsets.GetValueOrDefault(key);
                    if (previous != row) db.SetLyricsOffset(key, row.OffsetMs, row.UpdatedAt);
                    if (previous?.OffsetMs != value && _app.Library.ByKey(key) is { } t) _app.Lyrics.Invalidate(t);
                }
            foreach (var key in oldTags.Keys.Select(k => k.TrackKey).Concat(oldOffsets.Keys).Distinct()
                .Where(k => k.StartsWith("foreign:", StringComparison.Ordinal) && match(k[8..]) is not null))
            {
                db.Exec("DELETE FROM tag_override WHERE track_key = $k", null, ("$k", key));
                db.Exec("DELETE FROM lyrics_offset WHERE track_key = $k", null, ("$k", key));
            }
            if (state["nav"] is { } nav && Number(nav["updated_at"]) > db.NavUpdatedAt)
                db.SetNavItems(Objects(NormalizeNavigation(nav["items"])).Select(n => new NavItem(Str(n["type"]), n["ref"]?.GetValue<string>())).ToList(), Number(nav["updated_at"]));
            if (state["rules"] is { } rules && Number(rules["updated_at"]) > _app.Settings.Current.RulesUpdatedAt)
            {
                var s = _app.Settings.Current; var r = rules["value"]!;
                s.ArtistSeparators = ReadStrings(r["artist_separators"]); s.ArtistExceptions = ReadStrings(r["artist_exceptions"]);
                s.InferFromFileName = r["infer_from_file_name"]?.GetValue<bool>() ?? true;
                s.FileNameFormat = Str(r["file_name_format"]); s.LegacyCharset = r["legacy_charset"]?.GetValue<string>();
                s.RulesUpdatedAt = Number(rules["updated_at"]); _app.Settings.Save("sync-rules");
            }
            db.PruneSyncData();
        });
    }

    async Task ShareFeatures(List<LibraryFolder> folders, Dictionary<string, string> refs, Func<string, string?> match)
    {
        var files = await Task.Run(() => ReadFiles(folders, true));
        if (_stopped) return;
        bool imported = false;
        foreach (var file in files)
        {
            if (Number(file["analyzer_version"]) != AnalysisFeature.AnalyzerVersion || file["items"] is not JsonObject items) continue;
            foreach (var item in items)
            {
                if (match(item.Key) is not { } key || _app.Library.ByKey(key) is not { } t || item.Value is not JsonObject data) continue;
                if (Number(data["size"]) != t.Size || Math.Abs(Number(data["duration_ms"]) - t.DurationMs) > 1000) continue;
                if (_app.Db.Features(key) is { Failed: false } old && old.AnalyzerVersion == AnalysisFeature.AnalyzerVersion && old.Signature == AnalysisFeature.Signature(t)) continue;
                if (data["features"] is not { } features || FeaturesData.Parse(features.ToJsonString()) is null) continue;
                _app.Db.SaveFeatures(new FeaturesRow(key, AnalysisFeature.Signature(t), AnalysisFeature.AnalyzerVersion, features.ToJsonString(), false));
                imported = true;
            }
        }
        var output = new JsonObject();
        foreach (var row in _app.Db.AllFeatures().Where(r => !r.Failed && r.AnalyzerVersion == AnalysisFeature.AnalyzerVersion))
            if (refs.TryGetValue(row.TrackKey, out var reference) && _app.Library.ByKey(row.TrackKey) is { } t && row.Signature == AnalysisFeature.Signature(t) && row.Data is not null)
                output[reference] = new JsonObject { ["size"] = t.Size, ["duration_ms"] = t.DurationMs, ["features"] = JsonNode.Parse(row.Data) };
        var text = new JsonObject { ["format"] = 2, ["analyzer_version"] = AnalysisFeature.AnalyzerVersion, ["items"] = output }.ToJsonString();
        if (imported) AnalysisFeature.Current.Invalidate();
        foreach (var folder in folders.Where(f => f.Available))
        {
            var path = Path.Combine(folder.Root, ".jeekmusic", "sync", Str(_device["id"]) + ".features.json");
            if (_lastFeatures.GetValueOrDefault(path) == text && File.Exists(path)) continue;
            try
            {
                await Task.Run(() => { lock (_fileGate) { if (!_stopped) AtomicWrite(path, text); } });
                if (_stopped) return;
                _lastFeatures[path] = text;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("sync", "写入分析结果失败", e); }
        }
    }

    Control BuildSettings(SettingsContext c)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Note(L.T("同步工具负责传输音乐文件夹，Jeek Music 合并其中的用户数据。每台设备只写自己的同步文件。")));
        var name = new TextBox { Text = Str(_device["name"]), Width = 280 };
        name.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) { _device["name"] = name.Text.Trim(); File.WriteAllText(AppPaths.Device, _device.ToJsonString()); _lastWritten.Clear(); Schedule(); } };
        panel.Children.Add(Labeled(L.T("本机名称"), name));
        var status = Note(Status); panel.Children.Add(status);
        void Update() => status.Text = Status;
        Changed += Update; status.DetachedFromVisualTree += (_, _) => Changed -= Update;
        panel.Children.Add(SettingsWindow.Action(L.T("立即同步"), () => _ = Synchronize()));
        foreach (var file in _devices) panel.Children.Add(Note(Str(file["device"]?["name"]) + " — " + DateTimeOffset.FromUnixTimeMilliseconds(Number(file["written_at"])).ToLocalTime().ToString("g")));
        panel.Children.Add(SubHeading(L.T("30 天内被覆盖的播放列表")));
        foreach (var history in c.App.Db.SyncHistory().Where(h => h.kind == "PLAYLIST"))
        {
            var old = JsonNode.Parse(history.content)!.AsObject();
            panel.Children.Add(SettingsWindow.Action(L.F($"恢复「{Str(old["name"])}」 ({DateTimeOffset.FromUnixTimeMilliseconds(history.replacedAt):g})"), () =>
            {
                var refs = References();
                var matcher = new BackupRefs.Matcher(refs.GroupBy(x => x.Value.ToLowerInvariant()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Key));
                var uid = Str(old["uid"]); var p = c.App.Db.Playlists().FirstOrDefault(x => x.Uid == uid);
                var id = p?.Id ?? c.App.Db.CreatePlaylist(Str(old["name"]), uid);
                c.App.Db.SetPlaylistItems(id, ReadStrings(old["items"]).Select(r => matcher.Match(r.ToLowerInvariant()) is { } key ? (key, (string?)null) : ("foreign:" + r, (string?)r)).ToList(), name: Str(old["name"]));
                c.App.Playlists.Invalidate();
            }));
        }
        return panel;
    }
}

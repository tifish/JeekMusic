using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Core.Queue;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.UI;

namespace JeekMusic.Automation;

/// <summary>直接复用 UI 的业务服务；输入输出只用 JSON DOM，发布时无需反射或另一套调试实现。</summary>
public sealed class McpTools
{
    readonly AppServices app;

    public McpTools(AppServices services)
    {
        app = services;
        app.Player.Error += OnError;
    }

    const string Page = "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}";
    const string Ids = "{\"type\":\"array\",\"minItems\":1,\"maxItems\":1000,\"items\":{\"type\":\"integer\"}}";
    const string Positions = "{\"type\":\"array\",\"minItems\":1,\"maxItems\":1000,\"items\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}}";
    internal JsonArray Catalog { get; } = BuildCatalog();

    internal static JsonArray BuildCatalog() => new(
        Tool("get_status", "Read current playback, queue summary and library scan state. Playback commands are asynchronous; poll this tool to observe the result.", "", [], true),
        Tool("search_tracks", "Search using the UI library matching and sorting services. Empty query lists all tracks. Uses saved sorting, or file modification time newest first without a preference; sort/reverse override this query only. Invalid dates stay last; equal dates keep file/CUE order. Filters combine with AND; sorting precedes pagination. Returns mtimeUtcTicks, modifiedUtc (ISO UTC or null) and modifiedLocal (local display or em dash). Pages default to 50, max 200.",
            "\"query\":{\"type\":\"string\",\"maxLength\":1000},\"folderId\":{\"type\":\"integer\"},\"artistKey\":{\"type\":\"string\"},\"albumKey\":{\"type\":\"string\"},\"sort\":{\"type\":\"string\",\"enum\":[\"FileName\",\"Modified\",\"Title\",\"Artist\",\"Album\",\"Duration\",\"Format\"],\"description\":\"Optional per-query sort, same values as UI settings. Modified is file mtime newest first.\"},\"reverse\":{\"type\":\"boolean\",\"description\":\"Reverse base order; Modified becomes oldest first, unknown dates still last. Defaults to saved direction, or false when sort is supplied. Does not save a preference.\"}," + Page, [], true),
        Tool("get_track", "Read metadata, stable key, path, tags and cached file mtime: mtimeUtcTicks (.NET UTC ticks), modifiedUtc (ISO UTC or null), modifiedLocal (local date/time or em dash). No file metadata read or rescan.", "\"trackId\":{\"type\":\"integer\"}", ["trackId"], true),
        Tool("play_tracks", "Play trackIds in order starting at startIndex (default 0). Preserves pending manually queued songs, like the UI. Validates all tracks before changing playback.",
            "\"trackIds\":" + Ids + ",\"startIndex\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":999},\"shuffle\":{\"type\":\"boolean\"}", ["trackIds"]),
        Tool("playback", "Control playback. seek uses seconds relative to the current track, including CUE tracks; requires a loaded seekable track and ready output. Preserves play/pause and clamps the endpoint to 1s before track end to avoid seeking into EOF. next/previous start playback even when paused, after a valid target or restart is accepted and output safety checks pass; they never acknowledge hardware-volume confirmation. previous restarts the current track if past 3 seconds. Explicit seek also restores visible timed lyrics following, even during manual browsing. Returns an immediate state snapshot.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"resume\",\"pause\",\"toggle\",\"next\",\"previous\",\"seek\"],\"description\":\"next/previous start playback after successful navigation, including when paused; previous restarts after 3 seconds. Output safety gates still apply; navigation never acknowledges hardware-volume confirmation. seek preserves play/pause.\"},\"seconds\":{\"type\":\"number\",\"minimum\":0,\"maximum\":31536000,\"description\":\"Absolute seconds from the current track/CUE start; requires loaded seekable audio and ready output. An accepted seek immediately restores visible timed lyrics following. Values beyond duration fail; the endpoint is clamped to max(0, duration - 1 second).\"}", ["action"]),
        Tool("get_queue", "Read the actual play order, zero-based indices, current index and manual entries. Paginated.", Page, [], true),
        Tool("edit_queue", "Edit the queue: add/next require trackIds, remove/jump require index, move requires index and toIndex; clear needs no other arguments. Indices are zero-based. Adding to an empty queue starts playback, like the UI.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"add\",\"next\",\"remove\",\"jump\",\"move\",\"clear\"]},\"trackIds\":" + Ids + ",\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647},\"toIndex\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}", ["action"]),
        Tool("set_play_mode", "Set shuffle and/or repeat (off, all, one).", "\"shuffle\":{\"type\":\"boolean\"},\"repeat\":{\"type\":\"string\",\"enum\":[\"off\",\"all\",\"one\"]}", []),
        Tool("set_volume", "Set the current physical endpoint master volume (0–100) in both modes, affecting other apps on that endpoint. Never persisted/restored. muted controls this player only. Unavailable endpoint writes fail; mute-only remains supported.",
            "\"volume\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100,\"description\":\"Current endpoint master volume; requires volumeAvailable. No saved mode value.\"},\"muted\":{\"type\":\"boolean\",\"description\":\"Mute only JeekMusic; never writes endpoint or session mute.\"}", []),
        Tool("get_output", "Read WASAPI selection, actual bound endpoint, volume safety and audio format. sampleRate reports driver-probed supported shared/exclusive rates, real Windows mix rate, requested policy, applied state and failures. Shared client resampling is never reported as a system format change.", "", [], true),
        Tool("set_output", "Select a device, exclusive mode and/or sample-rate policy. sampleRateMode: system preserves Windows mix format (exclusive prefers native); auto follows each song; manual requires sampleRate from get_output. sampleRate alone selects manual. Shared switching changes the actual Windows endpoint mix format and affects other apps; unsupported/failed changes report errors and keep usable playback. Changes persist and may rebuild output at the saved position. Exclusive still requires unattenuated mixer state and physical volume confirmation where necessary.",
            "\"device\":{\"type\":\"string\",\"minLength\":1},\"exclusive\":{\"type\":\"boolean\"},\"sampleRateMode\":{\"type\":\"string\",\"enum\":[\"system\",\"auto\",\"manual\"]},\"sampleRate\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1536000,\"description\":\"Manual device rate in Hz, driver-probed in get_output; requires manual mode if sampleRateMode is supplied.\"}", []),
        Tool("list_playlists", "List playlists with local IDs, stable UIDs and names. Paginated.", Page, [], true),
        Tool("get_playlist", "Read playlist entries in manual order, including unavailable entries. Indices are zero-based. Paginated.", "\"playlistId\":{\"type\":\"integer\"}," + Page, ["playlistId"], true),
        Tool("edit_playlist", "Create/rename/delete/add/remove/move a playlist. create requires name; other actions require playlistId; rename requires name; add requires trackIds; remove/move require positions; move also needs target (index before moving). Changes persist and use normal sync events. Deleting removes only the playlist, never music files.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"create\",\"rename\",\"delete\",\"add\",\"remove\",\"move\"]},\"playlistId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200},\"trackIds\":" + Ids + ",\"positions\":" + Positions + ",\"target\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}", ["action"], destructive: true),
        Tool("list_folders", "Read library folders, IDs, paths and availability.", "", [], true),
        Tool("add_folder", "Schedule adding and scanning an existing absolute local folder. If it contains registered child folders, replaceChildren must explicitly be true. Work runs in the normal library queue; response means queued, not completed.",
            "\"path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":32767},\"replaceChildren\":{\"type\":\"boolean\"}", ["path"]),
        Tool("remove_folder", "Schedule removing a folder from the library; never deletes music files. Response means queued, not completed.", "\"folderId\":{\"type\":\"integer\"}", ["folderId"], destructive: true),
        Tool("rescan_library", "Schedule incremental scan (default) or full rebuild (full=true) in the normal library work queue. Response means queued, not completed. Observe get_status and logs for scan progress/errors.", "\"full\":{\"type\":\"boolean\"}", []),
        Tool("get_diagnostics", "Read version, process ID/executable path, runtime framework/architecture/dynamic code support, data paths, library counts, engine state, lyricsPanel follow/manual/position/layout diagnostics (created=false before panel creation), artistList groups/current letter/visible range diagnostics, folderNavigation default expansion/local collapse preferences/temporary reveal counts (created=false before window creation), and latest playback error. Read-only; never creates a window, navigates or starts audio. No arguments.", "", [], true),
        Tool("get_logs", "Read the tail of app or playback logs (default app, 50 lines, at most 200 lines / 64 KiB). No arbitrary file access.",
            "\"kind\":{\"type\":\"string\",\"enum\":[\"app\",\"playback\"]},\"lines\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}", [], true),
        Tool("show_window", "Show and activate the main player window. Other MCP tools do not activate windows.", "", [])
    );

    static JsonObject Tool(string name, string description, string properties, string[] required, bool readOnly = false, bool destructive = false) => new()
    {
        ["name"] = name, ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object", ["properties"] = JsonNode.Parse("{" + properties + "}"),
            ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["additionalProperties"] = false
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = destructive, ["openWorldHint"] = false }
    };

    string? _lastPlayerError;

    internal JsonObject Call(string name, JsonObject a)
    {
        var p = app.Player;
        switch (name)
        {
            case "get_status": return Status();
            case "search_tracks":
                var snapshot = app.Library.Snapshot;
                var query = a["query"]?.GetValue<string>() ?? "";
                IEnumerable<Track> tracks = string.IsNullOrWhiteSpace(query) ? snapshot.Tracks : ListBuilder.Search(snapshot, query);
                if (a["folderId"] is { } folder) tracks = tracks.Where(t => t.FolderId == folder.GetValue<long>());
                if (a["artistKey"] is { } artist)
                {
                    var artistIds = snapshot.ArtistByKey.TryGetValue(artist.GetValue<string>(), out var ar)
                        ? ar.Tracks.Select(t => t.Id).ToHashSet() : [];
                    tracks = tracks.Where(t => artistIds.Contains(t.Id));
                }
                if (a["albumKey"] is { } album) tracks = tracks.Where(t => t.AlbumKey == album.GetValue<string>());
                return Paginate(SortSearch(tracks, a, app.Settings.Current), a, TrackJson);
            case "get_track": return TrackJson(Track(Required(a, "trackId").GetValue<long>()));
            case "play_tracks":
                var ids = TrackIds(a);
                var start = a["startIndex"]?.GetValue<int>() ?? 0;
                CheckIndex(start, ids.Length);
                p.Play(null, ids, start, a["shuffle"]?.GetValue<bool>());
                return Status();
            case "playback":
                switch (a["action"]!.GetValue<string>())
                {
                    case "resume":
                        if (p.Queue.Items.Count == 0) throw new ArgumentException("Queue is empty; use play_tracks first");
                        if (p.CurrentTrack is null) p.PlayPause(); else p.Resume();
                        break;
                    case "pause": p.Pause(); break;
                    case "toggle": p.PlayPause(); break;
                    case "next": p.Next(); break;
                    case "previous": p.Previous(); break;
                    case "seek":
                        var seconds = Required(a, "seconds").GetValue<double>();
                        if (p.CurrentTrack is null) throw new ArgumentException("No current track");
                        if (p.Duration.TotalSeconds > 0 && seconds > p.Duration.TotalSeconds) throw new ArgumentException("seconds exceeds track duration");
                        if (!p.CanSeek) throw new ArgumentException("Current track is not seekable or output is not ready");
                        p.Seek(TimeSpan.FromSeconds(seconds)); break;
                }
                return Status();
            case "get_queue": return Queue(a);
            case "edit_queue":
                switch (a["action"]!.GetValue<string>())
                {
                    case "add": p.AddToQueue(TrackIds(a)); break;
                    case "next": p.PlayNext(TrackIds(a)); break;
                    case "clear": p.Queue.Clear(); break;
                    case "remove": p.Queue.RemoveAt(QueueIndex(a, "index")); break;
                    case "jump": p.JumpTo(QueueIndex(a, "index")); break;
                    case "move":
                        var from = QueueIndex(a, "index");
                        var to = QueueIndex(a, "toIndex");
                        p.Queue.Move(from, to); break;
                }
                return Queue(new JsonObject());
            case "set_play_mode":
                RequireAny(a, "shuffle", "repeat");
                if (a["shuffle"] is { } shuffle) p.SetShuffle(shuffle.GetValue<bool>());
                if (a["repeat"] is { } repeat) p.SetRepeat(Enum.Parse<RepeatMode>(repeat.GetValue<string>(), true));
                return Status();
            case "set_volume":
                RequireAny(a, "volume", "muted");
                if (a["volume"] is { } volume)
                {
                    if (!p.SetVolume(volume.GetValue<double>(), a["muted"]?.GetValue<bool>() ?? p.Muted))
                        throw new InvalidOperationException(p.VolumeDiagnostic ?? p.VolumeTip);
                }
                else p.SetMuted(a["muted"]!.GetValue<bool>());
                return Status();
            case "get_output": return Output();
            case "set_output":
                RequireAny(a, "device", "exclusive", "sampleRateMode", "sampleRate");
                var rateMode = a["sampleRateMode"]?.GetValue<string>();
                var sampleRate = a["sampleRate"]?.GetValue<int>();
                if (rateMode == "manual" && sampleRate is null || sampleRate is not null && rateMode is not (null or "manual"))
                    throw new ArgumentException("manual mode requires sampleRate; sampleRate cannot be combined with system/auto.");
                if (a["device"] is { } deviceNode)
                {
                    var device = deviceNode.GetValue<string>();
                    if (device != "auto" && !p.Devices.Any(d => d.name == device)) throw new ArgumentException("Unknown output device; use get_output");
                    app.Settings.Current.OutputDevice = device == "auto" ? null : device;
                    app.Settings.Current.OutputDeviceDescription = device == "auto" ? null : p.Devices.First(d => d.name == device).description;
                    app.Settings.Save("playback");
                    p.SetDevice(device == "auto" ? null : device);
                }
                if (a["exclusive"] is { } exclusive)
                {
                    p.SetExclusive(exclusive.GetValue<bool>());
                    if (p.Exclusive != exclusive.GetValue<bool>()) throw new InvalidOperationException(p.OutputSafetyMessage);
                }
                if (rateMode is not null || sampleRate is not null)
                    if (!p.SetSampleRate(rateMode ?? "manual", sampleRate))
                        throw new InvalidOperationException(p.SampleRateDiagnostic);
                return Output();
            case "list_playlists": return Paginate(app.Playlists.All, a, PlaylistJson);
            case "get_playlist":
                var playlist = Playlist(a);
                var entries = app.Playlists.Entries(playlist.Id);
                var result = Paginate(entries, a, e => new JsonObject
                {
                    ["index"] = e.Item.Position, ["trackKey"] = e.Item.TrackKey,
                    ["available"] = e.Track is not null && app.Library.PathOf(e.Track) is not null,
                    ["track"] = e.Track is null ? null : TrackJson(e.Track)
                });
                result["playlist"] = PlaylistJson(playlist);
                return result;
            case "edit_playlist": return EditPlaylist(a);
            case "list_folders": return new JsonObject { ["folders"] = new JsonArray(app.Library.Snapshot.Folders.Select(f => (JsonNode)new JsonObject
                { ["id"] = f.Id, ["name"] = f.DisplayName, ["path"] = f.Root, ["available"] = f.Available }).ToArray()) };
            case "add_folder":
                var path = a["path"]!.GetValue<string>();
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("path must be absolute");
                var check = app.Library.CheckFolder(path);
                if (check.Status == AddFolderStatus.NotFound) throw new ArgumentException("Folder does not exist");
                var replace = a["replaceChildren"]?.GetValue<bool>() ?? false;
                if (check.Status == AddFolderStatus.ContainsExisting && !replace) throw new ArgumentException("Folder contains registered children; set replaceChildren=true to replace them");
                if (check.Status is AddFolderStatus.AlreadyAdded or AddFolderStatus.AlreadyContained)
                    return new JsonObject { ["queued"] = false, ["status"] = check.Status.ToString() };
                _ = app.Library.AddFolderAsync(path, replace);
                return Queued();
            case "remove_folder":
                var folderId = a["folderId"]!.GetValue<long>();
                if (app.Library.Snapshot.Folder(folderId) is null) throw new ArgumentException("Unknown folderId");
                _ = app.Library.RemoveFolderAsync(folderId);
                return Queued();
            case "rescan_library": _ = app.Library.RescanAsync(a["full"]?.GetValue<bool>() ?? false); return Queued();
            case "get_diagnostics": return new JsonObject
                {
                    ["version"] = typeof(McpTools).Assembly.GetName().Version?.ToString(), ["protocolVersion"] = McpSession.ProtocolVersion,
                    ["processId"] = Environment.ProcessId, ["dataDirectory"] = Path.GetFullPath(AppPaths.DataDir),
                    ["processPath"] = Environment.ProcessPath,
                    ["frameworkDescription"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    ["dynamicCodeSupported"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
                    ["appDirectory"] = AppPaths.AppDir, ["logsDirectory"] = AppPaths.LogsDir,
                    ["engineReady"] = p.EngineReady, ["lastPlaybackError"] = _lastPlayerError,
                    ["status"] = Status(), ["output"] = Output(),
                    ["lyricsPanel"] = app.LyricsPanel?.Diagnostics() ?? new JsonObject { ["created"] = false },
                    ["artistList"] = app.ArtistList?.Diagnostics() ?? new JsonObject { ["created"] = false },
                    ["folderNavigation"] = app.FolderExpansion.Diagnostics(app.Navigation?.Nav)
                };
            case "get_logs": return Logs(a);
            case "show_window": Shell.Current.ShowMainWindow(); return new JsonObject { ["shown"] = true };
            default: throw new ArgumentException("Unknown tool: " + name);
        }
    }

    void OnError(string message) => _lastPlayerError = message;

    JsonObject Status()
    {
        var p = app.Player;
        return new JsonObject
        {
            ["playing"] = p.IsPlaying, ["idle"] = p.IsIdle, ["positionSeconds"] = p.Position.TotalSeconds,
            ["durationSeconds"] = p.Duration.TotalSeconds, ["volume"] = p.Volume, ["muted"] = p.Muted,
            ["output"] = VolumeState(p),
            ["shuffle"] = p.Queue.Shuffle, ["repeat"] = p.Queue.Repeat.ToString().ToLowerInvariant(),
            ["queueIndex"] = p.Queue.CurrentIndex, ["queueCount"] = p.Queue.Items.Count,
            ["track"] = p.CurrentTrack is { } track ? TrackJson(track) : null,
            ["scanning"] = app.Library.Scanning, ["trackCount"] = app.Library.Snapshot.Tracks.Count,
            ["snapshotVersion"] = app.Library.Snapshot.Version, ["unsupportedCount"] = app.Library.UnsupportedCount
        };
    }

    JsonObject TrackJson(Track t) => new()
    {
        ["id"] = t.Id, ["trackKey"] = t.TrackKey, ["folderId"] = t.FolderId, ["title"] = t.Title,
        ["artists"] = new JsonArray(t.Artists.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        ["artistKeys"] = new JsonArray(t.Artists.Select(s => (JsonNode?)JsonValue.Create(Core.Library.ArtistSplitter.NameKey(s))).ToArray()),
        ["artist"] = t.ArtistDisplay, ["album"] = t.Album, ["albumKey"] = t.AlbumKey,
        ["durationMs"] = t.DurationMs, ["path"] = app.Library.PathOf(t), ["fileName"] = t.FileName,
        ["trackNo"] = t.TrackNo, ["discNo"] = t.DiscNo, ["year"] = t.Year, ["genre"] = t.Genre,
        ["codec"] = t.Codec, ["sampleRate"] = t.SampleRate, ["bitsPerSample"] = t.BitsPerSample,
        ["channels"] = t.Channels, ["cueTrackNo"] = t.CueTrackNo,
        ["mtimeUtcTicks"] = t.Mtime, ["modifiedUtc"] = FileModified.Utc(t.Mtime)?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["modifiedLocal"] = Text.Modified(t.Mtime),
        ["tags"] = app.Library.Snapshot.Tags.TryGetValue(t.TrackKey, out var tags)
            ? new JsonObject { ["mood"] = tags.Mood, ["voice"] = tags.Voice, ["genre"] = tags.Genre } : null
    };

    internal static List<Track> SortSearch(IEnumerable<Track> tracks, JsonObject args, Settings.AppSettings settings) =>
        ListBuilder.Sort(tracks.ToList(), ListKind.Search,
            args["sort"] is { } sort ? Enum.Parse<SongSort>(sort.GetValue<string>()) : settings.SongSort,
            args["reverse"]?.GetValue<bool>() ?? (args.ContainsKey("sort") ? false : settings.SortDescending));

    JsonObject Queue(JsonObject a)
    {
        var q = app.Player.Queue;
        var result = Paginate(q.Items.Select((e, i) => (entry: e, index: i)).ToList(), a, item => new JsonObject
        {
            ["index"] = item.index, ["trackId"] = item.entry.TrackId, ["manual"] = item.entry.Manual,
            ["track"] = app.Library.ById(item.entry.TrackId) is { } track ? TrackJson(track) : null
        });
        result["currentIndex"] = q.CurrentIndex;
        return result;
    }

    JsonObject Output()
    {
        var p = app.Player;
        p.RefreshRateCapabilities();
        var f = p.Format;
        return new JsonObject
        {
            ["device"] = p.Device ?? "auto", ["exclusive"] = p.Exclusive,
            ["volumeState"] = VolumeState(p),
            ["sampleRate"] = SampleRateState(p),
            ["devices"] = new JsonArray(p.Devices.Select(d => (JsonNode)new JsonObject { ["id"] = d.name, ["description"] = d.description }).ToArray()),
            ["format"] = new JsonObject { ["codec"] = f.Codec, ["fileRate"] = f.FileRate, ["fileFormat"] = f.FileFormat,
                ["deviceRate"] = f.DeviceRate, ["deviceFormat"] = f.DeviceFormat, ["deviceChannels"] = f.DeviceChannels, ["ao"] = f.Ao, ["resampled"] = f.Resampled }
        };
    }

    internal static JsonObject SampleRateState(Playback.PlayerService p) => new()
    {
        ["mode"] = p.SampleRateMode, ["manualRate"] = p.ManualSampleRate, ["requestedRate"] = p.RequestedSampleRate,
        ["actualRate"] = p.ActualSampleRate, ["applied"] = p.SampleRateApplied,
        ["mixRate"] = p.RateCapabilities.MixRate, ["mixChannels"] = p.RateCapabilities.Channels,
        ["canSetShared"] = p.RateCapabilities.CanSetShared,
        ["sharedRates"] = new JsonArray(p.RateCapabilities.SharedRates.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        ["exclusiveRates"] = new JsonArray(p.RateCapabilities.ExclusiveRates.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        ["discovery"] = "driver-exact-standard-rates-plus-source-and-current; channel-layout-specific",
        ["sharedSemantics"] = "windows-endpoint-mix-format; affects-other-apps",
        ["diagnostic"] = p.SampleRateDiagnostic ?? p.RateCapabilities.Diagnostic
    };

    internal static JsonObject VolumeState(Playback.PlayerService p) => new()
    {
        ["semantics"] = "endpoint-master", ["muteScope"] = "player",
        ["actualDeviceId"] = p.ActualDeviceId, ["endpointVolume"] = p.EndpointVolumeValue,
        ["endpointMuted"] = p.EndpointMuted, ["volumeAvailable"] = p.VolumeAvailable,
        ["hardwareVolume"] = p.HardwareVolume, ["hardwareMute"] = p.HardwareMute,
        ["sessionVolume"] = p.SessionVolume, ["sessionMuted"] = p.SessionMuted,
        ["sessionPolicy"] = "preserve-external-block-exclusive",
        ["hardwareVolumeConfirmationRequired"] = p.HardwareVolumeConfirmationRequired,
        ["outputPending"] = p.OutputPending, ["blockReason"] = p.OutputBlockReason,
        ["diagnostic"] = p.VolumeDiagnostic
    };

    JsonObject EditPlaylist(JsonObject a)
    {
        var action = a["action"]!.GetValue<string>();
        if (action == "create")
        {
            var id = app.Playlists.Create(Required(a, "name").GetValue<string>());
            return PlaylistJson(app.Playlists.Get(id)!);
        }
        var playlist = Playlist(a);
        switch (action)
        {
            case "rename": app.Playlists.Rename(playlist.Id, Required(a, "name").GetValue<string>()); break;
            case "delete": app.Playlists.Delete(playlist.Id); return new JsonObject { ["deleted"] = true, ["playlistId"] = playlist.Id };
            case "add": app.Playlists.Add(playlist.Id, TrackIds(a).Select(Track)); break;
            case "remove":
            case "move":
                var positions = Required(a, "positions").AsArray().Select(n => n!.GetValue<int>()).ToArray();
                var count = app.Playlists.Entries(playlist.Id).Count;
                foreach (var position in positions) CheckIndex(position, count);
                if (action == "remove") app.Playlists.Remove(playlist.Id, positions);
                else
                {
                    var target = Required(a, "target").GetValue<int>();
                    if (target < 0 || target > count) throw new ArgumentException("target out of range");
                    app.Playlists.Move(playlist.Id, positions, target);
                }
                break;
        }
        return PlaylistJson(app.Playlists.Get(playlist.Id)!);
    }

    static JsonObject PlaylistJson(Playlist p) => new() { ["id"] = p.Id, ["uid"] = p.Uid, ["name"] = p.Name, ["createdAt"] = p.CreatedAt, ["updatedAt"] = p.UpdatedAt };
    Playlist Playlist(JsonObject a) => app.Playlists.Get(Required(a, "playlistId").GetValue<long>()) ?? throw new ArgumentException("Unknown playlistId");
    Track Track(long id) => app.Library.ById(id) ?? throw new ArgumentException("Unknown trackId: " + id);

    long[] TrackIds(JsonObject a)
    {
        var ids = Required(a, "trackIds").AsArray().Select(n => n!.GetValue<long>()).ToArray();
        foreach (var id in ids)
        {
            var track = Track(id);
            if (app.Library.PathOf(track) is not { } path || !File.Exists(path)) throw new ArgumentException("Track unavailable: " + id);
        }
        return ids;
    }

    int QueueIndex(JsonObject a, string key)
    {
        var index = Required(a, key).GetValue<int>();
        CheckIndex(index, app.Player.Queue.Items.Count);
        return index;
    }

    static void CheckIndex(int index, int count)
    {
        if (index < 0 || index >= count) throw new ArgumentException($"Index {index} out of range (count {count})");
    }

    static JsonNode Required(JsonObject a, string key) => a[key] ?? throw new ArgumentException("Missing argument: " + key);
    static void RequireAny(JsonObject a, params string[] keys)
    {
        if (!keys.Any(a.ContainsKey)) throw new ArgumentException("Provide at least one of: " + string.Join(", ", keys));
    }

    static JsonObject Paginate<T>(IReadOnlyList<T> items, JsonObject a, Func<T, JsonObject> convert)
    {
        var offset = a["offset"]?.GetValue<int>() ?? 0;
        var limit = a["limit"]?.GetValue<int>() ?? 50;
        var page = items.Skip(offset).Take(limit).Select(convert).Cast<JsonNode>().ToArray();
        return new JsonObject { ["total"] = items.Count, ["offset"] = offset,
            ["nextOffset"] = offset + page.Length < items.Count ? (int?)(offset + page.Length) : null, ["items"] = new JsonArray(page) };
    }

    JsonObject Queued() => new() { ["queued"] = true, ["snapshotVersion"] = app.Library.Snapshot.Version };

    static JsonObject Logs(JsonObject a)
    {
        var kind = a["kind"]?.GetValue<string>() ?? "app";
        var count = a["lines"]?.GetValue<int>() ?? 50;
        var path = Path.Combine(AppPaths.LogsDir, kind + ".log");
        if (!File.Exists(path)) return new JsonObject { ["kind"] = kind, ["text"] = "" };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Max(0, stream.Length - 64 * 1024);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (offset > 0) reader.ReadLine(); // 跳过从中间开始的 UTF-8 字符和半行。
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count);
        return new JsonObject { ["kind"] = kind, ["text"] = string.Join('\n', lines) };
    }
}

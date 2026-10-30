using JeekMusic.Sync;
using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Core.Queue;
using JeekMusic.Core.Analysis;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Online;
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
        Tool("get_status", "Read current playback, queue summary and library scan state. idle means no loaded audio stream and may retain a paused track/queue/position after output release. Playback commands are asynchronous; poll this tool to observe the result.", "", [], true),
        Tool("fetch_resources", "Start/status/cancel the same missing-resource batch as the song list menu. start requires kind (covers/lyrics) and scope (current_list/track_ids); track_ids additionally requires trackIds (1-1000), current_list rejects trackIds. current_list captures ALL available rows in the current filtered/source result, including virtualized rows, with the existing search 200-song cap; rejects a computing or empty list. Range is frozen before work; navigating later cannot change it. Existing local/cache resources take priority; no forced overwrite. Covers deduplicate albums and may fill missing album-member artwork using existing save policy; lyrics found locally are skipped. Uses current save settings (may embed/modify audio); never changes save preferences or enables online access. Online access for this kind must already be enabled. One batch across UI/MCP; rejects reentry including pending UI confirmation. Returns immediately; status reports preparing/running/cancelling/completed/cancelled/failed, captured targetSongCount and deduplicated totalUnits/processedUnits/savedUnits/failedUnits. cancel is cooperative and does not roll back completed writes. status/cancel accept only action. No playback or navigation changes.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"start\",\"status\",\"cancel\"]},\"kind\":{\"type\":\"string\",\"enum\":[\"covers\",\"lyrics\"]},\"scope\":{\"type\":\"string\",\"enum\":[\"current_list\",\"track_ids\"]},\"trackIds\":" + Ids, ["action"], destructive: true, openWorld: true),
        Tool("search_tracks", "Search using the UI library matching and sorting services. Empty query lists all tracks. Uses saved sorting, or file modification time newest first without a preference; sort/reverse override this query only. Invalid dates stay last; equal dates keep file/CUE order. Filters combine with AND; optional tags (up to four, one per dimension) use the UI tag filter; omitted/empty tags means no tag filtering and never changes saved UI preferences. Sorting precedes pagination. Returns mtimeUtcTicks, modifiedUtc (ISO UTC or null) and modifiedLocal (local display or em dash). Pages default to 50, max 200.",
            "\"tags\":{\"type\":\"array\",\"maxItems\":4,\"items\":{\"type\":\"string\",\"enum\":[\"SPORT\",\"NON_SPORT\",\"VOCAL\",\"INSTRUMENTAL\",\"CLASSICAL\",\"NON_CLASSICAL\",\"CALM\",\"NON_CALM\"]}},\"query\":{\"type\":\"string\",\"maxLength\":1000},\"folderId\":{\"type\":\"integer\"},\"artistKey\":{\"type\":\"string\"},\"albumKey\":{\"type\":\"string\"},\"sort\":{\"type\":\"string\",\"enum\":[\"FileName\",\"Modified\",\"Title\",\"Artist\",\"Album\",\"Duration\",\"Format\"],\"description\":\"Optional per-query sort, same values as UI settings. Modified is file mtime newest first.\"},\"reverse\":{\"type\":\"boolean\",\"description\":\"Reverse base order; Modified becomes oldest first, unknown dates still last. Defaults to saved direction, or false when sort is supplied. Does not save a preference.\"}," + Page, [], true),
        Tool("get_track", "Read metadata, stable key, path, committed smart tags and tagSources (AUTO/MANUAL, null without a result), and cached file mtime: mtimeUtcTicks (.NET UTC ticks), modifiedUtc (ISO UTC or null), modifiedLocal (local date/time or em dash). No file metadata read or rescan.", "\"trackId\":{\"type\":\"integer\"}", ["trackId"], true),
        Tool("explain_analysis", "Read committed automatic versus final tags, manual/title rule priority, versions, completeness, per-segment/window evidence, threshold margins (not probabilities), extra sampling reasons and bounded retry budget. Optional includeReviews returns at most the latest 100 local correction audit records; snapshots are review evidence, not independent ground truth. Never decodes audio or changes state.",
            "\"trackId\":{\"type\":\"integer\"},\"includeReviews\":{\"type\":\"boolean\"}", ["trackId"], true),
        Tool("set_smart_tags", "Correct smart tags for 1–1000 library trackIds, including offline songs, using the same atomic batch service as the UI. Provide at least one of mood/voice/genre/calmness; CALMNESS is independent of workout; CALM means quiet listening character and NON_CALM means not quiet, not system volume; omitted dimensions remain unchanged (including sync timestamps). A concrete value sets a manual override even when equal to automatic; AUTO restores automatic using a timestamped null sync record. Validates all songs/values before writing; failures roll back the entire batch and computed tags. Saves an atomic local before/after review snapshot with optional reason (not independent truth). Awaits one recomputation and sync notification; returns committed tags/tagSources for distinct tracks. Does not analyze audio, change file metadata or start playback.",
            "\"trackIds\":" + Ids + ",\"mood\":{\"type\":\"string\",\"enum\":[\"SPORT\",\"NON_SPORT\",\"AUTO\"]},\"voice\":{\"type\":\"string\",\"enum\":[\"VOCAL\",\"INSTRUMENTAL\",\"AUTO\"]},\"genre\":{\"type\":\"string\",\"enum\":[\"CLASSICAL\",\"NON_CLASSICAL\",\"AUTO\"]},\"calmness\":{\"type\":\"string\",\"enum\":[\"CALM\",\"NON_CALM\",\"AUTO\"]},\"reason\":{\"type\":\"string\",\"maxLength\":500}", ["trackIds"], minProperties: 2),
        Tool("play_tracks", "Play trackIds in order starting at startIndex (default 0). Preserves pending manually queued songs, like the UI. Validates all tracks before changing playback. Playback starts reveal the current song once in an existing visible song list containing it and in an open queue; never navigate or change selection. Fully visible rows keep their scroll position; offscreen or clipped rows center in the data viewport, clamped at list edges. See get_diagnostics.songVisibility/queueVisibility.",
            "\"trackIds\":" + Ids + ",\"startIndex\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":999},\"shuffle\":{\"type\":\"boolean\"}", ["trackIds"]),
        Tool("playback", "Control playback. toggle uses the same PlayPause service as unmodified Space in the main/mini player; it pauses/resumes without clearing track/queue/position. Explicit pause closes the stream and restores owned temporary Windows formats, retaining track/queue/position; resume rematches shared automatic format before continuing. Internal track/rebuild pauses do not restore. seek uses seconds relative to the current track, including CUE tracks; requires seekable loaded audio or a retained released-pause track (paused seeking does not open output). Preserves play/pause and clamps the endpoint to 1s before track end to avoid seeking into EOF. next/previous start playback even when paused, after a valid target or restart is accepted and output safety checks pass; they never acknowledge hardware-volume confirmation. previous restarts the current track if past 3 seconds. Explicit seek also restores visible timed lyrics following, even during manual browsing. Start/resume/navigation reveal the current song once in an existing visible list containing it and in an open queue; no navigation, selection change or progress following. Fully visible rows keep their scroll position; offscreen or clipped rows center in the data viewport, clamped at list edges. See get_diagnostics.songVisibility/queueVisibility. next/previous resolve any latest pending displayed-list handoff from captured memory before navigation; current track/position are retained until navigation. Manual pending songs have priority. Returns an immediate state snapshot.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"resume\",\"pause\",\"toggle\",\"next\",\"previous\",\"seek\"],\"description\":\"toggle shares unmodified main/mini player Space semantics and retains track/queue/position. pause restores owned temporary formats; resume rematches before continuing. next/previous start playback after successful navigation, including when paused; previous restarts after 3 seconds. Output safety gates still apply; navigation never acknowledges hardware-volume confirmation. seek preserves play/pause. next/previous first resolve any latest pending displayed-list handoff using captured memory; pending manual entries have priority. Successful starts keep fully visible rows in place or center offscreen/clipped rows within list edges, without navigation or selection changes.\"},\"seconds\":{\"type\":\"number\",\"minimum\":0,\"maximum\":31536000,\"description\":\"Absolute seconds from the current track/CUE start; requires loaded seekable audio or a retained released-pause track; paused seek does not open output. An accepted seek immediately restores visible timed lyrics following. Values beyond duration fail; the endpoint is clamped to max(0, duration - 1 second).\"}", ["action"]),
        Tool("get_queue", "Read the actual play order, zero-based indices, current index and manual entries. Includes read-only sourceState: committed/pending context, handoff revision/status and history-only sourceAnchorIndex. Paginated; does not resolve a pending handoff.", Page, [], true),
        Tool("edit_queue", "Edit the queue: add/next require trackIds, remove/jump require index, move requires index and toIndex; clear needs no other arguments. Indices are zero-based. Adding to an empty queue starts playback, like the UI. Playback starts/jumps share the UI one-shot current-row reveal: fully visible rows keep position; offscreen or clipped rows center, clamped at list edges. No navigation or selection change.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"add\",\"next\",\"remove\",\"jump\",\"move\",\"clear\"]},\"trackIds\":" + Ids + ",\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647},\"toIndex\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}", ["action"]),
        Tool("set_play_mode", "Set shuffle and/or repeat (off, all, one).", "\"shuffle\":{\"type\":\"boolean\"},\"repeat\":{\"type\":\"string\",\"enum\":[\"off\",\"all\",\"one\"]}", []),
        Tool("set_volume", "Set the current physical endpoint master volume (0–100) in both modes, affecting other apps on that endpoint. Never persisted/restored. muted controls this player only. Unavailable endpoint writes fail; mute-only remains supported.",
            "\"volume\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100,\"description\":\"Current endpoint master volume; requires volumeAvailable. No saved mode value.\"},\"muted\":{\"type\":\"boolean\",\"description\":\"Mute only Jeek Music; never writes endpoint or session mute.\"}", []),
        Tool("get_output", "Read output selection, volume safety and real audioFormat diagnostics: source precision/layout, client stream, actual endpoint valid/container bits and layout, Windows mix format, driver-supported complete tuples and conversion/failure details. temporaryFormat reports per-endpoint baseline/applied ownership, released-pause state and guarded restoration results. audioFormat/sampleRate.outputState is idle (no loaded output), pending (reload/safety gate), or active (loaded stream, not necessarily playing). While idle, actualDevice/actualRate are null and applied/resampled are false; windowsDeviceDefault/windowsMix remain device configuration, not playback output. displaySummary matches the UI: idle shows source rate and output-idle text without a conversion arrow; loaded playback retains real conversion/warning details. Never infers device bits from a float client stream. Complete Windows endpoint IDs (including redirected Remote Audio) are opaque and preserved. volumeState.diagnostic includes the attempted endpoint ID on resolution errors; unavailable format negotiation does not imply shared playback is unavailable.", "", [], true),
        Tool("set_output", "Select device and/or exclusive mode; automaticFormat controls SHARED automatic complete-format matching (rate, effective precision and known source layout). Disabled leaves the current Windows format unchanged immediately; any owned temporary format restores at explicit pause/end/release. Enabling while paused only stores preference. Exclusive has one mode and always negotiates automatically without changing the saved shared preference. Shared matching changes the actual endpoint format and affects other apps; explicit pause/end/exit, device release or entering exclusive restores the captured pre-write device/mix baseline only while both still match our last application. External edits are respected. Writes are independently read back through IPropertyStore and IAudioClient; failures preserve usable output and report diagnostics. Changes persist and retain the current position/pause state. Exclusive volume safety gates still apply.",
            "\"device\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Exact ID from get_output.devices, or auto for the multimedia default. Preserve the complete opaque ID, including remote endpoints; do not synthesize a local prefix.\"},\"exclusive\":{\"type\":\"boolean\"},\"automaticFormat\":{\"type\":\"boolean\",\"description\":\"Shared automatic device format matching; false does not immediately change Windows format; owned temporary format restores on pause/end/release. Paused enable does not write. In exclusive mode switch to shared first or include exclusive:false.\"}", []),
        Tool("list_playlists", "List playlists with local IDs, stable UIDs and names. Paginated.", Page, [], true),
        Tool("get_playlist", "Read playlist entries in manual order, including unavailable entries. Indices are zero-based. Paginated.", "\"playlistId\":{\"type\":\"integer\"}," + Page, ["playlistId"], true),
        Tool("edit_playlist", "Create/rename/delete/add/remove/move a playlist. create requires name; other actions require playlistId; rename requires name; add requires trackIds; remove/move require positions; move also needs target (index before moving). Changes persist and use normal sync events. Deleting removes only the playlist, never music files.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"create\",\"rename\",\"delete\",\"add\",\"remove\",\"move\"]},\"playlistId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200},\"trackIds\":" + Ids + ",\"positions\":" + Positions + ",\"target\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}", ["action"], destructive: true),
        Tool("lan_sync_status", "Read LAN sharing status, root IDs, permanent revocation and availability, addresses, port, invitationActive (no credential) and paired device IDs. Never returns invitation, tokens or private keys. Uses the production service.", "", [], true),
        Tool("lan_sync_control", "Control the production LAN sync service: start/stop, share/unshare a registered library folder, create a five-minute single-use TLS-pinned invitation (same credential used by the PC QR and Android scan/manual pairing), or revoke a device. share/unshare require folderId; invite requires host (LAN IPv4/DNS name); optional includeQr=true validates phone reachability and returns an in-memory qrPngBase64 using the same encoder as Settings; revoke requires deviceId. Only explicit library roots are shared, no arbitrary disk access. stop/revoke invalidate active transfer authorization. Removing a library root permanently invalidates its grant/plans; reusing a folder row ID never shares the new root. Invitation and QR are sensitive: do not log, publish or screenshot the returned value. No firewall changes.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"start\",\"stop\",\"share\",\"unshare\",\"invite\",\"revoke\"]},\"folderId\":{\"type\":\"integer\"},\"host\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":253},\"includeQr\":{\"type\":\"boolean\",\"description\":\"Return sensitive in-memory QR PNG; rejects loopback/unspecified/broadcast/multicast endpoints. Never log or screenshot.\"},\"deviceId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}", ["action"]),
        Tool("list_folders", "Read library folders, IDs, paths and availability.", "", [], true),
        Tool("add_folder", "Schedule adding and scanning an existing absolute local folder. If it contains registered child folders, replaceChildren must explicitly be true. Work runs in the normal library queue; response means queued, not completed.",
            "\"path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":32767},\"replaceChildren\":{\"type\":\"boolean\"}", ["path"]),
        Tool("remove_folder", "Schedule removing a folder from the library; never deletes music files. Response means queued, not completed.", "\"folderId\":{\"type\":\"integer\"}", ["folderId"], destructive: true),
        Tool("rescan_library", "Schedule incremental scan (default) or full rebuild (full=true) in the normal library work queue. Response means queued, not completed. Before enumeration the worker retries persisted tag transactions, never overwrites external conflicts, and defers affected folders while recovery is unresolved; see get_diagnostics.tagWriting and get_logs. Observe get_status and logs for scan progress/errors.", "\"full\":{\"type\":\"boolean\"}", []),
        Tool("get_diagnostics", "Read version, process ID/executable path, runtime framework/architecture/dynamic code support, data paths, library counts, engine state, typography default chain/installed Microsoft YaHei availability/actual shaped Chinese glyph families (normal and bold), lyricsPanel follow/manual/position/layout diagnostics (created=false before panel creation), artistList groups/current letter/visible range diagnostics, folderNavigation default expansion/local collapse preferences/temporary reveal counts (created=false before window creation), songVisibility/queueVisibility one-shot reveal, containment, actual full-row visibility, initial visibility, centering/edge-clamp result, center error and skip reason diagnostics (created=false before creation/after close), mainLayout actual navigation/song/artist/lyrics/splitter widths and visibility, responsive limits, one-shot folder-expansion fitting and navigationOverflow/songOverflow immediate click-through clipped row text reveal geometry/lifecycle/row height/borderless horizontal reading state and actual-theme composited palette/contrast diagnostics (no names; created=false before creation/after close), queueSource committed/pending source, handoff revision/status and history-only anchor index, tagWriting native format policy/limits, journaled commit/candidate binding, pending recovery count/problem (retried before any normal rescan), and latest saved/rejected outcome/reason without lyric content, and latest playback error. Output audioFormat/sampleRate share get_output idle/pending/active semantics; idle actualDevice/actualRate are null, while windowsDeviceDefault/windowsMix describe device configuration. Read-only; never creates a window, navigates or starts audio. No arguments.", "", [], true),
        Tool("get_logs", "Read the tail of app or playback logs (default app, 50 lines, at most 200 lines / 64 KiB). App logs include tagwriter success/failure, file path, cover/lyrics operations and ID3v1 preservation, without lyrics content. Older versions logged only failures. No arbitrary file access.",
            "\"kind\":{\"type\":\"string\",\"enum\":[\"app\",\"playback\"]},\"lines\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}", [], true),
        Tool("show_window", "Show and activate the main player window. Other MCP tools do not activate windows.", "", [])
    );

    static JsonObject Tool(string name, string description, string properties, string[] required, bool readOnly = false, bool destructive = false, int minProperties = 0, bool openWorld = false)
    {
        var tool = new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = JsonNode.Parse("{" + properties + "}"),
                ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["additionalProperties"] = false
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = destructive, ["openWorldHint"] = openWorld }
        };
        if (minProperties > 0) tool["inputSchema"]!["minProperties"] = minProperties;
        return tool;
    }

    string? _lastPlayerError;

    internal async Task<JsonObject> CallAsync(string name, JsonObject a)
    {
        if (name == "lan_sync_control" && a["action"]?.GetValue<string>() == "invite" && a["includeQr"]?.GetValue<bool>() == true)
        {
            McpSession.Validate(a, Catalog.OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == name)["inputSchema"]!.AsObject());
            var host = Required(a, "host").GetValue<string>();
            await LanPairingQr.ValidatePhoneHost(host);
            var invitation = app.LanSync.Invite(host);
            return new JsonObject { ["invitation"] = invitation, ["qrPngBase64"] = Convert.ToBase64String(LanPairingQr.Encode(invitation)) };
        }
        if (name != "set_smart_tags") return Call(name, a);
        McpSession.Validate(a, Catalog.OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == name)["inputSchema"]!.AsObject());
        var changes = new Dictionary<TagDimension, Tag?>();
        foreach (var (key, dimension) in new[] { ("mood", TagDimension.MOOD), ("voice", TagDimension.VOICE), ("genre", TagDimension.GENRE), ("calmness", TagDimension.CALMNESS) })
            if (a[key] is { } node)
            {
                var value = node.GetValue<string>();
                changes[dimension] = value == "AUTO" ? null : Tags.Of(dimension, value) ??
                    throw new ArgumentException("Invalid tag for " + key);
            }
        var tracks = Required(a, "trackIds").AsArray().Select(n => Track(n!.GetValue<long>())).DistinctBy(t => t.TrackKey).ToArray();
        await app.SmartTags.Override(tracks, changes, a["reason"]?.GetValue<string>());
        var committed = app.SmartTags.Read();
        return new JsonObject { ["count"] = tracks.Length,
            ["items"] = new JsonArray(tracks.Select(t => (JsonNode?)WithTags(TrackJson(t), committed.GetValueOrDefault(t.TrackKey))).ToArray()) };
    }

    internal JsonObject Call(string name, JsonObject a)
    {
        var p = app.Player;
        switch (name)
        {
            case "fetch_resources": return FetchResources(a);
            case "get_status": return Status();
            case "lan_sync_status": return app.LanSync.Diagnostics();
            case "lan_sync_control":
                switch (Required(a, "action").GetValue<string>())
                {
                    case "start": app.LanSync.Start(); break;
                    case "stop": app.LanSync.Stop(); break;
                    case "share": return app.LanSync.EnableRoot(Required(a, "folderId").GetValue<long>(), true);
                    case "unshare": return app.LanSync.EnableRoot(Required(a, "folderId").GetValue<long>(), false);
                    case "invite": return new JsonObject { ["invitation"] = app.LanSync.Invite(Required(a, "host").GetValue<string>()) };
                    case "revoke": app.LanSync.Revoke(Required(a, "deviceId").GetValue<string>()); break;
                    default: throw new ArgumentException("Unknown action");
                }
                return app.LanSync.Diagnostics();
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
                if (a["tags"] is JsonArray requestedTags)
                {
                    var selectedTags = requestedTags.Select(t => t!.GetValue<string>()).ToArray();
                    var dimensions = selectedTags.Select(ListBuilder.DimensionOf).ToArray();
                    if (dimensions.Any(d => d is null) || dimensions.Distinct().Count() != dimensions.Length)
                        throw new ArgumentException("Select at most one tag per dimension");
                    if (ListBuilder.FilterOf(app.Library.Snapshot, selectedTags) is { } predicate) tracks = tracks.Where(predicate);
                }
                return Paginate(SortSearch(tracks, a, app.Settings.Current), a, TrackJson);
            case "explain_analysis":
                var explained = Track(Required(a, "trackId").GetValue<long>());
                var explanation = app.SmartTags.Explain(explained);
                if (a["includeReviews"]?.GetValue<bool>() == true)
                    explanation["reviews"] = new JsonArray(app.Db.TagReviews(explained.TrackKey).Select(r => JsonNode.Parse(r)).ToArray());
                return explanation;
            case "get_track":
                var detail = Track(Required(a, "trackId").GetValue<long>());
                return WithTags(TrackJson(detail), app.SmartTags.Read().GetValueOrDefault(detail.TrackKey));
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
                RequireAny(a, "device", "exclusive", "automaticFormat");
                if (a["automaticFormat"] is not null && (a["exclusive"]?.GetValue<bool>() ?? p.Exclusive))
                    throw new ArgumentException("automaticFormat applies to shared mode; switch to shared or include exclusive:false.");
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
                if (a["automaticFormat"] is { } automatic)
                    if (!p.SetAutomaticAudioFormat(automatic.GetValue<bool>())) throw new InvalidOperationException(p.SampleRateDiagnostic);
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
                    ["tagWriting"] = Online.TagWriter.Diagnostics(),
                    ["typography"] = Typography.Diagnostics(),
                    ["appDirectory"] = AppPaths.AppDir, ["logsDirectory"] = AppPaths.LogsDir,
                    ["engineReady"] = p.EngineReady, ["lastPlaybackError"] = _lastPlayerError,
                    ["status"] = Status(), ["output"] = Output(),
                    ["lyricsPanel"] = app.LyricsPanel?.Diagnostics() ?? new JsonObject { ["created"] = false },
                    ["artistList"] = app.ArtistList?.Diagnostics() ?? new JsonObject { ["created"] = false },
                    ["songVisibility"] = app.SongVisibility?.Diagnostics() ?? new JsonObject { ["created"] = false },
                    ["queueVisibility"] = app.QueueVisibility?.Diagnostics() ?? new JsonObject { ["created"] = false },
                    ["queueSource"] = QueueSourceState(),
                    ["resourceBatch"] = app.Online?.Batches.Diagnostics() ?? new JsonObject { ["state"] = "unavailable", ["busy"] = false },
                    ["mainLayout"] = app.MainLayoutDiagnostics?.Invoke() ?? new JsonObject { ["created"] = false },
                    ["folderNavigation"] = app.FolderExpansion.Diagnostics(app.Navigation?.Nav)
                };
            case "get_logs": return Logs(a);
            case "show_window": Shell.Current.ShowMainWindow(); return new JsonObject { ["shown"] = true };
            default: throw new ArgumentException("Unknown tool: " + name);
        }
    }

    void OnError(string message) => _lastPlayerError = message;

    JsonObject FetchResources(JsonObject a)
    {
        var feature = app.Online ?? throw new InvalidOperationException("Online resource service is not ready");
        var action = Required(a, "action").GetValue<string>();
        if (action != "start")
        {
            if (a.Count != 1) throw new ArgumentException("status/cancel accept only action");
            if (action == "cancel") feature.Batches.Cancel();
            else if (action != "status") throw new ArgumentException("Unknown action");
        }
        else
        {
            var kind = Required(a, "kind").GetValue<string>() switch
            { "covers" => DownloadKind.Covers, "lyrics" => DownloadKind.Lyrics, _ => throw new ArgumentException("Unknown kind") };
            var scope = Required(a, "scope").GetValue<string>();
            Track[] tracks;
            string source;
            if (scope == "current_list")
            {
                if (a.ContainsKey("trackIds")) throw new ArgumentException("current_list rejects trackIds");
                var vm = app.Navigation ?? throw new InvalidOperationException("Song list is not ready");
                if (vm.ComputingRows) throw new InvalidOperationException("Song list is computing; retry when ready");
                tracks = vm.CaptureResourceTracks();
                source = vm.ContentTitle;
            }
            else if (scope == "track_ids")
            {
                tracks = Required(a, "trackIds").AsArray().Select(n => Track(n!.GetValue<long>())).ToArray();
                source = "trackIds";
            }
            else throw new ArgumentException("Unknown scope");
            if (!(kind == DownloadKind.Covers ? app.Settings.Current.OnlineCovers : app.Settings.Current.OnlineLyrics))
                throw new InvalidOperationException("Online access for this resource kind is disabled; enable it in settings first");
            var job = feature.Batches.Reserve(tracks, kind, scope, source);
            _ = feature.Batches.Run(job);
        }
        var result = feature.Batches.Diagnostics();
        result["currentList"] = app.Navigation is { } navigation ? new JsonObject
        {
            ["ready"] = !navigation.ComputingRows, ["source"] = navigation.CurrentSource?.Encode(),
            ["title"] = navigation.ContentTitle, ["availableSongCount"] = navigation.CaptureResourceTracks().Length,
            ["tagFilter"] = new JsonArray(navigation.TagFilter.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["rowsGeneration"] = navigation.RowsGeneration
        } : null;
        return result;
    }

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
            ? new JsonObject { ["mood"] = tags.Mood, ["voice"] = tags.Voice, ["genre"] = tags.Genre, ["calmness"] = tags.Calmness } : null,
        ["tagSources"] = Sources(tags)
    };

    static JsonObject WithTags(JsonObject track, SongTags? tags)
    {
        track["tags"] = tags is null ? null : new JsonObject { ["mood"] = tags.Mood, ["voice"] = tags.Voice, ["genre"] = tags.Genre, ["calmness"] = tags.Calmness };
        track["tagSources"] = Sources(tags);
        return track;
    }

    static JsonObject Sources(SongTags? tags) => new()
    {
        ["mood"] = tags?.Mood is null ? null : tags.MoodManual ? "MANUAL" : "AUTO",
        ["voice"] = tags?.Voice is null ? null : tags.VoiceManual ? "MANUAL" : "AUTO",
        ["genre"] = tags?.Genre is null ? null : tags.GenreManual ? "MANUAL" : "AUTO",
        ["calmness"] = tags?.Calmness is null ? null : tags.CalmnessManual ? "MANUAL" : "AUTO",
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
        result["sourceState"] = QueueSourceState();
        return result;
    }

    JsonObject QueueSourceState() => new()
    {
        ["context"] = ContextJson(app.Player.Queue.Context),
        ["pendingContext"] = ContextJson(app.Player.PendingSourceContext),
        ["revision"] = app.Player.SourceSwitchRevision, ["status"] = app.Player.SourceSwitchStatus,
        ["sourceAnchorIndex"] = app.Player.Queue.SourceAnchorIndex
    };

    static JsonObject? ContextJson(QueueContext? context)
    {
        if (context is null) return null;
        var result = context.Source switch
        {
            QueueSource.Folder f => new JsonObject { ["kind"] = "folder", ["folderId"] = f.FolderId, ["dir"] = f.Dir, ["recursive"] = f.Recursive },
            QueueSource.Artist a => new JsonObject { ["kind"] = "artist", ["artistKey"] = a.NameKey },
            QueueSource.Album a => new JsonObject { ["kind"] = "album", ["albumKey"] = a.AlbumKey },
            QueueSource.Playlist p => new JsonObject { ["kind"] = "playlist", ["playlistId"] = p.PlaylistId },
            QueueSource.Search s => new JsonObject { ["kind"] = "search", ["query"] = s.Query },
            _ => new JsonObject()
        };
        result["title"] = context.Title;
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
            ["audioFormat"] = AudioFormatState(p),
            ["devices"] = new JsonArray(p.Devices.Select(d => (JsonNode)new JsonObject { ["id"] = d.name, ["description"] = d.description }).ToArray()),
            ["format"] = new JsonObject { ["codec"] = f.Codec, ["fileRate"] = f.FileRate, ["fileFormat"] = f.FileFormat,
                ["deviceRate"] = f.DeviceRate, ["deviceFormat"] = f.DeviceFormat, ["deviceChannels"] = f.DeviceChannels, ["ao"] = f.Ao, ["resampled"] = f.Resampled }
        };
    }

    internal static JsonObject SampleRateState(Playback.PlayerService p) => new()
    {
        ["mode"] = p.Exclusive ? "native" : p.SampleRateMode,
        ["outputState"] = AudioFormatDisplay.OutputState(p),
        ["automaticFormat"] = p.AutomaticAudioFormat,
        ["requestedRate"] = p.RequestedSampleRate,
        ["actualRate"] = p.IsIdle ? null : p.ActualSampleRate, ["applied"] = !p.IsIdle && p.SampleRateApplied,
        ["mixRate"] = p.RateCapabilities.MixRate, ["mixChannels"] = p.RateCapabilities.Channels,
        ["canSetShared"] = p.RateCapabilities.CanSetShared,
        ["sharedRates"] = new JsonArray(p.RateCapabilities.SharedRates.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        ["exclusiveRates"] = new JsonArray(p.RateCapabilities.ExclusiveRates.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        ["discovery"] = "driver-exact-standard-rates-plus-source-and-current; channel-layout-specific",
        ["sharedSemantics"] = "windows-endpoint-mix-format; affects-other-apps",
        ["diagnostic"] = p.SampleRateDiagnostic ?? p.FormatConversion ?? p.RateCapabilities.Diagnostic
    };

    internal static JsonObject AudioFormatState(Playback.PlayerService p)
    {
        var source = p.SourceFormat;
        return new()
        {
            ["automaticFormat"] = p.AutomaticAudioFormat, ["activePolicy"] = p.Exclusive ? "exclusive-native" : p.AutomaticAudioFormat ? "shared-automatic" : "windows-current",
            ["outputState"] = AudioFormatDisplay.OutputState(p),
            ["displaySummary"] = AudioFormatDisplay.Summary(p), ["resampled"] = p.OutputResampled, ["hasWarning"] = p.HasOutputFormatWarning,
            ["applied"] = !p.IsIdle && p.AudioFormatApplied,
            ["source"] = new JsonObject { ["sampleRate"] = source.SampleRate, ["bits"] = source.Bits, ["precision"] = source.Precision,
                ["channels"] = source.Channels, ["channelMask"] = source.ChannelMask, ["layout"] = source.Layout, ["layoutKnown"] = source.ChannelMask is not null },
            ["clientStream"] = new JsonObject { ["sampleRate"] = p.Format.DeviceRate, ["sampleFormat"] = p.Format.DeviceFormat,
                ["channels"] = p.Format.DeviceChannels, ["semantics"] = "client-stream; not-device-effective-bit-depth" },
            ["actualDevice"] = p.IsIdle ? null : p.Exclusive ? new JsonObject { ["sampleRate"] = p.ActualSampleRate, ["containerBits"] = null, ["validBits"] = null,
                ["channels"] = p.Format.DeviceChannels, ["layout"] = null, ["readback"] = "exclusive-stream-rate; backend-does-not-expose-negotiated-valid-bits" } : DeviceFormatJson(p.ActualDeviceFormat),
            ["windowsDeviceDefault"] = DeviceFormatJson(p.RateCapabilities.DeviceFormat),
            ["windowsMix"] = DeviceFormatJson(p.RateCapabilities.MixFormat),
            ["selected"] = DeviceFormatJson(p.SelectedDeviceFormat),
            ["temporaryFormat"] = new JsonObject
            {
                ["pausedOutputReleased"] = p.PausedOutputReleased, ["restoreStatus"] = p.FormatRestoreStatus,
                ["restoreReason"] = p.FormatRestoreReason, ["diagnostic"] = p.FormatRestoreDiagnostic,
                ["ownershipPolicy"] = "per-endpoint-control-lease; restore-only-if-device-and-mix-still-equal-last-successful-application",
                ["owners"] = new JsonArray(p.TemporaryFormats.Select(o => (JsonNode)new JsonObject
                {
                    ["deviceId"] = o.DeviceId, ["baselineDevice"] = DeviceFormatJson(o.BaselineDevice), ["baselineMix"] = DeviceFormatJson(o.BaselineMix),
                    ["lastAppliedDevice"] = DeviceFormatJson(o.AppliedDevice), ["lastAppliedMix"] = DeviceFormatJson(o.AppliedMix)
                }).ToArray())
            },
            ["supportedFormats"] = new JsonArray((p.RateCapabilities.SupportedFormats ?? []).Select(f => (JsonNode)DeviceFormatJson(f)!).ToArray()),
            ["capabilityDiscovery"] = "exact-complete-tuples; standard-rates-plus-source/current; only-known-source-and-current-device-layouts",
            ["deviceReadback"] = "IPropertyStore.PKEY_AudioEngine_DeviceFormat; independent-of-client-float-format",
            ["conversion"] = p.FormatConversion, ["diagnostic"] = p.SampleRateDiagnostic,
            ["capabilityDiagnostic"] = p.RateCapabilities.Diagnostic
        };
    }

    internal static JsonObject? DeviceFormatJson(Playback.EndpointAudioFormat? f) => f is null ? null : new()
    {
        ["sampleRate"] = f.SampleRate, ["containerBits"] = f.ContainerBits, ["validBits"] = f.ValidBits,
        ["sampleKind"] = f.SampleKind, ["channels"] = f.Channels, ["channelMask"] = f.ChannelMask, ["layout"] = f.Layout
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

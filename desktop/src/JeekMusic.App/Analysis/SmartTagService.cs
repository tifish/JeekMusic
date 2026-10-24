using JeekMusic.Core.Analysis;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Settings;

namespace JeekMusic.Analysis;

/// <summary>UI、MCP 和后台分析共用的智能标签结果与手动覆盖；不读取或修改音频文件。</summary>
public sealed class SmartTagService(LibraryService library, SettingsStore settings)
{
    readonly Lock _recompute = new();
    LibraryDb Db => library.Db;
    public event Action? ResultsChanged;

    /// <summary>读取已提交的结果，避免编辑器刚保存后读到尚未刷新的曲库快照。</summary>
    public IReadOnlyDictionary<string, SongTags> Read()
    {
        return Db.AllTags().ToDictionary(kv => kv.Key, kv => new SongTags(
            kv.Value.GetValueOrDefault("MOOD").value, kv.Value.GetValueOrDefault("VOICE").value,
            kv.Value.GetValueOrDefault("GENRE").value, kv.Value.GetValueOrDefault("MOOD").source == "MANUAL",
            kv.Value.GetValueOrDefault("VOICE").source == "MANUAL", kv.Value.GetValueOrDefault("GENRE").source == "MANUAL"));
    }

    public void Recompute()
    {
        bool changed = false;
        lock (_recompute) Db.InTransaction(() => changed = RecomputeCore(Db.AllTracks()));
        if (changed) Online.LibraryRefresh.Soon(library);
    }

    // Caller owns both the recompute lock and the database transaction. A failed
    // result write rolls back the overrides too, including their sync timestamps.
    bool RecomputeCore(IReadOnlyList<Track> tracks)
    {
        var features = Db.AllFeatures().ToDictionary(f => f.TrackKey);
        var overrides = Db.TagOverrides().ToDictionary(o => (o.TrackKey, o.Dimension));
        var sensitivity = Enum.TryParse<Sensitivity>(settings.Current.Sensitivity, out var s) ? s : Sensitivity.NORMAL;
        var tags = new List<(string, string, string, string)>();
        foreach (var t in tracks)
        {
            var classical = ClassicalTitles.Matches(t.Title, t.Album);
            var auto = features.TryGetValue(t.TrackKey, out var row) && row.Signature == AnalysisFeature.Signature(t) &&
                row.AnalyzerVersion == AnalysisFeature.AnalyzerVersion && FeaturesData.FromRow(row) is { } f
                ? TagClassifier.Classify(f, sensitivity, classical) : new Dictionary<TagDimension, Tag?>();
            foreach (var dimension in Enum.GetValues<TagDimension>())
            {
                var name = dimension.ToString();
                var manual = overrides.GetValueOrDefault((t.TrackKey, name))?.Value;
                var value = manual ?? auto.GetValueOrDefault(dimension)?.ToString();
                if (value is null && classical && dimension == TagDimension.GENRE) value = Tag.CLASSICAL.ToString();
                if (value is not null) tags.Add((t.TrackKey, name, value, manual is null ? "AUTO" : "MANUAL"));
            }
        }
        var old = Db.AllTags();
        if (old.Sum(x => x.Value.Count) == tags.Count && tags.All(t => old.TryGetValue(t.Item1, out var dims) &&
            dims.TryGetValue(t.Item2, out var v) && v == (t.Item3, t.Item4))) return false;
        Db.ReplaceAllTags(tags);
        return true;
    }

    /// <summary>省略的维度保持不变；null 写带时间戳的恢复自动记录。全部歌曲和维度校验后一次事务保存、重算。</summary>
    public async Task Override(IReadOnlyList<Track> tracks, IReadOnlyDictionary<TagDimension, Tag?> changes)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(changes);
        if (tracks.Count == 0) throw new ArgumentException("At least one track is required");
        if (changes.Count == 0) throw new ArgumentException("At least one tag dimension is required");
        var edits = changes.ToArray();
        foreach (var (dimension, tag) in edits)
            if (!Enum.IsDefined(dimension) || tag is { } value && (!Enum.IsDefined(value) || value.Dimension() != dimension))
                throw new ArgumentException("Invalid tag or tag dimension");
        var targets = tracks.Select(t => t is not null ? (t.Id, t.TrackKey) :
            throw new ArgumentException("Track must not be null")).Distinct().ToArray();
        await Task.Run(() =>
        {
            lock (_recompute) Db.InTransaction(() =>
            {
                var current = Db.AllTracks();
                var byId = current.ToDictionary(t => t.Id);
                foreach (var (id, key) in targets)
                    if (!byId.TryGetValue(id, out var track) || track.TrackKey != key)
                        throw new ArgumentException("Unknown or replaced trackId: " + id);
                var keys = targets.Select(t => t.TrackKey).ToHashSet();
                var dimensions = edits.Select(e => e.Key.ToString()).ToHashSet();
                var previous = Db.TagOverrides().Where(o => keys.Contains(o.TrackKey) && dimensions.Contains(o.Dimension))
                    .Select(o => o.UpdatedAt).DefaultIfEmpty().Max();
                // A rapid reset, or an override synced from a clock ahead of us,
                // must be newer than the manual value it clears on other devices.
                var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), checked(previous + 1));
                foreach (var (_, key) in targets)
                    foreach (var (dimension, tag) in edits)
                        Db.SetTagOverride(key, dimension.ToString(), tag?.ToString(), now);
                RecomputeCore(current);
            });
        });
        // The commit succeeded. A broken refresh/event subscriber must not make
        // the caller report a save failure after persisting the whole batch.
        try { Online.LibraryRefresh.Soon(library); }
        catch (Exception e) { Log.Warn("analysis", "标签已保存，刷新失败", e); }
        if (ResultsChanged is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception e) { Log.Warn("analysis", "标签已保存，通知失败", e); }
    }
}

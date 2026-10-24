using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Analysis;
using JeekMusic.Automation;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using Microsoft.Data.Sqlite;

namespace JeekMusic.App.Tests;

// In-memory SQLite, synthetic cached scores, headless controls and fake endpoints.
// No real client, analysis worker, decoder, MCP pipe or physical audio is started.
[Collection("Avalonia UI")]
public sealed class SmartTagTests
{
    [Fact]
    public Task BatchChangesOnlyExplicitDimensionsAndSameAutomaticValueBecomesManual() => Run(async () =>
    {
        using var h = new Harness();
        var db = h.App.Db;
        db.SetTagOverride(h.Tracks[0].TrackKey, "VOICE", "INSTRUMENTAL", 10);
        var untouched = db.TagOverrides().Single();
        var features = db.AllFeatures().ToArray();
        var notifications = 0;
        h.App.SmartTags.ResultsChanged += () => notifications++;
        await h.App.SmartTags.Override(h.Tracks, new Dictionary<TagDimension, Tag?>
        { [TagDimension.MOOD] = Tag.SPORT, [TagDimension.GENRE] = Tag.CLASSICAL });
        Assert.Equal(1, notifications);
        Assert.Equal(untouched, db.TagOverrides().Single(o => o.Dimension == "VOICE"));
        Assert.Equal(features, db.AllFeatures());
        Assert.All(db.AllTracks(), t => Assert.Equal("file genre", t.Genre));
        Assert.All(h.App.SmartTags.Read().Values, t =>
        { Assert.Equal("SPORT", t.Mood); Assert.True(t.MoodManual); Assert.Equal("CLASSICAL", t.Genre); Assert.True(t.GenreManual); });
        Assert.Single(db.TagOverrides().Where(o => o.Dimension != "VOICE").Select(o => o.UpdatedAt).Distinct());
        // Auto already says VOCAL: choosing that value must still pin it manually.
        Assert.Equal("VOCAL", h.App.SmartTags.Read()[h.Tracks[1].TrackKey].Voice);
        await h.App.SmartTags.Override([h.Tracks[1]], new Dictionary<TagDimension, Tag?> { [TagDimension.VOICE] = Tag.VOCAL });
        Assert.True(h.App.SmartTags.Read()[h.Tracks[1].TrackKey].VoiceManual);
        var future = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000;
        db.SetTagOverride(h.Tracks[1].TrackKey, "VOICE", "INSTRUMENTAL", future);
        h.App.SmartTags.Recompute();
        await h.App.SmartTags.Override([h.Tracks[1]], new Dictionary<TagDimension, Tag?> { [TagDimension.VOICE] = null });
        var reset = db.TagOverrides().Single(o => o.TrackKey == h.Tracks[1].TrackKey && o.Dimension == "VOICE");
        Assert.Null(reset.Value); Assert.True(reset.UpdatedAt > future);
        Assert.Equal("VOCAL", h.App.SmartTags.Read()[h.Tracks[1].TrackKey].Voice);
        Assert.False(h.App.SmartTags.Read()[h.Tracks[1].TrackKey].VoiceManual);
        Assert.Equal(3, notifications);
        // The null tombstone survives export/import representation and prevents
        // older manual values from winning in the actual timestamp merge rules.
        var restored = new JsonObject { ["format"] = 2, ["device"] = new JsonObject { ["id"] = "remote", ["name"] = "Remote" }, ["written_at"] = reset.UpdatedAt, ["tag_overrides"] = new JsonObject
        { ["song"] = new JsonObject { ["VOICE"] = new JsonObject { ["value"] = null, ["updated_at"] = reset.UpdatedAt } } } };
        var old = new JsonObject { ["tag_overrides"] = new JsonObject
        { ["song"] = new JsonObject { ["VOICE"] = new JsonObject { ["value"] = "INSTRUMENTAL", ["updated_at"] = future } } } };
        var merged = JeekMusic.Core.Sync.SyncMerge.Merge(old, [restored], "local", reset.UpdatedAt);
        Assert.Null(merged.State["tag_overrides"]!["song"]!["VOICE"]!["value"]);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task OverrideAndComputedTagFailuresRollbackTheWholeBatch(bool failComputed) => Run(async () =>
    {
        using var h = new Harness();
        var db = h.App.Db;
        db.SetTagOverride(h.Tracks[0].TrackKey, "VOICE", "VOCAL", 123);
        h.App.SmartTags.Recompute();
        var beforeOverrides = db.TagOverrides().ToArray();
        var beforeTags = TagsJson(db);
        var notifications = 0;
        h.App.SmartTags.ResultsChanged += () => notifications++;
        db.Exec(failComputed
            ? "CREATE TRIGGER fail_tag BEFORE INSERT ON track_tag WHEN NEW.dimension = 'GENRE' BEGIN SELECT RAISE(ABORT, 'simulated computed failure'); END"
            : "CREATE TRIGGER fail_override BEFORE INSERT ON tag_override WHEN NEW.dimension = 'VOICE' BEGIN SELECT RAISE(ABORT, 'simulated override failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => h.App.SmartTags.Override(h.Tracks, new Dictionary<TagDimension, Tag?>
        { [TagDimension.MOOD] = Tag.SPORT, [TagDimension.VOICE] = Tag.INSTRUMENTAL, [TagDimension.GENRE] = Tag.CLASSICAL }));
        Assert.Equal(beforeOverrides, db.TagOverrides());
        Assert.Equal(beforeTags, TagsJson(db)); Assert.Equal(0, notifications);
    });

    [Fact]
    public Task InvalidOrReplacedTracksAndInvalidDimensionsDoNotWriteAnything() => Run(async () =>
    {
        using var h = new Harness();
        foreach (var invalid in new[] { new Track { Id = 999999, TrackKey = "unknown", Title = "unknown", Dir = "", FileName = "unknown.flac", ArtistDisplay = "" },
            new Track { Id = h.Tracks[1].Id, TrackKey = "replaced", Title = "replaced", Dir = "", FileName = "replaced.flac", ArtistDisplay = "" } })
            await Assert.ThrowsAsync<ArgumentException>(() => h.App.SmartTags.Override([h.Tracks[0], invalid],
                new Dictionary<TagDimension, Tag?> { [TagDimension.MOOD] = Tag.SPORT }));
        foreach (var invalid in new[] { new Dictionary<TagDimension, Tag?>(),
            new Dictionary<TagDimension, Tag?> { [TagDimension.VOICE] = Tag.SPORT },
            new Dictionary<TagDimension, Tag?> { [(TagDimension)99] = null },
            new Dictionary<TagDimension, Tag?> { [TagDimension.MOOD] = (Tag)99 } })
            await Assert.ThrowsAsync<ArgumentException>(() => h.App.SmartTags.Override(h.Tracks, invalid));
        Assert.Empty(h.App.Db.TagOverrides());
    });

    [Fact]
    public Task McpUsesCommittedSourcesAllowsOfflineSongsAndRejectsInvalidBatches() => Run(async () =>
    {
        using var h = new Harness();
        var tools = new McpTools(h.App);
        Assert.All(h.Tracks, t => Assert.Null(h.App.Library.PathOf(t)));
        var ids = new JsonArray(h.Tracks.Select(t => (JsonNode?)JsonValue.Create(t.Id)).ToArray());
        var response = await tools.CallAsync("set_smart_tags", new() { ["trackIds"] = ids.DeepClone(), ["voice"] = "VOCAL", ["genre"] = "AUTO" });
        Assert.Equal(2, response["count"]!.GetValue<int>());
        Assert.All(response["items"]!.AsArray(), t => Assert.Equal("MANUAL", t!["tagSources"]!["voice"]!.GetValue<string>()));
        var detail = tools.Call("get_track", new() { ["trackId"] = h.Tracks[0].Id });
        Assert.Equal("VOCAL", detail["tags"]!["voice"]!.GetValue<string>());
        Assert.Equal("MANUAL", detail["tagSources"]!["voice"]!.GetValue<string>());
        var before = h.App.Db.TagOverrides().ToArray();
        foreach (var args in new[] {
            new JsonObject { ["trackIds"] = ids.DeepClone() },
            new JsonObject { ["trackIds"] = new JsonArray(h.Tracks[0].Id, 999999), ["mood"] = "SPORT" },
            new JsonObject { ["trackIds"] = ids.DeepClone(), ["voice"] = "SPORT" },
            new JsonObject { ["trackIds"] = ids.DeepClone(), ["mood"] = "SPORT", ["genre"] = "bad" },
            new JsonObject { ["trackIds"] = new JsonArray(Enumerable.Repeat(h.Tracks[0].Id, 1001).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()), ["mood"] = "SPORT" } })
            await Assert.ThrowsAsync<ArgumentException>(() => tools.CallAsync("set_smart_tags", args));
        Assert.Equal(before, h.App.Db.TagOverrides());
        var dup = await tools.CallAsync("set_smart_tags", new() { ["trackIds"] = new JsonArray(h.Tracks[0].Id, h.Tracks[0].Id), ["voice"] = "AUTO" });
        Assert.Equal(1, dup["count"]!.GetValue<int>());
        Assert.Equal("AUTO", dup["items"]![0]!["tagSources"]!["voice"]!.GetValue<string>());
        Assert.False(h.App.Player.EngineReady);
    });

    [Theory]
    [InlineData("zh-CN", "cancel")] [InlineData("en", "escape")] [InlineData("zh-CN", "close")]
    public Task DialogCancelsWithoutWritesAndKeepRestoresNoChanges(string language, string close) => Run(async () =>
    {
        using var h = new Harness(language);
        var owner = new Window { Width = 650, Height = 600 }; owner.Show();
        var dialog = new SmartTagDialog(h.App, h.Tracks);
        var shown = dialog.ShowDialog(owner); Pump(dialog);
        try
        {
            Assert.False(Find<Button>(dialog, "ApplyTags")!.IsEnabled);
            Assert.Equal(12, dialog.GetLogicalDescendants().OfType<RadioButton>().Count());
            Assert.Equal(3, dialog.GetLogicalDescendants().OfType<RadioButton>().Count(r => r.IsChecked == true));
            var option = Find<RadioButton>(dialog, "MOODSPORT")!;
            option.IsChecked = true; Assert.True(Find<Button>(dialog, "ApplyTags")!.IsEnabled);
            Find<RadioButton>(dialog, "MOODKeep")!.IsChecked = true;
            Assert.False(Find<Button>(dialog, "ApplyTags")!.IsEnabled);
            option.IsChecked = true;
            Assert.Empty(h.App.Db.TagOverrides());
            if (close == "cancel") Find<Button>(dialog, "CancelTags")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (close == "escape") Press(dialog, Key.Escape);
            else dialog.Close();
            await shown;
            Assert.Empty(h.App.Db.TagOverrides());
        }
        finally { dialog.Close(); owner.Close(); }
    });

    [Theory]
    [InlineData("zh-CN", 560)] [InlineData("en", 340)]
    public Task DialogKeyboardAppliesOnlyChosenDimensionAndNarrowOptionsStayInsideViewport(string language, int width) => Run(async () =>
    {
        using var h = new Harness(language);
        var owner = new Window { Width = 650, Height = 700 }; owner.Show();
        var dialog = new SmartTagDialog(h.App, [h.Tracks[0]]) { Width = width };
        var shown = dialog.ShowDialog(owner); Pump(dialog);
        try
        {
            Assert.Contains(L.T("自动"), Find<TextBlock>(dialog, "CurrentVOICE")!.Text);
            var radios = dialog.GetLogicalDescendants().OfType<RadioButton>().ToArray();
            var scroll = dialog.GetLogicalDescendants().OfType<ScrollViewer>().First();
            foreach (var radio in radios)
            {
                var p = radio.TranslatePoint(default, scroll)!.Value;
                Assert.True(radio.Bounds.Width > 0);
                Assert.InRange(p.X, -.5, scroll.Bounds.Width);
                Assert.True(p.X + radio.Bounds.Width <= scroll.Bounds.Width + .5);
            }
            var keep = Find<RadioButton>(dialog, "VOICEKeep")!;
            keep.Focus(); Press(dialog, Key.Right);
            Assert.True(Find<RadioButton>(dialog, "VOICEVOCAL")!.IsChecked);
            Press(dialog, Key.Space); // Radio selection, never playback.
            Assert.False(h.App.Player.EngineReady);
            Find<Button>(dialog, "ApplyTags")!.Focus(); Press(dialog, Key.Enter);
            await shown;
            var saved = Assert.Single(h.App.Db.TagOverrides());
            Assert.Equal("VOICE", saved.Dimension); Assert.Equal("VOCAL", saved.Value);
        }
        finally { dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task DialogShowsAtomicSaveErrorAndAllowsCancellation() => Run(async () =>
    {
        using var h = new Harness();
        h.App.Db.Exec("CREATE TRIGGER fail_ui BEFORE INSERT ON tag_override BEGIN SELECT RAISE(ABORT, 'simulated UI save failure'); END");
        var owner = new Window(); owner.Show();
        var dialog = new SmartTagDialog(h.App, h.Tracks);
        var shown = dialog.ShowDialog(owner); Pump(dialog);
        try
        {
            Find<RadioButton>(dialog, "GENRECLASSICAL").IsChecked = true;
            Find<Button>(dialog, "ApplyTags").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && !Find<TextBlock>(dialog, "TagSaveError").IsVisible; i++) await Task.Delay(10);
            Assert.True(dialog.IsVisible); Assert.True(Find<TextBlock>(dialog, "TagSaveError").IsVisible);
            Assert.Contains(L.T("保存失败："), Find<TextBlock>(dialog, "TagSaveError").Text);
            Assert.True(Find<Button>(dialog, "ApplyTags").IsEnabled);
            Assert.True(Find<Button>(dialog, "CancelTags").IsEnabled);
            Assert.Empty(h.App.Db.TagOverrides());
            Press(dialog, Key.Escape); await shown;
            Assert.Empty(h.App.Db.TagOverrides());
        }
        finally { dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task CurrentSummaryDistinguishesMixedValuesAndMixedSourcesAndMenuOpensOneDialog() => Run(async () =>
    {
        using var h = new Harness();
        var states = new Dictionary<string, SongTags>
        {
            [h.Tracks[0].TrackKey] = new("SPORT", "VOCAL", "CLASSICAL", false, true, false),
            [h.Tracks[1].TrackKey] = new("SPORT", "INSTRUMENTAL", "CLASSICAL", true, true, false),
        };
        var mood = SmartTagDialog.Summary(h.Tracks, states, TagDimension.MOOD);
        Assert.Contains(L.T("运动"), mood); Assert.Contains(L.T("自动与手动混合"), mood);
        var voice = SmartTagDialog.Summary(h.Tracks, states, TagDimension.VOICE);
        Assert.Contains(L.T("多个值"), voice); Assert.Contains(L.T("手动"), voice);
        Assert.Contains(L.T("无分析结果"), SmartTagDialog.Summary(h.Tracks, new Dictionary<string, SongTags>(), TagDimension.GENRE));
        var window = new MainWindow(h.App) { ExitRequested = true }; window.Show(); Pump(window);
        try
        {
            var item = window.TagCorrectionItem(h.Tracks);
            Assert.Empty(item.Items); Assert.Equal(L.T("修正智能标签…"), item.Header);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(window);
            var dialog = Assert.Single(window.OwnedWindows.OfType<SmartTagDialog>());
            Assert.False(Find<Button>(dialog, "ApplyTags")!.IsEnabled);
            Press(dialog, Key.Tab); Assert.NotNull(dialog.FocusManager!.GetFocusedElement());
            dialog.Close(); await Task.Yield();
            Assert.Empty(h.App.Db.TagOverrides());
        }
        finally { window.Close(); }
    });

    static string TagsJson(LibraryDb db) => string.Join(";", db.AllTags().OrderBy(k => k.Key)
        .SelectMany(k => k.Value.OrderBy(d => d.Key).Select(d => $"{k.Key}:{d.Key}:{d.Value}")));
    static T Find<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    static void Pump(Window window) { for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
    static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible) { window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null); Pump(window); }
    }
    static async Task Run(Func<Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(async () => { await test(); return 0; }, CancellationToken.None);
    }

    sealed class Harness : IDisposable
    {
        public AppServices App { get; }
        public IReadOnlyList<Track> Tracks { get; }
        public Harness(string language = "zh-CN")
        {
            var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "jeek-tags-" + Guid.NewGuid(), "settings.json"));
            settings.Current.Language = language;
            App = new AppServices(settings, new LibraryDb(":memory:"), source => new PlayerService(source,
                options => new OutputModeSwitchTests.FakeEngine(options), new OutputModeSwitchTests.FakeEndpointVolume()));
            var folder = App.Db.AddFolder("fake", "\\offline-tags\\", "Z:\\offline-tags\\", "Offline tags");
            using (var tx = App.Db.Begin())
            {
                for (var i = 0; i < 2; i++)
                {
                    var name = $"song{i}.flac";
                    App.Db.ReplaceFile(tx, folder, "", name, [new Track { FolderId = folder, TrackKey = name, FileName = name, Dir = "", ArtistDisplay = "",
                        Title = "Synthetic song " + i, Genre = "file genre", DurationMs = 120000, Mtime = 42, Size = 1000 }]);
                }
                tx.Commit();
            }
            App.Db.UpdateFolderLocation(folder, "Z:\\offline-tags\\", false);
            Tracks = App.Db.AllTracks();
            var data = FeaturesData.Of(null, new ModelScores(0, -1, 0, 10, 0));
            App.Db.SaveFeatures(Tracks.Select(t => new FeaturesRow(t.TrackKey, AnalysisFeature.Signature(t), AnalysisFeature.AnalyzerVersion, data.ToJson(), false)));
            App.SmartTags.Recompute(); App.Library.RebuildSnapshot(); Dispatcher.UIThread.RunJobs();
        }
        public void Dispose() { App.Dispose(); L.Initialize("zh-CN"); }
    }
}

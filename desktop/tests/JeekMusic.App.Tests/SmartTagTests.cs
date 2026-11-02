using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
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
    [Theory] [InlineData(0, 1)] [InlineData(125, 126)] [InlineData(259, 258)]
    public Task CorrectionAdvancesFromOriginalFirstTargetAndPreservesPlayback(int target, int expected) => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        h.Window.PlayFrom(h.Track(50)); h.Ready(); h.Player.Pause(); h.Pump();
        h.Player.Seek(TimeSpan.FromSeconds(37)); h.Pump();
        var queue = h.Player.Queue.Items.ToArray(); var current = h.Player.CurrentTrack; var position = h.Player.Position;
        h.Table.SelectedItem = h.Rows[target]; h.ScrollTo(target);
        var context = h.Window.CaptureTagSelection([h.Track(target)]);
        await h.Window.ApplyQuickTag([h.Track(target)], TagDimension.VOICE, Tag.VOCAL, context);
        await Until(() => !h.Vm.ComputingRows && ((JeekMusic.UI.ViewModels.SongRow?)h.Table.SelectedItem)?.Track?.Id == h.Track(expected).Id); h.Pump();
        Assert.Equal(queue, h.Player.Queue.Items); Assert.Same(current, h.Player.CurrentTrack); Assert.Equal(position, h.Player.Position); Assert.False(h.Player.IsPlaying);
        Assert.NotEqual(h.Track(0).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        // Keeping the same open-menu targets does not advance again for another dimension or AUTO.
        await h.Window.ApplyQuickTag([h.Track(target)], TagDimension.GENRE, Tag.CLASSICAL, context);
        await h.Window.ApplyQuickTag([h.Track(target)], TagDimension.VOICE, null, context);
        await Until(() => !h.Vm.ComputingRows); h.Pump();
        Assert.Equal(h.Track(expected).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        Assert.Equal(queue, h.Player.Queue.Items);
    });

    [Fact] public Task BatchUsesDisplayedFirstNotLastOrSelectionInsertionOrderAndSurvivesFilteredRemoval() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        await h.App.SmartTags.Override([h.Track(120), h.Track(121), h.Track(125)], new Dictionary<TagDimension, Tag?> { [TagDimension.VOICE] = Tag.VOCAL });
        h.Vm.ToggleTag("VOCAL"); await Until(() => !h.Vm.ComputingRows && h.Rows.Count == 3); h.Pump();
        h.Window.PlayFrom(h.Track(121)); h.Ready(); h.Player.Pause(); h.Pump();
        var queue = h.Player.Queue.Items.ToArray(); var current = h.Player.CurrentTrack; var position = h.Player.Position;
        h.Table.SelectedItems.Add(h.Rows[2]); h.Table.SelectedItems.Add(h.Rows[0]);
        await h.Window.ApplyQuickTag([h.Track(125), h.Track(120)], TagDimension.VOICE, Tag.INSTRUMENTAL);
        await Until(() => !h.Vm.ComputingRows && h.Rows.Count == 1); h.Pump();
        Assert.True(h.Table.SelectedItem is not null, h.Tools.Call("get_diagnostics", new())["mainLayout"]!["smartTagColumns"]!["selectionAfterCorrection"]!.ToJsonString());
        Assert.Equal(h.Track(121).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        Assert.Equal(queue, h.Player.Queue.Items); Assert.Same(current, h.Player.CurrentTrack); Assert.Equal(position, h.Player.Position);
        await h.Window.ApplyQuickTag([h.Track(121)], TagDimension.VOICE, Tag.INSTRUMENTAL);
        await Until(() => !h.Vm.ComputingRows && h.Rows.Count == 0); h.Pump(); Assert.Null(h.Table.SelectedItem);
        Assert.Equal(queue, h.Player.Queue.Items); Assert.Same(current, h.Player.CurrentTrack); Assert.Equal(position, h.Player.Position);
    });

    [Fact] public Task UnannotatedRemovalKeepsNextAndOpenMenuOriginalTarget() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        h.Tools.Call("song_filter", new() { ["action"]="set", ["annotation"]="unannotated" });
        await Until(() => !h.Vm.ComputingRows); h.Pump();
        h.Table.SelectedItem=h.Rows[120]; h.ScrollTo(120);
        var original=h.Track(120); var next=h.Track(121);
        var context=h.Window.CaptureTagSelection([original]);
        await h.Window.ApplyQuickTag([original],TagDimension.VOICE,Tag.VOCAL,context);
        await Until(() => !h.Vm.ComputingRows && h.Rows.Count==259); h.Pump();
        Assert.Equal(next.Id,((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        await h.Window.ApplyQuickTag([original],TagDimension.GENRE,Tag.CLASSICAL,context);
        await Until(() => !h.Vm.ComputingRows); h.Pump();
        Assert.Equal(next.Id,((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        Assert.All(h.App.Db.TagOverrides(),o=>Assert.Equal(original.TrackKey,o.TrackKey));
        // Incoming sync uses the same recompute/snapshot notification, including AUTO withdrawal.
        h.App.Db.SetTagOverride(original.TrackKey,"VOICE",null,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000);
        h.App.Db.SetTagOverride(original.TrackKey,"GENRE",null,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000);
        h.App.SmartTags.Recompute(); h.App.Library.RebuildSnapshot();
        await Until(() => !h.Vm.ComputingRows && h.Rows.Count==260); h.Pump();
        Assert.Equal(next.Id,((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        h.Vm.ToggleTag("MANUAL"); Assert.DoesNotContain("UNANNOTATED",h.Vm.TagFilter);
        h.Vm.ToggleTag("UNANNOTATED"); Assert.DoesNotContain("MANUAL",h.Vm.TagFilter);
    });

    [Theory] [InlineData("MANUAL", "manual")] [InlineData("UNANNOTATED", "unannotated")]
    public void AnnotationSettingsRoundTripAndLegacyManualCompatibility(string token,string state)
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try {
            File.WriteAllText(path,new JsonObject { ["tagFilter"]=new JsonArray(token,"VOCAL") }.ToJsonString());
            var settings=new SettingsStore(path); Assert.Equal(state,ListBuilder.AnnotationOf(settings.Current.TagFilter));
            settings.WriteNow(); Assert.Equal(settings.Current.TagFilter,new SettingsStore(path).Current.TagFilter);
            Assert.Equal(new[] { "MANUAL","CALM" },ListBuilder.NormalizeFilter(["MANUAL","UNANNOTATED","CALM"]));
        } finally { File.Delete(path); }
    }

    [Fact] public Task OpenMenuKeepsTargetsAndAnchorAcrossDimensionsAndFullDialogUsesSameContinuation() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        h.Table.SelectedItem = h.Rows[120]; h.ScrollTo(120);
        var menu = new ContextMenu(); var root = h.Window.QuickTagCorrectionItem([h.Track(120)]); menu.Items.Add(root); menu.Open(h.Table); h.Pump();
        var panel = Assert.IsType<StackPanel>(root.Header);
        Find<ToggleButton>(panel, "QuickVOICEVOCAL").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => panel.IsEnabled && !h.Vm.ComputingRows); h.Pump();
        Assert.True(menu.IsOpen); Assert.Equal(h.Track(121).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        Find<ToggleButton>(panel, "QuickGENRECLASSICAL").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => panel.IsEnabled && !h.Vm.ComputingRows); h.Pump();
        Assert.True(menu.IsOpen); Assert.Equal(h.Track(121).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        Assert.All(h.App.Db.TagOverrides(), o => Assert.Equal(h.Track(120).TrackKey, o.TrackKey));
        h.Table.SelectedItem = h.Rows[150];
        Find<ToggleButton>(panel, "QuickCALMNESSCALM").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => panel.IsEnabled && !h.Vm.ComputingRows); h.Pump();
        Assert.Equal(h.Track(150).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id); menu.Close();
        var item = h.Window.TagCorrectionItem([h.Track(150)]); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); h.Pump();
        var dialog = Assert.Single(h.Window.OwnedWindows.OfType<SmartTagDialog>());
        Find<RadioButton>(dialog, "VOICEVOCAL").IsChecked = true; Find<Button>(dialog, "ApplyTags").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !dialog.IsVisible && !h.Vm.ComputingRows); h.Pump();
        Assert.Equal(h.Track(151).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        Assert.Empty(h.Player.Queue.Items);
    });

    [Fact] public Task LateSuccessCannotStealNewSelectionOrSearchAndFailureDoesNotAdvance() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        h.Table.SelectedItem = h.Rows[120]; h.ScrollTo(120);
        var save = h.Window.ApplyQuickTag([h.Track(120)], TagDimension.VOICE, Tag.VOCAL);
        h.Table.SelectedItem = h.Rows[150];
        await save; await Until(() => !h.Vm.ComputingRows); h.Pump();
        Assert.Equal(h.Track(150).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        var old = h.Rows; h.App.Library.RebuildSnapshot(); await Until(() => !h.Vm.ComputingRows && !ReferenceEquals(old, h.Rows)); h.Pump();
        Assert.Equal(h.Track(150).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        save = h.Window.ApplyQuickTag([h.Track(150)], TagDimension.GENRE, Tag.CLASSICAL);
        h.Vm.Navigate(new JeekMusic.UI.ViewModels.NavTarget.Search("song 200"));
        await save; await Until(() => !h.Vm.ComputingRows && h.Rows.Count == 1); h.Pump(); Assert.Null(h.Table.SelectedItem);
        h.Table.SelectedItem = h.Rows[0];
        h.App.Db.Exec("CREATE TRIGGER fail_selection BEFORE INSERT ON tag_override BEGIN SELECT RAISE(ABORT, 'failure'); END");
        await h.Window.ApplyQuickTag([h.Track(200)], TagDimension.VOICE, Tag.VOCAL);
        h.Pump(); Assert.Equal(h.Track(200).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
    });

    [Fact] public Task McpCorrectionUsesActualDescendingOrderAndSnapshotRefreshRetainsNextSelection() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        var previous = h.Rows; h.Vm.SetSort(SongSort.FileName);
        await Until(() => !h.Vm.ComputingRows && !ReferenceEquals(previous, h.Rows)); h.Pump();
        Assert.Equal(h.Track(259).Id, h.Rows[0].Track!.Id);
        await h.Tools.CallAsync("set_smart_tags", new() { ["trackIds"] = new JsonArray(h.Track(100).Id, h.Track(160).Id), ["voice"] = "VOCAL" });
        await Until(() => !h.Vm.ComputingRows); h.Pump();
        Assert.Equal(h.Track(159).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        var column = h.Table.Columns.Single(c => c.Tag is "Voice"); column.IsVisible = true; h.Pump();
        // The same cache/override refresh path used by incoming sync must refresh recycled visuals
        // while preserving the user's current selection rather than advancing again.
        h.App.Db.SetTagOverride(h.Track(159).TrackKey, "VOICE", "VOCAL", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        previous = h.Rows; h.App.SmartTags.Recompute(); h.App.Library.RebuildSnapshot();
        await Until(() => !h.Vm.ComputingRows && !ReferenceEquals(previous, h.Rows)); h.Pump();
        Assert.Equal(h.Track(159).Id, ((JeekMusic.UI.ViewModels.SongRow)h.Table.SelectedItem!).Track!.Id);
        Button Cell() => h.Table.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == "VOICE" &&
            b.DataContext is JeekMusic.UI.ViewModels.SongRow r && r.Track?.Id == h.Track(159).Id);
        Assert.True(Cell().GetVisualDescendants().OfType<PathIcon>().Single().IsVisible);
        h.App.Db.SetTagOverride(h.Track(159).TrackKey, "VOICE", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1);
        previous = h.Rows; h.App.SmartTags.Recompute(); h.App.Library.RebuildSnapshot();
        await Until(() => !h.Vm.ComputingRows && !ReferenceEquals(previous, h.Rows)); h.Pump();
        Assert.False(Cell().GetVisualDescendants().OfType<PathIcon>().Single().IsVisible);
        Assert.Empty(h.Player.Queue.Items);
    });

    [Fact] public Task ColumnMenuUsesNativeKeepOpenAndMcpPersistsAllRemainingColumns()=>Run(async()=> {
        using var h=new Harness(); var window=new MainWindow(h.App) { ExitRequested=true }; window.Show(); Pump(window);
        try {
            var grid=window.FindControl<DataGrid>("Table")!;
            window.RestoreLayout(new UiState { SongColumnsVersion=1,Columns=[new() { Tag="Tags",Order=0,Width=999,Visible=true },new() { Tag="Title",Order=1,Width=250,Visible=false },new() { Tag="Artist",Order=2,Width=130,Visible=true }] });
            Assert.DoesNotContain(grid.Columns,c=>c.Tag is "Tags"); Assert.True(grid.Columns.Single(c=>c.Tag is "Title").IsVisible);
            var menu=DataGridExtensions.BuildColumnMenu(grid); menu.Open(grid); Pump(window);
            var mood=menu.Items.OfType<MenuItem>().Single(i=>i.Tag is "Mood"); Assert.True(mood.StaysOpenOnClick); mood.IsChecked=true; mood.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(menu.IsOpen); Assert.True(grid.Columns.Single(c=>c.Tag is "Mood").IsVisible);
            var tools=new McpTools(h.App); var result=tools.Call("song_columns",new() { ["action"]="set",["tag"]="Calmness",["visible"]=true,["width"]=111,["order"]=2 });
            Assert.True(result["persisted"]!.GetValue<bool>()); Assert.True(menu.Items.OfType<MenuItem>().Single(i=>i.Tag is "Calmness").IsChecked);
            Assert.False(menu.Items.OfType<MenuItem>().Single(i=>i.Tag is "Title").IsEnabled);
            Assert.Throws<ArgumentException>(()=>tools.Call("song_columns",new() { ["action"]="set",["tag"]="Title",["visible"]=false }));
            var saved=UiState.Load(); Assert.DoesNotContain(saved.Columns,c=>c.Tag=="Tags"); Assert.Equal(111,saved.Columns.Single(c=>c.Tag=="Calmness").Width);
            menu.Close(); await Task.Yield();
        } finally { window.Close(); }
    });
    [Fact] public Task ManualFilterCoversAllFourDimensionsEqualAutomaticAndRemovesLastAuto()=>Run(async()=> {
        using var h=new Harness(); var t=h.Tracks[0]; var tools=new McpTools(h.App);
        h.App.Db.UpdateFolderLocation(t.FolderId,@"Z:\offline-tags\",true); h.App.Library.RebuildSnapshot(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0,tools.Call("search_tracks",new() { ["onlyManual"]=true })["total"]!.GetValue<int>());
        foreach(var dimension in Enum.GetValues<TagDimension>()) {
            var value=Core.Analysis.Tags.Of(dimension).First();
            await h.App.SmartTags.Override([t],new Dictionary<TagDimension,Tag?> { [dimension]=value }); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1,tools.Call("search_tracks",new() { ["onlyManual"]=true,["query"]="Synthetic",["limit"]=1 })["total"]!.GetValue<int>());
            Assert.True(ListBuilder.FilterOf(h.App.Library.Snapshot,["MANUAL"])!(t));
            Assert.False(ListBuilder.FilterOf(h.App.Library.Snapshot,["UNANNOTATED"])!(t));
            Assert.Equal(tools.Call("search_tracks",new() { ["annotation"]="all" })["total"]!.GetValue<int>() - 1,
                tools.Call("search_tracks",new() { ["annotation"]="unannotated" })["total"]!.GetValue<int>());
            await h.App.SmartTags.Override([t],new Dictionary<TagDimension,Tag?> { [dimension]=null }); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0,tools.Call("search_tracks",new() { ["onlyManual"]=true })["total"]!.GetValue<int>());
        }
        Assert.NotEmpty(h.App.Db.TagReviews(t.TrackKey)); // History alone is not current manual state.
        Assert.True(ListBuilder.FilterOf(h.App.Library.Snapshot,["UNANNOTATED"])!(t));
        Assert.Throws<ArgumentException>(()=>tools.Call("search_tracks",new() { ["onlyManual"]=true,["annotation"]="unannotated" }));
        var noResults = new LibrarySnapshot(h.App.Library.Snapshot.Folders, h.Tracks, new Dictionary<string,SongTags>());
        Assert.True(ListBuilder.FilterOf(noResults,["UNANNOTATED"])!(t));
        Assert.False(ListBuilder.FilterOf(noResults,["UNANNOTATED","VOCAL"])!(t));
        h.App.Preferences.Store.Append(JeekMusic.Core.Preferences.PreferenceProtocol.Dataset,"isolated-sealed-answer","answer",new JsonObject {
            ["source"]="explicit_human_blind_batch",["answer"]="SPORT",["basis"]="whole_recording_familiar",
            ["contextId"]=JeekMusic.Core.Preferences.PreferenceProtocol.Context,["recordingId"]=new string('a',64),
            ["contentSignature"]=new string('b',64),["manifestId"]="isolated-manifest",["role"]="sealed_test"
        });
        Assert.True(ListBuilder.FilterOf(h.App.Library.Snapshot,["UNANNOTATED"])!(t));
        Assert.Equal(0,tools.Call("search_tracks",new() { ["annotation"]="manual" })["total"]!.GetValue<int>());
    });
    [Theory] [InlineData("LOOSE")] [InlineData("NORMAL")] [InlineData("STRICT")] [InlineData("invalid")]
    public void OldStoredAndIncomingSettingsCannotChangeFixedNormalPolicy(string legacy)
    {
        var root=Path.Combine(Path.GetTempPath(),"jeek-fixed-normal-"+Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            var path=Path.Combine(root,"settings.json"); File.WriteAllText(path,new JsonObject { ["sensitivity"]=legacy }.ToJsonString());
            var settings=new SettingsStore(path); Assert.Equal("NORMAL",settings.Current.Sensitivity);
            settings.Current.Sensitivity=legacy; Assert.Equal("NORMAL",settings.Current.Sensitivity);
            settings.WriteNow(); Assert.Equal("NORMAL",JsonNode.Parse(File.ReadAllText(path))!["sensitivity"]!.GetValue<string>());
        } finally { Directory.Delete(root,true); }
    }
    [Fact] public Task FixedNormalReclassifiesCachedEvidenceWithoutDecodingAndPreservesManualAndTitleRules() => Run(async()=>
    {
        using var h=new Harness(); var t=h.Tracks[0];
        h.App.Settings.Current.Sensitivity="STRICT";
        var m=new ModelScores(-3,-1,0,.003*(Math.Exp(3)-1),0);
        var evidence=new AnalysisEvidence(6,[new(0,10000,false,160000,1,1,m,Enumerable.Repeat(m,12).ToArray(),null)],[],true,null,1,1,"not_needed");
        var data=FeaturesData.Of(null,m) with { AnalysisEvidence=evidence.ToJson() };
        foreach(var track in h.Tracks) h.App.Db.SaveFeatures(new FeaturesRow(track.TrackKey,AnalysisFeature.Signature(track),6,data.ToJson(),false));
        h.App.Db.SetTagOverride(h.Tracks[1].TrackKey,"MOOD","NON_SPORT",123);
        h.App.Db.SetTagOverride(h.Tracks[1].TrackKey,"CALMNESS","CALM",124);
        h.App.Db.Exec("UPDATE track SET title='Sonata Op. 1' WHERE id="+t.Id);
        var features=h.App.Db.AllFeatures().ToArray(); var overrides=h.App.Db.TagOverrides().ToArray();
        h.App.SmartTags.Recompute(); t=h.App.Db.AllTracks().Single(x=>x.Id==t.Id);
        var explanation=h.App.SmartTags.Explain(t);
        Assert.Equal("NON_SPORT",explanation["automatic"]!["MOOD"]!.GetValue<string>());
        Assert.Equal("CLASSICAL",explanation["automatic"]!["GENRE"]!.GetValue<string>());
        Assert.Equal("INSTRUMENTAL",explanation["automatic"]!["VOICE"]!.GetValue<string>());
        Assert.Equal("NORMAL",explanation["sensitivity"]!.GetValue<string>()); Assert.Equal(3.25,explanation["thresholds"]!["MOOD"]!.GetValue<double>());
        Assert.Equal("NON_SPORT",h.App.SmartTags.Read()[h.Tracks[1].TrackKey].Mood); Assert.Equal("CALM",h.App.SmartTags.Read()[h.Tracks[1].TrackKey].Calmness);
        Assert.Equal(features,h.App.Db.AllFeatures()); Assert.Equal(overrides,h.App.Db.TagOverrides());
        Assert.False(FeaturesData.NeedsAnalysis(h.App.Db.Features(t.TrackKey),AnalysisFeature.Signature(t)));
        await Task.CompletedTask;
    });
    [Fact]
    public void SavedLegacyFilterAndNewCalmnessNeverCollide()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jeek-calm-filter-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"tagFilter":["QUIET","CALM","POP"]}""");
            var settings = new SettingsStore(path);
            Assert.Equal(new[] { "NON_SPORT", "CALM", "NON_CLASSICAL" }, settings.Current.TagFilter);
            settings.WriteNow();
            Assert.Equal(settings.Current.TagFilter, new SettingsStore(path).Current.TagFilter);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public Task CalmnessReusesV5CacheAndPreservesLegacyOverridesAndAtomicAuto() => Run(async () =>
    {
        using var h = new Harness();
        var db = h.App.Db;
        var track = h.Tracks[0];
        var m = new ModelScores(-3, -1, .04, .01, 0);
        var e = new AnalysisEvidence(5, [new(0, 9750, false, 156000, 1, 1, m, Enumerable.Repeat(m, 10).ToArray(), null)], [], true, null, 1);
        var data = FeaturesData.Of(null, m) with { AnalysisEvidence = e.ToJson() };
        db.SaveFeatures(new FeaturesRow(track.TrackKey, AnalysisFeature.Signature(track), 5, data.ToJson(), false));
        db.SetTagOverride(track.TrackKey, "MOOD", "QUIET", 123);
        var before = db.AllFeatures().ToArray();
        h.App.SmartTags.Recompute();
        var migrated = db.Features(track.TrackKey)!;
        Assert.Equal(6, migrated.AnalyzerVersion);
        Assert.Equal(before.Single(r => r.TrackKey == track.TrackKey).Signature, migrated.Signature);
        var migratedData = FeaturesData.Parse(migrated.Data)!;
        Assert.Equal(data with { AnalysisEvidence = migratedData.AnalysisEvidence }, migratedData);
        var migratedEvidence = AnalysisEvidence.Parse(migratedData.AnalysisEvidence)!;
        Assert.True(migratedEvidence.BaseEvidenceReused);
        Assert.Equal("not_needed", migratedEvidence.RhythmState);
        Assert.Equal(e.Aggregate(), migratedEvidence.Aggregate());
        Assert.All(migratedEvidence.Segments, s => Assert.Null(s.Pulse));
        var afterMigration = db.AllFeatures().ToArray();
        Assert.False(FeaturesData.NeedsAnalysis(db.Features(track.TrackKey), AnalysisFeature.Signature(track)));
        var tags = h.App.SmartTags.Read()[track.TrackKey];
        Assert.Equal("NON_SPORT", tags.Mood); Assert.True(tags.MoodManual);
        Assert.Equal("CALM", tags.Calmness); Assert.False(tags.CalmnessManual);
        Assert.Null(h.App.SmartTags.Read()[h.Tracks[1].TrackKey].Calmness);
        var tools = new McpTools(h.App);
        await tools.CallAsync("set_smart_tags", new() { ["trackIds"] = new JsonArray(track.Id), ["calmness"] = "NON_CALM" });
        var result = tools.Call("get_track", new() { ["trackId"] = track.Id });
        Assert.Equal("NON_CALM", result["tags"]!["calmness"]!.GetValue<string>());
        Assert.Equal("MANUAL", result["tagSources"]!["calmness"]!.GetValue<string>());
        db.Exec("CREATE TRIGGER fail_calm BEFORE INSERT ON track_tag WHEN NEW.dimension='CALMNESS' BEGIN SELECT RAISE(ABORT,'fixture'); END");
        var overrides = db.TagOverrides().ToArray();
        var reviews = db.TagReviews(track.TrackKey).Count;
        await Assert.ThrowsAsync<SqliteException>(() => tools.CallAsync("set_smart_tags", new() { ["trackIds"] = new JsonArray(track.Id), ["calmness"] = "AUTO" }));
        Assert.Equal(overrides, db.TagOverrides()); Assert.Equal(reviews, db.TagReviews(track.TrackKey).Count);
        db.Exec("DROP TRIGGER fail_calm");
        await tools.CallAsync("set_smart_tags", new() { ["trackIds"] = new JsonArray(track.Id), ["calmness"] = "AUTO" });
        Assert.Equal("CALM", h.App.SmartTags.Read()[track.TrackKey].Calmness);
        Assert.Equal(123, db.TagOverrides().Single(o => o.Dimension == "MOOD").UpdatedAt);
        Assert.Equal("QUIET", db.TagOverrides().Single(o => o.Dimension == "MOOD").Value);
        Assert.Null(db.TagOverrides().Single(o => o.Dimension == "CALMNESS").Value);
        Assert.Equal(afterMigration, db.AllFeatures());
        var snapshot = new LibrarySnapshot([], h.Tracks, h.App.SmartTags.Read());
        Assert.True(ListBuilder.FilterOf(snapshot, ["NON_SPORT", "CALM"])!(track));
        Assert.False(ListBuilder.FilterOf(snapshot, ["SPORT", "CALM"])!(track));
        Assert.False(ListBuilder.FilterOf(snapshot, ["NON_CALM"])!(h.Tracks[1]));
        Assert.Null(ListBuilder.FilterOf(snapshot, []));
    });

    [Fact]
    public Task ExplanationAndAuditPreserveTitleAndManualPriority() => Run(async () =>
    {
        using var h = new Harness();
        var t = h.Tracks[0];
        var before = h.App.SmartTags.Explain(t);
        Assert.Equal("VOCAL", before["automatic"]!["VOICE"]!.GetValue<string>());
        Assert.True(before["legacyResult"]!.GetValue<bool>());
        await h.App.SmartTags.Override([t], new Dictionary<TagDimension, Tag?> { [TagDimension.VOICE] = Tag.INSTRUMENTAL }, "audible instrumental");
        var tool = new McpTools(h.App);
        var explained = tool.Call("explain_analysis", new JsonObject { ["trackId"] = t.Id, ["includeReviews"] = true });
        Assert.Equal("VOCAL", explained["automatic"]!["VOICE"]!.GetValue<string>());
        Assert.Equal("INSTRUMENTAL", explained["final"]!["VOICE"]!.GetValue<string>());
        var review = Assert.Single(explained["reviews"]!.AsArray());
        Assert.Equal("audible instrumental", review!["reason"]!.GetValue<string>());
        Assert.Equal("VOCAL", review["before"]!["final"]!["VOICE"]!.GetValue<string>());
        Assert.Equal("INSTRUMENTAL", review["after"]!["final"]!["VOICE"]!.GetValue<string>());
        Assert.Single(h.App.Db.TagReviews(t.TrackKey));
    });

    [Fact]
    public Task TitleRuleStillForcesClassicalUntilManualOverride() => Run(async () =>
    {
        using var h = new Harness();
        h.App.Db.Exec("UPDATE track SET title = 'Sonata Op. 1'");
        var track = h.App.Db.AllTracks()[0];
        h.App.SmartTags.Recompute();
        Assert.Equal("CLASSICAL", h.App.SmartTags.Explain(track)["automatic"]!["GENRE"]!.GetValue<string>());
        await h.App.SmartTags.Override([track], new Dictionary<TagDimension, Tag?> { [TagDimension.GENRE] = Tag.NON_CLASSICAL });
        var explained = h.App.SmartTags.Explain(track);
        Assert.True(explained["classicalTitleMatched"]!.GetValue<bool>());
        Assert.Equal("CLASSICAL", explained["automatic"]!["GENRE"]!.GetValue<string>());
        Assert.Equal("NON_CLASSICAL", explained["final"]!["GENRE"]!.GetValue<string>());
        await h.App.SmartTags.Override([track], new Dictionary<TagDimension, Tag?> { [TagDimension.GENRE] = null });
        Assert.Equal("CLASSICAL", h.App.SmartTags.Read()[track.TrackKey].Genre);
    });

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
            Assert.Equal(16, dialog.GetLogicalDescendants().OfType<RadioButton>().Count());
            Assert.Equal(4, dialog.GetLogicalDescendants().OfType<RadioButton>().Count(r => r.IsChecked == true));
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
            dialog.SetRenderScaling(1.5); Pump(dialog);
            TypographyTests.Save(dialog, $"smart-tags-{language}-{width}");
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

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public Task OptionalDimensionColumnsKeepOldLayoutAndRoundTripIndependently(string language) => Run(async () =>
    {
        using var h = new Harness(language);
        var window = new MainWindow(h.App) { ExitRequested = true }; window.Show(); Pump(window);
        try
        {
            var grid = window.FindControl<DataGrid>("Table")!;
            var old = new UiState { SongColumnsVersion = 1, Columns = [
                new() { Tag = "Tags", Width = 177, Order = 0, Visible = true },
                new() { Tag = "Title", Width = 240, Order = 1, Visible = true },
                new() { Tag = "Modified", Width = 180, Order = 2, Visible = false }] };
            window.RestoreLayout(old);
            Assert.DoesNotContain(grid.Columns,c=>c.Tag is "Tags");
            Assert.Equal(0,grid.Columns.Single(c=>c.Tag is "Title").DisplayIndex);
            Assert.False(grid.Columns.Single(c => c.Tag is "Modified").IsVisible);
            foreach (var (tag, _, _) in MainWindow.TagColumns)
            {
                var column = grid.Columns.Single(c => c.Tag as string == tag);
                Assert.False(column.IsVisible); Assert.False(column.CanUserSort);
            }
            var mood = grid.Columns.Single(c => c.Tag is "Mood");
            var calmness = grid.Columns.Single(c => c.Tag is "Calmness");
            mood.IsVisible = true; mood.Width = new DataGridLength(121); mood.DisplayIndex = 1;
            calmness.IsVisible = true; calmness.Width = new DataGridLength(109); calmness.DisplayIndex = 2;
            window.SaveLayout(old);
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(old, UiStateJson.Default.UiState), UiStateJson.Default.UiState)!;
            mood.IsVisible = false; calmness.IsVisible = false;
            window.RestoreLayout(roundTrip); Pump(window);
            Assert.True(mood.IsVisible); Assert.True(calmness.IsVisible); Assert.Equal(121, mood.Width.Value); Assert.Equal(109, calmness.Width.Value);
            Assert.Equal(1, mood.DisplayIndex); Assert.Equal(2, calmness.DisplayIndex);
            var diagnostics = new McpTools(h.App).Call("get_diagnostics", new())["mainLayout"]!["smartTagColumns"]!;
            Assert.Equal("single-track-dimension-menu", diagnostics["cellAction"]!.GetValue<string>());
            var columns = diagnostics["columns"]!.AsArray(); Assert.Equal(4, columns.Count);
            Assert.Equal(2, columns.Count(c => c!["visible"]!.GetValue<bool>()));
            Assert.Equal(["MOOD", "VOICE", "GENRE", "CALMNESS"], columns.Select(c => c!["dimension"]!.GetValue<string>()));
            Assert.Equal(language == "en" ? "Quick tag correction" : "快速修正标签", Avalonia.Automation.AutomationProperties.GetName(window.QuickTagCorrectionItem(h.Tracks)));
            Assert.False(window.QuickTagCorrectionItem([]).IsEnabled);
            Assert.False(window.TagCorrectionItem([]).IsEnabled);
            await Task.Yield();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task QuickMenuMarksOnlyManualValuesAndCapturesBatchBeforeCallerChangesSelection() => Run(async () =>
    {
        using var h = new Harness();
        var window = new MainWindow(h.App) { ExitRequested = true }; window.Show(); Pump(window);
        try
        {
            var selection = h.Tracks.ToList();
            var root = window.QuickTagCorrectionItem(selection);
            Assert.Empty(root.Items); // No cascaded dimension or value submenus.
            var panel = Assert.IsType<StackPanel>(root.Header);
            Assert.Equal(4, panel.Children.OfType<Grid>().Count());
            var vocal = Find<ToggleButton>(panel, "QuickVOICEVOCAL");
            Assert.False(vocal.IsChecked); Assert.True(Find<ToggleButton>(panel, "QuickVOICEAuto").IsChecked);
            Assert.Equal(12, panel.GetLogicalDescendants().OfType<ToggleButton>().Count());
            Assert.Empty(h.App.Db.TagReviews(h.Tracks[0].TrackKey));
            selection.Clear();
            vocal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.QuickTagCorrectionItem(h.Tracks).IsEnabled && h.App.Db.TagOverrides().Count == 2);
            Assert.All(h.App.Db.TagOverrides(), o => { Assert.Equal("VOICE", o.Dimension); Assert.Equal("VOCAL", o.Value); });
            Assert.All(h.App.SmartTags.Read().Values, t => Assert.True(t.VoiceManual));
            Assert.All(h.Tracks, t => Assert.Single(h.App.Db.TagReviews(t.TrackKey)));
            var marked = Assert.IsType<StackPanel>(window.QuickTagCorrectionItem(h.Tracks).Header);
            Assert.True(Find<ToggleButton>(marked, "QuickVOICEVOCAL").IsChecked);
            await window.ApplyQuickTag([h.Tracks[1]], TagDimension.VOICE, Tag.INSTRUMENTAL);
            var mixed = Assert.IsType<StackPanel>(window.QuickTagCorrectionItem(h.Tracks).Header);
            Assert.Contains(L.T("多个值"), Find<TextBlock>(mixed, "QuickCurrentVOICE").Text);
            Assert.All(mixed.GetLogicalDescendants().OfType<ToggleButton>().Where(b => b.Name!.StartsWith("QuickVOICE")), i => Assert.False(i.IsChecked));
            await window.ApplyQuickTag(h.Tracks, TagDimension.VOICE, null);
            Assert.All(h.App.Db.TagOverrides(), o => Assert.Null(o.Value));
            Assert.All(h.App.SmartTags.Read().Values, t => Assert.False(t.VoiceManual));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public Task InlinePanelSupportsKeyboardTraversalAtomicFailureRetryAndSingleClickCommit(string language) => Run(async () =>
    {
        using var h = new Harness(language);
        var window = new MainWindow(h.App) { ExitRequested = true }; window.Show(); Pump(window);
        var menu = new ContextMenu();
        var panelItem = window.QuickTagCorrectionItem(h.Tracks);
        menu.Items.Add(new MenuItem { Header = "Before" }); menu.Items.Add(panelItem); menu.Items.Add(window.TagCorrectionItem(h.Tracks));
        menu.Open(window); Pump(window);
        try
        {
            var panel = Assert.IsType<StackPanel>(panelItem.Header);
            var first = Find<ToggleButton>(panel, "QuickMOODSPORT");
            foreach (var button in panel.GetLogicalDescendants().OfType<ToggleButton>())
            {
                var point = button.TranslatePoint(default, panelItem)!.Value;
                Assert.InRange(point.X, 0, panelItem.Bounds.Width);
                Assert.True(point.X + button.Bounds.Width <= panelItem.Bounds.Width + .5);
            }
            panelItem.Focus(); Press(panelItem, Key.Right); Assert.True(first.IsFocused);
            Press(first, Key.Right); Assert.True(Find<ToggleButton>(panel, "QuickMOODNON_SPORT").IsFocused);
            Press(Find<ToggleButton>(panel, "QuickMOODNON_SPORT"), Key.Down);
            var voice = Find<ToggleButton>(panel, "QuickVOICEINSTRUMENTAL"); Assert.True(voice.IsFocused);
            h.App.Db.Exec("CREATE TRIGGER fail_quick BEFORE INSERT ON tag_override BEGIN SELECT RAISE(ABORT, 'simulated quick save failure'); END");
            Press(voice, Key.Enter);
            Assert.False(panel.IsEnabled); // All twelve options blocked while the transaction is pending.
            await Until(() => panel.IsEnabled);
            Assert.True(menu.IsOpen); Assert.True(Find<TextBlock>(panel, "QuickTagError").IsVisible);
            Assert.Empty(h.App.Db.TagOverrides()); Assert.All(h.Tracks, t => Assert.Empty(h.App.Db.TagReviews(t.TrackKey)));
            Assert.Equal("failed", new McpTools(h.App).Call("get_diagnostics", new())["mainLayout"]!["smartTagColumns"]!["quickCorrection"]!["state"]!.GetValue<string>());
            h.App.Db.Exec("DROP TRIGGER fail_quick");
            voice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => panel.IsEnabled && h.App.Db.TagOverrides().Count==2);
            Assert.True(menu.IsOpen); Assert.True(voice.IsChecked);
            Assert.Equal(Text.TagName("INSTRUMENTAL"),Find<TextBlock>(panel,"QuickCurrentVOICE").Text);
            Assert.Equal(1,Find<TextBlock>(panel,"QuickCurrentVOICE").MaxLines);
            Find<ToggleButton>(panel,"QuickGENRECLASSICAL").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>panel.IsEnabled && h.App.Db.TagOverrides().Count==4);
            Assert.True(menu.IsOpen); Assert.True(Find<ToggleButton>(panel,"QuickGENRECLASSICAL").IsChecked);
            Assert.All(h.App.Db.TagOverrides().Where(o=>o.Dimension=="VOICE"), o => Assert.Equal("INSTRUMENTAL", o.Value));
            Assert.Equal(4, h.App.Db.TagOverrides().Count);
        }
        finally { menu.Close(); window.Close(); }
    });

    [Fact]
    public Task CellMenuCancellationAndVirtualizedRebindingNeverWriteWrongTrackOrDimension() => Run(async () =>
    {
        using var h = new Harness();
        var window = new MainWindow(h.App) { ExitRequested = true }; window.Show(); Pump(window);
        var button = new Button { DataContext = new JeekMusic.UI.ViewModels.SongRow(h.Tracks[0], 0, null), Tag = "CALMNESS" };
        window.Content = button; Pump(window);
        ContextMenu? menu = null;
        using var subscription = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((opened, _) => menu = opened);
        try
        {
            window.ShowTagCellOptions(button); Pump(window);
            Assert.NotNull(menu); Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, menu.Placement); Assert.Same(button, menu.PlacementTarget); Assert.Empty(h.App.Db.TagOverrides());
            menu.Close(); Pump(window); Assert.Empty(h.App.Db.TagOverrides()); Assert.Empty(h.App.Db.TagReviews(h.Tracks[0].TrackKey));
            window.ShowTagCellOptions(button); Pump(window);
            var calm = menu!.Items.OfType<MenuItem>().Single(i => i.Name == "QuickCALMNESSCALM");
            // Simulate recycling the visual after opening the options for the first track.
            button.DataContext = new JeekMusic.UI.ViewModels.SongRow(h.Tracks[1], 1, null); button.Tag = "MOOD";
            calm.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => h.App.Db.TagOverrides().Count == 1 && window.QuickTagCorrectionItem(h.Tracks).IsEnabled);
            var saved = Assert.Single(h.App.Db.TagOverrides()); Assert.Equal(h.Tracks[0].TrackKey, saved.TrackKey);
            Assert.Equal("CALMNESS", saved.Dimension); Assert.Equal("CALM", saved.Value);
            Assert.Null(h.App.SmartTags.Read()[h.Tracks[1].TrackKey].Calmness);
            await window.ApplyQuickTag([h.Tracks[0]], TagDimension.CALMNESS, null);
            Assert.Null(Assert.Single(h.App.Db.TagOverrides()).Value);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RealCellsAcceptClickEnterSpaceCancelAndRefreshFilteredRowsWithoutPlayback() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        foreach (var column in h.Table.Columns) if (column.Tag is not ("Title" or "Mood" or "Calmness")) column.IsVisible = false;
        foreach (var (tag, _, _) in MainWindow.TagColumns) h.Table.Columns.Single(c => c.Tag as string == tag).IsVisible = true;
        h.Pump();
        var first = h.Track(0); var second = h.Track(1);
        h.Table.SelectedItems.Add(h.Rows[0]); h.Table.SelectedItems.Add(h.Rows[1]);
        ContextMenu? menu = null;
        using var subscription = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((opened, _) => menu = opened);
        Button Cell(string dimension) => h.Table.GetVisualDescendants().OfType<Button>().Single(b =>
            b.Tag as string == dimension && b.DataContext is JeekMusic.UI.ViewModels.SongRow r && r.Track?.Id == first.Id);
        void Click(Button cell)
        {
            var point = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), h.Window)!.Value;
            h.Window.MouseDown(point, MouseButton.Left); h.Window.MouseUp(point, MouseButton.Left); h.Pump();
        }
        var mood = Cell("MOOD");
        var text = mood.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.InRange(Math.Abs(text.TranslatePoint(default, mood)!.Value.Y + text.Bounds.Height / 2 - mood.Bounds.Height / 2), 0, .5);
        Assert.Contains(L.T("运动"), Avalonia.Automation.AutomationProperties.GetName(mood));
        Click(mood); Assert.NotNull(menu); Assert.True(menu.IsOpen); Assert.Empty(h.Player.Queue.Items);
        // Escape cancels through actual popup key routing; no implicit toggle on opening.
        menu.Focus(); Press(menu, Key.Escape); h.Pump(); Assert.False(menu.IsOpen); Assert.Empty(h.App.Db.TagOverrides());
        foreach (var key in new[] { Key.Enter, Key.Space })
        {
            mood.Focus(); Press(h.Window, key); h.Pump(); Assert.True(menu!.IsOpen); Assert.Empty(h.Player.Queue.Items);
            menu.Close(); h.Pump();
        }
        Click(mood); menu!.Close(); Click(mood); menu!.Close();
        Assert.Empty(h.Player.Queue.Items);
        Click(Cell("CALMNESS"));
        menu!.Items.OfType<MenuItem>().Single(i => i.Name == "QuickCALMNESSCALM").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => h.App.Db.TagOverrides().Count == 1 && !h.Vm.ComputingRows && h.Rows[0].Tags?.Calmness == "CALM");
        var saved = Assert.Single(h.App.Db.TagOverrides()); Assert.Equal(first.TrackKey, saved.TrackKey); Assert.Equal("CALMNESS", saved.Dimension);
        Assert.Empty(h.App.Db.TagReviews(second.TrackKey)); Assert.Empty(h.Player.Queue.Items); h.Pump();
        Assert.Equal(L.T("安静"), Cell("CALMNESS").GetVisualDescendants().OfType<TextBlock>().First().Text);
        Assert.Equal(L.T("手工标注"), ToolTip.GetTip(Cell("CALMNESS")));
        menu.Close(); h.Pump(); h.Vm.ToggleTag("CALM");
        await Until(() => !h.Vm.ComputingRows && h.Rows.Count == 1); h.Pump();
        Click(Cell("CALMNESS"));
        menu!.Items.OfType<MenuItem>().Single(i => i.Name == "QuickCALMNESSAuto").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !h.Vm.ComputingRows && h.Rows.Count == 0);
        Assert.Null(Assert.Single(h.App.Db.TagOverrides()).Value); Assert.Empty(h.Player.Queue.Items);
        Assert.Contains("CALM", h.Vm.TagFilter);
    });

    static void Press(Control target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = target });
    }

    [Fact]
    public void DimensionCellTreatsUnknownAsNeutralAndLegacyQuietAsNonSportOnly()
    {
        var row = new JeekMusic.UI.ViewModels.SongRow(null, 0, new("QUIET", null, "NON_CLASSICAL", false, false, true));
        Assert.Equal(L.T("非运动"), row.MoodText); Assert.Equal("—", row.CalmnessText); Assert.Equal("—", row.VoiceText);
        Assert.Equal(L.T("无分析结果"), row.CalmnessSource); Assert.Equal(L.T("自动"), row.MoodSource);
        Assert.Equal(L.T("手工标注"), row.GenreSource);
        Assert.Contains(row.MoodText, row.MoodActionName); Assert.Contains(row.MoodSource, row.MoodActionName);
    }

    static async Task Until(Func<bool> ready)
    {
        for (var i = 0; i < 200 && !ready(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(ready());
    }

    static string TagsJson(LibraryDb db) => string.Join(";", db.AllTags().OrderBy(k => k.Key)
        .SelectMany(k => k.Value.OrderBy(d => d.Key).Select(d => $"{k.Key}:{d.Key}:{d.Value}")));
    static T Find<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    static void Pump(Window window) { for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
    static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible)
            try { window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null); Pump(window); }
            catch (ObjectDisposedException) when (!window.IsVisible) { } // Async Apply can close during the input pump.

    }
    static async Task Run(Func<Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
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
            App.Db.SaveFeatures(Tracks.Select(t => new FeaturesRow(t.TrackKey, AnalysisFeature.Signature(t), 4, data.ToJson(), false)));
            App.SmartTags.Recompute(); App.Library.RebuildSnapshot(); Dispatcher.UIThread.RunJobs();
        }
        public void Dispose() { App.Dispose(); L.Initialize("zh-CN"); }
    }
}

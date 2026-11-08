using System.Text.Json.Nodes;
using Avalonia.Headless;
using JeekMusic.Automation;
using JeekMusic.Core.Queue;
using JeekMusic.Library;
using JeekMusic.Settings;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class TitleTextFilterTests
{
    [Theory]
    [InlineData("晴天", true, false)]
    [InlineData("晴天 Sunny", true, true)]
    [InlineData("Hello", false, true)]
    [InlineData("Ｈｅｌｌｏ", false, true)]
    [InlineData("𠮷野家", true, false)]
    [InlineData("〇", true, false)]
    [InlineData("かな", false, false)]
    [InlineData("한글", false, false)]
    [InlineData("Привет", false, false)]
    [InlineData("123 — 🎵", false, false)]
    [InlineData("", false, false)]
    public void TitleCharacterPairsAreIndependentStrictComplements(string title, bool chinese, bool english)
    {
        Assert.Equal(chinese, TitleTextFilter.Matches(title, "CHINESE"));
        Assert.Equal(!chinese, TitleTextFilter.Matches(title, "NON_CHINESE"));
        Assert.Equal(english, TitleTextFilter.Matches(title, "ENGLISH"));
        Assert.Equal(!english, TitleTextFilter.Matches(title, "NON_ENGLISH"));
    }

    [Fact]
    public void PipelineIntersectsTitleTagsAnnotationAndQueueSourceWithoutRequiringAnalysis()
    {
        Track Song(long id, string title) => new() { Id=id, FolderId=1, TrackKey=id.ToString(),
            Title=title, FileName=$"{id}.flac", Dir="", ArtistDisplay="中文 English" };
        Track[] songs=[Song(1,"晴天"),Song(2,"Sunny"),Song(3,"晴天 Sunny"),Song(4,"123")];
        var folder=new LibraryFolder { Id=1,VolumeId="fixture",PathInVolume="\\",Root="Z:\\",DisplayName="Fixture" };
        var snapshot=new LibrarySnapshot([folder],songs,new Dictionary<string,SongTags> {
            ["3"]=new("SPORT","VOCAL",null,false,true,false) });
        var source=new QueueSource.Folder(1,"",true);
        Assert.Equal(new long[] {1,3},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["CHINESE"] },null).Select(t=>t.Id));
        Assert.Equal(new long[] {1},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["CHINESE","NON_ENGLISH","UNANNOTATED"] },null).Select(t=>t.Id));
        Assert.Equal(new long[] {3},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["CHINESE","ENGLISH","VOCAL","MANUAL"] },null).Select(t=>t.Id));
        Assert.Equal(new long[] {4},ListBuilder.Tracks(snapshot,source,new() { TagFilter=["NON_CHINESE","NON_ENGLISH"] },null).Select(t=>t.Id));
        Assert.Empty(ListBuilder.Tracks(snapshot,source,new() { TagFilter=["CHINESE","INSTRUMENTAL"] },null));
        Assert.Equal(4,ListBuilder.Tracks(snapshot,source,new(),null).Count);
    }

    [Fact]
    public async Task MenuServiceMcpAndSettingsPreserveIndependentPairsAndClear()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(() => {
            using var h=new PlayingVisibilityTests.Harness("晴天 Sunny");
            h.Vm.ToggleTag("CHINESE"); h.Wait(()=>!h.Vm.ComputingRows);
            Assert.Single(h.Rows);
            h.Vm.ToggleTag("ENGLISH"); h.Wait(()=>!h.Vm.ComputingRows);
            Assert.Single(h.Rows); Assert.Contains("CHINESE",h.Vm.TagFilter);
            h.Vm.ToggleTag("NON_CHINESE"); h.Wait(()=>!h.Vm.ComputingRows);
            Assert.Equal(259,h.Rows.Count); Assert.DoesNotContain("CHINESE",h.Vm.TagFilter);
            Assert.Contains("ENGLISH",h.Vm.TagFilter);
            h.Vm.ToggleTag("NON_CHINESE"); h.Wait(()=>!h.Vm.ComputingRows);
            Assert.Equal(new[] {"ENGLISH"},h.Vm.TagFilter);
            h.Tools.Call("song_filter",new() { ["action"]="set",["tags"]=new JsonArray("CHINESE","ENGLISH") });
            h.Wait(()=>!h.Vm.ComputingRows); Assert.Single(h.Rows);
            var result=h.Tools.Call("search_tracks",new() { ["tags"]=new JsonArray("NON_CHINESE"),["limit"]=1 });
            Assert.Equal(259,result["total"]!.GetValue<int>());
            Assert.Equal(new[] {"CHINESE","ENGLISH"},h.Vm.TagFilter);
            Assert.Throws<ArgumentException>(()=>h.Tools.Call("song_filter",new() { ["action"]="set",["tags"]=new JsonArray("CHINESE","NON_CHINESE") }));
            Assert.Throws<ArgumentException>(()=>h.Tools.Call("search_tracks",new() { ["tags"]=new JsonArray("ENGLISH","NON_ENGLISH") }));
            h.Tools.Call("song_filter",new() { ["action"]="set",["annotation"]="unannotated" });
            h.Wait(()=>!h.Vm.ComputingRows); Assert.Single(h.Rows);
            Assert.Contains("CHINESE",h.Vm.TagFilter); Assert.Contains("ENGLISH",h.Vm.TagFilter);
            foreach(var name in new[] {"song_filter","search_tracks"}) {
                var tool=McpTools.BuildCatalog().OfType<JsonObject>().Single(t=>t["name"]!.GetValue<string>()==name);
                Assert.Equal(6,tool["inputSchema"]!["properties"]!["tags"]!["maxItems"]!.GetValue<int>());
                Assert.Contains("NON_ENGLISH",tool["inputSchema"]!["properties"]!["tags"]!["items"]!["enum"]!.AsArray().Select(x=>x!.GetValue<string>()));
            }
            Assert.False(h.Player.IsPlaying); Assert.Empty(h.Player.Queue.Items);
            h.Vm.ClearFilterCommand.Execute(null); h.Wait(()=>!h.Vm.ComputingRows);
            Assert.Equal(260,h.Rows.Count); Assert.Empty(h.Vm.TagFilter);
            return 0;
        },CancellationToken.None);
    }

    [Fact]
    public void PersistedPairsRoundTripAndContradictorySettingsNormalize()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try {
            var store=new SettingsStore(path);
            store.Current.TagFilter=["CHINESE","NON_ENGLISH","VOCAL","UNANNOTATED"]; store.WriteNow();
            Assert.Equal(store.Current.TagFilter,new SettingsStore(path).Current.TagFilter);
            Assert.Equal(new[] {"CHINESE","ENGLISH"},ListBuilder.NormalizeFilter(["CHINESE","NON_CHINESE","ENGLISH","NON_ENGLISH","CHINESE"]));
        } finally { File.Delete(path); }
    }
}

using JeekMusic.Automation;
using JeekMusic.Sync;
using System.Text.Json.Nodes;

namespace JeekMusic.App.Tests;

public sealed class LanProtocolTests
{
    [Fact]
    public void FolderIdentitySurvivesRelocationButNeverRowIdReuse()
    {
        using var db = new JeekMusic.Library.LibraryDb(":memory:");
        var id = db.AddFolder("volume", "A", "C:\\A", "A");
        var identity = Assert.Single(db.Folders()).Identity;
        Assert.Matches("^[0-9a-f]{32}$", identity);
        db.UpdateFolderLocation(id, "D:\\A", false);
        Assert.Equal(identity, Assert.Single(db.Folders()).Identity);
        db.RemoveFolder(id);
        Assert.Equal(id, db.AddFolder("volume", "B", "C:\\B", "B"));
        Assert.NotEqual(identity, Assert.Single(db.Folders()).Identity);
    }

    [Fact]
    public async Task FramesAreLengthBoundedAndRoundTripWithoutReflection()
    {
        using var stream = new MemoryStream();
        await LanSyncService.WriteFrame(stream, new JsonObject { ["path"] = "音乐/一.wav", ["size"] = 123L });
        stream.Position = 0; var result = await LanSyncService.ReadFrame(stream);
        Assert.Equal("音乐/一.wav", result["path"]!.GetValue<string>());
        foreach (var bytes in new byte[][] { [2, 0, 0, 1], [255, 255, 255, 255], [0, 0, 0, 1] })
            await Assert.ThrowsAsync<IOException>(() => LanSyncService.ReadFrame(new MemoryStream(bytes)));
    }
    [Fact]
    public void LanToolsHaveStrictProductionSchemasAndRedactedDiagnostics()
    {
        var catalog = McpTools.BuildCatalog();
        var status = catalog.Single(t => t!["name"]!.GetValue<string>() == "lan_sync_status")!;
        Assert.True(status["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Empty(status["inputSchema"]!["properties"]!.AsObject());
        var control = catalog.Single(t => t!["name"]!.GetValue<string>() == "lan_sync_control")!;
        Assert.False(control["inputSchema"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("boolean", control["inputSchema"]!["properties"]!["includeQr"]!["type"]!.GetValue<string>());
        Assert.Contains("sensitive", control["description"]!.GetValue<string>());
        Assert.Equal("action", Assert.Single(control["inputSchema"]!["required"]!.AsArray())!.GetValue<string>());
    }
}

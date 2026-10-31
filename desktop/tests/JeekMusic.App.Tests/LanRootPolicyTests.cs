using JeekMusic.Library;
using JeekMusic.Sync;
using System.Text.Json.Nodes;

namespace JeekMusic.App.Tests;

public sealed class LanRootPolicyTests
{
    static LibraryFolder Folder(long id, string identity, bool available = true) => new()
    { Id = id, Identity = identity, Root = $"C:\\Music{id}\\", VolumeId = "test", PathInVolume = $"\\Music{id}\\", DisplayName = $"Music{id}", Available = available };
    static JsonObject Grant(LibraryFolder folder, bool enabled = true, bool revoked = false) => new()
    { ["folderId"] = folder.Id, ["folderIdentity"] = folder.Identity, ["name"] = folder.DisplayName, ["enabled"] = enabled, ["revoked"] = revoked };

    [Fact]
    public void GlobalOffDoesNotEnrollFoldersAndGlobalOnEnrollsEveryIdentityIncludingOffline()
    {
        var roots = new JsonObject();
        var folders = new[] { Folder(1, "a"), Folder(2, "b", available: false) };
        Assert.False(LanSyncService.ReconcileRegisteredRoots(roots, folders, shareAll: false));
        Assert.Empty(roots);
        Assert.True(LanSyncService.ReconcileRegisteredRoots(roots, folders, shareAll: true));
        Assert.Equal(2, roots.Count);
        Assert.All(roots, root => Assert.True(root.Value!["enabled"]!.GetValue<bool>()));
        var stable = roots.ToJsonString();
        Assert.False(LanSyncService.ReconcileRegisteredRoots(roots, folders, shareAll: true));
        Assert.Equal(stable, roots.ToJsonString());
        Assert.True(LanSyncService.ReconcileRegisteredRoots(roots, [.. folders, Folder(3, "c")], shareAll: true));
        Assert.Equal(3, roots.Count);
        Assert.All(JsonNode.Parse(stable)!.AsObject(), old => Assert.Equal(old.Value!.ToJsonString(), roots[old.Key]!.ToJsonString()));
    }

    [Fact]
    public void MigrationEnablesDisabledValidIdentityButNeverRebindsLegacyOrRevokedNamespaces()
    {
        var a = Folder(1, "a"); var b = Folder(2, "b"); var c = Folder(3, "c");
        var roots = new JsonObject
        {
            ["stable"] = Grant(a, enabled: false),
            ["legacy"] = new JsonObject { ["folderId"] = b.Id, ["name"] = "Legacy", ["enabled"] = true },
            ["revoked"] = Grant(c, revoked: true)
        };
        Assert.True(LanSyncService.ReconcileRegisteredRoots(roots, [a, b, c], shareAll: true));
        Assert.True(roots["stable"]!["enabled"]!.GetValue<bool>());
        foreach (var id in new[] { "legacy", "revoked" })
        {
            Assert.True(roots[id]!["revoked"]!.GetValue<bool>());
            Assert.False(roots[id]!["enabled"]!.GetValue<bool>());
        }
        Assert.Null(roots["legacy"]!["folderIdentity"]);
        Assert.Equal(3, roots.Count(p => p.Value!["enabled"]!.GetValue<bool>()));
        Assert.Single(roots.Where(p => p.Key != "revoked" && p.Value!["folderIdentity"]?.GetValue<string>() == "c"));
    }

    [Fact]
    public void RowIdReuseRevokesOldNamespaceEvenWhenNoEmptySnapshotWasObserved()
    {
        var roots = new JsonObject { ["old"] = Grant(Folder(1, "old-identity")) };
        Assert.True(LanSyncService.ReconcileRegisteredRoots(roots, [Folder(1, "new-identity")], shareAll: true));
        Assert.True(roots["old"]!["revoked"]!.GetValue<bool>());
        Assert.False(roots["old"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("old-identity", roots["old"]!["folderIdentity"]!.GetValue<string>());
        var replacement = Assert.Single(roots.Where(p => p.Key != "old"));
        Assert.Equal("new-identity", replacement.Value!["folderIdentity"]!.GetValue<string>());
        // Restoring a record with an already revoked identity must also get a new namespace.
        Assert.True(LanSyncService.ReconcileRegisteredRoots(roots, [Folder(1, "old-identity")], shareAll: true));
        Assert.True(roots["old"]!["revoked"]!.GetValue<bool>());
        Assert.False(roots["old"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(3, roots.Count);
    }

    [Fact]
    public void RemovalWhileGlobalServiceIsOffStillPermanentlyRevokesOldIdentity()
    {
        var roots = new JsonObject { ["old"] = Grant(Folder(1, "old")) };
        Assert.True(LanSyncService.ReconcileRegisteredRoots(roots, [], shareAll: false));
        Assert.True(roots["old"]!["revoked"]!.GetValue<bool>());
        Assert.False(roots["old"]!["enabled"]!.GetValue<bool>());
    }
}

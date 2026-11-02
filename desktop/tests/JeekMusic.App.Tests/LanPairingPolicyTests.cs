using System.Text.Json.Nodes;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanPairingPolicyTests
{
    [Fact]
    public void MigrationGrantsEveryCapabilityWithoutChangingCredentialsOrOtherSettingsAndIsIdempotent()
    {
        var devices = JsonNode.Parse("""{"old":{"name":"old","tokenHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","generation":"stable","expiresAt":1,"permissions":{"readMusic":false,"writeFiles":false},"other":{"keep":true}},"legacy":{"tokenHash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"broken":{"name":"no credential","permissions":{"readMusic":false}}}""")!.AsObject();
        var original = devices.DeepClone();
        Assert.True(LanPairingPolicy.Normalize(devices));
        foreach (var id in new[] { "old", "legacy" }) Assert.True(JsonNode.DeepEquals(LanPairingPolicy.Permissions(), devices[id]!["permissions"]));
        foreach (var key in new[] { "name", "tokenHash", "generation", "expiresAt", "other" }) Assert.True(JsonNode.DeepEquals(original["old"]![key], devices["old"]![key]));
        Assert.Null(devices["legacy"]!["generation"]);
        Assert.True(JsonNode.DeepEquals(original["broken"], devices["broken"]));
        var stable = devices.ToJsonString();
        Assert.False(LanPairingPolicy.Normalize(devices)); Assert.Equal(stable, devices.ToJsonString());
        Assert.False(LanPairingPolicy.Normalize(new())); // No resurrected revoked device.
    }

    [Fact]
    public void LegacyPermissionInputsAreExplicitlyRejectedEvenWhenAllTrue()
    {
        LanPairingPolicy.RejectLegacyPermissions(null);
        foreach (var input in new[] { new JsonObject(), LanPairingPolicy.Permissions(), new JsonObject { ["readMusic"] = false } })
            Assert.Contains("revoke", Assert.Throws<NotSupportedException>(() => LanPairingPolicy.RejectLegacyPermissions(input)).Message);
    }
}

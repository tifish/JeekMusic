using System.Text.Json.Nodes;

namespace JeekMusic.Sync;

/// <summary>Pairing grants all existing application sync operations, within registered roots.</summary>
internal static class LanPairingPolicy
{
    internal const string Mode = "paired_all";
    internal static readonly string[] Keys = ["readMusic", "writeSync", "manageLearning", "downloadLearning", "writeFiles", "deleteFiles"];
    internal static JsonObject Permissions() => new(Keys.Select(k => new KeyValuePair<string, JsonNode?>(k, JsonValue.Create(true))));
    internal static bool HasCredential(JsonObject device) => device["tokenHash"] is JsonValue value && value.TryGetValue<string>(out var hash) && hash.Length == 64 && hash.All(char.IsAsciiHexDigit);

    // Never create a credential, rotate generation, or touch non-permission settings during migration.
    internal static bool Normalize(JsonObject devices)
    {
        bool changed = false;
        foreach (var device in devices.Select(p => p.Value).OfType<JsonObject>().Where(HasCredential))
        {
            if (!JsonNode.DeepEquals(device["permissions"], Permissions())) { device["permissions"] = Permissions(); changed = true; }
            if (device["permissionMigration"]?.GetValue<string>() != Mode) { device["permissionMigration"] = Mode; changed = true; }
        }
        return changed;
    }

    internal static void RejectLegacyPermissions(JsonObject? permissions)
    {
        if (permissions is not null) throw new NotSupportedException("Per-permission grants are retired. Pairing grants all sync capabilities; omit permissions when inviting. Use revoke to remove a device's access.");
    }
}

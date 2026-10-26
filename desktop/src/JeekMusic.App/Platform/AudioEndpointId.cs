namespace JeekMusic.Platform;

/// <summary>Adapt mpv device names without interpreting Windows' opaque endpoint IDs.</summary>
internal static class AudioEndpointId
{
    internal static string? FromDeviceName(string? name)
    {
        if (name is null or "auto") return null;
        var id = name.StartsWith("wasapi/", StringComparison.Ordinal) ? name[7..] : name;
        // Older mpv device names used only the braced render GUID. Retain that alias,
        // but never prepend a local endpoint prefix to a complete (e.g. RDP) ID.
        return Guid.TryParseExact(id, "B", out _) ? "{0.0.0.00000000}." + id : id;
    }
}

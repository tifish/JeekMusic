using System.Text.Json.Nodes;

namespace JeekMusic.Core.Sync;

/// <summary>Exact observed versions. These are change hints, never security identities.</summary>
public static class FileMetadata
{
    public const int Protocol = 2;
    public static string Version(long size, long modifiedNs) => $"m2:{size}:{modifiedNs}";
    public static string Stamp(Microsoft.Win32.SafeHandles.SafeFileHandle handle) => Version(
        RandomAccess.GetLength(handle),checked((File.GetLastWriteTimeUtc(handle).Ticks-DateTime.UnixEpoch.Ticks)*100));
    public static JsonObject Observe(string root, string relative)
    {
        Interlocked.Increment(ref MetadataObservations);
        // An unavailable root/ancestor is unknown, not an empty directory.
        LanFiles.Resolve(root, "", true);
        try
        {
            var full = LanFiles.Resolve(root, relative);
            var info = new FileInfo(full);
            if ((info.Attributes & FileAttributes.Directory) != 0) throw new IOException("Expected regular file");
            var size = info.Length;
            var ns = checked((info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100);
            return new() { ["known"] = true, ["exists"] = true, ["size"] = size, ["modifiedNs"] = ns,
                ["precisionNs"] = 100, ["metadataVersion"] = Version(size, ns) };
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Re-resolve the root after ENOENT: disconnection must not propagate deletions.
            LanFiles.Resolve(root, "", true);
            return new() { ["known"] = true, ["exists"] = false, ["metadataVersion"] = null };
        }
    }
    public static string? Token(JsonNode? row) => row?["metadataVersion"]?.GetValue<string>();
    public static long MetadataObservations, HashBytes, TransferBytes, SidecarParseBytes;
}

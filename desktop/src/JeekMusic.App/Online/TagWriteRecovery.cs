using System.Text.Json;
using System.Text.Json.Serialization;
using JeekMusic.Infrastructure;

namespace JeekMusic.Online;

internal sealed record TagWriteJournal(int Version, string Source, string Candidate, string Original,
    TagWriteCommit.Stamp SourceIdentity, string SourceHash, TagWriteCommit.Stamp CandidateIdentity, string CandidateHash, string MetadataHash);

[JsonSerializable(typeof(TagWriteJournal))]
internal partial class TagWriteJournalJson : JsonSerializerContext;

/// <summary>Write-ahead mapping, available before any library enumeration/deletion.</summary>
internal static class TagWriteRecovery
{
    static string DirectoryPath => Path.Combine(AppPaths.DataDir, "tag-transactions");
    internal static string? LastProblem { get; private set; }
    internal static int PendingCount { get; private set; }
    internal static string Persist(TagWriteJournal journal)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, TagWriteJournalJson.Default.TagWriteJournal);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(bytes); file.Flush(true); // Must complete BEFORE either name handoff.
        return path;
    }
    static TagWriteJournal Read(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new IOException("Oversized tag transaction journal");
        var j = JsonSerializer.Deserialize(File.ReadAllBytes(path), TagWriteJournalJson.Default.TagWriteJournal) ?? throw new IOException("Empty tag transaction journal");
        if (j.Source is null || j.Candidate is null || j.Original is null || j.SourceHash is null || j.CandidateHash is null || j.MetadataHash is null) throw new IOException("Incomplete tag transaction journal");
        var sourceParent = Path.GetDirectoryName(j.Source) ?? throw new IOException("Invalid tag transaction mapping");
        var staging = Path.Combine(sourceParent, ".jeekmusic", "tag-writes");
        bool InStaging(string p) => Path.IsPathFullyQualified(p) && Path.GetDirectoryName(p) is { } parent && parent.Equals(staging, StringComparison.OrdinalIgnoreCase);
        if (j.Version != 1 || !Path.IsPathFullyQualified(j.Source) || !InStaging(j.Candidate) || !InStaging(j.Original) ||
            !j.Original.EndsWith(".original", StringComparison.Ordinal) || new[] { j.SourceHash, j.CandidateHash, j.MetadataHash }.Any(h => h.Length != 64 || !h.All(Uri.IsHexDigit)))
            throw new IOException("Invalid tag transaction mapping");
        return j;
    }
    // False means scanner must defer this folder. Never turn an interrupted edit
    // into a missing-file deletion, including unreadable/partial/conflicting journals.
    internal static bool RecoverBeforeScan(string? root = null)
    {
        lock (TagWriteCommit.Gate)
        {
            LastProblem = null; PendingCount = 0; var safe = true;
            if (!Directory.Exists(DirectoryPath)) return true;
            string[] journals;
            try { journals = Directory.GetFiles(DirectoryPath, "*.json"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { PendingCount = 1; LastProblem = e.Message; Log.Warn("tagwriter", "Cannot inspect transaction journals; defer scan", e); return false; }
            foreach (var path in journals)
            {
                TagWriteJournal? j = null;
                try
                {
                    j = Read(path);
                    if (root is not null && !j.Source.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { PendingCount++; continue; }
                    TagWriteCommit.Recover(j);
                    File.Delete(path);
                    Log.Info("tagwriter", "Recovered/finalized tag transaction: " + j.Source);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or JsonException or ArgumentException or System.Security.SecurityException)
                {
                    PendingCount++; safe = false; LastProblem = e.Message;
                    Log.Warn("tagwriter", "Tag recovery pending; defer scan; journal=" + path + " source=" + j?.Source, e);
                }
            }
            return safe;
        }
    }
}

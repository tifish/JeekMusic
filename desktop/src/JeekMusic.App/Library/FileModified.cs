namespace JeekMusic.Library;

/// <summary>Cached file mtime is UTC .NET ticks, never a scan/add date. Nonpositive Unix dates and out-of-range values are unknown.</summary>
public static class FileModified
{
    public static bool IsValid(long ticks) => ticks > DateTime.UnixEpoch.Ticks && ticks <= DateTime.MaxValue.Ticks;
    public static DateTime? Utc(long ticks) => IsValid(ticks) ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public static int Compare(long a, long b, bool reverse = false)
    {
        var validA = IsValid(a); var validB = IsValid(b);
        if (validA != validB) return validA ? -1 : 1;
        return validA ? (reverse ? a.CompareTo(b) : b.CompareTo(a)) : 0;
    }
}

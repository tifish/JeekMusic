namespace JeekMusic.Core.Library;

/// <summary>
/// 备份 / 同步文件中的歌曲引用 = 相对于所属音乐文件夹的路径（移动端 BackupRefs.kt，移动端设计 §3.1；桌面端设计 §11.1）。
/// 音乐文件夹换了卷或换了上级目录（比如拷到新设备、挪到另一个盘），依然能对上。
/// </summary>
public static class BackupRefs
{
    public sealed record Root(string VolumeName, string RelativePath);

    /// <summary>
    /// track_key（"卷{separator}卷内路径 + 文件名[#NN]"）→ 相对引用（"子目录/文件名[#NN]"）。找不到所属的音乐文件夹时返回 null。
    /// 移动端的分隔符是 ':'；路径怎么表示（"/" 还是 "\"、大小写）由调用方保证和 <see cref="Root.RelativePath"/> 一致。
    /// </summary>
    public static string? RefOf(string trackKey, IEnumerable<Root> roots, char separator = ':')
    {
        var at = trackKey.IndexOf(separator);
        if (at <= 0) return null;
        var volume = trackKey[..at];
        var path = trackKey[(at + 1)..];
        var root = roots
            .Where(r => r.VolumeName == volume && path.StartsWith(r.RelativePath, StringComparison.Ordinal))
            .MaxBy(r => r.RelativePath.Length);
        return root is null ? null : path[root.RelativePath.Length..];
    }

    /// <summary>
    /// 恢复时的映射：先找完全相同的相对引用；找不到时按"后缀匹配"再找一次——
    /// 路径段从右往左比对，只有唯一命中时才采用（文件夹层级在新设备上可能不同）。
    /// </summary>
    public sealed class Matcher
    {
        private readonly IReadOnlyDictionary<string, string> _exact;
        private readonly Dictionary<string, List<KeyValuePair<string, string>>> _byFileName;

        /// <param name="refToTrackKey">本设备上每首歌的相对引用 → track_key</param>
        public Matcher(IReadOnlyDictionary<string, string> refToTrackKey)
        {
            _exact = refToTrackKey;
            _byFileName = refToTrackKey.Where(e => !e.Key.StartsWith("root:", StringComparison.OrdinalIgnoreCase)).GroupBy(e => LastSegment(e.Key)).ToDictionary(g => g.Key, g => g.ToList());
        }

        public string? Match(string reference)
        {
            if (_exact.TryGetValue(reference, out var exact)) return exact;
            if (reference.StartsWith("root:", StringComparison.OrdinalIgnoreCase)) return null;
            if (!_byFileName.TryGetValue(LastSegment(reference), out var candidates)) return null;
            if (candidates.Count == 1) return candidates[0].Value;
            // 同名文件有多个时，取共同后缀段最多且唯一的那个
            var segments = reference.Split('/');
            var scored = candidates.Select(c => (c.Value, Score: CommonSuffix(segments, c.Key.Split('/')))).ToList();
            var best = scored.Max(s => s.Score);
            var top = scored.Where(s => s.Score == best).ToList();
            return top.Count == 1 ? top[0].Value : null;
        }

        private static string LastSegment(string reference)
        {
            var slash = reference.LastIndexOf('/');
            return slash >= 0 ? reference[(slash + 1)..] : reference;
        }

        private static int CommonSuffix(string[] a, string[] b)
        {
            var n = 0;
            while (n < a.Length && n < b.Length && a[^(n + 1)] == b[^(n + 1)]) n++;
            return n;
        }
    }
}

using JeekMusic.Core.Text;

namespace JeekMusic.Core.Library;

/// <summary>某个目录（相对于音乐文件夹，以分隔符结尾）里直接含有的歌曲数。</summary>
public sealed record DirectoryCount(string RelativePath, int Count);

public sealed record FolderNode(string Name, string RelativePath, int TrackCount);

/// <summary>
/// 根据歌曲所在的目录在内存中折叠出直接子文件夹（移动端 FolderTree.kt，移动端设计 §5.2），
/// 不扫描文件系统，所以空文件夹自然不会出现（需求 F-3）。歌曲数包含更深的层级。
/// </summary>
public static class FolderTree
{
    /// <param name="path">父目录，以 <paramref name="separator"/> 结尾；根目录为 ""</param>
    /// <param name="counts">各目录直接含有的歌曲数</param>
    /// <param name="sortKey">把文件夹名转成排序键（中文转拼音，需求 U-8）；默认不转</param>
    /// <param name="separator">目录分隔符：移动端 "/"，桌面端 "\"</param>
    public static IReadOnlyList<FolderNode> Children(
        string path,
        IEnumerable<DirectoryCount> counts,
        Func<string, string>? sortKey = null,
        char separator = '/')
    {
        sortKey ??= static s => s;
        var byChild = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var count in counts)
        {
            if (!count.RelativePath.StartsWith(path, StringComparison.Ordinal) || count.RelativePath == path) continue;
            var rest = count.RelativePath[path.Length..];
            var end = rest.IndexOf(separator);
            var childName = end >= 0 ? rest[..end] : rest;
            if (childName.Length == 0) continue;
            if (byChild.TryGetValue(childName, out var total)) byChild[childName] = total + count.Count;
            else
            {
                byChild[childName] = count.Count;
                order.Add(childName);
            }
        }
        return order
            .Select(name => new FolderNode(name, $"{path}{name}{separator}", byChild[name]))
            .OrderBy(node => sortKey(node.Name), NaturalOrderComparer.Instance)
            .ToList();
    }
}

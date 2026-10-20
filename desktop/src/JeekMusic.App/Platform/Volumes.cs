namespace JeekMusic.Platform;

/// <summary>
/// 音乐文件夹的位置用"卷 + 卷内路径"记录，而不是盘符（设计 §4）：移动硬盘、U 盘每次插上的盘符可能不同。
/// <list type="bullet">
/// <item>本地卷：卷 ID 是 "{卷 GUID}"，卷内路径 "\Music\"；</item>
/// <item>网络路径（\\NAS\Music）：卷 ID 是 "unc"，卷内路径就是 UNC 路径；</item>
/// <item>取不到卷 GUID 的（映射的网络盘等）：卷 ID 是 "path"，卷内路径是完整路径。</item>
/// </list>
/// </summary>
public static class Volumes
{
    /// <summary>把一个目录解析成 (卷 ID, 卷内路径)；路径都以 "\" 结尾。</summary>
    public static (string volumeId, string pathInVolume) Locate(string directory)
    {
        var full = WithSlash(Path.GetFullPath(directory));
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return ("unc", full);
        var mount = new char[1024];
        if (Win32.GetVolumePathName(full, mount, mount.Length))
        {
            var mountPoint = new string(mount).TrimEnd('\0');
            var name = new char[64];
            if (Win32.GetVolumeNameForVolumeMountPoint(mountPoint, name, name.Length))
            {
                // "\\?\Volume{GUID}\"
                var volume = new string(name).TrimEnd('\0');
                var guid = volume[volume.IndexOf('{')..(volume.IndexOf('}') + 1)];
                return (guid, @"\" + full[mountPoint.Length..]);
            }
        }
        return ("path", full);
    }

    /// <summary>这个卷当前挂在哪里：返回完整路径（"\" 结尾），卷不在（盘拔掉了）时返回 null。</summary>
    public static string? CurrentRoot(string volumeId, string pathInVolume)
    {
        if (volumeId is "unc" or "path") return Directory.Exists(pathInVolume) ? pathInVolume : null;
        var buffer = new char[1024];
        if (!Win32.GetVolumePathNamesForVolumeName(@"\\?\Volume" + volumeId + @"\", buffer, buffer.Length, out _)) return null;
        // 以 \0 分隔的多个挂载点，取第一个（通常是盘符）
        var first = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return null;
        var root = first.TrimEnd('\\') + pathInVolume;
        return Directory.Exists(root) ? root : null;
    }

    /// <summary>歌曲的稳定键：卷 ID + "|" + 卷内路径，小写（Windows 的路径不区分大小写）。</summary>
    public static string TrackKey(string volumeId, string pathInVolumeOfFolder, string relativePath) =>
        (volumeId + "|" + pathInVolumeOfFolder + relativePath).ToLowerInvariant();

    public static string WithSlash(string path) => path.EndsWith('\\') ? path : path + '\\';
}

using System.Text.Json.Nodes;
using JeekMusic.Library;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>每设备的主动收起集合。只在既有窗口快照保存时落盘，不进入曲库或同步。</summary>
internal sealed class FolderExpansionState
{
    const string GroupKey = "folders:v1";
    readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    // 卷身份 + 卷内绝对目录：不依赖显示名、盘符或数据库行号；长度前缀隔开卷和路径。
    internal static string DirectoryKey(LibraryFolder folder, string dir)
    {
        var volume = folder.VolumeId.ToUpperInvariant();
        var path = (folder.PathInVolume.Replace('/', '\\').TrimEnd('\\') + "\\" + dir.Replace('/', '\\').Trim('\\')).TrimEnd('\\').ToUpperInvariant();
        return $"folder:v1:{volume.Length}:{volume}:{path}";
    }

    static string? Key(NavNode node) => node.Kind == NavKind.Folders ? GroupKey : node.FolderExpansionKey;

    public void Restore(UiState ui)
    {
        _collapsed.Clear();
        // ExpandedNav 的缺项从未代表明确收起，旧字段也没有保存/恢复调用；不据此迁移。
        foreach (var key in ui.CollapsedFolders ?? [])
            if (!string.IsNullOrEmpty(key) && (key.Equals(GroupKey, StringComparison.OrdinalIgnoreCase) || key.StartsWith("folder:v1:", StringComparison.OrdinalIgnoreCase)))
                _collapsed.Add(key);
    }

    public void Save(UiState ui) => ui.CollapsedFolders = _collapsed.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public void Apply(NavNode node)
    {
        if (Key(node) is not { } key) return;
        node.IsExpanded = !_collapsed.Contains(key);
    }

    public void RecordUserExpansion(NavNode node, bool expanded)
    {
        if (Key(node) is not { } key) return;
        if (expanded) _collapsed.Remove(key);
        else _collapsed.Add(key);
    }

    public JsonObject Diagnostics(IEnumerable<NavNode>? navigation)
    {
        var nodes = navigation?.SelectMany(Flatten).Where(n => n.Kind is NavKind.Folders or NavKind.Folder).ToArray() ?? [];
        return new JsonObject
        {
            ["created"] = navigation is not null,
            ["defaultExpanded"] = true,
            ["persistence"] = "device-local-ui-snapshot",
            ["keyVersion"] = 1,
            ["automaticNavigation"] = "preserve-collapsed",
            ["explicitLocate"] = "temporary-reveal",
            ["collapsedPreferenceCount"] = _collapsed.Count,
            ["nodeCount"] = nodes.Length,
            ["expandedNodeCount"] = nodes.Count(n => n.IsExpanded),
            ["temporarilyExpandedCount"] = nodes.Count(n => n.IsExpanded && Key(n) is { } key && _collapsed.Contains(key))
        };
    }

    static IEnumerable<NavNode> Flatten(NavNode node) => node.Children.SelectMany(Flatten).Prepend(node);
}

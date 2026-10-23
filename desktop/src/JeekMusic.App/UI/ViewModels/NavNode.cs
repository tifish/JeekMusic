using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekMusic.Library;

namespace JeekMusic.UI.ViewModels;

public enum NavKind { Folders, Folder, Artists, Playlists, Playlist, PinnedArtist }

/// <summary>
/// 左侧导航栏的一项（需求 W-2、W-3）：内置的"文件夹 / 艺术家 / 播放列表"，文件夹树的节点，播放列表，固定的艺术家。
/// </summary>
public sealed partial class NavNode : ObservableObject
{
    public required NavKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public ObservableCollection<NavNode> Children { get; } = new();

    /// <summary>文件夹节点：所在的音乐文件夹和目录。</summary>
    public long FolderId { get; init; }
    public string Dir { get; init; } = "";
    /// <summary>本机展开偏好的身份，与选择/曲库业务 Key 分开。</summary>
    internal string? FolderExpansionKey { get; init; }
    public long PlaylistId { get; init; }
    /// <summary>固定的艺术家：艺术家的 NameKey。</summary>
    public string ArtistKey { get; init; } = "";
    /// <summary>导航顺序里的 (type, ref)，拖动排序时用。</summary>
    public NavItem? Item { get; init; }

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isExpanded;

    /// <summary>内置项和固定艺术家可以拖动排序（需求 T-5）；文件夹树里的子项、播放列表不行。</summary>
    public bool IsTopLevel => Item is not null;

    public bool IsFolderRoot => Kind == NavKind.Folder && Dir.Length == 0;

    public string Key => Kind switch
    {
        NavKind.Folder => $"folder:{FolderId}:{Dir}",
        NavKind.Playlist => $"playlist:{PlaylistId}",
        NavKind.PinnedArtist => $"artist:{ArtistKey}",
        _ => Kind.ToString(),
    };
}

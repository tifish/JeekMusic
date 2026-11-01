namespace JeekMusic.UI;

/// <summary>
/// 图标：Windows 自带的 Segoe Fluent Icons（Windows 11）/ Segoe MDL2 Assets（Windows 10）字体里的字形，
/// 不用另外打包图标（设计 §12）。两个字体里这些码位是一样的。
/// </summary>
public static class Icons
{
    // Font-independent vector for the small, foreground-inheriting annotation marker.
    // Geometry belongs to its Avalonia dispatcher; do not share one across UI sessions.
    public static Avalonia.Media.Geometry ManualAnnotation => Avalonia.Media.Geometry.Parse(
        "M 1,9 L 8,2 L 10,4 L 3,11 L 0,12 Z M 9,1 L 10,0 Q 10.5,-0.5 11,0 L 12,1 Q 12.5,1.5 12,2 L 11,3 Z");

    public const string FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public const string Play = "";
    public const string Pause = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Shuffle = "";
    public const string RepeatAll = "";
    public const string RepeatOne = "";
    public const string Volume = "";
    public const string Volume0 = "";
    public const string Mute = "";
    public const string Moon = "";
    public const string Queue = "";
    public const string Lyrics = "";
    public const string MiniPlayer = "";
    public const string Expand = "";
    public const string Search = "";
    public const string Filter = "";
    public const string Settings = "";
    public const string Folder = "";
    public const string FolderOpen = "";
    public const string Artist = "";
    public const string Playlist = "";
    public const string Music = "";
    public const string Album = "";
    public const string Pin = "";
    public const string Unpin = "";
    public const string Back = "";
    public const string Forward = "";
    public const string More = "";
    public const string Add = "";
    public const string Close = "";
    public const string Refresh = "";
    public const string Delete = "";
    public const string Rename = "";
    public const string Move = "";
    public const string Info = "";
    public const string Tag = "";
    public const string Download = "";
    public const string Sync = "";
    public const string Lock = "";
    public const string Unlock = "";
    public const string Locate = "";
    public const string NavPane = "";
    public const string Checkmark = "";
    public const string Sort = "";
    public const string Speaker = "";
}

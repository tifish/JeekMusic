using CommunityToolkit.Mvvm.ComponentModel;
using JeekMusic.Library;
using JeekMusic.Core.Analysis;

namespace JeekMusic.UI.ViewModels;

/// <summary>
/// 歌曲表格的一行（需求 G-1）。播放列表里对不上本机歌曲的条目 <see cref="Track"/> 为 null，显示为不可用（需求 PL-3、SY-6）。
/// </summary>
public sealed partial class SongRow : ObservableObject
{
    public SongRow(Track? track, int position, SongTags? tags, string? missingRef = null, string? missingKey = null)
    {
        Track = track;
        Position = position;
        Tags = tags;
        MissingRef = missingRef;
        MissingKey = missingKey;
    }

    public Track? Track { get; }
    /// <summary>在来源列表里的位置（播放列表的条目位置）。</summary>
    public int Position { get; }
    public SongTags? Tags { get; }
    public string? MissingRef { get; }
    public string? MissingKey { get; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _cover;

    public bool Available => Track is not null;

    public string Title => Track?.Title ?? (MissingRef is { } r ? Path.GetFileNameWithoutExtension(r.Split('#')[0]) : L.T("（不可用）"));
    public string Artist => Track is { } t ? Text.Artist(t) : "";
    public string Album => Track?.Album ?? "";
    public string Duration => Track is { } t ? Text.Duration(t.DurationMs) : "";
    public string FileName => Track?.FileName ?? MissingRef ?? "";
    public string Modified => Track is { } t ? Text.Modified(t.Mtime) : "—";
    public string Format => Track?.FormatLabel ?? "";
    public string TagText => Text.Tags(Tags);
    public string MoodText => Text.TagCell(Tags, TagDimension.MOOD);
    public string VoiceText => Text.TagCell(Tags, TagDimension.VOICE);
    public string GenreText => Text.TagCell(Tags, TagDimension.GENRE);
    public string CalmnessText => Text.TagCell(Tags, TagDimension.CALMNESS);
    public string MoodSource => Text.TagSource(Tags, TagDimension.MOOD);
    public string VoiceSource => Text.TagSource(Tags, TagDimension.VOICE);
    public string GenreSource => Text.TagSource(Tags, TagDimension.GENRE);
    public string CalmnessSource => Text.TagSource(Tags, TagDimension.CALMNESS);
    public string MoodActionName => L.T("修正标签") + ": " + L.T("运动") + " · " + MoodText + " · " + MoodSource;
    public string VoiceActionName => L.T("修正标签") + ": " + L.T("人声") + " · " + VoiceText + " · " + VoiceSource;
    public string GenreActionName => L.T("修正标签") + ": " + L.T("古典") + " · " + GenreText + " · " + GenreSource;
    public string CalmnessActionName => L.T("修正标签") + ": " + L.T("安静") + " · " + CalmnessText + " · " + CalmnessSource;
    public double Opacity => Available ? 1 : 0.45;
}

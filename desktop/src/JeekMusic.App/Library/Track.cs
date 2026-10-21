namespace JeekMusic.Library;

/// <summary>
/// 一首歌（数据库的 track 行，也是内存曲库快照里的元素）。CUE 整轨展开的虚拟歌曲也是一条（<see cref="CueTrackNo"/> 不为 null）。
/// 创建后不再修改（快照是不可变的）；派生的展示字段由扫描时的 TrackBuilder 算好。
/// </summary>
public sealed class Track
{
    public long Id { get; init; }
    /// <summary>稳定键：卷 ID + "|" + 卷内路径（小写）；CUE 虚拟歌曲再加 "#NN"（设计 §4）。</summary>
    public required string TrackKey { get; init; }
    public long FolderId { get; init; }
    /// <summary>相对于音乐文件夹的目录，"\" 分隔、"\" 结尾；根目录为空串。</summary>
    public required string Dir { get; init; }
    public required string FileName { get; init; }

    public string? TitleTag { get; init; }
    public required string Title { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? AlbumKey { get; init; }
    /// <summary>原始艺术家标签，多值以 \u0000 连接；null 表示没有标签。</summary>
    public string? ArtistRaw { get; init; }
    /// <summary>展示用的艺术家；空串时界面显示"未知艺术家"。</summary>
    public required string ArtistDisplay { get; init; }
    public bool ArtistInferred { get; init; }
    /// <summary>拆分后的艺术家（需求 A-2）。</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];

    public int? TrackNo { get; init; }
    public int? DiscNo { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }
    public long DurationMs { get; init; }
    public string? Codec { get; init; }
    public int? Bitrate { get; init; }
    public int? SampleRate { get; init; }
    public int? BitsPerSample { get; init; }
    public int? Channels { get; init; }
    public bool HasEmbeddedLyrics { get; init; }
    public bool HasEmbeddedCover { get; init; }

    public int? CueTrackNo { get; init; }
    public long? StartMs { get; init; }
    /// <summary>null 表示播到文件末尾。</summary>
    public long? EndMs { get; init; }

    /// <summary>文件修改时间（UTC ticks），增量扫描用；也用于"按修改时间排序"。</summary>
    public long Mtime { get; init; }
    public long Size { get; init; }

    public string SortTitle { get; init; } = "";
    public string SortFile { get; init; } = "";
    public string SortArtist { get; init; } = "";

    public bool IsCue => CueTrackNo is not null;

    /// <summary>实际的音频文件名（CUE 虚拟歌曲是整轨文件）；完整路径要加上音乐文件夹的当前根目录。</summary>
    public string RelativePath => Dir + FileName;

    string? _formatLabel;
    public string FormatLabel
    {
        get
        {
            if (_formatLabel is not null) return _formatLabel;
            var codec = Codec?.ToUpperInvariant() ?? Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
            if (BitsPerSample is > 16 || SampleRate is > 48000)
                return _formatLabel = $"{codec} {BitsPerSample ?? 16}/{(SampleRate ?? 44100) / 1000.0:0.#}";
            return _formatLabel = codec;
        }
    }
}

/// <summary>音乐文件夹（library_folder 行）。</summary>
public sealed class LibraryFolder
{
    public long Id { get; init; }
    /// <summary>"{卷 GUID}" 或 "unc"。</summary>
    public required string VolumeId { get; init; }
    /// <summary>卷内路径，"\" 开头和结尾；网络路径是完整的 UNC 路径（"\\" 开头，"\" 结尾）。</summary>
    public required string PathInVolume { get; init; }
    /// <summary>当前解析到的完整路径（"\" 结尾）。</summary>
    public required string Root { get; set; }
    public required string DisplayName { get; init; }
    public bool Available { get; set; } = true;
    public long AddedAt { get; init; }
}

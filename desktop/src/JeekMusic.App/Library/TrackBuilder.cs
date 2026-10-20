using System.Text;
using JeekMusic.Core.Library;
using JeekMusic.Core.Text;
using JeekMusic.Platform;

namespace JeekMusic.Library;

/// <summary>扫描相关的规则设置（来自 AppSettings 的曲库规则，参与多设备同步）。</summary>
public sealed record ScanConfig(ArtistSplitConfig Split, bool InferFromFileName, FileNameFormat FileNameFormat, Encoding? LegacyCharset);

/// <summary>扫描时一个音频文件的位置信息。</summary>
public sealed record AudioFile(LibraryFolder Folder, string Dir, string FileName, long Mtime, long Size)
{
    public string RelativePath => Dir + FileName;
    public string FullPath => Folder.Root + Dir + FileName;
}

/// <summary>
/// 根据标签计算一首歌的全部派生字段（规则同移动端 data/scanner/TrackFactory.kt）：乱码修复、展示标题、展示艺术家、
/// 拆分后的艺术家、album_key、排序键。纯函数，不访问数据库；改了分隔符等设置后，也用它从库里的原始字段重算（<see cref="Rebuild"/>）。
/// </summary>
public sealed class TrackBuilder(ScanConfig config)
{
    public ScanConfig Config => config;

    string? Fix(string? value) => value is null ? null : CharsetFixer.Fix(value, config.LegacyCharset);

    public Track Build(AudioFile file, TrackTags? tags, long existingId)
    {
        var titleTag = Fix(tags?.Title);
        var rawArtists = tags?.Artists.Select(a => CharsetFixer.Fix(a, config.LegacyCharset)).ToList() ?? [];
        var album = Fix(tags?.Album);
        var albumArtist = Fix(tags?.AlbumArtist);
        var d = Derive(file.FileName, titleTag, rawArtists);
        return new Track
        {
            Id = existingId,
            TrackKey = Volumes.TrackKey(file.Folder.VolumeId, file.Folder.PathInVolume, file.RelativePath),
            FolderId = file.Folder.Id,
            Dir = file.Dir,
            FileName = file.FileName,
            TitleTag = titleTag,
            Title = d.Title,
            Album = album,
            AlbumArtist = albumArtist,
            AlbumKey = AlbumKeys.Of(album, albumArtist, file.Folder.VolumeId, file.Folder.PathInVolume + file.Dir),
            ArtistRaw = rawArtists.Count > 0 ? string.Join(LibraryDb.MultiValueSeparator, rawArtists) : null,
            ArtistDisplay = d.ArtistDisplay,
            ArtistInferred = d.Inferred,
            Artists = d.Artists,
            TrackNo = tags?.TrackNo,
            DiscNo = tags?.DiscNo,
            Year = tags?.Year,
            Genre = Fix(tags?.Genre),
            DurationMs = tags?.DurationMs ?? 0,
            Codec = tags?.Codec,
            Bitrate = tags?.Bitrate,
            SampleRate = tags?.SampleRate,
            BitsPerSample = tags?.BitsPerSample,
            Channels = tags?.Channels,
            HasEmbeddedLyrics = tags?.HasLyrics == true,
            HasEmbeddedCover = tags?.HasCover == true,
            Mtime = file.Mtime,
            Size = file.Size,
            SortTitle = SortKeys.SortKey(d.Title),
            SortFile = SortKeys.SortKey(file.FileName),
            SortArtist = SortKeys.SortKey(d.ArtistDisplay),
        };
    }

    /// <summary>
    /// CUE 整轨：一个文件展开成多条虚拟歌曲（需求 CUE-1 ~ CUE-3，移动端设计 §4.6）。
    /// 歌名、艺术家、音轨号来自 CUE；CUE 顶层 TITLE / PERFORMER 作为专辑名 / 专辑艺术家；缺少的字段取自文件本身的标签。
    /// </summary>
    public List<Track> BuildCue(AudioFile file, TrackTags? tags, CueSheet sheet, CueFile cueFile, IReadOnlyDictionary<string, long> existingIds)
    {
        var fileDuration = tags?.DurationMs ?? 0;
        var album = sheet.Title ?? Fix(tags?.Album);
        var albumArtist = sheet.Performer ?? Fix(tags?.AlbumArtist);
        var fileKey = Volumes.TrackKey(file.Folder.VolumeId, file.Folder.PathInVolume, file.RelativePath);
        var tracks = cueFile.Tracks.OrderBy(t => t.StartMs).ToList();
        var result = new List<Track>(tracks.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            var ct = tracks[i];
            long? end = i + 1 < tracks.Count ? tracks[i + 1].StartMs : null;
            var key = fileKey + "#" + ct.Number.ToString("00");
            var raw = ct.Performer is { } p ? [p] : tags?.Artists.Select(a => CharsetFixer.Fix(a, config.LegacyCharset)).ToList() ?? [];
            var title = ct.Title ?? ct.Number.ToString("00") + ". " + (album ?? Path.GetFileNameWithoutExtension(file.FileName));
            var display = string.Join(" / ", raw);
            result.Add(new Track
            {
                Id = existingIds.GetValueOrDefault(key),
                TrackKey = key,
                FolderId = file.Folder.Id,
                Dir = file.Dir,
                FileName = file.FileName,
                TitleTag = ct.Title,
                Title = title,
                Album = album,
                AlbumArtist = albumArtist,
                AlbumKey = AlbumKeys.Of(album, albumArtist, file.Folder.VolumeId, file.Folder.PathInVolume + file.Dir),
                ArtistRaw = raw.Count > 0 ? string.Join(LibraryDb.MultiValueSeparator, raw) : null,
                ArtistDisplay = display,
                ArtistInferred = false,
                Artists = ArtistSplitter.Split(raw, config.Split),
                TrackNo = ct.Number,
                DiscNo = tags?.DiscNo,
                Year = sheet.Year ?? tags?.Year,
                Genre = sheet.Genre ?? Fix(tags?.Genre),
                DurationMs = Math.Max(0, (end ?? fileDuration) - ct.StartMs),
                Codec = tags?.Codec,
                Bitrate = tags?.Bitrate,
                SampleRate = tags?.SampleRate,
                BitsPerSample = tags?.BitsPerSample,
                Channels = tags?.Channels,
                HasEmbeddedLyrics = false,
                HasEmbeddedCover = tags?.HasCover == true,
                CueTrackNo = ct.Number,
                StartMs = ct.StartMs,
                EndMs = end,
                Mtime = file.Mtime,
                Size = file.Size,
                SortTitle = SortKeys.SortKey(title),
                SortFile = SortKeys.SortKey(file.FileName),
                SortArtist = SortKeys.SortKey(display),
            });
        }
        return result;
    }

    /// <summary>用库里已存的原始字段重算派生字段（改了分隔符、例外名单或文件名推断设置后）。CUE 虚拟歌曲只重新拆分艺术家。</summary>
    public Track Rebuild(Track t)
    {
        var raw = t.ArtistRaw?.Split(LibraryDb.MultiValueSeparator).ToList() ?? [];
        if (t.IsCue)
        {
            var artists = ArtistSplitter.Split(raw, config.Split);
            return Copy(t, t.Title, t.ArtistDisplay, artists, false);
        }
        var d = Derive(t.FileName, t.TitleTag, raw);
        return Copy(t, d.Title, d.ArtistDisplay, d.Artists, d.Inferred);
    }

    static Track Copy(Track t, string title, string display, IReadOnlyList<string> artists, bool inferred) => new()
    {
        Id = t.Id, TrackKey = t.TrackKey, FolderId = t.FolderId, Dir = t.Dir, FileName = t.FileName, TitleTag = t.TitleTag,
        Title = title, Album = t.Album, AlbumArtist = t.AlbumArtist, AlbumKey = t.AlbumKey, ArtistRaw = t.ArtistRaw,
        ArtistDisplay = display, ArtistInferred = inferred, Artists = artists, TrackNo = t.TrackNo, DiscNo = t.DiscNo, Year = t.Year,
        Genre = t.Genre, DurationMs = t.DurationMs, Codec = t.Codec, Bitrate = t.Bitrate, SampleRate = t.SampleRate,
        BitsPerSample = t.BitsPerSample, Channels = t.Channels, HasEmbeddedLyrics = t.HasEmbeddedLyrics, HasEmbeddedCover = t.HasEmbeddedCover,
        CueTrackNo = t.CueTrackNo, StartMs = t.StartMs, EndMs = t.EndMs, Mtime = t.Mtime, Size = t.Size,
        SortTitle = SortKeys.SortKey(title), SortFile = t.SortFile, SortArtist = SortKeys.SortKey(display),
    };

    sealed record Derived(string Title, string ArtistDisplay, IReadOnlyList<string> Artists, bool Inferred);

    Derived Derive(string fileName, string? titleTag, IReadOnlyList<string> rawArtists)
    {
        var inferred = rawArtists.Count == 0 && config.InferFromFileName ? FileNameInferrer.Infer(fileName, config.FileNameFormat) : null;
        IReadOnlyList<string> artists;
        if (rawArtists.Count > 0) artists = ArtistSplitter.Split(rawArtists, config.Split);
        // 文件名里不能出现 "/"，网易云等用 "," 分隔多个艺术家，所以推断出的艺术家额外按逗号拆
        else if (inferred is not null)
            artists = ArtistSplitter.Split([inferred.Artist], config.Split with { Separators = config.Split.Separators.Append(",").ToHashSet() });
        else artists = [];
        return new Derived(
            Title: titleTag ?? inferred?.Title ?? Path.GetFileNameWithoutExtension(fileName),
            ArtistDisplay: rawArtists.Count > 0 ? string.Join(" / ", rawArtists) : inferred?.Artist ?? "",
            Artists: artists,
            Inferred: rawArtists.Count == 0 && inferred is not null);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JeekMusic.Spike;

public sealed class Track
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required string Duration { get; init; }
    public required string Folder { get; init; }

    string? _searchKey;
    public string SearchKey => _searchKey ??= $"{Title}\n{Artist}\n{Album}".ToLowerInvariant();
}

static class TrackSource
{
    static readonly string[] Titles =
    [
        "身骑白马", "大鱼", "起风了", "光年之外", "孤勇者", "青花瓷", "月亮代表我的心", "后来", "夜曲", "红豆",
        "千本桜", "夜に駆ける", "残酷な天使のテーゼ", "シャルル", "Lemon", "打上花火",
        "봄날", "밤편지", "Dynamite", "좋은 날",
        "Bohemian Rhapsody", "Hotel California", "The Sound of Silence", "Problem", "Yesterday",
        "Symphony No. 9 in D minor, Op. 125 \"Choral\": IV. Presto – Allegro assai", "Gymnopédie No. 1",
        "Clair de Lune", "Cello Suite No. 1 in G major, BWV 1007: I. Prélude", "Ave Maria, D. 839",
    ];

    static readonly string[] Artists =
    [
        "周深", "徐佳莹", "周杰伦", "邓丽君", "G.E.M. 邓紫棋", "陈奕迅 feat. 王菲", "刘若英", "王菲",
        "YOASOBI", "米津玄師", "高橋洋子", "初音ミク", "DAOKO × 米津玄師",
        "BTS (방탄소년단)", "IU (아이유)",
        "Queen", "Eagles", "Simon & Garfunkel", "Ariana Grande / Iggy Azalea", "The Beatles",
        "Herbert von Karajan / Berliner Philharmoniker", "Erik Satie", "Claude Debussy", "Yo-Yo Ma (马友友)",
    ];

    static readonly string[] Albums =
    [
        "深的深", "我心深处", "叶惠美", "淡淡幽情", "新的心跳", "THE BOOK", "STRAY SHEEP", "YOU BE LOVED",
        "LOVE YOURSELF 結 'Answer'", "Palette", "A Night at the Opera", "Hotel California (Remastered)",
        "Bookends", "My Everything", "Abbey Road", "Beethoven: Die 9 Symphonien", "Bach: The Cello Suites",
    ];

    static readonly string[] Variants = ["", " (Live)", " (伴奏)", " - Remastered 2011", " (TV Size)", " [弦乐版]", " (어쿠스틱)", " (Demo)"];

    public static List<Track> Synthetic(int count)
    {
        var rnd = new Random(42);
        var list = new List<Track>(count);
        for (var i = 0; i < count; i++)
        {
            var artist = Artists[rnd.Next(Artists.Length)];
            var album = Albums[rnd.Next(Albums.Length)];
            var seconds = rnd.Next(90, 900);
            list.Add(new Track
            {
                Title = Titles[rnd.Next(Titles.Length)] + Variants[rnd.Next(Variants.Length)],
                Artist = artist,
                Album = album,
                Duration = $"{seconds / 60}:{seconds % 60:00}",
                Folder = $@"D:\Music\{artist}\{album}",
            });
        }
        return list;
    }

    static readonly HashSet<string> AudioExtensions =
        new([".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".wma", ".ape"], StringComparer.OrdinalIgnoreCase);

    /// <summary>只看文件名（"艺术家 - 标题"），不读标签：这里只验证界面。</summary>
    public static List<Track> FromLibrary(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var sep = name.IndexOf(" - ", StringComparison.Ordinal);
                var dir = Path.GetDirectoryName(f) ?? "";
                return new Track
                {
                    Title = sep < 0 ? name : name[(sep + 3)..],
                    Artist = sep < 0 ? "" : name[..sep],
                    Album = Path.GetFileName(dir),
                    Duration = "",
                    Folder = dir,
                };
            })
            .ToList();
}

using System.Text.Json.Serialization;

namespace JeekMusic.Settings;

public enum ThemeMode { System, Light, Dark }

/// <summary>在线歌词保存位置（同移动端 LY-11）。</summary>
public enum LyricsSaveLocation { Sidecar, HiddenFolder, Embed, AppCache }

/// <summary>在线封面保存位置（同移动端 C-4）。</summary>
public enum CoverSaveMode { HiddenFolder, Embed, Sidecar }

/// <summary>在线来源；枚举的顺序就是自动获取时尝试的顺序（同移动端 OL-1）。</summary>
public enum OnlineSource { Netease, QQ, ITunes, MusicBrainz, LrcLib }

/// <summary>在线歌词、封面的获取方式。桌面端没有"仅 Wi-Fi"，改为"仅不计流量的网络"（Windows 的按流量计费连接）。</summary>
public enum FetchMode { Manual, Auto, Unmetered }

/// <summary>智能标签的分析时机（需求 M-5）：接通电源时 / 随时 / 手动。</summary>
public enum AnalysisMode { PluggedIn, Always, Manual }

/// <summary>歌曲排序（同移动端 U-8；桌面端多了专辑、时长、格式，需求 G-2）。</summary>
public enum SongSort { FileName, Modified, Title, Artist, Album, Duration, Format }

/// <summary>关闭主窗口时（需求 PC-2）。</summary>
public enum CloseAction { Tray, Exit }

public sealed class SleepPrefs
{
    public bool FinishSong { get; set; }
    public int CustomMinutes { get; set; } = 20;
    public int SongCount { get; set; } = 3;
    public int LastMinutes { get; set; } = 30;
    /// <summary>停止后让电脑睡眠（需求 O-8）。</summary>
    public bool SleepComputer { get; set; }
}

/// <summary>
/// 所有设置（需求 §3.11，设计 §14），保存在 settings.json。
/// 和规则相关的枚举（文件名格式、标签、灵敏度）用字符串存，取值和 Core 里的枚举名一致。
/// </summary>
public sealed class AppSettings
{
    // ── 曲库规则：参与多设备同步（需求 SY-2），改动时更新 RulesUpdatedAt ──
    public List<string> ArtistSeparators { get; set; } = [";", "/", "、", "feat.", "ft.", "featuring"];
    public List<string> ArtistExceptions { get; set; } = ["AC/DC", "Earth, Wind & Fire", "Simon & Garfunkel"];
    public bool InferFromFileName { get; set; } = true;
    /// <summary>ARTIST_TITLE 或 TITLE_ARTIST。</summary>
    public string FileNameFormat { get; set; } = "ARTIST_TITLE";
    /// <summary>强制指定的旧标签编码（GBK / Big5 / Shift_JIS）；null 表示自动。</summary>
    public string? LegacyCharset { get; set; }
    public long RulesUpdatedAt { get; set; }

    // ── 界面 ──
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>界面语言：默认简体中文；auto（跟随系统）/ zh-Hans / en。</summary>
    public string Language { get; set; } = "zh-Hans";
    // Missing stays missing on save, so contextual defaults survive an unrelated settings change.
    [JsonPropertyName("songSort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SongSort? SavedSongSort { get; set; }
    [JsonIgnore]
    public SongSort SongSort { get => SavedSongSort ?? SongSort.Modified; set => SavedSongSort = value; }
    [JsonIgnore]
    public bool HasSongSortPreference => SavedSongSort.HasValue;
    public SongSort SortFor(bool orderedView) => orderedView && !HasSongSortPreference ? SongSort.FileName : SongSort;
    public bool SortDescending { get; set; }
    /// <summary>智能标签筛选（需求 M-6）：选中的标签名，如 "SPORT"、"INSTRUMENTAL"；每个维度最多一个。</summary>
    public List<string> TagFilter { get; set; } = [];
    public bool IncludeSubfolders { get; set; }

    // ── 播放 ──
    /// <summary>指定的输出设备（mpv 的设备名）；null 表示跟随 Windows 的默认设备（需求 O-2）。</summary>
    public string? OutputDevice { get; set; }
    public string? OutputDeviceDescription { get; set; }
    public bool Exclusive { get; set; }
    // Legacy volume/sharedVolume/exclusiveVolume/sharedDeviceVolumes are deliberately
    // absent: JSON migration ignores them and the next save removes historical snapshots.
    public bool Muted { get; set; }
    public bool TaskbarProgress { get; set; } = true;
    public SleepPrefs Sleep { get; set; } = new();

    // ── 窗口 ──
    public CloseAction CloseAction { get; set; } = CloseAction.Tray;
    public bool CloseToTrayHintShown { get; set; }
    public bool LaunchAtStartup { get; set; }
    public bool MiniPlayerTopmost { get; set; } = true;

    // ── 快捷键（需求 PC-3）：动作名 → 快捷键文本（如 "Ctrl+Alt+Space"），空串表示关闭 ──
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public Dictionary<string, string> GlobalHotkeys { get; set; } = new()
    {
        ["PlayPause"] = "Ctrl+Alt+Space",
        ["Previous"] = "Ctrl+Alt+Left",
        ["Next"] = "Ctrl+Alt+Right",
        ["DesktopLyrics"] = "Ctrl+Alt+L",
    };

    // ── 桌面歌词（需求 PC-5）──
    public bool DesktopLyricsVisible { get; set; }
    public bool DesktopLyricsLocked { get; set; }
    public double DesktopLyricsFontSize { get; set; } = 30;
    public string DesktopLyricsColor { get; set; } = "#FFFFD54F";

    // ── 智能标签 ──
    public AnalysisMode AnalysisMode { get; set; } = AnalysisMode.PluggedIn;
    /// <summary>运动的识别灵敏度：LOOSE / NORMAL / STRICT（需求 M-8）。</summary>
    public string Sensitivity { get; set; } = "NORMAL";
    public int TagRulesVersion { get; set; }

    // ── 在线歌词、封面（同移动端 LY-8、C-3、OL-1）──
    public bool OnlineLyrics { get; set; }
    public FetchMode LyricsFetchMode { get; set; } = FetchMode.Manual;
    public LyricsSaveLocation LyricsSaveLocation { get; set; } = LyricsSaveLocation.Sidecar;
    public bool OnlineCovers { get; set; }
    public FetchMode CoverFetchMode { get; set; } = FetchMode.Manual;
    public CoverSaveMode CoverSaveMode { get; set; } = CoverSaveMode.HiddenFolder;
    public List<OnlineSource> LyricsSources { get; set; } = [OnlineSource.Netease, OnlineSource.QQ, OnlineSource.LrcLib];
    public List<OnlineSource> CoverSources { get; set; } = [OnlineSource.Netease, OnlineSource.QQ, OnlineSource.ITunes, OnlineSource.MusicBrainz];
    public bool CoversEmbedded { get; set; }
    public bool LyricsEmbedded { get; set; }

    // ── 更新（需求 UP-2）──
    public bool AutoUpdateCheck { get; set; } = true;
    public long LastUpdateCheck { get; set; }
    public int SkippedUpdateVersion { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;

using System.Text.Json;
using System.Text.Json.Serialization;
using JeekMusic.Infrastructure;

namespace JeekMusic.UI;

/// <summary>
/// 启动快照 snapshot.json（设计 §4.1、§12）：窗口位置和大小、导航栏宽度、表格列、上次看的视图、歌词面板。
/// 在窗口显示之前读出来，避免窗口先在默认位置出现再跳过去。
/// </summary>
public sealed class UiState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
    public double NavWidth { get; set; } = 230;

    /// <summary>上次的视图：folder / artist / pinned / playlist / album，加上参数。</summary>
    public string? ViewKind { get; set; }
    public long ViewId { get; set; }
    public string? ViewKey { get; set; }

    public List<ColumnState> Columns { get; set; } = [];
    public bool LyricsPanel { get; set; }
    public double LyricsWidth { get; set; } = 360;
    public bool LyricsFullPage { get; set; }

    public double? MiniX { get; set; }
    public double? MiniY { get; set; }
    public double? LyricsX { get; set; }
    public double? LyricsY { get; set; }
    public double? LyricsBarWidth { get; set; }

    public List<string> ExpandedNav { get; set; } = [];
    /// <summary>只有主动收起的文件夹分组/目录；缺项默认展开。旧 ExpandedNav 不表示收起意图。</summary>
    public List<string> CollapsedFolders { get; set; } = [];

    public static UiState Load()
    {
        try
        {
            if (File.Exists(AppPaths.Snapshot))
                return JsonSerializer.Deserialize(File.ReadAllBytes(AppPaths.Snapshot), UiStateJson.Default.UiState) ?? new UiState();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Warn("ui", "snapshot.json 读取失败", e);
        }
        return new UiState();
    }

    public void Save()
    {
        try
        {
            var tmp = AppPaths.Snapshot + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(this, UiStateJson.Default.UiState));
            File.Move(tmp, AppPaths.Snapshot, overwrite: true);
        }
        catch (IOException e)
        {
            Log.Warn("ui", "snapshot.json 写入失败", e);
        }
    }
}

public sealed class ColumnState
{
    public string Tag { get; set; } = "";
    public double Width { get; set; }
    public int Order { get; set; }
    public bool Visible { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiState))]
internal partial class UiStateJson : JsonSerializerContext;

using System.Text.Json;
using System.Text.Json.Serialization;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;

namespace JeekMusic.Analysis;

/// <summary>
/// track_features.data 里存的分析结果（JSON）：手工特征和 YAMNet 得分，字段同移动端的 TrackFeaturesEntity。
/// 也是同步文件里分析结果的格式（需求 SY-8，设计 §11.4）。
/// </summary>
public sealed record FeaturesData(
    double? Bpm,
    double BpmConfidence,
    double Loudness,
    double LoudnessRange,
    double OnsetRate,
    double CentroidHz,
    double LowRatio,
    double? VocalScore,
    double? ClassicalScore,
    double? CalmLevel,
    double? EnergyLevel,
    double? OrchestraLevel)
{
    public TrackFeatures ToFeatures(int analyzerVersion) =>
        new(analyzerVersion, Bpm, BpmConfidence, Loudness, LoudnessRange, OnsetRate, CentroidHz, LowRatio,
            VocalScore, ClassicalScore, CalmLevel, EnergyLevel, OrchestraLevel);

    public static FeaturesData Of(AudioFeatures? f, ModelScores? m) => new(
        f?.Bpm, f?.BpmConfidence ?? 0, f?.Loudness ?? -70, f?.LoudnessRange ?? 0, f?.OnsetRate ?? 0, f?.CentroidHz ?? 0, f?.LowRatio ?? 0,
        m?.Vocal, m?.Classical, m?.Calm, m?.Energy, m?.Orchestra);

    public string ToJson() => JsonSerializer.Serialize(this, AnalysisJson.Default.FeaturesData);

    public static FeaturesData? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize(json, AnalysisJson.Default.FeaturesData); }
        catch (JsonException) { return null; }
    }

    /// <summary>数据库行 → 分类用的特征；失败的、解析不了的返回 null。</summary>
    public static TrackFeatures? FromRow(FeaturesRow row) => row.Failed ? null : Parse(row.Data)?.ToFeatures(row.AnalyzerVersion);
}

/// <summary>导出给同步用的一首歌的分析结果（需求 SY-8）：带文件大小和时长作为校验。</summary>
public sealed record SharedFeatures(string TrackKey, int AnalyzerVersion, bool Failed, string? Data, long Size, long DurationMs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FeaturesData))]
internal partial class AnalysisJson : JsonSerializerContext;

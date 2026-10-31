namespace JeekMusic.Core.Analysis;

/// <summary>智能标签的四个维度（需求 M-1，移动端 TagClassifier.kt）。存储名（数据库、同步文件）是 MOOD、VOICE、GENRE、CALMNESS。</summary>
public enum TagDimension
{
    MOOD,
    VOICE,
    GENRE,
    CALMNESS,
}

/// <summary>智能标签。存储名同移动端：SPORT、NON_SPORT、VOCAL、INSTRUMENTAL、CLASSICAL、NON_CLASSICAL、CALM、NON_CALM。</summary>
public enum Tag
{
    SPORT,
    NON_SPORT,
    VOCAL,
    INSTRUMENTAL,
    CLASSICAL,
    NON_CLASSICAL,
    CALM,
    NON_CALM,
}

public static class Tags
{
    /// <summary>旧版本曲风分"古典 / 流行"、氛围分"运动 / 安静"，保存的筛选和手动修正里可能还是 POP、QUIET。</summary>
    private const string LegacyPop = "POP";

    private const string LegacyQuiet = "QUIET";

    public static readonly IReadOnlyList<Tag> All = Enum.GetValues<Tag>();

    public static TagDimension Dimension(this Tag tag) => tag switch
    {
        Tag.SPORT or Tag.NON_SPORT => TagDimension.MOOD,
        Tag.VOCAL or Tag.INSTRUMENTAL => TagDimension.VOICE,
        Tag.CALM or Tag.NON_CALM => TagDimension.CALMNESS,
        _ => TagDimension.GENRE,
    };

    /// <summary>存储名（数据库、同步文件里的值）就是枚举名，和移动端一致。</summary>
    public static string StorageName(this Tag tag) => tag.ToString();

    public static string StorageName(this TagDimension dimension) => dimension.ToString();

    public static TagDimension? ParseDimension(string? name) => name switch
    {
        "MOOD" => TagDimension.MOOD,
        "VOICE" => TagDimension.VOICE,
        "GENRE" => TagDimension.GENRE,
        "CALMNESS" => TagDimension.CALMNESS,
        _ => null,
    };

    /// <summary>移动端的 Tag.of(dimension, value)：存储名 → 标签，兼容旧值；不属于这个维度时返回 null。</summary>
    public static Tag? Of(TagDimension dimension, string? value)
    {
        if (dimension == TagDimension.GENRE && value == LegacyPop) return Tag.NON_CLASSICAL;
        if (dimension == TagDimension.MOOD && value == LegacyQuiet) return Tag.NON_SPORT;
        foreach (var tag in All)
        {
            if (tag.Dimension() == dimension && tag.StorageName() == value) return tag;
        }
        return null;
    }

    /// <summary>这个维度的所有标签。</summary>
    public static IReadOnlyList<Tag> Of(TagDimension dimension) => All.Where(t => t.Dimension() == dimension).ToList();
}

/// <summary>运动的识别灵敏度（需求 M-8）。阈值越低，归入"运动"的歌越多。</summary>
public enum Sensitivity
{
    LOOSE,
    NORMAL,
    STRICT,
}

public static class SensitivityExtensions
{
    /// <summary>模型得分的运动阈值。</summary>
    public static double Sport(this Sensitivity s) => s switch { Sensitivity.LOOSE => 2.75, Sensitivity.STRICT => 3.75, _ => 3.25 };

    /// <summary>旧的规则分类器（还没用模型重新分析的歌）使用的阈值。</summary>
    public static double Legacy(this Sensitivity s) => s switch { Sensitivity.LOOSE => 0.50, Sensitivity.STRICT => 0.70, _ => 0.60 };
}

/// <summary>YAMNet 模型给出的几组得分（移动端设计 §6.3）。</summary>
/// <param name="Vocal">ln(人声类 / 乐器类)，越大越像有人声。</param>
/// <param name="Classical">ln(古典曲风类 / 其他曲风类)，越大越像古典（v3 的分析结果是 ln(古典类和乐器类 / 流行类)）。</param>
/// <param name="Calm">舒缓类（Tender、Sad、Lullaby……）的得分之和。</param>
/// <param name="Energy">动感类（Exciting、Dance、Drum……）的得分之和。</param>
/// <param name="Orchestra">Orchestra 的得分：现在没有用到，留着供校准分析。</param>
public sealed record ModelScores(double Vocal, double Classical, double Calm, double Energy, double Orchestra);

/// <summary>
/// 一首歌的分析结果，对应移动端的 TrackFeaturesEntity（只保留分类用到的字段）。
/// 模型得分为 null 表示还没用模型分析过（旧版本的结果）。
/// </summary>
public sealed record TrackFeatures(
    int AnalyzerVersion,
    double? Bpm,
    double BpmConfidence,
    double Loudness,
    double LoudnessRange,
    double OnsetRate,
    double CentroidHz,
    double LowRatio,
    double? VocalScore = null,
    double? ClassicalScore = null,
    double? CalmLevel = null,
    double? EnergyLevel = null,
    double? OrchestraLevel = null,
    string? AnalysisEvidence = null)
{
    public ModelScores? ModelScores() =>
        VocalScore is { } v && ClassicalScore is { } c && CalmLevel is { } calm && EnergyLevel is { } e && OrchestraLevel is { } o
            ? new ModelScores(v, c, calm, e, o)
            : null;

    public AudioFeatures ToFeatures() => new(Bpm, BpmConfidence, Loudness, LoudnessRange, OnsetRate, CentroidHz, LowRatio);
}

/// <summary>
/// 根据模型得分分类（移动端 TagClassifier.kt，移动端设计 §6.4）。阈值用 109 首真实歌曲的人工标注校准
/// （mobile/tools/calibration/）。规则只依赖单首歌自己的得分，新增歌曲不会改变已有歌曲的标签。
/// </summary>
public static class TagClassifier
{
    /// <summary>人声和乐器之比的阈值：校准集里纯音乐最高 −2.83、人声最低 −1.72，取中间。</summary>
    public const double VocalThreshold = -2.3;

    /// <summary>古典曲风类和其他曲风类之比的阈值：在两个标注集上扫过 −1.5～0.3，−0.4 的准确率都最高。</summary>
    public const double ClassicalThreshold = -0.4;

    /// <summary>从这个分析版本起 classical_score 只用曲风类；更早的结果在重新分析前沿用旧阈值。</summary>
    private const int GenreOnlyVersion = 4;

    /// <param name="f">分析结果</param>
    /// <param name="sensitivity">运动的识别灵敏度</param>
    /// <param name="classicalTitle">歌名或专辑名里有古典作品的标志（<see cref="ClassicalTitles"/>），模型判为非古典时用它补救（需求 M-10）</param>
    /// <returns>每个维度的标签；没有结果的维度不在字典里或值为 null</returns>
    public static IReadOnlyDictionary<TagDimension, Tag?> Classify(TrackFeatures f, Sensitivity sensitivity, bool classicalTitle = false)
    {
        if (f.ModelScores() is not { } m)
        {
            var legacy = new Dictionary<TagDimension, Tag?> { [TagDimension.MOOD] = LegacyMoodClassifier.Classify(f.ToFeatures(), sensitivity) };
            // 还没用模型分析的歌：只凭曲名判断得出古典，判断不出非古典
            if (classicalTitle) legacy[TagDimension.GENRE] = Tag.CLASSICAL;
            return legacy;
        }
        var evidence = f.AnalyzerVersion == 5 ? global::JeekMusic.Core.Analysis.AnalysisEvidence.Parse(f.AnalysisEvidence) : null;
        var a = evidence?.Complete == true ? evidence.Aggregate() : null;
        var genre = a is not null ? (a.Classical >= ClassicalThreshold ? Tag.CLASSICAL : Tag.NON_CLASSICAL) :
            f.AnalyzerVersion >= GenreOnlyVersion ? Genre(m) : LegacyGenre(m);
        return new Dictionary<TagDimension, Tag?>
        {
            [TagDimension.MOOD] = a is null ? Mood(m, sensitivity) : a.Sport >= sensitivity.Sport() ? Tag.SPORT : Tag.NON_SPORT,
            [TagDimension.VOICE] = a is null ? Voice(m) : a.Vocal >= VocalThreshold ? Tag.VOCAL : Tag.INSTRUMENTAL,
            [TagDimension.GENRE] = classicalTitle ? Tag.CLASSICAL : genre,
            [TagDimension.CALMNESS] = CalmnessEvidence.From(evidence)?.Tag,
        };
    }

    /// <summary>运动 / 非运动（需求 M-1）：总有一个，慢歌、中速的歌、管弦乐等都算非运动。</summary>
    public static Tag Mood(ModelScores m, Sensitivity sensitivity) => SportScore(m) >= sensitivity.Sport() ? Tag.SPORT : Tag.NON_SPORT;

    public static Tag Voice(ModelScores m) => m.Vocal >= VocalThreshold ? Tag.VOCAL : Tag.INSTRUMENTAL;

    /// <summary>古典 / 非古典（需求 M-1）：总有一个，爵士、电影配乐等都算非古典。</summary>
    public static Tag Genre(ModelScores m) => m.Classical >= ClassicalThreshold ? Tag.CLASSICAL : Tag.NON_CLASSICAL;

    /// <summary>v3 的 classical_score 把钢琴、弦乐等乐器也算作古典，阈值不同；只在重新分析完成前使用。</summary>
    private static Tag LegacyGenre(ModelScores m) => m.Classical >= 0.5 ? Tag.CLASSICAL : Tag.NON_CLASSICAL;

    /// <summary>ln(动感 / 舒缓)。</summary>
    public static double SportScore(ModelScores m) => Math.Log(m.Energy + 0.003) - Math.Log(m.Calm + 0.003);
}

/// <summary>
/// 旧的规则分类器（v2，只看响度、低频、节奏等手工特征）：只用于还没用模型重新分析过的歌，
/// 保证升级后到重新分析完成之前"运动"不会突然变空（移动端 TagClassifier.kt）。
/// </summary>
public static class LegacyMoodClassifier
{
    public static Tag Classify(AudioFeatures features, Sensitivity sensitivity) =>
        SportScore(features) is { } sport && sport >= sensitivity.Legacy() ? Tag.SPORT : Tag.NON_SPORT;

    private static double? SportScore(AudioFeatures f)
    {
        if (f.Bpm is not { } bpm) return null;
        if (bpm is < 105.0 or > 185.0 || f.BpmConfidence < 1.3 || f.Loudness < -12) return null;
        return 0.35 * Norm(f.Loudness, -12.0, -7.0) +
               0.30 * Norm(f.LowRatio, 0.2, 0.6) +
               0.20 * Math.Clamp(1 - Math.Abs(Math.Log2(bpm / 130.0)), 0.0, 1.0) +
               0.15 * Norm(f.OnsetRate, 2.5, 4.5);
    }

    private static double Norm(double value, double low, double high) => Math.Clamp((value - low) / (high - low), 0.0, 1.0);
}

/// <summary>分析算法和分类规则的版本（移动端 TagRepository.kt）。</summary>
public static class AnalysisVersions
{
    /// <summary>分析算法的版本：升级后所有歌曲重新分析（需求 M-5）。</summary>
    public const int AnalyzerVersion = 5;

    /// <summary>分类规则的版本：规则变了但不需要重新分析时提高，启动时按保存的版本判断是否全部重算一次。</summary>
    public const int RulesVersion = 7;

    public const string SourceAuto = "AUTO";
    public const string SourceManual = "MANUAL";
}

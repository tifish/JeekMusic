using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace JeekMusic.UI;

/// <summary>Windows 文本优先微软雅黑；不附带字体，缺字体或字形时继续使用系统回退。</summary>
internal static class Typography
{
    internal const string DefaultFamilyChain = "Microsoft YaHei, Segoe UI";
    internal const string Sample = "中文歌曲歌词";

    internal static FontManagerOptions FontOptions() => new() { DefaultFamilyName = DefaultFamilyChain };

    internal static JsonObject Diagnostics()
    {
        var result = new JsonObject { ["defaultFamilyChain"] = DefaultFamilyChain };
        // 无 UI 平台的内存业务调用也可读取诊断，不能为查询创建应用或窗口。
        if (Application.Current is null)
        {
            result["initialized"] = false;
            return result;
        }
        var manager = FontManager.Current;
        result["initialized"] = true;
        result["effectiveDefaultFamily"] = string.Join(", ", manager.DefaultFontFamily.FamilyNames);
        result["microsoftYaHeiAvailable"] = manager.SystemFonts.Any(f => f.Name.Equals("Microsoft YaHei", StringComparison.OrdinalIgnoreCase));
        result["sample"] = Sample;
        result["normalFamilies"] = Resolve(FontWeight.Normal);
        result["boldFamilies"] = Resolve(FontWeight.Bold);
        return result;
    }

    // 从真正排版后的 GlyphRun 读取家族，不能把请求的 Typeface 名称当成解析结果。
    static JsonArray Resolve(FontWeight weight)
    {
        using var layout = new TextLayout(Sample, new Typeface(FontFamily.Default, weight: weight), 14, Brushes.Black);
        return new JsonArray(layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>()
            .Select(r => r.GlyphRun.GlyphTypeface.FamilyName).Distinct()
            .Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }
}

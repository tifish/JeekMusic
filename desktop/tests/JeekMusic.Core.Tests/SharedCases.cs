using System.Text.Json.Nodes;

namespace JeekMusic.Core.Tests;

/// <summary>读取两端共用的测试用例（shared/testcases/，csproj 把它们复制到输出目录的 testcases/ 下）。</summary>
internal static class SharedCases
{
    public static JsonObject Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testcases", name);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    public static IEnumerable<JsonObject> Cases(this JsonObject file, string key = "cases") =>
        file[key]!.AsArray().Select(n => n!.AsObject());

    public static string Description(this JsonObject c) => c["description"]?.GetValue<string>() ?? "";

    public static string? Str(this JsonObject c, string key) => c[key] is JsonValue v ? v.GetValue<string>() : null;

    public static List<string> Strings(this JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    /// <summary>"lines" 按 \n 连接。</summary>
    public static string Text(this JsonObject c) => string.Join("\n", c["lines"].Strings());

    public static bool Flag(this JsonObject c, string key) => c[key] is JsonValue v && v.GetValue<bool>();
}

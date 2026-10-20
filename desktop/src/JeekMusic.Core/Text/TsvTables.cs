using System.Reflection;
using System.Text;

namespace JeekMusic.Core.Text;

/// <summary>
/// 读取 shared/pinyin/ 下由 ICU4J 生成的表（嵌入资源，LogicalName 是文件名）。
/// 每行用制表符分列，"#" 开头的是注释；值里的 \\、\t、\n 是转义（见 shared/pinyin/Generate.java）。
/// </summary>
internal static class TsvTables
{
    public static IEnumerable<string[]> Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少嵌入资源 {name}（shared/pinyin/，运行 shared/pinyin/generate.sh 生成）");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var rows = new List<string[]>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var columns = line.Split('\t');
            for (var i = 0; i < columns.Length; i++) columns[i] = Unescape(columns[i]);
            rows.Add(columns);
        }
        return rows;
    }

    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n switch { 't' => '\t', 'n' => '\n', _ => n });
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>单个码位（第一列只有一个字符）→ 字符串。</summary>
    public static Dictionary<int, string> CharMap(string name)
    {
        var map = new Dictionary<int, string>();
        foreach (var row in Read(name))
        {
            var key = row[0];
            var rune = Rune.GetRuneAt(key, 0);
            if (rune.Utf16SequenceLength != key.Length) throw new InvalidDataException($"{name}：第一列应该是单个字符：{key}");
            map[rune.Value] = row[1];
        }
        return map;
    }
}

/// <summary>码位集合：有序的区间数组，二分查找。来自 shared/pinyin/sets.tsv。</summary>
internal sealed class CodePointSet
{
    private readonly int[] _starts;
    private readonly int[] _ends;

    private CodePointSet(int[] starts, int[] ends)
    {
        _starts = starts;
        _ends = ends;
    }

    public bool Contains(int codePoint)
    {
        int low = 0, high = _starts.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            if (codePoint < _starts[mid]) high = mid - 1;
            else if (codePoint > _ends[mid]) low = mid + 1;
            else return true;
        }
        return false;
    }

    public static Dictionary<string, CodePointSet> Load(string name)
    {
        var result = new Dictionary<string, CodePointSet>();
        foreach (var row in TsvTables.Read(name))
        {
            var ranges = row[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var starts = new int[ranges.Length];
            var ends = new int[ranges.Length];
            for (var i = 0; i < ranges.Length; i++)
            {
                var dash = ranges[i].IndexOf('-');
                starts[i] = Convert.ToInt32(ranges[i][..dash], 16);
                ends[i] = Convert.ToInt32(ranges[i][(dash + 1)..], 16);
            }
            result[row[0]] = new CodePointSet(starts, ends);
        }
        return result;
    }
}

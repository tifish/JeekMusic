using System.Text;
using System.Text.RegularExpressions;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

internal static partial class LanFileContent
{
    static readonly HashSet<string> Audio=[".mp3",".flac",".m4a",".mp4",".aac",".ogg",".oga",".opus",".wav",".mka",".amr"];
    static readonly HashSet<string> Sidecars=[".cue",".lrc",".jpg",".jpeg",".png",".webp"];
    public static bool Supported(string path)=>Audio.Contains(Path.GetExtension(path).ToLowerInvariant()) || Sidecars.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static async Task<IReadOnlyList<string>> Validate(string source,string relative,CancellationToken ct)
    {
        var ext=Path.GetExtension(relative).ToLowerInvariant();if(!Supported(relative))throw new IOException("Unsupported upload extension");
        await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous);
        var head=new byte[(int)Math.Min(input.Length,512)];await input.ReadExactlyAsync(head,ct);
        bool Prefix(params byte[] bytes)=>head.AsSpan().StartsWith(bytes);
        bool Text(string value,int offset=0)=>head.Length>=offset+value.Length && head.AsSpan(offset,value.Length).SequenceEqual(Encoding.ASCII.GetBytes(value));
        if(Text("MZ") || Prefix(0x7f,69,76,70) || Text("#!") && ext!=".amr")throw new IOException("Executable content rejected");
        bool valid=ext switch {
            ".flac"=>Text("fLaC"),".mp3"=>Text("ID3") || head.Length>=2 && head[0]==255 && (head[1]&0xe0)==0xe0,
            ".aac"=>head.Length>=2 && head[0]==255 && (head[1]&0xf6)==0xf0,
            ".m4a" or ".mp4"=>Text("ftyp",4),".ogg" or ".oga" or ".opus"=>Text("OggS"),
            ".wav"=>Text("RIFF") && Text("WAVE",8),".mka"=>Prefix(0x1a,0x45,0xdf,0xa3),".amr"=>Text("#!AMR"),
            ".jpg" or ".jpeg"=>Prefix(255,216,255),".png"=>Prefix(137,80,78,71,13,10,26,10),".webp"=>Text("RIFF") && Text("WEBP",8),
            ".cue" or ".lrc"=>true,_=>false
        };
        if(!valid)throw new IOException("Upload container signature rejected");
        if(ext is not (".cue" or ".lrc"))return [];
        if(input.Length>2_000_000)throw new IOException("Text sidecar budget exceeded");
        input.Position=0;using var reader=new StreamReader(input,new UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:true,leaveOpen:true);var text=await reader.ReadToEndAsync(ct);
        if(text.Contains('\0') || text.Contains('\uFFFD'))throw new IOException("Invalid text sidecar");
        if(ext!=".cue")return [];
        var references=new List<string>();
        var lines=Regex.Matches(text,@"^\s*FILE\b",RegexOptions.IgnoreCase|RegexOptions.Multiline);var declarations=CueFile().Matches(text);
        if(lines.Count!=declarations.Count || declarations.Count is <1 or >1000)throw new IOException("Invalid CUE references");
        foreach(Match m in declarations)
        {
            var path=(m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value).Replace('\\','/');
            if(path.StartsWith('/') || path.Contains(':'))throw new IOException("Rooted CUE reference rejected");
            var directory=relative.Contains('/')?relative[..relative.LastIndexOf('/')]:"";
            var parts=new List<string>();foreach(var part in (directory+"/"+path).Split('/',StringSplitOptions.RemoveEmptyEntries)) {
                if(part==".")continue;if(part=="..") { if(parts.Count==0)throw new IOException("Escaping CUE reference rejected");parts.RemoveAt(parts.Count-1); }else parts.Add(part);
            }
            var normalized=LanFiles.Relative(string.Join('/',parts));if(!Audio.Contains(Path.GetExtension(normalized).ToLowerInvariant()))throw new IOException("Unsupported CUE reference");
            references.Add(normalized);
        }
        return references.Distinct(StringComparer.Ordinal).ToArray();
    }
    [GeneratedRegex("^\\s*FILE\\s+(?:\"([^\"]+)\"|(\\S+))\\s+\\S+\\s*$",RegexOptions.IgnoreCase|RegexOptions.Multiline|RegexOptions.CultureInvariant)]
    private static partial Regex CueFile();
}

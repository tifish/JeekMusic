using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace JeekMusic.Core.Sync;

/// <summary>Bounded parsed-sidecar input cache. Unchanged CUEs never reopen music content,
/// including after restart. Like all change detection, validity is exact size+mtime.</summary>
public static class LanCueMetadataCache
{
    public static async Task<string> Text(string directory,string root,string relative,CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)+"\n"+relative)));
        var cache=Path.Combine(directory,key+".json");
        var before=FileMetadata.Observe(root,relative);var version=FileMetadata.Token(before);
        if(before["size"]!.GetValue<long>()>2_000_000)throw new IOException("CUE too large");
        if(File.Exists(cache) && new FileInfo(cache).Length<=12_100_000) {
            try { var row=JsonNode.Parse(await File.ReadAllTextAsync(cache,ct));if(row is JsonObject record && record["version"]?.GetValue<string>()==version && record["text"] is JsonValue value)return value.GetValue<string>(); }
            catch(Exception e) when(e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { /* Rebuild only this changed/corrupt private metadata record. */ }
        }
        var records=Directory.EnumerateFiles(directory,"*.json").Select(p=>new FileInfo(p)).ToArray();
        if(records.Length>=4096 && !File.Exists(cache) || records.Sum(f=>f.Length)>=64L*1024*1024)throw new IOException("CUE metadata cache budget exceeded");
        using var input=LanFiles.OpenRead(root,relative);using var reader=new StreamReader(input,Encoding.UTF8,true);
        var text=await reader.ReadToEndAsync(ct);Interlocked.Add(ref FileMetadata.SidecarParseBytes,input.Position);
        if(FileMetadata.Token(FileMetadata.Observe(root,relative))!=version)throw new IOException("CUE changed during parsing");
        var json=new JsonObject { ["version"]=version,["text"]=text }.ToJsonString();
        var temporary=cache+".new";await File.WriteAllTextAsync(temporary,json,ct);File.Move(temporary,cache,true);
        return text;
    }
}

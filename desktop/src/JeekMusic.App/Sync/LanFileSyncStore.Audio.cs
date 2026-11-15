using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Sync;

internal sealed partial class LanFileSyncStore
{
    long audioHashBytes,audioReusedBytes,audioReuseCopies;
    public JsonObject AudioDiagnostics()=>new() { ["protocol"]=1,["policy"]="changed_files_only_exact_media_sha256",["formats"]=new JsonArray("mp3","flac","wav","m4a","mp4"),["unsupported"]="full_file",["hashBytes"]=Interlocked.Read(ref audioHashBytes),["reusedBytes"]=Interlocked.Read(ref audioReusedBytes),["copies"]=Interlocked.Read(ref audioReuseCopies),["scanReadsAudio"]=false };
    public async Task<JsonObject> AudioLayout(string scope,string root,string path,JsonObject args,CancellationToken ct)
    {
        PathAllowed(scope,path);var expected=V(args["expectedVersion"]);
        if(expected==null || FileMetadata.Token(FileMetadata.Observe(root,path))!=expected)throw new LanFileDeferred("source_changed");
        await using var input=LanFiles.OpenRead(root,path);
        if(input.Length>limits.FileBytes)throw new LanFileDeferred("file_byte_budget");
        var before=FileMetadata.Stamp(input.SafeFileHandle);var layout=AudioEnvelope.Parse(input,path);
        if(layout==null || layout.Length<262144)return new() { ["supported"]=false };
        var result=new JsonObject { ["supported"]=true,["format"]=layout.Format,["start"]=layout.Start,["length"]=layout.Length,["sourceVersion"]=expected };
        if(N(args["mediaBytes"])==layout.Length)result["digest"]=await AudioEnvelope.Digest(input,layout.Start,layout.Length,ct,n=>Interlocked.Add(ref audioHashBytes,n));
        if(before!=FileMetadata.Stamp(input.SafeFileHandle) || FileMetadata.Token(FileMetadata.Observe(root,path))!=expected)throw new LanFileDeferred("source_changed");
        return result;
    }
    bool RecoverAudioReuse(string scope,JsonObject task)
    {
        if(S(task["state"])!="uploading" || task["reuseInProgress"]?.GetValue<bool>()!=true)return false;
        // An interrupted local copy was never acknowledged. Discard only its identity-owned
        // private staging object; committed user files and recovery backups are untouched.
        CleanupPartial(scope,task);task["reuseInProgress"]=false;SaveTask(scope,task,"aborted");return true;
    }
    public async Task<JsonObject> ReuseAudio(string scope,string root,JsonObject args,CancellationToken ct)
    {
        var op=Operation(S(args["operation"]));var task=Task(scope,op)??throw new UnauthorizedAccessException();
        RecoverAudioReuse(scope,task);
        if(S(task["state"])!="uploading")throw new LanFileDeferred("upload_restart_required");
        if(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-(task["createdAt"]?.GetValue<long>()??0)>7200)throw new LanFileDeferred("upload_expired");
        var path=S(task["path"]);PathAllowed(scope,path);RequireBaseline(scope,path,S(task["baselineVersion"]));
        var expected=V(task["expectedVersion"]);if(expected==null || FileMetadata.Token(FileMetadata.Observe(root,path))!=expected)throw new LanFileDeferred("source_changed");
        var hash=S(args["digest"]);if(hash.Length!=64 || hash.Any(c=>!char.IsAsciiHexDigitLower(c)))throw new ArgumentException("Invalid media digest");
        var offset=N(args["offset"]);var source=N(args["sourceOffset"]);var length=N(args["length"]);
        await using var input=LanFiles.OpenRead(root,path);if(input.Length>limits.FileBytes)throw new LanFileDeferred("file_byte_budget");var sourceStamp=FileMetadata.Stamp(input.SafeFileHandle);
        var layout=AudioEnvelope.Parse(input,path);
        if(layout==null || source<layout.Start || length<1 || source>layout.End-length || offset<0 || length>N(task["size"])-offset)throw new ArgumentException("Invalid media reuse range");
        await using var output=LanFileMutation.OpenPrivate(Partial(scope,op));var identity=LanFileMutation.Identity(output);var stamp=FileMetadata.Stamp(output.SafeFileHandle);
        if(task["partialIdentity"]!=null && S(task["partialIdentity"])!=identity || task["partialStamp"]!=null && S(task["partialStamp"])!=stamp || task["partialStamp"]==null && output.Length!=0)throw new LanFileConflict("private_staging_changed");
        if(offset!=output.Length)throw new LanFileDeferred("upload_offset_mismatch");
        task["partialIdentity"]=identity;task["partialStamp"]=stamp;task["reuseInProgress"]=true;SaveTask(scope,task,"uploading");
        var copied=false;
        try {
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[131072];var remaining=length;input.Position=source;output.Position=offset;
            while(remaining>0) { ct.ThrowIfCancellationRequested();var n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(remaining,buffer.Length)),ct);if(n==0)throw new EndOfStreamException();digest.AppendData(buffer,0,n);Interlocked.Add(ref audioHashBytes,n);await output.WriteAsync(buffer.AsMemory(0,n),ct);remaining-=n; }
            if(sourceStamp!=FileMetadata.Stamp(input.SafeFileHandle) || FileMetadata.Token(FileMetadata.Observe(root,path))!=expected)throw new LanFileDeferred("source_changed");
            copied=Convert.ToHexStringLower(digest.GetHashAndReset())==hash;
        } finally {
            if(!copied)output.SetLength(offset);
            output.Flush(true);task["partialStamp"]=FileMetadata.Stamp(output.SafeFileHandle);task["reuseInProgress"]=false;SaveTask(scope,task,"uploading");
        }
        if(copied) { Interlocked.Add(ref audioReusedBytes,length);Interlocked.Increment(ref audioReuseCopies); }
        return new() { ["offset"]=output.Length,["reused"]=copied };
    }
}

using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Core.Tests;

public sealed class AudioEnvelopeTests
{
    [Fact] public async Task SharedLayoutsIdentifyTheSameMediaAcrossTagRelocationAndRejectAmbiguity()
    {
        var cases=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"testcases","audio-envelope.json")))!.AsArray();
        foreach(var row in cases.OfType<JsonObject>()) {
            var path=row["path"]!.GetValue<string>();
            using var source=new MemoryStream(Convert.FromBase64String(row["source"]!.GetValue<string>()));
            using var target=new MemoryStream(Convert.FromBase64String(row["target"]!.GetValue<string>()));
            var a=AudioEnvelope.Parse(source,path);var b=AudioEnvelope.Parse(target,path);
            if(row["start"]!.GetValue<int>()<0) { Assert.Null(a);Assert.Null(b);continue; }
            Assert.NotNull(a);Assert.NotNull(b);Assert.Equal(row["start"]!.GetValue<long>(),a.Start);Assert.Equal(row["targetStart"]!.GetValue<long>(),b.Start);Assert.Equal(row["length"]!.GetValue<long>(),a.Length);
            long observed=0;var first=await AudioEnvelope.Digest(source,a.Start,a.Length,default,n=>observed+=n);
            Assert.Equal(a.Length,observed);Assert.Equal(first,await AudioEnvelope.Digest(target,b.Start,b.Length,default));
            target.Position=b.Start;target.WriteByte(0);
            Assert.NotEqual(first,await AudioEnvelope.Digest(target,b.Start,b.Length,default));
        }
    }
}

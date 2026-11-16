using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Core.Tests;

public sealed class GroupTranscriptTests
{
    [Fact] public void SharedBusinessIngressRejectsPermissiveJsonAndKeepsCacheVersionsSeparate()
    {
        var fixture=SharedCases.Load("sync-group-business.json");
        foreach(var row in fixture["invalid"]!.AsArray().OfType<JsonObject>())
        {
            var mutation=new GroupMutation("group-a",1,row["key"]!.GetValue<string>(),new(new(1,0),"pc-a","inc-a",1),row["payload"]!.GetValue<string>());
            Assert.ThrowsAny<Exception>(()=>GroupUserData.Materialize([mutation],2));
        }
        var seeds=GroupUserData.Flatten(fixture["cacheState"]!.AsObject());Assert.Equal(3,seeds.Count);
        var mutations=seeds.Select((pair,index)=>new GroupMutation("group-a",1,pair.Key,new(new(pair.Value.EditedAtMs,0),"pc-a","inc-a",index+1),pair.Value.Payload)).ToArray();
        var actual=GroupUserData.Flatten(GroupUserData.Materialize(mutations,500));
        Assert.Equal(seeds.ToDictionary(p=>p.Key,p=>p.Value.Payload),actual.ToDictionary(p=>p.Key,p=>p.Value.Payload));
        Assert.All(seeds.Keys,key=>Assert.False(GroupUserData.DeletesWhenMissing(key)));
    }
    sealed class MemoryRows : IGroupRows
    {
        readonly Dictionary<(string Kind,string Key),GroupRow> rows=new();
        public string? Read(string kind,string key)=>rows.GetValueOrDefault((kind,key))?.Value;
        public void Put(string kind,string key,string value,long cursor=0)=>rows[(kind,key)]=new(key,value,cursor);
        public void Delete(string kind,string key)=>rows.Remove((kind,key));
        public void DeleteThrough(string kind,long cursor) { foreach(var key in rows.Where(p=>p.Key.Kind==kind && p.Value.Cursor<=cursor).Select(p=>p.Key).ToArray())rows.Remove(key); }
        public void DeletePrefixThrough(string prefix,long cursor) { foreach(var key in rows.Where(p=>p.Key.Kind.StartsWith(prefix,StringComparison.Ordinal) && p.Value.Cursor<=cursor).Select(p=>p.Key).ToArray())rows.Remove(key); }
        public IReadOnlyList<GroupRow> Page(string kind,long after,long through,int limit=200)=>rows.Where(p=>p.Key.Kind==kind && p.Value.Cursor>after && p.Value.Cursor<=through).Select(p=>p.Value).OrderBy(r=>r.Cursor).ThenBy(r=>r.Key,StringComparer.Ordinal).Take(limit).ToArray();
        public IReadOnlyList<GroupRow> Scan(string kind,string afterKey="",int limit=200)=>rows.Where(p=>p.Key.Kind==kind && string.CompareOrdinal(p.Key.Key,afterKey)>0).Select(p=>p.Value).OrderBy(r=>r.Key,StringComparer.Ordinal).Take(limit).ToArray();
        public long Count(string kind)=>rows.Count(p=>p.Key.Kind==kind);
    }
    [Fact] public void SharedMemberTranscriptKeepsProvenanceAcrossSnapshotsAndGenerationReset()
    {
        var fixture=SharedCases.Load("sync-group-transcript.json");
        var rows=new MemoryRows();var journal=new GroupJournal(rows,()=>fixture["now"]!.GetValue<long>());
        journal.JoinClient(fixture["server"]!.AsObject(),fixture["actorId"]!.GetValue<string>(),fixture["incarnation"]!.GetValue<string>(),fixture["peerId"]!.GetValue<string>());
        foreach(var step in fixture["steps"]!.AsArray().OfType<JsonObject>())
        {
            void Act()
            {
                switch(step["action"]!.GetValue<string>())
                {
                    case "capture":journal.Capture(GroupUserData.Flatten(step["state"]!.AsObject()));break;
                    case "project":journal.Projected(GroupUserData.Flatten(step["state"]!.AsObject()),journal.PendingApply());break;
                    case "receipts":journal.AcceptReceipts(step["receipts"]!.AsArray());break;
                    case "snapshot":journal.ReceiveSnapshot(step["page"]!.AsObject());break;
                    case "receive":journal.Receive(step["page"]!.AsObject());break;
                    case "reset":journal.ResetGeneration(step["generation"]!.GetValue<long>());break;
                    default:throw new InvalidOperationException();
                }
            }
            if(step["error"] is { } error)Assert.Equal(error.GetValue<string>(),Assert.Throws<GroupSyncException>(Act).Code);else Act();
            var expected=step["expected"]!;var info=journal.Info();
            Assert.Equal(expected["outbox"]!.GetValue<long>(),info["pendingUploads"]!.GetValue<long>());
            Assert.Equal(expected["pendingApply"]!.GetValue<long>(),info["pendingApply"]!.GetValue<long>());
            Assert.Equal(expected["cursor"]!.GetValue<long>(),info["cursor"]!.GetValue<long>());
            Assert.Equal(expected["generation"]!.GetValue<long>(),info["generation"]!.GetValue<long>());
            if(expected["firstOutbox"] is { } first)Assert.True(JsonNode.DeepEquals(first,GroupSyncContract.WriteMutation(journal.Outbox()[0])),step["description"]!.GetValue<string>());
        }
        foreach(var row in fixture["fingerprints"]!.AsArray().OfType<JsonObject>())
            Assert.Equal(row["expected"]!.GetValue<string>(),GroupJournal.Fingerprint(GroupSyncContract.ReadMutation(row["mutation"]!.AsObject())));
    }

    [Fact] public void CanonicalValuesUseUtf16TupleLengthsAndNeverIncludeLegacyTimestamps()
    {
        var reference="collection:music/曲目🎵.flac";
        var state=JsonNode.Parse("""{"playlists":[{"uid":"list-a","name":"<test>/曲目🎵","items":[],"updated_at":1}],"tag_overrides":{}}""")!.AsObject();
        state["tag_overrides"]![reference]=new JsonObject { ["MOOD"]=new JsonObject { ["value"]="SPORT",["updated_at"]=1L } };
        var seeds=GroupUserData.Flatten(state);
        Assert.Contains(GroupUserData.TupleKey("tag",reference,"MOOD"),seeds.Keys);
        Assert.DoesNotContain("updated_at",seeds["playlist:list-a"].Payload!);
        var mutations=seeds.Select((p,index)=>new GroupMutation("group-a",1,p.Key,new(new(1,0),"pc-a","inc-a",index+1),p.Value.Payload));
        var restored=GroupUserData.Flatten(GroupUserData.Materialize(mutations,200));
        Assert.Equal(seeds.ToDictionary(p=>p.Key,p=>p.Value.Payload),restored.ToDictionary(p=>p.Key,p=>p.Value.Payload));
    }
}

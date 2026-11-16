using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using JeekMusic.Library;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class GroupJournalTests
{
    sealed class Fixture : IDisposable
    {
        readonly string directory=Path.Combine(Path.GetTempPath(),"jeek-group-"+Guid.NewGuid().ToString("N"));
        public LibraryDb Db { get; }
        public GroupSqlRows Rows { get; }
        public GroupJournal Journal { get; }
        public Fixture() { Directory.CreateDirectory(directory);Db=new(Path.Combine(directory,"library.db"));Rows=new(Db);Journal=new(Rows,()=>1000); }
        public void Tx(Action action)=>Db.InTransaction(action);
        public T Tx<T>(Func<T> action) { T result=default!;Db.InTransaction(()=>result=action());return result; }
        public void Dispose() { Db.Dispose();Directory.Delete(directory,true); }
    }
    static GroupMutation Change(string key,string? payload,long sequence=1,long time=20,string actor="phone-a",string incarnation="inc-a",long generation=1)=>
        new("group-a",generation,key,new(new(time,0),actor,incarnation,sequence),payload);
    static JsonObject Read(GroupRow row)=>JsonNode.Parse(row.Value)!.AsObject();
    static void Setup(Fixture server)=>server.Tx(()=> { server.Journal.Create("group-a","pc-a","inc-pc");server.Journal.JoinMember("phone-a","inc-a"); });
    [Fact] public void ExplicitRecoveryRetainsPendingOriginalClockUntilVerifiedReceipt()
    {
        using var server=new Fixture();using var client=new Fixture();Setup(server);
        client.Tx(()=> { client.Journal.JoinClient(server.Journal.Info(),"phone-a","inc-a","peer-a");client.Journal.Capture(new Dictionary<string,GroupSeed> { ["offset:song"]=new("10",17) }); });
        var original=client.Journal.Outbox().Single();
        server.Tx(()=>server.Journal.RemoveMember("phone-a"));
        Assert.Equal("recovery_approval_required",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.RecoverMember("phone-a","inc-new"))).Code);
        server.Tx(()=>server.Journal.AllowRecovery("phone-a"));
        var reply=server.Tx(()=>server.Journal.RecoverMember("phone-a","inc-new"));
        Assert.Equal(reply.ToJsonString(),server.Tx(()=>server.Journal.RecoverMember("phone-a","inc-new")).ToJsonString());
        client.Tx(()=>client.Journal.RecoverClient(reply,"inc-new"));
        var rebased=client.Journal.Outbox().Single();Assert.Equal(original.Version.Clock,rebased.Version.Clock);Assert.Equal(original.Payload,rebased.Payload);
        client.Tx(()=>client.Journal.RecoverClient(reply,"inc-new"));Assert.Equal(rebased,client.Journal.Outbox().Single());
        Assert.Single(client.Rows.Scan("recovery:group-a"));
        var receipt=server.Tx(()=>server.Journal.Push("phone-a","inc-new",[rebased]));
        client.Tx(()=>client.Journal.AcceptReceipts(receipt["receipts"]!.AsArray()));
        Assert.Empty(client.Journal.Outbox());Assert.Empty(client.Rows.Scan("recovery:group-a"));
    }

    [Fact] public void ReceiptLossReplaysExactlyWithoutAnotherJournalEntry()
    {
        using var server=new Fixture();Setup(server);
        var change=Change("offset:collection:music/song.flac","250");
        var first=server.Tx(()=>server.Journal.Push("phone-a","inc-a",[change]));
        var second=server.Tx(()=>server.Journal.Push("phone-a","inc-a",[change]));
        Assert.True(JsonNode.DeepEquals(first,second));
        Assert.Equal(1,server.Rows.Count("journal:group-a"));
        Assert.Equal("250",GroupSyncContract.ReadMutation(Read(server.Rows.Scan("register:group-a").Single())).Payload);
    }

    [Fact] public void AReusedOriginOrSequenceGapRollsBackAllBusinessAndJournalWrites()
    {
        using var server=new Fixture();Setup(server);
        var first=Change("offset:song","100");
        server.Tx(()=>server.Journal.Push("phone-a","inc-a",[first]));
        var next=Change("offset:other","200",2);
        var error=Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Push("phone-a","inc-a",[next,first with { Payload="999" }])));
        Assert.Equal("origin_reused",error.Code);
        Assert.Equal(1,server.Rows.Count("journal:group-a"));
        Assert.Null(server.Rows.Read("register:group-a","offset:other"));
        Assert.Equal("origin_sequence_gap",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Push("phone-a","inc-a",[next with { Version=next.Version with { OriginSequence=3 } }]))).Code);
    }

    [Fact] public void SenderIdentityCannotBorrowAnotherMembersOriginAndEmptyPushRequiresMembership()
    {
        using var server=new Fixture();Setup(server);
        Assert.Equal("origin_not_owned",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Push("phone-a","inc-a",[Change("offset:song","1",actor:"pc-a",incarnation:"inc-pc")]))).Code);
        Assert.Equal("member_not_active",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Push("unknown","inc-a",[]))).Code);
        Assert.Equal(0,server.Rows.Count("journal:group-a"));
    }

    [Fact] public void SnapshotKeepsLocalOutboxAndPreservesTombstoneWhenOlderValueReturns()
    {
        using var server=new Fixture();using var client=new Fixture();Setup(server);
        server.Tx(()=>server.Journal.Capture(new Dictionary<string,GroupSeed> { ["playlist:list-a"]=new(null,30),["offset:song"]=new("50",10) }));
        var info=server.Tx(server.Journal.Info);
        client.Tx(()=> { client.Journal.JoinClient(info,"phone-a","inc-a","peer-a");client.Journal.Capture(new Dictionary<string,GroupSeed> { ["playlist:list-a"]=new("{\"items\":[],\"name\":\"old\"}",10) }); });
        var page=server.Tx(()=>server.Journal.Snapshot("phone-a","snapshot-a"));
        client.Tx(()=>client.Journal.ReceiveSnapshot(page));
        Assert.Equal(1,client.Tx(client.Journal.Outbox).Count);
        var pending=client.Tx(client.Journal.PendingApply);
        Assert.Null(pending.Single(m=>m.Key=="playlist:list-a").Payload);
        Assert.Equal(2,client.Tx(client.Journal.Info)["cursor"]!.GetValue<long>());
        Assert.False(client.Tx(client.Journal.SnapshotProgress)["required"]!.GetValue<bool>());
    }

    [Fact] public void CompactionWaitsForOfflineMembersRetainsDeletionAndRejectsExpiredReplay()
    {
        using var server=new Fixture();Setup(server);
        var change=Change("playlist:list-a",null);
        server.Tx(()=>server.Journal.Push("phone-a","inc-a",[change]));
        server.Tx(()=>server.Journal.Acknowledge("pc-a",1,1));
        Assert.Equal(0,server.Tx(server.Journal.Compact));
        server.Tx(()=>server.Journal.Acknowledge("phone-a",1,1));
        Assert.Equal(1,server.Tx(server.Journal.Compact));
        Assert.Equal(0,server.Rows.Count("journal:group-a"));
        Assert.NotNull(server.Rows.Read("register:group-a","playlist:list-a"));
        Assert.Equal("origin_checkpoint_expired",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Push("phone-a","inc-a",[change]))).Code);
        Assert.Equal("reset_required",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Pull("group-a",1,0))).Code);
        Assert.Null(GroupSyncContract.ReadMutation(Read(server.Rows.Scan("register:group-a").Single())).Payload);
    }

    [Fact] public void MemberRemovalChangesGenerationAndNeverAcceptsOldGenerationWrites()
    {
        using var server=new Fixture();Setup(server);
        server.Tx(()=>server.Journal.RemoveMember("phone-a"));
        Assert.Equal(2,server.Tx(server.Journal.Info)["generation"]!.GetValue<long>());
        Assert.Equal("member_not_active",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Push("phone-a","inc-a",[]))).Code);
        Assert.Equal("member_not_active",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.JoinMember("phone-a","inc-a"))).Code);
        server.Tx(()=>server.Journal.JoinMember("phone-a","inc-a",replace:true));
        Assert.Equal("reset_required",Assert.Throws<GroupSyncException>(()=>server.Tx(()=>server.Journal.Push("phone-a","inc-a",[Change("offset:song","1")]))).Code);
    }

    [Fact] public void SnapshotIsFixedWhileNewChangesArriveAndIsReleasedAfterAcknowledgement()
    {
        using var server=new Fixture();Setup(server);
        var seeds=Enumerable.Range(0,401).ToDictionary(i=>"offset:song-"+i.ToString("D4"),i=>new GroupSeed(i.ToString(),i));
        server.Tx(()=>server.Journal.Capture(seeds));
        var first=server.Tx(()=>server.Journal.Snapshot("phone-a","snapshot-fixed"));
        Assert.Equal(200,first["rows"]!.AsArray().Count);
        server.Tx(()=>server.Journal.Push("phone-a","inc-a",[Change("offset:later","1",time:2000)]));
        var second=server.Tx(()=>server.Journal.SnapshotPage("phone-a","snapshot-fixed",first["next"]!.GetValue<long>()));
        var third=server.Tx(()=>server.Journal.SnapshotPage("phone-a","snapshot-fixed",second["next"]!.GetValue<long>()));
        Assert.Equal(401L,third["head"]!.GetValue<long>());Assert.False(third["hasMore"]!.GetValue<bool>());
        Assert.Single(third["rows"]!.AsArray());
        server.Tx(()=>server.Journal.Acknowledge("phone-a",1,401));
        Assert.Equal(0,server.Rows.Count("snapshot:snapshot-fixed"));
        Assert.Single(server.Tx(()=>server.Journal.Pull("group-a",1,401))["rows"]!.AsArray());
    }

    [Fact] public void ProjectionAndReceiptRollbackAlongWithBusinessTransaction()
    {
        using var server=new Fixture();Setup(server);
        Assert.Throws<IOException>(()=>server.Tx(()=> {
            server.Journal.Push("phone-a","inc-a",[Change("offset:song","250")]);
            server.Db.CreatePlaylist("Should roll back","list-a",1);
            throw new IOException("Synthetic interruption");
        }));
        Assert.Empty(server.Db.Playlists());Assert.Equal(0,server.Rows.Count("register:group-a"));
        Assert.Equal(0,server.Rows.Count("journal:group-a"));
        Assert.Equal(0,server.Journal.Info()["head"]!.GetValue<long>());
    }
}

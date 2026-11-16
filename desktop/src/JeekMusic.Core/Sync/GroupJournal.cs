using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace JeekMusic.Core.Sync;

public sealed record GroupRow(string Key, string Value, long Cursor);

/// <summary>All journal operations MUST run in the same transaction as their business projection.</summary>
public interface IGroupRows
{
    string? Read(string kind, string key);
    void Put(string kind, string key, string value, long cursor = 0);
    void Delete(string kind, string key);
    void DeleteThrough(string kind, long cursor);
    void DeletePrefixThrough(string prefix, long cursor);
    IReadOnlyList<GroupRow> Page(string kind, long after, long through, int limit = 200);
    IReadOnlyList<GroupRow> Scan(string kind, string afterKey = "", int limit = 200);
    long Count(string kind);
}

/// <summary>Bounded durable group logic, independent of SQLite, UI and transport. No file IO.</summary>
public sealed class GroupJournal(IGroupRows rows, Func<long> now)
{
    public const int PageSize = 200;
    public const int MaximumKeys = 100_000;
    public const int MaximumMembers = 64;
    public const int MaximumPayloadBytes = 524_288;
    JsonObject? Read(string kind, string key) => rows.Read(kind, key) is { } json ? JsonNode.Parse(json)!.AsObject() : null;
    void Put(string kind, string key, JsonObject value, long cursor = 0) => rows.Put(kind, key, value.ToJsonString(), cursor);
    static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    static long N(JsonNode? n) => SyncMerge.Number(n);
    static long WireNumber(JsonObject value,string key,long minimum=0)
    {
        if(value[key] is not JsonValue node)throw new GroupSyncException("invalid_contract");
        long result;if(node.TryGetValue<long>(out var wide))result=wide;else if(node.TryGetValue<int>(out var narrow))result=narrow;else throw new GroupSyncException("invalid_contract");
        Check(result>=minimum && result<=GroupSyncContract.MaxInteger,"invalid_contract");return result;
    }
    static void PageHeader(JsonObject page,bool snapshot)
    {
        _=WireNumber(page,"generation",1);_=WireNumber(page,"head");_=WireNumber(page,"next");
        Check(page["groupId"] is JsonValue id && id.TryGetValue<string>(out _) && page["hasMore"] is JsonValue more && more.TryGetValue<bool>(out _),"invalid_contract");
        if(snapshot)Check(page["snapshotId"] is JsonValue value && value.TryGetValue<string>(out _),"invalid_contract");
    }
    static void Check(bool value, string code) { if (!value) throw new GroupSyncException(code); }
    JsonObject Meta => Read("meta", "active") ?? throw new GroupSyncException("group_not_joined");
    static string Kind(JsonObject meta, string name) => name + ":" + S(meta["groupId"]);
    static string OriginKind(JsonObject meta, GroupVersion version) => Kind(meta, "origin") + ":" + version.OriginDeviceId + ":" + version.DeviceIncarnation;
    static string Sequence(long value) => value.ToString("D16", CultureInfo.InvariantCulture);
    static GroupClock Clock(JsonObject meta) => new(N(meta["clockMs"]), (int)N(meta["clockCounter"]));
    static void SetClock(JsonObject meta, GroupClock clock) { meta["clockMs"] = clock.PhysicalMs; meta["clockCounter"] = clock.LogicalCounter; }
    void SaveMeta(JsonObject meta) => Put("meta", "active", meta);
    public bool Active => rows.Read("meta", "active") is not null;

    public JsonObject Info()
    {
        var meta = Meta;
        return new() { ["groupId"] = S(meta["groupId"]), ["coordinatorDeviceId"] = S(meta["coordinatorDeviceId"]),
            ["generation"] = N(meta["generation"]), ["role"] = S(meta["role"]), ["actorId"] = S(meta["actorId"]),
            ["incarnation"] = S(meta["incarnation"]), ["peerId"] = S(meta["peerId"]), ["cursor"] = N(meta["cursor"]),
            ["head"] = N(meta["head"]), ["compactedThrough"] = N(meta["compactedThrough"]),
            ["pendingUploads"] = rows.Count(Kind(meta,"outbox")), ["pendingApply"] = rows.Count(Kind(meta,"apply")),
            ["registers"] = rows.Count(Kind(meta,"register")), ["members"] = rows.Count(Kind(meta,"member")), ["clockAheadMs"]=Math.Max(0,N(meta["clockMs"])-now()), ["userDataFormat"] = 3 };
    }

    public void Create(string groupId, string actorId, string incarnation)
    {
        Check(!Active, "group_already_joined");
        GroupSyncContract.Validate(new GroupReference(groupId, "identity"));
        GroupSyncContract.Validate(new GroupVersion(new(0, 0), actorId, incarnation, 1));
        var meta = new JsonObject { ["groupId"] = groupId, ["generation"] = 1L, ["role"] = "coordinator", ["actorId"] = actorId,
            ["coordinatorDeviceId"] = actorId, ["incarnation"] = incarnation, ["nextOrigin"] = 0L, ["head"] = 0L, ["cursor"] = 0L,
            ["clockMs"] = 0L, ["clockCounter"] = 0, ["compactedThrough"] = 0L, ["registerCount"] = 0L, ["seeded"] = false };
        SaveMeta(meta); JoinMember(actorId, incarnation);
    }

    public JsonObject JoinMember(string actorId, string incarnation, bool replace = false)
    {
        var meta = Meta; Check(S(meta["role"]) == "coordinator", "not_coordinator");
        GroupSyncContract.Validate(new GroupVersion(new(0,0), actorId, incarnation,1));
        var member = Read(Kind(meta, "member"), actorId);
        if(member is not null && member["active"]?.GetValue<bool>()!=true)Check(replace,"member_not_active");
        if (member is not null && S(member["incarnation"]) != incarnation) Check(replace, "member_incarnation_changed");
        if (member is null || S(member["incarnation"]) != incarnation)
        {
            long activeCount=0;string after="";
            while(true) { var page=rows.Scan(Kind(meta,"member"),after);foreach(var row in page)if(JsonNode.Parse(row.Value)?["active"]?.GetValue<bool>()==true)activeCount++;if(page.Count==0)break;after=page[^1].Key; }
            Check(activeCount < MaximumMembers, "member_limit");
            member = new() { ["incarnation"] = incarnation, ["acceptedOrigin"] = 0L, ["confirmedOrigin"] = 0L, ["ackCursor"] = 0L, ["ackGeneration"] = N(meta["generation"]) };
        }
        member["active"] = true; Put(Kind(meta,"member"), actorId, member);
        return Info();
    }

    public void JoinClient(JsonObject server, string actorId, string incarnation, string peerId)
    {
        var groupId = S(server["groupId"]); var generation = N(server["generation"]);
        _=WireNumber(server,"generation",1);
        GroupSyncContract.Validate(new GroupReference(groupId,"identity"));
        GroupSyncContract.Validate(new GroupVersion(new(0,0),actorId,incarnation,1));
        GroupSyncContract.Validate(new GroupVersion(new(0,0),S(server["coordinatorDeviceId"]),incarnation,1));
        Check(generation is >= 1 and <= GroupSyncContract.MaxInteger, "invalid_contract");
        if (Active)
        {
            var old = Meta;
            Check(S(old["groupId"]) == groupId && S(old["incarnation"]) == incarnation && S(old["role"]) == "member" && S(old["actorId"])==actorId && S(old["peerId"])==peerId && S(old["coordinatorDeviceId"])==S(server["coordinatorDeviceId"]), "active_group_conflict");
            ResetGeneration(generation); return;
        }
        SaveMeta(new() { ["groupId"] = groupId, ["generation"] = generation, ["role"] = "member", ["actorId"] = actorId,
            ["coordinatorDeviceId"] = S(server["coordinatorDeviceId"]), ["incarnation"] = incarnation, ["peerId"] = peerId,
            ["nextOrigin"] = 0L, ["clockMs"] = 0L, ["clockCounter"] = 0, ["cursor"] = 0L, ["head"] = 0L,
            ["compactedThrough"] = 0L, ["registerCount"] = 0L, ["seeded"] = false, ["needsSnapshot"] = true });
    }

    public void ResetGeneration(long generation)
    {
        var meta = Meta;
        Check(generation >= N(meta["generation"]) && generation <= GroupSyncContract.MaxInteger, "invalid_checkpoint");
        if (generation == N(meta["generation"])) return;
        meta["generation"] = generation; meta["cursor"] = 0L; meta["needsSnapshot"] = true;
        meta.Remove("snapshotId"); meta.Remove("snapshotAfter"); SaveMeta(meta);
    }

    public void Capture(IReadOnlyDictionary<string, GroupSeed> current)
    {
        var meta = Meta;
        Check(current.Count <= MaximumKeys, "register_limit");
        var candidates = new SortedDictionary<string,GroupSeed>(current.ToDictionary(p=>p.Key,p=>p.Value),StringComparer.Ordinal);
        // Missing user-owned registers are explicit deletions only after a known local projection.
        string after = "";
        while (true)
        {
            var page = rows.Scan(Kind(meta,"projection"),after);
            foreach (var row in page)
                if (!current.ContainsKey(row.Key) && GroupUserData.DeletesWhenMissing(row.Key)) candidates[row.Key] = new(null);
            if (page.Count == 0) break; after = page[^1].Key;
        }
        foreach (var (key, seed) in candidates)
        {
            var projected = Read(Kind(meta,"projection"),key);
            if (projected is not null && (projected["payload"] is null ? null : S(projected["payload"])) == seed.Payload) continue;
            if (seed.Payload is { } payload) Check(Encoding.UTF8.GetByteCount(payload) <= MaximumPayloadBytes, "payload_limit");
            long sequence = N(meta["nextOrigin"]) + 1;
            Check(sequence <= GroupSyncContract.MaxInteger, "origin_exhausted");
            GroupClock clock;
            if (meta["seeded"]?.GetValue<bool>() != true)
            {
                Check(seed.EditedAtMs is >= 0 and <= GroupSyncContract.MaxInteger, "invalid_contract");
                clock = new(seed.EditedAtMs,0);
                if (clock.PhysicalMs > Clock(meta).PhysicalMs) SetClock(meta,clock);
            }
            else { clock = GroupSyncContract.NextClock(Clock(meta),now());SetClock(meta,clock); }
            var mutation = new GroupMutation(S(meta["groupId"]),N(meta["generation"]),key,
                new(clock,S(meta["actorId"]),S(meta["incarnation"]),sequence),seed.Payload);
            ValidateBusiness(mutation);
            meta["nextOrigin"] = sequence;
            if (S(meta["role"]) == "coordinator") Accept(meta,mutation);
            else { Put(Kind(meta,"outbox"),Sequence(sequence),GroupSyncContract.WriteMutation(mutation),sequence); Register(meta,mutation,0); }
            Put(Kind(meta,"projection"),key,new() { ["payload"] = seed.Payload });
            var winner = RegisterValue(meta,key);
            if (winner.Payload == seed.Payload) rows.Delete(Kind(meta,"apply"),key);
        }
        meta["seeded"] = true; SaveMeta(meta);
    }

    static void ValidateBusiness(GroupMutation mutation)
    {
        GroupSyncContract.Validate(mutation);
        if (mutation.Payload is { } payload) Check(Encoding.UTF8.GetByteCount(payload) <= MaximumPayloadBytes,"payload_limit");
        try { _ = GroupUserData.Materialize([mutation],1); }
        catch (System.Text.Json.JsonException) { throw new GroupSyncException("invalid_user_data"); }
    }
    GroupMutation RegisterValue(JsonObject meta,string key) => GroupSyncContract.ReadMutation(Read(Kind(meta,"register"),key)!) with { Generation = N(meta["generation"]) };
    void Register(JsonObject meta,GroupMutation mutation,long receipt)
    {
        var old = Read(Kind(meta,"register"),mutation.Key);
        if (old is not null)
        {
            var previous = GroupSyncContract.ReadMutation(old);
            if (previous.Version.OriginDeviceId == mutation.Version.OriginDeviceId && previous.Version.DeviceIncarnation == mutation.Version.DeviceIncarnation && previous.Version.OriginSequence == mutation.Version.OriginSequence)
                Check(Fingerprint(previous) == Fingerprint(mutation),"origin_reused");
            if (GroupSyncContract.Compare(mutation.Version,previous.Version) <= 0) return;
        }
        else { var count=N(meta["registerCount"]);Check(count<MaximumKeys,"register_limit");meta["registerCount"]=count+1; }
        Put(Kind(meta,"register"),mutation.Key,GroupSyncContract.WriteMutation(mutation),receipt);
        Put(Kind(meta,"apply"),mutation.Key,GroupSyncContract.WriteMutation(mutation),receipt);
    }
    public static string Fingerprint(GroupMutation mutation)
    {
        GroupSyncContract.Validate(mutation);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in new[] { mutation.GroupId,mutation.Key,mutation.Version.Clock.PhysicalMs.ToString(CultureInfo.InvariantCulture),
            mutation.Version.Clock.LogicalCounter.ToString(CultureInfo.InvariantCulture),mutation.Version.OriginDeviceId,mutation.Version.DeviceIncarnation,
            mutation.Version.OriginSequence.ToString(CultureInfo.InvariantCulture),mutation.Payload })
        {
            var bytes = field is null ? null : Encoding.UTF8.GetBytes(field);
            hash.AppendData(Encoding.ASCII.GetBytes((bytes?.Length ?? -1).ToString(CultureInfo.InvariantCulture)+":"));
            if (bytes is not null) hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    long Accept(JsonObject meta,GroupMutation mutation)
    {
        var member = Read(Kind(meta,"member"),mutation.Version.OriginDeviceId);
        Check(member is not null && member["active"]?.GetValue<bool>() == true && S(member["incarnation"]) == mutation.Version.DeviceIncarnation,"member_not_active");
        var fingerprint = Fingerprint(mutation);
        if (mutation.Version.OriginSequence <= N(member!["acceptedOrigin"]))
        {
            var old = Read(OriginKind(meta,mutation.Version),Sequence(mutation.Version.OriginSequence));
            Check(old is not null,"origin_checkpoint_expired");
            Check(S(old!["fingerprint"]) == fingerprint,"origin_reused");
            return N(old["receipt"]);
        }
        Check(mutation.Version.OriginSequence == N(member["acceptedOrigin"])+1,"origin_sequence_gap");
        var receipt = N(meta["head"])+1; Check(receipt <= GroupSyncContract.MaxInteger,"stream_exhausted");
        Put(OriginKind(meta,mutation.Version),Sequence(mutation.Version.OriginSequence),new() { ["fingerprint"] = fingerprint,["receipt"] = receipt },receipt);
        Put(Kind(meta,"journal"),Sequence(receipt),GroupSyncContract.WriteMutation(mutation),receipt);
        member["acceptedOrigin"] = mutation.Version.OriginSequence; Put(Kind(meta,"member"),mutation.Version.OriginDeviceId,member);
        meta["head"] = receipt; Register(meta,mutation,receipt);
        return receipt;
    }

    public JsonObject Push(string sender,string incarnation,IReadOnlyList<GroupMutation> mutations)
    {
        var meta = Meta; Check(S(meta["role"]) == "coordinator","not_coordinator");
        RequireMember(sender,incarnation);
        Check(mutations.Count <= PageSize,"batch_limit");
        var receipts = new JsonArray();
        foreach (var mutation in mutations)
        {
            ValidateBusiness(mutation);
            Check(mutation.GroupId == S(meta["groupId"]),"group_mismatch");
            Check(mutation.Generation == N(meta["generation"]),"reset_required");
            Check(mutation.Version.OriginDeviceId == sender && mutation.Version.DeviceIncarnation == incarnation,"origin_not_owned");
            var receipt = Accept(meta,mutation);
            SetClock(meta,GroupSyncContract.NextClock(Clock(meta),now(),mutation.Version.Clock));
            receipts.Add((JsonNode)new JsonObject { ["originSequence"] = mutation.Version.OriginSequence,["fingerprint"] = Fingerprint(mutation),["receipt"] = receipt });
        }
        SaveMeta(meta);
        return new() { ["receipts"] = receipts,["head"] = N(meta["head"]),["generation"] = N(meta["generation"]) };
    }

    public IReadOnlyList<GroupMutation> Outbox() { var meta = Meta;return rows.Page(Kind(meta,"outbox"),0,GroupSyncContract.MaxInteger).Select(r=>GroupSyncContract.ReadMutation(JsonNode.Parse(r.Value)!.AsObject()) with { Generation = N(meta["generation"]) }).ToArray(); }
    public void AcceptReceipts(JsonArray receipts)
    {
        var meta = Meta; Check(receipts.Count <= PageSize,"batch_limit");
        Check(receipts.All(receipt=>receipt is JsonObject),"invalid_contract");
        foreach (var receipt in receipts.OfType<JsonObject>())
        {
            var sequence = WireNumber(receipt,"originSequence",1);_=WireNumber(receipt,"receipt",1);
            var pending = Read(Kind(meta,"outbox"),Sequence(sequence));
            Check(pending is not null,"unknown_receipt");
            Check(Fingerprint(GroupSyncContract.ReadMutation(pending!)) == S(receipt["fingerprint"]),"receipt_mismatch");
            rows.Delete(Kind(meta,"outbox"),Sequence(sequence));
        }
        if(rows.Count(Kind(meta,"outbox"))==0)rows.DeleteThrough(Kind(meta,"recovery"),GroupSyncContract.MaxInteger);
    }

    public JsonObject Pull(string groupId,long generation,long cursor,long? through = null)
    {
        var meta = Meta; Check(S(meta["role"]) == "coordinator","not_coordinator");
        var head = through ?? N(meta["head"]);
        Check(head <= N(meta["head"]),"invalid_checkpoint");
        GroupSyncContract.CheckCheckpoint(S(meta["groupId"]),N(meta["generation"]),N(meta["compactedThrough"]),head,groupId,generation,cursor);
        var page = rows.Page(Kind(meta,"journal"),cursor,head);
        var result = new JsonArray(page.Select(r=>(JsonNode)new JsonObject { ["receipt"] = r.Cursor,["mutation"] = JsonNode.Parse(r.Value) }).ToArray());
        var next = page.Count == 0 ? cursor : page[^1].Cursor;
        return new() { ["groupId"] = groupId,["generation"] = generation,["head"] = head,["next"] = next,["hasMore"] = next < head,["rows"] = result };
    }

    public void Receive(JsonObject page)
    {
        PageHeader(page,false);
        var meta = Meta;
        Check(S(meta["role"])=="member","not_member_replica");
        Check(S(page["groupId"]) == S(meta["groupId"]),"group_mismatch");
        Check(N(page["generation"]) == N(meta["generation"]),"reset_required");
        var cursor = N(meta["cursor"]); var next = cursor;
        var items = page["rows"] as JsonArray ?? throw new GroupSyncException("invalid_contract");
        Check(items.Count <= PageSize,"batch_limit");
        Check(items.All(item=>item is JsonObject),"invalid_contract");
        foreach (var item in items.OfType<JsonObject>())
        {
            var receipt = WireNumber(item,"receipt",1); Check(receipt == next+1 && receipt <= N(page["head"]),"invalid_checkpoint");
            var mutation = GroupSyncContract.ReadMutation(item["mutation"]!.AsObject()); ValidateBusiness(mutation);
            Check(mutation.GroupId == S(meta["groupId"]) && mutation.Generation == N(meta["generation"]),"reset_required");
            Register(meta,mutation,receipt); SetClock(meta,GroupSyncContract.NextClock(Clock(meta),now(),mutation.Version.Clock)); next = receipt;
        }
        Check(next == N(page["next"]),"invalid_checkpoint");
        meta["cursor"] = next; meta["head"] = N(page["head"]); SaveMeta(meta);
    }

    public IReadOnlyList<GroupMutation> PendingApply() { var meta=Meta;return rows.Scan(Kind(meta,"apply")).Select(r=>GroupSyncContract.ReadMutation(JsonNode.Parse(r.Value)!.AsObject()) with { Generation=N(meta["generation"]) }).ToArray(); }
    public void ReapplyCaches()
    {
        var meta=Meta;string after="";
        while(true)
        {
            var page=rows.Scan(Kind(meta,"register"),after);
            foreach(var row in page)if(GroupUserData.IsCache(row.Key))rows.Put(Kind(meta,"apply"),row.Key,row.Value,row.Cursor);
            if(page.Count==0)break;after=page[^1].Key;
        }
    }
    public void Projected(IReadOnlyDictionary<string,GroupSeed> actual,IReadOnlyList<GroupMutation> applied)
    {
        var meta=Meta;
        foreach(var mutation in applied)
        {
            var current=RegisterValue(meta,mutation.Key);
            Check(Fingerprint(current)==Fingerprint(mutation),"projection_changed");
            Put(Kind(meta,"projection"),mutation.Key,new() { ["payload"]=actual.GetValueOrDefault(mutation.Key)?.Payload });
            rows.Delete(Kind(meta,"apply"),mutation.Key);
        }
    }

    public void Acknowledge(string actorId,long generation,long cursor)
    {
        var meta=Meta;Check(S(meta["role"])=="coordinator","not_coordinator");
        GroupSyncContract.CheckCheckpoint(S(meta["groupId"]),N(meta["generation"]),N(meta["compactedThrough"]),N(meta["head"]),S(meta["groupId"]),generation,cursor);
        var member=Read(Kind(meta,"member"),actorId);Check(member is not null && member["active"]?.GetValue<bool>()==true,"member_not_active");
        Check(generation!=N(member!["ackGeneration"]) || cursor>=N(member["ackCursor"]),"checkpoint_rollback");
        member["ackGeneration"]=generation;member["ackCursor"]=cursor;Put(Kind(meta,"member"),actorId,member);
        if(Read(Kind(meta,"snapshot-owner"),actorId) is { } owner)
        {
            var snapshotId=S(owner["snapshotId"]);var header=Read("snapshot-header",snapshotId);
            if(header is not null && N(header["generation"])==generation && cursor>=N(header["head"]))
            {
                rows.DeleteThrough("snapshot:"+snapshotId,GroupSyncContract.MaxInteger);rows.Delete("snapshot-header",snapshotId);
                rows.Delete(Kind(meta,"snapshot-owner"),actorId);
            }
        }
    }

    public void RequireMember(string actorId,string? incarnation=null)
    {
        var meta=Meta;
        var member=Read(Kind(meta,"member"),actorId);
        Check(member is not null && member["active"]?.GetValue<bool>()==true,"member_not_active");
        if(incarnation is not null)Check(S(member!["incarnation"])==incarnation,"member_incarnation_changed");
    }

    public JsonObject Snapshot(string actorId,string snapshotId)
    {
        var meta=Meta;Check(S(meta["role"])=="coordinator","not_coordinator");RequireMember(actorId);
        GroupSyncContract.Validate(new GroupReference(snapshotId,"snapshot"));
        Check(rows.Read("snapshot-header",snapshotId) is null,"snapshot_id_reused");
        // Expired snapshots never indefinitely consume another member's admission budget.
        foreach(var stale in rows.Scan("snapshot-header",limit:MaximumMembers+1))
        {
            var expired=JsonNode.Parse(stale.Value)!.AsObject();
            if(N(expired["expiresAt"])<now()) { rows.DeleteThrough("snapshot:"+stale.Key,GroupSyncContract.MaxInteger);rows.Delete("snapshot-header",stale.Key); }
        }
        // At most one bounded snapshot per member. Remove its previous rows before copying.
        if(Read(Kind(meta,"snapshot-owner"),actorId) is { } old)
        {
            var oldId=S(old["snapshotId"]);rows.DeleteThrough("snapshot:"+oldId,GroupSyncContract.MaxInteger);
            rows.Delete("snapshot-header",oldId);
        }
        var header=new JsonObject { ["snapshotId"]=snapshotId,["groupId"]=S(meta["groupId"]),["generation"]=N(meta["generation"]),
            ["owner"]=actorId,["head"]=N(meta["head"]),["expiresAt"]=now()+900_000 };
        string after="";
        while(true)
        {
            var page=rows.Scan(Kind(meta,"register"),after);
            foreach(var row in page)
            {
                var mutation=GroupSyncContract.ReadMutation(JsonNode.Parse(row.Value)!.AsObject()) with { Generation=N(meta["generation"]) };
                Put("snapshot:"+snapshotId,row.Key,GroupSyncContract.WriteMutation(mutation),row.Cursor);
            }
            if(page.Count==0)break;after=page[^1].Key;
        }
        Put("snapshot-header",snapshotId,header);
        Put(Kind(meta,"snapshot-owner"),actorId,new() { ["snapshotId"]=snapshotId });
        return SnapshotPage(actorId,snapshotId,0);
    }

    public JsonObject SnapshotPage(string actorId,string snapshotId,long after)
    {
        var meta=Meta;RequireMember(actorId);
        var header=Read("snapshot-header",snapshotId);
        Check(header is not null && S(header["owner"])==actorId && S(header["groupId"])==S(meta["groupId"]),"snapshot_unavailable");
        Check(N(header!["expiresAt"])>=now() && N(header["generation"])==N(meta["generation"]),"snapshot_expired");
        Check(after>=0 && after<=N(header["head"]),"invalid_checkpoint");
        var page=rows.Page("snapshot:"+snapshotId,after,N(header["head"]));
        var next=page.Count==0?after:page[^1].Cursor;
        bool more=rows.Page("snapshot:"+snapshotId,next,N(header["head"]),1).Count>0;
        return new() { ["snapshotId"]=snapshotId,["groupId"]=S(meta["groupId"]),["generation"]=N(meta["generation"]),["head"]=N(header["head"]),
            ["next"]=next,["hasMore"]=more,["rows"]=new JsonArray(page.Select(r=>(JsonNode)new JsonObject { ["receipt"]=r.Cursor,["mutation"]=JsonNode.Parse(r.Value) }).ToArray()) };
    }

    public void ReceiveSnapshot(JsonObject page)
    {
        PageHeader(page,true);
        var meta=Meta;Check(S(meta["role"])=="member","not_member_replica");
        Check(S(page["groupId"])==S(meta["groupId"]),"group_mismatch");
        Check(N(page["generation"])==N(meta["generation"]),"reset_required");
        var id=S(page["snapshotId"]);Check(id.Length>0,"invalid_contract");
        var existing=S(meta["snapshotId"]);
        if(existing.Length>0)Check(existing==id && N(meta["snapshotHead"])==N(page["head"]),"snapshot_changed");
        var after=existing.Length>0?N(meta["snapshotAfter"]):0;
        var items=page["rows"] as JsonArray ?? throw new GroupSyncException("invalid_contract");
        Check(items.Count<=PageSize && items.All(n=>n is JsonObject),"batch_limit");
        foreach(var item in items.OfType<JsonObject>())
        {
            var receipt=WireNumber(item,"receipt",1);Check(receipt>after && receipt<=N(page["head"]),"invalid_checkpoint");
            var mutation=GroupSyncContract.ReadMutation(item["mutation"]!.AsObject());ValidateBusiness(mutation);
            Check(mutation.GroupId==S(meta["groupId"]) && mutation.Generation==N(meta["generation"]),"reset_required");
            Register(meta,mutation,receipt);SetClock(meta,GroupSyncContract.NextClock(Clock(meta),now(),mutation.Version.Clock));after=receipt;
        }
        Check(after==N(page["next"]),"invalid_checkpoint");
        meta["snapshotId"]=id;meta["snapshotHead"]=N(page["head"]);meta["snapshotAfter"]=after;
        if(page["hasMore"]?.GetValue<bool>()==false)
        {
            meta["cursor"]=N(page["head"]);meta["head"]=N(page["head"]);meta["needsSnapshot"]=false;
            meta.Remove("snapshotId");meta.Remove("snapshotHead");meta.Remove("snapshotAfter");
        }
        SaveMeta(meta);
    }

    public JsonObject SnapshotProgress()
    {
        var meta=Meta;
        return new() { ["required"]=meta["needsSnapshot"]?.GetValue<bool>()==true,["snapshotId"]=S(meta["snapshotId"]),["after"]=N(meta["snapshotAfter"]) };
    }
    public void RestartSnapshot() { var meta=Meta;meta["needsSnapshot"]=true;meta.Remove("snapshotId");meta.Remove("snapshotHead");meta.Remove("snapshotAfter");SaveMeta(meta); }

    public long Compact()
    {
        var meta=Meta;Check(S(meta["role"])=="coordinator","not_coordinator");
        var acknowledgements=new List<long?>();string after="";
        while(true)
        {
            var page=rows.Scan(Kind(meta,"member"),after);
            foreach(var row in page)
            {
                var member=JsonNode.Parse(row.Value)!.AsObject();
                if(member["active"]?.GetValue<bool>()==true)acknowledgements.Add(N(member["ackGeneration"])==N(meta["generation"])?N(member["ackCursor"]):null);
            }
            if(page.Count==0)break;after=page[^1].Key;
        }
        var through=GroupSyncContract.SafeCompactionThrough(N(meta["head"]),acknowledgements);
        if(through>N(meta["compactedThrough"]))
        {
            // Keep all registers/tombstones/outboxes. Superseded origin replays are rejected by the member's durable accepted sequence.
            rows.DeleteThrough(Kind(meta,"journal"),through);rows.DeletePrefixThrough(Kind(meta,"origin")+":",through);
            meta["compactedThrough"]=through;SaveMeta(meta);
        }
        return N(meta["compactedThrough"]);
    }

    public void RemoveMember(string actorId)
    {
        var meta=Meta;Check(S(meta["role"])=="coordinator","not_coordinator");
        Check(actorId!=S(meta["actorId"]),"cannot_remove_coordinator");
        var member=Read(Kind(meta,"member"),actorId);Check(member is not null,"member_not_active");
        member!["active"]=false;Put(Kind(meta,"member"),actorId,member);
        var generation=N(meta["generation"])+1;Check(generation<=GroupSyncContract.MaxInteger,"generation_exhausted");
        meta["generation"]=generation;meta["compactedThrough"]=N(meta["head"]);rows.DeleteThrough(Kind(meta,"journal"),N(meta["head"]));SaveMeta(meta);
    }
    public void AllowRecovery(string actorId)
    {
        var meta=Meta;Check(S(meta["role"])=="coordinator" && actorId!=S(meta["actorId"]),"not_coordinator");
        var member=Read(Kind(meta,"member"),actorId);Check(member is not null,"member_not_active");
        member!["recoveryAllowed"]=true;Put(Kind(meta,"member"),actorId,member);
    }
    public JsonObject RecoverMember(string actorId,string incarnation)
    {
        var meta=Meta;var member=Read(Kind(meta,"member"),actorId);
        Check(S(meta["role"])=="coordinator","not_coordinator");
        if(member?["active"]?.GetValue<bool>()==true && S(member["incarnation"])==incarnation)return Info();
        Check(member?["recoveryAllowed"]?.GetValue<bool>()==true,"recovery_approval_required");
        RemoveMember(actorId);return JoinMember(actorId,incarnation,replace:true);
    }
    /// <summary>Explicit recovery rebases only known unacknowledged events, keeping their original HLC.</summary>
    public void RecoverClient(JsonObject server,string incarnation)
    {
        var meta=Meta;Check(S(meta["role"])=="member" && S(server["groupId"])==S(meta["groupId"]) && S(server["coordinatorDeviceId"])==S(meta["coordinatorDeviceId"]),"group_mismatch");
        GroupSyncContract.Validate(new GroupVersion(new(0,0),S(meta["actorId"]),incarnation,1));
        var generation=WireNumber(server,"generation",1);Check(generation>=N(meta["generation"]),"invalid_checkpoint");
        if(S(meta["incarnation"])==incarnation) { ResetGeneration(generation);return; }
        string after="";
        while(true)
        {
            var page=rows.Scan(Kind(meta,"outbox"),after);
            foreach(var row in page) { rows.Put(Kind(meta,"rebase-stage"),row.Key,row.Value,row.Cursor);rows.Put(Kind(meta,"recovery"),S(meta["incarnation"])+":"+row.Key,row.Value,row.Cursor); }
            if(page.Count==0)break;after=page[^1].Key;
        }
        rows.DeleteThrough(Kind(meta,"outbox"),GroupSyncContract.MaxInteger);
        meta["incarnation"]=incarnation;meta["generation"]=generation;meta["nextOrigin"]=0L;meta["cursor"]=0L;meta["needsSnapshot"]=true;
        meta.Remove("snapshotId");meta.Remove("snapshotHead");meta.Remove("snapshotAfter");after="";
        while(true)
        {
            var page=rows.Scan(Kind(meta,"rebase-stage"),after);
            foreach(var row in page)
            {
                var old=GroupSyncContract.ReadMutation(JsonNode.Parse(row.Value)!.AsObject());var sequence=N(meta["nextOrigin"])+1;
                var rebased=old with { Generation=generation,Version=old.Version with { DeviceIncarnation=incarnation,OriginSequence=sequence } };
                meta["nextOrigin"]=sequence;Put(Kind(meta,"outbox"),Sequence(sequence),GroupSyncContract.WriteMutation(rebased),sequence);
            }
            if(page.Count==0)break;after=page[^1].Key;
        }
        rows.DeleteThrough(Kind(meta,"rebase-stage"),GroupSyncContract.MaxInteger);SaveMeta(meta);
    }
}

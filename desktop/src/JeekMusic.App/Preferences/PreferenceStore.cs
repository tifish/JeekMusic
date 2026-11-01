using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

/// <summary>Private, append-only revision ledger. Evaluation answers live in a separate attached DB.
/// DELETE journals + FULL sync permit SQLite's cross-database master-journal atomic commit.</summary>
public sealed class PreferenceStore : IDisposable
{
    readonly SqliteConnection db;
    readonly Lock gate = new();
    SqliteTransaction? transaction;
    public string DeviceId { get; }
    public PreferenceStore(string path, string deviceId)
    {
        SQLitePCL.Batteries_V2.Init();
        DeviceId = deviceId;
        db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open();
        Run("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;");
        Run("ATTACH DATABASE $path AS evaluation", ("$path", path == ":memory:" ? ":memory:" : path + ".evaluation"));
        Run("PRAGMA evaluation.journal_mode=DELETE; PRAGMA evaluation.synchronous=FULL; PRAGMA evaluation.secure_delete=ON;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version > 3) throw new InvalidDataException("Preference database needs a newer client");
        if (version == 0)
        {
            using var tx = db.BeginTransaction(); transaction=tx;
            Run("""
                CREATE TABLE events(id TEXT PRIMARY KEY, profile TEXT NOT NULL, entity TEXT NOT NULL, kind TEXT NOT NULL, json TEXT NOT NULL);
                CREATE INDEX events_entity ON events(profile,entity);
                CREATE TABLE evaluation.events(id TEXT PRIMARY KEY, profile TEXT NOT NULL, entity TEXT NOT NULL, kind TEXT NOT NULL, json TEXT NOT NULL);
                CREATE TABLE journal(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,sealed INTEGER NOT NULL);
                CREATE TABLE local(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE peers(id TEXT PRIMARY KEY,cursor INTEGER NOT NULL DEFAULT 0);
                PRAGMA user_version=1; PRAGMA evaluation.user_version=1;
                """);
            tx.Commit(); transaction=null;
        }
        if(version<2)
        {
            using var tx=db.BeginTransaction(); transaction=tx;
            Run("ALTER TABLE events ADD COLUMN dimension TEXT NOT NULL DEFAULT 'MOOD'; ALTER TABLE evaluation.events ADD COLUMN dimension TEXT NOT NULL DEFAULT 'MOOD'; CREATE INDEX events_head ON events(profile,kind,dimension); PRAGMA user_version=2; PRAGMA evaluation.user_version=2;");
            tx.Commit(); transaction=null;
        }
        if(version<3)
        {
            using var tx=db.BeginTransaction(); transaction=tx;
            // Only the dedicated learning databases. Keep the journal sequence as a deletion high-water mark.
            Run("CREATE TABLE reset_boundary(token TEXT PRIMARY KEY); DELETE FROM events; DELETE FROM evaluation.events; DELETE FROM journal; DELETE FROM peers; DELETE FROM local; INSERT INTO local VALUES('artifactResetPending','1'); PRAGMA user_version=3; PRAGMA evaluation.user_version=3;");
            tx.Commit(); transaction=null;
        }
    }
    SqliteCommand Command(string sql, params (string, object?)[] args)
    {
        var c = db.CreateCommand(); c.CommandText = sql; c.Transaction=transaction?.Connection is null ? null : transaction;
        foreach (var (k,v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }
    void Run(string sql, params (string, object?)[] args) { using var c = Command(sql,args); c.ExecuteNonQuery(); }
    object? Scalar(string sql, params (string, object?)[] args) { using var c = Command(sql,args); return c.ExecuteScalar(); }
    public string? Local(string key) { lock(gate) return Scalar("SELECT value FROM local WHERE key=$k", ("$k",key)) as string; }
    public void SetLocal(string key, string value) { lock(gate) Run("INSERT OR REPLACE INTO local VALUES($k,$v)", ("$k",key),("$v",value)); }
    public void SetLocalForGeneration(string key,string value,string expected) { lock(gate) { if(Generation!=expected) throw new OperationCanceledException("Learning data cleared"); SetLocal(key,value); } }
    public Dictionary<string,string> LocalRows(string prefix,int limit=100)
    {
        lock(gate) { using var c=Command("SELECT key,value FROM local WHERE key LIKE $p ORDER BY key LIMIT $n",("$p",prefix+"%"),("$n",limit)); using var r=c.ExecuteReader(); var values=new Dictionary<string,string>(); while(r.Read()) values[r.GetString(0)]=r.GetString(1); return values; }
    }
    public void RemoveLocal(string key) { lock(gate) Run("DELETE FROM local WHERE key=$k",("$k",key)); }
    public List<JsonObject> Read(string profile, string? kind = null, bool includeEvaluation = false)
    {
        lock(gate)
        {
            var source = includeEvaluation ? "(SELECT * FROM events UNION ALL SELECT * FROM evaluation.events)" : "events";
            using var c = Command($"SELECT json FROM {source} WHERE profile=$p AND ($k IS NULL OR kind=$k)", ("$p",profile),("$k",kind));
            using var r = c.ExecuteReader(); var result = new List<JsonObject>();
            while(r.Read()) result.Add(JsonNode.Parse(r.GetString(0))!.AsObject());
            return result;
        }
    }
    public List<JsonObject> Current(string profile, string kind, bool evaluation = false) => Read(profile,kind,evaluation).GroupBy(e => S(e["entityId"])).SelectMany(Heads).ToList();
    JsonObject Revision(string profile,string entity,string kind,JsonObject payload,bool resolve=false)
    {
        var heads=Heads(Read(profile,kind,true).Where(e=>S(e["entityId"])==entity));
        if(heads.Count>1 && !resolve) throw new InvalidOperationException("Concurrent revisions: review all heads before writing");
        return new JsonObject { ["protocol"]=JeekMusic.Core.Preferences.PreferenceProtocol.Version,["eventId"]=Guid.NewGuid().ToString("N"),["profileId"]=profile,["generation"]=Generation,["entityId"]=entity,["kind"]=kind,["deviceId"]=DeviceId,
            ["revision"]=heads.Select(h=>h["revision"]!.GetValue<long>()).DefaultIfEmpty(0).Max()+1,["parents"]=Strings(heads.Select(h=>S(h["eventId"]))),["at"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),["payload"]=payload.DeepClone() };
    }
    public JsonObject Append(string profile,string entity,string kind,JsonObject payload,bool resolve=false,string? expectedGeneration=null,string? eventId=null)
    {
        lock(gate) { if(expectedGeneration!=null && expectedGeneration!=Generation) throw new InvalidOperationException("Learning data cleared; start labeling again"); var old=eventId is null?null:Read(profile,includeEvaluation:true).SingleOrDefault(e=>S(e["eventId"])==eventId); if(old!=null) return old; var e=Revision(profile,entity,kind,payload,resolve); if(eventId!=null) e["eventId"]=eventId; Import([e]); return e; }
    }
    public void AppendBatch(IEnumerable<(string Profile,string Entity,string Kind,JsonObject Payload)> items,string? expectedGeneration=null)
    {
        var all=items.ToArray();
        if(all.Length is <1 or >100 || all.Select(i=>(i.Profile,i.Entity,i.Kind)).Distinct().Count()!=all.Length) throw new ArgumentException("Requires 1-100 distinct entities");
        lock(gate) { if(expectedGeneration!=null && expectedGeneration!=Generation) throw new InvalidOperationException("Learning data cleared; start labeling again"); Import(all.Select(i=>Revision(i.Profile,i.Entity,i.Kind,i.Payload)).ToArray()); }
    }
    static bool Sealed(JsonObject e) => S(e["kind"]) == "answer" && S(e["payload"]?["role"]) is "development" or "sealed_test";
    internal void AppendAndSetLocal(string profile,string entity,string kind,JsonObject payload,IReadOnlyDictionary<string,string> localValues,string expectedGeneration,Func<bool> guard)
    {
        lock(gate)
        {
            if(Generation!=expectedGeneration || !guard()) throw new InvalidOperationException("Learning evidence changed before atomic commit");
            ImportCore([Revision(profile,entity,kind,payload)],localValues);
        }
    }
    public int Import(IEnumerable<JsonObject> incoming)=>ImportCore(incoming,null);
    int ImportCore(IEnumerable<JsonObject> incoming,IReadOnlyDictionary<string,string>? localValues)
    {
        var rows = incoming.ToArray();
        if(rows.Length > 100) throw new ArgumentException("At most 100 revisions per page");
        foreach(var e in rows) Validate(e);
        lock(gate)
        {
            using var tx = db.BeginTransaction(); transaction=tx; int added=0;
            foreach(var e in rows)
            {
                var profile=S(e["profileId"]);
                if(S(e["generation"])!=Generation) continue; // Cleared/offline generation cannot resurrect.
                var id=S(e["eventId"]); var sealedRow=Sealed(e); var table=sealedRow ? "evaluation.events" : "events";
                var old=Scalar("SELECT json FROM events WHERE id=$id UNION ALL SELECT json FROM evaluation.events WHERE id=$id",("$id",id)) as string;
                if(old != null) { if(!JsonNode.DeepEquals(JsonNode.Parse(old),e)) throw new InvalidDataException("Revision ID reused with different content"); continue; }
                // Reject a parent belonging to another entity/profile; unknown parents may arrive on a later page.
                foreach(var parent in ReadStrings(e["parents"]))
                {
                    var parentJson=Scalar("SELECT json FROM events WHERE id=$id UNION ALL SELECT json FROM evaluation.events WHERE id=$id",("$id",parent)) as string;
                    if(parentJson is null) { if(S(e["kind"])=="profile" && e["payload"]?["deleted"]?.GetValue<bool>()==true) continue; throw new InvalidDataException("Missing revision ancestor; send parent page first"); }
                    var p=JsonNode.Parse(parentJson)!;
                    if(S(p["profileId"])!=S(e["profileId"]) || S(p["entityId"])!=S(e["entityId"]) || S(p["kind"])!=S(e["kind"]) || p["revision"]!.GetValue<long>()>=e["revision"]!.GetValue<long>())
                        throw new InvalidDataException("Invalid revision ancestry");
                }
                // An entity's purpose and content binding cannot be downgraded by a revision.
                if(S(e["kind"])=="answer")
                    foreach(var previous in Read(profile,"answer",true).Where(p=>S(p["entityId"])==S(e["entityId"])))
                        foreach(var key in new[] { "role","contextId","recordingId","contentSignature" })
                            if(S(previous["payload"]?[key])!=S(e["payload"]?[key])) throw new InvalidDataException("Answer purpose/content binding is immutable");
                Run($"INSERT INTO {table}(id,profile,entity,kind,json,dimension) VALUES($id,$p,$e,$k,$j,$d)",("$id",id),("$p",S(e["profileId"])),("$e",S(e["entityId"])),("$k",S(e["kind"])),("$j",e.ToJsonString()),("$d",Dimension(e["payload"])));
                Run("INSERT INTO journal(id,sealed) VALUES($id,$s)",("$id",id),("$s",sealedRow?1:0)); added++;

            }
            if(added>0) Run("DELETE FROM local WHERE key='validatedModel'");
            if(localValues!=null) foreach(var (key,value) in localValues) SetLocal(key,value);
            tx.Commit(); transaction=null; return added;
        }
    }
    public string[] ResetBoundary
    {
        get { lock(gate) { using var c=Command("SELECT token FROM reset_boundary ORDER BY token"); using var r=c.ExecuteReader(); var tokens=new List<string>(); while(r.Read()) tokens.Add(r.GetString(0)); return tokens.ToArray(); } }
    }
    public string Generation => Hash("personal-label-learning-v3\n"+string.Join("\n",ResetBoundary));
    void MergeBoundary(string[] tokens)
    {
        if(tokens.Any(t=>t.Length!=32 || t.Any(c=>!char.IsAsciiHexDigit(c)) || t!=t.ToLowerInvariant()) || tokens.Distinct().Count()!=tokens.Length) throw new ArgumentException("Invalid learning clear boundary");
        var added=tokens.Except(ResetBoundary).ToArray();
        if(added.Length==0) return;
        using var tx=db.BeginTransaction(); transaction=tx;
        foreach(var token in added) Run("INSERT INTO reset_boundary VALUES($t)",("$t",token));
        // A clear wins over all concurrent writes not observing every clear. No private answer bodies retained.
        Run("DELETE FROM events; DELETE FROM evaluation.events; DELETE FROM journal; DELETE FROM local WHERE key NOT IN ('deviceId','activeDimension','dailyCursor'); INSERT OR REPLACE INTO local VALUES('artifactResetPending','1');");
        tx.Commit(); transaction=null;
    }
    public void ClearLearning() { lock(gate) MergeBoundary(ResetBoundary.Append(Guid.NewGuid().ToString("N")).ToArray()); }
    public JsonObject Page(long after, int limit=100)
    {
        if(after<0 || limit is <1 or >100) throw new ArgumentException("Invalid cursor/page size");
        lock(gate)
        {
            using var c=Command("SELECT j.seq,COALESCE(a.json,b.json) FROM journal j LEFT JOIN events a ON a.id=j.id LEFT JOIN evaluation.events b ON b.id=j.id WHERE j.seq>$s ORDER BY j.seq LIMIT $n",("$s",after),("$n",limit));
            using var r=c.ExecuteReader(); var events=new JsonArray(); long next=after;
            while(r.Read()) { next=r.GetInt64(0); events.Add(JsonNode.Parse(r.GetString(1))); }
            var high=Convert.ToInt64(Scalar("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='journal'),0)"));
            if(events.Count<limit) next=high;
            if(after>high) throw new InvalidDataException("Cursor ahead of ledger; peer reset requires cursor 0");
            return new JsonObject { ["protocol"]=JeekMusic.Core.Preferences.PreferenceProtocol.Version,["resetBoundary"]=Strings(ResetBoundary),["generation"]=Generation,["events"]=events,["nextCursor"]=next,["hasMore"]=next<high };
        }
    }
    public long Cursor(string peer) { lock(gate) return Convert.ToInt64(Scalar("SELECT cursor FROM peers WHERE id=$p",("$p",peer)) ?? 0L); }
    public static void ValidatePage(JsonObject page)
    {
        if(page["protocol"]?.GetValue<int>() != JeekMusic.Core.Preferences.PreferenceProtocol.Version) throw new ArgumentException("Learning sync requires both devices to upgrade");
        if(page["events"] is not JsonArray rows || page["nextCursor"]?.GetValue<long>() is not >=0 || page["resetBoundary"] is not JsonArray) throw new ArgumentException("Invalid learning delta page");
        var boundary=ReadStrings(page["resetBoundary"]);
        if(boundary.Any(t=>t.Length!=32 || t.Any(c=>!char.IsAsciiHexDigit(c)) || t!=t.ToLowerInvariant()) || boundary.Distinct().Count()!=boundary.Length || rows.Count>100) throw new ArgumentException("Invalid learning clear boundary");
        if(S(page["generation"])!=Hash("personal-label-learning-v3\n"+string.Join("\n",boundary.Order(StringComparer.Ordinal)))) throw new ArgumentException("Invalid learning generation");
        foreach(var row in rows) { Validate(row!.AsObject()); if(S(row["generation"])!=S(page["generation"])) throw new ArgumentException("Mixed learning generations"); }
    }
    // Cursor advances only in the same transaction as all received events. A response lost after commit can be replayed.
    public int AcceptPage(string peer, JsonObject page)
    {
        ValidatePage(page);
        var rows=page["events"]!.AsArray();
        lock(gate)
        {
            var boundary=ReadStrings(page["resetBoundary"]);
            // Validate the full page before accepting its destructive boundary.
            MergeBoundary(boundary);
            // Import commits first. A crash before this cursor write only causes an idempotent replay.
            var added=Import(rows.Select(n => n!.AsObject()));
            Run("INSERT INTO peers VALUES($p,$c) ON CONFLICT(id) DO UPDATE SET cursor=MAX(cursor,excluded.cursor)",("$p",peer),("$c",page["nextCursor"]!.GetValue<long>()));
            SetLocal("sync:last:"+peer,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return added;
        }
    }
    public JsonObject SyncStatus()
    {
        lock(gate)
        {
            using var c=Command("SELECT id,cursor FROM peers ORDER BY id LIMIT 50"); using var r=c.ExecuteReader(); var peers=new JsonArray();
            while(r.Read()) peers.Add((JsonNode)new JsonObject { ["deviceId"]=r.GetString(0),["receivedCursor"]=r.GetInt64(1),["lastReceivedCommitAt"]=Local("sync:last:"+r.GetString(0)) });
            return new JsonObject { ["protocol"]=JeekMusic.Core.Preferences.PreferenceProtocol.Version,["generation"]=Generation,["clearBoundaryCount"]=ResetBoundary.Length,["highWater"]=Convert.ToInt64(Scalar("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='journal'),0)")),["peers"]=peers,["transport"]="direct_paired_lan_deltas",["legacyDirectoryRequired"]=false };
        }
    }
    public string[] Profiles() => [Dataset];
    public void Export(string profile, string path)
    {
        var content=new JsonObject { ["protocol"]=JeekMusic.Core.Preferences.PreferenceProtocol.Version,["resetBoundary"]=Strings(ResetBoundary),["generation"]=Generation,["events"]=new JsonArray(Read(profile,includeEvaluation:true).Select(e=>(JsonNode)e).ToArray()) };
        var tmp=path+".tmp"; File.WriteAllText(tmp,content.ToJsonString()); File.Move(tmp,path,true);
    }
    public void DeleteProfile(string profile) { if(profile!=Dataset) throw new ArgumentException("Unknown learning data"); ClearLearning(); }
    public void Dispose() => db.Dispose();
}

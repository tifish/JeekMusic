using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;
using JeekMusic.Library;

namespace JeekMusic.Sync;

/// <summary>Uses the library transaction so registers, receipts and user data commit together.</summary>
internal sealed class GroupSqlRows(LibraryDb db) : IGroupRows
{
    const long MaximumRows = 500_000;
    const long MaximumBytes = 256L * 1024 * 1024;
    const int PageBytes = 1_048_576;
    public string? Read(string kind,string key) => db.Scalar("SELECT value FROM sync_group_row WHERE kind=$kind AND key=$key",null,("$kind",kind),("$key",key)) as string;
    void Quota(long rowDelta,long byteDelta)
    {
        var quota=JsonNode.Parse(Read("quota","global") ?? throw new IOException("Missing group storage quota"))!.AsObject();
        long count=quota["rows"]!.GetValue<long>()+rowDelta, bytes=quota["bytes"]!.GetValue<long>()+byteDelta;
        if(count is <0 or >MaximumRows || bytes is <0 or >MaximumBytes)throw new GroupSyncException("group_storage_budget");
        db.Exec("UPDATE sync_group_row SET value=$value WHERE kind='quota' AND key='global'",null,
            ("$value",new JsonObject { ["rows"]=count,["bytes"]=bytes }.ToJsonString()));
    }
    public void Put(string kind,string key,string value,long cursor=0)
    {
        if(kind=="quota" || Encoding.UTF8.GetByteCount(value)>2_000_000)throw new GroupSyncException("row_budget");
        var old=Read(kind,key);Quota(old is null?1:0,Encoding.UTF8.GetByteCount(value)-(old is null?0:Encoding.UTF8.GetByteCount(old))+(old is null?Encoding.UTF8.GetByteCount(kind)+Encoding.UTF8.GetByteCount(key):0));
        db.Exec("INSERT INTO sync_group_row(kind,key,value,cursor) VALUES($kind,$key,$value,$cursor) ON CONFLICT(kind,key) DO UPDATE SET value=excluded.value,cursor=excluded.cursor",null,
            ("$kind",kind),("$key",key),("$value",value),("$cursor",cursor));
    }
    public void Delete(string kind,string key)
    {
        if(kind=="quota")throw new InvalidOperationException();
        var old=Read(kind,key);if(old is null)return;Quota(-1,-Encoding.UTF8.GetByteCount(old)-Encoding.UTF8.GetByteCount(kind)-Encoding.UTF8.GetByteCount(key));
        db.Exec("DELETE FROM sync_group_row WHERE kind=$kind AND key=$key",null,("$kind",kind),("$key",key));
    }
    void DeleteRange(string where,params (string name,object? value)[] arguments)
    {
        using var query=db.Command("SELECT COUNT(*),COALESCE(SUM(length(CAST(value AS BLOB))+length(CAST(kind AS BLOB))+length(CAST(key AS BLOB))),0) FROM sync_group_row WHERE "+where);
        foreach(var arg in arguments)query.Parameters.AddWithValue(arg.name,arg.value ?? DBNull.Value);
        long count,bytes;using(var reader=query.ExecuteReader()) { reader.Read();count=reader.GetInt64(0);bytes=reader.GetInt64(1); }
        Quota(-count,-bytes);db.Exec("DELETE FROM sync_group_row WHERE "+where,null,arguments);
    }
    public void DeleteThrough(string kind,long cursor) { if(kind=="quota")throw new InvalidOperationException();DeleteRange("kind=$kind AND cursor<=$cursor",("$kind",kind),("$cursor",cursor)); }
    public void DeletePrefixThrough(string prefix,long cursor)
    {
        if(!prefix.StartsWith("origin:",StringComparison.Ordinal))throw new InvalidOperationException();
        DeleteRange("kind>=$prefix AND kind<$end AND cursor<=$cursor",("$prefix",prefix),("$end",prefix+"\uffff"),("$cursor",cursor));
    }
    IReadOnlyList<GroupRow> ReadPage(string sql,int limit,params (string name,object? value)[] arguments)
    {
        if(limit is <1 or >201)throw new GroupSyncException("batch_limit");
        using var query=db.Command(sql);
        foreach(var arg in arguments)query.Parameters.AddWithValue(arg.name,arg.value ?? DBNull.Value);
        query.Parameters.AddWithValue("$limit",limit);
        using var reader=query.ExecuteReader();var result=new List<GroupRow>();int bytes=0;
        while(reader.Read())
        {
            var value=reader.GetString(1);int length=Encoding.UTF8.GetByteCount(value);
            if(result.Count>0 && bytes+length>PageBytes)break;
            result.Add(new(reader.GetString(0),value,reader.GetInt64(2)));bytes+=length;
        }
        return result;
    }
    public IReadOnlyList<GroupRow> Page(string kind,long after,long through,int limit=200)=>ReadPage(
        "SELECT key,value,cursor FROM sync_group_row WHERE kind=$kind AND cursor>$after AND cursor<=$through ORDER BY cursor,key LIMIT $limit",limit,("$kind",kind),("$after",after),("$through",through));
    public IReadOnlyList<GroupRow> Scan(string kind,string afterKey="",int limit=200)=>ReadPage(
        "SELECT key,value,cursor FROM sync_group_row WHERE kind=$kind AND key>$after ORDER BY key LIMIT $limit",limit,("$kind",kind),("$after",afterKey));
    public long Count(string kind)=>(long)db.Scalar("SELECT COUNT(*) FROM sync_group_row WHERE kind=$kind",null,("$kind",kind))!;
}

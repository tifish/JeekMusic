using System.Text.Json.Nodes;
using JeekMusic.Infrastructure;

namespace JeekMusic.Sync;

/// <summary>Device-local credentials, outside all group payloads and MCP diagnostic projections.</summary>
internal sealed class LanPeerStore
{
    readonly Lock gate=new();
    readonly string path=Path.Combine(AppPaths.DataDir,"sync-peers.json");
    void Write(JsonObject state)
    {
        var temporary=path+".tmp";var bytes=System.Text.Encoding.UTF8.GetBytes(state.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { file.Write(bytes);file.Flush(true); }
        if(!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes))throw new IOException("PC settings verification failed");
        if(File.Exists(path))File.Replace(temporary,path,path+".prev");else File.Move(temporary,path);
    }
    JsonObject Read()
    {
        if(!File.Exists(path))return new() { ["peers"]=new JsonObject() };
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
    public JsonObject Connection(string peerId) { lock(gate)return Read()["peers"]?[peerId]?.DeepClone().AsObject()??throw new ArgumentException("Unknown PC connection"); }
    public JsonArray List()
    {
        lock(gate)
        {
            var peers=Read()["peers"]!.AsObject();
            return new(peers.Select(p=>(JsonNode)new JsonObject { ["peerId"]=p.Key,["host"]=p.Value!["host"]?.DeepClone(),
                ["port"]=p.Value["port"]?.DeepClone(),["name"]=p.Value["name"]?.DeepClone() }).ToArray());
        }
    }
    public void Save(string peerId,JsonObject connection)
    {
        lock(gate)
        {
            var state=Read();var peers=state["peers"]!.AsObject();
            if(!peers.ContainsKey(peerId) && peers.Count>=32)throw new ArgumentException("PC connection limit reached");
            peers[peerId]=connection.DeepClone();Write(state);
        }
    }
    public string SavePaired(JsonObject connection)
    {
        lock(gate)
        {
            var entries=Read()["peers"]!.AsObject();
            var existing=entries.FirstOrDefault(p=>p.Value?["pin"]?.ToString()==connection["pin"]?.ToString());
            var peer=existing.Key??Guid.NewGuid().ToString("N");
            var merged=existing.Value?.DeepClone().AsObject()??new JsonObject();
            foreach(var pair in connection)merged[pair.Key]=pair.Value?.DeepClone();
            Save(peer,merged);return peer;
        }
    }
    public void Forget(string peerId) { lock(gate) { var state=Read();state["peers"]!.AsObject().Remove(peerId);Write(state); } }
    public static (JsonObject Connection,string Invitation) Invitation(string value,string actorId)
    {
        static ArgumentException Invalid()=>new("Invalid PC invitation");
        if(value.Length is <1 or >450 || value.Any(c=>c<=' ' || c>='\u007f') || !Uri.TryCreate(value,UriKind.Absolute,out var uri) || uri.Scheme!="jeeklan" || uri.Port is <1 or >65535 || uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0)throw Invalid();
        if(uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.Dns) || uri.Host.Length>253)throw Invalid();
        var parts=uri.AbsolutePath.Split('/');
        if(parts.Length!=3 || parts.Skip(1).Any(p=>p.Length!=64 || p.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))throw Invalid();
        return (new() { ["host"]=uri.Host,["port"]=uri.Port,["pin"]=parts[1],["deviceId"]=actorId },parts[2]);
    }
}

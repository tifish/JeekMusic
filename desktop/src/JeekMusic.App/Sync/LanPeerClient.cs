using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace JeekMusic.Sync;

internal sealed class LanPeerException(string code) : IOException("PC sync request refused: " + code)
{
    public string Code { get; } = code;
}

/// <summary>The same v4 TLS framing used by Android, with bounded requests and cancellation.</summary>
internal sealed class LanPeerClient(JsonObject connection) : IAsyncDisposable
{
    TcpClient? tcp;
    SslStream? stream;
    bool authenticated;
    long nextRequest;
    readonly SemaphoreSlim serial = new(1,1);
    static string S(JsonNode? node)=>node?.GetValue<string>()??"";
    public void Cancel() { stream?.Dispose();tcp?.Dispose();stream=null;tcp=null;authenticated=false; }
    async Task Connect(CancellationToken cancellation)
    {
        if(stream is not null)return;
        var pin=S(connection["pin"]);
        if(pin.Length!=64 || pin.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))throw new ArgumentException("Invalid PC fingerprint");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        tcp=new TcpClient { NoDelay=true };
        await tcp.ConnectAsync(S(connection["host"]),connection["port"]!.GetValue<int>(),deadline.Token);
        stream=new SslStream(tcp.GetStream(),false,(_,certificate,_,_)=> {
            if(certificate is null)return false;
            using var cert=X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return Convert.ToHexStringLower(SHA256.HashData(cert.RawData))==pin && cert.NotBefore.ToUniversalTime()<=DateTime.UtcNow && cert.NotAfter.ToUniversalTime()>=DateTime.UtcNow;
        });
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost=S(connection["host"]),EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13 },deadline.Token);
    }
    async Task Send(JsonObject value,int maximum,CancellationToken cancellation)
    {
        var bytes=Encoding.UTF8.GetBytes(value.ToJsonString());
        if(bytes.Length>maximum)throw new IOException("PC request exceeds operation budget");
        var header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,bytes.Length);
        await stream!.WriteAsync(header,cancellation);await stream.WriteAsync(bytes,cancellation);await stream.FlushAsync(cancellation);
    }
    async Task<JsonObject> Receive(int maximum,CancellationToken cancellation)
    {
        var header=new byte[4];await stream!.ReadExactlyAsync(header,cancellation);
        var length=BinaryPrimitives.ReadInt32BigEndian(header);
        if(length<2 || length>maximum)throw new IOException("Invalid PC response length");
        var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,cancellation);
        var value=JsonNode.Parse(new UTF8Encoding(false,true).GetString(bytes)) as JsonObject ?? throw new IOException("Invalid PC response");
        if(value.ContainsKey("error"))
        {
            var code=S(value["code"]);
            if(code.Length is <1 or >64 || code.Any(c=>!char.IsAsciiLetterOrDigit(c) && c!='_'))code="request_refused";
            throw new LanPeerException(code);
        }
        return value;
    }
    public async Task<JsonObject> Call(string operation,JsonObject? arguments=null,CancellationToken cancellation=default)
    {
        for(int attempt=0;;attempt++)
        {
            try { return await CallOnce(operation,arguments,cancellation); }
            catch(LanPeerException e) when(e.Code=="busy" && attempt<2 && operation!="pair") { await Task.Delay(600*(attempt+1),cancellation); }
        }
    }
    async Task<JsonObject> CallOnce(string operation,JsonObject? arguments,CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var token=deadline.Token;
            var wait=nextRequest-Environment.TickCount64;if(wait>0)await Task.Delay(TimeSpan.FromMilliseconds(wait),token);
            nextRequest=Environment.TickCount64+(operation is "state" or "preference_delta"?550:20);
            await Connect(token);
            if(operation!="pair" && !authenticated)
            {
                await Send(new() { ["op"]="auth",["protocol"]=4,["deviceId"]=S(connection["deviceId"]),["token"]=S(connection["token"]) },4096,token);
                var accepted=await Receive(4096,token);
                if(accepted["authenticated"]?.GetValue<bool>()!=true || accepted["protocol"]?.GetValue<int>()!=4)throw new LanPeerException("upgrade_required");
                authenticated=true;
            }
            var request=arguments?.DeepClone().AsObject()??new JsonObject();
            request["op"]=operation;request["deviceId"]=S(connection["deviceId"]);
            if(operation!="pair")request["token"]=S(connection["token"]);
            int maximum=operation switch { "pair"=>4096,"state"=>32_000_000,"preference_delta" or "group_push"=>4_000_000,"file_chunk"=>512*1024,_=>64*1024 };
            var bytes=Encoding.UTF8.GetByteCount(request.ToJsonString());if(bytes>maximum)throw new IOException("PC request exceeds operation budget");
            if(operation!="pair")
            {
                await Send(new() { ["op"]="request",["kind"]=operation,["length"]=bytes },4096,token);
                if((await Receive(4096,token))["ready"]?.GetValue<bool>()!=true)throw new IOException("PC did not accept request");
            }
            await Send(request,maximum,token);
            return await Receive(operation=="state"?32_000_000:operation.StartsWith("group_",StringComparison.Ordinal)?4_000_000:2_000_000,token);
        }
        catch { Cancel();throw; }
        finally { serial.Release(); }
    }
    public async Task<JsonObject> Operation(string operation,JsonObject arguments,CancellationToken cancellation)
    {
        string? job=null;
        try
        {
            var result=await Call(operation,arguments,cancellation);var started=Environment.TickCount64;int delay=50;
            while(result["pending"]?.GetValue<bool>()==true)
            {
                job=S(result["job"]);
                if(job.Length!=32 || Environment.TickCount64-started>31*60_000)throw new LanFileDeferred("remote_job_time_budget");
                await Task.Delay(delay,cancellation);delay=Math.Min(500,delay*2);
                result=await Call("plan_status",new() { ["job"]=job },cancellation);
            }
            job=null;
            if(result["conflict"]?.GetValue<bool>()==true)throw new LanFileConflict(S(result["reason"]));
            if(result["deferred"]?.GetValue<bool>()==true)throw new LanFileDeferred(S(result["reason"]));
            return result;
        }
        finally
        {
            if(job is not null)
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await using var control=new LanPeerClient(connection);await control.Call("plan_cancel",new() { ["job"]=job },deadline.Token); }
                catch(Exception e) when(e is IOException or OperationCanceledException or SocketException) { }
            }
        }
    }
    public ValueTask DisposeAsync() { Cancel();return ValueTask.CompletedTask; }
}

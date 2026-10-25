using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekMusic.Core.Sync;
using JeekMusic.Infrastructure;

namespace JeekMusic.Sync;

/// <summary>TLS 1.2/1.3, pinned certificate + one-use invitation. Credentials never enter diagnostics/logs.</summary>
public sealed class LanSyncService : IDisposable
{
    readonly AppServices _app;
    readonly object _gate = new();
    readonly SemaphoreSlim _clients = new(4);
    readonly Dictionary<string, (string Device, string Root, DateTime Expires, IReadOnlyList<LanFiles.Entry> Files)> _plans = new();
    readonly string _configPath = Path.Combine(AppPaths.DataDir, "lan-sync.json");
    readonly string _inbox = Path.Combine(AppPaths.DataDir, "lan-inbox");
    X509Certificate2? _certificate;
    JsonObject _config;
    TcpListener? _listener;
    CancellationTokenSource? _stop;
    string? _invitation;
    DateTime _invitationUntil;
    string _lastOperation = "";
    long _bytesServed;
    DateTime _lastNotify;
    public SyncFeature? Merge { get; set; }
    public string Problem { get; private set; } = "";
    public event Action? Changed;
    public int RootRevision { get; private set; }
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Certificate().RawData));
    JsonObject Roots => _config["roots"]!.AsObject();
    JsonObject Devices => _config["devices"]!.AsObject();

    public LanSyncService(AppServices app)
    {
        _app = app;
        try { _config = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { _config = new(); }
        _config["roots"] ??= new JsonObject(); _config["devices"] ??= new JsonObject();
        if (_config["enabled"]?.GetValue<bool>() == true)
            try { Start(); } catch (IOException) { /* A failed optional listener must not prevent player startup. */ }
    }

    X509Certificate2 Certificate()
    {
        lock (_gate)
        {
        if (_certificate is not null) return _certificate;
        // Windows key storage protects the non-exportable private key in the current user's key store.
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var thumbprint = _config["certificate"]?.GetValue<string>();
        _certificate = thumbprint is null ? null! : store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)!;
        if (_certificate is null)
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=JeekMusic LAN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
            store.Add(_certificate);
            _config["certificate"] = _certificate.Thumbprint;
            Save();
        }
        return _certificate;
        }
    }

    void Save() => SyncFeature.AtomicWrite(_configPath, _config.ToJsonString());
    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());
    public void Start(int port = 39843)
    {
        lock (_gate)
        {
            if (_listener is not null) return;
            try
            {
                Certificate(); // Only provision a private key when the user enables LAN sharing.
                _listener = new TcpListener(IPAddress.Any, port); _listener.Start(8);
                _stop = new CancellationTokenSource();
                _config["enabled"] = true; _config["port"] = Port; Save(); Problem = "";
                _ = Accept(_listener, _stop.Token);
            }
            catch (SocketException) { _listener?.Stop(); _listener = null; Problem = "Cannot listen on LAN port. Check port use/network permissions; firewall rules are not changed."; throw new IOException(Problem); }
            catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException or IOException)
            { _listener?.Stop(); _listener = null; Problem = "Cannot start LAN sharing: certificate storage or configuration is unavailable. Player remains usable."; throw new IOException(Problem); }
        }
        Notify();
    }
    public void Stop()
    {
        lock (_gate) { _stop?.Cancel(); _listener?.Stop(); _listener = null; _invitation = null; _plans.Clear(); _config["enabled"] = false; Save(); }
        Notify();
    }
    public JsonObject EnableRoot(long folderId, bool enabled)
    {
        var folder = _app.Library.Snapshot.Folder(folderId) ?? throw new ArgumentException("Unknown library folder");
        if (enabled) LanFiles.Resolve(folder.Root, "", true);
        lock (_gate)
        {
            var root = Roots.FirstOrDefault(p => p.Value?["folderId"]?.GetValue<long>() == folderId);
            var id = root.Key ?? Guid.NewGuid().ToString("N");
            Roots[id] = new JsonObject { ["folderId"] = folderId, ["enabled"] = enabled };
            RootRevision++;
            _plans.Clear(); Save(); Notify(); return new JsonObject { ["rootId"] = id, ["enabled"] = enabled };
        }
    }
    public string? Reference(long folderId, string relative)
    {
        lock (_gate)
        {
            var root = Roots.FirstOrDefault(p => p.Value?["folderId"]?.GetValue<long>() == folderId);
            return root.Key is null ? null : "root:" + root.Key + "/" + relative.Replace('\\', '/');
        }
    }
    public string Invite(string host)
    {
        if (Port == 0) Start();
        if (host.Length > 253 || !Uri.CheckHostName(host).Equals(UriHostNameType.IPv4) && !Uri.CheckHostName(host).Equals(UriHostNameType.Dns)) throw new ArgumentException("Enter LAN IPv4 address or hostname");
        lock (_gate)
        {
            _invitation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _invitationUntil = DateTime.UtcNow.AddMinutes(5);
            return $"jeeklan://{host}:{Port}/{Fingerprint}/{_invitation}";
        }
    }
    public void Revoke(string id) { lock (_gate) { Devices.Remove(id); _plans.Clear(); Save(); } Notify(); }
    public JsonObject Diagnostics()
    {
        lock (_gate) return new JsonObject
        {
            ["enabled"] = Port != 0, ["port"] = Port, ["problem"] = Problem, ["protocol"] = 1,
            ["lastOperation"] = _lastOperation, ["bytesServed"] = _bytesServed,
            ["addresses"] = new JsonArray(NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => (JsonNode?)JsonValue.Create(a.Address.ToString())).ToArray()),
            ["roots"] = new JsonArray(Roots.Select(p => { var f = _app.Library.Snapshot.Folder(p.Value!["folderId"]!.GetValue<long>()); return (JsonNode)new JsonObject
                { ["rootId"] = p.Key, ["folderId"] = p.Value["folderId"]!.DeepClone(), ["name"] = f?.DisplayName, ["enabled"] = p.Value["enabled"]!.DeepClone(), ["available"] = f?.Available ?? false }; }).ToArray()),
            ["devices"] = new JsonArray(Devices.Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["name"] = p.Value!["name"]!.DeepClone() }).ToArray())
        };
    }
    string RootPath(string id)
    {
        lock (_gate)
        {
            if (Roots[id] is not JsonObject root || root["enabled"]?.GetValue<bool>() != true) throw new IOException("Root is not shared");
            var folder = _app.Library.Snapshot.Folder(root["folderId"]!.GetValue<long>());
            if (folder is not { Available: true } || !Directory.Exists(folder.Root)) throw new IOException("Root is offline; no deletions inferred");
            return LanFiles.Resolve(folder.Root, "", true);
        }
    }
    async Task Accept(TcpListener listener, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancel);
                if (!_clients.Wait(0)) { client.Dispose(); continue; }
                _ = Handle(client, cancel).ContinueWith(_ => _clients.Release(), TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    async Task Handle(TcpClient client, CancellationToken stopping)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        using (var ssl = new SslStream(client.GetStream()))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10)); var cancel = timeout.Token;
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate(), EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, cancel);
                for (var request = 0; request < 100_000 && !cancel.IsCancellationRequested; request++)
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    var a = await ReadFrame(ssl, cancel);
                    JsonObject response;
                    try { response = await Dispatch(a, cancel); }
                    // Never echo exception/request content: it may contain credentials or local paths.
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
                    { response = new JsonObject { ["error"] = e is UnauthorizedAccessException ? "Authorization denied; pair again or ask the PC to authorize this device." : "Request refused: root unavailable, unsafe path, changed source, invalid selection, or access denied. Retry and check PC sharing." }; }
                    await WriteFrame(ssl, response, cancel);
                    if (response.ContainsKey("error")) break;
                }
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or System.Text.Json.JsonException) { }
        }
    }
    async Task<JsonObject> Dispatch(JsonObject a, CancellationToken cancel)
    {
        var op = a["op"]?.GetValue<string>();
        if (op == "pair")
        {
            lock (_gate)
            {
                var invitation = a["invitation"]?.GetValue<string>() ?? "";
                if (_invitation is null || _invitationUntil < DateTime.UtcNow || !Fixed(invitation, _invitation)) throw new UnauthorizedAccessException();
                var id = a["deviceId"]?.GetValue<string>() ?? "";
                if (id.Length is < 16 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid device id");
                var name = a["name"]?.GetValue<string>() ?? "Android";
                if (name.Length > 100) throw new ArgumentException("Invalid device name");
                var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                Devices[id] = new JsonObject { ["name"] = name, ["tokenHash"] = Hash(token) };
                _invitation = null; Save(); Notify();
                return new JsonObject { ["token"] = token, ["protocol"] = 1 };
            }
        }
        var device = a["deviceId"]?.GetValue<string>() ?? "";
        lock (_gate)
            if (Devices[device] is not JsonObject authorized || !Fixed(authorized["tokenHash"]!.GetValue<string>(), Hash(a["token"]?.GetValue<string>() ?? ""))) throw new UnauthorizedAccessException();
        lock (_gate) _lastOperation = op ?? "";
        if (op == "roots") return new JsonObject { ["roots"] = new JsonArray(Diagnostics()["roots"]!.AsArray().Where(n => n!["enabled"]!.GetValue<bool>()).Select(n => n!.DeepClone()).ToArray()) };
        if (op == "state")
        {
            var file = a["state"]!.AsObject();
            if (file["format"]?.GetValue<int>() != 2 || file["device"]?["id"]?.GetValue<string>() != device) throw new UnauthorizedAccessException();
            Directory.CreateDirectory(_inbox);
            SyncFeature.AtomicWrite(Path.Combine(_inbox, device + ".json"), file.ToJsonString());
            if (a["features"] is JsonObject features && features["format"]?.GetValue<int>() == 2)
                SyncFeature.AtomicWrite(Path.Combine(_inbox, device + ".features.json"), features.ToJsonString());
            return await Dispatcher.UIThread.InvokeAsync(() => Merge?.LanExchange() ?? throw new IOException("Sync merge not ready"));
        }
        var rootId = a["rootId"]?.GetValue<string>() ?? "";
        var root = RootPath(rootId);
        if (op == "tree")
        {
            var relative = a["path"]?.GetValue<string>() ?? "";
            return new JsonObject { ["directories"] = new JsonArray(LanFiles.Directories(root, relative).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
        }
        if (op == "plan")
        {
            var selected = a["selection"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            if (selected.Length is < 1 or > 1000) throw new ArgumentException("Invalid selection");
            var entries = await LanFiles.Manifest(root, selected, cancel);
            lock (_gate)
            {
                foreach (var key in _plans.Where(p => p.Value.Device == device || p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToArray()) _plans.Remove(key);
                if (_plans.Count >= 16) throw new IOException("Too many plans");
                var id = Guid.NewGuid().ToString("N"); _plans[id] = (device, rootId, DateTime.UtcNow.AddHours(2), entries);
                return new JsonObject { ["plan"] = id, ["total"] = entries.Count, ["bytes"] = entries.Sum(e => e.Size), ["verification"] = "sha256-all-selected" };
            }
        }
        (string Device, string Root, DateTime Expires, IReadOnlyList<LanFiles.Entry> Files) plan;
        lock (_gate)
            if (!_plans.TryGetValue(a["plan"]?.GetValue<string>() ?? "", out plan) || plan.Device != device || plan.Root != rootId || plan.Expires < DateTime.UtcNow) throw new UnauthorizedAccessException();
        if (op == "manifest")
        {
            var offset = a["offset"]?.GetValue<int>() ?? 0;
            if (offset < 0) throw new ArgumentException("Invalid offset");
            return new JsonObject { ["files"] = new JsonArray(plan.Files.Skip(offset).Take(200).Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["size"] = e.Size, ["modifiedNs"] = e.ModifiedNs, ["hash"] = e.Hash,
                ["dependencies"] = new JsonArray((e.Dependencies ?? []).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) }).ToArray()) };
        }
        if (op == "read")
        {
            var relative = a["path"]?.GetValue<string>() ?? "";
            var entry = plan.Files.FirstOrDefault(e => e.Path == relative) ?? throw new UnauthorizedAccessException();
            var offset = a["offset"]!.GetValue<long>();
            if (offset < 0 || offset > entry.Size) throw new ArgumentException("Invalid offset");
            using var input = LanFiles.OpenRead(root, relative);
            if (input.Length != entry.Size) throw new IOException("Source changed");
            // Fixed, bounded transport chunks only; no custom block-diff algorithm or whole-file allocation.
            input.Position = offset;
            var buffer = new byte[(int)Math.Min(256 * 1024, input.Length - offset)];
            await input.ReadExactlyAsync(buffer, cancel);
            lock (_gate) { _bytesServed += buffer.Length; if (DateTime.UtcNow - _lastNotify > TimeSpan.FromMilliseconds(500)) { _lastNotify = DateTime.UtcNow; Notify(); } }
            return new JsonObject { ["data"] = Convert.ToBase64String(buffer) };
        }
        throw new ArgumentException("Unknown operation");
    }
    internal IEnumerable<JsonObject> Incoming(bool features)
    {
        if (!Directory.Exists(_inbox)) yield break;
        foreach (var path in Directory.EnumerateFiles(_inbox, "*.json").Where(p => p.EndsWith(".features.json") == features))
        {
            JsonObject? file = null;
            try { if (new FileInfo(path).Length <= 32_000_000) file = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch (IOException) { } catch (System.Text.Json.JsonException) { }
            if (file is not null) yield return file;
        }
    }
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    static bool Fixed(string a, string b) => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    internal static async Task<JsonObject> ReadFrame(Stream stream, CancellationToken cancel = default)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, cancel); var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 2 or > 32_000_000) throw new IOException("Invalid frame size");
        var buffer = new byte[length]; await stream.ReadExactlyAsync(buffer, cancel);
        return JsonNode.Parse(buffer)?.AsObject() ?? throw new IOException("Invalid frame");
    }
    internal static async Task WriteFrame(Stream stream, JsonObject value, CancellationToken cancel = default)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value.ToJsonString());
        if (bytes.Length > 32_000_000) throw new IOException("Response exceeds protocol frame limit");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancel); await stream.WriteAsync(bytes, cancel); await stream.FlushAsync(cancel);
    }
    public void Dispose() { _stop?.Cancel(); _listener?.Stop(); _certificate?.Dispose(); }
}

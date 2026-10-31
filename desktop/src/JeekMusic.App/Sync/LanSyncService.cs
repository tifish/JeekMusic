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
using JeekMusic.Library;

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
        // Pre-release folderId-only grants cannot establish which record originally owned the ID.
        foreach (var r in Roots.Select(p => p.Value).OfType<JsonObject>())
            if (r["folderIdentity"] is null) r["enabled"] = false;
        app.Library.SnapshotChanged += ReconcileRoots;
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
    public void Start(int? port = null)
    {
        lock (_gate)
        {
            if (_listener is not null) return;
            try
            {
                Certificate(); // Only provision a private key when the user enables LAN sharing.
                ReconcileRoots(shareAll: true);
                _listener = new TcpListener(IPAddress.Any, port ?? _config["port"]?.GetValue<int>() ?? 39843); _listener.Start(8);
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
        if (!enabled) throw new InvalidOperationException("Per-folder unshare is no longer supported. All registered library folders are shared while LAN service is enabled. Stop the LAN service to disable sharing.");
        var folder = _app.Db.Folders().FirstOrDefault(f => f.Id == folderId) ?? throw new ArgumentException("Unknown library folder");
        if (Port == 0) throw new InvalidOperationException("LAN service is disabled. Start it to share all registered library folders; per-folder sharing is no longer supported.");
        ReconcileRoots();
        lock (_gate)
        {
            var root = Roots.FirstOrDefault(p => p.Value?["folderIdentity"]?.GetValue<string>() == folder.Identity && p.Value?["revoked"]?.GetValue<bool>() != true);
            if (root.Key is null) throw new InvalidOperationException("Library root identity is unavailable");
            return new JsonObject { ["rootId"] = root.Key, ["enabled"] = true, ["sharingMode"] = "library", ["deprecated"] = true };
        }
    }
    public string? Reference(long folderId, string relative)
    {
        lock (_gate)
        {
            var identity = _app.Library.Snapshot.Folder(folderId)?.Identity;
            var root = Roots.FirstOrDefault(p => identity is { Length: > 0 } && p.Value?["folderIdentity"]?.GetValue<string>() == identity && p.Value?["revoked"]?.GetValue<bool>() != true);
            return root.Key is null ? null : "root:" + root.Key + "/" + relative.Replace('\\', '/');
        }
    }
    public string Invite(string host)
    {
        if (Port == 0) throw new InvalidOperationException("LAN service is disabled. Enable it to pair and automatically share registered music folders.");
        if (host.Length > 253 || !Uri.CheckHostName(host).Equals(UriHostNameType.IPv4) && !Uri.CheckHostName(host).Equals(UriHostNameType.Dns)) throw new ArgumentException("Enter LAN IPv4 address or hostname");
        lock (_gate)
        {
            _invitation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _invitationUntil = DateTime.UtcNow.AddMinutes(5);
            Notify();
            return $"jeeklan://{host}:{Port}/{Fingerprint}/{_invitation}";
        }
    }
    public bool IsInvitationCurrent(string value)
    {
        lock (_gate) return Port != 0 && _invitation is not null && DateTime.UtcNow < _invitationUntil && value.EndsWith("/" + _invitation, StringComparison.Ordinal);
    }
    public void Revoke(string id) { lock (_gate) { Devices.Remove(id); _plans.Clear(); Save(); } Notify(); }
    public JsonObject Diagnostics()
    {
        ReconcileRoots();
        var folders = _app.Db.Folders();
        lock (_gate) return new JsonObject
        {
            ["enabled"] = Port != 0, ["port"] = Port, ["problem"] = Problem, ["protocol"] = 1, ["invitationActive"] = _invitation is not null && DateTime.UtcNow < _invitationUntil,
            ["lastOperation"] = _lastOperation, ["bytesServed"] = _bytesServed,
            ["sharingMode"] = "library", ["libraryFolderCount"] = folders.Count,
            ["sharedFolderCount"] = Port == 0 ? 0 : Roots.Count(p => p.Value?["enabled"]?.GetValue<bool>() == true && p.Value?["revoked"]?.GetValue<bool>() != true),
            ["addresses"] = new JsonArray(NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => (JsonNode?)JsonValue.Create(a.Address.ToString())).ToArray()),
            ["roots"] = new JsonArray(Roots.Select(p => { var f = folders.FirstOrDefault(f => f.Id == p.Value!["folderId"]!.GetValue<long>() && f.Identity == p.Value["folderIdentity"]?.GetValue<string>()); return (JsonNode)new JsonObject
                { ["rootId"] = p.Key, ["folderId"] = p.Value!["folderId"]!.DeepClone(), ["name"] = f?.DisplayName ?? p.Value["name"]?.GetValue<string>(), ["revoked"] = p.Value["revoked"]?.GetValue<bool>() ?? false, ["enabled"] = p.Value["enabled"]!.DeepClone(), ["available"] = f is { Available: true } && Directory.Exists(f.Root) }; }).ToArray()),
            ["devices"] = new JsonArray(Devices.Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["name"] = p.Value!["name"]!.DeepClone() }).ToArray())
        };
    }
    string RootPath(string id)
    {
        ReconcileRoots();
        lock (_gate)
        {
            if (Roots[id] is not JsonObject root || root["enabled"]?.GetValue<bool>() != true) throw new IOException("Root is not shared");
            var folder = _app.Db.Folders().FirstOrDefault(f => f.Id == root["folderId"]!.GetValue<long>());
            if (folder is not { Available: true } || folder.Identity != root["folderIdentity"]?.GetValue<string>() || !Directory.Exists(folder.Root)) throw new IOException("Root is offline or authorization expired; no deletions inferred");
            return LanFiles.Resolve(folder.Root, "", true);
        }
    }
    void ReconcileRoots() => ReconcileRoots(Port != 0);
    void ReconcileRoots(bool shareAll)
    {
        // Use persisted records, not an initially empty/loading UI snapshot. Removal is permanent
        // revocation, while an unavailable disk leaves its record/identity intact.
        var folders = _app.Db.Folders();
        lock (_gate)
        {
            if (ReconcileRegisteredRoots(Roots, folders, shareAll))
            { _plans.Clear(); RootRevision++; Save(); Notify(); }
        }
    }
    // Identity, never a reusable row ID, owns the network namespace. Legacy ID-only and
    // revoked grants remain tombstones; migration must allocate a fresh namespace for them.
    internal static bool ReconcileRegisteredRoots(JsonObject roots, IReadOnlyList<LibraryFolder> folders, bool shareAll)
    {
        var identities = folders.Select(f => f.Identity).Where(i => !string.IsNullOrEmpty(i)).ToHashSet();
        bool changed = false;
        foreach (var root in roots.Select(p => p.Value).OfType<JsonObject>())
            if (root["folderIdentity"]?.GetValue<string>() is not { Length: > 0 } identity || !identities.Contains(identity) || root["revoked"]?.GetValue<bool>() == true)
            {
                if (root["enabled"]?.GetValue<bool>() != false || root["revoked"]?.GetValue<bool>() != true)
                { root["enabled"] = false; root["revoked"] = true; changed = true; }
            }
        if (!shareAll) return changed;
        foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f.Identity)))
        {
            var existing = roots.FirstOrDefault(p => p.Value?["folderIdentity"]?.GetValue<string>() == folder.Identity && p.Value?["revoked"]?.GetValue<bool>() != true);
            if (existing.Value is JsonObject root)
            {
                if (root["enabled"]?.GetValue<bool>() != true || root["folderId"]?.GetValue<long>() != folder.Id || root["name"]?.GetValue<string>() != folder.DisplayName)
                { root["enabled"] = true; root["folderId"] = folder.Id; root["name"] = folder.DisplayName; changed = true; }
            }
            else
            {
                roots[Guid.NewGuid().ToString("N")] = new JsonObject { ["folderId"] = folder.Id, ["folderIdentity"] = folder.Identity, ["name"] = folder.DisplayName, ["enabled"] = true };
                changed = true;
            }
        }
        return changed;
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
                    { response = new JsonObject { ["code"] = e is UnauthorizedAccessException ? (a["op"]?.GetValue<string>() == "pair" ? "invitation_rejected" : "unauthorized") : "request_refused", ["error"] = e is UnauthorizedAccessException ? "Authorization denied; pair again or ask the PC to authorize this device." : "Request refused: root unavailable, unsafe path, changed source, invalid selection, or access denied. Retry and check PC sharing." }; }
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
                return new JsonObject { ["token"] = token, ["protocol"] = 1, ["name"] = Environment.MachineName };
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
            if (SyncMerge.ParseFile(file.ToJsonString()) is null) throw new ArgumentException("Invalid sync state");
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
    internal IEnumerable<JsonObject> Incoming(bool features) => ReadIncoming(_inbox, features);

    internal static IEnumerable<JsonObject> ReadIncoming(string directory, bool features)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Where(p => p.EndsWith(".features.json") == features))
        {
            JsonObject? file = null;
            try
            {
                if (new FileInfo(path).Length <= 32_000_000)
                {
                    var text = File.ReadAllText(path);
                    file = features ? JsonNode.Parse(text) as JsonObject : SyncMerge.ParseFile(text);
                }
            }
            catch (IOException) { } catch (System.Text.Json.JsonException) { }
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
    public void Dispose() { _app.Library.SnapshotChanged -= ReconcileRoots; _stop?.Cancel(); _listener?.Stop(); _certificate?.Dispose(); }
}

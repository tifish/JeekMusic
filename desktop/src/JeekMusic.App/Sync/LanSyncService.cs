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
    readonly LanAdmission _admission = new();
    readonly SemaphoreSlim _largeFrames = new(2);
    readonly SemaphoreSlim _work = new(2);
    readonly SemaphoreSlim _planWork = new(1);
    readonly Dictionary<string, CancellationTokenSource> _deviceLifetimes = new();
    readonly Dictionary<string, (DateTime Window, int Count, DateTime Plan, int Plans, DateTime Write, DateTime Learn)> _rates = new();
    readonly Dictionary<string, PlanJob> _jobs = new();
    sealed class PlanJob(string device, CancellationTokenSource cancel)
    {
        public string Device = device;
        public CancellationTokenSource Cancel = cancel;
        public Task<JsonObject> Task = null!;
        public DateTime Created = DateTime.UtcNow;
    }
    readonly Dictionary<string, (DateTime Window, long Bytes)> _transfers = new();
    DateTime _transferWindow;
    long _transferBytes;
    long _authFailures, _protocolFailures, _limited, _overload;
    JsonObject _invitePermissions = Permissions();
    int _inviteDays = 90;
    static JsonObject Permissions(bool write = false, bool learn = false, bool download = false) => new() { ["readMusic"] = true, ["writeSync"] = write, ["manageLearning"] = learn, ["downloadLearning"] = download };
    static string Stamp() => Guid.NewGuid().ToString("N");
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
    DateTime _lastNotify, _globalWindow;
    int _globalRequests;
    public SyncFeature? Merge { get; set; }
    public string Problem { get; private set; } = "";
    public event Action? Changed;
    public int RootRevision { get; private set; }
    int _rootAuthorizationRevision;
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Certificate().RawData));
    JsonObject Roots => _config["roots"]!.AsObject();
    JsonObject Devices => _config["devices"]!.AsObject();

    public LanSyncService(AppServices app)
    {
        _app = app;
        try { _config = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { _config = new(); }
        _config["bindAddress"] ??= _config["port"] is null ? "127.0.0.1" : "0.0.0.0";
        _config["roots"] ??= new JsonObject(); _config["devices"] ??= new JsonObject();
        // Preserve existing media/data peers. Remote compute/download now needs local opt-in.
        foreach (var d in Devices.Select(p => p.Value).OfType<JsonObject>())
        {
            if (d["permissions"] is null) { d["permissions"] = Permissions(write: true); d["permissionMigration"] = "v4-read-write-learning-requires-approval"; }
            d["generation"] ??= Stamp();
            d["expiresAt"] ??= DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeSeconds();
        }
        Save();
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
                var bind = IPAddress.Parse(_config["bindAddress"]?.GetValue<string>() ?? "0.0.0.0");
                _listener = new TcpListener(bind, port ?? _config["port"]?.GetValue<int>() ?? 39843); _listener.Start(32);
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
        lock (_gate) { InvalidateAll(); _stop?.Cancel(); _listener?.Stop(); _listener = null; _invitation = null; _plans.Clear(); _config["enabled"] = false; Save(); }
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
    public string Invite(string host, int? advertisedPort = null, JsonObject? permissions = null, int credentialDays = 90)
    {
        if (Port == 0) throw new InvalidOperationException("LAN service is disabled. Enable it to pair and automatically share registered music folders.");
        if (host.Length > 253 || !Uri.CheckHostName(host).Equals(UriHostNameType.IPv4) && !Uri.CheckHostName(host).Equals(UriHostNameType.Dns)) throw new ArgumentException("Enter LAN IPv4 address or hostname");
        if (advertisedPort is < 1 or > 65535 || credentialDays is < 1 or > 365) throw new ArgumentException("Invalid endpoint or lifetime");
        lock (_gate)
        {
            _invitePermissions = ValidatePermissions(permissions ?? Permissions()); _inviteDays = credentialDays;
            _invitation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _invitationUntil = DateTime.UtcNow.AddMinutes(5);
            Notify();
            return $"jeeklan://{host}:{advertisedPort ?? Port}/{Fingerprint}/{_invitation}";
        }
    }
    public bool IsInvitationCurrent(string value)
    {
        lock (_gate) return Port != 0 && _invitation is not null && DateTime.UtcNow < _invitationUntil && value.EndsWith("/" + _invitation, StringComparison.Ordinal);
    }
    public void Revoke(string id) { lock (_gate) { Invalidate(id); Devices.Remove(id); _rates.Remove(id); _transfers.Remove(id); Save(); } Notify(); }
    static JsonObject ValidatePermissions(JsonObject permissions)
    {
        if (permissions.Any(p => p.Key is not ("readMusic" or "writeSync" or "manageLearning" or "downloadLearning"))) throw new ArgumentException("Unknown permission");
        var result = Permissions();
        foreach (var key in result.Select(p => p.Key).ToArray()) result[key] = permissions[key]?.GetValue<bool>() ?? false;
        if (result["downloadLearning"]!.GetValue<bool>() && !result["manageLearning"]!.GetValue<bool>()) throw new ArgumentException("Downloads require learning permission");
        return result;
    }
    public void Authorize(string id, JsonObject permissions, int credentialDays = 90)
    {
        if (credentialDays is < 1 or > 365) throw new ArgumentException("Lifetime must be 1–365 days");
        var grants = ValidatePermissions(permissions);
        lock (_gate)
        {
            var d = Devices[id] as JsonObject ?? throw new ArgumentException("Unknown device");
            Invalidate(id); d["permissions"] = grants; d["generation"] = Stamp();
            d["expiresAt"] = DateTimeOffset.UtcNow.AddDays(credentialDays).ToUnixTimeSeconds(); Save();
        }
        Notify();
    }
    public void Configure(string bindAddress, int port)
    {
        if (!IPAddress.TryParse(bindAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || port is < 1 or > 65535 || (!ip.Equals(IPAddress.Any) && (ip.GetAddressBytes()[0] == 0 || ip.GetAddressBytes()[0] >= 224)) || ip.Equals(IPAddress.Broadcast)) throw new ArgumentException("IPv4 bind and port 1–65535 required");
        lock (_gate)
        {
            if (Port != 0) throw new InvalidOperationException("Stop service before configuring listener");
            _config["bindAddress"] = ip.ToString(); _config["port"] = port; Save();
        }
        Notify();
    }
    void Invalidate(string id)
    {
        if (_deviceLifetimes.Remove(id, out var lifetime)) { lifetime.Cancel(); lifetime.Dispose(); }
        foreach (var key in _plans.Where(p => p.Value.Device == id).Select(p => p.Key).ToArray()) _plans.Remove(key);
        foreach (var key in _jobs.Where(p => p.Value.Device == id).Select(p => p.Key).ToArray()) { RetireJob(_jobs[key]); _jobs.Remove(key); }
        _app.Preferences.Training.CancelOwned("lan:" + id);
    }
    static void RetireJob(PlanJob job)
    {
        if (job.Task.IsCompleted) job.Cancel.Dispose();
        else { job.Cancel.Cancel(); _ = job.Task.ContinueWith(_ => job.Cancel.Dispose(), TaskScheduler.Default); }
    }
    void InvalidateAll() { foreach (var id in Devices.Select(p => p.Key).ToArray()) Invalidate(id); }
    (string Device, string Generation, CancellationToken Lifetime) Authenticate(JsonObject a)
    {
        var id = a["deviceId"]?.GetValue<string>() ?? "";
        lock (_gate)
        {
            if (Devices[id] is not JsonObject d || !Fixed(d["tokenHash"]!.GetValue<string>(), Hash(a["token"]?.GetValue<string>() ?? "")) || d["expiresAt"]!.GetValue<long>() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            { if (Devices[id] is not null && Devices[id]!["expiresAt"]!.GetValue<long>() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) Invalidate(id); throw new UnauthorizedAccessException(); }
            if (_deviceLifetimes.TryGetValue(id, out var old) && old.IsCancellationRequested) { _deviceLifetimes.Remove(id); old.Dispose(); }
            if (!_deviceLifetimes.TryGetValue(id, out var lifetime))
            {
                _deviceLifetimes[id] = lifetime = new();
                lifetime.Token.Register(() => _app.Preferences.Training.CancelOwned("lan:" + id));
                lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(d["expiresAt"]!.GetValue<long>() - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, 6 * 3600)));
            }
            return (id, d["generation"]!.GetValue<string>(), lifetime.Token);
        }
    }
    void Require(string device, string permission)
    {
        if (Devices[device]?["permissions"]?[permission]?.GetValue<bool>() != true) throw new UnauthorizedAccessException();
    }
    void RequireOperation(string device, string? operation)
    {
        if (operation == "capabilities") return;
        if (operation is "state" or "preference_delta") Require(device, "writeSync");
        else if (operation is "preference_training" or "preference_results")
        {
            if (Devices[device]?["permissions"]?["readMusic"]?.GetValue<bool>() != true && Devices[device]?["permissions"]?["manageLearning"]?.GetValue<bool>() != true) throw new UnauthorizedAccessException();
        }
        else Require(device, "readMusic");
    }
    public JsonObject CancelTask(string id, string? device = null)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || device is not null && job.Device != device) throw new UnauthorizedAccessException();
            if (!job.Task.IsCompleted) job.Cancel.Cancel();
            Notify(); return new JsonObject { ["state"] = job.Cancel.IsCancellationRequested ? "cancelled" : "complete" };
        }
    }
    void Rate(string device, string? op)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow; var r = _rates.GetValueOrDefault(device);
            if ((now - r.Window).TotalSeconds >= 1) { r.Window = now; r.Count = 0; }
            if ((now - _globalWindow).TotalSeconds >= 1) { _globalWindow = now; _globalRequests = 0; }
            bool limited = ++r.Count > 80 || ++_globalRequests > 320;
            if (op == "plan")
            {
                if ((now - r.Plan).TotalMinutes >= 1) { r.Plan = now; r.Plans = 0; }
                limited |= ++r.Plans > 4;
            }
            if (op is "state" or "preference_delta") { limited |= (now - r.Write).TotalMilliseconds < 500; if (!limited) r.Write = now; }
            _rates[device] = r;
            if (limited) { Interlocked.Increment(ref _limited); throw Busy(); }
        }
    }
    void TransferBudget(string device, int bytes)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow; var rate = _transfers.GetValueOrDefault(device);
            if ((now - rate.Window).TotalSeconds >= 1) rate = (now, 0);
            if ((now - _transferWindow).TotalSeconds >= 1) { _transferWindow = now; _transferBytes = 0; }
            if (rate.Bytes + bytes > 16 * 1024 * 1024 || _transferBytes + bytes > 64 * 1024 * 1024) { Interlocked.Increment(ref _limited); throw Busy(); }
            rate.Bytes += bytes; _transferBytes += bytes; _transfers[device] = rate;
        }
    }
    LanBusyException Busy() { Interlocked.Increment(ref _overload); return new(); }
    JsonObject BusyResponse() { Interlocked.Increment(ref _overload); return Error("busy"); }
    sealed class LanBusyException : IOException { }

    public JsonObject Diagnostics()
    {
        ReconcileRoots();
        var folders = _app.Db.Folders();
        lock (_gate) return new JsonObject
        {
            ["enabled"] = Port != 0, ["port"] = Port, ["problem"] = Problem, ["protocol"] = 4, ["legacySmallFrames"] = true,
            ["bindAddress"] = _config["bindAddress"]?.GetValue<string>() ?? "0.0.0.0", ["configuredPort"] = _config["port"]?.GetValue<int>() ?? 39843,
            ["activeConnections"] = _admission.Active, ["unauthenticatedConnections"] = _admission.Pending,
            ["sourceBuckets"] = _admission.SourceCount, ["connectionRejected"] = _admission.Rejected,
            ["authenticationFailures"] = Interlocked.Read(ref _authFailures), ["protocolFailures"] = Interlocked.Read(ref _protocolFailures), ["rateLimited"] = Interlocked.Read(ref _limited), ["overloadRejected"] = Interlocked.Read(ref _overload),
            ["budgets"] = new JsonObject { ["handshakeSeconds"] = 5, ["authenticationSeconds"] = 5, ["requestSeconds"] = 30, ["writeSeconds"] = 15, ["connections"] = 24, ["unauthenticated"] = 8, ["authenticated"] = 16, ["largeFrames"] = 2, ["expensiveWork"] = 2, ["planConcurrency"] = 1, ["deviceRequestsPerSecond"] = 80, ["globalRequestsPerSecond"] = 320, ["plansPerDevicePerMinute"] = 4, ["deviceReadBytesPerSecond"] = 16 * 1024 * 1024, ["globalReadBytesPerSecond"] = 64 * 1024 * 1024, ["authenticationBytes"] = LanLimits.AuthFrame, ["smallFrameBytes"] = LanLimits.SmallFrame, ["largeFrameBytes"] = LanLimits.LargeFrame, ["planFiles"] = 100000, ["planBytes"] = 500L * 1024 * 1024 * 1024, ["planSeconds"] = 1800 },
            ["learningOwner"] = _app.Preferences.Training.RemoteOwner,
            ["tasks"] = new JsonArray(_jobs.Select(j => (JsonNode)new JsonObject { ["id"] = j.Key, ["deviceId"] = j.Value.Device, ["state"] = j.Value.Cancel.IsCancellationRequested ? "cancelled" : j.Value.Task.IsFaulted || j.Value.Task.IsCompletedSuccessfully && j.Value.Task.Result.ContainsKey("error") ? "failed" : j.Value.Task.IsCompleted ? "complete" : "running" }).ToArray()),
            ["invitationActive"] = _invitation is not null && DateTime.UtcNow < _invitationUntil,
            ["lastOperation"] = _lastOperation, ["bytesServed"] = _bytesServed,
            ["sharingMode"] = "library", ["libraryFolderCount"] = folders.Count,
            ["sharedFolderCount"] = Port == 0 ? 0 : Roots.Count(p => p.Value?["enabled"]?.GetValue<bool>() == true && p.Value?["revoked"]?.GetValue<bool>() != true),
            ["addresses"] = new JsonArray(NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => (JsonNode?)JsonValue.Create(a.Address.ToString())).ToArray()),
            ["roots"] = new JsonArray(Roots.Select(p => { var f = folders.FirstOrDefault(f => f.Id == p.Value!["folderId"]!.GetValue<long>() && f.Identity == p.Value["folderIdentity"]?.GetValue<string>()); return (JsonNode)new JsonObject
                { ["rootId"] = p.Key, ["folderId"] = p.Value!["folderId"]!.DeepClone(), ["name"] = f?.DisplayName ?? p.Value["name"]?.GetValue<string>(), ["revoked"] = p.Value["revoked"]?.GetValue<bool>() ?? false, ["enabled"] = p.Value["enabled"]!.DeepClone(), ["available"] = f is { Available: true } && Directory.Exists(f.Root) }; }).ToArray()),
            ["devices"] = new JsonArray(Devices.Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["name"] = p.Value!["name"]!.DeepClone(), ["permissions"] = p.Value["permissions"]!.DeepClone(), ["expiresAt"] = p.Value["expiresAt"]!.DeepClone(), ["migration"] = p.Value["permissionMigration"]?.DeepClone() }).ToArray())
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
            var previouslyShared = Roots.Where(p => p.Value?["enabled"]?.GetValue<bool>() == true && p.Value?["revoked"]?.GetValue<bool>() != true).Select(p => p.Key).ToArray();
            if (ReconcileRegisteredRoots(Roots, folders, shareAll))
            {
                if (previouslyShared.Any(id => Roots[id]?["enabled"]?.GetValue<bool>() != true || Roots[id]?["revoked"]?.GetValue<bool>() == true))
                { InvalidateAll(); _plans.Clear(); _rootAuthorizationRevision++; }
                RootRevision++; Save(); Notify();
            }
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
                var lease = _admission.Enter(((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(), DateTime.UtcNow);
                if (lease is null) { client.Dispose(); continue; }
                _ = Task.Run(async () => { try { await Handle(client, lease, cancel); } finally { _admission.Exit(lease); } });
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    static JsonObject Error(string code) => new() { ["code"] = code, ["error"] = code switch
    {
        "unauthorized" or "invitation_rejected" => "Authorization denied; ask the PC administrator to authorize or pair this device.",
        "upgrade_required" => "Upgrade both clients for authenticated large frames and asynchronous plans.",
        "busy" => "Resource budget reached. Retry after 30 seconds or select smaller folders.",
        _ => "Request refused. Check authorization, source availability and selection on the PC."
    }};
    async Task Handle(TcpClient client, LanAdmission.Lease lease, CancellationToken stopping)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        using (var ssl = new SslStream(client.GetStream()))
        {
            using var abort = lifetime.Token.Register(client.Dispose);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate(), EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var first = await ReadFrame(ssl, deadline.Token, LanLimits.AuthFrame, authentication: true);
                if (first["op"]?.GetValue<string>() == "pair")
                {
                    var paired = await Dispatch(first, deadline.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15)); await WriteFrame(ssl, paired, deadline.Token); return;
                }
                var auth = Authenticate(first);
                if (!_admission.Authenticate(lease, auth.Device)) { await WriteFrame(ssl, BusyResponse(), deadline.Token); return; }
                using var revoked = auth.Lifetime.Register(lifetime.Cancel);
                var modern = first["op"]?.GetValue<string>() == "auth" && first["protocol"]?.GetValue<int>() == 4;
                if (first["op"]?.GetValue<string>() == "auth" && !modern) { await WriteFrame(ssl, Error("upgrade_required"), deadline.Token); return; }
                JsonObject? pending = modern ? null : first;
                if (modern) await WriteFrame(ssl, new JsonObject { ["protocol"] = 4, ["authenticated"] = true }, deadline.Token);
                for (var request = 0; request < 100_000 && !lifetime.IsCancellationRequested; request++)
                {
                    bool large = false;
                    try
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(30));
                        string? kind = null;
                        var a = pending; pending = null;
                        if (a is null)
                        {
                            if (modern)
                            {
                                var header = await ReadFrame(ssl, deadline.Token, LanLimits.AuthFrame, authentication: true);
                                kind = header["kind"]?.GetValue<string>();
                                var length = header["length"]?.GetValue<int>() ?? 0;
                                if (header["op"]?.GetValue<string>() != "request" || length < 2 || length > LanLimits.Request(kind)) throw new IOException("Invalid request header");
                                lock (_gate) RequireOperation(auth.Device, kind); // Deny unauthorized work before body allocation or acknowledgement.
                                if (length > LanLimits.SmallFrame || kind is "state" or "preference_delta")
                                {
                                    large = _largeFrames.Wait(0);
                                    if (!large) { await WriteFrame(ssl, BusyResponse(), deadline.Token); return; }
                                }
                                await WriteFrame(ssl, new JsonObject { ["ready"] = true }, deadline.Token);
                                a = await ReadFrame(ssl, deadline.Token, length);
                                if (a["op"]?.GetValue<string>() != kind) throw new IOException("Operation mismatch");
                            }
                            else a = await ReadFrame(ssl, deadline.Token, LanLimits.SmallFrame);
                        }
                        if (!modern && a["op"]?.GetValue<string>() is "auth" or "pair") throw new UnauthorizedAccessException();
                        var current = Authenticate(a);
                        if (current.Device != auth.Device || current.Generation != auth.Generation) throw new UnauthorizedAccessException();
                        kind = a["op"]?.GetValue<string>();
                        if (!large && kind is "state" or "preference_delta") { large = _largeFrames.Wait(0); if (!large) throw Busy(); }
                        Rate(auth.Device, kind);
                        deadline.CancelAfter(TimeSpan.FromSeconds(kind == "plan" && !modern ? 90 : 30));
                        var response = await Dispatch(a, deadline.Token, modern);
                        lifetime.Token.ThrowIfCancellationRequested();
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        await WriteFrame(ssl, response, deadline.Token, LanLimits.Response(kind));
                    }
                    finally { if (large) _largeFrames.Release(); }
                }
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or System.Text.Json.JsonException or FormatException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException or OverflowException)
            {
                Interlocked.Increment(ref _protocolFailures);
                if (e is UnauthorizedAccessException) Interlocked.Increment(ref _authFailures);
                try
                {
                    using var errorDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); errorDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                    await WriteFrame(ssl, Error(e is UnauthorizedAccessException ? "unauthorized" : e is LanBusyException ? "busy" : e is FrameTooLargeException ? "upgrade_required" : "request_refused"), errorDeadline.Token);
                }
                catch (Exception ignored) when (ignored is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or SocketException) { }
            }
        }
    }
    async Task<JsonObject> Dispatch(JsonObject a, CancellationToken cancel, bool modern = false)
    {
        var op = a["op"]?.GetValue<string>();
        if (op == "pair")
        {
            lock (_gate)
            {
                var invitation = a["invitation"]?.GetValue<string>() ?? "";
                if (_invitation is null || _invitationUntil < DateTime.UtcNow || !Fixed(invitation, _invitation)) { throw new UnauthorizedAccessException(); }
                var id = a["deviceId"]?.GetValue<string>() ?? "";
                if (id.Length is < 16 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid device id");
                var name = a["name"]?.GetValue<string>() ?? "Android";
                if (name.Length > 100) throw new ArgumentException("Invalid device name");
                var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                if (Devices[id] is null && Devices.Count >= 64) throw Busy();
                Invalidate(id);
                Devices[id] = new JsonObject { ["name"] = name, ["tokenHash"] = Hash(token), ["generation"] = Stamp(), ["permissions"] = _invitePermissions.DeepClone(), ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(_inviteDays).ToUnixTimeSeconds() };
                _invitation = null; Save(); Notify();
                return new JsonObject { ["token"] = token, ["protocol"] = 4, ["name"] = Environment.MachineName };
            }
        }
        cancel.ThrowIfCancellationRequested();
        var device = Authenticate(a).Device;
        if (op == "capabilities") { lock (_gate) return new JsonObject { ["protocol"] = 4, ["permissions"] = Devices[device]!["permissions"]!.DeepClone(), ["expiresAt"] = Devices[device]!["expiresAt"]!.DeepClone() }; }
        lock (_gate)
        {
            RequireOperation(device, op);
        }
        if (op is not ("roots" or "state" or "preference_training" or "preference_results" or "preference_delta" or "tree" or "plan" or "plan_status" or "plan_cancel" or "manifest" or "read")) throw new ArgumentException("Unknown operation");
        lock (_gate) _lastOperation = op;
        if (op == "roots") return new JsonObject { ["roots"] = new JsonArray(Diagnostics()["roots"]!.AsArray().Where(n => n!["enabled"]!.GetValue<bool>()).Select(n => n!.DeepClone()).ToArray()) };
        if (op == "state")
        {
            var file = a["state"] as JsonObject ?? throw new ArgumentException("Missing state");
            if (file["format"]?.GetValue<int>() != 2 || file["device"]?["id"]?.GetValue<string>() != device) throw new UnauthorizedAccessException();
            if (SyncMerge.ParseFile(file.ToJsonString()) is null) throw new ArgumentException("Invalid sync state");
            // Compatibility format-2 transport calls the domain merge directly; no disk inbox or directory writes.
            if (!_work.Wait(0)) throw Busy();
            try { return await Dispatcher.UIThread.InvokeAsync(() => { lock (_gate) { cancel.ThrowIfCancellationRequested(); Authenticate(a); Require(device, "writeSync"); return Merge?.LanExchange(file, a["features"] as JsonObject, a["includeFeatures"]?.GetValue<bool>()??true, cancel, commit => { lock (_gate) { cancel.ThrowIfCancellationRequested(); Authenticate(a); Require(device, "writeSync"); commit(); } }) ?? throw new IOException("Sync merge not ready"); } }); }
            finally { _work.Release(); }
        }
        if (op is "preference_training" or "preference_results")
        {
            if(a["protocol"]?.GetValue<int>()!=3) throw new ArgumentException("Learning sync requires both devices to upgrade");
            var training=_app.Preferences.Training;
            if(op=="preference_results")
            {
                if (!_work.Wait(0)) throw Busy();
                try { cancel.ThrowIfCancellationRequested(); return training.ResultPage(a["offset"]?.GetValue<int>()??0,a["version"]?.GetValue<string>(),cancel); }
                finally { _work.Release(); }
            }
            var action=a["action"]?.GetValue<string>()??"status";
            if(action is "learn" or "resume" or "cancel")
            {
                lock (_gate)
                {
                    cancel.ThrowIfCancellationRequested(); Authenticate(a); Require(device, "manageLearning");
                    if (action == "cancel") { training.CancelOwned("lan:" + device); return training.LearningStatus(); }
                    var r = _rates.GetValueOrDefault(device); var now = DateTime.UtcNow;
                    if ((now - r.Learn).TotalMinutes < 1) throw Busy();
                    r.Learn = now; _rates[device] = r;
                    var download = a["downloadConfirmed"]?.GetValue<bool>() == true;
                    if (download) Require(device, "downloadLearning");
                    return training.StartLearning(download, owner: "lan:" + device, allowDownload: Devices[device]!["permissions"]!["downloadLearning"]!.GetValue<bool>());
                }
            }
            if(action!="status") throw new ArgumentException("Unsupported remote learning action");
            if (!_work.Wait(0)) throw Busy();
            try { cancel.ThrowIfCancellationRequested(); return _app.Preferences.Status(); }
            finally { _work.Release(); }
        }
        if (op == "preference_delta")
        {
            if (!_work.Wait(0)) throw Busy();
            try { lock (_gate) { cancel.ThrowIfCancellationRequested(); Authenticate(a); Require(device, "writeSync"); if (a["page"] is not JsonObject) throw new ArgumentException("Missing delta page"); return _app.Preferences.Delta(a); } }
            finally { _work.Release(); }
        }
        if (op is "plan_status" or "plan_cancel")
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(a["job"]?.GetValue<string>() ?? "", out var job) || job.Device != device) throw new UnauthorizedAccessException();
                if (op == "plan_cancel") return CancelTask(a["job"]!.GetValue<string>(), device);
                if (!job.Task.IsCompleted) return new JsonObject { ["pending"] = true, ["job"] = a["job"]!.DeepClone() };
                return job.Task.GetAwaiter().GetResult().DeepClone().AsObject();
            }
        }
        var rootId = a["rootId"]?.GetValue<string>() ?? "";
        var root = RootPath(rootId);
        if (op == "tree")
        {
            var relative = a["path"]?.GetValue<string>() ?? "";
            if (!_work.Wait(0)) throw Busy();
            try { return new JsonObject { ["pageSize"] = modern ? 200 : 100_000, ["directories"] = new JsonArray(LanFiles.Directories(root, relative, cancel).Skip(Math.Max(0, a["offset"]?.GetValue<int>() ?? 0)).Take(modern ? 200 : 100_000).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) }; } finally { _work.Release(); }
        }
        if (op == "plan")
        {
            var selected = (a["selection"] as JsonArray ?? throw new ArgumentException("Missing selection")).Select(n => n!.GetValue<string>()).ToArray();
            if (selected.Length is < 1 or > 1000) throw new ArgumentException("Invalid selection");
            if (!_planWork.Wait(0)) throw Busy();
            CancellationTokenSource workCancel;
            try
            {
            lock (_gate)
            {
                cancel.ThrowIfCancellationRequested();
                foreach (var key in _jobs.Where(j => j.Value.Task.IsCompleted && (j.Value.Device == device || (DateTime.UtcNow - j.Value.Created).TotalHours > 2)).Select(j => j.Key).ToArray()) { RetireJob(_jobs[key]); _jobs.Remove(key); }
                if (_jobs.Values.Any(j => j.Device == device && !j.Task.IsCompleted) || _jobs.Count >= 64) throw Busy();
                workCancel = CancellationTokenSource.CreateLinkedTokenSource(Authenticate(a).Lifetime, _stop!.Token);
            }
            } catch { _planWork.Release(); throw; }
            workCancel.CancelAfter(TimeSpan.FromMinutes(modern ? 30 : 1.5));
            var revision = _rootAuthorizationRevision;
            async Task<JsonObject> BuildPlan()
            {
                try
                {
                    var entries = await LanFiles.Manifest(root, selected, workCancel.Token);
                    lock (_gate)
                    {
                        workCancel.Token.ThrowIfCancellationRequested(); Authenticate(a); Require(device, "readMusic");
                        if (revision != _rootAuthorizationRevision || RootPath(rootId) != root) throw new UnauthorizedAccessException();
                        foreach (var key in _plans.Where(p => p.Value.Device == device || p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToArray()) _plans.Remove(key);
                        if (_plans.Count >= 16 || _plans.Values.Sum(p => p.Files.Count) + entries.Count > 200_000 || _plans.Values.Sum(p => ManifestMemory(p.Files)) + ManifestMemory(entries) > 128L * 1024 * 1024) throw Busy();
                        var id = Stamp(); _plans[id] = (device, rootId, DateTime.UtcNow.AddHours(2), entries);
                        return new JsonObject { ["plan"] = id, ["total"] = entries.Count, ["bytes"] = entries.Sum(e => e.Size), ["verification"] = "sha256-all-selected" };
                    }
                }
                catch (OperationCanceledException) { return BusyResponse(); }
                catch (LanBusyException) { return Error("busy"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return Error("request_refused"); }
                finally { _planWork.Release(); }
            }
            if (!modern) { using (workCancel) using (cancel.Register(workCancel.Cancel)) return await Task.Run(BuildPlan); }
            lock (_gate)
            {
                var id = Stamp(); var job = new PlanJob(device, workCancel); _jobs[id] = job;
                job.Task = Task.Run(BuildPlan);
                return new JsonObject { ["pending"] = true, ["job"] = id };
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
            var offset = a["offset"]?.GetValue<long>() ?? throw new ArgumentException("Missing offset");
            if (offset < 0 || offset > entry.Size) throw new ArgumentException("Invalid offset");
            if (!_work.Wait(0)) throw Busy();
            try
            {
            using var input = LanFiles.OpenRead(root, relative);
            if (input.Length != entry.Size) throw new IOException("Source changed");
            // Fixed, bounded transport chunks only; no custom block-diff algorithm or whole-file allocation.
            input.Position = offset;
            var buffer = new byte[(int)Math.Min(256 * 1024, input.Length - offset)];
            TransferBudget(device, buffer.Length);
            await input.ReadExactlyAsync(buffer, cancel);
            lock (_gate) { _bytesServed += buffer.Length; if (DateTime.UtcNow - _lastNotify > TimeSpan.FromMilliseconds(500)) { _lastNotify = DateTime.UtcNow; Notify(); } }
            cancel.ThrowIfCancellationRequested();
            return new JsonObject { ["data"] = Convert.ToBase64String(buffer) };
            } finally { _work.Release(); }
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
    static long ManifestMemory(IReadOnlyList<LanFiles.Entry> entries) => entries.Sum(e => 192L + e.Path.Length * 2L + (e.Dependencies?.Sum(d => 32L + d.Length * 2L) ?? 0));
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    static bool Fixed(string a, string b) => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    internal static async Task<JsonObject> ReadFrame(Stream stream, CancellationToken cancel = default, int maximum = LanLimits.LargeFrame, bool authentication = false)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, cancel); var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 2 || length > maximum) throw new FrameTooLargeException();
        var buffer = new byte[length]; await stream.ReadExactlyAsync(buffer, cancel);
        return LanLimits.Parse(buffer, authentication);
    }
    internal static async Task WriteFrame(Stream stream, JsonObject value, CancellationToken cancel = default, int maximum = LanLimits.LargeFrame)
    {
        using var buffer = new FrameBuffer(maximum, cancel);
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer)) { value.WriteTo(writer); writer.Flush(); }
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, (int)buffer.Length);
        await stream.WriteAsync(prefix, cancel); await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancel); await stream.FlushAsync(cancel);
    }
    sealed class FrameBuffer(int maximum, CancellationToken cancel) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> bytes) { var copy = bytes.ToArray(); Write(copy, 0, copy.Length); }
        public override void Write(byte[] bytes, int offset, int count) { cancel.ThrowIfCancellationRequested(); if (Length + count > maximum) throw new IOException("Response budget exceeded; upgrade or split data"); base.Write(bytes, offset, count); }
    }
    sealed class FrameTooLargeException : IOException { }
    public void Dispose() { _app.Library.SnapshotChanged -= ReconcileRoots; lock (_gate) InvalidateAll(); _stop?.Cancel(); _listener?.Stop(); _certificate?.Dispose(); }
}

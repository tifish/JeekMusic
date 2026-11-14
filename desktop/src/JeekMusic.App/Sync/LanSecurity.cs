using System.Text.Json;
using System.Text.Json.Nodes;

namespace JeekMusic.Sync;

/// <summary>Fixed global ceilings remain authoritative even with distributed sources. No queues.</summary>
internal sealed class LanAdmission
{
    // File transfer, learning, automatic polling and conflict exchange use independent sessions.
    // Keep the global (16) and per-source (8) ceilings authoritative.
    internal const int DeviceSessions = 4;
    internal sealed class Lease(string source)
    {
        public string Source { get; } = source;
        public string? Device;
    }
    sealed class Source { public DateTime Window; public int Attempts; }
    readonly object gate = new();
    readonly Dictionary<string, Source> sources = new();
    readonly HashSet<Lease> live = new();
    DateTime window;
    int attempts;
    public long Rejected { get; private set; }
    public int Active { get { lock (gate) return live.Count; } }
    public int Pending { get { lock (gate) return live.Count(l => l.Device is null); } }
    public int SourceCount { get { lock (gate) return sources.Count; } }
    public Lease? Enter(string ip, DateTime now)
    {
        lock (gate)
        {
            if ((now - window).TotalSeconds >= 1) { window = now; attempts = 0; }
            if (++attempts > 64) { Rejected++; return null; }
            if (!sources.TryGetValue(ip, out var source))
            {
                foreach (var key in sources.Where(p => (now - p.Value.Window).TotalSeconds >= 60).Select(p => p.Key).ToArray()) sources.Remove(key);
                if (sources.Count >= 1024) { Rejected++; return null; }
                sources[ip] = source = new Source { Window = now };
            }
            if ((now - source.Window).TotalSeconds >= 60) { source.Window = now; source.Attempts = 0; }
            if (++source.Attempts > 120 || live.Count >= 24 || live.Count(l => l.Device is null) >= 8 || live.Count(l => l.Device is null && l.Source == ip) >= 4)
            { Rejected++; return null; }
            var lease = new Lease(ip); live.Add(lease); return lease;
        }
    }
    public bool Authenticate(Lease lease, string device)
    {
        lock (gate)
        {
            if (lease.Device == device) return true;
            if (lease.Device is not null || live.Count(l => l.Device is not null) >= 16 || live.Count(l => l.Device == device) >= DeviceSessions || live.Count(l => l.Device is not null && l.Source == lease.Source) >= 8)
            { Rejected++; return false; }
            lease.Device = device; return true;
        }
    }
    public void Exit(Lease lease) { lock (gate) live.Remove(lease); }
}

internal static class LanLimits
{
    public const int SmallFrame = 64 * 1024;
    public const int AuthFrame = 4096;
    public const int LargeFrame = 32_000_000;
    public static int Request(string? op) => op switch { "state" => LargeFrame, "preference_delta" => 4_000_000, "file_chunk"=>512*1024, _ => SmallFrame };
    public static int Response(string? op) => op == "state" ? LargeFrame : 2_000_000;
    public static void Validate(JsonObject value, bool authentication)
    {
        var count = 0;
        void Visit(JsonNode? node, int depth)
        {
            if (++count > (authentication ? 32 : 1_000_000) || depth > (authentication ? 4 : 32)) throw new IOException("JSON budget exceeded");
            if (node is JsonObject obj)
            {
                if (obj.Count > (authentication ? 8 : 100_000)) throw new IOException("Too many fields");
                foreach (var p in obj) { if (p.Key.Length > (authentication ? 128 : 4096)) throw new IOException("Field too long"); Visit(p.Value, depth + 1); }
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child, depth + 1);
            else if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > (authentication ? 256 : 1_000_000)) throw new IOException("String too long");
        }
        Visit(value, 0);
        foreach (var field in new[] { "op", "deviceId", "token", "invitation", "rootId", "plan", "job", "kind", "scope", "operation", "mapping" })
            if (value[field] is { } n && (n is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length > 64)) throw new IOException("Invalid control field");
    }
    public static JsonObject Parse(byte[] bytes, bool authentication)
    {
        // Validate token count, strings and duplicates before allocating a DOM.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = authentication ? 4 : 32 });
        var objects = new Stack<HashSet<string>>();
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > (authentication ? 64 : 1_000_000)) throw new IOException("JSON token budget exceeded");
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueSpan.Length > (authentication ? 512 : 16_384)) throw new IOException("Field budget exceeded");
                var key = reader.GetString()!;
                if (!objects.Peek().Add(key) || objects.Peek().Count > (authentication ? 8 : 100_000)) throw new IOException("Duplicate or excessive fields");
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                if (reader.ValueSpan.Length > (authentication ? 1024 : 4_000_000)) throw new IOException("String budget exceeded");
                var text = reader.GetString()!; // strict UTF-8 validation, before DOM creation
                if (text.Length > (authentication ? 256 : 1_000_000)) throw new IOException("String budget exceeded");
            }
        }
        var value = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = authentication ? 4 : 32 }) as JsonObject ?? throw new IOException("Object required");
        Validate(value, authentication); return value;
    }
}

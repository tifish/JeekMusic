using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using JeekMusic.Infrastructure;

namespace JeekMusic.Sync;

internal sealed record LanFirewallRequest(string Owner, string Program, string Bind, int Port)
{
    internal bool Needed => Port != 0 && !IPAddress.IsLoopback(IPAddress.Parse(Bind));
    internal JsonObject Json() => new() { ["owner"] = Owner, ["program"] = Program, ["bind"] = Bind, ["port"] = Port };
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void Validate(string owner, string bind, int port)
    {
        if (owner.Length != 64 || owner.Any(c => !char.IsAsciiHexDigitLower(c)) || !IPAddress.TryParse(bind, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || port is < 0 or > 65535 || (!ip.Equals(IPAddress.Any) && (ip.GetAddressBytes()[0] == 0 || ip.GetAddressBytes()[0] >= 224))) throw new ArgumentException("Invalid firewall helper request");
    }
}

internal interface ILanFirewallBackend
{
    Task<JsonObject> Query(LanFirewallRequest request);
    Task<JsonObject> Query(LanFirewallRequest request, CancellationToken cancel) => Query(request).WaitAsync(cancel);
    Task<int> Apply(LanFirewallRequest request);
    async Task<JsonObject> Remove(LanFirewallRequest request)
    {
        var result = await Query(request);
        if (result["satisfied"]?.GetValue<bool>() == true) return result;
        var exit = await Apply(request);
        result = await Query(request);
        if (exit != 0) { result["satisfied"] = false; result["helperExitCode"] = exit; }
        return result;
    }
}

/// <summary>One serialized desired state per current-user data directory. No general command endpoint.</summary>
internal sealed class LanFirewall(ILanFirewallBackend backend, string owner, string program, Action changed)
{
    readonly object gate = new();
    readonly SemaphoreSlim serial = new(1);
    LanFirewallRequest desired = new(owner, program, "127.0.0.1", 0);
    Task pending = Task.CompletedTask;
    JsonObject status = new() { ["state"] = "not_checked", ["message"] = "Windows firewall has not been checked.", ["reachability"] = "not_verified" };
    long revision;
    CancellationTokenSource readCancel = new();

    internal static LanFirewall Create(Action changed)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current user SID unavailable");
        var owner = LanFirewallRequest.Hash(sid + "|" + Path.GetFullPath(AppPaths.DataDir).TrimEnd('\\').ToUpperInvariant());
        return new(new WindowsLanFirewall(), owner, Environment.ProcessPath ?? throw new InvalidOperationException("Executable unavailable"), changed);
    }
    internal JsonObject Status() { lock (gate) return status.DeepClone().AsObject(); }
    internal Task Set(string bind, int port)
    {
        LanFirewallRequest.Validate(owner, bind, port);
        lock (gate)
        {
            readCancel.Cancel(); readCancel.Dispose(); readCancel = new();
            desired = new(owner, program, bind, port); var version = ++revision;
            status = desired.Json(); status["state"] = "pending"; status["reachability"] = "not_verified";
            var cancel = readCancel.Token;
            pending = Task.Run(() => Reconcile(version, cancel)); return pending;
        }
    }
    internal Task Wait() { lock (gate) return pending; }
    async Task Reconcile(long version, CancellationToken cancel)
    {
        try { await serial.WaitAsync(cancel); }
        catch (OperationCanceledException) { return; }
        try
        {
            LanFirewallRequest request;
            lock (gate) { if (version != revision) return; request = desired; }
            JsonObject result;
            try
            {
                var watch = Stopwatch.StartNew();
                result = request.Needed ? await backend.Query(request, cancel) : await backend.Remove(request);
                if (request.Needed && result["satisfied"]?.GetValue<bool>() != true)
                {
                    lock (gate) { if (version != revision) return; status["state"] = "configuring"; status["message"] = "Updating the application TCP rule; Windows may request administrator consent."; }
                    changed();
                    var exit = await backend.Apply(request);
                    result = await backend.Query(request, cancel);
                    if (exit != 0 || result["satisfied"]?.GetValue<bool>() != true)
                    {
                        result["state"] = result["state"]?.GetValue<string>() == "policy_blocked" ? "policy_blocked" : exit == 1223 ? "uac_cancelled" : exit == 5 ? "access_denied" : "configuration_failed";
                        result["message"] = exit == 1223 ? "Administrator consent was declined. Repair the firewall to allow connections." : "Windows did not accept or activate the exact application rule. Check administrator permissions, managed policy or third-party firewall.";
                        result["helperExitCode"] = exit;
                    }
                }
                result["durationMs"] = watch.ElapsedMilliseconds;
                Log.Info("lifecycle", $"Firewall {(request.Needed ? "start" : "cleanup")}: {watch.ElapsedMilliseconds} ms ({result["state"]})");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException or TimeoutException)
            {
                result = new() { ["state"] = "check_failed", ["message"] = "Windows firewall status could not be verified: " + e.GetType().Name };
            }
            result["reachability"] = "not_verified";
            result["note"] = "Only Windows application rules are managed. Router/NAT/UPnP are unchanged; third-party firewalls and external reachability require separate verification.";
            lock (gate) { if (version == revision) { status = request.Json(); foreach (var pair in result) status[pair.Key] = pair.Value?.DeepClone(); } }
            changed();
        }
        finally { serial.Release(); }
    }
}

internal sealed class WindowsLanFirewall : ILanFirewallBackend
{
    static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    static string Encoded(LanFirewallRequest request, bool apply)
    {
        using var stream = typeof(WindowsLanFirewall).Assembly.GetManifestResourceStream("JeekMusic.LanFirewall.ps1") ?? throw new IOException("Missing firewall helper");
        using var reader = new StreamReader(stream);
        // Serialized data only, never executable input. The privileged script is embedded in this EXE.
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Json().ToJsonString()));
        var source = "$request=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "'))|ConvertFrom-Json; $apply=$" + (apply ? "true" : "false") + ";\n" + reader.ReadToEnd();
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(source));
    }
    static async Task<(int Exit, string Output)> Run(LanFirewallRequest request, bool apply, CancellationToken cancel = default)
    {
        var start = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Encoded(request, apply) }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Firewall query could not start");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync(cancel).WaitAsync(TimeSpan.FromSeconds(45), cancel); }
        catch (TimeoutException) { child.Kill(entireProcessTree: true); throw; }
        catch (OperationCanceledException) { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
        await error; return (child.ExitCode, await output);
    }
    public Task<JsonObject> Query(LanFirewallRequest request) => Query(request, CancellationToken.None);
    public async Task<JsonObject> Query(LanFirewallRequest request, CancellationToken cancel)
    {
        var result = await Run(request, false, cancel);
        if (result.Exit != 0) throw new IOException("Windows firewall query failed");
        return JsonNode.Parse(result.Output)?.AsObject() ?? throw new IOException("Windows firewall query returned no result");
    }
    public async Task<JsonObject> Remove(LanFirewallRequest request)
    {
        // With an existing elevated token, remove and verify in one helper process.
        // Non-elevated callers retain the exact-rule query and fixed UAC helper.
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            var result = await Run(request, true);
            if (result.Exit != 0) throw new IOException("Windows firewall cleanup failed");
            return JsonNode.Parse(result.Output)?.AsObject() ?? throw new IOException("Windows firewall cleanup returned no result");
        }
        var current = await Query(request);
        if (current["satisfied"]?.GetValue<bool>() == true) return current;
        var exit = await Apply(request);
        current = await Query(request);
        if (exit != 0) { current["state"] = exit == 1223 ? "uac_cancelled" : "configuration_failed"; current["satisfied"] = false; current["helperExitCode"] = exit; }
        return current;
    }
    public async Task<int> Apply(LanFirewallRequest request)
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return (await Run(request, true)).Exit;
        // Elevate only this fixed helper, never the player. No script path, command, output file or executable path is accepted.
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--lan-firewall-helper", request.Owner, request.Bind, request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        try
        {
            using var child = Process.Start(start) ?? throw new IOException("Firewall helper could not start");
            await child.WaitForExitAsync(); return child.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return 1223; }
    }
    internal static int Helper(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[3], out var port)) return 87;
        try
        {
            LanFirewallRequest.Validate(args[1], args[2], port);
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
            return Run(new(args[1], Environment.ProcessPath!, args[2], port), true).GetAwaiter().GetResult().Exit;
        }
        catch (Exception e) when (e is ArgumentException or IOException or Win32Exception or TimeoutException) { return 1; }
    }
}

using JeekMusic.Infrastructure;

namespace JeekMusic.Playback;

internal sealed record OwnedDeviceFormatInfo(string DeviceId, EndpointAudioFormat BaselineDevice, EndpointAudioFormat BaselineMix,
    EndpointAudioFormat AppliedDevice, EndpointAudioFormat AppliedMix);

/// <summary>Per-physical-endpoint temporary format ownership. Call only after closing our old stream.</summary>
internal sealed class TemporaryDeviceFormats(IDeviceAudioFormat service) : IDisposable
{
    sealed class Ownership(DeviceAudioSnapshot baseline, IDisposable lease)
    {
        public DeviceAudioSnapshot Baseline { get; } = baseline;
        public IDisposable Lease { get; } = lease;
        public EndpointAudioFormat Device { get; set; } = baseline.Device;
        public EndpointAudioFormat Mix { get; set; } = baseline.Mix;
        public bool Applied { get; set; }
        public string? RestoreFailure { get; set; }
    }
    readonly Dictionary<string, Ownership> _owners = new(StringComparer.OrdinalIgnoreCase);
    public string RestoreStatus { get; private set; } = "none";
    public string? RestoreReason { get; private set; }
    public string? RestoreDiagnostic { get; private set; }
    public IReadOnlyList<OwnedDeviceFormatInfo> Owners => _owners.Where(p => p.Value.Applied).Select(p =>
        new OwnedDeviceFormatInfo(p.Key, p.Value.Baseline.Device, p.Value.Baseline.Mix, p.Value.Device, p.Value.Mix)).ToArray();

    void Drop(string id)
    {
        if (_owners.Remove(id, out var owner)) owner.Lease.Dispose();
    }

    public bool Apply(string device, EndpointAudioFormat target, out string? diagnostic)
    {
        diagnostic = null;
        if (_owners.TryGetValue(device, out var previous))
        {
            var current = service.CaptureSnapshot(device, out diagnostic);
            if (current is null) return false;
            if (current.Device != previous.Device || current.Mix != previous.Mix)
            {
                // The external format is now authoritative. A new play/application gets
                // a fresh baseline rather than keeping a snapshot from before that edit.
                Drop(device);
                RestoreStatus = "external-change";
                RestoreDiagnostic = "Windows format changed externally; the old baseline was discarded.";
            }
        }
        if (!_owners.TryGetValue(device, out var owner))
        {
            var lease = service.TryAcquireControl(device, out diagnostic);
            if (lease is null) return false;
            var baseline = service.CaptureSnapshot(device, out diagnostic);
            if (baseline is null) { lease.Dispose(); return false; }
            owner = new(baseline, lease);
            _owners.Add(device, owner);
        }
        if (!service.SetSharedFormat(device, target, out diagnostic))
        {
            // SetSharedFormat has its own complete rollback. Keep an older successful
            // ownership, but never claim a newly rejected or partially failed write.
            if (!owner.Applied) Drop(device);
            return false;
        }
        owner.Applied = true;
        owner.Device = target;
        owner.Mix = target with { ContainerBits = 32, ValidBits = 32, SampleKind = "float" };
        owner.RestoreFailure = null;
        var pending = _owners.Where(p => p.Value.RestoreFailure is not null).Select(p => p.Key + ": " + p.Value.RestoreFailure).ToArray();
        RestoreStatus = pending.Length > 0 ? "failed" : "active";
        RestoreDiagnostic = pending.Length > 0 ? string.Join("; ", pending) : null;
        Log.Playback($"临时设备格式已应用：{device}；基线 {owner.Baseline.Device}；当前 {target}");
        return true;
    }

    public void RestoreAll(string reason, bool releaseFailed = false)
    {
        if (_owners.Count == 0) return;
        RestoreReason = reason;
        var failed = new List<string>();
        var external = false;
        foreach (var (id, owner) in _owners.ToArray())
        {
            if (!owner.Applied) { Drop(id); continue; }
            var result = service.RestoreIfUnchanged(owner.Baseline, owner.Device, owner.Mix);
            Log.Playback($"恢复设备基线：{reason}；{id}；{result.Status}；{result.Diagnostic}");
            if (result.Status == "failed")
            {
                owner.RestoreFailure = result.Diagnostic ?? "Format restoration failed.";
                failed.Add(id + ": " + result.Diagnostic);
                if (releaseFailed) Drop(id);
            }
            else
            {
                external |= result.Status == "external-change";
                Drop(id);
            }
        }
        RestoreStatus = failed.Count > 0 ? "failed" : external ? "external-change" : "restored";
        RestoreDiagnostic = failed.Count > 0 ? string.Join("; ", failed) :
            external ? "Windows format changed externally; the old baseline was not restored." : null;
    }

    public void Dispose()
    {
        RestoreAll("exit", releaseFailed: true);
        foreach (var id in _owners.Keys.ToArray()) Drop(id);
    }
}

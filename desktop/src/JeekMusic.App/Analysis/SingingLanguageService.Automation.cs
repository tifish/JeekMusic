using System.Text.Json.Nodes;
using JeekMusic.Library;
using JeekMusic.Settings;

namespace JeekMusic.Analysis;

public sealed partial class SingingLanguageService
{
    internal static readonly TimeSpan AutomaticRetryDelay=TimeSpan.FromMinutes(5);
    readonly Dictionary<string,(string Signature,DateTimeOffset At)> automaticRetries=[];
    DateTimeOffset modelRetryAt;
    bool automationAttached,automaticRun,automaticSuppressed,automaticSuspended;
    AnalysisMode automaticMode;
    string? automaticWaiting;
    string automaticReason="not_started";
    DateTimeOffset? nextAutomaticRetry;

    JsonObject AutomaticStatus()=>new() {
        ["enabled"]=automationAttached && automaticMode!=AnalysisMode.Manual,
        ["analysisMode"]=automaticMode.ToString(),["reason"]=automaticReason,
        ["active"]=automaticRun && Busy,["suppressedUntilRestart"]=automaticSuppressed,
        ["modelDownload"]="automatic_when_allowed",["retrySeconds"]=(int)AutomaticRetryDelay.TotalSeconds,
        ["nextRetryAt"]=nextAutomaticRetry?.ToUnixTimeMilliseconds(),
        ["restartPolicy"]="rebuild_missing_queue_from_committed_results"
    };

    /// <summary>Called on startup/library/policy/task events. Returns only a failure retry deadline;
    /// completed libraries need no timer. Completed results are the durable restart checkpoint.</summary>
    internal TimeSpan? ReconcileAutomatic(AnalysisMode mode,bool onAcPower,bool fullscreen)
    {
        lock(gate) {
            if(disposed) return null;
            automationAttached=true; automaticMode=mode; nextAutomaticRetry=null;
            automaticReason=mode==AnalysisMode.Manual?"manual_mode":fullscreen?"fullscreen":mode==AnalysisMode.PluggedIn && !onAcPower?"battery":"ready";
            if(automaticReason!="ready") {
                automaticWaiting="演唱语言自动识别已暂停（分析条件尚未满足）";
                if(automaticRun && Busy && !automaticSuppressed) { automaticSuspended=true; stop?.Cancel(); }
                return null;
            }
            if(automaticSuppressed) { automaticReason="cancelled_by_user";return null; }
            if(Busy) { automaticReason=automaticRun?"running":"manual_task";return null; }
            var now=DateTimeOffset.UtcNow;
            if(modelRetryAt>now) return Retry(modelRetryAt);
            var tracks=library.Snapshot.Tracks;
            var existing=library.Db.Languages(tracks);
            var missing=tracks.Where(t=>!existing.ContainsKey(t.TrackKey)).ToArray();
            var available=missing.Where(t=>library.PathOf(t) is not null).ToArray();
            var pending=available.Where(t=>!automaticRetries.TryGetValue(t.TrackKey,out var retry) ||
                retry.Signature!=SingingLanguage.Signature(t) || retry.At<=now).ToArray();
            if(pending.Length>0) {
                automaticReason="running"; automaticWaiting=null;
                // Desktop automation owns first-use model preparation; explicit MCP/UI preparation
                // keeps its existing confirmation contract. Android remains manual.
                Begin(pending,downloadConfirmed:true,force:false,automatic:true);
            } else {
                var at=available.Select(t=>automaticRetries[t.TrackKey].At).Cast<DateTimeOffset?>().Min();
                if(at is not null) return Retry(at.Value);
                automaticReason=missing.Length==0?"complete":"offline";
                automaticWaiting=missing.Length==0?"演唱语言已全部识别":"演唱语言自动识别等待音乐文件可用";
            }
            return null;

            TimeSpan Retry(DateTimeOffset at) {
                automaticReason="retry_wait"; nextAutomaticRetry=at;
                automaticWaiting="演唱语言识别失败，稍后自动重试";
                return at-now;
            }
        }
    }
}

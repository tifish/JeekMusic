using System.Text.Json.Nodes;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Preferences;

// Public diagnostics are assembled from a closed vocabulary, never exception text or worker stderr.
internal static class LearningFailure
{
    internal static readonly string[] Codes = ["dependency_missing","download_failed","download_integrity","recording_missing","decode_unsupported","decode_length","decode_nonfinite","silent_audio","recording_changed","memory_budget","time_budget","worker_exit","cancelled","interrupted","insufficient_material","validation_failed","check_material_required","local_io","unexpected_failure","history_unavailable","disk_space","work_budget","invalidated"];
    internal static JsonObject Create(string code,string stage,string? dimension=null,int? exitCode=null,string? errorType=null,long? expected=null,long? actual=null,bool historical=false)
    {
        if(!Codes.Contains(code)) code="unexpected_failure";
        if(stage is not ("prepare" or "extract" or "train" or "evaluate" or "update_library" or "check_material" or "orchestrate")) stage="orchestrate";
        var (message,next,retryable)=code switch {
            "dependency_missing"=>("本机学习运行时或解码组件缺失。","检查 Python 3.13+ 和 FFmpeg；重新准备本地学习组件后重试。",true),
            "download_failed"=>("学习组件下载或安装失败。","检查网络和可用空间后重试；已核验的下载会复用。",true),
            "download_integrity"=>("学习组件未通过完整性校验。","重新准备组件；校验通过前不会运行该组件。",true),
            "recording_missing"=>("学习需要的本地录音文件已不可用。","连接音乐目录或恢复原录音后重试。",true),
            "decode_unsupported"=>("录音无法解码或格式不受支持。","确认原文件能正常播放，并更新 FFmpeg 后重试。",true),
            "decode_length"=>("解码片段的采样长度不符合学习要求。","更新到当前版本后重试；若仍失败，检查原录音是否完整。",true),
            "decode_nonfinite"=>("解码片段包含无效采样值。","检查原录音完整性或使用有效的同一录音后重试。",true),
            "silent_audio"=>("学习片段没有足够的有效声音。","检查原录音及其静音片段，修复文件后重试。",true),
            "recording_changed"=>("录音在学习期间发生变化。","等待文件写入完成并刷新音乐目录后重试。",true),
            "memory_budget"=>("学习工作进程达到内存限额。","关闭占用内存较多的应用后重试；已完成分析保留。",true),
            "time_budget"=>("学习工作进程达到时间限额。","暂停播放并降低本机负载后继续学习；已完成分析保留。",true),
            "worker_exit"=>("学习工作进程异常退出。","重试一次；若重复出现，复制下方脱敏诊断以便排查。",true),
            "cancelled"=>("学习已取消。","点击继续学习，可复用已完成的分析。",true),
            "interrupted"=>("上次学习在客户端或工作进程退出时中断。","点击继续学习；远程任务需要在授权设备或本机重新开始。",true),
            "insufficient_material"=>("当前维度的有效学习材料不足。","查看该维度的材料缺项，继续在歌曲列表修正熟悉的歌曲。",false),
            "validation_failed"=>("学习结果未通过效果检查。","继续使用原有识别；保留检查标准，继续积累日常标注。",false),
            "check_material_required"=>("缺少独立的效果检查材料或身份核验。","查看该维度的材料状态；当前继续使用原有识别。",false),
            "disk_space"=>("磁盘可用空间不足。","为学习组件保留至少 6 GB 可用空间后重试。",true),
            "work_budget"=>("本轮学习工作限额已到。","点击继续学习，可复用已完成的分析。",true),
            "invalidated"=>("标注、权限或录音状态在学习期间变化。","确认当前材料和授权后重新开始学习。",true),
            "local_io"=>("本机学习文件无法读写。","检查磁盘空间及应用数据目录权限后重试。",true),
            "history_unavailable"=>("历史学习失败没有保留足够的原因信息。","可以重试；新版本会保留脱敏阶段和错误码。",true),
            _=>("学习未能完成。","重试一次；若重复出现，复制下方脱敏诊断以便排查。",true)
        };
        if(errorType is not ("ValueError" or "TimeoutError" or "MemoryError" or "FileNotFoundError" or "CalledProcessError" or "ModuleNotFoundError" or "ImportError" or "OSError" or "Win32Exception" or "InvalidOperationException" or "IOException" or "JsonException")) errorType=null;
        if(dimension is not ("MOOD" or "VOICE" or "GENRE" or "CALMNESS")) dimension=null;
        var result=new JsonObject { ["version"]=1,["code"]=code,["stage"]=stage,["message"]=message,["nextStep"]=next,["retryable"]=retryable,["dimension"]=dimension,["exitCode"]=exitCode,["workerErrorType"]=errorType,["historical"]=historical,["occurredAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        if(expected is >=0 and <=100000000) result["expectedSamples"]=expected;
        if(actual is >=0 and <=100000000) result["actualSamples"]=actual;
        return result;
    }
    internal static JsonObject FromWorker(JsonNode? raw,string stage,string dimension,int? exitCode,bool historical=false)
    {
        var type=S(raw?["errorType"]); var literal=S(raw?["error"]);
        var code=S(raw?["failure"]?["code"]);
        if(!Codes.Contains(code)) code=literal switch {
            "Incomplete 10-second audio"=>"decode_length",
            "Component hash mismatch"=>"download_integrity",
            "Recording changed" or "Recording changed during extraction"=>"recording_changed",
            "Silent context"=>"silent_audio",
            "PCA rank below two"=>"insufficient_material",
            _=>type switch { "TimeoutError" or "TimeoutExpired"=>"time_budget","MemoryError"=>"memory_budget","ModuleNotFoundError" or "ImportError"=>"dependency_missing","FileNotFoundError"=>stage=="extract"?"recording_missing":"dependency_missing","CalledProcessError"=>stage=="prepare"?"download_failed":"decode_unsupported",_=>"worker_exit" }
        };
        var result=Create(code,stage,dimension,exitCode,type,Number(raw?["failure"]?["expectedSamples"]),Number(raw?["failure"]?["actualSamples"]),historical);
        result["workerPhase"]=Phase(S(raw?["failure"]?["workerPhase"]))??(code=="decode_length"?"decode":null); return result;
    }
    static long? Number(JsonNode? node)=>node is JsonValue v && v.TryGetValue<long>(out var n)?n:null;
    internal static JsonObject Sanitize(JsonNode raw,string? dimension=null)
    {
        var result=Create(S(raw["code"]),S(raw["stage"]),dimension??S(raw["dimension"]),Number(raw["exitCode"]) is { } exit && exit is >=int.MinValue and <=int.MaxValue?(int)exit:null,S(raw["workerErrorType"]),Number(raw["expectedSamples"]),Number(raw["actualSamples"]),raw["historical"] is JsonValue h && h.TryGetValue<bool>(out var b) && b);
        if(Number(raw["occurredAt"]) is >0 and <4102444800000L) result["occurredAt"]=Number(raw["occurredAt"]);
        result["workerPhase"]=Phase(S(raw["workerPhase"]));
        return result;
    }
    static string? Phase(string value)=>value is "startup" or "download" or "install" or "model_load" or "recording_check" or "decode" or "embedding" or "prepare" or "extract" or "train" or "evaluate"?value:null;
    internal static JsonObject FromException(Exception e,string stage,string dimension)=>Create(e switch {
        OperationCanceledException=>"cancelled",System.ComponentModel.Win32Exception=>"dependency_missing",FileNotFoundException=>stage=="extract"?"recording_missing":"dependency_missing",IOException=>"local_io",_=>"unexpected_failure"
    },stage,dimension,errorType:e.GetType().Name);
    internal static string Summary(JsonNode failure)=>S(failure["message"])+"\n"+S(failure["nextStep"]);
    internal static string Diagnostics(JsonNode failure)=>string.Join("\n",new[]{"version","code","stage","workerPhase","dimension","exitCode","workerErrorType","historical","expectedSamples","actualSamples","occurredAt"}.Select(k=>k+"="+(failure[k]?.ToJsonString()??"unknown")));
}

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using JeekMusic.Core.Analysis;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace JeekMusic.Analysis;

/// <summary>Local, explicit vocal-language analysis. No title/lyrics heuristics or audio uploads.</summary>
public sealed class SingingLanguageService : IDisposable
{
    public const string ModelSha256="be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21";
    public const long ModelBytes=77691713;
    readonly LibraryService library;
    readonly string model;
    readonly object gate=new();
    CancellationTokenSource? stop;
    Task? work;
    bool disposed;
    string state="idle";
    string? error;
    string? errorDetail;
    readonly Queue<JsonObject> recentResults=[];
    int total, processed, failed;
    long? current;
    public event Action? Changed;
    public SingingLanguageService(LibraryService library, string? modelDirectory=null)
    {
        this.library=library;
        model=Path.Combine(modelDirectory??Path.Combine(AppPaths.DataDir,"models","singing-language"),"ggml-tiny.bin");
    }
    public bool ModelReady => File.Exists(model) && new FileInfo(model).Length==ModelBytes;
    public bool Busy { get { lock(gate) return state is "preparing" or "running" or "cancelling"; } }
    public string StatusText { get { lock(gate) return state switch {
        "preparing"=>L.T("正在下载演唱语种识别模型…"),
        "running"=>L.F($"正在识别演唱语言 {processed} / {total}"),
        "cancelling"=>L.T("正在取消语种识别…"), "failed"=>L.T("演唱语种识别失败，请重试"),
        "completed"=>L.F($"演唱语种识别完成 {processed} / {total}"),
        "completed_with_errors"=>L.F($"演唱语种识别完成 {processed} / {total}，失败 {failed}"),
        "cancelled"=>L.T("演唱语种识别已取消"), _=>L.T("演唱语种尚未识别") }; } }
    public JsonObject Read() { lock(gate) return new() {
        ["state"]=state,["modelReady"]=ModelReady,["modelBytes"]=ModelBytes,["modelSha256"]=ModelSha256,
        ["total"]=total,["processed"]=processed,["failed"]=failed,["currentTrackId"]=current,["error"]=error,
        ["errorDetail"]=errorDetail,["recentResults"]=new JsonArray(recentResults.Select(r=>r.DeepClone()).ToArray()),
        ["policy"]=SingingLanguage.Policy,["audioUploaded"]=false,["writesAudio"]=false,
        ["model"]="whisper-tiny-multilingual",["sampleSecondsAtMost"]=60,["threads"]=2,
        ["unknownMatchesNegatives"]=false,["instrumentalMatchesLanguageFilters"]=false,
        ["confidenceIsCalibrated"]=false,["pairedSync"]=new JsonObject { ["version"]=1,["transport"]="paired-lan-metadata",["sources"]=new JsonArray("AUTO","MANUAL"),
            ["restoresAutomaticWithTombstone"]=true,["requiresSizeDurationAndCueStartMatch"]=true,["directoryAdapter"]=false } }; }
    public SingingLanguage? Result(Track track) => library.Db.Languages([track]).GetValueOrDefault(track.TrackKey);
    public void Prepare(bool downloadConfirmed) => Begin([],downloadConfirmed,false);
    public void Start(IReadOnlyList<Track> targets, bool downloadConfirmed=false, bool force=false)
    {
        if(targets.Count==0) throw new ArgumentException("No library tracks to analyze");
        var captured=targets.DistinctBy(t=>t.TrackKey).ToArray();
        foreach(var track in captured) ValidateRecording(track);
        Begin(captured,downloadConfirmed,force);
    }
    void Begin(Track[] tracks,bool downloadConfirmed,bool force)
    {
        lock(gate) {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(Busy) throw new InvalidOperationException("Vocal-language analysis already running");
            if(!ModelReady && !downloadConfirmed) throw new InvalidOperationException("Initial local model download (78 MB) requires downloadConfirmed=true");
            var existing=library.Db.Languages(tracks);
            var pending=tracks.Where(t=>force || !existing.ContainsKey(t.TrackKey)).ToArray();
            stop?.Dispose(); stop=new();
            var token=stop.Token;
            total=pending.Length; processed=failed=0; error=errorDetail=null; current=null; recentResults.Clear();
            state=ModelReady?"running":"preparing";
            work=Task.Run(async()=> {
                try {
                    await EnsureModel(token);
                    token.ThrowIfCancellationRequested();
                    if(pending.Length>0) {
                        RuntimeOptions.RuntimeLibraryOrder=[RuntimeLibrary.CpuNoAvx];
                        using var factory=WhisperFactory.FromPath(model,new() { UseGpu=false });
                        await using var processor=factory.CreateBuilder().WithThreads(2).WithLanguageDetection()
                            .WithNoContext().WithTemperature(0).WithTemperatureInc(0).WithNoSpeechThreshold(.6f)
                            .WithProbabilities().Build();
                        using var tagger=new AudioTagger();
                        foreach(var track in pending) {
                            token.ThrowIfCancellationRequested();
                            lock(gate) { current=track.Id; state="running"; } Notify();
                            try {
                                using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);
                                budget.CancelAfter(TimeSpan.FromSeconds(120));
                                var result=await Analyze(track,processor,tagger,budget.Token);
                                lock(gate) {
                                    token.ThrowIfCancellationRequested();
                                    if(disposed) return;
                                    library.Db.InTransaction(()=> { ValidateRecording(track); library.Db.SaveLanguage(track,result,"AUTO"); });
                                }
                                await library.RefreshAsync();
                                RecordOutcome(track.Id,"committed",null);
                            }
                            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
                            catch(Exception ex) {
                                Log.Warn("singing-language","演唱语种识别失败",ex);
                                lock(gate) { failed++; error=ex.GetType().Name; errorDetail=ex.Message; }
                                RecordOutcome(track.Id,"failed",ex.Message);
                            }
                            lock(gate) processed++;
                            Notify();
                        }
                    }
                    lock(gate) { token.ThrowIfCancellationRequested(); state=failed==0?"completed":"completed_with_errors"; current=null; }
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested) { lock(gate) { state="cancelled"; current=null; } }
                catch(Exception ex) { Log.Warn("singing-language","演唱语种任务失败",ex); lock(gate) { state="failed"; error=ex.GetType().Name; errorDetail=ex.Message; current=null; } }
                finally { Notify(); }
            });
        }
        Notify();
    }
    async Task EnsureModel(CancellationToken ct)
    {
        if(ModelReady) {
            await using var input=File.OpenRead(model);
            if(Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct))==ModelSha256) return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        var temporary=model+"."+Guid.NewGuid().ToString("N")+".download";
        try {
            using var client=new HttpClient { Timeout=TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JeekMusic/1.0");
            await using(var input=await client.GetStreamAsync("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",ct))
            await using(var output=File.Create(temporary)) await input.CopyToAsync(output,ct);
            await using(var input=File.OpenRead(temporary)) {
                if(input.Length!=ModelBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct))!=ModelSha256)
                    throw new InvalidDataException("Whisper model checksum mismatch");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary,model,true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static IReadOnlyList<(long StartMs,long EndMs)> Windows(Track track)
    {
        var start=track.StartMs??0;
        var duration=Math.Max(0,Math.Min(track.DurationMs,(track.EndMs??(start+track.DurationMs))-start));
        if(duration<3000) return [];
        var length=Math.Min(20000,duration);
        var windows=new List<(long StartMs,long EndMs)>();
        foreach(var fraction in new[] {.2,.5,.8}) {
            var from=start+(long)((duration-length)*fraction);
            if(windows.Count>0 && from<windows[^1].EndMs) from=windows[^1].EndMs;
            if(from+length<=start+duration) windows.Add((from,from+length));
        }
        return windows;
    }
    async Task<SingingLanguage> Analyze(Track track,WhisperProcessor processor,AudioTagger tagger,CancellationToken ct)
    {
        var path=ValidateRecording(track);
        var samples=new JsonArray();
        foreach(var (from,to) in Windows(track)) {
            ct.ThrowIfCancellationRequested();
            var pcm=await PcmDecoder.Window(path,from,to,ct);
            if(pcm.Length<(to-from)*16-1600) throw new InvalidDataException("Truncated vocal-language sample");
            var score=tagger.Analyze(pcm,ct);
            var sample=new JsonObject { ["startMs"]=from,["endMs"]=to,["vocalScore"]=score?.Vocal,["language"]=null,["probability"]=null,["speechConfirmed"]=false };
            samples.Add((JsonNode)sample);
            if(score is null || score.Vocal<TagClassifier.VocalThreshold || pcm.Length<48000) continue;
            ct.ThrowIfCancellationRequested();
            var (language,probability)=processor.DetectLanguageWithProbability(pcm);
            ct.ThrowIfCancellationRequested();
            sample["language"]=language; sample["probability"]=float.IsFinite(probability)?(double)probability:null;
            if(language is null || !float.IsFinite(probability) || probability<.6) continue;
            processor.ChangeLanguage(language);
            var letters=0;
            await foreach(var segment in processor.ProcessAsync(pcm,ct))
                if(float.IsFinite(segment.NoSpeechProbability) && segment.NoSpeechProbability<.6f && segment.Probability>=.25f)
                    letters+=segment.Text.Count(char.IsLetter);
            sample["speechConfirmed"]=letters>=4;
        }
        return Aggregate(samples);
    }
    internal static SingingLanguage Aggregate(JsonArray samples)
    {
        var supported=samples.OfType<JsonObject>().Where(s=>s["speechConfirmed"]?.GetValue<bool>()==true && s["language"] is not null && s["probability"]?.GetValue<double>()>=.6).ToArray();
        var languages=supported.GroupBy(s=>s["language"]!.GetValue<string>()).Where(g=>g.Count()>=2 || g.Max(s=>s["probability"]!.GetValue<double>())>=.9)
            .Select(g=>g.Key).Order(StringComparer.Ordinal).ToArray();
        var instrumental=samples.Count>=2 && samples.OfType<JsonObject>().All(s=>s["vocalScore"]?.GetValue<double>() is { } v && v<TagClassifier.VocalThreshold);
        return new(languages.Length>0?"known":instrumental?"instrumental":"unknown",languages,"AUTO",
            languages.Length>0?supported.Where(s=>languages.Contains(s["language"]!.GetValue<string>())).Min(s=>s["probability"]!.GetValue<double>()):null,samples);
    }
    string ValidateRecording(Track target)
    {
        var current=library.Db.TrackById(target.Id);
        var path=current is null?null:library.PathOf(current);
        var file=path is null?null:new FileInfo(path);
        if(current is null || current.TrackKey!=target.TrackKey || SingingLanguage.Signature(current)!=SingingLanguage.Signature(target) || file?.Exists!=true || file.Length!=target.Size || file.LastWriteTimeUtc.Ticks!=target.Mtime)
            throw new InvalidOperationException("Recording changed or offline; rescan before vocal-language analysis");
        return path!;
    }
    public void SetManual(IReadOnlyList<Track> tracks,string value)
    {
        var result=value=="AUTO"?null:SingingLanguage.Manual(value);
        var targets=tracks.DistinctBy(t=>t.TrackKey).ToArray();
        if(targets.Length is <1 or >1000) throw new ArgumentException("Select 1–1000 library tracks");
        lock(gate) {
            ObjectDisposedException.ThrowIf(disposed,this);
            library.Db.InTransaction(()=> {
                var all=library.Db.AllTracks().ToDictionary(t=>t.Id);
                foreach(var target in targets)
                    if(!all.TryGetValue(target.Id,out var current) || current.TrackKey!=target.TrackKey || SingingLanguage.Signature(current)!=SingingLanguage.Signature(target))
                        throw new InvalidOperationException("Track changed; refresh before correcting language");
                foreach(var target in targets) library.Db.SaveLanguage(target,result,"MANUAL");
            });
        }
        library.RebuildSnapshot(); Notify();
    }
    void RecordOutcome(long id,string outcome,string? detail)
    {
        lock(gate) {
            recentResults.Enqueue(new JsonObject { ["trackId"]=id,["state"]=outcome,["error"]=detail });
            while(recentResults.Count>20) recentResults.Dequeue();
        }
    }
    void Notify() { if(!disposed) Dispatcher.UIThread.Post(()=> { if(!disposed) Changed?.Invoke(); }); }
    public void Cancel() { lock(gate) if(Busy) { state="cancelling"; stop?.Cancel(); } Notify(); }
    public void Dispose() { lock(gate) { disposed=true; stop?.Cancel(); } }
}

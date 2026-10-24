using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class TemporaryDeviceFormatTests
{
    static EndpointAudioFormat Pcm(int rate,int valid=24,int container=24,int channels=2,uint mask=3)=>new(rate,container,valid,channels,mask,"pcm");
    static EndpointAudioFormat Mix(EndpointAudioFormat value)=>value with{ContainerBits=32,ValidBits=32,SampleKind="float"};

    sealed class Formats:IDeviceAudioFormat
    {
        public Dictionary<string,(EndpointAudioFormat Device,EndpointAudioFormat Mix)> States { get; }=new(StringComparer.OrdinalIgnoreCase)
        {
            ["render-device"]=(Pcm(48000,16,16),Mix(Pcm(48000))),
            ["second-device"]=(Pcm(96000,16,16),Mix(Pcm(96000)))
        };
        public HashSet<string> Leases { get; }=new(StringComparer.OrdinalIgnoreCase);
        public List<(string Id,EndpointAudioFormat Format)> Writes { get; }=[];
        public List<string> Restores { get; }=[];
        public int Captures;
        public bool RejectWrite,RejectRestore;
        public Action? BeforeRestore;
        public string Id(string? device)=>(device??"render-device").Replace("wasapi/","");
        public DeviceAudioCapabilities CaptureFormat(string? device,SourceAudioFormat source)
        {
            var id=Id(device);var state=States[id];
            return new(id,state.Mix.SampleRate,state.Mix.Channels,[44100,48000,96000],[44100,48000,96000],true,null,state.Device,state.Mix,
                [Pcm(44100,16,16),Pcm(44100),Pcm(48000,16,16),Pcm(48000),Pcm(96000)]);
        }
        public bool SetSharedFormat(string device,EndpointAudioFormat format,out string? diagnostic)
        {
            Writes.Add((device,format));diagnostic=RejectWrite?"test write failure":null;
            if(RejectWrite)return false;
            States[device]=(format,Mix(format));return true;
        }
        public IDisposable? TryAcquireControl(string device,out string? diagnostic)
        {
            diagnostic=null;
            if(!Leases.Add(device)){diagnostic="another owner";return null;}
            return new Lease(()=>Leases.Remove(device));
        }
        sealed class Lease(Action release):IDisposable
        {
            bool _disposed;
            public void Dispose(){if(_disposed)return;_disposed=true;release();}
        }
        public DeviceAudioSnapshot? CaptureSnapshot(string device,out string? diagnostic)
        {
            Captures++;diagnostic=null;var state=States[device];
            return new(device,state.Device,state.Mix,[],[]);
        }
        public DeviceFormatRestoreResult RestoreIfUnchanged(DeviceAudioSnapshot baseline,EndpointAudioFormat device,EndpointAudioFormat mix)
        {
            BeforeRestore?.Invoke();var actual=States[baseline.DeviceId];
            if(actual.Device!=device||actual.Mix!=mix)return new("external-change","external format preserved");
            if(RejectRestore)return new("failed","test restore failure");
            States[baseline.DeviceId]=(baseline.Device,baseline.Mix);Restores.Add(baseline.DeviceId);return new("restored");
        }
    }

    static async Task Run(Action<OutputModeSwitchTests.Fixture,Formats> test)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(()=>
        {
            var formats=new Formats();using var f=new OutputModeSwitchTests.Fixture(deviceFormats:formats);test(f,formats);
        },CancellationToken.None);
    }

    [Fact]
    public void CompleteBaselineIsKeptAcrossRetargetingAndNotReplacedWithTheLastSongFormat()
    {
        var service=new Formats();var baseline=Pcm(96000,24,32,6,0x60f);
        service.States["render-device"]=(baseline,Mix(baseline));
        using var owner=new TemporaryDeviceFormats(service);
        Assert.True(owner.Apply("render-device",Pcm(44100),out _));
        Assert.True(owner.Apply("render-device",Pcm(48000,16,16),out _));
        Assert.Equal(baseline,Assert.Single(owner.Owners).BaselineDevice);
        Assert.Empty(service.Restores);
        owner.RestoreAll("pause");
        Assert.Equal((baseline,Mix(baseline)),service.States["render-device"]);
        Assert.Equal("restored",owner.RestoreStatus);Assert.Empty(service.Leases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExternalDeviceOrMixChangeIsNeverOverwrittenByAnOldSnapshot(bool mixOnly)
    {
        var service=new Formats();using var owner=new TemporaryDeviceFormats(service);
        Assert.True(owner.Apply("render-device",Pcm(44100),out _));
        var external=mixOnly?(Pcm(44100),Mix(Pcm(96000))):(Pcm(96000),Mix(Pcm(96000)));
        service.States["render-device"]=external;
        owner.RestoreAll("pause");
        Assert.Equal(external,service.States["render-device"]);Assert.Empty(service.Restores);
        Assert.Equal("external-change",owner.RestoreStatus);Assert.Empty(owner.Owners);Assert.Empty(service.Leases);
    }

    [Fact]
    public void ASecondControllerCannotWriteOrRollBackTheFirstController()
    {
        var service=new Formats();var baseline=service.States["render-device"];
        using var first=new TemporaryDeviceFormats(service);using var second=new TemporaryDeviceFormats(service);
        Assert.True(first.Apply("render-device",Pcm(44100),out _));
        Assert.False(second.Apply("render-device",Pcm(96000),out var diagnostic));Assert.Equal("another owner",diagnostic);
        second.RestoreAll("pause");Assert.Equal(Pcm(44100),service.States["render-device"].Device);
        first.RestoreAll("pause");Assert.Equal(baseline,service.States["render-device"]);
        Assert.True(second.Apply("render-device",Pcm(96000),out _));second.RestoreAll("exit");
        Assert.Equal(baseline,service.States["render-device"]);
    }

    [Fact]
    public void RejectedWriteReleasesANewLeaseAndFailedRestorationKeepsARetryableBaseline()
    {
        var service=new Formats();var baseline=service.States["render-device"];
        using var owner=new TemporaryDeviceFormats(service);
        service.RejectWrite=true;
        Assert.False(owner.Apply("render-device",Pcm(44100),out _));Assert.Empty(service.Leases);Assert.Empty(owner.Owners);
        service.RejectWrite=false;Assert.True(owner.Apply("render-device",Pcm(44100),out _));
        service.RejectRestore=true;owner.RestoreAll("pause");
        Assert.Equal("failed",owner.RestoreStatus);Assert.NotNull(owner.RestoreDiagnostic);Assert.Single(owner.Owners);
        service.RejectRestore=false;owner.RestoreAll("pause");
        Assert.Equal(baseline,service.States["render-device"]);Assert.Empty(service.Leases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ExplicitPauseRestoresAfterClosingTheStreamAndResumeKeepsCuePositionAndQueue(bool toggle)=>Run((f,service)=>
    {
        var baseline=service.States["render-device"];var queue=f.Player.Queue.Items.ToArray();
        f.Player.SetAutomaticAudioFormat(true);f.Ready();
        service.BeforeRestore=()=>Assert.True(f.Current.Disposed);
        if(toggle)f.Player.PlayPause();else f.Player.Pause();
        Assert.Equal(baseline,service.States["render-device"]);Assert.False(f.Player.IsPlaying);Assert.True(f.Player.PausedOutputReleased);
        Assert.Equal(12.345,f.Player.Position.TotalSeconds,3);Assert.Equal(queue,f.Player.Queue.Items);Assert.True(f.Player.CanSeek);
        Assert.False(f.Player.OutputResampled);Assert.False(f.Player.AudioFormatApplied);
        f.Player.SeekBy(TimeSpan.FromSeconds(5));Assert.Equal(17.345,f.Player.Position.TotalSeconds,3);
        if(toggle)f.Player.PlayPause();else f.Player.Resume();
        var load=Assert.Single(f.Current.Commands,c=>c[0]=="loadfile");
        var start=load[4].Split(',').Single(o=>o.StartsWith("start="))[6..];
        Assert.Equal(77.345,double.Parse(start,System.Globalization.CultureInfo.InvariantCulture),6); // CUE-relative decoder start
        f.Ready();
        Assert.True(f.Player.IsPlaying);Assert.Equal(17.345,f.Player.Position.TotalSeconds,3);Assert.Equal(Pcm(44100),service.States["render-device"].Device);
        Assert.Equal(baseline.Device,Assert.Single(f.Player.TemporaryFormats).BaselineDevice);
        f.Player.Pause();Assert.Equal(baseline,service.States["render-device"]);
    });

    [Fact]
    public Task PlaybackRevealSurvivesReleasedPauseButDoesNotRepeatForInternalOutputRebuilds()=>Run((f,service)=>
    {
        var started=new List<long>();f.Player.PlaybackStarted+=track=>started.Add(track.Id);
        f.Player.SetAutomaticAudioFormat(true);f.Ready();Assert.Empty(started);
        var queue=f.Player.Queue.Items.ToArray();var position=f.Player.Position;
        f.Player.Pause();Assert.Empty(started);Assert.False(f.Player.EngineReady);
        f.Player.Resume();Assert.Empty(started);f.Ready();
        Assert.Equal(new[]{f.Player.CurrentTrack!.Id},started);
        Assert.Equal(queue,f.Player.Queue.Items);Assert.Equal(position,f.Player.Position);
        f.Player.SetAutomaticAudioFormat(false);f.Ready();
        f.Player.SetAutomaticAudioFormat(true);f.Ready();
        Assert.Single(started);
        f.Player.Pause();f.Player.Resume();f.Player.Pause();f.Ready();
        Assert.Single(started);Assert.False(f.Player.IsPlaying); // Cancel before FileLoaded cannot reveal later.
    });

    [Fact]
    public Task PausedUserSystemEditsBecomeTheNextPlayBaseline()=>Run((f,service)=>
    {
        f.Player.SetAutomaticAudioFormat(true);f.Ready();f.Player.Pause();
        var edited=(Pcm(96000,24,32),Mix(Pcm(96000)));
        service.States["render-device"]=edited;
        f.Player.Resume();f.Ready();Assert.Equal(edited.Item1,Assert.Single(f.Player.TemporaryFormats).BaselineDevice);
        f.Player.Pause();Assert.Equal(edited,service.States["render-device"]);
    });

    [Theory]
    [InlineData("main")]
    [InlineData("mini")]
    [InlineData("mcp")]
    public Task SpaceAndMcpToggleShareTheExplicitPauseFormatLifecycle(string entry) => Run((f, formats) =>
    {
        var baseline = formats.States["render-device"];
        f.Player.SetAutomaticAudioFormat(true); f.Ready();
        var queue = f.Player.Queue.Items.ToArray();
        var position = f.Player.Position;
        var index = f.Player.Queue.CurrentIndex;
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        settings.Current.AutomaticAudioFormat = true;
        using var app = new AppServices(settings, new LibraryDb(":memory:"), _ => f.Player);
        var vm = new PlayerViewModel(f.Player, app.SleepTimer, settings) { ProgressVisible = false };
        Window? window = entry == "main" ? new MainWindow(app) { ExitRequested = true } :
            entry == "mini" ? new MiniPlayerWindow(vm, false) : null;
        var clicks = 0;
        var restores = 0;
        if (window is MiniPlayerWindow mini) mini.RestoreRequested += () => restores++;
        try
        {
            window?.Show();
            // Exercise real window hookup, including a play button whose Command would double-toggle.
            var playerVm = window is MainWindow main ? main.ViewModel.Player : vm;
            var playButton = window?.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Command, playerVm.PlayPauseCommand));
            if (playButton is not null) { playButton.Click += (_, _) => clicks++; Assert.True(playButton.Focus()); }
            var old = f.Current;
            Toggle();
            Assert.True(old.Disposed);
            Assert.True(f.Player.PausedOutputReleased);
            Assert.False(f.Player.IsPlaying);
            Assert.Equal(baseline, formats.States["render-device"]);
            Assert.Equal(position, f.Player.Position);
            Assert.Equal(queue, f.Player.Queue.Items);
            Assert.Equal(index, f.Player.Queue.CurrentIndex);

            // Ordinary output/restore buttons must toggle playback without opening a menu/restoring a window.
            var ordinary = window is MainWindow mw ? mw.FindControl<Button>("OutputModeButton") :
                window?.FindControl<Button>("RestoreButton");
            if (ordinary is not null) { ordinary.Click += (_, _) => clicks++; Assert.True(ordinary.Focus()); }
            Toggle(); f.Ready();
            Assert.True(f.Player.IsPlaying);
            Assert.False(f.Player.PausedOutputReleased);
            Assert.Equal(position, f.Player.Position);
            Assert.Equal(queue, f.Player.Queue.Items);
            Assert.Equal(index, f.Player.Queue.CurrentIndex);
            Assert.Equal(Pcm(44100), formats.States["render-device"].Device);
            Assert.Equal(0, clicks);
            Assert.Equal(0, restores);
            Assert.Empty(f.Endpoint.Writes);
            if (window is MainWindow mainWindow) Assert.Null(mainWindow.ActiveOutputMenu);
            if (window is MiniPlayerWindow miniWindow) Assert.False(miniWindow.Topmost);
        }
        finally
        {
            if (window is MainWindow main) main.ViewModel.Player.ProgressVisible = false;
            vm.ProgressVisible = false;
            window?.Close();
        }

        void Toggle()
        {
            if (window is null) new McpTools(app).Call("playback", new() { ["action"] = "toggle" });
            else
            {
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            }
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public Task PlayingExternalChangeIsRespectedAtPauseAndBecomesAFreshBaselineOnResume()=>Run((f,service)=>
    {
        f.Player.SetAutomaticAudioFormat(true);f.Ready();
        var edited=(Pcm(96000),Mix(Pcm(96000)));service.States["render-device"]=edited;
        f.Player.Pause();Assert.Equal(edited,service.States["render-device"]);Assert.Equal("external-change",f.Player.FormatRestoreStatus);
        f.Player.Resume();f.Ready();f.Player.Pause();Assert.Equal(edited,service.States["render-device"]);
    });

    [Fact]
    public Task NaturalSongChangeKeepsTheBaselineAndNaturalQueueEndActuallyReleasesAndRestores()=>Run((f,service)=>
    {
        var baseline=service.States["render-device"];
        f.Player.SetRepeat(JeekMusic.Core.Queue.RepeatMode.Off);f.Player.SetAutomaticAudioFormat(true);f.Ready();
        f.Current.EndCurrent(Mpv.EndReason.Eof);Dispatcher.UIThread.RunJobs();f.Ready();
        Assert.Equal(2,f.Player.CurrentTrack!.Id);Assert.True(f.Player.IsPlaying);Assert.Empty(service.Restores);
        Assert.Equal(baseline.Device,Assert.Single(f.Player.TemporaryFormats).BaselineDevice);
        var engines=f.Engines.Count;f.Current.EndCurrent(Mpv.EndReason.Eof);Dispatcher.UIThread.RunJobs();
        Assert.Equal(engines,f.Engines.Count);Assert.True(f.Player.IsIdle);Assert.False(f.Player.IsPlaying);
        Assert.Equal(TimeSpan.Zero,f.Player.Position);Assert.Equal(2,f.Player.CurrentTrack.Id);Assert.Equal(2,f.Player.Queue.Items.Count);
        Assert.Equal(baseline,service.States["render-device"]);Assert.Equal("queue-end",f.Player.FormatRestoreReason);
    });

    [Fact]
    public Task DeviceChangeAndExclusiveReleaseOldSharedOwnershipWithoutCrossDeviceSnapshots()=>Run((f,service)=>
    {
        var first=service.States["render-device"];var second=service.States["second-device"];
        f.Player.SetAutomaticAudioFormat(true);f.Ready();f.Endpoint.Volumes["second-device"]=70;
        f.Player.SetDevice("wasapi/second-device");f.Ready();
        Assert.Equal(first,service.States["render-device"]);Assert.Equal(second.Device,Assert.Single(f.Player.TemporaryFormats).BaselineDevice);
        f.Player.SetExclusive(true);f.Current.Properties["audio-out-params/samplerate"]="44100";f.Ready();
        Assert.Equal(second,service.States["second-device"]);Assert.Empty(f.Player.TemporaryFormats);Assert.True(f.Player.AutomaticAudioFormat);
        f.Player.Pause();Assert.Equal(second,service.States["second-device"]);
    });

    [Fact]
    public Task DisablingAutomaticDoesNotRestoreUntilPauseAndEnablingWhilePausedDoesNotWrite()=>Run((f,service)=>
    {
        var baseline=service.States["render-device"];f.Player.Pause();var count=service.Writes.Count;
        f.Player.SetAutomaticAudioFormat(true);Assert.Equal(count,service.Writes.Count);Assert.Equal(baseline,service.States["render-device"]);
        f.Player.Resume();f.Ready();Assert.Equal(Pcm(44100),service.States["render-device"].Device);
        f.Player.SetAutomaticAudioFormat(false);Assert.Equal(Pcm(44100),service.States["render-device"].Device);
        f.Player.Pause();Assert.Equal(baseline,service.States["render-device"]);
        count=service.Writes.Count;f.Player.Resume();f.Ready();Assert.Equal(count,service.Writes.Count);
    });

    [Fact]
    public Task RestoreFailureKeepsQueueAndPositionAndIsVisibleInMcpUntilSuccessfulRetry()=>Run((f,service)=>
    {
        var baseline=service.States["render-device"];f.Player.SetAutomaticAudioFormat(true);f.Ready();service.RejectRestore=true;
        f.Player.Pause();Assert.Equal(12.345,f.Player.Position.TotalSeconds,3);Assert.False(f.Player.IsPlaying);Assert.Equal(2,f.Player.Queue.Items.Count);
        var state=McpTools.AudioFormatState(f.Player)["temporaryFormat"]!;
        Assert.Equal("failed",state["restoreStatus"]!.GetValue<string>());Assert.True(f.Player.HasOutputFormatWarning);
        service.RejectRestore=false;f.Player.Pause();Assert.Equal(baseline,service.States["render-device"]);Assert.False(f.Player.HasOutputFormatWarning);
    });

    [Fact]
    public Task ClearingQueueAndNormalExitRestoreWithoutRemovingOtherEndpointsConfiguration()=>Run((f,service)=>
    {
        var baseline=service.States["render-device"];var other=service.States["second-device"];
        f.Player.SetAutomaticAudioFormat(true);f.Ready();f.Player.Queue.Clear();
        Assert.Null(f.Player.CurrentTrack);Assert.Equal(baseline,service.States["render-device"]);Assert.Equal(other,service.States["second-device"]);
        f.Player.Play(null,[1],0);f.Ready();Assert.Single(f.Player.TemporaryFormats);
        f.Player.Dispose();Assert.Equal(baseline,service.States["render-device"]);Assert.Empty(service.Leases);
    });
}

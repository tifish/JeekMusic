using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JeekMusic.Automation;
using JeekMusic.Library;
using JeekMusic.Platform;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI;
using Xunit.Abstractions;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class DeviceSampleRateTests(ITestOutputHelper output)
{
    internal static EndpointAudioFormat Pcm(int rate, int valid = 24, int container = 24, int channels = 2, uint mask = 3) => new(rate, container, valid, channels, mask, "pcm");
    sealed class Formats : IDeviceAudioFormat, IExclusiveAudioFormat
    {
        public EndpointAudioFormat Actual = Pcm(48000, 16, 16);
        public bool RejectWrite, SharedAvailable = true;
        public List<EndpointAudioFormat> Supported = [Pcm(44100,16,16), Pcm(44100), Pcm(44100,24,32), Pcm(48000,16,16), Pcm(48000), Pcm(96000)];
        public List<(string Device, EndpointAudioFormat Format)> Writes { get; } = [];
        public Action? BeforeWrite;
        public bool RestoreFails { get; set; }
        public List<string> Restores { get; } = [];
        public IDisposable? TryAcquireControl(string device,out string? diagnostic) { diagnostic=null;return new Lease(); }
        sealed class Lease:IDisposable { public void Dispose(){} }
        public DeviceAudioSnapshot? CaptureSnapshot(string device,out string? diagnostic)
        {
            diagnostic=null;return new(device,Actual,Actual with{ContainerBits=32,ValidBits=32,SampleKind="float"},[],[]);
        }
        public DeviceFormatRestoreResult RestoreIfUnchanged(DeviceAudioSnapshot baseline,EndpointAudioFormat expectedDevice,EndpointAudioFormat expectedMix)
        {
            if(Actual!=expectedDevice)return new("external-change");
            if(RestoreFails)return new("failed","test restore failure");
            Actual=baseline.Device;Restores.Add(baseline.DeviceId);return new("restored");
        }
        public DeviceAudioCapabilities Capture(string? device, int? rate, int channels) => CaptureFormat(device, new(rate,null,channels,3,"stereo","unknown"));
        public DeviceAudioCapabilities CaptureFormat(string? device, SourceAudioFormat source) => new(device?.Replace("wasapi/", "") ?? "render-device",
            Actual.SampleRate, Actual.Channels, Supported.Select(f=>f.SampleRate).Distinct().ToArray(), Supported.Select(f=>f.SampleRate).Distinct().ToArray(),
            SharedAvailable, SharedAvailable ? null : "IPolicyConfig unavailable", Actual, Actual with {ContainerBits=32,ValidBits=32,SampleKind="float"}, Supported);
        public bool SetSharedRate(string device,int rate,out string? diagnostic) => SetSharedFormat(device, Actual with {SampleRate=rate},out diagnostic);
        public bool SetSharedFormat(string device,EndpointAudioFormat format,out string? diagnostic)
        {
            BeforeWrite?.Invoke(); Writes.Add((device,format));
            diagnostic=RejectWrite ? "SetDeviceFormat: 0x80004001" : null;
            if(!RejectWrite) Actual=format;
            return !RejectWrite;
        }
        public int? ReadMixRate(string device)=>Actual.SampleRate;
        public string? Select(string? device,int sampleRate,int channels,int? sourceBits)=>"s32";
    }
    static async Task Run(Action<OutputModeSwitchTests.Fixture,Formats> test,int? initialBits=null,int nextRate=96000)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(()=>
        {
            var formats=new Formats();
            using var f=new OutputModeSwitchTests.Fixture(formats:formats,deviceFormats:formats,initialBits:initialBits,nextRate:nextRate);
            test(f,formats);
        },CancellationToken.None);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task AutoClosesOwnStreamBeforeCompleteSystemWriteAndPreservesCuePositionAndPause(bool paused)=>Run((f,formats)=>
    {
        if(paused){f.Player.Pause();Dispatcher.UIThread.RunJobs();}
        var old=f.Current; formats.BeforeWrite=()=>Assert.True(old.Disposed);
        Assert.True(f.Player.SetAutomaticAudioFormat(true));
        if(paused)
        {
            Assert.Empty(formats.Writes);Assert.False(f.Player.IsPlaying);
            Assert.Equal(48000,formats.Actual.SampleRate);
            f.Player.Resume();
        }
        f.Ready();
        Assert.Equal(Pcm(44100),Assert.Single(formats.Writes).Format);
        Assert.Equal(12.345,f.Player.Position.TotalSeconds,3);
        Assert.True(f.Player.IsPlaying);
        Assert.True(f.Player.SampleRateApplied);
        Assert.Equal(24,f.Player.ActualDeviceFormat!.ValidBits);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public Task SameRateDifferentBitDepthIsNotSkippedAndEverySongReconcilesExternalChanges()=>Run((f,formats)=>
    {
        f.Player.SetAutomaticAudioFormat(true); f.Ready();
        Assert.Equal(Pcm(44100,16,16),formats.Actual);
        Assert.Single(f.Current.Commands,c=>c[0]=="loadfile");
        formats.Actual=Pcm(48000,16,16); // a real external/test reset must not survive a same-rate next song
        f.Player.RefreshOutput(forceRateRefresh:true);
        Assert.False(f.Player.SampleRateApplied);
        f.Current.EndCurrent(Mpv.EndReason.Eof);Dispatcher.UIThread.RunJobs();f.Ready();
        Assert.Equal(2,f.Player.CurrentTrack!.Id);
        Assert.Equal(Pcm(44100),formats.Actual);
        Assert.True(f.Player.SampleRateApplied);
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(2,formats.Writes.Count);
    },initialBits:16,nextRate:44100);

    [Fact]
    public Task FailedWriteKeepsAutomaticPreferenceAndUsablePlaybackWithActualFormat()=>Run((f,formats)=>
    {
        formats.RejectWrite=true;
        Assert.False(f.Player.SetAutomaticAudioFormat(true));f.Ready();
        Assert.True(f.Player.AutomaticAudioFormat);
        Assert.Equal(48000,f.Player.ActualSampleRate);
        Assert.False(f.Player.SampleRateApplied);
        Assert.True(f.Player.IsPlaying);
        Assert.NotNull(f.Player.SampleRateDiagnostic);
        Assert.Single(formats.Writes);
        formats.RejectWrite=false;
        f.Player.Next();f.Ready();
        Assert.Equal(Pcm(96000),formats.Actual);
        Assert.True(f.Player.SampleRateApplied);
    });

    [Fact]
    public Task DisablingDoesNotWriteRestoreSnapshotOrReloadPlayback()=>Run((f,formats)=>
    {
        f.Player.SetAutomaticAudioFormat(true);f.Ready();
        var old=f.Current;var count=formats.Writes.Count;
        Assert.True(f.Player.SetAutomaticAudioFormat(false));
        Assert.Same(old,f.Current);Assert.False(old.Disposed);
        Assert.Equal(count,formats.Writes.Count);
        Assert.Equal(Pcm(44100),formats.Actual);
        Assert.True(f.Player.IsPlaying);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemoteEndpointWithoutExclusiveTuplesKeepsSharedPlaybackAndAutomaticPreference(bool policyAvailable)=>Run((f,formats)=>
    {
        const string remote="{3.0.0.00000001}.{6C26BA7D-F0B2-4225-B422-8168C5261E45}";
        formats.Supported.Clear();
        formats.SharedAvailable=policyAvailable;
        f.Endpoint.Volumes[remote]=37;
        f.Endpoint.DeviceId=remote;
        f.Player.RefreshOutput();f.Ready();
        f.Player.SetAutomaticAudioFormat(true);f.Ready();
        Assert.True(f.Player.IsPlaying);
        Assert.True(f.Player.AutomaticAudioFormat);
        Assert.False(f.Player.Exclusive);
        Assert.Equal(remote,f.Player.ActualDeviceId);
        Assert.Equal("wasapi/"+remote,f.Current.Properties["audio-device"]);
        Assert.Equal(12.345,f.Player.Position.TotalSeconds,3);
        Assert.Equal(48000,f.Player.ActualSampleRate);
        Assert.True(f.Player.OutputResampled);
        Assert.NotNull(f.Player.SampleRateDiagnostic);
        Assert.Empty(formats.Writes);
        Assert.Empty(f.Endpoint.Writes);
        f.Player.Pause();
        Assert.True(f.Player.IsIdle);
        Assert.False(f.Player.OutputResampled);
        var paused=McpTools.AudioFormatState(f.Player);
        Assert.Null(paused["actualDevice"]);
        Assert.Equal(48000,paused["windowsDeviceDefault"]!["sampleRate"]!.GetValue<int>());
        f.Player.Resume();f.Ready();
        Assert.True(f.Player.IsPlaying);
        Assert.Equal(12.345,f.Player.Position.TotalSeconds,3);
    });

    [Fact]
    public Task ExclusiveAlwaysUsesEachNativeRateAndDoesNotChangeSharedAutomaticPreference()=>Run((f,formats)=>
    {
        f.Player.SetAutomaticAudioFormat(true);f.Ready();var count=formats.Writes.Count;
        f.Player.SetExclusive(true);f.Current.Properties["audio-out-params/samplerate"]="44100";f.Ready();
        var loads=f.Current.Commands.Where(c=>c[0]=="loadfile").ToArray();
        Assert.Contains("audio-samplerate=44100",loads[0][4]);
        Assert.Contains("audio-samplerate=96000",loads[1][4]);
        Assert.True(f.Player.AutomaticAudioFormat);Assert.Equal(count,formats.Writes.Count);
        Assert.Null(f.Player.ActualDeviceFormat); // never call a float client stream hardware 32-bit
        f.Player.SetExclusive(false);f.Ready();Assert.True(f.Player.SampleRateApplied);
    });

    [Fact]
    public Task DeviceSwitchReprobesCompleteFormatsAndKeepsPosition()=>Run((f,formats)=>
    {
        f.Player.SetAutomaticAudioFormat(true);f.Ready();
        formats.Actual=Pcm(48000,16,16);f.Endpoint.Volumes["second-device"]=70;
        f.Player.SetDevice("wasapi/second-device");f.Ready();
        Assert.Equal("second-device",formats.Writes[^1].Device);
        Assert.Equal(Pcm(44100),formats.Actual);
        Assert.Equal(12.345,f.Player.Position.TotalSeconds,3);
        Assert.Empty(f.Endpoint.Writes);
    });

    [Fact]
    public Task McpAndMenuExposeOnlyAutomaticControlsAndReadActualEffectiveBits()=>Run((f,formats)=>
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");var settings=new SettingsStore(path);
        OutputSettingsBinding.Attach(f.Player,settings);
        using var app=new AppServices(settings,new LibraryDb(":memory:"),_=>f.Player);
        var mcp=new McpTools(app);
        mcp.Call("set_output",new(){["automaticFormat"]=true});f.Ready();
        Assert.True(settings.Current.AutomaticAudioFormat);
        var state=mcp.Call("get_output",new())["audioFormat"]!;
        Assert.Equal(24,state["actualDevice"]!["validBits"]!.GetValue<int>());
        Assert.Null(state["source"]!["bits"]);Assert.True(state["applied"]!.GetValue<bool>());
        Assert.Throws<ArgumentException>(()=>mcp.Call("set_output",new(){["exclusive"]=true,["automaticFormat"]=false}));
        var window=new MainWindow(app){ExitRequested=true};window.ViewModel.Player.ProgressVisible=false;
        try
        {
            var menu=window.CreateOutputMenu();
            Assert.All(menu.Items.OfType<MenuItem>(),i=>Assert.Empty(i.Items));
            var shared=menu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,L.T("共享模式")));
            var sharedAuto=menu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,L.T("共享模式自动匹配歌曲格式")));
            Assert.True(sharedAuto.IsChecked);Assert.True(sharedAuto.IsEnabled);
            Assert.True(sharedAuto.Margin.Left>shared.Margin.Left);
            Assert.Equal(menu.Items.IndexOf(shared)+1,menu.Items.IndexOf(sharedAuto));
            Assert.Contains(menu.Items.OfType<MenuItem>(),i=>i.Header is TextBlock text&&text.Text?.Contains("24")==true&&text.MaxWidth==480);
            Assert.Equal("WAV · 44.1 kHz → 44.1 kHz "+L.T("共享"),window.ViewModel.Player.FormatText);
            Assert.False(f.Player.OutputResampled);
            Assert.False(f.Player.HasOutputFormatWarning);
            formats.Actual=Pcm(48000,24,32);f.Player.RefreshOutput(forceRateRefresh:true);
            Assert.Equal("WAV · 44.1 kHz → 48 kHz "+L.T("共享")+L.T("（已重采样）")+" ⚠",window.ViewModel.Player.FormatText);
            Assert.Contains(window.ViewModel.Player.FormatText,window.ViewModel.Player.OutputModeTip);
            var output=mcp.Call("get_output",new())["audioFormat"]!;
            Assert.Equal(window.ViewModel.Player.FormatText,output["displaySummary"]!.GetValue<string>());
            Assert.True(output["resampled"]!.GetValue<bool>());
            Assert.True(output["hasWarning"]!.GetValue<bool>());
            Assert.Contains("24",AudioFormatDisplay.Device(f.Player));Assert.Contains("32",AudioFormatDisplay.Device(f.Player));
            Assert.Contains(L.T("位深未知"),AudioFormatDisplay.Source(f.Player));
            var automatic=window.CreateOutputMenu().Items.OfType<MenuItem>().Single(i=>Equals(i.Header,L.T("共享模式自动匹配歌曲格式")));
            automatic.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.False(f.Player.AutomaticAudioFormat);
            f.Player.SetAutomaticAudioFormat(true);f.Ready();
            f.Player.SetExclusive(true);f.Current.Properties["audio-out-params/samplerate"]="44100";f.Ready();
            var exclusiveMenu=window.CreateOutputMenu();
            var inactiveSharedAuto=exclusiveMenu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,L.T("共享模式自动匹配歌曲格式")));
            Assert.True(inactiveSharedAuto.IsChecked);Assert.False(inactiveSharedAuto.IsEnabled);
            var sharedChoice=exclusiveMenu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,L.T("共享模式")));
            sharedChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));f.Ready();
            Assert.False(f.Player.Exclusive);Assert.True(f.Player.AutomaticAudioFormat);
        }
        finally{window.Close();}
        settings.WriteNow();Assert.True(new SettingsStore(path).Current.AutomaticAudioFormat);File.Delete(path);
    });

    [Theory]
    [InlineData("manual",false)] [InlineData("system",false)] [InlineData("auto",true)]
    public async Task UnreleasedManualPreferencesMigrateWithoutWritingOldManualRate(string legacy,bool expected)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(()=>
        {
            var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
            File.WriteAllText(path,"{\"sampleRateMode\":\""+legacy+"\",\"manualSampleRate\":96000}");
            var settings=new SettingsStore(path);
            using var f=new OutputModeSwitchTests.Fixture(settings,deviceFormats:new Formats());
            Assert.Equal(expected,f.Player.AutomaticAudioFormat);
            Assert.Null(settings.Current.SampleRateMode);Assert.Null(settings.Current.ManualSampleRate);
            settings.WriteNow();Assert.DoesNotContain("manualSampleRate",File.ReadAllText(path));File.Delete(path);
        },CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReleasedOutputDoesNotCompareSourceWithIdleDeviceDefault(bool exclusive) => Run((f, formats) =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        using var app = new AppServices(new SettingsStore(path), new LibraryDb(":memory:"), _ => f.Player);
        var window = new MainWindow(app) { ExitRequested = true };
        var vm = window.ViewModel.Player;
        vm.ProgressVisible = false;
        var mcp = new McpTools(app);
        try
        {
            if (!exclusive) SaveFormatWindow(window, "shared-playing-mismatch");
            f.Player.SetAutomaticAudioFormat(true); f.Ready();
            if (exclusive) { f.Player.SetExclusive(true); f.Current.Properties["audio-out-params/samplerate"] = "44100"; f.Ready(); }
            Assert.Contains("44.1 kHz → 44.1 kHz", vm.FormatText);
            var position = f.Player.Position;
            f.Player.Pause(); Dispatcher.UIThread.RunJobs();
            Assert.True(f.Player.IsIdle); Assert.False(f.Player.IsPlaying);
            Assert.Equal(48000, formats.Actual.SampleRate);
            Assert.Equal(position, f.Player.Position);
            Assert.Contains("44.1 kHz", vm.FormatText);
            Assert.Contains(L.T(" · 输出空闲"), vm.FormatText);
            Assert.DoesNotContain("→", vm.FormatText);
            Assert.DoesNotContain(L.T("（已重采样）"), vm.FormatText);
            Assert.DoesNotContain("⚠", vm.FormatText);
            Assert.Contains(L.T("设备默认（输出空闲）：") + "48 kHz", vm.OutputModeTip);
            Assert.Contains(L.T("当前没有活动音频输出，设备默认格式不表示正在重采样。"), vm.OutputModeTip);
            var idle = mcp.Call("get_output", new());
            var audio = idle["audioFormat"]!;
            Assert.Equal("idle", audio["outputState"]!.GetValue<string>());
            Assert.Equal("idle", idle["sampleRate"]!["outputState"]!.GetValue<string>());
            Assert.Null(audio["actualDevice"]); Assert.Null(idle["sampleRate"]!["actualRate"]);
            Assert.False(audio["resampled"]!.GetValue<bool>()); Assert.False(audio["applied"]!.GetValue<bool>());
            Assert.False(idle["format"]!["resampled"]!.GetValue<bool>());
            Assert.False(audio["hasWarning"]!.GetValue<bool>());
            Assert.Equal(48000, audio["windowsDeviceDefault"]!["sampleRate"]!.GetValue<int>());
            Assert.Equal(vm.FormatText, audio["displaySummary"]!.GetValue<string>());
            Assert.Contains(window.CreateOutputMenu().Items.OfType<MenuItem>(), item => item.Header is TextBlock text &&
                text.Text!.Contains(L.T("设备默认（输出空闲）：") + "48 kHz"));
            SaveFormatWindow(window, exclusive ? "exclusive-output-idle" : "shared-output-idle");

            f.Player.Resume();
            Assert.Contains("44.1 kHz → …", vm.FormatText); // StateChanged refreshes pending display immediately.
            Assert.DoesNotContain(L.T(" · 输出空闲"), vm.FormatText);
            f.Current.Properties["audio-out-params/samplerate"] = "44100"; f.Ready();
            Assert.True(f.Player.IsPlaying); Assert.Equal(position, f.Player.Position);
            Assert.Contains("44.1 kHz → 44.1 kHz", vm.FormatText);
            Assert.Equal("active", McpTools.AudioFormatState(f.Player)["outputState"]!.GetValue<string>());
            SaveFormatWindow(window, exclusive ? "exclusive-resumed-matched" : "shared-resumed-matched");

            f.Player.Next();
            Assert.Contains("96 kHz → …", vm.FormatText);
            f.Current.Properties["audio-params/samplerate"] = "96000";
            f.Current.Properties["audio-out-params/samplerate"] = "96000"; f.Ready();
            Assert.Contains("96 kHz → 96 kHz", vm.FormatText);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task IdleRestoreFailureRemainsAWarningButNotAResamplingClaim() => Run((f, formats) =>
    {
        f.Player.SetAutomaticAudioFormat(true); f.Ready();
        formats.RestoreFails = true;
        f.Player.Pause();
        Assert.True(f.Player.IsIdle);
        Assert.Contains("⚠", AudioFormatDisplay.Summary(f.Player));
        Assert.DoesNotContain("→", AudioFormatDisplay.Summary(f.Player));
        Assert.False(f.Player.OutputResampled);
        Assert.Contains("test restore failure", AudioFormatDisplay.Warning(f.Player));
        Assert.True(McpTools.AudioFormatState(f.Player)["hasWarning"]!.GetValue<bool>());
    });

    [Fact]
    public Task EnginePauseWithALoadedStreamIsNotMisreportedAsReleasedOutput() => Run((f, formats) =>
    {
        f.Current.TrySet("pause", "yes"); Dispatcher.UIThread.RunJobs();
        Assert.False(f.Player.IsPlaying); Assert.False(f.Player.IsIdle);
        Assert.Equal("active", AudioFormatDisplay.OutputState(f.Player));
        Assert.Contains("44.1 kHz → 48 kHz", AudioFormatDisplay.Summary(f.Player));
        Assert.True(f.Player.OutputResampled);
        Assert.DoesNotContain(L.T(" · 输出空闲"), AudioFormatDisplay.Summary(f.Player));
    });

    static void SaveFormatWindow(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("JEEKMUSIC_VISUAL_OUTPUT") is not { Length: > 0 } path) return;
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        window.Background = Avalonia.Media.Brushes.White;
        window.Width = 1100; window.Height = 600;
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame); Directory.CreateDirectory(path);
        frame.Save(Path.Combine(path, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [Theory]
    [InlineData("mp3",16,"floatp",null,"lossy-not-applicable")]
    [InlineData("pcm_f32le",32,"float",null,"float32")]
    [InlineData("pcm_f64le",64,"double",null,"float64")]
    [InlineData("flac",24,"floatp",24,"integer-metadata")]
    [InlineData("flac",null,"s32",null,"unknown")]
    public async Task SourcePrecisionComesFromTheSourceCodecRatherThanTheDecodedFloatContainer(string codec,int? metadataBits,
        string decoderFormat,int? expectedBits,string expectedPrecision)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(()=>
        {
            using var f=new OutputModeSwitchTests.Fixture(initialBits:metadataBits,configureEngine:engine=>
            {
                engine.Properties["audio-codec-name"]=codec;
                engine.Properties["audio-params/format"]=decoderFormat;
            });
            Assert.Equal(expectedBits,f.Player.SourceFormat.Bits);
            Assert.Equal(expectedPrecision,f.Player.SourceFormat.Precision);
            var source=McpTools.AudioFormatState(f.Player)["source"]!;
            Assert.Equal(expectedPrecision,source["precision"]!.GetValue<string>());
            Assert.Equal(expectedBits,source["bits"]?.GetValue<int>());
        },CancellationToken.None);
    }

    [Fact]
    public Task SummaryKeepsFilePrecisionModeAndSafetyWarningsDuringExclusiveReload()=>Run((f,formats)=>
    {
        Assert.Contains(f.Player.CurrentTrack!.FormatLabel,AudioFormatDisplay.Summary(f.Player));
        Assert.Contains("24/44.1",AudioFormatDisplay.Summary(f.Player));
        Assert.Contains(L.T("共享"),AudioFormatDisplay.Summary(f.Player));
        f.Endpoint.HardwareVolume=false;
        f.Player.SetExclusive(true);
        Assert.Contains(L.T("独占"),AudioFormatDisplay.Summary(f.Player));
        Assert.Contains("44.1 kHz → …",AudioFormatDisplay.Summary(f.Player));
        Assert.DoesNotContain(L.T("（已重采样）"),AudioFormatDisplay.Summary(f.Player));
        f.Current.Properties["audio-out-params/samplerate"]="44100";f.Ready();
        Assert.True(f.Player.HasOutputFormatWarning);
        Assert.Contains("⚠",AudioFormatDisplay.Summary(f.Player));
        Assert.Equal(f.Player.OutputSafetyMessage,AudioFormatDisplay.Warning(f.Player));
        f.Player.Resume();
        Assert.Contains("44.1 kHz → 44.1 kHz",AudioFormatDisplay.Summary(f.Player));
        Assert.Contains(L.T("独占"),AudioFormatDisplay.Summary(f.Player));
        Assert.False(f.Player.OutputResampled);
        Assert.False(f.Player.HasOutputFormatWarning);
    },initialBits:24);

    [Fact]
    public Task SummaryReportsClientResamplingEvenWhenFinalDeviceRateMatchesTheSource()=>Run((f,formats)=>
    {
        formats.Actual=Pcm(44100,16,16);
        f.Current.Properties["audio-params/samplerate"]="44100";
        f.Current.Properties["audio-out-params/samplerate"]="48000";
        f.Current.Emit(Mpv.EventId.AudioReconfig);Dispatcher.UIThread.RunJobs();
        Assert.Equal(44100,f.Player.ActualSampleRate);
        Assert.True(f.Player.OutputResampled);
        Assert.Contains("44.1 kHz → 44.1 kHz",AudioFormatDisplay.Summary(f.Player));
        Assert.Contains(L.T("（已重采样）"),AudioFormatDisplay.Summary(f.Player));
        Assert.False(f.Player.HasOutputFormatWarning);
    });

    [Fact]
    public async Task DecoderKnownSideLayoutReconfiguresBeforeUnmutingWithoutRepeatingTheReload()
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(()=>
        {
            var formats=new Formats();var side=Pcm(44100,24,32,6,0x60f);formats.Supported.Add(side);
            var settings=new SettingsStore(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"));settings.Current.AutomaticAudioFormat=true;
            using var f=new OutputModeSwitchTests.Fixture(settings,formats:formats,deviceFormats:formats,configureEngine:engine=>
            {
                engine.Properties["audio-params/channels"]="5.1(side)";
                engine.Properties["audio-params/channel-count"]="6";
            });
            f.Ready();
            Assert.Equal(side,formats.Actual);
            Assert.Single(formats.Writes); // startup pause only reads metadata; first play uses the decoded side layout
            Assert.Equal(0x60fu,f.Player.SourceFormat.ChannelMask);
            Assert.True(f.Player.SampleRateApplied);
            Assert.True(f.Player.IsPlaying);
            Assert.Equal("auto-safe",f.Current.Properties["audio-channels"]);
            f.Ready();Assert.Single(formats.Writes);
        },CancellationToken.None);
    }

    [Fact]
    public void PolicyProbesCompleteTuplesAndDistinguishesEffectiveFromContainerBits()
    {
        var current=Pcm(48000,16,16);
        var source=new SourceAudioFormat(44100,24,2,3,"stereo","integer-metadata");
        var valid24in32=Pcm(44100,24,32);
        var supported=new[]{Pcm(44100,16,16),valid24in32,Pcm(96000,32,32)};
        Assert.Equal(valid24in32,AudioFormatPolicy.Choose(source,current,supported).Target);
        Assert.Equal(24,valid24in32.IntegerPrecision);
        Assert.Equal(valid24in32,AudioFormatPolicy.Choose(source with{Bits=null,Precision="unknown"},current,supported).Target);
        Assert.Equal(Pcm(44100,16,16),AudioFormatPolicy.Choose(source with{Bits=16},current,supported).Target);
        var reduced=AudioFormatPolicy.Choose(source,current,[Pcm(44100,16,16)]);
        Assert.Contains("Precision conversion",reduced.Conversion);
    }

    [Fact]
    public void PolicyKeepsSideAndBackLayoutsDistinctAndUnknownLayoutsPreserveSpeakerConfiguration()
    {
        var current=Pcm(48000,24,24);
        var source=new SourceAudioFormat(44100,24,6,0x60f,"5.1(side)","integer-metadata");
        var side=Pcm(44100,24,32,6,0x60f);var back=Pcm(44100,24,24,6,0x3f);
        Assert.Equal(side,AudioFormatPolicy.Choose(source,current,[side,back,Pcm(44100)]).Target);
        var fallback=AudioFormatPolicy.Choose(source,current,[back,Pcm(44100)]);
        Assert.Equal(Pcm(44100),fallback.Target);Assert.Contains("Channel conversion",fallback.Conversion);
        var unknown=AudioFormatPolicy.Choose(source with{ChannelMask=null,Layout=null},current,[side,Pcm(44100)]);
        Assert.Equal(Pcm(44100),unknown.Target);Assert.Contains("unknown",unknown.Conversion);
        Assert.Null(ChannelLayouts.Mask("6 channels"));Assert.NotEqual(ChannelLayouts.Mask("5.1"),ChannelLayouts.Mask("5.1(side)"));
    }

    [RealAudioFact]
    public void RealEndpointCompleteFormatIsIndependentlyReadBackAndFullOriginalRestored()
    {
        Assert.Empty(System.Diagnostics.Process.GetProcessesByName("JeekMusic"));
        var service=new DeviceAudioFormat();var source=new SourceAudioFormat(44100,null,2,3,"stereo","unknown");
        var caps=service.CaptureFormat(null,source);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(caps));
        Assert.NotNull(caps.DeviceId);Assert.NotNull(caps.DeviceFormat);
        var original=service.CaptureSystemFormat(caps.DeviceId!);
        var plan=AudioFormatPolicy.Choose(source,caps.DeviceFormat!,caps.SupportedFormats!);Assert.NotNull(plan.Target);
        try
        {
            Assert.True(service.SetSharedFormat(caps.DeviceId!,plan.Target!,out var diagnostic),diagnostic);
            var actual=service.CaptureFormat(caps.DeviceId,source);
            Assert.Equal(plan.Target,actual.DeviceFormat);
            Assert.Equal(plan.Target!.SampleRate,actual.MixFormat!.SampleRate);
            Assert.Equal(plan.Target.ChannelMask,actual.MixFormat.ChannelMask);
            output.WriteLine($"Independent PKEY device readback: {actual.DeviceFormat}; client mix: {actual.MixFormat}");
        }
        finally
        {
            service.RestoreSystemFormat(caps.DeviceId!,original);
            var restored=service.CaptureSystemFormat(caps.DeviceId!);
            Assert.Equal(original.Endpoint,restored.Endpoint);Assert.Equal(original.Mix,restored.Mix);
        }
    }
}

public sealed class RealAudioFactAttribute:FactAttribute
{
    public RealAudioFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("JEEKMUSIC_TEST_REAL_AUDIO")!="1")
            Skip="Opt-in endpoint mutation requires no other JeekMusic client and JEEKMUSIC_TEST_REAL_AUDIO=1.";
    }
}



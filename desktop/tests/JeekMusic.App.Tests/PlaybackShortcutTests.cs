using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.UI.ViewModels;
using JeekMusic.UI;

namespace JeekMusic.App.Tests;

[CollectionDefinition("Avalonia UI")]
public sealed class AvaloniaUiCollection;

public sealed class ShortcutTestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ShortcutTestApplication>()
        // GlyphRun caches its rendering backend process-wide: use Skia consistently
        // with the real-font/layout fixtures, while keeping windows and audio simulated.
        .UseSkia().With(Typography.FontOptions())
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        Resources["ContentControlThemeFontFamily"] = Avalonia.Media.FontFamily.Default;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
    }
}

[Collection("Avalonia UI")]
public sealed class PlaybackShortcutTests
{
    [Theory]
    [InlineData(Key.Left, false)]
    [InlineData(Key.Z, true)]
    [InlineData(Key.Right, false)]
    [InlineData(Key.X, false)]
    public Task PlaybackShortcutRunsBeforeTheSongTableConsumesTheKey(Key key, bool previous) => Run(() =>
    {
        var table = new DataGrid { ItemsSource = new[] { "First", "Second" } };
        var window = new Window { Content = table };
        var previousCalls = 0;
        var nextCalls = 0;
        var seeks = new List<double>();
        PlaybackShortcuts.Attach(window, () => previousCalls++, () => nextCalls++, seeks.Add, _ => { });
        // Reproduce the child control consuming the event before the window's bubbling handler.
        table.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        try
        {
            window.Show();
            Assert.True(table.Focus());
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal(previous ? 1 : 0, previousCalls);
            Assert.Equal(key == Key.X ? 1 : 0, nextCalls);
            Assert.Equal(key == Key.Left ? new[] { -5.0 } : key == Key.Right ? new[] { 5.0 } : [], seeks);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SearchInputKeepsCaretMovementAndTypingZX() => Run(() =>
    {
        var input = new TextBox { Text = "zx" };
        var window = new Window { Content = input };
        var calls = 0;
        PlaybackShortcuts.Attach(window, () => calls++, () => calls++, _ => calls++, _ => calls++);
        try
        {
            window.Show();
            input.Focus();
            input.CaretIndex = 2;
            Press(window, Key.Left);
            Assert.Equal(1, input.CaretIndex);
            Press(window, Key.Right);
            Assert.Equal(2, input.CaretIndex);
            Press(window, Key.Z);
            window.KeyTextInput("z");
            Press(window, Key.X);
            window.KeyTextInput("x");
            Assert.Equal("zxzx", input.Text);
            Assert.Equal(0, calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SliderKeepsArrowAdjustmentsAndSupportsZXForTracks() => Run(() =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, SmallChange = 1 };
        var window = new Window { Content = slider };
        var previousCalls = 0;
        var nextCalls = 0;
        var seeks = new List<double>();
        PlaybackShortcuts.Attach(window, () => previousCalls++, () => nextCalls++, seeks.Add, _ => { });
        try
        {
            window.Show();
            slider.Focus();
            Press(window, Key.Right);
            Assert.Equal(51, slider.Value);
            Press(window, Key.Left);
            Assert.Equal(50, slider.Value);
            Assert.Equal(0, previousCalls + nextCalls);
            Assert.Empty(seeks);
            Press(window, Key.Z);
            Press(window, Key.X);
            Assert.Equal(1, previousCalls);
            Assert.Equal(1, nextCalls);
            Assert.Equal(50, slider.Value);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(Key.Left, RawInputModifiers.Control)]
    [InlineData(Key.Right, RawInputModifiers.Control)]
    [InlineData(Key.Left, RawInputModifiers.Alt)]
    [InlineData(Key.Right, RawInputModifiers.Alt)]
    [InlineData(Key.Left, RawInputModifiers.Control | RawInputModifiers.Alt)]
    [InlineData(Key.Right, RawInputModifiers.Control | RawInputModifiers.Alt)]
    [InlineData(Key.Left, RawInputModifiers.Shift)]
    [InlineData(Key.Right, RawInputModifiers.Meta)]
    [InlineData(Key.Up, RawInputModifiers.Control)]
    [InlineData(Key.Down, RawInputModifiers.Alt)]
    [InlineData(Key.Up, RawInputModifiers.Shift)]
    [InlineData(Key.Down, RawInputModifiers.Meta)]
    [InlineData(Key.Z, RawInputModifiers.Shift)]
    [InlineData(Key.X, RawInputModifiers.Control)]
    public Task ModifiedKeysDoNotTriggerTrackShortcuts(Key key, RawInputModifiers modifiers) => Run(() =>
    {
        var button = new Button();
        var window = new Window { Content = button };
        var calls = 0;
        PlaybackShortcuts.Attach(window, () => calls++, () => calls++, _ => calls++, _ => calls++);
        try
        {
            window.Show();
            button.Focus();
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
            Assert.Equal(0, calls);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProgressAndVolumeSlidersRouteExactlyOnce(bool progressFocused) => Run(() =>
    {
        var progress = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = 50, SmallChange = 1 };
        var window = new Window { Content = new StackPanel { Children = { progress, volume } } };
        var seeks = new List<double>();
        var levels = new List<double>();
        PlaybackShortcuts.Attach(window, () => { }, () => { }, seeks.Add, levels.Add, progress, volume);
        try
        {
            window.Show();
            (progressFocused ? progress : volume).Focus();
            Press(window, Key.Right);
            Assert.Equal(progressFocused ? new[] { 5.0 } : [], seeks);
            Assert.Equal(progressFocused ? 50 : 51, volume.Value);
            Press(window, Key.Up);
            Press(window, Key.Down);
            Assert.Equal(new[] { 5.0, -5.0 }, levels);
            Assert.Equal(50, progress.Value);
            Assert.Equal(progressFocused ? 50 : 51, volume.Value);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("list")]
    [InlineData("table")]
    [InlineData("tree")]
    [InlineData("combo")]
    [InlineData("numeric")]
    [InlineData("menu")]
    public Task NavigationAndEditingControlsKeepUpDown(string kind) => Run(() =>
    {
        Control control = kind switch
        {
            "list" => new ListBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            "table" => new DataGrid { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            "tree" => new TreeView { Items = { new TreeViewItem { Header = "One", Items = { new TreeViewItem { Header = "Two" } } } } },
            "combo" => new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            "numeric" => new NumericUpDown { Value = 5 },
            _ => new Menu { Items = { new MenuItem { Header = "File", Items = { new MenuItem { Header = "Open" } } } } },
        };
        var window = new Window { Content = control };
        var calls = 0;
        PlaybackShortcuts.Attach(window, () => calls++, () => calls++, _ => calls++, _ => calls++);
        try
        {
            window.Show();
            foreach (var key in new[] { Key.Up, Key.Down })
            {
                var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
                control.RaiseEvent(e);
                Assert.Equal(0, calls);
            }
            if (control is ListBox list)
            {
                list.Focus();
                Press(window, Key.Down);
                Assert.Equal(1, list.SelectedIndex);
            }
            if (control is TreeView tree)
            {
                var item = (TreeViewItem)tree.Items[0]!;
                item.Focus();
                Press(window, Key.Right);
                Assert.True(item.IsExpanded);
                Press(window, Key.Left);
                Assert.False(item.IsExpanded);
            }
            if (kind is "menu" or "combo" or "numeric")
                foreach (var key in new[] { Key.Left, Key.Right, Key.Z, Key.X })
                    control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            Assert.Equal(0, calls);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ServiceShortcutsAccumulateAndClampCueSeekWithoutChangingPauseOrQueue(bool paused) => Run(() =>
    {
        using var f = new OutputModeSwitchTests.Fixture();
        if (paused) { f.Player.Pause(); Dispatcher.UIThread.RunJobs(); }
        var button = new Button();
        var window = new Window { Content = button };
        PlaybackShortcuts.Attach(window, f.Player);
        var completed = 0;
        f.Player.TrackCompleted += () => completed++;
        try
        {
            window.Show(); button.Focus();
            // Fake decoder deliberately leaves time-pos stale until completion.
            Press(window, Key.Right); Press(window, Key.Right); Press(window, Key.Left);
            Assert.Equal(17.345, f.Player.Position.TotalSeconds, 3);
            Assert.Equal(new[] { "77.345", "82.345", "77.345" }, f.Current.Commands.Where(c => c[0] == "seek").Select(c => c[1]));
            f.Current.Properties["time-pos"] = "77.345";
            Assert.Equal(17.345, f.Player.Position.TotalSeconds, 3);
            f.Current.Properties["time-pos"] = "78.345";
            Assert.Equal(18.345, f.Player.Position.TotalSeconds, 3);
            for (var i = 0; i < 5; i++) Press(window, Key.Left);
            Assert.Equal(0, f.Player.Position.TotalSeconds);
            for (var i = 0; i < 30; i++) Press(window, Key.Right);
            Assert.Equal(119, f.Player.Position.TotalSeconds, 3);
            Assert.Equal("179", f.Current.Commands.Last(c => c[0] == "seek")[1]);
            Assert.Equal(!paused, f.Player.IsPlaying);
            Assert.Equal(paused ? "yes" : "no", f.Current.Properties["pause"]);
            Assert.Equal(1, f.Player.CurrentTrack!.Id);
            Assert.Equal(0, completed);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task UnavailableSeekDoesNotLoadSkipOrOpenTheOutputGate() => Run(() =>
    {
        using var f = new OutputModeSwitchTests.Fixture();
        var count = f.Current.Commands.Count;
        f.Current.Properties["seekable"] = "no";
        f.Player.SeekBy(TimeSpan.FromSeconds(5));
        Assert.Equal(count, f.Current.Commands.Count);
        f.Current.Properties["seekable"] = "yes";
        f.Current.RejectSeek = true;
        f.Player.SeekBy(TimeSpan.FromSeconds(5));
        Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        f.Current.RejectSeek = false;
        f.Endpoint.FailCapture = true;
        f.Player.RefreshOutput();
        count = f.Current.Commands.Count;
        f.Player.SeekBy(TimeSpan.FromSeconds(5));
        Assert.Equal(count, f.Current.Commands.Count);
        Assert.True(f.Player.OutputPending);
        Assert.False(f.Player.IsPlaying);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        Assert.Equal("yes", f.Current.Properties["pause"]);
        using var empty = new PlayerService(f, _ => throw new Exception("Must not create an engine"), f.Endpoint);
        empty.SeekBy(TimeSpan.FromSeconds(5));
        Assert.Null(empty.CurrentTrack);
        Assert.False(empty.EngineReady);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task VolumeShortcutsReadExternalVolumeClampAndPreserveMute(bool muted) => Run(() =>
    {
        using var f = new OutputModeSwitchTests.Fixture();
        f.Player.SetMuted(muted);
        f.Endpoint.Muted = true;
        var button = new Button();
        var window = new Window { Content = button };
        PlaybackShortcuts.Attach(window, f.Player);
        try
        {
            window.Show(); button.Focus();
            f.Endpoint.Volume = 22;
            Press(window, Key.Up); Press(window, Key.Up);
            Assert.Equal(32, f.Endpoint.Volume);
            f.Endpoint.Volume = 98;
            Press(window, Key.Up);
            Assert.Equal(100, f.Endpoint.Volume);
            f.Endpoint.Volume = 3;
            Press(window, Key.Down);
            Assert.Equal(0, f.Endpoint.Volume);
            Assert.Equal(muted, f.Player.Muted);
            Assert.True(f.Endpoint.Muted);
            Assert.Equal(muted ? "yes" : "no", f.Current.Properties["mute"]);
            Assert.Equal(4, f.Endpoint.Writes.Count);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("hardware")]
    [InlineData("read")]
    [InlineData("pending")]
    public Task VolumeShortcutsRespectUnavailableOutput(string reason) => Run(() =>
    {
        using var f = new OutputModeSwitchTests.Fixture();
        if (reason == "hardware") { f.Endpoint.HardwareVolume = false; f.Player.SetExclusive(true); f.Ready(); }
        if (reason == "read") f.Endpoint.FailCapture = true;
        if (reason == "pending") f.Player.SetExclusive(true);
        f.Player.AdjustVolume(5);
        Assert.Empty(f.Endpoint.Writes);
        Assert.False(f.Player.VolumeAvailable);
        Assert.True(f.Player.OutputPending);
        Assert.Equal("yes", f.Current.Properties["mute"]);
        Assert.Equal("yes", f.Current.Properties["pause"]);
    });

    [Fact]
    public Task MiniPlayerConstructorConnectsSeekAndVolumeToTheSharedService() => Run(() =>
    {
        using var f = new OutputModeSwitchTests.Fixture();
        var vm = new PlayerViewModel(f.Player, new SleepTimer(f.Player), new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        var window = new MiniPlayerWindow(vm, false);
        try
        {
            window.Show();
            Assert.True(window.FindControl<Button>("RestoreButton")!.Focus());
            Press(window, Key.Right);
            Assert.Equal(17.345, f.Player.Position.TotalSeconds, 3);
            Press(window, Key.Up);
            Assert.Equal(60, f.Endpoint.Volume);
            Press(window, Key.Down);
            Assert.Equal(55, f.Endpoint.Volume);
        }
        finally { window.Close(); vm.ProgressVisible = false; }
    });

    [Fact]
    public Task MainWindowConnectsProgressVolumeAndPreservesAltNavigation() => Run(() =>
    {
        using var f = new OutputModeSwitchTests.Fixture();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        using var app = new AppServices(new SettingsStore(path), new JeekMusic.Library.LibraryDb(":memory:"), _ => f.Player);
        var window = new MainWindow(app) { ExitRequested = true };
        try
        {
            window.Show();
            var table = window.FindControl<DataGrid>("Table")!;
            var tableRight = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right };
            table.RaiseEvent(tableRight);
            Assert.True(tableRight.Handled);
            Assert.Equal(17.345, f.Player.Position.TotalSeconds, 3);
            table.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            table.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
            Assert.Empty(f.Endpoint.Writes);
            var progress = window.FindControl<Slider>("Progress")!;
            Assert.True(progress.Focus());
            Press(window, Key.Right);
            Assert.Equal(17.345, f.Player.Position.TotalSeconds, 3);
            Press(window, Key.Left);
            Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
            Press(window, Key.Up);
            Assert.Equal(60, f.Endpoint.Volume);
            var volume = window.FindControl<Slider>("VolumeSlider")!;
            Assert.True(volume.Focus());
            Press(window, Key.Down);
            Assert.Equal(55, f.Endpoint.Volume);
            Assert.Equal(2, f.Endpoint.Writes.Count);

            // Exercise the actual MainWindow bubbling handler, not just the shared filter.
            window.ViewModel.Navigate(new NavTarget.Artists(null));
            window.ViewModel.Navigate(new NavTarget.Search("test"));
            var back = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left, KeyModifiers = KeyModifiers.Alt };
            window.RaiseEvent(back);
            Assert.True(back.Handled);
            Assert.IsType<NavTarget.Artists>(window.ViewModel.Current);
            var forward = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, KeyModifiers = KeyModifiers.Alt };
            window.RaiseEvent(forward);
            Assert.True(forward.Handled);
            Assert.IsType<NavTarget.Search>(window.ViewModel.Current);
            var global = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt };
            window.RaiseEvent(global);
            Assert.False(global.Handled);
            Assert.IsType<NavTarget.Search>(window.ViewModel.Current);
            Assert.Equal(12.345, f.Player.Position.TotalSeconds, 3);
        }
        finally { window.ViewModel.Player.ProgressVisible = false; window.Close(); }
    });

    static async Task Run(Action test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(test, CancellationToken.None);
    }

    static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
    }
}

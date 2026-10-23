using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using JeekMusic.UI;

namespace JeekMusic.App.Tests;

[CollectionDefinition("Avalonia UI")]
public sealed class AvaloniaUiCollection;

public sealed class ShortcutTestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ShortcutTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public override void Initialize()
    {
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
    [InlineData(Key.Left, true)]
    [InlineData(Key.Z, true)]
    [InlineData(Key.Right, false)]
    [InlineData(Key.X, false)]
    public Task TrackShortcutRunsBeforeTheSongTableConsumesTheKey(Key key, bool previous) => Run(() =>
    {
        var table = new DataGrid { ItemsSource = new[] { "First", "Second" } };
        var window = new Window { Content = table };
        var previousCalls = 0;
        var nextCalls = 0;
        PlaybackShortcuts.Attach(window, () => previousCalls++, () => nextCalls++);
        // Reproduce the child control consuming the event before the window's bubbling handler.
        table.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        try
        {
            window.Show();
            Assert.True(table.Focus());
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Equal(previous ? 1 : 0, previousCalls);
            Assert.Equal(previous ? 0 : 1, nextCalls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SearchInputKeepsCaretMovementAndTypingZX() => Run(() =>
    {
        var input = new TextBox { Text = "zx" };
        var window = new Window { Content = input };
        var calls = 0;
        PlaybackShortcuts.Attach(window, () => calls++, () => calls++);
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
        PlaybackShortcuts.Attach(window, () => previousCalls++, () => nextCalls++);
        try
        {
            window.Show();
            slider.Focus();
            Press(window, Key.Right);
            Assert.Equal(51, slider.Value);
            Press(window, Key.Left);
            Assert.Equal(50, slider.Value);
            Assert.Equal(0, previousCalls + nextCalls);
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
    [InlineData(Key.Z, RawInputModifiers.Shift)]
    [InlineData(Key.X, RawInputModifiers.Control)]
    public Task ModifiedKeysDoNotTriggerTrackShortcuts(Key key, RawInputModifiers modifiers) => Run(() =>
    {
        var button = new Button();
        var window = new Window { Content = button };
        var calls = 0;
        PlaybackShortcuts.Attach(window, () => calls++, () => calls++);
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

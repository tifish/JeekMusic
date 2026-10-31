using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.Automation;
using System.Text.Json.Nodes;
using JeekMusic.UI;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class SpacePlaybackShortcutTests
{
    [Fact]
    public void McpToggleKeepsItsExistingSchemaAndDescribesTheSharedService()
    {
        var catalog = McpTools.BuildCatalog().OfType<JsonObject>().ToArray();
        var tool = catalog.Single(t => t["name"]!.GetValue<string>() == "playback");
        Assert.Contains("same PlayPause service", tool["description"]!.GetValue<string>());
        var properties = tool["inputSchema"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "action", "seconds" }, properties.Select(p => p.Key));
        Assert.Equal(new[] { "resume", "pause", "toggle", "next", "previous", "seek" },
            properties["action"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("Space semantics", properties["action"]!["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("slider")]
    [InlineData("table")]
    [InlineData("list")]
    [InlineData("tree")]
    public Task OrdinarySelectionAndSliderFocusStillUsePlaybackSpace(string kind) => Run(() =>
    {
        Control control = kind switch
        {
            "slider" => new Slider { Minimum = 0, Maximum = 100, Value = 50 },
            "table" => new DataGrid { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            "tree" => new TreeView { Items = { new TreeViewItem { Header = "One" } } },
            _ => new ListBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
        };
        var window = new Window { Content = control };
        var calls = 0;
        Attach(window, () => calls++);
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var target = control switch
            {
                ListBox selectionList => selectionList.ContainerFromIndex(0)!,
                TreeView tree => (Control)tree.Items[0]!,
                _ => control,
            };
            Assert.True(target.Focus());
            Down(window); Down(window); Up(window);
            Assert.Equal(1, calls);
            if (control is Slider slider) Assert.Equal(50, slider.Value);
            if (control is ListBox list) Assert.Equal(0, list.SelectedIndex);
            if (control is DataGrid table) Assert.Equal(0, table.SelectedIndex);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task InlineMenuKeepsBothSpaceEdges() => Run(() =>
    {
        var item = new MenuItem { Header = "menu" };
        var window = new Window { Content = new Menu { Items = { item } } };
        var calls = 0;
        var handled = new List<bool>();
        Attach(window, () => calls++);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add(e.Handled), RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.KeyUpEvent, (_, e) => handled.Add(e.Handled), RoutingStrategies.Tunnel, true);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); Assert.True(item.Focus());
            Down(window); Up(window);
            Assert.Equal(0, calls);
            Assert.Equal(new[] { false, false }, handled);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("button")]
    [InlineData("press")]
    [InlineData("repeat")]
    [InlineData("toggle")]
    [InlineData("check")]
    [InlineData("radio")]
    public Task SpaceOwnsBothEdgesAndRepeatsWithoutClickingTheFocusedControl(string kind) => Run(() =>
    {
        Button button = kind switch
        {
            "press" => new Button { ClickMode = ClickMode.Press },
            "repeat" => new RepeatButton(),
            "toggle" => new ToggleButton { IsChecked = false },
            "check" => new CheckBox { IsChecked = false },
            "radio" => new RadioButton { IsChecked = false },
            _ => new Button(),
        };
        var window = new Window { Content = button };
        var calls = 0;
        var clicks = 0;
        Attach(window, () => calls++);
        button.Click += (_, _) => clicks++;
        try
        {
            window.Show();
            Assert.True(button.Focus());
            Down(window); Down(window); Down(window);
            Assert.Equal(1, calls);
            Assert.Equal(0, clicks);
            Up(window);
            Assert.Equal(1, calls);
            Down(window); Up(window);
            Assert.Equal(2, calls);
            Assert.Equal(0, clicks);
            Assert.False(button.IsPressed);
            if (button is ToggleButton toggle) Assert.False(toggle.IsChecked);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task HeldSpaceKeepsItsOwnershipAcrossFocusAndModifierChanges() => Run(() =>
    {
        var first = new Button();
        var second = new CheckBox { IsChecked = false };
        var input = new TextBox { Text = "search" };
        var window = new Window { Content = new StackPanel { Children = { first, second, input } } };
        var calls = 0;
        var clicks = 0;
        Attach(window, () => calls++);
        second.Click += (_, _) => clicks++;
        try
        {
            window.Show(); first.Focus(); Down(window);
            second.Focus(); Down(window);
            input.Focus(); Down(window, RawInputModifiers.Shift);
            Up(window, RawInputModifiers.Shift);
            Assert.Equal(1, calls);
            Assert.Equal(0, clicks);
            Assert.False(second.IsChecked);
            Assert.Equal("search", input.Text);
            first.Focus(); Down(window); Up(window);
            Assert.Equal(2, calls);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(RawInputModifiers.Control)]
    [InlineData(RawInputModifiers.Alt)]
    [InlineData(RawInputModifiers.Shift)]
    [InlineData(RawInputModifiers.Meta)]
    [InlineData(RawInputModifiers.Control | RawInputModifiers.Alt)]
    public Task ModifiedSpaceIsNotInterceptedEvenWhenTheModifierIsReleasedWhileHeld(RawInputModifiers modifiers) => Run(() =>
    {
        var button = new Button();
        var window = new Window { Content = button };
        var calls = 0;
        var handled = new List<bool>();
        Attach(window, () => calls++);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add(e.Handled), RoutingStrategies.Tunnel, true);
        try
        {
            window.Show(); button.Focus();
            Down(window, modifiers); Down(window); Up(window);
            Assert.Equal(0, calls);
            Assert.Equal(new[] { false, false }, handled);
            Down(window); Up(window);
            Assert.Equal(1, calls);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("text")]
    [InlineData("combo")]
    [InlineData("numeric")]
    [InlineData("autocomplete")]
    public Task EditingControlsKeepSpaceAndTextInput(string kind) => Run(() =>
    {
        Control editor = kind switch
        {
            "combo" => new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 },
            "numeric" => new NumericUpDown { Value = 5 },
            "autocomplete" => new AutoCompleteBox(),
            _ => new TextBox { Text = "rename" },
        };
        var button = new Button();
        var window = new Window { Content = new StackPanel { Children = { editor, button } } };
        var calls = 0;
        var intercepted = false;
        Attach(window, () => calls++);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => intercepted |= e.Handled, RoutingStrategies.Tunnel, true);
        try
        {
            window.Show(); window.UpdateLayout();
            var target = editor is ComboBox ? editor : editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? editor;
            Assert.True(target.Focus());
            Down(window); Up(window);
            Assert.False(intercepted);
            Assert.Equal(0, calls);
            if (target is TextBox text)
            {
                text.CaretIndex = text.Text?.Length ?? 0;
                window.KeyTextInput(" 中文 "); // Committed input must still follow the normal TextBox route.
                Assert.EndsWith(" 中文 ", text.Text);
            }
            // Repeats from a press begun in an editor must not start playback after Tab/mouse focus changes.
            target.Focus(); Down(window); button.Focus(); Down(window); Up(window);
            Assert.Equal(0, calls);
            Down(window); Up(window);
            Assert.Equal(1, calls);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RealPopupMenusRetainSpaceRouting(bool flyout) => Run(() =>
    {
        var button = new Button { Content = "menu" };
        var window = new Window { Content = button };
        var item = new MenuItem { Header = "item", ToggleType = MenuItemToggleType.CheckBox };
        var context = new ContextMenu();
        var menu = new MenuFlyout();
        if (flyout) menu.Items.Add(item); else context.Items.Add(item);
        var calls = 0;
        Attach(window, () => calls++);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Space) Assert.False(e.Handled);
        }, RoutingStrategies.Tunnel, true);
        try
        {
            window.Show(); button.Focus();
            if (flyout) menu.ShowAt(button); else context.Open(button);
            Dispatcher.UIThread.RunJobs();
            Assert.True(item.Focus());
            Assert.True(item.IsFocused);
            var popup = TopLevel.GetTopLevel(item)!;
            if (popup != window)
                popup.AddHandler(InputElement.KeyDownEvent, (_, e) =>
                {
                    if (e.Key == Key.Space) Assert.False(e.Handled);
                }, RoutingStrategies.Tunnel, true);
            // Menu input may be consumed before routed events; send real raw input to
            // its focused popup, and assert any routed input remains unclaimed too.
            Down(popup); Up(popup);
            Assert.Equal(0, calls);
        }
        finally { context.Close(); menu.Hide(); window.Close(); }
    });

    [Fact]
    public Task ModalDialogButtonStillClicksWithoutTogglingTheOwner() => Run(() =>
    {
        var owner = new Window();
        var button = new Button { Content = "confirm" };
        var dialog = new Window { Content = button };
        var calls = 0;
        var clicks = 0;
        Attach(owner, () => calls++);
        button.Click += (_, _) => clicks++;
        try
        {
            owner.Show();
            _ = dialog.ShowDialog(owner);
            Assert.True(button.Focus());
            Down(dialog); Up(dialog);
            Assert.Equal(1, clicks);
            Assert.Equal(0, calls);
        }
        finally { dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task DeactivationRearmsSpaceWhenTheReleaseHappenedElsewhere() => Run(() =>
    {
        var button = new Button();
        var window = new Window { Content = button };
        var calls = 0;
        Attach(window, () => calls++);
        try
        {
            window.Show(); button.Focus(); Down(window);
            window.Hide(); Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsActive);
            window.Show(); Dispatcher.UIThread.RunJobs();
            button.Focus(); Down(window); Up(window);
            Assert.Equal(2, calls);
        }
        finally { window.Close(); }
    });

    static void Attach(Window window, Action toggle) =>
        PlaybackShortcuts.Attach(window, () => { }, () => { }, _ => { }, _ => { }, playPause: toggle);

    static void Down(TopLevel window, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPress(Key.Space, modifiers, PhysicalKey.Space, " ");
    static void Up(TopLevel window, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyRelease(Key.Space, modifiers, PhysicalKey.Space, " ");

    static async Task Run(Action test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(test, CancellationToken.None);
    }
}

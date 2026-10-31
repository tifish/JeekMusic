using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using JeekMusic.UI;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class SongCopyTests
{
    [Fact]
    public Task CopiesFullTitlesInListOrderIncludingVirtualizedSelection() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness(trackFiveTitle: "完整歌名 — 中文 🎵 (Live) " + new string('长', 120));
        h.Table.SelectedItems.Clear();
        h.Table.SelectedItems.Add(h.Rows[230]);
        h.Table.SelectedItems.Add(h.Rows[5]);
        h.Table.Focus();
        Copy(h.Window);
        h.Pump();
        Assert.Equal(h.Rows[5].Title + Environment.NewLine + h.Rows[230].Title,
            await h.Window.Clipboard!.TryGetTextAsync());
        Assert.Equal(2, h.Window.SongCopyDiagnostics()["lastCopiedCount"]!.GetValue<int>());
        Assert.Equal("copied", h.Tools.Call("get_diagnostics", new())["mainLayout"]!["songCopy"]!["state"]!.GetValue<string>());
        h.Table.SelectedItem = h.Rows[5];
        Copy(h.Window);
        Assert.Equal(h.Rows[5].Title, await h.Window.Clipboard!.TryGetTextAsync());
    });

    [Fact]
    public Task EmptySelectionAndOtherModifiersLeaveClipboardAlone() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        await h.Window.Clipboard!.SetTextAsync("keep");
        h.Table.SelectedItems.Clear(); h.Table.Focus(); Copy(h.Window);
        Assert.Equal("keep", await h.Window.Clipboard!.TryGetTextAsync());
        h.Table.SelectedItem = h.Rows[1];
        h.Window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "c");
        h.Window.KeyRelease(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "c");
        Assert.Equal("idle", h.Window.SongCopyDiagnostics()["state"]!.GetValue<string>());
    });

    [Fact]
    public Task SearchBoxKeepsNormalTextCopy() => Run(async () =>
    {
        using var h = new PlayingVisibilityTests.Harness();
        h.Table.SelectedItem = h.Rows[1];
        var input = h.Window.FindControl<TextBox>("SearchBox")!;
        input.Text = "song"; input.Focus(); input.SelectionStart = 1; input.SelectionEnd = 3;
        Copy(h.Window); h.Pump();
        Assert.Equal("on", await h.Window.Clipboard!.TryGetTextAsync());
        Assert.Equal("idle", h.Window.SongCopyDiagnostics()["state"]!.GetValue<string>());
    });

    static void Copy(MainWindow window)
    {
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
    }

    static async Task Run(Func<Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(async () => { await test(); return 0; }, CancellationToken.None);
    }
}

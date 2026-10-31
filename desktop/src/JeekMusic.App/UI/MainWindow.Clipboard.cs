using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

public partial class MainWindow
{
    string _songCopyState = "idle";
    int _songCopyCount;

    async void OnSongCopyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.Control) return;
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().Any(v => v is TextBox) == true ||
            FocusManager?.GetFocusedElement() is TextBox) return;

        // Run before DataGrid's built-in cell copy, including the empty selection case.
        e.Handled = true;
        var selected = Table.SelectedItems.OfType<SongRow>().ToHashSet();
        var titles = _vm.Rows.Where(selected.Contains).Select(row => row.Title).ToArray();
        if (titles.Length == 0) return;
        try
        {
            _songCopyState = "copying";
            var clipboard = Clipboard ?? throw new InvalidOperationException("Clipboard unavailable");
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, titles));
            _songCopyCount = titles.Length;
            _songCopyState = "copied";
        }
        catch (Exception)
        {
            _songCopyState = "failed";
            _vm.ShowToast(L.T("无法复制歌名，请重试"));
        }
    }

    // Report shortcut outcomes without reading or exposing the system clipboard.
    internal JsonObject SongCopyDiagnostics() => new()
    {
        ["shortcut"] = "Ctrl+C", ["order"] = "current-list",
        ["selectedCount"] = Table.SelectedItems.OfType<SongRow>().Count(),
        ["state"] = _songCopyState, ["lastCopiedCount"] = _songCopyCount
    };
}

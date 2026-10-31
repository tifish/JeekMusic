using Avalonia.Controls;
using Avalonia.VisualTree;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>窗口布局的保存和恢复（需求 W-1、G-1）：导航栏宽度、表格列的宽度 / 顺序 / 显示与否。</summary>
public partial class MainWindow
{
    public void RestoreLayout(UiState ui)
    {
        _app.FolderExpansion.Restore(ui);
        _vm.ApplyFolderExpansion();
        _navWidth = SafeNavWidth(ui.NavWidth);
        RefreshLayoutBudget();
        _navWidth = MainArea.ColumnDefinitions[0].Width.Value;
        // 按保存的顺序恢复现有列，跳过已移除的列，避免旧位置留下空档。
        var columnOrder = 0;
        foreach (var state in ui.Columns.OrderBy(c => c.Order))
        {
            var column = Table.Columns.FirstOrDefault(c => c.Tag as string == state.Tag);
            if (column is null) continue;
            if (state.Width > 20) column.Width = new DataGridLength(state.Width);
            column.IsVisible = state.Visible;
            if (state.Order >= 0) column.DisplayIndex = columnOrder++;
        }
        // New dimensions stay hidden when absent from an older snapshot; explicit choices survive.
        foreach (var (tag, _, _) in TagColumns)
            if (!ui.Columns.Any(c => c.Tag == tag)) Table.Columns.Single(c => c.Tag as string == tag).IsVisible = false;
        // 默认隐藏的列：文件名、智能标签（右键表头可以打开）
        if (ui.Columns.Count == 0)
            foreach (var c in Table.Columns)
                if (c.Tag is "FileName" or "Tags") c.IsVisible = false;
        // Older snapshots always recorded the hidden default, without recording user intent.
        // Reveal once on upgrade; the versioned snapshot then respects show/hide, width and order.
        if (ui.SongColumnsVersion < 1 && Table.Columns.FirstOrDefault(c => c.Tag is "Modified") is { } modified)
        {
            modified.IsVisible = true;
            modified.Width = new DataGridLength(Math.Max(154, modified.Width.Value));
        }
        Table.ColumnHeaderContextRequested();
        RestoreExtraLayout(ui);
    }

    public void SaveLayout(UiState ui)
    {
        _app.FolderExpansion.Save(ui);
        ui.SongColumnsVersion = 1;
        ui.NavWidth = SafeNavWidth(_navWidth);
        ui.Columns = Table.Columns.Select(c => new ColumnState
        {
            Tag = c.Tag as string ?? "", Width = c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth, Order = c.DisplayIndex, Visible = c.IsVisible,
        }).ToList();
        SaveExtraLayout(ui);
    }

    partial void RestoreExtraLayout(UiState ui);
    partial void SaveExtraLayout(UiState ui);
}

static class DataGridExtensions
{
    /// <summary>右键表头：选择显示哪些列（需求 G-1）。</summary>
    public static void ColumnHeaderContextRequested(this DataGrid grid)
    {
        if (grid.Tag is "header-menu") return;
        grid.Tag = "header-menu";
        grid.AddHandler(Control.ContextRequestedEvent, (_, e) =>
        {
            if (e.Source is not Avalonia.Visual v || v.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is null) return;
            var menu = new ContextMenu();
            foreach (var c in grid.Columns)
            {
                var col = c;
                var title = (c.Header as string)?.TrimEnd('↑', '↓', ' ') ?? "";
                var item = new MenuItem { Header = title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = c.IsVisible, IsEnabled = c.Tag as string != "Title" };
                item.Click += (_, _) => col.IsVisible = !col.IsVisible;
                menu.Items.Add(item);
            }
            e.Handled = true;
            menu.Open(grid);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}

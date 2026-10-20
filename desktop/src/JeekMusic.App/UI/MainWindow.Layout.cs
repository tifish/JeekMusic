using Avalonia.Controls;
using JeekMusic.Library;

namespace JeekMusic.UI;

/// <summary>窗口布局的保存和恢复（需求 W-1、G-1）：导航栏宽度、表格列的宽度 / 顺序 / 显示与否。</summary>
public partial class MainWindow
{
    public void RestoreLayout(UiState ui)
    {
        MainArea.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(ui.NavWidth, 160, 480));
        foreach (var state in ui.Columns)
        {
            var column = Table.Columns.FirstOrDefault(c => c.Tag as string == state.Tag);
            if (column is null) continue;
            if (state.Width > 20) column.Width = new DataGridLength(state.Width);
            column.IsVisible = state.Visible;
            if (state.Order >= 0 && state.Order < Table.Columns.Count) column.DisplayIndex = state.Order;
        }
        // 默认隐藏的列：文件名、修改时间、智能标签（右键表头可以打开）
        if (ui.Columns.Count == 0)
            foreach (var c in Table.Columns)
                if (c.Tag is "FileName" or "Modified" or "Tags") c.IsVisible = false;
        Table.ColumnHeaderContextRequested();
        RestoreExtraLayout(ui);
    }

    public void SaveLayout(UiState ui)
    {
        ui.NavWidth = MainArea.ColumnDefinitions[0].ActualWidth;
        ui.Columns = Table.Columns.Select(c => new ColumnState
        {
            Tag = c.Tag as string ?? "", Width = c.ActualWidth, Order = c.DisplayIndex, Visible = c.IsVisible,
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
            if (e.Source is not Avalonia.Visual v || v.FindAncestorOfType<Avalonia.Controls.Primitives.DataGridColumnHeader>(includeSelf: true) is null) return;
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

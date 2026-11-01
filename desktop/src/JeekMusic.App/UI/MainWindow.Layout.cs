using Avalonia.Controls;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
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
        // 文件名默认隐藏，历史Tags列已移除，恢复时自动跳过。
        if (ui.Columns.Count == 0)
            foreach (var c in Table.Columns)
                if (c.Tag is "FileName") c.IsVisible = false;
        // Older snapshots always recorded the hidden default, without recording user intent.
        // Reveal once on upgrade; the versioned snapshot then respects show/hide, width and order.
        if (ui.SongColumnsVersion < 1 && Table.Columns.FirstOrDefault(c => c.Tag is "Modified") is { } modified)
        {
            modified.IsVisible = true;
            modified.Width = new DataGridLength(Math.Max(154, modified.Width.Value));
        }
        Table.Columns.Single(c=>c.Tag is "Title").IsVisible=true;
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

    JsonObject SongColumnsStatus() => new()
    {
        ["created"]=!_layoutClosed,["summaryColumnRemoved"]=true,
        ["columns"]=new JsonArray(Table.Columns.OrderBy(c=>c.DisplayIndex).Select(c=>(JsonNode)new JsonObject { ["tag"]=c.Tag as string,["visible"]=c.IsVisible,["width"]=c.Width.IsAbsolute?c.Width.Value:c.ActualWidth,["order"]=c.DisplayIndex,["required"]=c.Tag is "Title" }).ToArray()),
        ["headerMenu"]=DataGridExtensions.HeaderMenuDiagnostics(Table)
    };
    JsonObject ControlSongColumns(JsonObject args)
    {
        var action=args["action"]?.GetValue<string>()??"status";
        if(action=="status") { if(args.Count>1) throw new ArgumentException("status accepts only action"); return SongColumnsStatus(); }
        if(action=="save") { if(args.Count>1) throw new ArgumentException("save accepts only action"); PersistSongColumns(); var saved=SongColumnsStatus(); saved["persisted"]=true; return saved; }
        if(action!="set") throw new ArgumentException("Unknown column action");
        var column=Table.Columns.SingleOrDefault(c=>c.Tag as string==args["tag"]?.GetValue<string>())??throw new ArgumentException("Unknown song column");
        if(args["visible"] is null && args["width"] is null && args["order"] is null) throw new ArgumentException("Provide visible, width or order");
        var visible=args["visible"]?.GetValue<bool>(); var width=args["width"] is { } widthNode?double.Parse(widthNode.ToJsonString(),System.Globalization.CultureInfo.InvariantCulture):(double?)null; var order=args["order"]?.GetValue<int>();
        if(column.Tag is "Title" && visible==false || width is { } w && (!double.IsFinite(w) || w<64 || w>3000) || order is { } o && (o<0 || o>=Table.Columns.Count)) throw new ArgumentException("Invalid song column layout; title is always visible");
        if(visible is { } show) column.IsVisible=show;
        if(width is { } size) column.Width=new DataGridLength(size);
        if(order is { } index) column.DisplayIndex=index;
        PersistSongColumns();
        var result=SongColumnsStatus(); result["persisted"]=true; return result;
    }
    internal void PersistSongColumns()
    {
        var ui=UiState.Load(); SaveLayout(ui); ui.Save();
        var saved=UiState.Load();
        if(saved.Columns.Count!=ui.Columns.Count || ui.Columns.Any(c=>!saved.Columns.Any(other=>other.Tag==c.Tag && other.Visible==c.Visible && other.Order==c.Order && Math.Abs(other.Width-c.Width)<.01))) throw new IOException("Could not persist song column layout");
    }
    partial void RestoreExtraLayout(UiState ui);
    partial void SaveExtraLayout(UiState ui);
}

static class DataGridExtensions
{
    static readonly ConditionalWeakTable<DataGrid,ContextMenu> HeaderMenus=new();
    internal static ContextMenu BuildColumnMenu(DataGrid grid)
    {
        var menu=new ContextMenu();
        foreach(var column in grid.Columns)
        {
            var title=(column.Header as string)?.TrimEnd('↑','↓',' ')??"";
            var item=new MenuItem { Header=title,Tag=column.Tag,ToggleType=MenuItemToggleType.CheckBox,IsChecked=column.IsVisible,IsEnabled=column.Tag as string!="Title",StaysOpenOnClick=true };
            item.Click+=(_,_)=> { column.IsVisible=item.IsChecked; if(TopLevel.GetTopLevel(grid) is MainWindow owner) { try { owner.PersistSongColumns(); } catch(IOException e) { JeekMusic.Infrastructure.Log.Warn("ui","保存列布局失败",e); } } };
            void Changed(object? sender,Avalonia.AvaloniaPropertyChangedEventArgs e) { if(e.Property==DataGridColumn.IsVisibleProperty) item.IsChecked=column.IsVisible; }
            column.PropertyChanged+=Changed;
            menu.Closed+=(_,_)=>column.PropertyChanged-=Changed;
            menu.Items.Add(item);
        }
        HeaderMenus.Remove(grid); HeaderMenus.Add(grid,menu); return menu;
    }
    internal static JsonObject HeaderMenuDiagnostics(DataGrid grid)
    {
        HeaderMenus.TryGetValue(grid,out var menu);
        return new JsonObject { ["open"]=menu?.IsOpen==true,["nativeStaysOpenOnClick"]=true,
            ["items"]=new JsonArray((menu?.Items.OfType<MenuItem>()??[]).Select(i=>(JsonNode)new JsonObject { ["tag"]=i.Tag as string,["checked"]=i.IsChecked,["enabled"]=i.IsEnabled,["staysOpenOnClick"]=i.StaysOpenOnClick }).ToArray()) };
    }
    /// <summary>右键表头：选择显示哪些列（需求 G-1）。</summary>
    public static void ColumnHeaderContextRequested(this DataGrid grid)
    {
        if (grid.Tag is "header-menu") return;
        grid.Tag = "header-menu";
        grid.AddHandler(Control.ContextRequestedEvent, (_, e) =>
        {
            if (e.Source is not Avalonia.Visual v || v.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is null) return;
            var menu = BuildColumnMenu(grid);
            e.Handled = true;
            menu.Open(grid);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}

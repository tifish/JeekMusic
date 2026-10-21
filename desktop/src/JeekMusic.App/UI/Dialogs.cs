using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace JeekMusic.UI;

/// <summary>简单的模态对话框：确认、输入文字、选择（用代码搭，不单独写 .axaml）。</summary>
public static class Dialogs
{
    static Window Frame(string title, Control content, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new Border { Padding = new Thickness(20), Child = content },
    };

    static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }

    /// <summary>确认对话框；返回 true 表示按了确认按钮。</summary>
    public static async Task<bool> Confirm(Window owner, string title, string message, string ok = "确定", string cancel = "取消", bool danger = false)
    {
        var result = false;
        var okButton = new Button { Content = L.T(ok), IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (!danger) okButton.Classes.Add("accent");
        var cancelButton = new Button { Content = L.T(cancel), IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, Buttons(okButton, cancelButton) } };
        var w = Frame(title, panel);
        okButton.Click += (_, _) => { result = true; w.Close(); };
        cancelButton.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>只有一个"知道了"按钮的提示。</summary>
    public static async Task Info(Window owner, string title, string message)
    {
        var okButton = new Button { Content = L.T("知道了"), IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, Buttons(okButton) } };
        var w = Frame(title, panel);
        okButton.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    /// <summary>输入一行文字；取消时返回 null。<paramref name="selectStem"/> 为 true 时只选中扩展名前面的部分（改文件名用）。</summary>
    public static async Task<string?> Prompt(Window owner, string title, string label, string initial = "", bool selectStem = false)
    {
        string? result = null;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0) };
        var okButton = new Button { Content = L.T("确定"), IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        okButton.Classes.Add("accent");
        var cancelButton = new Button { Content = L.T("取消"), IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { Children = { new TextBlock { Text = label }, box, Buttons(okButton, cancelButton) } };
        var w = Frame(title, panel);
        void Accept()
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            result = box.Text.Trim();
            w.Close();
        }
        okButton.Click += (_, _) => Accept();
        cancelButton.Click += (_, _) => w.Close();
        w.Opened += (_, _) =>
        {
            box.Focus();
            var end = selectStem && initial.LastIndexOf('.') is var dot && dot > 0 ? dot : initial.Length;
            box.SelectionStart = 0;
            box.SelectionEnd = end;
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>从几项里选一项；取消时返回 -1。</summary>
    public static async Task<int> Choose(Window owner, string title, string message, IReadOnlyList<string> options)
    {
        var result = -1;
        var panel = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) } } };
        var w = Frame(title, panel);
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var b = new Button { Content = options[i], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => { result = index; w.Close(); };
            panel.Children.Add(b);
        }
        var cancel = new Button { Content = L.T("取消"), IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(Buttons(cancel));
        await w.ShowDialog(owner);
        return result;
    }
}

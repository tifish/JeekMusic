using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace JeekMusic.UI;

/// <summary>窗口内支持左右手单键切歌，在列表处理按键之前响应，保留输入和方向键调节操作。</summary>
internal static class PlaybackShortcuts
{
    public static void Attach(Window window, Action previous, Action next)
    {
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Z or Key.X)) return;
            var source = e.Source as Visual ?? window.FocusManager?.GetFocusedElement() as Visual;
            var ancestors = source?.GetSelfAndVisualAncestors().ToList();
            if (ancestors?.Any(v => v is TextBox or ComboBox or NumericUpDown) == true) return;
            if (e.Key is Key.Left or Key.Right && ancestors?.Any(v => v is Slider or TreeView) == true) return;

            if (e.Key is Key.Left or Key.Z) previous(); else next();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}

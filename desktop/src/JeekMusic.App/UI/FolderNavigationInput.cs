using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.UI.ViewModels;

namespace JeekMusic.UI;

/// <summary>
/// 只观察实际输入目标的一次展开变化。绑定初始化、恢复选中、控件重建和程序赋值不是用户意图；
/// 父目录收起造成子项隐藏/卸载也不记录。每次输入只检查一个节点，不遍历目录树。
/// </summary>
internal static class FolderNavigationInput
{
    public static void Attach(TreeView tree, FolderExpansionState state)
    {
        Observe(InputElement.PointerPressedEvent);
        Observe(InputElement.PointerReleasedEvent);
        Observe(InputElement.KeyDownEvent);

        void Observe<T>(RoutedEvent<T> input) where T : RoutedEventArgs
        {
            tree.AddHandler(input, (_, e) =>
            {
                var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
                if (item?.DataContext is not NavNode { Kind: NavKind.Folder or NavKind.Folders } node) return;
                var before = item.IsExpanded;
                // 手势识别（如标题双击）可能在输入路由后才改变展开；等本次路由/手势结束再比较。
                Dispatcher.UIThread.Post(() =>
                {
                    if (item.IsExpanded != before) state.RecordUserExpansion(node, item.IsExpanded);
                });
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }
}

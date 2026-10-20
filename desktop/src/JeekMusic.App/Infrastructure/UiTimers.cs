using Avalonia.Threading;

namespace JeekMusic.Infrastructure;

public static class UiTimers
{
    /// <summary>
    /// 创建不自动启动的定时器。注意 Avalonia 的 DispatcherTimer(间隔, 优先级, 回调) 构造函数会立即启动，
    /// 睡眠定时器就是因此一启动就"到时间"了。
    /// </summary>
    public static DispatcherTimer UiTimer(TimeSpan interval, DispatcherPriority priority, Action tick)
    {
        var t = new DispatcherTimer(priority) { Interval = interval };
        t.Tick += (_, _) => tick();
        return t;
    }
}

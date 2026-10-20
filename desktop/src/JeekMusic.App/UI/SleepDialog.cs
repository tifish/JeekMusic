using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JeekMusic.UI;

/// <summary>
/// 睡眠定时（需求 P-8、O-8，同移动端）：按时间（预设或自定义分钟数，停止前渐弱，可以"播完当前这首再停止"）或按歌曲数；
/// 可以"停止后让电脑睡眠"。上次的选择会记住。
/// </summary>
public static class SleepDialog
{
    public static async Task Show(Window owner, AppServices app)
    {
        var timer = app.SleepTimer;
        var prefs = app.Settings.Current.Sleep;
        var w = new Window
        {
            Title = "睡眠定时", Width = 380, SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        if (timer.Active)
        {
            panel.Children.Add(new TextBlock
            {
                Text = timer.Remaining is { } r ? $"还剩 {Text.Duration(r)}{(timer.FinishSong ? "，到时间后播完当前这首再停止" : "")}" : $"再播 {timer.SongsLeft} 首后停止",
                FontWeight = FontWeight.SemiBold,
            });
            var cancel = new Button { Content = "取消定时" };
            cancel.Click += (_, _) => { timer.Cancel(); w.Close(); };
            panel.Children.Add(cancel);
            panel.Children.Add(new Separator());
        }
        var finish = new CheckBox { Content = "时间到后播完当前这首再停止（不渐弱）", IsChecked = prefs.FinishSong };
        var sleepPc = new CheckBox { Content = "停止后让电脑睡眠", IsChecked = prefs.SleepComputer };
        void StartMinutes(int minutes)
        {
            prefs.FinishSong = finish.IsChecked == true;
            prefs.SleepComputer = sleepPc.IsChecked == true;
            prefs.LastMinutes = minutes;
            app.Settings.Save("sleep");
            timer.StartMinutes(minutes, prefs.FinishSong, prefs.SleepComputer);
            w.Close();
        }
        panel.Children.Add(new TextBlock { Text = "按时间", Opacity = 0.65 });
        var presets = new WrapPanel();
        foreach (var m in new[] { 15, 30, 45, 60, 90 })
        {
            var b = new Button { Content = $"{m} 分钟", Margin = new Thickness(0, 0, 6, 6) };
            if (m == prefs.LastMinutes) b.Classes.Add("accent");
            b.Click += (_, _) => StartMinutes(m);
            presets.Children.Add(b);
        }
        panel.Children.Add(presets);
        var custom = new NumericUpDown { Value = prefs.CustomMinutes, Minimum = 1, Maximum = 600, Increment = 5, Width = 140, FormatString = "0" };
        var customGo = new Button { Content = "开始" };
        customGo.Click += (_, _) =>
        {
            prefs.CustomMinutes = (int)(custom.Value ?? 20);
            StartMinutes(prefs.CustomMinutes);
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "自定义（分钟）", VerticalAlignment = VerticalAlignment.Center }, custom, customGo } });
        panel.Children.Add(finish);

        panel.Children.Add(new TextBlock { Text = "按歌曲数（手动切歌不计数）", Opacity = 0.65, Margin = new Thickness(0, 8, 0, 0) });
        var songs = new NumericUpDown { Value = prefs.SongCount, Minimum = 1, Maximum = 200, Width = 140, FormatString = "0" };
        var songsGo = new Button { Content = "开始" };
        songsGo.Click += (_, _) =>
        {
            prefs.SongCount = (int)(songs.Value ?? 3);
            prefs.SleepComputer = sleepPc.IsChecked == true;
            app.Settings.Save("sleep");
            timer.StartSongs(prefs.SongCount, prefs.SleepComputer);
            w.Close();
        };
        var current = new Button { Content = "播完当前这首" };
        current.Click += (_, _) => { prefs.SleepComputer = sleepPc.IsChecked == true; app.Settings.Save("sleep"); timer.StartSongs(1, prefs.SleepComputer); w.Close(); };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "播完", VerticalAlignment = VerticalAlignment.Center }, songs, new TextBlock { Text = "首", VerticalAlignment = VerticalAlignment.Center }, songsGo } });
        panel.Children.Add(current);
        panel.Children.Add(new Separator());
        panel.Children.Add(sleepPc);
        w.Content = panel;
        await w.ShowDialog(owner);
    }
}

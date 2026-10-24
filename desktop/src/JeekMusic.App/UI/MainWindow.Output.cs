using Avalonia.Controls;

namespace JeekMusic.UI;

public partial class MainWindow
{
    void ShowOutputMenu(Control? anchor = null) => CreateOutputMenu().Open(anchor ?? OutputButton);

    internal ContextMenu CreateOutputMenu()
    {
        var menu = new ContextMenu();
        var settings = _app.Settings.Current;
        void Device(string? name, string label)
        {
            var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = settings.OutputDevice == name };
            item.Click += (_, _) =>
            {
                settings.OutputDevice = name; settings.OutputDeviceDescription = label;
                _app.Settings.Save("playback"); _app.Player.SetDevice(name);
            };
            menu.Items.Add(item);
        }
        Device(null, L.T("跟随 Windows 的默认设备"));
        foreach (var device in _app.Player.Devices.Where(d => d.name != "auto")) Device(device.name, device.description);
        menu.Items.Add(new Separator());
        var p = _app.Player;
        p.RefreshRateCapabilities();
        var shared = new MenuItem { Header = L.T("共享模式"), ToggleType = MenuItemToggleType.Radio, IsChecked = !p.Exclusive };
        shared.Click += (_, _) => p.SetExclusive(false);
        menu.Items.Add(shared);
        var exclusive = new MenuItem { Header = L.T("WASAPI 独占模式"), ToggleType = MenuItemToggleType.Radio, IsChecked = p.Exclusive };
        exclusive.Click += (_, _) => p.SetExclusive(true);
        menu.Items.Add(exclusive);
        var automatic = new MenuItem { Header = L.T("自动匹配歌曲格式"), ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = p.AutomaticAudioFormat, IsEnabled = !p.Exclusive };
        automatic.Click += (_, _) => p.SetAutomaticAudioFormat(!p.AutomaticAudioFormat);
        menu.Items.Add(automatic);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = AudioFormatDisplay.Source(p), IsEnabled = false });
        menu.Items.Add(new MenuItem { Header = AudioFormatDisplay.Device(p), IsEnabled = false });
        if ((p.SampleRateDiagnostic ?? p.FormatConversion ?? (!p.Exclusive ? p.RateCapabilities.Diagnostic : null)) is { } diagnostic)
            menu.Items.Add(new MenuItem { Header = diagnostic, IsEnabled = false });
        return menu;
    }
}

using Avalonia.Controls;

namespace JeekMusic.UI;

public partial class MainWindow
{
    internal ContextMenu? ActiveOutputMenu { get; private set; }

    void ShowOutputMenu(Control? anchor = null, bool includeDevices = true)
    {
        ActiveOutputMenu?.Close();
        var menu = CreateOutputMenu(includeDevices);
        ActiveOutputMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(ActiveOutputMenu, menu)) ActiveOutputMenu = null; };
        menu.Open(anchor ?? OutputButton);
    }

    internal ContextMenu CreateOutputMenu(bool includeDevices = true)
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
        if (includeDevices)
        {
            Device(null, L.T("跟随 Windows 的默认设备"));
            foreach (var device in _app.Player.Devices.Where(d => d.name != "auto")) Device(device.name, device.description);
            menu.Items.Add(new Separator());
        }
        var p = _app.Player;
        p.RefreshRateCapabilities();
        var shared = new MenuItem { Header = L.T("共享模式"), ToggleType = MenuItemToggleType.Radio, IsChecked = !p.Exclusive };
        shared.Click += (_, _) => p.SetExclusive(false);
        menu.Items.Add(shared);
        var automatic = new MenuItem { Header = L.T("共享模式自动匹配歌曲格式"), ToggleType = MenuItemToggleType.CheckBox,
            Margin = new Avalonia.Thickness(20, 0, 0, 0), IsChecked = p.AutomaticAudioFormat, IsEnabled = !p.Exclusive };
        automatic.Click += (_, _) => p.SetAutomaticAudioFormat(!p.AutomaticAudioFormat);
        menu.Items.Add(automatic);
        var exclusive = new MenuItem { Header = L.T("WASAPI 独占模式"), ToggleType = MenuItemToggleType.Radio, IsChecked = p.Exclusive };
        exclusive.Click += (_, _) => p.SetExclusive(true);
        menu.Items.Add(exclusive);
        menu.Items.Add(new Separator());
        void Info(string text) => menu.Items.Add(new MenuItem { Header = new TextBlock
            { Text = text, MaxWidth = 480, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsEnabled = false });
        Info(AudioFormatDisplay.Source(p));
        Info(AudioFormatDisplay.Device(p));
        if (AudioFormatDisplay.Warning(p) is { } diagnostic)
            Info(diagnostic);
        return menu;
    }
}

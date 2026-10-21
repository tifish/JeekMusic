using Avalonia.Controls;

namespace JeekMusic.UI;

public partial class MainWindow
{
    void ShowOutputMenu()
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
        var exclusive = new MenuItem { Header = L.T("WASAPI 独占模式"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = settings.Exclusive };
        exclusive.Click += (_, _) =>
        {
            settings.Exclusive = !settings.Exclusive;
            _app.Settings.Save("playback"); _app.Player.SetExclusive(settings.Exclusive);
        };
        menu.Items.Add(exclusive);
        menu.Open(OutputButton);
    }
}

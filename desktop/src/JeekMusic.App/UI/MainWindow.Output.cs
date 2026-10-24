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
        foreach (var exclusive in new[] { false, true })
        {
            var group = new MenuItem { Header = exclusive ? L.T("WASAPI 独占模式") : L.T("共享模式"),
                ToggleType = MenuItemToggleType.Radio, IsChecked = p.Exclusive == exclusive };
            void Rate(string mode, int? rate, string label, bool enabled = true)
            {
                var item = new MenuItem { Header = label, IsEnabled = enabled, ToggleType = MenuItemToggleType.Radio,
                    IsChecked = p.Exclusive == exclusive && p.SampleRateMode == mode && (mode != "manual" || p.ManualSampleRate == rate && p.SampleRateApplied) };
                item.Click += (_, _) =>
                {
                    p.SetExclusive(exclusive);
                    if (p.Exclusive == exclusive) p.SetSampleRate(mode, rate);
                };
                group.Items.Add(item);
            }
            Rate("system", null, exclusive ? L.T("原始采样率（兼容模式）") : L.T("保留 Windows 混音格式"));
            Rate("auto", null, L.T("自动跟随歌曲"), exclusive || p.RateCapabilities.CanSetShared);
            foreach (var rate in exclusive ? p.RateCapabilities.ExclusiveRates : p.RateCapabilities.SharedRates)
                Rate("manual", rate, $"{rate / 1000.0:0.###} kHz");
            menu.Items.Add(group);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = L.T("实际设备采样率：") + (p.ActualSampleRate is { } actual ? $"{actual} Hz" : "—"), IsEnabled = false });
        if ((p.SampleRateDiagnostic ?? p.RateCapabilities.Diagnostic) is { } diagnostic)
            menu.Items.Add(new MenuItem { Header = diagnostic, IsEnabled = false });
        return menu;
    }
}

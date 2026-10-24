using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JeekMusic.Infrastructure;
using JeekMusic.Library;
using JeekMusic.Settings;
using JeekMusic.UI;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class OutputMenuInteractionTests
{
    [Theory]
    [InlineData("left",false)] [InlineData("right",false)] [InlineData("keyboard",false)]
    [InlineData("left",true)] [InlineData("right",true)] [InlineData("keyboard",true)]
    [InlineData("device",false)] [InlineData("device",true)]
    public async Task OpeningOutputMenuNeverChangesModeDeviceAutomaticPreferenceOrPlayback(string input,bool exclusive)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(()=>
        {
            using var f=new OutputModeSwitchTests.Fixture(configureEngine:engine=>
            {
                engine.Properties["audio-device-list/count"]="3";
                engine.Properties["audio-device-list/0/name"]="auto";
                engine.Properties["audio-device-list/1/name"]="wasapi/render-device";
                engine.Properties["audio-device-list/1/description"]="Render device";
                engine.Properties["audio-device-list/2/name"]="wasapi/second-device";
                engine.Properties["audio-device-list/2/description"]="Second device";
            });
            f.Endpoint.Volumes["second-device"]=70;
            var settings=new SettingsStore(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"));
            settings.Current.AutomaticAudioFormat=true;
            using var app=new AppServices(settings,new LibraryDb(":memory:"),_=>f.Player);
            f.Player.SetExclusive(exclusive);f.Ready();
            f.Current.EmitProperty("audio-device-list",null);Dispatcher.UIThread.RunJobs();
            var window=new MainWindow(app){ExitRequested=true};window.ViewModel.Player.ProgressVisible=false;
            try
            {
                window.Show();window.UpdateLayout();Dispatcher.UIThread.RunJobs();
                var button=window.FindControl<Button>(input=="device"?"OutputButton":"OutputModeButton")!;
                var clicks=0;var contexts=0;var saves=0;
                button.Click+=(_,_)=>clicks++;
                button.AddHandler(Control.ContextRequestedEvent,(_,_)=>contexts++,RoutingStrategies.Bubble,handledEventsToo:true);
                settings.Changed+=category=>{if(category=="playback")saves++;};
                var engines=f.Engines.Count;var position=f.Player.Position;var playing=f.Player.IsPlaying;
                var device=f.Player.Device;var automatic=f.Player.AutomaticAudioFormat;
                var point=button.TranslatePoint(new Point(button.Bounds.Width/2,button.Bounds.Height/2),window)!.Value;
                Assert.True(button.Bounds.Width>0&&button.Bounds.Height>0);
                if(input=="keyboard")
                {
                    Assert.True(button.Focus());
                    window.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.None,null);
                    window.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.None,null);
                }
                else
                {
                    var mouse=input=="right"?MouseButton.Right:MouseButton.Left;
                    window.MouseMove(point);window.MouseDown(point,mouse);window.MouseUp(point,mouse);
                }
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(window.ActiveOutputMenu);Assert.True(window.ActiveOutputMenu!.IsOpen);
                Assert.Equal(input=="right"?0:1,clicks);Assert.Equal(input=="right"?1:0,contexts);
                Assert.Equal(exclusive,f.Player.Exclusive);Assert.Equal(device,f.Player.Device);
                Assert.Equal(automatic,f.Player.AutomaticAudioFormat);Assert.Equal(engines,f.Engines.Count);
                Assert.Equal(position,f.Player.Position);Assert.Equal(playing,f.Player.IsPlaying);
                Assert.Equal(0,saves);Assert.Empty(f.Endpoint.Writes);
                var items=window.ActiveOutputMenu.Items.ToArray();
                var modesStart=input=="device"?4:0;
                Assert.Equal(L.T("共享模式"),Assert.IsType<MenuItem>(items[modesStart]).Header);
                Assert.Equal(L.T("共享模式自动匹配歌曲格式"),Assert.IsType<MenuItem>(items[modesStart+1]).Header);
                Assert.Equal(L.T("WASAPI 独占模式"),Assert.IsType<MenuItem>(items[modesStart+2]).Header);
                Assert.IsType<Separator>(items[modesStart+3]);
                Assert.All(items.Skip(modesStart+4),item=>
                {
                    var info=Assert.IsType<MenuItem>(item);
                    Assert.False(info.IsEnabled);Assert.IsType<TextBlock>(info.Header);
                });
                if(input=="device")
                {
                    Assert.Equal(L.T("跟随 Windows 的默认设备"),Assert.IsType<MenuItem>(items[0]).Header);
                    Assert.True(Assert.IsType<MenuItem>(items[0]).IsChecked);
                    Assert.Equal("Render device",Assert.IsType<MenuItem>(items[1]).Header);
                    Assert.Equal("Second device",Assert.IsType<MenuItem>(items[2]).Header);
                    Assert.IsType<Separator>(items[3]);
                    Assert.IsType<MenuItem>(items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));f.Ready();
                    Assert.Equal("wasapi/second-device",f.Player.Device);
                    Assert.Equal(f.Player.Device,settings.Current.OutputDevice);
                    Assert.True(Assert.IsType<MenuItem>(window.CreateOutputMenu().Items[2]).IsChecked);
                    Assert.IsType<MenuItem>(items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));f.Ready();
                    Assert.Null(f.Player.Device);Assert.Null(settings.Current.OutputDevice);
                    Assert.True(Assert.IsType<MenuItem>(window.CreateOutputMenu().Items[0]).IsChecked);
                }
                window.ActiveOutputMenu.Close();Dispatcher.UIThread.RunJobs();Assert.Null(window.ActiveOutputMenu);
            }
            finally{window.Close();}
        },CancellationToken.None);
    }
}

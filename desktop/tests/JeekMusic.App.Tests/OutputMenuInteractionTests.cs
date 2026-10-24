using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    public async Task OpeningOutputMenuNeverChangesModeDeviceAutomaticPreferenceOrPlayback(string input,bool exclusive)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(ShortcutTestApplication));
        await session.Dispatch(()=>
        {
            using var f=new OutputModeSwitchTests.Fixture();
            var settings=new SettingsStore(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"));
            settings.Current.AutomaticAudioFormat=true;
            using var app=new AppServices(settings,new LibraryDb(":memory:"),_=>f.Player);
            f.Player.SetExclusive(exclusive);f.Ready();
            var window=new MainWindow(app){ExitRequested=true};window.ViewModel.Player.ProgressVisible=false;
            try
            {
                window.Show();window.UpdateLayout();Dispatcher.UIThread.RunJobs();
                var button=window.FindControl<Button>("OutputModeButton")!;
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
                    var mouse=input=="left"?MouseButton.Left:MouseButton.Right;
                    window.MouseMove(point);window.MouseDown(point,mouse);window.MouseUp(point,mouse);
                }
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(window.ActiveOutputMenu);Assert.True(window.ActiveOutputMenu!.IsOpen);
                Assert.Equal(input=="right"?0:1,clicks);Assert.Equal(input=="right"?1:0,contexts);
                Assert.Equal(exclusive,f.Player.Exclusive);Assert.Equal(device,f.Player.Device);
                Assert.Equal(automatic,f.Player.AutomaticAudioFormat);Assert.Equal(engines,f.Engines.Count);
                Assert.Equal(position,f.Player.Position);Assert.Equal(playing,f.Player.IsPlaying);
                Assert.Equal(0,saves);Assert.Empty(f.Endpoint.Writes);
                window.ActiveOutputMenu.Close();Dispatcher.UIThread.RunJobs();Assert.Null(window.ActiveOutputMenu);
            }
            finally{window.Close();}
        },CancellationToken.None);
    }
}

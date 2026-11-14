using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using JeekMusic.Library;
using JeekMusic.Playback;
using JeekMusic.Settings;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class LanFileAdmissionTests
{
    [Theory]
    [InlineData("release")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task AuthenticatedFileAdmissionWaitsBrieflyAndNeverLeaksThePermit(string mode)
    {
        using var session=HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(async()=> {
            var directory=Path.Combine(Path.GetTempPath(),"jeek-file-admission-"+Guid.NewGuid());
            using var app=new AppServices(new SettingsStore(Path.Combine(directory,"settings.json")),new LibraryDb(":memory:"),
                source=>new PlayerService(source,o=>new OutputModeSwitchTests.FakeEngine(o),new OutputModeSwitchTests.FakeEndpointVolume()),
                owner=>new LanSyncService(owner,null,Path.Combine(directory,"lan.json"),false));
            var service=app.LanSync;
            var permit=(SemaphoreSlim)typeof(LanSyncService).GetField("_fileWork",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
            var dispatch=typeof(LanSyncService).GetMethod("DispatchFile",BindingFlags.Instance|BindingFlags.NonPublic)!;
            using var cancellation=new CancellationTokenSource();
            Assert.True(permit.Wait(0));
            try {
                var task=(Task<JsonObject>)dispatch.Invoke(service,[new JsonObject { ["op"]="file_open",["fileProtocol"]=3 },cancellation.Token,true])!;
                Assert.False(task.IsCompleted); // A short metadata/report collision must not immediately return busy.
                if(mode=="release") {
                    await Task.Delay(30);permit.Release();
                    // Admission succeeded; the deliberately credential-free request reaches authentication.
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>task);
                    Assert.Equal(1,permit.CurrentCount);
                } else if(mode=="cancel") {
                    cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
                    Assert.Equal(0,permit.CurrentCount);
                } else {
                    await Assert.ThrowsAnyAsync<IOException>(()=>task.WaitAsync(TimeSpan.FromSeconds(2)));
                    Assert.Equal(0,permit.CurrentCount);
                }
            } finally { if(permit.CurrentCount==0)permit.Release(); }
            return 0;
        },CancellationToken.None);
    }
}

using JeekMusic.Core.Sync;

namespace JeekMusic.Core.Tests;

public sealed class LanMetadataRefreshTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"jeek-refresh-"+Guid.NewGuid().ToString("N"));
    public LanMetadataRefreshTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    [Fact] public async Task RefreshChecksNamesBudgetRootAndCancellationRatherThanInferringUnknownAsAbsent() {
        File.WriteAllText(Path.Combine(root,"A.wav"),"audio");var first=await LanFiles.Manifest(root,[""]);
        File.WriteAllText(Path.Combine(root,"B.wav"),"audio2");
        Assert.Throws<IOException>(()=>LanFiles.RefreshMetadata(root,first,["B.wav"],budget:new(Files:1)));
        Assert.Throws<IOException>(()=>LanFiles.RefreshMetadata(root,first,["B.wav"],budget:new(Bytes:1)));
        Assert.Throws<IOException>(()=>LanFiles.RefreshMetadata(root,first,["../escape.wav"]));
        using var c=new CancellationTokenSource();c.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>LanFiles.RefreshMetadata(root,first,["A.wav"],cancel:c.Token));
        Directory.CreateDirectory(Path.Combine(root,"folder"));Assert.Null(LanFiles.RefreshMetadata(root,first,["folder"]));
        // Canonical Unicode collisions include directories, not only audio entries.
        Directory.CreateDirectory(Path.Combine(root,"e\u0301.wav"));File.WriteAllText(Path.Combine(root,"é.wav"),"audio");
        Assert.Throws<IOException>(()=>LanFiles.RefreshMetadata(root,first,["é.wav"]));
        Directory.Delete(root,true);Assert.ThrowsAny<IOException>(()=>LanFiles.RefreshMetadata(root,first,[]));Directory.CreateDirectory(root);
    }
}

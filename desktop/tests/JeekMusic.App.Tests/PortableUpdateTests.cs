using JeekMusic.Update;

namespace JeekMusic.App.Tests;

public sealed class PortableUpdateTests
{
    [Theory]
    [InlineData("data/settings.json", false)]
    [InlineData("Data\\library.db", false)]
    [InlineData("data/logs/app.log", false)]
    [InlineData("runtime/JeekMusic.dll", true)]
    [InlineData("Models/yamnet.onnx", true)]
    [InlineData("portable.json", true)]
    [InlineData("JeekMusic.exe", true)]
    public void UpdatePreservesUserDataWhileReplacingPayload(string path, bool expected) =>
        Assert.Equal(expected, Updater.IsPayloadFile(path));
}

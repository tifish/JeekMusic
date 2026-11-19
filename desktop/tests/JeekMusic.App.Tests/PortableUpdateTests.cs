using System.Net;
using System.Net.Http;
using JeekMusic.Update;

namespace JeekMusic.App.Tests;

public sealed class PortableUpdateTests
{
    [Fact]
    public async Task SelectsNewestPublishedDesktopZipByNumericVersion()
    {
        using var handler = new ReleaseHandler();
        using var http = new HttpClient(handler);
        var update = Assert.IsType<UpdateInfo>(await UpdateService.FindReleaseAsync(http, currentVersion: 9));
        Assert.Equal("desktop-v1.0.11", update.Tag);
        Assert.Equal("1.0.11", update.VersionName);
        Assert.Equal(42, update.Size);
        Assert.Equal(new string('a', 64), update.Sha256);
        Assert.Equal(new[] { "git/matching-refs/tags/desktop-v1.0.", "releases/tags/desktop-v1.0.14",
            "releases/tags/desktop-v1.0.13", "releases/tags/desktop-v1.0.12", "releases/tags/desktop-v1.0.11" }, handler.Requests);

        handler.Requests.Clear();
        Assert.Null(await UpdateService.FindReleaseAsync(http, currentVersion: 14));
        Assert.Single(handler.Requests);
    }

    sealed class ReleaseHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/repos/tifish/JeekMusic/".Length..];
            Requests.Add(path);
            var json = path switch
            {
                "git/matching-refs/tags/desktop-v1.0." => """
                    [{"ref":"refs/tags/desktop-v1.0.10"},{"ref":"refs/tags/desktop-v1.0.11"},
                     {"ref":"refs/tags/v1.0.900"},{"ref":"refs/tags/desktop-v1.0.14"},
                     {"ref":"refs/tags/desktop-v1.0.13"},{"ref":"refs/tags/desktop-v1.0.12"},
                     {"ref":"refs/tags/desktop-v1.0.99-preview"}]
                    """,
                "releases/tags/desktop-v1.0.14" => null,
                "releases/tags/desktop-v1.0.13" => """{"draft":false,"prerelease":true,"assets":[]}""",
                "releases/tags/desktop-v1.0.12" => """
                    {"draft":false,"prerelease":false,"assets":[{"name":"sources.zip","state":"uploaded"},
                     {"name":"JeekMusic-desktop-1.0.12-win-x64.zip","state":"starter"}]}
                    """,
                "releases/tags/desktop-v1.0.11" => $$"""
                    {"draft":false,"prerelease":false,"body":"Changes","assets":[
                     {"name":"JeekMusic-desktop-1.0.11-win-x64.zip","state":"uploaded","size":42,
                      "browser_download_url":"https://github.com/tifish/JeekMusic/releases/download/desktop-v1.0.11/JeekMusic-desktop-1.0.11-win-x64.zip",
                      "digest":"sha256:{{new string('a', 64)}}" }]}
                    """,
                _ => throw new InvalidOperationException("Unexpected request: " + path)
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    [Theory]
    [InlineData("data/settings.json", false)]
    [InlineData("Data\\library.db", false)]
    [InlineData("data/logs/app.log", false)]
    [InlineData("runtime/JeekMusic.dll", true)]
    [InlineData("Models/yamnet.onnx", true)]
    [InlineData("portable.json", true)]
    [InlineData("JeekMusic.exe", true)]
    public void UpdatePreservesUserDataWhileReplacingPayload(string path, bool expected) =>
        Assert.Equal(expected, Updater.IsPayloadFile(path, portableTarget: true));

    [Fact]
    public void LegacyInstallKeepsItsExistingDataLocation()
    {
        Assert.False(Updater.IsPayloadFile("portable.json", portableTarget: false));
        Assert.False(Updater.IsPayloadFile("data/settings.json", portableTarget: false));
        Assert.True(Updater.IsPayloadFile("JeekMusic.exe", portableTarget: false));
    }
}

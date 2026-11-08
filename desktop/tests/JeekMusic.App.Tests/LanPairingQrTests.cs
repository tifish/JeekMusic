using System.Net;
using JeekMusic.Sync;

namespace JeekMusic.App.Tests;

public sealed class LanPairingQrTests
{
    [Theory]
    [InlineData("music.example.com:45678", "tls://music.example.com:45678")]
    [InlineData(" https://MUSIC.example.com:45678/ ", "tls://music.example.com:45678")]
    [InlineData("tls://203.0.113.7:39843", "tls://203.0.113.7:39843")]
    [InlineData("https://music.example.com", "tls://music.example.com:443")]
    [InlineData("", "")]
    public void ExternalAddressNormalizes(string input, string expected) => Assert.Equal(expected, LanPairingQr.NormalizeExternalAddress(input));

    [Theory]
    [InlineData("music.example.com")]
    [InlineData("http://music.example.com:39843")]
    [InlineData("https://music.example.com:39843/path")]
    [InlineData("tls://user:pass@music.example.com:39843")]
    [InlineData("tls://music.example.com:39843?q=x")]
    [InlineData("tls://music.example.com:39843#x")]
    [InlineData("tls://localhost:39843")]
    [InlineData("tls://127.0.0.1:39843")]
    [InlineData("tls://0.0.0.0:39843")]
    [InlineData("tls://224.0.0.1:39843")]
    [InlineData("tls://[::1]:39843")]
    [InlineData("tls://music.example.com:0")]
    [InlineData("tls://music.example.com:65536")]
    [InlineData("tls://music.example.com:39843\\path")]
    public void ExternalAddressRejectsInvalidEndpoints(string input) => Assert.Throws<ArgumentException>(() => LanPairingQr.NormalizeExternalAddress(input));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("192.168.1.255", false)]
    [InlineData("192.168.1.2", true)]
    [InlineData("10.0.2.2", true)]
    public void PhoneAddressRejectsUnusableEndpoints(string ip, bool expected) => Assert.Equal(expected, LanPairingQr.IsPhoneAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task HostValidationAndPngEncodingUseProductionHelpers()
    {
        foreach (var host in new[] { "localhost", "127.0.0.1", "0.0.0.0", "224.0.0.1", "255.255.255.255", "192.168.1.2/path", "a\n" })
            await Assert.ThrowsAnyAsync<Exception>(() => LanPairingQr.ValidatePhoneHost(host));
        await LanPairingQr.ValidatePhoneHost("10.0.2.2");
        // Deliberately invalid/expired credential, safe for test artifact independent decoding.
        var png = LanPairingQr.Encode("jeeklan://10.0.2.2:39843/" + new string('0', 64) + "/" + new string('0', 64));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8));
        Assert.InRange(png.Length, 100, 100_000);
    }
}

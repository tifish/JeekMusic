using System.Net;
using System.Net.Sockets;
using Net.Codecrete.QrCodeGenerator;

namespace JeekMusic.Sync;

internal static class LanPairingQr
{
    // This restriction is for phone-facing invitations. MCP loopback integration remains supported.
    internal static bool IsPhoneAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return !IPAddress.IsLoopback(address) && b[0] != 0 && b[0] < 224 && b[3] != 255;
    }

    internal static async Task ValidatePhoneHost(string host)
    {
        if (host.Length is < 1 or > 253 || host.Any(c => c <= ' ' || c > 126) ||
            Uri.CheckHostName(host) is not (UriHostNameType.IPv4 or UriHostNameType.Dns))
            throw new ArgumentException("Invalid LAN address");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is SocketException or TimeoutException)
        { throw new ArgumentException("Cannot resolve a usable LAN IPv4 address"); }
        if (addresses.Length == 0 || addresses.Any(a => !IsPhoneAddress(a))) throw new ArgumentException("Address is not reachable from a phone");
    }

    internal static byte[] Encode(string invitation) => QrCode.EncodeText(invitation, QrCode.Ecc.Medium).ToPngBitmap(6, 4);
}

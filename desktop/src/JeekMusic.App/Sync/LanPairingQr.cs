using System.Net;
using System.Net.Sockets;
using Net.Codecrete.QrCodeGenerator;

namespace JeekMusic.Sync;

internal static class LanPairingQr
{
    // This is a TLS TCP endpoint, never an HTTP path or reverse-proxy URL.
    internal static (string Host, int Port) ParseExternalAddress(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 300 || value.Any(c => c <= ' ' || c > 126) || value.Contains('\\'))
            throw new ArgumentException("Enter hostname:port or tls://hostname:port");
        var text = value.Contains("://", StringComparison.Ordinal) ? value : "tls://" + value;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("tls" or "https" or "jeeklan") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.Dns) || uri.Port is < 1 or > 65535 ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host, out var ip) && !IsPhoneAddress(ip))
            throw new ArgumentException("Use a phone-reachable TLS TCP hostname and port, without a path");
        return (uri.IdnHost.ToLowerInvariant(), uri.Port);
    }

    internal static string NormalizeExternalAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var endpoint = ParseExternalAddress(value);
        return $"tls://{endpoint.Host}:{endpoint.Port}";
    }

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

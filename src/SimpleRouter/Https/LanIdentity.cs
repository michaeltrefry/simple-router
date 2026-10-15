using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SimpleRouter.Https;

/// <summary>The names and addresses other machines on the LAN may use to reach this one.</summary>
public sealed class LanIdentity(Func<string> hostName, Func<IReadOnlyList<IPAddress>> addresses)
{
    public static LanIdentity System { get; } = new(Dns.GetHostName, InterfaceAddresses);

    /// <summary>The host name's first label, lower-cased: <c>studio</c> for <c>Studio.local</c> or <c>studio.lan</c>.</summary>
    public string ShortName
    {
        get
        {
            var name = FullName;
            var dot = name.IndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
    }

    private string FullName => hostName().Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>The Bonjour name first (what Claude Desktop is pointed at), then the bare and full host names.</summary>
    public IReadOnlyList<string> DnsNames() => new[] { $"{ShortName}.local", ShortName, FullName }.Distinct().ToList();

    /// <summary>
    /// Non-loopback unicast addresses. IPv6 link-local addresses are left out: a URL can only reach them with a zone ID,
    /// which browsers and Electron do not accept.
    /// </summary>
    public IReadOnlyList<IPAddress> LanAddresses() => addresses()
        .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
        .Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(a.GetAddressBytes()) : a)
        .Distinct()
        .ToList();

    public string LanUrl(int httpsPort) => $"https://{ShortName}.local:{httpsPort}";

    private static IReadOnlyList<IPAddress> InterfaceAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .ToList();
}

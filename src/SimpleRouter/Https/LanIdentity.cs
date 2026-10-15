using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SimpleRouter.Https;

/// <summary>
/// The names and addresses other machines on the LAN may use to reach this one. <paramref name="localHostName"/> is the
/// Bonjour (mDNS) name, null where there is none; on macOS the host name can come from DHCP or reverse DNS and differ
/// from it, and only the Bonjour name resolves as <c>&lt;name&gt;.local</c>.
/// </summary>
public sealed class LanIdentity(Func<string> hostName, Func<string?> localHostName, Func<IReadOnlyList<IPAddress>> addresses)
{
    private static readonly Lazy<string?> MacLocalHostName = new(ReadMacLocalHostName);

    public static LanIdentity System { get; } = new(Dns.GetHostName, () => MacLocalHostName.Value, InterfaceAddresses);

    /// <summary>The host name's first label, lower-cased: <c>studio</c> for <c>Studio.local</c> or <c>studio.lan</c>.</summary>
    private string ShortName
    {
        get
        {
            var name = FullName;
            var dot = name.IndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
    }

    private string FullName => hostName().Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>The Bonjour name, else the host name's first label.</summary>
    public string BonjourName => localHostName()?.Trim().ToLowerInvariant() is { Length: > 0 } name ? name : ShortName;

    /// <summary>The Bonjour <c>.local</c> name first (what Claude Desktop is pointed at), then the bare and full host names.</summary>
    public IReadOnlyList<string> DnsNames() => new[] { $"{BonjourName}.local", ShortName, FullName }.Distinct().ToList();

    /// <summary>
    /// Non-loopback unicast addresses. IPv6 link-local addresses are left out: a URL can only reach them with a zone ID,
    /// which browsers and Electron do not accept.
    /// </summary>
    public IReadOnlyList<IPAddress> LanAddresses() => addresses()
        .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
        .Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(a.GetAddressBytes()) : a)
        .Distinct()
        .ToList();

    public string LanUrl(int httpsPort) => $"https://{BonjourName}.local:{httpsPort}";

    private static string? ReadMacLocalHostName()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        try
        {
            using var scutil = Process.Start(new ProcessStartInfo("/usr/sbin/scutil", ["--get", "LocalHostName"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (scutil is null)
            {
                return null;
            }
            var output = scutil.StandardOutput.ReadToEndAsync();
            if (!scutil.WaitForExit(TimeSpan.FromSeconds(2)))
            {
                scutil.Kill();
                return null;
            }
            return scutil.ExitCode == 0 ? output.Result.Trim() : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<IPAddress> InterfaceAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .ToList();
}

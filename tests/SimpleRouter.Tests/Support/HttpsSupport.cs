using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Cli;
using SimpleRouter.Https;

namespace SimpleRouter.Tests.Support;

/// <summary>Creates router certificates in a test data directory and calls the router over HTTPS trusting only a given certificate.</summary>
public static class HttpsSupport
{
    public const string HostName = "testhost";
    public static readonly IPAddress LanV4 = IPAddress.Parse("192.168.1.20");
    public static readonly IPAddress LanV6 = IPAddress.Parse("fd00::20");

    /// <summary>A machine named <see cref="HostName"/> (no separate Bonjour name) with two LAN addresses and a link-local one.</summary>
    public static Action<IServiceCollection> Lan() => Lan(HostName, null);

    public static Action<IServiceCollection> Lan(string hostName, string? localHostName) => services =>
        services.AddSingleton(new LanIdentity(() => hostName, () => localHostName,
            () => [LanV4, LanV6, IPAddress.Parse("fe80::1%4")]));

    public static async Task<(int ExitCode, string Output)> CreateCertificateAsync(string dataDirectory, Action<IServiceCollection>? lan = null)
    {
        var output = new StringWriter();
        var exitCode = await CommandLine.RunAsync(["cert", "create", .. RouterHost.Args(dataDirectory), "--SimpleRouter:HttpsPort=8791"], TextReader.Null, output, lan ?? Lan());
        RouterHost.ClearPool(dataDirectory);
        return (exitCode, output.ToString());
    }

    /// <summary>The exported CA certificate other machines trust.</summary>
    public static X509Certificate2 PublicCertificate(string dataDirectory) =>
        X509CertificateLoader.LoadCertificateFromFile(Path.Combine(dataDirectory, "certs", "router.crt"));

    public static X509Certificate2 LeafCertificate(string dataDirectory) =>
        X509CertificateLoader.LoadCertificateFromFile(Path.Combine(dataDirectory, "certs", "router-leaf.crt"));

    public static IReadOnlyList<Uri> Addresses(RouterHost host) =>
        host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a)).ToList();

    public static int HttpsPort(RouterHost host) => Addresses(host).Single(a => a.Scheme == Uri.UriSchemeHttps).Port;

    /// <summary>
    /// A client that dials the router's loopback HTTPS port whatever host the URL names (so the TLS name check runs
    /// against <c>testhost.local</c> without DNS) and accepts only a chain ending at <paramref name="trusted"/>.
    /// </summary>
    public static HttpClient PinnedClient(X509Certificate2 trusted, int port, string host, Action<X509Certificate2>? served = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                    {
                        return false;
                    }
                    var presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    var trustedChain = TrustsOnly(trusted, presented);
                    if (served is null)
                    {
                        presented.Dispose();
                    }
                    else
                    {
                        served(presented);
                    }
                    return trustedChain;
                },
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri($"https://{host}:{port}") };
    }

    private static bool TrustsOnly(X509Certificate2 anchor, X509Certificate2 presented)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(anchor);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(presented);
    }

    /// <summary>A password-protected PKCS#12 file for <c>localhost</c>, standing in for a certificate the user brings.</summary>
    public static (string Path, X509Certificate2 Public) UserCertificate(string directory, string password, TimeSpan lifetime)
    {
        using var certificate = SelfSignedLeaf("user-supplied", lifetime);
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"user-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        return (path, X509CertificateLoader.LoadCertificate(certificate.RawData));
    }

    /// <summary>A self-signed end-entity certificate with its key for <c>localhost</c> and <c>127.0.0.1</c>.</summary>
    public static X509Certificate2 SelfSignedLeaf(string commonName, TimeSpan lifetime)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.Add(lifetime));
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SimpleRouter.Https;

/// <summary>
/// Builds the router's self-signed server certificate in the shape of the ASP.NET Core development certificate, which
/// macOS (Keychain "Always Trust"), Windows (Trusted Root store) and Chromium all accept as a trusted self-signed
/// server certificate: RSA 2048 with SHA-256, an end-entity (critical <c>CA:false</c>) so it can never sign another
/// certificate, critical key usage digitalSignature + keyEncipherment, serverAuth only, and the host names and
/// addresses as subject alternative names (Chromium ignores the common name).
/// </summary>
public static class RouterCertificateFactory
{
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>Apple rejects TLS server certificates valid for more than 398 days.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(397);

    public static X509Certificate2 Create(string commonName, IEnumerable<string> dnsNames, IEnumerable<IPAddress> addresses, DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(commonName);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var names = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
        {
            names.AddDnsName(name);
        }
        foreach (var address in addresses)
        {
            names.AddIpAddress(address);
        }
        request.CertificateExtensions.Add(names.Build());
        // Backdated a little so a client whose clock runs slightly behind does not see a not-yet-valid certificate.
        return request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Lifetime));
    }
}

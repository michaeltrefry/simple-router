using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SimpleRouter.Https;

/// <summary>
/// Builds the router's HTTPS certificates: a single-use private CA and the server leaf it signs. Claude Code's TLS stack
/// rejects a self-signed end-entity certificate even when told to trust it, so clients trust the CA instead. The CA
/// signs exactly one leaf and its private key is then discarded, never stored; it may only issue end-entity
/// certificates (path length 0) for DNS names under <c>localhost</c>, <c>.local</c> and the leaf's other host names.
/// The name constraints are DNS-only because Claude Code rejects IP-address constraints; IP SANs are unconstrained.
/// Both are RSA 2048 with SHA-256, and the leaf has the shape macOS, Windows and Chromium accept for a TLS server.
/// </summary>
public static class RouterCertificateFactory
{
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string NameConstraintsOid = "2.5.29.30";

    /// <summary>Apple rejects TLS server certificates valid for more than 398 days.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(397);

    /// <summary>Returns the leaf with its private key and the CA certificate without one.</summary>
    public static (X509Certificate2 Leaf, X509Certificate2 Authority) Create(
        string commonName, IReadOnlyList<string> dnsNames, IEnumerable<IPAddress> addresses, DateTimeOffset now)
    {
        // Backdated a little so a client whose clock runs slightly behind does not see a not-yet-valid certificate.
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.Add(Lifetime);
        using var authority = CreateAuthority($"{commonName} CA", PermittedDnsSubtrees(dnsNames), notBefore, notAfter);
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest(Name(commonName), leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));
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
        using var leaf = request.Create(authority, notBefore, notAfter, SerialNumber());
        return (leaf.CopyWithPrivateKey(leafKey), X509CertificateLoader.LoadCertificate(authority.RawData));
    }

    /// <summary><c>localhost</c> and <c>.local</c>, plus each other leaf name as an exact subtree.</summary>
    public static IReadOnlyList<string> PermittedDnsSubtrees(IEnumerable<string> dnsNames) =>
        new[] { "localhost", ".local" }
            .Concat(dnsNames.Where(n => !n.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !n.EndsWith(".local", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static X509Certificate2 CreateAuthority(string commonName, IReadOnlyList<string> permitted, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(Name(commonName), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, DnsNameConstraints(permitted), critical: true));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>RFC 5280 NameConstraints with only permitted dNSName subtrees.</summary>
    private static byte[] DnsNameConstraints(IEnumerable<string> permitted)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            foreach (var name in permitted)
            {
                using (writer.PushSequence())
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
                }
            }
        }
        return writer.Encode();
    }

    private static X500DistinguishedName Name(string commonName)
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(commonName);
        return subject.Build();
    }

    private static byte[] SerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return serial;
    }
}

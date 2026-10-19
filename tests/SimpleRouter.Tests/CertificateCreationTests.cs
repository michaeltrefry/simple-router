using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary><c>cert create</c>: a single-use CA other machines trust and the server certificate it signed for the router's LAN names.</summary>
public class CertificateCreationTests
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    private static async Task<(string DataDirectory, string Output)> CreateAsync()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var (exitCode, output) = await HttpsSupport.CreateCertificateAsync(dataDirectory);
        Assert.Equal(0, exitCode);
        return (dataDirectory, output);
    }

    [Fact]
    public async Task The_certificate_names_this_machine_and_its_lan_and_loopback_addresses()
    {
        var (dataDirectory, _) = await CreateAsync();
        using var certificate = HttpsSupport.LeafCertificate(dataDirectory);

        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Equal(["testhost.local", "testhost", "localhost"], names.EnumerateDnsNames());
        Assert.Equal(
            [HttpsSupport.LanV4, HttpsSupport.LanV6, IPAddress.Loopback, IPAddress.IPv6Loopback],
            names.EnumerateIPAddresses());
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task The_server_certificate_is_an_rsa_leaf_signed_by_the_exported_ca_within_the_platform_lifetime_cap()
    {
        var (dataDirectory, _) = await CreateAsync();
        using var authority = HttpsSupport.PublicCertificate(dataDirectory);
        using var certificate = HttpsSupport.LeafCertificate(dataDirectory);

        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, usage.KeyUsages);
        Assert.Equal([ServerAuth], certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.Critical);
        Assert.False(constraints.CertificateAuthority);
        Assert.Equal(authority.SubjectName.RawData, certificate.IssuerName.RawData);
        Assert.NotEqual(certificate.SubjectName.RawData, certificate.IssuerName.RawData);
        Assert.Equal(
            authority.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifierBytes.ToArray(),
            certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single().KeyIdentifier!.Value.ToArray());
        Assert.True(BuildsTo(authority, certificate));
        Assert.Equal(2048, certificate.GetRSAPublicKey()!.KeySize);
        Assert.InRange((certificate.NotAfter - certificate.NotBefore).TotalDays, 390, 398);
        Assert.Equal("1.2.840.113549.1.1.11", certificate.SignatureAlgorithm.Value); // sha256WithRSAEncryption
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task The_ca_may_sign_only_end_entities_for_dns_names_it_permits_and_permits_every_name_the_leaf_carries()
    {
        var (dataDirectory, _) = await CreateAsync();
        using var authority = HttpsSupport.PublicCertificate(dataDirectory);
        using var leaf = HttpsSupport.LeafCertificate(dataDirectory);

        var constraints = authority.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.Critical);
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);
        var usage = authority.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, usage.KeyUsages);
        Assert.Equal(authority.SubjectName.RawData, authority.IssuerName.RawData);
        Assert.InRange((authority.NotAfter - authority.NotBefore).TotalDays, 390, 398);
        var nameConstraints = authority.Extensions.Single(e => e.Oid!.Value == NameConstraintsOid);
        Assert.True(nameConstraints.Critical);
        var permitted = PermittedDnsSubtrees(nameConstraints);
        Assert.Equal(["localhost", ".local", "testhost"], permitted);
        Assert.All(leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames(),
            name => Assert.Contains(permitted, subtree => Permits(subtree, name)));
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task The_ca_private_key_is_never_stored_and_the_encrypted_file_holds_only_the_server_key()
    {
        var (dataDirectory, _) = await CreateAsync();
        using var authority = HttpsSupport.PublicCertificate(dataDirectory);
        using var leaf = HttpsSupport.LeafCertificate(dataDirectory);
        using var authorityKey = authority.GetRSAPublicKey()!;
        var modulus = authorityKey.ExportParameters(false).Modulus!;
        await using var host = await RouterHost.StartInAsync(dataDirectory);
        var protector = host.Services.GetRequiredService<SecretProtector>();

        var keyBearing = new List<X509Certificate2>();
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("-shm") && !f.EndsWith("-wal")))
        {
            // The live router's SQLite connection has this file open for writing on Windows.
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy);
            var bytes = copy.ToArray();
            Assert.False(bytes.AsSpan().IndexOf(modulus) >= 0, $"{file} holds the CA key material");
            Assert.DoesNotContain("PRIVATE KEY", System.Text.Encoding.Latin1.GetString(bytes));
            if (TryUnprotectPkcs12(protector, bytes) is { } stored)
            {
                keyBearing.AddRange(stored.Where(c => c.HasPrivateKey));
                Assert.Contains(stored, c => c.Thumbprint == authority.Thumbprint && !c.HasPrivateKey);
            }
        }

        Assert.Equal([leaf.Thumbprint], keyBearing.Select(c => c.Thumbprint));
    }

    [Fact]
    public async Task The_private_key_is_owner_only_and_encrypted_and_the_exported_file_holds_only_the_public_certificate()
    {
        var (dataDirectory, _) = await CreateAsync();
        var certs = Path.Combine(dataDirectory, "certs");
        var keyFile = Path.Combine(certs, "router.pfx.protected");

        var exported = await File.ReadAllTextAsync(Path.Combine(certs, "router.crt"));
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", exported);
        Assert.DoesNotContain("PRIVATE KEY", exported);
        Assert.False(HttpsSupport.PublicCertificate(dataDirectory).HasPrivateKey);
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(keyFile, null));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(certs));
        }
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task A_bonjour_name_that_differs_from_the_dhcp_host_name_is_the_local_name_and_the_url()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var (exitCode, output) = await HttpsSupport.CreateCertificateAsync(dataDirectory, HttpsSupport.Lan("dhcp-name.lan", "Studio"));
        using var certificate = HttpsSupport.LeafCertificate(dataDirectory);

        Assert.Equal(0, exitCode);
        Assert.Equal(["studio.local", "dhcp-name", "dhcp-name.lan", "localhost"],
            certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
        Assert.Contains("https://studio.local:8791", output);
        using var authority = HttpsSupport.PublicCertificate(dataDirectory);
        Assert.Equal(["localhost", ".local", "dhcp-name", "dhcp-name.lan"],
            PermittedDnsSubtrees(authority.Extensions.Single(e => e.Oid!.Value == NameConstraintsOid)));
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task An_existing_world_readable_certs_folder_is_made_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var dataDirectory = RouterHost.NewDataDirectory();
        var certs = Path.Combine(dataDirectory, "certs");
        Directory.CreateDirectory(certs);
        File.SetUnixFileMode(certs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Assert.Equal(0, (await HttpsSupport.CreateCertificateAsync(dataDirectory)).ExitCode);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(certs));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(certs, "router.pfx.protected")));
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task The_command_prints_the_file_names_fingerprint_and_import_commands()
    {
        var (dataDirectory, output) = await CreateAsync();
        using var certificate = HttpsSupport.PublicCertificate(dataDirectory);

        Assert.Contains(Path.Combine(dataDirectory, "certs", "router.crt"), output);
        Assert.Contains("testhost.local, testhost, localhost, 192.168.1.20, fd00::20, 127.0.0.1, ::1", output);
        Assert.Contains(string.Join(':', Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c))), output);
        Assert.Contains("sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain", output);
        Assert.Contains("certutil -addstore Root", output);
        Assert.Contains("https://testhost.local:8791", output);
        Assert.DoesNotContain("Replaced", output);
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task Running_it_again_replaces_the_certificate_and_warns_that_other_machines_must_re_import()
    {
        var (dataDirectory, _) = await CreateAsync();
        using var first = HttpsSupport.PublicCertificate(dataDirectory);

        var (exitCode, output) = await HttpsSupport.CreateCertificateAsync(dataDirectory);
        using var second = HttpsSupport.PublicCertificate(dataDirectory);

        Assert.Equal(0, exitCode);
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        Assert.Contains("Replaced", output);
        Assert.Contains("re-import", output);
        Directory.Delete(dataDirectory, recursive: true);
    }

    private const string NameConstraintsOid = "2.5.29.30";

    /// <summary>The permitted dNSName subtrees, failing on any other kind of name or on excluded subtrees.</summary>
    private static List<string> PermittedDnsSubtrees(X509Extension nameConstraints)
    {
        var reader = new AsnReader(nameConstraints.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        var subtrees = sequence.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        sequence.ThrowIfNotEmpty();
        var names = new List<string>();
        while (subtrees.HasData)
        {
            var subtree = subtrees.ReadSequence();
            Assert.Equal(new Asn1Tag(TagClass.ContextSpecific, 2), subtree.PeekTag());
            names.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
            subtree.ThrowIfNotEmpty();
        }
        return names;
    }

    /// <summary>RFC 5280 dNSName matching: the name itself or any name under it; a leading dot permits only names under it.</summary>
    private static bool Permits(string subtree, string name) => subtree.StartsWith('.')
        ? name.EndsWith(subtree, StringComparison.OrdinalIgnoreCase)
        : name.Equals(subtree, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + subtree, StringComparison.OrdinalIgnoreCase);

    private static bool BuildsTo(X509Certificate2 anchor, X509Certificate2 leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(anchor);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf) && chain.ChainElements.Count == 2;
    }

    private static X509Certificate2Collection? TryUnprotectPkcs12(SecretProtector protector, byte[] bytes)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12Collection(Convert.FromBase64String(protector.Unprotect(System.Text.Encoding.UTF8.GetString(bytes))), null);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return null;
        }
    }
}

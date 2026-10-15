using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary><c>cert create</c>: a self-signed server certificate other machines can trust for the router's LAN names.</summary>
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
        using var certificate = HttpsSupport.PublicCertificate(dataDirectory);

        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Equal(["testhost.local", "testhost", "localhost"], names.EnumerateDnsNames());
        Assert.Equal(
            [HttpsSupport.LanV4, HttpsSupport.LanV6, IPAddress.Loopback, IPAddress.IPv6Loopback],
            names.EnumerateIPAddresses());
        Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task The_certificate_is_a_self_signed_rsa_server_leaf_within_the_platform_lifetime_cap()
    {
        var (dataDirectory, _) = await CreateAsync();
        using var certificate = HttpsSupport.PublicCertificate(dataDirectory);

        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, usage.KeyUsages);
        Assert.Equal([ServerAuth], certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.Critical);
        Assert.False(constraints.CertificateAuthority);
        Assert.Equal(certificate.SubjectName.RawData, certificate.IssuerName.RawData);
        Assert.Equal(2048, certificate.GetRSAPublicKey()!.KeySize);
        Assert.InRange((certificate.NotAfter - certificate.NotBefore).TotalDays, 390, 398);
        Assert.Equal("1.2.840.113549.1.1.11", certificate.SignatureAlgorithm.Value); // sha256WithRSAEncryption
        Directory.Delete(dataDirectory, recursive: true);
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
        using var certificate = HttpsSupport.PublicCertificate(dataDirectory);

        Assert.Equal(0, exitCode);
        Assert.Equal(["studio.local", "dhcp-name", "dhcp-name.lan", "localhost"],
            certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
        Assert.Contains("https://studio.local:8791", output);
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
}

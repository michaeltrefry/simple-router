using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace SimpleRouter.Https;

/// <summary>
/// The certificate HTTPS is served with: the configured PKCS#12 file when <c>Https:CertificatePath</c> is set, else the
/// one <c>cert create</c> stored in the data directory, else none (HTTPS off). The generated private key is stored as a
/// PKCS#12 blob encrypted with <see cref="SecretProtector"/> in an owner-only file; only the public certificate is
/// exported as <c>router.crt</c>. Disposing releases the loaded key (on macOS, a temporary keychain).
/// </summary>
public sealed class ServerCertificate : IDisposable
{
    private static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    private readonly RouterOptions _options;
    private readonly SecretProtector _protector;
    private readonly LanIdentity _lan;
    private readonly TimeProvider _time;
    private readonly Lazy<X509Certificate2?> _current;

    public ServerCertificate(IOptions<RouterOptions> options, SecretProtector protector, LanIdentity lan, TimeProvider time)
    {
        _options = options.Value;
        _protector = protector;
        _lan = lan;
        _time = time;
        _current = new Lazy<X509Certificate2?>(Load);
    }

    public string Folder => Path.Combine(_options.DataDirectory, "certs");
    public string PublicPath => Path.Combine(Folder, "router.crt");
    public string KeyPath => Path.Combine(Folder, "router.pfx.protected");
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Https.CertificatePath);

    /// <summary>The certificate to serve HTTPS with, or null when there is none.</summary>
    public X509Certificate2? Current => _current.Value;

    /// <summary>Where <see cref="Current"/> came from, for display; never the key.</summary>
    public string Source => IsConfigured ? _options.Https.CertificatePath! : PublicPath;

    /// <summary>Generates a certificate for this machine's current names and addresses, replacing any earlier one.</summary>
    public (X509Certificate2 Certificate, bool Replaced) Create()
    {
        var replaced = File.Exists(KeyPath);
        using var certificate = RouterCertificateFactory.Create(
            $"Simple Router ({_lan.BonjourName})",
            [.. _lan.DnsNames(), "localhost"],
            [.. _lan.LanAddresses(), IPAddress.Loopback, IPAddress.IPv6Loopback],
            _time.GetUtcNow());

        CreateOwnerOnlyFolder(Folder);
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            WriteOwnerOnly(KeyPath, _protector.Protect(Convert.ToBase64String(pfx)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
        File.WriteAllText(PublicPath, certificate.ExportCertificatePem() + "\n");
        return (X509CertificateLoader.LoadCertificate(certificate.RawData), replaced);
    }

    public void Dispose()
    {
        if (_current.IsValueCreated)
        {
            _current.Value?.Dispose();
        }
    }

    public bool ExpiresSoon(X509Certificate2 certificate) =>
        new DateTimeOffset(certificate.NotAfter.ToUniversalTime()) - _time.GetUtcNow() < ExpiryWarning;

    /// <summary>
    /// This machine's names and IPv4 addresses (or the single listen address) that the certificate does not cover.
    /// IPv6 LAN addresses are not checked: temporary (privacy) addresses rotate daily, and a client reaching the
    /// router over IPv6 uses its name.
    /// </summary>
    public IReadOnlyList<string> UncoveredNames(X509Certificate2 certificate, IPAddress listenAddress)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var dnsNames = names?.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var addresses = names?.EnumerateIPAddresses().ToHashSet() ?? [];
        var wildcard = listenAddress.Equals(IPAddress.Any) || listenAddress.Equals(IPAddress.IPv6Any);
        IEnumerable<IPAddress> expected = wildcard
            ? _lan.LanAddresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            : [listenAddress];
        return _lan.DnsNames().Where(n => !dnsNames.Contains(n))
            .Concat(expected.Where(a => !addresses.Contains(a)).Select(a => a.ToString()))
            .ToList();
    }

    /// <summary>Logs where HTTPS is served, the certificate's expiry, and warnings a LAN client would otherwise hit as a TLS error.</summary>
    public void LogStatus(ILogger logger, IPAddress listenAddress, int httpsPort)
    {
        if (Current is not { } certificate)
        {
            return;
        }
        logger.LogInformation("HTTPS certificate {Source} (SHA-256 {Fingerprint}) expires {Expiry:yyyy-MM-dd}.",
            Source, Fingerprint(certificate), certificate.NotAfter);
        if (ExpiresSoon(certificate))
        {
            logger.LogWarning("The HTTPS certificate expires {Expiry:yyyy-MM-dd}; run 'simple-router cert create' and re-import router.crt on the other machines.",
                certificate.NotAfter);
        }
        if (IPAddress.IsLoopback(listenAddress))
        {
            return;
        }
        logger.LogInformation("LAN clients: {LanUrl}", _lan.LanUrl(httpsPort));
        var uncovered = UncoveredNames(certificate, listenAddress);
        if (uncovered.Count > 0)
        {
            logger.LogWarning("The HTTPS certificate does not name {Names}, so clients using them fail the TLS check; run 'simple-router cert create' after a host name or IP change and re-import router.crt.",
                string.Join(", ", uncovered));
        }
    }

    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c)));

    private X509Certificate2? Load()
    {
        if (IsConfigured)
        {
            return LoadConfigured(_options.Https.CertificatePath!, _options.Https.Password);
        }
        if (!File.Exists(KeyPath))
        {
            return null;
        }
        byte[]? pfx = null;
        try
        {
            pfx = Convert.FromBase64String(_protector.Unprotect(File.ReadAllText(KeyPath)));
            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                $"The router's HTTPS certificate in {Folder} cannot be decrypted with this data directory's key ring; run 'simple-router cert create' to replace it.", e);
        }
        finally
        {
            if (pfx is not null)
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
    }

    private static X509Certificate2 LoadConfigured(string path, string? password)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{RouterOptions.Section}:Https:CertificatePath '{path}' could not be opened as a PKCS#12 file ({e.GetType().Name}); check the path and {RouterOptions.Section}:Https:Password.", e);
        }
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"{RouterOptions.Section}:Https:CertificatePath '{path}' holds no private key.");
        }
        return certificate;
    }

    private static void CreateOwnerOnlyFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, ownerOnly);
        File.SetUnixFileMode(path, ownerOnly);
    }

    private static void WriteOwnerOnly(string path, string contents)
    {
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var writer = new StreamWriter(temporary, options))
        {
            writer.Write(contents);
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

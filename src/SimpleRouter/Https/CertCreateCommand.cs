using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace SimpleRouter.Https;

/// <summary><c>cert create</c>: generates the router's HTTPS certificate and prints how to trust it on other machines.</summary>
public sealed class CertCreateCommand(ServerCertificate certificates, LanIdentity lan, IOptions<RouterOptions> options)
{
    public async Task<int> RunAsync(TextWriter output)
    {
        var (certificate, replaced) = certificates.Create();
        using (certificate)
        {
            var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            var lanUrl = lan.LanUrl(options.Value.HttpsPort);
            await output.WriteLineAsync($"""
                {(replaced ? "Replaced the router's HTTPS certificate. Machines that trusted the old one must re-import router.crt." : "Created the router's HTTPS certificate.")}
                  Certificate (copy to other machines): {certificates.PublicPath}
                  Private key (encrypted, stays here):  {certificates.KeyPath}
                  Names: {string.Join(", ", names.EnumerateDnsNames().Concat(names.EnumerateIPAddresses().Select(a => a.ToString())))}
                  SHA-256 fingerprint: {ServerCertificate.Fingerprint(certificate)}
                  Expires: {certificate.NotAfter:yyyy-MM-dd}

                Trust it on each machine that connects (compare the fingerprint after copying router.crt):
                  macOS:   sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain router.crt
                           (or Keychain Access: drag router.crt into the System keychain, open it, Trust > Always Trust)
                  Windows: certutil -addstore Root router.crt   (in an administrator prompt; for this user only: certutil -user -addstore Root router.crt)

                Restart the router with --SimpleRouter:ListenAddress=0.0.0.0 and point LAN clients at {lanUrl}.
                Run 'cert create' again after this machine's name or IP changes.
                """);
            if (certificates.IsConfigured)
            {
                await output.WriteLineAsync($"Note: {RouterOptions.Section}:Https:CertificatePath is set, so the router serves that certificate instead.");
            }
        }
        return 0;
    }
}

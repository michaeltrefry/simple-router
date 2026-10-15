using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace SimpleRouter.Https;

/// <summary><c>cert create</c>: generates the router's CA and HTTPS certificate and prints how to trust the CA on other machines.</summary>
public sealed class CertCreateCommand(ServerCertificate certificates, LanIdentity lan, IOptions<RouterOptions> options)
{
    public async Task<int> RunAsync(TextWriter output)
    {
        var (leaf, authority, replaced) = certificates.Create();
        using (leaf)
        using (authority)
        {
            var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            var lanUrl = lan.LanUrl(options.Value.HttpsPort);
            await output.WriteLineAsync($"""
                {(replaced ? "Replaced the router's HTTPS certificate. Machines that trusted the old router.crt must re-import the new one." : "Created the router's HTTPS certificate.")}
                  CA certificate (copy to other machines and trust): {certificates.PublicPath}
                  Server certificate (for diagnostics):              {certificates.LeafPath}
                  Server private key (encrypted, stays here):        {certificates.KeyPath}
                  Names: {string.Join(", ", names.EnumerateDnsNames().Concat(names.EnumerateIPAddresses().Select(a => a.ToString())))}
                  router.crt SHA-256 fingerprint: {ServerCertificate.Fingerprint(authority)}
                  Expires: {leaf.NotAfter:yyyy-MM-dd}

                router.crt is a CA made for this router alone: it signed only this server certificate and its private key
                was discarded, so it can never sign anything else, and it is limited to the names
                {string.Join(", ", RouterCertificateFactory.PermittedDnsSubtrees(names.EnumerateDnsNames()))} (plus IP addresses).

                Trust it on each machine that connects (compare the fingerprint after copying router.crt):
                  macOS:   sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain router.crt
                           (or Keychain Access: drag router.crt into the System keychain, open it, Trust > Always Trust)
                  Windows: certutil -addstore Root router.crt   (in an administrator prompt; for this user only: certutil -user -addstore Root router.crt)
                  Claude Code / Claude Desktop also read NODE_EXTRA_CA_CERTS=/path/to/router.crt

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

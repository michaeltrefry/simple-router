using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleRouter.Gateway;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

/// <summary>Serving HTTPS to LAN clients with the router's certificate while plain HTTP stays on loopback.</summary>
public class HttpsServingTests
{
    private const string LanClient = "192.168.1.50";

    private static async Task<RouterHost> StartWithCertificateAsync(Action<IServiceCollection>? services = null, params string[] extraArgs)
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        Assert.Equal(0, (await HttpsSupport.CreateCertificateAsync(dataDirectory)).ExitCode);
        return await StartAsync(dataDirectory, services, ["--SimpleRouter:ListenAddress=0.0.0.0", .. extraArgs]);
    }

    private static Task<RouterHost> StartAsync(string dataDirectory, Action<IServiceCollection>? services, string[] args) =>
        RouterHost.StartInAsync(dataDirectory, new FakeHandler(), s =>
        {
            RemoteIp.Register(s);
            HttpsSupport.Lan()(s);
            services?.Invoke(s);
        }, args);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string remote, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(RemoteIp.Header, remote);
        if (key is not null)
        {
            request.Headers.Add(GatewayAuthMiddleware.RouterKeyHeader, key);
        }
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_lan_client_trusting_the_exported_certificate_lists_models_over_https_and_loopback_http_still_serves()
    {
        await using var host = await StartWithCertificateAsync();
        using var trusted = HttpsSupport.PublicCertificate(host.DataDirectory);
        using var https = HttpsSupport.PinnedClient(trusted, HttpsSupport.HttpsPort(host), "testhost.local");
        var key = await host.GatewayKeyAsync();

        using var overHttps = await GetAsync(https, "/v1/models", LanClient, key);
        using var keyless = await GetAsync(https, "/v1/models", LanClient);
        using var overHttp = await GetAsync(host.Client, "/v1/models", "127.0.0.1", key);

        Assert.Equal(HttpStatusCode.OK, overHttps.StatusCode);
        Assert.Contains("\"data\"", await overHttps.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, keyless.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overHttp.StatusCode);
        Assert.Equal(Uri.UriSchemeHttp, host.Client.BaseAddress!.Scheme);
    }

    [Fact]
    public async Task A_self_signed_certificate_from_an_older_cert_create_is_still_served_and_startup_says_to_recreate_it()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        await using (var first = await StartAsync(dataDirectory, null, []))
        {
            first.KeepData = true;
            using var old = HttpsSupport.SelfSignedLeaf("localhost", TimeSpan.FromDays(90));
            var certs = Path.Combine(dataDirectory, "certs");
            Directory.CreateDirectory(certs);
            await File.WriteAllTextAsync(Path.Combine(certs, "router.pfx.protected"),
                first.Services.GetRequiredService<SecretProtector>().Protect(Convert.ToBase64String(old.Export(X509ContentType.Pkcs12))));
            await File.WriteAllTextAsync(Path.Combine(certs, "router.crt"), old.ExportCertificatePem());
        }
        var logs = new CapturingLogs();
        await using var host = await StartAsync(dataDirectory, s => s.AddLogging(b => b.AddProvider(logs)), []);
        using var trusted = HttpsSupport.PublicCertificate(dataDirectory);
        using var https = HttpsSupport.PinnedClient(trusted, HttpsSupport.HttpsPort(host), "localhost");

        using var response = await GetAsync(https, "/v1/models", LanClient, await host.GatewayKeyAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(logs.Warnings, w => w.Contains("self-signed") && w.Contains("cert create") && w.Contains("router.crt"));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("[fd00::20]")]
    [InlineData("testhost")]
    public async Task Every_lan_name_in_the_certificate_passes_the_clients_name_check(string lanHost)
    {
        await using var host = await StartWithCertificateAsync();
        using var trusted = HttpsSupport.PublicCertificate(host.DataDirectory);
        using var https = HttpsSupport.PinnedClient(trusted, HttpsSupport.HttpsPort(host), lanHost);

        using var response = await GetAsync(https, "/v1/models", LanClient, await host.GatewayKeyAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_client_pinned_to_a_different_certificate_or_asking_for_an_unlisted_name_is_refused_the_handshake()
    {
        await using var host = await StartWithCertificateAsync();
        var otherDirectory = RouterHost.NewDataDirectory();
        Assert.Equal(0, (await HttpsSupport.CreateCertificateAsync(otherDirectory)).ExitCode);
        using var other = HttpsSupport.PublicCertificate(otherDirectory);
        using var trusted = HttpsSupport.PublicCertificate(host.DataDirectory);
        using var wrongAnchor = HttpsSupport.PinnedClient(other, HttpsSupport.HttpsPort(host), "testhost.local");
        using var wrongName = HttpsSupport.PinnedClient(trusted, HttpsSupport.HttpsPort(host), "elsewhere.local");

        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(wrongAnchor, "/v1/models", LanClient));
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(wrongName, "/v1/models", LanClient));
        Directory.Delete(otherDirectory, recursive: true);
    }

    [Theory]
    [InlineData("/ui")]
    [InlineData("/ui/settings")]
    [InlineData("/_framework/blazor.web.js")]
    public async Task Over_https_the_dashboard_is_refused_to_a_lan_client_and_served_to_this_machine(string path)
    {
        await using var host = await StartWithCertificateAsync();
        using var trusted = HttpsSupport.PublicCertificate(host.DataDirectory);
        using var https = HttpsSupport.PinnedClient(trusted, HttpsSupport.HttpsPort(host), "localhost");

        using var fromLan = await GetAsync(https, path, LanClient);
        using var fromHere = await GetAsync(https, path, "127.0.0.1");

        Assert.Equal(HttpStatusCode.Forbidden, fromLan.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fromHere.StatusCode);
    }

    [Fact]
    public async Task With_a_certificate_plain_http_listens_only_on_loopback_and_https_on_the_listen_address()
    {
        await using var host = await StartWithCertificateAsync();

        var addresses = HttpsSupport.Addresses(host);

        var http = Assert.Single(addresses, a => a.Scheme == Uri.UriSchemeHttp);
        Assert.Equal("127.0.0.1", http.Host);
        Assert.Equal("0.0.0.0", Assert.Single(addresses, a => a.Scheme == Uri.UriSchemeHttps).Host);
    }

    [Fact]
    public async Task With_a_loopback_listen_address_and_a_certificate_both_listeners_stay_on_loopback()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        Assert.Equal(0, (await HttpsSupport.CreateCertificateAsync(dataDirectory)).ExitCode);
        await using var host = await StartAsync(dataDirectory, null, ["--SimpleRouter:ListenAddress=127.0.0.1"]);

        Assert.All(HttpsSupport.Addresses(host), a => Assert.Equal("127.0.0.1", a.Host));
        Assert.Equal(2, HttpsSupport.Addresses(host).Count);
    }

    [Fact]
    public async Task A_configured_certificate_is_served_instead_of_the_generated_one()
    {
        var (path, configured) = HttpsSupport.UserCertificate(Path.Combine(Path.GetTempPath(), "simple-router-tests"), "user-pass", TimeSpan.FromDays(90));
        await using var host = await StartWithCertificateAsync(null,
            $"--SimpleRouter:Https:CertificatePath={path}", "--SimpleRouter:Https:Password=user-pass");
        X509Certificate2? served = null;
        using var https = HttpsSupport.PinnedClient(configured, HttpsSupport.HttpsPort(host), "localhost", c => served = c);

        using var response = await GetAsync(https, "/v1/models", LanClient, await host.GatewayKeyAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(configured.Thumbprint, served!.Thumbprint);
        served.Dispose();
        configured.Dispose();
        File.Delete(path);
    }

    [Fact]
    public async Task A_configured_certificate_alone_turns_on_https_without_a_generated_one()
    {
        var (path, configured) = HttpsSupport.UserCertificate(Path.Combine(Path.GetTempPath(), "simple-router-tests"), "user-pass", TimeSpan.FromDays(90));
        await using var host = await StartAsync(RouterHost.NewDataDirectory(), null,
            ["--SimpleRouter:ListenAddress=0.0.0.0", $"--SimpleRouter:Https:CertificatePath={path}", "--SimpleRouter:Https:Password=user-pass"]);
        using var https = HttpsSupport.PinnedClient(configured, HttpsSupport.HttpsPort(host), "localhost");

        using var response = await GetAsync(https, "/v1/models", LanClient, await host.GatewayKeyAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("127.0.0.1", Assert.Single(HttpsSupport.Addresses(host), a => a.Scheme == Uri.UriSchemeHttp).Host);
        configured.Dispose();
        File.Delete(path);
    }

    [Fact]
    public async Task A_configured_certificate_that_cannot_be_opened_stops_startup_naming_the_setting()
    {
        var (path, configured) = HttpsSupport.UserCertificate(Path.Combine(Path.GetTempPath(), "simple-router-tests"), "user-pass", TimeSpan.FromDays(90));
        configured.Dispose();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(RouterHost.NewDataDirectory(), null,
            [$"--SimpleRouter:Https:CertificatePath={path}", "--SimpleRouter:Https:Password=wrong"]));

        Assert.Contains("SimpleRouter:Https:CertificatePath", error.Message);
        Assert.DoesNotContain("wrong", error.Message);
        File.Delete(path);
    }

    [Fact]
    public async Task Startup_warns_when_the_certificate_expires_soon_or_misses_a_current_lan_name()
    {
        var (path, configured) = HttpsSupport.UserCertificate(Path.Combine(Path.GetTempPath(), "simple-router-tests"), "user-pass", TimeSpan.FromDays(10));
        configured.Dispose();
        var logs = new CapturingLogs();
        await using var host = await StartAsync(RouterHost.NewDataDirectory(), s => s.AddLogging(b => b.AddProvider(logs)),
            ["--SimpleRouter:ListenAddress=0.0.0.0", $"--SimpleRouter:Https:CertificatePath={path}", "--SimpleRouter:Https:Password=user-pass"]);

        Assert.Contains(logs.Warnings, w => w.Contains("expires"));
        Assert.Contains(logs.Warnings, w => w.Contains("testhost.local") && w.Contains("192.168.1.20") && w.Contains("cert create"));
        File.Delete(path);
    }

    [Fact]
    public async Task A_freshly_created_certificate_logs_no_warning_and_the_sidebar_shows_its_expiry_and_lan_url()
    {
        var logs = new CapturingLogs();
        await using var host = await StartWithCertificateAsync(s => s.AddLogging(b => b.AddProvider(logs)));
        using var trusted = HttpsSupport.PublicCertificate(host.DataDirectory);

        using var page = await GetAsync(host.Client, "/ui", "127.0.0.1");
        var html = await page.Content.ReadAsStringAsync();

        Assert.DoesNotContain(logs.Warnings, w => w.Contains("certificate"));
        Assert.Contains($"https://testhost.local:{HttpsSupport.HttpsPort(host)}", html);
        Assert.Contains(trusted.NotAfter.ToString("yyyy-MM-dd"), html);
    }

    [Fact]
    public async Task The_bonjour_name_not_a_dhcp_host_name_is_the_lan_url_and_its_absence_from_the_certificate_is_warned()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        Assert.Equal(0, (await HttpsSupport.CreateCertificateAsync(dataDirectory)).ExitCode);
        var logs = new CapturingLogs();
        await using var host = await StartAsync(dataDirectory, s =>
        {
            HttpsSupport.Lan("dhcp-name.lan", "Studio")(s);
            s.AddLogging(b => b.AddProvider(logs));
        }, ["--SimpleRouter:ListenAddress=0.0.0.0"]);

        using var page = await GetAsync(host.Client, "/ui", "127.0.0.1");

        Assert.Contains($"https://studio.local:{HttpsSupport.HttpsPort(host)}", await page.Content.ReadAsStringAsync());
        Assert.Contains(logs.Warnings, w => w.Contains("studio.local"));
    }

    private sealed class CapturingLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
        public void Dispose() { }
    }
}

using System.Net;
using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Claude;
using SimpleRouter.Codex;
using SimpleRouter.Data;
using SimpleRouter.Gateway;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.SettingsHarness;

namespace SimpleRouter.Tests;

/// <summary>The settings page's Connect Claude / Connect Codex flows against fake issuers, and gateway key regeneration.</summary>
public class SettingsAccountsTests
{
    [Fact]
    public async Task Connect_Claude_exchanges_the_pasted_code_stores_the_login_and_fetches_the_model_list()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.OAuth.Respond = _ => Json("""{"access_token":"ui-access-secret","refresh_token":"ui-refresh-secret","expires_in":28800}""");
        host.Upstream.Respond = request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? Json("""{"data":[{"id":"upstream-a"},{"id":"upstream-b"}],"has_more":false}""")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        using var context = Context(host);
        var cut = Render(context);

        await cut.ClickAsync("#claude-connect .connect");
        var authorize = new Uri(cut.Find("#claude-connect .authorize").GetAttribute("href")!);
        Assert.Equal(RouterHost.AuthorizeUrl, authorize.GetLeftPart(UriPartial.Path));
        var state = QueryHelpers.ParseQuery(authorize.Query)["state"].ToString();
        await cut.ChangeAsync("#claude-connect .code", "wrong#state");
        await cut.ClickAsync("#claude-connect .submit");
        Assert.Contains("state did not match", cut.Find("#claude-connect .form-error").TextContent);
        Assert.Empty(host.OAuth.Requests);

        await cut.ChangeAsync("#claude-connect .code", $"ui-code#{state}");
        await cut.ClickAsync("#claude-connect .submit");

        Assert.Equal("Claude subscription connected. Found 2 Claude models.", cut.Find("#claude-connect .form-notice").TextContent);
        Assert.Contains("\"ui-code\"", System.Text.Encoding.UTF8.GetString(Assert.Single(host.OAuth.Requests).Body));
        var stored = await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync();
        Assert.Equal("ui-refresh-secret", stored!.RefreshToken);
        Assert.Equal("Bearer ui-access-secret", host.Upstream.Requests[^1].Headers["authorization"]);
        Assert.DoesNotContain("ui-access-secret", cut.Markup);
        Assert.DoesNotContain("ui-refresh-secret", cut.Markup);
        Assert.Equal(["upstream-a", "upstream-b"], cut.FindAll("#discovered-claude option").Select(o => o.GetAttribute("value")));
    }

    [Fact]
    public async Task A_Claude_login_that_cannot_be_stored_shows_an_error_and_keeps_the_page_working()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.OAuth.Respond = _ => Json("""{"access_token":"ui-access-secret","refresh_token":"ui-refresh-secret","expires_in":28800}""");
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER refuse_claude_login BEFORE UPDATE ON ClaudeCredentials BEGIN SELECT RAISE(ABORT, 'refused'); END;");
        }
        using var context = Context(host);
        var cut = Render(context);

        await cut.ClickAsync("#claude-connect .connect");
        var state = QueryHelpers.ParseQuery(new Uri(cut.Find("#claude-connect .authorize").GetAttribute("href")!).Query)["state"].ToString();
        await cut.ChangeAsync("#claude-connect .code", $"ui-code#{state}");
        await cut.ClickAsync("#claude-connect .submit");

        Assert.Equal("Claude login failed; see the server log.", cut.Find("#claude-connect .form-error").TextContent);
        Assert.Equal("claude-refresh-secret", (await host.Services.GetRequiredService<ClaudeCredentialStore>().GetAsync())!.RefreshToken);
        await cut.ClickAsync("#claude-connect .cancel");
        await cut.ClickAsync("#claude-connect .connect");
        Assert.NotNull(cut.Find("#claude-connect .authorize"));
    }

    [Fact]
    public async Task Refresh_model_lists_fetches_the_logged_in_subscriptions_lists_and_shows_them()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.Upstream.Respond = request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? Json("""{"data":[{"id":"upstream-a"},{"id":"upstream-new"}],"has_more":false}""")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        using var context = Context(host);
        var cut = Render(context);
        Assert.Empty(cut.FindAll("#discovered-claude option"));

        await cut.ClickAsync("#model-lists .refresh");

        cut.WaitForAssertion(() => Assert.Equal("Found 2 Claude models.", cut.Find("#model-lists .form-notice").TextContent), Wait);
        Assert.Equal("Bearer claude-access-secret", Assert.Single(host.Upstream.Requests).Headers["authorization"]);
        Assert.Empty(host.Codex.Requests);
        Assert.Equal(["upstream-a", "upstream-new"], cut.FindAll("#discovered-claude option").Select(o => o.GetAttribute("value")));
    }

    [Fact]
    public async Task Refresh_model_lists_without_a_login_asks_for_one()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            await db.ClaudeCredentials.ExecuteDeleteAsync();
        }
        using var context = Context(host);
        var cut = Render(context);

        await cut.ClickAsync("#model-lists .refresh");

        cut.WaitForAssertion(() => Assert.Contains("No subscription is logged in", cut.Find("#model-lists .form-error").TextContent), Wait);
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task Connect_Codex_shows_the_device_code_polls_until_approved_and_stores_the_login()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var polls = 0;
        host.CodexAuth.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/accounts/deviceauth/usercode" => Json("""{"device_auth_id":"dev-ui","user_code":"WXYZ-9876","interval":"1"}"""),
            "/api/accounts/deviceauth/token" when Interlocked.Increment(ref polls) == 1 => Json("""{"error":"authorization_pending"}""", HttpStatusCode.Forbidden),
            "/api/accounts/deviceauth/token" => Json("""{"authorization_code":"auth-ui","code_verifier":"verifier-ui"}"""),
            "/oauth/token" => Json($$"""{"id_token":"{{CodexLoginTests.IdToken("acct-ui")}}","access_token":"codex-ui-access","refresh_token":"codex-ui-refresh","expires_in":3600}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        host.Codex.Respond = _ => Json("""{"models":[{"slug":"upstream-c"}]}""");
        using var context = Context(host);
        var cut = Render(context);

        await cut.ClickAsync("#codex-connect .connect");

        Assert.Equal("WXYZ-9876", cut.Find("#codex-connect .user-code").TextContent);
        Assert.Equal($"{RouterHost.CodexIssuer}/codex/device", cut.Find("#codex-connect .verification").GetAttribute("href"));
        cut.WaitForAssertion(() => Assert.Equal("Codex subscription connected. Found 1 Codex models.",
            cut.Find("#codex-connect .form-notice").TextContent), Wait);
        var stored = await host.Services.GetRequiredService<CodexCredentialStore>().GetAsync();
        Assert.Equal(("codex-ui-refresh", "acct-ui"), (stored!.RefreshToken, stored.AccountId));
        Assert.Equal(2, polls);
        Assert.DoesNotContain("codex-ui-access", cut.Markup);
        Assert.Empty(cut.FindAll("#codex-connect .user-code"));
    }

    [Fact]
    public async Task A_denied_Codex_device_code_shows_the_failure_and_stores_nothing()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        host.CodexAuth.Respond = request => request.RequestUri!.AbsolutePath == "/api/accounts/deviceauth/usercode"
            ? Json("""{"device_auth_id":"dev-ui","user_code":"WXYZ-9876","interval":"1"}""")
            : Json("""{"error":"expired_token"}""", HttpStatusCode.BadRequest);
        using var context = Context(host);
        var cut = Render(context);

        await cut.ClickAsync("#codex-connect .connect");

        cut.WaitForAssertion(() => Assert.Equal("Codex login failed: Codex device authorization was denied or has expired.",
            cut.Find("#codex-connect .form-error").TextContent), Wait);
        Assert.Null(await host.Services.GetRequiredService<CodexCredentialStore>().GetAsync());
    }

    [Fact]
    public async Task Regenerating_the_gateway_key_rejects_the_old_key_at_once_and_never_renders_either_key()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var oldKey = await host.GatewayKeyAsync();
        using var context = Context(host);
        var cut = Render(context);
        using (var before = await host.Client.GetAsync("/v1/models"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await cut.ClickAsync("#gateway-key-settings .regenerate");
        await cut.ClickAsync("#gateway-key-settings .confirm-regenerate");

        Assert.Contains("old key no longer works", cut.Find("#gateway-key-settings .form-notice").TextContent);
        using (var stale = await host.Client.GetAsync("/v1/models"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }
        var newKey = await host.GatewayKeyAsync();
        Assert.NotEqual(oldKey, newKey);
        using var fresh = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        fresh.Headers.Add("x-api-key", newKey);
        using (var accepted = await new HttpClient { BaseAddress = host.Client.BaseAddress }.SendAsync(fresh))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        Assert.DoesNotContain(oldKey, cut.Markup);
        Assert.DoesNotContain(newKey, cut.Markup);

        await cut.ClickAsync("#gateway-key-settings .copy-key");
        Assert.Equal(newKey, context.JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments.Single());

        // A restarted store reads the regenerated key, not the first one.
        var restarted = new GatewayKeyStore(host.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<SimpleRouter.Data.RouterDbContext>>(),
            host.Services.GetRequiredService<SecretProtector>());
        Assert.Equal(newKey, await restarted.GetAsync());
    }
}

using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Cli;
using SimpleRouter.Codex;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class CodexLoginTests
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    public static string Jwt(JsonObject claims) =>
        "eyJhbGciOiJub25lIn0." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString())) + ".sig";

    public static string IdToken(string accountId) =>
        Jwt(new JsonObject { ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId } });

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A fake issuer whose device code is approved after <paramref name="pendingPolls"/> polls, or answers polls with <paramref name="pollFailure"/>.</summary>
    private static FakeHandler Issuer(int pendingPolls = 1, HttpStatusCode? pollFailure = null)
    {
        var polls = 0;
        return new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath switch
            {
                "/api/accounts/deviceauth/usercode" => Json("""{"device_auth_id":"dev-1","user_code":"ABCD-1234","interval":"1"}"""),
                "/api/accounts/deviceauth/token" when pollFailure is { } failure => Json("""{"error":"expired_token"}""", failure),
                "/api/accounts/deviceauth/token" when polls++ < pendingPolls => Json("""{"error":"authorization_pending"}""", HttpStatusCode.Forbidden),
                "/api/accounts/deviceauth/token" => Json("""{"authorization_code":"auth-code","code_challenge":"c","code_verifier":"verifier-1"}"""),
                "/oauth/token" => Json($$"""{"id_token":"{{IdToken("acct-login")}}","access_token":"codex-access-login","refresh_token":"codex-refresh-login","expires_in":3600}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
        };
    }

    private static Task<int> RunLoginAsync(string dataDirectory, FakeHandler issuer, StringWriter output, params string[] extraArgs) =>
        CommandLine.RunAsync(["login", "codex", .. RouterHost.Args(dataDirectory), .. extraArgs], TextReader.Null, output,
            RouterHost.FakeHttp(new FakeHandler(), new FakeHandler(), codexAuth: issuer));

    [Fact]
    public async Task Device_code_flow_polls_until_approved_and_stores_encrypted_tokens_and_account()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var issuer = Issuer(pendingPolls: 1);
        var output = new StringWriter();

        var exit = await RunLoginAsync(dataDirectory, issuer, output);

        Assert.Equal(0, exit);
        Assert.Contains($"{RouterHost.CodexIssuer}/codex/device", output.ToString());
        Assert.Contains("ABCD-1234", output.ToString());
        var paths = issuer.Requests.Select(r => r.Uri.AbsolutePath).ToList();
        Assert.Equal(["/api/accounts/deviceauth/usercode", "/api/accounts/deviceauth/token", "/api/accounts/deviceauth/token", "/oauth/token"], paths);
        Assert.Equal(ClientId, JsonNode.Parse(issuer.Requests[0].Body)!["client_id"]!.GetValue<string>());
        var poll = JsonNode.Parse(issuer.Requests[1].Body)!;
        Assert.Equal("dev-1", poll["device_auth_id"]!.GetValue<string>());
        Assert.Equal("ABCD-1234", poll["user_code"]!.GetValue<string>());
        var exchange = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(issuer.Requests[3].Body));
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("auth-code", exchange["code"]);
        Assert.Equal("verifier-1", exchange["code_verifier"]);
        Assert.Equal($"{RouterHost.CodexIssuer}/deviceauth/callback", exchange["redirect_uri"]);
        Assert.Equal(ClientId, exchange["client_id"]);

        var app = RouterApp.Build(RouterHost.Args(dataDirectory));
        var stored = await app.Services.GetRequiredService<CodexCredentialStore>().GetAsync();
        await app.DisposeAsync();
        Assert.Equal("codex-refresh-login", stored!.RefreshToken);
        Assert.Equal("codex-access-login", stored.AccessToken);
        Assert.Equal("acct-login", stored.AccountId);
        var row = ReadCredentialRow(dataDirectory);
        Assert.DoesNotContain("codex-refresh-login", row);
        Assert.DoesNotContain("codex-access-login", row);
        Assert.DoesNotContain("acct-login", row);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task A_denied_or_expired_code_fails_the_cli_with_exit_code_1_and_stores_nothing(HttpStatusCode failure)
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var issuer = Issuer(pollFailure: failure);
        var output = new StringWriter();

        var exit = await RunLoginAsync(dataDirectory, issuer, output);

        Assert.Equal(1, exit);
        Assert.Contains("Codex login failed: Codex device authorization was denied or has expired.", output.ToString());
        Assert.DoesNotContain(issuer.Requests, r => r.Uri.AbsolutePath == "/oauth/token");
        Assert.Null(await StoredAsync(dataDirectory));
    }

    [Fact]
    public async Task A_code_never_entered_times_out_with_exit_code_1()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var output = new StringWriter();

        var exit = await RunLoginAsync(dataDirectory, Issuer(pendingPolls: int.MaxValue), output, "--SimpleRouter:Codex:DeviceCodeTimeout=00:00:01.5");

        Assert.Equal(1, exit);
        Assert.Contains("Codex login failed: the device code was not entered in time.", output.ToString());
        Assert.Null(await StoredAsync(dataDirectory));
    }

    [Fact]
    public async Task An_unreachable_issuer_fails_the_cli_cleanly()
    {
        var output = new StringWriter();

        var exit = await RunLoginAsync(RouterHost.NewDataDirectory(), new FakeHandler { Respond = _ => throw new HttpRequestException("refused") }, output);

        Assert.Equal(1, exit);
        Assert.Contains("Codex login failed: Codex auth endpoint is unreachable.", output.ToString());
    }

    [Fact]
    public async Task A_transient_network_failure_while_polling_keeps_waiting_for_approval()
    {
        var dataDirectory = RouterHost.NewDataDirectory();
        var output = new StringWriter();
        var issuer = Issuer(pendingPolls: 0);
        var approve = issuer.Respond;
        var polls = 0;
        issuer.Respond = request => request.RequestUri!.AbsolutePath == "/api/accounts/deviceauth/token" && polls++ == 0
            ? throw new HttpRequestException("connection reset")
            : approve(request);

        var exit = await RunLoginAsync(dataDirectory, issuer, output);

        Assert.Equal(0, exit);
        Assert.Equal(2, polls);
        Assert.Equal("codex-refresh-login", (await StoredAsync(dataDirectory))!.RefreshToken);
    }

    [Fact]
    public void Account_id_is_read_from_the_auth_claim_or_the_top_level_claim()
    {
        Assert.Equal("acct-nested", CodexOAuthClient.AccountId(IdToken("acct-nested")));
        Assert.Equal("acct-top", CodexOAuthClient.AccountId(Jwt(new JsonObject { ["chatgpt_account_id"] = "acct-top" })));
        Assert.Null(CodexOAuthClient.AccountId("not-a-jwt"));
    }

    private static async Task<CodexTokens?> StoredAsync(string dataDirectory)
    {
        var app = RouterApp.Build(RouterHost.Args(dataDirectory));
        var stored = await app.Services.GetRequiredService<CodexCredentialStore>().GetAsync();
        await app.DisposeAsync();
        return stored;
    }

    private static string ReadCredentialRow(string dataDirectory)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "simple-router.db")}");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedRefreshToken || '|' || ProtectedAccessToken || '|' || ProtectedAccountId FROM CodexCredentials";
        var row = (string)command.ExecuteScalar()!;
        SqliteConnection.ClearPool(connection);
        return row;
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleRouter.Codex;
using SimpleRouter.Data;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

/// <summary>Codex models served through the seeded tiers, against a fake Codex backend and issuer.</summary>
public class CodexRoutingTests
{
    private static HttpRequestMessage Message(string sessionId, string modelClass = "high", bool stream = true, string path = "/v1/messages")
    {
        var body = new JsonObject
        {
            ["model"] = "claude-opus-5-5",
            ["max_tokens"] = 1024,
            ["system"] = "You are terse.",
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "List the files in src." } },
            ["tools"] = new JsonArray { new JsonObject { ["name"] = "list_files", ["input_schema"] = new JsonObject { ["type"] = "object" } } },
        };
        if (stream)
        {
            body["stream"] = true;
        }
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionKey.HeaderName, sessionId);
        request.Headers.Add(TierResolver.HeaderName, modelClass);
        return request;
    }

    /// <summary>Seeded catalog, both subscriptions logged in; the tier's first new session (round-robin turn 0) goes to Claude.</summary>
    private static async Task<RouterHost> StartAsync(Func<HttpRequestMessage, HttpResponseMessage>? codex = null)
    {
        var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        await host.LoginCodexAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        host.Codex.Respond = codex ?? (_ => Stream(ReasoningTextAndToolCall));
        await host.AuthorizedClientAsync();
        return host;
    }

    /// <summary>Spends the tier's Claude turn on another session, so the next new session lands on the Codex member.</summary>
    private static async Task PinNextSessionToCodexAsync(RouterHost host, string modelClass)
    {
        using var claude = await host.Client.SendAsync(Message("claude-session", modelClass, stream: false));
        Assert.Equal(HttpStatusCode.OK, claude.StatusCode);
        Assert.Single(host.Upstream.Requests);
    }

    [Fact]
    public async Task A_high_session_pinned_to_gpt_6_1_sol_streams_reasoning_text_and_a_tool_call()
    {
        await using var host = await StartAsync();
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("high", response.Headers.GetValues(MessageRouter.ModelClassHeader).Single());
        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("gpt-6.1-sol", message.Model);
        Assert.Equal(["thinking", "text", "tool_use"], message.Blocks.Select(b => b.Type));
        Assert.Equal("Let me look.", message.Blocks[1].Text);
        Assert.Equal("call_abc123", message.Blocks[2].Id);
        Assert.Equal("src", message.Blocks[2].Input!["path"]!.GetValue<string>());
        Assert.Equal("tool_use", message.StopReason);

        var sent = Assert.Single(host.Codex.Requests);
        Assert.Equal($"{RouterHost.CodexApiBase}/responses", sent.Uri.ToString());
        Assert.Equal("Bearer codex-access-current", sent.Headers["authorization"]);
        Assert.Equal("acct-test", sent.Headers["chatgpt-account-id"]);
        Assert.Equal("codex_cli_rs", sent.Headers["originator"]);
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("gpt-6.1-sol", body["model"]!.GetValue<string>());
        Assert.Equal("You are terse.", body["instructions"]!.GetValue<string>());
        Assert.Equal("list_files", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(body["prompt_cache_key"]!.GetValue<string>(), sent.Headers["session_id"]);

        await using var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync();
        Assert.Equal("gpt-6.1-sol", (await db.SessionPins.SingleAsync(p => p.ClientSessionId == "codex-session")).ModelId);
    }

    [Fact]
    public async Task A_max_session_can_be_pinned_to_gpt_6_astra_and_stays_there()
    {
        await using var host = await StartAsync();
        await PinNextSessionToCodexAsync(host, "max");

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var response = await host.Client.SendAsync(Message("astra-session", "max"));
            Assert.Equal("gpt-6-astra", AnthropicSse.Parse(await response.Content.ReadAsStringAsync()).Model);
        }

        Assert.All(host.Codex.Requests, r => Assert.Equal("gpt-6-astra", JsonNode.Parse(r.Body)!["model"]!.GetValue<string>()));
        Assert.Equal(2, host.Codex.Requests.Count);
        Assert.Single(host.Upstream.Requests);
    }

    [Fact]
    public async Task A_non_streaming_client_gets_the_stream_folded_into_one_message()
    {
        await using var host = await StartAsync();
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session", stream: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("message", message["type"]!.GetValue<string>());
        Assert.Equal("gpt-6.1-sol", message["model"]!.GetValue<string>());
        Assert.Equal(["thinking", "text", "tool_use"], message["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("call_abc123", message["content"]![2]!["id"]!.GetValue<string>());
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(Assert.Single(host.Codex.Requests).Body)!["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_non_streaming_client_gets_a_mid_stream_failure_as_an_error_response()
    {
        await using var host = await StartAsync(_ => Stream(Sse(Created,
            """{"type":"response.failed","response":{"error":{"code":"usage_limit_reached","message":"Usage limit reached."}}}""")));
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session", stream: false));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("rate_limit_error", error["error"]!["type"]!.GetValue<string>());
        Assert.Equal("Usage limit reached.", error["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_upstream_429_keeps_its_status_and_retry_after_with_an_anthropic_body()
    {
        await using var host = await StartAsync(_ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"type":"usage_limit_reached","message":"The usage limit has been reached."}}""", Encoding.UTF8, "application/json"),
            };
            limited.Headers.TryAddWithoutValidation("Retry-After", "120");
            return limited;
        });
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("120", response.Headers.RetryAfter!.ToString());
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("error", error["type"]!.GetValue<string>());
        Assert.Equal("rate_limit_error", error["error"]!["type"]!.GetValue<string>());
        Assert.Equal("The usage limit has been reached.", error["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_credential_rejected_even_after_a_forced_refresh_is_a_router_side_502_not_a_client_401()
    {
        await using var host = await StartAsync(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        host.CodexAuth.Respond = _ => Refreshed();
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("login codex", await response.Content.ReadAsStringAsync());
        Assert.Equal(["Bearer codex-access-current", "Bearer codex-access-new"], host.Codex.Requests.Select(r => r.Headers["authorization"]));
        Assert.Single(host.CodexAuth.Requests);
    }

    [Fact]
    public async Task A_401_on_an_unexpired_token_is_retried_once_after_a_forced_refresh()
    {
        await using var host = await StartAsync(request => request.Headers.Authorization!.Parameter == "codex-access-new"
            ? Stream(ReasoningTextAndToolCall)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        host.CodexAuth.Respond = _ => Refreshed();
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tool_use", AnthropicSse.Parse(await response.Content.ReadAsStringAsync()).StopReason);
        Assert.Equal(2, host.Codex.Requests.Count);
        Assert.Equal("codex-access-new", (await host.Services.GetRequiredService<CodexCredentialStore>().GetAsync())!.AccessToken);
    }

    [Fact]
    public async Task A_403_for_a_plan_without_codex_reports_the_plan_error_not_a_login_problem()
    {
        await using var host = await StartAsync(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"usage_not_included","message":"Your plan does not include Codex."}}""", Encoding.UTF8, "application/json"),
        });
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal("rate_limit_error", error["type"]!.GetValue<string>());
        Assert.Equal("Your plan does not include Codex.", error["message"]!.GetValue<string>());
        Assert.Empty(host.CodexAuth.Requests);
    }

    [Fact]
    public async Task A_403_without_a_known_code_asks_for_a_codex_login()
    {
        await using var host = await StartAsync(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"detail":"Forbidden"}""") });
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("run 'simple-router login codex'", await response.Content.ReadAsStringAsync());
        Assert.Single(host.Codex.Requests);
    }

    [Fact]
    public async Task A_stream_cut_off_before_completion_ends_with_an_error_event()
    {
        await using var host = await StartAsync(_ => Stream(Sse(Created, TextDelta(0, "Half"))));
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        var message = AnthropicSse.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Codex stream ended before completion.", message.Error);
        Assert.DoesNotContain("message_stop", message.Events);
    }

    [Fact]
    public async Task Count_tokens_for_a_codex_model_is_estimated_without_an_upstream_call()
    {
        await using var host = await StartAsync();
        await PinNextSessionToCodexAsync(host, "high");
        (await host.Client.SendAsync(Message("codex-session"))).Dispose();

        using var response = await host.Client.SendAsync(Message("codex-session", path: "/v1/messages/count_tokens"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["input_tokens"]!.GetValue<int>() > 0);
        Assert.Single(host.Codex.Requests);
    }

    [Fact]
    public async Task Without_a_codex_login_new_sessions_all_go_to_claude()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        host.Codex.Respond = _ => Stream(ReasoningTextAndToolCall);
        await host.AuthorizedClientAsync();

        foreach (var session in new[] { "s1", "s2", "s3" })
        {
            using var response = await host.Client.SendAsync(Message(session, stream: false));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, host.Upstream.Requests.Count);
        Assert.Empty(host.Codex.Requests);
    }

    [Fact]
    public async Task Without_a_claude_login_new_sessions_all_go_to_codex()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginCodexAsync();
        host.Codex.Respond = _ => Stream(ReasoningTextAndToolCall);
        await host.AuthorizedClientAsync();

        foreach (var session in new[] { "s1", "s2", "s3" })
        {
            using var response = await host.Client.SendAsync(Message(session, stream: false));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, host.Codex.Requests.Count);
        Assert.Empty(host.Upstream.Requests);
    }

    [Fact]
    public async Task A_codex_pinned_session_moves_to_claude_after_the_codex_login_is_removed()
    {
        await using var host = await StartAsync();
        await PinNextSessionToCodexAsync(host, "high");
        (await host.Client.SendAsync(Message("codex-session"))).Dispose();
        Assert.Single(host.Codex.Requests);

        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<RouterDbContext>>().CreateDbContextAsync())
        {
            db.CodexCredentials.RemoveRange(db.CodexCredentials);
            await db.SaveChangesAsync();
        }
        using var response = await host.Client.SendAsync(Message("codex-session", stream: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.Codex.Requests);
        Assert.Equal(2, host.Upstream.Requests.Count);
        Assert.Equal("claude-opus-5-5", JsonNode.Parse(host.Upstream.Requests[^1].Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_and_the_new_token_and_rotation_are_used_and_persisted()
    {
        await using var host = await StartAsync();
        await host.LoginCodexAsync(accessToken: "codex-access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "codex-refresh-old");
        host.CodexAuth.Respond = _ => Refreshed();
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refresh = Assert.Single(host.CodexAuth.Requests);
        Assert.Equal($"{RouterHost.CodexIssuer}/oauth/token", refresh.Uri.ToString());
        var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(refresh.Body));
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("codex-refresh-old", form["refresh_token"]);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", form["client_id"]);
        Assert.Equal("Bearer codex-access-new", Assert.Single(host.Codex.Requests).Headers["authorization"]);
        var stored = await host.Services.GetRequiredService<CodexCredentialStore>().GetAsync();
        Assert.Equal("codex-refresh-new", stored!.RefreshToken);
        Assert.Equal("codex-access-new", stored.AccessToken);
        Assert.Equal("acct-test", stored.AccountId);
    }

    [Fact]
    public async Task Concurrent_requests_with_an_expired_token_share_one_refresh()
    {
        await using var host = await StartAsync();
        await host.LoginCodexAsync(accessToken: "codex-access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "codex-refresh-old");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.CodexAuth.RespondAsync = async _ =>
        {
            await release.Task;
            return Refreshed();
        };
        await PinNextSessionToCodexAsync(host, "high");

        var sends = Enumerable.Range(0, 5).Select(_ => host.Client.SendAsync(Message("codex-session"))).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.CodexAuth.Requests.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "No refresh was attempted.");
            await Task.Delay(20);
        }
        // Give the other requests time to reach the token provider while the first refresh is held open.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        release.SetResult();
        var responses = await Task.WhenAll(sends);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(host.CodexAuth.Requests);
        Assert.Equal(5, host.Codex.Requests.Count);
        Assert.All(host.Codex.Requests, r => Assert.Equal("Bearer codex-access-new", r.Headers["authorization"]));
    }

    [Fact]
    public async Task A_rejected_refresh_is_an_anthropic_502_and_keeps_the_stored_refresh_token()
    {
        await using var host = await StartAsync();
        await host.LoginCodexAsync(accessToken: "codex-access-old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "codex-refresh-old");
        host.CodexAuth.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant","secret":"leak-me"}""") };
        await PinNextSessionToCodexAsync(host, "high");

        using var response = await host.Client.SendAsync(Message("codex-session"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("leak-me", body);
        Assert.Contains("Codex subscription credentials expired or were revoked; run 'simple-router login codex'.", body);
        Assert.Empty(host.Codex.Requests);
        Assert.Equal("codex-refresh-old", (await host.Services.GetRequiredService<CodexCredentialStore>().GetAsync())!.RefreshToken);
    }

    private static HttpResponseMessage Refreshed() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"access_token":"codex-access-new","refresh_token":"codex-refresh-new","expires_in":3600}""", Encoding.UTF8, "application/json"),
    };
}

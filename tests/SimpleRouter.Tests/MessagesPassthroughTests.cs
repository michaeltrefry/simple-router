using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleRouter.Claude;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class MessagesPassthroughTests
{
    private const string RequestBody = """{"model":"claude-sonnet-4-5","max_tokens":64,"system":"Be brief.","messages":[{"role":"user","content":"hi"}]}""";

    private static HttpRequestMessage MessagesRequest(string path = "/v1/messages", string body = RequestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "interleaved-thinking-2025-05-14");
        return request;
    }

    [Fact]
    public async Task Non_streaming_response_is_returned_byte_for_byte()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        var upstreamBody = Encoding.UTF8.GetBytes("""{"id":"msg_1","type":"message","content":[{"type":"text","text":"héllo"}]}""");
        host.Upstream.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(upstreamBody) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response.Headers.Add("request-id", "req_upstream_1");
            return response;
        };
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(upstreamBody, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("req_upstream_1", response.Headers.GetValues("request-id").Single());
    }

    [Fact]
    public async Task Unsigned_thinking_from_another_provider_is_dropped_but_signed_thinking_is_kept()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();
        const string body = """
            {"model":"claude-sonnet-4-5","max_tokens":64,"messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"from codex","signature":""},{"type":"text","text":"a"}]},
              {"role":"user","content":"again"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"from claude","signature":"sig-1"},{"type":"thinking","thinking":"bare"},{"type":"text","text":"b"}]},
              {"role":"user","content":"more"}]}
            """;

        using var response = await client.SendAsync(MessagesRequest(body: body));

        using var sent = JsonDocument.Parse(Assert.Single(host.Upstream.Requests).Body);
        var messages = sent.RootElement.GetProperty("messages");
        Assert.Equal(["text"], messages[1].GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal(["thinking", "text"], messages[3].GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("sig-1", messages[3].GetProperty("content")[0].GetProperty("signature").GetString());
    }

    private async Task<JsonNode> SendThroughClaudeAsync(string body)
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();
        using var response = await client.SendAsync(MessagesRequest(body: body));
        return JsonNode.Parse(Assert.Single(host.Upstream.Requests).Body)!;
    }

    [Fact]
    public async Task Codex_encrypted_reasoning_is_dropped_and_a_reasoning_only_turn_keeps_a_placeholder()
    {
        var sent = await SendThroughClaudeAsync("""
            {"model":"claude-sonnet-4-5","max_tokens":64,"messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"from codex","signature":"codex:ENC"}]},
              {"role":"user","content":"again"}]}
            """);

        Assert.Equal("""[{"type":"text","text":"[reasoning omitted]"}]""", sent["messages"]![1]!["content"]!.ToJsonString());
    }

    [Fact]
    public async Task Thinking_is_turned_off_when_the_last_tool_use_turn_lost_its_foreign_thinking()
    {
        var sent = await SendThroughClaudeAsync("""
            {"model":"claude-sonnet-4-5","max_tokens":2048,"thinking":{"type":"enabled","budget_tokens":1024},"messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"codex:ENC"},{"type":"tool_use","id":"call_1","name":"ls","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"a"}]}]}
            """);

        Assert.Null(sent["thinking"]);
        Assert.Equal("tool_use", sent["messages"]![1]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Thinking_stays_on_when_the_last_tool_use_turn_keeps_its_claude_thinking()
    {
        var sent = await SendThroughClaudeAsync("""
            {"model":"claude-sonnet-4-5","max_tokens":2048,"thinking":{"type":"enabled","budget_tokens":1024},"messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"sig-1"},{"type":"tool_use","id":"call_1","name":"ls","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"a"}]}]}
            """);

        Assert.Equal("enabled", sent["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Upstream_request_uses_subscription_auth_and_claude_code_identity()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-current");
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var client = await host.AuthorizedClientAsync();
        var gatewayKey = await host.GatewayKeyAsync();

        using var response = await client.SendAsync(MessagesRequest());

        var sent = Assert.Single(host.Upstream.Requests);
        Assert.Equal($"{RouterHost.UpstreamBase}/v1/messages", sent.Uri.ToString());
        Assert.Equal("Bearer access-current", sent.Headers["authorization"]);
        Assert.False(sent.Headers.ContainsKey("x-api-key"));
        Assert.False(sent.Headers.ContainsKey("x-weave-router-key"));
        Assert.DoesNotContain(gatewayKey, string.Join(" ", sent.Headers.Values));
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.Equal($"interleaved-thinking-2025-05-14,{ClaudeUpstream.OAuthBeta}", sent.Headers["anthropic-beta"]);

        using var body = JsonDocument.Parse(sent.Body);
        var system = body.RootElement.GetProperty("system").EnumerateArray().Select(b => b.GetProperty("text").GetString());
        Assert.Equal([ClaudeIdentity.Text, "Be brief."], system);
        Assert.Equal("claude-sonnet-5-5", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hi", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Streaming_response_is_forwarded_incrementally_and_byte_for_byte()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        var pipe = new Pipe();
        host.Upstream.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        };
        var first = Encoding.UTF8.GetBytes("event: message_start\ndata: {\"type\":\"message_start\"}\n\n");
        var second = Encoding.UTF8.GetBytes("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"hé\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        var client = await host.AuthorizedClientAsync();

        using var request = MessagesRequest(body: RequestBody.Replace("\"max_tokens\":64", "\"max_tokens\":64,\"stream\":true"));
        await pipe.Writer.WriteAsync(first);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();

        // The second event is only produced after the first has reached the client, so a buffering proxy times out here.
        var received = await ReadExactlyAsync(stream, first.Length).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(first, received);

        await pipe.Writer.WriteAsync(second);
        await pipe.Writer.CompleteAsync();
        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        Assert.Equal(second, rest.ToArray());
    }

    [Fact]
    public async Task A_client_error_status_and_body_pass_through()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync();
        const string error = """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210000 tokens > 200000 maximum"}}""";
        host.Upstream.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(error) };
            response.Headers.Add("request-id", "req_upstream_9");
            return response;
        };
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync());
        Assert.Equal("req_upstream_9", response.Headers.GetValues("request-id").Single());
        Assert.Single(host.Upstream.Requests);
    }

    [Fact]
    public async Task Count_tokens_is_proxied_with_only_the_model_rewritten()
    {
        await using var host = await RouterHost.StartAsync();
        await host.LoginAsync(accessToken: "access-current");
        const string upstreamBody = """{"input_tokens":12}""";
        host.Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(upstreamBody) };
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest("/v1/messages/count_tokens?beta=true"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(upstreamBody, await response.Content.ReadAsStringAsync());
        var sent = Assert.Single(host.Upstream.Requests);
        Assert.Equal($"{RouterHost.UpstreamBase}/v1/messages/count_tokens?beta=true", sent.Uri.ToString());
        Assert.Equal(RequestBody.Replace("claude-sonnet-4-5", "claude-sonnet-5-5"), Encoding.UTF8.GetString(sent.Body));
        Assert.Equal("Bearer access-current", sent.Headers["authorization"]);
        Assert.Contains(ClaudeUpstream.OAuthBeta, sent.Headers["anthropic-beta"]);
    }

    [Fact]
    public async Task Without_any_login_the_tier_is_empty_and_the_503_names_the_login_commands()
    {
        await using var host = await RouterHost.StartAsync();
        var client = await host.AuthorizedClientAsync();

        using var response = await client.SendAsync(MessagesRequest());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var message = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>();
        Assert.StartsWith("No enabled model is available in the mid tier.", message);
        Assert.Contains("'simple-router login claude'", message);
        Assert.Contains("'simple-router login codex'", message);
        Assert.Empty(host.Upstream.Requests);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}

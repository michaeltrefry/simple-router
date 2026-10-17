using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using SimpleRouter.Routing;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

/// <summary>
/// The translated Codex (Responses) and local (Chat Completions) streams, read by the official Anthropic .NET SDK through
/// its streaming Messages API against a running router: the SDK's own parser and accumulator must accept every stream.
/// </summary>
public class AnthropicSdkStreamTests
{
    private const string LocalSeed = """
        {
          "defaultTier": "high",
          "models": [ { "id": "local-a", "provider": "local", "upstreamModelId": "upstream-local", "enabled": true, "baseUrl": "http://local-a.test/v1" } ],
          "tiers": { "high": [ "local-a" ] },
          "aliases": []
        }
        """;

    /// <summary>Only Codex is logged in, so every high-tier session is served by gpt-6.1-sol.</summary>
    private static async Task<RouterHost> StartCodexAsync(Func<HttpRequestMessage, HttpResponseMessage> codex)
    {
        var host = await RouterHost.StartAsync();
        await host.LoginCodexAsync();
        host.Codex.Respond = codex;
        return host;
    }

    /// <summary>A single healthy local model serves the high tier.</summary>
    private static async Task<RouterHost> StartLocalAsync(Func<HttpRequestMessage, HttpResponseMessage> chat)
    {
        var seedPath = Path.Combine(Path.GetTempPath(), "simple-router-tests", $"seed-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, LocalSeed);
        var local = new FakeHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/models") ? new HttpResponseMessage(HttpStatusCode.OK) : chat(request),
        };
        return await RouterHost.StartInAsync(RouterHost.NewDataDirectory(), local, $"--SimpleRouter:SeedFile={seedPath}");
    }

    private static async Task<AnthropicClient> SdkClientAsync(RouterHost host, string sessionId)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add(SessionKey.HeaderName, sessionId);
        http.DefaultRequestHeaders.Add(TierResolver.HeaderName, "high");
        return new AnthropicClient { BaseUrl = host.Client.BaseAddress!.ToString().TrimEnd('/'), ApiKey = await host.GatewayKeyAsync(), MaxRetries = 0, HttpClient = http };
    }

    private static readonly JsonObject ListFilesTool = new()
    {
        ["name"] = "list_files",
        ["description"] = "Lists files.",
        ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } } },
    };

    /// <summary>A Messages request built from its wire JSON, so the SDK sends exactly what a client would.</summary>
    private static MessageCreateParams Params(JsonArray messages) =>
        MessageCreateParams.FromRawUnchecked(
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(),
            Raw(new JsonObject
            {
                ["model"] = "claude-opus-5-5",
                ["max_tokens"] = 1024,
                ["system"] = "You are terse.",
                ["messages"] = messages,
                ["tools"] = new JsonArray { ListFilesTool.DeepClone() },
            }));

    private static Dictionary<string, JsonElement> Raw(JsonObject body) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body.ToJsonString())!;

    private static JsonArray FirstTurn() => new() { new JsonObject { ["role"] = "user", ["content"] = "List the files in src." } };

    /// <summary>The first turn, the assistant's tool call and its result: the request a client sends after running the tool.</summary>
    private static JsonArray ToolResultTurn(string callId) =>
    [
        new JsonObject { ["role"] = "user", ["content"] = "List the files in src." },
        JsonNode.Parse($$$"""{"role":"assistant","content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"{{{callId}}}","name":"list_files","input":{"path":"src"}}]}"""),
        JsonNode.Parse($$$"""{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{{callId}}}","content":"a.cs\nb.cs"}]}"""),
    ];

    private static async Task<Message> StreamAsync(RouterHost host, JsonArray messages, string sessionId = "sdk-session")
    {
        var client = await SdkClientAsync(host, sessionId);
        return await client.Messages.CreateStreaming(Params(messages)).Aggregate();
    }

    private static string Text(Message message) =>
        string.Concat(message.Content.Select(b => b.TryPickText(out var text) ? text.Text : ""));

    private static ToolUseBlock ToolUse(Message message) =>
        Assert.Single(message.Content.Select(b => b.TryPickToolUse(out var tool) ? tool : null).OfType<ToolUseBlock>());

    [Fact]
    public async Task A_codex_stream_with_reasoning_text_and_a_fragmented_tool_call_accumulates_in_the_sdk()
    {
        await using var host = await StartCodexAsync(_ => Stream(ReasoningTextAndToolCall));

        var message = await StreamAsync(host, FirstTurn());

        Assert.Equal("gpt-6.1-sol", message.Model.Raw());
        Assert.True(message.Content[0].TryPickThinking(out var thinking));
        Assert.Equal("Need the file list.\n\nThen read it.", thinking!.Thinking);
        Assert.Equal("Let me look.", Text(message));
        var tool = ToolUse(message);
        Assert.Equal(("call_abc123", "list_files"), (tool.ID, tool.Name));
        Assert.Equal("""{"path":"src","depth":2}""", JsonSerializer.Serialize(tool.Input));
        Assert.Equal(StopReason.ToolUse, message.StopReason!.Value());
        Assert.Equal(20, message.Usage.InputTokens);
        Assert.Equal(100, message.Usage.CacheReadInputTokens);
        Assert.Equal(45, message.Usage.OutputTokens);
    }

    [Fact]
    public async Task A_codex_tool_call_round_trip_accumulates_in_the_sdk()
    {
        await using var host = await StartCodexAsync(_ => Stream(Sse(Created, ItemAdded(0, "message"), TextDelta(0, "Two files: a.cs and b.cs."),
            ItemDone(0, "message"), Completed(input: 60, output: 9))));

        var message = await StreamAsync(host, ToolResultTurn("call_abc123"));

        Assert.Equal("Two files: a.cs and b.cs.", Text(message));
        Assert.Equal(StopReason.EndTurn, message.StopReason!.Value());
        Assert.Equal(9, message.Usage.OutputTokens);
        var input = JsonNode.Parse(Assert.Single(host.Codex.Requests).Body)!["input"]!.AsArray();
        var output = input.Single(i => i!["type"]?.GetValue<string>() == "function_call_output")!;
        Assert.Equal("call_abc123", output["call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_codex_stream_that_fails_after_output_surfaces_as_an_sdk_error()
    {
        await using var host = await StartCodexAsync(_ => Stream(Sse(Created, TextDelta(0, "Half"),
            """{"type":"response.failed","response":{"error":{"code":"invalid_prompt","message":"Invalid prompt."}}}""")));

        var error = await Assert.ThrowsAsync<AnthropicSseException>(() => StreamAsync(host, FirstTurn()));

        Assert.Contains("\"type\":\"invalid_request_error\",\"message\":\"Invalid prompt.\"", error.Message);
    }

    [Fact]
    public async Task A_codex_stream_cut_off_before_completion_surfaces_as_an_sdk_error()
    {
        await using var host = await StartCodexAsync(_ => Stream(Sse(Created, TextDelta(0, "Half"))));

        var error = await Assert.ThrowsAsync<AnthropicSseException>(() => StreamAsync(host, FirstTurn()));

        Assert.Contains("\"message\":\"Codex stream ended before completion.\"", error.Message);
    }

    private static HttpResponseMessage ChatSse(params string[] chunks) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(chunks.Select(c => $"data: {c}\n\n")), Encoding.UTF8, "text/event-stream"),
        };

    [Fact]
    public async Task A_local_stream_with_text_and_a_fragmented_tool_call_accumulates_in_the_sdk()
    {
        await using var host = await StartLocalAsync(_ => ChatSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"Checking."}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_7","type":"function","function":{"name":"list_files","arguments":"{\"path\""}}]}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"src\"}"}}]}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"id":"c1","choices":[],"usage":{"prompt_tokens":40,"completion_tokens":9}}""",
            "[DONE]"));

        var message = await StreamAsync(host, FirstTurn());

        Assert.Equal("local-a", message.Model.Raw());
        Assert.Equal("Checking.", Text(message));
        var tool = ToolUse(message);
        Assert.Equal(("call_7", "list_files"), (tool.ID, tool.Name));
        Assert.Equal("""{"path":"src"}""", JsonSerializer.Serialize(tool.Input));
        Assert.Equal(StopReason.ToolUse, message.StopReason!.Value());
        Assert.Equal(40, message.Usage.InputTokens);
        Assert.Equal(9, message.Usage.OutputTokens);
    }

    [Fact]
    public async Task A_local_stream_with_reasoning_accumulates_a_signed_thinking_block_in_the_sdk()
    {
        await using var host = await StartLocalAsync(_ => ChatSse(
            """{"id":"c4","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Need the "}}]}""",
            """{"id":"c4","choices":[{"index":0,"delta":{"reasoning_content":"file list."}}]}""",
            """{"id":"c4","choices":[{"index":0,"delta":{"content":"Checking."}}]}""",
            """{"id":"c4","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":40,"completion_tokens":9}}""",
            "[DONE]"));

        var message = await StreamAsync(host, FirstTurn());

        Assert.True(message.Content[0].TryPickThinking(out var thinking));
        Assert.Equal(("Need the file list.", "local:"), (thinking!.Thinking, thinking.Signature));
        Assert.Equal("Checking.", Text(message));
    }

    [Fact]
    public async Task A_local_tool_call_round_trip_accumulates_in_the_sdk()
    {
        await using var host = await StartLocalAsync(_ => ChatSse(
            """{"id":"c2","choices":[{"index":0,"delta":{"role":"assistant","content":"Two files."}}]}""",
            """{"id":"c2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":70,"completion_tokens":3}}""",
            "[DONE]"));

        var message = await StreamAsync(host, ToolResultTurn("call_7"));

        Assert.Equal("Two files.", Text(message));
        Assert.Equal(StopReason.EndTurn, message.StopReason!.Value());
        Assert.Equal(3, message.Usage.OutputTokens);
        var sent = JsonNode.Parse(host.Local.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).Body)!["messages"]!.AsArray();
        Assert.Equal("call_7", sent[^1]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_local_stream_that_fails_after_output_surfaces_as_an_sdk_error()
    {
        await using var host = await StartLocalAsync(_ => ChatSse(
            """{"id":"c3","choices":[{"index":0,"delta":{"role":"assistant","content":"partial"}}]}""",
            """{"error":{"message":"out of memory"}}"""));

        var error = await Assert.ThrowsAsync<AnthropicSseException>(() => StreamAsync(host, FirstTurn()));

        Assert.Contains("\"message\":\"out of memory\"", error.Message);
    }

    public static TheoryData<string> CompletingCodexStreams => new(Streams.Where(f => !f.Value.Fails).Select(f => f.Key));

    public static TheoryData<string> FailingCodexStreams => new(Streams.Where(f => f.Value.Fails).Select(f => f.Key));

    private static string BlockType(ContentBlock block) =>
        block.TryPickText(out _) ? "text" : block.TryPickThinking(out _) ? "thinking" : block.TryPickToolUse(out _) ? "tool_use" : "other";

    [Theory]
    [MemberData(nameof(CompletingCodexStreams))]
    public async Task Every_completing_codex_stream_fixture_accumulates_in_the_sdk(string fixture)
    {
        var sse = Streams[fixture].Sse;
        await using var host = await StartCodexAsync(_ => Stream(sse));

        var message = await StreamAsync(host, FirstTurn());

        var expected = ResponsesStreamTranslatorTests.Translate(sse);
        Assert.Equal(expected.Blocks.Select(b => b.Type), message.Content.Select(BlockType));
        Assert.Equal(string.Concat(expected.Blocks.Where(b => b.Type == "text").Select(b => b.Text)), Text(message));
        Assert.Equal(expected.StopReason, message.StopReason!.Raw());
    }

    [Theory]
    [MemberData(nameof(FailingCodexStreams))]
    public async Task Every_failing_codex_stream_fixture_surfaces_as_an_sdk_error(string fixture)
    {
        await using var host = await StartCodexAsync(_ => Stream(Streams[fixture].Sse));

        var error = await Assert.ThrowsAsync<AnthropicSseException>(() => StreamAsync(host, FirstTurn()));

        Assert.Contains("\"type\":\"error\"", error.Message);
    }

    [Theory]
    [MemberData(nameof(LocalRoutingTests.FramingVariants), MemberType = typeof(LocalRoutingTests))]
    public async Task Every_local_stream_framing_variant_accumulates_in_the_sdk(string _, string upstream)
    {
        await using var host = await StartLocalAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream"),
        });

        var message = await StreamAsync(host, FirstTurn());

        Assert.Equal("hi", Text(message));
        Assert.Equal(StopReason.EndTurn, message.StopReason!.Value());
    }
}

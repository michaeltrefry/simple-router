using System.Text.Json.Nodes;
using SimpleRouter.Codex;
using SimpleRouter.Tests.Support;
using static SimpleRouter.Tests.Support.ResponsesFixtures;

namespace SimpleRouter.Tests;

public class ResponsesStreamTranslatorTests
{
    /// <summary>Runs a Responses SSE fixture through the translator as the upstream loop does, and parses the result strictly.</summary>
    private static AnthropicSse.Message Translate(string responsesSse, int estimate = 7)
    {
        var translator = new ResponsesStreamTranslator("gpt-6.1-sol", estimate);
        var events = new List<JsonObject> { translator.Start() };
        foreach (var frame in responsesSse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (translator.Ended)
            {
                break;
            }
            var data = frame.Split('\n').Single(l => l.StartsWith("data: "))["data: ".Length..];
            events.AddRange(translator.Feed(JsonNode.Parse(data)!.AsObject()));
        }
        if (!translator.Ended)
        {
            events.AddRange(translator.Abort("ended early"));
        }
        return AnthropicSse.Parse(ResponsesStreamTranslator.Format(events));
    }

    [Fact]
    public void Reasoning_text_and_a_fragmented_tool_call_become_thinking_text_and_tool_use_blocks()
    {
        var message = Translate(ReasoningTextAndToolCall);

        Assert.Equal("gpt-6.1-sol", message.Model);
        Assert.Equal(["thinking", "text", "tool_use"], message.Blocks.Select(b => b.Type));
        Assert.Equal("Need the file list.\n\nThen read it.", message.Blocks[0].Text);
        Assert.Equal("Let me look.", message.Blocks[1].Text);
        Assert.Equal("call_abc123", message.Blocks[2].Id);
        Assert.Equal("list_files", message.Blocks[2].Name);
        Assert.Equal("""{"path":"src","depth":2}""", message.Blocks[2].Input!.ToJsonString());
        Assert.Equal("tool_use", message.StopReason);
        Assert.Equal(20, message.Usage!["input_tokens"]!.GetValue<int>());
        Assert.Equal(100, message.Usage["cache_read_input_tokens"]!.GetValue<int>());
        Assert.Equal(45, message.Usage["output_tokens"]!.GetValue<int>());
        Assert.Equal("message_stop", message.Events[^1]);
    }

    [Fact]
    public void Parallel_calls_with_fragmented_arguments_keep_their_own_call_ids_and_inputs()
    {
        var message = Translate(Sse(
            Created,
            CallAdded(0, "call_one", "read_file"),
            ArgsDelta(0, "{\"pa"),
            CallAdded(1, "call_two", "read_file"),
            ArgsDelta(1, "{\"path\":"),
            ArgsDelta(0, "th\":\"a.txt\"}"),
            ArgsDelta(1, "\"b.txt\"}"),
            CallDone(0, "call_one", "read_file", ""),
            CallDone(1, "call_two", "read_file", ""),
            Completed()));

        Assert.Equal(["tool_use", "tool_use"], message.Blocks.Select(b => b.Type));
        Assert.Equal(["call_one", "call_two"], message.Blocks.Select(b => b.Id));
        Assert.Equal("""{"path":"a.txt"}""", message.Blocks[0].Input!.ToJsonString());
        Assert.Equal("""{"path":"b.txt"}""", message.Blocks[1].Input!.ToJsonString());
        Assert.Equal("tool_use", message.StopReason);
    }

    [Fact]
    public void A_plain_text_completion_ends_the_turn()
    {
        var message = Translate(Sse(Created, TextDelta(0, "Hello"), ItemDone(0, "message"), Completed(input: 9, output: 1)));

        Assert.Equal("Hello", Assert.Single(message.Blocks).Text);
        Assert.Equal("end_turn", message.StopReason);
        Assert.Equal(9, message.Usage!["input_tokens"]!.GetValue<int>());
        Assert.Null(message.Usage["cache_read_input_tokens"]);
    }

    [Fact]
    public void An_incomplete_response_stops_at_max_tokens_and_drops_an_unfinished_call()
    {
        var message = Translate(Sse(
            Created,
            TextDelta(0, "Partial"),
            ItemDone(0, "message"),
            CallAdded(1, "call_cut", "write_file"),
            ArgsDelta(1, "{\"content\":\"abc"),
            """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":5,"output_tokens":64}}}"""));

        Assert.Equal("Partial", Assert.Single(message.Blocks).Text);
        Assert.Equal("max_tokens", message.StopReason);
        Assert.Equal(64, message.Usage!["output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void An_incomplete_response_after_a_finished_call_still_asks_the_client_to_run_it()
    {
        var message = Translate(Sse(
            Created,
            CallDone(0, "call_done", "ls", "{}"),
            """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}"""));

        Assert.Equal("call_done", Assert.Single(message.Blocks).Id);
        Assert.Equal("tool_use", message.StopReason);
    }

    [Fact]
    public void A_failed_response_ends_with_an_error_event_and_no_message_stop()
    {
        var message = Translate(Sse(
            Created,
            TextDelta(0, "Start"),
            """{"type":"response.failed","response":{"status":"failed","error":{"code":"server_error","message":"The model crashed."}}}"""));

        Assert.Equal("The model crashed.", message.Error);
        Assert.Equal("Start", Assert.Single(message.Blocks).Text);
        Assert.DoesNotContain("message_stop", message.Events);
        Assert.DoesNotContain("message_delta", message.Events);
    }

    [Theory]
    [InlineData("usage_limit_reached", "rate_limit_error")]
    [InlineData("context_length_exceeded", "invalid_request_error")]
    [InlineData("whatever", "api_error")]
    public void A_stream_error_event_maps_its_code_onto_an_anthropic_error_type(string code, string type)
    {
        var translator = new ResponsesStreamTranslator("m", 1);
        translator.Start();

        var events = translator.Feed(JsonNode.Parse($$"""{"type":"error","code":"{{code}}","message":"nope"}""")!.AsObject());

        var error = Assert.Single(events);
        Assert.Equal(type, error["error"]!["type"]!.GetValue<string>());
        Assert.Equal("nope", error["error"]!["message"]!.GetValue<string>());
        Assert.True(translator.Ended);
    }

    [Fact]
    public void A_stream_without_a_terminal_event_is_aborted()
    {
        var message = Translate(Sse(Created, TextDelta(0, "Half")));

        Assert.Equal("ended early", message.Error);
        Assert.DoesNotContain("message_stop", message.Events);
    }

    [Fact]
    public void Items_that_only_arrive_whole_are_synthesized()
    {
        var message = Translate(Sse(
            Created,
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","summary":[{"type":"summary_text","text":"Thought."}]}}""",
            """{"type":"response.output_item.done","output_index":1,"item":{"type":"message","content":[{"type":"output_text","text":"Answer."}]}}""",
            Completed()));

        Assert.Equal(["thinking", "text"], message.Blocks.Select(b => b.Type));
        Assert.Equal(["Thought.", "Answer."], message.Blocks.Select(b => b.Text));
    }

    [Fact]
    public void Output_only_present_on_the_completed_response_is_still_delivered()
    {
        var output = """[{"type":"message","content":[{"type":"output_text","text":"Late."}]},{"type":"function_call","call_id":"call_late","name":"ls","arguments":"{}"}]""";

        var message = Translate(Sse(Created, Completed(outputItems: output)));

        Assert.Equal(["text", "tool_use"], message.Blocks.Select(b => b.Type));
        Assert.Equal("call_late", message.Blocks[1].Id);
        Assert.Equal("tool_use", message.StopReason);
    }

    [Fact]
    public void Nameless_calls_are_dropped_and_invalid_call_ids_are_replaced()
    {
        var message = Translate(Sse(
            Created,
            CallDone(0, "call_x", "", "{}"),
            CallDone(1, "call.with.dots", "ls", "not json"),
            Completed()));

        var block = Assert.Single(message.Blocks);
        Assert.Matches("^toolu_[0-9a-f]{24}$", block.Id);
        Assert.Equal("{}", block.Input!.ToJsonString());
        Assert.Equal("tool_use", message.StopReason);
    }

    [Fact]
    public void Non_streaming_aggregation_folds_the_events_into_one_message()
    {
        var translator = new ResponsesStreamTranslator("gpt-6.1-sol", 3);
        var builder = new AnthropicMessageBuilder();
        builder.Apply(translator.Start());
        foreach (var frame in ReasoningTextAndToolCall.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            translator.Feed(JsonNode.Parse(frame.Split('\n')[1]["data: ".Length..])!.AsObject()).ForEach(builder.Apply);
        }

        var message = builder.Message;
        Assert.Null(builder.Error);
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        var content = message["content"]!.AsArray();
        Assert.Equal("Need the file list.\n\nThen read it.", content[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("", content[0]!["signature"]!.GetValue<string>());
        Assert.Equal("Let me look.", content[1]!["text"]!.GetValue<string>());
        Assert.Equal("call_abc123", content[2]!["id"]!.GetValue<string>());
        Assert.Equal(2, content[2]!["input"]!["depth"]!.GetValue<int>());
        Assert.Equal(45, message["usage"]!["output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void A_refusal_streams_as_text_and_stops_with_refusal()
    {
        var message = Translate(Sse(
            Created,
            """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"I can't "}""",
            """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"help with that."}""",
            ItemDone(0, "message"),
            Completed()));

        Assert.Equal("I can't help with that.", Assert.Single(message.Blocks).Text);
        Assert.Equal("refusal", message.StopReason);
    }

    [Fact]
    public void A_refusal_that_only_arrives_whole_is_folded_and_stops_with_refusal()
    {
        var message = Translate(Sse(
            Created,
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"refusal","refusal":"No."}]}}""",
            Completed()));

        Assert.Equal("No.", Assert.Single(message.Blocks).Text);
        Assert.Equal("refusal", message.StopReason);
    }

    [Fact]
    public void An_incomplete_response_still_delivers_text_only_on_the_terminal_response_but_not_a_truncated_call()
    {
        var output = """[{"type":"message","content":[{"type":"output_text","text":"Late."}]},{"type":"function_call","call_id":"call_cut","name":"ls","arguments":"{\"pa"}]""";

        var message = Translate(Sse(
            Created,
            $$$"""{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":{{{output}}}}}"""));

        Assert.Equal("Late.", Assert.Single(message.Blocks).Text);
        Assert.Equal("max_tokens", message.StopReason);
    }

    [Fact]
    public void Encrypted_reasoning_becomes_the_thinking_signature_even_without_a_summary()
    {
        var message = Translate(Sse(
            Created,
            SummaryDelta(0, 0, "Thought."),
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","summary":[],"encrypted_content":"ENC-A"}}""",
            """{"type":"response.output_item.done","output_index":1,"item":{"type":"reasoning","summary":[],"encrypted_content":"ENC-B"}}""",
            TextDelta(2, "Done."),
            ItemDone(2, "message"),
            Completed()));

        Assert.Equal(["thinking", "thinking", "text"], message.Blocks.Select(b => b.Type));
        Assert.Equal(["Thought.", ""], message.Blocks.Take(2).Select(b => b.Text));
        Assert.Equal(["codex:ENC-A", "codex:ENC-B"], message.Blocks.Take(2).Select(b => b.Signature));
    }

    [Fact]
    public void Non_streaming_aggregation_keeps_the_encrypted_reasoning_signature()
    {
        var translator = new ResponsesStreamTranslator("gpt-6.1-sol", 3);
        var builder = new AnthropicMessageBuilder();
        builder.Apply(translator.Start());
        foreach (var evt in new[]
        {
            SummaryDelta(0, 0, "Thought."),
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","summary":[],"encrypted_content":"ENC"}}""",
            Completed(),
        })
        {
            translator.Feed(JsonNode.Parse(evt)!.AsObject()).ForEach(builder.Apply);
        }

        var thinking = Assert.Single(builder.Message["content"]!.AsArray())!;
        Assert.Equal("Thought.", thinking["thinking"]!.GetValue<string>());
        Assert.Equal("codex:ENC", thinking["signature"]!.GetValue<string>());
    }
}

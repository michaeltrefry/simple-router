using System.Text;
using System.Text.Json.Nodes;
using SimpleRouter.Local;
using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class ChatStreamTranslatorTests
{
    private static AnthropicSse.Message Run(params string[] chunks)
    {
        var translator = new ChatStreamTranslator("local-a", estimatedInputTokens: 7);
        var sse = new StringBuilder(translator.Start());
        foreach (var chunk in chunks)
        {
            sse.Append(translator.Feed(JsonNode.Parse(chunk)!.AsObject()));
        }
        sse.Append(translator.Finish());
        return AnthropicSse.Parse(sse.ToString());
    }

    private static string Content(string text) => new JsonObject
    {
        ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = text } } },
    }.ToJsonString();

    private static string Tool(int index, string? id, string? name, string args)
    {
        var function = new JsonObject();
        if (name is not null)
        {
            function["name"] = name;
        }
        function["arguments"] = args;
        var call = new JsonObject { ["index"] = index };
        if (id is not null)
        {
            call["id"] = id;
            call["type"] = "function";
        }
        call["function"] = function;
        return new JsonObject
        {
            ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray { call } } } },
        }.ToJsonString();
    }

    private static string Finish(string reason) => $$"""{"choices":[{"index":0,"delta":{},"finish_reason":"{{reason}}"}]}""";

    private const string Usage = """{"choices":[],"usage":{"prompt_tokens":50,"completion_tokens":12}}""";

    [Fact]
    public void Text_only_stream_is_one_text_block_ending_the_turn()
    {
        var message = Run(Content("Hel"), Content("lo"), Finish("stop"), Usage);

        var block = Assert.Single(message.Blocks);
        Assert.Equal(("text", "Hello"), (block.Type, block.Text));
        Assert.Equal("end_turn", message.StopReason);
        Assert.Equal("local-a", message.Model);
        Assert.Equal(50, message.Usage!["input_tokens"]!.GetValue<int>());
        Assert.Equal(12, message.Usage!["output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Text_then_a_fragmented_tool_call_yields_a_tool_use_block_with_parsed_input()
    {
        var message = Run(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"","tool_calls":null}}]}""",
            Content("Let me look."),
            Tool(0, "call_abc", "Read", ""),
            Tool(0, null, null, "{\"pa"),
            Tool(0, null, null, "th\": \"src/"),
            Tool(0, null, null, "a.cs\"}"),
            Finish("tool_calls"),
            Usage);

        Assert.Equal(2, message.Blocks.Count);
        Assert.Equal("Let me look.", message.Blocks[0].Text);
        var tool = message.Blocks[1];
        Assert.Equal(("tool_use", "call_abc", "Read"), (tool.Type, tool.Id, tool.Name));
        Assert.Equal("""{"path":"src/a.cs"}""", tool.Input!.ToJsonString());
        Assert.Equal("tool_use", message.StopReason);
    }

    [Fact]
    public void Parallel_tool_calls_with_interleaved_fragments_become_sequential_blocks_by_index()
    {
        var message = Run(
            Tool(0, "call_a", "Read", "{\"path\":"),
            Tool(1, "call_b", "Grep", "{\"pattern\""),
            Tool(0, null, null, "\"a.txt\"}"),
            Tool(1, null, null, ":\"TODO\"}"),
            Tool(2, "call_c", "", "{}"),
            Finish("tool_calls"));

        Assert.Equal(["call_a", "call_b"], message.Blocks.Select(b => b.Id));
        Assert.Equal("""{"path":"a.txt"}""", message.Blocks[0].Input!.ToJsonString());
        Assert.Equal("""{"pattern":"TODO"}""", message.Blocks[1].Input!.ToJsonString());
        Assert.Equal("tool_use", message.StopReason);
        Assert.Equal(7, message.Usage!["input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Unparseable_tool_arguments_degrade_to_an_empty_object()
    {
        var message = Run(Tool(0, "call_a", "Bash", "{\"cmd\": \"ls"), Finish("tool_calls"));

        Assert.Equal("{}", Assert.Single(message.Blocks).Input!.ToJsonString());
    }

    [Fact]
    public void Reasoning_fields_and_leading_think_tags_split_across_chunks_stream_as_one_signed_thinking_block()
    {
        var message = Run(
            """{"choices":[{"index":0,"delta":{"reasoning_content":"pondering"}}]}""",
            """{"choices":[{"index":0,"delta":{"reasoning":"more"}}]}""",
            Content("  <thi"),
            Content("nk>step one</th"),
            Content("ink>\n\nAnswer: use <think> tags"),
            Content(" sparingly."),
            Finish("stop"));

        Assert.Collection(message.Blocks,
            thinking =>
            {
                Assert.Equal("thinking", thinking.Type);
                Assert.Equal("ponderingmorestep one", thinking.Text);
                Assert.Equal(ChatResponseTranslator.SignaturePrefix, thinking.Signature);
            },
            text => Assert.Equal(("text", "Answer: use <think> tags sparingly."), (text.Type, text.Text)));
    }

    [Fact]
    public void Reasoning_after_text_opens_a_new_thinking_block_and_tool_calls_follow_both()
    {
        var message = Run(
            """{"choices":[{"index":0,"delta":{"reasoning_content":"first"}}]}""",
            Content("Looking."),
            """{"choices":[{"index":0,"delta":{"reasoning_content":"second"}}]}""",
            Tool(0, "call_1", "Read", "{}"),
            Finish("tool_calls"));

        Assert.Equal(["thinking", "text", "thinking", "tool_use"], message.Blocks.Select(b => b.Type));
        Assert.Equal("second", message.Blocks[2].Text);
    }

    [Fact]
    public void A_stream_of_only_reasoning_still_produces_a_valid_message()
    {
        var message = Run(Content("<think>only thoughts</thi"), Finish("length"));

        var thinking = Assert.Single(message.Blocks);
        Assert.Equal(("thinking", "only thoughts</thi"), (thinking.Type, thinking.Text));
        Assert.Equal("max_tokens", message.StopReason);
    }

    [Fact]
    public void Text_that_only_resembles_an_open_tag_is_kept()
    {
        var message = Run(Content("<th"), Content("is is fine"), Finish("stop"));

        Assert.Equal("<this is fine", Assert.Single(message.Blocks).Text);
    }

    [Fact]
    public void A_stream_cut_off_before_a_finish_reason_ends_in_an_error_event()
    {
        var translator = new ChatStreamTranslator("local-a", 1);
        var sse = translator.Start() + translator.Feed(JsonNode.Parse(Content("partial"))!.AsObject()) + translator.Abort("Local model stream ended unexpectedly.");

        Assert.Contains("event: content_block_stop", sse);
        Assert.EndsWith("event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"Local model stream ended unexpectedly.\"}}\n\n", sse);
        Assert.DoesNotContain("message_stop", sse);
    }

    [Fact]
    public void An_index_less_chunk_repeating_the_current_call_id_continues_that_call()
    {
        static string Call(string? id, string? name, string args)
        {
            var call = new JsonObject();
            if (id is not null)
            {
                call["id"] = id;
            }
            call["function"] = name is null ? new JsonObject { ["arguments"] = args } : new JsonObject { ["name"] = name, ["arguments"] = args };
            return new JsonObject
            {
                ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray { call } } } },
            }.ToJsonString();
        }

        var message = Run(
            Call("call_a", "Read", "{\"path\":"),
            Call("call_a", null, "\"a.txt\"}"),
            Call("call_b", "Grep", "{\"pattern\":"),
            Call(null, null, "\"TODO\"}"),
            Finish("tool_calls"));

        Assert.Equal(["call_a", "call_b"], message.Blocks.Select(b => b.Id));
        Assert.Equal("""{"path":"a.txt"}""", message.Blocks[0].Input!.ToJsonString());
        Assert.Equal("""{"pattern":"TODO"}""", message.Blocks[1].Input!.ToJsonString());
    }
}

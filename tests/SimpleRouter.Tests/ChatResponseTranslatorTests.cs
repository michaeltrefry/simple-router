using System.Text.Json.Nodes;
using SimpleRouter.Api;
using SimpleRouter.Local;

namespace SimpleRouter.Tests;

public class ChatResponseTranslatorTests
{
    [Theory]
    [InlineData("stop", false, "end_turn")]
    [InlineData("length", false, "max_tokens")]
    [InlineData("length", true, "max_tokens")]
    [InlineData("tool_calls", true, "tool_use")]
    [InlineData("function_call", true, "tool_use")]
    [InlineData("stop", true, "tool_use")]
    [InlineData("tool_calls", false, "end_turn")]
    [InlineData("content_filter", false, "refusal")]
    [InlineData(null, false, "end_turn")]
    public void Finish_reasons_map_to_anthropic_stop_reasons(string? finishReason, bool hasToolUse, string expected)
    {
        Assert.Equal(expected, ChatResponseTranslator.StopReason(finishReason, hasToolUse));
    }

    [Fact]
    public void Text_and_tool_calls_become_an_anthropic_message()
    {
        var chat = JsonNode.Parse("""
            {"id":"chatcmpl-1","object":"chat.completion","model":"upstream","choices":[{"index":0,"finish_reason":"tool_calls",
              "message":{"role":"assistant","reasoning_content":"secret plan","content":"<think>plan</think>\n\nI'll read it.",
                "tool_calls":[{"id":"call_1","type":"function","function":{"name":"Read","arguments":"{\"path\":\"a.txt\"}"}},
                              {"id":"call_2","type":"function","function":{"name":"Grep","arguments":"not json"}},
                              {"id":"call_3","type":"function","function":{"name":"","arguments":"{}"}}]}}],
             "usage":{"prompt_tokens":120,"completion_tokens":30,"total_tokens":150}}
            """)!.AsObject();

        var message = ChatResponseTranslator.Translate(chat, "local-a", estimatedInputTokens: 99);

        Assert.Equal("message", message["type"]!.GetValue<string>());
        Assert.Equal("assistant", message["role"]!.GetValue<string>());
        Assert.StartsWith("msg_", message["id"]!.GetValue<string>());
        Assert.Equal("local-a", message["model"]!.GetValue<string>());
        Assert.Equal("""
            [{"type":"thinking","thinking":"secret planplan","signature":"local:"},{"type":"text","text":"I'll read it."},{"type":"tool_use","id":"call_1","name":"Read","input":{"path":"a.txt"}},{"type":"tool_use","id":"call_2","name":"Grep","input":{}}]
            """, message["content"]!.ToJsonString(WireJson.Relaxed));
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        Assert.True(message.ContainsKey("stop_sequence") && message["stop_sequence"] is null);
        Assert.Equal("""{"input_tokens":120,"output_tokens":30}""", message["usage"]!.ToJsonString(WireJson.Relaxed));
    }

    [Fact]
    public void Missing_usage_falls_back_to_the_input_estimate_and_bad_tool_ids_are_replaced()
    {
        var chat = JsonNode.Parse("""
            {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"call:bad id","type":"function","function":{"name":"Bash","arguments":"{\"cmd\":\"ls\"}"}}]}}]}
            """)!.AsObject();

        var message = ChatResponseTranslator.Translate(chat, "local-a", estimatedInputTokens: 42);

        var tool = message["content"]![0]!;
        Assert.Matches("^toolu_[0-9a-f]{24}$", tool["id"]!.GetValue<string>());
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        Assert.Equal("""{"input_tokens":42,"output_tokens":0}""", message["usage"]!.ToJsonString(WireJson.Relaxed));
    }
}

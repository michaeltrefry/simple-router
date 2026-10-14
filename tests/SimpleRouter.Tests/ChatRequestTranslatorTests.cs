using System.Text.Json.Nodes;
using SimpleRouter.Api;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

public class ChatRequestTranslatorTests
{
    private static CatalogModel Model(bool vision = true, bool tools = true) => new()
    {
        Id = "local-a",
        Provider = ProviderKind.Local,
        UpstreamModelId = "upstream-local-a",
        SupportsTools = tools,
        SupportsVision = vision,
    };

    private static JsonObject Translate(string anthropic, CatalogModel? model = null, int estimate = 0) =>
        ChatRequestTranslator.Translate(JsonNode.Parse(anthropic)!.AsObject(), model ?? Model(), estimate);

    [Fact]
    public void Text_system_and_sampling_parameters_are_preserved()
    {
        var chat = Translate("""
            {"model":"claude-sonnet","max_tokens":100,"temperature":0.2,"top_p":0.9,"top_k":5,"stop_sequences":["END"],
             "system":[{"type":"text","text":"Be brief."},{"type":"text","text":"Be kind.","cache_control":{"type":"ephemeral"}}],
             "messages":[{"role":"user","content":"hi"},{"role":"assistant","content":[{"type":"text","text":"hello"}]},
                         {"role":"user","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}
            """);

        Assert.Equal("upstream-local-a", chat["model"]!.GetValue<string>());
        Assert.Equal(100, chat["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.2, chat["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, chat["top_p"]!.GetValue<double>());
        Assert.Equal("""["END"]""", chat["stop"]!.ToJsonString(WireJson.Relaxed));
        Assert.Null(chat["top_k"]);
        Assert.Null(chat["stream"]);
        Assert.Equal("""
            [{"role":"system","content":"Be brief.\n\nBe kind."},{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"user","content":"a\nb"}]
            """, chat["messages"]!.ToJsonString(WireJson.Relaxed));
    }

    [Fact]
    public void Streaming_requests_ask_for_usage()
    {
        var chat = Translate("""{"model":"m","max_tokens":1,"stream":true,"messages":[{"role":"user","content":"hi"}]}""");

        Assert.True(chat["stream"]!.GetValue<bool>());
        Assert.True(chat["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("""{"type":"auto"}""", "\"auto\"", null)]
    [InlineData("""{"type":"any"}""", "\"required\"", null)]
    [InlineData("""{"type":"none"}""", "\"none\"", null)]
    [InlineData("""{"type":"tool","name":"Read","disable_parallel_tool_use":true}""", """{"type":"function","function":{"name":"Read"}}""", false)]
    public void Tools_and_tool_choice_map_to_functions(string toolChoice, string expectedChoice, bool? parallel)
    {
        var chat = Translate("""{"model":"m","max_tokens":1,"tool_choice":""" + toolChoice + """
            ,"tools":[{"name":"Read","description":"Read a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}},
                      {"type":"web_search_20250305","name":"web_search"}],
             "messages":[{"role":"user","content":"hi"}]}
            """);

        Assert.Equal("""
            [{"type":"function","function":{"name":"Read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}}]
            """, chat["tools"]!.ToJsonString(WireJson.Relaxed));
        Assert.Equal(expectedChoice, chat["tool_choice"]!.ToJsonString(WireJson.Relaxed));
        Assert.Equal(parallel, chat["parallel_tool_calls"]?.GetValue<bool>());
    }

    [Fact]
    public void Tools_are_omitted_for_a_model_without_tool_support()
    {
        var chat = Translate("""
            {"model":"m","max_tokens":1,"tool_choice":{"type":"auto"},"tools":[{"name":"Read","input_schema":{"type":"object"}}],"messages":[{"role":"user","content":"hi"}]}
            """, Model(tools: false));

        Assert.Null(chat["tools"]);
        Assert.Null(chat["tool_choice"]);
    }

    [Fact]
    public void Tool_use_and_results_round_trip_with_matching_call_ids()
    {
        var chat = Translate("""
            {"model":"m","max_tokens":1,"messages":[
              {"role":"user","content":"read both"},
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"hmm","signature":"sig"},
                {"type":"redacted_thinking","data":"x"},
                {"type":"text","text":"Reading."},
                {"type":"tool_use","id":"toolu_1","name":"Read","input":{"path":"a.txt"}},
                {"type":"tool_use","id":"toolu_2","name":"Read","input":{"path":"b.txt"}}]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"toolu_2","content":[{"type":"text","text":"line 1"},{"type":"text","text":"line 2"}]},
                {"type":"tool_result","tool_use_id":"toolu_1","is_error":true,"content":"not found"},
                {"type":"text","text":"continue"}]}]}
            """);

        Assert.Equal("""
            [{"role":"user","content":"read both"},{"role":"assistant","content":"Reading.","tool_calls":[{"id":"toolu_1","type":"function","function":{"name":"Read","arguments":"{\"path\":\"a.txt\"}"}},{"id":"toolu_2","type":"function","function":{"name":"Read","arguments":"{\"path\":\"b.txt\"}"}}]},{"role":"tool","tool_call_id":"toolu_2","content":"line 1\nline 2"},{"role":"tool","tool_call_id":"toolu_1","content":"Error: not found"},{"role":"user","content":"continue"}]
            """, chat["messages"]!.ToJsonString(WireJson.Relaxed));
    }

    [Fact]
    public void Assistant_turn_with_only_tool_calls_has_null_content()
    {
        var chat = Translate("""
            {"model":"m","max_tokens":1,"messages":[{"role":"user","content":"go"},
              {"role":"assistant","content":[{"type":"tool_use","id":"call_9","name":"Bash","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_9"}]}]}
            """);

        var assistant = chat["messages"]![1]!;
        Assert.Null(assistant["content"]);
        Assert.Equal("{}", assistant["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("""{"role":"tool","tool_call_id":"call_9","content":""}""", chat["messages"]![2]!.ToJsonString(WireJson.Relaxed));
    }

    [Fact]
    public void Images_become_data_urls_for_vision_models_including_tool_result_images()
    {
        var chat = Translate("""
            {"model":"m","max_tokens":1,"messages":[
              {"role":"user","content":[{"type":"text","text":"what is this"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}},
                                        {"type":"image","source":{"type":"url","url":"https://example.test/cat.jpg"}}]},
              {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Shot","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"captured"},{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"BBBB"}}]}]}]}
            """);

        Assert.Equal("""
            [{"role":"user","content":[{"type":"text","text":"what is this"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}},{"type":"image_url","image_url":{"url":"https://example.test/cat.jpg"}}]},{"role":"assistant","content":null,"tool_calls":[{"id":"t1","type":"function","function":{"name":"Shot","arguments":"{}"}}]},{"role":"tool","tool_call_id":"t1","content":"captured"},{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,BBBB"}}]}]
            """, chat["messages"]!.ToJsonString(WireJson.Relaxed));
    }

    [Fact]
    public void Images_are_replaced_by_a_note_for_models_without_vision()
    {
        var chat = Translate("""
            {"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}},{"type":"text","text":"describe"}]}]}
            """, Model(vision: false));

        Assert.Equal("""
            [{"role":"user","content":"[image omitted: this model does not accept images]\ndescribe"}]
            """, chat["messages"]!.ToJsonString(WireJson.Relaxed));
    }

    [Fact]
    public void Assistant_text_blocks_are_joined_by_newlines()
    {
        var chat = Translate("""
            {"model":"m","max_tokens":1,"messages":[{"role":"user","content":"hi"},
              {"role":"assistant","content":[{"type":"text","text":"First."},{"type":"text","text":"Second."}]}]}
            """);

        Assert.Equal("First.\nSecond.", chat["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1000, 600, 500, 400)]
    [InlineData(1000, 999, 500, 1)]
    [InlineData(1000, 2000, 500, 1)]
    [InlineData(1000, 100, 500, 500)]
    [InlineData(null, 900, 500, 500)]
    public void Max_tokens_is_clamped_to_what_the_context_window_leaves_after_the_prompt(int? window, int estimate, int maxTokens, int expected)
    {
        var model = Model();
        model.ContextWindow = window;

        var chat = Translate($$"""{"model":"m","max_tokens":{{maxTokens}},"messages":[{"role":"user","content":"hi"}]}""", model, estimate);

        Assert.Equal(expected, chat["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Tool_history_is_written_as_text_for_a_model_without_tool_support()
    {
        var chat = Translate("""
            {"model":"m","max_tokens":1,"messages":[{"role":"user","content":"read it"},
              {"role":"assistant","content":[{"type":"text","text":"Reading."},{"type":"tool_use","id":"call_1","name":"Read","input":{"path":"a.txt"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"file body"},{"type":"text","text":"now summarize"}]}]}
            """, Model(tools: false));

        Assert.Equal("""
            [{"role":"user","content":"read it"},{"role":"assistant","content":"Reading.\n[called tool Read with {\"path\":\"a.txt\"}]"},{"role":"user","content":"[result of tool call call_1]: file body\nnow summarize"}]
            """, chat["messages"]!.ToJsonString(WireJson.Relaxed));
    }
}

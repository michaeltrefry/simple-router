using System.Text.Json.Nodes;
using SimpleRouter.Codex;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

public class ResponsesRequestTranslatorTests
{
    private static readonly CatalogModel Model = new() { Id = "gpt-6.1-sol", Provider = ProviderKind.Codex, UpstreamModelId = "upstream-sol" };

    private static JsonObject Translate(string anthropic) =>
        ResponsesRequestTranslator.Translate(JsonNode.Parse(anthropic)!.AsObject(), Model, "cache-key");

    [Fact]
    public void Request_level_fields_follow_the_codex_backend_contract()
    {
        var responses = Translate("""
            {"model":"claude-opus","max_tokens":4096,"temperature":0.2,"top_p":0.9,"stop_sequences":["X"],"metadata":{"user_id":"u"},
             "stream":false,"system":[{"type":"text","text":"Rule one."},{"type":"text","text":"Rule two."}],
             "messages":[{"role":"user","content":"hi"}]}
            """);

        Assert.Equal("upstream-sol", responses["model"]!.GetValue<string>());
        Assert.True(responses["stream"]!.GetValue<bool>());
        Assert.False(responses["store"]!.GetValue<bool>());
        Assert.Equal("Rule one.\n\nRule two.", responses["instructions"]!.GetValue<string>());
        Assert.Equal("cache-key", responses["prompt_cache_key"]!.GetValue<string>());
        foreach (var unsupported in new[] { "max_output_tokens", "max_tokens", "temperature", "top_p", "metadata", "stop", "stop_sequences", "tools" })
        {
            Assert.False(responses.ContainsKey(unsupported), unsupported);
        }
        Assert.Equal("""[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}]""", responses["input"]!.ToJsonString());
    }

    [Fact]
    public void Tool_use_and_tool_result_round_trip_by_call_id_in_turn_order()
    {
        var responses = Translate("""
            {"system":"Be brief.","messages":[
              {"role":"user","content":"List src."},
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"hidden","signature":""},
                {"type":"text","text":"Listing."},
                {"type":"tool_use","id":"call_abc123","name":"list_files","input":{"path":"src"}},
                {"type":"tool_use","id":"toolu_2","name":"read_file","input":{"path":"a"}}]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"call_abc123","content":"a.txt\nb.txt"},
                {"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":[{"type":"text","text":"denied"}]},
                {"type":"text","text":"Continue."}]}]}
            """);

        var input = responses["input"]!.AsArray();
        Assert.Equal(["message", "message", "function_call", "function_call", "function_call_output", "function_call_output", "message"],
            input.Select(i => i!["type"]!.GetValue<string>()));
        Assert.Equal("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Listing."}]}""", input[1]!.ToJsonString());
        Assert.Equal("call_abc123", input[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("list_files", input[2]!["name"]!.GetValue<string>());
        Assert.Equal("""{"path":"src"}""", input[2]!["arguments"]!.GetValue<string>());
        Assert.Equal("toolu_2", input[3]!["call_id"]!.GetValue<string>());
        Assert.Equal("call_abc123", input[4]!["call_id"]!.GetValue<string>());
        Assert.Equal("a.txt\nb.txt", input[4]!["output"]!.GetValue<string>());
        Assert.Equal("toolu_2", input[5]!["call_id"]!.GetValue<string>());
        Assert.Equal("Error: denied", input[5]!["output"]!.GetValue<string>());
        Assert.Equal("Continue.", input[6]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("hidden", responses.ToJsonString());
    }

    [Fact]
    public void Images_become_input_images_and_tool_result_images_follow_the_output()
    {
        var responses = Translate("""
            {"messages":[
              {"role":"user","content":[
                {"type":"text","text":"What is this?"},
                {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}},
                {"type":"image","source":{"type":"url","url":"https://example.test/cat.jpg"}}]},
              {"role":"assistant","content":[{"type":"tool_use","id":"call_s","name":"screenshot","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_s","content":[
                {"type":"text","text":"captured"},
                {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"BBBB"}}]}]}]}
            """);

        var input = responses["input"]!.AsArray();
        var first = input[0]!["content"]!.AsArray();
        Assert.Equal("""{"type":"input_image","image_url":"data:image/png;base64,AAAA"}""", first[1]!.ToJsonString());
        Assert.Equal("""{"type":"input_image","image_url":"https://example.test/cat.jpg"}""", first[2]!.ToJsonString());
        Assert.Equal("function_call_output", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("captured", input[2]!["output"]!.GetValue<string>());
        Assert.Equal("""{"type":"message","role":"user","content":[{"type":"input_image","image_url":"data:image/jpeg;base64,BBBB"}]}""", input[3]!.ToJsonString());
    }

    [Fact]
    public void Function_tools_map_to_non_strict_responses_tools_and_server_tools_are_dropped()
    {
        var responses = Translate("""
            {"messages":[{"role":"user","content":"x"}],"tools":[
              {"name":"read_file","description":"Read a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}},
              {"type":"web_search_20250305","name":"web_search"}]}
            """);

        var tool = Assert.Single(responses["tools"]!.AsArray())!;
        Assert.Equal("""{"type":"function","name":"read_file","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}}},"strict":false}""", tool.ToJsonString());
        Assert.Equal("auto", responses["tool_choice"]!.GetValue<string>());
        Assert.True(responses["parallel_tool_calls"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("""{"type":"auto"}""", "\"auto\"", true)]
    [InlineData("""{"type":"any"}""", "\"required\"", true)]
    [InlineData("""{"type":"none"}""", "\"none\"", true)]
    [InlineData("""{"type":"tool","name":"read_file","disable_parallel_tool_use":true}""", """{"type":"function","name":"read_file"}""", false)]
    public void Tool_choice_maps_onto_the_responses_shape(string choice, string expected, bool parallel)
    {
        var responses = Translate($$$"""
            {"messages":[{"role":"user","content":"x"}],"tool_choice":{{{choice}}},
             "tools":[{"name":"read_file","input_schema":{"type":"object"}}]}
            """);

        Assert.Equal(expected, responses["tool_choice"]!.ToJsonString());
        Assert.Equal(parallel, responses["parallel_tool_calls"]!.GetValue<bool>());
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleRouter.Codex;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

/// <summary>The normalized effort read from a client request and the level each target is sent.</summary>
public class EffortTests
{
    private static JsonObject Request(string fields) =>
        JsonNode.Parse($$"""{"model":"m","max_tokens":64,"messages":[{"role":"user","content":"hi"}]{{fields}}}""")!.AsObject();

    [Theory]
    [InlineData("""
        ,"output_config":{"effort":"high"}
        """, "high")]
    [InlineData("""
        ,"output_config":{"effort":"XHIGH"}
        """, "xhigh")]
    [InlineData("""
        ,"output_config":{"effort":"low"},"thinking":{"type":"enabled","budget_tokens":60000}
        """, "low")]
    [InlineData("""
        ,"output_config":{"effort":"extreme"},"thinking":{"type":"enabled","budget_tokens":1024}
        """, "low")]
    [InlineData("""
        ,"thinking":{"type":"enabled","budget_tokens":4095}
        """, "low")]
    [InlineData("""
        ,"thinking":{"type":"enabled","budget_tokens":4096}
        """, "medium")]
    [InlineData("""
        ,"thinking":{"type":"enabled","budget_tokens":16383}
        """, "medium")]
    [InlineData("""
        ,"thinking":{"type":"enabled","budget_tokens":16384}
        """, "high")]
    [InlineData("""
        ,"thinking":{"type":"enabled","budget_tokens":32768}
        """, "xhigh")]
    [InlineData("""
        ,"thinking":{"type":"enabled","budget_tokens":65536}
        """, "max")]
    [InlineData("""
        ,"thinking":{"type":"adaptive"},"output_config":{"effort":"medium"}
        """, "medium")]
    [InlineData("""
        ,"thinking":{"type":"adaptive"}
        """, null)]
    [InlineData("""
        ,"thinking":{"type":"disabled"}
        """, null)]
    [InlineData("", null)]
    [InlineData("""
        ,"output_config":{"effort":"3"}
        """, null)]
    public void Effort_comes_from_output_config_else_the_thinking_budget(string fields, string? expected)
    {
        var effort = Effort.Read(Request(fields));
        Assert.Equal(expected, effort is { } e ? Effort.Name(e) : null);
    }

    private static readonly CatalogModel CodexDefault = new() { Id = "gpt-6.1-sol", Provider = ProviderKind.Codex, UpstreamModelId = "gpt-6.1-sol" };

    private static readonly CatalogModel CodexWithMax = new()
    {
        Id = "gpt-6-astra",
        Provider = ProviderKind.Codex,
        UpstreamModelId = "gpt-6-astra",
        Effort = Mapping("""{"levels":["minimal","low","medium","high","xhigh","max"]}"""),
    };

    private static EffortMapping Mapping(string json) => JsonSerializer.Deserialize<EffortMapping>(json, Effort.Json)!;

    private static string? CodexEffort(CatalogModel model, string effort) =>
        ResponsesRequestTranslator.Translate(Request($$""","output_config":{"effort":"{{effort}}"}"""), model, "k")["reasoning"]?["effort"]?.GetValue<string>();

    [Theory]
    [InlineData("none", "minimal")]
    [InlineData("minimal", "minimal")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "xhigh")]
    public void Codex_models_get_the_nearest_default_level(string requested, string sent) =>
        Assert.Equal(sent, CodexEffort(CodexDefault, requested));

    [Fact]
    public void A_codex_model_that_lists_max_gets_max() => Assert.Equal("max", CodexEffort(CodexWithMax, "max"));

    [Fact]
    public void Codex_keeps_its_reasoning_summary_and_gets_no_effort_without_one()
    {
        var reasoning = ResponsesRequestTranslator.Translate(Request(""), CodexDefault, "k")["reasoning"]!;
        Assert.Equal("""{"summary":"auto"}""", reasoning.ToJsonString());

        var withEffort = ResponsesRequestTranslator.Translate(Request("""
            ,"output_config":{"effort":"high"}
            """), CodexDefault, "k")["reasoning"]!;
        Assert.Equal("""{"summary":"auto","effort":"high"}""", withEffort.ToJsonString());
    }

    private static CatalogModel Local(string? effort) => new()
    {
        Id = "local-a",
        Provider = ProviderKind.Local,
        UpstreamModelId = "local-a",
        Effort = effort is null ? null : Mapping(effort),
    };

    private const string ReasoningEffort = """{"mode":"reasoning_effort","levels":["low","medium","high"]}""";

    private const string Kwargs = """
        {"mode":"chat_template_kwargs","levels":["low","high"],
         "kwargs":{"low":{"enable_thinking":false},"high":{"enable_thinking":true}}}
        """;

    private static JsonObject LocalChat(string? mapping, string effort) =>
        ChatRequestTranslator.Translate(Request($$""","output_config":{"effort":"{{effort}}"}"""), Local(mapping), 0);

    [Theory]
    [InlineData("minimal", "low")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "high")]
    [InlineData("max", "high")]
    public void A_reasoning_effort_local_model_gets_the_nearest_listed_level(string requested, string sent)
    {
        var chat = LocalChat(ReasoningEffort, requested);
        Assert.Equal(sent, chat["reasoning_effort"]!.GetValue<string>());
        Assert.False(chat.ContainsKey("chat_template_kwargs"));
    }

    [Theory]
    [InlineData("none", """{"enable_thinking":false}""")]
    [InlineData("low", """{"enable_thinking":false}""")]
    // Equally far from low and high: the lower level wins.
    [InlineData("medium", """{"enable_thinking":false}""")]
    [InlineData("high", """{"enable_thinking":true}""")]
    [InlineData("max", """{"enable_thinking":true}""")]
    public void A_chat_template_kwargs_local_model_gets_the_kwargs_of_the_nearest_listed_level(string requested, string kwargs)
    {
        var chat = LocalChat(Kwargs, requested);
        Assert.Equal(kwargs, chat["chat_template_kwargs"]!.ToJsonString());
        Assert.False(chat.ContainsKey("reasoning_effort"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"mode":"none","levels":["low","high"]}""")]
    [InlineData("""{"mode":"reasoning_effort"}""")]
    public void A_local_model_without_an_effort_mapping_gets_no_reasoning_parameters(string? mapping)
    {
        var chat = LocalChat(mapping, "high");
        Assert.False(chat.ContainsKey("reasoning_effort"));
        Assert.False(chat.ContainsKey("chat_template_kwargs"));
    }

    [Fact]
    public void A_local_model_with_a_mapping_gets_no_reasoning_parameters_without_a_client_effort()
    {
        var chat = ChatRequestTranslator.Translate(Request(""), Local(ReasoningEffort), 0);
        Assert.False(chat.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void A_mapping_round_trips_through_its_seed_shape()
    {
        var mapping = Mapping(Kwargs);
        Assert.Equal(EffortMode.ChatTemplateKwargs, mapping.Mode);
        Assert.Equal([EffortLevel.Low, EffortLevel.High], mapping.Levels);
        var json = JsonSerializer.Serialize(mapping, Effort.Json);
        Assert.Contains("\"chat_template_kwargs\"", json);
        Assert.Equal(json, JsonSerializer.Serialize(Mapping(json), Effort.Json));
        Assert.Throws<JsonException>(() => Mapping("""{"levels":["huge"]}"""));
    }
}

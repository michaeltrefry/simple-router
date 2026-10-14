using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SimpleRouter.Local;

/// <summary>
/// Translates a Chat Completions response into an Anthropic message. Upstream reasoning (<c>reasoning_content</c>,
/// <c>reasoning</c>, a leading <c>&lt;think&gt;</c> block) is omitted rather than surfaced as thinking blocks.
/// </summary>
public static partial class ChatResponseTranslator
{
    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidToolUseId();

    public static JsonObject Translate(JsonObject chat, string model, int estimatedInputTokens)
    {
        var choice = (chat["choices"] as JsonArray)?.FirstOrDefault();
        var message = choice?["message"];
        var content = new JsonArray();

        var filter = new ThinkTagFilter();
        var text = filter.Feed(ChatRequestTranslator.Str(message?["content"]) ?? "") + filter.Flush();
        if (text.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        foreach (var call in message?["tool_calls"] as JsonArray ?? [])
        {
            var name = ChatRequestTranslator.Str(call?["function"]?["name"]);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            content.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = ToolUseId(ChatRequestTranslator.Str(call!["id"])),
                ["name"] = name,
                ["input"] = JsonNode.Parse(ToolInput(ChatRequestTranslator.Str(call["function"]!["arguments"]))),
            });
        }

        return new JsonObject
        {
            ["id"] = MessageId(),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = StopReason(ChatRequestTranslator.Str(choice?["finish_reason"]), content.Any(b => b!["type"]!.GetValue<string>() == "tool_use")),
            ["stop_sequence"] = null,
            ["usage"] = Usage(chat["usage"], estimatedInputTokens),
        };
    }

    /// <summary>Truncation wins; otherwise any emitted tool call means the client must run tools, whatever the upstream said.</summary>
    public static string StopReason(string? finishReason, bool hasToolUse) => finishReason switch
    {
        "length" => "max_tokens",
        _ when hasToolUse => "tool_use",
        "content_filter" => "refusal",
        _ => "end_turn",
    };

    public static JsonObject Usage(JsonNode? usage, int estimatedInputTokens) => new()
    {
        ["input_tokens"] = Int(usage?["prompt_tokens"]) ?? estimatedInputTokens,
        ["output_tokens"] = Int(usage?["completion_tokens"]) ?? 0,
    };

    /// <summary>Anthropic clients require ids matching <c>^[a-zA-Z0-9_-]+$</c>; the replacement is reused on the next turn.</summary>
    public static string ToolUseId(string? id) =>
        id is not null && ValidToolUseId().IsMatch(id) ? id : "toolu_" + Guid.NewGuid().ToString("N")[..24];

    /// <summary>The arguments as a JSON object; malformed or non-object arguments degrade to <c>{}</c> so the turn still parses.</summary>
    public static string ToolInput(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return "{}";
        }
        try
        {
            return JsonNode.Parse(arguments) is JsonObject ? arguments.Trim() : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    public static string MessageId() => "msg_" + Guid.NewGuid().ToString("N")[..24];

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}

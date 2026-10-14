using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleRouter.Claude;

/// <summary>
/// Subscription-authenticated /v1/messages calls must lead with the Claude Code system
/// identity; otherwise Anthropic answers 429 regardless of remaining quota.
/// </summary>
public static class ClaudeIdentity
{
    public const string Text = "You are Claude Code, Anthropic's official CLI for Claude.";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Returns <paramref name="body"/> with the identity as its first system block. Non-object bodies are returned unchanged.</summary>
    public static byte[] Ensure(byte[] body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }
        if (root is not JsonObject request)
        {
            return body;
        }

        var blocks = new JsonArray();
        switch (request["system"])
        {
            case JsonArray existing when existing.Count > 0:
                if (IdentifiesAsClaudeCode(existing[0]?["text"]))
                {
                    return body;
                }
                blocks.Add(TextBlock(Text));
                foreach (var block in existing)
                {
                    blocks.Add(block?.DeepClone());
                }
                break;
            case JsonValue value when value.TryGetValue<string>(out var system) && system.Length > 0:
                if (IdentifiesAsClaudeCode(value))
                {
                    return body;
                }
                blocks.Add(TextBlock(Text));
                blocks.Add(TextBlock(system));
                break;
            default:
                blocks.Add(TextBlock(Text));
                break;
        }

        request["system"] = blocks;
        return JsonSerializer.SerializeToUtf8Bytes(request, Relaxed);
    }

    private static bool IdentifiesAsClaudeCode(JsonNode? text) =>
        text is JsonValue v && v.TryGetValue<string>(out var s) && s.TrimStart().StartsWith(Text, StringComparison.Ordinal);

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
}

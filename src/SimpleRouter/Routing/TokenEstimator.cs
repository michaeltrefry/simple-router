using System.Text.Json.Nodes;

namespace SimpleRouter.Routing;

/// <summary>Rough input-token estimate (about four characters per token) used for context-window fit and local token counts.</summary>
public static class TokenEstimator
{
    private const int CharsPerToken = 4;

    // Base64 length says nothing about the tokens an image costs; count a typical image instead.
    private const int ImageChars = 1600 * CharsPerToken;

    public static int Estimate(JsonObject request)
    {
        long chars = Count(request["system"]) + Count(request["messages"]) + Count(request["tools"]);
        return (int)Math.Min(int.MaxValue, Math.Max(1, (chars + CharsPerToken - 1) / CharsPerToken));
    }

    private static long Count(JsonNode? node) => node switch
    {
        JsonObject { } obj when obj["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "base64" => ImageChars,
        JsonObject obj => obj.Sum(p => p.Key.Length + Count(p.Value)),
        JsonArray array => array.Sum(Count),
        JsonValue value when value.TryGetValue<string>(out var s) => s.Length,
        null => 0,
        _ => node.ToJsonString().Length,
    };
}

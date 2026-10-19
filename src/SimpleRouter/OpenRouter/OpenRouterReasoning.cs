using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleRouter.Local;

namespace SimpleRouter.OpenRouter;

/// <summary>Opaque signatures carry complete OpenRouter reasoning entries through the Messages wire format.</summary>
public static class OpenRouterReasoning
{
    public const string Prefix = "openrouter:";

    /// <summary>Merge explicitly identified fragments, retaining the first-seen order and all opaque metadata.</summary>
    public static void Accumulate(JsonArray collected, JsonArray fragments)
    {
        foreach (var fragment in fragments.OfType<JsonObject>())
        {
            var index = fragment["index"];
            var id = ChatRequestTranslator.Str(fragment["id"]);
            var type = ChatRequestTranslator.Str(fragment["type"]);
            var existing = collected.OfType<JsonObject>().FirstOrDefault(entry =>
                (index is not null ? JsonNode.DeepEquals(index, entry["index"]) : id is not null && id == ChatRequestTranslator.Str(entry["id"]))
                && (type is null || entry["type"] is null || type == ChatRequestTranslator.Str(entry["type"]))
                && (id is null || entry["id"] is null || id == ChatRequestTranslator.Str(entry["id"])));
            if (existing is null)
            {
                collected.Add(fragment.DeepClone());
                continue;
            }
            foreach (var (name, value) in fragment)
            {
                if (name is "text" or "summary" or "data" or "signature"
                    && ChatRequestTranslator.Str(value) is { } part && ChatRequestTranslator.Str(existing[name]) is { } previous)
                    existing[name] = previous + part;
                else if (value is not null) existing[name] = value.DeepClone();
            }
        }
    }

    public static JsonArray Blocks(IEnumerable<JsonNode?> details)
    {
        var blocks = new JsonArray();
        foreach (var detail in details.OfType<JsonObject>())
        {
            var encoded = Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(detail.ToJsonString()));
            blocks.Add(ChatRequestTranslator.Str(detail["type"]) == "reasoning.encrypted"
                ? new JsonObject { ["type"] = "redacted_thinking", ["data"] = encoded }
                : new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = ChatRequestTranslator.Str(detail["text"]) ?? ChatRequestTranslator.Str(detail["summary"]) ?? "",
                    ["signature"] = encoded,
                });
        }
        return blocks;
    }

    public static void Replay(JsonObject request, JsonObject chat)
    {
        // Translation can expand a user turn into several tool messages, but assistant turns remain one-for-one.
        var originals = (request["messages"] as JsonArray ?? []).Where(m => ChatRequestTranslator.Str(m?["role"]) == "assistant").ToArray();
        var translated = (chat["messages"] as JsonArray ?? []).OfType<JsonObject>().Where(m => ChatRequestTranslator.Str(m["role"]) == "assistant").ToArray();
        for (var i = 0; i < originals.Length; i++)
        {
            var message = translated[i];
            if (message.Remove("reasoning_content", out var reasoning)) message["reasoning"] = reasoning;
            var details = new JsonArray();
            foreach (var block in originals[i]?["content"] as JsonArray ?? [])
            {
                var encoded = ChatRequestTranslator.Str(block?["signature"]) ?? ChatRequestTranslator.Str(block?["data"]);
                if (encoded?.StartsWith(Prefix, StringComparison.Ordinal) != true) continue;
                try
                {
                    if (JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded[Prefix.Length..]))) is JsonObject detail)
                        details.Add(detail);
                }
                catch (Exception e) when (e is FormatException or JsonException) { }
            }
            if (details.Count > 0) message["reasoning_details"] = details;
        }
    }
}

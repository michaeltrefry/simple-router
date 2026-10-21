using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SimpleRouter.SystemOne;

/// <summary>The question types and media a SystemOne model accepts; a request using anything else is refused before the upstream is called.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<DecisionSupport>))]
public enum DecisionSupport
{
    None = 0,
    Choice = 1,
    Noul = 2,
    Score = 4,
    Box = 8,
    Images = 16,
    Videos = 32,
    All = Choice | Noul | Score | Box | Images | Videos,
}

public static class DecisionSupports
{
    public static readonly (string Name, DecisionSupport Flag)[] QuestionTypes =
        [("choice", DecisionSupport.Choice), ("noul", DecisionSupport.Noul), ("score", DecisionSupport.Score), ("box", DecisionSupport.Box)];

    public static IEnumerable<string> QuestionTypeNames(this DecisionSupport support) =>
        QuestionTypes.Where(q => support.HasFlag(q.Flag)).Select(q => q.Name);

    /// <summary>
    /// What <paramref name="request"/> uses that <paramref name="support"/> lacks, or null. Media is the router's top-level
    /// <c>images</c>/<c>videos</c> arrays or an <c>image_url</c>/<c>video_url</c> part anywhere in the body, where OpenRouter's
    /// decision models read them. A question type the router does not know is left to the upstream.
    /// </summary>
    public static string? Unsupported(this DecisionSupport support, JsonObject request)
    {
        if (request["questions"] is JsonObject questions)
        {
            foreach (var (name, question) in questions)
            {
                var type = question?["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
                if (QuestionTypes.FirstOrDefault(q => q.Name == type) is { Name: not null } known && !support.HasFlag(known.Flag))
                    return $"{type} questions (question '{name}')";
            }
        }
        var parts = Parts(request).ToHashSet();
        if (!support.HasFlag(DecisionSupport.Images) && (request["images"] is JsonArray { Count: > 0 } || parts.Contains("image_url")))
            return "images";
        if (!support.HasFlag(DecisionSupport.Videos) && (request["videos"] is JsonArray { Count: > 0 } || parts.Contains("video_url")))
            return "videos";
        return null;
    }

    private static IEnumerable<string> Parts(JsonNode? node)
    {
        var pending = new Stack<JsonNode?>([node]);
        while (pending.TryPop(out var current))
        {
            if (current is JsonObject obj)
            {
                if (obj["type"] is JsonValue v && v.TryGetValue<string>(out var type) && type is "image_url" or "video_url")
                    yield return type;
                foreach (var (_, child) in obj) pending.Push(child);
            }
            else if (current is JsonArray array)
            {
                foreach (var child in array) pending.Push(child);
            }
        }
    }
}

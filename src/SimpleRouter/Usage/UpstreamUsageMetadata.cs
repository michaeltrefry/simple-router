using System.Text.Json.Nodes;

namespace SimpleRouter.Usage;

/// <summary>Provider accounting captured before translation; independent of the configured routing entry.</summary>
public sealed record UpstreamUsageMetadata(string? ResolvedModelId, string? GenerationId, decimal? CostUsd)
{
    private static readonly object Key = new();

    public static UpstreamUsageMetadata? Get(HttpContext context) => context.Items.TryGetValue(Key, out var value)
        ? value as UpstreamUsageMetadata : null;

    public static void Reset(HttpContext context) => context.Items.Remove(Key);

    public static void Capture(HttpContext context, JsonObject chunk)
    {
        var old = Get(context);
        var cost = chunk["usage"] is JsonObject usage && usage["cost"] is JsonValue value
            && value.TryGetValue<decimal>(out var amount) && amount >= 0 ? amount : (decimal?)null;
        context.Items[Key] = new UpstreamUsageMetadata(Text(chunk["model"]) ?? old?.ResolvedModelId,
            Text(chunk["id"]) ?? old?.GenerationId, cost ?? old?.CostUsd);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text) && text.Length <= 512 ? text : null;

    public static long ToMicros(decimal dollars)
    {
        try { return (long)Math.Round(dollars * 1_000_000m, MidpointRounding.AwayFromZero); }
        catch (OverflowException) { return long.MaxValue; }
    }
}

using System.Text.Json.Nodes;
using SimpleRouter.Data;

namespace SimpleRouter.Usage;

/// <summary>
/// Token counts of one response, in Anthropic's usage terms (input excludes cache reads and writes). The cache writes
/// with a one-hour lifetime are a part of <see cref="CacheWriteTokens"/>, priced at their own (higher) rate.
/// </summary>
public sealed record TokenUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long CacheWrite1hTokens = 0)
{
    public static readonly TokenUsage None = new(0, 0, 0, 0);

    /// <summary>
    /// Applies the fields an Anthropic <c>usage</c> object sets over this one. A stream reports usage on
    /// <c>message_start</c> and (cumulatively) on <c>message_delta</c>, so the later report wins field by field.
    /// </summary>
    public TokenUsage Merge(JsonNode? usage) => usage is not JsonObject u
        ? this
        : new TokenUsage(
            Count(u["input_tokens"]) ?? InputTokens,
            Count(u["output_tokens"]) ?? OutputTokens,
            Count(u["cache_read_input_tokens"]) ?? CacheReadTokens,
            Count(u["cache_creation_input_tokens"]) ?? CacheWriteTokens,
            Count(u["cache_creation"]?["ephemeral_1h_input_tokens"]) ?? CacheWrite1hTokens);

    /// <summary>The cost in micro-USD: tokens times USD per million tokens is exactly micro-USD.</summary>
    public long CostUsdMicros(ModelPrice? price) => price is null
        ? 0
        : (long)Math.Round(InputTokens * price.InputPerMTok + OutputTokens * price.OutputPerMTok
            + CacheReadTokens * price.CacheReadPerMTok + (CacheWriteTokens - CacheWrite1hTokens) * price.CacheWritePerMTok
            + CacheWrite1hTokens * price.CacheWrite1hPerMTok, MidpointRounding.AwayFromZero);

    private static long? Count(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var n) && n >= 0 ? n : null;
}

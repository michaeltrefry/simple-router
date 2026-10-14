using SimpleRouter.Routing;

namespace SimpleRouter.Data;

/// <summary>A model's list price in USD per million tokens; a model without a row is priced at $0.</summary>
public sealed class ModelPrice
{
    public required string ModelId { get; set; }
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
    public decimal CacheReadPerMTok { get; set; }
    public decimal CacheWritePerMTok { get; set; }
    public decimal CacheWrite1hPerMTok { get; set; }
}

/// <summary>One served Messages request: who asked, which model answered, its tokens and their priced cost.</summary>
public sealed class RequestUsage
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string? ClientSessionId { get; set; }
    public required string ModelId { get; set; }
    public ProviderKind Provider { get; set; }
    public ModelTier Tier { get; set; }
    public int Status { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    /// <summary>The part of <see cref="CacheWriteTokens"/> written with a one-hour lifetime.</summary>
    public long CacheWrite1hTokens { get; set; }
    public long CostUsdMicros { get; set; }
    public long LatencyMs { get; set; }
    public string? Effort { get; set; }
}

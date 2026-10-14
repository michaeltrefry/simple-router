using System.Globalization;
using System.Net.Http.Headers;

namespace SimpleRouter.Routing;

/// <summary>Whether a provider's limit headers report an exhausted window, and when the latest exhausted window resets.</summary>
public sealed record LimitReading(bool Exhausted, DateTimeOffset? ResetAt)
{
    public static readonly LimitReading Open = new(false, null);
}

/// <summary>Reads subscription usage-limit headers from Claude and Codex responses.</summary>
public static class LimitHeaders
{
    private static readonly string[] AnthropicPrefixes =
        ["anthropic-ratelimit-unified-", "anthropic-ratelimit-unified-5h-", "anthropic-ratelimit-unified-7d-"];

    /// <summary>
    /// Anthropic unified limits: a window is exhausted when its <c>status</c> is <c>rejected</c> or its
    /// <c>utilization</c> (a 0-1 fraction) reaches 1; it resets at its <c>reset</c> (unix seconds or RFC 3339).
    /// </summary>
    public static LimitReading Anthropic(HttpResponseHeaders headers)
    {
        var reading = LimitReading.Open;
        foreach (var prefix in AnthropicPrefixes)
        {
            var exhausted = string.Equals(Get(headers, prefix + "status"), "rejected", StringComparison.OrdinalIgnoreCase)
                || Number(Get(headers, prefix + "utilization")) >= 1;
            if (exhausted)
            {
                reading = Merge(reading, Instant(Get(headers, prefix + "reset")));
            }
        }
        return reading;
    }

    /// <summary>
    /// Codex limits: the <c>primary</c> (rolling) or <c>secondary</c> (weekly) window is exhausted at a
    /// <c>used-percent</c> of 100 or more; it resets at <c>reset-at</c> (unix seconds) or after <c>reset-after-seconds</c>.
    /// </summary>
    public static LimitReading Codex(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var reading = LimitReading.Open;
        foreach (var window in new[] { "primary", "secondary" })
        {
            var prefix = $"x-codex-{window}-";
            if (Number(Get(headers, prefix + "used-percent")) >= 100)
            {
                reading = Merge(reading, Instant(Get(headers, prefix + "reset-at")) ?? After(now, Number(Get(headers, prefix + "reset-after-seconds"))));
            }
        }
        return reading;
    }

    /// <summary>The instant a <c>Retry-After</c> header names, as seconds or an HTTP date.</summary>
    public static DateTimeOffset? RetryAfter(HttpResponseHeaders headers, DateTimeOffset now) => headers.RetryAfter switch
    {
        { Delta: { } delta } => now + delta,
        { Date: { } date } => date,
        _ => null,
    };

    /// <summary>A reset instant given as unix seconds or an RFC 3339 timestamp.</summary>
    public static DateTimeOffset? Instant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null;
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) ? instant : null;
    }

    public static DateTimeOffset? After(DateTimeOffset now, double? seconds) => seconds > 0 ? now.AddSeconds(seconds.Value) : null;

    private static LimitReading Merge(LimitReading reading, DateTimeOffset? reset)
    {
        // An account is limited until every exhausted window has reset; an unknown reset leaves the known ones standing.
        var latest = reset is null || reading.ResetAt > reset ? reading.ResetAt : reset;
        return new LimitReading(true, latest);
    }

    private static string? Get(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}

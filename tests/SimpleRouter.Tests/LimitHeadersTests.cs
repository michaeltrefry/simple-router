using System.Net;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

public class LimitHeadersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage With(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    [Fact]
    public void Two_exhausted_codex_windows_hold_until_the_later_reset()
    {
        using var response = With(
            ("x-codex-primary-used-percent", "100"), ("x-codex-primary-reset-after-seconds", "600"),
            ("x-codex-secondary-used-percent", "101.5"), ("x-codex-secondary-reset-at", Now.AddDays(3).ToUnixTimeSeconds().ToString()));

        Assert.Equal(new LimitReading(true, Now.AddDays(3)), LimitHeaders.Codex(response.Headers, Now));
    }

    [Fact]
    public void Windows_below_their_limit_read_as_open()
    {
        using var response = With(
            ("x-codex-primary-used-percent", "99.9"), ("x-codex-primary-reset-after-seconds", "600"),
            ("anthropic-ratelimit-unified-status", "allowed_warning"), ("anthropic-ratelimit-unified-5h-utilization", "0.98"),
            ("anthropic-ratelimit-unified-reset", "1760000000"));

        Assert.Equal(LimitReading.Open, LimitHeaders.Codex(response.Headers, Now));
        Assert.Equal(LimitReading.Open, LimitHeaders.Anthropic(response.Headers));
    }

    [Fact]
    public void An_exhausted_anthropic_window_without_a_reset_keeps_a_sibling_windows_known_reset()
    {
        using var response = With(
            ("anthropic-ratelimit-unified-5h-status", "rejected"), ("anthropic-ratelimit-unified-5h-reset", "2026-10-14T15:00:00Z"),
            ("anthropic-ratelimit-unified-7d-status", "rejected"));

        Assert.Equal(new LimitReading(true, Now.AddHours(3)), LimitHeaders.Anthropic(response.Headers));
    }
}

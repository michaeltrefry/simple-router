using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

public class TierResolverTests
{
    private static readonly TierRules SeedRules = new(
        [("*fable*", ModelTier.Max), ("*opus*", ModelTier.High), ("*sonnet*", ModelTier.Mid), ("*haiku*", ModelTier.Low)],
        ModelTier.High);

    [Theory]
    [InlineData("max", "claude-haiku-5-5", ModelTier.Max)]
    [InlineData("high", null, ModelTier.High)]
    [InlineData("MID", "claude-opus-5-5", ModelTier.Mid)]
    [InlineData(" Low ", "default", ModelTier.Low)]
    [InlineData(null, "claude-fable-5-1", ModelTier.Max)]
    [InlineData(null, "claude-opus-5-5", ModelTier.High)]
    [InlineData(null, "claude-sonnet-5-5", ModelTier.Mid)]
    [InlineData(null, "Claude-Haiku-5-5", ModelTier.Low)]
    [InlineData(null, "default", ModelTier.High)]
    [InlineData(null, null, ModelTier.High)]
    [InlineData("", "claude-sonnet-5-5", ModelTier.Mid)]
    public void Header_then_alias_then_default(string? header, string? model, ModelTier expected)
    {
        Assert.True(TierResolver.TryResolve(header, model, SeedRules, out var tier));
        Assert.Equal(expected, tier);
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("1")]
    [InlineData("opus")]
    public void An_invalid_header_does_not_resolve(string header)
    {
        Assert.False(TierResolver.TryResolve(header, "claude-sonnet-5-5", SeedRules, out _));
    }

    [Fact]
    public void Default_tier_is_configurable_and_earlier_aliases_win()
    {
        var rules = new TierRules([("claude-sonnet-*-fast", ModelTier.Low), ("*sonnet*", ModelTier.Mid)], ModelTier.Low);

        Assert.True(TierResolver.TryResolve(null, "claude-sonnet-5-5-fast", rules, out var aliased));
        Assert.True(TierResolver.TryResolve(null, "claude-sonnet-5-5", rules, out var second));
        Assert.True(TierResolver.TryResolve(null, "default", rules, out var fallback));

        Assert.Equal(ModelTier.Low, aliased);
        Assert.Equal(ModelTier.Mid, second);
        Assert.Equal(ModelTier.Low, fallback);
    }

    [Fact]
    public async Task An_alias_that_backtracks_too_long_is_skipped_as_no_match()
    {
        var rules = new TierRules([("*a*a*a*a*a*a*a*a*a*a*a*b", ModelTier.Max), ("*a*", ModelTier.Low)], ModelTier.High);
        var model = new string('a', 2_000);

        var resolve = Task.Run(() => (TierResolver.TryResolve(null, model, rules, out var tier), tier));
        Assert.Same(resolve, await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.Equal((true, ModelTier.Low), await resolve);
    }
}

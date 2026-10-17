using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SimpleRouter.Data;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

public class LocalConversationsTests
{
    private static readonly CatalogModel SingleSlot = new() { Id = "mimo", UpstreamModelId = "mimo", Provider = ProviderKind.Local, MaxConversations = 1 };

    private static (LocalConversations Conversations, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new RouterOptions();
        options.Local.ConversationIdle = TimeSpan.FromMinutes(5);
        return (new LocalConversations(Options.Create(options), time), time);
    }

    [Fact]
    public void A_held_model_refuses_other_conversations_until_its_holder_is_idle_past_the_window()
    {
        var (conversations, time) = Create();

        conversations.TryEnter(SingleSlot, "a")!.Dispose();
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.True(conversations.Admits(SingleSlot, "a"));
        Assert.False(conversations.Admits(SingleSlot, "b"));
        Assert.Null(conversations.TryEnter(SingleSlot, "b"));

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(conversations.Admits(SingleSlot, "b"));
        Assert.NotNull(conversations.TryEnter(SingleSlot, "b"));
        Assert.False(conversations.Admits(SingleSlot, "a"));
    }

    [Fact]
    public void A_request_in_flight_holds_its_place_however_long_it_runs()
    {
        var (conversations, time) = Create();

        var hold = conversations.TryEnter(SingleSlot, "a")!;
        time.Advance(TimeSpan.FromHours(1));

        Assert.False(conversations.Admits(SingleSlot, "b"));
        hold.Dispose();
        hold.Dispose();
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(conversations.Admits(SingleSlot, "b"));
    }

    [Fact]
    public void A_limit_counts_conversations_and_a_forced_entry_exceeds_it()
    {
        var (conversations, _) = Create();
        var twoSlots = new CatalogModel { Id = "strata", UpstreamModelId = "strata", Provider = ProviderKind.Local, MaxConversations = 2 };

        Assert.NotNull(conversations.TryEnter(twoSlots, "a"));
        Assert.NotNull(conversations.TryEnter(twoSlots, "b"));
        Assert.Null(conversations.TryEnter(twoSlots, "c"));
        Assert.NotNull(conversations.TryEnter(twoSlots, "c", force: true));
        Assert.True(conversations.Admits(twoSlots, "c"));
    }

    [Fact]
    public void A_model_without_a_limit_admits_everyone()
    {
        var (conversations, _) = Create();
        var unlimited = new CatalogModel { Id = "vllm", UpstreamModelId = "vllm", Provider = ProviderKind.Local };

        conversations.TryEnter(unlimited, "a");

        Assert.True(conversations.Admits(unlimited, "b"));
        Assert.NotNull(conversations.TryEnter(unlimited, "b"));
    }
}

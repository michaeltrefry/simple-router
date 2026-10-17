using Microsoft.Extensions.Options;
using SimpleRouter.Data;

namespace SimpleRouter.Routing;

/// <summary>
/// Which conversations (session keys) each local model with <see cref="CatalogModel.MaxConversations"/> is serving. A
/// single-slot server keeps one conversation's cache and rereads the whole prompt whenever another runs on it, so while
/// a model holds its limit of active conversations, others are steered to the tier's next model. A conversation is
/// active while a request is in flight and for <see cref="LocalOptions.ConversationIdle"/> after its last one ends.
/// In memory only: after a restart the first conversations to arrive hold the models.
/// </summary>
public sealed class LocalConversations(IOptions<RouterOptions> options, TimeProvider time)
{
    private sealed class Holder
    {
        public int InFlight;
        public DateTimeOffset LastActive;
    }

    private readonly Dictionary<string, Dictionary<string, Holder>> _models = [];

    /// <summary>True unless <paramref name="model"/> is at its limit with conversations other than <paramref name="sessionKey"/>.</summary>
    public bool Admits(CatalogModel model, string sessionKey)
    {
        if (model.MaxConversations is not { } max)
        {
            return true;
        }
        lock (_models)
        {
            var holders = Active(model.Id);
            return holders.ContainsKey(sessionKey) || holders.Count < max;
        }
    }

    /// <summary>
    /// Holds <paramref name="model"/> for <paramref name="sessionKey"/> while a request is in flight when it
    /// <see cref="Admits"/> the conversation, or unconditionally when <paramref name="force"/> is set; dispose to release.
    /// Null when refused.
    /// </summary>
    public IDisposable? TryEnter(CatalogModel model, string sessionKey, bool force = false)
    {
        if (model.MaxConversations is not { } max)
        {
            return Released.Instance;
        }
        lock (_models)
        {
            var holders = Active(model.Id);
            if (!force && !holders.ContainsKey(sessionKey) && holders.Count >= max)
            {
                return null;
            }
            if (!holders.TryGetValue(sessionKey, out var holder))
            {
                holders[sessionKey] = holder = new Holder();
            }
            holder.InFlight++;
            holder.LastActive = time.GetUtcNow();
            return new Lease(this, model.Id, sessionKey);
        }
    }

    /// <summary>The model's holders, with those idle past the window removed.</summary>
    private Dictionary<string, Holder> Active(string modelId)
    {
        if (!_models.TryGetValue(modelId, out var holders))
        {
            _models[modelId] = holders = [];
        }
        var cutoff = time.GetUtcNow() - options.Value.Local.ConversationIdle;
        foreach (var (key, holder) in holders.Where(h => h.Value.InFlight == 0 && h.Value.LastActive <= cutoff).ToList())
        {
            holders.Remove(key);
        }
        return holders;
    }

    private void Exit(string modelId, string sessionKey)
    {
        lock (_models)
        {
            if (_models.TryGetValue(modelId, out var holders) && holders.TryGetValue(sessionKey, out var holder))
            {
                holder.InFlight--;
                holder.LastActive = time.GetUtcNow();
            }
        }
    }

    private sealed class Lease(LocalConversations owner, string modelId, string sessionKey) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit(modelId, sessionKey);
            }
        }
    }

    private sealed class Released : IDisposable
    {
        public static readonly Released Instance = new();

        public void Dispose()
        {
        }
    }
}

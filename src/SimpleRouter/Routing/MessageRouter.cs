using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleRouter.Api;

namespace SimpleRouter.Routing;

/// <summary>
/// Resolves the tier and session model for a Messages request and hands it to that model's upstream. A model that fails
/// before writing anything is cooled (<see cref="ModelCooldowns"/>) and the request moves to the next eligible model of
/// the same tier, re-pinning the session; each model is tried at most once. A tier left with no model, for any reason,
/// is the exhaustion 429 (<see cref="Unavailable"/>), never another tier.
/// </summary>
public sealed class MessageRouter(ModelSelector selector, IEnumerable<IModelUpstream> upstreams, ModelCooldowns cooldowns, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<RouterOptions> options, ILogger<MessageRouter> logger)
{
    public const string ModelClassHeader = "X-Weave-Model-Class";

    /// <summary>Prefix of the exhaustion 429's message; clients pause on it.</summary>
    public const string Unavailable = "model_class_unavailable";

    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <param name="countTokens">Token counting must not consume a round-robin turn or create a pin.</param>
    public async Task RouteAsync(HttpContext context, bool countTokens)
    {
        var ct = context.RequestAborted;
        var tierHeader = context.Request.Headers[TierResolver.HeaderName];
        var rules = await selector.GetTierRulesAsync(ct);
        if (await ReadObjectAsync(context.Request, ct) is not { } request)
        {
            if (TierResolver.TryResolve(tierHeader, null, rules, out var fallback))
            {
                context.Response.Headers[ModelClassHeader] = fallback.Name();
            }
            await AnthropicError.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error",
                "Request body must be a well-formed JSON object.");
            return;
        }

        var requestedModel = request["model"] is JsonValue m && m.TryGetValue<string>(out var model) ? model : null;
        if (!TierResolver.TryResolve(tierHeader, requestedModel, rules, out var tier))
        {
            await AnthropicError.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error",
                $"{TierResolver.HeaderName} must be one of: max, high, mid, low.");
            return;
        }
        context.Response.Headers[ModelClassHeader] = tier.Name();

        var session = SessionKey.Derive(context.Request.Headers[SessionKey.HeaderName], request);
        var needs = RequestNeeds.Of(request);
        if (countTokens)
        {
            if (await selector.PeekAsync(tier, session, needs, ct) is not { } peeked)
            {
                await WriteTierEmptyAsync(context, tier, needs, ct);
                return;
            }
            await UpstreamFor(peeked).SendAsync(new UpstreamCall(context, request, session, peeked, true, needs.EstimatedTokens));
            return;
        }

        var tried = new HashSet<string>();
        while (await selector.SelectAsync(tier, session, needs, ct, tried) is { } chosen)
        {
            tried.Add(chosen.Id);
            // Upstreams rewrite the body for their provider; a failover must start from the client's request.
            var call = new UpstreamCall(context, (JsonObject)request.DeepClone(), session, chosen, false, needs.EstimatedTokens);
            var result = await UpstreamFor(chosen).SendAsync(call);
            if (result.Fault is { } fault)
            {
                await cooldowns.RecordAsync(chosen, fault);
            }
            if (result.Written)
            {
                return;
            }
            logger.LogWarning("Model {model} could not serve the request ({reason}); trying the next model in tier {tier}",
                chosen.Id, result.Fault?.Kind, tier.Name());
        }
        await WriteTierEmptyAsync(context, tier, needs, ct);
    }

    private IModelUpstream UpstreamFor(Data.CatalogModel model) => upstreams.First(u => u.Provider == model.Provider);

    /// <summary>
    /// Clients pause on a 429 <c>rate_limit_error</c> and retry after <c>Retry-After</c>, so every empty tier answers so:
    /// until the earliest cooldown ends, else after the fixed limit backoff. Missing or rejected logins are named.
    /// </summary>
    private async Task WriteTierEmptyAsync(HttpContext context, ModelTier tier, RequestNeeds needs, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var reset = await selector.EarliestResetAsync(tier, needs, ct);
        var wait = reset is { } r ? r - now : options.Value.Limits.LimitCooldown;
        var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var missing = await selector.MissingLoginsAsync(tier, needs, ct);
        var hint = missing.Count == 0
            ? ""
            : " Run " + string.Join(" or ", missing.Select(p => $"'simple-router login {p.ToString().ToLowerInvariant()}'")) + ".";
        var when = reset is { } at ? $"the earliest resets at {at.UtcDateTime:u}" : $"retry in {seconds} s";
        await AnthropicError.WriteAsync(context, StatusCodes.Status429TooManyRequests, "rate_limit_error",
            $"{Unavailable}: no model in the {tier.Name()} tier can serve this request now (usage limit, login or availability); {when}.{hint}");
    }

    /// <summary>
    /// Null unless the body is a JSON object that can be read and re-serialized: duplicate keys and lone surrogate
    /// escapes are valid to the parser but throw on first access or on serialization.
    /// </summary>
    private static async Task<JsonObject?> ReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            var body = await JsonNode.ParseAsync(request.Body, documentOptions: Strict, cancellationToken: ct) as JsonObject;
            if (body is not null)
            {
                JsonSerializer.SerializeToUtf8Bytes(body, WireJson.Relaxed);
            }
            return body;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleRouter.Api;
using SimpleRouter.Claude;

namespace SimpleRouter.Routing;

/// <summary>Resolves the tier and session model for a Messages request, rewrites its <c>model</c>, and forwards it.</summary>
public sealed class MessageRouter(ModelSelector selector, ClaudeUpstream claude)
{
    public const string ModelClassHeader = "X-Weave-Model-Class";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <param name="assignPin">False for token counting, which must not consume a round-robin turn or create a pin.</param>
    public async Task RouteAsync(HttpContext context, string path, bool assignPin)
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
        var chosen = assignPin ? await selector.SelectAsync(tier, session, ct) : await selector.PeekAsync(tier, session, ct);
        if (chosen is null)
        {
            await AnthropicError.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "api_error",
                $"No enabled model is available in the {tier.Name()} tier.");
            return;
        }

        request["model"] = chosen.UpstreamModelId;
        await claude.ForwardAsync(context, path, JsonSerializer.SerializeToUtf8Bytes(request, Relaxed), injectIdentity: assignPin);
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
                JsonSerializer.SerializeToUtf8Bytes(body, Relaxed);
            }
            return body;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

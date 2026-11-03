using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SimpleRouter.Codex;
using SimpleRouter.Routing;

namespace SimpleRouter.RouterCom;

public static class RouterComRequest
{
    public static JsonObject Translate(UpstreamCall call)
    {
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.Session.Key)));
        var body = ResponsesRequestTranslator.Translate(call.Request, call.Model, cacheKey, RouterComResponses.SignaturePrefix);
        body["stream"] = call.Request["stream"]?.DeepClone() ?? JsonValue.Create(false);
        // Summary controls are model-specific on Router.com; do not force Codex's summary policy on other providers.
        if (body["reasoning"] is JsonObject reasoning)
        {
            reasoning.Remove("summary");
            if (reasoning.Count == 0) body.Remove("reasoning");
        }
        // Unlike the subscription backend, Router.com's Responses API accepts ordinary generation controls.
        body["max_output_tokens"] = call.Request["max_tokens"]?.DeepClone();
        foreach (var name in new[] { "temperature", "top_p", "metadata" })
            if (call.Request[name] is { } value) body[name] = value.DeepClone();
        var routing = call.Model.RouterCom ?? new RouterComRoutingOptions();
        if (routing.AllowFlexTier is { } allow) body["allow_flex_tier"] = allow;
        if (routing.FallbackTimeoutSeconds is { } fallback) body["fallback_timeout"] = fallback;
        if (routing.ProviderTimeoutSeconds is { } timeout) body["provider_timeout"] = timeout;
        return body;
    }
}

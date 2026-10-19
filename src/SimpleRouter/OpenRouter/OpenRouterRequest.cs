using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SimpleRouter.Local;
using SimpleRouter.Routing;

namespace SimpleRouter.OpenRouter;

/// <summary>OpenRouter's Chat Completions options are independent of local context caps and template kwargs.</summary>
public static class OpenRouterRequest
{
    public static JsonObject Translate(UpstreamCall call)
    {
        var chat = ChatRequestTranslator.Translate(call.Request, call.Model, call.EstimatedInputTokens, applyLocalOptions: false);
        chat["session_id"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.Session.Key)));
        if (Effort.Sent(call.Request, call.Model) is { } effort)
        {
            chat["reasoning"] = new JsonObject { ["effort"] = Effort.Name(effort), ["exclude"] = false };
        }
        else if (ChatRequestTranslator.Str(call.Request["thinking"]?["type"]) == "adaptive")
        {
            chat["reasoning"] = new JsonObject { ["enabled"] = true, ["exclude"] = false };
        }

        OpenRouterReasoning.Replay(call.Request, chat);
        if (call.Model.OpenRouter is not { } routing)
        {
            return chat;
        }
        var pluginId = call.Model.UpstreamModelId switch
        {
            "openrouter/auto" => "auto-router",
            "openrouter/auto-beta" => "auto-beta-router",
            _ => null,
        };
        if (pluginId is not null)
        {
            var plugin = new JsonObject { ["id"] = pluginId };
            if (routing.CostTier is { } tier) plugin["cost_tier"] = tier;
            if (routing.AllowedModels is { } allowed) plugin["allowed_models"] = new JsonArray(allowed.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            if (routing.ExcludedModels is { } excluded) plugin["excluded_models"] = new JsonArray(excluded.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            chat["plugins"] = new JsonArray(plugin);
        }
        var prices = new JsonObject();
        if (routing.MaxPromptPrice is { } prompt) prices["prompt"] = prompt;
        if (routing.MaxCompletionPrice is { } completion) prices["completion"] = completion;
        if (prices.Count > 0) chat["provider"] = new JsonObject { ["max_price"] = prices };
        return chat;
    }
}

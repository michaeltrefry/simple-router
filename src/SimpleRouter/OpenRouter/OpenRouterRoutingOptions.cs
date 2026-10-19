using System.Text.Json;

namespace SimpleRouter.OpenRouter;

/// <summary>OpenRouter auto-routing constraints; prices are USD per million tokens, not reasoning effort.</summary>
public sealed class OpenRouterRoutingOptions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string? CostTier { get; set; }
    public List<string>? AllowedModels { get; set; }
    public List<string>? ExcludedModels { get; set; }
    public decimal? MaxPromptPrice { get; set; }
    public decimal? MaxCompletionPrice { get; set; }

    public static bool IsAuto(string upstreamModelId) => upstreamModelId is "openrouter/auto" or "openrouter/auto-beta";

    public static void Validate(OpenRouterRoutingOptions? options)
    {
        if (options is null) return;
        if (options.CostTier is not null and not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new InvalidDataException("OpenRouter cost tier must be low, medium, high, xhigh or max, or empty for the provider default.");
        if (options.MaxPromptPrice is < 0 || options.MaxCompletionPrice is < 0)
            throw new InvalidDataException("OpenRouter maximum prices cannot be negative.");
        if ((options.AllowedModels ?? []).Concat(options.ExcludedModels ?? []).Any(m => string.IsNullOrWhiteSpace(m) || m.Any(char.IsWhiteSpace)))
            throw new InvalidDataException("OpenRouter model filters must contain non-empty model IDs or patterns without whitespace.");
    }
}

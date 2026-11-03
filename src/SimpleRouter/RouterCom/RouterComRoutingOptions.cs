using System.Text.Json;

namespace SimpleRouter.RouterCom;

/// <summary>Router.com cost-efficient routing, independent of reasoning effort and local tier membership.</summary>
public sealed class RouterComRoutingOptions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool? AllowFlexTier { get; set; } = true;
    public int? FallbackTimeoutSeconds { get; set; } = 30;
    public int? ProviderTimeoutSeconds { get; set; } = 140;

    public static void Validate(RouterComRoutingOptions? options)
    {
        if (options?.FallbackTimeoutSeconds is <= 0 || options?.ProviderTimeoutSeconds is <= 0)
            throw new InvalidDataException("Router.com timeouts must be positive seconds, or empty for provider defaults.");
    }
}

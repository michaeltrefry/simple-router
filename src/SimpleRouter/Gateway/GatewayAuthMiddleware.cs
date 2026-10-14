using System.Security.Cryptography;
using System.Text;
using SimpleRouter.Api;

namespace SimpleRouter.Gateway;

/// <summary>Rejects <c>/v1</c> requests that do not carry the gateway key.</summary>
public sealed class GatewayAuthMiddleware(RequestDelegate next, GatewayKeyStore keys)
{
    public const string RouterKeyHeader = "X-Weave-Router-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        var expected = Encoding.UTF8.GetBytes(await keys.GetAsync(context.RequestAborted));
        if (PresentedKeys(context.Request).Any(k => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k), expected)))
        {
            await next(context);
            return;
        }

        await AnthropicError.WriteAsync(context, StatusCodes.Status401Unauthorized, "authentication_error", "Invalid or missing router key.");
    }

    private static IEnumerable<string> PresentedKeys(HttpRequest request)
    {
        foreach (var value in request.Headers[RouterKeyHeader])
        {
            if (!string.IsNullOrEmpty(value)) yield return value;
        }
        foreach (var value in request.Headers["x-api-key"])
        {
            if (!string.IsNullOrEmpty(value)) yield return value;
        }
        foreach (var value in request.Headers.Authorization)
        {
            if (value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                yield return value["Bearer ".Length..].Trim();
            }
        }
    }
}

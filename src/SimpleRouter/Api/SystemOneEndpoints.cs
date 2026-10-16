using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SimpleRouter.Routing;
using SimpleRouter.SystemOne;

namespace SimpleRouter.Api;

public static class SystemOneEndpoints
{
    public static void MapSystemOneEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/systemone/models", async (HttpContext context, SystemOneProxy proxy) =>
            await context.Response.WriteAsJsonAsync(new
            {
                data = (await proxy.ModelsAsync(context.RequestAborted)).Select(m => new { id = m.Id, healthy = m.Healthy }),
            }, context.RequestAborted));

        app.MapPost("/v1/systemone", async (HttpContext context, SystemOneProxy proxy, IOptions<RouterOptions> options) =>
        {
            // Raised past the server-wide limit before the body is read: media arrives base64-encoded in it.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = options.Value.SystemOne.MaxBodyBytes;
            }
            JsonObject? request;
            try
            {
                request = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            }
            catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                await AnthropicError.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large",
                    $"The body is larger than {options.Value.SystemOne.MaxBodyBytes} bytes.");
                return;
            }
            catch (JsonException)
            {
                request = null;
            }
            if (request is null)
            {
                await AnthropicError.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", "The body must be a JSON object.");
                return;
            }

            var sessionId = context.Request.Headers[SessionKey.HeaderName].ToString().Trim();
            var reply = await proxy.DecideAsync(request, sessionId.Length == 0 ? null : sessionId, context.RequestAborted);
            if (reply.ErrorType is { } type)
            {
                await AnthropicError.WriteAsync(context, reply.Status, type, reply.ErrorMessage!);
                return;
            }
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.ContentType ?? "application/json";
            await context.Response.Body.WriteAsync(reply.Body, context.RequestAborted);
        });
    }
}

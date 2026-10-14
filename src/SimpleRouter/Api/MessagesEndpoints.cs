using SimpleRouter.Routing;

namespace SimpleRouter.Api;

public static class MessagesEndpoints
{
    public static void MapMessagesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/messages", (HttpContext context, MessageRouter router) =>
            router.RouteAsync(context, countTokens: false));
        app.MapPost("/v1/messages/count_tokens", (HttpContext context, MessageRouter router) =>
            router.RouteAsync(context, countTokens: true));
    }
}

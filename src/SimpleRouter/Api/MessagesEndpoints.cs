using SimpleRouter.Claude;

namespace SimpleRouter.Api;

public static class MessagesEndpoints
{
    public static void MapMessagesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/messages", (HttpContext context, ClaudeUpstream upstream) =>
            upstream.ForwardAsync(context, "/v1/messages", injectIdentity: true));
        app.MapPost("/v1/messages/count_tokens", (HttpContext context, ClaudeUpstream upstream) =>
            upstream.ForwardAsync(context, "/v1/messages/count_tokens", injectIdentity: false));
    }
}

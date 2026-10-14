using Microsoft.Extensions.Options;

namespace SimpleRouter.Api;

public static class ModelsEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 1000;

    public static void MapModelsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (HttpContext context, IOptions<RouterOptions> options, int? limit) =>
        {
            var take = limit ?? DefaultLimit;
            if (take is < 1 or > MaxLimit)
            {
                return AnthropicError.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error",
                    $"limit must be between 1 and {MaxLimit}.");
            }

            var ids = options.Value.ModelIds;
            var page = ids.Take(take).ToList();
            return context.Response.WriteAsJsonAsync(new
            {
                data = page.Select(id => new
                {
                    type = "model",
                    id,
                    display_name = id,
                    created_at = DateTimeOffset.UnixEpoch,
                }),
                has_more = ids.Count > page.Count,
                first_id = page.FirstOrDefault(),
                last_id = page.LastOrDefault(),
            });
        });
    }
}

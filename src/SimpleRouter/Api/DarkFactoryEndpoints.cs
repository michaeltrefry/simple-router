using Microsoft.AspNetCore.Http.Features;
using SimpleRouter.Usage;

namespace SimpleRouter.Api;

/// <summary>The orchestrator's read endpoints: whether the subscriptions are spent, and what a client session has cost.</summary>
public static class DarkFactoryEndpoints
{
    /// <summary>
    /// The session ID as the client escaped it. Read from the raw target: the decoded path keeps <c>%2F</c> escaped but
    /// unescapes <c>%25</c>, so an ID holding "/" or "%" could not be recovered from it unambiguously.
    /// </summary>
    private static string SessionIdOf(HttpContext context)
    {
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value!;
        return Uri.UnescapeDataString(target.Split('?', 2)[0].TrimEnd('/').Split('/')[^2]);
    }

    public static void MapDarkFactoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/subscriptions/usage", async (HttpContext context, SubscriptionUsage usage) =>
            await context.Response.WriteAsJsonAsync(await usage.ReportAsync(context.RequestAborted), context.RequestAborted));

        app.MapGet("/v1/sessions/{clientSessionId}/cost", async (HttpContext context, UsageRecorder recorder) =>
        {
            var clientSessionId = SessionIdOf(context);
            if (await recorder.SessionCostAsync(clientSessionId, context.RequestAborted) is not { } cost)
            {
                await AnthropicError.WriteAsync(context, StatusCodes.Status404NotFound, "not_found_error", "No requests are recorded for this session.");
                return;
            }
            await context.Response.WriteAsJsonAsync(new
            {
                session_id = clientSessionId,
                request_count = cost.Count,
                actual_cost_usd_micros = cost.CostUsdMicros,
            }, context.RequestAborted);
        });
    }
}

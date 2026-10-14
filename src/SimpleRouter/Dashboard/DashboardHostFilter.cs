namespace SimpleRouter.Dashboard;

/// <summary>
/// Rejects dashboard requests (everything outside <c>/v1</c>) whose Host is not loopback, so a DNS-rebinding page cannot
/// read the dashboard. <c>/v1</c> is exempt: it is guarded by the gateway key and may be reached by other host names.
/// </summary>
public sealed class DashboardHostFilter(RequestDelegate next)
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "[::1]", "::1" };

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/v1") || LoopbackHosts.Contains(context.Request.Host.Host))
        {
            return next(context);
        }
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Task.CompletedTask;
    }
}

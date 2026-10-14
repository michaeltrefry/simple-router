namespace SimpleRouter.Api;

/// <summary>Writes errors in the Anthropic Messages API error shape.</summary>
public static class AnthropicError
{
    public static Task WriteAsync(HttpContext context, int status, string type, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { type = "error", error = new { type, message } });
    }
}

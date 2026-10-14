namespace SimpleRouter.Codex;

/// <summary>Maps Codex error codes and HTTP statuses onto Anthropic error types.</summary>
public static class CodexErrors
{
    /// <summary>The Anthropic error type for a Responses error <c>code</c> (or type) reported in a stream.</summary>
    public static string Type(string? code) => code switch
    {
        "rate_limit_exceeded" or "usage_limit_reached" or "usage_not_included" or "insufficient_quota" => "rate_limit_error",
        "context_length_exceeded" or "invalid_prompt" or "invalid_request_error" => "invalid_request_error",
        "server_is_overloaded" or "overloaded" => "overloaded_error",
        _ => "api_error",
    };

    /// <summary>The HTTP status a non-streaming client gets for an error type reported mid-stream.</summary>
    public static int Status(string type) => type switch
    {
        "rate_limit_error" => StatusCodes.Status429TooManyRequests,
        "invalid_request_error" => StatusCodes.Status400BadRequest,
        "overloaded_error" => 529,
        _ => StatusCodes.Status502BadGateway,
    };

    /// <summary>The Anthropic error type for an upstream HTTP status, which is passed through unchanged.</summary>
    public static string TypeForStatus(int status) => status switch
    {
        400 or 422 => "invalid_request_error",
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        413 => "request_too_large",
        429 => "rate_limit_error",
        503 or 529 => "overloaded_error",
        _ => "api_error",
    };
}

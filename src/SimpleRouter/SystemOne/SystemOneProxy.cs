using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SimpleRouter.Api;
using SimpleRouter.Data;
using SimpleRouter.Local;
using SimpleRouter.Routing;

namespace SimpleRouter.SystemOne;

/// <summary>
/// The upstream's answer, passed to the client unchanged, or a router error (<see cref="ErrorType"/> set) written in the
/// Anthropic error shape. <see cref="Model"/> is the catalog model that was called, when one was.
/// </summary>
public sealed record SystemOneReply(int Status, byte[] Body, string? ContentType, CatalogModel? Model, string? ErrorType = null, string? ErrorMessage = null)
{
    public static SystemOneReply Error(int status, string type, string message, CatalogModel? model = null) =>
        new(status, [], null, model, type, message);
}

/// <summary>
/// Forwards a SystemOne decision request to the catalog model it names: <c>model</c> becomes the upstream model ID and
/// the rest of the body is sent as it came. Decision models are never routed; the client picks one by ID.
/// </summary>
public sealed class SystemOneProxy(IHttpClientFactory httpClientFactory, IDbContextFactory<RouterDbContext> dbFactory, SecretProtector secrets,
    ILogger<SystemOneProxy> logger)
{
    public const string HttpClientName = "systemone-upstream";

    /// <summary>Rewrites <paramref name="request"/>'s <c>model</c> in place: its images and videos can be large, so it is not copied.</summary>
    public async Task<SystemOneReply> DecideAsync(JsonObject request, CancellationToken ct)
    {
        var id = request["model"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        CatalogModel? model = null;
        if (id is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.Provider == ProviderKind.SystemOne && m.Enabled, ct);
        }
        if (model is null)
        {
            return SystemOneReply.Error(StatusCodes.Status404NotFound, "not_found_error",
                id is null ? "model is required: the ID of a SystemOne model." : $"No enabled SystemOne model is named '{id}'.");
        }

        request["model"] = model.UpstreamModelId;
        using var message = new HttpRequestMessage(HttpMethod.Post, LocalUpstream.Endpoint(model, "systemone"))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, WireJson.Relaxed)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (model.ProtectedApiKey is { } key)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Unprotect(key));
        }

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, ct);
            return new SystemOneReply((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct),
                response.Content.Headers.ContentType?.ToString(), model);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("SystemOne model {model} is unreachable: {error}", model.Id, e.Message);
            return SystemOneReply.Error(StatusCodes.Status503ServiceUnavailable, "overloaded_error", $"SystemOne model {model.Id} is unreachable.", model);
        }
    }
}

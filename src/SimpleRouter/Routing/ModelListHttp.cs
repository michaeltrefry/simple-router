using System.Net;
using System.Text.Json.Nodes;

namespace SimpleRouter.Routing;

/// <summary>The bounded GET both subscription model-list sources share.</summary>
public static class ModelListHttp
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Upstream clients have no timeout (streams run long), so the list call bounds itself.</summary>
    public static HttpClient Client(IHttpClientFactory factory, string name)
    {
        var client = factory.CreateClient(name);
        client.MaxResponseContentBufferSize = MaxBodyBytes;
        return client;
    }

    /// <summary>Sends and disposes <paramref name="request"/>; a non-success status or a non-object body throws.</summary>
    public static async Task<JsonObject> GetJsonAsync(HttpClient client, HttpRequestMessage request, string provider, CancellationToken ct)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(Timeout);
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new ModelListHttpException(response.StatusCode, $"{provider} returned HTTP {(int)response.StatusCode} for its model list.");
            }
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)) as JsonObject
                ?? throw new ModelListException($"{provider} sent a malformed model list.");
        }
    }
}

/// <summary>A model list request answered with a non-success status.</summary>
public sealed class ModelListHttpException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

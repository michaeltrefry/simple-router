using System.Net;

namespace SimpleRouter.Tests.Support;

/// <summary>An HTTP handler that records requests (with bodies) and answers with a configurable responder.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, byte[] Body);

    public List<Recorded> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        lock (Requests)
        {
            Requests.Add(new Recorded(request.Method, request.RequestUri!, headers, body));
        }
        return Respond(request);
    }
}

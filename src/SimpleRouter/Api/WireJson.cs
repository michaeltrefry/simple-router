using System.Text.Encodings.Web;
using System.Text.Json;

namespace SimpleRouter.Api;

/// <summary>Serializer options for bodies sent to clients and upstreams: non-ASCII text stays unescaped.</summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

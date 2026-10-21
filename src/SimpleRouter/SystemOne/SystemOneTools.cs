using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SimpleRouter.Routing;

namespace SimpleRouter.SystemOne;

/// <summary>The MCP tools at <c>/v1/mcp</c>: the same listing and decisions as the HTTP endpoints, through <see cref="SystemOneProxy"/>.</summary>
/// <remarks>
/// Services come in through the constructor, never as method parameters: the SDK leaves a service parameter out of the
/// input schema by matching <see cref="System.Reflection.ParameterInfo"/> instances, and concurrent first reflection over
/// a method can hand out two different sets of them, so the parameter would leak into the schema as a required input.
/// </remarks>
[McpServerToolType]
public sealed class SystemOneTools(SystemOneProxy proxy, IHttpContextAccessor http)
{
    [McpServerTool(Name = "systemone_models", ReadOnly = true, Idempotent = true)]
    [Description("Lists the SystemOne decision models this router serves, with whether each endpoint passed its last health check. Pass an id as systemone_decide's model.")]
    public async Task<string> ModelsAsync(CancellationToken ct) =>
        JsonSerializer.Serialize(new { data = (await proxy.ModelsAsync(ct)).Select(m => new { id = m.Id, healthy = m.Healthy }) });

    [McpServerTool(Name = "systemone_decide", ReadOnly = true)]
    [Description("""
        Asks a decision model questions about an input and returns a probability for every answer, without generating text.
        Each question is {"type": "choice" | "noul" | "score", "instructions": "...", "criteria": ...}: choice takes an object
        of option name to description (or null), noul is yes/no, score takes an ordered array of labels. All questions are
        answered in one call. Returns the model's JSON: answers per question (pick, probabilities, confidence) and usage.
        """)]
    public async Task<CallToolResult> DecideAsync(
        [Description("A SystemOne model id from systemone_models.")] string model,
        [Description("The input to decide about: text, or a JSON value.")] JsonElement state,
        [Description("Question name to question, as described above.")] JsonObject questions,
        [Description("Optional images as http(s) URLs or base64 data URLs (PNG, JPEG, WebP); every question sees all of them.")] string[]? images = null,
        [Description("Optional videos as http(s) URLs or base64 data URLs (MP4, WebM, MOV, MKV), read at 2 frames per second.")] string[]? videos = null,
        CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(state),
            ["questions"] = questions.DeepClone(),
        };
        if (images is { Length: > 0 })
        {
            request["images"] = new JsonArray(images.Select(i => (JsonNode?)i).ToArray());
        }
        if (videos is { Length: > 0 })
        {
            request["videos"] = new JsonArray(videos.Select(v => (JsonNode?)v).ToArray());
        }

        var sessionId = http.HttpContext?.Request.Headers[SessionKey.HeaderName].ToString().Trim();
        var reply = await proxy.DecideAsync(request, string.IsNullOrEmpty(sessionId) ? null : sessionId, ct);
        var text = reply.ErrorType is not null ? reply.ErrorMessage! : Encoding.UTF8.GetString(reply.Body);
        return new CallToolResult
        {
            IsError = reply.ErrorType is not null || reply.Status is < 200 or >= 300,
            Content = [new TextContentBlock { Text = reply.ErrorType is null && reply.Status is < 200 or >= 300 ? $"HTTP {reply.Status}: {text}" : text }],
        };
    }
}

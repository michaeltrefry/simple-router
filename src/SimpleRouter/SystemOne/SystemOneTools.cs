using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SimpleRouter.Routing;

namespace SimpleRouter.SystemOne;

/// <summary>The MCP tools at <c>/v1/mcp</c>: the same listing and decisions as the HTTP endpoints, through <see cref="SystemOneProxy"/>.</summary>
[McpServerToolType]
public sealed class SystemOneTools
{
    [McpServerTool(Name = "systemone_models", ReadOnly = true, Idempotent = true)]
    [Description("Lists the SystemOne decision models this router serves: whether each endpoint passed its last health check, the question types it answers, and whether it accepts images and videos. Pass an id as systemone_decide's model.")]
    public static async Task<string> ModelsAsync(SystemOneProxy proxy, CancellationToken ct) =>
        JsonSerializer.Serialize(new { data = (await proxy.ModelsAsync(ct)).Select(m => m.Wire()) });

    [McpServerTool(Name = "systemone_decide", ReadOnly = true)]
    [Description("""
        Asks a decision model questions about an input and returns a probability for every answer, without generating text.
        Each question is {"type": "choice" | "noul" | "score" | "box", "instructions": "...", "criteria": ...}: choice takes an object
        of option name to description (or null), noul is yes/no, score takes an ordered array of labels, box locates the target
        its instructions describe in an image and takes no criteria. All questions are answered in one call. Use only the
        question types, images and videos systemone_models lists for the model; anything else is refused. Returns the model's
        JSON: answers per question (pick, probabilities, confidence; box: edges as fractions) and usage.
        """)]
    public static async Task<CallToolResult> DecideAsync(
        SystemOneProxy proxy,
        IHttpContextAccessor http,
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

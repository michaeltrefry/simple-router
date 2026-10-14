using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SimpleRouter.Routing;

/// <summary>A pin key plus the client-reported session ID it was derived from, when there was one.</summary>
public sealed record SessionIdentity(string Key, string? ClientSessionId);

/// <summary>
/// Derives the pin key from the client session ID (header, else <c>metadata.user_id</c>, else a system-prompt hash)
/// combined with the first user message, so sub-agents that share a client session get their own pins.
/// </summary>
public static partial class SessionKey
{
    public const string HeaderName = "X-Claude-Code-Session-Id";

    [GeneratedRegex("session_([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex BundledSessionId();

    // Duplicate keys fail the parse instead of throwing on first access; lone surrogates throw InvalidOperationException on read.
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    public static SessionIdentity Derive(string? headerSessionId, JsonObject request)
    {
        var clientSessionId = Normalize(headerSessionId) ?? MetadataSessionId(request);
        var source = clientSessionId is not null ? "client:" + clientSessionId : "system:" + Hash(ContentText(request["system"]));
        var firstUser = request["messages"] is JsonArray messages
            ? messages.FirstOrDefault(m => m?["role"] is JsonValue role && role.TryGetValue<string>(out var r) && r == "user")?["content"]
            : null;
        return new SessionIdentity(Hash(source + "\nfirst-user:" + Hash(ContentText(firstUser))), clientSessionId);
    }

    /// <summary>Claude Code sends either a JSON string with <c>session_id</c> or <c>user_…_account_…_session_&lt;uuid&gt;</c>.</summary>
    private static string? MetadataSessionId(JsonObject request)
    {
        if (request["metadata"]?["user_id"] is not JsonValue value || !value.TryGetValue<string>(out var userId))
        {
            return null;
        }
        if (userId.TrimStart().StartsWith('{'))
        {
            try
            {
                return JsonNode.Parse(userId, documentOptions: Strict)?["session_id"] is JsonValue id && id.TryGetValue<string>(out var sessionId)
                    ? Normalize(sessionId)
                    : null;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }
        var match = BundledSessionId().Match(userId);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string? Normalize(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    /// <summary>Text blocks contribute their text; other blocks their JSON minus <c>cache_control</c>, which clients move between turns.</summary>
    private static string ContentText(JsonNode? content) => content switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray blocks => string.Join("\n", blocks.Select(BlockText)),
        _ => content.ToJsonString(),
    };

    private static string BlockText(JsonNode? block)
    {
        if (block is not JsonObject obj)
        {
            return block?.ToJsonString() ?? "";
        }
        if (obj["text"] is JsonValue text && text.TryGetValue<string>(out var s))
        {
            return s;
        }
        var copy = (JsonObject)obj.DeepClone();
        copy.Remove("cache_control");
        return copy.ToJsonString();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

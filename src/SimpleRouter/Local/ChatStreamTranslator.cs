using System.Text;
using System.Text.Json.Nodes;
using SimpleRouter.Api;

namespace SimpleRouter.Local;

/// <summary>
/// Turns Chat Completions stream chunks into an Anthropic Messages SSE stream. Reasoning and text stream live as
/// thinking and text blocks (a switch between them closes one and opens the other); tool calls are accumulated by upstream
/// index (their arguments may arrive fragmented and interleaved) and emitted after them as sequential <c>tool_use</c>
/// blocks once each call's JSON is complete.
/// </summary>
public sealed class ChatStreamTranslator(string model, int estimatedInputTokens)
{
    private sealed class ToolCall
    {
        public string? Id;
        public readonly StringBuilder Name = new();
        public readonly StringBuilder Arguments = new();
    }

    private readonly ThinkTagFilter _think = new();
    private readonly SortedDictionary<int, ToolCall> _tools = [];
    private int _blocks;
    private bool _reasoningField;
    private int? _open;
    private string? _openKind;
    private int? _lastToolIndex;
    private string? _finishReason;
    private JsonNode? _usage;

    /// <summary>True once the upstream reported a finish reason.</summary>
    public bool Finished => _finishReason is not null;

    public string Start() => Event("message_start", new JsonObject
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject
        {
            ["id"] = ChatResponseTranslator.MessageId(),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = estimatedInputTokens, ["output_tokens"] = 0 },
        },
    });

    public string Feed(JsonObject chunk)
    {
        if (chunk["usage"] is JsonObject usage)
        {
            _usage = usage;
        }
        if ((chunk["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice)
        {
            return "";
        }

        var sse = new StringBuilder();
        var delta = choice["delta"];
        var reasoning = ChatResponseTranslator.Reasoning(delta);
        _reasoningField |= reasoning.Length > 0;
        Append(sse, "thinking", reasoning);
        if (ChatRequestTranslator.Str(delta?["content"]) is { } content)
        {
            var (thinking, text) = _think.Feed(content);
            // A server that fills a reasoning field may also leave the same reasoning in <think> tags (llama.cpp's deepseek-legacy format).
            Append(sse, "thinking", _reasoningField ? "" : thinking);
            Append(sse, "text", text);
        }
        foreach (var call in delta?["tool_calls"] as JsonArray ?? [])
        {
            AccumulateToolCall(call);
        }
        if (ChatRequestTranslator.Str(choice["finish_reason"]) is { } reason)
        {
            _finishReason = reason;
        }
        return sse.ToString();
    }

    public string Finish()
    {
        var sse = new StringBuilder();
        var (thinking, text) = _think.Flush();
        Append(sse, "thinking", _reasoningField ? "" : thinking);
        Append(sse, "text", text);
        Close(sse);

        var toolUses = 0;
        foreach (var call in _tools.Values.Where(c => c.Name.Length > 0))
        {
            var index = _blocks++;
            toolUses++;
            sse.Append(Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = index,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = ChatResponseTranslator.ToolUseId(call.Id),
                    ["name"] = call.Name.ToString(),
                    ["input"] = new JsonObject(),
                },
            }));
            sse.Append(Delta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = ChatResponseTranslator.ToolInput(call.Arguments.ToString()) }));
            sse.Append(Stop(index));
        }

        sse.Append(Event("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = ChatResponseTranslator.StopReason(_finishReason, toolUses > 0), ["stop_sequence"] = null },
            ["usage"] = ChatResponseTranslator.Usage(_usage, estimatedInputTokens),
        }));
        sse.Append(Event("message_stop", new JsonObject { ["type"] = "message_stop" }));
        return sse.ToString();
    }

    /// <summary>Ends a stream that cannot complete with an Anthropic <c>error</c> event; no message_stop follows.</summary>
    public string Abort(string message)
    {
        var sse = new StringBuilder();
        Close(sse);
        sse.Append(Event("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message },
        }));
        return sse.ToString();
    }

    private void AccumulateToolCall(JsonNode? call)
    {
        var id = ChatRequestTranslator.Str(call?["id"]);
        // Some servers omit the index; a different id then starts a new call, while a repeated or missing one continues it.
        var index = call?["index"] is JsonValue i && i.TryGetValue<int>(out var n)
            ? n
            : _lastToolIndex is { } last && (string.IsNullOrEmpty(id) || id == _tools[last].Id) ? last : _tools.Count;
        _lastToolIndex = index;
        if (!_tools.TryGetValue(index, out var tool))
        {
            _tools[index] = tool = new ToolCall();
        }
        if (!string.IsNullOrEmpty(id))
        {
            tool.Id ??= id;
        }
        tool.Name.Append(ChatRequestTranslator.Str(call?["function"]?["name"]));
        tool.Arguments.Append(ChatRequestTranslator.Str(call?["function"]?["arguments"]));
    }

    /// <param name="kind"><c>thinking</c> or <c>text</c>.</param>
    private void Append(StringBuilder sse, string kind, string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        if (_openKind != kind)
        {
            Close(sse);
            _open = _blocks++;
            _openKind = kind;
            sse.Append(Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = _open,
                ["content_block"] = kind == "text"
                    ? new JsonObject { ["type"] = "text", ["text"] = "" }
                    : new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
            }));
        }
        sse.Append(Delta(_open!.Value, kind == "text"
            ? new JsonObject { ["type"] = "text_delta", ["text"] = text }
            : new JsonObject { ["type"] = "thinking_delta", ["thinking"] = text }));
    }

    private void Close(StringBuilder sse)
    {
        if (_open is not { } index)
        {
            return;
        }
        if (_openKind == "thinking")
        {
            sse.Append(Delta(index, new JsonObject { ["type"] = "signature_delta", ["signature"] = ChatResponseTranslator.SignaturePrefix }));
        }
        sse.Append(Stop(index));
        _open = null;
        _openKind = null;
    }

    private static string Delta(int index, JsonObject delta) =>
        Event("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta });

    private static string Stop(int index) =>
        Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });

    private static string Event(string name, JsonObject data) => $"event: {name}\ndata: {data.ToJsonString(WireJson.Relaxed)}\n\n";
}

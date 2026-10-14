using System.Text;
using System.Text.Json.Nodes;
using SimpleRouter.Api;
using SimpleRouter.Local;

namespace SimpleRouter.Codex;

/// <summary>
/// Turns Responses stream events into Anthropic Messages stream events. Output text (and refusals) and reasoning
/// summaries stream live as text and thinking blocks; a thinking block's signature is the reasoning item's encrypted
/// content behind <see cref="SignaturePrefix"/>, so the next request can hand it back to Codex. Function calls
/// are buffered per output item, since their arguments arrive in fragments, and emitted whole as a <c>tool_use</c> block
/// (id = <c>call_id</c>) when the item is done. Blocks never interleave, so indexes stay sequential.
/// </summary>
public sealed class ResponsesStreamTranslator(string model, int estimatedInputTokens)
{
    /// <summary>Marks a thinking signature as Codex encrypted reasoning rather than an Anthropic signature.</summary>
    public const string SignaturePrefix = "codex:";

    private sealed class FunctionCall
    {
        public string? CallId;
        public string? Name;
        public readonly StringBuilder Arguments = new();
    }

    private readonly Dictionary<int, FunctionCall> _calls = [];
    private readonly HashSet<int> _seenItems = [];
    private int _blocks;
    private int? _openBlock;
    private int? _openItem;
    private string? _openKind;
    private int? _openSummaryIndex;
    private int _toolUses;
    private bool _refused;

    /// <summary>True once a terminal event (completion, failure or error) has been translated.</summary>
    public bool Ended { get; private set; }

    /// <summary>The Codex error code the stream failed with (<c>context_length_exceeded</c>, say), if it reported one.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>The Anthropic error type the stream ended with; null unless it failed.</summary>
    public string? ErrorType { get; private set; }

    public JsonObject Start() => new()
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
    };

    public List<JsonObject> Feed(JsonObject evt)
    {
        var events = new List<JsonObject>();
        var outputIndex = Int(evt["output_index"]) ?? 0;
        switch (Str(evt["type"]))
        {
            case "response.output_text.delta":
                Live(events, outputIndex, "text", Str(evt["delta"]));
                break;
            case "response.refusal.delta":
                _refused = true;
                Live(events, outputIndex, "text", Str(evt["delta"]));
                break;
            case "response.reasoning_summary_text.delta":
                var summaryIndex = Int(evt["summary_index"]) ?? 0;
                var separator = _openItem == outputIndex && _openKind == "thinking" && _openSummaryIndex != summaryIndex ? "\n\n" : "";
                Live(events, outputIndex, "thinking", separator + Str(evt["delta"]));
                _openSummaryIndex = summaryIndex;
                break;
            case "response.reasoning_text.delta":
                Live(events, outputIndex, "thinking", Str(evt["delta"]));
                break;
            case "response.output_item.added" when Str(evt["item"]?["type"]) == "function_call":
                Call(outputIndex, evt["item"]);
                break;
            case "response.function_call_arguments.delta":
                Call(outputIndex, null).Arguments.Append(Str(evt["delta"]));
                break;
            case "response.function_call_arguments.done" when Str(evt["arguments"]) is { } arguments:
                Call(outputIndex, null).Arguments.Clear().Append(arguments);
                break;
            case "response.output_item.done":
                ItemDone(events, outputIndex, evt["item"]);
                break;
            case "response.completed":
                Completed(events, evt["response"]);
                break;
            case "response.incomplete":
                FoldUnseen(events, evt["response"], calls: false);
                Close(events);
                var reason = Str(evt["response"]?["incomplete_details"]?["reason"]);
                Finish(events, _toolUses > 0 ? "tool_use" : reason == "content_filter" ? "refusal" : "max_tokens", evt["response"]?["usage"]);
                break;
            case "response.failed":
                var failure = evt["response"]?["error"];
                events.AddRange(Abort(Str(failure?["message"]) ?? "Codex response failed.", Str(failure?["code"])));
                break;
            case "error":
                var error = evt["error"] as JsonObject ?? evt;
                events.AddRange(Abort(Str(error["message"]) ?? "Codex reported an error mid-stream.", Str(error["code"]) ?? Str(error["type"])));
                break;
        }
        return events;
    }

    /// <summary>Ends a stream that cannot complete with an Anthropic <c>error</c> event; no message_stop follows.</summary>
    public List<JsonObject> Abort(string message, string? code = null)
    {
        var events = new List<JsonObject>();
        CloseLive(events);
        (ErrorCode, ErrorType) = (code, CodexErrors.Type(code));
        events.Add(new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = ErrorType, ["message"] = message },
        });
        Ended = true;
        return events;
    }

    private void Completed(List<JsonObject> events, JsonNode? response)
    {
        FoldUnseen(events, response, calls: true);
        Close(events);
        Finish(events, _toolUses > 0 ? "tool_use" : _refused ? "refusal" : "end_turn", response?["usage"]);
    }

    /// <summary>Covers items whose events were lost: the terminal response carries every output item.</summary>
    /// <param name="calls">False when the response was cut off, so a function call's arguments may be truncated.</param>
    private void FoldUnseen(List<JsonObject> events, JsonNode? response, bool calls)
    {
        var index = 0;
        foreach (var item in response?["output"] as JsonArray ?? [])
        {
            var isCall = Str(item?["type"]) == "function_call";
            if (isCall ? calls && (!_seenItems.Contains(index) || _calls.ContainsKey(index)) : !_seenItems.Contains(index))
            {
                ItemDone(events, index, item);
            }
            index++;
        }
    }

    private void ItemDone(List<JsonObject> events, int outputIndex, JsonNode? item)
    {
        var seen = !_seenItems.Add(outputIndex);
        switch (Str(item?["type"]))
        {
            case "function_call":
                var call = Call(outputIndex, item);
                if (Str(item?["arguments"]) is { Length: > 0 } arguments)
                {
                    call.Arguments.Clear().Append(arguments);
                }
                _calls.Remove(outputIndex);
                EmitToolUse(events, call);
                break;
            case "message" when !seen:
                var parts = item!["content"] as JsonArray ?? [];
                _refused |= parts.Any(p => Str(p?["type"]) == "refusal");
                var text = string.Concat(parts.Select(p => Str(p?["type"]) switch
                {
                    "output_text" => Str(p!["text"]),
                    "refusal" => Str(p!["refusal"]),
                    _ => null,
                }));
                Live(events, outputIndex, "text", text);
                break;
            case "reasoning":
                if (!seen)
                {
                    var summary = string.Join("\n\n", (item!["summary"] as JsonArray ?? []).Select(p => Str(p?["text"])).Where(s => !string.IsNullOrEmpty(s)));
                    Live(events, outputIndex, "thinking", summary);
                }
                if (Str(item?["encrypted_content"]) is { Length: > 0 } encrypted)
                {
                    Open(events, outputIndex, "thinking");
                    events.Add(Delta(_openBlock!.Value, new JsonObject { ["type"] = "signature_delta", ["signature"] = SignaturePrefix + encrypted }));
                }
                break;
        }
        if (_openItem == outputIndex)
        {
            CloseLive(events);
        }
    }

    private FunctionCall Call(int outputIndex, JsonNode? item)
    {
        _seenItems.Add(outputIndex);
        if (!_calls.TryGetValue(outputIndex, out var call))
        {
            _calls[outputIndex] = call = new FunctionCall();
        }
        call.CallId ??= Str(item?["call_id"]);
        call.Name ??= Str(item?["name"]);
        return call;
    }

    private void EmitToolUse(List<JsonObject> events, FunctionCall call)
    {
        // A nameless call would make the client invoke tool "" in a loop.
        if (string.IsNullOrEmpty(call.Name))
        {
            return;
        }
        CloseLive(events);
        var index = _blocks++;
        _toolUses++;
        events.Add(new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = index,
            ["content_block"] = new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = ChatResponseTranslator.ToolUseId(call.CallId),
                ["name"] = call.Name,
                ["input"] = new JsonObject(),
            },
        });
        events.Add(Delta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = ChatResponseTranslator.ToolInput(call.Arguments.ToString()) }));
        events.Add(Stop(index));
    }

    /// <summary>Appends text or thinking to the block for <paramref name="outputIndex"/>, opening it (and closing any other) first.</summary>
    private void Live(List<JsonObject> events, int outputIndex, string kind, string? text)
    {
        _seenItems.Add(outputIndex);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        Open(events, outputIndex, kind);
        events.Add(Delta(_openBlock!.Value, kind == "text"
            ? new JsonObject { ["type"] = "text_delta", ["text"] = text }
            : new JsonObject { ["type"] = "thinking_delta", ["thinking"] = text }));
    }

    /// <summary>Makes the block for <paramref name="outputIndex"/> the open one, closing any other first.</summary>
    private void Open(List<JsonObject> events, int outputIndex, string kind)
    {
        if (_openItem != outputIndex || _openKind != kind)
        {
            CloseLive(events);
            _openBlock = _blocks++;
            _openItem = outputIndex;
            _openKind = kind;
            events.Add(new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = _openBlock,
                ["content_block"] = kind == "text"
                    ? new JsonObject { ["type"] = "text", ["text"] = "" }
                    : new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
            });
        }
    }

    private void CloseLive(List<JsonObject> events)
    {
        if (_openBlock is { } index)
        {
            events.Add(Stop(index));
        }
        _openBlock = null;
        _openItem = null;
        _openKind = null;
        _openSummaryIndex = null;
    }

    private void Close(List<JsonObject> events)
    {
        CloseLive(events);
        // A call still open at completion is complete; at truncation its arguments are cut off, so it is dropped.
        _calls.Clear();
    }

    private void Finish(List<JsonObject> events, string stopReason, JsonNode? usage)
    {
        var input = Int(usage?["input_tokens"]);
        var cached = Int(usage?["input_tokens_details"]?["cached_tokens"]) ?? 0;
        var finalUsage = new JsonObject
        {
            ["input_tokens"] = input is { } total ? Math.Max(0, total - cached) : estimatedInputTokens,
            ["output_tokens"] = Int(usage?["output_tokens"]) ?? 0,
        };
        if (cached > 0)
        {
            finalUsage["cache_read_input_tokens"] = cached;
        }
        events.Add(new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
            ["usage"] = finalUsage,
        });
        events.Add(new JsonObject { ["type"] = "message_stop" });
        Ended = true;
    }

    public static string Format(IEnumerable<JsonObject> events)
    {
        var sse = new StringBuilder();
        foreach (var e in events)
        {
            sse.Append("event: ").Append(Str(e["type"])).Append("\ndata: ").Append(e.ToJsonString(WireJson.Relaxed)).Append("\n\n");
        }
        return sse.ToString();
    }

    private static JsonObject Delta(int index, JsonObject delta) =>
        new() { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta };

    private static JsonObject Stop(int index) => new() { ["type"] = "content_block_stop", ["index"] = index };

    private static string? Str(JsonNode? node) => ChatRequestTranslator.Str(node);

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}

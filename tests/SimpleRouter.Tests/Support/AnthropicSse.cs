using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleRouter.Tests.Support;

/// <summary>
/// Strict Anthropic Messages SSE parser: enforces event order, sequential block indexes, delta types matching their
/// block (text, thinking, tool_use), usage on message_start, and that accumulated tool input is a JSON object. A stream ends with message_stop or,
/// for a failed stream, a terminal <c>error</c> event (then <see cref="Message.Error"/> is set). Any violation throws.
/// </summary>
public static class AnthropicSse
{
    public sealed record Block(string Type, string Text, string? Id, string? Name, JsonObject? Input, string Signature = "");

    public sealed record Message(string Model, List<Block> Blocks, string? StopReason, JsonObject? Usage, List<string> Events, string? Error);

    public static Message Parse(string sse)
    {
        var frames = sse.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var events = new List<string>();
        var blocks = new List<Block>();
        var text = new System.Text.StringBuilder();
        var json = new System.Text.StringBuilder();
        var signature = new System.Text.StringBuilder();
        int? open = null;
        string? model = null, stopReason = null, error = null;
        JsonObject? usage = null;
        var stopped = false;

        foreach (var frame in frames)
        {
            var lines = frame.Split('\n');
            if (lines.Length != 2 || !lines[0].StartsWith("event: ") || !lines[1].StartsWith("data: "))
            {
                throw new FormatException($"Malformed SSE frame: {frame}");
            }
            var name = lines[0]["event: ".Length..];
            var data = JsonNode.Parse(lines[1]["data: ".Length..])!.AsObject();
            Check(data["type"]!.GetValue<string>() == name, $"event {name} has data.type {data["type"]}");
            Check(!stopped, "event after message_stop");
            Check(error is null, "event after error");
            Check(events.Count > 0 || name == "message_start", "stream must begin with message_start");
            events.Add(name);

            switch (name)
            {
                case "message_start":
                    Check(events.Count == 1, "duplicate message_start");
                    var msg = data["message"]!.AsObject();
                    Check(msg["role"]!.GetValue<string>() == "assistant" && msg["type"]!.GetValue<string>() == "message", "bad message_start");
                    Check(msg["content"]!.AsArray().Count == 0, "message_start content must be empty");
                    model = msg["model"]!.GetValue<string>();
                    Check(msg["usage"]?["input_tokens"] is JsonValue && msg["usage"]?["output_tokens"] is JsonValue, "message_start usage needs input_tokens and output_tokens");
                    break;
                case "ping":
                    break;
                case "content_block_start":
                    Check(open is null, "block started while another is open");
                    Check(stopReason is null, "block after message_delta");
                    var index = data["index"]!.GetValue<int>();
                    Check(index == blocks.Count, $"block index {index}, expected {blocks.Count}");
                    var cb = data["content_block"]!.AsObject();
                    var type = cb["type"]!.GetValue<string>();
                    if (type == "text")
                    {
                        Check(cb["text"]!.GetValue<string>() == "", "text block must start empty");
                    }
                    else if (type == "thinking")
                    {
                        Check(cb["thinking"]!.GetValue<string>() == "", "thinking block must start empty");
                    }
                    else if (type == "tool_use")
                    {
                        Check(cb["input"] is JsonObject { Count: 0 }, "tool_use must start with empty input");
                        Check(!string.IsNullOrEmpty(cb["id"]?.GetValue<string>()) && !string.IsNullOrEmpty(cb["name"]?.GetValue<string>()), "tool_use needs id and name");
                    }
                    else
                    {
                        throw new FormatException($"unexpected block type {type}");
                    }
                    blocks.Add(new Block(type, "", cb["id"]?.GetValue<string>(), cb["name"]?.GetValue<string>(), null));
                    open = index;
                    text.Clear();
                    json.Clear();
                    signature.Clear();
                    break;
                case "content_block_delta":
                    Check(open == data["index"]!.GetValue<int>(), "delta for a block that is not open");
                    var delta = data["delta"]!.AsObject();
                    var deltaType = delta["type"]!.GetValue<string>();
                    if (blocks[^1].Type == "text")
                    {
                        Check(deltaType == "text_delta", $"{deltaType} in text block");
                        text.Append(delta["text"]!.GetValue<string>());
                    }
                    else if (blocks[^1].Type == "thinking")
                    {
                        Check(deltaType is "thinking_delta" or "signature_delta", $"{deltaType} in thinking block");
                        text.Append(delta["thinking"]?.GetValue<string>());
                        signature.Append(delta["signature"]?.GetValue<string>());
                    }
                    else
                    {
                        Check(deltaType == "input_json_delta", $"{deltaType} in tool_use block");
                        json.Append(delta["partial_json"]!.GetValue<string>());
                    }
                    break;
                case "content_block_stop":
                    Check(open == data["index"]!.GetValue<int>(), "stop for a block that is not open");
                    var last = blocks[^1];
                    blocks[^1] = last.Type is "text" or "thinking"
                        ? last with { Text = text.ToString(), Signature = signature.ToString() }
                        : last with { Input = JsonNode.Parse(json.Length == 0 ? "{}" : json.ToString()) as JsonObject ?? throw new FormatException("tool input is not an object") };
                    open = null;
                    break;
                case "message_delta":
                    Check(open is null, "message_delta with an open block");
                    Check(stopReason is null, "duplicate message_delta");
                    stopReason = data["delta"]!["stop_reason"]!.GetValue<string>();
                    usage = data["usage"]!.AsObject();
                    Check(usage["output_tokens"] is not null, "message_delta usage needs output_tokens");
                    break;
                case "message_stop":
                    Check(stopReason is not null, "message_stop before message_delta");
                    stopped = true;
                    break;
                case "error":
                    error = data["error"]!["message"]!.GetValue<string>();
                    Check(!string.IsNullOrEmpty(data["error"]!["type"]?.GetValue<string>()), "error needs a type");
                    break;
                default:
                    throw new FormatException($"unexpected event {name}");
            }
        }
        Check(stopped || error is not null, "stream did not end with message_stop or error");
        return new Message(model!, blocks, stopReason, usage, events, error);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new FormatException(message);
        }
    }
}

using System.Text;
using System.Text.Json.Nodes;
using SimpleRouter.Local;

namespace SimpleRouter.Codex;

/// <summary>Folds Anthropic stream events into the equivalent non-streaming message, or the error that ended the stream.</summary>
public sealed class AnthropicMessageBuilder
{
    private readonly StringBuilder _json = new();
    private JsonObject? _message;
    private JsonArray _content = [];

    /// <summary>The <c>error</c> object of a terminal error event, if the stream failed.</summary>
    public JsonObject? Error { get; private set; }

    public void Apply(JsonObject evt)
    {
        switch (ChatRequestTranslator.Str(evt["type"]))
        {
            case "message_start":
                _message = (JsonObject)evt["message"]!.DeepClone();
                _content = (JsonArray)_message["content"]!;
                break;
            case "content_block_start":
                _content.Add(evt["content_block"]!.DeepClone());
                _json.Clear();
                break;
            case "content_block_delta":
                var block = _content[^1]!.AsObject();
                var delta = evt["delta"]!;
                switch (ChatRequestTranslator.Str(delta["type"]))
                {
                    case "text_delta":
                        block["text"] = ChatRequestTranslator.Str(block["text"]) + ChatRequestTranslator.Str(delta["text"]);
                        break;
                    case "thinking_delta":
                        block["thinking"] = ChatRequestTranslator.Str(block["thinking"]) + ChatRequestTranslator.Str(delta["thinking"]);
                        break;
                    case "signature_delta":
                        block["signature"] = ChatRequestTranslator.Str(block["signature"]) + ChatRequestTranslator.Str(delta["signature"]);
                        break;
                    case "input_json_delta":
                        _json.Append(ChatRequestTranslator.Str(delta["partial_json"]));
                        break;
                }
                break;
            case "content_block_stop" when ChatRequestTranslator.Str(_content[^1]!["type"]) == "tool_use":
                _content[^1]!["input"] = JsonNode.Parse(_json.Length == 0 ? "{}" : _json.ToString());
                break;
            case "message_delta" when _message is not null:
                _message["stop_reason"] = evt["delta"]?["stop_reason"]?.DeepClone();
                _message["stop_sequence"] = evt["delta"]?["stop_sequence"]?.DeepClone();
                _message["usage"] = evt["usage"]?.DeepClone();
                break;
            case "error":
                Error = (JsonObject)evt["error"]!.DeepClone();
                break;
        }
    }

    public JsonObject Message => _message ?? throw new InvalidOperationException("No message_start was applied.");
}

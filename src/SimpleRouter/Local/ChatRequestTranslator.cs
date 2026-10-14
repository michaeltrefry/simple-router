using System.Text;
using System.Text.Json.Nodes;
using SimpleRouter.Api;
using SimpleRouter.Data;

namespace SimpleRouter.Local;

/// <summary>Translates an Anthropic Messages request into an OpenAI Chat Completions request for a local model.</summary>
public static class ChatRequestTranslator
{
    public const string ImageOmitted = "[image omitted: this model does not accept images]";

    public static JsonObject Translate(JsonObject request, CatalogModel model)
    {
        var messages = new JsonArray();
        if (Text(request["system"], "\n\n") is { Length: > 0 } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }
        foreach (var message in request["messages"] as JsonArray ?? [])
        {
            if (Str(message?["role"]) == "assistant")
            {
                messages.Add(Assistant(message!["content"]));
            }
            else
            {
                AddUser(messages, message?["content"], model.SupportsVision);
            }
        }

        var chat = new JsonObject { ["model"] = model.UpstreamModelId, ["messages"] = messages };
        foreach (var (from, to) in new[] { ("max_tokens", "max_tokens"), ("temperature", "temperature"), ("top_p", "top_p"), ("stop_sequences", "stop") })
        {
            if (request[from] is { } value)
            {
                chat[to] = value.DeepClone();
            }
        }
        if (request["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var streaming) && streaming)
        {
            chat["stream"] = true;
            chat["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (model.SupportsTools)
        {
            AddTools(chat, request);
        }
        return chat;
    }

    private static JsonObject Assistant(JsonNode? content)
    {
        if (content is not JsonArray blocks)
        {
            return new JsonObject { ["role"] = "assistant", ["content"] = Str(content) ?? "" };
        }

        var text = new StringBuilder();
        var calls = new JsonArray();
        foreach (var block in blocks)
        {
            switch (Str(block?["type"]))
            {
                case "text":
                    text.Append(Str(block!["text"]));
                    break;
                case "tool_use":
                    calls.Add(new JsonObject
                    {
                        ["id"] = Str(block!["id"]),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = Str(block["name"]),
                            ["arguments"] = block["input"]?.ToJsonString(WireJson.Relaxed) ?? "{}",
                        },
                    });
                    break;
                // Thinking blocks are dropped: a local model cannot verify their signatures.
            }
        }

        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 || calls.Count == 0 ? text.ToString() : null };
        if (calls.Count > 0)
        {
            message["tool_calls"] = calls;
        }
        return message;
    }

    /// <summary>Tool results become <c>tool</c> messages, which must directly follow the assistant's calls; the rest of the turn follows them.</summary>
    private static void AddUser(JsonArray messages, JsonNode? content, bool vision)
    {
        if (content is not JsonArray blocks)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = Str(content) ?? "" });
            return;
        }

        var resultImages = new List<JsonNode>();
        var parts = new List<JsonNode>();
        foreach (var block in blocks)
        {
            switch (Str(block?["type"]))
            {
                case "text":
                    parts.Add(TextPart(Str(block!["text"]) ?? ""));
                    break;
                case "image":
                    parts.Add(ImagePart(block!, vision));
                    break;
                case "tool_result":
                    messages.Add(ToolMessage(block!, vision, resultImages));
                    break;
            }
        }
        parts.InsertRange(0, resultImages);
        if (parts.Count > 0)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray([.. parts]) });
        }
    }

    private static JsonObject ToolMessage(JsonNode block, bool vision, List<JsonNode> images)
    {
        var lines = new List<string>();
        if (block["content"] is JsonArray parts)
        {
            foreach (var part in parts)
            {
                switch (Str(part?["type"]))
                {
                    case "text":
                        lines.Add(Str(part!["text"]) ?? "");
                        break;
                    case "image" when vision:
                        images.Add(ImagePart(part!, vision));
                        break;
                    case "image":
                        lines.Add(ImageOmitted);
                        break;
                }
            }
        }
        else if (Str(block["content"]) is { } text)
        {
            lines.Add(text);
        }

        var content = string.Join("\n", lines);
        if (block["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError)
        {
            content = "Error: " + content;
        }
        return new JsonObject { ["role"] = "tool", ["tool_call_id"] = Str(block["tool_use_id"]), ["content"] = content };
    }

    private static JsonObject TextPart(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ImagePart(JsonNode block, bool vision)
    {
        var source = block["source"];
        var url = Str(source?["type"]) == "base64"
            ? $"data:{Str(source!["media_type"])};base64,{Str(source["data"])}"
            : Str(source?["url"]);
        return vision && url is not null
            ? new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } }
            : TextPart(ImageOmitted);
    }

    private static void AddTools(JsonObject chat, JsonObject request)
    {
        var tools = new JsonArray();
        // Server tools (web search and the like) have no input_schema and cannot run on a local model.
        foreach (var tool in (request["tools"] as JsonArray ?? []).Where(t => t?["input_schema"] is JsonObject))
        {
            var function = new JsonObject { ["name"] = Str(tool!["name"]) };
            if (Str(tool["description"]) is { } description)
            {
                function["description"] = description;
            }
            function["parameters"] = tool["input_schema"]!.DeepClone();
            tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
        }
        if (tools.Count == 0)
        {
            return;
        }
        chat["tools"] = tools;

        if (request["tool_choice"] is not JsonObject choice)
        {
            return;
        }
        chat["tool_choice"] = Str(choice["type"]) switch
        {
            "any" => "required",
            "none" => "none",
            "tool" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = Str(choice["name"]) } },
            _ => "auto",
        };
        if (choice["disable_parallel_tool_use"] is JsonValue d && d.TryGetValue<bool>(out var disabled) && disabled)
        {
            chat["parallel_tool_calls"] = false;
        }
    }

    private static string? Text(JsonNode? content, string separator) => content switch
    {
        JsonArray blocks => string.Join(separator, blocks.Where(b => Str(b?["type"]) == "text").Select(b => Str(b!["text"]))),
        _ => Str(content),
    };

    internal static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

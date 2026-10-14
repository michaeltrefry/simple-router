using System.Text.Json.Nodes;
using SimpleRouter.Api;
using SimpleRouter.Data;
using SimpleRouter.Local;

namespace SimpleRouter.Codex;

/// <summary>
/// Translates an Anthropic Messages request into a Codex Responses request. The Codex backend only streams and rejects
/// <c>max_output_tokens</c>, sampling knobs and <c>metadata</c>, so those are not sent; earlier thinking blocks are
/// dropped since only Anthropic can verify them.
/// </summary>
public static class ResponsesRequestTranslator
{
    public static JsonObject Translate(JsonObject request, CatalogModel model, string promptCacheKey)
    {
        var responses = new JsonObject
        {
            ["model"] = model.UpstreamModelId,
            ["stream"] = true,
            ["store"] = false,
            ["reasoning"] = new JsonObject { ["summary"] = "auto" },
        };
        if (SystemText(request["system"]) is { Length: > 0 } instructions)
        {
            responses["instructions"] = instructions;
        }
        var input = new JsonArray();
        foreach (var message in request["messages"] as JsonArray ?? [])
        {
            if (Str(message?["role"]) == "assistant")
            {
                AddAssistant(input, message!["content"]);
            }
            else
            {
                AddUser(input, message?["content"]);
            }
        }
        responses["input"] = input;
        AddTools(responses, request);
        responses["prompt_cache_key"] = promptCacheKey;
        return responses;
    }

    private static void AddAssistant(JsonArray input, JsonNode? content)
    {
        if (content is not JsonArray blocks)
        {
            AddMessage(input, "assistant", Str(content) is { Length: > 0 } text ? [OutputText(text)] : []);
            return;
        }
        var parts = new List<JsonNode>();
        foreach (var block in blocks)
        {
            switch (Str(block?["type"]))
            {
                case "text":
                    parts.Add(OutputText(Str(block!["text"]) ?? ""));
                    break;
                case "tool_use":
                    // Text before a call is its own message, so the turn's order survives.
                    AddMessage(input, "assistant", parts);
                    parts = [];
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = Str(block!["id"]),
                        ["name"] = Str(block["name"]),
                        ["arguments"] = block["input"]?.ToJsonString(WireJson.Relaxed) ?? "{}",
                    });
                    break;
            }
        }
        AddMessage(input, "assistant", parts);
    }

    /// <summary>Tool results become <c>function_call_output</c> items in place; images inside them follow as a user message.</summary>
    private static void AddUser(JsonArray input, JsonNode? content)
    {
        if (content is not JsonArray blocks)
        {
            AddMessage(input, "user", [InputText(Str(content) ?? "")]);
            return;
        }
        var parts = new List<JsonNode>();
        foreach (var block in blocks)
        {
            switch (Str(block?["type"]))
            {
                case "text":
                    parts.Add(InputText(Str(block!["text"]) ?? ""));
                    break;
                case "image" when Image(block!) is { } image:
                    parts.Add(image);
                    break;
                case "tool_result":
                    AddMessage(input, "user", parts);
                    parts = [];
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = Str(block!["tool_use_id"]),
                        ["output"] = ToolOutput(block, parts),
                    });
                    break;
            }
        }
        AddMessage(input, "user", parts);
    }

    private static string ToolOutput(JsonNode block, List<JsonNode> images)
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
                    case "image" when Image(part!) is { } image:
                        images.Add(image);
                        break;
                }
            }
        }
        else if (Str(block["content"]) is { } text)
        {
            lines.Add(text);
        }
        var output = string.Join("\n", lines);
        return block["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError ? "Error: " + output : output;
    }

    private static void AddMessage(JsonArray input, string role, List<JsonNode> parts)
    {
        if (parts.Count > 0)
        {
            input.Add(new JsonObject { ["type"] = "message", ["role"] = role, ["content"] = new JsonArray([.. parts]) });
        }
    }

    private static JsonObject InputText(string text) => new() { ["type"] = "input_text", ["text"] = text };

    private static JsonObject OutputText(string text) => new() { ["type"] = "output_text", ["text"] = text };

    private static JsonObject? Image(JsonNode block)
    {
        var source = block["source"];
        var url = Str(source?["type"]) == "base64"
            ? $"data:{Str(source!["media_type"]) ?? "image/png"};base64,{Str(source["data"])}"
            : Str(source?["url"]);
        return url is null ? null : new JsonObject { ["type"] = "input_image", ["image_url"] = url };
    }

    private static void AddTools(JsonObject responses, JsonObject request)
    {
        var tools = new JsonArray();
        // Server tools (web search and the like) have no input_schema and only Anthropic can run them.
        foreach (var tool in (request["tools"] as JsonArray ?? []).Where(t => t?["input_schema"] is JsonObject))
        {
            var function = new JsonObject { ["type"] = "function", ["name"] = Str(tool!["name"]) };
            if (Str(tool["description"]) is { } description)
            {
                function["description"] = description;
            }
            function["parameters"] = tool["input_schema"]!.DeepClone();
            function["strict"] = false;
            tools.Add(function);
        }
        if (tools.Count == 0)
        {
            return;
        }
        responses["tools"] = tools;

        var choice = request["tool_choice"] as JsonObject;
        responses["tool_choice"] = Str(choice?["type"]) switch
        {
            "any" => "required",
            "none" => "none",
            "tool" => new JsonObject { ["type"] = "function", ["name"] = Str(choice!["name"]) },
            _ => "auto",
        };
        responses["parallel_tool_calls"] = !(choice?["disable_parallel_tool_use"] is JsonValue d && d.TryGetValue<bool>(out var disabled) && disabled);
    }

    private static string? SystemText(JsonNode? system) => system switch
    {
        JsonArray blocks => string.Join("\n\n", blocks.Where(b => Str(b?["type"]) == "text").Select(b => Str(b!["text"]))),
        _ => Str(system),
    };

    private static string? Str(JsonNode? node) => ChatRequestTranslator.Str(node);
}

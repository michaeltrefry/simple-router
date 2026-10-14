using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SimpleRouter.Tests.Support;

/// <summary>Synthetic Codex Responses stream fixtures.</summary>
public static class ResponsesFixtures
{
    /// <summary>Frames events as the Responses API streams them: a named event per JSON payload.</summary>
    public static string Sse(params string[] events) =>
        string.Concat(events.Select(e => $"event: {JsonNode.Parse(e)!["type"]}\ndata: {e}\n\n"));

    public static HttpResponseMessage Stream(string sse) =>
        new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    public const string Created = """{"type":"response.created","response":{"id":"resp_1","status":"in_progress","output":[]}}""";

    public static string Completed(int input = 120, int output = 45, int cached = 0, string outputItems = "[]") =>
        $$$"""{"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":{{{input}}},"input_tokens_details":{"cached_tokens":{{{cached}}}},"output_tokens":{{{output}}}},"output":{{{outputItems}}}}}""";

    public static string TextDelta(int outputIndex, string text) =>
        $$$"""{"type":"response.output_text.delta","output_index":{{{outputIndex}}},"content_index":0,"delta":{{{JsonValue.Create(text).ToJsonString()}}}}""";

    public static string SummaryDelta(int outputIndex, int summaryIndex, string text) =>
        $$$"""{"type":"response.reasoning_summary_text.delta","output_index":{{{outputIndex}}},"summary_index":{{{summaryIndex}}},"delta":{{{JsonValue.Create(text).ToJsonString()}}}}""";

    public static string ItemAdded(int outputIndex, string type) =>
        $$$"""{"type":"response.output_item.added","output_index":{{{outputIndex}}},"item":{"type":"{{{type}}}","id":"item_{{{outputIndex}}}"}}""";

    public static string ItemDone(int outputIndex, string type) =>
        $$$"""{"type":"response.output_item.done","output_index":{{{outputIndex}}},"item":{"type":"{{{type}}}","id":"item_{{{outputIndex}}}"}}""";

    public static string CallAdded(int outputIndex, string callId, string name) =>
        $$$"""{"type":"response.output_item.added","output_index":{{{outputIndex}}},"item":{"type":"function_call","id":"fc_{{{outputIndex}}}","call_id":"{{{callId}}}","name":"{{{name}}}","arguments":""}}""";

    public static string ArgsDelta(int outputIndex, string fragment) =>
        $$$"""{"type":"response.function_call_arguments.delta","output_index":{{{outputIndex}}},"delta":{{{JsonValue.Create(fragment).ToJsonString()}}}}""";

    public static string CallDone(int outputIndex, string callId, string name, string arguments) =>
        $$$"""{"type":"response.output_item.done","output_index":{{{outputIndex}}},"item":{"type":"function_call","id":"fc_{{{outputIndex}}}","call_id":"{{{callId}}}","name":"{{{name}}}","arguments":{{{JsonValue.Create(arguments).ToJsonString()}}}}}""";

    /// <summary>Reasoning (two summary parts), text, then a tool call whose arguments arrive in fragments.</summary>
    public static readonly string ReasoningTextAndToolCall = Sse(
        Created,
        ItemAdded(0, "reasoning"),
        SummaryDelta(0, 0, "Need the "),
        SummaryDelta(0, 0, "file list."),
        SummaryDelta(0, 1, "Then read it."),
        ItemDone(0, "reasoning"),
        ItemAdded(1, "message"),
        TextDelta(1, "Let me "),
        TextDelta(1, "look."),
        ItemDone(1, "message"),
        CallAdded(2, "call_abc123", "list_files"),
        ArgsDelta(2, "{\"path\":"),
        ArgsDelta(2, "\"src\",\"dep"),
        ArgsDelta(2, "th\":2}"),
        CallDone(2, "call_abc123", "list_files", "{\"path\":\"src\",\"depth\":2}"),
        Completed(input: 120, output: 45, cached: 100));
}

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

    /// <summary>Two calls whose argument fragments interleave.</summary>
    public static readonly string ParallelCalls = Sse(
        Created,
        CallAdded(0, "call_one", "read_file"),
        ArgsDelta(0, "{\"pa"),
        CallAdded(1, "call_two", "read_file"),
        ArgsDelta(1, "{\"path\":"),
        ArgsDelta(0, "th\":\"a.txt\"}"),
        ArgsDelta(1, "\"b.txt\"}"),
        CallDone(0, "call_one", "read_file", ""),
        CallDone(1, "call_two", "read_file", ""),
        Completed());

    /// <summary>Text only, ending the turn.</summary>
    public static readonly string PlainText = Sse(Created, TextDelta(0, "Hello"), ItemDone(0, "message"), Completed(input: 9, output: 1));

    /// <summary>Cut off at max_output_tokens with a call still streaming its arguments.</summary>
    public static readonly string IncompleteWithUnfinishedCall = Sse(
        Created,
        TextDelta(0, "Partial"),
        ItemDone(0, "message"),
        CallAdded(1, "call_cut", "write_file"),
        ArgsDelta(1, "{\"content\":\"abc"),
        """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":5,"output_tokens":64}}}""");

    /// <summary>Cut off at max_output_tokens after a finished call.</summary>
    public static readonly string IncompleteAfterFinishedCall = Sse(
        Created,
        CallDone(0, "call_done", "ls", "{}"),
        """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}""");

    /// <summary>Fails with a server error after some text.</summary>
    public static readonly string FailedAfterText = Sse(
        Created,
        TextDelta(0, "Start"),
        """{"type":"response.failed","response":{"status":"failed","error":{"code":"server_error","message":"The model crashed."}}}""");

    /// <summary>Ends without a terminal event.</summary>
    public static readonly string WithoutTerminal = Sse(Created, TextDelta(0, "Half"));

    /// <summary>Reasoning and text that only arrive as whole items.</summary>
    public static readonly string WholeItems = Sse(
        Created,
        """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","summary":[{"type":"summary_text","text":"Thought."}]}}""",
        """{"type":"response.output_item.done","output_index":1,"item":{"type":"message","content":[{"type":"output_text","text":"Answer."}]}}""",
        Completed());

    /// <summary>A nameless call and a call with an invalid ID and unparsable arguments.</summary>
    public static readonly string NamelessAndInvalidCalls = Sse(
        Created,
        CallDone(0, "call_x", "", "{}"),
        CallDone(1, "call.with.dots", "ls", "not json"),
        Completed());

    /// <summary>A refusal streamed in deltas.</summary>
    public static readonly string Refusal = Sse(
        Created,
        """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"I can't "}""",
        """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"help with that."}""",
        ItemDone(0, "message"),
        Completed());

    /// <summary>A refusal that only arrives as a whole item.</summary>
    public static readonly string WholeRefusal = Sse(
        Created,
        """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"refusal","refusal":"No."}]}}""",
        Completed());

    /// <summary>Two reasoning items carrying encrypted content, one with a summary, then text.</summary>
    public static readonly string EncryptedReasoning = Sse(
        Created,
        SummaryDelta(0, 0, "Thought."),
        """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","summary":[],"encrypted_content":"ENC-A"}}""",
        """{"type":"response.output_item.done","output_index":1,"item":{"type":"reasoning","summary":[],"encrypted_content":"ENC-B"}}""",
        TextDelta(2, "Done."),
        ItemDone(2, "message"),
        Completed());

    /// <summary>Text and a call present only on the completed response.</summary>
    public static readonly string OutputOnlyOnCompleted = Sse(Created, Completed(outputItems:
        """[{"type":"message","content":[{"type":"output_text","text":"Late."}]},{"type":"function_call","call_id":"call_late","name":"ls","arguments":"{}"}]"""));

    /// <summary>Cut off at max_output_tokens, with text and a truncated call only on the terminal response.</summary>
    public static readonly string IncompleteWithTerminalOutput = Sse(
        Created,
        """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"message","content":[{"type":"output_text","text":"Late."}]},{"type":"function_call","call_id":"call_cut","name":"ls","arguments":"{\"pa"}]}}""");

    /// <summary>Every stream fixture by name, and whether it ends with an error event instead of <c>message_stop</c>.</summary>
    public static readonly IReadOnlyDictionary<string, (string Sse, bool Fails)> Streams = new Dictionary<string, (string, bool)>
    {
        [nameof(ReasoningTextAndToolCall)] = (ReasoningTextAndToolCall, false),
        [nameof(ParallelCalls)] = (ParallelCalls, false),
        [nameof(PlainText)] = (PlainText, false),
        [nameof(IncompleteWithUnfinishedCall)] = (IncompleteWithUnfinishedCall, false),
        [nameof(IncompleteAfterFinishedCall)] = (IncompleteAfterFinishedCall, false),
        [nameof(FailedAfterText)] = (FailedAfterText, true),
        [nameof(WithoutTerminal)] = (WithoutTerminal, true),
        [nameof(WholeItems)] = (WholeItems, false),
        [nameof(NamelessAndInvalidCalls)] = (NamelessAndInvalidCalls, false),
        [nameof(Refusal)] = (Refusal, false),
        [nameof(WholeRefusal)] = (WholeRefusal, false),
        [nameof(EncryptedReasoning)] = (EncryptedReasoning, false),
        [nameof(OutputOnlyOnCompleted)] = (OutputOnlyOnCompleted, false),
        [nameof(IncompleteWithTerminalOutput)] = (IncompleteWithTerminalOutput, false),
    };
}

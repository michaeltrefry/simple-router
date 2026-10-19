using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleRouter.Usage;

/// <summary>
/// Observes the Anthropic-shaped response unchanged. Holds one bounded SSE event, or the existing bounded
/// non-stream usage buffer, never a stream. Only usage, completion markers and allowlisted reasons leave the tap.
/// </summary>
public sealed class UsageTap(Stream inner, HttpResponse response, string modelId, ILogger logger) : Stream
{
    private const int MaxLineBytes = 256 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private static readonly byte[] DataPrefix = "data:"u8.ToArray();
    private static readonly byte[] EventPrefix = "event:"u8.ToArray();
    private readonly MemoryStream _pending = new();
    private readonly MemoryStream _event = new();
    private bool? _eventStream;
    private bool _overflow, _eventOverflow, _unreadable, _malformed, _finalized;
    private bool _started, _stopped, _message, _error;
    private string? _stopReason;
    private TokenUsage? _usage;

    public TokenUsage? Usage()
    {
        FinalizeObservation();
        return _usage;
    }

    public RequestCompletion Completion(bool requestedStream, string? failureKind = null, bool cancelled = false, Exception? exception = null)
    {
        FinalizeObservation();
        var streaming = _eventStream ?? requestedStream;
        if (cancelled) return RequestCompletion.Failed(streaming, "client_cancelled", _stopReason);
        if (failureKind is not null) return RequestCompletion.Failed(streaming, failureKind, _stopReason);
        if (exception is not null) return RequestCompletion.Failed(streaming,
            exception is OperationCanceledException ? "upstream_timeout" : "transport_error", _stopReason);
        if (_error) return RequestCompletion.Failed(streaming, "upstream_error", _stopReason);
        if (response.StatusCode is < 200 or >= 300) return RequestCompletion.Failed(streaming, "http_error", _stopReason);
        if (_malformed) return RequestCompletion.Failed(streaming, "malformed_response", _stopReason);
        if (streaming && !_stopped) return RequestCompletion.Failed(true, "truncated_stream", _stopReason);
        if (_unreadable) return new(streaming, "unknown", StopReason: _stopReason);
        if (streaming) return _started && _stopped
            ? new(true, "completed", StopReason: _stopReason)
            : RequestCompletion.Failed(true, "truncated_stream", _stopReason);
        return _message ? new(false, "completed", StopReason: _stopReason)
            : RequestCompletion.Failed(false, "malformed_response", _stopReason);
    }

    private void FinalizeObservation()
    {
        if (_finalized) return;
        _finalized = true;
        if (_eventStream == true)
        {
            EndLine();
            DispatchEvent();
        }
        else if (_eventStream == false && !_overflow)
        {
            Apply(_pending.GetBuffer().AsSpan(0, (int)_pending.Length), streaming: false);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Observe(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Observe(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    private void Observe(ReadOnlySpan<byte> data)
    {
        _eventStream ??= response.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        if (_eventStream == false)
        {
            if (!_overflow && _pending.Length + data.Length > MaxBodyBytes)
            {
                _overflow = _unreadable = true;
                _pending.SetLength(0);
                logger.LogWarning("The response of {model} is over {limit} bytes; its usage is not read and it is recorded at $0", modelId, MaxBodyBytes);
            }
            if (!_overflow) _pending.Write(data);
            return;
        }
        while (!data.IsEmpty)
        {
            var newline = data.IndexOf((byte)'\n');
            var part = newline < 0 ? data : data[..newline];
            if (!_overflow && _pending.Length + part.Length <= MaxLineBytes) _pending.Write(part);
            else _overflow = _unreadable = true;
            if (newline < 0) return;
            EndLine();
            data = data[(newline + 1)..];
        }
    }

    private void EndLine()
    {
        var line = _pending.GetBuffer().AsSpan(0, (int)_pending.Length).TrimEnd((byte)'\r');
        if (_overflow) _eventOverflow = true;
        else if (line.IsEmpty) DispatchEvent();
        else if (line.StartsWith(EventPrefix))
        {
            // An SSE error is a failure even if its data is absent or malformed.
            if (Encoding.UTF8.GetString(line[EventPrefix.Length..]).Trim() == "error") _error = true;
        }
        else if (line.StartsWith(DataPrefix))
        {
            var payload = line[DataPrefix.Length..];
            if (_event.Length + payload.Length + 1 <= MaxLineBytes)
            {
                if (_event.Length > 0) _event.WriteByte((byte)'\n');
                _event.Write(payload);
            }
            else _eventOverflow = _unreadable = true;
        }
        _pending.SetLength(0);
        _overflow = false;
    }

    private void DispatchEvent()
    {
        if (!_eventOverflow && _event.Length > 0)
            Apply(_event.GetBuffer().AsSpan(0, (int)_event.Length), streaming: true);
        _event.SetLength(0);
        _eventOverflow = false;
    }

    private void Apply(ReadOnlySpan<byte> json, bool streaming)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                _malformed = true;
                return;
            }
            var type = Text(root["type"]);
            if (type == "error" || root["error"] is not null) _error = true;
            JsonNode? usage;
            if (streaming)
            {
                if (type == "message_start")
                {
                    _started = root["message"] is JsonObject;
                    if (!_started) _malformed = true;
                }
                if (type == "message_stop") _stopped = true;
                usage = type switch { "message_start" => root["message"]?["usage"], "message_delta" => root["usage"], _ => null };
                if (type == "message_delta") _stopReason = SafeStopReason(root["delta"]?["stop_reason"]) ?? _stopReason;
            }
            else
            {
                _message = type == "message" && root["content"] is JsonArray;
                usage = root["usage"];
                _stopReason = SafeStopReason(root["stop_reason"]);
            }
            if (usage is JsonObject) _usage = (_usage ?? TokenUsage.None).Merge(usage);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            _malformed = true;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string? SafeStopReason(JsonNode? node) => Text(node) is { } reason
        && reason is "end_turn" or "max_tokens" or "stop_sequence" or "tool_use" or "pause_turn" or "refusal" or "model_context_window_exceeded"
        ? reason : null;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

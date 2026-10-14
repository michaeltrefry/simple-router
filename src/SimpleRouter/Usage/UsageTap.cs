using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleRouter.Usage;

/// <summary>
/// Passes the Anthropic-shaped response body through unchanged while reading its token usage: from
/// <c>message_start</c> and <c>message_delta</c> events of a stream (one line held at a time, never the stream), else
/// from the top-level <c>usage</c> of a JSON body (copied up to a cap). Every upstream writes Anthropic's shape, so one
/// tap covers passthrough and translated responses alike.
/// </summary>
public sealed class UsageTap(Stream inner, HttpResponse response) : Stream
{
    private const int MaxLineBytes = 256 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private static readonly byte[] DataPrefix = "data:"u8.ToArray();
    private static readonly byte[] UsageKey = "\"usage\""u8.ToArray();

    private readonly MemoryStream _pending = new();
    private bool? _eventStream;
    private bool _overflow;
    private TokenUsage? _usage;

    /// <summary>The usage the response reported, or null when it reported none (an error, or an oversized body).</summary>
    public TokenUsage? Usage()
    {
        if (_eventStream == true)
        {
            EndLine();
        }
        else if (_eventStream == false && !_overflow)
        {
            Apply(_pending.GetBuffer().AsSpan(0, (int)_pending.Length), root => root["usage"]);
        }
        return _usage;
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
                _overflow = true;
                _pending.SetLength(0);
            }
            if (!_overflow)
            {
                _pending.Write(data);
            }
            return;
        }

        while (!data.IsEmpty)
        {
            var newline = data.IndexOf((byte)'\n');
            var part = newline < 0 ? data : data[..newline];
            if (!_overflow && _pending.Length + part.Length <= MaxLineBytes)
            {
                _pending.Write(part);
            }
            else
            {
                _overflow = true;
            }
            if (newline < 0)
            {
                return;
            }
            EndLine();
            data = data[(newline + 1)..];
        }
    }

    private void EndLine()
    {
        var line = _pending.GetBuffer().AsSpan(0, (int)_pending.Length).TrimEnd((byte)'\r');
        if (!_overflow && line.StartsWith(DataPrefix) && line.IndexOf(UsageKey) >= 0)
        {
            Apply(line[DataPrefix.Length..], data => (data["type"] as JsonValue)?.GetValueKind() == JsonValueKind.String
                ? data["type"]!.GetValue<string>() switch
                {
                    "message_start" => data["message"]?["usage"],
                    "message_delta" => data["usage"],
                    _ => null,
                }
                : null);
        }
        _pending.SetLength(0);
        _overflow = false;
    }

    private void Apply(ReadOnlySpan<byte> json, Func<JsonObject, JsonNode?> usageOf)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject root && usageOf(root) is JsonObject usage)
            {
                _usage = (_usage ?? TokenUsage.None).Merge(usage);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // Not usage we can read; the response itself is unaffected.
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

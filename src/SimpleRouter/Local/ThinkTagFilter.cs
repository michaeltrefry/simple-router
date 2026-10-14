using System.Text;

namespace SimpleRouter.Local;

/// <summary>
/// Removes a leading <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning block from streamed content. Only a block that opens
/// the content (after whitespace) counts, so prose that mentions the tag is kept. Tags split across chunks are handled.
/// </summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private enum State { Leading, Inside, AfterClose, Passthrough }

    private readonly StringBuilder _pending = new();
    private State _state;

    public string Feed(string text)
    {
        var s = _pending.Append(text).ToString();
        _pending.Clear();
        while (true)
        {
            switch (_state)
            {
                case State.Leading:
                    var trimmed = s.TrimStart();
                    if (trimmed.StartsWith(Open, StringComparison.Ordinal))
                    {
                        s = trimmed[Open.Length..];
                        _state = State.Inside;
                        continue;
                    }
                    if (Open.StartsWith(trimmed, StringComparison.Ordinal))
                    {
                        _pending.Append(s);
                        return "";
                    }
                    _state = State.Passthrough;
                    return s;
                case State.Inside:
                    var end = s.IndexOf(Close, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        _pending.Append(s, s.Length - PartialCloseLength(s), PartialCloseLength(s));
                        return "";
                    }
                    s = s[(end + Close.Length)..];
                    _state = State.AfterClose;
                    continue;
                case State.AfterClose:
                    s = s.TrimStart();
                    if (s.Length > 0)
                    {
                        _state = State.Passthrough;
                    }
                    return s;
                default:
                    return s;
            }
        }
    }

    /// <summary>Text held back while it could still be the opening tag; an unterminated reasoning block is dropped.</summary>
    public string Flush()
    {
        var s = _state == State.Leading ? _pending.ToString() : "";
        _pending.Clear();
        return s;
    }

    private static int PartialCloseLength(string s)
    {
        for (var k = Math.Min(Close.Length - 1, s.Length); k > 0; k--)
        {
            if (s.EndsWith(Close[..k], StringComparison.Ordinal))
            {
                return k;
            }
        }
        return 0;
    }
}

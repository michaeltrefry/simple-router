using System.Text;

namespace SimpleRouter.Local;

/// <summary>
/// Splits a leading <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning block off streamed content. Only a block that opens
/// the content (after whitespace) counts, so prose that mentions the tag stays text. Tags split across chunks are handled.
/// </summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private enum State { Leading, Inside, AfterClose, Passthrough }

    private readonly StringBuilder _pending = new();
    private State _state;

    /// <summary>The reasoning and the text in <paramref name="text"/>; either may be empty while a tag could still be forming.</summary>
    public (string Thinking, string Text) Feed(string text)
    {
        var s = _pending.Append(text).ToString();
        _pending.Clear();
        var thinking = "";
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
                        return (thinking, "");
                    }
                    _state = State.Passthrough;
                    return (thinking, s);
                case State.Inside:
                    var end = s.IndexOf(Close, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        var held = PartialCloseLength(s);
                        _pending.Append(s, s.Length - held, held);
                        return (thinking + s[..^held], "");
                    }
                    thinking += s[..end];
                    s = s[(end + Close.Length)..];
                    _state = State.AfterClose;
                    continue;
                case State.AfterClose:
                    s = s.TrimStart();
                    if (s.Length > 0)
                    {
                        _state = State.Passthrough;
                    }
                    return (thinking, s);
                default:
                    return (thinking, s);
            }
        }
    }

    /// <summary>Text held back while it could still be the opening tag, or reasoning held back while it could be the closing one.</summary>
    public (string Thinking, string Text) Flush()
    {
        var s = _pending.ToString();
        _pending.Clear();
        return _state switch
        {
            State.Leading => ("", s),
            State.Inside => (s, ""),
            _ => ("", ""),
        };
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

using System.Text;
using System.Text.Json;
using SimpleRouter.Claude;

namespace SimpleRouter.Tests;

public class ClaudeIdentityTests
{
    private static string[] SystemTexts(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("system").EnumerateArray().Select(b => b.GetProperty("text").GetString()!).ToArray();
    }

    [Fact]
    public void Adds_identity_when_system_is_absent()
    {
        var result = ClaudeIdentity.Ensure(Encoding.UTF8.GetBytes("""{"model":"m"}"""));
        Assert.Equal([ClaudeIdentity.Text], SystemTexts(result));
    }

    [Fact]
    public void Prepends_identity_to_string_system()
    {
        var result = ClaudeIdentity.Ensure(Encoding.UTF8.GetBytes("""{"system":"custom"}"""));
        Assert.Equal([ClaudeIdentity.Text, "custom"], SystemTexts(result));
    }

    [Fact]
    public void Prepends_identity_to_block_system_preserving_block_fields()
    {
        var result = ClaudeIdentity.Ensure(Encoding.UTF8.GetBytes("""{"system":[{"type":"text","text":"custom","cache_control":{"type":"ephemeral"}}]}"""));
        Assert.Equal([ClaudeIdentity.Text, "custom"], SystemTexts(result));
        using var json = JsonDocument.Parse(result);
        Assert.Equal("ephemeral", json.RootElement.GetProperty("system")[1].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("""{"system":"You are Claude Code, Anthropic's official CLI for Claude. More."}""")]
    [InlineData("""{"system":[{"type":"text","text":"You are Claude Code, Anthropic's official CLI for Claude."}]}""")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Leaves_body_unchanged_when_already_identified_or_not_an_object(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        Assert.Same(bytes, ClaudeIdentity.Ensure(bytes));
    }
}

using System.Text.Json.Nodes;
using SimpleRouter.Routing;

namespace SimpleRouter.Tests;

public class SessionKeyTests
{
    private const string SessionUuid = "1b4e28ba-2fa1-4d3b-9c5e-0123456789ab";

    private static JsonObject Request(string firstUser = "build the thing", string? userId = null, string system = "You are helpful.", params string[] later)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = firstUser } };
        foreach (var text in later)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ok" });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        }
        var request = new JsonObject { ["model"] = "default", ["system"] = system, ["messages"] = messages };
        if (userId is not null)
        {
            request["metadata"] = new JsonObject { ["user_id"] = userId };
        }
        return request;
    }

    [Fact]
    public void Header_session_id_is_the_client_session_id_and_wins_over_metadata()
    {
        var identity = SessionKey.Derive("header-session", Request(userId: $"user_abc_account_x_session_{SessionUuid}"));

        Assert.Equal("header-session", identity.ClientSessionId);
        Assert.NotEqual(SessionKey.Derive(SessionUuid, Request()).Key, identity.Key);
        Assert.Equal(SessionKey.Derive("header-session", Request()).Key, identity.Key);
    }

    [Fact]
    public void Bundled_metadata_user_id_yields_its_session_uuid()
    {
        var identity = SessionKey.Derive(null, Request(userId: $"user_0f3a9c_account_9a8b7c6d-0000-4000-8000-000000000000_session_{SessionUuid}"));

        Assert.Equal(SessionUuid, identity.ClientSessionId);
        Assert.Equal(SessionKey.Derive(SessionUuid, Request()).Key, identity.Key);
    }

    [Fact]
    public void Json_metadata_user_id_yields_its_session_id()
    {
        var identity = SessionKey.Derive(null, Request(userId: """{"device_id":"d1","account_uuid":"a1","session_id":"json-session"}"""));

        Assert.Equal("json-session", identity.ClientSessionId);
        Assert.Equal(SessionKey.Derive("json-session", Request()).Key, identity.Key);
    }

    [Fact]
    public void Without_a_client_session_id_the_system_prompt_identifies_the_session()
    {
        var first = SessionKey.Derive(null, Request(system: "Agent A"));
        var sameSystem = SessionKey.Derive(null, Request(system: "Agent A", userId: "user_without_session"));
        var otherSystem = SessionKey.Derive(null, Request(system: "Agent B"));

        Assert.Null(first.ClientSessionId);
        Assert.Equal(first.Key, sameSystem.Key);
        Assert.NotEqual(first.Key, otherSystem.Key);
    }

    [Fact]
    public void Different_first_user_messages_in_one_client_session_get_different_keys()
    {
        var main = SessionKey.Derive("shared", Request(firstUser: "main task"));
        var subAgent = SessionKey.Derive("shared", Request(firstUser: "sub-agent task"));

        Assert.NotEqual(main.Key, subAgent.Key);
        Assert.Equal(main.ClientSessionId, subAgent.ClientSessionId);
    }

    [Fact]
    public void Later_turns_and_moved_cache_control_keep_the_key()
    {
        var firstTurn = Request();
        firstTurn["messages"]![0]!["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "build the thing", ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } },
        };
        var laterTurn = Request(later: ["and test it", "ship it"]);
        laterTurn["messages"]![0]!["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "build the thing" } };

        Assert.Equal(SessionKey.Derive("s", firstTurn).Key, SessionKey.Derive("s", laterTurn).Key);
    }
}

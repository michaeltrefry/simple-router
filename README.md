# simple-router

A single-user localhost router that serves the Anthropic Messages API and routes to Claude subscription, Codex subscription and local OpenAI-compatible models.

## Quick start

```sh
dotnet run --project src/SimpleRouter -- login claude   # sign in to your Claude subscription
dotnet run --project src/SimpleRouter -- key show       # print the gateway key for your client
dotnet run --project src/SimpleRouter                   # serve on http://127.0.0.1:8790
```

Clients authenticate with the gateway key via `X-Weave-Router-Key`, `x-api-key` or `Authorization: Bearer`.

Endpoints: `POST /v1/messages` (streaming and non-streaming), `POST /v1/messages/count_tokens`, `GET /v1/models`.

Data (SQLite database and encryption key ring) lives in `~/.simple-router` by default (`--SimpleRouter:DataDirectory=...`).

## Routing

Each request is served in a tier (`max`, `high`, `mid`, `low`), chosen by:

1. the `x-weave-model-class` header (an unknown value is a 400), else
2. the requested `model` matched against the alias table (`*fable*` → max, `*opus*` → high, `*sonnet*` → mid, `*haiku*` → low), else
3. the default tier (`high`, so `"model": "default"` lands there).

Responses carry `X-Weave-Model-Class: <tier>`. A new session gets the next enabled model in its tier (round-robin, cursor persisted), and keeps it for the life of the session, across restarts. The session key combines the client session ID (`X-Claude-Code-Session-Id`, else the session in `metadata.user_id`, else a hash of the system prompt) with the first user message, so sub-agents get their own pins.

The model catalog, tier membership, aliases and default tier are loaded from `seed.json` into the database on first run (`--SimpleRouter:SeedFile=...`); edit the `Models`, `TierMembers`, `TierAliases` tables and the `default_tier` setting to change them.

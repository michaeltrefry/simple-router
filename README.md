# simple-router

A single-user localhost router that serves the Anthropic Messages API and routes to Claude subscription, Codex subscription and local OpenAI-compatible models.

## Quick start

```sh
dotnet run --project src/SimpleRouter -- login claude   # sign in to your Claude subscription
dotnet run --project src/SimpleRouter -- login codex    # sign in to your ChatGPT (Codex) subscription
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

### Codex subscription

`login codex` runs the Codex CLI's device-code flow: open the printed URL, sign in to ChatGPT and enter the code; the command polls until you do (15 minutes at most) and stores the refresh token and ChatGPT account ID encrypted. Access tokens are refreshed automatically before they expire.

The Codex models (`gpt-6-astra` in max, `gpt-6.1-sol` in high, `gpt-5.6-terra` in mid, `gpt-6-luna` in low) share their tiers with the Claude models in round-robin. Requests are translated to the Codex Responses API (`chatgpt.com/backend-api/codex/responses`, always streamed; a non-streaming client gets the stream folded into one message) and back: reasoning summaries arrive as thinking blocks whose signature carries Codex's encrypted reasoning (prefixed `codex:`), which goes back to Codex on the next turn and is dropped if the session later moves to a Claude model. The Codex backend does not accept `max_tokens`, `temperature`, `top_p`, stop sequences or `metadata`, so they are not sent. Upstream HTTP errors keep their status (a 429 stays a 429). A 401 is retried once after a forced token refresh; a 401/403 that is still a rejected credential becomes a 502 asking for `login codex`, while a plan or usage refusal keeps its error code. A subscription without a stored login is not eligible, so its tier members are skipped; a tier left empty for that reason answers 503 naming the login command. `count_tokens` returns the estimate without calling Codex.

Each subscription's model list is fetched at login, when the server starts (if the last check is older than the interval), every 6 hours (`--SimpleRouter:ModelListRefreshInterval`, `00:00:00` disables it) and on demand through `ModelDiscovery.RefreshAsync`: Claude from `GET /v1/models` with the subscription token, Codex from `GET {codex api base}/models?client_version=...` (`--SimpleRouter:Codex:ClientVersion`). A subscription model missing from its provider's list is never selected and its status reads "unknown model"; an undated configured ID matches a listed dated snapshot (`claude-sonnet-4-5` matches `claude-sonnet-4-5-20250929`). Until a provider's list has been fetched successfully, all of its configured models stay selectable. A failed or empty fetch keeps the last good list and records the error.

### Local models

Mid and Low give new sessions to a healthy local OpenAI-compatible model first (round-robin among them) and fall back to the subscription models only when no local model is eligible. A local model is eligible when it is enabled, its last health check (`GET {baseUrl}/models`, once before serving and then every 30 s, `--SimpleRouter:Local:HealthCheckIntervalSeconds`) succeeded, it supports tools if the request sends any, and its `contextWindow` holds the request's estimated tokens (about 4 characters each) plus `max_tokens` capped at 1024; `max_tokens` sent to it is clamped to what the window leaves after the prompt. A local stream that sends nothing for 120 s (`--SimpleRouter:Local:StreamIdleTimeout`) ends with an error event. A pinned session moves only when its model stops being eligible.

Requests are translated to Chat Completions (`{baseUrl}/chat/completions`) and responses, streamed or not, back to Anthropic messages. Upstream reasoning (`reasoning_content`, `reasoning`, a leading `<think>` block) is omitted, and thinking blocks from earlier turns are dropped. Images are sent as data URLs only to models with `supportsVision`; otherwise they are replaced by a short note. `count_tokens` for a local model returns the estimate without calling it.

The seeded local models are disabled and point at placeholder `localhost` URLs with a placeholder 128k context window. To point them at real endpoints, create `~/.simple-router/seed.local.json` (`--SimpleRouter:SeedOverrideFile=...`); it is applied on every start and only changes the fields it sets. API keys are stored encrypted in the database and never logged.

```json
{
  "models": [
    { "id": "mimo-2.6", "enabled": true, "baseUrl": "http://<host>:<port>/v1", "upstreamModelId": "<served model id>", "apiKey": "<optional>", "contextWindow": 131072, "supportsVision": false }
  ]
}
```

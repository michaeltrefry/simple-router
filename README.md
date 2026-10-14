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

Endpoints: `POST /v1/messages` (streaming and non-streaming), `POST /v1/messages/count_tokens`, `GET /v1/models`, `GET /v1/subscriptions/usage`, `GET /v1/sessions/{client_session_id}/cost` (see [Usage and cost](#usage-and-cost)).

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

The Codex models (`gpt-6-astra` in max, `gpt-6.1-sol` in high, `gpt-5.6-terra` in mid, `gpt-6-luna` in low) share their tiers with the Claude models in round-robin. Requests are translated to the Codex Responses API (`chatgpt.com/backend-api/codex/responses`, always streamed; a non-streaming client gets the stream folded into one message) and back: reasoning summaries arrive as thinking blocks whose signature carries Codex's encrypted reasoning (prefixed `codex:`), which goes back to Codex on the next turn and is dropped if the session later moves to a Claude model. The Codex backend does not accept `max_tokens`, `temperature`, `top_p`, stop sequences or `metadata`, so they are not sent. Limits, plan refusals and overloads move the request to another model (see [Limits and failover](#limits-and-failover)); other upstream HTTP errors keep their status. A 401 is retried once after a forced token refresh; a 401/403 that is still a rejected credential is an account rejection (see below). A subscription without a stored login is not eligible, so its tier members are skipped; a tier left empty for that reason answers the exhaustion 429 naming the login command. `count_tokens` returns the estimate without calling Codex.

Each subscription's model list is fetched at login, when the server starts (if the last check is older than the interval), every 6 hours (`--SimpleRouter:ModelListRefreshInterval`, `00:00:00` disables it) and on demand through `ModelDiscovery.RefreshAsync`: Claude from `GET /v1/models` with the subscription token, Codex from `GET {codex api base}/models?client_version=...` (`--SimpleRouter:Codex:ClientVersion`). A subscription model missing from its provider's list is never selected and its status reads "unknown model"; an undated configured ID matches a listed dated snapshot (`claude-sonnet-4-5` matches `claude-sonnet-4-5-20250929`). Until a provider's list has been fetched successfully, all of its configured models stay selectable. A failed or empty fetch keeps the last good list and records the error.

### Limits and failover

A model that cannot serve a request before anything has been sent to the client is put on a cooldown and the request moves to the next eligible model of the same tier (local first in Mid/Low); the session is re-pinned there and stays. Each model is tried at most once per request, and another tier is never used. Once the first byte has gone to the client there is no failover; a limit reported mid-stream still cools the model for later requests.

| Trigger | Cooldown |
| --- | --- |
| HTTP 429, Codex `usage_limit_reached` | until the reported reset (exhausted limit header window, Codex `resets_at`/`resets_in_seconds`, else `Retry-After`), else 60 s (`--SimpleRouter:Limits:LimitCooldown`) |
| Plan rejection: Codex `usage_not_included` or a model the plan lacks, Claude 404 `not_found_error` or 403 `permission_error` whose message names the model, plan or access | 6 h (`--SimpleRouter:Limits:PlanRejectionCooldown`) |
| Account rejection: Claude 401 or any other 403, Codex 401/403 still rejected after the forced refresh, a token refresh the token endpoint rejects (4xx or malformed) | 5 min for every model of that subscription (`--SimpleRouter:Limits:AuthRejectionCooldown`); status reads `login needed (auth rejected)` |
| 5xx, 529 overloaded (Codex `server_is_overloaded` before anything is written included), subscription or its token endpoint unreachable or failing (5xx), local 503 | `Retry-After`, else 30 s (`--SimpleRouter:Limits:OverloadedCooldown`) |
| Local connection refused | none; marked unhealthy until its next good health check |
| Local context-length rejection (context length/window/size, context overflow, prompt too long, ...), or a request estimated too large for the model | none; skipped for that request |

A successful response whose limit headers say a window is spent (Claude `anthropic-ratelimit-unified[-5h|-7d]-status: rejected` or `-utilization` ≥ 1; Codex `x-codex-primary|secondary-used-percent` ≥ 100) cools the model until that window's reset, so the next request skips it. Resets are read as unix seconds, unix milliseconds or RFC 3339, and a provider-reported reset is capped at 8 days. Cooldowns are stored in the database (they survive restarts), and `ModelSelector.StatusesAsync` reports a cooling model as `cooling until <time>`.

When no model of the tier is left, for any reason (limits, cooldowns, unhealthy locals, missing or rejected logins, no model fits the request), the answer is `429` with `X-Weave-Model-Class`, `{"type":"error","error":{"type":"rate_limit_error","message":"model_class_unavailable: ..."}}` and `Retry-After` set to the seconds until the earliest cooldown ends, or to the limit backoff (60 s, `--SimpleRouter:Limits:LimitCooldown`) when nothing is cooling. The message ends with `Run 'simple-router login <provider>'.` when a tier member's subscription has no login or was just rejected.

### Local models

Mid and Low give new sessions to a healthy local OpenAI-compatible model first (round-robin among them) and fall back to the subscription models only when no local model is eligible. A local model is eligible when it is enabled, its last health check (`GET {baseUrl}/models`, once before serving and then every 30 s, `--SimpleRouter:Local:HealthCheckIntervalSeconds`) succeeded, it supports tools if the request sends any, and its `contextWindow` holds the request's estimated tokens (about 4 characters each) plus `max_tokens` capped at 1024; `max_tokens` sent to it is clamped to what the window leaves after the prompt. A local stream that sends nothing for 120 s (`--SimpleRouter:Local:StreamIdleTimeout`) ends with an error event. A pinned session moves only when its model stops being eligible.

Requests are translated to Chat Completions (`{baseUrl}/chat/completions`) and responses, streamed or not, back to Anthropic messages. Upstream reasoning (`reasoning_content`, `reasoning`, a leading `<think>` block) is omitted, and thinking blocks from earlier turns are dropped. Images are sent as data URLs only to models with `supportsVision`; otherwise they are replaced by a short note. `count_tokens` for a local model returns the estimate without calling it.

The seeded local models are disabled and point at placeholder `localhost` URLs with a placeholder 128k context window. To point them at real endpoints, create `~/.simple-router/seed.local.json` (`--SimpleRouter:SeedOverrideFile=...`); it is applied on every start and only changes the fields it sets. API keys are stored encrypted in the database and never logged.

```json
{
  "models": [
    { "id": "mimo-2.6", "enabled": true, "baseUrl": "http://<host>:<port>/v1", "upstreamModelId": "<served model id>", "apiKey": "<optional>", "contextWindow": 131072, "supportsVision": false,
      "effort": { "mode": "reasoning_effort", "levels": [ "low", "medium", "high" ] } }
  ]
}
```

### Reasoning effort

The client's effort is `output_config.effort` (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`). Without it, an enabled `thinking` budget sets it: under 4,096 tokens `low`, under 16,384 `medium`, under 32,768 `high`, under 65,536 `xhigh`, else `max`. With neither (thinking absent, disabled or adaptive) no effort is sent and the target uses its default. A level the target does not list is sent as the nearest one it does (the lower on a tie).

- Claude: the request is passed through unchanged.
- Codex: `reasoning.effort`, from the model's `effort.levels` (default `minimal` to `xhigh`; `max` becomes `xhigh` unless the model lists `max`).
- Local: per the model's `effort` in the seed or `seed.local.json`. `mode` is `none` (default, nothing sent), `reasoning_effort` (sends `reasoning_effort: <level>`) or `chat_template_kwargs` (sends `kwargs[<level>]` as `chat_template_kwargs`, e.g. `{ "none": { "enable_thinking": false }, "high": { "enable_thinking": true } }`); `levels` lists the levels the model accepts.

A database seeded before a model had an `effort` mapping takes it from `seed.json` on the next start.

## Usage and cost

Every served `/v1/messages` request (not `count_tokens`) is stored in the `RequestUsages` table with its client session ID, model, provider, tier, HTTP status, input/output/cache-read/cache-write tokens, cost, latency and requested `output_config.effort`. Tokens are read from the Anthropic-shaped response as it passes through, without buffering a stream: `message_start` and `message_delta` usage for streams (passthrough Claude and the translated Codex and local streams alike), the top-level `usage` of a JSON body otherwise.

Cost is priced from the `ModelPrices` table (USD per million input, output, cache-read and cache-write tokens, with one-hour cache writes at their own `CacheWrite1hPerMTok` rate), seeded once from `prices` in `seed.json`. **The seeded prices are estimates** of public list prices (Codex prices are guesses; local models are $0), used only to report spend; edit the table to change them. A model without a price row costs $0.

- `GET /v1/sessions/{client_session_id}/cost` → `{"session_id", "request_count", "actual_cost_usd_micros"}` for the ID sent as `X-Claude-Code-Session-Id` (percent-escaped in the path; an escaped `/` is part of the ID) (or found in `metadata.user_id`); `404` when no request is recorded for it.
- `GET /v1/subscriptions/usage` → `{"as_of", "all_exhausted", "resumes_at", "known_credentials", "credentials": [{"provider": "claude"|"codex", "source": "managed", "enabled", "routable", "state": "ok"|"cooling"|"auth_rejected"|"unknown"}]}`. `known_credentials` counts stored subscription logins. `all_exhausted` is true when every enabled subscription model in a tier (of a logged-in subscription) is cooling or auth-rejected; local models never count. `resumes_at` is then the earliest of their cooldown ends, else null. `routable` is false only for an `auth_rejected` login (log in again); a `cooling` login stays routable (it is spent until its reset, and the orchestrator pauses on `all_exhausted`), as does an `unknown` one (no enabled, tiered model of that subscription).

Both sit behind the gateway key like the rest of `/v1`.

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

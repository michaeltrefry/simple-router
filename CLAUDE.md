# simple-router — CLAUDE

Single-user .NET 10 localhost router exposing the Anthropic Messages API. One process: ASP.NET Core minimal APIs + SQLite (EF Core).

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (unit: `tests/SimpleRouter.Tests`, end-to-end: `tests/SimpleRouter.AcceptanceTests`)
- Run server: `dotnet run --project src/SimpleRouter`
- CLI: `dotnet run --project src/SimpleRouter -- login claude` / `-- login codex` / `-- key show`
- New migration: `dotnet tool restore && dotnet ef migrations add <Name> --project src/SimpleRouter -o Data/Migrations`

Any setting can be overridden with `--SimpleRouter:<Name>=<value>` or `SimpleRouter__<Name>` env vars (see `RouterOptions.cs`).

## Layout

- `src/SimpleRouter/RouterApp.cs` — composition root (built-in DI only); used by server, CLI and tests.
- `Gateway/` — router-generated gateway key and `/v1` auth middleware.
- `Claude/` — subscription OAuth (PKCE login, refresh), encrypted credential store, upstream forwarder.
- `Codex/` — Codex subscription: device-code login, refresh, Anthropic ⇄ Responses translation (request, stream, non-streaming fold) and the upstream.
- `Api/` — endpoint mappings (Messages, models, Dark Factory usage/cost) and Anthropic-shaped errors.
- `Routing/` — tier resolution, session-key derivation, round-robin selection with persisted pins (local-first in Mid/Low), catalog seeding (`seed.json`, plus the per-machine `seed.local.json` override in the data directory), `IModelUpstream`, the per-provider upstream contract (an attempt that fails before writing returns `NotServed` so `MessageRouter` fails over), limit headers and persisted per-model cooldowns (`ModelCooldowns`), and subscription model-list discovery (`ModelDiscovery`, timer, per-model status).
- `Local/` — OpenAI-compatible local endpoints: Anthropic ⇄ Chat Completions translation (request, response, SSE stream), health checks.
- `Usage/` — per-request usage recording (`UsageTap` reads token usage off the Anthropic-shaped response body, `UsageRecorder` prices it from `ModelPrices`) and the subscription usage report.
- `Dashboard/` — Blazor Server monitoring UI at `/ui` (outside the `/v1` key check; loopback bind plus a loopback `Host` check, `DashboardHostFilter`, are the boundary). Panels derive from `LivePanel` and reload when `DashboardEvents` fires (usage recorded, pin assigned, cooldown written) or on a slow timer. `/ui/settings` (`SettingsPage`, `Components/Settings/`) edits the catalog through `CatalogSettings` over the Blazor circuit (no mutation HTTP endpoints); routing reads the catalog, tier rules and prices from the database per request, and local edits re-probe `LocalHealth`. Never render tokens, keys or bodies.
- `Data/` — `RouterDbContext` and migrations.

## Conventions

- Bind loopback only. Secrets at rest go through `SecretProtector` (ASP.NET Data Protection).
- Never log secrets, tokens, keys or request/response bodies.
- Real local endpoint hosts and keys belong in `seed.local.json` (gitignored), never in `seed.json`.
- This repo is public: no private identifiers (emails, org names, account IDs, personal paths) in code, commits, fixtures or PR text.
- Upstream endpoints are configurable; tests use fakes (`FakeHandler`, `FakeAnthropic`) and never call real services.
- Tests assert behavior that would break if the production code were removed. Keep files small; minimal code; comments only for non-obvious why.

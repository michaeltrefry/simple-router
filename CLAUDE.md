# simple-router — CLAUDE

Single-user .NET 10 localhost router exposing the Anthropic Messages API. One process: ASP.NET Core minimal APIs + SQLite (EF Core).

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (unit: `tests/SimpleRouter.Tests`, end-to-end: `tests/SimpleRouter.AcceptanceTests`)
- Run server: `dotnet run --project src/SimpleRouter`
- CLI: `dotnet run --project src/SimpleRouter -- login claude` / `-- login codex` / `-- key show` / `-- models refresh` / `-- cert create`
- New migration: `dotnet tool restore && dotnet ef migrations add <Name> --project src/SimpleRouter -o Data/Migrations`

Any setting can be overridden with `--SimpleRouter:<Name>=<value>` or `SimpleRouter__<Name>` env vars (see `RouterOptions.cs`).

## Layout

- `src/SimpleRouter/RouterApp.cs` — composition root (built-in DI only); used by server, CLI and tests.
- `Gateway/` — router-generated gateway key and `/v1` auth middleware.
- `Claude/` — subscription OAuth (PKCE login, refresh), encrypted credential store, upstream forwarder.
- `Codex/` — Codex subscription: device-code login, refresh, Anthropic ⇄ Responses translation (request, stream, non-streaming fold) and the upstream.
- `Api/` — endpoint mappings (Messages, models, Dark Factory usage/cost, SystemOne) and Anthropic-shaped errors.
- `Routing/` — tier resolution, session-key derivation, round-robin selection with persisted pins (local-first in Mid/Low; a client session's later pins in a tier reuse its eligible model without a turn), catalog seeding (`seed.json`, plus the per-machine `seed.local.json` override in the data directory), `IModelUpstream`, the per-provider upstream contract (an attempt that fails before writing returns `NotServed` so `MessageRouter` fails over), limit headers and persisted per-model cooldowns (`ModelCooldowns`), in-memory per-local-model conversation holds (`LocalConversations`: a model with `MaxConversations` steers other conversations to the tier's next model), and subscription model-list discovery (`ModelDiscovery`, timer, per-model status).
- `SystemOne/` — decision models (`ProviderKind.SystemOne`): `SystemOneProxy` forwards `/v1/systemone` bodies to `{baseUrl}/systemone` with the upstream model ID, health-gated, timed out and usage-recorded; `SystemOneTools` are the MCP tools at `/v1/mcp` (stateless, `ModelContextProtocol.AspNetCore`) over the same proxy. Never a tier member, never in `/v1/models`, and the router never calls one on its own (only the `LocalHealth` probe).
- `Local/` — OpenAI-compatible local endpoints: Anthropic ⇄ Chat Completions translation (request, response, SSE stream), health checks.
- `Usage/` — per-request usage recording (`UsageTap` reads token usage off the Anthropic-shaped response body, `UsageRecorder` prices it from `ModelPrices`) and the subscription usage report.
- `Dashboard/` — Blazor Server monitoring UI at `/ui` (outside the `/v1` key check; `DashboardHostFilter` is the boundary: a loopback remote address taken from the TCP connection, plus a loopback `Host` and same-port `Origin`; forwarded headers are forced off in `RouterApp.Build` so `X-Forwarded-For` can never rewrite that address; a local reverse proxy would make every request look local, so do not add one or re-enable forwarded headers). Panels derive from `LivePanel` and reload when `DashboardEvents` fires (usage recorded, pin assigned, cooldown written) or on a slow timer. `UsageCharts` (top of `/ui`) buckets `RequestUsages` per model over 24h/7d/30d and draws them with `StackedBarChart`, server-rendered SVG with no chart script; SQLite cannot compare `DateTimeOffset`, so it reads rows newest-first by `Id` and stops at the window start. `/ui/settings` (`SettingsPage`, `Components/Settings/`) edits the catalog through `CatalogSettings` over the Blazor circuit (no mutation HTTP endpoints); routing reads the catalog, tier rules and prices from the database per request, and local edits re-probe `LocalHealth`. Renaming a local model (`CatalogSettings.RenameAsync`) moves every row keyed by its model ID in one transaction; a new model-keyed table must be added there. Never render tokens, keys or bodies.
- `Https/` — `cert create` (`CertCreateCommand`) and `ServerCertificate`: a single-use private CA (`RouterCertificateFactory`: `CA:true` pathlen 0, DNS-only name constraints, private key discarded after signing, never stored) and the RSA server leaf it signs naming `LanIdentity`'s host names and addresses; the leaf with its key plus the public CA in a PKCS#12 encrypted with `SecretProtector` in an owner-only file under `<data>/certs`, the CA as `router.crt` (what clients trust) and the leaf as `router-leaf.crt` beside it; or the configured `Https:CertificatePath`. With a certificate `RouterApp` serves HTTPS on `ListenAddress:HttpsPort` and keeps plain HTTP on loopback.
- `Data/` — `RouterDbContext` and migrations.

## Conventions

- Bind every IPv4 interface (`0.0.0.0`) by default; `ListenAddress=127.0.0.1` restricts access to this machine. LAN access is for `/v1` only, so never let a non-`/v1` path skip the dashboard filter; when a certificate exists, plain HTTP stays on loopback. Secrets at rest go through `SecretProtector` (ASP.NET Data Protection).
- Never log secrets, tokens, keys or request/response bodies.
- Real local endpoint hosts and keys belong in `seed.local.json` (gitignored), never in `seed.json`.
- This repo is public: no private identifiers (emails, org names, account IDs, personal paths) in code, commits, fixtures or PR text.
- Upstream endpoints are configurable; tests use fakes (`FakeHandler`, `FakeAnthropic`) and never call real services.
- Tests assert behavior that would break if the production code were removed. Keep files small; minimal code; comments only for non-obvious why.

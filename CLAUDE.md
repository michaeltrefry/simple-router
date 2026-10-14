# simple-router — CLAUDE

Single-user .NET 10 localhost router exposing the Anthropic Messages API. One process: ASP.NET Core minimal APIs + SQLite (EF Core).

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (unit: `tests/SimpleRouter.Tests`, end-to-end: `tests/SimpleRouter.AcceptanceTests`)
- Run server: `dotnet run --project src/SimpleRouter`
- CLI: `dotnet run --project src/SimpleRouter -- login claude` / `-- key show`
- New migration: `dotnet tool restore && dotnet ef migrations add <Name> --project src/SimpleRouter -o Data/Migrations`

Any setting can be overridden with `--SimpleRouter:<Name>=<value>` or `SimpleRouter__<Name>` env vars (see `RouterOptions.cs`).

## Layout

- `src/SimpleRouter/RouterApp.cs` — composition root (built-in DI only); used by server, CLI and tests.
- `Gateway/` — router-generated gateway key and `/v1` auth middleware.
- `Claude/` — subscription OAuth (PKCE login, refresh), encrypted credential store, upstream forwarder.
- `Api/` — endpoint mappings and Anthropic-shaped errors.
- `Routing/` — tier resolution, session-key derivation, round-robin selection with persisted pins, catalog seeding (`seed.json`).
- `Data/` — `RouterDbContext` and migrations.

## Conventions

- Bind loopback only. Secrets at rest go through `SecretProtector` (ASP.NET Data Protection).
- Never log secrets, tokens, keys or request/response bodies.
- This repo is public: no private identifiers (emails, org names, account IDs, personal paths) in code, commits, fixtures or PR text.
- Upstream endpoints are configurable; tests use fakes (`FakeHandler`, `FakeAnthropic`) and never call real services.
- Tests assert behavior that would break if the production code were removed. Keep files small; minimal code; comments only for non-obvious why.

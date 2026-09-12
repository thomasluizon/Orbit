# Orbit API

.NET 10 / EF Core / MediatR CQRS REST API. Hosts at `api.useorbit.org`.

## Stack

.NET 10, C# 14 (net10.0's default language version), PostgreSQL via EF Core 10, MediatR 14, FluentValidation 12, JWT Bearer, OpenAI GPT-4.1-mini, BCrypt, Firebase Admin SDK (FCM), Lib.Net.Http.WebPush (VAPID).

## Architecture (one-pager)

Clean architecture, four projects: `Orbit.Api` → `Orbit.Application` → `Orbit.Domain` ← `Orbit.Infrastructure`. CQRS via MediatR. Commands and queries live per-feature folder in `Orbit.Application`. Domain entities use factory methods (`Habit.Create()`, `User.Create()`). Generic repository + Unit of Work in Infrastructure. Each project has a scoped `CLAUDE.md` — read those when editing inside that project.

## Cross-cutting hard rules

These apply everywhere — they override project-local conventions if they conflict.

- **Suppressions are a closed set.** `tools/suppression-allowlist.json` declares every `#pragma warning disable` and `[SuppressMessage]` under `src/`, per file and per rule with a site count and a reason, and `Guard Conventions` fails an undeclared one. The EF scaffolder's migration pragmas are exempted by a visible directory entry, not by a hidden regex. `node tools/check-suppression-allowlist.mjs --print` reseeds the shape; the reasons are written by hand.
- **Timezone:** All user-facing dates MUST use `IUserDateService.GetUserTodayAsync(userId)`. `DateTime.UtcNow` is only for `*AtUtc` timestamps, cache keys, and `TimeZoneInfo` conversion into the user's timezone - Roslyn `ORBIT0004` (`src/Orbit.Analyzers`) blocks other raw `DateTime.UtcNow` reads in Api/Application/Infrastructure and blocks `DateOnly.FromDateTime(DateTime.UtcNow...)` in all four projects with no exemption.
- **Authorization:** default `[Authorize]`; Roslyn `ORBIT0003` (`src/Orbit.Analyzers`) fails the build on a Controller with no class-level `[Authorize]`/`[AllowAnonymous]` (per-action attributes on every action also satisfy it). Exempt (mark `[AllowAnonymous]`): `/health`, `/api/auth/*`, signature-verified webhooks.
- **Validation:** Every new feature needs validators in `Orbit.Application/<Feature>/Validators/` AND domain-entity guards in factory/update methods. The backend is the source of truth — never trust the frontend.
- **Logging levels.** Inject `ILogger<T>`; structured properties in PascalCase, English only (`logger.LogInformation("Action {Property}", value)`). Reserve each level so prod logs carry only signal:
  - **Trace/Debug** — routine per-operation success, AI call lifecycle + token usage, per-step progress, per-delivery sends. Filtered out in prod.
  - **Information** — low-frequency business/lifecycle events only: signup, subscription change, sync-completed-with-N (gate the log on `N > 0`), account deletion, streak-freeze activation, the daily AI-cost summary, service start/stop. Never internal steps.
  - **Warning** — recoverable/degraded paths (retries, empty AI response, best-effort metric-write failure).
  - **Error** — actionable failures. Let Sentry capture exceptions; don't add a redundant manual `LogError(ex)` that mirrors a captured exception — keep exactly one Sentry issue + one console error per unhandled exception (the `UnhandledExceptionHandler` console log is held off Sentry via its `Logging:Sentry:LogLevel` category).
  - Prod minimum levels (framework namespaces → `Warning`) live in `appsettings.Production.json`.
- **No workarounds.** Root-cause every bug. No `TODO`/`FIXME`/`HACK` in committed code. No empty `catch {}`. No `as any`-equivalents like unjustified `null!`.
- **No dead code.** Delete unused methods, types, parameters, branches the moment they become orphaned by your change.
- **Narration comments are `ORBIT0001` build errors** (`src/Orbit.Analyzers`; only `///`/`/** */` XML-doc or a URL-linked WHY note survive; autofix via `dotnet format`). The analyzer is silent in local builds but fails CI — grep for bare `//` before pushing.
- **Redundant transaction rollbacks are `ORBIT0002` build errors** (`src/Orbit.Analyzers`, category Reliability): an explicit `RollbackAsync()`/`Rollback()` on a `using`/`await using`-scoped EF `IDbContextTransaction` is banned — scope disposal already rolls back an uncommitted transaction, so let the using-scope dispose it and keep the `catch` to `ChangeTracker.Clear(); throw;`. A genuinely manually-owned transaction (declared without `using`, or reached via a field/parameter) is left alone. Like ORBIT0001 it is silent in local builds but fails CI (the in-box SDK compiler is older than the analyzer's `Microsoft.CodeAnalysis` 5.6.0, so csc skips it with `CS9057` locally).
- **Missing authorization attributes (`ORBIT0003`, Security), raw `DateTime.UtcNow` for user-facing dates (`ORBIT0004`, Reliability), and a `DbSet<T>` without a `modelBuilder.Entity<T>(...)` config (`ORBIT0005`, Design) are build errors too** (`src/Orbit.Analyzers`; they replaced the Claude-session-only `csharp-authz`/`csharp-tz`/`csharp-fluentconfig` hooks so the rules hold for every tool and every human). Same CI-only visibility as ORBIT0001/0002: run `dotnet test tests/Orbit.Analyzers.Tests` for local proof. Pre-existing deliberate UTC-instant/UTC-window sites carry targeted `#pragma warning disable ORBIT0004` blocks. **A new suppression needs an allowlist ENTRY, not a comment**: `tools/suppression-allowlist.json` is a closed set and `Guard Conventions` fails any `#pragma warning disable` or `[SuppressMessage]` under `src/` that it does not declare, whatever the rule id and whatever the comment says. Each entry carries its file, its rule, its site count and its own reason, so a reviewer reads the justification in the diff instead of following a link out of the repository.

## Cross-repo parity contract

The TypeScript consumer is `thomasluizon/orbit-ui-mobile` (Turborepo: Next.js web + Expo mobile + `@orbit/shared`). When you change an endpoint here, the consumer side MUST update in lockstep:

| API change | Consumer change |
|---|---|
| New endpoint | `packages/shared/src/api/endpoints.ts` constant + Server Action (web) + apiClient call (mobile) + query key (if read) |
| New DTO field | `packages/shared/src/types/*.ts` Zod schema |
| Renamed/removed field | Both Zod schema AND every callsite in both apps |

If you're launched from `orbit-ui-mobile`, do the consumer edits in the same session. This repository carries a `contract-aligner` subagent (`.claude/agents/contract-aligner.md`). Invoke it by name when both repos have staged changes.

## LSP

C# LSP (Roslyn, via the CWM.RoslynNavigator MCP server — install once with `dotnet tool install -g CWM.RoslynNavigator`) is wired into Claude Code through the **mobile repo's** `.mcp.json` (sessions are almost always launched from there), pointed at this repo's `Orbit.slnx`. It exposes symbol-level tools (`find_symbol`, `find_references`, `find_implementations`, `get_type_hierarchy`, `get_diagnostics`, ...) instead of reading whole files. When launched directly from orbit-api, no LSP — fall back to grep/glob/read.

## opencode compatibility

The workflow is tool-agnostic between Claude Code and opencode; `.claude/` stays the single source of truth.

- `AGENTS.md` (repo root) is Codex's entry doc: the worker contract plus `## Code Review Rules`. It DEFERS to this CLAUDE.md for conventions; change behaviour here, never there. (The old never-create-AGENTS.md rule was retired by orbit-ui-mobile's REBUILD.md D22/D26, which dropped opencode from that repo's CI. opencode remains live in THIS repo via `opencode.json`, `.opencode/agents/*.md`, and the `/second-opinion` skill; an AGENTS.md is safe alongside it only because this one defers back to CLAUDE.md instead of forking the rules.)
- `opencode.json` (committed) loads the scoped `tests/CLAUDE.md`.
- `.opencode/agents/*.md` are thin pointers to the matching `.claude/agents/*.md` bodies. Edit behavior in `.claude/agents/` only; when adding a new agent, create both the body and its pointer.

## Git workflow

Branch protection on `main`: no direct pushes, no force pushes, no branch deletion. Branches: `feature/xxx`, `fix/xxx`, `chore/xxx`. **Squash merge only.** **`main` requires zero approving reviews, and `pullfrog-approval` is a required status check pinned to app id `1768019`**: the blocking path is the required contexts, which hold the deterministic `guards.yml` jobs plus `pullfrog-approval`. Pullfrog is the only pull request reviewer. Its review instructions live in the Pullfrog console, never in a repository file, because a pull request can edit any file on its own head. Never reuse a branch after its PR is squash-merged. Never `--no-verify`/`--no-gpg-sign`. **Never perform an admin merge in any shape**: no `gh pr merge --admin`, no direct `PUT /repos/{owner}/{repo}/pulls/{number}/merge`, and no direct GraphQL `mergePullRequest` mutation. Naming the two raw API calls is deliberate; forbidding only the CLI flag leaves both API paths open. The admin override is Thomas's alone: if a merge genuinely needs one, STOP and ask him to merge it himself.

Pre-commit hook: a git-level `pre-commit` (lefthook, `lefthook.yml`) runs `dotnet format Orbit.slnx --include <staged .cs>` on staged `.cs` files and re-stages the fixes. This repo has no Node, so there is no auto-install — bootstrap once per machine with `winget install evilmartians.lefthook` (fallbacks: `scoop install lefthook`, or a GitHub-release binary + `lefthook self-update`), then run `lefthook install` inside the clone to write `.git/hooks/pre-commit`.

## Testing

- xUnit + FluentAssertions. Unit tests only — the integration suite was removed as outdated; do not add integration tests or a real-DB harness.
- Every new feature needs unit tests: commands, queries, validators, domain logic.
- Test accounts bypass email verification via the `TEST_ACCOUNTS` env var (`email:code` pairs) — see `tests/CLAUDE.md`.
- **Query-shape / N+1 guard:** `tests/Orbit.Infrastructure.Tests/Persistence/QueryRoundTripCountTests.cs` runs the filtered-include and batched reads (`GetRetrospectiveQuery`, `GetDailySummaryQuery`, `ExportUserDataQuery` shapes) over SQLite in-memory with a `CountingDbCommandInterceptor` and asserts the SQL round-trip count is invariant to row volume — the tripwire for an N+1 regression. Reuses the shared `SqliteOrbitDbContextFactory` shim (extracted for these tests; the ~6 inline copies remain a dedup follow-up).
- **Benchmarks (not tests):** `bench/Orbit.Benchmarks` is a BenchmarkDotNet console (`net10.0`, `[MemoryDiagnoser][ShortRunJob]`) over the three pure hot paths (`HabitScheduleService`, `HabitMetricsCalculator.Calculate`, `RetrospectiveMetricsCalculator.Compute`). It is deliberately outside `dotnet test`/coverage/Stryker. `benchmark.yml` runs it nightly, exports JSON, and `check_bench.py` compares each mean to `bench/baseline.json` (~50% tolerance, report-only). Run locally: `dotnet run -c Release --project bench/Orbit.Benchmarks -- --filter '*'` (or `--list flat` to enumerate).

## Deployment

Render (Docker, auto-deploy on push to main) + Supabase PostgreSQL (session pooler) + Resend (email) + Firebase (FCM) + Stripe.

## Generated gating matrix

Run `node tools/gating-matrix.mjs` to write `gating-matrix.json` at the repository root. The file is not committed.
The `Gating matrix drift` check generates it twice, verifies deterministic output, and publishes the `gating-matrix` artifact.
Its provenance hash covers every C# input read. Live `AppConfigs` rows can override the compiled defaults it reports.

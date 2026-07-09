# Foundation Stabilization Plan

Last updated: 2026-07-09

## Purpose

This plan keeps foundation work separate from new gameplay systems. Each phase
must pass its quality gate before the next phase starts.

## Phase 0: Baseline And Quality Gates

Status: Complete

Delivered foundation:

- A committed project baseline and a dedicated phase branch.
- Repository-wide editor and line-ending conventions.
- Warnings-as-errors and deterministic .NET builds.
- Locked NuGet dependency graphs.
- Backend unit tests for validation, tokens, connection parsing, and local session
  storage.
- A protected PostgreSQL integration test for the current auth, character, and
  world join happy path.
- Unity runtime, EditMode test, and PlayMode test assemblies.
- A Unity login bootstrap PlayMode smoke test.
- An isolated local PostgreSQL test environment.
- A GitHub Actions backend quality gate.
- Root onboarding and manual test documentation.

## Phase 1: World Join Correctness

Status: Complete

Required outcomes:

- A character cannot be active on more than one world.
- Concurrent ticket creation cannot produce multiple active tickets.
- A WorldServer cannot consume a ticket issued for another world.
- Ticket consumption and world session claiming are transactional.
- World session release and reconnect behavior are idempotent.
- Regression and concurrency tests cover every rule above.

The first implementation should keep ticket consumption and the authoritative
world session claim in PostgreSQL so they can share one transaction. Redis can be
introduced later behind the same session abstraction when scale requires it.

Delivered foundation:

- Ordered, advisory-lock-protected database migrations.
- One active unconsumed ticket per character, backed by a database constraint.
- Character row locking across ticket creation and consumption.
- World-bound consumption that preserves a ticket sent to the wrong server.
- Transactional world-session claim and reconnect token rotation.
- One active world-session lease per character across all worlds.
- Heartbeat extension, graceful shutdown release, and idempotent explicit release.
- Local WorldServer reconnect handling and expired-session replacement.
- Unit, integration, concurrency, and migration regression tests for the rules
  above.

## Later Foundation Phases

### Phase 2: Security And API Resilience

- Service authentication between WorldServer and AuthService.
- Development-only debug endpoints.
- Authentication rate limits, logout, and session revocation.
- Structured API errors, correlation identifiers, and dependency failure mapping.

### Phase 3: World Registry And Operations

- Heartbeat-driven online world status.
- Separate liveness and readiness endpoints.
- Fail-fast configuration validation.
- Safe local port binding and staging-ready service configuration.

### Phase 4: Unity Client Stability

- Request timeout and structured client errors.
- Correct request concurrency and scene transition state.
- Reliable world leave and expired-session handling.
- Camera focus, collision, and Input Actions improvements.

## Quality Gate

Every phase must satisfy all applicable checks:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet test ShooterMmo.slnx --configuration Release --no-build
```

Unity changes must also compile in Unity 6000.5.2f1 and pass both EditMode and
PlayMode test suites. Documentation must be updated in the same change as any
behavior, architecture, configuration, or workflow change.

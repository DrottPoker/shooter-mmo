# Shooter MMO

Shooter MMO is an early classless, profession-driven open-world MMORPG with a
modern third-person shooter direction. The current repository contains the
account, character, world join, local WorldServer, and Unity client
foundation for the first playable MVP.

## Current Foundation

- ASP.NET Core AuthService backed by PostgreSQL.
- Headless .NET WorldServer with LiteNetLib UDP session and movement transport.
- Transactional PostgreSQL world-session leases with reconnect, heartbeat, and
  idempotent release.
- One active session per account, replacement-aware client disconnects,
  authentication rate limits, and service-authenticated WorldServer calls.
- RFC Problem Details responses with correlation identifiers and dependency failure
  mapping.
- UDP-readiness-gated world registration with advertised endpoints, build
  compatibility metadata, timeout-based status, and graceful offline updates.
- Split liveness and protocol-level dependency readiness checks.
- A versioned realtime protocol and fixed-step movement simulation shared by
  .NET and Unity.
- Framework-neutral .NET helpers plus an AuthService-only HTTP helper project.
- Unity 6 client scenes for login, character selection, and a local world preview.
- Timeout-aware Unity API handling, serialized UI operations, and automatic 401
  recovery.
- Server-authoritative Input Action movement with prediction, reconciliation,
  stale-input neutralization, snapshots, stall-recovering remote interpolation,
  shared test-map collision, and collision-safe third-person camera controls.
- Spatial interest management with reliable visibility transitions, per-peer UDP
  quotas, bounded session heartbeats, and low-cardinality network metrics.
- Versioned collision baking with checksummed chunks consumed by both
  WorldServer and Unity prediction through position-driven chunk streaming.
- PostgreSQL and Redis development infrastructure through Docker Compose.

Inventory, combat, persistent world simulation, complex terrain collision, and
dynamic rigid-body simulation are intentionally deferred.

## Requirements

- .NET SDK 10.0.301 or a compatible .NET 10 feature band.
- Docker Desktop with Linux containers.
- Unity Editor 6000.5.2f1.

## Quick Start

Create the ignored local environment file and replace its placeholder secrets:

```powershell
Copy-Item .env.example .env
```

Start local infrastructure:

```powershell
docker compose up -d --wait
```

Run the backend services in separate terminals:

```powershell
dotnet run --project AuthService
dotnet run --project WorldServer
```

AuthService and WorldServer load the repository-root `.env` file for local
development. Real environment variables and command-line configuration override
the file. Never commit `.env`.

Open `shooter-mmorpg-unity-client` in Unity and enter Play Mode from
`Assets/Scenes/LoginMenu.unity`.

See [Project Overview](docs/PROJECT_OVERVIEW.md) for a short orientation and
[Local Development](docs/LOCAL_DEVELOPMENT.md) for the complete manual flow.

## Quality Checks

Run the standard backend checks from the repository root:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
& ./Tools/Verify-DependencyPolicy.ps1
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet test ShooterMmo.slnx --configuration Release --no-build
dotnet run --project Tools/WorldCollisionCompiler -- `
  WorldData/Authoring/local-world-1.collision-authoring.json `
  WorldData/Runtime/Resources/ShooterMmo/WorldCollision/local-world-1 `
  --verify
```

The PostgreSQL integration test is skipped unless a dedicated test connection is
configured. Start the isolated test database and run all tests with:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:SHOOTER_MMO_TEST_POSTGRES = (Get-Content .env | Where-Object { $_ -like "SHOOTER_MMO_TEST_POSTGRES=*" }).Split("=", 2)[1]
dotnet test ShooterMmo.slnx --configuration Release
docker compose -f docker-compose.test.yml down
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
```

The integration test resets the `public` schema. Never point
`SHOOTER_MMO_TEST_POSTGRES` at a development, staging, or production database.

Run Unity tests from `Window > General > Test Runner`:

- EditMode validates client serialization, session state, input, and authored
  gameplay assets.
- PlayMode validates the persistent runtime and realtime client bootstrap.

The same suites can run headlessly with `Tools/Run-UnityTests.ps1`. CI enables
the Unity job on a licensed Windows self-hosted runner when the repository
variable `UNITY_CI_ENABLED` is `true`.

## Repository Layout

- `AuthService`: account, session, character, world, join ticket, HTTP pipeline,
  and service-owned configuration.
- `WorldServer`: headless realtime transport, authoritative simulation, active
  local sessions, and service-owned configuration.
- `Shared`: framework-neutral backend helpers and .NET adapters for shared game
  source under `Shared/DotNet`.
- `GameProtocol`: local Unity package containing the realtime binary contract.
- `GameSimulation`: local Unity package containing shared fixed-step movement.
- `WorldData`: neutral authoring and baked collision chunks shared by the server
  and Unity.
- `Tools/WorldCollisionCompiler`: command-line collision bake and verification
  tool.
- `Tests`: backend unit and PostgreSQL integration tests.
- `shooter-mmorpg-unity-client`: Unity client project and Unity tests.
- `docs`: project overview, architecture, features, product scope, and local
  development documentation.

Repository-wide tool configuration remains in root: `.env.example`, Compose,
`Directory.Build.props`, `global.json`, and `ShooterMmo.slnx`. Service settings
belong under each service's `Config` folder. Unity runtime settings belong under
`Assets/Resources/Config` with their C# definitions under `Assets/Scripts/Config`.

The complete documentation map is available in
[docs/README.md](docs/README.md).

## Development Rules

Repository-specific contribution rules are defined in [AGENTS.md](AGENTS.md).
Behavior, configuration, architecture, and user-facing workflow changes must be
documented and manually testable.

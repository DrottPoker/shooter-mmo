# Shooter MMO

Shooter MMO is an early classless, profession-driven, open-world MMORPG with a
modern third-person shooter direction. This repository contains the global
identity, regional simulation topology, authoritative movement, shared collision,
and Unity client foundation for the first playable MVP.

## Architecture In One Minute

The canonical runtime hierarchy is:

```text
Global Services -> Fleet -> Node -> SimulationWorker -> SimulationAssignment -> Shard
```

- Global accounts and characters can use any region or shard.
- A Fleet groups regional compute, such as EU or US.
- A Node is one machine or container host.
- A SimulationWorker is one headless authoritative process.
- A Shard is a player-selectable copy of the shared game simulation.
- World means shared map and content data, not a server process.
- There are no realms.
- Zones and layers are future scaling units and are not implemented yet.

See [Project Architecture](docs/PROJECT_ARCHITECTURE.md) for the complete model.

## Current Foundation

- ASP.NET Core AuthService with PostgreSQL authority.
- Headless .NET SimulationWorker with LiteNetLib UDP.
- Explicit World, Fleet, Node, Shard, Worker, runtime, and assignment records.
- Capacity-aware shard discovery and exact-runtime placement.
- Worker heartbeat leases, graceful offline handling, stale-owner failover, and
  split-brain process shutdown.
- One active account login and one active simulated character per account.
- Short-lived join tickets bound to character, shard, worker, and runtime.
- Transactional reconnect, heartbeat, expiry, and exact session release.
- Versioned realtime protocol shared by .NET and Unity.
- Server-assigned network entity ids, reliable spawn and despawn, spatial
  interest management, reusable snapshot encoding, aggregate snapshot
  backpressure, UDP quotas, and network metrics.
- Fixed-step server-authoritative movement with Unity prediction,
  reconciliation, and remote interpolation.
- Shared checksummed World collision chunks with ramps, walls, steps, slope
  handling, and position-driven streaming.
- Strict WorldData item authoring with nine representative definitions,
  deterministic runtime catalog and structural fingerprints, stable category,
  tag, equipment-slot, and Secure Container tier identities, and pure item,
  Bag, unitless integer weight, and encumbrance rules. The shared base character
  capacity is 200.
- A Unity item catalog window for searchable gameplay authoring, strict shared
  validation, deterministic baking, structural-change review, and client-owned
  icons and presentation metadata cached from a bundled Resources catalog.
- A transactional PostgreSQL item catalog mirror, constrained item custody
  schema, and idempotent empty item-state bootstrap for existing and new
  characters.
- Account-authenticated conditionally cached item catalog, complete item-state,
  focused bank and Recovery Storage reads, and offline-safe item mutation APIs
  with stable Problem Details codes.
- One internal AuthService item transaction kernel with canonical idempotency,
  stable PostgreSQL lock order, optimistic revisions, atomic item and Bag
  commands, Recovery deliveries, Secure Container tier changes, carried-state
  recomputation, policy lifecycle, quest-grant cleanup, and relational audit.
- Exact-session carry-state admission and heartbeat propagation, with base and
  Bag capacity, monotonic item-state revisions, authoritative sprint limits, and
  one shared encumbrance calculation for SimulationWorker and Unity prediction.
- Structured API errors, correlation ids, rate limits, no-store token responses,
  and split health checks.
- External headless SimulationWorker stress generation with in-memory
  exact-runtime tickets, deterministic bot movement, process-resource sampling,
  bounded latency statistics, and phase timing.
- Long-running active simulation bots that share the real local shard with
  Unity players, use the complete UDP flow, churn through graceful logout and
  login, and keep synthetic identity and session data out of PostgreSQL.
- Client-observed F2 diagnostics for frame timing, prediction, reconciliation,
  snapshot health, payload rates, and visible entities without broadcasting
  server process information to game clients.
- Worker-side operational status logs and metrics that distinguish real players
  from synthetic bots and report CPU, memory, traffic, drops, and tick timing.
- Unity 6 login, character selection, shard selection, and WorldScene flow.
- Temporary UI only. Networking, gameplay, state, service, and tooling code are
  maintained as long-term foundations.

Phases 1 through 8 of the slot-based item foundation are implemented. Shared
content, pure rules, Unity authoring, deterministic baking, the transactional
PostgreSQL catalog mirror, constrained custody schema, and complete empty
character item-state bootstrap now exist. AuthService exposes revision-cached
catalog reads, owned item-state, bank and Recovery Storage reads, and
account-authenticated offline mutation routes. The same transaction kernel owns
policy records, insurance removal, quest-grant cleanup, Secure Container tier
changes, Recovery claims, idempotency, revisions, weight, and audit. Active
characters now mutate through an exact-session, exact-worker SimulationWorker
boundary with live service access and committed carry propagation. Player-facing
Unity inventory state and UI remain a later phase. Carry state drives shared
authoritative and predicted movement.
Combat, persistent NPCs, zones, layers, complex terrain meshes, and production
orchestration remain deferred.

## Requirements

- .NET SDK 10.0.301 or a compatible .NET 10 feature band.
- Docker Desktop with Linux containers.
- Unity Editor 6000.5.2f1.

## Quick Start

Create the ignored local environment file if it does not already exist, then
replace placeholder credentials:

```powershell
Copy-Item .env.example .env
```

Start PostgreSQL and Redis:

```powershell
docker compose up -d --wait
```

Run the two backend processes in separate PowerShell terminals:

```powershell
dotnet run --project AuthService
```

```powershell
dotnet run --project SimulationWorker
```

Expected result:

- AuthService listens on `http://localhost:5000`.
- SimulationWorker binds UDP `27015`, registers
  `local-simulation-worker-1`, and receives assignment to `local-shard-1`.
- `GET http://localhost:5000/api/shards` reports the local shard online.

Open `shooter-mmorpg-unity-client` in Unity and enter Play Mode from
`Assets/Scenes/LoginMenu.unity`.

AuthService and SimulationWorker load the root `.env` for local development.
Real environment variables and command-line values override it. Never commit
`.env`.

## Standard Quality Checks

Run from the repository root:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
& ./Tools/Verify-DependencyPolicy.ps1
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet run --project Tools/ItemCatalogCompiler `
  --configuration Release `
  --no-build -- `
  WorldData/Authoring/Items/core.item-catalog.json `
  WorldData/Runtime/Items/core.item-catalog.json `
  --verify
dotnet test ShooterMmo.slnx --configuration Release --no-build
dotnet run --project Tools/WorldCollisionCompiler -- `
  WorldData/Authoring/local-world-1.collision-authoring.json `
  WorldData/Runtime/Resources/ShooterMmo/WorldCollision/local-world-1 `
  --verify
```

Start the isolated PostgreSQL test database before running integration tests:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:SHOOTER_MMO_TEST_POSTGRES = (Get-Content .env | Where-Object { $_ -like "SHOOTER_MMO_TEST_POSTGRES=*" }).Split("=", 2)[1]
dotnet test ShooterMmo.slnx --configuration Release
docker compose -f docker-compose.test.yml down
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
```

The integration suite resets the `public` schema. It refuses a database name
that does not contain `test`, but the variable must still never target shared or
production data.

Run Unity tests through `Window > General > Test Runner`, or use the licensed
headless workflow documented in [Local Development](docs/LOCAL_DEVELOPMENT.md).

## Repository Layout

| Path | Responsibility |
| --- | --- |
| `AuthService` | Identity, characters, topology, placement, tickets, sessions, item persistence, policy-safe offline item APIs, internal item transactions, HTTP, and owned config |
| `SimulationWorker` | Headless UDP, authoritative simulation, entity state, worker lease, and owned config |
| `Shared` | Framework-neutral backend helpers and .NET shared-source adapters |
| `GameProtocol` | Local Unity package containing protocol source |
| `GameSimulation` | Local Unity package containing shared simulation source |
| `WorldData` | Neutral World content, including checksummed collision chunks and the deterministic item catalog and pure rules |
| `Tools` | Verification, content compilers, stress benchmark, shared headless bot client, and active bot population |
| `Tests` | Backend unit, realtime, and PostgreSQL integration tests |
| `shooter-mmorpg-unity-client` | Unity project and Unity tests |
| `docs` | Architecture, implemented features, setup, and product references |

Repository-wide Compose, SDK, solution, build, and environment contracts remain
in root. Service-specific settings live under each service's `Config` folder.
Unity runtime settings live under `Assets/Resources/Config` with definitions in
`Assets/Scripts/Config`.

## Documentation

Start with [Project Overview](docs/PROJECT_OVERVIEW.md), then use the complete
[documentation index](docs/README.md).

The locked planned item, inventory, carry-weight, death-loot, corpse, insurance,
and recovery rules are in
[Inventory And Death Loot Design](docs/INVENTORY_AND_DEATH_LOOT_DESIGN.md). The
complete dependency-ordered delivery plan is in
[Items And Inventory Implementation Plan](docs/ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

Repository rules are defined in [AGENTS.md](AGENTS.md). Behavior,
configuration, architecture, and user workflows must be documented and manually
testable in the same change.

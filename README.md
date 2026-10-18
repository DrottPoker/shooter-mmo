# MMORPG

MMORPG is an early classless, profession-driven, open-world MMORPG with a
World of Warcraft style fantasy setting and RuneScape-inspired click-to-move
combat. That direction is defined in
[Combat And Setting Design](docs/COMBAT_AND_SETTING_DESIGN.md) and is not
implemented yet. This repository contains the global
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
- A Shard may be rebound to another World between worker runtimes only after it
  is fully offline and its World-local state is drained.
- There are no realms.
- Zones and layers are future scaling units and are not implemented yet.

See [Project Architecture](docs/PROJECT_ARCHITECTURE.md) for the complete model.

## Current Foundation

- ASP.NET Core GameService with PostgreSQL authority, built as a modular
  monolith whose Platform, Accounts, Characters, Placement, and Items projects
  only reach each other through published contracts. See
  [GameService Modules](docs/GAME_SERVICE_MODULES.md).
- Headless .NET SimulationWorker with LiteNetLib UDP.
- Explicit World, Fleet, Node, Shard, Worker, runtime, and assignment records.
- Guarded offline Shard-to-World rebinding plus World-keyed worker content and
  Unity scene selection.
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
- Strict item catalog authoring with eleven fantasy definitions, retired-id
  tombstones, deterministic runtime catalog and structural fingerprints, stable
  category, tag, equipment-slot, and Secure Container tier identities, and pure
  item, equipment stack, blocked-slot, Bag, unitless integer weight, and
  encumbrance rules. The base character carry capacity is 2000.
- A Unity item catalog window for searchable gameplay authoring, strict shared
  validation, deterministic baking, structural-change review, and client-owned
  icons and presentation metadata cached from a bundled Addressables catalog.
- A transactional PostgreSQL item catalog mirror, constrained item custody
  schema, and idempotent empty item-state bootstrap for existing and new
  characters.
- Account-authenticated conditionally cached item catalog, complete item-state,
  focused bank and Recovery Storage reads, and offline-safe item mutation APIs
  with stable Problem Details codes.
- One internal GameService item transaction kernel with canonical idempotency,
  stable PostgreSQL lock order, optimistic revisions, atomic item and Bag
  commands, Recovery deliveries, Secure Container tier changes, carried-state
  recomputation, policy lifecycle, quest-grant cleanup, and relational audit.
- A durable player-death boundary that partitions protected, effective insured,
  and lootable custody once, persists three-section five-minute corpses and
  presentation-only snapshots, restores unexpired corpses to the exact assigned
  worker runtime, and audits idempotent expiry destruction.
- Content-controlled Mob corpse variants with a two-minute live worker default,
  normal restart loss without PostgreSQL cleanup, deterministic idempotent loot
  grant ids, and selected durable boss custody over the shared corpse
  restoration and expiry foundation.
- Exact-session concurrent corpse inspection and mutation with proximity and
  lifetime validation, targeted revisions, bidirectional full and partial item
  transfers, compatible stack merges, atomic ordinary slot and Bag aggregate
  swaps, committed viewer deltas, and no database transaction held across a
  client network wait.
- Exact-session carry-state admission and heartbeat propagation, with base and
  Bag capacity, monotonic item-state revisions, authoritative movement speed, and
  one shared encumbrance calculation for SimulationWorker and Unity prediction.
- A persistent Unity inventory controller with one bundled gameplay and
  presentation catalog cache, monotonic complete and focused snapshots,
  operation-id journaling, authoritative post-operation refresh, reconnect
  restoration, and stable update-required handling for catalog mismatch.
- A temporary uGUI inventory panel on the permanent client foundation, with the
  canonical layout, `B` character-storage, `C` equipment plus character-storage,
  and `I` complete views, plus controls for every currently exposed live item
  and corpse mutation, including atomic occupied
  slot and Bag aggregate swaps, corpse-internal rearrangement, and typed corpse
  equipment destinations.
- Persistent Unity corpse presence and view state with complete chunk assembly,
  monotonic deltas, stable closure handling, a generic replaceable capsule,
  click-to-open interaction, and typed bidirectional drag-and-drop transfers.
- A persistent Gold balance with a ledger, one NPC vendor that sells supplies
  and buys carried items at fixed catalog prices, and shared Iron Vein resource
  nodes mined with a pickaxe, with live Shard depletion and respawn.
- OpenTelemetry metrics, logs, and traces from both server processes, exported
  over OTLP to a local Collector, Prometheus, Loki, Tempo, and Grafana stack with
  a playtest dashboard, a GameService dashboard, and tested Prometheus alert
  rules. Export is opt-in, and hot-path metric recording stays allocation-free.
  See [Monitoring](docs/MONITORING.md).
- Structured API errors, correlation ids, rate limits, no-store token responses,
  Redis-backed distributed authentication throttling, and a bounded Redis
  account-session cache with revocation tombstones.
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
- Bounded item-operation timeouts and payloads, low-cardinality item and corpse
  metrics, correlated lifecycle logs, and audited Recovery, corpse, and permitted
  operation-retention cleanup.
- Unity 6 login, character selection, shard selection, and WorldId-driven scene
  flow through a validated client catalog.
- Temporary UI only. Networking, gameplay, state, service, and tooling code are
  maintained as long-term foundations.

The slot-based item and world-actor foundation is implemented; its status is
summarized in [Project Overview](docs/PROJECT_OVERVIEW.md). Combat, damage,
death-event production, loot-table generation, durable unique actors, zones,
layers, and production orchestration remain deferred.

## Requirements

- .NET SDK 10.0.301 or a compatible .NET 10 feature band.
- Docker Desktop with Linux containers.
- Unity Editor 6000.5.4f1.
- Optional: Unity CLI for Editor automation and coding-agent access, described
  in [Local Development](docs/LOCAL_DEVELOPMENT.md#unity-editor-automation-with-unity-cli).

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

Optionally start the monitoring stack, which shows both services live in
Grafana on `http://127.0.0.1:3000` while `Telemetry__OtlpEndpoint` is set in
`.env`, as `.env.example` does. See [Monitoring](docs/MONITORING.md).

```powershell
docker compose -f docker-compose.observability.yml up -d --wait
```

Run the two backend processes in separate PowerShell terminals:

```powershell
dotnet run --project src/Server/GameService
```

```powershell
dotnet run --project src/Server/SimulationWorker
```

Expected result:

- GameService listens on `http://localhost:5000`.
- SimulationWorker binds UDP `27015`, registers
  `local-simulation-worker-1`, loads the committed world-actor revision, and
  receives assignment to `local-shard-1`.
- `GET http://localhost:5000/api/shards` reports the local shard online.

Open `src/Client` in Unity and enter Play Mode from
`Assets/Scenes/LoginMenu.unity`.

The original focused test map is `development-world-1`. The larger greybox map
is `development-world-2`; its layout and Unity workflow are defined in
[Development Worlds](docs/DEVELOPMENT_WORLDS.md). The local shard is currently
bound to Development World 2 for broader gameplay testing. Switching either
direction remains a separate offline operational step.

GameService and SimulationWorker load the root `.env` for local development.
Real environment variables and command-line values override it. Never commit
`.env`.

## Standard Quality Checks

Run from the repository root:

```powershell
dotnet restore Mmorpg.slnx --locked-mode
& ./tools/Verify-DependencyPolicy.ps1
dotnet format Mmorpg.slnx --verify-no-changes --no-restore
dotnet build Mmorpg.slnx --configuration Release --no-restore
dotnet test Mmorpg.slnx --configuration Release --no-build
dotnet run --project tools/ItemCatalogCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldCollisionCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldActorCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/ProtocolGenerator --configuration Release --no-build -- --verify
& ./tools/Verify-Observability.ps1
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Start the isolated PostgreSQL and Redis test containers before running
integration tests:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:MMORPG_TEST_POSTGRES = (Get-Content .env | Where-Object { $_ -like "MMORPG_TEST_POSTGRES=*" }).Split("=", 2)[1]
$env:MMORPG_TEST_REDIS = (Get-Content .env | Where-Object { $_ -like "MMORPG_TEST_REDIS=*" }).Split("=", 2)[1]
dotnet test Mmorpg.slnx --configuration Release
docker compose -f docker-compose.test.yml down
Remove-Item Env:MMORPG_TEST_POSTGRES, Env:MMORPG_TEST_REDIS
```

Expected result: every test passes and none is skipped. The test step takes
under a minute, and the checks above without Unity about two minutes; the
slowest test project is `Mmorpg.GameService.Items.Tests`.

Every PostgreSQL test gets its own fresh database, copied from a template that
each test run builds once with the migrations and startup seeds. The databases
sit next to the database named in `MMORPG_TEST_POSTGRES` and are named after the
test project, `<base>_<project>_*`. All database names must contain `test`, and
the test role needs `CREATEDB`, which the test container's role has. Use
`127.0.0.1`, not `localhost`, as the host, as `.env.example` does: the test
container publishes its port on IPv4 loopback only, and on Windows every new
connection to `localhost` first waits about two seconds for `::1` to fail. Run
one test run per project and server at a time: a second concurrent run of the
same project fails at once. The variable must still never target shared or
production data. Redis tests write only keys below a unique random prefix and
remove them afterwards. The test Redis container listens on `127.0.0.1:56380`
and keeps no data on disk.

For a fast loop that needs no containers, or to run one test project:

```powershell
dotnet test Mmorpg.slnx --configuration Release --filter "Category!=Integration"
dotnet test tests/Server/Mmorpg.GameService.Items.Tests --configuration Release
```

The test projects, where a new test goes, the traits, the database rules, and
how CI splits these checks into parallel jobs are described in
[Backend Tests](tests/README.md).

Run Unity tests through `Window > General > Test Runner`, or use the licensed
headless workflow documented in [Local Development](docs/LOCAL_DEVELOPMENT.md).
CI runs both Unity suites for every pull request on a self-hosted Windows
runner, described in
[Unity CI Runner](docs/LOCAL_DEVELOPMENT.md#unity-ci-runner).

## Repository Layout

| Path | Responsibility |
| --- | --- |
| `src/Server/GameService` | GameService host: startup, configuration loading, database startup and the frozen baseline migrations, health, and owned config |
| `src/Server/GameService.Platform` | Shared GameService plumbing: Problem Details pipeline, token hashing, Redis connection, transaction scope, migration runner, and the module contract |
| `src/Server/GameService.Accounts` | Accounts, sessions, account and worker authentication, rate limits, and Redis session acceleration |
| `src/Server/GameService.Characters` | Characters, ownership, and currency |
| `src/Server/GameService.Placement` | Topology, worker registry, Shard placement, join tickets, simulation session leases, and Development simulation bots |
| `src/Server/GameService.Items` | Item catalog, item state and transactions, corpses, death loot, Recovery, policies, expiry, maintenance, and item development tools |
| `src/Server/SimulationWorker` | Headless UDP, authoritative simulation, entity and actor state, interaction authority, worker lease, bounded corpse presentation and viewer state, and owned config |
| `src/Server/Common` | Framework-neutral helpers shared by the server processes |
| `src/Server/Telemetry` | OpenTelemetry setup shared by the server processes: export options, resource, metric views, sampling, and OTLP exporters |
| `src/Shared/GameProtocol` | Local Unity package containing the realtime protocol: the schema in `Schema~`, the code generated from it in `Runtime/Generated`, and the hand-written cross-field rules of each family |
| `src/Shared/GameSimulation` | Local Unity package containing shared simulation source |
| `content` | Authored game content: item and actor catalogs with one file per definition, and per-World manifests, spawns, and collision sources |
| `src/Shared/WorldData` | Local Unity package with shared domain rules and compilers, the Unity content tools, and the deterministic `Generated` data compiled from `content` |
| `src/Shared/DotNet` | .NET projects that compile the shared Unity package sources for the server |
| `src/Client` | Unity project and Unity tests |
| `observability` | Local monitoring stack configuration: OpenTelemetry Collector, Prometheus with alert rules and their tests, Loki, Tempo, and the Grafana data sources and dashboards |
| `tools` | Verification, content compilers and their shared pipeline library, the realtime protocol generator, stress benchmark, shared headless bot client, and active bot population |
| `tests` | Backend tests: one test project per server module, shared runtime and tool tests, cross-module integration tests, and shared test infrastructure. See [Backend Tests](tests/README.md) |
| `docs` | Architecture, implemented features, setup, and product references |

Repository-wide Compose, SDK, solution, build, and environment contracts remain
in root. Service-specific settings live under each service's `Config` folder.
Unity runtime settings live under `Assets/Content/Config` with definitions in
`Assets/Scripts/Core`.

## Documentation

Start with [Project Overview](docs/PROJECT_OVERVIEW.md), then use the complete
[documentation index](docs/README.md).

The agreed setting, movement, camera, combat triangle, damage type, weapon,
ammunition, and magic direction is in
[Combat And Setting Design](docs/COMBAT_AND_SETTING_DESIGN.md). The planned
first combat slice, with its decisions and implementation order, is in
[Combat And Mob Slice Plan](docs/COMBAT_AND_MOB_SLICE_PLAN.md). The vendor,
currency, and gathering decisions and their implementation are in
[Vendor And Gathering Plan](docs/VENDOR_AND_GATHERING_PLAN.md).

The item, inventory, carry-weight, death-loot, corpse, insurance, and recovery
rules are in
[Inventory And Death Loot Design](docs/INVENTORY_AND_DEATH_LOOT_DESIGN.md), and
the delivered phases in
[Items And Inventory Delivery History](docs/ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).
The implemented scalable world-actor, NPC, Mob, visual spawn-authoring, and
interaction foundation plus its deferred contract is in
[NPC And Mob System Design](docs/NPC_AND_MOB_SYSTEM_DESIGN.md).
The binary realtime protocol, its schema, and its generator are described in
[Realtime Protocol](docs/REALTIME_PROTOCOL.md).
The authored content layout, its file rules, and the content compilers are in
[Game Content](content/README.md).

Repository rules are defined in [AGENTS.md](AGENTS.md). Behavior,
configuration, architecture, and user workflows must be documented and manually
testable in the same change. [CLAUDE.md](CLAUDE.md) imports the same rules for
Claude Code and adds its verification and Unity CLI workflow notes.

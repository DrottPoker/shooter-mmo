# Project Architecture

This document owns the project-wide architecture: terminology and naming, the
topology model, the process and repository boundaries and their contracts, the
realtime threading and performance contracts, the durable item and world actor
boundaries, the durable data model, runtime identity, trust boundaries, the core
runtime flows, configuration ownership, health, and the future scaling
contract.

Implemented behavior with its routes, error codes, limits, and defaults is in
[Service Features](SERVICE_FEATURES.md). GameService module internals are in
[GameService Modules](GAME_SERVICE_MODULES.md), the wire contract is in
[Realtime Protocol](REALTIME_PROTOCOL.md), telemetry is in
[Monitoring](MONITORING.md), and Unity internals are in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md). The game rules are in
[Game Design](GAME_DESIGN.md), the item rules in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), and the
world actor model in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).
Development map identities and scene coordinates are in
[Development Worlds](DEVELOPMENT_WORLDS.md).

## Canonical Terminology

The following terms are architectural contracts. Do not use `WorldServer`,
`realm`, or `world instance` as aliases for them.

| Term | Definition |
| --- | --- |
| Global Services | Durable services shared by every region and shard |
| World | Shared game content and world data, not a server or process |
| Fleet | A regional or operational compute group, such as EU or US |
| Node | One machine or container host inside a fleet |
| SimulationWorker | One headless process that runs authoritative simulation |
| Worker runtime | One process generation identified by a unique runtime id |
| SimulationAssignment | The authoritative worker-to-shard mapping |
| Shard | A player-selectable copy of the shared world simulation |
| World actor | A server-owned runtime entity created from shared World content |
| NPC | A social or service-oriented world actor assembled from composable capabilities |
| Mob | A combat-oriented world actor with AI, aggro, loot, corpse, and respawn behavior |
| Zone | A future authoritative spatial partition inside a shard |
| Layer | A future population copy of a zone or area inside one shard |

The complete world actor vocabulary, including resource nodes, capabilities,
and the actor identity scopes, is in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md#canonical-terminology).

There is no Realm layer. Accounts, characters, progression, and the future
economy are global. Fleets are compute placement boundaries, not data silos.
Players will be able to use the same character in different regions and shards,
subject to the one-active-session rules.

## Naming

`Mmorpg` is the internal codename until the game has a product name. It is the
root of every namespace (`Mmorpg.GameService`, `Mmorpg.SimulationWorker`,
`Mmorpg.Server.Common`, `Mmorpg.GameProtocol`, `Mmorpg.GameSimulation`,
`Mmorpg.WorldData`, `Mmorpg.Client`, `Mmorpg.Tools`), of the project and Unity
assembly names, and of the Unity packages (`com.mmorpg.*`). Type names, file
names, and Addressables addresses never contain the codename, so a later rename
only touches namespaces, project and package names, and the test environment
variables (`MMORPG_TEST_POSTGRES`, `MMORPG_TEST_REDIS`).

`GameService` is the backend process for accounts, characters, items, corpses,
and Shard placement.

Database identifiers use snake_case, such as `character_item_states`, and JSON
over HTTP and in content files uses camelCase, such as `sessionToken` and
`maximumStackSize`.

Persisted and local infrastructure identifiers keep their original names until
a deliberate migration: the `shooter_mmo` database names and users, the Docker
container and volume names, the `shooter-mmo` Redis key prefix, and the
repository name. Renaming them would orphan existing local data and caches. The
SimulationWorker codes for failed GameService calls also keep an older prefix,
such as `auth_service_timeout`.

## Topology Model

```text
Global Services
  GameService
  PostgreSQL
  Redis
  future economy, social, inventory, and persistence services

World: development-world-1
  stress arena: 64 m, everything inside one interest radius

World: development-world-2
  test World: 384 m, one zone for every developed feature

Fleet: local-fleet
  Node: local-node-1
    SimulationWorker: local-simulation-worker-1
      Worker runtime: generated opaque id per process start
      SimulationAssignment: worker -> local-shard-1
    SimulationWorker: local-simulation-worker-2 (StressArena launch profile)
      SimulationAssignment: worker -> local-shard-2

Shard: local-shard-1
  World: development-world-2
  Fleet: local-fleet

Shard: local-shard-2 (Local Stress Arena)
  World: development-world-1
  Fleet: local-fleet
```

Both development Worlds are registered independently of shard placement from
their canonical manifests. The default local worker requests
`development-world-2` for `local-shard-1`, and the `StressArena` launch profile
requests `development-world-1` for `local-shard-2`; adding another World does
not start it or hot-swap a running shard.

A shard belongs to one fleet and references exactly one World definition at a
time. The binding is accepted runtime state, not permanent character or shard
ownership. A replacement worker proposes its configured `WorldId` on its first
heartbeat. GameService locks the shard row and may persist the new binding only
when there is no healthy assignment, pending unexpired join ticket, active
unexpired simulation session, or open durable corpse. The same transaction
creates the new assignment, so concurrent registration cannot bypass the check.
A running shard cannot hot-swap Worlds. A worker that stopped heartbeating is
treated as gone once the heartbeat timeout passes. Its pending tickets and
simulation sessions are released before the blockers are counted, so a crashed
worker cannot block a rebind until its leases expire. Open durable corpses
always block.

A node also belongs to one fleet. GameService permits an assignment only when
the worker's node and target shard belong to the same fleet. After a successful
offline World rebind, the replacement worker must start with the same `WorldId`
and matching World content.

The current safe scale unit is one active SimulationWorker per shard and one
active shard per SimulationWorker. PostgreSQL partial unique indexes enforce
both sides. Zones and layers will later extend the assignment model when one
process can no longer simulate a complete shard. No placeholder zone or layer
objects exist today.

## System Context

```text
                      HTTPS or local HTTP
Unity Client ------------------------------------> GameService --------> PostgreSQL
     |                                                  ^
     | LiteNetLib UDP                                   | service-authenticated HTTP
     v                                                  |
SimulationWorker --------------------------------------+
     |
     +--------------------------------------------> Redis readiness

GameService --------------------------------------> Redis session cache,
                                                     auth rate limits,
                                                     and readiness
```

Unity uses GameService for identity, characters, shard discovery, and placement.
GameService returns a SimulationWorker endpoint only with a short-lived join
ticket. Realtime gameplay packets travel directly between Unity and the assigned
worker. GameService never proxies realtime traffic.

GameService and SimulationWorker both consume the deterministic WorldData
runtime item catalog as a bundled build input. That is a content dependency,
not a network authority relationship.

## Repository Boundaries

### GameService

`GameService` is the only ASP.NET Core application. It owns:

- Accounts, password hashes, and revocable account sessions.
- Characters, ownership, character looks, levels, and currency with its
  ledger.
- World, Fleet, Node, Shard, SimulationWorker, and SimulationAssignment records.
- Worker heartbeats, online status, capacity, runtime fencing, and failover.
- Capacity-aware shard placement and short-lived join tickets.
- Global simulation-session leases.
- Transactional item catalog mirroring, the constrained durable item schema,
  and complete empty character item-state bootstrap.
- Every durable item mutation, durable corpses, the death partition, Recovery
  Storage, and item maintenance.
- HTTP authentication, policies, rate limiting, Problem Details, correlation
  ids, sensitive response caching rules, health routes, and OpenTelemetry
  export of its metrics, request traces, and logs.
- Bounded Redis cache-aside account-session validation and distributed
  authentication rate limits. PostgreSQL remains the durable session authority.
- PostgreSQL schema migrations plus idempotent topology and item bootstrap.

GameService is one process and one deployable, but its code is a modular
monolith whose project references enforce one dependency order:

```text
Platform <- Accounts <- Characters <- Placement <- Items <- host
```

Each module exposes only its module class, its options, and its `Contracts`
namespace. A module reads, writes, or locks another module's tables only
through the owner's contract, inside the caller's transaction, or through SQL
fragments the owner publishes. A lower module publishes an SPI when a higher
module must react in the same transaction. [GameService Modules](GAME_SERVICE_MODULES.md)
describes the projects, table ownership, the access rules, the lock order,
every contract, and the guard tests.

GameService owns one bounded Npgsql pool per process, sized by
`Database:MaximumPoolSize` to leave headroom on a default 100-connection
PostgreSQL server for worker traffic, health probes, stress sampling,
administration, and shutdown. Deployments with multiple GameService replicas
must budget the combined per-process pool limits below PostgreSQL's
non-reserved connection capacity.

### SimulationWorker

`SimulationWorker` is a .NET Generic Host console application. It does not host
ASP.NET. LiteNetLib owns its UDP endpoint. It owns:

- UDP admission, protocol validation, and join and leave handshakes.
- Exact worker runtime identity and its GameService registration lease.
- Active simulation sessions and connection-to-entity bindings.
- Server-assigned network entity ids and reliable spawn and despawn.
- Fixed-rate authoritative movement and collision queries.
- Input sequence processing, fixed-rate owner snapshots, and budget-scheduled
  remote snapshots.
- Spatial interest management with enter and exit hysteresis.
- Snapshot encoding reuse for peers with equal visibility and MTU-safe chunks.
- Per-peer UDP quotas, fair aggregate snapshot scheduling with reserved burst
  headroom, and low-cardinality realtime metrics.
- Server-side population gauges that distinguish real players, synthetic bots,
  and unauthenticated peers without extending the gameplay protocol.
- Bounded session heartbeat fan-out.
- Live world actors: identity, spawn lifecycle, interaction authority,
  capability dispatch, Mob brains, resource node charges, and gathering timing.
- Server-authoritative combat: health, mana, targets, attack timing,
  projectiles, deaths, respawns, Mob loot rolls, and the ammunition ledger.
- Live corpse discovery, proximity, and viewer state, and worker-owned Mob
  corpses.
- Broadcasting each player's look and visible equipment.
- The development admin tools, only in the Development environment.
- Position-driven world collision chunk streaming.
- Fail-fast configuration, content, and collision validation.

Configuration lives under `src/Server/SimulationWorker/Config`. A process
receives its worker, Fleet, Node, Shard, and desired World identity through
validated configuration. `content/worlds/<WorldId>/world.json` owns map-local
identity, client scene, movement bounds, authoritative spawn, and item service
coordinates. The build copies it with the World's compiled collision and actor
runtime content into one output World directory, and selecting
`SimulationWorker:WorldId` loads that directory. Shared movement physics,
networking, quotas, interest, collision streaming, and corpse radii remain
process policy rather than map content.

#### Realtime Server Structure And Threading Contract

`RealtimeSimulationService` is a thin hosted service. Its public constructor
passes the injected services to `RealtimeServerComposition`, the only place that
creates realtime components, and `ExecuteAsync` runs `RealtimeServer.RunAsync`.
The components are not registered in dependency injection, so other hosted
services cannot reach loop-confined state. All of them live below
`src/Server/SimulationWorker/Realtime` in the `Mmorpg.SimulationWorker.Realtime`
namespace, in folders that mirror the protocol families:

| Folder | Responsibility |
| --- | --- |
| `Transport` | `LiteNetLibRealtimeTransport` owns the `NetManager` and resolves peers by id, `RealtimeConnectionAdmission` decides connection requests, `RealtimePeerSender` sends packets and protocol rejections, `RealtimeDeliveryMethods` maps the delivery of a generated message descriptor to LiteNetLib. |
| `Peers` | `RealtimePeerContext` per connection, `RealtimePeerRegistry` for the peer table, `RealtimePeerLifecycle` for connect and disconnect, `RealtimePeerSupervisor` for delayed disconnects, timeouts, invalidation and the orphan sweep. |
| `Dispatch` | `RealtimeMessageDispatcher` applies the per-packet checks and calls one route per message type from the unauthenticated or authenticated table in `RealtimeMessageRoutes`, which takes each route's channel and delivery from the generated message descriptor. |
| `Operations` | `RealtimeCompletionQueue`, `RealtimeCompletionProcessor`, `RealtimeOperationTracker`, `AuthorityOperationScheduler`, and the completion records. |
| `Session`, `Movement`, `Replication`, `Items`, `Corpses`, `WorldActors`, `Combat`, `Mobs`, `Admin` | One loop-side handler per feature plus, where GameService calls are involved, a `*AuthorityOperations` class for the off-loop part. `SimulationTickLoop` runs the fixed ticks, `EntityPresenceReplicator` owns the interest manager, `SnapshotReplicator` owns snapshot budgets and fairness, `CombatSystem` and `MobSystem` run the combat and Mob phases, and `PlayerCombatAuthority` commits arrow batches and deaths. |

One dedicated thread, named `SimulationWorker realtime loop`, owns the realtime
state. Each iteration polls the transport, drains completed operations within a
budget, runs due simulation ticks, supervises peers, sweeps orphaned sessions,
and hands the queued packets to the transport at once. The thread then blocks
in a high-resolution timer wait until the next tick or the next transport poll,
whichever comes first. Every change to these types must keep the following
contract:

- Loop confinement. Every component except `RealtimeCompletionQueue` and
  `RealtimeOperationTracker` is confined to the loop thread and uses no lock.
  Off-loop code may only call synchronized services, post completions, and log,
  and it may read only the immutable `PeerId` of a peer context.
- The thread boundary is a type boundary. A feature's off-loop part is a
  `*AuthorityOperations` class whose constructor receives only synchronized
  services, the completion queue, and a logger.
- LiteNetLib callbacks run inside `PollEvents` because `UnsyncedEvents` stays
  false. A freed peer id can only be reused after its disconnect event, which is
  what makes resolving peers by id safe. Never enable `UnsyncedEvents`.
- Async work starts inline on the loop and is tracked; it is never wrapped in
  `Task.Run` or `Task.Yield`. The synchronous prefix matters: a detached release
  removes the local session before its first await, world interaction open,
  close, and rejections complete synchronously, and an uncontended corpse
  mutation acquires its gate synchronously.
- All completions use one FIFO queue across peers and features, and each
  completion names the handler that finalizes it. A pass processes at least one
  completion and stops when the budget of one eighth of a tick is used.
- Every finalizer first checks that the peer context still owns its id and is
  connected, then that the session id and token still match. A stale corpse
  completion releases a view reservation only for a context that still owns its
  id.
- Every durable operation of one character runs in that peer's authority lane,
  one at a time: item, corpse, world interaction, and gathering operations,
  arrow batches, and the death. Client operations queue at most eight behind
  the running one. An arrow batch and a death are system operations that the
  limit never drops: at most one batch waits, and a death rejects every waiting
  client operation.
- Peers are iterated in registry insertion order. Snapshot fairness and
  broadcast order depend on it, so the table stays a plain dictionary.
- A tick runs, in this order: collision streaming around the players, the World
  spawn, and moving Mobs; carry state and movement per player in entity id
  order; the grid of player positions; due world actor timers and world actor
  activity; Mob brains and movement; the combat phase; interaction
  revalidation every fifth of a second; the gathering pass; changed looks and
  snapshots at the snapshot rate; and corpse presence twice per second. At most
  five ticks catch up before the clock resynchronizes.
- Shutdown stops the transport, waits for in-flight operations, lets the combat
  authority commit the arrow shots it can still name exactly, and then clears
  combat state, peers, interest, bindings, entities, item and carry state,
  corpse viewers, interaction leases, world actors, cached corpse views, and the
  orphan sweep state, zeroes the population gauges, and logs
  `Realtime transport stopped.` last. Other completions produced during
  shutdown are not finalized.
- A failing packet or completion disconnects only its own peer with
  `internal_error`. An exception in a tick, peer supervision, or the orphan
  sweep still stops the host.
- Components log through the service's `ILogger<RealtimeSimulationService>`, so
  the log category and templates stay stable. Completion record names appear in
  the failure log and must not change.
- Corpse range checks keep their exact float arithmetic, and the realtime code
  never changes `src/Shared`.

`IRealtimePeerConnection` and `IRealtimePeerDirectory` are the transport seam.
Tests drive single components in memory with recording peers, and
`RealtimeServerHarness` drives the whole service over real UDP with a scripted
GameService.

### Server Common And Telemetry

`src/Server/Common` (`Mmorpg.Server.Common`) contains backend helpers that have
more than one backend consumer:

- Optional root `.env` loading.
- Connection-string parsing for PostgreSQL, Redis, and service endpoints.
- The Redis health probe and the common health response models.
- Identifier validation and deterministic scoped GUIDs for derived ids.
- The precise loop timer that the realtime loop waits with.
- File loaders for World manifests, the compiled item catalog, and the
  character appearance content.

It must not become a dumping ground for feature logic. `src/Server/Telemetry`
(`Mmorpg.Server.Telemetry`) builds the OpenTelemetry pipeline of both processes,
as [Monitoring](MONITORING.md) describes.

### GameProtocol

`src/Shared/GameProtocol/Runtime` is a local Unity package and the single source
for the binary realtime contract. `src/Shared/DotNet/GameProtocol` compiles the
same files for backend processes and tests.

The protocol is described once in
`src/Shared/GameProtocol/Schema~/realtime-protocol.json`. `tools/ProtocolGenerator`
turns the schema into `Runtime/Generated`: the version, channels, limits,
`RealtimeMessageType`, the message descriptors, the protocol enums, and the
models, codecs, and validation of every message family. The static
`RealtimeProtocol` facade is one partial class: `RealtimeProtocol.cs` holds the
framing and the span-based packet reader and writer that every generated codec
uses, and a family keeps a folder only for its hand-written cross-field rules.
[Realtime Protocol](REALTIME_PROTOCOL.md) describes the wire format, the schema,
the families, the descriptors, and the versioning rules.

### Realtime Performance Contract

Every new replicated or authoritative gameplay feature must define its load
shape before it can be treated as complete:

- State the worst-case producer count, recipient count, update cadence, encoded
  bytes, and database operations per player action.
- Keep owner feedback, input acknowledgement, session leases, and reliable
  authority results independent from degradable remote presentation traffic.
- Batch or reuse identical encoded state across recipients. Do not encode the
  same immutable update once per viewer.
- Put a bounded scheduler, queue, or aggregate coordinator in front of shared
  resources. One hot aggregate must not consume one database connection per
  waiting client.
- Keep every unreliable packet within LiteNetLib's `1023` byte single-packet
  limit. Do not depend on hidden transport fragmentation.
- Define the overload behavior explicitly. Dense remote state may reduce its
  fair update cadence, but owner reconciliation must remain fixed-rate and
  measurable.
- Add low-cardinality timing, backlog, allocation, byte, fan-out, and deferred
  work telemetry for any new high-frequency path, register its meter for export,
  and keep its recording allocation-free.
- Extend `StackStressGenerator` with a stable workload before raising a
  supported player tier. Compare the same mode, seed, bot count, ramp, and
  duration before and after an optimization.

Per-entity tasks, timers, database sessions, and HTTP polling remain prohibited
for moving populations. A feature that introduces all-to-all work must either
prove the bounded tier or add spatial, frequency, or byte-budget degradation.

### GameSimulation

`src/Shared/GameSimulation/Runtime` is the shared fixed-step simulation source.
Unity uses it directly as a local package and `src/Shared/DotNet/GameSimulation`
compiles it for SimulationWorker.

It owns click-to-move path following, the path limits, the route planner,
carry-state encumbrance, gravity, facing, capsule collision against oriented
boxes and one-sided triangles, steps, slopes, bounds, collision raycasts, and
collision-data codecs. `MovementPathPlanner` runs A* on a fine internal grid
sampled from the collision world, with several walkable levels per cell, and
string pulls the route into at most 32 waypoints. The Unity client plans player
routes with it, and SimulationWorker plans Mob routes with the same code. Its
carry wrapper delegates capacity, hard-cap, and multiplier arithmetic to the
pure WorldData item rules. A carry tuple above the hard cap is valid, because
death can leave an involuntary overflow. Only voluntary transitions are
limited, through the shared `EncumbranceRules.IsAllowedVoluntaryTransition` rule
that GameService and the Unity target advisor both use. It has no Unity,
transport, database, or presentation dependency.

Movement is bit-identical in .NET and Unity:

- Shared simulation math wraps every float intermediate in an explicit `(float)`
  cast. Unity's Mono runtime keeps float intermediates in double precision and
  rounds only stored values, while .NET rounds every operation, so without the
  casts Unity prediction drifts from the worker. Sine, cosine, and arctangent
  come from the platform C runtime in double precision and are rounded to
  float, so a libm difference between platforms only matters in the rare case
  where it crosses a float rounding boundary. The world actor compiler rounds
  every float step of its spawn group and area placement the same way, so the
  Unity Editor places exactly what the .NET tools place.
- `GameSimulationCompatibility.Revision` names the movement simulation revision.
  Workers report it in their heartbeat, the client refuses a placement or join
  acceptance with another revision, and every change to movement results needs
  a new revision. A golden test runs one shared
  movement scenario in .NET and in Unity EditMode and pins the revision together
  with a fingerprint of the result, and a second fingerprint pins the route
  planner, so a movement change without a revision bump, or a runtime that
  diverges, fails.

Combat is not part of GameSimulation. Nothing about combat is predicted, so it
runs only in SimulationWorker, as
[Server-Authoritative Combat](#server-authoritative-combat) describes.

### WorldData

Authored content and compiled data are separate. `content/` at the repository
root is the only authored source: the item and actor catalogs with one file per
definition, the combat rules, the character appearance content, and one
directory per World with its `world.json`, actor spawns, and collision source.
The `src/Shared/WorldData` Unity package holds the shared domain rules and
compilers, the Unity content tools, and the deterministic data compiled from
`content/` below `Generated/`. `Mmorpg.WorldData.Content.ContentLayout` defines
every content and generated location and the definition file naming rules for
Unity, the .NET tools, and tests. A shard references one World and its worker
loads that World's manifest and compiled data. [Game Content](../content/README.md)
describes the layout and the pipeline.

`tools/WorldCollisionCompiler`, `tools/ItemCatalogCompiler`, and
`tools/WorldActorCompiler` share the `tools/ContentPipeline` library for their
command line, strict JSON parsing, and generated-file verification. Each
compiles every World at once and fails verification for stale or orphaned
generated data. `tools/WorldCollisionCompiler` builds and verifies the same data
consumed by SimulationWorker and Unity. Static collision is deterministic and
shared. Dynamic collision uses a mutable spatial hash behind the same
collision-query interface, leaving room for doors, lifts, and other server-owned
objects.

`content/items` owns the strict neutral item catalog source, and
`src/Shared/WorldData/Generated/Items` contains its deterministic compiled form.
The catalog has stable identities, one complete revision, per-definition and
per-tier structural fingerprints, and no Shard identity. Framework-neutral
catalog and pure rule source is compiled for Unity by the `Mmorpg.WorldData`
assembly and for .NET tooling and tests through `src/Shared/DotNet/WorldData`.
Neither compilation target becomes an authority. GameService validates and
mirrors the compiled catalog at startup, and every durable mutation reapplies
the authoritative rules in GameService.

Neutral actor definitions live below `content/actors`. Each World owns its
spawn authoring at `content/worlds/<WorldId>/actor-spawns.json` and
deterministic runtime content at
`src/Shared/WorldData/Generated/Worlds/<WorldId>/world-actors.json`.
`tools/WorldActorCompiler`, SimulationWorker startup, Unity Editor authoring,
and tests use the same framework-neutral compiler and runtime validator. Unity
scene authoring imports and exports that content but does not replace it as the
source consumed by SimulationWorker.

The pure item rules cover stack compatibility, Bag slot tag acceptance,
equipment compatibility, empty and non-empty Bag locations, Bag containment-cycle rejection, item policy capabilities, unitless
integer weight arithmetic, the 140 percent hard cap, the linear fixed-point
encumbrance multiplier, vendor pricing (`VendorPricing`), and the Loot all plan
(`CorpseLootAllPlanner`). GameService and the Unity client run the same rule
code, so the client shows what the server will decide. GameSimulation wraps the
carry tuple in an immutable shared movement state and applies the same
multiplier in SimulationWorker and Unity prediction. These rules have no HTTP,
PostgreSQL, UnityEngine, or SimulationWorker runtime dependency.

`src/Shared/WorldData/Editor/Items` contains the Editor-only
`Mmorpg.WorldData.Editor` assembly and the canonical
`MMORPG > Tools > Item Catalog` window. It edits the neutral authoring JSON and
invokes the same strict .NET compiler used by CI. Runtime WorldData assemblies
do not reference `UnityEditor`.

`src/Shared/WorldData/Editor/ContentTools` is the UI Toolkit foundation that the
content tools share: Item Catalog, Actor Studio, and Spawn Authoring. It
provides a list with search, filters, and multi-selection; fields that edit
every selected entry at once and show a mixed state when the entries differ;
live validation with the shared compilers inside Unity, with each finding
mapped to its entry and field; and undo through Unity's own Undo. Every edit
records an exact JSON snapshot of the edited content, so Ctrl+Z, the Edit menu,
and Undo History work as elsewhere in Unity, and null never turns into an empty
object on undo. A vector field edits only the axis that changed, and a
continuous edit such as a Scene view handle drag becomes one undo step. A tool
window keeps its undo state across script reloads and asks to save unsaved
changes when it closes. The compilers, the content files, and
`ContentFileTransaction` are shared with the command-line tools, so a tool never
produces other compiled content than CI.

`Assets/Editor` contains project-local orchestration tools below the shared
`MMORPG > Tools` menu root. `Inventory Item Grants` runs guarded,
machine-readable GameService Development commands and renders their response;
it never references PostgreSQL or item persistence directly, and GameService
applies every requested grant through the item transaction kernel. Its
implementation is in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#development-item-tools).

Unity owns a separate client presentation catalog keyed by stable definition id
and loaded through Addressables. It references icons, localization keys,
fallback text, garments, and prefab presentation keys, but cannot duplicate or
override authoritative gameplay fields, and server item state carries
definition ids instead of presentation assets, as
[Client Presentation](INVENTORY_AND_DEATH_LOOT_DESIGN.md#client-presentation-and-catalog-caching)
describes.

### Unity Client

`src/Client` owns presentation, input, prediction, reconciliation,
interpolation, scene transitions, HTTP serialization, and the persistent UDP
client. It never owns authoritative gameplay state: it predicts movement and
item operations for display and reconciles with the authoritative results. Its
code is split into area assemblies whose dependency direction the compiler
enforces, as [Assemblies](UNITY_CLIENT_ARCHITECTURE.md#assemblies) lists, and
all runtime content, including World scenes and generated collision, loads
through Addressables. The client internals are in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md).

The Debug menu (F2) is an observation surface for the local client. It derives
frame, prediction, reconciliation, snapshot, and payload metrics and the
player's coordinates from local state, and draws the baked collision around the
player on request. Worker process and server-wide population data stay in
SimulationWorker logs and metrics. The Admin menu (F3) holds development tools
that the servers carry out only in the Development environment: the look, the
walk speed, a teleport, full health, and invulnerability.

### Tests And Tools

Backend tests live in `tests`, one test project per server module plus shared,
tools, and integration projects; unit, socket-level, and isolated PostgreSQL
tests live with the module they test. Unity tests live inside the Unity
project. [Backend Tests](../tests/README.md) describes the layout, where a new
test goes, and the database rules.

`tools` holds repository-wide verification, the content compilers, the protocol
generator, the art pipeline scripts below `tools/art`, the CI runner scripts
below `tools/ci`, and the external stress and bot tools; [Tools](../tools/README.md)
lists them. The stress and bot tools exercise the real processes and never gain
authority of their own: `StackStressGenerator` and `ActiveSimulationBots` reach
GameService only through Development-only, loopback-only, secret-protected
routes, as [Development Surfaces](#development-surfaces) describes. Their own
documents are [Stack Stress Testing](STACK_STRESS_TESTING.md) and
[Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md).

### Durable Item Boundary

GameService owns the durable item schema, mirrored definitions, character item
states, top-level container identities, corpse custody, and every durable item transaction. PostgreSQL is the custody
authority. All durable commands enter one item transaction kernel,
`ItemTransactionService`, whose internals, operation kinds, and lock order are
in [GameService Modules](GAME_SERVICE_MODULES.md#items-transaction-kernel). The
item rules it enforces are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), and the
implemented routes, codes, and limits in
[Service Features](SERVICE_FEATURES.md#items-and-corpses).

The responsibilities split as follows:

- GameService validates and commits every custody, quantity, policy, delivery,
  and currency change. A currency change runs through the Characters wallet in
  the same transaction as the items it pays for.
- SimulationWorker owns the live authority for its Shard: proximity from the
  authoritative player position, service access derived from open
  interactions, authoritative encumbrance from committed carry tuples, the
  equipment GameService last committed for each session, live corpse discovery
  and viewer state, and worker-owned Mob corpses. It never writes inventory
  tables and holds no item collection.
- Unity sends intents and may predict an operation for display, but it never
  owns custody; a rejection rolls its prediction back.
- Redis never takes part in item custody.

Player-driven mutations arrive through two paths, which coordinate through the
same character row lock, while expiry, maintenance, and the Development
commands run as a system actor through the same kernel:

- Offline account routes submit an account actor derived from authentication
  and require the character to be offline. The transaction locks the character
  and its item state before checking active simulation-session ownership.
  Simulation admission uses the same character row lock, so a mutation and a
  session acquisition cannot both pass. Bank and Recovery Storage are never
  reachable offline.
- The live path starts with a reliable intent on the assigned worker. The
  worker serializes it in the character's authority lane and requests the
  durable mutation over the service-authenticated GameService boundary with an
  idempotent operation id. GameService revalidates the exact account,
  character, simulation session and token, active account session, worker,
  runtime, Shard, and assignment inside the same transaction as the mutation.
  The worker applies carry state and equipment only from a committed result.

The worker asserts only service access that an open interaction already
validated, and evaluates it when the operation starts. Bank and Recovery
Storage need an open interaction with a world actor whose `bank` or
`recovery_storage` capability is available. Insurance, quest item-lifecycle, and vendor actions need their own
capability, and a vendor trade carries the vendor profile of the validated
capability, never a client value. A gathering yield needs an open interaction
with a resource node. GameService uses server-owned insurance prices, quest
grant configuration, catalog item values, and vendor prices.

For local testing, SimulationWorker reads a Development-only option that makes
Bank and Recovery access globally true; startup rejects it in every other
environment. It never includes insurance and moves no authority: the worker
still supplies its evaluated access, and GameService still validates the exact
live identity and commits the durable transaction. Production therefore keeps
the interaction requirement without a separate code path or protocol contract.

Corpses follow the same boundary:

- Player corpses and content-selected boss corpses use durable PostgreSQL
  custody with a database-timed absolute expiry. GameService partitions one
  death event transactionally and expires remaining loot through one durable
  cleanup operation.
- A worker restores only open, unexpired corpses for its exact runtime and Shard
  assignment, and keeps reconciling that assignment at a bounded interval, so a
  new durable corpse becomes discoverable without a restart. Reconciliation is
  idempotent and never imports another runtime's corpse. The runtime store
  holds only GameService-provided identity, transform, presentation, section
  summaries, revision, and absolute expiry.
- Normal Mob corpses are worker-owned and disappear on restart. A live loot
  claim enters the exact-session item boundary with a deterministic grant id,
  while durable Mob corpses share the player corpse record, restoration,
  interaction, and expiry code.
- A worker-local per-corpse mutation coordinator sends at most one mutation for
  the same corpse to GameService at a time, while different corpses stay
  parallel. One hotspot therefore never occupies the database pool with clients
  waiting on the same row lock, and GameService stays the idempotent final
  transaction authority. Targeted item and container revisions let unrelated
  corpse operations commit even when the corpse revision advanced.
- Open and refresh send a complete snapshot only to the requesting viewer. A
  committed revision broadcasts one targeted delta or replacement to every
  viewer, encoded once and reused across viewers.

No database transaction stays open across a client network round trip, and no
HTTP item grant route exists: Development item packages and grants run as
process-local GameService commands through the same kernel. GameService also
bounds HTTP bodies, canonical commands, statements, and lock waits, and runs
system-authority maintenance for expiry and retention, as
[Persistence And Maintenance](SERVICE_FEATURES.md#persistence-and-maintenance)
describes.

### World Actor And Interaction Boundary

The world actor boundary adds no new service or topology layer:

- WorldData owns deterministic actor definitions, capability descriptors with
  their profile ids, factions, presentation references, spawn definitions, spawn
  groups, spawn areas, respawn profiles, resource profiles, patrol paths, and
  the Mob combat, brain, locomotion, and loot profiles.
- SimulationWorker owns live actor identity, spawn lifecycle, active state,
  authoritative interaction, range and line-of-sight validation, capability
  dispatch, Mob brains and movement, combat and death events, loot rolls, live
  resource node charges, gathering timing, and the central actor timer queue
  for its assigned Shard.
- GameProtocol carries actor presence, interaction messages, capability
  actions, gathering state, and combat, compiled from the same source for .NET
  and Unity.
- Unity owns advisory pointer targeting, immutable replicated actor and
  interaction state, prefab presentation, temporary uGUI, and Editor authoring
  tools. Authoritative vendor, quest, crafting, gathering, insurance, or combat
  behavior never lives in a MonoBehaviour.
- GameService remains the durable authority when a capability mutates items,
  currency, quests, policies, or progression.

Normal actors do not receive individual PostgreSQL rows. SimulationWorker
reconstructs their baseline from compiled WorldData when an assignment starts.
Stable actor-definition and spawn-definition ids remain distinct from fresh
worker-runtime actor and network entity ids. Selected unique actors may later
opt into durable state without changing the normal population model.

NPCs and resource nodes are event-driven. Mobs use bounded central dormant and
active scheduling buckets keyed by activity profile, tier, and tick interval. No
actor owns a task, timer, thread, database session, or HTTP poller: delayed
actor work, such as a resource node or Mob respawn, is a due-tick entry in the
central `WorldActorTimerQueue`, and resource node charges are live Shard state
that a restart restores to full. Actor visibility reuses the shared
network-entity id allocator and the existing spatial interest management, with
bounded assignment-local despawn tombstones for actor-specific interest exits.

The typed capability registry maps every supported capability kind to one
authoritative handler, and a deferred handler is the safe default for a kind
without business authority. Capability operations reuse the established
interaction session instead of adding target authority of their own. The
generic interaction flow is:

```text
Unity pointer click, approach into range, and open intent
  -> assigned SimulationWorker
  -> exact session, target, Shard, revision, range, line-of-sight, state,
     capability, and rate validation
  -> one authoritative player-to-target interaction session
  -> focused capability summary and correlated results
  -> revalidation on every later capability operation
```

The interaction distances, the validation and closure rules, and the
one-active-interaction rule are in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md#authoritative-interaction-validation).
The server measures from the authoritative player root to the closest point on
server-owned target bounds, and client hit data never proves authority. A corpse
view uses the same shared interaction lease while the corpse view and
transaction protocol stay authoritative for corpse contents.

## Durable Data Model

| Table | Responsibility |
| --- | --- |
| `schema_migrations` | Applied migration history |
| `accounts` | Global account identity and password hash |
| `account_sessions` | Account login sessions with their token hash, expiry, and revocation reason |
| `characters` | Global characters owned by accounts, with their name, level, and currency balance |
| `character_currency_ledger` | Append-only record of every currency change with source kind, source id, item operation, signed delta, and balance after |
| `character_appearances` | One row per character with its body model, its look choices, and whether its headwear is lowered |
| `world_definitions` | Shared World content identities, mirrored from the World manifests |
| `fleets` | Regional or operational compute groups |
| `simulation_nodes` | Machines or hosts assigned to fleets |
| `shards` | Player-selectable simulations with their Fleet, rule set, and current World binding |
| `simulation_workers` | Logical workers and current runtime metadata, including the protocol, simulation, collision, and item catalog revisions |
| `simulation_assignments` | Historical and active worker-to-shard mappings |
| `simulation_join_tickets` | Short-lived exact-runtime admission credentials, stored as hashes |
| `character_simulation_sessions` | Global active simulation leases |
| `item_catalog_revisions` | Applied deterministic catalog revisions and the current one |
| `item_system_settings` | Base carry capacity and the inventory and bank slot counts from the catalog's character settings, and the currency display name and starting balance from its economy settings |
| `item_categories`, `item_tags`, `item_tool_kinds`, `equipment_slots` | Catalog vocabulary, kept separate by domain meaning, with display order, slot maximum quantity, and active state |
| `item_definitions` | Stable id, display metadata, category, unit weight, stack maximum, destruction capability, required character level, value in whole currency units, structural fingerprint, and active state |
| `item_definition_tags`, `item_definition_default_policies`, `item_definition_tool_kinds` | A definition's tags, default policies, and tool kinds |
| `item_definition_equipment_slots`, `item_definition_blocked_equipment_slots` | Compatible equipment slots and the slots an equipped definition blocks |
| `bag_definitions`, `bag_definition_slots`, `bag_definition_slot_tags` | Bag carry bonus and its stable general and specialized slots |
| `vendor_profiles`, `vendor_offers`, `vendor_sale_categories` | Vendor profiles with purchase and sale price basis points and active state, their ordered offers, and the categories each vendor buys |
| `character_item_states` | Character aggregate revision, carried weight, carry capacity, and the owned top-level container ids |
| `item_containers`, `item_container_slots`, `item_container_slot_tags` | Typed containers with owner binding, revision, and lifecycle, and their stable general or specialized slots |
| `item_instances` | Durable identity, definition, quantity, revision, and exactly one location: a container slot or an equipment slot |
| `item_instance_policies` | Protected-on-death and one-death insurance lifecycle records |
| `recovery_deliveries`, `recovery_delivery_items` | Per-character system deliveries with source, availability, expiry, and claim state, and their items |
| `item_operations` | Global idempotency id, canonical request hash, status, and replayable result |
| `item_operation_changes`, `item_destructions` | The append-only change audit and the destruction records |
| `corpses` | Durable player and persistent Mob corpses: source snapshot, Shard transform, presentation key, revision, absolute expiry, and close lifecycle |
| `corpse_sections` | Bindings of the general inventory, equipment, and Bag sections to real item containers |
| `corpse_snapshots` | Non-interactive insured equipment and protected or insured Bag presentation metadata |
| `death_events` | One row per authoritative death event with its original operation, canonical request hash, corpse, result correlation, and the optional killer kind and id |
| `live_mob_loot_grants` | Every committed live Mob loot grant by its grant id, with the operation, character, and source corpse, so a grant commits once |

Every table belongs to exactly one GameService module, as
[Table Ownership](GAME_SERVICE_MODULES.md#table-ownership) lists. Only the owner
writes or locks its rows; [Access Rules](GAME_SERVICE_MODULES.md#access-rules)
describes how other modules reach them.

Active account login sessions are unique by account. Active simulation sessions
are unique by both character and account. This prevents one account token from
running multiple characters simultaneously, even if the token is copied to a
second client.

Every service-created character has exactly one active permanent inventory,
bank, and Recovery Storage container, and the character
item-state row references those exact owned container types. CHECK constraints
make an item instance point to one existing container slot or one character
equipment slot, never both, and deferrable partial unique indexes allow only
one item in either assignment while still permitting an atomic swap. A retired
catalog row stays with `is_active = false`, so audit records and old corpses
keep their references.

No delete path exists for accounts or characters today, but the foreign keys
define what a future delete flow removes. Deleting a character cascades its item
state, owned containers, contained and equipped items, policies, and recovery
deliveries. Item operations and change audit remain, while deleted actor
and item references become null. Such a flow must go through the owning
modules' contracts.

Corpse snapshots do not contain an item-instance foreign key. Real loot exists
only in section-container custody, while protected and insured real instances
use Recovery Storage. Deleting the source character may clear its corpse
reference without deleting the corpse name, transform, lifetime, sections, or
audit.

## Runtime Identity And Assignment Safety

`SimulationWorkerId` is a stable logical process slot. Every process start also
creates a random `RuntimeId`. Tickets and simulation sessions bind to both.

Heartbeat processing is transactional:

1. GameService locks the target shard row.
2. It validates World, Fleet, Node, and Shard topology.
3. A healthy existing assignment fences a different worker.
4. A timed-out or explicitly offline owner is released atomically.
5. Pending tickets and active sessions for every superseded runtime are
   invalidated, including when the logical worker moves to another shard.
6. A newer runtime may replace an older live runtime for the same worker id.
   Any runtime may replace one that is offline or past the heartbeat timeout,
   because start times reported by different hosts are not comparable. A start
   time more than ten minutes in the future is rejected. A replacement runtime
   that has never owned the worker id treats `worker_runtime_changed` as "not
   yet", keeps retrying while its transport rejects every connection, and takes
   over once the previous lease expires. It stops only if another runtime stays
   live beyond `SimulationWorker:RegistryAcquireTimeoutSeconds`. A runtime that
   has owned the worker id and then receives `worker_runtime_changed` has lost
   authority and stops at once.
7. The exact active SimulationAssignment is created or retained.

SimulationWorker renews a local registration lease from successful heartbeat
responses. The local lease runs from the moment the heartbeat request was sent,
minus a safety margin of at most one second, so it always ends before the
database lease. Simulation session leases work the same way: GameService
returns the lease duration, and the worker sets each session deadline on its own
clock from the request send time. Clock differences between hosts therefore
never extend or cut short a lease. UDP joins are rejected without a valid lease.
If the lease expires, or GameService reports that the runtime lost authority,
the process stops. This limits split-brain behavior during network partitions
and allows another worker to take over after the authoritative heartbeat
timeout.

## Trust Boundaries

### Player Authentication

Registration and login issue opaque account tokens. PostgreSQL stores only a
SHA-256 hash. Protected routes use the `AccountSession` ASP.NET authentication
scheme and policy.

A later login replaces the prior account session in one transaction, consumes
its tickets, and releases its active simulation sessions. Logout and manual
revocation perform the same dependent cleanup with distinct reason codes.

### Service Authentication

SimulationWorker sends `X-Simulation-Worker-ID` and
`X-Simulation-Worker-Secret`. GameService validates the configured secret with a
fixed-time comparison and adds the worker identity claim. Endpoint policies and
request payload checks prevent a worker from acting for another worker id or
runtime.

### Realtime Admission

The LiteNetLib connection key filters unrelated traffic but is not player
authentication. The join ticket is the player credential. It is bound to an
account session, character, shard, worker, and runtime, then consumed exactly
once through the authenticated service channel.

The client validates placement protocol and simulation revisions before opening
UDP. Join acceptance must match the placed character, shard, World, simulation
revision, and collision revision.

### Development Surfaces

Development tools reach authority only through guarded paths, and none of them
adds a production code path:

- The `--dev-items-*` commands are process-local GameService commands, not HTTP
  routes. They run only in the Development environment against a loopback
  PostgreSQL host with a non-production database name, and they apply every
  change through the item transaction kernel.
- The development bot ticket route and the stack stress fixture routes exist
  only when explicitly enabled in the Development environment, accept only
  loopback callers, and require their own secret. Bot identities, tickets, and
  sessions stay in memory.
- The SimulationWorker admin tools and the global Bank and Recovery access are
  options that fail startup outside the Development environment.

## Core Runtime Flows

### Topology Bootstrap

1. GameService validates `Simulation:Topology` and loads every canonical World
   manifest at startup.
2. Database migrations create or upgrade the durable topology schema.
3. The seeder idempotently mirrors World definitions from manifests and ensures
   configured Fleets, Nodes, and Shards without selecting a World for them.
4. A newly created shard may remain unbound and is excluded from discovery and
   join admission until a worker completes its first accepted heartbeat.
5. Seeded shards remain offline because topology alone does not create a healthy
   worker assignment.

### Worker Registration

1. SimulationWorker validates its configuration and loads the output
   `WorldData/Worlds/<WorldId>/world.json`, the actor runtime, the item catalog,
   the combat rules, the character appearance content, and the collision
   runtime.
2. It binds UDP and reports transport readiness.
3. It sends worker, runtime, topology, desired World, endpoint, capacity,
   protocol, simulation, collision, and item catalog metadata to GameService.
4. GameService validates the World database entry and fleet boundaries, safely
   binds or rebinds the shard, creates the assignment, and returns a
   database-timed online lease with the Shard's rule set.
5. The worker renews its local registration lease and continues heartbeats.

### Shard Discovery And Placement

1. Unity calls `GET /api/shards`.
2. GameService lists logical shards and reconciles worker-reported connections
   with authoritative unexpired simulation sessions while aggregating fresh
   assigned-worker capacity.
3. Worker host and UDP port are not exposed in this public list.
4. Unity requests `POST /api/shards/{shardId}/join` for an owned character.
5. GameService locks the account and character, applies global active-session
   rules, reserves authoritative session and pending-ticket capacity under a
   worker row lock, refuses a worker whose item catalog revision differs from
   the mirrored catalog, and creates a ticket.
6. The response contains the selected shard plus an exact worker runtime
   endpoint and compatibility metadata.

The rules of each step are in
[Shard Discovery And Placement](SERVICE_FEATURES.md#shard-discovery-and-placement).

### Join And Reconnect

1. Unity connects to the placement endpoint and sends the ticket reliably.
2. SimulationWorker consumes it through GameService with its worker and runtime
   identity.
3. Ticket consumption and session claim occur in one PostgreSQL transaction,
   which also returns the fenced carry tuple, the equipment, and the character's
   look.
4. SimulationWorker creates or reconnects the entity and binds it to the peer.
5. A new entity is added incrementally to the spatial interest index. Existing
   visibility sets are updated only for nearby observers instead of globally
   refreshing every peer.
6. Join acceptance returns topology, compatibility, movement, entity, Shard
   rule, and non-secret session metadata.
7. A reliable spawn baseline establishes the joining peer's presentation state,
   while only affected existing peers receive the new entity spawn.

A reconnect is valid only for the exact active shard, worker, and runtime. It
preserves session and entity state, rotates the secret simulation-session token,
and disconnects the older peer generation. While the reconnect is still being
finalized, the older peer only detaches its connection. It never removes the
shared entity or carry state that the reconnecting client takes over. The reused
entity resets its movement input sequence, because the new client generation
starts its input sequence from the beginning.

### Leave, Disconnect, Expiry, And Failover

1. Unity requests leave with the exact simulation-session id.
2. SimulationWorker releases the exact session token generation through
   GameService.
3. The worker removes the peer binding and entity and sends reliable despawn.
4. Unexpected disconnect uses the same cleanup path. The worker first drops the
   local session, so its heartbeat stops renewing the lease, and then calls
   release with two bounded retries.
5. If release is unavailable, the database lease expires on its own because
   nothing renews it. A join that is rejected during finalization releases its
   session the same way. As a safety net, the worker releases any local session
   that no peer has owned for twice the join handshake timeout.
6. A leave or disconnect never cancels durable combat work: the session is
   released only after the character's death and last arrow batch committed.
7. An unexpected failure while processing one packet or one completed
   operation disconnects only that peer. The simulation loop and every other
   player keep running. GameService error codes and messages are normalized to
   short printable text before they enter realtime protocol fields.
8. Worker shutdown or authoritative failover releases its assignment, pending
   tickets, and bound sessions.

### Authoritative Movement

1. SimulationWorker sends validated movement settings, shared-data revisions,
   and the admission-fenced carry tuple on join.
2. A click plans a route around obstacles with the shared planner in Unity. Each
   input names the route by path id, and the batch carries the route's
   waypoints until the worker confirms it. Unity predicts locally and sends
   redundant batches of recent unacknowledged inputs.
3. SimulationWorker processes only newer sequences at its fixed tick rate. It
   validates a new route against the World bounds and the path length limit and
   follows the same waypoints. A route that breaks the limits is adopted as
   already ended. Clients send input and routes, never accepted positions.
4. Static and dynamic collision plus carry-state speed rules run through the
   shared simulation. Both server and Unity use the exact committed item-state
   revision, weight, capacity, and fixed-point multiplier.
5. Session heartbeat advances carry state monotonically. SimulationWorker sends
   a reliable carry-state update before the newer state affects local movement.
6. Interest management determines which entities each connection can observe.
7. SimulationWorker sends visible authoritative states at its snapshot rate.
8. The local client acknowledges, rewinds, and replays prediction. Remote clients
   interpolate behind the latest server tick.

The rates, limits, and input rules are in
[Server-Authoritative Movement](SERVICE_FEATURES.md#server-authoritative-movement).

### Authoritative In-World Item Mutation

1. Unity sends an item interaction intent to its assigned SimulationWorker.
2. SimulationWorker validates the exact live session, proximity, interaction,
   and service-access rules and queues the intent in the character's authority
   lane.
3. The worker calls GameService over its service-authenticated channel with an
   idempotent operation id and exact worker-runtime fencing.
4. GameService locks and validates durable PostgreSQL item state, commits the
   complete transaction, and returns the new revisions together with the
   character's current carry state and equipment.
5. SimulationWorker updates authoritative encumbrance and equipment only from
   the committed result, sends a reliable carry update before it affects
   movement, and forwards the result and the committed item changes to Unity.
6. Unity confirms or rolls back its prediction and patches its inventory from
   the committed change, or refreshes the authoritative snapshot when it cannot
   patch.

### Server-Authoritative Combat

Combat runs only in SimulationWorker and is never predicted, so it stays out of
GameSimulation and needs no cross-runtime float rules. The worker reuses the
shared motor and route planner for Mob movement without changing them.

1. A loop-confined combatant registry holds one combat state per player entity
   and per Mob, keyed by network entity id: health, mana, life state, target,
   attack timing, the equipment lockout, and the in-combat time that
   regeneration reads.
2. The combat phase of every tick first advances the durable combat work, then
   resolves due projectile impacts, then starts, releases, or cancels attacks
   for every combatant in entity id order, then resolves deaths, and then
   updates the combat flags, regeneration, and respawns. One injectable random
   source per worker runtime rolls damage and loot, so every outcome is
   reproducible in tests.
3. Every changed combatant and every combat event reaches its viewers once at
   the end of the tick.
4. Durable effects leave the loop as tracked operations that retry with the
   same identity, so a retry never duplicates an effect. Spent arrows commit in
   batches, and a player death commits through the death partition with its
   killer and the shots that were not committed yet, both in the character's
   authority lane; a durable Mob corpse commits through the durable Mob corpse
   route. An arrow batch gives up after a bounded number of attempts and leaves
   its arrows with the player, while a death and a durable Mob corpse retry
   until they commit or GameService refuses them for good.
5. A dead player respawns only after its death committed and the committed
   carry state was applied.

The implemented combat rules, timings, and failure handling are in
[Target-Based Combat](SERVICE_FEATURES.md#target-based-combat) and
[Mob Brains](SERVICE_FEATURES.md#mob-brains), and the game rules in
[Game Design](GAME_DESIGN.md#combat-model).

### Authoritative Player Death, Restoration, And Loot

1. Authoritative combat produces one unique death event, with its Mob or
   character killer, only after the owning SimulationWorker has resolved the
   death.
2. SimulationWorker submits the exact account, character, simulation session,
   token, worker id, runtime id, Shard, expected item-state revision, transform,
   and corpse presentation key over its authenticated GameService client.
3. GameService revalidates the complete live authority, claims both the item
   operation and death-event identities, then locks the character, Bag
   aggregates, containers, items, and policies in the durable transaction.
4. The transaction partitions the items as
   [Player Death Partition](INVENTORY_AND_DEATH_LOOT_DESIGN.md#player-death-partition)
   defines and commits every affected revision, carry state, policy
   consumption, snapshot, and audit together. Death never fails on carry
   weight.
5. Each corpse stores database creation time plus its absolute deadline.
   GameService cleanup locks the corpse first, destroys each remaining item once
   with audit, and closes even an already-empty corpse only at that deadline.
6. After worker registration, SimulationWorker requests one read-only snapshot
   for its exact fresh runtime and active Shard assignment. It keeps the
   presentation state against database time advanced by a monotonic local
   clock.
7. Corpse presence snapshots expose nearby runtime identity and presentation
   only. A player may open one corpse within interaction range. Multiple players
   may inspect the same corpse, and the worker validates proximity and cached
   lifetime before every refresh or mutation.
8. GameService opens a short read-only transaction for a complete three-section
   snapshot. Loot, deposit, corpse-internal movement, and Bag swaps enter the
   normal item transaction kernel with targeted item, destination, and Bag
   aggregate revisions.
9. The per-corpse worker coordinator serializes calls into the same durable
   aggregate without serializing different corpses. The mutation transaction
   closes before GameService loads the committed view. SimulationWorker applies
   returned carry only for custody-changing transfers, keeps the newest corpse
   revision when HTTP completions arrive out of order, encodes one targeted
   delta, and reuses it for every other viewer. Pure corpse rearrangement
   advances corpse, container, and item revisions without changing character
   carry or item-state revision.
10. Expiry, invalidation, and not-found results close every affected view with a
    stable code. Runtime or session fencing failures disconnect the affected
    peer. A worker restart restores the corpse at its persisted transform with
    the unchanged deadline.

## Configuration Ownership

- `src/Server/GameService/Config` owns GameService defaults and topology
  bootstrap. Each GameService module validates the keys it owns; the host loads
  them all before any service starts, as described in
  [GameService Modules](GAME_SERVICE_MODULES.md#configuration).
- `src/Server/SimulationWorker/Config` owns worker identity, endpoint,
  networking, simulation, collision streaming, quotas, interest, and corpse
  settings.
- `content/` owns the World manifests, the actor and item catalogs, the combat
  rules, the character appearance content, and the Development item packages.
  The builds copy their compiled or checked forms into each service's output,
  and each service validates them strictly at startup.
- `src/Client/Assets/Content/Config` owns runtime Unity endpoint and timeout
  configuration.
- The ignored root `.env` owns local secrets and machine overrides, and
  `.env.example` is the committed environment contract, as
  [Local Development](LOCAL_DEVELOPMENT.md#local-environment-file) describes.
- Root Compose, solution, SDK, and build files are repository-wide concerns.

Both backend processes load owned JSON, the environment-specific JSON, the
optional root `.env`, real environment variables, and command-line values in
increasing precedence. Invalid application configuration fails before work is
accepted. Configuration files are not reloaded while a process runs, so every
value in use has passed startup validation; change a value by restarting the
process. The options and their defaults are in
[Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults).

## Health And Operations

GameService exposes `/health/live` and `/health/ready`, and `/` redirects to
readiness. Liveness confirms the process. Readiness runs a real PostgreSQL
`select 1` query and Redis `PING` within `HealthChecks:TimeoutMilliseconds` and
returns HTTP 503 when an obligatory dependency fails. Redis accelerates
account-session validation and authentication rate limits, as
[Redis Account-Session Acceleration](SERVICE_FEATURES.md#redis-account-session-acceleration)
describes, but it never decides item custody, simulation authority, or durable
session truth.

SimulationWorker has no HTTP server. `--health-check-only` loads and validates
its configuration and content like a normal start, then checks GameService
readiness, Redis, and UDP port availability, prints each dependency, and exits
with code 0 when all pass and 1 otherwise.

Local Compose ports bind to `127.0.0.1`.

Both processes build their OpenTelemetry pipeline through
`src/Server/Telemetry` and push metrics, traces, and logs over OTLP to one
OpenTelemetry Collector, which routes them to the storage backends, so the
services never depend on a storage backend. Export is off unless
`Telemetry:OtlpEndpoint` is set. The fixed tick never starts a span, and
hot-path metric recording does not allocate. The catalog, the stack,
dashboards, alerts, and operating guidance are in [Monitoring](MONITORING.md).

## Future Scaling Contract

The next topology extension is not another naming rewrite. It will add:

- Zone definitions and authoritative zone ownership inside a shard.
- Cross-zone handoff with one owner at every simulation tick.
- Layer definitions for selected high-population areas inside a shard.
- Placement policies that keep groups together and prevent content exploits.
- Durable shared event policies for bosses, resources, and economy where layers
  must not create duplicate rewards.

Until that work exists, a shard is never presented as partially distributed.

The service split and deployment direction is planned as follows:

- (planned) A separate SocialService process owns chat, friends, guild and
  party chat, private messages, presence, cross-Shard communication, and basic
  moderation. It stays apart from SimulationWorker so chat and friends remain
  online when a simulation process restarts.
- (planned) Clients use HTTPS for the GameService API, WebSocket over TLS (WSS)
  for SocialService, and UDP for SimulationWorker gameplay.
- SimulationWorker never permanently owns player inventory, the future auction
  house, or social systems.
- (planned) The item and economy boundary, with inventory, bank, item instances,
  corpse custody, loot transfers, and later crafting, the auction house, and
  trades, stays a set of GameService modules behind the durable transaction
  boundary. A later extraction into a service of its own must preserve the same
  authority and idempotency contracts.
- (planned) Early online tests run on a single Linux VPS with Docker Compose,
  Nginx as the HTTPS and WSS reverse proxy, GameService, SocialService, one or
  more SimulationWorker containers, PostgreSQL, and Redis. Only SSH on TCP 22,
  HTTP, HTTPS, and WSS on TCP 80 and 443, and the world UDP ports, such as 27015
  and 27016, are exposed; PostgreSQL and Redis are never public.

Only UI may remain explicitly temporary. All other boundaries in this document
are production-oriented foundations.

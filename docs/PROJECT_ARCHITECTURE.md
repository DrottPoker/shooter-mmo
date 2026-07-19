# Project Architecture

Last updated: 2026-07-19

## Purpose

This document is the source of truth for project-wide architecture, naming,
ownership, trust boundaries, and runtime flows. Detailed Unity internals belong
in [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md). Implemented
behavior belongs in the two feature documents. The approved planned world-actor
contract belongs in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).
Development map identities and scene-authoring coordinates belong in
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
| Mob | A combat-oriented world actor with future AI, aggro, loot, corpse, and respawn behavior |
| Zone | A future authoritative spatial partition inside a shard |
| Layer | A future population copy of a zone or area inside one shard |

There is no Realm layer. Accounts, characters, progression, and the future
economy are global. Fleets are compute placement boundaries, not data silos.
Players will be able to use the same character in different regions and shards,
subject to the one-active-session rules.

## Topology Model

```text
Global Services
  AuthService
  PostgreSQL
  Redis
  future economy, social, inventory, and persistence services

World: development-world-1
  complete small-map development content

World: development-world-2
  registered large-map development content, Unity scene and collision pending

Fleet: local-fleet
  Node: local-node-1
    SimulationWorker: local-simulation-worker-1
      Worker runtime: generated opaque id per process start
      SimulationAssignment: worker -> local-shard-1

Shard: local-shard-1
  World: development-world-1
  Fleet: local-fleet
```

Both development Worlds are registered independently of shard placement from
their canonical manifests. The local worker currently requests
`development-world-2`; adding another World does not start it or hot-swap a
running shard.

A shard belongs to one fleet and references exactly one World definition at a
time. The binding is accepted runtime state, not permanent character or shard
ownership. A replacement worker proposes its configured `WorldId` on its first
heartbeat. AuthService locks the shard row and may persist the new binding only
when there is no healthy assignment, pending unexpired join ticket, active
unexpired simulation session, or open durable corpse. The same transaction
creates the new assignment, so concurrent registration cannot bypass the
check. A running shard cannot hot-swap Worlds.

A node also belongs to one fleet. AuthService permits an assignment only when
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
Unity Client ------------------------------------> AuthService --------> PostgreSQL
     |                                                  ^
     | LiteNetLib UDP                                   | service-authenticated HTTP
     v                                                  |
SimulationWorker --------------------------------------+
     |
     +--------------------------------------------> Redis readiness

AuthService --------------------------------------> Redis readiness
```

Unity uses AuthService for identity, characters, shard discovery, and placement.
AuthService returns a SimulationWorker endpoint only with a short-lived join
ticket. Realtime gameplay packets travel directly between Unity and the assigned
worker. AuthService never proxies realtime traffic.
AuthService also consumes the deterministic WorldData runtime item catalog as a
bundled build input. That is a content dependency, not a network authority
relationship.

## Repository Boundaries

### AuthService

`AuthService` is the only ASP.NET Core application. It owns:

- Accounts, password hashes, and revocable account sessions.
- Characters and ownership.
- World, Fleet, Node, Shard, SimulationWorker, and SimulationAssignment records.
- Worker heartbeats, online status, capacity, runtime fencing, and failover.
- Capacity-aware shard placement and short-lived join tickets.
- Global simulation-session leases.
- Transactional item catalog mirroring, constrained durable item schema, and
  complete empty character item-state bootstrap.
- Conditionally cached current-catalog reads, no-store owned item-state, bank,
  and Recovery Storage reads, and offline-safe account item mutations.
- HTTP authentication, policies, rate limiting, Problem Details, correlation
  ids, sensitive response caching rules, and health routes.
- PostgreSQL schema migrations plus idempotent topology and item bootstrap.

Feature folders remain inside the service that owns them. Configuration lives
under `AuthService/Config`.

AuthService owns one bounded Npgsql pool per process. The checked-in
`Database:MaximumPoolSize` is `64`, leaving connection headroom on a default
100-connection PostgreSQL server for worker traffic, health probes, stress
sampling, administration, and shutdown. Deployments with multiple AuthService
replicas must budget the combined per-process pool limits below PostgreSQL's
non-reserved connection capacity. Transient Npgsql connection failures return
`503 database_unavailable` instead of an unclassified internal server error.

### SimulationWorker

`SimulationWorker` is a .NET Generic Host console application. It does not host
ASP.NET. LiteNetLib owns its UDP endpoint. It owns:

- UDP admission, protocol validation, and join and leave handshakes.
- Exact worker runtime identity and its AuthService registration lease.
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
- Position-driven world collision chunk streaming.
- Fail-fast configuration and collision validation.

Configuration lives under `SimulationWorker/Config`. A process receives its
worker, Fleet, Node, Shard, and desired World identity through validated
configuration. `WorldData/Worlds/<WorldId>/world.json` owns map-local identity,
client scene, movement bounds, authoritative spawn, and item service
coordinates. Selecting `SimulationWorker:WorldId` loads that self-contained
World directory, including collision and actor runtime content. Shared movement
physics, networking, quotas, interest, collision streaming, and corpse radii
remain process policy rather than map content.

### Shared

`Shared` contains backend helpers that have more than one backend consumer:

- Optional root `.env` loading.
- Connection-string parsing.
- Redis protocol health checks.
- Common health response models.

It must not become a dumping ground for feature logic.

### GameProtocol

`GameProtocol/Runtime` is a local Unity package and the single source for the
binary realtime contract. `Shared/DotNet/GameProtocol` compiles the same files
for backend processes and tests.

Protocol version 14 includes:

- Join, leave, rejection, and structured disconnect messages.
- Shard and World identity in join acceptance.
- Server-assigned network entity ids.
- Reliable ordered entity spawn and despawn.
- Bounded, sequenced movement input batches.
- A dedicated fixed-rate owner snapshot with server tick, snapshot sequence,
  input acknowledgement, and authoritative reconciliation state.
- Chunked remote simulation snapshots whose visible entity updates rotate
  fairly when the worker-wide byte budget cannot carry the full dense set.
- Movement, simulation revision, and collision revision metadata.
- Admission-fenced carry state in join acceptance and reliable ordered updates
  for newer committed item-state revisions.
- Bounded reliable ordered item-operation intents and committed or rejected
  results with authoritative carry, item, container, and delivery revisions.
- Chunked corpse presence and view state with destination slot tags and
  canonical equipment-slot ids, plus reliable open, close, refresh, full or
  partial bidirectional transfer, corpse-internal move, atomic ordinary slot
  and Bag-swap, result, and view-closure messages.
- Packet magic, version, type, size, and bounded-field validation.

Channel 0 is reliable ordered control. Channel 1 is sequenced movement input.
Owner and remote snapshots use unchanneled unreliable delivery. Remote packets
retain application-level tick and chunk metadata, while the owner packet is
small enough to remain prioritized at the configured snapshot rate.

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
  work telemetry for any new high-frequency path.
- Extend `StackStressGenerator` with a stable workload before raising a
  supported player tier. Compare the same mode, seed, bot count, ramp, and
  duration before and after an optimization.

Per-entity tasks, timers, database sessions, and HTTP polling remain prohibited
for moving populations. A feature that introduces all-to-all work must either
prove the bounded tier or add spatial, frequency, or byte-budget degradation.

### GameSimulation

`GameSimulation/Runtime` is the shared fixed-step simulation source. Unity uses
it directly as a local package and `Shared/DotNet/GameSimulation` compiles it
for SimulationWorker.

It owns movement integration, carry-state encumbrance, sprint restrictions,
gravity, facing, capsule collision, steps, slopes, bounds, and collision-data
codecs. Its carry wrapper delegates capacity, hard-cap, and multiplier arithmetic
to the pure WorldData item rules. It has no Unity, transport, database, or
presentation dependency.

### WorldData

`WorldData` is shared content, so it correctly remains at repository root.
`WorldData/Shared` contains global catalogs and domain rules.
`WorldData/Worlds/<WorldId>` is a self-contained map package with a canonical
`world.json`, collision and actor authoring, deterministic actor runtime, and
compiled checksummed collision chunks. A shard references one World and its
worker loads that directory.

`Tools/WorldCollisionCompiler` builds and verifies the same data consumed by
SimulationWorker and Unity. Static collision is deterministic and shared.
Dynamic collision uses a mutable spatial hash behind the same collision-query
interface, leaving room for doors, lifts, and other server-owned objects.

`WorldData/Shared/Authoring/Items` owns the strict neutral item catalog source,
and `WorldData/Shared/Runtime/Items` contains its deterministic compiled form. The catalog
has stable identities, one complete revision, per-definition and per-tier
structural fingerprints, and no Shard identity. Framework-neutral catalog and
pure rule source is compiled for Unity by the `ShooterMmo.WorldData` assembly and
for .NET tooling and tests through `Shared/DotNet/WorldData`. Neither compilation
target becomes an authority. AuthService validates and mirrors the compiled
catalog at startup. Future mutation paths must still reapply the authoritative
rules before committing durable item state.

Neutral actor definitions live below
`WorldData/Shared/Authoring/Actors`. Each World owns its spawn authoring at
`WorldData/Worlds/<WorldId>/Authoring/actor-spawns.json` and deterministic
runtime content at `WorldData/Worlds/<WorldId>/Runtime/world-actors.json`.
`Tools/WorldActorCompiler`, SimulationWorker startup, Unity Editor authoring,
and tests use the same framework-neutral compiler and runtime validator. Unity
scene authoring imports and exports that content but does not replace it as the
source consumed by SimulationWorker.

The current pure rules cover stack compatibility, Bag slot tag acceptance,
equipment compatibility, Secure Container eligibility, empty and non-empty Bag
locations, Bag containment-cycle rejection, unitless integer weight arithmetic,
base character capacity `200`, the 140 percent hard cap, and the linear
fixed-point encumbrance multiplier. GameSimulation wraps the carry tuple in an
immutable shared movement state and applies the same multiplier and sprint
threshold in SimulationWorker and Unity prediction. These rules have
no HTTP, PostgreSQL, UnityEngine, or SimulationWorker runtime dependency.

`WorldData/Editor/Items` contains the Editor-only `ShooterMmo.WorldData.Editor`
assembly and the canonical `Shooter MMO > Tools > Item Catalog` window. It edits
the neutral authoring JSON and invokes the same strict .NET compiler used by CI.
Runtime WorldData assemblies do not reference `UnityEditor`.

`Assets/Editor` contains project-local orchestration tools below the shared
`Shooter MMO > Tools` menu root. `Inventory Item Grants` invokes guarded,
machine-readable AuthService Development commands and renders their response. It
never references PostgreSQL or item persistence directly. AuthService routes
every requested grant through `ItemTransactionService`, so catalog, slot,
policy, revision, weight, and hard-cap authority remain in the backend.

Unity owns a separate client presentation catalog keyed by stable definition id
under `Assets/Resources/Items/Presentation`. It references icons, localization
keys, fallback text, and optional prefab presentation keys, but cannot duplicate
or override authoritative gameplay fields. Its deterministic presentation
revision records the exact source gameplay revision. The client validates and
caches the bundled catalog once per matching revision, while server item state
uses definition ids instead of transferring presentation assets.

The persistent Unity bootstrap also owns one `InventoryClientController`. The
controller keeps catalog indexes, complete and focused snapshots, observed
revisions, operation ids, structured failures, and refresh orchestration outside
scene panels. The replaceable uGUI panel reads that state and sends intents, but
it never owns item custody or applies an optimistic mutation.

### Unity Client

`shooter-mmorpg-unity-client` owns presentation, input, prediction,
reconciliation, interpolation, scene transitions, HTTP serialization, and the
persistent UDP client. It never owns authoritative gameplay state. See
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md).

The F2 panel is an observation surface for the local client. It derives frame,
prediction, reconciliation, snapshot, and payload metrics from local state.
Worker process and server-wide population data stay in SimulationWorker logs
and metrics.

### Tests And Tools

`Tests/ShooterMmo.Backend.Tests` contains unit, socket-level, and isolated
PostgreSQL integration tests. Unity tests live inside the Unity project. `Tools`
contains repository-wide verification, collision and item content builds, and
external stress tools. `StackStressGenerator` has a worker-only mode with an
in-memory loopback authority and a full-stack mode that uses normal AuthService
account, character, login, placement, and session routes against a guarded
disposable local PostgreSQL database. Explicit full-stack inventory and shared
loot workloads provision minimal starting state through secret-protected,
Development-only, loopback fixture endpoints, then measure the normal UDP worker
authority, AuthService item transaction, and PostgreSQL custody path. Both modes
use manually polled headless UDP clients without per-bot transport threads.
Bounded latency reservoirs and process samplers keep long local soak tests from
accumulating every acknowledgement sample.

`ActiveSimulationBots` reuses the same tool-only headless UDP client core but
runs beside the real AuthService and SimulationWorker. A Development-only,
loopback-only AuthService endpoint issues protected in-memory bot identities and
exact-runtime tickets. Worker ticket consumption, session heartbeat and release
route in-memory bot credentials to that authority and all other credentials to
the unchanged PostgreSQL services. Reserved worker capacity prevents bots from
occupying every connection slot needed by real local players.

### Durable Item Boundary

Status: Phases 1 through 15 content, authoring, schema, catalog mirror, character
bootstrap, authoritative reads, policy lifecycle, internal transaction kernel,
offline account APIs, carry-state delivery, shared encumbrance, and realtime item
mutation plus Unity inventory integration, death partition, durable player
corpse custody, concurrent corpse interaction, NPC insurance and quest item
  lifecycle, live or durable Mob corpse variants, and operational hardening
  implemented

AuthService owns the durable item schema, mirrored definitions, character item
states, top-level container identities, account Secure Container entitlements,
and the implemented internal transaction boundary. PostgreSQL remains the
authority. The schema, read model, and command kernel are present before player
item traffic so later routes build on one constrained custody model instead of
inventing endpoint-local state or SQL.

AuthService account-session routes expose the current catalog, complete
item-state, focused bank state, and focused Recovery Storage state only when the
requested active character belongs to that account. Each query uses one
PostgreSQL `REPEATABLE READ`, read-only transaction. Character responses use
`no-store`. The neutral catalog uses its deterministic revision as an ETag and
returns `304 Not Modified` when the supplied ETag still matches. Inventory
instance rows contain stable definition ids and active policy summaries rather
than repeated definitions, presentation data, policy sources, or operation
metadata.

Account-session mutation routes cover relocation, stack split and merge,
allowed destruction, Recovery claims, and account Secure Container tier changes.
They submit only an account actor derived from authentication and require the
character to be offline. The transaction locks both the character and its item
state before checking active simulation-session ownership. Simulation admission
uses the same character row lock, so a mutation and session acquisition cannot
both pass concurrently. In-world proximity and city-service validation remain
owned by the SimulationWorker live boundary.

All durable commands enter `ItemTransactionService`. One command opens one
connection and one `READ COMMITTED` transaction, claims the operation row, and
then locks character item states, Bag aggregate roots and containers, item rows,
and policy or delivery rows in canonical sorted order. A savepoint lets a stable
domain rejection persist its idempotent result while rolling back the complete
candidate mutation. Successful commands recompute weight and equipped Bag
capacity, advance revisions, append relational audit changes, and persist a
replayable result in the same transaction. Equipment-slot item roots contribute
zero carried weight. The active equipped Bag's contents still count and its
capacity bonus still applies. Startup bootstrap corrects older stored carry
tuples and advances the character item-state revision only when reconciliation
changes weight or capacity.

The transaction context resolves current definitions and slot data from the
mirrored catalog but delegates stack, equipment, Bag, Secure Container, policy
capability, and integer-weight decisions to WorldData rules. The kernel does not
depend on HTTP, Unity, or SimulationWorker runtime state. Account, system, and
simulation-worker authorization contexts are explicit command inputs. Offline
access and exact live-session access are actor requirements enforced while the
durable character lock is held.

`ItemPolicyService` applies protected-on-death and eligible one-death insurance
records and removes active insurance through idempotent transactions.
`QuestItemService` grants protected quest items with exact quest-grant lineage,
suppresses duplicate active grants, and removes only that lineage on abandon.
Phase 13 exposes no account-owned policy or grant route. The live path begins in
the validated NPC interaction and uses the service-authenticated simulation item
boundary.

Bag content containers and Bag item rows form one aggregate. Every child command
locks the Bag item before its child container or item, and aggregate swaps verify
the expected Bag item plus content-container revisions. Secure Container tier
changes lock every affected character state in character-id order and coordinate
with character bootstrap through one account-entitlement advisory key.

SimulationWorker owns authoritative encumbrance for its assigned Shard. Join
admission returns a character-row and item-state-row fenced carry tuple, and
session heartbeat can advance that tuple only by its monotonic item-state
revision. The worker applies it to movement through shared GameSimulation rules
and sends committed revisions to Unity over the reliable control channel.

SimulationWorker owns live item-service proximity from its authoritative player
position. While a character is active, a bounded per-peer queue serializes
reliable item intents before the worker requests durable mutation through the
service-authenticated AuthService boundary. AuthService revalidates the exact
account, character, simulation session and token, active account session,
worker, runtime, Shard, and assignment inside the same transaction as the
mutation. The worker applies carry state only from a committed result and never
writes inventory tables or holds an item collection.

Bank and Recovery Storage operations require a matching worker-validated service
point. Secure Container operations require no city service. Insurance and quest
item-lifecycle operations require a matching Phase 12 interaction session and
capability handler. The worker asserts only the access already validated by that
session. AuthService revalidates the live authority tuple, uses server-owned
insurance price and quest grant configuration, and commits through the same item
transaction kernel.

SimulationWorker also owns live corpse discovery, proximity, and viewer state.
The runtime store contains only AuthService-provided corpse identity, transform,
presentation, section summaries, revision, and absolute expiry. A shared
per-peer authority queue permits one in-flight item or corpse operation and one
active corpse view per player while allowing many viewers on one corpse. Every
open, refresh, or mutation rechecks three-dimensional proximity and the cached
database-timed lifetime before crossing the service boundary.

For local testing only, SimulationWorker loads an environment-specific
Development option that can make Bank and Recovery access globally true. Startup
rejects that option in every other environment. The override does not include
insurance and does not move authority into Unity or SimulationWorker: the worker
still supplies its evaluated access state, and AuthService still validates the
exact live identity and commits the durable transaction. Production therefore
retains authored service-point proximity without a separate code path or protocol
contract.

Player corpses use durable PostgreSQL custody with a database-timed absolute
five-minute expiry. AuthService partitions one death event transactionally,
persists the corpse transform and presentation metadata, and expires remaining
loot through one durable cleanup operation. A replacement SimulationWorker
restores only open, unexpired corpses for its exact runtime and Shard assignment.
The worker continues reconciling that durable assignment at a bounded interval
so a newly created durable corpse becomes discoverable without restarting the
worker. Reconciliation remains idempotent and never imports another runtime's
corpse.
AuthService serves complete three-section view snapshots and commits corpse loot
through the existing transaction kernel. Targeted item and container revisions
let unrelated corpse operations commit even when the global corpse revision has
advanced. The corpse row still serializes final custody, and Bag roots are locked
before either aggregate's contents. A worker-local per-corpse mutation
coordinator sends at most one mutation for the same corpse to AuthService at a
time, while different corpses remain parallel. This prevents one hotspot from
occupying the database pool with clients waiting on the same row lock and keeps
AuthService as the idempotent final transaction authority. Open and refresh
send a complete snapshot only to the requesting viewer. A committed revision
broadcasts one targeted delta or complete replacement to every viewer. The
worker encodes each immutable update once, reuses those bytes across viewers,
and never holds a database transaction while waiting for a client.
Normal Mob corpses are worker-owned and disappear on restart. Content-selected
bosses reuse the durable path. Live claims use a deterministic grant id through
the exact-session item transaction boundary, while durable Mob corpses share the
player corpse record, restoration, interaction, and expiry code. These choices
do not introduce Zone or Layer ownership.

Phase 4 added authenticated reads. Phase 5 added the internal transaction kernel.
Phase 6 adds policy-safe account reads and offline mutations on that kernel.
Phase 7 adds session-bound carry state and shared SimulationWorker and Unity
encumbrance. Phase 8 adds service-authenticated active-character item mutation
without adding worker inventory state. Phase 9 adds the persistent Unity catalog,
snapshot, revision, operation-journal, targeted-refresh, reusable drag-and-drop
interaction layer, and temporary uGUI presentation. Unity still has no item
authority, SimulationWorker still holds no item collection, and there is no
gameplay grant route. A guarded one-shot Development fixture command uses the
existing durable kernel and is not a service endpoint.

Phase 10 adds an exact-session player-death route, an internal system death
adapter, durable corpse restoration for the assigned worker, and an idempotent
AuthService expiry loop. SimulationWorker holds only the bounded runtime corpse
identity and presentation state returned by AuthService, never PostgreSQL item
rows or an alternate custody model. Phase 11 adds exact-session corpse reads and
mutations, shared viewers, proximity and lifetime enforcement, committed deltas,
Unity presence and view state, temporary presentation, bidirectional item
transfers, ordinary occupied-slot swaps, and atomic corpse Bag swaps. Combat
death generation remains a later gameplay boundary.

Protocol version `11` retains the existing framing and adds corpse presence,
interaction, result, chunked view-state, and view-closure message types. Corpse
chunks are packed by encoded UTF-8 size under the existing `1200` byte limit.
The Phase 8 ordinary container-item swap continues to reuse its existing
two-item id and revision intent shape.

The complete planned contract is defined in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), with the
proposed schema and delivery order in
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

### World Actor And Interaction Boundary

Status: Phase 12 foundation plus Phase 13 and 14 lifecycle integration implemented

Phase 12 extends the repository boundaries without creating a new service or
topology layer:

- WorldData owns deterministic actor definitions, NPC capability descriptors,
  factions, presentation references, spawn definitions, spawn groups, spawn
  areas, respawn profiles, and patrol paths.
- SimulationWorker owns live actor identity, spawn lifecycle, active state,
  authoritative interaction, range and line-of-sight validation, NPC capability
  dispatch, and centrally scheduled Mob activity for its assigned Shard.
- GameProtocol uses version `13` for actor presence, interaction messages, and
  typed insurance or quest item-lifecycle actions
  compiled from the same source for .NET and Unity.
- Unity owns advisory crosshair targeting, immutable replicated actor and
  interaction state, prefab presentation, temporary uGUI, and Editor authoring
  tools.
- AuthService remains the durable authority when a later capability mutates
  items, currency, quests, policies, or progression.

NPC and Mob are distinct actor kinds. NPC capability combinations are content,
not subclasses. Faction and disposition determine friendly or hostile behavior.
Every first-version city NPC, including guards, is invulnerable by an
authoritative server rule.

Normal actors do not receive individual PostgreSQL rows. SimulationWorker
reconstructs their baseline from compiled WorldData when an assignment starts.
Stable actor-definition and spawn-definition ids remain distinct from fresh
worker-runtime actor and network entity ids. Selected unique actors may later
opt into durable state without changing the normal population model.

NPCs are event-driven. Mobs use bounded central dormant and active scheduling
buckets keyed by activity profile, tier, and tick interval. No actor owns a
task, timer, thread, database session, or HTTP poller. Actor visibility reuses
the shared network-entity id allocator and existing spatial interest
management. Bounded assignment-local despawn tombstones retain only the runtime
identity required to send reliable actor-specific interest exits, and clear on
assignment reconstruction or worker shutdown.

The typed capability registry maps every supported capability kind to one
authoritative handler. Deferred handlers remain the safe default. Phase 13
registers async insurance, quest-offer, and explicit quest-turn-in handlers
without changing actor identity or targeting. Insurance apply or removal and
quest accept or abandon reuse the established interaction session. Quest
completion remains unavailable because no progression authority exists yet.

The generic interaction flow is:

```text
Unity crosshair target and E intent
  -> assigned SimulationWorker
  -> exact session, target, Shard, revision, range, line-of-sight, state,
     capability, and rate validation
  -> one authoritative player-to-target interaction session
  -> focused capability summary and correlated results
  -> revalidation on every later capability operation
```

The shared values are a `6.0` metre client discovery distance, a `0.15` metre
client spherecast tolerance, a `3.0` metre authoritative start range, and a
`3.5` metre authoritative maintain range. The server measures from the
authoritative player root to the closest point on server-owned target bounds.
Client hit data is advisory and never proves authority.

One player may hold one active world interaction session, including an existing
corpse view. Many players may hold independent sessions with the same target.
Target despawn, target revision change, range or line-of-sight failure,
disconnect, or assignment change closes the affected session. The
player-specific capability summary is returned after open and is not duplicated
into ordinary spawn or movement packets.

Unity presentation resolves a `presentationArchetypeId` to a presentation-only
`WorldActorView`, `NpcView`, or `MobView` prefab. Authoritative vendor, quest,
crafting, insurance, or combat behavior never lives in a MonoBehaviour. The
permanent Editor entry points are
`Shooter MMO > Tools > Content > Actor Studio` and
`Shooter MMO > Tools > Content > Spawn Authoring`, with visual scene handles
that export canonical neutral WorldData.

The existing corpse view and transaction protocols remain authoritative. Phase
12 registers corpse presentation in the shared client target-selection
foundation and makes its view consume the shared interaction lease. Phase 13
adds policy and quest item lifecycle. Phase 14 combines live worker-owned Mob
corpses and durable player or selected boss corpses through that same presence,
view, range, lease, mutation, carry-state, and closure foundation. Vendor
transactions, quest progression, complete Mob AI, combat, damage, death-event
production, and loot-table generation remain later phases.

Phase 15 hardens this existing authority graph rather than adding a new service
or mutation path. AuthService bounds HTTP bodies, canonical commands,
PostgreSQL statements, and lock waits. It publishes identity-free item and corpse
measurements, carries a bounded correlation id across worker HTTP calls, and
runs system-authority maintenance for audited Recovery expiry plus policy-bound
retention. Closed corpses are removed only after their deadline and only when
their sections are empty. Durable destruction and death references prevent
operation evidence from being removed. SimulationWorker retains ownership of
live corpses and central actor scheduling.

## Durable Data Model

| Table | Responsibility |
| --- | --- |
| `accounts` | Global account identity and password hash |
| `account_sessions` | One active account login session and revocation reason |
| `characters` | Global characters owned by accounts |
| `world_definitions` | Shared World content identities |
| `fleets` | Regional or operational compute groups |
| `simulation_nodes` | Machines or hosts assigned to fleets |
| `shards` | Player-selectable simulations tied to a World and Fleet |
| `simulation_workers` | Logical workers and current runtime metadata |
| `simulation_assignments` | Historical and active worker-to-shard mappings |
| `simulation_join_tickets` | Short-lived exact-runtime admission credentials |
| `character_simulation_sessions` | Global active simulation leases |
| `schema_migrations` | Applied migration history |
| `item_catalog_revisions` | Applied deterministic catalog revisions and one current revision per catalog |
| `item_categories`, `item_tags`, `equipment_slots` | Stable catalog identities kept separate by domain meaning |
| `item_definitions` and `item_definition_*` | Mirrored definition data, tags, equipment compatibility, location rules, and default policies |
| `bag_definitions`, `bag_definition_slots`, `bag_definition_slot_tags` | Mirrored Bag capacity and specialized slot acceptance |
| `secure_container_tiers` | Mirrored account-selectable Secure Container tiers |
| `item_system_settings` | Data-driven base carry, inventory-slot, and bank-slot bootstrap values |
| `account_secure_container_entitlements` | One selected Secure Container tier per account |
| `item_containers`, `item_container_slots`, `item_container_slot_tags` | Typed custody identities and stable general or specialized slots |
| `character_item_states` | Character aggregate revision, weight, capacity, and required top-level container ids |
| `item_instances` | Durable quantity, revision, and exactly one container-slot or equipment assignment |
| `item_instance_policies` | Protected-on-death and insurance lifecycle foundation |
| `recovery_deliveries`, `recovery_delivery_items` | Per-character system delivery queue and delivered item membership |
| `item_operations` | Global idempotency id, canonical request hash, status, and replay result |
| `item_operation_changes`, `item_destructions` | State-change and destruction audit foundation |
| `corpses` | Durable source snapshot, Shard transform, presentation key, revision, absolute expiry, and close lifecycle |
| `corpse_sections` | General inventory, equipment, and Bag-section bindings to real item containers |
| `corpse_snapshots` | Non-interactive Secure tier, insured equipment, and protected or insured Bag presentation metadata |
| `death_events` | Unique authoritative death event, original operation, canonical request hash, corpse, and immutable result correlation |

Active account login sessions are unique by account. Active simulation sessions
are unique by both character and account. This prevents one account token from
running multiple characters simultaneously, even if the token is copied to a
second client.

Every service-created character has exactly one active permanent inventory,
bank, Secure Container, and Recovery Storage container. The character item-state
row references those exact owned container types. An item instance must point to
one existing container slot or one character equipment slot, never both, and
deferrable unique occupancy constraints allow only one item in either
assignment while preserving a future atomic swap path.

Deleting a character cascades its item state, owned containers, contained and
equipped items, policies, and recovery deliveries. The account-level Secure
Container entitlement remains until the account is deleted. Item operations and
change audit remain, while deleted actor and item references become null. This
keeps deletion behavior explicit without retaining live custody rows.

Corpse snapshots do not contain an item-instance foreign key. Real loot exists
only in section-container custody, while protected and insured real instances use
Recovery Storage. Deleting the source character may clear its corpse reference
without deleting the corpse name, transform, lifetime, sections, or audit.

## Runtime Identity And Assignment Safety

`SimulationWorkerId` is a stable logical process slot. Every process start also
creates a random `RuntimeId`. Tickets and simulation sessions bind to both.

Heartbeat processing is transactional:

1. AuthService locks the target shard row.
2. It validates World, Fleet, Node, and Shard topology.
3. A healthy existing assignment fences a different worker.
4. A timed-out or explicitly offline owner is released atomically.
5. Pending tickets and active sessions for every superseded runtime are
   invalidated, including when the logical worker moves to another shard.
6. A newer runtime may replace an older runtime for the same worker id.
7. The exact active SimulationAssignment is created or retained.

SimulationWorker renews a local registration lease from successful heartbeat
responses. UDP joins are rejected without a valid lease. If the lease expires,
or AuthService reports that the runtime lost authority, the process stops. This
limits split-brain behavior during network partitions and allows another worker
to take over after the authoritative heartbeat timeout.

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
`X-Simulation-Worker-Secret`. AuthService validates the configured secret with a
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

## Core Runtime Flows

### Topology Bootstrap

1. AuthService validates `Simulation:Topology` and loads every canonical World
   manifest at startup.
2. Database migrations create or upgrade the durable topology schema.
3. The seeder idempotently mirrors World definitions from manifests and ensures
   configured Fleets, Nodes, and Shards without selecting a World for them.
4. A newly created shard may remain unbound and is excluded from discovery and
   join admission until a worker completes its first accepted heartbeat.
5. Seeded shards remain offline because topology alone does not create a healthy
   worker assignment.

### Worker Registration

1. SimulationWorker validates config and loads
   `WorldData/Worlds/<WorldId>/world.json`, actor runtime, and collision runtime.
2. It binds UDP and reports transport readiness.
3. It sends worker, runtime, topology, desired World, endpoint, capacity,
   protocol, simulation, and collision metadata to AuthService.
4. AuthService validates the World database entry and fleet boundaries, safely
   binds or rebinds the shard, creates the assignment, and returns a
   database-timed online lease.
5. The worker renews its local registration lease and continues heartbeats.

### Shard Discovery And Placement

1. Unity calls `GET /api/shards`.
2. AuthService lists logical shards and reconciles worker-reported connections
   with authoritative unexpired simulation sessions while aggregating fresh
   assigned-worker capacity.
3. Worker host and UDP port are not exposed in this public list.
4. Unity requests `POST /api/shards/{shardId}/join` for an owned character.
5. AuthService locks the account and character, applies global active-session
   rules, reserves authoritative session and pending-ticket capacity under a
   worker row lock, and creates a ticket.
6. The response contains the selected shard plus an exact worker runtime
   endpoint and compatibility metadata.

### Join And Reconnect

1. Unity connects to the placement endpoint and sends the ticket reliably.
2. SimulationWorker consumes it through AuthService with its worker and runtime
   identity.
3. Ticket consumption and session claim occur in one PostgreSQL transaction.
4. SimulationWorker creates or reconnects the entity and binds it to the peer.
5. A new entity is added incrementally to the spatial interest index. Existing
   visibility sets are updated only for nearby observers instead of globally
   refreshing every peer.
6. Join acceptance returns topology, compatibility, movement, entity, and
   non-secret session metadata.
7. A reliable spawn baseline establishes the joining peer's presentation state,
   while only affected existing peers receive the new entity spawn.

A reconnect is valid only for the exact active shard, worker, and runtime. It
preserves session and entity state, rotates the secret simulation-session token,
and disconnects the older peer generation.

### Leave, Disconnect, Expiry, And Failover

1. Unity requests leave with the exact simulation-session id.
2. SimulationWorker releases the exact session token generation through
   AuthService.
3. The worker removes the peer binding and entity and sends reliable despawn.
4. Unexpected disconnect uses the same cleanup path.
5. If release is unavailable, the database lease expires and local expiry
   enforcement removes the session.
6. Worker shutdown or authoritative failover releases its assignment, pending
   tickets, and bound sessions.

### Authoritative Movement

1. SimulationWorker sends validated movement settings, shared-data revisions,
   and the admission-fenced carry tuple on join.
2. Unity samples Input Actions at the provided fixed rate, predicts locally,
   and sends redundant batches of recent unacknowledged inputs.
3. SimulationWorker processes only newer sequences at 30 Hz. Clients send input,
   never accepted positions.
4. Static and dynamic collision plus carry-state sprint and speed rules run
   through the shared simulation. Both server and Unity use the exact committed
   item-state revision, weight, capacity, and fixed-point multiplier.
5. Session heartbeat advances carry state monotonically. SimulationWorker sends
   a reliable carry-state update before the newer state affects local movement.
6. Interest management determines which entities each connection can observe.
7. SimulationWorker sends visible authoritative states at 15 Hz.
8. The local client acknowledges, rewinds, and replays prediction. Remote clients
   interpolate behind the latest server tick.

### Authoritative In-World Item Mutation

Status: Implemented transport and authority boundary

1. Unity sends an item interaction intent to its assigned SimulationWorker.
2. SimulationWorker validates the exact live session, proximity, interaction,
   and service-access rules.
3. The worker calls AuthService over its service-authenticated channel with an
   idempotent operation id and exact worker-runtime fencing.
4. AuthService locks and validates durable PostgreSQL item state, commits the
   complete transaction, and returns new inventory and carry-state revisions.
5. SimulationWorker updates authoritative encumbrance only from the committed
   result, sends a reliable carry update before it affects movement, and forwards
   the result to Unity.
6. Unity applies only a newer authoritative carry revision, correlates the
   operation id, and refreshes the authoritative Bank, Recovery, or complete
   item snapshot until it reaches the committed revision. It never optimistically
   mutates item custody or quantity.

No database transaction remains open across a client network round trip.

### Authoritative Player Death, Restoration, And Loot

Status: Durable Phase 10 and interactive Phase 11 boundaries implemented, live
combat producer pending

1. The future authoritative combat system produces one unique death event only
   after its owning SimulationWorker has resolved death. Until that producer
   exists, the same boundary is exercised by system and PostgreSQL tests.
2. SimulationWorker submits the exact account, character, simulation session,
   token, worker id, runtime id, Shard, expected item-state revision, transform,
   and generic corpse presentation key over its authenticated AuthService client.
3. AuthService revalidates the complete live authority, claims both the item
   operation and death-event identities, then locks the character, Bag
   aggregates, containers, items, and policies in the durable transaction.
4. Currency, bank, Recovery Storage, and Secure Container custody remain where
   they were. Protected and effective insured items move to correlated Recovery
   deliveries. Remaining permanent inventory, equipment, Bag roots, and Bag
   children move to durable corpse sections. All affected revisions, carry state,
   policy consumption, snapshots, and audit commit together.
   If removing the equipped Bag bonus leaves retained Secure Container weight
   above the hard cap, death still commits. The durable transaction kernel then
   permits only non-worsening remediation until the state returns within the
   cap.
5. Each corpse stores database creation time plus the exact five-minute player
   deadline. AuthService cleanup locks the corpse first, destroys each remaining
   item once with audit, and closes even an already-empty corpse only at that
   deadline.
6. After worker registration, SimulationWorker requests one read-only snapshot
   for its exact fresh runtime and active Shard assignment. It keeps the generic
   presentation state against database time advanced by a monotonic local clock.
7. Protocol-v11 presence snapshots expose nearby runtime identity and
   presentation only. A player may open one corpse within three metres. Multiple
   players may inspect the same corpse, and the worker validates proximity and
   cached lifetime before every refresh or mutation.
8. AuthService opens a short read-only transaction for a complete three-section
   snapshot. Loot, deposit, corpse-internal movement, and Bag swaps enter the
   normal item transaction kernel with targeted item, destination, and Bag
   aggregate revisions. Full stacks move, partial stacks split or merge,
   complete incompatible items swap only between mutually compatible slots,
   and occupied Bag roots exchange their complete content aggregates atomically
   under the standard capacity and hard-cap rules. Corpse equipment slots carry
   their canonical equipment-slot id and reject incompatible definitions.
9. A per-corpse worker coordinator serializes calls into the same durable
   aggregate without serializing different corpses. The mutation transaction
   closes before AuthService loads the committed view. SimulationWorker applies
   returned carry only for custody-changing transfers, keeps the newest corpse
   revision when HTTP completions arrive out of order, encodes one targeted
   delta, and reuses it for every viewer. Open and refresh snapshots return only
   to their requester. Pure corpse rearrangement advances corpse, container,
   and item revisions without changing character carry or item-state revision.
   Unity refreshes stale bases and never applies optimistic custody.
10. Expiry, invalidation, and not-found results close every affected view with a
    stable code. Runtime or session fencing failures disconnect the affected
    peer. A worker restart restores the corpse at its persisted transform with
    the unchanged deadline.

## Configuration Ownership

- `AuthService/Config` owns AuthService defaults and topology bootstrap.
- `SimulationWorker/Config` owns worker identity, endpoint, networking,
  simulation, collision streaming, quotas, and interest settings.
- `shooter-mmorpg-unity-client/Assets/Resources/Config` owns runtime Unity
  endpoint and timeout configuration.
- The ignored root `.env` owns local secrets and machine overrides.
- `.env.example` is the committed environment contract.
- Root Compose, solution, SDK, and build files are repository-wide concerns.

Both backend processes load owned JSON, optional root `.env`, real environment
variables, and command-line values in increasing precedence. Invalid application
configuration fails before work is accepted.

## Health And Operations

AuthService exposes `/health/live` and `/health/ready`. Readiness performs a real
PostgreSQL query and Redis `PING` and returns HTTP 503 when an obligatory
dependency fails.

SimulationWorker uses `--health-check-only` because it has no HTTP server. It
checks AuthService readiness, Redis, and UDP port availability and returns exit
code 1 on failure.

Local Compose ports bind to `127.0.0.1`. Realtime metrics avoid account,
character, session, entity, or peer ids as metric labels.

## Future Scaling Contract

The next topology extension is not another naming rewrite. It will add:

- Zone definitions and authoritative zone ownership inside a shard.
- Cross-zone handoff with one owner at every simulation tick.
- Layer definitions for selected high-population areas inside a shard.
- Placement policies that keep groups together and prevent content exploits.
- Durable shared event policies for bosses, resources, and economy where layers
  must not create duplicate rewards.

Until that work exists, a shard is never presented as partially distributed.
Only UI may remain explicitly temporary. All other boundaries in this document
are production-oriented foundations.

# Project Architecture

Last updated: 2026-07-15

## Purpose

This document is the source of truth for project-wide architecture, naming,
ownership, trust boundaries, and runtime flows. Detailed Unity internals belong
in [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md). Implemented
behavior belongs in the two feature documents.

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

World: local-world-1
  shared map, collision, and future content definitions

Fleet: local-fleet
  Node: local-node-1
    SimulationWorker: local-simulation-worker-1
      Worker runtime: generated opaque id per process start
      SimulationAssignment: worker -> local-shard-1

Shard: local-shard-1
  World: local-world-1
  Fleet: local-fleet
```

A shard belongs to one fleet and references one World definition. A node also
belongs to one fleet. AuthService permits an assignment only when the worker's
node and target shard belong to the same fleet.

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

### SimulationWorker

`SimulationWorker` is a .NET Generic Host console application. It does not host
ASP.NET. LiteNetLib owns its UDP endpoint. It owns:

- UDP admission, protocol validation, and join and leave handshakes.
- Exact worker runtime identity and its AuthService registration lease.
- Active simulation sessions and connection-to-entity bindings.
- Server-assigned network entity ids and reliable spawn and despawn.
- Fixed-rate authoritative movement and collision queries.
- Input sequence processing and periodic snapshots.
- Spatial interest management with enter and exit hysteresis.
- Snapshot encoding reuse for peers with equal visibility.
- Per-peer UDP quotas, fair aggregate snapshot backpressure, and
  low-cardinality realtime metrics.
- Server-side population gauges that distinguish real players, synthetic bots,
  and unauthenticated peers without extending the gameplay protocol.
- Bounded session heartbeat fan-out.
- Position-driven world collision chunk streaming.
- Fail-fast configuration and collision validation.

Configuration lives under `SimulationWorker/Config`. A process receives its
Fleet, Node, Shard, and World identity through validated configuration.

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

Protocol version 7 includes:

- Join, leave, rejection, and structured disconnect messages.
- Shard and World identity in join acceptance.
- Server-assigned network entity ids.
- Reliable ordered entity spawn and despawn.
- Bounded, sequenced movement input batches.
- Chunked simulation snapshots with server tick, snapshot sequence, and input
  acknowledgement.
- Movement, simulation revision, and collision revision metadata.
- Admission-fenced carry state in join acceptance and reliable ordered updates
  for newer committed item-state revisions.
- Packet magic, version, type, size, and bounded-field validation.

Channel 0 is reliable ordered control. Channel 1 is sequenced movement input.
Snapshots use unchanneled unreliable delivery and application-level tick and
chunk metadata.

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

`WorldData` is shared content, so it correctly remains at repository root. It
contains neutral collision authoring and compiled, checksummed chunks keyed by
World id. A shard references a World and its worker loads that World's data.

`Tools/WorldCollisionCompiler` builds and verifies the same data consumed by
SimulationWorker and Unity. Static collision is deterministic and shared.
Dynamic collision uses a mutable spatial hash behind the same collision-query
interface, leaving room for doors, lifts, and other server-owned objects.

`WorldData/Authoring/Items` now owns the strict neutral item catalog source, and
`WorldData/Runtime/Items` contains its deterministic compiled form. The catalog
has stable identities, one complete revision, per-definition and per-tier
structural fingerprints, and no Shard identity. Framework-neutral catalog and
pure rule source is compiled for Unity by the `ShooterMmo.WorldData` assembly and
for .NET tooling and tests through `Shared/DotNet/WorldData`. Neither compilation
target becomes an authority. AuthService validates and mirrors the compiled
catalog at startup. Future mutation paths must still reapply the authoritative
rules before committing durable item state.

The current pure rules cover stack compatibility, Bag slot tag acceptance,
equipment compatibility, Secure Container eligibility, empty and non-empty Bag
locations, Bag containment-cycle rejection, unitless integer weight arithmetic,
base character capacity `200`, the 140 percent hard cap, and the linear
fixed-point encumbrance multiplier. GameSimulation wraps the carry tuple in an
immutable shared movement state and applies the same multiplier and sprint
threshold in SimulationWorker and Unity prediction. These rules have
no HTTP, PostgreSQL, UnityEngine, or SimulationWorker runtime dependency.

`WorldData/Editor/Items` contains the Editor-only `ShooterMmo.WorldData.Editor`
assembly and the canonical `Tools > Shooter MMO > Item Catalog` window. It edits
the neutral authoring JSON and invokes the same strict .NET compiler used by CI.
Runtime WorldData assemblies do not reference `UnityEditor`.

Unity owns a separate client presentation catalog keyed by stable definition id
under `Assets/Resources/Items/Presentation`. It references icons, localization
keys, fallback text, and optional prefab presentation keys, but cannot duplicate
or override authoritative gameplay fields. Its deterministic presentation
revision records the exact source gameplay revision. The client validates and
caches the bundled catalog once per matching revision, while server item state
uses definition ids instead of transferring presentation assets.

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
external stress tools. `SimulationStressGenerator` hosts an in-memory loopback
authority and manually polled headless UDP clients without adding per-bot
transport threads or a stress admission path to production services. Its
bounded latency reservoirs and process samplers make longer local soak tests
safe to run without the tool itself accumulating every acknowledgement sample.

`ActiveSimulationBots` reuses the same tool-only headless UDP client core but
runs beside the real AuthService and SimulationWorker. A Development-only,
loopback-only AuthService endpoint issues protected in-memory bot identities and
exact-runtime tickets. Worker ticket consumption, session heartbeat and release
route in-memory bot credentials to that authority and all other credentials to
the unchanged PostgreSQL services. Reserved worker capacity prevents bots from
occupying every connection slot needed by real local players.

### Durable Item Boundary

Status: Phases 1 through 7 content, authoring, schema, catalog mirror, character
bootstrap, authoritative reads, policy lifecycle, internal transaction kernel,
offline account APIs, carry-state delivery, and shared encumbrance implemented;
realtime item mutation integration planned

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
part of the Phase 8 worker boundary.

All durable commands enter `ItemTransactionService`. One command opens one
connection and one `READ COMMITTED` transaction, claims the operation row, and
then locks character item states, Bag aggregate roots and containers, item rows,
and policy or delivery rows in canonical sorted order. A savepoint lets a stable
domain rejection persist its idempotent result while rolling back the complete
candidate mutation. Successful commands recompute weight and equipped Bag
capacity, advance revisions, append relational audit changes, and persist a
replayable result in the same transaction.

The transaction context resolves current definitions and slot data from the
mirrored catalog but delegates stack, equipment, Bag, Secure Container, policy
capability, and integer-weight decisions to WorldData rules. The kernel does not
depend on HTTP, Unity, or SimulationWorker runtime state. Account and system
authorization contexts are explicit command inputs. Offline access is an actor
requirement enforced while the durable character lock is held.

`ItemPolicyService` applies protected-on-death and eligible one-death insurance
records and removes active insurance through idempotent system transactions.
`QuestItemService` grants protected quest items with exact quest-grant lineage,
suppresses duplicate active grants, and removes only that lineage on abandon.
These internal services do not add an insurance NPC, quest gameplay runtime, or
public grant route.

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

SimulationWorker will also own live proximity, interaction, combat, and corpse
presentation. While a character is active, the worker will request durable item
mutations through an authenticated AuthService boundary fenced to the exact
character, simulation session, worker, runtime, and Shard. SimulationWorker
will not write inventory tables directly.

Player corpses will use durable custody with an absolute expiry and can be
restored by a replacement worker. Normal NPC corpses may remain worker-owned and
disappear on restart, while content-selected bosses may use the durable corpse
path. These choices do not introduce Zone or Layer ownership.

Phase 4 added authenticated reads. Phase 5 added the internal transaction kernel.
Phase 6 adds policy-safe account reads and offline mutations on that kernel.
Phase 7 adds session-bound carry state and shared SimulationWorker and Unity
encumbrance. There is still no gameplay grant route, worker inventory state,
service-authenticated worker item mutation route, or Unity inventory state. The
Unity content tooling remains presentation and authoring support, not item
authority.

The complete planned contract is defined in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), with the
proposed schema and delivery order in
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

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

This table lists the implemented schema only. Corpse identity, corpse sections,
death events, and snapshot tables remain planned for their later phase. The
current container type contract can represent their future item custody without
introducing Zone or Layer identity.

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

1. AuthService validates `Simulation:Topology` at startup.
2. Database migrations create or upgrade the durable topology schema.
3. The seeder idempotently ensures configured Worlds, Fleets, Nodes, and Shards.
4. Seeded shards remain offline because topology alone does not create a healthy
   worker assignment.

### Worker Registration

1. SimulationWorker validates config and loads collision near the spawn point.
2. It binds UDP and reports transport readiness.
3. It sends worker, runtime, topology, endpoint, capacity, protocol, simulation,
   and collision metadata to AuthService.
4. AuthService validates or creates the assignment and returns a database-timed
   online lease.
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

### Planned In-World Item Mutation

Status: Planned and not implemented

1. Unity sends an item or corpse interaction intent to its assigned
   SimulationWorker.
2. SimulationWorker validates the exact live session, proximity, interaction,
   and service-access rules.
3. The worker calls AuthService over its service-authenticated channel with an
   idempotent operation id and exact worker-runtime fencing.
4. AuthService locks and validates durable PostgreSQL item state, commits the
   complete transaction, and returns new inventory and carry-state revisions.
5. SimulationWorker updates authoritative encumbrance only from the committed
   result and forwards the result to Unity.

No database transaction remains open across a client network round trip.

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

# Project Overview

Last updated: 2026-07-15

## What Shooter MMO Is

Shooter MMO is an early classless, profession-driven, open-world MMORPG with a
modern third-person shooter direction. The project is currently focused on a
stable technical foundation for identity, character access, regional compute,
server-authoritative simulation, and a Unity client before larger gameplay
systems are added.

The current playable flow is intentionally small:

1. A player registers or logs in through AuthService.
2. The player creates and selects a character.
3. The client lists logical shards and selects one.
4. AuthService places the character on the healthy SimulationWorker assigned to
   that shard and returns a short-lived ticket plus its UDP endpoint.
5. Unity connects through LiteNetLib, presents the ticket, and enters WorldScene.
6. Unity predicts local movement while SimulationWorker remains authoritative.
7. Leaving the shard releases the exact simulation session before returning to
   character selection.

## Shared World And Global Player Data

There are no realms in this architecture. Accounts, characters, progression,
and the future economy are global services shared by all fleets and shards. A
character is not permanently owned by an EU or US shard and can later choose a
different region with the same durable data.

The word `World` means game content and world data, such as map identity,
collision data, terrain, and future content definitions. It does not mean a
machine, process, region, or player-facing server.

The runtime topology uses these terms:

| Term | Meaning |
| --- | --- |
| Fleet | A regional or operational group of compute, such as EU or US |
| Node | One machine or container host inside a fleet |
| SimulationWorker | One headless simulation process running on a node |
| SimulationAssignment | The authoritative mapping between a worker and a shard |
| Shard | A player-selectable copy of the shared world simulation |
| World | Shared content and data consumed by one or more shards |

Zones and layers are reserved for future spatial partitioning and population
scaling. They are not implemented and are not faked in the current runtime.

## Main Components

- **Unity client** owns presentation, input, prediction, reconciliation, remote
  interpolation, scene flow, and temporary UI.
- **AuthService** owns accounts, account sessions, characters, topology,
  SimulationWorker registration, shard discovery, placement, join tickets, and
  global character simulation-session leases. It also owns the PostgreSQL item
  schema, catalog mirror, character item-state bootstrap, and authenticated
  read models.
- **SimulationWorker** is a headless .NET console process that owns the realtime
  UDP transport, active entities, interest management, and authoritative
  movement for its assigned shard.
- **PostgreSQL** is the durable authority for identity, topology, assignments,
  tickets, session leases, mirrored item definitions, and the item custody
  foundation.
- **Redis** is currently an operational readiness dependency. It does not yet
  own gameplay or authentication state.
- **GameProtocol** is the versioned binary contract shared by Unity and
  SimulationWorker.
- **GameSimulation** is the fixed-step movement and collision implementation
  compiled from the same source for server authority and client prediction.
- **WorldData** contains neutral world collision authoring, checksummed runtime
  chunks, deterministic item content, and framework-neutral pure item rules.
- **Shared** contains framework-neutral configuration, networking, and health
  helpers for backend processes.

## Current Foundation

The repository currently supports:

- PostgreSQL-backed registration, login, logout, session revocation, and one
  active client session per account.
- Character creation and listing.
- Explicit World, Fleet, Node, Shard, SimulationWorker, and
  SimulationAssignment records.
- Seeded shards that remain offline until a valid worker heartbeat exists.
- Worker runtime generations, exact-runtime service authentication, heartbeat
  leases, graceful offline registration, and stale-worker failover.
- Split-brain protection that stops a worker after its local registration lease
  expires or AuthService rejects its authority.
- Capacity-aware shard placement that exposes a worker endpoint only in the
  short-lived join response.
- Join tickets bound to an exact character, shard, worker, and worker runtime.
- One active simulation session per character across every shard and region.
- Reconnect, heartbeat, expiry, runtime fencing, and exact release behavior.
- LiteNetLib UDP join, leave, structured disconnect, and reliable entity
  lifecycle messages.
- Server-assigned network entity ids and one-to-one connection ownership.
- Spatial interest management, per-peer UDP quotas, bounded heartbeat
  concurrency, and low-cardinality network metrics.
- Sequenced movement input, a fixed 30 Hz authoritative simulation, 15 Hz
  snapshots, local prediction, reconciliation, and remote interpolation.
- Shared capsule collision against the authored test map, including walls,
  ramps, steps, cover, slope handling, and position-driven collision chunks.
- Split liveness and readiness checks with real PostgreSQL and Redis probes.
- Structured HTTP errors, correlation ids, authentication rate limits, and
  no-store token responses.
- Unity API timeouts, serialized operations, duplicate-click protection, 401
  recovery, and persistent realtime state across scene changes.
- Backend unit and isolated PostgreSQL integration tests plus Unity EditMode and
  PlayMode coverage.
- A finite SimulationWorker benchmark and a separate long-running active bot
  population tool, both using a shared headless UDP client foundation without
  persistent bot accounts.
- A strict item catalog compiler with nine representative definitions, stable
  content identities, deterministic catalog and structural revisions, and pure
  stack, slot, equipment, Secure Container, Bag, unitless integer-weight, and
  encumbrance rules. The shared base character capacity is `200`.
- A Unity Editor item catalog workflow that edits the neutral gameplay source,
  delegates validation and structural fingerprints to the shared compiler,
  bakes deterministic runtime content, and maps every definition to a bundled
  client presentation entry with a separate revision.
- A transactional PostgreSQL catalog mirror, exact item location and occupancy
  constraints, and idempotent active-character bootstrap with empty inventory,
  bank, Secure Container, and Recovery Storage identities.
- Authenticated, read-only catalog and complete owned-character inventory
  snapshots that resolve instance definition ids against one catalog revision.
- An internal AuthService item transaction kernel that owns idempotency,
  authorization context, optimistic revisions, stable row locking, slot and Bag
  validation, prospective weight, carried-state updates, and audit.

## Current Scale Boundary

The data model and contracts separate logical topology from process runtime, but
the current executable deliberately uses one active SimulationWorker per shard.
The database enforces one active assignment per worker and one active worker per
shard. This is the safe base unit before a shard is split spatially.

When required, the next scale step is to add zones as authoritative spatial
partitions and layers as controlled population copies inside a shard. That work
will extend SimulationAssignment and placement. It must not redefine World or
introduce isolated realms.

## Item Foundation Status And Next Step

Phases 1 through 5 of the durable item and inventory plan are complete. The
repository has the neutral WorldData catalog, deterministic runtime content,
structural change detection, strict shared validation, pure rules, a custom
Unity authoring and bake window, and a transactional AuthService PostgreSQL
mirror. The schema now represents item location, containers, slots, equipment,
policies, recovery, operations, and audit. Every active character receives
empty permanent inventory, bank, Secure Container, and Recovery Storage state
with base carry capacity `200`. Account-authenticated reads expose the current
catalog and one coherent owned-character snapshot without permitting mutation.

AuthService now also has one internal transaction kernel for grants, relocation,
equipment, stack changes, consumption, destruction, complete Bag swaps, Recovery
deliveries and claims, and Secure Container tier changes. It commits item,
container, character, carry, entitlement, idempotency, and audit state in one
PostgreSQL transaction. The foundation intentionally has no item mutation HTTP
surface or player-facing inventory behavior. The next approved step is Phase 6's
policy-safe account API boundary.

The remaining locked direction is slot-based rather than grid-based and includes:

- Account and worker mutation surfaces built on the implemented internal
  transaction kernel and PostgreSQL item-definition, instance, slot, operation,
  audit, policy, and read-model foundation.
- Player and gameplay access to permanent character inventory, per-character
  bank, equipment, physical Bag items, per-character Secure Container contents,
  and account-selected Secure Container tiers.
- Unitless integer carry weight, base character capacity `200`, a 140 percent
  hard cap at base weight `280`, and shared authoritative encumbrance behavior.
- A player inventory layout with equipment on the left, contextual containers
  in the upper-right area, and character inventory in the lower-right area.
- Player-visible claims from the system-write-only Recovery Storage foundation.
- Transactional death partition, durable five-minute player corpses, concurrent
  looting, one-death insurance, and configurable NPC corpse persistence.

The persistent schema, character custody identities, authoritative reads, and
internal item mutations exist, but no gameplay system invokes item grants or
mutations and no player-facing behavior exists. See
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) and
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

## Intentionally Deferred

- Zone ownership, cross-zone handoff, and layer orchestration.
- Triangle-mesh terrain and cave collision beyond the oriented-box test map.
- Replicated dynamic collision transforms and general rigid-body simulation.
- Combat, weapons, abilities, damage, death, and respawning.
- Gameplay-created item instances, player or worker mutation routes, bank service
  access, live carry-state integration, Unity inventory behavior, corpse
  identity, and loot transactions.
- Crafting, gathering, professions, and the broader economy.
- Persistent NPCs, quests, guilds, social systems, and world events.
- Production orchestration, metric export, dashboards, alerts, and live
  operations.

## Engineering Standard

Only UI is intentionally temporary while the custom interface is designed.
Networking, state ownership, service boundaries, gameplay systems, persistence,
configuration, and tooling are maintainable foundations from their first
implementation.

## Where To Read Next

- [Project Architecture](PROJECT_ARCHITECTURE.md) is the system-wide source of
  truth.
- [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md) explains the client
  in detail.
- [Service Features](SERVICE_FEATURES.md) records implemented backend behavior.
- [Game Features](GAME_FEATURES.md) records implemented player-facing behavior.
- [Local Development](LOCAL_DEVELOPMENT.md) explains setup and verification.
- [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) explains local visual
  population and connection churn testing.
- [MVP Specification](MVP_SPEC.md) defines the current product scope.
- [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) defines
  the locked planned inventory and death-loot rules.
- [Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md)
  defines how that foundation will be delivered and verified.

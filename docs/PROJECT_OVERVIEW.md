# Project Overview

Last updated: 2026-07-16

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
- Authenticated item-state, bank, and Recovery Storage reads plus a neutral
  item catalog with revision ETag validation and no client presentation assets.
- An internal AuthService item transaction kernel that owns idempotency,
  authorization context, optimistic revisions, stable row locking, slot and Bag
  validation, prospective weight, carried-state updates, and audit.
- Offline-safe account item operations for relocation, split, merge,
  destruction, Recovery claims, and account Secure Container tier changes.
- Pure policy capability evaluation, auditable protected and insured records,
  insurance removal, and exact quest-grant cleanup and reaccept behavior.
- Admission-fenced and heartbeat-refreshed carry state, protocol version `8`,
  movement revision `movement-simulation-v3`, and identical authoritative and
  predicted encumbrance behavior.

## Current Scale Boundary

The data model and contracts separate logical topology from process runtime, but
the current executable deliberately uses one active SimulationWorker per shard.
The database enforces one active assignment per worker and one active worker per
shard. This is the safe base unit before a shard is split spatially.

When required, the next scale step is to add zones as authoritative spatial
partitions and layers as controlled population copies inside a shard. That work
will extend SimulationAssignment and placement. It must not redefine World or
introduce isolated realms.

## Item Foundation Status

Phases 1 through 9 of the durable item and inventory plan are complete. The
repository has the neutral WorldData catalog, deterministic runtime content,
structural change detection, strict shared validation, pure rules, a custom
Unity authoring and bake window, and a transactional AuthService PostgreSQL
mirror. The schema now represents item location, containers, slots, equipment,
policies, recovery, operations, and audit. Every active character receives
empty permanent inventory, bank, Secure Container, and Recovery Storage state
with base carry capacity `200`. Account-authenticated reads expose the current
catalog, one coherent owned-character snapshot, and focused bank and Recovery
Storage views.

AuthService now also has one internal transaction kernel for grants, relocation,
equipment, stack changes, ordinary container-slot swaps, consumption,
destruction, complete Bag swaps, Recovery
deliveries and claims, and Secure Container tier changes. It commits item,
container, character, carry, entitlement, idempotency, and audit state in one
PostgreSQL transaction. Phase 6 adds policy application and removal, exact
quest-grant cleanup, conditionally cached catalog reads, stable Problem Details,
and account-session mutation routes. Account mutations lock the same character
row as simulation admission and reject an active session with
`item_offline_access_required`. Phase 7 carries the committed item-state
revision, weight, and base plus Bag capacity through admission and heartbeat to
SimulationWorker. The shared GameSimulation rules now apply the exact sprint
threshold and linear movement multiplier in both authoritative movement and
Unity prediction. Phase 8 adds bounded reliable item intents, authoritative
worker service-point access, exact live-session and worker-runtime fencing, and
committed carry propagation through the existing durable kernel. Phase 9 adds
the persistent Unity catalog and inventory controller, monotonic complete and
focused snapshots, operation journaling, authoritative refresh, reconnect
restoration, and the temporary three-area uGUI inventory panel.

Carried weight excludes every equipment-slot item, including the equipped Bag
root. Permanent inventory, equipped Bag contents, carried empty Bags, and Secure
Container contents still count, while the equipped Bag capacity bonus remains
active. Startup bootstrap reconciles older denormalized carry rows and advances
their item-state revision only when the stored tuple changes.

The remaining locked direction is slot-based rather than grid-based and includes:

- Final inventory visual design, drag-and-drop interaction polish, accessibility,
  and item policy detail presentation on the implemented client foundation.
- Transactional death partition, durable five-minute player corpses, concurrent
  looting, one-death insurance, and configurable NPC corpse persistence.

The persistent schema, character custody identities, authoritative reads,
offline account mutations, policy services, internal item mutations, and
player-facing Unity inventory foundation exist, but no gameplay system invokes
item grants. See
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) and
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

## Intentionally Deferred

- Zone ownership, cross-zone handoff, and layer orchestration.
- Triangle-mesh terrain and cave collision beyond the oriented-box test map.
- Replicated dynamic collision transforms and general rigid-body simulation.
- Combat, weapons, abilities, damage, death, and respawning.
- Gameplay-created item instances, corpse identity, death partition, and loot
  transactions.
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

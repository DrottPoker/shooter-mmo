# Project Overview

Last updated: 2026-10-20

## What MMORPG Is

MMORPG is an early classless, profession-driven, open-world MMORPG with a
World of Warcraft style fantasy setting and RuneScape-inspired click-to-move
combat, defined in [Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md).
That direction replaces the earlier modern third-person shooter direction.
Click-to-move movement and the first combat slice, one bow with arrows against
wolves and other players, are implemented; the rest of that direction is not
implemented yet. The project is currently focused on a
stable technical foundation for identity, character access, regional compute,
server-authoritative simulation, and a Unity client before larger gameplay
systems are added.

The current playable flow is intentionally small:

1. A player registers or logs in through GameService.
2. The player creates and selects a character.
3. The client lists logical shards and selects one.
4. GameService places the character on the healthy SimulationWorker assigned to
   that shard and returns a short-lived ticket plus its UDP endpoint.
5. Unity resolves the accepted World through its scene catalog, connects through
   LiteNetLib, presents the ticket, and enters that authored World scene.
6. Unity predicts local movement while SimulationWorker remains authoritative.
7. In the World, the player can buy a pickaxe from the NPC vendor with Gold,
   mine Iron Ore from shared Iron Veins, and sell the ore back to the vendor.
8. With the Training Shortbow and Bronze Arrows equipped, the player can fight
   the wolves and other players, loot their corpses, die, and respawn.
9. Leaving the shard releases the exact simulation session before returning to
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

A shard references one World at a time, but that binding may be changed between
worker process generations. GameService accepts the change only while the shard
is offline and has no pending join, active simulation session, or open durable
corpse state. Runtime World changes are not supported.

Zones and layers are reserved for future spatial partitioning and population
scaling. They are not implemented and are not faked in the current runtime.

## Main Components

- **Unity client** owns presentation, input, prediction, reconciliation, remote
  interpolation, scene flow, and temporary UI.
- **GameService** owns accounts, account sessions, characters, topology,
  SimulationWorker registration, shard discovery, placement, join tickets, and
  global character simulation-session leases. It also owns the PostgreSQL item
  schema, catalog mirror, character item-state bootstrap, authenticated read
  models, and the character currency wallet with its ledger.
- **SimulationWorker** is a headless .NET console process that owns the realtime
  UDP transport, active players and world actors, interest management,
  authoritative movement, actor activity tiers, interaction sessions, live
  resource node charges, gathering cycles, and respawn, and authoritative combat,
  Mob brains, deaths, and respawns for its assigned shard.
- **PostgreSQL** is the durable authority for identity, topology, assignments,
  tickets, session leases, mirrored item definitions and vendor profiles, the
  item custody foundation, and character currency with its ledger.
- **Redis** accelerates account-session validation, coordinates authentication
  rate limits across GameService replicas, and remains an operational readiness
  dependency. PostgreSQL remains the durable authority and Redis owns no
  gameplay, inventory, or economy state.
- **GameProtocol** is the versioned binary contract shared by Unity and
  SimulationWorker.
- **GameSimulation** is the fixed-step movement and collision implementation
  compiled from the same source for server authority and client prediction.
- **WorldData** contains neutral world collision authoring, checksummed runtime
  chunks, deterministic item content, framework-neutral pure item rules, and
  deterministic actor and spawn content.
- **Shared** contains framework-neutral configuration, networking, and health
  helpers for backend processes.

## Current Foundation

The repository currently supports:

- PostgreSQL-backed registration, login, logout, session revocation, and one
  active client session per account.
- Character creation and listing.
- Explicit World, Fleet, Node, Shard, SimulationWorker, and
  SimulationAssignment records.
- Transactionally guarded offline Shard-to-World rebinding with worker-runtime
  fencing and World-keyed collision, actor, and Unity scene selection.
- Seeded shards that remain offline until a valid worker heartbeat exists.
- Worker runtime generations, exact-runtime service authentication, heartbeat
  leases, graceful offline registration, and stale-worker failover.
- Split-brain protection that stops a worker after its local registration lease
  expires or GameService rejects its authority.
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
- Shared capsule collision against the authored test maps, including walls,
  ramps, steps, cover, slope handling, terrain with hills, cliffs, and a
  tunnel, and position-driven collision chunks.
- Split liveness and readiness checks with real PostgreSQL and Redis probes.
- Structured HTTP errors, correlation ids, authentication rate limits, and
  no-store token responses.
- Bounded cache-aside account-session validation with fail-open PostgreSQL reads,
  revocation tombstones, and low-cardinality Redis acceleration metrics.
- Unity API timeouts, serialized operations, duplicate-click protection, 401
  recovery, and persistent realtime state across scene changes.
- Backend unit and isolated PostgreSQL integration tests plus Unity EditMode and
  PlayMode coverage.
- A finite SimulationWorker benchmark and a separate long-running active bot
  population tool, both using a shared headless UDP client foundation without
  persistent bot accounts.
- A strict item catalog compiler with eleven fantasy definitions, retired-id
  tombstones, character settings, item values, tool kinds, vendor profiles, the
  currency name and starting balance, deterministic catalog and structural
  revisions, and pure stack, slot, equipment, blocked-slot, Secure Container,
  Bag, unitless integer-weight, encumbrance, and vendor pricing rules. The base
  character carry capacity is `2000`.
- A Unity Editor item catalog workflow that edits the neutral gameplay source,
  delegates validation and structural fingerprints to the shared compiler,
  bakes deterministic runtime content, and maps every definition to a bundled
  client presentation entry with a separate revision.
- A transactional PostgreSQL catalog mirror, exact item location and occupancy
  constraints, and idempotent active-character bootstrap with empty inventory,
  bank, Secure Container, and Recovery Storage identities.
- Authenticated item-state, bank, and Recovery Storage reads plus a neutral
  item catalog with revision ETag validation and no client presentation assets.
- An internal GameService item transaction kernel that owns idempotency,
  authorization context, optimistic revisions, stable row locking, slot and Bag
  validation, prospective weight, carried-state updates, and audit.
- Offline-safe account item operations for relocation, split, merge, and
  destruction in carried storage and for account Secure Container tier changes.
  The Bank and Recovery Storage are reachable only in the world.
- Pure policy capability evaluation, auditable protected and insured records,
  insurance removal, and exact quest-grant cleanup and reaccept behavior.
- Admission-fenced and heartbeat-refreshed carry state, protocol version `18`,
  movement revision `movement-simulation-v6`, and identical authoritative and
  predicted encumbrance behavior.
- A persistent Gold balance per character with a starting balance of `100`, a
  wallet that writes one ledger row per change, and currency that stays with the
  character on death.
- One NPC vendor, Mira's General Store, that sells basic supplies and buys
  carried items at fixed prices through idempotent GameService transactions.
- Shared Iron Vein resource nodes with live Shard charges, continuous mining
  with a pickaxe in the `Tool` slot, depletion for every player, and respawn
  through a central timer queue.
- An art pipeline for 3D models: Blender sources in Git LFS, a review sheet and
  live review in Blender through the Blender Lab MCP server, recorded approvals
  that gate the FBX export, fixed Unity import rules, and a CI check of the whole
  chain. The Iron Vein is its first model, see [Art Pipeline](ART_PIPELINE.md).
- RuneScape-style click-to-move: the client plans a route around obstacles on
  a fine internal grid in the background, predicts it at once, and
  SimulationWorker follows the same waypoints with authoritative collision. An
  orbit camera turns with the middle mouse button or arrow keys and zooms from
  0.6 to 15 meters.
- A RuneScape-style world menu: a right click lists every option for every
  target under the cursor, such as Attack, Trade, Mine, Loot, Walk here, and
  Examine, and a left click runs the top option. NPC services and examine texts
  come from actor content through the actor spawn.
- Deterministic NPC, Mob, and resource node definitions, factions, presentation
  references, activity, respawn, and resource profiles, spawn points, groups,
  areas, and patrol paths with one shared command-line and Unity Editor compiler
  path.
- Bounded worker-runtime actors with fresh runtime and network identities,
  reliable interest presence, event-driven NPCs, and centrally bucketed Mob
  dormant or active scheduling.
- Server-authoritative click interaction using exact-session, runtime,
  Shard, target-revision, range, bounds, line-of-sight, capability, rate, and
  one-active-session validation.
- Server-authoritative target-based combat: a left click on a wolf or another
  player walks into weapon range and auto-attacks with the Training Shortbow.
  SimulationWorker owns range, line of sight, attack timing, damage, arrows in
  flight, the equipment-change lockout, health, regeneration, and respawn, and
  spends arrows in batches through GameService.
- One death event per death, Mob or player, with its killer. A player death runs
  the existing death partition into Recovery Storage and a durable corpse, and a
  Mob death rolls its loot profile into a live or durable corpse.
- Mob brains that idle or patrol, detect players on a shared spatial grid, chase,
  bite, and evade home past their leash, with moving Mobs in movement snapshots.

## Current Scale Boundary

The data model and contracts separate logical topology from process runtime, but
the current executable deliberately uses one active SimulationWorker per shard.
The database enforces one active assignment per worker and one active worker per
shard. This is the safe base unit before a shard is split spatially.

When required, the next scale step is to add zones as authoritative spatial
partitions and layers as controlled population copies inside a shard. That work
will extend SimulationAssignment and placement. It must not redefine World or
introduce isolated realms.

## World Actors, NPCs, Mobs, And Interaction

One scalable WorldData, SimulationWorker, GameProtocol, and Unity foundation
serves every world actor:

- `NPC` means a social or service actor assembled from capabilities such as
  dialogue, vendor, quest, crafting, insurance, trainer, bank, or Recovery
  Storage.
- `Mob` means a combat actor with AI, aggro, combat, loot, corpse, and respawn
  behavior.
- A resource node, such as the Iron Vein, is a gatherable actor with exactly one
  gathering capability and a resource profile. Its charges, depletion, and
  respawn are live Shard state in SimulationWorker.
- Faction and disposition own friendly or hostile state. Actor kind does not.
- City NPCs, including guards, and resource nodes are invulnerable in the first
  version.
- Normal actors are reconstructed from compiled WorldData after worker restart
  instead of receiving one PostgreSQL row per instance.
- NPCs are event-driven. Mobs use centrally scheduled dormant and active tiers
  without per-actor asynchronous loops.
- Players choose an actor's option in the world menu and walk into range before
  the client asks to open it.
  SimulationWorker validates exact session, target, Shard, revision, range, line of sight, active
  state, and capability before opening one interaction session.
- One player may hold one active interaction, including a corpse view, while
  many players may interact with the same target.
- Unity prefabs remain presentation-only. Visual authoring exports canonical
  actor and spawn content back to WorldData. Content owns Mob corpse lifetime
  and live or durable persistence over the existing corpse client path. Load
  coverage exercises the central actor scheduler, dormant and active
  transitions, and independent interaction leases.

The permanent Editor workflows live at
`MMORPG > Tools > Content > Actor Studio` and
`MMORPG > Tools > Content > Spawn Authoring`. The vendor capability trades
through GameService, as decided in
[Vendor And Gathering Plan](VENDOR_AND_GATHERING_PLAN.md). Combat, Mob brains,
and Mob loot rolls are implemented as recorded in the
[Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md).
Quest progression and crafting are not implemented yet. See
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).

## Item Foundation Status

The item, inventory, death loot, and corpse foundation is implemented. The
delivered phases are listed in
[Items And Inventory Delivery History](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md),
and the rules in [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).

- **Catalog.** Neutral content compiled by one shared compiler, a Unity
  authoring and bake window, a transactional PostgreSQL mirror with retirement
  and compatibility fences, and a test-owned fixture catalog.
- **Custody.** Every active character has a permanent inventory, a Bank, a
  Secure Container, and Recovery Storage. One transaction kernel commits grants,
  relocation, equipment with stacks and blocked slots, stack changes, swaps,
  consumption, destruction, Bag swaps, Recovery deliveries and claims, Secure
  Container tiers, and policies with idempotency and audit.
- **Carry weight.** The committed weight and capacity reach SimulationWorker at
  admission and heartbeat, and the shared GameSimulation rules slow movement in
  both authority and Unity prediction.
- **In-world access.** Item intents run through SimulationWorker with exact
  session fencing. The Bank and Recovery Storage need an open interaction with an
  NPC that offers them; insurance, quest items, and vendor trade use their NPC
  capabilities.
- **Economy and gathering.** Vendor purchases, vendor sales, and gathered
  resources commit through the same transaction kernel. Purchases and gathered
  ore share one carried acquisition rule that tops up stacks before it fills
  free carried slots, and the wallet debits or credits currency in the same
  transaction.
- **Death and corpses.** One transaction partitions a death into Recovery
  deliveries and a durable five-minute player corpse. Corpses are looted, filled,
  and rearranged concurrently, Bags swap atomically, and live or durable Mob
  corpses grant loot with retry-safe ids.
- **Unity.** A persistent catalog, inventory, and corpse state with revision
  coherence and operation journaling sits under a temporary uGUI inventory.
- **Operations.** Statement, lock, and payload bounds, low-cardinality metrics,
  retention cleanup, and load scenarios cover the kernel.

Still to come on this foundation:

- Final inventory visual design, interaction polish, and accessibility.
- Consume and use intents and final corpse art.
- Quest progression and rewards, further vendors and resource types, and
  gathering progression.

## Intentionally Deferred

- Zone ownership, cross-zone handoff, and layer orchestration.
- Replicated dynamic collision transforms and general rigid-body simulation.
- Combat beyond the first slice: abilities, further weapons, the combat
  triangle, magic and mana, threat, and Mob abilities beyond one attack.
- Final combat and corpse presentation. Unity shows both with temporary UI.
- Crafting, professions, gathering progression, and the broader economy beyond
  the first NPC vendor.
- Durable unique actor persistence and the remaining NPC capability behavior
  remain deferred beyond the implemented actor, interaction, and combat
  runtime. Complete quests, guilds, social systems, and
  world events also remain later work.
- Production orchestration and live operations. Metrics, logs, traces,
  dashboards, and alerts exist for local and staged use, see
  [Monitoring](MONITORING.md).

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
  the inventory and death-loot rules.
- [Vendor And Gathering Plan](VENDOR_AND_GATHERING_PLAN.md) records the vendor,
  currency, and gathering decisions and how the slice was implemented.
- [Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md) records the combat
  and Mob decisions, how the slice was implemented, and its acceptance run.
- [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md) defines the approved
  world-actor, NPC, Mob, spawn-authoring, and interaction foundation.
- [Items And Inventory Delivery History](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md)
  records how that foundation was delivered.

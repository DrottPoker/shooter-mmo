# NPC And Mob System Design

Last updated: 2026-10-26

Status: Canonical design, foundation implemented

## Purpose

This document is the canonical product and architecture design for scalable
world actors, NPCs, Mobs, actor spawning, and player interaction in MMORPG.
It locks the foundation needed before vendor, quest, crafting, insurance, combat,
and Mob-loot features are connected.

The inventory, item-custody, death-partition, and corpse-container rules remain
owned by
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md). The
project-wide topology and service boundaries remain owned by
[Project Architecture](PROJECT_ARCHITECTURE.md). The foundation was delivered as
Phase 12 in
[Items And Inventory Delivery History](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

The foundation is a permanent content, runtime, protocol, client-state, and
tooling layer. Its Unity presentation is temporary. It does not implement
quest progression, crafting recipes, final actor art, or final interaction
UI. The vendor and gathering slice in
[Vendor And Gathering Plan](VENDOR_AND_GATHERING_PLAN.md) builds the vendor
capability and resource nodes on it, and the combat slice in
[Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md) builds Mob damage,
death, loot, and respawn on it.

## Implementation Status

The implemented foundation includes:

- Strict shared actor definitions in `content/actors`, with one file per actor,
  per-World spawn authoring in `content/worlds/<WorldId>/actor-spawns.json`, and
  deterministic runtime actor content in
  `src/Shared/WorldData/Generated/Worlds/<WorldId>/world-actors.json`.
- One framework-neutral compiler and runtime validator used by the command-line
  tool, Unity Editor tools, SimulationWorker startup, and automated tests.
- A shared six-definition actor catalog, the `development-world-1` stress
  arena with 14 deterministic instances, including nine Iron Veins and four
  Feral Wolves, and the `development-world-2` test World with 21 deterministic
  instances, including one Feral Alpha, five Feral Wolves, seven Iron Veins, and
  four Oak Trees.
- Mob combat, brain, locomotion, and loot profiles in actor format 2, validated
  by the compiler and, for loot, against the compiled item catalog.
  SimulationWorker uses the combat and loot profiles for Mob health, death,
  and loot, as described in [Mob Death And Respawn](#mob-death-and-respawn),
  and the brain and locomotion profiles for the Mob brains in
  [Service Features](SERVICE_FEATURES.md#mob-brains).
- The `resource_node` actor kind, the `gathering` capability kind, the
  capability `profileId`, and resource profiles in actor format 3, and the
  neutral `world` faction for resource nodes. The compiler validates vendor
  profile ids, resource profile tool kinds, and yields against the compiled item
  catalog.
- Collision bounds in actor format 5: a resource node blocks character movement
  with a box that the World collision compilers bake into the World's collision
  chunks, as described in
  [Collision Bounds](../content/README.md#collision-bounds).
- Reliable interest-based actor presence, bounded actor state, and correlated
  interaction messages in the versioned realtime protocol.
- A bounded worker-runtime actor store, fresh runtime and network identities,
  restart reconstruction, event-driven NPCs and resource nodes, central Mob
  schedule buckets, and the central `WorldActorTimerQueue` for delayed actor
  work such as resource node and Mob respawn.
- A typed capability-handler registry. Insurance, quest offer, quest
  completion, Character Bank, Recovery Storage, vendor, and gathering have
  handlers; every other kind has an explicit deferred server handler until its
  business system exists. The client never turns that deferred result into
  success.
- Live shared resource node charges with depletion and respawn, and a central
  gathering loop, described in
  [Resource Nodes And Gathering](#resource-nodes-and-gathering).
- Exact-session, exact-runtime, assignment, Shard, bounds, range, line-of-sight,
  target-revision, capability-revision, rate, and one-active-lease authority.
- Permanent Unity actor, interaction, targeting, operation, revision, and
  reconnect state beneath presentation-only actor views and temporary uGUI.
- Shared target selection and lease ownership with the existing authoritative
  corpse view and mutation flow.

Insurance, quest item lifecycle, and vendor purchase and sale actions reuse the
same validated interaction session and dispatch contract before crossing the
service-authenticated GameService boundary. A vendor trade carries the vendor
profile of the validated capability as its vendor access, never a client value.
Quest progression and completion remain unavailable. Opening the vendor only
confirms it, so the client can show the offers from its bundled item catalog.
The Character Bank and Recovery Storage handlers only confirm
the capability: item operations gain Bank or Recovery access while the player
holds an open interaction with an actor that offers them, as described in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).

Mob content selects live or durable corpse custody and an exact lifetime. The
normal wolf uses `live` and `120` seconds. The selected alpha boss uses
`durable` and `600` seconds. Deterministic death-event grant ids protect
successful live loot claims from duplication, while durable bosses reuse player
corpse restoration and expiry. Combat damages and kills Mobs, rolls their loot,
and respawns them, and Mob brains detect, chase, bite, and evade home.

Load coverage validates this foundation at operational scale without changing
its ownership. Automated load coverage schedules `2,000` event-driven NPCs and
`2,000` centrally scheduled active or dormant Mobs, then opens `128` concurrent
NPC interactions through independent player leases. The existing
`Mmorpg.SimulationWorker.WorldActors` meter reports identity-free NPC, Mob,
resource node, event-driven, dormant, active, interaction, depleted node, and
gathering measurements. No actor receives an independent background task or
durable row.

## Canonical Terminology

| Term | Meaning |
| --- | --- |
| World actor | A server-owned runtime entity created from shared World content and represented to interested clients |
| NPC | A social or service-oriented world actor whose behavior is assembled from capabilities such as dialogue, vendor, quest, crafting, insurance, trainer, bank, or Recovery Storage access |
| Mob | A combat-oriented world actor with combat, aggro, AI, death, loot, and respawn contracts |
| Resource node | An invulnerable interactable world object, such as an Iron Vein, with exactly one gathering capability and live shared charges |
| Capability | A composable interaction role attached to an NPC or resource node definition without requiring a new actor class |
| Resource profile | Actor catalog content that a gathering capability names: required tool kind, yield, cycle time, charges, and respawn delay |
| Actor definition id | A globally stable content id for one NPC, Mob, or resource node archetype |
| Spawn definition id | A globally stable content id for one spawn point, spawn group, spawn area, or patrol assignment |
| Runtime actor id | A worker-runtime-scoped identity for one live actor instance |
| Network entity id | The current realtime identity used for interest management and protocol messages |
| Presentation archetype id | A content reference resolved by Unity to a presentation-only prefab |
| Interaction bounds | Server-owned bounds used to measure distance and select a line-of-sight target point |
| Collision bounds | The box that blocks character movement at a resource node, baked into the World collision; zero for NPCs and Mobs |
| Interaction session | One server-owned, player-to-target session opened after authoritative validation |
| Faction | Content identity used by disposition, reputation, and future combat rules |
| Disposition | The current relationship of an actor toward a player, independent from NPC or Mob kind |

`NPC` and `Mob` are not interchangeable. An NPC is not a generic name for every
non-player character. A Mob is the canonical name for a combat enemy such as a
Feral Wolf. A vendor, quest giver, crafter, trainer, banker, or city guard is an
NPC. A resource node is neither: it is a world object that players gather from,
and it reuses the actor content, spawn, identity, interest, interaction, and
lease foundation unchanged.

Actor kind does not encode friendly or hostile state. Faction and disposition
own that decision. A city guard remains an NPC even if a future criminal system
makes the guard hostile toward a specific player.

All city NPCs, including guards, are invulnerable in the first implementation.
The foundation delivers only that invulnerable NPC damage policy, so the actor
compiler rejects a damageable NPC definition. Later combat design may add an
explicit non-city NPC damage policy without changing actor identity or
capability composition.

## Locked Design Invariants

1. WorldData is the source of truth for actor and spawn content.
2. Unity scenes and prefabs are authoring and presentation surfaces, never a
   second gameplay authority.
3. NPC roles are freely composable capabilities. A vendor can also be a quest
   giver, quest turn-in target, crafter, trainer, or another supported service.
4. Adding a new combination of existing capabilities requires content, not a
   new runtime actor subclass.
5. SimulationWorker owns live actor state, interaction authority, interest, and
   runtime identity for its assigned Shard.
6. GameService remains the authority for global durable mutations invoked by a
   capability, including items, currency, quests, policies, and durable
   progression.
7. Unity sends advisory target and action intents. It never proves range, line
   of sight, capability, faction, actor state, or authorization.
8. A player may have only one active world interaction session, including an
   existing corpse view. Many players may interact with the same target
   concurrently.
9. Normal NPCs, Mobs, and resource nodes do not receive one PostgreSQL row per
   runtime instance.
10. Normal actor state is reconstructed from compiled WorldData after a worker
    restart. Durable unique actors remain an explicit future opt-in.
11. NPCs and resource nodes are event-driven. Mobs use centrally scheduled
    active and dormant simulation tiers. No actor owns an asynchronous loop,
    thread, or timer; delayed actor work goes through the central
    `WorldActorTimerQueue`.
12. Stable content and spawn ids never double as worker-runtime or network ids.
13. Actor replication reuses the existing entity registry and spatial interest
    management instead of creating a parallel visibility system.
14. Capability state is requested after an interaction opens. It is not copied
    into every actor spawn or movement packet.
15. Interaction range and line of sight are revalidated for every operation,
    not only when the panel first opens.

## Content Ownership And Compilation

Neutral actor authoring and its compiled form live here:

```text
content/
  actors/
    catalog.json                  factions, presentation, and every actor profile
    definitions/<actor-id>.json   one actor definition per file
  worlds/<WorldId>/
    actor-spawns.json             spawn points, groups, areas, and patrol paths
src/Shared/WorldData/Generated/
  Worlds/<WorldId>/
    world-actors.json             compiled runtime content for one World
```

A definition file is named after its actor id. The compiled runtime result is
deterministic, framework-neutral, and independent from a Unity scene or asset
database. Every World's runtime embeds the shared catalog, so an actor change
recompiles every World. The general content rules are in `content/README.md`.

The same compiler and validation rules must be consumed by:

- Unity Editor authoring tools.
- A command-line compiler and verifier.
- SimulationWorker startup.
- Backend unit and content tests.
- Continuous integration.

The command-line entry point is `tools/WorldActorCompiler`. By default it
compiles the repository actor catalog against every World below
`content/worlds` and fails when a World lacks `world.json`, `actor-spawns.json`,
or `collision.json`. `--content-root` and `--generated-root` point it at other
directories for isolated tests and for Unity Editor validation. `--verify`
compiles in memory and fails when committed runtime output is missing, differs,
or exists for a World that no longer exists, without rewriting anything.

Compilation produces a complete actor-content revision and stable structural
fingerprints. Equivalent canonical input produces byte-for-byte equivalent
runtime output. Invalid references, duplicate ids, invalid bounds, unsupported
capability combinations, and non-finite transforms fail compilation.

An assigned SimulationWorker loads only a fully validated compiled revision.
Startup or assignment activation fails clearly when the required revision is
missing, corrupt, or unsupported. Runtime code does not silently repair authoring
errors.

## Actor Definitions

Every actor definition contains at least:

- Stable actor definition id.
- Actor kind: `npc`, `mob`, or `resource_node`.
- Display name or localization key.
- Examine text: one trimmed line of up to 200 characters that the right-click
  menu's Examine shows.
- Presentation archetype id.
- Faction id.
- Interaction bounds and targeting anchor.
- Capability descriptors for an NPC or resource node. Mobs have no
  capabilities.
- Mob activity, respawn, combat, brain, locomotion, and loot-profile
  references for a Mob. NPCs leave every Mob profile reference empty.
- Explicit lifecycle and respawn profile references.
- Content tags used by authoring, queries, and future rule systems.

Actor definitions contain gameplay references and validation data, not Unity
materials, components, scene object references, or authoritative MonoBehaviour
state.

The first NPC definition contract uses an invulnerable damage policy. Every Mob
references a combat, brain, and locomotion profile in `content/actors/catalog.json`
and may reference a loot profile:

| Profile | Fields | Rule |
| --- | --- | --- |
| Combat | maximum health, attack style, attack damage type, damage roll, attack interval, attack range | The styles and damage types are the closed sets of [Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md) |
| Brain | aggression (`aggressive` or `defensive`), detection range, leash range, give-up time | An aggressive brain needs a detection range, and the leash range exceeds it |
| Locomotion | walk speed, patrol speed, collision radius and height | The patrol speed is above 0 and at most the walk speed, and the capsule height exceeds twice its radius |
| Loot | ordered entries of item definition, quantity range, and chance in basis points | Every entry names a non-quest item definition whose stack holds the largest quantity |

A Mob without a loot profile drops nothing. Loot rolls follow the authored entry
order, which also orders the corpse slots.

A resource node is invulnerable, uses the neutral `world` faction, and has no
activity, respawn, corpse, or Mob profile references. It has exactly one
capability, of kind `gathering`, whose `profileId` names a resource profile in
`content/actors/catalog.json`:

| Field | Rule |
| --- | --- |
| `requiredToolKind` | An item catalog tool kind that at least one definition provides |
| `yieldDefinitionId`, `yieldQuantity` | An existing item definition that is not a quest item or a Bag and has no default policies, with a quantity that fits one stack |
| `cycleSeconds` | Seconds per gathering cycle, `0.5` to `60` |
| `charges` | Yields before the node depletes, `1` to `1000` |
| `respawnSeconds` | Seconds a depleted node stays depleted, `1` to `86400` |

`WorldActorItemReferences.Validate` checks these references, Mob loot, and
vendor capability profile ids against the compiled item catalog, so
`tools/WorldActorCompiler` runs after the item catalog compiler. The shipped
`resource.iron_vein` profile requires a `pickaxe`, yields one
`material.iron_ore` every 3 seconds, has 5 charges, and respawns after 30
seconds. The `resource.oak_tree` profile requires a `woodcutting_axe` and yields
one `material.oak_log` at the same pace, charges, and respawn.

## NPC Capability Composition

Capabilities are typed content descriptors resolved through a server-side
registry. Initial capability kinds are reserved for:

- Dialogue.
- Vendor.
- Quest offer.
- Quest turn-in.
- Crafting.
- Insurance.
- Trainer.
- Bank.
- Recovery Storage.
- Gathering, which only a resource node may have.

An NPC may contain any valid combination of the NPC kinds. The compiled
definition keeps the authored capability order, which is the order of the
right-click menu, so the first capability is the left-click action; the order
never changes authority. Each capability has a stable capability id, kind,
display name, `menuOption`, `profileId`, availability metadata, and revisioned
server state when required. `menuOption` is the verb the menu shows before the
actor's name, one trimmed line of up to 32 characters, such as `Talk-to`,
`Trade`, or `Mine`.
A `vendor` capability's `profileId` names a vendor profile of the item catalog,
such as `vendor.city_general_store` on Mira the Quartermaster's Trade
capability, a `gathering` capability's names a resource profile, and every other
kind leaves it empty. The opened capability summary carries it, so the client
renders the vendor's offers from its bundled item catalog without a listing
message.

The opened capability summary is player-specific. SimulationWorker filters it
through current disposition, actor state, capability state, and future
progression or access rules. A summary returned to one player must never be
reused as authority for another player. Static capability identity is shared:
the actor spawn carries every capability's id, kind, and menu option, so the
client can offer the right-click menu before the interaction opens. Availability
and its expected revision remain session-scoped and arrive only with the opened
summary.

The foundation proves composition and discovery. The insurance, quest, and
vendor item-lifecycle handlers bridge the already validated interaction
session to GameService. The worker passes exact session, worker runtime, Shard,
interaction session, capability, operation, and revision identity, and for a
vendor trade the capability's vendor profile, offer, quantity, and the price the
client showed. GameService then commits the server-owned price, policy,
grant-lineage, or trade transaction. The handler does not repeat target, range,
line-of-sight, or capability discovery logic.

There must not be authoritative `VendorMonoBehaviour`,
`QuestGiverMonoBehaviour`, or equivalent prefab scripts. Presentation helpers
may render a capability icon or prompt, but the capability registry and
SimulationWorker validation own the live meaning.

## Spawn Definitions And Identity

World content supports these authoring concepts:

- Actor spawn point for one NPC, Mob, or resource node instance.
- Spawn group for a bounded set of instances, such as guards or a cluster of
  resource nodes.
- Spawn area for randomized positions inside an authored boundary.
- Patrol path with ordered points and optional waits.
- Respawn profile with content-owned timing and population limits.
- Optional tags and grouping keys for future events and rule areas.

Every spawn references one actor definition and one World. It owns a stable spawn
definition id, transform or area, optional patrol reference, activation settings,
and respawn settings. Spawn content never stores a network entity id.

When a SimulationWorker activates an assignment, it reconstructs normal actor
baseline state from the compiled spawn definitions. Each live instance receives
a fresh runtime actor id and network entity id scoped to that worker runtime.
A reconnecting client rebuilds its view from reliable interest messages. It does
not assume a runtime id survived a worker restart.

Normal restarts may reset ephemeral NPC interaction sessions, Mob health, aggro,
patrol progress, resource node charges, and respawn timers until a later design
explicitly makes one of those states durable. A restart restores every resource
node with full charges. A selected unique boss may later opt into durable state,
but this exception must not turn ordinary actor population into database rows.

## Runtime Revisions And Session Fencing

Actor state uses separate revision scopes:

- The compiled actor-content revision identifies one complete compatible
  WorldData result.
- A runtime actor-state revision advances monotonically within one worker
  runtime when replicated actor state changes.
- An interaction revision advances when target validity, interaction bounds,
  disposition, or capability availability changes in a way that can invalidate
  an open or requested action.
- A capability may expose a narrower capability-state revision for later
  service-specific optimistic concurrency.

Ordinary movement snapshots do not advance the interaction revision merely
because an actor changed position. Range and line of sight are checked from the
latest authoritative transform on every request. A worker restart creates fresh
runtime and network identities, so revisions from the old runtime are never
compared with the new actor instance.

An interaction session is fenced to the exact simulation session, worker
runtime, target runtime id, and interaction revision. It is transient and is
never restored after disconnect or worker restart.

## SimulationWorker Runtime Foundation

SimulationWorker owns these implemented responsibilities:

- `WorldActorStore` for bounded actor identity, assignment activation, spawn
  expansion, active state, restart reconstruction, and assignment-local
  despawn tombstones.
- `RealtimeSimulationService` integration for the shared network-entity id
  allocator and existing spatial-interest enter and exit lifecycle.
- `WorldInteractionAuthorityService` for target, range, line-of-sight, session,
  revision, capability, and assignment validation.
- `WorldActorCapabilityRegistry` for typed capability dispatch and explicit
  deferred handlers for capabilities without business authority.
- `WorldActorActivityScheduler` for central bounded dormant and active Mob
  buckets.
- `WorldActorTimerQueue`, one min-heap of actor timers ordered by due tick,
  which `WorldActorRuntimeDriver` drains every tick. Resource node respawn and
  Mob respawn use it.
- `GatheringHandler`, the central gathering pass of the realtime loop.

NPCs and resource nodes do not tick merely because they exist. They react to
interaction requests, content changes, or centrally scheduled events.
Stationary NPCs should impose no per-frame behavior work beyond existing
interest queries and replication when state changes.

Mobs use two initial scheduling tiers:

- Dormant: no nearby relevant player and no per-tick AI evaluation.
- Active: relevant players or combat state require centrally batched updates.

The Mob activity profile owns activation radius, larger deactivation radius,
active update cadence, and coarse dormant-check cadence within validated global
limits. The larger deactivation radius provides hysteresis. Content selects a
profile instead of embedding scheduler constants in a prefab or actor class.

The scheduler answers its range checks from one grid of player positions per
tick, and a Mob that fights, walks home, or died stays active until it is home
and idle again. The Mob brain is an explicit state machine with the states
`Idle`, `Patrol`, `Chase`, `Attack`, `Evade`, and `Dead`, described in
[Service Features](SERVICE_FEATURES.md#mob-brains). A respawning Mob is a new
actor, so `Respawning` is no brain state, and an `Investigate` state may follow
with sound or ability content.

The runtime must not create one `Task`, timer, thread, database session, or HTTP
poller per actor. Work is performed by bounded stores, spatial queries, event
queues, and central fixed-step or coarse-schedule batches.

## Resource Nodes And Gathering

SimulationWorker owns resource node state and gathering timing. GameService
owns the tool check and the item grant, because only GameService knows the
character's equipment, and it checks both in one transaction.

- **Node state.** Charges are live Shard state in `WorldActorStore`, shared by
  every player. Each node keeps its remaining charges, the charges reserved by
  cycles whose grant is in flight, and a generation that advances on every
  depletion, so a stale completion or respawn never touches a newer cycle. A
  worker restart restores every node with full charges.
- **Start.** The client opens the node and sends `CapabilityAction`
  `StartGathering`. The usual interaction authority validates the lease, range,
  line of sight, and capability revision, and the gathering capability handler
  rejects a depleted node with `resource_node_depleted`. The realtime loop then
  registers one gathering activity per player and sends `GatheringState`
  `Started` with the cycle time.
- **Cycle.** The gathering pass runs every tick after the lease revalidation. A
  due cycle reserves one charge and queues a `gather_resource` request to
  GameService behind the player's other authority work, with the yield and
  required tool kind of the node's resource profile. When every remaining
  charge is reserved by other gatherers, the cycle waits for the next tick.
- **Yield.** A committed grant consumes the charge, applies the committed carry
  state, and sends `GatheringState` `Yielded` with the yield and the committed
  item-state revision. The next cycle starts one cycle time later.
- **Depletion and respawn.** The last charge makes the node inactive, which
  advances its revisions, so its interaction leases close at their next
  revalidation, and replicates `IsActive = false` to every viewer. Everyone
  gathering there stops with `resource_node_depleted`. The respawn is scheduled
  in `WorldActorTimerQueue`
  and restores the charges and the active state; no node owns a task or timer.
- **Stop.** Moving more than 0.1 m from where gathering started stops it with
  `gathering_interrupted`, and a closed interaction with
  `world_interaction_closed`. A rejected grant releases the charge and stops
  with the rejection code, such as `gathering_tool_required` or
  `inventory_full`. An uncertain GameService outcome is retried up to three
  times with the same operation id, so a lost answer never grants twice. A
  rejection that ends the item session disconnects the player.
- **Bots.** Synthetic development sessions cannot gather.

## Interaction Targeting And Input

The player chooses an interaction in the right-click world menu, or with a left
click on its top option. The menu lists every capability of every target under
the pointer, nearest first. Choosing one walks the player into range, asks the
worker to open the interaction, and then starts that capability: a vendor,
bank, Recovery Storage, quest turn-in, crafting, or trainer capability runs at
once, gathering starts, and dialogue, insurance, and quest offers leave the
interaction panel open for their choices. Escape closes it. Target selection
is advisory because a client cannot be trusted as a security boundary.

One reusable client target system serves:

- NPCs.
- Corpses.
- Resource nodes.
- Crafting stations.
- Loot containers.
- Doors.
- Future interactable world objects.

Corpse target selection runs on this shared client foundation,
and makes the existing corpse view consume the same one-active-interaction
lease. It does not replace the authoritative corpse view or transaction
protocol.

The initial shared interaction values are:

| Rule | Value |
| --- | --- |
| Client pointer range from the camera | `100` metres |
| Client approach distance | `2.5` metres |
| Client open distance | `2.75` metres |
| Authoritative start range | `3.0` metres |
| Authoritative maintain range | `3.5` metres |
| Client spherecast tolerance | `0.15` metres |

The client checks a ray through the pointer first and may use a small spherecast
to make thin or animated presentation targets practical. It lists only
registered interactable views that no solid geometry hides, nearest first, and
shows the left-click option next to the pointer. An option walks the player to
the approach distance and sends the open intent once the authoritative owner
position is within the open distance, inside the start range. The pointer range
does not grant authority to open an interaction.

The start range is the player's base interaction range. The maintain range adds
small hysteresis so movement and reconciliation near the boundary do not flicker
an already open panel. The client uses these shared values instead of per-prefab
constants. Future player progression or capability-specific range changes must
still resolve to server-owned rules.

## Authoritative Interaction Validation

SimulationWorker calculates distance from the authoritative player root to the
closest point on the target's server-owned interaction bounds. It traces line
of sight from a server-defined interaction origin on the authoritative player
capsule to the target anchor or closest valid bounds point. It uses WorldData
and dynamic collision queries. Client camera position, Unity colliders, render
bounds, and reported hit points are never trusted by the server.

Opening an interaction validates all of the following:

1. The connection owns an active exact simulation session.
2. The target runtime id exists in the current worker runtime.
3. The target is in the same Shard and currently valid for interaction.
4. The target kind and expected interaction revision match.
5. The target is inside the `3.0` metre start range.
6. Authoritative collision permits line of sight.
7. The requested action is supported by the target.
8. The player has no other active interaction session.
9. Rate and payload limits permit the request.

A successful open creates one interaction session bound to player, simulation
session, worker runtime, target runtime id, and target interaction revision. The
response contains only the current authoritative capability summary needed by
the panel. The summary is filtered for that player and carries its relevant
revision. Spawn packets do not carry the full summary.

Every later capability operation validates the interaction session again,
including target state, capability availability, line of sight, and the `3.5`
metre maintain range. A session closes when the client closes it, the target
despawns, the target revision invalidates the session, range or line of sight
fails, the player disconnects, or the worker assignment changes.

The client closes an old session before opening a different target. Overlapping
open requests are rejected with a stable active-interaction error. Multiple
players may independently hold sessions against the same NPC.

## Realtime Protocol Contract

Actor and interaction messages are part of the versioned realtime protocol. A
change increments the protocol version once and updates .NET, Unity, handshake,
tests, and diagnostics together. An old or partially updated
client receives the existing explicit protocol-mismatch path instead of
misreading a packet.

The protocol separates actor visibility from interaction state:

- Reliable actor spawn and despawn identity. Since protocol version 20 the
  spawn also carries the examine text and every capability's id, kind, and menu
  option in authored order, but no availability, revision, or profile.
- Bounded actor state updates only when replicated state changes. A resource
  node's available or depleted state reuses `IsActive`.
- World interaction open, close, and capability-action intents. The
  capability actions include `VendorPurchase` and `VendorSale` with offer,
  quantity, and expected price, and `StartGathering`.
- Opened state with interaction session identity and capability summary,
  including each capability's `profileId`.
- Operation result correlated by operation id.
- Stable server-initiated interaction closure.
- `GatheringState`, a reliable server message with kind `Started`, `Yielded`,
  or `Stopped` for the player's own gathering.

An open intent contains at least:

- Operation id.
- Target network entity id.
- Target kind.
- Expected interaction revision.
- Requested action.

Subsequent capability intents also contain the interaction session id,
capability id, capability kind, and expected relevant revision. Strings, lists,
and payloads are bounded. Packet encoders must measure encoded size and retain
the existing transport limit. Large future capability state must use explicit
chunking or a focused service read rather than an oversized actor packet.

The server never accepts client-supplied distance, hit point, bounds, line of
sight, faction outcome, capability availability, or actor authority.

## Unity Runtime And Presentation

The Unity foundation separates permanent state from replaceable presentation:

- `WorldActorClientController` owns immutable actor presence and monotonic state.
- `WorldInteractionClientController` owns target observation, operation
  correlation, the active authoritative interaction session, closure, and
  reconnect cleanup.
- `WorldMenuController` collects the right-click world menu for the registered
  presentation targets under the pointer from its providers, runs the top
  option on a left click, and offers Walk here and Examine itself.
- `WorldInteractionTargetingController` adds the capability and Loot options,
  approaches a chosen target, and starts the chosen capability once the
  interaction opens.
- `WorldActorView` is the presentation base for actor identity and target bounds.
- `NpcView`, `MobView`, and `ResourceNodeView` provide kind-specific
  presentation hooks without owning authority. `ResourceNodeView` keeps a
  depleted node visible with its depleted model, and the node cannot be targeted
  until it respawns.
- A presentation registry resolves `presentationArchetypeId` to an authored
  prefab.
- `TemporaryWorldInteractionPanel` is a replaceable uGUI surface over the
  permanent controller and capability model.

The presentation registry fails visibly for a missing archetype and may use one
explicit development fallback. It never changes actor kind or capability data.
Runtime prefabs are instantiated only from authoritative actor presence inside
the existing interest system.

The interaction panel displays the target name and available actions such as
Talk, Trade, Quests, Crafting, Insurance, Bank, Recovery, or Mine. The available list
comes from the opened server response. Selecting an action sends an intent to a
registered capability adapter. The client may present capabilities whose business
handler is intentionally deferred, but it must label that state clearly and
must not fake a successful transaction.

Pointer targeting remains advisory. Target highlighting, menu colors, and the
left-click hint are presentation only. The cursor is always free, so opening or
closing the panel or the menu never changes camera or pointer behavior.

## Unity Editor Authoring UX

All project-specific Editor tools remain below `MMORPG > Tools`.

The Editor tools are:

- `MMORPG > Tools > Content > Actor Studio`.
- `MMORPG > Tools > Content > Spawn Authoring`.

Actor Studio supports:

- Search and filtering by actor kind, faction, capability, tag, and problems.
- Creation from NPC, Mob, or resource node templates.
- Safe duplication with a new stable id.
- Multi-selection: kind, faction, disposition, presentation, tags, Mob
  lifecycle settings, and interaction and collision bounds edit every selected
  actor at once. Collision bounds are editable for resource nodes only.
- Every edit is one step in Unity's Undo, and unsaved edits survive script
  reloads.
- Capability add, remove, and reorder.
- Live validation with the shared compiler, marked on the actor and field it
  concerns.
- Renaming an actor updates the spawns of the edited World. Actors that other
  Worlds place keep their id, and placed actors cannot be deleted.
- Dropdown selection for referenced profiles and presentation archetypes,
  including the capability profile: vendor profiles of the compiled item
  catalog for `vendor` and resource profiles for `gathering`. Resource profiles
  themselves are edited in `content/actors/catalog.json`.
- Interaction-bounds preview.
- Deterministic save, compile, verify, and change preview.
- Clear distinction between authored source, compiled output, and Unity
  presentation mapping.

Spawn Authoring supports:

- One list of every spawn point, group, area, and patrol path, with search and
  filtering by type, actor, and problems.
- Visual scene handles for spawn points, groups, areas, and patrol points.
  Clicking a marker in the Scene view selects its entry, and one handle moves a
  whole selection.
- Multi-selection: actor, patrol path, position, yaw, group layout, area
  settings, and Mob lifecycle settings edit every selected entry that has them.
  A vector edit changes only the edited axis of each entry.
- Every edit, including a whole handle drag, is one step in Unity's Undo, and
  unsaved edits survive script reloads.
- Ground snapping of the selection against authored scene collision.
- Actor prefab preview selected through the presentation archetype.
- Spawn-group and population-limit authoring.
- Respawn and activation-profile selection.
- Stable id generation and live validation with the shared compiler, marked on
  the entry and field it concerns. Renaming a patrol path updates the spawns
  that follow it, and a followed patrol path cannot be deleted.
- Change preview showing the exact canonical WorldData change.
- One-click save, compile, and verify.

Editor scene components are temporary authoring adapters. They store stable
content references and editable authoring values, but the saved neutral
WorldData is the source consumed by SimulationWorker. A scene without exported
WorldData does not create production spawns. A compiled actor or spawn must be
reconstructable without opening Unity.

## Faction, Disposition, And Invulnerability

Every actor references a faction. Disposition is evaluated by server rules and
may later include reputation, crime, quests, or events. Actor kind remains
unchanged when disposition changes.

The foundation keeps only enough faction data to preserve identity and expose a
server-owned friendly, neutral, or hostile disposition. It does not implement
the complete reputation or criminal system.

NPC damage policy is `invulnerable` in the first version. Unity may show feedback
for an invalid attack, but SimulationWorker owns the rejection. Guards use the
same NPC invulnerability rule. Resource nodes are invulnerable too and use the
neutral `world` faction. A damageable Mob with a combat profile is a combatant
of the worker's combat system.

## Mob Loot And Corpse Boundary

Mobs produce Mob corpses, not NPC corpses. Normal Mob corpses are live
SimulationWorker containers with an approximately two-minute default lifetime
and no restart guarantee. Selected bosses may opt into the durable corpse path.

The foundation establishes actor, spawn, runtime identity, and Mob lifecycle
references, and `MobCorpseLifecycleService` works over those identities. Its
input is an authoritative death event plus already resolved loot seeds. It
derives stable corpse, entry, grant, and create ids, then selects the live store
or the shared durable item transaction kernel from content.

Successful live loot materialization into player custody uses the deterministic
grant id as the item operation identity. Normal actor scalability is preserved:
there is still no durable per-instance Mob custody before death, and ordinary
unclaimed corpses never require PostgreSQL cleanup.

### Mob Death And Respawn

SimulationWorker's combat system, described in
[Service Features](SERVICE_FEATURES.md#target-based-combat), produces Mob
deaths. In Unity a Mob offers only Attack and Examine, never an interaction, as
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#combat-client)
describes:

- Every damageable Mob with a combat profile is a combatant with the profile's
  maximum health. A Mob lost in a worker restart returns with full health, like
  every other actor.
- At 0 health the Mob dies: it can no longer be targeted, every attacker's
  target on it clears, and its death event id is
  `ScopedGuid("mob-death", runtimeActorId)`. Each Mob life has its own runtime
  id, so the id is stable for retries and unique per death.
- The worker rolls the loot profile in entry order. Each entry drops with its
  chance in basis points and a quantity between its bounds. An empty roll
  leaves no corpse.
- `MobCorpseLifecycleService` creates the corpse with the rolled loot seeds. A
  durable corpse retries with the same death event id and a backoff from 0.5
  seconds that doubles up to 10 seconds until it commits. When GameService
  answers that the corpse can never be created, the Mob leaves without one.
- The actor despawns with the reason `died` once its corpse exists, or, without
  a corpse, at the end of the combat phase, after its death reached the
  viewers. Its respawn profile schedules the respawn on `WorldActorTimerQueue`.
  `WorldActorStore.Respawn` recreates the same compiled spawn instance with a
  fresh runtime id and network entity id, and interest spawns it for nearby
  players. Respawn timers reset on a worker restart.

## Stable Error Contract

These protocol error codes are stable:

```text
world_actor_not_found
world_actor_unavailable
world_interaction_invalid
world_interaction_active
world_interaction_out_of_range
world_interaction_line_of_sight_blocked
world_interaction_target_changed
world_interaction_capability_unavailable
world_interaction_session_invalid
```

`GatheringState` `Stopped` carries one of these stable codes, or the rejection
code of the GameService grant:

```text
resource_node_depleted
gathering_interrupted
world_interaction_closed
gathering_failed
```

Codes and status semantics are contracts. Display messages may be localized.
Authority or runtime fencing failures continue to use the existing simulation
session, worker, and protocol errors where appropriate.

## Security And Abuse Controls

- Interaction intents use the authenticated realtime connection and exact
  simulation-session authority.
- Operation ids are bounded and cannot create unbounded per-player history.
- Per-peer intent rate limits and one in-flight authority operation prevent
  request fanout.
- Actor, capability, and action enums reject unknown values.
- Content ids and display strings have explicit encoded-length limits.
- Range, line of sight, target state, and capability are evaluated on every
  request.
- Error responses do not reveal hidden actor or capability state outside player
  interest.
- Logs may include low-cardinality actor kind, capability kind, and error code,
  but never use account, character, session, actor, spawn, or entity ids as
  metric labels.

## Verification Contract

Automated coverage includes:

- Deterministic actor and spawn compilation.
- Duplicate ids, missing references, invalid bounds, invalid transforms, and
  illegal NPC damage policy.
- Free composition of multiple capabilities on one NPC without a new actor
  runtime class.
- Deterministic Editor import, export, bake, and verify behavior.
- Assignment activation and worker-restart reconstruction from WorldData.
- Stable separation of content, spawn, runtime actor, and network ids.
- Reliable actor interest spawn and despawn without capability payload bloat.
- Direct ray and spherecast target selection.
- Start range, maintain-range hysteresis, closest-point bounds distance, and
  line-of-sight validation.
- Same-Shard, live-target, expected-revision, session, and capability fencing.
- One active interaction per player and many players on one NPC.
- Mutual exclusion between an NPC interaction and the same player's corpse
  view, while many players may still view one corpse.
- Player-specific capability filtering and revision state cannot leak or be
  reused between two players on the same NPC.
- Revalidation and stable closure after movement, despawn, revision change,
  disconnect, or assignment change.
- Protocol round trips, malformed payload rejection, version mismatch, and MTU
  limits.
- Immutable Unity actor and interaction state with no optimistic authority.
- Presentation-prefab replacement without changing gameplay content.
- Corpse registration in the shared client targeting foundation without a
  corpse transaction rewrite.
- Dormant and active Mob scheduling seams without per-actor tasks.
- No normal NPC or Mob instance persistence row requirement.
- A `4,000` actor mixed-tier load and a `128` player NPC interaction hotspot
  without per-actor loops, shared-player lease leakage, or identity metric labels.
- Resource node compilation and item references, the vendor capability profile
  in the opened summary, vendor actions at the vendor only, resource node
  charges, reservation and release, gathering start, cycles, yields,
  rejections, uncertain retries, depletion that stops every gatherer, respawn
  through the timer queue, stops after movement or a closed interaction, full
  charges on assignment activation, and the depleted Unity presentation.

Implementation must also pass locked dependency restore, dependency policy,
formatter verification, the existing item-catalog and collision verification,
`tools/WorldActorCompiler --verify`, the complete Release build, every backend
test including isolated PostgreSQL integration tests, and all Unity EditMode and
PlayMode tests.

The manual acceptance gate must prove:

1. Create one NPC in Actor Studio with at least dialogue, vendor, quest, and
   crafting capabilities.
2. Place it visually with Spawn Authoring, ground-snap it, preview it, export the
   canonical change, and compile successfully.
3. Create and place one Mob definition through the same tools.
4. Restart SimulationWorker and verify both actors reconstruct from WorldData.
5. Join with Unity and left-click the NPC, or right-click it and choose one of its
   capabilities. The player walks into range before the interaction opens.
6. Verify the temporary uGUI lists the authoritative capability summary.
7. Try from outside start range and through blocking collision and observe the
   stable rejection without opening local state.
8. Open the same NPC from two clients and verify both sessions remain valid.
9. Move one player beyond `3.5` metres and verify only that player's interaction
   closes.
10. Restart or reconnect and verify actor presence returns from authoritative
    interest state without stale interaction state.

## Foundation Boundary

The foundation lets content creators create new NPC and Mob
definitions, compose existing NPC capabilities, place and verify spawns visually,
and exercise authoritative targeted interaction without adding a new runtime
actor class or a database row per actor.

The foundation explicitly stops before the following. The vendor and gathering
slice has since added vendor buying and selling, resource nodes, gathering, and
resource node respawn on these contracts.

- Final insurance, quest, and vendor UI.
- Quest progression and turn-in gameplay.
- Crafting recipes or production.
- Trainer or profession progression, including Mining experience.
- Aggro behavior, Mob attacks, or Mob AI. The combat slice has since added
  health, damage, combat-driven death events, Mob loot rolls, Mob respawn, and
  Mob brains.
- Durable unique boss state.
- Final NPC, Mob, prompt, dialogue, or interaction-panel art.
- Zone, Layer, multi-worker Shard, or world-event orchestration.

These later systems must extend the foundation contracts instead of bypassing
them with prefab-owned authority, endpoint-specific target checks, or parallel
actor registries.

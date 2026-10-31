# NPC And Mob System Design

This document is the canonical design of world actors: NPCs, Mobs, and resource
nodes, how they are authored, spawned, identified, scheduled, and replicated,
and how players interact with them. Item custody, death, and corpse containers
are in [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).
Combat, Mob behavior, and gathering as product rules are in
[Game Design](GAME_DESIGN.md). The project-wide topology and service boundaries
are in
[Project Architecture](PROJECT_ARCHITECTURE.md#world-actor-and-interaction-boundary).

## Status

Implemented:

- The actor content model with one compiler for the command line, the Unity
  Editor tools, SimulationWorker startup, and tests.
- NPCs, Mobs, and resource nodes with stable content ids, fresh runtime
  identities, reconstruction from content after a worker restart, and reliable
  interest-based presence.
- The authoritative interaction session with exact-session, target, revision,
  range, line-of-sight, capability, rate, and one-active-lease checks, shared
  with the corpse view.
- Capability handlers for insurance, quest offers, the Bank, Recovery Storage,
  the vendor, and gathering.
- Shared resource nodes with live charges, depletion, respawn, and collision
  bounds that block movement.
- Mob combat, brains, loot, live and durable corpses, and respawn.
- Central scheduling of NPCs and Mobs without per-actor tasks or durable rows.

Planned: the dialogue, crafting, trainer, and quest completion capabilities,
which answer with an explicit deferred or unavailable result today, quest
progression, threat tables, Mob abilities and groups, durable unique actor
state, and the final actor art and interaction UI; see
[Deferred Work](#deferred-work). The Unity presentation of actors and the
interaction panel is temporary. The content, runtime, protocol, client state,
and tooling are permanent foundations. The checked-in actor definitions are in
`content/actors`, and where the development Worlds place them is in
[Development Worlds](DEVELOPMENT_WORLDS.md).

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

All city NPCs, including guards, are invulnerable in the first implementation,
and so are resource nodes. The actor compiler rejects a damageable NPC or
resource node definition. Later combat design may add an explicit non-city NPC
damage policy without changing actor identity or capability composition.

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
recompiles every World. The content rules and the compile and verify commands
are in [Game Content](../content/README.md).

The same compiler and validation rules are consumed by the Unity Editor
authoring tools, the command-line compiler `tools/WorldActorCompiler`,
SimulationWorker startup, backend unit and content tests, and continuous
integration.

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
- Examine text: one short line that the world menu's Examine shows.
- Presentation archetype id.
- Faction id and disposition.
- Damage policy: `invulnerable` or `damageable`.
- Interaction bounds and targeting anchor, and collision bounds.
- Capability descriptors for an NPC or resource node. Mobs have no
  capabilities.
- For a Mob: its activity, respawn, combat, brain, locomotion, and loot profile
  references, and its corpse persistence and lifetime. NPCs and resource nodes
  leave every Mob field empty.
- Content tags used by authoring, queries, and future rule systems.

The text and bounds limits are in
[Examine Text And Menu Options](../content/README.md#examine-text-and-menu-options)
and [Collision Bounds](../content/README.md#collision-bounds).

Actor definitions contain gameplay references and validation data, not Unity
materials, components, scene object references, or authoritative MonoBehaviour
state.

### Mob Profiles

Every Mob references a combat, brain, and locomotion profile in
`content/actors/catalog.json` and may reference a loot profile:

| Profile | Fields | Rule |
| --- | --- | --- |
| Combat | maximum health, attack style, attack damage type, damage roll, attack interval, attack range | The styles and damage types are the closed sets of the [combat model](GAME_DESIGN.md#combat-model) |
| Brain | aggression (`aggressive` or `defensive`), detection range, leash range, give-up time | An aggressive brain needs a detection range, and the leash range exceeds it |
| Locomotion | walk speed, patrol speed, collision radius and height | The patrol speed is above 0 and at most the walk speed, and the capsule height exceeds twice its radius |
| Loot | ordered entries of item definition, quantity range, and chance in basis points | Every entry names a non-quest item definition whose stack holds the largest quantity |

A Mob without a loot profile drops nothing. Loot rolls follow the authored entry
order, which also orders the corpse slots.

### Resource Node Definitions

A resource node is invulnerable, uses the neutral `world` faction, and has no
activity, respawn, corpse, or Mob profile references. It has exactly one
capability, of kind `gathering`, whose `profileId` names a resource profile in
`content/actors/catalog.json`. A resource profile sets:

- The required tool kind, an item catalog tool kind that at least one item
  definition provides.
- The yield: an item definition that is not a quest item or a Bag and has no
  default policies, and its quantity per cycle, at most one stack.
- The cycle time.
- The charges a node holds before it depletes.
- The respawn delay after which a depleted node returns with full charges.

A resource node's collision bounds block character and Mob movement, while
sight, interaction line of sight, and projectiles pass them. A depleted node
keeps its footprint, such as a stump or rubble, so one box serves both states.
The allowed ranges and the shipped Iron Vein and Oak Tree profiles are in
[Resource Nodes And Item References](../content/README.md#resource-nodes-and-item-references).

The actor compiler checks Mob loot, vendor capability profile ids, and resource
profiles against the compiled item catalog, so it runs after the item catalog
compiler.

## NPC Capability Composition

Capabilities are typed content descriptors resolved through a server-side
registry. The capability kinds are:

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
world menu, so the first capability is the left-click action; the order never
changes authority. Each capability has a stable capability id, kind, display
name, menu option, profile id, availability metadata, and revisioned server
state when required. The menu option is the verb the menu shows before the
actor's name, such as `Talk-to`, `Trade`, or `Mine`. A `vendor` capability's
profile id names a vendor profile of the item catalog, a `gathering`
capability's names a resource profile, and every other kind leaves it empty.

The opened capability summary is player-specific. SimulationWorker filters it
through current disposition, actor state, capability state, and future
progression or access rules. A summary returned to one player must never be
reused as authority for another player. Static capability identity is shared:
the actor spawn carries every capability's id, kind, and menu option, so the
client can offer the world menu before the interaction opens. Availability and
its expected revision remain session-scoped and arrive only with the opened
summary.

Capability handlers bridge the validated interaction session to the business
authority behind it, without repeating target, range, line-of-sight, or
capability discovery logic:

- Insurance, quest offers, and vendor purchases and sales cross to GameService
  with the exact session, worker runtime, Shard, interaction session,
  capability, operation, and revision identity, and for a vendor trade the
  capability's vendor profile, offer, quantity, and the price the client
  showed. GameService then commits the server-owned price, policy, grant
  lineage, or trade transaction.
- Opening a vendor only confirms it, so the client shows the offers from its
  bundled item catalog without a listing message.
- The Bank and Recovery Storage handlers only confirm the capability. Item
  operations gain Bank or Recovery access while the player holds the open
  interaction, as
  [In-World Service Flow](INVENTORY_AND_DEATH_LOOT_DESIGN.md#in-world-service-flow)
  describes.
- Gathering starts a gathering activity, as
  [Resource Nodes And Gathering](#resource-nodes-and-gathering) describes.
- Quest turn-in answers that quest completion is unavailable until quest
  definitions exist.
- Dialogue, crafting, and trainer have an explicit deferred handler until their
  business systems exist. The client never turns a deferred result into
  success.

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
- Patrol path with ordered points and optional waits, for Mobs only.
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
because an actor changed position, and a Mob's rest pose advances only its
state revision. Range and line of sight are checked from the latest
authoritative transform on every request. A worker restart creates fresh
runtime and network identities, so revisions from the old runtime are never
compared with the new actor instance.

An interaction session is fenced to the exact simulation session, worker
runtime, target runtime id, and interaction revision. It is transient and is
never restored after disconnect or worker restart.

## Runtime Scheduling

NPCs and resource nodes do not tick merely because they exist. They react to
interaction requests, content changes, or centrally scheduled events.
Stationary NPCs impose no per-frame behavior work beyond existing interest
queries and replication when state changes.

Mobs use two scheduling tiers:

- Dormant: no nearby relevant player and no per-tick AI evaluation. A coarse
  check wakes the Mob when a player comes within its activation range.
- Active: relevant players or combat state require centrally batched updates.

The Mob activity profile owns the activation radius, a larger deactivation
radius, the active update cadence, and the coarse dormant-check cadence within
validated global limits. The larger deactivation radius provides hysteresis.
Content selects a profile instead of embedding scheduler constants in a prefab
or actor class. One grid of player positions per tick answers the range checks
and detection, so the cost does not grow with every Mob compared against every
player. A Mob that fights, walks home, or died stays active until it is home and
idle again.

The Mob brain is an explicit state machine with the states `Idle`, `Patrol`,
`Chase`, `Attack`, `Evade`, and `Dead`, described in
[Mob Brains](SERVICE_FEATURES.md#mob-brains), with its product rules in
[Mobs And Encounters](GAME_DESIGN.md#mobs-and-encounters). A respawning Mob is
a new actor, so `Respawning` is no brain state, and an `Investigate` state may
follow with sound or ability content.

Delayed actor work, such as the respawn of a depleted resource node or a dead
Mob, is a due-tick entry in the central `WorldActorTimerQueue`, which the
realtime loop drains every tick. The runtime must not create one `Task`, timer,
thread, database session, or HTTP poller per actor. Work is performed by bounded
stores, spatial queries, event queues, and central fixed-step or coarse-schedule
batches.

The design target is thousands of actors on one worker: about 2,000
event-driven NPCs and 2,000 centrally scheduled Mobs, with many players holding
independent interactions with the same NPC, and no actor with a background task
or a durable row.

### Runtime Components

In SimulationWorker, `WorldActorStore` holds bounded actor identity, assignment
activation, spawn expansion, active state, restart reconstruction, and
assignment-local despawn tombstones. Actors share the realtime loop's network
entity id allocator and its spatial interest enter and exit lifecycle.
`WorldInteractionAuthorityService` validates target, range, line of sight,
session, revision, capability, and assignment. `WorldActorCapabilityRegistry`
dispatches typed capabilities and holds the explicit deferred handlers.
`WorldActorActivityScheduler` keeps the central dormant and active Mob buckets.
`WorldActorRuntimeDriver` drains the `WorldActorTimerQueue` every tick,
`GatheringHandler` runs the central gathering pass, and
`MobCorpseLifecycleService` turns a Mob death into its corpse.

## Resource Nodes And Gathering

SimulationWorker owns resource node state and gathering timing. GameService
owns the tool check and the item grant, because only GameService knows the
character's committed equipment, and it checks both in one transaction. The
product rules of gathering are in [Gathering](GAME_DESIGN.md#gathering), and the
implemented cycle with its codes and limits in
[Resource Nodes And Gathering](SERVICE_FEATURES.md#resource-nodes-and-gathering).

- Charges are live Shard state, shared by every player. A worker restart
  restores every node with full charges.
- Gathering never starts on a depleted node or without an item of the node's
  tool kind in the Tool slot, and GameService checks the tool again for every
  yield, because it may have been taken off since the start.
- Each cycle reserves its charge before it asks for the grant, so concurrent
  gatherers never take more than the node holds, and a generation that advances
  on every depletion keeps a stale completion or respawn away from a newer
  cycle.
- The last charge depletes the node for every viewer, closes its interaction
  leases, and stops everyone gathering there. The central timer queue respawns
  it; no node owns a task or timer.
- Moving away or closing the interaction stops gathering, and an uncertain
  grant is retried with the same operation id, so a lost answer never grants
  twice.
- Synthetic development sessions cannot gather.

The messages that carry gathering are described in
[Capability Actions](REALTIME_PROTOCOL.md#capability-actions).

## Interaction Targeting And Input

The player chooses an interaction in the right-click world menu, or with a left
click on its top option. The menu lists every capability of every target under
the pointer, nearest first. Choosing one walks the player into range, asks the
worker to open the interaction, and then starts that capability: a vendor,
bank, Recovery Storage, quest turn-in, crafting, or trainer capability runs at
once, gathering starts, and dialogue, insurance, and quest offers leave the
interaction panel open for their choices. Escape closes it. Target selection
is advisory because a client cannot be trusted as a security boundary. The
menu's behavior is described in [World Menu](GAME_FEATURES.md#world-menu).

One reusable client target system serves:

- NPCs.
- Corpses.
- Resource nodes.
- Crafting stations.
- Loot containers.
- Doors.
- Future interactable world objects.

Corpse target selection runs on this shared client foundation and makes the
existing corpse view consume the same one-active-interaction lease. It does not
replace the authoritative corpse view or transaction protocol.

The shared interaction values are:

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

## Protocol Contract

Actor and interaction messages are part of the versioned realtime protocol, so
an old or partially updated client receives the explicit protocol-mismatch path
instead of misreading a packet. The messages are listed in
[Message Descriptors](REALTIME_PROTOCOL.md#message-descriptors), and the
versioning rules are in [Versioning](REALTIME_PROTOCOL.md#versioning). The
design rules are:

- Actor visibility is separate from interaction state. A reliable actor spawn
  carries the actor's identity, its examine text, and every capability's id,
  kind, and menu option in authored order, but no availability, revision, or
  profile.
- Actor state updates are bounded and sent only when replicated state changes.
  A resource node's available or depleted state is the actor's active state.
- Capability state, including every capability's profile id, arrives only with
  the opened, player-specific capability summary.
- An open request names an operation id, the target's network entity id and
  kind, the expected interaction revision, and the requested action. Every
  later capability action also names the interaction session, the capability
  id and kind, and the expected relevant revision. Results are correlated by
  operation id, and server-initiated closure uses stable codes.
- Strings, lists, and payloads are bounded. Packet encoders measure the encoded
  size and keep the transport limit. Large future capability state uses
  explicit chunking or a focused service read rather than an oversized actor
  packet.
- The server never accepts client-supplied distance, hit point, bounds, line of
  sight, faction outcome, capability availability, or actor authority.

## Client Presentation

The Unity client keeps permanent actor, interaction, targeting, operation,
revision, and reconnect state below presentation-only actor views and the
temporary interaction panel, as
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#world-actor-and-interaction-client)
describes. The design rules are:

- Actor views are presentation only. NPC, Mob, and resource node views add
  kind-specific presentation without owning authority. A depleted resource node
  stays visible with its depleted model and cannot be targeted until it
  respawns.
- A presentation registry resolves a presentation archetype id to an authored
  prefab. It fails visibly for a missing archetype and may use one explicit
  development fallback. It never changes actor kind or capability data.
  Runtime prefabs are instantiated only from authoritative actor presence inside
  the existing interest system.
- The interaction panel shows the target name and the available actions, such
  as Talk, Trade, Quests, Crafting, Insurance, Bank, Recovery, or Mine, from the
  opened server response. Selecting an action sends an intent to a registered
  capability adapter. The client may present capabilities whose business
  handler is intentionally deferred, but it must label that state clearly and
  must not fake a successful transaction.
- Pointer targeting remains advisory. Target highlighting, menu colors, and the
  left-click hint are presentation only. The cursor is always free, so opening
  or closing the panel or the menu never changes camera or pointer behavior.

## Authoring Tools

Actor Studio and Spawn Authoring in the Unity Editor create, duplicate, edit,
place, and validate actors and spawns, as
[Unity Content Tools](../content/README.md#unity-content-tools) describes. The
design rules are:

- Editor scene components are temporary authoring adapters. They store stable
  content references and editable authoring values, but the saved neutral
  WorldData is the source consumed by SimulationWorker. A scene without
  exported WorldData does not create production spawns, and a compiled actor or
  spawn must be reconstructable without opening Unity.
- The tools validate every change with the shared compiler, generate stable ids,
  and write deterministic canonical content, with a preview of the exact
  canonical change.
- Edits never break references. Renaming an actor updates the spawns of the
  edited World, actors that other Worlds place keep their id, and placed actors
  cannot be deleted. Renaming a patrol path updates the spawns that follow it,
  and a followed patrol path cannot be deleted.

## Faction, Disposition, And Invulnerability

Every actor references a faction and has a disposition. Disposition is evaluated
by server rules and may later include reputation, crime, quests, or events.
Actor kind remains unchanged when disposition changes.

The foundation keeps only enough faction data to preserve identity and expose a
server-owned friendly, neutral, or hostile disposition. It does not implement
the complete reputation or criminal system. A Mob's aggression, whether it
attacks the players it detects or fights only once damaged, comes from its brain
profile, not from its disposition.

NPC damage policy is `invulnerable` in the first version. Unity may show feedback
for an invalid attack, but SimulationWorker owns the rejection. Guards use the
same NPC invulnerability rule. Resource nodes are invulnerable too and use the
neutral `world` faction. A damageable Mob with a combat profile is a combatant
of the worker's combat system.

## Mob Loot And Corpse Boundary

Mobs produce Mob corpses, not NPC corpses. Normal Mob corpses are live
SimulationWorker containers with an approximately two-minute default lifetime
and no restart guarantee. Selected bosses opt into the durable corpse path.
Content selects live or durable custody and the exact lifetime for each Mob.
The corpse rules are in
[Mob Corpse Lifecycle](INVENTORY_AND_DEATH_LOOT_DESIGN.md#mob-corpse-lifecycle).

A Mob corpse is created from one authoritative death event and the loot already
rolled for it. Stable corpse, entry, grant, and create ids derive from that
event, and content selects the live store or the shared durable item
transaction kernel. A successful live loot claim uses its deterministic grant
id as the item operation identity, so a retry never duplicates it. Normal actor
scalability is preserved: there is still no durable per-instance Mob custody
before death, and ordinary unclaimed corpses never require PostgreSQL cleanup.

### Mob Death And Respawn

A Mob has no capabilities, so the world menu offers Attack and Examine for it
but never an interaction. The combat system produces its death, as
[Target-Based Combat](SERVICE_FEATURES.md#target-based-combat) describes.

- Every damageable Mob with a combat profile is a combatant. A Mob lost in a
  worker restart returns with full health, like every other actor.
- Each Mob life has its own runtime id, and its death event id derives from it,
  so the id is stable for retries and unique per death.
- The loot profile is rolled once, at the death. An empty roll leaves no
  corpse.
- The actor despawns once its corpse exists or its death has reached the
  viewers. Its respawn profile recreates the same compiled spawn instance with
  a fresh runtime id and network entity id through the central timer queue, and
  respawn timers reset on a worker restart.

## Stable Error Contract

Interaction and gathering failures use stable codes, listed in
[Error Codes](SERVICE_FEATURES.md#error-codes):

- World interaction codes cover a missing or unavailable target, an invalid or
  overlapping interaction, the range, line of sight, a changed target, an
  unavailable capability, and an invalid interaction session.
- A stopped gathering carries a code for a depleted node, an interrupted or
  closed gathering, or a failed grant, or the refusal code of the GameService
  grant.

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
- Synthetic development bot sessions never mutate durable state: they cannot
  trade, gather, or use an item lifecycle capability.
- Logs may include low-cardinality actor kind, capability kind, and error code,
  but never use account, character, session, actor, spawn, or entity ids as
  metric labels.

## Deferred Work

The foundation lets content creators define new NPCs, Mobs, and resource nodes,
compose existing NPC capabilities, place and verify spawns visually, and
exercise authoritative targeted interaction without adding a new runtime actor
class or a database row per actor. It stops before the following:

- Dialogue, crafting, and trainer business operations, quest progression and
  turn-in, and trainer and profession progression, including Mining and
  Woodcutting experience.
- Threat tables, Mob abilities beyond one attack, Mob groups that assist each
  other, and an `Investigate` brain state.
- Mob combat data for defense, elemental weaknesses, and movement impairment
  resistance.
- Durable unique actor state, such as a boss that survives a restart, and an
  opt-in for durable resource node state.
- An explicit damage policy for non-city NPCs.
- Final insurance, quest, vendor, and interaction UI, and final NPC models,
  prompts, and dialogue presentation.
- Zone, Layer, multi-worker Shard, and world-event orchestration.

These later systems must extend the foundation contracts instead of bypassing
them with prefab-owned authority, endpoint-specific target checks, or parallel
actor registries.

# NPC And Mob System Design

Last updated: 2026-07-18

Status: Canonical design, Phase 12 foundation implemented

## Purpose

This document is the canonical product and architecture design for scalable
world actors, NPCs, Mobs, actor spawning, and player interaction in Shooter MMO.
It locks the foundation needed before vendor, quest, crafting, insurance, combat,
and Mob-loot features are connected.

The inventory, item-custody, death-partition, and corpse-container rules remain
owned by
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md). The
project-wide topology and service boundaries remain owned by
[Project Architecture](PROJECT_ARCHITECTURE.md). The dependency-ordered delivery
gate is Phase 12 in
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

Phase 12 implements a permanent content, runtime, protocol, client-state, and
tooling foundation. Its Unity presentation is temporary. Phase 12 does not implement
vendor economy, quest progression, crafting recipes, combat damage, complete Mob
AI, Mob loot generation, final actor art, or final interaction UI.

## Phase 12 Implementation Status

The implemented foundation includes:

- Strict canonical authoring in `WorldData/Authoring/Actors` and
  `WorldData/Authoring/ActorSpawns`, plus deterministic runtime content in
  `WorldData/Runtime/Actors`.
- One framework-neutral compiler and runtime validator used by the command-line
  tool, Unity Editor tools, SimulationWorker startup, and automated tests.
- A checked-in `local-world-1` baseline with the Feral Wolf, City Guard, and
  Mira the Quartermaster definitions and five deterministic actor instances.
- Protocol version `12`, reliable interest-based actor presence, bounded actor
  state, and correlated interaction messages.
- A bounded worker-runtime actor store, fresh runtime and network identities,
  restart reconstruction, event-driven NPCs, and central Mob schedule buckets.
- A typed capability-handler registry. All Phase 12 capability kinds have an
  explicit deferred server handler until their later business phase replaces
  it. The client never turns that deferred result into success.
- Exact-session, exact-runtime, assignment, Shard, bounds, range, line-of-sight,
  target-revision, capability-revision, rate, and one-active-lease authority.
- Permanent Unity actor, interaction, targeting, operation, revision, and
  reconnect state beneath presentation-only actor views and temporary uGUI.
- Shared target selection and lease ownership with the existing authoritative
  corpse view and mutation flow.

Phase 13 replaces the insurance, quest-offer, and quest-turn-in deferred slots
with explicit handlers. Insurance and quest item lifecycle actions reuse the
same validated interaction session and dispatch contract before crossing the
service-authenticated AuthService boundary. Quest progression and completion
remain unavailable. No Phase 14 Mob combat, loot, or corpse behavior is part of
this implementation.

## Canonical Terminology

| Term | Meaning |
| --- | --- |
| World actor | A server-owned runtime entity created from shared World content and represented to interested clients |
| NPC | A social or service-oriented world actor whose behavior is assembled from capabilities such as dialogue, vendor, quest, crafting, insurance, trainer, bank, or Recovery Storage access |
| Mob | A combat-oriented world actor with combat, aggro, AI, death, loot, and respawn contracts |
| Capability | A composable interaction role attached to an NPC definition without requiring a new actor class |
| Actor definition id | A globally stable content id for one NPC or Mob archetype |
| Spawn definition id | A globally stable content id for one spawn point, spawn group, spawn area, or patrol assignment |
| Runtime actor id | A worker-runtime-scoped identity for one live actor instance |
| Network entity id | The current realtime identity used for interest management and protocol messages |
| Presentation archetype id | A content reference resolved by Unity to a presentation-only prefab |
| Interaction bounds | Server-owned bounds used to measure distance and select a line-of-sight target point |
| Interaction session | One server-owned, player-to-target session opened after authoritative validation |
| Faction | Content identity used by disposition, reputation, and future combat rules |
| Disposition | The current relationship of an actor toward a player, independent from NPC or Mob kind |

`NPC` and `Mob` are not interchangeable. An NPC is not a generic name for every
non-player character. A Mob is the canonical name for a combat enemy such as a
Feral Wolf. A vendor, quest giver, crafter, trainer, banker, or city guard is an
NPC.

Actor kind does not encode friendly or hostile state. Faction and disposition
own that decision. A city guard remains an NPC even if a future criminal system
makes the guard hostile toward a specific player.

All city NPCs, including guards, are invulnerable in the first implementation.
Phase 12 delivers only that invulnerable NPC damage policy, so its first actor
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
6. AuthService remains the authority for global durable mutations invoked by a
   future capability, including items, currency, quests, policies, and durable
   progression.
7. Unity sends advisory target and action intents. It never proves range, line
   of sight, capability, faction, actor state, or authorization.
8. A player may have only one active world interaction session, including an
   existing corpse view. Many players may interact with the same target
   concurrently.
9. Normal NPCs and Mobs do not receive one PostgreSQL row per runtime instance.
10. Normal actor state is reconstructed from compiled WorldData after a worker
    restart. Durable unique actors remain an explicit future opt-in.
11. NPCs are event-driven. Mobs use centrally scheduled active and dormant
    simulation tiers. No actor owns an asynchronous loop, thread, or timer.
12. Stable content and spawn ids never double as worker-runtime or network ids.
13. Actor replication reuses the existing entity registry and spatial interest
    management instead of creating a parallel visibility system.
14. Capability state is requested after an interaction opens. It is not copied
    into every actor spawn or movement packet.
15. Interaction range and line of sight are revalidated for every operation,
    not only when the panel first opens.

## Content Ownership And Compilation

Neutral actor authoring belongs under:

```text
WorldData/
  Authoring/
    Actors/
    ActorSpawns/
  Runtime/
    Actors/
```

The exact file split may group related definitions for authoring convenience,
but the compiled runtime result must be deterministic, framework-neutral, and
independent from a Unity scene or asset database.

The same compiler and validation rules must be consumed by:

- Unity Editor authoring tools.
- A command-line compiler and verifier.
- SimulationWorker startup.
- Backend unit and content tests.
- Continuous integration.

The command-line entry point is `Tools/WorldActorCompiler`. Its default mode
loads the committed canonical actor and spawn manifests, while explicit source
and output arguments support isolated tests. `--verify` compiles in memory and
fails when committed runtime output is missing or differs without rewriting it.

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
- Actor kind: `npc` or `mob`.
- Display name or localization key.
- Presentation archetype id.
- Faction id.
- Interaction bounds and targeting anchor.
- Capability descriptors for an NPC.
- Mob activity, brain, combat, locomotion, and loot-profile references for a Mob
  when those systems exist.
- Explicit lifecycle and respawn profile references.
- Content tags used by authoring, queries, and future rule systems.

Actor definitions contain gameplay references and validation data, not Unity
materials, components, scene object references, or authoritative MonoBehaviour
state.

The first NPC definition contract uses an invulnerable damage policy. The first
Mob contract may reference future brain, combat, and loot profiles before those
systems are active, but Phase 12 must distinguish an intentionally unresolved
future profile from an invalid content reference.

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

An NPC may contain any valid combination. Capability ordering in the compiled
definition is deterministic and may provide presentation order without changing
authority. Each capability has a stable capability id, kind, optional referenced
profile id, availability metadata, and revisioned server state when required.

The opened capability summary is player-specific. SimulationWorker filters it
through current disposition, actor state, capability state, and future
progression or access rules. A summary returned to one player must never be
reused as authority for another player. Static capability identity may be shared
internally, but availability and its expected revision remain session-scoped.

Phase 12 proves composition and discovery. Phase 13 registers insurance and
quest item-lifecycle handlers that bridge the already validated interaction
session to AuthService. The worker passes exact session, worker runtime, Shard,
interaction session, capability, operation, and revision identity. AuthService
then commits the server-owned price, policy, or grant-lineage transaction. The
handler does not repeat target, range, line-of-sight, or capability discovery
logic.

There must not be authoritative `VendorMonoBehaviour`,
`QuestGiverMonoBehaviour`, or equivalent prefab scripts. Presentation helpers
may render a capability icon or prompt, but the capability registry and
SimulationWorker validation own the live meaning.

## Spawn Definitions And Identity

World content supports these authoring concepts:

- Actor spawn point for one NPC or Mob instance.
- Spawn group for a bounded set of Mob instances.
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
patrol progress, and respawn timers until a later design explicitly makes one of
those states durable. A selected unique boss may later opt into durable state,
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
  deferred Phase 12 handlers.
- `WorldActorActivityScheduler` for central bounded dormant and active Mob
  buckets.

NPCs do not tick merely because they exist. They react to interaction requests,
content changes, or centrally scheduled events. Stationary NPCs should impose
no per-frame behavior work beyond existing interest queries and replication
when state changes.

Mobs use two initial scheduling tiers:

- Dormant: no nearby relevant player and no per-tick AI evaluation.
- Active: relevant players or combat state require centrally batched updates.

The Mob activity profile owns activation radius, larger deactivation radius,
active update cadence, and coarse dormant-check cadence within validated global
limits. The larger deactivation radius provides hysteresis. Content selects a
profile instead of embedding scheduler constants in a prefab or actor class.

Phase 12 establishes the tier contract, transition rules, counters, and testable
scheduler seam. Full combat behavior remains later work. The first complete Mob
brain is expected to use an explicit state machine with `Idle`, `Patrol`,
`Investigate`, `Chase`, `Attack`, `Return`, `Dead`, and `Respawning` states, but
Phase 12 does not implement those behaviors.

The runtime must not create one `Task`, timer, thread, database session, or HTTP
poller per actor. Work is performed by bounded stores, spatial queries, event
queues, and central fixed-step or coarse-schedule batches.

## Interaction Targeting And Input

The player's interaction action is `E`. The crosshair supplies the intended
target, but target selection is advisory because a client cannot be trusted as a
security boundary.

One reusable client target system serves:

- NPCs.
- Corpses.
- Resource nodes.
- Crafting stations.
- Loot containers.
- Doors.
- Future interactable world objects.

Phase 12 migrates corpse target selection into this shared client foundation
and makes the existing corpse view consume the same one-active-interaction
lease. It does not replace the authoritative corpse view or transaction
protocol.

The initial shared interaction values are:

| Rule | Value |
| --- | --- |
| Client discovery distance | `6.0` metres |
| Authoritative start range | `3.0` metres |
| Authoritative maintain range | `3.5` metres |
| Client spherecast tolerance | `0.15` metres |

The client checks a crosshair-centred ray first and may use a small spherecast to
make thin or animated presentation targets practical. It ranks only registered
interactable views and provides a prompt for the best unobstructed candidate.
The discovery distance does not grant authority to open an interaction.

The start range is the player's base interaction range. The maintain range adds
small hysteresis so movement and reconciliation near the boundary do not flicker
an already open panel. Phase 12 uses these shared values instead of per-prefab
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

Phase 12 targets protocol version `12`, immediately after the current version
`11`. Implementation must increment the protocol version once and update .NET,
Unity, handshake, tests, and diagnostics together. An old or partially updated
client receives the existing explicit protocol-mismatch path instead of
misreading a packet.

The planned protocol separates actor visibility from interaction state:

- Reliable actor spawn and despawn identity.
- Bounded actor state updates only when replicated state changes.
- World interaction open, close, and capability-action intents.
- Opened state with interaction session identity and capability summary.
- Operation result correlated by operation id.
- Stable server-initiated interaction closure.

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
- `WorldInteractionTargetingController` performs advisory crosshair targeting
  against registered presentation targets.
- `WorldActorView` is the presentation base for actor identity and target bounds.
- `NpcView` and `MobView` provide kind-specific presentation hooks without
  owning authority.
- A presentation registry resolves `presentationArchetypeId` to an authored
  prefab.
- `TemporaryWorldInteractionPanel` is a replaceable uGUI surface over the
  permanent controller and capability model.

The presentation registry fails visibly for a missing archetype and may use one
explicit development fallback. It never changes actor kind or capability data.
Runtime prefabs are instantiated only from authoritative actor presence inside
the existing interest system.

The interaction panel displays the target name and available actions such as
Talk, Shop, Quests, Crafting, Insurance, Bank, or Recovery. The available list
comes from the opened server response. Selecting an action sends an intent to a
registered capability adapter. Phase 12 may present capabilities whose business
handler is intentionally deferred, but it must label that state clearly and
must not fake a successful transaction.

The current crosshair remains presentation-only. Target highlighting and prompt
color are advisory. Closing the panel restores the normal camera and pointer
behavior through the existing input and UI coordination foundation.

## Unity Editor Authoring UX

All project-specific Editor tools remain below `Shooter MMO > Tools`.

Phase 12 adds:

- `Shooter MMO > Tools > Content > Actor Studio`.
- `Shooter MMO > Tools > Content > Spawn Authoring`.

Actor Studio supports:

- Search and filtering by actor kind, faction, capability, and tag.
- Creation from NPC or Mob templates.
- Safe duplication with a new stable id.
- Capability add, remove, reorder, and inline validation.
- Dropdown selection for referenced profiles and presentation archetypes.
- Interaction-bounds preview.
- Deterministic save, compile, verify, and change preview.
- Clear distinction between authored source, compiled output, and Unity
  presentation mapping.

Spawn Authoring supports:

- Visual scene handles for spawn points, spawn areas, and patrol paths.
- Ground snapping against authored scene collision.
- Actor prefab preview selected through the presentation archetype.
- Spawn-group and population-limit authoring.
- Respawn and activation-profile selection.
- Stable id generation and duplicate-reference validation.
- Import from canonical WorldData into the scene authoring view.
- Export preview showing the exact canonical WorldData change.
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

Phase 12 requires only enough faction data to preserve identity and expose a
server-owned friendly, neutral, or hostile disposition. It does not implement
the complete reputation or criminal system.

NPC damage policy is `invulnerable` in the first version. Unity may show feedback
for an invalid attack, but SimulationWorker owns the rejection. Guards use the
same NPC invulnerability rule. Mobs reserve damageable combat state for the later
combat phase.

## Mob Loot And Corpse Boundary

Mobs produce Mob corpses, not NPC corpses. Normal Mob corpses are planned as
live SimulationWorker containers with an approximately two-minute default
lifetime and no restart guarantee. Selected bosses may later opt into the
durable corpse path.

Phase 12 establishes actor, spawn, runtime identity, and future Mob lifecycle
references. It does not generate loot, create Mob corpses, or call the durable
item transaction kernel. Those behaviors belong to the later Mob corpse phase
and must reuse the canonical corpse-container rules.

Successful future loot materialization into player custody requires an
idempotent grant id. Normal actor scalability must not be traded for durable
per-instance custody before death or loot exists.

## Stable Error Contract

Phase 12 reserves these stable protocol error codes:

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

Phase 12 requires automated coverage for:

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

Implementation must also pass locked dependency restore, dependency policy,
formatter verification, the existing item-catalog and collision verification,
`Tools/WorldActorCompiler --verify`, the complete Release build, every backend
test including isolated PostgreSQL integration tests, and all Unity EditMode and
PlayMode tests.

The manual acceptance gate must prove:

1. Create one NPC in Actor Studio with at least dialogue, vendor, quest, and
   crafting capabilities.
2. Place it visually with Spawn Authoring, ground-snap it, preview it, export the
   canonical change, and compile successfully.
3. Create and place one Mob definition through the same tools.
4. Restart SimulationWorker and verify both actors reconstruct from WorldData.
5. Join with Unity, point the crosshair at the NPC from within `3.0` metres, and
   press `E`.
6. Verify the temporary uGUI lists the authoritative capability summary.
7. Try from outside start range and through blocking collision and observe the
   stable rejection without opening local state.
8. Open the same NPC from two clients and verify both sessions remain valid.
9. Move one player beyond `3.5` metres and verify only that player's interaction
   closes.
10. Restart or reconnect and verify actor presence returns from authoritative
    interest state without stale interaction state.

## Phase Boundary

Phase 12 is complete only when content creators can create new NPC and Mob
definitions, compose existing NPC capabilities, place and verify spawns visually,
and exercise authoritative crosshair interaction without adding a new runtime
actor class or a database row per actor.

Phase 12 explicitly stops before:

- Vendor buying or selling.
- Insurance purchase or removal UI and pricing.
- Quest offer, acceptance, progression, turn-in, or abandonment gameplay.
- Crafting recipes or production.
- Trainer or profession progression.
- Combat, health, damage, aggro behavior, or full Mob AI.
- Mob loot generation or Mob corpse creation.
- Durable unique boss state.
- Final NPC, Mob, prompt, dialogue, or interaction-panel art.
- Zone, Layer, multi-worker Shard, or world-event orchestration.

These later systems must extend the Phase 12 contracts instead of bypassing
them with prefab-owned authority, endpoint-specific target checks, or parallel
actor registries.

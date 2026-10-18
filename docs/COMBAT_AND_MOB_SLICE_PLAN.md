# Combat And Mob Slice Plan

Last updated: 2026-10-18

Status: Decided and in progress. The product owner accepted every
recommendation on 2026-10-18, including C1. Implementation follows the
[Implementation Plan](#implementation-plan): step 1, the combat content, is
implemented; steps 2 to 6 are not.

## Purpose

This plan prepares the MVP slice "Target-based combat and simple Mob" from
[MVP Specification](MVP_SPEC.md#mvp-slices):

- One Ranged weapon, `weapon.training_shortbow`, that consumes
  `ammunition.bronze_arrow` from the 1000-unit `ammunition` equipment slot.
- Target selection and auto attack, where a character must stop moving before
  it attacks.
- Health and Normal damage.
- Authoritative death events that feed the existing player death partition and
  the Mob corpse foundation.
- One simple Mob that spawns, idles or patrols, detects players, approaches,
  attacks, takes damage, dies, drops simple live loot, and respawns.

It also settles the combat item data that decision D7 and finding B11 in
[Items And Inventory Alignment Plan](ITEMS_INVENTORY_ALIGNMENT_PLAN.md)
deferred: base damage, damage type, combat style and weapon family,
ammunition family and bonus, level requirements, the equipment-change attack
lockout, and per-shot ammunition consumption with worker-batched commits.

The product rules come from [Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md),
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md), and
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md). This plan
only adds the decisions and the architecture needed to build them.

## Current Foundation

The audit on 2026-10-18 at `development` `4f38e9c` found:

| Area | What exists | What combat needs |
| --- | --- | --- |
| GameSimulation | Deterministic click-to-move path following, the A* grid planner, the kinematic motor, and `CollisionRaycast` with the `CombatQueries` layer. Revision `movement-simulation-v6`. | Nothing new. Combat is server-authoritative and not predicted, so the shared revision and golden fingerprints stay. The worker reuses the motor and planner for Mob movement. |
| Realtime protocol | Version 16, 27 messages, no combat state. World actors replicate only through reliable `WorldActorState`; snapshots carry players only. | A Combat message family, Mob entries in snapshots, and an owner item delta. |
| SimulationWorker | A 30 Hz fixed tick, interest management, world actors with dormant and active Mob tiers that only change a tier flag, `WorldActorLineOfSightService`, `MobCorpseLifecycleService`, and `GameServiceClient.ProcessPlayerDeathAsync`. Both death seams have no production caller. | Health, attacks, AI, Mob movement, respawn, deaths, and ammunition batching. The worker knows no equipment and does not load the item catalog today. |
| GameService.Items | The transaction kernel with `ConsumeItemQuantityCommand` (no route), the death partition with idempotent death events, durable and live Mob corpse boundaries, and equipped ammunition stacks up to 1000. | A worker-only batched consumption route, ammunition consumption inside the death transaction, an equipment summary for the worker, and a level requirement on equip. |
| Content | `mob.feral_wolf` (hostile, damageable, live corpse 120 s) and `mob.feral_alpha` (durable corpse 600 s, not spawned). Activity profile `mob.standard`, respawn profile `mob.normal` (30 s). Patrol paths for the wolves. No combat fields on items or actors. | Combat data on item definitions, Mob combat, brain, locomotion, and loot profiles, and player combat rules. |
| Unity client | Remote player interpolation, world actor views that jump to each reliable state, pointer targeting that treats a Mob like an NPC, and no health or combat UI. | Combat targeting, approach to weapon range, Mob interpolation, and temporary combat presentation. |

## Scope

In scope:

- Everything listed in [Purpose](#purpose).
- Player versus player attacks behind a Shard rule, as decided in C8.
- The Feral Alpha as a second Mob that uses the durable corpse path, as decided
  in C14.

Out of scope, each waiting for its own slice:

- Abilities, the ability bar, the global cooldown, the combat triangle, armor,
  defense, resistances, weaknesses, mana, and magic.
- Threat tables, taunt, and the 120 percent target switch margin. The Mob keeps
  a damage ledger that a threat table replaces later.
- Potion and food use intents and their cooldowns.
- Character experience and levelling. Only the level requirement and a durable
  level of 1 arrive here, as decided in C11.
- Gameplay rule areas and the presence lifecycle, including the five-minute
  body after a disconnect in the Open Risk area. See C17.
- The right-click context menu, Examine, final combat art, animations, and the
  final HUD.

## Product Decisions

The product owner chose the recommendation for every decision on 2026-10-18.
Numbers are starting values for tuning, not final balance.

| Id | Decision | Recommendation | Alternatives | Status |
| --- | --- | --- | --- | --- |
| C1 | Slice order against vendor and gathering | Both slices run in parallel. Combat steps 1 and 2 do not touch the realtime protocol. Vendor and gathering is smaller, so it takes the next protocol version first, and combat rebases on the latest `development` before its protocol step. Both slices also change the item catalog shape, so the second one to merge rebases its catalog format version too. | Combat first; strictly one slice after the other. | Decided 2026-10-18 |
| C2 | Mob aggression | Hostile Mobs are aggressive: a wolf attacks a player it can see within 8 m. Faction and disposition stay the source of that rule, so a neutral Mob only fights back. | Mobs fight only when attacked. Then "detect nearby players" from the Mob MVP is not exercised. | Decided 2026-10-18 |
| C3 | Starting numbers | Player 100 health. Training Shortbow 10 to 18 Normal damage every 2.4 s, range 10 m. Bronze Arrow +5 percent. Feral Wolf 50 health, bite 4 to 9 every 1.8 s at 1.5 m, speed 4.5 m/s (the player walks at 5), detection 8 m, leash 30 m, respawn 30 s. Player versus player then takes about 14 s with equal gear, inside the 10 to 20 s target, and a wolf takes 4 arrows. | Other values. All of them are content data and can be tuned without code. | Decided 2026-10-18 |
| C4 | Hit model | Every attack hits. Damage is an even integer roll between the weapon minimum and maximum, plus the ammunition bonus. Misses and accuracy arrive with armor and defense. | Fixed damage per weapon; a hit chance now. | Decided 2026-10-18 |
| C5 | Arrow flight | An arrow leaves on release and hits after its flight time at 30 m/s, about 0.3 s at 10 m. The arrow is spent even if the target dies first. Wolf bites hit at once. | Every attack hits at the moment of release. | Decided 2026-10-18 |
| C6 | Healing in this slice | Health regenerates 2 percent of maximum per second after 10 s out of combat. A Mob heals fully when it returns home. Potion and food use come in their own slice. | No regeneration until potions and food; slower regeneration. | Decided 2026-10-18 |
| C7 | Player death and respawn | The character stays dead for 5 s and then respawns with full health at the World spawn. Its durable corpse stays at the place of death for the existing 5 minutes. | Immediate respawn; a respawn button. | Decided 2026-10-18 |
| C8 | Player versus player | Allowed in this slice behind a Shard rule `playerVersusPlayer`, on for the development Shard. The future rule areas then restrict it to the Open Risk area. | Mobs only until rule areas exist. | Decided 2026-10-18 |
| C9 | Wolf loot | A new item `material.wolf_pelt` (always 1) plus 3 to 8 Bronze Arrows at 40 percent. The pelt gives the vendor slice something to buy. | Existing items only. | Decided 2026-10-18 |
| C10 | Who may loot a live Mob corpse | Everyone, as today. The open world is full loot anyway. | Only the player who dealt the most damage for the first 60 s. | Decided 2026-10-18 |
| C11 | Level requirements | Item definitions get `requiredCharacterLevel`, enforced on equip. Characters get a durable level of 1 and a development command to change it. Experience and levelling come in a progression slice. The shortbow and arrows require level 1. | Defer level requirements; build experience and levels now. | Decided 2026-10-18 |
| C12 | Equipment-change lockout | 1.0 s, refreshed by every change and never stacked, in and out of combat, as the design proposes. It applies whenever the item in an equipment slot changes. Topping up or splitting the equipped arrow stack does not lock. Movement stays possible. | Top-ups and splits lock too. | Decided 2026-10-18 |
| C13 | Ammunition batch interval | Every 5 s while arrows are pending, and always before another item operation, death, leave, and worker shutdown. A worker crash may forget at most about 5 s of shots, which only ever leaves arrows with the player. | 1 s (more database writes); 15 s (larger crash window). | Decided 2026-10-18 |
| C14 | Feral Alpha | Spawn one Feral Alpha in Development World 2 near the wolves with the same brain: 240 health, bite 7 to 12 every 2.4 s at 2.0 m, detection 10 m, its own 300 s respawn profile, 3 pelts and a 25 percent Copper Ring. It exercises the durable Mob corpse path with a real death. | Wolves only. | Decided 2026-10-18 |
| C15 | Leash and reset | A Mob gives up and walks home when it is more than 30 m from home or loses its target. It cannot be damaged on the way and heals fully when it arrives, so it cannot be pulled away and shot down for free. | Damageable while walking home. | Decided 2026-10-18 |
| C16 | Attacking without a weapon | Not possible in this slice. Without a Ranged weapon, or without matching arrows, the character keeps its target and shows why it cannot attack. | A weak unarmed melee attack. | Decided 2026-10-18 |
| C17 | Disconnect during combat | Stays with the presence lifecycle slice. Until then a disconnect removes the character, so a player can escape a fight by disconnecting. The worker already records the in-combat time that the risk timer will use. | An interim rule that keeps the body in the world for about 10 s after a disconnect in combat. | Decided 2026-10-18 |

## Architecture

### Ownership

| Owner | Responsibility in this slice |
| --- | --- |
| Shared content | Combat data on item definitions, Mob combat, brain, locomotion, and loot profiles, and player combat rules, validated by the existing framework-neutral compilers |
| SimulationWorker | Health, targets, attack timing, range, line of sight, hit resolution, projectiles, the attack lockout, the ammunition ledger, Mob AI and movement, death detection, death events, loot rolls, Mob respawn, player respawn, and combat replication |
| GameService | The level requirement on equip, durable ammunition consumption batches, the death partition including the last batch, durable Mob corpses, and the equipment summary for the worker |
| Unity | Attack intents, approach to weapon range, interpolation, and temporary presentation. Unity never decides range, damage, or death. |

Combat stays out of GameSimulation. Nothing about combat is predicted, so a
server-only implementation avoids the Mono and .NET float rules entirely. The
worker reuses the shared motor and planner for Mob movement without changing
them.

### Combat Content

**Item catalog.** Catalog format 4, implemented in step 1 and described in
[Item Catalog Authoring](../content/items/README.md):

- Combat styles (`melee`, `ranged`, `magic`) and damage types (`normal`,
  `fire`, `cold`, `nature`, `shadow`) are closed sets in code. Code gives
  meaning to `ranged` (it consumes ammunition). No damage modifier exists yet,
  so every type deals its rolled damage.
- `weaponFamilies`: id, combat style, ammunition family, and projectile speed.
  `bow` is `ranged`, uses `arrow`, and flies at 30 m/s.
- `ammunitionFamilies`: `arrow`.
- A `weapon` block on every `weapon` definition: weapon family, damage type,
  minimum and maximum damage, attack interval in seconds, and range.
- An `ammunition` block on every `ammunition` definition: ammunition family and
  damage bonus percent.
- `requiredCharacterLevel` on every definition, at least 1 and at most the
  temporary cap of 200 in the catalog's `progression` settings.

Combat values and level requirements belong to the catalog revision but not to
the structural fingerprint, because they never change custody. Structural
fingerprints also hash their own version instead of the catalog format version,
so the new format needed no data migration and no local database reset.
GameService stores only the level requirement, which its equip rule needs.

**Actor catalog.** Actor format 2, implemented in step 1 and described in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md#actor-definitions):

- `combatProfiles`: maximum health and one attack with style, damage type,
  damage roll, interval, and range.
- `brainProfiles`: aggression (`aggressive` or `defensive`), detection range,
  leash range, and give-up time.
- `locomotionProfiles`: walk speed and collision capsule.
- `lootProfiles`: ordered entries of item definition id, quantity range, and
  chance in basis points. `tools/WorldActorCompiler` checks every entry against
  the compiled item catalog.
- Every Mob references a combat, brain, and locomotion profile and may reference
  a loot profile. NPCs stay invulnerable and leave every Mob profile empty.

**Combat rules.** The player rules arrive with their consumer in step 3: a small
strictly validated content file holds the player maximum health, the respawn
delay, the out-of-combat delay and regeneration rate, the in-combat duration,
and the equipment lockout, and the worker copies it into its output like
`world.json`.

The Unity Item Catalog editor and Actor Studio edit and preserve every new
field, with live validation from the shared compilers.

The worker loads the generated item catalog. Its heartbeat reports the catalog
revision, and GameService rejects a worker whose revision differs from the
active catalog, the same lockstep rule that the protocol version follows.

### Character Equipment Summary

The worker learns what a character has equipped from GameService, never from
the client. `ISimulationCharacterStateProvider` grows from carry state into a
live item state: the item-state revision, carry weight, carry capacity, and one
entry per occupied equipment slot with item instance id, definition id,
quantity, and item revision. Every worker-facing response that already carries
the character's revision also carries this summary: ticket consume at join, the
session heartbeat, simulation item and corpse operations, the death partition,
and consumption batches. The worker keeps the highest revision, exactly as
`CarryStateStore` does today, which it replaces.

### Combat State In The Worker

A loop-confined `CombatantRegistry` holds one combat state per player entity
and per Mob actor, keyed by network entity id:

- Health and maximum health.
- Life state: alive, dead, or evading home.
- The attack target, the next attack tick, and the attack status.
- The attack lockout tick for players.
- The in-combat-until tick, which regeneration and the future logout timer read.

The tick gets a combat phase. The order becomes:

1. Collision streaming. Moving Mobs become anchors, so a Mob never walks into an
   unloaded chunk.
2. Carry state and movement per player in entity id order.
3. World actor activity and Mob brains and movement.
4. Combat: due projectile impacts in impact tick and sequence order, then attack
   releases in entity id order, then deaths, then regeneration.
5. Interaction revalidation, snapshots, and corpse presence as today.

Ordering by tick, sequence, and entity id makes every outcome reproducible in
tests with a seeded random source. The random source is one injectable
`ICombatRandom` per worker runtime.

### Attack Timing

Content gives attack intervals in seconds, and the worker converts them once to
whole ticks: 2.4 s is 72 ticks at 30 Hz.

1. The client sends `AttackTarget` once. The target stays until the client sends
   `ClearTarget`, the target dies or despawns, or the target becomes invalid.
2. Every tick the worker checks the attacker: alive, standing still, target in
   range, line of sight, attack cooldown reached, no lockout, a weapon, and for
   Ranged a matching arrow. Standing still means no active path and no
   horizontal velocity after this tick's movement phase.
3. When everything holds, the attack releases: one arrow is counted against the
   ammunition ledger, the cooldown restarts, and the impact is scheduled.
4. When something is missing, nothing releases and the owner sees the attack
   status, such as `Moving`, `OutOfRange`, `LineOfSightBlocked`,
   `EquipmentLockout`, or `AmmunitionMissing`.

The first shot fires on the first tick after the character stops in range with
its cooldown ready. Moving between shots is allowed; stopping is the price of
every shot. Movement never clears the target, because walking into range is
movement too. The client clears the target when the player clicks the ground or
presses Escape.

Range is the distance from the attacker's root to the closest point of the
target's combat bounds: the interaction bounds for Mobs, and the movement
capsule for players. Line of sight reuses the `CombatQueries` raycast from eye
height to the target's bounds centre, generalized from
`WorldActorLineOfSightService` to every combatant.

### Hit Resolution And Damage

Damage follows the design's order, with only the first two steps active:

1. The weapon rolls its base damage and gives the damage type.
2. Ammunition adds its bonus percent of that roll, rounded half up.
3. The combat triangle and armor: not yet.
4. Elemental weakness or resistance: not yet.

`CombatDamageCalculator` is a pure function with these steps in order, so later
slices add steps without touching timing or replication. Health never drops
below 0, and a combatant at 0 is dead.

An arrow's impact tick is its release tick plus its flight time, at least one
tick. A bounded impact queue applies the damage at that tick if the target is
still alive. The arrow is spent either way. A Mob bite resolves in its release
tick.

### Ammunition Consumption

Every shot consumes one arrow, but one PostgreSQL transaction per shot is too
heavy. The worker keeps one `AmmunitionLedger` per player:

| Field | Meaning |
| --- | --- |
| Item instance id, definition id | The equipped ammunition stack from the equipment summary |
| Durable quantity and item revision | The last committed state |
| Pending | Shots not yet sent to GameService |
| In-flight batch | Batch id and quantity of the batch being committed |

The live quantity is durable minus pending minus in flight. A shot needs a live
quantity of at least 1.

A batch commits through a new worker-only route,
`POST /api/simulation-sessions/{id}/ammunition-consumptions`, which runs a
`ConsumeEquippedAmmunitionCommand` in the existing kernel. The batch id is the
operation id, so a retry after a lost response replays the stored result. The
command checks that the item is ammunition in the character's `ammunition`
slot, has the expected item revision, and holds at least the quantity. At zero
it deletes the stack, as the existing consume handler does.

A batch starts:

- 5 s after the first pending shot (C13).
- Before any other item or corpse operation of the same character.
- When the session ends by leave or disconnect, before the session is released.
- When the worker shuts down, before it releases its sessions.

At death the pending shots travel inside the death request, and the death
transaction consumes them before it partitions the items. Death therefore never
races a separate batch.

All durable operations of one character run one at a time in a per-character
authority lane: client item and corpse intents, consumption batches, and death.
Client intents keep today's limit of one running and eight waiting. Batches and
the death are system operations that are never dropped: at most one batch waits,
and a pending death rejects every waiting client intent with
`combat_character_dead`.

Every committed batch advances the character's item-state revision, as every
item mutation must. Two rules keep that invisible to the player:

- The worker sends the owner `OwnerItemQuantityChanged` with the old and new
  revision, the stack's new quantity, and its new revision. The client patches
  its inventory when its known revision matches the old one and refreshes fully
  otherwise.
- When a client intent names a revision that only the worker's own batches have
  advanced since, the worker forwards it with the current revision. The intent
  was computed on a state that differed only in the arrow count, and an intent
  that targets the arrow stack still fails on that stack's own revision.

Failure handling:

| Case | Behavior |
| --- | --- |
| Timeout or GameService error | Retry the same batch id with backoff. Shooting continues against the live quantity. |
| Item revision changed | Reread the equipment summary. Pending shots that no longer have a stack are dropped and counted. |
| Session no longer valid | The session ends; pending shots are dropped and counted. |
| Worker crash | Pending and unacknowledged shots are forgotten. The player keeps at most about 5 s of arrows. Nothing is ever duplicated or destroyed twice. |

### Equipment-Change Attack Lockout

The lockout is a worker-owned tick on the player. When the worker forwards an
operation that may change which item sits in an equipment slot, such as equip,
unequip, a swap, or a corpse Bag swap, it blocks attacks until the result
arrives. A committed change then locks attacks until 1.0 s after that result;
a rejection only lifts the wait. Every change refreshes the end instead of
adding to it. Movement continues, and later consumables will too.

### Level Requirements

`characters.level` is a new Characters column with default 1. Items reads it
inside the equip transaction through a new Characters contract, recorded as a
cross-module statement in [GameService Modules](GAME_SERVICE_MODULES.md), and
`item_definitions.required_character_level` is mirrored from the catalog. An
equip below the requirement fails with `equipment_level_requirement_not_met`. A
development command sets a character's level for testing, refused outside the
development environment like the other development tools.

### Death Events And Respawn

**Mob death.** When a Mob reaches 0 health it stops thinking, cannot be
targeted, and clears every attacker's target. The worker rolls the Mob's loot
profile and calls `MobCorpseLifecycleService` with the death event id
`ScopedGuid("mob-death", runtimeActorId)`. Each Mob life has its own runtime id,
so the id is stable for retries and unique per death. A live corpse is created
on the loop; a durable corpse for the Feral Alpha runs as a system operation
and retries with the same id until it commits. The actor despawns only after
its corpse exists, with the despawn reason `died`. A Mob whose loot roll is
empty leaves no corpse.

**Mob respawn.** The death schedules a respawn at the death tick plus the
respawn profile's delay, on the central due-tick timer queue that vendor and
gathering introduces for resource nodes. `WorldActorStore.Respawn` recreates
the same compiled spawn instance with a fresh runtime id and network entity id,
as a worker restart does, and interest spawns it for nearby players. Respawn
timers reset on a worker restart, as the NPC design allows.

**Player death.** When a player reaches 0 health:

1. The worker marks the character dead, stops its movement, closes its world
   interaction and corpse view, clears every attacker's target, and rejects
   further combat, movement, item, and interaction intents with
   `combat_character_dead`.
2. The character's lane sends the death partition with a new death event id
   that is stored first, so every retry reuses it. The request carries the
   pending ammunition, the authoritative position and rotation, and the killer
   (a Mob definition id or a character id), which the death event records for
   audit and future reputation rules.
3. GameService partitions the items as today. The worker applies the durable
   corpse and the new equipment summary.
4. After the respawn delay (C7) the character teleports to the World spawn with
   full health. Every client snaps the entity instead of interpolating it, and
   the owner resets its prediction.

A death that cannot commit is retried with backoff while the character stays
dead. A disconnect while dead waits for the death before the session is
released. A worker crash between the lethal hit and the commit loses that death:
the character keeps its items and joins again at the spawn. That window is one
GameService round trip.

### Mob AI

Mobs keep the two central tiers. Nothing gets its own task, timer, or thread.

| Tier | Work |
| --- | --- |
| Dormant | No brain and no movement. A coarse check every 2 s wakes the Mob when a player comes within the activation range. |
| Active | The brain thinks at the profile's active rate, 10 Hz, staggered by entity id so all Mobs do not think on the same tick. A moving Mob moves every simulation tick. |

A Mob in combat, evading, or dead stays active until it is back home and idle.

The brain is an explicit state machine:

| State | Behavior |
| --- | --- |
| Idle | Stands at its spawn. |
| Patrol | Walks its patrol path with the authored waits. |
| Chase | Walks toward its target until it stands in attack range with line of sight. |
| Attack | Stands still and attacks on its cooldown. Returns to Chase when the target leaves range. |
| Evade | Walks home, cannot be damaged, and heals fully on arrival (C15). |
| Dead | Waits for its corpse and despawn. |

Detection: an aggressive brain looks for the nearest living player inside its
detection range with line of sight. Damage always starts combat, for any brain.
The Mob keeps a small ledger of who damaged it. When its target dies or becomes
invalid, it takes the attacker with the most damage that is still valid, then
the nearest detected player, and evades otherwise. A threat table later
replaces that choice without changing the states.

Spatial queries: a per-tick grid of living player positions serves detection and
the activity tiers. Today's tier check compares every Mob with every player,
which does not scale to the planned 2,000 Mobs.

Movement: each locomotion profile builds a `MovementSimulationSettings` with its
walk speed and capsule, and the Mob follows its waypoints with the same
`PlayerMovementSimulation.Step` and collision as players. Routes come from the
shared `MovementPathPlanner` through a planning queue on the loop with a fixed
budget of plans per tick and a node cap sized to the leash range. A Mob replans
when its target moves more than 1.5 m from the planned goal or its path is
blocked. Mobs do not collide with players or with each other.

Replication: a moving Mob travels in the unreliable snapshots exactly like a
remote player and is interpolated the same way. When it stops, one reliable
`WorldActorState` carries its rest position. Movement advances the state
revision but never the interaction revision.

### Realtime Protocol

Vendor and gathering takes protocol version 17 (C1), so combat takes version 18
and the next free message ids. New messages form a `Combat` family:

| Message | Direction | Delivery | Content |
| --- | --- | --- | --- |
| `CombatIntent` | Client to server | Reliable ordered | Operation id, kind `AttackTarget` or `ClearTarget`, target entity id |
| `CombatIntentResult` | Server to client | Reliable ordered | Operation id, kind, success, or a stable error |
| `CombatantState` | Server to client | Reliable ordered, interest filtered | Entity id, state revision, health, maximum health, flags (dead, in combat, evading), target entity id |
| `CombatEventBatch` | Server to client | Reliable ordered, interest filtered | Server tick and up to a bounded number of events: attack released (attacker, target, impact tick), hit (target, damage, damage type, health after), died |
| `OwnerCombatState` | Server to client | Reliable ordered, owner only | State revision, life state and respawn tick, target, attack status, next attack tick, lockout end tick, equipped ammunition item and live quantity |
| `OwnerItemQuantityChanged` | Server to client | Reliable ordered, owner only, Items family | Reason, old and new item-state revision, carry state, item id, new quantity (0 when consumed), new item revision |

Other changes in the same version:

- `SimulationSnapshot` entities may name world actor entities.
- `JoinAccepted` carries the Shard rules, so the client knows whether a left
  click on another player attacks.
- `CombatantState` follows every `EntitySpawn` and `WorldActorSpawn` of a
  combatant, so a client never shows a combatant without health.

Stable error codes: `combat_target_not_found`, `combat_target_not_attackable`,
`combat_target_dead`, `combat_target_self`, `combat_player_versus_player_disabled`,
`combat_character_dead`, and `combat_intent_invalid`. Conditions that change
every tick are attack statuses in `OwnerCombatState`, not errors. Combat intents
use a per-peer rate limit like world interactions. Combat events go only to
peers that see the attacker or the target. Every message fits the 1,200-byte
limit; event batches split into several packets when needed.

### Unity Client

A new `Mmorpg.Client.Combat` assembly sits between WorldActors and Ui:

- `CombatClientController` owns combatant states, the owner combat state, and
  the event stream, with monotonic revisions and reconnect cleanup.
- `CombatTargetingController` is an `IWorldClickHandler` registered before
  interaction targeting. A left click on a Mob, or on a player when the Shard
  allows it, selects it, sends `AttackTarget`, and walks to the weapon range
  from the catalog minus a small margin. It walks again when the target moves
  out of range or the owner state reports blocked line of sight. A ground click
  or Escape sends `ClearTarget`.
- World interaction targeting stops treating Mobs as NPCs. Corpses keep their
  flow.
- `MobView` interpolates from snapshots with the remote player interpolation,
  which moves to a place both Gameplay and WorldActors can use.
- `InventoryClientController` applies `OwnerItemQuantityChanged`, and the
  equipped arrow slot shows the live quantity.

Temporary presentation, all runtime-built so no scene or prefab changes are
needed:

- Owner health, live arrow count, and attack status.
- A target frame and health bars over damaged combatants.
- White damage numbers for Normal damage.
- A simple arrow from bow to target timed to the impact tick.
- A death overlay with the respawn countdown.

### Telemetry

A `Mmorpg.SimulationWorker.Combat` meter with low-cardinality instruments:
attacks, hits, and deaths by combatant kind, active combatants, Mob brain and
combat phase durations, consumption batches by outcome, pending and dropped
arrows, and death partition duration. GameService counts the new consumption
operation kind in its existing item operation metrics. No identity appears as a
label. [Monitoring](MONITORING.md) and the dashboards list the new instruments.

### Verification

- Content: compiler tests for every new field and profile, cross-catalog loot
  references, and Unity editor round trips.
- GameService integration: batch replay, revision conflict, wrong slot, full
  consumption, death with pending arrows in one transaction, the level
  requirement, and the equipment summary in every response.
- Worker unit tests: tick-exact attack timing, the standing-still rule, range and
  line of sight, the damage steps, projectile impacts, the lockout, every ledger
  trigger and failure case, revision rebasing, death and respawn for both kinds,
  and every brain transition.
- Worker loop and harness tests: a player kills a wolf over real UDP against the
  scripted GameService, loots the corpse, and the wolf respawns; a wolf kills a
  player and the death partition runs once.
- Protocol goldens in .NET and Unity for every new message.
- Unity EditMode tests for the combat controllers and click priority, and the
  PlayMode bootstrap.
- Load: the existing 2,000 Mob scenario with active combat and moving Mobs.

## Implementation Plan

Each step is one pull request into `development` with passing CI, including the
Unity gate, and updates its documents in the same change.

1. **Combat content.** Item catalog format 4 with combat data and
   `requiredCharacterLevel`; actor format 2 with combat, brain, locomotion, and
   loot profiles; `material.wolf_pelt` and the Feral Alpha spawn; the Unity
   editors; the GameService level column, catalog mirror, equip rule, and
   development command. No protocol change.
2. **GameService combat boundaries.** The equipment summary in every
   worker-facing response; the batched consumption route and command; pending
   arrows and the killer inside the death partition; the worker's catalog
   revision check; the worker client methods and their response validation.
   No protocol change.
3. **Protocol and authoritative combat.** Rebase on the latest `development`
   first. The new protocol version; the worker's catalog and rules loading,
   combatant registry, character lanes, attack timing, damage, projectiles,
   lockout, ammunition ledger, Mob and player death producers, Mob respawn,
   player respawn, regeneration, replication, and the Shard rule; the client's
   protocol handling and `CombatClientController` without presentation; the
   stress clients and bots. Mobs are still stationary targets in this step.
4. **Unity combat presentation.** Combat targeting and approach, Mob clicks,
   the temporary HUD, health bars, damage numbers, arrows, the death overlay,
   and the live arrow count in the inventory, after the inventory placeholder UI
   work has merged.
5. **Mob AI.** The player grid, staggered brain buckets, the brain states,
   detection, movement with the shared motor, the planning queue, evade and
   reset, patrols, Mob snapshots, client Mob interpolation, and the load
   scenario.
6. **Acceptance.** The full manual loop below on the local stack, the slice
   marked complete in [MVP Specification](MVP_SPEC.md), and the planned combat
   items moved to implemented in the design documents.

## Coordination

- **Vendor and gathering.** It will also change the realtime protocol and the
  item catalog shape. Each slice rebases on the latest `development` before its
  protocol step, and the protocol version and catalog format version are taken
  in merge order (C1).
- **Inventory placeholder UI.** It changes `TemporaryInventoryPanel` and
  `TemporaryWorldInteractionPanel`. Combat presentation lives in new files, and
  the live arrow count in the inventory waits until that work has merged.

## Manual Acceptance

On the local stack with Development World 2 and a character that has the
Training Shortbow and Bronze Arrows equipped:

1. Walk toward the wolves. A wolf notices the character at about 8 m and runs
   toward it.
2. Left-click the wolf. The character walks into bow range, stops, and shoots.
   Each arrow shows a white damage number, and the arrow count drops by one per
   shot.
3. Click the ground while attacking. The character walks away and stops
   shooting.
4. Kill the wolf. It despawns, a corpse appears, and the corpse holds a Wolf
   Pelt and sometimes arrows. Loot it.
5. Wait 30 s. A new wolf appears at its spawn.
6. Swap the bow out and back in during a fight. The character cannot shoot for
   about 1 s after each change.
7. Shoot until the arrows run out. The character keeps its target and reports
   that it has no ammunition. The inventory and a fresh login show the same
   arrow count as the HUD.
8. Let wolves kill the character. It dies, its corpse appears where it fell,
   and after 5 s it respawns at the spawn with full health and nothing but its
   Secure Container contents. Loot the corpse back.
9. Pull a wolf more than 30 m from home. It walks back, cannot be damaged on
   the way, and has full health at home.
10. With two clients, one character attacks the other and the death partition
    runs once.

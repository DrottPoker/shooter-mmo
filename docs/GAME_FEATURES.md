# Game Features

Last updated: 2026-07-19

## Purpose

This document records implemented player-facing game features and how they work.
It is deliberately separate from service capabilities and the broader product
design. Features are added here only when working code exists.

## Account Entry Flow

Status: UI prototype on implemented account flow

The temporary LoginMenu UI allows a player to register or log in. A successful
request stores the account session in the persistent Unity client state and loads
CharacterSelect. Duplicate submissions are blocked while a request is active.
Only one client session may remain active for an account. A successful login on
a second client replaces the first client, disconnects its active shard peer if
needed, clears its local account state, and returns it to LoginMenu with a
temporary Unity Console explanation.

This is functional development UI, not final art or UX.

## Character Selection

Status: UI prototype on implemented character flow

The temporary CharacterSelect UI supports:

- Loading the account's characters.
- Creating a character.
- Selecting one character for shard entry.
- Loading and selecting an online shard with region, fleet, player count, and
  capacity information.
- Requesting and validating a simulation join in one sequential operation.
- Logging out before returning to LoginMenu.

Character appearance, customization, deletion, progression, and statistics are
not implemented.

## Shard Entry And Exit

Status: Foundation implemented with temporary UI

A selected character can enter an online local shard through the authenticated
placement and join-ticket flow. AuthService selects the assigned healthy worker
runtime and returns its endpoint only with the ticket. The client validates the
placement, connects with LiteNetLib, sends the ticket through the shared
versioned protocol, and loads the World scene mapped from the accepted WorldId
only after SimulationWorker accepts the session. The selected World scene
displays character, shard, World, worker, runtime, session, and UDP connection
state.

Leave sends the exact simulation-session id over the active peer and waits for server
acknowledgement before returning to CharacterSelect. Unexpected disconnects
clear shard state and return an authenticated player to CharacterSelect. HTTP
401 from AuthService still clears account state and returns the player to
LoginMenu.

## World Preview

Status: Initial content on implemented network gameplay foundation

DevelopmentWorld1 contains the original 30 by 30 meter scene-authored test map.
DevelopmentWorld2 provides a 512 by 512 meter greybox map for broader traversal
and content development. Each registered World scene owns a LocalPlayer prefab
reference, an explicit spawn point, and a separate entity presentation root.
After SimulationWorker accepts a character join, the mapped scene creates one
runtime LocalPlayer instance whose prefab owns the configured third-person
camera. Without an active joined session, no local player exists in the scene.
DevelopmentWorld1 retains the static boundaries, slope, three step heights,
cover, and dedicated camera-collision wall used by focused movement tests.

The test shard has no durable gameplay simulation yet. During authenticated play, other
connected characters are represented by an authored RemotePlayer prefab and
rendered from interpolated SimulationWorker snapshots. SimulationWorker assigns each live
player a network entity id. Reliable spawn and despawn messages decide when a
remote player exists, while unreliable snapshots update only its movement.
Remote instances are kept under the active World scene's separate
`EntityPresentationRoot`, outside the runtime LocalPlayer hierarchy.
SimulationWorker uses distance-based interest management, so distant entities are
removed reliably and recreated reliably when they enter the configured area of
interest. Snapshot loss never decides whether a remote player exists.

## Player Movement

Status: Server-authoritative movement and test-map collision implemented

- Move with WASD.
- Sprint with Shift.
- Jump with Space.
- Sprint can only begin while grounded. A sprint that began on the ground can
  continue through a jump while Shift remains held.
- Releasing Shift in the air ends sprint, and pressing it again cannot restart
  sprint until the player reaches the ground.
- Airborne movement input cannot accelerate, stop, redirect, or rotate the
  character. A jump or fall preserves its horizontal takeoff momentum until the
  character is grounded again. The camera remains independently controllable.
- Camera-relative movement follows the current mouse-controlled view.
- While aiming, the player faces the camera direction so lateral movement
  behaves as shooter strafing.
- Aim cancels sprint and blocks both sprint and jump until Aim is released. The
  shared simulation enforces this rule for prediction and SimulationWorker authority.
- Movement uses a PlayerInput-owned Unity Input Actions asset.
- The local player predicts each fixed input tick immediately. A separate local
  presentation state interpolates those predictions at the render frame rate so
  the player and camera do not move in 30 Hz steps.
- SimulationWorker owns the accepted position, velocity, facing, grounded state, and
  sprint state.
- SimulationWorker resolves the player capsule against the baked ground, boundaries,
  ramp, steps, cover, and camera test wall.
- Walkable slopes up to the configured 45 degree limit hold a grounded character
  in place when movement input stops. Steeper surfaces are not treated as ground.
- The simulation capsule uses a slope-aware support height to avoid penetration
  correction along the slope. Presentation removes only that geometric offset,
  preserving render-frame interpolation instead of replacing it with the latest
  surface height.
- Grounded transitions over configured steps and small drops use an additional
  100 ms vertical presentation blend. Jumping, airborne movement, steep surfaces,
  teleports, and large corrections bypass this blend.
- Authoritative snapshots acknowledge processed input sequences. The client
  replays remaining input and smooths small corrections.
- Remote players advance on an adaptive frame-rate render clock through a
  snapshot buffer approximately 100 ms behind server time. Bounded clock
  correction restores that delay after a network stall instead of accumulating
  permanent latency.
- SimulationWorker neutralizes movement and action state after 500 ms without a
  newer input sequence, then resumes from the next valid input.

Gamepad bindings and player-configurable rebinding are not implemented yet.

Movement speed, gravity, terminal fall speed, jump, facing, capsule dimensions,
slope and step rules, authored collision, and world bounds are validated and
simulated by SimulationWorker.
The active UDP connection carries sequenced input and periodic movement
snapshots in addition to session control.
The character root is the shared logical ground point for players and NPCs.
`CharacterBody` configures the CharacterController contact envelope around that
point, including skin width, without moving the presentation hierarchy. Visual
assets use a feet-at-zero convention below PlayerVisual, and spawn placement
aligns the character root directly to the spawn point. Direct scene preview uses
normal gravity and CharacterController collision without forced per-frame ground
snapping. Authenticated play keeps simulation at the shared fixed tick rate and
renders an interpolated presentation state at the same root convention.

Each development map is baked into versioned collision chunks shared by the
server and client prediction. Authenticated movement therefore collides with
the selected scene's authored BoxColliders. SimulationWorker sends the authoritative collision revision
when the character joins, and the client refuses to predict against a different
revision. Isolated LocalPlayer prefab tests may use Unity CharacterController,
but authenticated World-scene movement requires the joined SimulationWorker
session.

Complex terrain meshes, caves, moving platforms, dynamic doors, and rigid-body
objects are not gameplay features yet. Dynamic server collision already has a
spatial registry, while transform replication and non-box collision formats are
later slices.

## Third-Person Camera

Status: Local foundation implemented

- Mouse movement controls the camera continuously while gameplay input is
  captured.
- Hold the right mouse button to enter the current aim input state.
- F1 releases the cursor for temporary debug UI interaction. F1 captures it again.
- Normal framing uses a right-shoulder offset so the player does not block the
  center of the view.
- The LocalPlayer prefab owns `LocalPlayerCamera`, so other scene cameras can be
  added without becoming the gameplay camera by accident.
- Normal framing uses an explicit vertical offset and a closer follow distance
  to keep the character lower and left of center in a modern shooter composition.
- Aim smoothly tightens the right-shoulder framing and changes camera FOV from
  60 to 45 degrees.
- Camera distance is fixed. Mouse-wheel zoom is not available.
- Camera focus follows a single player CameraTarget height.
- Vertical look is clamped from -50 to 75 degrees.
- Spherecast collision moves the camera in front of walls and restores its
  desired distance after the obstruction clears.
- The collision filter ignores the local player hierarchy.

## Crosshair And World Debug HUD

Status: Gameplay contract implemented with temporary UI presentation

- An unequipped player sees a small white dot at screen center.
- Crosshair definitions support dot and cross shapes, size, thickness, gap,
  color, and runtime spread.
- Future weapon equipment can replace the active definition without changing
  camera or input code.
- The temporary World Client Debug panel is scrollable and anchored to the
  bottom-left corner.
- F2 hides or restores the World Debug panel.
- During an authenticated simulation session, World Debug shows only information
  available to that client. It includes frame average, p95, and maximum time,
  FPS, Unity memory, connection identity, ping, snapshot age and observed rate,
  estimated missing snapshot sequences, payload rates, known entities, pending
  predicted inputs, and reconciliation statistics.
- SimulationWorker CPU, memory, total population, bot population, capacity, and
  internal tick timing remain server-side operational data and are not sent to
  gameplay clients.
- The crosshair is hidden while F1 has released the cursor.

## Carry Weight And Encumbrance

Status: Implemented authority and transport foundation

Every accepted world session receives the character's authoritative unitless
carried weight, capacity, and item-state revision. Base capacity is `200`, and an
equipped Bag can add its authored capacity bonus. Permanent inventory,
Bag contents, carried empty Bags, and Secure Container contents contribute to
weight. Items assigned to equipment slots, including the equipped Bag item
itself, do not. The equipped Bag's capacity bonus still applies. Bank, Recovery
Storage, and corpse custody do not contribute.

Movement remains at full speed through 100 percent capacity. Sprint is allowed
at exactly 100 percent and disabled above it. Above 100 percent, walk and sprint
base speed use the same linear multiplier until movement reaches `0.20` at the
140 percent hard cap. Durable weight-increasing item operations that would
exceed that cap are rejected atomically.

AuthService owns the persistent tuple. SimulationWorker owns its movement
effect, and Unity predicts with the same shared GameSimulation rules. Join and
reconnect restore one fenced committed revision. Later revisions advance over
the reliable control path. The F2 panel shows the current weight, capacity,
revision, movement percentage, and sprint eligibility.

Active characters now have one authoritative in-world mutation path for
relocation, equip, unequip, stack split and merge, allowed destruction, Secure
Container access, ordinary occupied-slot swap, and complete Recovery Storage
claims. Bank and Recovery
Storage require proximity to their configured worker service points. Secure
Container access has no city requirement. Only committed AuthService results can
change the live carry tuple, and reconnect restores the same committed revision.

Local Development explicitly enables global Bank and Recovery access so item
flows can be tested anywhere on the map. Production keeps the configured
service-point requirement, and insurance access is never included in the
Development override.

Unity now exposes the first player-facing inventory loop. Press `B` in
an active World scene for character storage only, `C` for equipment together
with character storage, or `I` for the complete Development view including Bank
and Recovery.
Drag items to valid container or equipment destinations. Dropping onto an
occupied compatible container slot merges compatible stacks and otherwise
submits one atomic swap. Click selection is retained only for split and
allowed-destroy actions. All mutations use the existing authoritative
SimulationWorker and AuthService path. The panel renders only refreshed
committed snapshots and never pretends that a pending mutation has completed.
See the manual and automated checks in [Local Development](LOCAL_DEVELOPMENT.md).

## Player Inventory Foundation

Status: Functional MVP foundation with temporary presentation

The inventory panel keeps equipment on the left. Bank or Recovery Storage uses
the upper-right context area, while Permanent inventory, equipped Bag contents,
and Secure Container stay visible in the lower-right. Bank and Recovery may be
inspected globally for the owning character. Mutations still require a joined
simulation session. Production validates live city-service access, while the
explicit Development configuration permits Bank and Recovery mutations from any
authoritative player position.

The header shows authoritative weight, capacity, load percentage, movement
multiplier, sprint eligibility, and item-state revision. General and specialized
Bag slots are visibly distinct. Local definition lookups disable obvious invalid
equipment, tag, Secure Container, non-empty Bag, stack, destroy, and hard-cap
targets, but the server remains final authority.

The client loads the gameplay and presentation catalogs once through its
persistent bootstrap. Stable definition ids resolve fallback names,
localization keys, optional icons, and optional prefab presentation keys
locally. A source or server catalog revision mismatch reports that a client
update is required and does not render stale item state.

`B` toggles character storage, `C` toggles equipment plus character storage, and
`I` toggles the complete Development view. Pressing a different inventory key
switches the open view, and `Escape` closes it. Shooter pointer capture is
released while the panel is open. Each window is a separate fixed module root,
so opening Equipment or Context never moves Character Inventory. The uGUI
visuals are deliberately temporary, but catalog, snapshot, revision,
operation-id, error, refresh, reconnect, and authority handling are permanent
foundations.

Nearby durable corpses use a generic replaceable capsule presentation. Press
`E` within three metres to open the nearest corpse, or use the Corpse tab in the
complete Development view. The contextual module shows general inventory,
equipment, and Bag contents only after a complete authoritative snapshot has
arrived. Drag full items into Permanent inventory, the equipped Bag, or Secure
Container, or drag carried items back into compatible corpse slots. Enable the
partial-stack control before dragging to request a specific quantity in either
direction. Items can also be dragged between empty or occupied slots in any
corpse section. Compatible stacks with remaining capacity merge. Dropping a
complete item that cannot merge onto an occupied slot swaps the two items only
when both original slots accept the opposite item and the result stays within
the hard cap. Every corpse equipment slot displays its equipment type and
canonical id, and accepts only a compatible definition. Dropping either equipped
Bag onto the other occupied Bag slot submits one atomic aggregate swap when both
complete Bags satisfy the rules. Bank, Recovery Storage, and ordinary equipped
items cannot enter corpse custody. The panel never moves either side
optimistically. Custody-changing transfers refresh character inventory to the
committed revision, while pure corpse rearrangement updates only corpse state.

## Durable Player Death Foundation

Status: Durable and interactive foundation implemented, live combat activation pending

An authoritative player death can now be committed as one idempotent durable
transaction. Currency and Secure Container contents stay with the character.
Protected items and otherwise-lootable insured items enter Recovery Storage,
while the remaining permanent inventory, equipment, Bag, and independently
evaluated Bag children enter a five-minute PostgreSQL corpse. Insurance is
consumed only when it actually protects an item. A protected item does not reveal
an item placeholder to a future looter.

Death cannot be rejected because removing the equipped Bag bonus leaves retained
Secure Container weight above 140 percent. This involuntary state is durable,
but subsequent item operations may neither increase weight nor worsen the exact
load ratio until the character is within the hard cap. Any further weight
increase or ratio degradation is rejected.

Player corpses retain their Shard, transform, source name, generic presentation
key, three item sections, snapshots, revisions, database creation time, and one
absolute five-minute deadline. They remain through the deadline even when empty.
A restarted SimulationWorker restores only its own open and unexpired Shard
corpses. Expiry destroys each remaining item once with durable audit.

The interactive corpse loop is available for existing durable corpses.
SimulationWorker restores their presentation, advertises nearby corpses,
enforces three-dimensional proximity and lifetime, and permits multiple players
to inspect the same corpse. Full and partial item transfers, corpse-internal
moves, compatible stack merges, ordinary occupied-slot swaps, typed corpse
equipment destinations, and Bag aggregate swaps commit through AuthService.
Every viewer receives committed state, while a stale or losing request gets a
stable refreshable result. The dead character competes under the same rules as
every other player.

The project still has no combat death producer. Development testing creates a
real durable corpse from an offline source character through `Shooter MMO >
Tools > Inventory Item Grants`. The generic capsule and current uGUI are
temporary visuals over permanent protocol, state, authority, transaction, and
revision foundations.

## World Actors And Interaction

Status: Phase 12 foundation implemented with temporary presentation

The shared player-facing foundation for NPCs, Mobs, corpses, and future world
interactables is implemented:

- `NPC` identifies a social or service actor with freely composable dialogue,
  vendor, quest, crafting, insurance, trainer, bank, and Recovery capabilities.
- `Mob` identifies a combat actor with later AI, aggro, combat, loot, corpse,
  and respawn behavior.
- The player points the crosshair at a registered target and presses `E`.
  Pressing `E` again closes the active NPC or corpse interaction.
- Unity may discover and prompt for targets within `6.0` metres using a direct
  ray and `0.15` metre spherecast tolerance.
- SimulationWorker opens an interaction only within the authoritative `3.0`
  metre start range after validating session, Shard, target identity and
  revision, active state, line of sight, capability, and request limits.
- An open interaction remains valid through `3.5` metres to avoid boundary
  flicker, but the server revalidates every later action.
- One player may hold one active interaction, including a corpse view. Multiple
  players may interact with the same target independently.
- The temporary uGUI lists only the capability summary returned by the server.
  Permanent actor, target, operation, revision, interaction-session, and
  reconnect state remains outside that panel.
- Corpse targeting joins the shared crosshair selection UX and interaction
  lease without replacing the existing authoritative corpse view and mutation
  flow.

All city NPCs, including guards, are invulnerable in the first version. Faction
and disposition own friendly or hostile behavior independently from NPC or Mob
kind. The checked-in World contains one composable service NPC, one guard NPC,
two Mob definitions, and five deterministic actor instances. Capability buttons
whose business systems belong to later phases return an explicit deferred
server result and never fabricate success. Vendor transactions, quest
progression, crafting, combat AI, damage, death-event production, and Mob
loot-table generation remain unimplemented. Phase 14 provides the downstream
live or durable Mob corpse lifecycle once an authoritative producer supplies a
death event and resolved loot seeds. Phase 15 changes no player-facing rule. It
hardens the same flows with bounded work, retention cleanup, operational
measurements, concurrency scenarios, and a real `100` bot movement baseline.

## Planned Feature Categories

These categories are defined by the project direction but are not implemented.
They remain in the MVP specification until working behavior is available:

- Dynamic collision transform replication.
- Terrain and cave collision beyond the current oriented-box format.
- Shooter combat, weapons, damage, death, and respawning.
- Final inventory art, interaction polish, accessibility, item policy details,
  and loot presentation.
- Live combat death production, final corpse art and loot presentation,
  Mob loot-table generation, and insurance or quest UI polish.
- Gathering, crafting, professions, and player economy.
- Complete NPC capability behavior, Mob AI, quests, events, and world
  activities.
- Character progression and long-term persistence.
- Zone partitioning, cross-zone handoff, and population layers.
- Social, grouping, guild, chat, and trading systems.
- Final UI, audio, visual effects, animation, and accessibility.

The neutral item catalog, pure Phase 1 rules, Phase 2 Unity authoring, Phase 3
PostgreSQL foundation, Phase 4 authenticated catalog and owned-character reads,
Phase 5 internal transaction kernel, Phase 6 policy-safe offline account APIs,
Phase 7 shared live encumbrance, Phase 8 active-character mutation, Phase 9
Unity inventory foundation, Phase 10 durable player-death partition and corpse
persistence, Phase 11 concurrent corpse interaction, and Phase 12 world actors
and authoritative interaction now exist. Phase 13 insurance and quest item
lifecycle, Phase 14 Mob corpse variants, and Phase 15 operational hardening are
also complete.
AuthService supports owned
item-state, bank, Secure Container, and Recovery access plus offline relocation,
split, merge, allowed destruction, Recovery claim, and tier-change operations.
Internal policy and quest services apply auditable lineage without adding a
gameplay quest or insurance NPC operation. SimulationWorker now serializes
reliable item intents, validates live service access, uses the exact-session
service boundary, and advances encumbrance only from a committed result. These
foundations now feed one persistent Unity view of the authoritative item
collection and its temporary player controls.
The planned item rules are defined in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md). They are
connected to the Phase 9 Unity inventory presentation.

The implemented inventory presentation keeps character equipment on the left. The
right side is split with contextual containers such as bank, corpse, Recovery
Storage, or world loot above the character inventory. Permanent inventory,
equipped Bag contents, and Secure Container access remain in the lower-right
area while another container is open.

Item icons and other visual metadata are client-owned presentation assets keyed
by stable definition id. The bundled presentation catalog records the exact
gameplay source revision and its own deterministic presentation revision. Unity
validates and caches this catalog once per matching revision, then inventory,
Bank, and Recovery Storage views reuse local lookups for state received from the
server. The corpse context consumes protocol-v11 presence, complete snapshots,
targeted deltas, operation results, and stable closure messages. World-loot
keeps only its prepared adapter identity until a later authoritative phase.

## Feature Documentation Template

When a game feature is implemented, document it here with:

1. A clear status such as Foundation, MVP, or Production-ready. Use Prototype
   only for temporary UI presentation.
2. The player-visible behavior.
3. Inputs, rules, and important edge cases.
4. Whether the behavior is local, server-authoritative, or persistent.
5. Known limitations.
6. A link to manual test steps in [Local Development](LOCAL_DEVELOPMENT.md).

# Game Features

Last updated: 2026-10-16

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
placement and join-ticket flow. GameService selects the assigned healthy worker
runtime and returns its endpoint only with the ticket. The client validates the
placement, connects with LiteNetLib, sends the ticket through the shared
versioned protocol, and loads the World scene mapped from the accepted WorldId
only after SimulationWorker accepts the session. The selected World scene
displays character, shard, World, worker, runtime, session, and UDP connection
state.

Leave sends the exact simulation-session id over the active peer and waits for server
acknowledgement before returning to CharacterSelect. Unexpected disconnects
clear shard state and return an authenticated player to CharacterSelect. HTTP
401 from GameService still clears account state and returns the player to
LoginMenu.

## World Preview

Status: Initial content on implemented network gameplay foundation

DevelopmentWorld1 contains the original 30 by 30 meter scene-authored test map.
DevelopmentWorld2 provides a 512 by 512 meter greybox map with a central service
settlement, a northern traversal course, an eastern sightline lane, southwest
wilderness, and a reserved southeast expansion pad. It includes visual-only
guides for canonical actor coordinates while actor content remains owned by
WorldData. Each registered World scene owns a LocalPlayer prefab
reference, an explicit spawn point, and a separate entity presentation root.
After SimulationWorker accepts a character join, the mapped scene creates one
runtime LocalPlayer instance whose prefab owns the orbit camera. Without an active joined session, no local player exists in the scene.
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

Status: Server-authoritative click-to-move implemented

- Left-click the ground to walk there. The client plans a route around walls,
  buildings, and cover on a fine internal grid that the player never sees, and
  the character starts walking on the next simulation tick.
- A click on a spot that cannot be reached, such as a wall face or a roof, walks
  to the closest reachable point on open ground. A point under a roof, bridge,
  or mountain that is lower than the click, such as a tunnel floor right below a
  clicked mountainside, is used only when no open point is reachable. A click
  more than 64 meters away walks toward it as far as the search reaches.
- A new click replaces the current route at once.
- Route planning runs on a background thread, so a long search never stalls a
  frame. The previous route continues until the new one is ready.
- The character walks at one speed, 5 meters per second scaled by carry weight,
  and turns toward its direction of travel. It follows ramps and steps up to the
  step height, and it falls when it walks off a higher ledge. An airborne
  character keeps its momentum until it lands.
- A route that collision blocks for half a second ends, so the character never
  keeps pushing against a wall.
- A small yellow disc marks the destination until the character arrives.
- Clicks and scrolling over interface panels never reach the world.
- Keyboard movement, sprint, jump, and aim no longer exist. Running with run
  energy is planned in [Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md).
- Movement uses a PlayerInput-owned Unity Input Actions asset.
- The local player predicts each fixed input tick immediately. A separate local
  presentation state interpolates those predictions at the render frame rate so
  the player and camera do not move in 30 Hz steps.
- SimulationWorker owns the accepted position, velocity, facing, grounded state,
  and route progress. It follows the same route waypoints as the client and
  checks only their limits, so the collision stays authoritative.
- SimulationWorker resolves the player capsule against the baked ground,
  boundaries, buildings, ramps, steps, and cover.
- Walkable slopes up to the configured 45 degree limit hold a grounded character
  in place when it stands. Steeper surfaces are not treated as ground.
- The simulation capsule uses a slope-aware support height to avoid penetration
  correction along the slope. Presentation removes only that geometric offset,
  preserving render-frame interpolation instead of replacing it with the latest
  surface height.
- Grounded transitions over configured steps and small drops use an additional
  100 ms vertical presentation blend. Airborne movement, steep surfaces,
  teleports, and large corrections bypass this blend.
- Authoritative snapshots acknowledge processed input sequences and route
  progress. The client replays remaining input and smooths small corrections.
- Remote players advance on an adaptive frame-rate render clock through a
  snapshot buffer approximately 100 ms behind server time. Bounded clock
  correction restores that delay after a network stall instead of accumulating
  permanent latency.
- A route continues on SimulationWorker when input packets are briefly lost,
  because every route has an end.

Gamepad bindings and player-configurable rebinding are not implemented yet.

Movement speed, gravity, terminal fall speed, facing, capsule dimensions, slope
and step rules, route limits, authored collision, and world bounds are validated
and simulated by SimulationWorker.
The active UDP connection carries sequenced input with the route of the newest
click and periodic movement snapshots in addition to session control.
The character root is the shared logical ground point for players and NPCs.
`CharacterBody` configures the CharacterController contact envelope around that
point, including skin width, without moving the presentation hierarchy. Visual
assets use a feet-at-zero convention below PlayerVisual, and spawn placement
aligns the character root directly to the spawn point. Authenticated play keeps
simulation at the shared fixed tick rate and renders an interpolated
presentation state at the same root convention.

Each development map is baked into versioned collision chunks shared by the
server and client prediction. Authenticated movement and route planning
therefore use the selected scene's authored boxes, meshes, and terrain. SimulationWorker sends
the authoritative collision revision when the character joins, and the client
refuses to predict against a different revision. The LocalPlayer prefab moves
only in an authenticated World-scene session and has no offline movement mode.

The Development World 2 traversal course is walkable with click-to-move. Its
ramps end flush with the raised platforms, and the observation steps climb in
rises below the step height to a bridge that leads onto the observation deck.

Terrain works with click-to-move. In the Development World 2 highlands the
player walks over hills and up a mountain's walkable face, is stopped by cliffs
steeper than 45 degrees, and walks through a tunnel below the mountain. Rock
paint marks the ground that is too steep to climb. Moving platforms, dynamic
doors, and rigid-body objects are not gameplay features yet. Dynamic server
collision already has a spatial registry, while transform replication is a
later slice.

## Orbit Camera

Status: Local foundation implemented

- The camera orbits the player from behind and above. Drag with the middle mouse
  button to turn and tilt it, or hold the arrow keys: left and right turn it,
  up and down tilt it.
- The scroll wheel zooms between 3 and 30 meters. Each notch changes the
  distance by 15 percent, and the zoom eases in.
- Tilt is limited to 10 through 80 degrees above the horizon. The camera starts
  12 meters away and 40 degrees above the horizon, behind the player.
- The cursor always stays visible and free.
- The LocalPlayer prefab owns `LocalPlayerCamera`, so other scene cameras can be
  added without becoming the gameplay camera by accident.
- Camera focus follows a single player CameraTarget height.
- Spherecast collision pulls the camera in front of walls at once and eases it
  back out after the obstruction clears. Hiding roofs and occluding walls is
  planned.
- The collision filter ignores the local player hierarchy.

## World Debug HUD

Status: Temporary UI presentation

- The temporary World Client Debug panel is scrollable and anchored to the
  bottom-left corner.
- F2 hides or restores the World Debug panel.
- During an authenticated simulation session, World Debug shows only information
  available to that client. It includes frame average, p95, and maximum time,
  FPS, Unity memory, connection identity, ping, snapshot age and observed rate,
  estimated missing snapshot sequences, payload rates, known entities, pending
  predicted inputs, reconciliation statistics, the current route and waypoint,
  and the latest route plan with its search size and time.
- SimulationWorker CPU, memory, total population, bot population, capacity, and
  internal tick timing remain server-side operational data and are not sent to
  gameplay clients.

## Carry Weight And Encumbrance

Status: Implemented authority and transport foundation

Every accepted world session receives the character's authoritative unitless
carried weight, capacity, and item-state revision. Base capacity is `2000`, set in
catalog content, and an equipped Bag can add its authored capacity bonus. Permanent inventory,
Bag contents, carried empty Bags, and Secure Container contents contribute to
weight. Items assigned to equipment slots, including the equipped Bag item
itself, do not. The equipped Bag's capacity bonus still applies. Bank, Recovery
Storage, and corpse custody do not contribute.

Movement remains at full speed through 100 percent capacity. Above 100 percent,
walking speed uses a linear multiplier until movement reaches `0.20` at the
140 percent hard cap. Durable weight-increasing item operations that would
exceed that cap are rejected atomically.

GameService owns the persistent tuple. SimulationWorker owns its movement
effect, and Unity predicts with the same shared GameSimulation rules. Join and
reconnect restore one fenced committed revision. Later revisions advance over
the reliable control path. The F2 panel shows the current weight, capacity,
revision, and movement percentage.

Active characters now have one authoritative in-world mutation path for
relocation, equip, unequip, stack split and merge, allowed destruction, Secure
Container access, ordinary occupied-slot swap, and complete Recovery Storage
claims. Bank and Recovery Storage require an open interaction with an NPC that
offers them, such as Mira the Quartermaster. Walking out of range or losing line
of sight closes the interaction and ends the access. Secure Container access has
no city requirement. Only committed GameService results can
change the live carry tuple, and reconnect restores the same committed revision.

Local Development explicitly enables global Bank and Recovery access so item
flows can be tested anywhere on the map. Production keeps the NPC interaction
requirement, and insurance access is never included in the Development override.

Unity now exposes the first player-facing inventory loop. Press `B` in
an active World scene for character storage only, `C` for equipment together
with character storage, or `I` for the complete Development view including Bank
and Recovery.
Drag items to valid container or equipment destinations. Dropping onto an
occupied compatible container slot merges compatible stacks and otherwise
submits one atomic swap. Click selection is retained only for split and
allowed-destroy actions. The ammunition slot holds one stack up to its
slot limit. Dropping a compatible stack onto the equipped stack tops it up, and
`Split to Target` from the equipped stack moves part of it into an empty slot.
An item cannot be equipped into a slot that an equipped item blocks, such as
the off hand next to a two-handed bow, or while it would block an occupied
slot. An item whose level requirement is above the character's level cannot be
equipped either; every character is level 1 and every shipped item requires
level 1 until experience exists. All mutations use the existing authoritative
SimulationWorker and GameService path. The panel renders only refreshed
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
multiplier, and item-state revision. General and specialized
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
switches the open view, and `Escape` closes it. Clicks on the panel never move
the character. Each window is a separate fixed module root,
so opening Equipment or Context never moves Character Inventory. The uGUI
visuals are deliberately temporary, but catalog, snapshot, revision,
operation-id, error, refresh, reconnect, and authority handling are permanent
foundations.

Nearby durable corpses use a generic replaceable capsule presentation. Click a
corpse to walk to it and open it. An open corpse always shows in the complete
view, which the panel switches to; switching to `B` or `C` closes the corpse.
The Corpse tab lists nearby corpses but cannot open them. The contextual module
shows general inventory, equipment, and Bag contents only after a complete
authoritative snapshot has arrived. Drag full items into Permanent inventory,
the equipped Bag, or Secure Container, or drag carried items back into
compatible corpse slots. Enable the
partial-stack control before dragging to request a specific quantity in either
direction. Items can also be dragged between empty or occupied slots in any
corpse section. Compatible stacks with remaining capacity merge. Dropping a
complete item that cannot merge onto an occupied slot swaps the two items only
when both original slots accept the opposite item and the result stays within
the hard cap. Every corpse equipment slot displays its equipment type and
canonical id, and accepts only a compatible definition up to its equipment slot
limit. Dropping either equipped
Bag onto the other occupied Bag slot submits one atomic aggregate swap when both
complete Bags satisfy the rules. Bank, Recovery Storage, and ordinary equipped
items cannot enter corpse custody. The panel never moves either side
optimistically. Custody-changing transfers refresh character inventory to the
committed revision, while pure corpse rearrangement updates only corpse state.

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
server. The corpse context consumes presence, complete snapshots, targeted deltas,
operation results, and stable closure messages. World loot has a reserved
context but no producer yet.

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
increase or ratio degradation is rejected. The character can still join a Shard
in this state and moves at the minimum `0.20` multiplier.

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
equipment destinations, and Bag aggregate swaps commit through GameService.
Every viewer receives committed state, while a stale or losing request gets a
stable refreshable result. The dead character competes under the same rules as
every other player.

The project still has no combat death producer. Development testing creates a
real durable corpse from an offline source character through `MMORPG >
Tools > Inventory Item Grants`. The generic capsule and current uGUI are
temporary visuals over permanent protocol, state, authority, transaction, and
revision foundations.

## World Actors And Interaction

Status: Implemented with temporary presentation

The shared player-facing foundation for NPCs, Mobs, corpses, and future world
interactables is implemented:

- `NPC` identifies a social or service actor with freely composable dialogue,
  vendor, quest, crafting, insurance, trainer, bank, and Recovery capabilities.
- `Mob` identifies a combat actor with later AI, aggro, combat, loot, corpse,
  and respawn behavior.
- The pointer picks a registered target with a direct ray and a `0.15` metre
  spherecast tolerance, up to `100` metres from the camera and never through
  solid geometry. A prompt names the target under the pointer.
- A left click on the target walks the player to `2.75` metres and then asks
  SimulationWorker to open the interaction. A new click elsewhere cancels the
  walk, and `Escape` closes the active NPC or corpse interaction.
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
- Corpse targeting joins the shared pointer selection and interaction
  lease without replacing the existing authoritative corpse view and mutation
  flow.

All city NPCs, including guards, are invulnerable in the first version. Faction
and disposition own friendly or hostile behavior independently from NPC or Mob
kind. The checked-in World contains one composable service NPC, one guard NPC,
two Mob definitions, and five deterministic actor instances. The Character Bank
and Recovery Storage buttons open the inventory with that storage, which stays
usable while the interaction is open, and the insurance and quest offer
capabilities run their item lifecycle. Dialogue, vendor, crafting, trainer, and
quest completion buttons return an explicit deferred server result and never
fabricate success. Vendor transactions, quest progression, crafting, combat AI,
damage, death-event production, and Mob loot-table generation remain
unimplemented. The live and durable Mob corpse lifecycle is ready for an
authoritative producer that supplies a death event and resolved loot seeds.

## Planned Feature Categories

These categories are defined by the project direction but are not implemented.
They remain in the MVP specification until working behavior is available:

- Dynamic collision transform replication.
- Combat, weapons, damage, death, and respawning.
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

## Feature Documentation Template

When a game feature is implemented, document it here with:

1. A clear status such as Foundation, MVP, or Production-ready. Use Prototype
   only for temporary UI presentation.
2. The player-visible behavior.
3. Inputs, rules, and important edge cases.
4. Whether the behavior is local, server-authoritative, or persistent.
5. Known limitations.
6. A link to manual test steps in [Local Development](LOCAL_DEVELOPMENT.md).

# Game Features

Last updated: 2026-10-21

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

Two development Worlds exist, both readable greyboxes with a meter grid on the
ground, signs that name every zone and test station, and floor labels with the
measured values. [Development Worlds](DEVELOPMENT_WORLDS.md) has their full
layouts.

- DevelopmentWorld2, the test World, is 384 by 384 meters with a walled town in
  the center and one test zone in each direction: a movement course with step,
  slope, and gap stations just inside and outside the movement limits, stairs,
  a bridge, a maze, and camera tests; the highlands terrain with a peak, cliffs,
  and a tunnel; a combat range with a PvP arena and a range lane; an interest
  road to an outpost beyond the interest radius; the wolf woods with the Feral
  Alpha's lair; a quarry with seven Iron Veins; an open meadow; and a sandbox
  reserved for new features. Green stations get the character through, red
  ones must not.
- DevelopmentWorld1, the stress arena, is a 64 by 64 meter walled floor that
  keeps every player, bot, and actor inside one interest radius, with Mira,
  nine Iron Veins, a wolf pen, and a platform around an open spawn.

Each registered World scene owns a LocalPlayer prefab reference, an explicit
spawn point, and a separate entity presentation root. After SimulationWorker
accepts a character join, the mapped scene creates one runtime LocalPlayer
instance whose prefab owns the orbit camera. Without an active joined session,
no local player exists in the scene.

The test shard has no durable gameplay simulation yet. During authenticated play, other
connected characters are represented by an authored RemotePlayer prefab and
rendered from interpolated SimulationWorker snapshots. Every player, the local
one included, shows the male human body, which stands, walks, and jogs with the
speed it moves at; the body type at character creation is planned. With the
World Debug setting `Tint bodies` on, off by default, the local player is tinted
purple, other players orange, and synthetic development bots blue. SimulationWorker assigns each live
player a network entity id. Reliable spawn and despawn messages decide when a
remote player exists, while unreliable snapshots update only its movement.
Remote instances are kept under the active World scene's separate
`EntityPresentationRoot`, outside the runtime LocalPlayer hierarchy.
SimulationWorker uses distance-based interest management, so distant entities are
removed reliably and recreated reliably when they enter the configured area of
interest. Snapshot loss never decides whether a remote player exists.

## World Menu

Status: Implemented with temporary presentation

Every world action goes through one menu, as in RuneScape:

- A right click opens a menu with every option for what is under the pointer,
  headed `Choose Option` and ending with `Cancel`. A left click runs the top
  option at once, without opening the menu.
- The menu lists every target under the pointer, nearest first, and never
  targets hidden behind solid geometry. Each target's actions come first, then
  `Walk here`, then `Examine` for each target. A wolf in front of an Iron Vein
  gives `Attack Feral Wolf`, `Mine Iron Vein`, `Walk here`, `Examine Feral
  Wolf`, `Examine Iron Vein`, and `Cancel`.
- An NPC or resource node offers one option per capability, in the order its
  content lists them, with the verb from the content, such as `Talk-to`,
  `Trade`, `Bank`, `Recover`, `Insure`, `Quests`, `Turn-in`, and `Craft` for Mira
  the Quartermaster or `Mine` for an Iron Vein. The first is the left-click
  action. Choosing one walks into range, opens the interaction, and starts that
  service: `Trade` opens the vendor, `Bank` the Character Bank, `Recover`
  Recovery Storage, and `Mine` starts mining. `Talk-to`, `Insure`, and `Quests`
  open the interaction panel, where their choices are. When the interaction is
  already open, the service starts without walking.
- A living Mob offers `Attack`, and so does another character when the Shard
  allows player versus player. A Mob walking home after a fight offers no
  `Attack`, so a left click on it walks there.
- A corpse offers `Loot Corpse of <name>`.
- `Walk here` is always offered and walks to the ground under the pointer.
- `Examine` shows the target's short description from the actor content in the
  message area in the bottom left corner, such as "A lean, hungry wolf. It eyes
  you like a meal." A corpse shows "The remains of <name>." Messages fade out
  after 12 seconds. A target the player cannot reach also leaves a message
  there.
- Target names are colored by kind: NPCs and Mobs yellow, objects such as
  resource nodes cyan, corpses orange, and other characters white.
- Next to the pointer, the left-click option is shown with the number of other
  options, such as `Attack Feral Wolf / 3 more options`. Bare ground shows
  nothing.
- The menu closes when an option or `Cancel` is chosen, on a click elsewhere in
  the world, on `Escape`, or when the pointer moves more than a short distance
  away from it. An `Escape` that closes the menu does nothing else: the attack,
  the open interaction, and the inventory stay as they are.
- Options run against the world as it is when they are chosen, so an option for
  a target that left or died in the meantime does nothing and ends nothing
  else, such as an attack.
- While the character is dead the menu offers nothing.
- Every option except `Examine` ends an attack, as walking away does.

Capability availability is not known before an interaction opens, so the menu
lists every capability, and a capability that is not available reports that in
the interaction panel. SimulationWorker still validates range, line of sight,
revision, session, and availability for every request.

## Player Movement

Status: Server-authoritative click-to-move implemented

- Left-click the ground, or choose `Walk here` in the [World Menu](#world-menu),
  to walk there. The client plans a route around walls,
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
- Remote players advance on an adaptive frame-rate render clock two snapshot
  intervals, about 133 ms, behind an estimate of server time that advances
  smoothly between snapshots. They walk at an even speed instead of speeding up
  and slowing down with every snapshot. Bounded clock correction restores that
  delay after a network stall instead of accumulating permanent latency.
- SimulationWorker moves the character exactly one step per input, like the
  prediction, so uneven packet timing no longer corrects the local player back
  and forth. A route continues for about 100 ms when inputs are late; after a
  longer gap the character waits on the worker until the inputs arrive and then
  catches up.
- Health bars and damage numbers are placed after the camera followed the
  player, so they stay over the heads they belong to.

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

The Development World 2 movement course shows each limit with click-to-move:
steps up to 0.34 m and slopes up to 42 degrees are climbed, while 0.36 m steps
and 48 degree slopes are not; gaps of 1.5 and 1.0 m are walked through and a
0.6 m gap is walked around. Routes never step off a ledge higher than 0.4 m, so
from a raised platform they take the stairs down. Ramps and stairs lead onto
platforms joined by a bridge that can be walked over and under.

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
- The Presentation section has the setting `Tint bodies: you purple, players
  orange, bots blue`, off by default and kept between sessions.
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
`Split` from the equipped stack moves part of it into an empty slot.
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

The inventory panel has three columns. Equipment is a paper doll on the left.
Bank, Recovery Storage, a vendor, or a corpse uses the middle context column,
while Permanent inventory, equipped Bag contents, and the Secure Container, shown
as Warded Pouch, stay visible in the right column. Bank and Recovery may be
inspected globally for the owning character. Mutations still require a joined
simulation session. Production validates live city-service access, while the
explicit Development configuration permits Bank and Recovery mutations from any
authoritative player position.

A carry meter shows authoritative weight, capacity, load percentage, and
movement speed, from green through amber when slowed to red at the 140 percent
hard cap. The character header shows the Gold balance and the item-state
revision. Every slot has the same size. Occupied slots show the item name, a
category color, the stack
quantity, and a protection or insurance badge, and empty slots are darker.
Specialized Bag slots show the item types they accept, and an equipment slot
that an equipped two-handed item blocks is marked Blocked. Hovering a slot
shows a tooltip with the item details. Local definition lookups disable obvious invalid
equipment, tag, Secure Container, non-empty Bag, stack, destroy, and hard-cap
targets, but the server remains final authority.

Moving, swapping, merging, splitting, equipping, unequipping, and destroying
items react at once: the item lands where you drop it before the server answers,
and you can keep dragging while up to eight moves wait. The server still decides
every move. When it rejects one, every waiting move is undone, the items jump
back and flash red, and the status line says `Item move undone:` with the
reason. A split stack can be moved once the server has confirmed it, a fraction
of a second later. A Recovery claim, a Bag that moves with its contents, corpse
loot, and vendor trades still wait for the server, but their results, like every
committed change, update the inventory directly instead of reloading it.

The client loads the gameplay and presentation catalogs once through its
persistent bootstrap. Stable definition ids resolve fallback names,
localization keys, optional icons, and optional prefab presentation keys
locally. A source or server catalog revision mismatch reports that a client
update is required and does not render stale item state.

`B` toggles character storage, `C` toggles equipment plus character storage, and
`I` toggles the complete Development view. Pressing a different inventory key
switches the open view, and `Escape` closes it. Clicks on the panel never move
the character. Each window is a separate fixed module root,
so opening Equipment or Context never moves the character column. The uGUI
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
accepts only a compatible definition up to its equipment slot limit. Dropping either equipped
Bag onto the other occupied Bag slot submits one atomic aggregate swap when both
complete Bags satisfy the rules. Drag a whole corpse item onto a compatible
equipment slot to equip it straight from the corpse: an empty slot equips it,
equipped arrows of the same type are topped up, and an occupied slot swaps your
equipped item into the corpse slot the loot came from. A live Mob corpse, such
as a Feral Wolf's, cannot take an item back, so its loot fills an empty slot or
tops up a stack of the same item in the inventory or the equipment, but never
swaps. Bank and Recovery Storage items cannot enter corpse
custody, and an ordinary equipped item enters it only through that swap. The panel never moves either side
of a corpse transfer ahead of the server. A custody-changing transfer updates
the character inventory from the committed change, or reloads it when the change
cannot be described, while pure corpse rearrangement updates only corpse state.

A live Mob corpse closes for every viewer once its last item is taken, and any
corpse closes when it expires. The Corpse context then names the reason in
neutral text, such as `The corpse is empty.` or `The corpse expired and is no
longer available.`, and the same applies when the viewer dies. Closing the view
yourself leaves no message. Only a corpse that ended through an error, such as
`corpse_invalidated` when its state could no longer be trusted, shows a red
error.

The implemented inventory presentation keeps character equipment on the left,
contextual containers such as bank, corpse, Recovery Storage, or world loot in
the middle, and the character inventory on the right. Permanent inventory,
equipped Bag contents, and Secure Container access remain in the right column
while another container is open.

Item icons and other visual metadata are client-owned presentation assets keyed
by stable definition id. The bundled presentation catalog records the exact
gameplay source revision and its own deterministic presentation revision. Unity
validates and caches this catalog once per matching revision, then inventory,
Bank, and Recovery Storage views reuse local lookups for state received from the
server. The corpse context consumes presence, complete snapshots, targeted deltas,
operation results, and stable closure messages. World loot has a reserved
context but no producer yet.

## Durable Player Death Foundation

Status: Implemented, with combat deaths since protocol 18

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

[Combat](#combat) produces these deaths. Development testing can also create
a real durable corpse from an offline source character through `MMORPG >
Tools > Inventory Item Grants`. The generic capsule and current uGUI are
temporary visuals over permanent protocol, state, authority, transaction, and
revision foundations.

## World Actors And Interaction

Status: Implemented with temporary presentation

The shared player-facing foundation for NPCs, Mobs, corpses, and future world
interactables is implemented:

- `NPC` identifies a social or service actor with freely composable dialogue,
  vendor, quest, crafting, insurance, trainer, bank, and Recovery capabilities.
- `Mob` identifies a combat actor. A left click on a Mob attacks it, an
  aggressive Mob attacks characters it notices, and a dead Mob leaves loot in
  its corpse and respawns.
- A resource node identifies a gatherable world object, such as an Iron Vein,
  with exactly one gathering capability. See [Gathering](#gathering).
- The pointer picks registered targets with a direct ray and a `0.15` metre
  spherecast tolerance, up to `100` metres from the camera and never through
  solid geometry. The [World Menu](#world-menu) offers their options.
- Choosing an option of a target walks the player to `2.5` metres and asks
  SimulationWorker to open the interaction once it is within `2.75` metres, and
  the interaction panel says `Walking to <name>...` meanwhile. A new option
  elsewhere cancels the walk, and `Escape` closes the active NPC or corpse
  interaction.
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

All city NPCs, including guards, and all resource nodes are invulnerable in the
first version. Faction and disposition own friendly or hostile behavior
independently from actor kind. The checked-in actor catalog contains one
composable service NPC, one guard NPC, two Mob definitions, and the Iron Vein
resource node, each with an examine text and a menu option per capability.
Development World 1 places 14 deterministic actor instances and
Development World 2 17. The Character Bank and Recovery Storage buttons open
the inventory with that storage, and `Trade` opens it with the vendor; each stays
usable while the interaction is open. The insurance and quest offer
capabilities run their item lifecycle. Dialogue, crafting, trainer, and quest
completion buttons return an explicit server result and never fabricate
success. Quest progression and crafting remain unimplemented. Combat damages
and kills Mobs, rolls their loot, and fills the live or durable Mob corpse, and
Mobs chase, bite, and walk home.

## Currency And Vendor Trade

Status: MVP implemented with temporary presentation

- Every character has a persistent balance of one currency, Gold. The currency
  name and the starting balance are catalog content. A new character starts with
  `100` Gold, and existing characters keep their balance. The inventory's
  character header shows the balance, such as `1,250 Gold`, before the item-state
  revision.
- Currency is not an item. It takes no slot and no weight, and it stays with the
  character on death.
- Mira the Quartermaster offers `Trade`, in her right-click menu and in her
  interaction panel. Choosing it opens the complete inventory view on the
  `Vendor` tab with the General Store and the current balance. Trading works
  only while the interaction stays open; walking out of range closes it.
- The General Store sells Bronze Arrow for `1`, Minor Healing Potion for `25`,
  Cooked Trout for `10`, and Starter Pickaxe for `50` Gold, at 100 percent of
  each item's value, with unlimited stock. Each offer has a quantity field and a
  button that names the total, such as `Buy for 50 Gold`.
- A purchase goes into carried storage. It first tops up existing stacks, then
  fills free slots: matching specialized Bag slots, Permanent inventory, and
  general Bag slots, never the Secure Container. A purchase that does not fit,
  that the balance cannot pay, or that would pass the 140 percent hard cap is
  rejected without a partial result.
- To sell, drag a carried item from Permanent inventory, the equipped Bag, or
  the Secure Container onto the `Sell` slot, choose a quantity, which defaults to
  the whole stack, and confirm `Sell <n> <item> for <total>`. The vendor pays 40
  percent of the value, rounded down on the total, and part of a stack can be
  sold. A sale worth 0 Gold after rounding is refused. Equipped and banked items, protected and insured items, quest items,
  non-empty Bags, and items without a value are refused. There is no buyback:
  a sold item is destroyed with an audit record.
- Prices are fixed. The client shows the price it expects, and GameService
  rejects the trade if the price no longer matches. Client checks of price,
  balance, quantity, location, policies, and weight are advisory only.
- A purchase or sale is one idempotent GameService transaction that changes the
  balance and the items together and writes a currency ledger row, so trades
  are persistent and auditable.

Known limitations: one vendor with one profile, no buyback, no stock limits, no
player trading, and temporary uGUI. Manual test steps are in
[Local Development](LOCAL_DEVELOPMENT.md#vendor-and-gathering-verification).

## Gathering

Status: MVP implemented with temporary presentation

- Iron Veins are resource nodes. Development World 2 has seven in the quarry
  southeast of the town, and Development World 1 has nine east of the spawn.
- An Iron Vein is the game's first authored 3D model: grey faceted rocks with
  chunky rust-orange iron ore that shows from every camera angle. A depleted
  vein keeps its rocks, now with broken flat tops and rubble around them, but
  without ore.
- Equip a gathering tool, such as the Starter Pickaxe, in the `Tool` slot and
  left-click a vein, or choose `Mine Iron Vein` in its right-click menu. The
  character walks into range, the interaction opens, and mining starts. Without
  a gathering tool the panel asks the player to equip one.
- One click mines continuously: one Iron Ore every 3 seconds, shown as `Mine...`
  with a progress bar and the gathered count. The ore enters carried storage by
  the same rule as a purchase.
- Mining stops when the vein is depleted, the character moves, the interaction
  closes, carried storage is full, the ore would pass the hard cap, or the tool
  is no longer equipped. The panel names the reason, and `Mine` starts again.
- Veins are shared by every player on the Shard. A vein holds 5 ore. When the
  last one is mined, it turns depleted for every player and everyone mining it
  stops. A depleted vein stays visible but cannot be targeted, and it returns
  after 30 seconds.
- SimulationWorker owns the vein charges, cycle timing, depletion, and respawn
  as live Shard state, so a worker restart restores every vein to full.
  GameService checks the required tool in the `Tool` slot and grants each ore
  transactionally.

Known limitations: one resource type, no Mining experience or level, no success
chance per cycle, and a placeholder vein model. Manual test steps are in
[Local Development](LOCAL_DEVELOPMENT.md#vendor-and-gathering-verification).

## Combat

Status: Implemented with temporary presentation

Combat is target based and server authoritative:

- A left click on a living Mob, or on another character when the Shard allows
  player versus player, or `Attack` in its right-click menu, selects it as the
  target. The character walks into the range of its equipped weapon, about 9 m
  for the Training Shortbow, and attacks on the weapon's cooldown, every 2.4
  seconds for the shortbow, while it stands still. Moving between shots is
  allowed.
- Any other world option except `Examine`, or `Escape`, ends the attack.
- A bow needs arrows of its family in the Ammunition slot. Every shot spends one
  arrow, even when it flies at a target that died, and the inventory shows the
  live arrow count. An equipment change holds attacks for 1 second.
- The top-left frame shows health, arrows, and the attack status, such as `Out
  of range` or `No ammunition`. The target frame shows the target's health,
  damaged combatants get health bars, hits show white damage numbers, and
  arrows fly to their target.
- A character at 0 health dies: `You died` counts down 5 seconds above every
  open window, the character respawns at the World spawn with full health, and
  its items follow the
  [Durable Player Death Foundation](#durable-player-death-foundation). The
  inventory shows the moved items at once, without a new login.
- Out of combat for 10 seconds, a character regains 2 percent of its health
  every second.
- Wolves are aggressive: a wolf notices a living character within 8 m that it
  can see, runs to it at 4.5 m/s, and bites for 4 to 9 every 1.8 seconds. A
  wolf hit by a character always fights back. A wolf that is lured more than 30
  m from where the fight started, or that neither bites nor is hit for 10
  seconds, turns back home, cannot be damaged on the way, and arrives with full
  health. Wolves walk their patrol path between fights.
- A Feral Wolf has 50 health, drops a Wolf Pelt and sometimes Bronze Arrows,
  and returns after 30 seconds. The Feral Alpha boss has 240 health, leaves a
  durable corpse, notices characters within 10 m, bites for 7 to 12 every 2.4
  seconds, and returns after 300 seconds. In Development World 2 five
  wolves live in the wolf woods southwest of the town, X and Z between `-120`
  and `-90`, and the Feral Alpha stands in its lair at `(-160, 0, -160)`. In
  Development World 1 four wolves patrol a pen west of the spawn.

[Combat Verification](LOCAL_DEVELOPMENT.md#combat-verification) walks through
the loop.

## Planned Feature Categories

These categories are defined by the project direction but are not implemented.
They remain in the MVP specification until working behavior is available:

- Dynamic collision transform replication.
- A threat table, Mob abilities and groups, melee and magic weapons, armor, and
  final combat visuals.
- Final inventory art, interaction polish, accessibility, item policy details,
  and loot presentation.
- Final corpse art and loot presentation, and insurance or quest UI polish.
- Crafting, professions, further resource types and gathering progression, and
  the player economy beyond the first NPC vendor.
- Complete NPC capability behavior, quests, events, and world activities.
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

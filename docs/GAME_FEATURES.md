# Game Features

What a player can do and see in the game today: the flows, the controls, the
panels, and the numbers a player notices. Everything here is implemented. Every
panel is temporary development UI over permanent client and server foundations,
while the final custom UI is being designed.

How the client builds these features is in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md), and the server
behavior behind them is in [Service Features](SERVICE_FEATURES.md). The product
direction and what is planned next are in [Game Design](GAME_DESIGN.md), and how
to check each feature by hand is in [Manual Testing](MANUAL_TESTING.md).

## Controls

| Input | Does |
| --- | --- |
| Left click | Runs the top option of the [World Menu](#world-menu) at once: walk, attack, talk, gather, or loot |
| Left button, held after a click that walked | Steers the walk toward the ground under the pointer, see [Player Movement](#player-movement) |
| Right click | Opens the world menu with every option for what is under the pointer |
| Middle mouse button, dragged | Turns and tilts the camera |
| Arrow keys | Left and right turn the camera, up and down tilt it |
| Scroll wheel | Zooms the camera |
| `Escape` | Closes an open world menu and does nothing else; otherwise ends the attack, closes the open interaction and the inventory, and cancels an Admin teleport pick |
| `B` | Opens or closes the inventory with character storage only |
| `C` | Opens or closes the inventory with equipment and character storage |
| `I` | Opens or closes the complete inventory with Bank, Recovery Storage, a corpse, or a vendor |
| `F2` | Opens or closes the [Debug Menu](#debug-menu-f2) |
| `F3` | Opens or closes the [Admin Menu](#admin-menu-f3), whose tools work in the Development environment |

Clicks and scrolling over a panel never reach the world. The cursor always stays
visible and free. There is no keyboard movement, sprint, jump, or aim. Gamepad
bindings and player-configurable rebinding are not implemented.

## Account Entry Flow

- LoginMenu shows one card with a `Log In` and a `Create Account` tab. `Log In`
  takes an email or a username and a password; `Create Account` takes an email,
  a username, and a password, with the rules of each below its field, and logs
  the new account in. The GameService address and the request and realtime
  timeouts show at the bottom of the screen.
- `Log In` starts with the last login name used on this computer and puts the
  caret in the password. In the Editor and in development builds, the fields
  start with the local development account `player_one` and its password.
- `Tab` and `Shift+Tab` move between the fields, and `Enter` sends the open
  tab. A successful request loads CharacterSelect. While a request runs, the
  form is disabled, so a second click or `Enter` cannot send it again. A failed
  request shows its reason in red under the button, with its stable error
  code; when GameService does not answer, the message names its address.
- Only one client session may be active for an account. A login on a second
  client replaces the first one: the first client is disconnected from its
  shard, clears its local account state, and returns to LoginMenu with an
  explanation in the Unity Console.
- When the account session expires or is revoked, the client returns to
  LoginMenu within a few seconds, also outside a World.

## Character Selection

CharacterSelect has a top bar with the signed-in account, `Refresh`, and
`Log Out`; the account's characters on the left, each with its body model and
gold; and on the right the chosen character with the shards and `Enter World`,
or the creation of a new character. A status line at the bottom shows the
result of the last action.

- A click chooses a character or an online shard; an offline shard cannot be
  chosen. Each shard shows its region, fleet, and `Online <players> /
  <capacity>` or `Offline`.
- `Enter World`, `Enter`, or a double click on the chosen character or shard
  enters the World of the chosen shard with the chosen character, as
  [Shard Entry And Exit](#shard-entry-and-exit) describes. Without a character
  or an online shard, a line above the button says what is missing.
- CharacterSelect starts with the character and shard of the last `Enter World`
  on this computer; otherwise it chooses the first character and the first
  online shard.
- `New Character` opens the creation, and an account without characters starts
  there. Creating a character takes its name, its body model, `Human Male` or
  `Human Female`, and its look with rows that step through the choices with `<`
  and `>`: face, eyes, scar, tattoo, underwear per body slot, and hair. A
  required choice starts at its first option, and scar, tattoo, and hair start
  at `None`, so a character may be bald. `Create Character` or `Enter` creates
  it and chooses it; `Cancel` returns to the chosen character.
- `Refresh` reloads the characters and the shards and keeps what is chosen.
  `Log Out` logs the account out and returns to LoginMenu.
- While an action runs, every control is disabled.

The body model and the look are stored with the character, and every client
shows them. Changing the look in the game, such as at a barber, deleting a
character, progression, and statistics are not implemented.

## Shard Entry And Exit

- Joining validates the shard placement, connects to the shard's worker, and
  loads the World only after the worker accepts the character. CharacterSelect
  shows `Joined shard <shard> as <character>. Loading <scene>.` and stays visible,
  with its buttons disabled, until the World replaces it. A World that fails to
  load shows the error and leaves the shard. A refused join stays in
  CharacterSelect and shows the server's reason.
- `Leave Shard` in the Debug menu waits until the worker confirms the leave and
  then returns to CharacterSelect.
- An unexpected disconnect returns the player to CharacterSelect, and an expired
  account session returns it to LoginMenu.

## Worlds

The game has two development Worlds, both readable greyboxes with a meter grid
on the ground, signs that name every zone and test station, and floor labels
with the measured values. Development World 2, the test World, is 384 by 384
meters with a walled town in the center and a test zone in each direction, and
Development World 1, the stress arena, is a 64 by 64 meter walled floor that
keeps every player, bot, and actor within one interest radius, so everyone sees
everyone. Their layouts are in [Development Worlds](DEVELOPMENT_WORLDS.md).

Other players, NPCs, Mobs, and resource nodes appear when they come within
128 m and disappear when they move beyond 144 m. Other characters move smoothly,
at an even speed, about 133 ms behind the server; after a network stall that
delay recovers instead of growing. Interest is described in
[Interest Management](SERVICE_FEATURES.md#interest-management).

## Characters And Their Looks

- Every player shows a human body, male or female, in the look stored with its
  character: face, eyes, paint layers such as a scar, a tattoo, or underwear,
  and hair. The body stands, walks, and jogs with the speed it moves at.
- Every player wears the garments of the items it has equipped, such as a linen
  shirt, a wool robe, or a blue wool cloak, and holds its weapon and shield. The
  local player changes as soon as it equips an item, and others as the worker
  sends their equipment. Robes and cloaks swing with the body.
- A gathering tool shows only while its character gathers; see
  [Gathering](#gathering).
- A worn hood can be lowered and raised with the `Lower <item>` or
  `Raise <item>` button in the inventory footer, and every client shows it.
- A dying body keeps its clothes until its character respawns. A player corpse
  then shows the dead character's body, look, and the garments still in it, and
  an item looted from it, such as the cloak, leaves the lying body at once.
- Development bots show either body with a varied look, outfit, and weapons
  chosen by their ids.
- In the Development environment, the Admin menu's Look tab changes the stored
  body model and look; see [Admin Menu (F3)](#admin-menu-f3).
- The Debug menu setting `Tint bodies: you purple, players orange, bots blue`,
  off by default, tints the local player purple, other players orange, and
  development bots blue.

How the bodies and garments are put together is in
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md).

## World Menu

Every world action goes through one menu, as in RuneScape:

- A right click opens a menu with every option for what is under the pointer,
  headed `Choose Option` and ending with `Cancel`. A left click runs the top
  option at once, without opening the menu.
- The menu lists every target under the pointer, up to 100 m from the camera,
  nearest first, and never targets hidden behind solid geometry. Each target's
  actions come first, then `Walk here`, then `Examine` for each target. A wolf in
  front of an Iron Vein gives `Attack Feral Wolf`, `Mine Iron Vein`, `Walk here`,
  `Examine Feral Wolf`, `Examine Iron Vein`, and `Cancel`.
- An NPC or resource node offers one option per capability, in the order its
  content lists them, with the verb from the content, such as `Talk-to`,
  `Trade`, `Bank`, `Recover`, `Insure`, `Quests`, `Turn-in`, and `Craft` for Mira
  the Quartermaster, `Mine` for an Iron Vein, or `Chop down` for an Oak Tree. The
  first is the left-click action. Choosing one walks into range, opens the
  interaction, and starts that service: `Trade` opens the vendor, `Bank` the
  Character Bank, `Recover` Recovery Storage, and `Mine` starts mining.
  `Talk-to`, `Insure`, and `Quests` open the interaction panel, where their
  choices are. When the interaction is already open, the service starts without
  walking.
- A living Mob offers `Attack`, and so does another character when the Shard
  allows player versus player. A Mob walking home after a fight offers no
  `Attack`, so a left click on it walks there.
- A corpse offers `Loot Corpse of <name>`.
- `Walk here` is always offered and walks to the ground under the pointer.
- `Examine` shows the target's short description from the actor content in the
  message area in the bottom left corner, such as "A lean, hungry wolf. It eyes
  you like a meal." A corpse shows "The remains of <name>." The area shows the
  newest five messages, and each fades out after 12 seconds. A target the player
  cannot reach also leaves a message there.
- Target names are colored by kind: NPCs and Mobs yellow, objects such as
  resource nodes cyan, corpses orange, and other characters white.
- Next to the pointer, the left-click option is shown with the number of other
  options, such as `Attack Feral Wolf / 3 more options`. Bare ground shows
  nothing.
- The menu closes when an option or `Cancel` is chosen, on a click elsewhere in
  the world, on `Escape`, or when the pointer moves a short distance away from
  it. An `Escape` that closes the menu does nothing else: the attack, the open
  interaction, and the inventory stay as they are.
- Options run against the world as it is when they are chosen, so an option for
  a target that left or died in the meantime does nothing and ends nothing else,
  such as an attack.
- While the character is dead the menu offers nothing.
- Every option except `Examine` ends an attack, as walking away does.

Capability availability is not known before an interaction opens, so the menu
lists every capability, and a capability that is not available says so in the
interaction panel. SimulationWorker still validates range, line of sight,
revision, session, and availability for every request.

## Player Movement

- Left-click the ground, or choose `Walk here`, to walk there. The client plans
  a route around walls, buildings, and cover on a fine internal grid that the
  player never sees, and the character starts walking on the next simulation
  tick.
- A click on a spot that cannot be reached, such as a wall face or a roof, walks
  to the closest reachable point on open ground. A point under a roof, bridge,
  or mountain that is lower than the click, such as a tunnel floor right below a
  clicked mountainside, is used only when no open point is reachable. A click
  more than 64 meters away walks toward it as far as the search reaches.
- A new click replaces the current route at once, and the previous route
  continues until the new one is planned, so a long search never stalls a frame.
- Holding the left button down after a click that walked steers the walk: the
  destination follows the ground under the pointer, with a new route up to ten
  times a second once the point moved 0.25 m or more, so holding the button with
  the pointer ahead of the character keeps it walking. Hovering a target or a
  panel while holding changes nothing, and a pointer that meets no ground keeps
  the current route. Releasing the button lets the character walk on to the
  last destination. A left click on a target and an option chosen from the menu
  never steer, and a right click or death ends the steering.
- The character walks at one speed, 5 meters per second, slowed by a heavy load
  as [Carry Weight And Encumbrance](#carry-weight-and-encumbrance) describes, and
  turns toward its direction of travel. It climbs steps up to 0.35 m and slopes
  up to 45 degrees; steeper surfaces are not ground. Routes never step off a
  ledge higher than 0.4 m, so from a raised platform they take the stairs down; a
  character that leaves the ground falls and keeps its momentum until it lands.
- A route that collision blocks for half a second ends, so the character never
  keeps pushing against a wall.
- A small yellow disc marks the destination until the character arrives.
- The character moves smoothly at the frame rate, not in steps of the 30 Hz
  simulation, and small corrections from the server are smoothed. The worker
  moves the character one step per input like the client, so uneven packet timing
  does not pull it back and forth. A route continues for about 100 ms when inputs
  arrive late; after a longer gap the character waits until the inputs arrive and
  then catches up.
- Steps and short drops on flat ground are smoothed over 100 ms; ramps, falls,
  teleports, and large corrections are not.

SimulationWorker owns the position, collision, and route progress; see
[Server-Authoritative Movement](SERVICE_FEATURES.md#server-authoritative-movement).
The movement course and the highlands of Development World 2 show every limit,
as [Development Worlds](DEVELOPMENT_WORLDS.md#movement-course) describes. In the
highlands the player walks over hills, up a mountain's walkable face, and
through a tunnel below it, and is stopped by cliffs steeper than 45 degrees,
which rock paint marks. Moving platforms, dynamic doors, and physics objects are
not implemented, and running with run energy is planned in
[Movement And Camera](GAME_DESIGN.md#movement-and-camera).

## Orbit Camera

- The camera orbits the player from behind and above. Drag with the middle mouse
  button to turn and tilt it, or hold the arrow keys: left and right turn it, up
  and down tilt it.
- The scroll wheel zooms between 0.6 and 15 meters from the pivot at neck height,
  15 percent per notch, and the zoom eases in. Fully zoomed in, the camera stays
  about 30 centimeters from the character.
- Tilt is limited to 10 through 80 degrees above the horizon. The camera starts
  12 meters away and 40 degrees above the horizon, behind the player.
- Walls pull the camera in front of them at once, and it eases back out after the
  obstruction clears. The camera never collides with its own character. Hiding
  roofs and occluding walls is not implemented.
- Scrolling over a panel does not zoom.

## Debug Menu (F2)

- F2 opens and closes the Debug menu in the top-right corner, and its `X` button
  closes it too. It is closed when a World scene loads. Its values refresh four
  times a second.
- The menu has four tabs:
  - **Overview**: the player's position, facing, and collision chunk, the ground
    under the pointer, `Copy player` and `Copy pointer` buttons that put
    `x, y, z` on the clipboard, a quick check of the frame, ping, latest
    snapshot, and connection, and what the world shows for debugging.
  - **Performance**: FPS against a 60 FPS budget, frame average, p95, and
    maximum time, resolution, quality, VSync, Unity memory, and the managed heap.
  - **Network**: connection state, character, shard, World, worker, runtime, UDP
    endpoint and protocol, the observed server tick, round trip time, snapshot
    age and observed rate, application packets and payload in and out,
    estimated snapshot loss, and known entities.
  - **Movement**: the authority, tick and snapshot rates, the walk speed, carry
    weight and capacity, item-state revision and movement percentage, pending
    predicted inputs, reconciliations and hard snaps, the latest correction, the
    current route and waypoint, and the latest route plan with its search size
    and time.
- `Show in the world` on the Overview tab has three settings. The first two are
  off whenever the client starts, and the third is off by default:
  - `Collision around you` draws the baked collision in a square 48 m wide
    around the player while the menu is open: green for walkable ground, yellow
    for surfaces too steep to stand on, red for boxes that block movement and
    sight, orange for boxes that block movement only, such as resource nodes, and
    purple for boxes that block sight only. It is the collision the planner, the
    prediction, and SimulationWorker use.
  - `Path you follow` draws the rest of the current route in cyan.
  - `Tint bodies: you purple, players orange, bots blue` is kept between
    sessions.
- The footer shows the status and `Leave Shard`.
- The Debug menu shows only what the client itself observes. SimulationWorker
  CPU, memory, total population, bot population, capacity, and internal tick
  timing stay server-side and are not sent to game clients.

## Admin Menu (F3)

- F3 opens and closes the Admin menu below the player frame. Its tools work only
  against a SimulationWorker in the Development environment that allows them, as
  the local stack does; see
  [Development Admin Tools](SERVICE_FEATURES.md#development-admin-tools). Such a
  worker sends the character's admin state after every join; without it the menu
  greys out its tabs and says that the tools are off.
- **Look** steps through the stored body model, every appearance slot, every
  paint category and slot, and the hair, with `none` where a character may go
  without, and shows whether a player chooses each at creation only or in the
  game too. A Development GameService stores the change, and the new look shows
  when the worker sends it back.
- **Movement** sets the walk speed to 1x, 2x, 3x, 5x, or 10x. The character walks
  at that speed without corrections until it leaves the World, and a reconnect
  of the same session keeps it.
- **Movement** also teleports: `Pick a spot`, then a left click on the ground, or,
  while the menu is open, `Teleport here` in the right-click world menu. `Escape`
  cancels a pick. SimulationWorker moves a living character onto walkable ground
  inside the World and shows it there to everyone who sees it, as a respawn does.
- **Combat** shows the character's health and mana, fills both with
  `Restore health and mana`, and switches `Invulnerable`, which makes hits leave
  its health as it is until the character leaves the World.
- The footer shows what the worker answered, such as
  `Teleport to 85.0, 1.9, 80.0: done.` or the reason a tool was refused.

## Carry Weight And Encumbrance

- A character can carry 2,000 weight units, plus the capacity bonus of its
  equipped Bag. Items in Permanent inventory and in the equipped Bag count;
  equipped items, the Bank, Recovery Storage, and corpses do not. The full rules are in
  [Carry Weight And Encumbrance](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carry-weight-and-encumbrance).
- Up to full capacity the character walks at full speed. Above it the speed
  falls evenly to 20 percent at the 140 percent hard cap. No action may carry the
  character past the hard cap. A character above it, such as one from older
  data, still joins and moves at 20 percent speed, and may only lighten its
  load.
- The inventory's carry meter and the Debug menu's Movement tab show the current
  weight, capacity, load, and movement speed.

## Player Inventory Foundation

`B` shows character storage only, `C` adds the equipment, and `I` shows the
complete view with the contextual storage. Pressing another inventory key
switches the view, and pressing the same key or `Escape` closes it. Opening the
Bank or Recovery Storage of an NPC, a vendor, or a corpse opens the complete
view with it.

Each column is a window of its own over the world, which stays undimmed. A click
or scroll on a window never reaches the world, while the world beside and between
the windows works as when they are closed: a click walks or attacks, a held
button steers the walk, a right-click opens the world menu, and the wheel zooms.

The panel has three columns that never move each other:

- **Equipment**, on the left, is a paper doll of five full rows of three: tool,
  head, and Bag on top, shoulders, neck, and back below them, main hand, body,
  and off hand in the middle, hands, legs, and ammunition below them, and the two
  rings beside the feet at the bottom. No item equips to the shoulders slot yet.
  A slot the catalog adds later appears in an extra row.
- **Context**, in the middle, shows the Bank, Recovery Storage, a corpse, or a
  vendor in tabs, with a `Refresh` button. Its header names the NPC whose open
  interaction offers the shown storage or trade. Recovery Storage heads each
  delivery with its source, `Recovered: consumed insurance` or
  `Recovered: protected on death`.
- **Character**, on the right, keeps its place in every view: a carry meter,
  Permanent inventory, and the equipped Bag's contents. Its header shows the balance
  and the item-state revision, such as `1,250 Gold    Revision 12`.

Every slot is a square of the same size, and the slot grids sit in the middle of
their columns. An occupied slot shows the item's icon, a category color strip
along its top edge, the stack quantity in its lower-right corner, and a `P` or
`I` badge for protection on death or insurance. Until approved item art exists,
every item shows the same placeholder icon, a grey cube, so the category strip
and the tooltip tell items apart. An empty slot is darker and shows only its
equipment slot name or accepted types. An empty equipment slot that an equipped
item blocks, such as the off hand next to the two-handed Training Shortbow, is
red and marked Blocked. Hovering a slot shows a tooltip with the item name,
category, quantity and stack limit, weight, equipment slots and blocked slots,
active policies with their source, such as `Insurance NPC`, `Quest grant`, or
`Catalog rule`, the destruction restriction, and the slot location; tooltips
never show during a drag.

The carry meter spans zero to the 140 percent hard cap with a marker at full
capacity. It is green up to full capacity, amber while movement is slowed, and
red at the hard cap, and its caption shows weight, capacity, load, and movement
speed. The footer below the character column holds the selected item, its split
quantity, `Split`, `Destroy` with `Confirm destroy`, and `Clear`, and the status
line, so they work in every view.

Items move by drag and drop:

- Drop an item on a valid container or equipment slot. Valid targets light up
  green and invalid ones red. The dragged item's icon, with its quantity, trails
  below and to the right of the pointer, so it never hides the target, and the
  slot it came from fades until the drag ends.
- Dropping onto an occupied slot merges compatible stacks and otherwise swaps
  the two items when each fits the other's slot. On an occupied equipment slot, a
  stack of the equipped item tops it up, and another item equips and sends the
  equipped one to the slot the new item came from.
- A click only selects an item for `Split` or `Destroy`. `Split` moves part of a
  stack, also of the equipped arrows, into an empty slot.
- The ammunition slot holds one stack of up to 1,000 arrows.
- An item cannot be equipped into a slot that an equipped item blocks, such as
  the off hand next to a two-handed bow, or while it would block an occupied
  slot. An item whose level requirement is above the character's level cannot be
  equipped either; every character is level 1, and every item requires level 1
  until experience exists.
- Dropping any item of a Recovery delivery claims the whole delivery.

A right-click moves an item to where it belongs, as a shortcut for a drag:

- An equippable item in Permanent inventory or the equipped Bag equips: it
  takes the first free compatible slot, such as Ring 1 and
  then Ring 2, or else replaces the item in the first compatible slot, which
  swaps into the slot the new item came from. Equipped items that block the slot
  move to the inventory first, so a two-handed bow takes the shield off. Arrows of
  the equipped kind top up the equipped stack.
- An equipped item goes to the inventory.
- While the Bank tab shows, every one of your own items goes to the Bank, and a
  Bank item comes back to the inventory.
- Any item of a Recovery delivery claims the whole delivery into Permanent
  inventory.
- A corpse item takes a free compatible equipment slot, or tops up equipped
  arrows, but never replaces an equipped item; anything else goes to the
  inventory. A corpse Bag takes an empty Bag slot together with its contents.
- While a vendor's tab shows, a carried item goes into the `Sell` slot, where you
  confirm the sale as after a drag.
- The inventory fills as a purchase does: whole items top up a stack first, then
  take a free slot, matching specialized Bag slots first, then Permanent
  inventory, then general Bag slots.
- A right-click that has nothing to do, such as on an ore with no Bank open, says
  why in the status line.

Every item move shows at once: moving, swapping, merging, splitting, equipping
and unequipping items and Bags, destroying, claiming a Recovery delivery,
looting, `Loot all`, depositing, rearranging a corpse, swapping Bags with a
corpse, and buying or selling at a vendor. The item lands where you drop it and
the balance changes before the server answers, and you can keep going while up
to eight moves wait. The server still decides every move. When it rejects one,
every waiting move is undone, the items jump back and flash red for 1.5 seconds,
and the status line says `Item move undone:` with the reason. A new stack from a
split, a purchase, or a partial loot, and the contents of a Bag you just
equipped, can be used once the server has confirmed them, a fraction of a second
later. Committed changes update the inventory directly, and opening the Bank or
Recovery tab shows them without reloading. A client whose item catalog does not
match the server's says that a client update is required and shows no stale
items.

The Bank and Recovery Storage can be inspected from anywhere, but using them
needs an open interaction with an NPC that offers them, such as Mira the
Quartermaster; walking out of range or losing line of sight closes the
interaction and ends the access. In the Development environment, the local stack
lets both be used from anywhere, so item flows can be tested anywhere on the map;
insurance always needs the NPC.

## Corpses And Looting

A player corpse lies where and as the character fell, in the last pose of its
death, in its body and look, wearing the garments still in it. A wolf's corpse
shows the wolf lying dead, and the Feral Alpha's the larger, darker wolf. A
corpse without a model of its own is a capsule. A player corpse lasts 5 minutes,
also when it is empty, a Feral Wolf's 2 minutes, and the Feral Alpha's 10
minutes.

Left-click a corpse, or choose `Loot Corpse of <name>`, to walk to it and open
it. An open corpse always shows in the complete view, which the panel switches
to; switching to `B` or `C`, or closing the inventory, closes the corpse. The
Corpse tab lists the corpses within 32 m but opens none; it says to click a
corpse in the world. The corpse shows its general inventory, equipment, and Bag
contents once its complete state has arrived, and `Loot all`, `Close`,
`Refresh`, and `Partial stack: off` above them.

- Drag whole items into Permanent inventory or the equipped Bag, or drag
  carried items back into compatible corpse slots. Turn on
  `Partial stack` before dragging to move a chosen quantity in either direction.
- Items can be dragged between empty or occupied slots in any corpse section.
  Compatible stacks with room merge. A whole item that cannot merge swaps with
  the item on the occupied slot only when both slots accept the other item and
  the result stays within the hard cap. Every corpse equipment slot shows its
  equipment type and accepts only a compatible item.
- Drag a whole corpse item onto a compatible equipment slot to equip it straight
  from the corpse: an empty slot equips it, equipped arrows of the same type are
  topped up, and an occupied slot swaps your equipped item into the corpse slot
  the loot came from.
- Dropping a corpse Bag onto your occupied Bag slot swaps the two Bags with their
  contents, and onto your empty Bag slot equips it with everything in it. An
  empty corpse Bag, such as a spare one from the corpse inventory, drops into an
  empty slot like any item.
- A live Mob corpse, such as a Feral Wolf's, cannot take an item back, so its loot
  fills an empty slot or tops up a stack of the same item in the inventory or
  the equipment, but never swaps, and it gives only whole stacks.
- Bank and Recovery Storage items cannot enter a corpse, and an equipped item
  enters one only through a swap.
- `Loot all` takes everything that fits in one server operation: the corpse
  equipment first, then the corpse inventory, then its Bag contents. An
  equippable item takes a free compatible equipment slot, and equipped arrows
  are topped up; the rest goes into the inventory as a right-click would place
  it, also as part of a stack when the whole stack does not fit. A corpse Bag
  with items takes an empty Bag slot with everything in it; when you already
  have a Bag, its contents come first and the emptied Bag follows. Whatever does
  not fit in a slot or under the hard cap stays in the corpse.

Every corpse transfer and rearrangement shows on both sides at once, and other
players looking at the same corpse see it change. Several players may open the
same corpse, and the dead character loots by the same rules as everyone else.
A live Mob corpse closes for every viewer once its last item is taken, and any
corpse closes when it expires or when the viewer dies. The Corpse context then
names the reason in neutral text, such as `The corpse is empty.` or
`The corpse expired and is no longer available.` Closing the view yourself
leaves no message. Only a corpse that ended through an error, such as one whose
state could no longer be trusted, shows a red error. A refused loot, such as one
past the hard cap, shows its reason in red while the corpse stays open.

## Death

A character at 0 health dies. Above every open window, `You died` counts down
`Respawning in <n> s` from 5 seconds, and the character then respawns at the
World spawn with full health and mana. What happens to its items follows the
[Player Death Partition](INVENTORY_AND_DEATH_LOOT_DESIGN.md#player-death-partition):

- Currency stays with the character.
- Items protected on death, and insured items that would otherwise be lootable,
  go to Recovery Storage; insurance is used up only when it actually protects an
  item.
- Everything else from Permanent inventory, the equipment, and the Bag goes into
  the corpse at the place of death, where anyone may loot it for 5 minutes.
  Whatever is left when it expires is destroyed.

The inventory shows the moved items at once, without a new login. A death that
leaves more weight than the hard cap allows, because the equipped Bag's bonus
went with the corpse, still happens; see
[Carry Weight And Encumbrance](#carry-weight-and-encumbrance). A worker restart
keeps player corpses until they expire. Development testing can also create a
real corpse from an offline character with the development item tools; see
[Inventory Item Grants Window](MANUAL_TESTING.md#inventory-item-grants-window).

## World Actors And Interaction

- NPCs offer services. Mira the Quartermaster offers dialogue, trade, the
  Character Bank, Recovery Storage, insurance, quests, and crafting, and the City
  Guards dialogue and training. Every NPC and resource node is invulnerable.
- Mobs fight: the Feral Wolf and the Feral Alpha; see [Combat](#combat).
- Resource nodes are gathered: Iron Veins and Oak Trees; see
  [Gathering](#gathering).
- Choosing an option of a target walks the player to about 2.5 m from it, and
  the interaction opens within 3 m and stays open up to 3.5 m while the target is
  in sight, as
  [Interaction Targeting And Input](NPC_AND_MOB_SYSTEM_DESIGN.md#interaction-targeting-and-input)
  describes. The interaction panel says `Walking to <name>...` meanwhile. A new
  option elsewhere cancels the walk, and `Escape` closes the active NPC or corpse
  interaction.
- A player has one active interaction at a time, including a corpse view, and
  several players may use the same target at once.
- The interaction panel lists only the capabilities the server returned when the
  interaction opened. `Character Bank` and `Recovery Storage` show as
  `Open Character Bank` and `Open Recovery Storage`, because they open the
  inventory with that storage; `Trade` opens it with the vendor; each stays
  usable while the interaction is open.
- `Insurance` lists the character's insurance-eligible items, each with
  `Insure: <item>` or `Insured, remove: <item>`. `Available Quests` offers to
  accept the quest, which grants its required item, the Sealed Letter, and to
  abandon it, which removes that item again. The inventory and Recovery Storage
  show the result.
- Talk, crafting, training, and quest completion answer that they are not
  available yet; they never pretend to succeed. Quest progression and crafting
  are not implemented.

## Currency And Vendor Trade

- Every character has a persistent balance of one currency, Gold. A new
  character starts with 100 Gold. The inventory's character header shows the
  balance, such as `1,250 Gold`.
- Currency is not an item. It takes no slot and no weight, and it stays with the
  character on death.
- Mira the Quartermaster offers `Trade`, in her right-click menu and in her
  interaction panel. Choosing it opens the complete inventory view on the
  `Vendor` tab with the General Store and the current balance. Trading works only
  while the interaction stays open; walking out of range closes it.
- The General Store sells arrows, a potion, food, both gathering tools, clothes,
  a helm, the melee and magic weapons, and the shield at 100 percent of each
  item's value, with unlimited stock. Its offers are the vendor profile
  `content/items/vendors/vendor.city_general_store.json`, and the item values
  are in the item definitions; see
  [Vendor Profiles](../content/items/README.md#vendor-profiles).
- The Vendor tab shows the balance, then every offer in catalog order with its
  unit price, a quantity field, and a button that names the total as you type,
  such as `Buy for 50 Gold`. Without an open vendor interaction the tab says to
  talk to a vendor, such as Mira, and choose `Trade`.
- A purchase goes into carried storage. It first tops up existing stacks, then
  fills free slots: matching specialized Bag slots, Permanent inventory, and
  general Bag slots. A purchase that does not fit,
  that the balance cannot pay, or that would pass the hard cap is refused without
  a partial result.
- To sell, drag a carried item from Permanent inventory or the equipped Bag onto
  the `Sell` slot, choose a quantity, which defaults to the
  whole stack, and confirm `Sell <n> <item> for <total>`, or press `Cancel`. The
  vendor pays 40 percent of the value, rounded down on the total, and part of a
  stack can be sold. A sale worth 0 Gold after rounding is refused. Equipped and
  banked items, protected and insured items, quest items, non-empty Bags, and
  items without a value are refused. There is no buyback: a sold item is
  destroyed.
- Prices are fixed. The client shows the price it expects, and the server refuses
  the trade if the price no longer matches. A purchase or sale changes the
  balance and the items together and is recorded, so trades persist.

Known limitations: one vendor with one profile, no buyback, no stock limits, and
no trading between players. The pricing rules are in
[Vendor Trades](INVENTORY_AND_DEATH_LOOT_DESIGN.md#vendor-trades).

## Gathering

- Iron Veins and Oak Trees are resource nodes. Development World 2 has seven
  Iron Veins in the quarry southeast of the town and four Oak Trees in the oak
  grove south of the town, and Development World 1 has nine Iron Veins east of
  the spawn.
- An Iron Vein is grey faceted rock with chunky rust-orange iron ore that shows
  from every camera angle. A depleted vein keeps its rocks, now with broken flat
  tops and rubble around them, but without ore.
- An Oak Tree is a fantasy oak about 5.6 m high: a twisted trunk on thick roots, a
  broad crown of faceted green clumps, and large golden acorns hanging below it. A
  felled tree is a stump with a pale cut face and wood chips.
- Equip a gathering tool in the `Tool` slot and left-click a node, or choose its
  option in the right-click menu, such as `Mine Iron Vein`. The character walks
  into range, the interaction opens, and gathering starts. Each node needs its own
  tool kind: the Starter Pickaxe mines an Iron Vein, and the Woodcutting Axe chops
  an Oak Tree with `Chop down Oak Tree`. Without a gathering tool the panel says
  `Equip a gathering tool in the Tool slot to gather here.`, and with the wrong one
  gathering never starts and the panel names the tool it needs, such as
  `Woodcutting Axe required in the Tool slot.`
- Resource nodes are solid: characters and Mobs walk around a vein or an oak's
  trunk, and a click beyond one routes around it. A felled oak's stump and a
  depleted vein's rubble stay solid. Sight and arrows pass them.
- One click gathers continuously: one Iron Ore or one Oak Log every 3 seconds,
  shown as `Mine...` or `Chop...` with a progress bar and the gathered count. The
  yield enters carried storage by the same rule as a purchase.
- While a character gathers, it holds the tool of its `Tool` slot in both hands
  and swings it, two strokes per yield, and every player nearby sees it: the
  Starter Pickaxe, a curved iron pick with a blue ore crystal and a brass band
  with the rune Fehu, in an overhead stroke, and the Woodcutting Axe, a bearded
  axe with a golden oak leaf on its cheeks and a golden acorn at the butt, in a
  diagonal chop. A sword and a shield it holds are put away meanwhile, and the
  tool goes back to the `Tool` slot when gathering stops. A character that walks
  away stops swinging at once.
- Gathering stops when the node is depleted, the character moves, the interaction
  closes, carried storage is full, the yield would pass the hard cap, or the tool
  is no longer equipped. The panel shows the gathered count and the reason, and
  the capability button, such as `Mine`, starts again.
- Nodes are shared by every player on the Shard. A node holds 5 yields. When the
  last one is taken, it turns depleted for every player and everyone gathering
  from it stops. A depleted node stays visible but cannot be targeted, and it
  returns after 30 seconds. A worker restart restores every node to full.

Known limitations: two resource types, no Mining or Woodcutting experience or
level, and no success chance per cycle. The character swings in the direction it
faces, which is toward the node after it walked there, but it does not turn to a
node it already stands next to, and a stroke reaches about 0.7 m ahead whatever
the node's size. In the highest frames of the strokes some garments, such as a
shirt's sleeve on the female body or a raised hood, let the skin come through by
up to about 2 cm. A node's collision is an upright box aligned to the World axes,
so a round trunk collides as a square.

## Combat

Combat is target based and server authoritative:

- A left click on a living Mob, on another character when the Shard allows player
  versus player, or `Attack` in its right-click menu, selects it as the target.
  The character walks into the range of its equipped weapon, a little inside it,
  such as about 9 m for the Training Shortbow, and attacks on the weapon's
  cooldown while it stands still. Moving between attacks is allowed. Without a
  weapon the character does not walk and reports `No weapon equipped`.
- An attack starts at once and releases when its weapon family's windup ends:
  the sword's blow lands after 0.35 s, the axe's after 0.6 s, the bow's arrow
  leaves after 0.8 s, and the staff's bolt after 0.15 s. A step, a new target, or
  anything else that would stop an attack before then cancels it: nothing is
  spent, and the next attack may start at once. A target that walks on during
  the windup is still hit while it stays within a quarter more than the weapon's
  range. An arrow or a bolt in flight still hits when the character moves on.
- Any other world option except `Examine`, or `Escape`, ends the attack.
- When the worker reports the target out of range, the character walks closer
  by itself, at most once a second, and 2 m closer each time the line of sight is
  blocked.
- A bow needs arrows of its family in the Ammunition slot. Every shot spends one
  arrow, even when it flies at a target that died, and the inventory's
  ammunition slot shows the live arrow count, also 0 or 1, without redrawing the
  panel. An equipment change holds attacks for 1 second.
- The weapons:

| Weapon | Hands | Range | Damage | Every | Notes |
| --- | --- | --- | --- | --- | --- |
| Training Shortbow | Both | 10 m | 10 to 18 | 2.4 s | Needs arrows, which fly at 30 m/s; a Bronze Arrow adds 5 percent |
| Iron Sword | Main hand | 2.5 m | 11 to 19 | 2.4 s | A cut with a step in |
| Iron Greataxe | Both | 2.5 m | 16 to 27 | 3 s | Rests on the right shoulder; a two-handed swing |
| Oak Staff | Both | 10 m | 10 to 18 | 2.4 s | 5 mana per bolt; the bolt flies at 20 m/s |

- The Training Shortbow is held in the left hand and drawn with the right. Every
  attack turns the archer into a stance with the right foot back, draws the
  string to the jaw with an arrow on it while the bow bends, and looses that
  arrow at the release. A melee weapon in one hand without an attack of its own
  strikes with a step into a blow. The Heater Shield goes in the off hand and has
  no armor values yet. Weapons and shields show in the hands of their wearer, and
  everyone who sees an attacker sees its attack.
- The Oak Staff deals Normal damage like every weapon. A character has 100 mana,
  and every bolt costs 5 mana when it leaves the staff. Without enough mana the
  attack status shows `Not enough mana`, the target stays selected, and the
  character casts again by itself once its mana is back. A character holds the
  staff upright beside its right hip, also while it walks, and every cast thrusts
  it toward the target while the other hand points at it. A glowing blue-white
  bolt leaves the staff's crystal.
- A character that attacks while standing turns to face its target.
- The top-left frame shows health with a bar, mana with a blue bar, the live
  arrow count, the attack status, and `In combat` while the character is in
  combat. A refused attack request shows its reason below the frame for four
  seconds. The attack status is one of
  `No target`, `Attacking`, `Stop moving to attack`, `Out of range`,
  `No line of sight`, `Equipment changed`, `No weapon equipped`,
  `No ammunition`, `Wrong ammunition`, `Not enough mana`, or `Dead`.
- The target frame at the top center shows the target's name, health, and
  `In combat` or `Returning home`. Every living Mob within 40 m of the character
  has a nameplate, also at full health: its name over a thin health bar with the
  health as numbers in it, such as `32/50`, and its level to the right of the
  bar, as tall as the bar. The bar is red for an aggressive Mob and yellow for a
  defensive one. When the health changes, the bar and its numbers slide to the
  new value in 0.3 seconds. A nameplate is full size within 10 m and shrinks to
  a little over half size at 40 m. It disappears when the Mob dies or walks out
  of range, and the nearest nameplate draws over farther ones. Nameplates and
  damage numbers are sharp white text with a black outline. The character and every other
  damaged living character in view within 60 m have a small health bar, green,
  amber below half, and red below a quarter. Every hit shows a white damage
  number that rises and fades over 1.1 seconds, above the nameplate on a Mob, and
  arrows and bolts fly to their target.
- A character is in combat until 5 seconds after its last attack or hit. Out of
  combat it regains 1 health and 1 mana every second. A respawn and a login start
  with full health and mana. A character has 100 health.
- Dying is described in [Death](#death).
- Feral Wolves are aggressive: a Feral Wolf notices a living character within
  8 m that it can see, runs to it at 4.5 m/s, and bites for 4 to 9 every 1.8
  seconds. A wolf hit by a character always fights back. A wolf that is lured more than 30 m
  from where the fight started, or that neither bites nor is hit for 10 seconds,
  turns back home, cannot be damaged on the way, and arrives with full health.
  Wolves walk their patrol path between fights.
- A Feral Wolf is level 2, has 50 health, drops a Wolf Pelt and sometimes Bronze
  Arrows, and returns after 30 seconds. The Feral Alpha boss is level 5, has 240
  health, notices characters within 10 m, bites for 7 to 12 every 2.4 seconds,
  drops three Wolf Pelts and sometimes a Copper Ring in a durable corpse, and
  returns after 300 seconds. In Development World 2 five wolves live in the wolf
  woods southwest of the town, and the Feral Alpha stands in its lair; in
  Development World 1 four wolves patrol a pen west of the spawn.
- The Grey Wolf is a defensive level 1 wolf with the Feral Wolf's model, health,
  bite, and loot. It never attacks first, but a Grey Wolf hit by a character
  fights back like a Feral Wolf. Three Grey Wolves stand in the meadow south of
  the town in Development World 2.

The rules behind combat are in [Target-Based Combat](SERVICE_FEATURES.md#target-based-combat)
and [Mob Brains](SERVICE_FEATURES.md#mob-brains).

## Not Implemented Yet

What the game does not have yet is in the [Status](GAME_DESIGN.md#status) and
[Deferred Features](GAME_DESIGN.md#deferred-features) of Game Design, and the
status of each area of the project in [Project Overview](PROJECT_OVERVIEW.md).

# Manual Testing

How to check features by hand in the running game. Every check lists its steps
and ends with the expected result. What each feature does is described in
[Game Features](GAME_FEATURES.md). The automated tests run through
`tools/Test-Changes.ps1` and CI, as
[Checks For A Change](LOCAL_DEVELOPMENT.md#checks-for-a-change) describes.

## When To Use It

- After a change that a player can see or that changes a flow, run the sections
  of the features it touches before opening the pull request.
- Before a playtest or after a large merge, run the [shared setup](#shared-setup),
  the [account flow](#account-character-and-shard-flow), and the sections of the
  features in focus.
- When a feature gains something a player can try, add or change its checks
  here in the same change, written once, in the section of its feature.

Manual checks confirm what only a person can judge, such as how a model, an
animation, or a flow looks and feels, and that the parts work together on the
real stack. They never replace the automated tests.

## Shared Setup

Every section starts from here unless it says otherwise.
[Local Development](LOCAL_DEVELOPMENT.md#backend-services) describes the
services and what they log. No check needs a change in the Unity Editor: every
scene, prefab, input action, and asset they use is committed.

### Start The Stack

1. Create `.env` once, as
   [Local Environment File](LOCAL_DEVELOPMENT.md#local-environment-file)
   describes.
2. Start PostgreSQL and Redis:

   ```powershell
   docker compose up -d --wait
   ```

3. In a second terminal, start GameService:

   ```powershell
   dotnet run --project src/Server/GameService
   ```

4. In a third terminal, start the default worker, which runs `local-shard-1`
   with Development World 2, the test World:

   ```powershell
   dotnet run --project src/Server/SimulationWorker
   ```

5. Only for checks that name `local-shard-2` or the stress arena, start the
   second worker in a fourth terminal:

   ```powershell
   dotnet run --project src/Server/SimulationWorker --launch-profile StressArena
   ```

Expected result: `http://localhost:5000/health/ready` reports `ready`, each
worker logs `Activated <n> world actors`, and GameService's `/api/shards` lists
the shard of each worker online.

After pulling changes, restart both services and rebuild every standalone
client, so the client, GameService, and every worker run the same commit.

### Join With The Unity Client

1. Open `src/Client` in the Unity Editor and wait for the import to finish.
2. Open `Assets/Scenes/LoginMenu.unity` and enter Play Mode.
3. Open the `Create Account` tab, enter a unique `Email` and `Username`, and
   click `Create Account`; or log in with an existing account on the `Log In`
   tab.
4. In CharacterSelect, enter a name, click `Create Character`, choose the
   character and `Local Shard 1`, and click `Enter World`.

Expected result: CharacterSelect reports `Joined shard local-shard-1 as <name>.
Loading DevelopmentWorld2.`, and the character stands on the spawn pad of the
town, 18 m south of Mira the Quartermaster.
[Development Worlds](DEVELOPMENT_WORLDS.md#development-world-2-test-world) maps
the zones that the checks below visit.

Many checks need items first. Give them while the character is offline, as
[Development Tools For Testing](#development-tools-for-testing) describes, then
join.

### Add A Second Client

Checks with two players run the Unity Editor as one client and a Windows player
as the other:

1. In Unity, open `File > Build Profiles`, select Windows, turn on `Development
   Build`, and build the existing scene list, `LoginMenu` and `CharacterSelect`,
   into the ignored `Builds` folder. Do not add or reorder scenes: World scenes
   are Addressables, and the player build builds their content first.
2. Run the built player next to the Editor and log in with another account.

Expected result: the player reaches LoginMenu and joins the same shard as the
Editor client. Rebuild it after every change to the client, the protocol, or
the shared simulation.

### Stop Everything

1. Exit Play Mode and close every standalone player.
2. Stop development bots, every worker, and GameService with Ctrl+C in their
   terminals.
3. Stop PostgreSQL and Redis with `docker compose stop`; their data stays. To
   start from an empty database, follow [Reset The Local Development
   Database](LOCAL_DEVELOPMENT.md#reset-the-local-development-database).

Expected result: no worker, GameService, or bot process is left, and
`docker compose ps` lists no running container of the stack.

## Development Tools For Testing

These tools work only in the Development environment, which `dotnet run` uses.

### Inventory Item Grants Window

`MMORPG > Tools > Inventory Item Grants` in the Unity Editor gives an offline
character items, test packages, Gold, and a durable corpse. It never writes to
PostgreSQL itself: before every command it builds GameService in Release, which
takes a few seconds when nothing changed, and runs one of the
[development item commands](SERVICE_FEATURES.md#development-item-commands).
They accept only the Development environment, a local database, and an offline
character, because they cannot update the live state of a worker that owns a
joined character.
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#development-item-tools)
describes how the window works.

1. Start PostgreSQL, Redis, and GameService. No separate build is needed, and
   GameService may keep running.
2. Create the character in the client, then leave the shard or exit Play Mode.
3. Open `MMORPG > Tools > Inventory Item Grants`. Expected result: the toolbar
   shows `Building GameService...` and then `GameService command running...`,
   and `Target` lists every local character with its account, online state,
   item count, weight, and capacity.
4. Select the offline character. To give one item, choose it under `Give
   Individual Item` with a quantity and the destination `Permanent Inventory`,
   `Bank`, or `Secure Container`, and click `Give Item`. Expected result: the
   status reports the grant, and the character row shows the new item count,
   weight, and revision at once.
5. To give a [test package](#test-packages), choose it under `Give Test
   Package` and click `Give Package`. Expected result: an empty character
   receives exactly the package's items. A package needs a character with no
   items and no Recovery deliveries, so the package controls are unavailable
   afterwards, while individual grants stay available.
6. To give Gold, enter a positive `Amount` under `Give Currency`, 100 by
   default, and click `Give Currency`. Expected result: `Granted <amount>
   currency to <character>, who now has <balance>.`, and `Balance` shows the new
   value. The grant goes through the character's wallet and writes a
   `development` row to the currency ledger. A grant above the maximum balance
   of 1,000,000,000,000 is rejected and changes nothing. Never set currency with
   SQL, which would bypass the ledger.
7. To create a durable player corpse, keep `Shard Id` at `local-shard-1`, enter
   a free World position such as `(0, 0, -10)`, beside the Development World 2
   spawn, and click `Create Durable Corpse`. The selected character is the one
   who died: an empty character first receives the Full Inventory Test Pack,
   and a character with items dies with what it has. A character whose only
   items are pending Recovery deliveries cannot be used. Expected result:
   `Created corpse <id> for <character> on local-shard-1 at (0, 0, -10). Restart
   SimulationWorker so it restores the durable corpse.` The corpse expires five
   minutes after its creation. Restart SimulationWorker before joining.

GameService rejects an invalid request, such as a stack above its size, an item
the Secure Container does not allow, or a grant above the 140 percent carry
limit, without a partial transaction. When a command fails, the window says why
and logs the complete output, with the stack trace, as one error in the Unity
Console:

- When GameService does not compile, the window reports `GameService could not
  be built, so the tool did not run an older build.` with the first compiler
  errors, and runs nothing.
- When GameService stops before it answers, for example because the local
  database does not match the source, the window shows the exception message
  first, such as `GameService failed before it returned a development item
  response: Npgsql.PostgresException (0x80004005): 42703: column "sort_order"
  does not exist`.

Closing the window, a script reload, the five-minute build timeout, or the
60-second command timeout ends the running build or command together with every
process it started.

### Test Packages

The packages come from `content/development/item-packages.json`, which
GameService copies next to its build output. Before the first grant, the tool
checks a package against the current item catalog. A package that names an
unknown definition, exceeds a stack size, equips an item into a slot that
cannot hold it or that another equipped item blocks, or no longer reaches its
exact expected load is rejected with a message that names the package and every
problem, and nothing is granted.

| Package | Command id | What it gives |
| --- | --- | --- |
| Full Inventory Test Pack | `full_inventory` | An equipped Traveler's Pack with Minor Healing Potions, Iron Ore, and 100 Bronze Arrows in its specialized slots; a spare Traveler's Pack, the Training Shortbow, the Leather Jerkin, the Starter Pickaxe, a Copper Ring, and Cooked Trout in Permanent Inventory; a Sealed Letter in the Secure Container; Iron Ore stacks of 20 and 19, one potion, 10 arrows, and a third Traveler's Pack in the Bank; and one Recovery delivery of ore and potions. Carry weight `1040 / 2500` |
| Equipment Test Pack | `equipment` | A Traveler's Pack, the Training Shortbow with 500 Bronze Arrows, the Iron Sword, the Heater Shield, the Oak Staff, the Iron Greataxe, the Leather Jerkin, the Starter Pickaxe, the Woodcutting Axe, and two Copper Rings in Permanent Inventory |
| Stack Split And Merge Pack | `stack_operations` | Pairs of ore, potion, and arrow stacks in the Bank |
| Encumbrance 100% Pack | `encumbrance_100` | Carry weight exactly at the base capacity, `2000 / 2000` |
| Encumbrance 140% Pack | `encumbrance_140` | Carry weight exactly at the 140 percent limit, `2800 / 2000` |
| Secure Container Pack | `secure_container` | Four stacks that fill the Secure Container |
| Recovery Delivery Pack | `recovery_delivery` | One Recovery delivery with ore and potions |
| Outfits Test Pack | `outfits` | Every item of clothing and armor that shows a garment, with the wool robe, hood, and cloak in every color, in Permanent Inventory |

### Development Item Commands

The commands behind the window also run from a terminal, for automation or as a
fallback, with the same guards.
[Development Item Commands](SERVICE_FEATURES.md#development-item-commands)
lists what each one does. `dotnet run` builds GameService first, so never add
`--no-build`:

```powershell
dotnet run --project src/Server/GameService --configuration Release -- --dev-items-list
dotnet run --project src/Server/GameService --configuration Release -- --dev-items-grant <characterId> material.iron_ore 20 bank
dotnet run --project src/Server/GameService --configuration Release -- --dev-items-package <characterId> full_inventory
dotnet run --project src/Server/GameService --configuration Release -- --dev-items-corpse <characterId> local-shard-1 0 0 -10
dotnet run --project src/Server/GameService --configuration Release -- --dev-items-level <characterId> 5
dotnet run --project src/Server/GameService --configuration Release -- --dev-items-currency <characterId> 500
```

`--dev-items-list` prints the character ids that the other commands take.
`--dev-items-level` is how a level requirement is tested, because experience
does not exist yet: every character starts at level 1, and GameService refuses
to equip an item whose `requiredCharacterLevel` is higher with
`equipment_level_requirement_not_met`. Every shipped item requires level 1, and
the highest level is the catalog's `progression.maximumCharacterLevel`.

### Development Worker Settings

The checked-in `src/Server/SimulationWorker/Config/appsettings.Development.json`
turns on two development capabilities, which
[Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults) describes.
SimulationWorker refuses to start with either of them outside the Development
environment:

- `DevelopmentItemInteractions:GlobalBankAndRecoveryAccess` opens the Bank and
  Recovery Storage everywhere, so inventory checks need no NPC. The worker logs
  `Development global Bank and Recovery Storage access is enabled.` Without it,
  the Bank and Recovery Storage need an open interaction with an NPC that
  offers them, as in production; insurance always needs the insurance NPC.
- `DevelopmentAdmin:Enabled` turns on the tools of the
  [Admin menu (F3)](#admin-menu-f3). The worker logs `Development admin tools
  are enabled`.

To turn one off for a run, set its variable in the worker's terminal before
starting it, and remove the variable afterwards:

```powershell
$env:DevelopmentItemInteractions__GlobalBankAndRecoveryAccess = "false"
dotnet run --project src/Server/SimulationWorker
Remove-Item Env:DevelopmentItemInteractions__GlobalBankAndRecoveryAccess
```

`DevelopmentAdmin__Enabled` works the same way.

### Admin Menu And Development Bots

- The Admin menu (F3) changes the look, the walk speed, and the position of
  your character, restores its health and mana, and makes it invulnerable; see
  [Admin Menu (F3)](#admin-menu-f3).
- [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) runs a synthetic
  population next to Unity players, by default on the stress arena. The checks
  of crowds, bodies, and looks use it.

### Look Into The Database

Some checks read PostgreSQL. Run a query through the container, so that no
credential appears on the command line:

```powershell
docker compose exec postgres sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "<query>"'
```

Expected result: `psql` prints the rows. Only read with SQL: item and currency
changes always go through GameService.

## Account, Character, And Shard Flow

### Login Menu

1. Enter Play Mode from `LoginMenu`.

Expected result: a card with the `Log In` and `Create Account` tabs shows on a
dark screen, with the caret in `Password`. The bottom of the screen shows `Game
Service: http://localhost:5000`, `Request Timeout: 10 seconds`, and `Realtime
Timeout: 10 seconds`, the values of `Assets/Content/Config/ClientConfig.asset`,
and no field changes the endpoint.

### Login Keys And Errors

1. Enter Play Mode from `LoginMenu` while GameService is stopped, and press
   `Enter`. Expected result: the card shows `GameService at
   http://localhost:5000 does not answer. Is it running? [network_error]` in
   red under the button, and the form is enabled again.
2. Start GameService, open `Create Account`, and press `Tab` and `Shift+Tab`.
   Expected result: the caret moves between `Email`, `Username`, and
   `Password`, and `Tab` writes no character.
3. On `Log In`, enter a wrong password and press `Enter`. Expected result: the
   card shows `Login or password is incorrect. [invalid_credentials]`, and the
   password field takes the caret with its text selected.

### Register, Create, And Join

1. Open `Create Account`, enter a unique `Email` and `Username`, and click
   `Create Account`.
2. In CharacterSelect, which opens the creation for a new account, enter a
   name and press `Enter`; then choose `Local Shard 1` and click `Enter World`.
3. Press F2 and open the Debug menu's `Network` tab.

Expected result:

- CharacterSelect lists each shard as `<name>` over `LOCAL  ·  Local
  Development`, with `Online <players> / <capacity>` in green or a dimmed
  `Offline` that cannot be chosen. A created character is chosen at once and
  shows `Human Male  ·  100 gold`, or `Human Female`, under its name.
- `DevelopmentWorld2` loads. The local player appears only after the accepted
  join, at the World's spawn.
- The `Network` tab shows the state `Joined`, `local-shard-1 /
  development-world-2`, worker `local-simulation-worker-1` with its runtime, the
  endpoint `127.0.0.1:27015 / v<n>` with the current protocol version, and an
  increasing observed server tick. The other tabs show the 30 Hz simulation and
  15 Hz snapshot rates, client FPS and frame timing, the prediction backlog,
  reconciliations, the round trip time, the snapshot age, packet and payload
  rates, the estimated snapshot loss, and the entities the client knows. The
  menu never shows the worker's CPU, memory, capacity, or total player and bot
  counts; those appear only in the worker terminal and in the metrics.
- SimulationWorker logs the joined simulation session.
- The Unity Console shows this sequence for a login; a registration logs
  `Validating registration details for username '<username>'.` and `Account
  '<username>' (<account-id>) registered and logged in.` as its first two
  lines instead:

  ```text
  [AUTH] Validating login credentials for username '<username>'.
  [AUTH] Account '<username>' (<account-id>) logged in.
  [CLIENT] Character '<name>' (<character-id>) is requesting access to shard 'local-shard-1'.
  [AUTH] GameService issued a short-lived join ticket for character '<name>' on shard 'local-shard-1'.
  [CLIENT] Opening UDP connection to SimulationWorker 'local-simulation-worker-1' runtime '<worker-runtime-id>' at 127.0.0.1:27015/udp for shard 'local-shard-1'.
  [CLIENT] UDP transport connected to 127.0.0.1:27015/udp. Sending the short-lived join ticket to SimulationWorker.
  [SIMULATION] Account '<account-id>' with character '<name>' (<character-id>) connected to shard 'local-shard-1' for world 'development-world-2' through worker 'local-simulation-worker-1' runtime '<worker-runtime-id>'. Simulation session '<simulation-session-id>' controls network entity '<entity-id>'.
  [CLIENT] Server-authoritative movement is active at 30 ticks per second with 15 snapshots per second. Player versus player is allowed on this Shard.
  ```

- An authentication, API, transport, timeout, protocol, or SimulationWorker
  rejection appears as a red Console error with its category and stable error
  code. No password, bearer token, join ticket, service secret, or simulation
  session token ever appears in the Console.

### Leave And Log Out

1. Press F2 and click `Leave Shard`.
2. Click `Log Out`.

Expected result: `Leave Shard` gets the server's acknowledgement, closes UDP,
and returns to CharacterSelect with the same character and shard chosen, and
SimulationWorker logs the released simulation session. `Log Out` revokes the
GameService session before the client clears its local state and returns to
LoginMenu, whose `Log In` tab shows the account's login name.

### Remembered Choices And Shortcuts

Needs an account with two characters and the stack with both workers.

1. Choose the second character and `Local Stress Arena`, enter the World, and
   exit Play Mode.
2. Enter Play Mode from `LoginMenu` and press `Enter`.

Expected result: `Log In` shows the account's login name; after the login,
CharacterSelect chooses the second character and `Local Stress Arena`. A double
click on a character, or `Enter`, enters the World with it. Stopping the stress
arena worker and pressing `Refresh` chooses `Local Shard 1` instead, and with no
worker running, `Enter World` is disabled and the line above it reads `No shard
is online. Start a SimulationWorker, then press Refresh.`

### The Stress Arena Shard

1. Start the stress arena worker as in [Start The Stack](#start-the-stack).
2. Join `Local Stress Arena` from CharacterSelect.

Expected result: CharacterSelect lists `Local Stress Arena` online next to
`Local Shard 1`, and the join loads `DevelopmentWorld1` with the character on
the spawn pad in the middle of the walled arena.

## Client Stability

### Busy Buttons And Lost Connections

1. Double-click `Create Account`, `Create Character`, `Refresh`, `Enter World`,
   and `Leave Shard`. Expected result: only one operation starts each time, and
   the controls stay disabled until it ends.
2. Stop SimulationWorker and click `Enter World`. Expected result:
   CharacterSelect shows a structured error and loads no World scene: after a
   graceful stop GameService reports `shard_unavailable` at once, and a worker
   that ended without one yields a realtime network or timeout error after the
   10-second realtime timeout.
3. Start SimulationWorker again, join, and click `Leave Shard`. Expected result:
   only one leave runs, CharacterSelect loads after the acknowledgement, the
   worker logs the released session, and a snapshot that arrives during the
   leave causes no `invalid_snapshot_delivery` error.
4. Join again and stop SimulationWorker while the World scene is open. Expected
   result: the client clears its shard state and returns to CharacterSelect.
5. While the client shows CharacterSelect, log into the same account
   elsewhere, for example with `POST /api/accounts/login` and the body
   `{ "login": "<username>", "password": "<password>" }`, which revokes the
   client's session. Expected result: within about five seconds, or at the next
   `Refresh`, the client clears all local state and returns to
   LoginMenu.

### One Active Account Session

Needs the [second client](#add-a-second-client).

1. Join `local-shard-1` with character one in the standalone player.
2. In the Editor, log into the same account. Selecting another character is
   possible only after this login has replaced the first session.
3. Wait up to five seconds and read the standalone player's log.
4. Join `local-shard-1` with character two from the Editor.

Expected result: the standalone player is disconnected, clears its account and
shard state, and returns to LoginMenu, and its log has an `[AUTH]` message that
the account logged in from another client, with `account_session_replaced`. The
first character disappears for other clients once its server session ends. Only
the second session stays authorized, and only its character stays connected.

## Movement And Camera

### Click To Move And The Orbit Camera

Keep the Game view focused in Development World 2.

1. Left-click the ground a few meters in front of the character. Expected
   result: a small yellow disc marks the destination. The character starts
   walking on the next simulation tick without a visible server round trip,
   turns toward its direction of travel, stops on the disc, and the disc
   disappears.
2. From the spawn, click the ground behind the service hall north of Mira, so
   that the straight line crosses the hall, and watch the Debug menu's
   `Movement` tab. Expected result: the character walks around the hall, never
   into it. The tab shows the path, the current waypoint, and the latest plan
   with its search nodes and milliseconds, and frames do not stall while it
   plans.
3. Click the roof of the service hall. Expected result: the character walks to
   the closest reachable point beside the hall and stops there.
4. Click a new spot while the character walks. Expected result: the new click
   replaces the route at once.
5. Left-click the ground a few meters ahead and keep the button down, then move
   the pointer in a wide arc around the character, over Mira, and over the
   Debug menu, and release it. Expected result: the character keeps walking
   toward the ground under the pointer and turns with it, the yellow disc
   follows the pointer, and the path id in the `Movement` tab rises about ten
   times a second while the pointer moves. Over Mira it walks on instead of
   talking, over the menu it keeps its route, and after the release it walks
   to the last disc and stops. Hold the button with the pointer still a few
   meters ahead of the character. Expected result: it keeps walking, because
   the camera follows it and the ground under the pointer moves ahead.
6. Drag with the middle mouse button, then hold each arrow key. Expected result:
   dragging turns and tilts the camera around the character, left and right
   turn it, up and down tilt it, and the tilt stops at 10 and 80 degrees.
7. Scroll the mouse wheel both ways. Expected result: each notch zooms by about
   15 percent between 0.6 and 15 meters, and the zoom eases instead of jumping.
   Fully zoomed in, the camera stays about 30 centimeters from the head and the
   shoulders at every tilt.
8. Walk so that a building stands between the character and the camera.
   Expected result: the camera moves in front of the building at once and eases
   back out afterwards.
9. With the Debug menu open, click and scroll on it. Expected result: the
   character does not move and the camera does not zoom.
10. Hover Mira the Quartermaster, then left-click her from more than three
    meters away and keep the button down. Expected result: `Talk-to Mira the
    Quartermaster / 9 more options` appears next to the pointer. The character
    walks to about 2.5 meters from her and stops, and her interaction panel
    opens; moving the pointer while holding does not steer it.
11. Press Escape. Expected result: the interaction closes.

Throughout, the cursor stays visible and free. WASD, Shift, Space, F1, and E do
nothing, and the right mouse button opens the [world menu](#world-menu). The
`Movement` tab reports no hard snaps, the latest position correction stays near
zero while the character walks, and the SimulationWorker terminal shows no
`unknown_movement_path` or `invalid_movement_input` error.

### The Movement Course

The movement course north of the town tests every limit of the shared
movement simulation. Each station has a sign and floor labels, and
[Development Worlds](DEVELOPMENT_WORLDS.md#movement-course) lists what each one
must show. Bright green marks a test the character gets through and bright red
one it must not get through.

1. Confirm that the Debug menu's `Movement` tab shows `Server authoritative with
   client prediction` and the `Network` tab an increasing observed server tick.
2. Click spots far apart, and click a new spot to change direction sharply
   while the character walks.
3. Walk through the town's north gate to the course. Click the top of each step
   pad and each slope pad, the far side of the wall with the gaps, the platform
   at the top of the stairs and then the ground below its ledge, and both ends
   of the bridge.
4. Click a spot halfway up a walkable slope and wait at least three seconds
   there.
5. Click beyond the World's boundary wall.
6. Watch the Unity Console and the SimulationWorker terminal while moving for
   at least 30 seconds, then stop SimulationWorker while the character moves.

Expected result:

- Input stays responsive, because the client predicts the same fixed-step rules
  as SimulationWorker. The server tick rises steadily, snapshots acknowledge
  input without protocol errors, and normal snapshots cause no repeated large
  snaps on flat ground, slopes, or steps.
- The character gets through every green test and through no red one. Routes
  lead around walls and through the gaps that are wide enough, and they take
  the stairs instead of stepping off the ledge.
- Steps and short drops change height smoothly instead of snapping the
  rendered character or the camera to each step.
- Standing on the slope leaves the character still, and its feet stay on the
  slope surface while it moves and while it stands.
- A route against the boundary ends at the closest reachable point, and a route
  that collision blocks ends after half a second instead of pushing against the
  wall.
- Stopping SimulationWorker clears the simulation session and returns the
  client to CharacterSelect.

A client whose baked World data or compiled movement rules differ from those of
the worker, such as an old standalone build, refuses to start movement and
reports a collision or simulation revision error.

### A World Scene Without A Join

1. Open `Assets/Scenes/DevelopmentWorld2.unity` directly and press Play.
2. Press F2.

Expected result: the scene, its spawn, and its references load, but no local
player, gameplay camera, or input object is created, no missing-reference
error appears, and the Debug menu reports `No active simulation session. Local
player is not spawned.` Movement is tested only through the join flow, because
a local player stands for an accepted presence in the World.

## World Menu

Give the character the [Equipment Test Pack](#test-packages) first, and equip
the Training Shortbow with the Bronze Arrows and the Starter Pickaxe.

1. Move the pointer over Mira without clicking. Expected result: next to the
   pointer, `Talk-to Mira the Quartermaster / 9 more options` appears with her
   name in yellow. Over bare ground nothing appears.
2. Right-click Mira. Expected result: a menu headed `Choose Option` opens at
   the pointer with `Talk-to`, `Trade`, `Bank`, `Recover`, `Insure`, `Quests`,
   `Turn-in`, and `Craft Mira the Quartermaster`, then `Walk here`, `Examine Mira
   the Quartermaster`, and `Cancel`.
3. Choose `Examine Mira the Quartermaster`. Expected result: the menu closes,
   the character does not move, and the bottom left corner shows "She keeps the
   city's stores, its accounts, and whatever the dead leave behind." The
   message fades out after about 12 seconds.
4. Right-click Mira from a distance and choose `Trade`. Expected result: the
   interaction panel says `Walking to Mira the Quartermaster...`, the character
   walks to her, the interaction opens, and the inventory opens on the `Vendor`
   tab without a click in the panel. Right-click her again and choose `Bank`.
   Expected result: the Character Bank opens at once, without walking.
5. Walk away, then left-click Mira. Expected result: the character walks to her
   and the interaction panel opens with her capabilities, the same as
   `Talk-to`.
6. Right-click the ground. Expected result: only `Walk here` and `Cancel`. Move
   the pointer well away from the menu. Expected result: the menu closes.
   Right-click again, then press Escape or left-click the ground. Expected
   result: the menu closes and the character does not walk.
7. With the pickaxe in the `Tool` slot, right-click an Iron Vein in the quarry.
   Expected result: `Mine Iron Vein` in cyan, `Walk here`, `Examine Iron Vein`,
   and `Cancel`. Choose `Mine Iron Vein`. Expected result: the character walks
   to the vein and starts mining.
8. Walk to the wolf woods southwest of the town and right-click a wolf.
   Expected result: `Attack Feral Wolf`, `Walk here`, `Examine Feral Wolf`, and
   `Cancel`; when another target, such as a second wolf, stands behind it, its
   options follow in distance order. Choose `Examine Feral Wolf` while
   attacking. Expected result: the attack goes on, and "A lean, hungry wolf. It
   eyes you like a meal." appears. Right-click the ground and press Escape.
   Expected result: the menu closes and the attack goes on. Choose `Walk here`.
   Expected result: the attack ends and the character walks.
9. Kill a wolf and right-click its corpse. Expected result: `Loot Corpse of
   Feral Wolf` in orange, `Walk here`, `Examine Corpse of Feral Wolf`, and
   `Cancel`. Left-click the corpse. Expected result: the character walks to it
   and the corpse opens.
10. Let a wolf kill the character. Expected result: while `You died` counts
    down, neither click opens a menu or moves the character.

## World Actors And NPC Services

### Interest Range

1. Walk west from the town along the interest road to the outpost at
   `(-172, 0, 0)`, past the cyan markers at 64, 128, and 144 meters.
2. Walk back to the town.

Expected result: on the way out, Mira and the town guards disappear from the
client and the outpost guard appears; on the way back it reverses.

### Open And Close An Interaction

1. Left-click Mira. Expected result: the character walks to about 2.5 meters
   from her, and the interaction panel opens only after the server accepts it,
   with the capabilities she offers this player. A capability that is not
   implemented yet, such as `Talk` or `Crafting`, reports `This capability is
   not available yet.` in red (`world_interaction_capability_deferred`), never a
   local success.
2. Stand north of the service hall, so that the hall hides Mira from the
   camera, and click where she stands behind it. Expected result: the hall takes
   the click, the character walks toward it, and no interaction opens.
3. Open the interaction within range, then click the ground more than 3.5
   meters away. Expected result: the server closes that interaction. Escape
   also closes an open interaction.
4. With the [second client](#add-a-second-client), open Mira in both clients.
   Expected result: both interactions stay open side by side.
5. With Mira's interaction open, click a corpse. Expected result: Mira's
   interaction closes first and the corpse opens; a player has one open
   interaction at a time.

### Bank And Recovery Storage At Mira

Give the character the Full Inventory Test Pack, and start the worker without
the development access, as
[Development Worker Settings](#development-worker-settings) shows:

```powershell
$env:DevelopmentItemInteractions__GlobalBankAndRecoveryAccess = "false"
dotnet run --project src/Server/SimulationWorker
```

1. Join, press I, and move a Bank item into Permanent Inventory. Expected
   result: the item moves back at once, flashes red, and the status line says
   `Item move undone:` with the reason that the character is outside validated
   bank access (`bank_access_required`).
2. Left-click Mira and click `Open Character Bank` in the interaction panel.
   Expected result: `Character Bank is open while this interaction stays open.`,
   and the inventory opens in the complete view on the Bank, whose header shows
   `via Mira the Quartermaster`. Moving an item between the Bank and Permanent
   Inventory now commits.
3. Click `Open Recovery Storage`. Expected result: Recovery Storage opens, and
   the prepared delivery can be claimed.
4. Walk away until the interaction closes, then move a Bank item again.
   Expected result: the move is undone again with the same reason.

Remove the variable with
`Remove-Item Env:DevelopmentItemInteractions__GlobalBankAndRecoveryAccess`
when finished.

### Insurance And Quest Items

Mira offers one-death insurance, for 100 Gold with the checked-in GameService
settings, and a quest that grants one Sealed Letter. While the character is
offline, give it a `weapon.training_shortbow` in Permanent Inventory. A new
character starts with 100 Gold, enough for one policy; give more with `Give
Currency` for repeated tries. Then join and left-click Mira.

1. In the `Insurance` section, click `Insure: Training Shortbow`. Expected
   result: the button turns into `Insured, remove: Training Shortbow`, the
   item's tooltip lists `Insured (Insurance NPC)`, and the balance drops by 100
   Gold. The item can hold only one active policy.
2. Click `Insured, remove: Training Shortbow`. Expected result: the item stays
   and loses its insurance.
3. In `Quest item lifecycle`, click `Accept quest and grant required item`
   twice. Expected result: one Sealed Letter appears, whose tooltip lists
   `Protected on death (Quest grant)`, and the second acceptance adds none.
4. Select the Sealed Letter in the inventory. Expected result: `Destroy` is
   disabled, and the tooltip says `Cannot be destroyed`. Click `Abandon quest
   and remove its grant lineage`, then accept again. Expected result: abandoning
   removes exactly that letter, and accepting creates exactly one new one.
5. Walk more than 3.5 meters away, or out of sight. Expected result: the
   interaction closes, as every interaction does.

The [currency ledger](#look-into-the-database) has an `insurance` row for the
charge. After a death, Recovery Storage labels a consumed insurance as
`Recovered: consumed insurance` and a protected item as `Recovered: protected on
death`.

### After A Worker Restart

1. Restart SimulationWorker, then join again.

Expected result: the worker logs `Activated <n> world actors from revision
<revision>`, the actors come back from the compiled World data with new runtime
identities, and no old actor or interaction survives.

## Debug Menu (F2)

1. Join and press F2. Expected result: the Debug menu opens in the top-right
   corner on its `Overview` tab with your position, facing, and collision
   chunk, and `Ground at pointer` follows the pointer over the ground.
   `Collision around you` is off. Press `Copy player` and paste somewhere: the
   clipboard holds `x, y, z`.
2. Turn on `Collision around you`. Expected result: within about 24 m of you,
   the ground boxes show their edges, and the gate posts and the hall walls
   show red box edges. Walk to the highlands northeast of the town, around
   `(85, 2, 80)`. Expected result: the terrain shows as a green triangle net
   with the steep cliff faces in yellow, `Shapes shown` counts the shapes, and
   the frame rate stays high. In the quarry the resource node boxes are orange.
3. Turn on `Path you follow` and click the ground far away. Expected result: a
   cyan line runs from you along the rest of the route.
4. Press F2 to close the menu. Expected result: the lines disappear. Press F2
   again. Expected result: they come back, since the settings stay on until the
   client restarts.
5. Turn on `Tint bodies: you purple, players orange, bots blue`. Expected
   result: your skin turns faintly purple, a second client's character faintly
   orange, and bots faintly blue, while eyes, teeth, and tongue keep their
   colors. Turn it off. Expected result: every body shows its own skin. The
   setting is off in a new installation and is kept between sessions.

## Admin Menu (F3)

The worker must log `Development admin tools are enabled`.

1. Press F3. Expected result: the Admin menu opens below the player frame, and
   its footer says `The admin tools are on.`
2. On the `Movement` tab press `3x`, then click the ground far away. Expected
   result: the footer says `Walk speed 300%: done.`, `3x` is highlighted, you
   walk three times as fast without stutter, and the Debug menu's `Movement`
   tab shows `15.0 m/s (300% of the World's)` and no hard snaps. Press `1x`.
3. Press `Pick a spot`, then left-click the ground a few meters away. Expected
   result: you appear there at once, and the footer says `Teleport to ...:
   done.` Right-click the ground. Expected result: the world menu offers
   `Teleport here` after `Walk here`, and choosing it teleports too. Press `Pick
   a spot` and then Escape. Expected result: `Teleport cancelled.`
4. On the `Combat` tab turn on `Invulnerable`, teleport to the wolf woods around
   `(-105, 0, -105)`, and let a wolf bite you. Expected result: the bites show
   no damage, and your health stays full. Turn it off, take a bite, and press
   `Restore health and mana`. Expected result: health and mana are full again,
   and the tab shows `100 / 100, mana 100 / 100`.
5. The `Look` tab is checked in
   [The Look Tab Of The Admin Menu](#the-look-tab-of-the-admin-menu).
6. Stop SimulationWorker and start it again with
   `$env:DevelopmentAdmin__Enabled = "false"`, join, and press F3. Expected
   result: the tabs are greyed out, and the footer says `The admin tools are
   off: only a SimulationWorker in the Development environment allows them.`
   Remove the variable afterwards.

## Inventory And Equipment

Each item move shows at once, before the server answers, and the server still
decides every move: a rejected move jumps back, flashes red, and the status
line says `Item move undone:` with the reason.

### Carry State Of A New Character

1. Join with a new character, press F2, and open the `Movement` tab.
2. Leave to CharacterSelect and join the same character again.

Expected result: the tab shows `Carry weight / capacity` `0 / 2000` and `Item
revision / movement` with the committed revision and `100%`. After the rejoin
the same carry values and revision return, and movement works.

### Inventory Views

Give a new offline character the Full Inventory Test Pack, then join. The
development worker opens the Bank and Recovery Storage everywhere.

1. Press B. Expected result: only the right column shows: the carry meter,
   Permanent Inventory, the contents of the equipped Traveler's Pack, and the
   Warded Pouch, the Secure Container. The character header shows `100 Gold`
   and the item-state revision.
2. Press C. Expected result: the equipment paper doll appears on the left, in
   five rows: `Tool`, `Head`, `Bag`; `Ammunition`, `Neck`, `Hands`; `Main
   Hand`, `Body`, `Off Hand`; `Ring 1`, `Legs`, `Ring 2`; and `Back` and `Feet`.
3. Press I. Expected result: the complete view adds the middle column with its
   `Bank`, `Recovery`, `Corpse`, and `Vendor` tabs.
4. Press the key of the open view, then another inventory key, then Escape.
   Expected result: the key of the open view closes it, another key switches
   views, and Escape closes any view, while the cursor stays free. Hidden
   modules leave no background or header behind, and the right column keeps its
   position and size in all three views.
5. Look at the slots and hover items. Expected result: every slot has the same
   size. Occupied slots show the item name, a colored top strip for the item
   category, and the stack quantity in the lower-right corner; empty slots are
   darker. The specialized Traveler's Pack slots show `Food/Potion`,
   `Material`, and `Ammunition`. A tooltip shows the item's name, category,
   quantity, weight, equipment slots, and policies. The carry meter is green
   with `Weight 1040 / 2500    Load 41.6%    Speed 100%`.

### Equip, Unequip, And Swap

1. Drag the Training Shortbow, the Leather Jerkin, the Starter Pickaxe, or the
   Copper Ring from Permanent Inventory onto a compatible empty equipment slot,
   then back onto an empty Permanent slot. Expected result: valid targets
   highlight green and invalid ones red, the dragged label trails beside the
   pointer without covering the target, and the item lands where you drop it.
   The carry meter drops by the item's weight when it is equipped and regains
   it when the item returns to carried storage.
2. Drag a second compatible item onto an occupied equipment slot. Expected
   result: the two swap, and the previously equipped item takes the slot the
   new one came from.
3. Equip the shortbow. Expected result: `Off Hand` turns red and shows
   `Blocked`, and its tooltip names the shortbow.
4. Click a destination without dragging. Expected result: nothing moves.

### Ammunition

1. Drag the 100 Bronze Arrows from the Bag's ammunition slot onto the
   `Ammunition` equipment slot. Expected result: the weight drops by 100, and
   the slot shows `100`.
2. Select the equipped arrows, enter `40` in the footer below the right column,
   click `Split`, and drag the split part onto an empty Permanent slot.
   Expected result: 60 stay equipped, and the weight rises by 40.
3. Drag the 40 arrows back onto the equipped stack, then drag the stack back
   onto the Bag's ammunition slot. Expected result: the equipped stack is back
   at 100, and then the weight is back at `1040 / 2500`.

### Bags And Specialized Slots

1. Drag the empty Traveler's Pack from Permanent Inventory into a general slot
   of the equipped Bag and back. Drag the empty Traveler's Pack in the Bank to
   Permanent Inventory and back. Expected result: every move commits.
2. Drag the equipped Traveler's Pack, which holds items, onto ordinary
   Permanent, Bag, and Bank slots. Expected result: every destination rejects
   it, because a Bag with items moves only as a whole aggregate.
3. Drag the potions, the ore, and the arrows out of their specialized slots and
   back. Drag the shortbow onto a specialized slot and onto the Secure
   Container. Expected result: the supplies move freely, and both targets
   reject the shortbow.

### Bank Stacks And Swaps

1. In the Bank, click an Iron Ore stack, enter a quantity in the footer, click
   `Split`, and drag the split part onto an empty Bank slot. Then drag it back
   onto its peer. Expected result: the split creates a new stack, which can be
   moved once the server has confirmed it, and the merge restores the original
   quantity.
2. Drag two unlike items between compatible occupied ordinary slots. Expected
   result: they exchange slots. Repeat against an incompatible specialized or
   Secure Container slot. Expected result: neither moves.

### Recovery Deliveries

1. On the `Recovery` tab, drag either item of the prepared delivery onto a
   Permanent or Bank slot. Expected result: the whole delivery leaves Recovery
   Storage, and both of its items appear in the chosen container.

### Carry Weight And Encumbrance

Start from `1040 / 2500` with the Bank stacks of the Full Inventory Test Pack.

1. Move the Bank ore stack of 20 to Permanent Inventory. Expected result:
   `2240 / 2500`.
2. Move the Bank ore stack of 19. Expected result: `3380 / 2500`, above 100
   percent, so the speed drops and the carry meter turns amber with `(slowed)`.
3. Move the single Bank potion, then the empty Bank Traveler's Pack. Expected
   result: `3400 / 2500`, then exactly `3500 / 2500` with `Load 140%` and
   `Speed 20%`; the carry meter is full and red with `(at the carry limit)`.
4. Try to move the Bank arrows into carried storage. Expected result: the
   target is disabled locally, the server would reject the move, and the weight
   stays at `3500 / 2500`.
5. Walk. Expected result: the character walks at a fifth of its speed, and
   the body walks slower without sliding.

The Encumbrance 100% and 140% packs give a fresh character the two limits
directly.

### Right-Click Moves

Give the character the Equipment Test Pack.

1. Press I and right-click an equippable item in Permanent Inventory. Expected
   result: it lands in its equipment slot at once; a ring takes `Ring 1` and a
   second ring `Ring 2`. Right-click an equipped item. Expected result: it goes
   back to the first free inventory slot.
2. With the Heater Shield equipped, right-click the Training Shortbow. Expected
   result: the shield moves to the inventory, and the bow takes the main hand.
3. Choose the `Bank` tab and right-click an inventory item, then a Bank item.
   Expected result: the first goes into the Bank, topping up a stack of the same
   item, and the second comes back to the inventory.
4. Right-click an item that has nowhere to go, such as an ore with no Bank
   open. Expected result: the status line says why nothing moved.

The right-click at a vendor and on corpse items is checked in [Vendor](#vendor)
and [Loot All](#loot-all).

### Rejoin

1. Leave to CharacterSelect and join again. Press I.
2. Open and refresh the Bank and Recovery tabs several times.

Expected result: every item location, quantity, carry value, and the revision
come back without loss or duplication, and the item labels stay stable.

## Vendor

Mira's `Trade` capability opens the General Store. It sells every offer of
`content/items/vendors/vendor.city_general_store.json` at its item's value with
unlimited stock, and buys every carried item with a value except quest items
for 40 percent of the value, rounded down on the total, without buyback.

1. Join with a new character and press B. Expected result: the character header
   shows `100 Gold`; an existing character keeps its balance.
2. Left-click Mira and choose `Trade`. Expected result: the interaction reports
   `Trade is open while this interaction stays open.`, and the inventory opens
   in the complete view on the `Vendor` tab with `General Store` and `You have
   100 Gold`. The offers are listed in the profile's order: the Bronze Arrow,
   the Minor Healing Potion, the Cooked Trout, the Starter Pickaxe, the
   Woodcutting Axe, the clothes, the wool robe, hood, and cloak in their three
   colors, the Nasal Helm, and then the Iron Sword, the Oak Staff, the Iron
   Greataxe, and the Heater Shield, each with its unit price, such as `50 Gold
   each` for the Starter Pickaxe. Each offer has a quantity field, `1` by
   default, and a button that names the total while you type, such as `Buy for
   50 Gold`.
3. Click `Buy for 50 Gold` next to the Starter Pickaxe. Expected result: the
   vendor panel reports `Purchased.`, the pickaxe appears in carried storage at
   once, and the balance shows `50 Gold`.
4. Mine five Iron Ore as [Mining](#mining) describes, return to Mira, choose
   `Trade`, and drag the ore stack onto the `Sell` slot. Expected result: a
   quantity field shows the whole stack, and the button reads `Sell 5 Iron Ore
   for 20 Gold`. Enter a smaller quantity to sell part of the stack, or click
   `Cancel` to discard the sale.
5. Click the sell button. Expected result: the panel reports `Sold.`, the stack
   shrinks by the sold quantity, and the balance rises by 4 Gold per ore,
   rounded down on the total.
6. Right-click a carried item while the `Vendor` tab shows. Expected result: it
   shows in the `Sell` slot with `Sell <n> <item> for <total>`, and nothing is
   sold until you confirm.
7. Read the newest ledger rows:

   ```powershell
   docker compose exec postgres sh -lc 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select character_id, source_kind, delta, balance_after from character_currency_ledger order by id desc limit 10;"'
   ```

   Expected result: the character has a `starting_balance` row of `100`, a
   `vendor_purchase` row of `-50`, and one `vendor_sale` row per sale, and each
   `balance_after` is the previous balance plus `delta`.

## Gathering

An Iron Vein yields one Iron Ore every 3 seconds to a character with a pickaxe
in the `Tool` slot. It holds 5 ore for everyone together, and a depleted vein
returns after 30 seconds. An Oak Tree works the same with a Woodcutting Axe and
yields Oak Logs. The quarry with seven Iron Veins lies southeast of the town,
and the oak grove with four Oak Trees south of the town behind the `OAK GROVE`
sign; the stress arena has nine Iron Veins east of its spawn.

### Mining

1. With the Starter Pickaxe in carried storage but not equipped, left-click an
   Iron Vein. Expected result: the character walks into range and the
   interaction opens, but the panel reports `Equip a gathering tool in the Tool
   slot to gather here.` and nothing is mined.
2. Press C, drag the pickaxe onto the `Tool` slot, and click `Mine` in the
   interaction panel. Expected result: mining starts. The panel shows `Mine...`
   with a progress bar that fills every 3 seconds and `Gathered <n> Iron Ore.`,
   every ore appears in carried storage, and the carry weight rises. From now
   on, clicking an Iron Vein starts mining as soon as its interaction opens.
3. Keep mining. Expected result: after the fifth ore the vein switches to its
   depleted model for every client, mining stops, and the panel reports `The
   resource node is depleted.` The depleted vein cannot be targeted, and about
   30 seconds later it returns and can be mined again.
4. Start mining a vein that has ore left, then click the ground. Expected
   result: mining stops as soon as the character moves, no further ore arrives,
   and the panel reports `Moving stopped gathering.` until the interaction
   closes.
5. With the [second client](#add-a-second-client), mine the same vein with
   both. Expected result: both clients see the vein deplete at the same moment,
   and the second player's mining stops then too.

### Woodcutting

1. Buy a Woodcutting Axe from Mira for 50 Gold, or grant the Equipment Test
   Pack, which holds both tools. With the pickaxe still in the `Tool` slot, walk
   through the town's south gate to the oak grove and left-click an Oak Tree.
   Expected result: the character walks around to the trunk, the interaction
   opens, the panel reports `Woodcutting Axe required in the Tool slot.` at
   once, and no gathering starts.
2. Click the ground on the far side of the tree. Expected result: the character
   walks around the trunk, never through it.
3. Drag the Woodcutting Axe onto the `Tool` slot and choose `Chop down Oak
   Tree` in the tree's world menu. Expected result: the panel shows `Chop...`
   with a progress bar and `Gathered <n> Oak Log.`, one log every 3 seconds.
   After the fifth log the tree turns into a stump with a pale cut face and wood
   chips for every client, and about 30 seconds later the whole oak returns.
4. Sell the logs at Mira. Expected result: `Sell 5 Oak Log for 20 Gold`.

### Tools In The Hands

Give the character the Equipment Test Pack.

1. Equip the Iron Sword in `Main Hand`, the Heater Shield in `Off Hand`, and the
   Starter Pickaxe in `Tool`. Expected result: the character holds the sword
   and the shield, and no pickaxe.
2. Mine an Iron Vein. Expected result: when mining starts, the sword and the
   shield disappear, and the character holds the pickaxe in both hands and
   swings it, two strokes per ore.
3. Click the ground. Expected result: the character stops swinging and walks at
   once; as soon as mining stops, the pickaxe disappears and the sword and the
   shield are back.
4. Equip the Woodcutting Axe in `Tool` and chop an Oak Tree. Expected result:
   the character swings the axe diagonally down while it chops, and once it
   stops, the axe disappears and the hands hold what they held before.
5. With the second client watching, mine a vein with the first. Expected
   result: the second client sees the first character hold and swing the
   pickaxe while it mines, and put it away when it stops.

## Combat

Wolves are the combat targets in Development World 2: five Feral Wolves patrol
the wolf woods southwest of the town, the Feral Alpha stands in its lair at
`(-160, 0, -160)`, and three defensive Grey Wolves stand in the meadow south of
the town, around `(-12, 0, -92)`. Give the character the Equipment Test Pack.

### Bow Combat Loop

1. Join `local-shard-1`. Expected result: SimulationWorker registers its shard
   without `shard_rule_set_unknown`. The Console shows `[CLIENT]
   Server-authoritative movement is active at 30 ticks per second with 15
   snapshots per second. Player versus player is allowed on this Shard.`, and
   the top-left combat frame shows `Health 100 / 100`, `Mana 100 / 100` with a
   full blue bar, `No arrows equipped`, and `No target`.
2. Press C and equip the Training Shortbow in `Main Hand` and the Bronze Arrows
   in `Ammunition`. Expected result: the frame shows `Arrows 500`.
3. Walk to the wolf woods with several clicks and hover a wolf. Expected
   result: `Attack Feral Wolf` appears next to the pointer with the number of
   further options.
4. Left-click a wolf from more than 10 m away. Expected result: the target frame
   shows `Feral Wolf` with `50 / 50`, the character walks to about 9 m and
   stops, the status reads `Attacking`, arrows fly, white damage numbers rise
   above the wolf's nameplate, its health bar slides down, and the arrow count
   drops by one per shot in the frame and in the inventory's `Ammunition` slot.
5. Click the ground or press Escape while it fights. Expected result: the target
   frame closes, the status reads `No target`, the shooting stops, and the
   arrow count stays.
6. Attack a wolf until it dies. Expected result: the target frame closes, the
   wolf leaves a corpse, and a new wolf appears at its spawn after 30 seconds.
   The corpse holds a Wolf Pelt and sometimes Bronze Arrows; looting it is
   checked in [Mob Corpses](#mob-corpses).
7. Unequip the bow and click a wolf. Expected result: the character does not
   walk, and the status reads `No weapon equipped`. Without arrows it reads `No
   ammunition`, and right after equipping the bow again it reads `Equipment
   changed` for about 1 second, during which the character cannot shoot.
8. Equip a small stack of arrows, such as 10 bought from Mira, and shoot until
   they run out. Expected result: the character keeps its target and reports
   `No ammunition`. A few seconds later, once the spent arrows are committed,
   the inventory and a fresh login show the same arrow count as the frame.

### Weapons And Their Attacks

1. With the shortbow and arrows equipped, attack a wolf from about 9 m and
   watch the character. Expected result: the character holds the bow in the
   left hand, turns into an archer's stance with the right foot back, takes the
   string with the right hand, and draws it to the jaw while the bow bends and
   an arrow lies on the string. At the loose, 0.8 s after the attack starts, the
   string springs back and that arrow flies to the wolf, where the damage
   number rises. Click the ground right after the shot. Expected result: the
   character walks away while the arrow still flies and hits.
2. Attack a wolf and click the ground during the draw, before the loose.
   Expected result: the attack ends with a short fade, the arrow on the string
   disappears, no arrow flies, and the arrow count stays the same.
3. Equip the Iron Greataxe in `Main Hand` and attack a wolf. Expected result:
   the axe rests on the right shoulder, and every attack is a two-handed swing
   whose blow lands as the damage number rises; the left hand takes the haft
   during the swing and lets go after it.
4. Equip the Iron Sword and attack a wolf. Expected result: the character walks
   up to the wolf, into the sword's 2.5 m reach, and hits without an arrow;
   every attack is a cut with a step in, landing as the damage number rises,
   and the damage numbers show 11 to 19.
5. Watch these attacks from the second client. Expected result: it shows the
   same clips, the drawn string, and the arrows.

### Magic And Mana

1. Equip the Oak Staff in `Main Hand`. Expected result: the character holds the
   staff upright beside its right hip with the elbow bent and the crystal near
   its head, also while it walks and jogs; a shield in the off hand goes to the
   inventory, because the staff takes both hands.
2. Left-click a wolf from more than 10 m away. Expected result: the character
   walks to about 9 m and stops, and every 2.4 seconds it thrusts the staff
   toward the wolf while its left hand points at it. A glowing blue-white bolt
   leaves the crystal and flies to the wolf, white damage numbers of 10 to 18
   rise, and `Mana` in the combat frame drops by 5 per bolt.
3. Keep attacking until the mana runs out. Expected result: at 0 to 4 mana the
   status reads `Not enough mana`, the target stays selected, and no bolt flies.
   About 5 seconds after the last attack or hit, `In combat` disappears, and
   health and mana rise by 1 per second; at 5 mana the character casts again by
   itself.
4. Press F3 and, on the `Combat` tab, press `Restore health and mana`. Expected
   result: health and mana fill up at once.
5. Choose `Trade` at Mira. Expected result: the General Store sells the Oak
   Staff for 120 Gold.
6. Watch the cast from the second client. Expected result: it shows the staff
   held upright, the cast, and the bolt the same way.

### Wolves

1. Walk to within about 8 m of a wolf and stand still. Expected result: the
   wolf notices the character, runs to it, and bites, the health in the frame
   drops, and the character gets a small health bar over its head.
2. Walk away about 35 m. Expected result: the wolf turns back, the target frame
   shows `Returning home`, arrows do no damage while it runs, and it arrives at
   its place with full health.

The wolves of Development World 2 share one patrol path and move as a pack, so
three or four of them kill a lone archer in about 10 seconds.

### Mob Nameplates

1. Walk south out of the town gate into the meadow, toward the three Grey
   Wolves. Expected result: once a wolf is within 40 m, a nameplate appears
   over it, also at full health: `Grey Wolf` over a thin yellow bar with `50/50`
   in it and `1` to the right of the bar, as tall as the bar. All text is sharp
   and white with a black outline, also with the Game view zoomed in. The wolves
   do not come for the character.
2. Walk toward a Grey Wolf from 40 m away. Expected result: its nameplate starts
   at a little over half size and grows until it is full size at about 10 m,
   always resting just above the wolf.
3. Walk to the wolf woods southwest of the town. Expected result: every Feral
   Wolf within 40 m has a red bar with `50/50` and level `2`, and a nearer
   nameplate draws over a farther one. At the lair the Feral Alpha has a red bar
   with `240/240` and level `5`. A wolf farther than 40 m has no nameplate.
4. With the shortbow, attack a Grey Wolf. Expected result: each hit slides the
   bar and its numbers down to the new health in about 0.3 seconds, for example
   from `50/50` through the numbers in between to `32/50`, instead of jumping;
   the damage number rises above the nameplate, the target frame shows the new
   health at once, and the Grey Wolf now bites back.
5. Kill it. Expected result: its nameplate disappears with it, and the wolf
   that returns 30 seconds later shows `50/50` at once.
6. Walk away from a wolf until it is more than 40 m away. Expected result: its
   nameplate disappears, and it comes back when the character walks closer.

### Death And Respawn

1. Press I to open the inventory, and let the Feral Alpha or several wolves
   kill the character.

Expected result: `You died` with `Respawning in 5 s` counting down appears
above the open inventory, clicks do nothing while the character is dead, and
the inventory shows at once that only the Secure Container contents are left.
The body falls and lies on its back. After the respawn the character stands at
the World spawn with full health and does not walk on, its items lie in its
corpse at the place of death, and the wolves walk home. Loot the corpse back as
[A Player Corpse](#a-player-corpse) describes.

### Player Versus Player

1. With the [second client](#add-a-second-client) on another character, attack
   it with the first until it dies.

Expected result: the target's client shows `You died`, one corpse with its
lootable items appears where it fell, and the character respawns at the spawn
after 5 seconds. Both clients stay connected. The Shard's rule set allows this
on `local-shard-1`.

### Combat On The Playtest Dashboard

1. Start the monitoring stack as [Monitoring](MONITORING.md) describes, fight
   for a while, and open the MMORPG Playtest dashboard.

Expected result: the `Combat` panel shows the attacks, hits, deaths, engaged
and moving Mobs, and Mob routes, the `Tick phases` panel shows the Mob and
combat phases, and the `Arrows and deaths` panel shows the committed arrow
batches.

## Corpses And Loot

A corpse opens in the complete inventory view with its own middle column. Every
move between a corpse and carried storage shows on both sides at once.

### Mob Corpses

A Mob corpse lies as the Mob's own model in the last pose of its Death clip,
turned as it died; a Mob without a model of its own lies as a generic capsule.
A Feral Wolf's corpse lives in the worker for 120 seconds, and the Feral
Alpha's is durable for 600 seconds. Actor Studio (`MMORPG > Tools > Content >
Actor Studio`) shows these values as `Corpse Persistence` and `Corpse Lifetime
Seconds` of each Mob.

1. Kill a wolf and click its corpse. Expected result: the character walks to
   it, and the corpse opens. The wolf lies on its side.
2. Drag every item into the inventory. Expected result: after the last item the
   corpse disappears, and the Corpse context shows `The corpse is empty.` in
   neutral text, not a red error.
3. Drag a wolf's Bronze Arrows onto your equipped Bronze Arrows, and its pelt
   onto a Wolf Pelt stack in the inventory. Expected result: both stacks are
   topped up. Dropping wolf loot on a different item is rejected, because a
   live Mob corpse cannot take an item back.
4. Leave a wolf corpse alone. Expected result: after 120 seconds it closes with
   `The corpse expired and is no longer available.` in neutral text.
5. Kill a wolf, then restart SimulationWorker before its corpse expires, and
   join again. Expected result: the wolf corpse is gone, because a live corpse
   exists only in the worker.
6. Kill the Feral Alpha, which has 240 health; `Invulnerable` from the Admin
   menu helps. Expected result: its corpse holds three Wolf Pelts and sometimes
   a Copper Ring. Restart SimulationWorker and join again. Expected result: the
   Alpha's corpse lies where it died with the same loot, and it expires 10
   minutes after the kill.

### Loot All

Create a durable corpse at `(0, 0, -10)` from a second offline character, as
[Inventory Item Grants Window](#inventory-item-grants-window) describes, give
the looter the Equipment Test Pack, restart SimulationWorker, and join the
looter.

1. Walk to the corpse and open it. Right-click a corpse item. Expected result:
   equipment takes a free equipment slot, never replacing an equipped item,
   arrows of the equipped kind top up the equipped stack, and anything else goes
   to the inventory.
2. Click `Loot all`. Expected result: the corpse empties at once as far as there
   is room: equipment fills free slots, arrows top up the equipped stack, a
   corpse Bag takes an empty Bag slot with its contents, and the rest fills the
   inventory. What does not fit stays in the corpse, and when nothing fits, the
   status line says `Nothing in the corpse fits in your equipment or
   inventory.`
3. Kill a Feral Wolf, open its corpse, and click `Loot all`. Expected result:
   the pelt goes to the inventory, and the empty corpse closes with `The corpse
   is empty.`

### A Player Corpse

A combat death, as in [Death And Respawn](#death-and-respawn), leaves a durable
player corpse that lasts five minutes. The development fixture creates the same
corpse without a fight. Give the source character the Equipment Test Pack, so
its corpse holds several weapons, and use a looter with an empty `Main Hand`.

1. Leave the shard, open `MMORPG > Tools > Inventory Item Grants`, select the
   source character, and note `Balance` under `Give Currency`. Create a durable
   corpse from it as
   [Inventory Item Grants Window](#inventory-item-grants-window) describes.
   Expected result: the corpse is created through the same death pipeline as a
   combat death and holds the character's lootable items, and `Balance` is
   unchanged, because Gold stays with the character.
2. Restart SimulationWorker and join the looter. Expected result: the corpse
   lies beside the spawn as the body of the source character lying on its back
   in its look.
3. Open the corpse. Expected result: the character column stays fixed while the
   middle `Corpse` column opens with the corpse's equipment, inventory, and Bag
   sections, and each corpse equipment slot shows its type, such as `Head` or
   `Body`.
4. Drag the corpse's Iron Sword onto your empty `Main Hand`, then its Oak Staff
   onto your occupied `Main Hand`. Expected result: the first drop equips the
   sword, and the second puts the staff in `Main Hand` and the sword in the
   corpse slot the staff left. With Bronze Arrows equipped, the corpse's arrows
   dropped on them top up the `Ammunition` slot.
5. Drag a carried item into an empty corpse slot, then drop two complete items
   that cannot merge onto each other in both directions. Expected result: the
   deposit stays in the corpse, each valid drop on an occupied slot swaps the
   two items in one step, and the corpse stays open.
6. Move a corpse item to another corpse slot, split a corpse stack into another
   corpse slot, merge compatible corpse stacks, and drop two complete corpse
   items that cannot merge onto each other. Expected result: each commits as a
   move inside the corpse, the last one as a swap, and the character's weight
   and item-state revision do not change.
7. Drop a corpse item on a corpse equipment slot of its type, then on one of
   another type. Expected result: the compatible slot accepts it, and the other
   rejects it without moving either item.

### Two Clients At One Corpse

Create three disposable accounts with one character each: an offline corpse
source and two looters. Give both looters the Full Inventory Test Pack while
they are offline, so each has an equipped Bag with items, and create the corpse
from the source at `(0, 0, -10)`. Start or restart SimulationWorker after the
corpse exists, then join one looter in the Editor and the other in the
[second client](#add-a-second-client).

1. Click the corpse in both clients. Expected result: both looters walk into
   range and open the same corpse view.
2. Drag two different corpse items into valid empty carried slots at nearly the
   same time. Expected result: both moves appear in both views, with no lost or
   duplicated item. Then drag the same remaining item in both clients at once.
   Expected result: exactly one client gets it, and the other receives a stable
   error and then the current state.
3. Click `Partial stack: off` so that it reads `Partial stack: on`, enter a
   quantity, and drag part of a corpse stack to a valid destination in one
   client while the other loots from the same stack. Expected result: no
   quantity ever becomes negative, and the total is kept. A partial deposit of
   a compatible stack into the corpse keeps the total too.
4. In one client, drag a child item of the corpse's Bag. At the same time, in
   the other client, drag the corpse Bag onto your occupied `Bag` equipment
   slot. Expected result: only a complete compatible outcome commits, no Bag
   aggregate is split, and both clients agree afterwards.
5. Create a fresh corpse, restart SimulationWorker before its five minutes end,
   and join again. Expected result: the corpse comes back at `(0, 0, -10)` with
   its original expiry. At the expiry it disappears and closes any open view,
   without getting a new five-minute lifetime.

## Character Looks And Garments

The body model, the look, the hair, and whether the hood is down are stored
with the character, and every client shows them. Garments come from equipped
items.

### Character Creation

1. Log in and click `New Character`. Expected result: below `Name`, the `Body`
   buttons `Human Male`, chosen, and `Human Female`, and the `Look` rows `Face:
   Stubble`, `Eyes: Teal`, `Scar: None`, `Tattoo: None`, `Underwear (legs):
   Briefs`, and `Hair: None`, each with `<` and `>`.
2. Click `Human Female`, choose `Face: Freckles`, `Eyes: Hazel`, and `Hair: Dark
   Brown Hair`, enter a name, and press `Create Character`. Expected result:
   the list shows the new character, chosen, with `Human Female` under its name.
3. Join with it. Expected result: the woman shows the freckled face, hazel
   eyes, and the dark brown hair, in her underwear, a bra and briefs.

### The Look Tab Of The Admin Menu

GameService must run in the Development environment, as `dotnet run` does.

1. Join with a male character, press F3, and open the `Look` tab. Expected
   result: it starts with the `Body` row, `Human_Male` with the note
   `creation`, followed by rows for the face, the eyes, the paint layers, and
   the hair, each noted `creation` or `in game` for when a player chooses it.
2. Press `>` on the `Face` row, which shows `Stubble`, then on the `Eye` row,
   which shows `Teal`. Expected result: the rows show `Clean` and `Brown`, and
   the body shows the clean-shaven face and brown eyes at once; zoom in with the
   mouse wheel to see them.
3. Press `>` on the `Scar` row, which shows `none`. Expected result: `Scar`, and
   a pale scar runs over the brow, the outer corner of the right eye, and down
   the cheek.
4. Press `>` on the `Tattoo` row, which shows `none` and `in game`. Expected
   result: `ForearmRunes`, six runes, f u þ a r k, in a column down the outside
   of the left forearm between two thin lines, in dark blue-black ink, and
   nothing on the right forearm. Press `>` again. Expected result: `Tattoo`, a
   round rune emblem on the upper breastbone and an armband around each upper
   arm, and the runes are gone. Press `>` once more to remove it.
5. Press `>` on the `Hair` row. Expected result: `DarkBrownHair`, and the
   character gets straight dark brown hair with a middle parting and a short
   fringe that covers the ears and falls to the shoulders.
6. The `Underwear (legs)` row, `Briefs`, offers no `none`, because underwear is
   always worn.
7. Leave the shard and join again. Expected result: the body keeps the face,
   the scar, the eyes, the tattoo, and the hair.
8. Press `>` on the `Body` row. Expected result: the row shows `Human_Female`,
   and your character is the woman at once, on the same spot, with her own
   face and eye rows, `Blush` or `Freckles` and `Teal` or `Hazel`, and the
   `Underwear (body)` row, `Bra`. The tattoos sit at the same places on her
   body. She walks and jogs like the man, without errors in the Console. Press
   `>` again. Expected result: the man is back, with the face and eyes you chose
   for him before.

Against a GameService outside the Development environment the game messages
show `Only a Development GameService changes a body model or a look; character
creation chooses them.`, and nothing changes.

### Garments From Items

Give the offline character the Outfits Test Pack, join, and press C. Check each
step on the man and, after `>` on the `Body` row of the Admin menu, on the
woman.

1. Drag `Wool Trousers` onto `Legs`, then `Leather Jerkin` onto `Body`.
   Expected result: at each drop the body wears the brown wool trousers, and
   then the leather jerkin over them at the waist, both as meshes of their own
   close to the skin. The jerkin has short sleeves, no collar, and an open V
   laced across, where the skin shows between the laces. The skin under the
   garments is hidden except near their edges, so nothing shows through when
   you walk and turn, and a look into a sleeve, into the V, or at the hips below
   the hem shows skin, never a hole in the body. On the woman standing still,
   no skin shows through the jerkin at the shoulder seams.
2. Drag `Linen Shirt` onto `Body`. Expected result: the linen shirt takes the
   jerkin's place, and the jerkin moves into the shirt's inventory slot, since
   a body wears one chest piece.
3. Choose `Tattoo` on the `Tattoo` row of the Admin menu and drag `Net Shirt`
   onto `Body`. Expected result: the tattoo and the skin show through the
   meshes of the net, and on the woman the bra too, since the net shirt does not
   hide underwear.
4. Wear `Wool Trousers`, `Leather Boots`, `Long-Sleeved Linen Shirt`, and
   `Leather Gloves`. Expected result: the trousers are tucked into brown leather
   boots that reach the middle of the calf, and brown leather gloves with five
   fingers that follow the hands cover the shirt's cuffs. No toes or fingertips
   show through, and nothing of the trousers or the sleeves shows through the
   boots or the gloves when you walk, jog, and turn; a look into a boot's shaft
   or a glove's cuff shows the trouser leg or the sleeve going on inside. The
   soles sink about 1 cm into the ground. Drag the boots and gloves back into
   the inventory. Expected result: the bare feet and hands are back, whole.
5. Wear `Leather Boots` and `Wool Robe`, then `Long-Sleeved Linen Shirt`, then
   `Wool Robe` again, and `Wool Cloak` on the `Back` slot beside `Feet`.
   Expected result: the robe reaches the ankles with the boots showing below it,
   and the shirt and the robe replace each other, since both are chest pieces.
   The robe's skirt starts just below the navel and falls straight over the
   hips, wider toward the hem, without showing the shape of the legs. The cloak
   lies on the trapezius beside the neck, not on the shoulder muscle, and fans
   out in front from a large round bronze clasp on each side, with the throat
   and the chest free. Its back hangs from the nape between the arms and falls
   straight over the skirt, with no skirt showing through it and no bulge in
   the middle of the back. With `Wool Trousers` under the robe, the trousers are
   not drawn.
6. Wear `Long-Sleeved Linen Shirt` and `Wool Trousers` under the cloak.
   Expected result: the cloak hangs taut from the nape down the back to the
   middle of the calves, between the arms, without following the back or the
   buttocks.
7. Wear the robe and the cloak again, then walk, jog, stop, and turn. Expected
   result: the skirt follows the legs, the cloak is pushed back by legs that
   kick back and swings behind over the skirt, both settle when you stop, and no
   leg pokes out through the skirt or the cloak, no skirt shows through the
   cloak, and no hole shows anywhere.
8. Wear `Long-Sleeved Linen Shirt` and `Wool Cloak`, choose `DarkBrownHair` on
   the `Hair` row, then wear `Wool Hood` on `Head`. Expected result: the hair
   falls to the shoulders over the cloak. The grey-brown hood covers the head,
   with the fringe and a little hair at the sides of the face showing in its
   opening; the hair is pressed flat under it and comes through nowhere, and
   the hood's short collar lies over the neck and the cloak, ending above the
   bronze clasps.
9. Wear `Nasal Helm` on `Head`. Expected result: the helmet takes the hood's
   place, and the hood moves into the inventory. A pointed riveted iron helmet
   sits above the brows and the ears with the leather lining showing below the
   iron and the nasal down in front of the nose, and the hair, pressed flat
   under it, hangs out below it to the shoulders. Choose `none` on the `Hair`
   row. Expected result: a bald head under the helmet; the hair is part of the
   look, not an item.
10. Walk, jog, and turn with the hood and with the helmet. Expected result:
    nothing of the hair, the cloak, or the shirt shows through the hood or the
    helmet.

Drag the items back into the inventory and the bare body is back. What a
character wears comes from its equipment, so it is the same after a new join.

### Dye Colors

1. At Mira's General Store, buy the Wool Cloak for 35 Gold and the Blue Wool
   Cloak for 50 Gold with the 100 Gold of a new character. Drag `Wool Cloak`
   onto `Back`. Expected result: the dark brown wool cloak hangs down the back
   from the nape, held in front by its bronze clasps.
2. Drag `Blue Wool Cloak` onto `Back`. Expected result: the cloak turns dark
   blue with the same folds, hems, and stitches, the clasps stay bronze, and the
   brown cloak moves into the inventory slot the blue one came from.
3. With the Outfits Test Pack, wear `Blue Wool Robe`, `Blue Wool Hood`, `Blue
   Wool Cloak`, and `Leather Boots`. Expected result: the robe and the hood are
   the same medium blue, and the cloak a darker blue over them. With `Red Wool
   Robe`, `Red Wool Hood`, and `Red Wool Cloak` it is the same in a warm madder
   red with the cloak darker. With `Wool Robe`, `Wool Hood`, and `Wool Cloak` the
   robe and the hood are undyed grey-brown and the cloak dark brown.
4. Switch to the woman. Expected result: she wears the same items in the same
   colors.

### Lower And Raise The Hood

1. Wear `Wool Hood`, `Wool Robe`, and `Wool Cloak`, and press C. Expected
   result: the inventory footer shows `Lower Wool Hood`.
2. Press it. Expected result: the hood falls back onto the shoulders, and the
   button reads `Raise Wool Hood`. Press it again to raise the hood.
3. Leave the shard with the hood lowered and join again. Expected result: the
   hood is still down.

### Other Players, Bots, And Corpses

1. Join the same shard near the first character with the
   [second client](#add-a-second-client). Expected result: each client shows
   the other player in its own body model, look, hair, robe, hood, and cloak.
   Lowering or raising the hood, changing an item, or stepping the `Face` and
   `Body` rows of the Admin menu in the first client changes the character in
   the second within a moment, and the change stays after leaving and joining
   again.
2. Start the stress arena worker, start development bots on it as
   [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) describes, and join
   `Local Stress Arena`. Expected result: the bots show both bodies, about half
   each, with varied faces, eyes, hair, and outfits of shirts, trousers, boots,
   robes, and cloaks. Some hold weapons, such as a sword, a shield, both, a
   shortbow, or a staff, but never a shield together with a weapon that takes
   both hands. Each bot keeps its body and look while it walks in and out of
   view.
3. Let the dressed character die, such as to the wolves. Expected result: the
   dying body keeps its clothes until the respawn, then the corpse lies there
   in the character's body and look, with the hood as it was, wearing the robe,
   hood, and cloak. Loot the cloak from the corpse with the second character.
   Expected result: the cloak leaves the lying body at once.

## Art In The Game

The models, materials, clips, catalogs, prefabs, and item presentation of
every approved asset are committed, so no Unity Editor step is needed before
these checks. [Art Pipeline](ART_PIPELINE.md#assets) describes each asset.

### Resource Node Models

1. In Unity, open `Assets/Prefabs/WorldActors/Resource_IronVein.prefab`.
   Expected result: the Inspector names it a Prefab Variant of
   `Resource_IronVein`, with the children `Available` and `Depleted`, both
   enabled in the prefab. `Available` shows grey faceted rocks with orange ore,
   and `Depleted` the same rocks with broken flat tops, rubble, and no ore.
2. In the game, walk to the quarry. Expected result: each Iron Vein, about
   1.5 m wide and 1 m high, shows rust-orange ore from every camera angle, and a
   click on the rocks opens it. A depleted vein shows the depleted model and
   returns with ore about 30 seconds later.
3. Open `Assets/Prefabs/WorldActors/Resource_OakTree.prefab`. Expected result: a
   Prefab Variant of `Resource_OakTree` with `Available` and `Depleted`.
   `Available` shows a twisted brown trunk on five roots under a broad crown of
   faceted dark and light green clumps, with four large golden acorns hanging
   below its rim, and `Depleted` the stump cut at knee height with a pale cut
   face, two splinters, the same roots, and wood chips.
4. In the game, walk to the `OAK GROVE` sign at `(28, 0, -74)`. Expected
   result: four Oak Trees around `(40, 0, -90)`, each about 5.6 m high, three
   times the character, turned differently, and a click anywhere on a tree,
   crown included, opens it. A felled tree shows the stump and returns whole
   about 30 seconds later.

### Human Bodies And Clips

1. In Unity, open `Assets/Prefabs/Player/HumanMaleBody.prefab`. Expected
   result: a Prefab Variant of `Human_Male` with an `Animator` that uses
   `HumanLocomotion` and the avatar `Human_MaleAvatar`, a
   `CharacterLocomotionAnimator`, and a `PlayerBodyAppearance` with the skin
   materials `M_Body_Male` and `M_Face_Male`. `HumanFemaleBody.prefab` is the
   same for `Human_Female`, with `Human_FemaleAvatar`, `M_Body_Female`, and
   `M_Face_Female`.
2. Open `Assets/Prefabs/Player/LocalPlayer.prefab`. Expected result:
   `PlayerVisual` has no child and a `PlayerBodySlot` whose `Models` is
   `PlayerBodyModels`, which lists `Human_Male` first, then `Human_Female`.
3. Join with a new character. Expected result: the character is the male body
   in underwear, standing in `Idle` and breathing.
4. Click the ground 15 m away. Expected result: the body jogs there at 5 m/s
   with its feet on the ground, landing near the center line under the hips as
   in the animation library rather than wide apart, without sliding, and stands
   again when it arrives.
5. Let the wolves kill the character without a weapon. Expected result: when
   the death overlay appears, the body falls into `Death`, a hit, the knees
   buckling, a fall to its right and backward, and lies on its back with the
   arms out until the countdown ends; then it stands in `Idle` at the spawn.
6. Watch another character die from the second client, and join a client while
   a character is dead. Expected result: the other body falls the same way and
   lies until it respawns; a client that joins later sees it lying in the end
   pose without falling again. After the respawn, its corpse stays at the place
   of death as the same body lying in the same pose and direction, and a click
   on it opens the corpse.

### Weapons And Tools In The Wardrobe

`MMORPG > Tools > Art Pipeline > Wardrobe` shows a body on a checkered ground
with chosen garments, paint, and attachments, and plays its clips.

1. Tick `IronSword` and `HeaterShield` under `Attachments`, and play `Idle`,
   `Walk`, and `Jog` on both bodies. Expected result: the sword lies in the
   closed right hand with the blade forward, the shield hangs from the closed
   left hand outward along the forearm with its point down, both follow the
   hands, and `Held` lists both.
2. Tick `OakStaff` instead, and play `Idle`, `Walk`, `Jog`, and `Cast`.
   Expected result: a knotted oak staff about 1.7 m long with a silver-shod
   foot, a leather grip between two silver bands, a twig with two oak leaves,
   and four roots that hold a glowing blue crystal. The right hand holds it
   upright beside the hip with the elbow bent, also while walking and jogging,
   and `Cast` thrusts it forward again and again while the left hand points
   ahead.
3. Tick `MiningPickaxe` and play `Mine` on both bodies. Expected result: the
   body lifts the pickaxe over its right shoulder and strikes it down in front
   of it every 1.5 s, both hands on the haft, the left one on the leather wrap
   near the butt. Tick `WoodcuttingAxe` instead and play `Chop`. Expected
   result: the axe is lifted over the right shoulder and hewn diagonally down
   to the left.
4. In the game, equip the Iron Sword and the Heater Shield. Expected result:
   the character holds them at once, as in the Wardrobe, and unequipping one
   opens its hand. Equip the Training Shortbow. Expected result: it takes the
   main hand and blocks the off hand, so the sword and the shield go to the
   inventory and the bow sits in the left hand.

### Garments In The Wardrobe

Pick each body in turn in `Body`.

1. Tick `WoolTrousers` and `LeatherJerkin`. Expected result: the body wears
   both, and `Drawn` reads `WoolTrousers, LeatherJerkin`. Press `Jog`. Expected
   result: the body jogs, the ground moves along under it at 5.0 m/s with the
   feet planted on it while they are down, each cycle runs to its end before
   the next, and the jerkin follows the arms and the torso; only at the armpits
   can a little skin come through as the arms swing. Drag in the view to see
   the body from every side and scroll to come closer: below the hem the hips
   are whole, the V shows the chest between the laces, and a look up into a
   sleeve shows the arm going on inside. Press `Death`. Expected result: the
   body falls and lies on its back in the jerkin.
2. Tick `WoolTrousers`, `LeatherBoots`, `LinenLongShirt`, and `LeatherGloves`.
   Expected result: `Drawn` lists all four. Press `Walk`, `Jog`, and `Death`.
   Expected result: the boots follow the feet and bend at the front of the
   ankle, the gloves' fingers follow the hands, and the body falls and lies in
   the outfit.
3. Tick `LeatherBoots`, `WoolRobe`, and `WoolCloak`. Press `Walk`, `Jog`, and
   `Death`, and drag around the body. Expected result: the skirt and the cloak
   swing with the steps, the legs stay inside the skirt and in front of the
   cloak, the cloak lies over the skirt, and its inside shows from the front.
   When the body falls, the legs swing up through the cloak and the skirt for a
   moment, as the art pipeline reports for `Death`, and then it lies in the
   robe and the cloak.
4. Tick `LinenLongShirt`, `WoolCloak`, `DarkBrownHair`, and `WoolHoodUp`.
   Expected result: `Drawn` lists all four, with the hair pressed flat under
   the hood. Tick `WoolHoodDown` and then `IronNasalHelm` instead of the hood up,
   and press `Walk`, `Jog`, and `Death`. Expected result: the hair takes its own
   form under the lowered hood, whose bag stays on the upper back, and its
   pressed form under the helmet; the helmet moves with the head, and neither
   the hair nor the cloak comes through the hood or the helmet.

## Client Project And Content Loading

1. Open `src/Client` and wait for the import. Expected result: the Console
   shows no compiler error, and each top-level folder below `Assets/Scripts`
   holds one assembly definition, with a `csc.rsp` beside it.
2. Add the line `#warning probe` inside any class below
   `src/Client/Assets/Scripts` or `src/Shared/GameSimulation/Runtime`, then run
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools/Run-UnityEditorTests.ps1 -Mode EditMode -Filter ClientAssemblyArchitectureTests`.
   Expected result: the Console shows `error CS1030: #warning: 'probe'`, and the
   script prints only that line under `Compile errors:` and ends with exit code
   2, because project code compiles with warnings as errors. Remove the line and
   run the script again. Expected result: the scripts compile and the tests pass.
3. Open `Window > Asset Management > Addressables > Groups`. Expected result:
   the groups `Client Content`, `Item Icons`, `World Actor Prefabs`, `World
   Collision`, and `World Scenes` exist, and `World Scenes` holds
   `DevelopmentWorld1` and `DevelopmentWorld2`.
4. Open `File > Build Profiles`. Expected result: the scene list holds only
   `LoginMenu` and `CharacterSelect`.
5. Join `Local Shard 1` from LoginMenu. Expected result: CharacterSelect shows
   `Loading DevelopmentWorld2.` briefly, the World scene opens with the local
   player and the authored NPC, Mob, and resource node prefabs, the Console has
   no warning or error, and `Leave Shard` in the Debug menu returns to
   CharacterSelect.
6. To check built content, build the Addressables content and switch the Play
   Mode Script to `Use Existing Build`, as
   [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#content-loading-with-addressables)
   describes, and repeat step 5. Expected result: the same as in step 5. Switch
   back to `Use Asset Database` afterwards.

## Two Clients

These checks need the [second client](#add-a-second-client) and two accounts.

1. Join `local-shard-1` with a different character in each client, and click
   to walk with each while watching it from the other.

Expected result: each client has one predicted local player and one view of the
other character, a root of the World scene in the Hierarchy; the local player
is never duplicated. The other character moves smoothly instead of jumping
between the 15 snapshots per second, and after a short network or frame stall
its motion returns to its normal small delay instead of keeping extra delay.
Leaving or disconnecting removes its view at once, without waiting for a
snapshot timeout.

The other two-client checks sit with their features:
[one active account session](#one-active-account-session),
[NPC interactions side by side](#open-and-close-an-interaction),
[shared resource nodes](#mining), [weapon clips](#weapons-and-their-attacks),
[player versus player](#player-versus-player),
[looting one corpse together](#two-clients-at-one-corpse), and
[looks, garments, and corpses](#other-players-bots-and-corpses).

## Backend API Smoke Test

These checks call GameService directly from PowerShell. Start PostgreSQL,
Redis, and GameService; the shard checks also start and stop the default worker.

### Account And Character

```powershell
$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$auth = Invoke-RestMethod http://localhost:5000/api/accounts/register `
  -Method Post `
  -ContentType "application/json" `
  -Body (@{
    email = "smoke-$suffix@example.test"
    username = "smoke_$suffix"
    password = "TestPass123!"
  } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($auth.sessionToken)" }
$character = Invoke-RestMethod http://localhost:5000/api/characters `
  -Method Post `
  -Headers $headers `
  -ContentType "application/json" `
  -Body (@{ name = "Smoke $($suffix.Substring(0, 8))" } | ConvertTo-Json)
$auth.sessionId
$character.id
```

Expected result: the registration returns a session token and a `sessionId`,
and the character is created. Token-bearing responses carry `Cache-Control:
no-store` and `Pragma: no-cache`.

### Item Reads

```powershell
$catalog = Invoke-RestMethod http://localhost:5000/api/item-catalog -Headers $headers
$snapshot = Invoke-RestMethod "http://localhost:5000/api/characters/$($character.id)/item-state" -Headers $headers
$catalog.definitions.Count
$snapshot.permanentInventory.slots.Count
$snapshot.bank.slots.Count
$snapshot.secureContainer.contents.slots.Count
$snapshot.currency
$snapshot.carriedWeight
$snapshot.carryCapacity
```

Expected result: one catalog definition per file in
`content/items/definitions`, then `20`, `40`, `4`, `100`, `0`, and `2000`: the
slot counts, the starting balance, and the base capacity of
`content/items/catalog.json`. The snapshot is empty but complete, and it holds
definition ids and item state only, no client icon, repeated definition data,
operation payload, or credential.

### Conditional Catalog And Item HTTP Contract

`-SkipHttpErrorCheck` needs PowerShell 7, so run this in `pwsh` with `$headers`
and `$character` from above:

```powershell
$catalogResponse = Invoke-WebRequest -Uri http://localhost:5000/api/item-catalog -Headers $headers
$conditionalHeaders = $headers.Clone()
$conditionalHeaders['If-None-Match'] = $catalogResponse.Headers.ETag
$unchangedResponse = Invoke-WebRequest -SkipHttpErrorCheck -Uri http://localhost:5000/api/item-catalog -Headers $conditionalHeaders
$itemStateResponse = Invoke-WebRequest -Uri "http://localhost:5000/api/characters/$($character.id)/item-state" -Headers $headers
$bankResponse = Invoke-WebRequest -Uri "http://localhost:5000/api/characters/$($character.id)/bank" -Headers $headers
$recoveryResponse = Invoke-WebRequest -Uri "http://localhost:5000/api/characters/$($character.id)/recovery" -Headers $headers
$itemState = $itemStateResponse.Content | ConvertFrom-Json
$tierResponse = Invoke-WebRequest `
  -Method Post `
  -Uri http://localhost:5000/api/items/secure-container-tier `
  -Headers $headers `
  -ContentType application/json `
  -Body (@{
    operationId = [Guid]::NewGuid()
    expectedEntitlementRevision = $itemState.secureContainer.entitlementRevision
    tierId = $itemState.secureContainer.tierId
    characterRevisions = @(@{
      characterId = $character.id
      revision = $itemState.itemStateRevision
    })
  } | ConvertTo-Json -Depth 5)
@($catalogResponse, $unchangedResponse, $itemStateResponse, $bankResponse, $recoveryResponse, $tierResponse).StatusCode
@($itemStateResponse, $bankResponse, $recoveryResponse) | ForEach-Object { $_.Headers.'Cache-Control' }
```

Expected result: the status codes are `200`, `304`, `200`, `200`, `200`, and
`200`. The catalog has an ETag, and the unchanged response has no body. Every
character response reports `no-store`. The request for the same tier succeeds
idempotently while the character is offline.

### Shard Topology And Registration

1. With GameService running and no worker, call
   `Invoke-RestMethod http://localhost:5000/api/shards`.
2. Start SimulationWorker and call it again.
3. Stop SimulationWorker with Ctrl+C and call it again.
4. Start SimulationWorker, end it without a graceful stop, for example by
   closing its terminal window, wait longer than the worker heartbeat timeout,
   30 seconds by default, and call it again.

Expected result: `local-shard-1` is offline before the first heartbeat, online
while fresh heartbeats arrive, offline at once after the graceful stop, and
offline after the heartbeat timeout following the ungraceful stop. The response
shows each shard's World, fleet, region, rule set, player count, and capacity,
but never a worker host, port, or runtime.

### Join Ticket

With the worker running and the shard online:

```powershell
$join = Invoke-RestMethod http://localhost:5000/api/shards/local-shard-1/join `
  -Method Post `
  -Headers $headers `
  -ContentType "application/json" `
  -Body (@{ characterId = $character.id } | ConvertTo-Json)
$join.shard.id
$join.shard.worldId
$join.endpoint
```

Expected result: `local-shard-1`, `development-world-2`, and an endpoint that
names `local-simulation-worker-1`, its current runtime, host `127.0.0.1`, and
UDP port `27015`, which the public shard list never shows. The ticket is
short-lived and is consumed only by the UDP handshake of a client; no HTTP
route of the worker takes it.

### Logout And Session Revocation

```powershell
Invoke-RestMethod http://localhost:5000/api/accounts/logout -Method Post -Headers $headers
Invoke-RestMethod http://localhost:5000/api/characters -Headers $headers
```

Expected result: the logout returns `204 No Content`, and the next call with
the same token fails with `401 Unauthorized`. A logout also consumes pending
join tickets and releases the simulation sessions of that account session. An
authenticated session revokes one of its account's sessions, its own included,
with `DELETE /api/accounts/sessions/{sessionId}`, which also returns `204`.

### Database Checks

Read the item foundation as
[Look Into The Database](#look-into-the-database) describes, with these
queries:

```text
select catalog_id, revision, is_current from item_catalog_revisions order by applied_at;
select count(*) as active_characters_missing_item_state from characters c left join character_item_states s on s.character_id = c.id where c.deleted_at is null and s.character_id is null;
```

Expected result: catalog `core` has exactly one row with `is_current` true, and
`active_characters_missing_item_state` is `0`. After a change to the item
catalog, GameService logs one entry when it applies the new catalog revision at
startup.

### Logs

Keep GameService and a worker running for a few minutes while a client plays.

Expected result: GameService periodically logs an item summary with
transaction, contention, lifecycle, corpse, Recovery, and cleanup counts, and
the worker's status line separates real players from bots and live from durable
corpses. No password, session token, join ticket, service secret, or command
body appears in either terminal.

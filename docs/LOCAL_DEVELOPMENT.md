# Local Development

Last updated: 2026-07-13

## Requirements

- .NET SDK 10.0 or newer.
- Docker Desktop.
- Unity Editor for the client project.

## Local Environment File

Create the ignored local environment file before running Compose or either
backend service:

```powershell
Copy-Item .env.example .env
```

Replace every `replace-with-...` placeholder in `.env`. AuthService and WorldServer
share `WORLD_SERVER_ID` and `WORLD_SERVER_SERVICE_SECRET`. Both services search
their content root and parent directories for `.env`. Process environment
variables and command-line values take precedence.

If the PostgreSQL Docker volume already exists, changing `POSTGRES_PASSWORD` does
not change the password stored inside PostgreSQL. Either keep the current local
password in both `.env` entries or update the database role interactively:

```powershell
docker exec -it shooter_mmo_postgres psql -U shooter_mmo -d shooter_mmo
```

Then run `\password shooter_mmo` inside `psql`. Deleting the Compose volume also
recreates the credentials, but permanently removes local database data.

## Repository Quality Checks

Restore locked dependencies and run the standard backend quality gate:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet test ShooterMmo.slnx --configuration Release --no-build
```

Expected result:

- Restore accepts every committed `packages.lock.json` file.
- Format reports no files that need changes.
- Build completes with zero warnings and zero errors.
- Unit tests pass.
- The PostgreSQL integration test is skipped unless its dedicated connection is
  configured.

## Isolated PostgreSQL Integration Tests

The integration test resets the target database's `public` schema. Always use the
isolated test Compose file and never point the test variable at a development,
staging, or production database.

Start the test database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
```

Set the dedicated connection and run all backend tests:

```powershell
$testConnectionLine = Get-Content .env |
  Where-Object { $_ -like "SHOOTER_MMO_TEST_POSTGRES=*" } |
  Select-Object -First 1
$env:SHOOTER_MMO_TEST_POSTGRES = $testConnectionLine.Split("=", 2)[1]
dotnet test ShooterMmo.slnx --configuration Release
```

Expected result:

- All unit tests pass.
- PostgreSQL migration concurrency, ticket concurrency, wrong-world protection,
  reconnect, heartbeat, cross-world exclusion, and idempotent release tests pass
  instead of being skipped.

Clean up the isolated environment:

```powershell
docker compose -f docker-compose.test.yml down
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
```

## Local Infrastructure

Start PostgreSQL and Redis locally:

```powershell
docker compose up -d
```

The local services use these ports:

- PostgreSQL: `localhost:5432`
- Redis: `127.0.0.1:6379`
- AuthService: `http://localhost:5000`
- WorldServer realtime transport: `0.0.0.0:27015/udp`
- WorldServer advertised client endpoint: `127.0.0.1:27015/udp`

Local PostgreSQL credentials and WorldServer service credentials live only in
the ignored `.env` file. `.env.example` documents every required key without
placing active credentials in application settings or Compose YAML.
`WORLD_ADVERTISED_HOST` and `WORLD_ADVERTISED_UDP_PORT` override the client-facing
endpoint without changing the local bind port.

## Backend Services

Run AuthService:

```powershell
dotnet run --project AuthService
```

Check AuthService liveness and readiness:

```powershell
Invoke-RestMethod http://localhost:5000/health/live
Invoke-RestMethod http://localhost:5000/health/ready
```

Expected result: liveness reports `live`. Readiness reports `ready` only after a
real PostgreSQL `select 1` query and Redis `PING` both succeed.

Register a test account:

```powershell
$body = @{
  email = "player@example.com"
  username = "player_one"
  password = "TestPass123!"
} | ConvertTo-Json

$auth = Invoke-RestMethod http://localhost:5000/api/accounts/register `
  -Method Post `
  -Body $body `
  -ContentType "application/json"
```

Use the returned session token:

```powershell
$headers = @{ Authorization = "Bearer $($auth.sessionToken)" }
```

The response also contains `sessionId`. Token-bearing responses include
`Cache-Control: no-store` and `Pragma: no-cache`.

Logout the current session:

```powershell
Invoke-RestMethod http://localhost:5000/api/accounts/logout `
  -Method Post `
  -Headers $headers
```

Expected result: the endpoint returns `204 No Content`, and the same bearer token
returns `401 Unauthorized` on the next authenticated request.

An authenticated session can revoke another session owned by the same account:

```powershell
Invoke-RestMethod "http://localhost:5000/api/accounts/sessions/$sessionId" `
  -Method Delete `
  -Headers $headers
```

Expected result: the endpoint returns `204 No Content`. Any unconsumed join ticket
issued by the revoked session is invalidated, and any world-session lease owned by
that account session is released.

Create a character:

```powershell
$characterBody = @{ name = "Hero One" } | ConvertTo-Json

$character = Invoke-RestMethod http://localhost:5000/api/characters `
  -Method Post `
  -Headers $headers `
  -Body $characterBody `
  -ContentType "application/json"
```

Run WorldServer:

```powershell
dotnet run --project WorldServer
```

WorldServer is a headless .NET Generic Host. It does not expose HTTP routes. A
successful start logs that `local-world-1` is listening on UDP port `27015` with
realtime protocol version 5, the movement-simulation revision, the advertised
endpoint, and the loaded collision revision.

Run a one-time WorldServer startup health check:

```powershell
dotnet run --project WorldServer -- --health-check-only
```

Expected result: PostgreSQL is verified through AuthService readiness, Redis is
verified directly with `PING`, collision data is loaded and checksum-validated,
UDP port availability is checked, and the process exits with code `0`. Stop
Redis, stop AuthService, corrupt a collision chunk, or occupy UDP port 27015 and
repeat to verify a nonzero exit:

```powershell
$LASTEXITCODE
```

Verify that WorldServer owns its UDP socket:

```powershell
Get-NetUDPEndpoint -LocalPort 27015
```

Expected result: the command lists IPv4 and optionally IPv6 listeners for port
27015. WorldServer health is an executable startup check rather than an HTTP
surface.

World registry test:

1. Start AuthService without WorldServer and call `GET /api/worlds`.
2. Start WorldServer and call the endpoint again.
3. Stop WorldServer normally with Ctrl+C and call the endpoint again.
4. Start WorldServer, then terminate it without graceful shutdown. Wait longer
   than the configured 30-second timeout and call the endpoint again.

Expected result: `local-world-1` is offline before the first heartbeat, online
while fresh heartbeats arrive, immediately offline after graceful shutdown, and
offline after the heartbeat timeout following an ungraceful stop. While online,
the response uses the advertised host and UDP port.

Create a join ticket after WorldServer has heartbeated the world online:

```powershell
$joinBody = @{ characterId = $character.id } | ConvertTo-Json

$join = Invoke-RestMethod http://localhost:5000/api/worlds/local-world-1/join `
  -Method Post `
  -Headers $headers `
  -Body $joinBody `
  -ContentType "application/json"

```

The ticket is intentionally short-lived and is consumed by the Unity UDP
handshake. Do not attempt to send it to an HTTP WorldServer endpoint. The backend
socket tests verify join, leave, and the reliable entity lifecycle with:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --filter "FullyQualifiedName~RealtimeServerServiceTests|FullyQualifiedName~RealtimeEntityLifecycleTests"
```

Expected result: the authenticated client joins, moves, and leaves with exact
session cleanup. The two-client lifecycle test also proves that an existing peer
receives reliable ordered spawn and despawn for the other player.

## Unity Client Flow

The Unity project includes temporary runtime UI for the current backend flow.
Only this UI presentation is intentionally temporary. The runtime bootstrap,
session handling, API layer, scene lifecycle, input, camera, and gameplay systems
are maintained as long-term foundations. The UI is created automatically when
each scene starts. The local player and test map must be authored as Unity assets
and are not generated at runtime.

## One-Time Unity WorldScene Authoring

The code foundation expects real scene and prefab assets. Complete these manual
steps in Unity Editor after the scripts compile.

### Create The Input Actions Asset

1. Create the folder `Assets/Input`.
2. In that folder, select `Create > Input Actions` and name the asset
   `PlayerControls`.
3. Open it and create an action map named `Player`.
4. Add these actions and bindings:
   - `Move`: Value, Vector2. Add a 2D Vector composite with WASD.
   - `Sprint`: Button. Bind left Shift.
   - `Jump`: Button. Bind Space.
   - `Aim`: Button. Bind `<Mouse>/rightButton`.
   - `Look`: Value, Vector2. Bind `<Mouse>/delta`.
   - `ToggleDebugCursor`: Button. Bind `<Keyboard>/f1`.
   - `ToggleWorldDebug`: Button. Bind `<Keyboard>/f2`.
5. Click `Save Asset`.

Expected result: `PlayerControls.inputactions` contains one `Player` map and the
seven actions with no missing bindings.

### Create The LocalPlayer Prefab

1. Create the folders `Assets/Prefabs` and `Assets/Prefabs/Player`.
2. In an empty scene or the current WorldScene, create an empty GameObject named
   `LocalPlayer` at position `0, 0, 0`.
3. Add `CharacterController` and use:
   - Radius: `0.35`
   - Height: `2`
   - Slope Limit: `45`
   - Step Offset: `0.35`
   - Skin Width: `0.035`
   - Min Move Distance: `0`
4. Add `CharacterBody` and set Controller Skin Width Ratio to `0.1`. It manages
   the CharacterController center automatically. With these dimensions the
   resulting Center is `0, 1.035, 0`, which places the controller contact
   envelope at local Y zero.
5. Add `PlayerInput`. Assign `PlayerControls` to Actions, set Default Map to
   `Player`, and set Behavior to `Invoke C Sharp Events`.
6. Add `LocalPlayerInput` and `LocalPlayerController`.
   - Grounded Vertical Velocity: `-2`
7. Add a child named `CameraTarget` at local position `0, 1.45, 0` and assign it
   to the LocalPlayerController Camera Target field.
8. Add a child named `LocalPlayerCamera` directly under LocalPlayer. Tag it
   `MainCamera` and add Camera, AudioListener, and ThirdPersonCameraController.
   Configure:
   - Camera Near Clip Plane: `0.05`
   - Camera Far Clip Plane: `500`
   - Shoulder Offset: `1.1`
   - Aim Shoulder Offset: `1.3`
   - Vertical Offset: `0.45`
   - Aim Vertical Offset: `0.35`
   - Distance: `5.25`
   - Initial Pitch: `12`
   - Min Pitch: `-50`
   - Max Pitch: `75`
   - Normal Field Of View: `60`
   - Aim Field Of View: `50`
   - Aim Transition Sharpness: `12`
9. Assign LocalPlayerCamera to both the PlayerInput Camera field and the
   LocalPlayerController Player Camera field.
10. Add an empty child named `PlayerVisual` at local position `0, 0, 0`.
   Add a Capsule child named `Body` at local position `0, 1, 0` and remove its
   CapsuleCollider. All future character models must be authored or positioned
   so their foot plane is local Y zero below PlayerVisual.
11. Optional visual children such as Eyes must also have their primitive colliders
   removed. CharacterController must remain the only player collider.
12. Adjust the visual or material as desired without changing the root scale.
13. Drag the `LocalPlayer` root into `Assets/Prefabs/Player` to create
   `LocalPlayer.prefab`, then delete the temporary scene instance if it was not
   created directly in WorldScene.

Expected result: the prefab root has CharacterController, PlayerInput,
CharacterBody, LocalPlayerInput, and LocalPlayerController. There is exactly one
collision controller on the player. The root is the logical ground point,
PlayerVisual is at local Y zero, CameraTarget and the player-owned
LocalPlayerCamera are assigned on LocalPlayerController, and there is exactly one
Camera and one AudioListener in the prefab.

### Create The RemotePlayer Prefab

1. In an empty scene or the current WorldScene, create an empty GameObject named
   `RemotePlayer` at position `0, 0, 0`.
2. Add `RemotePlayerView` to the root.
3. Add an empty child named `PlayerVisual` at local position `0, 0, 0`.
4. Add a Capsule child named `Body` below PlayerVisual at local position
   `0, 1, 0`.
5. Remove the CapsuleCollider that Unity adds to Body.
6. Give Body a visually distinct material if desired. Keep its foot plane at
   local Y zero.
7. Verify the complete RemotePlayer hierarchy contains no Collider, Rigidbody,
   CharacterController, PlayerInput, Camera, or AudioListener component.
8. Drag the RemotePlayer root into `Assets/Prefabs/Player` to create
   `RemotePlayer.prefab`, then delete the temporary scene instance.

Expected result: `Assets/Prefabs/Player/RemotePlayer.prefab` contains one
`RemotePlayerView` and at least one Renderer. It is a presentation-only network
view with no local input, physics authority, camera, or audio listener.

### Build The Test Map

1. Open `Assets/Scenes/WorldScene.unity`.
2. Create an empty root named `Environment` with empty `Boundaries` and
   `Obstacles` children. Reset all three transforms.
3. Create these Cube objects. Parent the walls under `Boundaries`, the test
   objects under `Obstacles`, and Ground directly under `Environment`:
   - `Ground`: position `0, -0.25, 0`, scale `30, 0.5, 30`.
   - `NorthWall`: position `0, 1.5, 14.5`, scale `30, 3, 1`.
   - `SouthWall`: position `0, 1.5, -14.5`, scale `30, 3, 1`.
   - `WestWall`: position `-14.5, 1.5, 0`, scale `1, 3, 30`.
   - `EastWall`: position `14.5, 1.5, 0`, scale `1, 3, 30`.
   - `CameraTestWall`: position `0, 1.5, 4`, scale `8, 3, 0.5`.
   - `LowCover`: position `-4, 0.75, -2`, scale `4, 1.5, 1`.
   - `HighCover`: position `5, 1.5, 1`, scale `2, 3, 2`.
   - `Ramp`: position `0, 0.4, -7`, rotation `12, 0, 0`, scale `5, 0.5, 8`.
   - `Step01`: position `-8, 0.15, -7`, scale `2, 0.3, 2`.
   - `Step02`: position `-8, 0.3, -5.5`, scale `2, 0.6, 2`.
   - `Step03`: position `-8, 0.45, -4`, scale `2, 0.9, 2`.
4. Keep the BoxCollider on all 12 map primitives and do not add Rigidbodies.
5. Mark Environment and every child as static.
6. Create Ground, Wall, and Obstacle materials under
   `Assets/Art/Materials/TestMap` and assign them to make collision surfaces
   visually distinct.

Expected result: WorldScene contains a bounded movement area with flat ground,
a slope, three step heights, low and high cover, and a dedicated wall for camera
collision testing.

### Configure And Bake World Collision

This is required once for the existing WorldScene and again whenever an
authoritative map collider changes.

1. Exit Play Mode and open `Assets/Scenes/WorldScene.unity`.
2. Select the `Environment` root.
3. Click `Add Component` and add `World Collision Authoring`.
4. Configure the component:
   - World Id: `local-world-1`
   - Chunk Size: `32`
   - Collision Root: drag the same `Environment` object into this field
   - Layer Mask: `3`
5. Save WorldScene.
6. Select `Shooter MMO > World Collision > Bake Open Scene` from Unity's top
   menu.
7. Wait for asset import and script compilation to complete.

Expected result: Unity Console reports `[WORLD COLLISION] Baked world
'local-world-1'` with 12 boxes, four chunks, and a SHA-256 revision. The command
updates:

- `WorldData/Authoring/local-world-1.collision-authoring.json`
- `WorldData/Runtime/Resources/ShooterMmo/WorldCollision/local-world-1/manifest.json`
- Four `chunk_*.bytes` files in the same runtime directory

The bake stops with a red error if the collision root contains enabled,
non-trigger Collider types other than BoxCollider. This is intentional until a
later collision format adds terrain and triangle-mesh chunks.

Verify the checked-in bake from the repository root:

```powershell
dotnet run --project Tools/WorldCollisionCompiler -- `
  WorldData/Authoring/local-world-1.collision-authoring.json `
  WorldData/Runtime/Resources/ShooterMmo/WorldCollision/local-world-1 `
  --verify
```

Expected result: the command reports the same world revision and four verified
chunks. It exits with code 1 if the authoring JSON and runtime data differ.

### Compose WorldScene

1. Create an empty root named `Gameplay` and reset its transform.
2. Add a child named `PlayerSpawn` at position `0, 0, -1` with rotation
   `0, 0, 0`.
3. Drag `LocalPlayer.prefab` into the Gameplay root. WorldSceneContext places it
   at PlayerSpawn when the scene starts.
4. Remove any separate Main Camera from WorldScene. The LocalPlayer prefab owns
   the only gameplay camera and AudioListener.
5. Create an empty child of Gameplay named `EntityPresentationRoot` and reset
   its transform. This object owns runtime views for replicated entities and
   must not be a child of LocalPlayer.
6. Create an empty GameObject named `WorldSceneContext` and add the
   `WorldSceneContext` component.
7. Assign its Local Player field to the LocalPlayer prefab instance and Player
   Spawn Point to PlayerSpawn.
8. Assign `RemotePlayer.prefab` to the Remote Player Prefab field. Drag the
   prefab asset from the Project window, not a temporary scene instance.
9. Assign Entity Presentation Root to the `EntityPresentationRoot` transform.
10. Keep one Directional Light in the scene and save the scene.

Expected result: no gameplay object is created by a runtime bootstrap. Entering
Play Mode places the local prefab instance at PlayerSpawn, connects the camera,
keeps the authored remote prefab available for replicated characters, parents
runtime remote views below `EntityPresentationRoot`, and reports no
missing-reference or input-configuration errors.

The Unity project also contains separate EditMode and PlayMode test assemblies.
Open `Window > General > Test Runner` and run both suites before delivering Unity
changes.

Expected result:

- EditMode validates API parsing, client state, Input Actions, collision resource
  loading, prediction, reconciliation, remote interpolation, both player prefab
  contracts, and authored WorldScene composition.
- PlayMode validates that loading `LoginMenu` creates the persistent client
  bootstrap, realtime client, and runtime login panel.

Before using the Unity client, start these services:

```powershell
docker compose up -d
dotnet run --project AuthService
dotnet run --project WorldServer
```

Then open `shooter-mmorpg-unity-client` in Unity and start from:

```text
Assets/Scenes/LoginMenu.unity
```

The networking packages are declared in `Packages/manifest.json`. If Unity was
open while they were first added, exit Play Mode and wait for package resolution
and script compilation to finish. If Unity still displays the old duplicate
`ShooterMmo.GameProtocol` assembly error after compilation, close and reopen the
project once so its package cache is rebuilt. The persistent bootstrap adds
`RealtimeWorldClient` to its own runtime object. The only new authored movement
reference is the Remote Player Prefab field on WorldSceneContext, described
above.

World snapshots use LiteNetLib's unchanneled `Unreliable` delivery. LiteNetLib
reports these receive events as channel 0 even if a channel number was supplied
to the send overload. Snapshot validation therefore checks the protocol message
type and delivery method, while reliable control messages still validate channel
0 explicitly. After changing realtime transport code, exit Unity Play Mode and
restart WorldServer so both processes use the current protocol implementation.
Protocol version 5 also validates the compiled movement-simulation revision,
server-assigned network entity ids, and reliable entity lifecycle messages.
Rebuild every standalone client after a protocol or simulation revision change.

Scene flow:

- `LoginMenu`
- `CharacterSelect`
- `WorldScene`

Expected result in `LoginMenu`:

- A `Login Menu` panel appears in the Game view.
- `Auth Service` displays `http://localhost:5000`.
- Request timeout displays 10 seconds.
- Realtime timeout displays 10 seconds.

These values are stored in
`Assets/Resources/ShooterMmoClientConfig.asset`. Create build-specific variants
or change the asset before building a client for another environment. Endpoint
fields are no longer editable from the runtime login panel.

Manual Unity test flow:

1. Change `Email` and `Username` to unique values.
2. Click `Register`.
3. Unity loads `CharacterSelect`.
4. Create a character if none exists.
5. Select a character.
6. Select `Local World 1`.
7. Click `Join Selected World`.
8. Unity loads `WorldScene`.

Expected result:

- `WorldScene` shows the selected character on `local-world-1`.
- A local test player spawns at the authored PlayerSpawn in the test map.
- You can move with `WASD`, sprint with `Shift`, jump with `Space`, control the
  camera continuously with the mouse, and hold the right mouse button to aim.
- A small unarmed crosshair dot appears at screen center.
- F1 releases or recaptures the debug cursor, and F2 hides or restores the
  compact bottom-left World Debug panel.
- World Debug displays `Joined` and `127.0.0.1:27015/udp`.
- World Debug displays `Authority: WorldServer`, an increasing server tick, and
  `Simulation: 30 Hz / Snapshots: 15 Hz`.
- Local movement responds immediately through prediction and remains corrected
  to WorldServer snapshots without repeated visible snapping on flat ground.
- `Leave World` receives server acknowledgement, closes UDP, and returns to
  `CharacterSelect`.
- WorldServer logs the joined and released world-session ids.
- `Back To Login` revokes the current AuthService session before clearing local
  client state.

Expected Unity Console sequence for a successful login and world join:

```text
[AUTH] Validating login credentials for username 'player_one'.
[AUTH] Account 'player_one' (<account-id>) logged in.
[CLIENT] Character 'Hero One' (<character-id>) is requesting access to world 'local-world-1'.
[AUTH] AuthService issued a short-lived join ticket for character 'Hero One' and world 'local-world-1'.
[CLIENT] Opening UDP connection to 127.0.0.1:27015/udp.
[CLIENT] UDP transport connected to 127.0.0.1:27015/udp. Sending the short-lived join ticket to WorldServer.
[WORLDSERVER] Account '<account-id>' with character 'Hero One' (<character-id>) connected to world 'local-world-1'. World session '<world-session-id>' controls network entity '<entity-id>'.
[CLIENT] Server-authoritative movement is active at 30 ticks per second with 15 snapshots per second.
```

An authentication, API, transport, timeout, protocol, or WorldServer rejection
appears as a red Unity Console error with the responsible category and stable
error code. Passwords, bearer tokens, join-ticket values, service secrets, and
secret world-session tokens are never written to the console.

### LiteNetLib Package Signature Warning

Unity Package Manager shows a yellow missing-signature warning for LiteNetLib
2.1.4 because the package is delivered by the scoped OpenUPM third-party
registry and is not signed by Unity. It is not a LiteNetLib compilation error.
The project pins the exact version in `Packages/manifest.json` and
`Packages/packages-lock.json`.

No Unity Editor action is required for this warning. Do not select an available
package update without reviewing the upstream changelog and source changes.
Before a production release, review the resolved package source and license, and
consider vendoring the reviewed source or pinning an immutable reviewed upstream
revision if the release threat model requires stronger supply-chain control.

Unity client stability test:

1. Double-click `Register`, `Create Character`, refresh, join, and leave buttons.
2. Verify only one operation starts and controls remain disabled until it ends.
3. Stop WorldServer, click `Join Selected World`, and wait for the realtime
   timeout.
4. Verify CharacterSelect shows a structured realtime network or timeout error
   and does not load WorldScene.
5. Restart WorldServer, join again, and use `Leave World`.
6. Verify only one leave runs, CharacterSelect loads after acknowledgement, and
   WorldServer logs the released session.
7. Verify that an in-flight world snapshot during leave does not produce
   `invalid_snapshot_delivery`.
8. Join again and stop WorldServer while WorldScene is active.
9. Verify the client clears world state and returns to CharacterSelect after the
   unexpected disconnect.
10. Revoke the current account session, then refresh characters.
11. Verify the client clears all local state and returns to LoginMenu after HTTP
    401.

Single active account session test with a standalone build:

1. Restart AuthService so it applies the latest database migration, then start
   WorldServer.
2. Rebuild the standalone development client so it contains the current session
   monitor and realtime disconnect handling.
3. Start the standalone client and enter `local-world-1` with character one.
4. In Unity Play Mode, log into the same account. Selecting character two is
   allowed only after this new login has replaced the first session.
5. Wait up to five seconds and inspect the first client's Unity log.
6. Join `local-world-1` with character two from the newly authenticated client.

Expected result:

- The first client is disconnected, clears its account and world state, and
  returns to LoginMenu.
- Its Console contains an `[AUTH]` message explaining that the account logged in
  from another client and includes `account_session_replaced`.
- The first character disappears from other clients after its exact server
  session is invalidated.
- Only the second account session remains authorized and only its selected
  character can remain connected.

Camera and input test:

1. Move with keyboard WASD, sprint with left Shift, and jump with Space.
2. Jump without sprinting, press Shift while airborne, and keep moving forward.
3. Begin sprinting on the ground, jump while holding Shift, then release and
   press Shift again before landing.
4. Move the mouse without holding a mouse button and test the full upward and
   downward look range.
5. Hold right mouse button and move sideways with A and D.
6. While holding right mouse button, hold Shift and press Space while moving.
7. Begin sprinting, then press and hold right mouse button.
8. Press F1, use the visible cursor, then press F1 again.
9. Press F2 twice to hide and restore the World Debug panel.
10. Walk with a wall between the camera target and desired camera position.

Expected result:

- Movement is supplied by the PlayerInput-owned Input Actions asset rather than
  direct device polling or runtime-created bindings.
- Sprint cannot begin in the air. A ground-started sprint continues through a
  jump only while Shift remains held.
- Falling and landing remain continuous with no visible forced downward snap.
- The LocalPlayer root and PlayerVisual both use local Y zero as the foot plane.
  CharacterBody aligns the controller contact envelope without moving visuals.
- Mouse look remains active without holding Aim.
- The camera can look substantially farther upward and downward without turning
  fully upside down.
- Aim turns the player toward the camera heading and supports strafing.
- Aim blocks sprint and jump. Entering Aim while sprinting immediately returns
  movement to walk speed.
- F1 releases pointer input without causing Aim, then restores captured shooter
  input.
- Camera focus stays at the single `CameraTarget` height without the previous
  duplicated vertical offset.
- Normal camera framing stays over the right shoulder.
- Normal camera distance is 4.75 meters. Aim smoothly tightens the shoulder
  offset, moves to 4.25 meters, and changes FOV from 60 to 45.
- The mouse wheel does not change camera distance.
- The camera moves in front of walls using a spherecast and returns to the desired
  distance when the obstruction clears.
- The unarmed crosshair dot is centered while the cursor is captured and hidden
  while the debug cursor is released.
- The compact World Debug panel stays in the bottom-left corner and F2 controls
  its visibility.

Server-authoritative movement test:

1. Start PostgreSQL, Redis, AuthService, and WorldServer with the commands above.
2. Enter WorldScene through LoginMenu and CharacterSelect. Do not start directly
   from WorldScene for this test.
3. Confirm World Debug shows `Authority: WorldServer` and an increasing Server
   Tick value.
4. Move, rotate, sprint, jump, release movement, and change direction sharply.
5. Walk into the four boundaries, CameraTestWall, LowCover, and HighCover.
6. Walk up and down Ramp, release all movement input while standing halfway up,
   wait for at least three seconds, then traverse Step01, Step02, and Step03.
7. Watch the Unity Console and WorldServer terminal while moving for at least
   30 seconds.
8. Stop WorldServer while the character is moving.

Expected result:

- Input remains responsive because the client predicts the same fixed-step rules
  used by WorldServer.
- Normal snapshots do not cause repeated large position snaps on flat ground,
  the ramp, or steps.
- Sprint cannot begin in the air and jump remains responsive even if the input
  batch containing its edge is duplicated.
- The server tick increases continuously and snapshots acknowledge input without
  protocol errors.
- If input packets stop for more than the configured 500 ms timeout, WorldServer
  neutralizes movement and action state instead of continuing the last command.
- The capsule stops at walls and cover, follows the walkable ramp, and climbs
  the configured step heights in both the predicted and authoritative state.
- Releasing movement input on Ramp leaves the character stationary, and the
  rendered feet remain aligned with the ramp surface while moving and idle.
- Step01, Step02, Step03, and short grounded drops transition smoothly instead
  of snapping the rendered player or camera directly to each discrete height.
- Jump takeoff, airborne falling, and landing from heights above the ground snap
  range remain responsive and are not delayed by step presentation smoothing.
- The client refuses to activate movement and reports a collision revision error
  if its baked world data differs from WorldServer.
- The client refuses to activate movement and reports a simulation revision
  error if its compiled movement rules differ from WorldServer.
- Stopping WorldServer clears the active world session and returns the client to
  CharacterSelect through the existing disconnect recovery flow.

Remote interpolation test with a standalone build:

1. Create a Windows development build containing LoginMenu, CharacterSelect,
   and WorldScene in that order.
2. Run the build and keep Unity Editor available as the second client.
3. Log in with two different accounts and select two different characters on
   `local-world-1`.
4. Join the world from both clients.
5. Move each character while watching it from the other client.

Expected result: each client owns one predicted local player and creates one
presentation-only RemotePlayer instance for the other character under
`Gameplay/EntityPresentationRoot`. The local player is not duplicated under
that root. Remote motion is interpolated instead of jumping directly between 15
Hz snapshots. After a temporary network or frame stall, the remote render clock
restores its intended buffer instead of retaining permanent extra delay.
Leaving or disconnecting sends reliable despawn and removes the corresponding
remote view immediately without waiting for a snapshot timeout.

Direct movement-only test:

1. Open `Assets/Scenes/WorldScene.unity`.
2. Press Play.
3. Move the local test player across the ramp, step tests, cover, and camera wall.

Expected result:

- The scene-authored test map, LocalPlayer prefab instance, camera, and spawn point
  are used without runtime object generation.
- Movement works without starting the backend, but server session data only
  appears after the full login and world join flow.
- Direct scene preview uses Unity CharacterController collision. It is an
  offline authoring check and does not exercise prediction or server authority.

## Related Documentation

- [Project Overview](PROJECT_OVERVIEW.md)
- [Project Architecture](PROJECT_ARCHITECTURE.md)
- [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md)
- [Service Features](SERVICE_FEATURES.md)
- [Game Features](GAME_FEATURES.md)

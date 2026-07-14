# Local Development

Last updated: 2026-07-14

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

Replace every `replace-with-...` placeholder in `.env`. AuthService and
SimulationWorker share `SIMULATION_WORKER_ID` and
`SIMULATION_WORKER_SERVICE_SECRET`. The local worker also receives
`SIMULATION_FLEET_ID`, `SIMULATION_NODE_ID`, `SIMULATION_SHARD_ID`, and
`SIMULATION_WORLD_ID`. These values must match the topology in
`AuthService/Config/appsettings.json`. Both services search their content root
and parent directories for `.env`. Process environment variables and
command-line values take precedence.

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
& .\Tools\Verify-DependencyPolicy.ps1
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet test ShooterMmo.slnx --configuration Release --no-build
```

Expected result:

- Restore accepts every committed `packages.lock.json` file.
- Dependency policy verification confirms exact Unity and NuGet versions, local
  package paths, Unity lock coverage, and the absence of tracked client builds.
- Format reports no files that need changes.
- Build completes with zero warnings and zero errors.
- Unit tests pass.
- The PostgreSQL integration test is skipped unless its dedicated connection is
  configured.

NuGet and GitHub Actions updates are proposed monthly by Dependabot against
`development`. Unity Package Manager updates remain explicit because Dependabot
does not own `Packages/manifest.json`. Update Unity packages in a focused branch,
let Unity rewrite `packages-lock.json`, run both Unity suites, and run the backend
quality gate before merging. `LiteNetLib` must stay aligned between
`SimulationWorker/SimulationWorker.csproj` and the Unity manifest.

Run the deterministic realtime scalability workload separately when changing
interest selection, snapshot encoding, or quota code:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --configuration Release `
  --filter "Category=Load"
```

Expected result: 25,000 entities and 1,000 observers complete spatial selection
and snapshot encode/decode inside the five-second regression budget.

Run the external headless SimulationWorker stress flow before accepting a new
capacity baseline or after changing the realtime loop, collision, interest,
snapshot, or session-heartbeat behavior. The generator uses in-memory stress
identities and tickets, so it does not write accounts or characters to
PostgreSQL. Complete commands, expected results, measurements, and cleanup are
documented in [Simulation Stress Testing](SIMULATION_STRESS_TESTING.md).

Use `Tools/ActiveSimulationBots` when a long-running synthetic population must
share the normal AuthService, SimulationWorker, and shard with a Unity player.
Its account-login bypass is Development-only and in memory, while every bot
still uses an exact-runtime one-time ticket and the complete UDP join, input,
snapshot, and leave flow. Configuration, startup commands, expected behavior,
capacity reservation, Ctrl+C cleanup, and the visual Unity test are documented
in [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md).

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
- PostgreSQL migration concurrency, ticket concurrency, wrong-worker protection,
  reconnect, heartbeat, cross-shard and cross-character exclusion, assignment
  failover, and idempotent release tests pass
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
- SimulationWorker realtime transport: `0.0.0.0:27015/udp`
- SimulationWorker advertised client endpoint: `127.0.0.1:27015/udp`

Local PostgreSQL credentials and SimulationWorker service credentials live only in
the ignored `.env` file. `.env.example` documents every required key without
placing active credentials in application settings or Compose YAML.
`SIMULATION_WORKER_ADVERTISED_HOST` and
`SIMULATION_WORKER_ADVERTISED_UDP_PORT` override the client-facing endpoint
without changing the local bind port.

## Backend Services

Run AuthService in the first terminal:

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

Register a test account and keep the returned account session active:

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

Create a character:

```powershell
$characterBody = @{ name = "Hero One" } | ConvertTo-Json

$character = Invoke-RestMethod http://localhost:5000/api/characters `
  -Method Post `
  -Headers $headers `
  -Body $characterBody `
  -ContentType "application/json"
```

Run SimulationWorker in the second terminal:

```powershell
dotnet run --project SimulationWorker
```

SimulationWorker is a headless .NET Generic Host. It does not expose HTTP routes.
A successful start logs worker `local-simulation-worker-1`, fleet `local-fleet`,
node `local-node-1`, shard `local-shard-1`, World `local-world-1`, UDP port
`27015`, runtime id, realtime protocol version 6, simulation revision, collision
revision, and loaded collision chunks. Every 30 seconds it also logs aggregate
realtime packet, byte, entity, peer, quota, and snapshot counters. The same
interval logs a worker status line with connected real players, synthetic bots,
unauthenticated peers, CPU, working set, packet and payload rates, snapshot
drops, and quota rejections. Detailed tick and phase timing remains in the
server-side performance line.

Routine `HttpClient` request-start, request-send, and successful-response logs
for `AuthServiceClient` are filtered below `Warning`. This keeps bot heartbeat
and release traffic from flooding the SimulationWorker terminal while HTTP
warnings, failures, simulation lifecycle events, worker status, and performance
logs remain visible.

The default resilience settings allow eight concurrent active-session
heartbeats, 120 inbound packets per second with a 240-packet burst, 128 KiB per
second inbound with a 256 KiB burst, and 256 KiB per second of unreliable
snapshots per peer with a 512 KiB burst. Aggregate unreliable snapshot output is
limited to 38 MiB per second with a 4 MiB burst and fair recipient rotation.
Interest enters at 128 meters and exits at 144 meters. Collision loads within
two chunks of active anchors and unloads outside three chunks. Invalid values
fail startup.

Run a one-time SimulationWorker startup health check while the normal worker is
stopped:

```powershell
dotnet run --project SimulationWorker -- --health-check-only
```

Expected result: PostgreSQL is verified through AuthService readiness, Redis is
verified directly with `PING`, collision data is loaded and checksum-validated,
UDP port availability is checked, and the process exits with code `0`. Stop
Redis, stop AuthService, corrupt a collision chunk, or occupy UDP port 27015 and
repeat to verify a nonzero exit:

```powershell
$LASTEXITCODE
```

Verify that SimulationWorker owns its UDP socket:

```powershell
Get-NetUDPEndpoint -LocalPort 27015
```

Expected result: the command lists IPv4 and optionally IPv6 listeners for port
27015. SimulationWorker health is an executable startup check rather than an HTTP
surface.

Simulation topology and registration test:

1. Start AuthService without SimulationWorker and call `GET /api/shards`.
2. Start SimulationWorker and call the endpoint again.
3. Stop SimulationWorker normally with Ctrl+C and call the endpoint again.
4. Start SimulationWorker, then terminate it without graceful shutdown. Wait longer
   than the configured 30-second timeout and call the endpoint again.

Expected result: `local-shard-1` is offline before the first heartbeat, online
while fresh heartbeats arrive, immediately offline after graceful shutdown, and
offline after the heartbeat timeout following an ungraceful stop. The public
response includes World, fleet, region, player count, and capacity, but does not
expose worker host, port, or runtime.

Create a join ticket after SimulationWorker has heartbeated the shard online:

```powershell
$joinBody = @{ characterId = $character.id } | ConvertTo-Json

$join = Invoke-RestMethod http://localhost:5000/api/shards/local-shard-1/join `
  -Method Post `
  -Headers $headers `
  -Body $joinBody `
  -ContentType "application/json"

```

Expected result: `join.shard.id` is `local-shard-1`, `join.shard.worldId` is
`local-world-1`, and `join.endpoint` identifies
`local-simulation-worker-1`, its current runtime, and `127.0.0.1:27015`. The
public shard list did not contain that endpoint.

The ticket is intentionally short-lived and is consumed by the Unity UDP
handshake. Do not attempt to send it to an HTTP SimulationWorker endpoint. The backend
socket tests verify join, leave, and the reliable entity lifecycle with:

```powershell
dotnet test Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj `
  --filter "FullyQualifiedName~RealtimeSimulationServiceTests|FullyQualifiedName~RealtimeEntityLifecycleTests"
```

Expected result: the authenticated client joins, moves, and leaves with exact
session cleanup. The two-client lifecycle test also proves that an existing peer
receives reliable ordered spawn and despawn for the other player.

Optional logout test after the join flow is complete:

```powershell
Invoke-RestMethod http://localhost:5000/api/accounts/logout `
  -Method Post `
  -Headers $headers
```

Expected result: the endpoint returns `204 No Content`. The bearer token then
returns `401 Unauthorized`, pending tickets are consumed, and active simulation
sessions owned by that account session are released.

An authenticated session can similarly revoke an owned session with
`DELETE /api/accounts/sessions/{sessionId}`.

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
3. Do not place a LocalPlayer instance in WorldScene. The player exists only
   after an accepted character join.
4. Remove any separate Main Camera from WorldScene. The runtime LocalPlayer owns
   the only gameplay camera and AudioListener.
5. Create an empty child of Gameplay named `EntityPresentationRoot` and reset
   its transform. This object owns runtime views for replicated entities and
   must not be a child of LocalPlayer.
6. Create an empty GameObject named `WorldSceneContext` and add the
   `WorldSceneContext` component.
7. Assign the `LocalPlayer.prefab` asset to Local Player Prefab and assign
   Player Spawn Point to PlayerSpawn. Drag the prefab asset from the Project
   window, not a scene instance.
8. Assign `RemotePlayer.prefab` to the Remote Player Prefab field. Drag the
   prefab asset from the Project window, not a temporary scene instance.
9. Assign Entity Presentation Root to the `EntityPresentationRoot` transform.
10. Keep one Directional Light in the scene and save the scene.

Expected result: WorldScene contains no LocalPlayer or gameplay camera before a
join. After an accepted join, WorldSceneContext creates one LocalPlayer from the
assigned prefab at PlayerSpawn, connects it to the authoritative movement state,
and connects its camera. Runtime remote views remain below
`EntityPresentationRoot`. Opening WorldScene directly without a joined session
creates no player and reports no missing-reference error.

The Unity project also contains separate EditMode and PlayMode test assemblies.
Open `Window > General > Test Runner` and run both suites before delivering Unity
changes. You can run the same suites outside the Editor with:

```powershell
$env:UNITY_EDITOR_PATH = "C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe"
& .\Tools\Run-UnityTests.ps1
```

Expected result:

- EditMode validates API parsing, client state, Input Actions, collision resource
  loading, prediction, reconciliation, remote interpolation, both player prefab
  contracts, and authored WorldScene composition.
- PlayMode validates that loading `LoginMenu` creates the persistent client
  bootstrap, realtime client, and runtime login panel, and that WorldScene
  without a joined session creates no LocalPlayer.

The CI Unity job uses a Windows self-hosted runner because Unity requires an
installed and activated Editor. To enable it:

1. In the GitHub repository, open `Settings > Actions > Runners` and register a
   Windows x64 self-hosted runner.
2. Add the custom label `unity-6000.5.2f1` to that runner.
3. Install and activate Unity `6000.5.2f1` for the Windows account that runs the
   runner service.
4. Add repository variable `UNITY_CI_ENABLED` with value `true`.
5. If Unity is not installed in the default Unity Hub path, add repository
   variable `UNITY_EDITOR_PATH` containing the full path to `Unity.exe`.

Expected result: pushes and pull requests run both Unity suites, upload XML and
Unity logs as `unity-test-results`, and fail the Unity quality gate on compiler
or test errors. Until the runner and variable exist, the Unity job is shown as
skipped instead of waiting forever for an unavailable runner.

Before using the Unity client, start these services:

```powershell
docker compose up -d
dotnet run --project AuthService
dotnet run --project SimulationWorker
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
`RealtimeSimulationClient` to its own runtime object. The only new authored movement
reference is the Remote Player Prefab field on WorldSceneContext, described
above.

Simulation snapshots use LiteNetLib's unchanneled `Unreliable` delivery. LiteNetLib
reports these receive events as channel 0 even if a channel number was supplied
to the send overload. Snapshot validation therefore checks the protocol message
type and delivery method, while reliable control messages still validate channel
0 explicitly. After changing realtime transport code, exit Unity Play Mode and
restart SimulationWorker so both processes use the current protocol implementation.
Protocol version 6 also validates shard and World identity, the exact placement,
the compiled movement-simulation revision,
server-assigned network entity ids, and reliable entity lifecycle messages.
Rebuild every standalone client after a protocol or simulation revision change.
Standalone build output belongs under ignored `ClientBuilds` or `Builds`
directories and must never be committed.

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
`Assets/Resources/Config/ShooterMmoClientConfig.asset`. Create build-specific
variants or change the asset before building a client for another environment.
Endpoint fields are no longer editable from the runtime login panel.

Manual Unity test flow:

1. Change `Email` and `Username` to unique values.
2. Click `Register`.
3. Unity loads `CharacterSelect`.
4. Create a character if none exists.
5. Select a character.
6. Select `Local Shard 1`.
7. Click `Join Selected Shard`.
8. Unity loads `WorldScene`.

Expected result:

- `WorldScene` shows the selected character on shard `local-shard-1` using
  World `local-world-1`.
- A local test player is instantiated only after the accepted join and starts
  from the authoritative state associated with the authored PlayerSpawn.
- You can move with `WASD`, sprint with `Shift`, jump with `Space`, control the
  camera continuously with the mouse, and hold the right mouse button to aim.
- A small unarmed crosshair dot appears at screen center.
- F1 releases or recaptures the debug cursor, and F2 hides or restores the
  scrollable bottom-left World Client Debug panel.
- World Debug displays `Joined` and `127.0.0.1:27015/udp`.
- World Debug displays shard `local-shard-1`, World `local-world-1`, worker
  `local-simulation-worker-1`, and the current runtime id.
- World Debug displays an increasing observed server tick, the configured 30 Hz
  simulation and 15 Hz snapshot rates, client FPS and frame timing, prediction
  backlog, reconciliation statistics, ping, snapshot age, observed packet and
  payload rates, estimated snapshot loss, and client-known entities.
- World Debug does not display SimulationWorker CPU, memory, capacity, or total
  player and bot populations. Those values appear only in the SimulationWorker
  terminal and metrics surface.
- Local movement responds immediately through prediction and remains corrected
  to SimulationWorker snapshots without repeated visible snapping on flat ground.
- `Leave Shard` receives server acknowledgement, closes UDP, and returns to
  `CharacterSelect`.
- SimulationWorker logs the joined and released simulation-session ids.
- `Back To Login` revokes the current AuthService session before clearing local
  client state.

Expected Unity Console sequence for a successful login and simulation join:

```text
[AUTH] Validating login credentials for username 'player_one'.
[AUTH] Account 'player_one' (<account-id>) logged in.
[CLIENT] Character 'Hero One' (<character-id>) is requesting access to shard 'local-shard-1'.
[AUTH] AuthService issued a short-lived join ticket for character 'Hero One' on shard 'local-shard-1'.
[CLIENT] Opening UDP connection to SimulationWorker 'local-simulation-worker-1' runtime '<worker-runtime-id>' at 127.0.0.1:27015/udp for shard 'local-shard-1'.
[CLIENT] UDP transport connected to 127.0.0.1:27015/udp. Sending the short-lived join ticket to SimulationWorker.
[SIMULATION] Account '<account-id>' with character 'Hero One' (<character-id>) connected to shard 'local-shard-1' for world 'local-world-1' through worker 'local-simulation-worker-1' runtime '<worker-runtime-id>'. Simulation session '<simulation-session-id>' controls network entity '<entity-id>'.
[CLIENT] Server-authoritative movement is active at 30 ticks per second with 15 snapshots per second.
```

An authentication, API, transport, timeout, protocol, or SimulationWorker rejection
appears as a red Unity Console error with the responsible category and stable
error code. Passwords, bearer tokens, join-ticket values, service secrets, and
secret simulation-session tokens are never written to the console.

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
3. Stop SimulationWorker, click `Join Selected Shard`, and wait for the realtime
   timeout.
4. Verify CharacterSelect shows a structured realtime network or timeout error
   and does not load WorldScene.
5. Restart SimulationWorker, join again, and use `Leave Shard`.
6. Verify only one leave runs, CharacterSelect loads after acknowledgement, and
   SimulationWorker logs the released session.
7. Verify that an in-flight simulation snapshot during leave does not produce
   `invalid_snapshot_delivery`.
8. Join again and stop SimulationWorker while WorldScene is active.
9. Verify the client clears shard state and returns to CharacterSelect after the
   unexpected disconnect.
10. Revoke the current account session, then refresh characters.
11. Verify the client clears all local state and returns to LoginMenu after HTTP
    401.

Single active account session test with a standalone build:

1. Restart AuthService so it applies the latest database migration, then start
   SimulationWorker.
2. Rebuild the standalone development client so it contains the current session
   monitor and realtime disconnect handling.
3. Start the standalone client and enter `local-shard-1` with character one.
4. In Unity Play Mode, log into the same account. Selecting character two is
   allowed only after this new login has replaced the first session.
5. Wait up to five seconds and inspect the first client's Unity log.
6. Join `local-shard-1` with character two from the newly authenticated client.

Expected result:

- The first client is disconnected, clears its account and shard state, and
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
- The World Client Debug panel stays in the bottom-left corner, scrolls when the
  viewport is small, and F2 controls its visibility.

Server-authoritative movement test:

1. Start PostgreSQL, Redis, AuthService, and SimulationWorker with the commands above.
2. Enter WorldScene through LoginMenu and CharacterSelect. Do not start directly
   from WorldScene for this test.
3. Confirm World Debug shows server-authoritative client prediction and an
   increasing observed server tick value.
4. Move, rotate, sprint, jump, release movement, and change direction sharply.
5. Walk into the four boundaries, CameraTestWall, LowCover, and HighCover.
6. Walk up and down Ramp, release all movement input while standing halfway up,
   wait for at least three seconds, then traverse Step01, Step02, and Step03.
7. Watch the Unity Console and SimulationWorker terminal while moving for at least
   30 seconds.
8. Stop SimulationWorker while the character is moving.

Expected result:

- Input remains responsive because the client predicts the same fixed-step rules
  used by SimulationWorker.
- Normal snapshots do not cause repeated large position snaps on flat ground,
  the ramp, or steps.
- Sprint cannot begin in the air and jump remains responsive even if the input
  batch containing its edge is duplicated.
- The server tick increases continuously and snapshots acknowledge input without
  protocol errors.
- If input packets stop for more than the configured 500 ms timeout, SimulationWorker
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
  if its baked world data differs from SimulationWorker.
- The client refuses to activate movement and reports a simulation revision
  error if its compiled movement rules differ from SimulationWorker.
- Stopping SimulationWorker clears the active simulation session and returns the client to
  CharacterSelect through the existing disconnect recovery flow.

Remote interpolation test with a standalone build:

1. Create a Windows development build containing LoginMenu, CharacterSelect,
   and WorldScene in that order.
2. Run the build and keep Unity Editor available as the second client.
3. Log in with two different accounts and select two different characters on
   `local-shard-1`.
4. Join the shard from both clients.
5. Move each character while watching it from the other client.

Expected result: each client owns one predicted local player and creates one
presentation-only RemotePlayer instance for the other character under
`Gameplay/EntityPresentationRoot`. The local player is not duplicated under
that root. Remote motion is interpolated instead of jumping directly between 15
Hz snapshots. After a temporary network or frame stall, the remote render clock
restores its intended buffer instead of retaining permanent extra delay.
Leaving or disconnecting sends reliable despawn and removes the corresponding
remote view immediately without waiting for a snapshot timeout.

WorldScene lifecycle test without a join:

1. Open `Assets/Scenes/WorldScene.unity`.
2. Press Play.

Expected result:

- The scene-authored map, spawn point, prefab references, and presentation root
  remain available.
- No LocalPlayer, gameplay camera, or local input object is created.
- World Debug reports that no active simulation session exists.

Use the full login and simulation join flow above to test movement across the
ramp, steps, cover, and camera wall. WorldScene movement is intentionally not an
offline preview because a local player represents an accepted world presence.

## Related Documentation

- [Project Overview](PROJECT_OVERVIEW.md)
- [Project Architecture](PROJECT_ARCHITECTURE.md)
- [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md)
- [Service Features](SERVICE_FEATURES.md)
- [Game Features](GAME_FEATURES.md)

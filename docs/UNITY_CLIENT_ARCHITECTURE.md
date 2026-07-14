# Unity Client Architecture

Last updated: 2026-07-14

## Purpose

This document is the source of truth for the current Unity client architecture.
It describes runtime responsibilities, dependencies, state, scene flow, API
handling, and local gameplay controls.

## Project Boundary

The Unity project lives in `shooter-mmorpg-unity-client` and targets Unity
6000.5.2f1. Runtime code is compiled into `ShooterMmo.Runtime`. EditMode and
PlayMode tests use separate assemblies.

The current runtime UI is intentionally temporary while the custom UI is being
designed. The client bootstrap, state ownership, API layer, scene lifecycle,
input, camera, and gameplay foundations are long-term systems and must be built
to production-quality structural standards from the start.

The first world environment uses simple, scene-authored Unity primitives so
scene flow and controls remain testable. The map, local player, camera, input
asset, and spawn point are real Unity assets and are never created by runtime
generation code.

## Runtime Structure

```text
ShooterMmoClientBootstrap
  +-- ShooterMmoClientSession
  +-- ShooterMmoApiClient
  +-- RealtimeSimulationClient
  +-- ShooterMmoClientConfig
  +-- scene lifecycle recovery

Scene panel
  +-- ClientOperationState
  +-- API coroutine flow
  +-- ClientSessionRecovery

WorldSceneContext
  +-- scene-authored LocalPlayer prefab reference
  +-- scene-authored PlayerSpawn
  +-- scene-authored RemotePlayer prefab reference
  +-- scene-authored EntityPresentationRoot
  +-- runtime LocalPlayer instance after an accepted join
  +-- runtime remote-player view instances below EntityPresentationRoot

LocalPlayer prefab
  +-- CharacterController
  +-- CharacterBody
  +-- PlayerInput and LocalPlayerInput
  +-- LocalPlayerController
  +-- PlayerVisual
  +-- CameraTarget
  +-- LocalPlayerCamera
      +-- Camera and AudioListener
      +-- ThirdPersonCameraController

Realtime movement
  +-- NetworkMovementSession
  +-- UnityWorldCollisionLoader
  +-- baked WorldData chunks
  +-- ClientMovementPrediction
  +-- shared PlayerMovementSimulation
  +-- RemoteMovementInterpolation
  +-- RemotePlayerView
```

## Persistent Client Bootstrap

`ShooterMmoClientBootstrap` creates the persistent runtime root and survives scene
changes. It initializes configuration, owns one `RealtimeSimulationClient`, observes
scene transitions, and performs fallback leave when an active WorldScene is left
outside the normal panel flow.

The fallback release is bound to the exact simulation-session identity. Completion of
an older request cannot clear a newer local reconnect state. If graceful leave
fails after the scene already changed, the client closes UDP so SimulationWorker can
run disconnect cleanup.

## Configuration

`ShooterMmoClientConfig` is a ScriptableObject loaded from
`Assets/Resources/Config/ShooterMmoClientConfig.asset`. Its C# definition lives
in `Assets/Scripts/Config`. It currently contains:

- AuthService base URL.
- HTTP request timeout in seconds.
- Realtime operation timeout in seconds.
- Account-session validation interval in seconds.

SimulationWorker host, runtime, and UDP port come from AuthService's short-lived
shard placement response. They are not part of the public shard list and are not
duplicated in client configuration. Different environments should use
build-specific configuration assets or a future build configuration pipeline.

## Client Session State

`ShooterMmoClientSession` stores the current in-memory client view:

- AuthService endpoint.
- Account and bearer session details.
- Selected character and shard.
- Active simulation-session, shard, World, worker, and worker-runtime metadata.

This state is a client cache, not an authority. AuthService and SimulationWorker remain
authoritative. The persistent bootstrap validates an authenticated account
session every five seconds, including outside WorldScene. HTTP 401 clears all
local session state before loading LoginMenu.

## API Layer

`ShooterMmoApiClient` wraps UnityWebRequest in coroutines. It is responsible for:

- Request serialization and response deserialization.
- Bearer authentication headers.
- Configured request timeouts.
- Validation of expected response payloads.
- Structured mapping of HTTP, timeout, network, and invalid-response failures.

`ShooterMmoApiError` carries the failure kind, HTTP status, stable server code,
message, and correlation id. Panels display a safe message rather than raw JSON.
Array responses are validated through `JsonArrayUtility` before they reach UI
state.

## Client Diagnostics

`ClientLog` is the single Unity Console formatting boundary for runtime flow
diagnostics. It assigns one stable category prefix to each entry:

- `[AUTH]` for account authentication, session logout, and join-ticket issuance.
- `[CLIENT]` for local API flow, transport state, and recovery actions.
- `[SIMULATION]` for accepted simulation joins, accepted leaves, and server
  rejections.

Information entries confirm expected state transitions. Failed operations use
Unity error entries, while recovery details that follow an already reported
failure use warnings. Line breaks are normalized before output so remote error
messages cannot create misleading log entries.

Passwords, bearer tokens, join-ticket values, service secrets, and secret
simulation-session tokens must never be passed to `ClientLog`. Stable account,
character, shard, World, worker, runtime, and public simulation-session
identifiers may be logged for local diagnosis.

## Realtime Networking Layer

`RealtimeSimulationClient` is a persistent MonoBehaviour owned by the bootstrap. It
wraps one LiteNetLib `NetManager` and polls network events on Unity's main thread.
It owns an explicit connection state:

```text
Disconnected -> Connecting -> Joining -> Joined -> Leaving -> Disconnected
```

Join and leave are coroutine operations with configured deadlines, structured
errors, and one completion path. A join consumes the complete `JoinShardResponse`,
checks ticket expiry, protocol version, simulation revision, shard identity,
World identity, and exact worker runtime placement, then connects to the provided
UDP endpoint. It sends the short-lived join ticket in a versioned reliable
ordered packet. Join acceptance must match the placed character, shard, World,
simulation revision, and collision revision before non-secret metadata is copied
into `ShooterMmoClientSession`.

Unexpected disconnect clears the active shard view and returns an authenticated
player to CharacterSelect. Join rejection stays in CharacterSelect and displays
the structured server code. Protocol decoding rejects wrong versions, invalid
types, oversized values, and trailing data.

If another client logs into the same account, SimulationWorker can send
`account_session_replaced` over the reliable control path. The client aborts its
transport, clears the entire account session, logs an `[AUTH]` error, and loads
LoginMenu. The periodic AuthService validation provides the same recovery when
the displaced client is not connected to a shard.

Protocol version 6 uses two explicit LiteNetLib channels plus unchanneled
snapshot delivery:

- Channel 0 uses reliable ordered delivery for join, leave, disconnect, entity
  spawn, and entity despawn control messages.
- Channel 1 uses sequenced delivery for redundant movement input batches.
- Simulation snapshot chunks use LiteNetLib's unchanneled `Unreliable` delivery.
  LiteNetLib reports these packets with receive channel 0. Server tick, snapshot
  sequence, message type, and chunk metadata provide application-level ordering
  without confusing snapshots with reliable control messages.
- Correctly delivered snapshots that overtake join acceptance or remain in
  flight during and immediately after leave are ignored outside the `Joined`
  state. They are not protocol failures because LiteNetLib delivery methods do
  not provide ordering relative to each other.

Every accepted simulation session identifies the local player's server-assigned
nonzero network entity id. `RealtimeSimulationClient` owns an in-memory registry of
the reliable spawn baseline and subsequent spawn or despawn changes. The
registry survives the CharacterSelect to WorldScene transition, so entities
that spawned before scene loading are still presented. Conflicting reuse of an
active entity id is treated as a protocol failure. Snapshots never create or
remove entities. They update only ids already admitted by the reliable
lifecycle.

`NetworkMovementSession` is created only from a validated join response. It owns
the server-provided movement settings and initial state used by the client. The
client does not maintain a second editable copy of movement speed, tick rate,
gravity, bounds, or snapshot frequency for an active network session.
The join response must also match the client's compiled movement-simulation
revision and baked collision revision. Either mismatch aborts activation with a
structured client error.

`GameProtocol/Runtime` is installed as a local Unity package. LiteNetLib is
installed from OpenUPM. The runtime assembly references both by assembly name.
LiteNetLib is pinned to version 2.1.4 in both the project manifest and package
lock. Unity displays a missing-signature warning because the scoped third-party
registry package is not signed by Unity. This warning does not indicate a
compile or runtime failure, but every package upgrade still requires source and
changelog review.

## Scene And UI Flow

The build flow is:

```text
LoginMenu -> CharacterSelect -> WorldScene
     ^              ^               |
     +--------------+---------------+
```

### LoginMenu

`LoginMenuPanel` creates the temporary login UI, runs registration or login, and
loads CharacterSelect after successful authentication.

### CharacterSelect

`CharacterSelectPanel` loads characters and shards sequentially. Each shard row
shows its region, fleet, status, active players, and capacity. The panel supports
character creation, selection, refresh, logout, and simulation join. Join ticket
creation and the UDP handshake run inside one coroutine so the operation cannot
be partially overlapped by another click.

### WorldScene

`WorldScenePanel` displays scrollable client-observed diagnostics in the bottom-left
corner. F2 toggles its visibility through the Player Input Actions asset. Its
Leave Shard action waits for an exact-session UDP leave acknowledgement before
loading CharacterSelect. It displays connection state, active UDP endpoint,
frame timing, Unity memory, client prediction backlog, reconciliation counts,
ping, snapshot health, observed payload rates, and client-known entities.

`RealtimeSimulationClient` counts application-level realtime packets and bytes,
unique snapshot sequences, snapshot chunks, and estimated missing sequences.
`WorldDebugTelemetryTracker` derives rolling frame percentiles and half-second
network rates from those local counters. The panel labels payload rates as
application payload rather than total socket bandwidth.

The gameplay protocol does not carry SimulationWorker process diagnostics.
Worker CPU, memory, population, capacity, and internal timing stay in the worker
metrics and logs instead of being disclosed to every connected game client.

## Operation Serialization

Each panel uses `ClientOperationState` instead of a boolean busy flag. Only one
named operation can own a panel at a time. Relevant controls are disabled until
the operation completes, preventing duplicate clicks and overlapping refresh,
join, leave, or authentication requests.

## Unauthorized Recovery

`ClientSessionRecovery` centralizes HTTP 401 handling:

1. Clear account, selection, and active simulation-session state.
2. Load LoginMenu.
3. Stop the failed panel flow from continuing with stale data.

The stable `account_session_replaced` code identifies a login from another
client. Recovery writes a specific temporary Unity Console message rather than
presenting it as an ordinary expired session.

Logout is different from recovery. A normal Back to Login action first asks
AuthService to revoke the active account session, then clears local state.

## Local Player Lifecycle

`WorldSceneContext` is a scene composition root. It validates serialized scene
and prefab references, then creates one LocalPlayer prefab instance only when
WorldScene opens with an accepted realtime join and movement session. It connects
that runtime player to server-authoritative movement, consumes the cached
reliable entity baseline, routes entity snapshots, and connects the player-owned
camera to input and CameraTarget. Opening WorldScene without an active joined
session creates no local player. The context never selects a global camera and
never generates a player asset, map object, material, light, or collider.
Runtime instances of the explicitly authored RemotePlayer prefab are created
only from reliable player-entity spawn messages and are parented under the
scene-authored `EntityPresentationRoot`. The runtime local player remains
separate from that presentation hierarchy and is destroyed with WorldScene.

`LocalPlayerInput` reads the `PlayerInput` instance owned by the LocalPlayer
prefab. The referenced Input Actions asset defines movement, sprint, jump, aim,
look, debug cursor, and debug panel bindings. This keeps device bindings out of
gameplay and UI code and allows future rebinding and control-scheme work without
changing those systems.

`CharacterBody` is the reusable collision foundation for local players, remote
players, and NPCs. The character root is its logical ground point. CharacterBody
keeps skin width proportional to radius and offsets the CharacterController
center so the lower edge of its contact envelope is local Y zero. Presentation
objects are not moved to compensate for physics margins. Teleportation places
the root directly at the requested ground position. A future network or AI
movement driver can therefore reuse the same body without depending on local
input.

Authenticated movement keeps a separate collision pose and rendered pose on
walkable slopes. The shared motor stores the slope-aware capsule support height
needed to avoid penetration and downhill drift. `GroundedMovementPresentation`
queries the same baked collision world and removes the geometric slope support
offset from local and remote render poses without replacing their interpolated
height. Its `GroundedVerticalPresentation` applies a bounded 100 ms blend to
small height changes on flat walkable support, covering steps and short grounded
drops. Ramps, jumps, airborne movement, teleports, and large corrections bypass
this extra blend. Presentation never feeds back into prediction, reconciliation,
input packets, or SimulationWorker state.

`LocalPlayerController` retains an offline CharacterController path for isolated
prefab testing, but WorldScene does not create an offline player. A runtime
WorldScene player always uses `ClientMovementPrediction` and the exact shared
fixed-step capsule simulation and baked collision world. Normal movement faces
its travel direction. Aim faces the camera heading so left and right movement
become shooter-style strafing. Sprint is a grounded state transition: it may
remain active through a jump but cannot start while airborne. Both isolated
prefab testing and authenticated prediction preserve takeoff momentum and facing
while airborne. Movement input resumes only after grounded state is restored,
while the third-person camera remains independently controllable.
`RefreshCharacterDimensions` remains the runtime entry point when a future
character system changes collider dimensions.

For network movement, input is sampled at the server-provided tick rate and each
command receives an input sequence and client tick. The local state is predicted
immediately and up to four newest unacknowledged commands are sent in each batch.
On an authoritative snapshot, `ClientMovementPrediction` removes acknowledged
commands, starts from the server state, and replays the remaining commands.
`LocalMovementPresentation` interpolates consecutive predicted states at the
render frame rate, while `LocalPlayerController` smooths reconciliation
corrections smaller than three meters and applies larger corrections immediately.
The presentation interpolation never feeds positions back into input packets or
the shared simulation. The shared simulation source is installed as
`com.shootermmo.game-simulation` and contains no Unity dependencies.

`UnityWorldCollisionLoader` loads the selected world's manifest from the local
`com.shootermmo.world-data` package. `UnityWorldCollisionStream` then loads and
checksum-validates chunks around the initial player, local prediction, and
visible remote entities. It retains a larger chunk ring before unloading, which
prevents boundary churn. The join response carries SimulationWorker's collision
revision, and `NetworkMovementSession` does not start when the local revision
differs. A missing, corrupt, or coordinate-mismatched streamed chunk closes the
active session through the normal structured client failure path.

`ClientMovementPrediction`, reconciliation, grounded presentation, and remote
presentation all query the same mutable `ChunkedStaticCollisionWorld`. Loading
changes which authored chunks are resident but never changes SimulationWorker
authority or feeds presentation positions back into prediction.

`WorldCollisionAuthoring` defines the world id, chunk size, collision root, and
layer mask on an authored scene object. The Editor command
`Shooter MMO > World Collision > Bake Open Scene` scans enabled, non-trigger
BoxColliders below that root and writes neutral authoring JSON plus versioned
binary resources into `WorldData`. Unsupported collider types fail the bake
explicitly. Runtime code never scans the Unity scene or treats PhysX as network
authority.

`RemotePlayerView` is presentation-only. It has no input, camera, audio listener,
rigidbody, or collider. It stores the network entity id, persistent character
id, display name, and presentation archetype supplied by the spawn message. It
buffers server states in
`RemoteMovementInterpolation` and advances an adaptive monotonic frame-rate
render clock approximately 100 ms behind the latest server tick. The clock uses
bounded catch-up and slow-down corrections during normal delivery. After a
larger network stall it restores the intended buffer delay instead of retaining
permanent extra latency. A reliable despawn removes the matching view
immediately. Missing unreliable snapshots never decide entity lifetime.

`ThirdPersonCameraController` consumes look input continuously while the gameplay
cursor is captured. F1 switches between captured shooter input and a released
debug cursor. The component and Camera live on the LocalPlayerCamera child owned
by the LocalPlayer prefab. Normal framing uses a 1.1 meter right-shoulder offset,
a 0.45 meter vertical offset, a 4.75 meter follow distance, and a 12 degree
initial pitch. Aim changes the offsets to 1.3 and 0.35 meters, moves the camera
to 4.25 meters, and reduces FOV from 60 to 45 degrees using a
frame-rate-independent transition. Player-controlled camera zoom is not
supported. Vertical input is clamped from -50 to 75 degrees. Camera orbit uses
the single CameraTarget pivot. A
non-allocating spherecast follows the complete offset camera path, moves the
camera in front of obstructions, and filters the local player hierarchy.

`CrosshairDefinition` is a gameplay-facing value type with shape, size,
thickness, gap, and color. `CrosshairController` owns the active definition and
dynamic spread and exposes change APIs for future equipment and weapon systems.
`CrosshairPanel` only presents that state through the current temporary IMGUI
layer. The controller defaults to the unarmed dot and does not depend on the UI
implementation.

The current network simulation collides with all 12 baked BoxColliders in the
test map, including the rotated ramp, steps, cover, and boundaries. The capsule
dimensions, slope limit, step height, ground snap distance, and substep budget
come from the validated server join settings. Direct scene preview still uses
Unity CharacterController against the authored scene colliders as an offline
authoring check. Authenticated movement does not use CharacterController to
decide network position.

## Test Architecture

EditMode tests cover client session cleanup, operation serialization, structured
Problem Details parsing, diagnostic prefix formatting, configuration loading,
PlayerInput action binding, invalid array rejection, dynamic crosshair
configuration, local reconciliation, redundant input batches, remote
interpolation, collision resource loading, authored player prefab contracts, and
WorldScene composition.

PlayMode tests verify that loading LoginMenu creates the persistent client
bootstrap, persistent realtime client, and runtime login panel.

Manual flows and expected results are documented in
[Local Development](LOCAL_DEVELOPMENT.md).

## Extension Rules

- Keep HTTP serialization and failure mapping inside the API layer.
- Keep realtime transport state and packet handling inside
  `RealtimeSimulationClient`, not scene panels.
- Keep deterministic movement rules in `GameSimulation`, not in transport or
  presentation components.
- Keep authored collision in `WorldData`; never duplicate scene geometry as
  hand-maintained server constants.
- Keep prediction and reconciliation separate from remote interpolation.
- Keep persistent cross-scene state in the client session, not scene panels.
- Keep one operation owner per panel until a more explicit navigation state
  machine replaces it.
- Route every WorldScene exit through exact-session UDP leave or disconnect
  fallback.
- Add player-facing behavior to [Game Features](GAME_FEATURES.md) when it becomes
  implemented.
- Update this document when client ownership, scene flow, networking, input, or
  persistent runtime structure changes.
- Temporary implementation shortcuts are permitted only inside the replaceable
  UI presentation layer. UI-independent state and behavior must not be embedded
  in temporary panels.
- Every delivered Unity change must explicitly state whether manual Editor work
  is required. Required Inspector, scene, asset, package, input, or build-setting
  steps must be listed with expected results.

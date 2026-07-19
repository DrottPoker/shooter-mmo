# Unity Client Architecture

Last updated: 2026-07-19

## Purpose

This document is the source of truth for the current Unity client architecture.
It describes runtime responsibilities, dependencies, state, scene flow, API
handling, and local gameplay controls.

The implemented Phase 12 client and Editor contract for world actors is defined
in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md). Later capability
business behavior and final presentation remain explicitly outside the current
runtime claim.

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
  +-- InventoryClientController
      +-- ClientItemCatalog
      +-- InventoryClientState
      +-- InventoryOperationJournal
  +-- ShooterMmoClientConfig
  +-- scene lifecycle recovery

Scene panel
  +-- ClientOperationState
  +-- API coroutine flow
  +-- ClientSessionRecovery

TemporaryInventoryPanel
  +-- replaceable runtime uGUI presentation
  +-- persistent InventoryClientController state
  +-- no item custody or optimistic mutation

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
changes. It initializes configuration, owns one `RealtimeSimulationClient` and
one `InventoryClientController`, observes scene transitions, and performs
fallback leave when an active World scene is left outside the normal panel flow.

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
- A direct TextAsset reference to the gameplay runtime item catalog supplied by
  the local WorldData package.

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
session every five seconds, including outside the active World scene. HTTP 401
clears all local session state before loading LoginMenu.

## API Layer

`ShooterMmoApiClient` wraps UnityWebRequest in coroutines. It is responsible for:

- Request serialization and response deserialization.
- Bearer authentication headers.
- Configured request timeouts.
- Validation of expected response payloads.
- Structured mapping of HTTP, timeout, network, and invalid-response failures.
- Owned-character complete item-state, focused Bank, and focused Recovery
  Storage reads used by the persistent inventory controller.

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

Protocol version 11 uses two explicit LiteNetLib channels plus unchanneled
snapshot delivery:

- Channel 0 uses reliable ordered delivery for join, leave, disconnect, entity
  lifecycle, carry-state updates, and item-operation intent and result control
  messages.
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
registry survives the transition from CharacterSelect to the selected World
scene, so entities
that spawned before scene loading are still presented. Conflicting reuse of an
active entity id is treated as a protocol failure. Snapshots never create or
remove entities. They update only ids already admitted by the reliable
lifecycle.

`NetworkMovementSession` is created only from a validated join response. It owns
the server-provided movement settings, initial state, and admission-fenced carry
tuple used by the client. The tuple contains the monotonic item-state revision,
unitless carried weight, and capacity. Reliable carry-state updates advance the
tuple only when their revision is newer. The client does not maintain a second
editable copy of movement speed, tick rate, gravity, bounds, snapshot frequency,
weight, or capacity for an active network session.
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
LoginMenu -> CharacterSelect -> WorldSceneCatalog[accepted WorldId]
     ^              ^                         |
     +--------------+-------------------------+
```

`Assets/Resources/Worlds/world-scene-catalog.json` is the client-owned mapping
from authoritative `WorldId` to an authored Unity scene name. AuthService and
SimulationWorker never send a scene path. CharacterSelect resolves the selected
shard's World before requesting a join ticket and rejects an unknown mapping or
a scene missing from the build. It resolves the authoritative placement World
again before opening UDP, so a shard list cached across an offline rebind cannot
load the previous scene. The checked-in catalog maps `development-world-1` to
`DevelopmentWorld1` and `development-world-2` to `DevelopmentWorld2`. Both
scenes are authored and included in Build Profiles. CharacterSelect still
rejects a mapped scene that is absent from the build.

### LoginMenu

`LoginMenuPanel` creates the temporary login UI, runs registration or login, and
loads CharacterSelect after successful authentication.

### CharacterSelect

`CharacterSelectPanel` loads characters and shards sequentially. Each shard row
shows its region, fleet, status, active players, and capacity. The panel supports
character creation, selection, refresh, logout, and simulation join. Join ticket
creation and the UDP handshake run inside one coroutine so the operation cannot
be partially overlapped by another click.

### World Scenes

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
and prefab references plus the active session's exact World-to-scene mapping,
then creates one LocalPlayer prefab instance only when a catalog World scene
opens with an accepted realtime join and movement session. It connects
that runtime player to server-authoritative movement, consumes the cached
reliable entity baseline, routes entity snapshots, and connects the player-owned
camera to input and CameraTarget. Opening a World scene without an active joined
session creates no local player. The context never selects a global camera and
never generates a player asset, map object, material, light, or collider.
Runtime instances of the explicitly authored RemotePlayer prefab are created
only from reliable player-entity spawn messages and are parented under the
scene-authored `EntityPresentationRoot`. The runtime local player remains
separate from that presentation hierarchy and is destroyed with the active
World scene.

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
prefab testing, but registered World scenes do not create an offline player. A
runtime World-scene player always uses `ClientMovementPrediction` and the exact
shared fixed-step capsule simulation and baked collision world. Normal movement faces
its travel direction. Aim faces the camera heading so left and right movement
become shooter-style strafing. Sprint is a grounded state transition: it may
remain active through a jump but cannot start while airborne. Both isolated
prefab testing and authenticated prediction preserve takeoff momentum and facing
while airborne. Movement input resumes only after grounded state is restored,
while the third-person camera remains independently controllable.
`RefreshCharacterDimensions` remains the runtime entry point when a future
character system changes collider dimensions.

Authenticated prediction also consumes the exact carry tuple owned by
`NetworkMovementSession`. The shared simulation allows sprint at exactly 100
percent capacity, disables sprint above it, and linearly reduces movement to
`0.20` at the 140 percent hard cap. A reliable newer revision is applied before
subsequent prediction and reconciliation replay. The temporary F2 panel exposes
weight, capacity, item-state revision, movement percentage, and sprint
eligibility for observation without becoming inventory state or authority.

`RealtimeSimulationClient.TrySendItemOperation` accepts only a typed Phase 8
protocol intent while joined and sends it on the reliable ordered control path.
Committed or rejected results are decoded only in the joined state. A committed
newer carry revision advances `NetworkMovementSession` before
`ItemOperationCompleted` is raised. The transport does not own item collections.
The persistent `InventoryClientController` consumes the result, correlates its
operation id and kind, and refreshes authoritative HTTP state before permitting
another mutation. A duplicate completion for the last finalized operation is
ignored safely.

`ClientItemCatalog` compiles and revision-checks the bundled gameplay TextAsset,
loads the presentation catalog once through its existing Resources cache, and
indexes definitions and equipment slots by stable id. Gameplay and presentation
source revisions must match before item UI can render. Every HTTP snapshot must
also match the bundled gameplay revision. A mismatch clears renderable item
state and produces `item_catalog_update_required` instead of accepting stale
definitions. Icons, localization keys, fallback labels, and optional prefab
presentation keys resolve locally. HTTP responses carry only stable definition
ids and instance state.

Durable item-instance revisions are non-negative, and a newly granted or split
item begins at revision `0`. Unity `JsonUtility` can materialize an exact
all-default object for a JSON `null` in an optional item or equipped-Bag field.
The snapshot mapper normalizes only that exact optional placeholder back to no
item. Required Recovery delivery items and every partially populated malformed
item still fail validation.

`InventoryClientState` owns immutable complete and focused snapshots plus the
latest observed character, Bank, and Recovery revisions. A focused response may
advance its slice, but any newer focused revision marks the complete snapshot
incoherent and disables mutation until a full refresh reaches the newest known
revision. Same-revision content disagreement is an error. Older responses are
ignored. Initial join and reconnect trigger a complete refresh automatically.

`InventoryOperationJournal` permits one pending mutation, retains its generated
operation id and rejection refresh scope, and never changes custody locally.
Successful results always cause a complete refresh to the committed revision.
Stale and concurrency rejections first refresh their focused slice where safe,
then restore complete coherence. A disconnect with a pending operation marks
the local result uncertain, clears the journal, and relies on reconnect refresh
to resolve the committed outcome.

### Inventory UI

`TemporaryInventoryPanel` is the only replaceable part of the Phase 9 inventory
implementation. It creates runtime uGUI below a World-scene controller and reads
the persistent state without embedding API, protocol, revision, or item-rule
ownership. `B` toggles character storage only, `C` toggles equipment plus
character storage, and `I` toggles the complete Development view with contextual
storage. Pressing a different inventory key switches modes, `Escape` closes the
panel, and the camera releases pointer capture while it is open. Equipment,
Context, and Character Inventory each own a separate panel root at fixed anchors.
Mode changes affect root visibility only and never rewrite another module's
layout.

The stable layout has equipment on the left, a contextual container in the
upper-right, and character storage in the lower-right. Permanent inventory,
equipped Bag contents, and Secure Container remain visible while Bank or
Recovery Storage is selected. Bank and Recovery may be inspected through global
owning-account reads. Every mutation still travels through the joined
SimulationWorker and is committed through AuthService authority.

`InventoryDragCoordinator`, `InventoryDragSource`, typed
`InventoryDragPayload`, and `InventoryDropTarget` form the reusable uGUI
interaction layer. Relocation, equip, unequip, split, merge, atomic ordinary
container-slot swap, and complete Recovery claims start only from a drop. A drop
onto an occupied slot merges compatible stacks first and otherwise swaps only
when both items pass the opposite slot rules. Valid and invalid targets provide
green or red feedback, then revalidate the current state before sending an
operation.
Item clicks only select action controls for split or allowed destruction. A
Recovery item represents its complete delivery during a drag because the durable
claim remains atomic.

The panel displays authoritative weight, capacity, load, movement multiplier,
sprint eligibility, and item-state revision. A UI-only target advisor reuses pure
WorldData equipment, stack, Bag, Secure Container, slot-tag, weight, and hard-cap
rules to disable obvious invalid targets. Server authority always revalidates any
submitted action, and the client never applies an optimistic custody change.

World-loot retains a reserved context identity without inventing snapshots or
operations. Corpse context is active through the Phase 11 state described below.

### Corpse Client State And Presentation

`CorpseClientController` is a persistent bootstrap-owned adapter over
`RealtimeSimulationClient`. It owns immutable nearby-presence and active-view
state, assembles complete multi-packet snapshots, applies only monotonic
targeted deltas, correlates one pending corpse operation, and requests a refresh
when a stale base or concurrency result requires it. It never writes inventory
or corpse custody locally. A disconnect clears uncertain transient state, and a
new joined session rebuilds presence and views from server authority.

Protocol version `11` carries chunked presence, destination slot tags,
canonical corpse equipment-slot ids, open, close, refresh, full-item and
partial-stack loot or deposit, corpse-internal move, ordinary slot swap, atomic
Bag swap, operation-result, targeted view-state, and view-closure messages on
the reliable ordered path. Complete snapshots must contain exactly the canonical
general inventory, equipment, and Bag sections. Chunk metadata, container
revisions, slot capacities, ids, tags, equipment-slot identities, and item
revisions must remain coherent before state becomes visible. A committed
custody-changing result forces the inventory controller to refresh until it
reaches the returned item-state revision, even when the previous full snapshot
was internally coherent. Pure corpse rearrangement does not request a character
refresh because its response contains no character revision or carry change.

`CorpsePresentationController` creates one replaceable generic capsule for every
nearby presence entry and updates it from server position and presentation key.
It uses a shared renderer property block and never creates per-frame materials.
Pressing `E` opens the nearest corpse within the configured three-metre range.
No authored scene or prefab object is required for the temporary presentation.

The Context module switches to Corpse when an open snapshot arrives. It renders
all three sections and reuses `InventoryDragPayload` for corpse sources. Dropping
a full or selected partial stack into Permanent inventory, the equipped Bag, or
Secure Container submits a typed corpse intent. Dropping a corpse item onto
another corpse slot submits an authoritative internal move, split, merge, or
complete swap. Equipment destinations show the catalog display name and stable
equipment-slot id, and local feedback rejects obvious type mismatches. Dropping
a corpse Bag onto the occupied player Bag slot submits one atomic aggregate
swap. Green and red target feedback remains advisory, while AuthService owns all
final slot, equipment, policy, revision, weight, capacity, and hard-cap
validation.

### Development Item Tools

`Shooter MMO > Tools > Inventory Item Grants` is an Editor-only local
development window. It discovers initialized characters and active definitions
through a short-lived AuthService command, then invokes individual grants or
deterministic test packages through the authoritative `ItemTransactionService`.
The Editor owns only process orchestration and presentation. It does not connect
to PostgreSQL, reproduce catalog rules, mutate runtime client state, or add a
gameplay endpoint.

The command boundary is available only in the Development environment against a
loopback, non-production-like PostgreSQL database. Every target character must
be offline. This guard is required because the Editor command mutates durable
state directly and has no active SimulationWorker result path that can advance
the joined session's carry tuple and item revision. Safe online grants require a
future service-authenticated development intent routed through the owning
SimulationWorker. Individual grants can extend an existing character, while
packages require an empty character so their exact test state is reproducible.
Backend validation remains authoritative for stack limits, destination eligibility,
weight, the 140 percent hard cap, revisions, policies, and container slots. The
response is machine-readable and refreshes the Editor view after a successful
mutation.

The same window can create a durable corpse for an offline source character at
a selected Shard position. An empty source is first populated by the standard
Phase 9 package, then the normal system-death adapter performs the Phase 10
partition. The Editor does not manufacture corpse rows, custody, revisions, or
snapshots. SimulationWorker restores the committed corpse through its normal
runtime registration path. These Development commands add no remotely callable
gameplay route.

SimulationWorker separately loads
`Config/appsettings.Development.json`. Its explicit
`DevelopmentItemInteractions:GlobalBankAndRecoveryAccess` option lets a joined
local Development character use Bank and Recovery Storage from any authoritative
world position. The option is rejected if enabled outside the Development
environment. It does not grant insurance access or weaken session, worker,
runtime, Shard, assignment, AuthService transaction, item-rule, or hard-cap
authority. Production continues to derive Bank and Recovery access from authored
service points.

All Unity Editor commands owned by the project use the shared
`Shooter MMO > Tools` root. The item catalog authoring window is at
`Shooter MMO > Tools > Item Catalog`, and collision baking is at
`Shooter MMO > Tools > World Collision > Bake Open Scene`. `Bake Build World
Scenes` validates the World catalog against enabled Build Profiles scenes and
bakes every mapped scene in one pass.

For network movement, input is sampled at the server-provided tick rate and each
command receives an input sequence and client tick. The local state is predicted
immediately and up to four newest unacknowledged commands are sent in each batch.
On an authoritative snapshot, `ClientMovementPrediction` removes acknowledged
commands, starts from the server state, and replays the remaining commands.
Prediction and reconciliation replay share one reusable collision-query
workspace, avoiding per-step broadphase collection allocation without changing
the deterministic movement result.
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
`Shooter MMO > Tools > World Collision > Bake Open Scene` scans enabled, non-trigger
BoxColliders below that root and writes neutral authoring JSON plus versioned
binary resources into `WorldData`. Unsupported collider types fail the bake
explicitly. The shared compiler canonicalizes signed floating-point zero before
encoding, so Unity and .NET produce identical chunk hashes for equivalent
transforms. Runtime code never scans the Unity scene or treats PhysX as network
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
debug cursor. The inventory panel independently releases capture while open and
restores the applicable debug or gameplay cursor state when closed. The
component and Camera live on the LocalPlayerCamera child owned by the LocalPlayer
prefab. Normal framing uses a 1.1 meter right-shoulder offset,
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

## Phase 12 World Actor And Interaction Client

Status: Permanent client foundation implemented with replaceable presentation

Phase 12 adds permanent client state below replaceable presentation:

- `WorldActorClientController` owns immutable, monotonic actor presence and
  replicated state across scene transitions and reconnect cleanup.
- `WorldInteractionClientController` owns correlated operations, the active
  authoritative interaction session, capability summaries, server closure, and
  uncertain-state cleanup.
- `WorldInteractionTargetingController` owns advisory crosshair targeting
  against registered NPC, corpse, and future world-object views.
- `WorldActorView` provides presentation identity and target registration.
  `NpcView` and `MobView` add kind-specific presentation hooks without actor
  authority.
- A presentation registry maps `presentationArchetypeId` to authored prefabs.
- `TemporaryWorldInteractionPanel` is a replaceable uGUI action list over the
  permanent controller and capability contracts.

The actor and interaction messages use protocol version `13`. The persistent
realtime client decodes them through the shared GameProtocol source and keeps
the existing explicit mismatch path for older or partially updated clients.
Phase 13 adds typed insurance apply or remove and quest accept or abandon
payloads to the existing capability action instead of adding a second NPC or
item interaction transport.

Phase 14 requires no protocol bump or parallel Mob-loot controller. The existing
corpse presence and nullable source-character contracts represent both live Mob
and durable player or boss corpses. The same client view, crosshair target,
operation journal, canonical three-section snapshot, committed delta, inventory
refresh, and reconnect cleanup paths remain authoritative.

Phase 15 is server and tooling hardening. It does not add Unity state, a new
packet, scene object, prefab, inspector reference, or presentation flow. The
licensed EditMode and PlayMode suites remain the regression gate for the
persistent inventory, corpse, actor, interaction, and bootstrap ownership above.

The `E` input action requests interaction with the current crosshair target or
closes the current NPC or corpse interaction.
Client selection checks a direct centre ray first and then a `0.15` metre
spherecast tolerance among registered targets within a `6.0` metre discovery
distance. The result only chooses an intent. SimulationWorker independently owns
the `3.0` metre start range, `3.5` metre maintain range, closest-point bounds
distance, line of sight, target revision, capability, and session validation.

Actor prefabs are presentation-only. There is no authoritative
`VendorMonoBehaviour`, `QuestGiverMonoBehaviour`, crafting component, insurance
component, or Mob AI component in Unity. Changing a prefab cannot change actor
kind, capability, faction, spawn, damage policy, or interaction rules.

The panel displays only the authoritative capability summary received after an
interaction opens. Insurance actions enumerate eligible items from the
persistent authoritative inventory snapshot and submit exact character and item
revisions. Quest item-lifecycle actions submit the exact character revision and
Permanent Inventory destination. The panel never applies an optimistic policy,
grant, removal, currency charge, or item deletion. A committed result triggers a
full inventory refresh at the returned item-state revision.

Inventory policy summaries carry a safe presentation source, not a raw grant or
insurance lineage id. Temporary item labels distinguish `Insured` from
`Protected` and render `Insurance NPC`, `Quest grant`, or `Catalog rule` as the
source. Recovery headings translate the durable source kind into consumed
insurance or protected-on-death delivery labels.

Current corpse presentation registers with the shared target controller and
its open view consumes the shared one-active-interaction lease while retaining
`CorpseClientController`, the Phase 11 corpse protocol, and durable transaction
authority. Phase 12 changes selection and session coordination, not corpse
custody.

All new Editor tools remain under the established menu root:

- `Shooter MMO > Tools > Content > Actor Studio` owns searchable NPC and Mob
  authoring, templates, safe duplication, capability composition, references,
  validation, preview, compile, and verify.
- `Shooter MMO > Tools > Content > Spawn Authoring` owns scene handles, ground
  snapping, spawn areas and groups, patrol paths, prefab preview, canonical
  change preview, export, compile, and verify.

Editor scene components are authoring adapters. They import and export stable
neutral definitions, but only compiled WorldData creates runtime actors. Actor
and spawn content must remain buildable and verifiable without opening Unity.

The persistent bootstrap creates the actor, presentation, targeting,
interaction, and temporary panel controllers at runtime. Existing canonical
content works through fallback presentation without scene edits. A content
creator uses the Editor windows only when changing definitions, spawn source,
or the optional presentation registry asset.

## Test Architecture

EditMode tests cover client session cleanup, operation serialization, structured
Problem Details parsing, diagnostic prefix formatting, configuration loading,
PlayerInput action binding, invalid array rejection, dynamic crosshair
configuration, local reconciliation, redundant input batches, remote
interpolation, collision resource loading, authored player prefab contracts, and
development-world scene composition. They also execute the shared encumbrance
reference points, sprint threshold, movement prediction, and monotonic carry-revision
handling used by SimulationWorker. Phase 8 EditMode coverage also round-trips
typed Secure Container intents and committed item results without adding client
authority fields. Phase 9 coverage loads the real bundled gameplay and
presentation catalogs, verifies cache reuse and mismatch rejection, validates
complete and focused snapshots, exercises stale and divergent revision handling,
correlates operation ids, and checks specialized slots, Secure Container rules,
non-empty Bags, split quantities, the exact hard cap, external corpse swaps, and
committed-revision refresh eligibility. Phase 11 coverage
verifies presence assembly, duplicate and stale chunks, complete canonical
three-section views, targeted delta consistency, monotonic revisions, every
typed corpse intent, and corpse drag payload identity.

Phase 12 EditMode coverage verifies monotonic actor presence and state,
reconnect cleanup, operation correlation, authoritative capability summaries,
direct-ray and spherecast target selection, unrelated-collider filtering, and
deterministic Actor Studio and Spawn Authoring compiler integration.

Phase 13 coverage round-trips typed lifecycle payloads and committed
item-revision messages, verifies safe policy-source mapping, and keeps the
temporary panel below the persistent inventory and interaction controllers.

Phase 14 EditMode coverage verifies the normal and boss corpse settings through
the neutral Actor Studio round trip. Existing corpse protocol and client tests
continue to cover the shared Mob presentation path, so no scene or prefab
fixture is added.

Phase 15 adds no Unity test fixture because it has no Unity-facing behavior. All
EditMode and PlayMode tests still run as the release-hardening regression gate.

PlayMode tests verify that loading LoginMenu creates the persistent client
bootstrap, realtime, inventory, corpse, actor, interaction, targeting, and
presentation controllers plus the runtime login panel. World-scene coverage
opens and closes the runtime uGUI inventory root and finds the runtime corpse
and actor presentation controllers.

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
- Keep persistent cross-scene session state in `ShooterMmoClientSession` and
  item and corpse state in their bootstrap-owned controllers, not scene panels.
- Keep planned actor and world-interaction state in bootstrap-owned controllers,
  not actor prefabs or the temporary interaction panel.
- Keep one operation owner per panel until a more explicit navigation state
  machine replaces it.
- Route every World-scene exit through exact-session UDP leave or disconnect
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

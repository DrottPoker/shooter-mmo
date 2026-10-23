# Unity Client Architecture

Last updated: 2026-10-23

## Purpose

This document is the source of truth for the current Unity client architecture.
It describes runtime responsibilities, dependencies, state, scene flow, API
handling, and local gameplay controls.

The implemented client and Editor contract for world actors is defined
in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md). Later capability
business behavior and final presentation remain explicitly outside the current
runtime claim.

## Project Boundary

The Unity project lives in `src/Client` and targets Unity
6000.5.4f1. Runtime code is compiled into ten area assemblies, described in
[Assemblies](#assemblies). All runtime content loads through Addressables, as
described in [Content Loading With Addressables](#content-loading-with-addressables).
EditMode and PlayMode tests use separate assemblies.

The current runtime UI is intentionally temporary while the custom UI is being
designed. The client bootstrap, state ownership, API layer, scene lifecycle,
input, camera, and gameplay foundations are long-term systems and must be built
to production-quality structural standards from the start.

The first world environment uses simple, scene-authored Unity primitives so
scene flow and controls remain testable. The map, local player, camera, input
asset, and spawn point are real Unity assets and are never created by runtime
generation code.

## Assemblies

Runtime code under `Assets/Scripts` is split into one assembly per area. Each
folder holds one asmdef, and every reference between client assemblies points
to a lower layer, so the compiler rejects a cycle or an upward dependency. The
layers from the bottom up:

| Assembly | Owns | Client references |
|---|---|---|
| `Mmorpg.Client.Api` | `ApiClient`, HTTP contracts, `ApiError` | none |
| `Mmorpg.Client.Core` | `ClientSession`, `ClientConfig`, `ClientLog`, `SceneNames`, `ClientAddresses`, `ClientAssets` | Api |
| `Mmorpg.Client.Worlds` | World scene catalog, `ClientSceneLoader`, collision loading and authoring | Core |
| `Mmorpg.Client.Networking` | `RealtimeSimulationClient`, `IRealtimeCorpseChannel`, `NetworkMovementSession`, `ClientSessionRecovery` | Api, Core, Worlds |
| `Mmorpg.Client.Items` | Inventory and corpse client state, item catalog, presentation catalog loading | Api, Core, Networking |
| `Mmorpg.Client.Gameplay` | Local and remote players, orbit camera, input, click-to-move navigation, pointer focus, game messages, prediction, remote player and Mob interpolation, `WorldSceneContext` | Api, Core, Worlds, Networking |
| `Mmorpg.Client.WorldActors` | Actor and interaction state, the shared pointer picker, the world menu and its providers, interaction targeting, actor and corpse presentation | Api, Core, Networking, Items, Gameplay |
| `Mmorpg.Client.Combat` | `CombatClientController` and `CombatClientState`, the combat state mirror, combat targeting and approach, the combatant view locator, projectile presentation | Core, Networking, Items, Gameplay, WorldActors |
| `Mmorpg.Client.Ui` | Every panel including the combat HUD, drag and drop, `ClientOperationState` | Every row above it |
| `Mmorpg.Client.App` | `ClientBootstrap`, the composition root | Every row above it |

Ui is the top feature area; only the App composition root, which creates the
panels, sits above it. Ui and App reference Combat: the combat HUD and the
inventory's live arrow count read its state. Each namespace
equals its assembly name. Areas that read `ClientSession` reference Api as
well, because the session caches Api contracts. The Editor-only
`Mmorpg.Client.Editor` assembly references Core, Worlds, and Gameplay plus the
shared WorldData editor and the Addressables editor assemblies. It also holds
`ArtModelImportRules`, the import rules for every model the art pipeline
exports to `Assets/Art/Models`, and `ArtAppearanceCatalogs`, which writes the
Gameplay appearance catalogs of the exported bodies, described in
[Art Pipeline](ART_PIPELINE.md).

`ClientAssemblyArchitectureTests` pins the exact declared references of every
client assembly, including shared and package assemblies, the layer order, and
the namespace rule. It reads the asmdef files because Unity adds
`UnityEngine.UI` to every compiled assembly. A new dependency is a deliberate
change: add the asmdef reference, keep it pointing down, and update the test.

## Runtime Structure

```text
ClientBootstrap (Mmorpg.Client.App composition root)
  +-- ClientSession
  +-- ApiClient
  +-- RealtimeSimulationClient
  +-- InventoryClientController
      +-- ClientItemCatalog
      +-- InventoryClientState (confirmed and shown state, pending operations)
      +-- InventoryOperationPredictor
      +-- InventoryItemStateChangePatch
  +-- CombatClientController
      +-- CombatClientState
  +-- ClientConfig
  +-- scene lifecycle recovery

Scene panel
  +-- ClientOperationState
  +-- API coroutine flow
  +-- ClientSessionRecovery

TemporaryInventoryPanel
  +-- replaceable runtime uGUI presentation
  +-- TemporaryInventoryPanel.Widgets (modules, slot grids, slots, carry meter)
  +-- InventoryPresentation (pure layout, label, and tooltip helpers)
  +-- InventoryTooltipView and InventorySlotView
  +-- persistent InventoryClientController state
  +-- shows predicted item operations, never owns item custody

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
      +-- Body (HumanMaleBody prefab, also in the RemotePlayer prefab)
          +-- Animator with HumanLocomotion, CharacterLocomotionAnimator
          +-- PlayerBodyAppearance (combined parts, tint)
  +-- CameraTarget
  +-- LocalPlayerCamera
      +-- Camera and AudioListener
      +-- OrbitCameraController

Realtime movement
  +-- NetworkMovementSession
  +-- UnityWorldCollisionLoader
  +-- baked WorldData chunks
  +-- LocalPlayerNavigator
      +-- shared MovementPathPlanner on a background thread
  +-- ClientMovementPrediction
  +-- shared PlayerMovementSimulation
  +-- RemoteMovementPresenter
      +-- RemoteMovementInterpolation and RemoteRenderClock
      +-- grounded presentation
      +-- RemotePlayerView (routed by WorldSceneContext)
      +-- RemoteActorMovement in MobView (routed by WorldActorPresentationController)

World pointer input
  +-- WorldPointerFocus, owned by ClientBootstrap
  +-- WorldMenuController, providers sorted by WorldMenuOrder
      +-- WorldPointerPicker, every target under the pointer, nearest first
      +-- CombatTargetingController (Combat, first: Attack)
      +-- WorldInteractionTargetingController (WorldInteraction: capabilities, Loot)
      +-- itself (WalkAndExamine: Walk here, Examine)
  +-- TemporaryWorldContextMenu (replaceable runtime uGUI)
  +-- GameMessageLog, owned by ClientBootstrap
      +-- TemporaryGameMessagePanel (replaceable runtime uGUI)
  +-- MovementDestinationMarker

Combat (World scene, created at runtime by ClientBootstrap)
  +-- CombatantViewLocator (local player, remote players, world actor views)
  +-- CombatTargetingController
      +-- CombatClickRules, CombatTargetSelection, CombatApproachPlanner
      +-- CombatLoadoutRules (weapon range from the bundled item catalog)
  +-- CombatProjectilePresentation (replaceable arrows)
  +-- TemporaryCombatHud (replaceable runtime uGUI)
      +-- CombatHudPresentation (pure labels and values)
```

## Persistent Client Bootstrap

`ClientBootstrap` creates the persistent runtime root and survives scene
changes. It initializes configuration, owns one `RealtimeSimulationClient`, one
`InventoryClientController`, and the corpse, world actor, world interaction, and
combat controllers, observes scene transitions, and performs fallback leave when
an active World scene is left outside the normal panel flow.

`ClientBootstrap` is the only composition root, and no runtime assembly
references it. It creates the persistent services and passes them explicitly
to every component that needs them; there is no static service access. Scene
controllers that it creates at runtime are added to an inactive GameObject,
given their dependencies through `Initialize`, and then activated, so `Awake`
and `OnEnable` already see them. The scene-authored `WorldSceneContext`
receives the realtime client when its scene loads, before `Start` runs. Tests
construct the same components with their own dependencies.

The fallback release is bound to the exact simulation-session identity. Completion of
an older request cannot clear a newer local reconnect state. If graceful leave
fails after the scene already changed, the client closes UDP so SimulationWorker can
run disconnect cleanup.

## Configuration

`ClientConfig` is a ScriptableObject at
`Assets/Content/Config/ClientConfig.asset`. The bootstrap loads it through
Addressables at startup, synchronously, and keeps its handle until the bootstrap
is destroyed, which also keeps the referenced gameplay catalog loaded. A missing
configuration logs a warning and uses local fallback endpoints. Its C#
definition lives in `Assets/Scripts/Core`. It currently contains:

- GameService base URL.
- HTTP request timeout in seconds.
- Realtime operation timeout in seconds.
- Account-session validation interval in seconds.
- A direct TextAsset reference to the gameplay runtime item catalog supplied by
  the local WorldData package.
- `RuntimePrimitiveMaterial`, the `M_RuntimePrimitive` URP Lit material for the
  primitives that runtime presentation creates itself: the destination marker,
  arrows, corpse capsules, and fallback world actor models. The reference puts
  the material and its shader into the Client Content bundle. The bootstrap
  passes it to those components, which create their primitives through
  `RuntimePrimitives.Create`. A player build has no default material for URP, so
  a bare `GameObject.CreatePrimitive` falls back to the built-in Standard shader,
  which URP draws with its pink error shader; only the Editor hides that.
  `RuntimePresentationTests` fails on a missing material and on any other
  `GameObject.CreatePrimitive` call in the runtime scripts.

SimulationWorker host, runtime, and UDP port come from GameService's short-lived
shard placement response. They are not part of the public shard list and are not
duplicated in client configuration. Different environments should use
build-specific configuration assets or a future build configuration pipeline.

## Client Session State

`ClientSession` stores the current in-memory client view:

- GameService endpoint.
- Account and bearer session details.
- Selected character and shard.
- Active simulation-session, shard, World, worker, and worker-runtime metadata.

This state is a client cache, not an authority. GameService and SimulationWorker remain
authoritative. The persistent bootstrap validates an authenticated account
session every five seconds, including outside the active World scene. HTTP 401
clears all local session state before loading LoginMenu.

## API Layer

`ApiClient` wraps UnityWebRequest in coroutines. It is responsible for:

- Request serialization and response deserialization.
- Bearer authentication headers.
- Configured request timeouts.
- Validation of expected response payloads.
- Structured mapping of HTTP, timeout, network, and invalid-response failures.
- Owned-character complete item-state, focused Bank, and focused Recovery
  Storage reads used by the persistent inventory controller.
- Tracking every request in flight. Unity stops a coroutine without disposing
  its iterator, so each owner disposes its client in `OnDestroy`, which aborts
  and releases any request that has not completed.

`ApiError` carries the failure kind, HTTP status, stable server code,
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
into `ClientSession`.

Unexpected disconnect clears the active shard view and returns an authenticated
player to CharacterSelect. `RealtimeSimulationClient` also raises `SessionEnded`
once whenever a joined session ends for any reason, including a graceful leave,
`Abort`, or a failure while leaving. The inventory, corpse, world actor, world
interaction, and combat controllers reset their session-scoped state on that
event, so a pending operation, open view, or corpse presence sequence never
blocks the next join. `SessionJoined` is raised when the worker accepts a join,
before any later message of that session is handled; the combat controller
clears its state there as well, because combat revisions start again in every
session. The accepted join also carries the Shard rules, which the client
exposes as `ShardRules` while the session is joined, such as whether player
versus player attacks are allowed. Join rejection stays in CharacterSelect and
displays the structured server code. Protocol decoding rejects wrong versions, invalid
types, oversized values, and trailing data.

If another client logs into the same account, SimulationWorker can send
`account_session_replaced` over the reliable control path. The client aborts its
transport, clears the entire account session, logs an `[AUTH]` error, and loads
LoginMenu. The periodic GameService validation provides the same recovery when
the displaced client is not connected to a shard.

The realtime protocol uses two explicit LiteNetLib channels plus unchanneled
snapshot delivery:

- Channel 0 uses reliable ordered delivery for join, leave, disconnect, entity
  lifecycle, carry-state updates, item-operation intent and result, combat, and
  the other control messages.
- Channel 1 uses sequenced delivery for redundant movement input batches. A
  batch also carries the path of its newest input until an owner snapshot
  confirms that path, so a path can never arrive after the inputs that name it.
- Dedicated owner snapshots and chunked remote simulation snapshots use
  LiteNetLib's unchanneled `Unreliable` delivery. LiteNetLib reports these
  packets with receive channel 0. Server tick, snapshot sequence, message type,
  and remote chunk metadata provide application-level ordering without
  confusing snapshots with reliable control messages. The client recognizes
  them by the unreliable delivery in their generated message descriptor, see
  [Realtime Protocol](REALTIME_PROTOCOL.md#message-descriptors).
- The owner snapshot is the only source for local input acknowledgement,
  prediction reconciliation, and collision-streaming anchors. It carries the
  worker's state after the acknowledged input, and the worker steps once per
  input like the prediction, so a replay of the later inputs lands where the
  prediction stands.
- `RealtimeSimulationClient.TrySendMovementInputs` wakes LiteNetLib's update
  thread after each batch, so the input leaves at once instead of on the
  transport's next 15 ms update. Remote chunks
  update admitted remote entities and may arrive at a lower fair cadence under
  dense worker-wide byte pressure.
- Correctly delivered snapshots that overtake join acceptance or remain in
  flight during and immediately after leave are ignored outside the `Joined`
  state. They are not protocol failures because LiteNetLib delivery methods do
  not provide ordering relative to each other.
- While a leave is pending, SimulationWorker keeps sending reliable presence,
  world actor, interaction, corpse, gathering, combat, and item-operation
  messages until it has released the session. The client ignores these
  in-flight messages in the `Leaving` state, so a graceful leave never turns
  into a protocol disconnect.
- After the session ended in one poll, later packets in the same poll are
  ignored, so the first failure reason is the one shown to the player.

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

`src/Shared/GameProtocol/Runtime` is installed as a local Unity package. Its
`Generated` folder is written by `tools/ProtocolGenerator` from the protocol
schema, including stable `.meta` files, so a protocol change never needs a
Unity Editor step; see [Realtime Protocol](REALTIME_PROTOCOL.md). LiteNetLib is
installed from OpenUPM. The runtime assembly references both by assembly name.
LiteNetLib is pinned to version 2.1.4 in both the project manifest and package
lock. Unity displays a missing-signature warning because the scoped third-party
registry package is not signed by Unity. This warning does not indicate a
compile or runtime failure, but every package upgrade still requires source and
changelog review.

The client renders with the Universal Render Pipeline. `PC_RPAsset` and
`Mobile_RPAsset` in `Assets/Settings/Rendering` each own one Universal Renderer
that uses the package default post-processing data. Graphics settings and the
`PC` quality level use `PC_RPAsset` (4x MSAA, HDR, 80 m shadow distance), and the
`Mobile` quality level uses `Mobile_RPAsset` (no MSAA, no HDR, 40 m shadow
distance). Every project material uses `Universal Render Pipeline/Lit`. The
runtime fallback presentation sets both `_BaseColor` and `_Color`, so it stays
correct in any pipeline. There is no post-processing Volume in the scenes and no
baked lighting yet. The URP global settings reference `Assets/DefaultVolumeProfile.asset`
as the default volume profile, with the neutral values URP creates. Its earlier
reference pointed at an asset that never existed, so every fresh Library created
a new profile. The shader prefiltering data in `PC_RPAsset` and the runtime
settings list of the global settings are committed as the last Windows
development build wrote them, so the next build has nothing to change.

The manifest lists only packages the client uses. The unused template packages
AI Navigation, Collab Proxy, Multiplayer Center, Timeline, and Visual Scripting
were removed and are added back only when a feature needs them. The project has
no project-wide Input System actions asset; gameplay input uses the
`PlayerControls` asset referenced by the local player.

`com.unity.pipeline` is an exact-pinned experimental Editor automation
dependency that the Unity CLI and coding agents use to drive the running Editor.
Client code does not reference it. Its runtime assembly compiles only in the
Editor and in builds that define `ENABLE_PROFILER` or `ENABLE_RUNTIME_PIPELINE`,
and its Player server stays disabled because the Pipeline Runtime setting
`enableInBuilds` is `false`. The workflow and security rules are in
[Local Development](LOCAL_DEVELOPMENT.md#unity-editor-automation-with-unity-cli).

## Content Loading With Addressables

The client uses Addressables 2.9.1 for all runtime content; there is no
`Resources` folder and no `Resources.Load` call. Content lives in folders below
`Assets/Content`. Each folder is one Addressables folder entry whose address is
its path below `Assets/Content`, so an asset's address is its path below that
root including the file extension, and a new file in a content folder needs no
settings change. `ClientAddresses` lists every fixed address:

| Group | Entries | Address | Packing |
|---|---|---|---|
| Client Content | `Assets/Content/Config`, `Worlds`, `Items/Presentation`, `WorldActors` | `Config/ClientConfig.asset`, `Worlds/world-scene-catalog.json`, `Items/Presentation/item-presentation-catalog.json`, `WorldActors/WorldActorPresentationRegistry.asset` | Together |
| Item Icons | `Assets/Content/Items/Icons` | `Items/Icons/<file>` | Together |
| World Actor Prefabs | `Assets/Prefabs/WorldActors` | `WorldActorPrefabs/<file>`, loaded by GUID through the registry | Together |
| World Collision | `Packages/com.mmorpg.world-data/Generated/WorldCollision` | `WorldCollision/<WorldId>/manifest.json` and `<chunk>.bytes` | Together |
| World Scenes | One entry per catalog World scene | The scene name | Separately, one bundle per scene |

`ClientAssets` in Core loads small content synchronously: configuration,
catalogs, icons, and collision chunks. It locates an address first, so a
missing address becomes a structured error instead of an Addressables
exception, and the caller releases every handle it owns. Scenes and actor
prefabs load asynchronously where they are used.

All groups build locally into the player. A normal player build builds the
Addressables content first. The build content state is written to
`Library/com.unity.addressables/ContentState`, because the client ships no
remote content updates. In the Editor, the default play mode script is "Use
Asset Database", so Play Mode and the tests need no content build.

`ClientAddressablesLayout` in the Editor assembly defines the groups, folder
entries, World scene entries, and the Build Profiles scene list in code. `MMORPG
> Tools > Addressables > Apply Client Layout` reapplies it, and
`ClientAddressablesTests` fails on any difference, on a runtime address without
an entry, and on any `Resources` folder or call.

## Scene And UI Flow

The build flow is:

```text
LoginMenu -> CharacterSelect -> WorldSceneCatalog[accepted WorldId]
     ^              ^                         |
     +--------------+-------------------------+
```

`Assets/Content/Worlds/world-scene-catalog.json` is the client-owned mapping
from authoritative `WorldId` to an authored Unity scene name. GameService and
SimulationWorker never send a scene path. CharacterSelect resolves the selected
shard's World before requesting a join ticket and rejects an unknown mapping or
a scene that is not an Addressables World scene. It resolves the authoritative
placement World again before opening UDP, so a shard list cached across an
offline rebind cannot load the previous scene. The checked-in catalog maps
`development-world-1` to `DevelopmentWorld1` and `development-world-2` to
`DevelopmentWorld2`. Both scenes are authored Addressables entries addressed by
scene name. Build Profiles contain only LoginMenu and CharacterSelect.

Every scene change goes through `ClientSceneLoader`. LoginMenu and
CharacterSelect load as built-in scenes. After an accepted join, CharacterSelect
loads the World scene asynchronously and stays visible, with its controls
disabled, until the World scene replaces it. If the load fails, the panel shows
the error and leaves the joined simulation session gracefully. Addressables
releases the World scene's handle and bundles when the scene unloads, so a
return to a built-in scene needs no explicit release.

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
owner snapshot sequences, remote snapshot chunks, and estimated missing owner
sequences.
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
GameService to revoke the active account session, then clears local state.

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
prefab. The referenced Input Actions asset defines the left select click, the
right context menu click, pointer position, middle-button camera orbit and its
mouse delta, arrow-key camera rotation, scroll zoom, cancel, inventory, and
debug panel bindings. This keeps
device bindings out of gameplay and UI code and allows future rebinding and
control-scheme work without changing those systems. The cursor is never locked.

`ClientBootstrap` owns one `WorldPointerFocus` and passes it to the World scene,
the local player, the camera, and the World Debug panel. It reports whether the
pointer is over interface: uGUI panels through the EventSystem, and the
immediate-mode debug panel through the rectangle it reports while drawing. A
click or scroll over a panel therefore never reaches the world.

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
drops. Ramps, airborne movement, teleports, and large corrections bypass
this extra blend. Presentation never feeds back into prediction, reconciliation,
input packets, or SimulationWorker state.

`LocalPlayerController` moves the player only in an authenticated World-scene
session; the prefab has no offline movement mode. It reads no clicks itself:
the [World Menu](#world-menu) does, and its Walk here casts the pointer ray
through `TryFindGroundPoint` against the same baked collision world that
prediction uses, so world actors and their trigger bounds never stop it, and
the hit point becomes the destination.
`LocalPlayerNavigator` plans the route with the shared `MovementPathPlanner` on
a background thread. The collision world is lock-protected, and only one plan
runs at a time, so a long search never stalls a frame while chunk streaming
continues on the main thread. Every walk,
approach, or stop is a new intent with an id; only the newest intent's plan is
applied, and the previous path continues until it is ready. The navigator gives
every applied plan a new path id. A plan that stopped at the 32 waypoint limit
continues under the same intent once its last waypoint is reached. An approach
trims the route where it first comes within range of the target. Only a start
already within range counts as arrival and stops the player; an approach without
a route reports no route, and the previous path continues. Normal movement
faces its travel direction, and an airborne player keeps its takeoff momentum
and facing until it lands. `RefreshCharacterDimensions` remains the runtime
entry point when a future character system changes collider dimensions.

Authenticated prediction also consumes the exact carry tuple owned by
`NetworkMovementSession`. The shared simulation keeps full walking speed through
100 percent capacity and linearly reduces it to `0.20` at the 140 percent hard
cap. An involuntary death overflow above the hard cap is a valid carry tuple and
stays at `0.20`. A reliable newer revision is applied before subsequent
prediction and reconciliation replay. The temporary F2 panel exposes weight,
capacity, item-state revision, and movement percentage for observation without
becoming inventory state or authority.

`RealtimeSimulationClient.TrySendItemOperation` accepts only a typed protocol intent while
joined and sends it on the reliable ordered control path.
Committed or rejected results are decoded only in the joined state. A committed
newer carry revision advances `NetworkMovementSession` before
`ItemOperationCompleted` is raised. The transport does not own item collections.
The persistent `InventoryClientController` consumes the result, correlates its
operation id and kind, and refreshes authoritative HTTP state before permitting
another mutation. A duplicate completion for the last finalized operation is
ignored safely.

`ClientItemCatalog` compiles and revision-checks the bundled gameplay TextAsset
through the shared `ItemCatalogJson` shape that the Item Catalog editor also uses,
loads the presentation catalog once through Addressables and caches it per
gameplay revision, and indexes definitions and equipment slots by stable id. Gameplay and presentation
source revisions must match before item UI can render. Every HTTP snapshot must
also match the bundled gameplay revision. A mismatch clears renderable item
state and produces `item_catalog_update_required` instead of accepting stale
definitions. Icons, localization keys, fallback labels, and optional prefab
presentation keys resolve locally. An icon is a Sprite whose Addressables
address is stored in the catalog's `iconAddress` field, below `Items/Icons/`;
the client loads each icon once and keeps it for the process lifetime. HTTP responses carry only stable definition
ids and instance state.

Durable item-instance revisions are non-negative, and a newly granted or split
item begins at revision `0`. Unity `JsonUtility` can materialize an exact
all-default object for a JSON `null` in an optional item or equipped-Bag field.
The snapshot mapper normalizes only that exact optional placeholder back to no
item. Required Recovery delivery items and every partially populated malformed
item still fail validation.

The complete snapshot from `GET /api/characters/{characterId}/item-state` also
carries the character's `currency` balance. The mapper rejects a balance below
0 or above `VendorPricing.MaximumCurrencyBalance`. Every balance change while
the character is online is an item transaction, so the balance belongs to the
item-state revision and takes part in the same-revision coherence check. The
client never changes the balance locally.

`InventoryClientState` owns immutable complete and focused snapshots plus the
latest observed character, Bank, and Recovery revisions. A focused response may
advance its slice, but any newer focused revision marks the complete snapshot
incoherent and disables mutation until a full refresh reaches the newest known
revision. Same-revision content disagreement is an error. Older responses are
ignored. Initial join and reconnect trigger a complete refresh automatically.
A complete snapshot at the revision the client already holds, with equal
content, also proves the state and enables mutation again, so a leave and
rejoin or a reconnect that changed no item never leaves the inventory locked.

Spent arrows commit in worker batches that no client intent caused. The worker
reports each committed batch with `OwnerItemQuantityChanged`: the item-state
revision it applies to, the new carry state, the equipped stack, its new
quantity, and its new revision, where quantity `0` means the stack was used up.
`RealtimeSimulationClient` first advances `NetworkMovementSession` with the
carry state, raising `CarryStateChanged` when it changed, and then raises
`OwnerItemQuantityChanged`. `InventoryClientController` patches the stack in the
`ammunition` equipment slot in place, or empties the slot at quantity `0`, and
takes the new carry values and revision, but only when the complete snapshot is
coherent at exactly the revision the change applies to and the change is the
single transaction after it, because every item transaction advances the
item-state revision by one. The patch keeps the inventory status, so a pending
operation stays busy. In every other case, such as a missed earlier change, an
incoherent snapshot, or a stack the client does not know as equipped
ammunition, it calls `EnsureFullState` with the new revision, which refreshes
unless a coherent snapshot already reached it.

Other committed changes that no client intent caused, such as a death that moves
the carried items into the corpse and Recovery Storage, reach the client only as
a newer carry state in `NetworkMovementSession`, which
`RealtimeSimulationClient.CarryItemStateRevision` exposes. Every frame,
`InventoryClientController` compares it with the newest known revision, and
`InventoryServerRevisionFollower` asks for a complete refresh to that revision
once no own operation is pending and no refresh runs. A refresh that fails or
stays behind is retried for the same revision 1 second after it ended, and every
further failure doubles the delay up to 30 seconds, so a failing GameService is
never asked every frame; a newer revision or a new session starts over at once.
The check runs a frame after the carry state arrives, so an arrow batch that was
already patched in place needs no refresh.

Every committed transaction of the owner that the worker can describe whole
reaches the client as `OwnerItemStateChanged`: the client operation that caused
it, if any, the revision it applies to, the new carry state and balance, the new
revision of every touched container, and the final state of every changed item,
where a removed item carries only its id. The worker sends it before the carry
state and the operation result, for item operations, corpse loot and deposits,
NPC services such as vendor trades, and gathering yields.
`InventoryItemStateChangePatch` applies it to the coherent confirmed snapshot it
follows: every changed item leaves its slot, then takes its new place, an item
that moved into a container the snapshot does not show, such as a corpse, leaves
the view, and every changed container must have its new revision. A Bag that
enters the Bag slot brings its contents container, laid out as its catalog Bag
with the items the change lists inside it; a Bag that leaves it takes its
contents out of view. A claim's deliveries leave Recovery Storage, and every one
of their items must take a place the change names. The patched snapshot equals
what `GET /api/characters/{characterId}/item-state` returns for the new
revision, so no reload follows. A change the patch cannot describe, such as an
item arriving in Recovery Storage, or one that does not follow the known
revision, calls `EnsureFullState` with the new revision instead. Opening the
Bank or Recovery tab reloads only an incoherent state; the Refresh button still
reloads on request.

`InventoryClientState` keeps the confirmed snapshot and shows it with every
pending item operation applied in order. `FullSnapshot`, `Bank`,
`RecoveryStorage`, and `TryFindItem` return the shown state that the panel and
the target rules use; `ConfirmedSnapshot` and `TryFindConfirmedItem` return the
state the server confirmed. A drop creates an `InventoryPredictedOperation` that
names items and destinations only. `InventoryOperationPredictor` checks it with
the same `InventoryTargetAdvisor` rules as the drop targets and applies it the
way GameService commits it: a relocation, unequip, split, merge, container swap,
equip with its swap into the source slot, destroy, or Recovery claim, with the
carried weight adjusted for items that enter or leave carried storage. A Bag
equip shows the Bag's empty contents, laid out as its catalog Bag under a
temporary container id that nothing may move into until the server names its
own (a move or corpse loot into it is refused with a short wait message), and adds the Bag's capacity bonus; unequipping an empty Bag removes both. A
claim places the delivery, in item order, in the first free slots of Permanent
Inventory or the Bank that accept each item, as GameService does. A split, a
purchase, or a partial corpse loot shows its new stack under a temporary id that
nothing may act on until the server confirms it. Up to
`MaximumPendingOperations` (8) operations may wait, so a player can drag item
after item without waiting. Every predicted step, corpse and vendor steps
included, must end within the 140 percent hard cap that GameService checks on
each voluntary transaction, so a swap to a smaller Bag cannot show an overload.

Corpse and vendor operations join the same queue through
`IInventoryOperationQueue`, which `InventoryClientController` implements, as
operations that carry their own prediction and their own send.
`CorpseClientController` shows loot, loot into equipment, deposits, moves inside
the corpse, and Bag swaps at once: `CorpseInventoryPrediction` applies the
inventory side, merging into a compatible stack or swapping a whole item, and
`CorpseViewEdit`s show the corpse side on `CorpseClientState.ShownView`, the
confirmed view with the edits applied. The corpse side stays shown after a
successful result until a newer corpse view arrives, which the worker sends
right after the result, or for at most 2 seconds before a refresh. A corpse
operation waits in the queue while the corpse has a refresh in flight and fails
once its corpse is closed. `VendorPrediction` shows a purchase the way
GameService acquires it, topping up unprotected stacks of the definition in
Permanent Inventory and then the equipped Bag before taking the first free slot
among specialized Bag slots, Permanent Inventory, and general Bag slots, and a
sale as a smaller or removed stack; both change the balance at once.
`WorldInteractionClientController` sends a queued trade once the interaction is
idle.

`InventoryClientController` sends the first pending operation once the
confirmed state is current, with the revisions the confirmed state expects, and
sends the next only after the previous one committed. A later operation thus
always builds on the committed result of the one before it. The committed change
of the sent operation names its operation id, so the client stops applying that
prediction at once and never applies a swap twice. When the result arrives first
and no change patched the state, the operation stays shown until a reload
reaches its revision.

A rejection undoes every pending operation, because the others were shown on top
of the rejected one, sets the server's error, and raises `PredictionRolledBack`
with the items involved; a rejection that `RequiresInventoryRefresh` then
reloads its scope. A newer confirmed state that no longer fits a pending
operation, such as a gathered stack that lands in the target slot, undoes that
operation and every one after it with `item_move_superseded`; an operation
already sent stays pending without a prediction, because the server still
answers it. Every operation that leaves the queue without committing raises
`PredictionsDiscarded`, so the corpse and world interaction controllers drop
what they showed. `CanMutate` reports whether an operation that needs the
confirmed state may start, and `CanPredict` whether another item operation may
be shown. A reload after a committed operation runs behind the shown state, so
the panel says Loading only for a first load. A disconnect with a pending
operation marks the local result uncertain, discards every prediction, and
relies on the reconnect refresh to resolve the committed outcome.

### Inventory UI

`TemporaryInventoryPanel` is the only replaceable part of the inventory
implementation. It creates runtime uGUI below a World-scene controller and reads
the persistent state without embedding API, protocol, revision, or item-rule
ownership. `B` toggles character storage only, `C` toggles equipment plus
character storage, and `I` toggles the complete Development view with contextual
storage. Pressing a different inventory key switches modes, and `Escape` closes
the panel. Clicks on the panel never reach the world. Equipment,
Context, and Character Inventory each own a separate panel root at fixed anchors.
Mode changes affect root visibility only and never rewrite another module's
layout.

The stable layout has three columns. Character storage is the right column and
keeps its place in every mode, so `B` shows only that column, `C` adds the
equipment column on the left, and `I` adds the contextual storage column in the
middle. Permanent inventory, equipped Bag contents, and the Secure Container,
shown with its catalog tier name such as Warded Pouch, remain visible while
Bank, Recovery Storage, a corpse, or a vendor is selected. Bank and Recovery may
be inspected through global owning-account reads, and the context header names
the NPC whose open interaction offers the shown storage or trade. Every mutation
still travels through the joined SimulationWorker and is committed through
GameService authority.

The equipment column lays the catalog equipment slots out like a paper doll in
three columns: tool, head, and Bag on top, ammunition, neck, and hands below
them, main hand, body, and off hand in the middle, the two rings beside legs,
and feet at the bottom. `EquipmentPaperDollLayout` owns these positions and
appends any slot it does not know, such as one added to the catalog later, in
extra rows. Every slot in every module shares one size, fitted to five columns
in the character column. An occupied slot shows the item name, a category color
strip in place of item art, the stack quantity in its lower-right corner, a `P`
or `I` badge for protection on death or insurance, and the equipment slot or
accepted item types in its upper-left corner. An empty slot is darker and shows
only its equipment slot name or accepted types. An empty equipment slot that an
equipped item blocks, such as off hand next to the two-handed Training
Shortbow, is red and marked Blocked. Hovering a slot shows a tooltip with the
item name, category, quantity and stack limit, weight, equipment slots and
blocked slots, active policies with their source, destruction and Secure
Container restrictions, and the slot location. Tooltips never show during a
drag.

The character column starts with a carry meter that spans zero to the 140
percent hard cap with a marker at full capacity. It is green up to full
capacity, amber while movement is slowed, and red at the hard cap, and its
caption shows authoritative weight, capacity, load, and movement speed. The
selected item, its split quantity, Split, Destroy, and Clear controls, and the
status line sit in a footer below the character column, so they work in every
mode. The character header shows the currency balance with the catalog's
currency display name and the item-state revision, such as
`100 Gold    Revision 12`.

`InventoryDragCoordinator`, `InventoryDragSource`, typed
`InventoryDragPayload`, and `InventoryDropTarget` form the reusable uGUI
interaction layer. Relocation, equip, unequip, split, merge, atomic ordinary
container-slot swap, and complete Recovery claims start only from a drop. A drop
onto an occupied slot merges compatible stacks first and otherwise swaps only
when both items pass the opposite slot rules. On an occupied equipment slot,
a stack of the equipped definition tops up the equipped stack, and any other
container item equips with a swap that `InventoryTargetAdvisor.CanEquip`
checks: the equipped item must fit the slot the new item leaves, a non-empty
equipped Bag stays, the new item's blocked slots must be empty, and the carry
cap must hold. Valid and invalid targets provide
green or red feedback, then revalidate the current state before sending an
operation. A valid drop shows its result at once, and the player may drag the
next item while earlier moves wait for the server. A state change during a drag
redraws the panel only when the drag ends, so a confirmation never cancels the
drag, and drops always judge the current slot instead of the one drawn. An
undone move puts the items back, outlines them in red for 1.5 seconds, and the
status line says `Item move undone:` with the server's reason. The drag ghost
trails below and to the right of the pointer, so it never hides the highlighted
target.
Item clicks only select action controls for split or allowed destruction. A
Recovery item represents its complete delivery during a drag because the durable
claim remains atomic.

The panel displays authoritative weight, capacity, load, movement multiplier,
and item-state revision. A UI-only target advisor reuses pure
WorldData equipment, stack, Bag, Secure Container, slot-tag, weight, and hard-cap
rules to disable obvious invalid targets and to predict item operations. Server
authority always revalidates any submitted action, and a prediction only shows
what the confirmed state will become: the committed change or a reload always
replaces it.

World-loot retains a reserved context identity without inventing snapshots or
operations. Corpse context is active through the corpse state described below.

### Vendor Context

The Context module has a Vendor tab, `InventoryContextKind.Vendor`. Choosing a
vendor capability, such as Trade at Mira the Quartermaster, opens it through the
worker, and the confirmed result raises `InventoryCapabilityOpened`, which opens
the complete view on that tab. The capability's `profileId` names a vendor
profile of the bundled item catalog, and an unknown profile asks for a client
update instead of guessing prices. Without
an open vendor interaction the tab only says to talk to a vendor, because
trading needs the interaction to stay open.

The tab shows the balance, then every offer in catalog order with its unit
price, a quantity field, and a `Buy for <total>` button whose total follows the
field as you type. Below it a `Sell` drop slot accepts a dragged carried item:
one in the permanent inventory, the equipped Bag, or the Secure Container, but
not an equipped item, the equipped Bag itself, or a banked item. A dropped item
shows a quantity field that defaults to the whole stack, a
`Sell <n> <item> for <total>` button, and `Cancel`. Every purchase and sale
sends the total it showed as the expected price, together with the known
item-state revision and, for a sale, the item revision.

`VendorClientRules` in `Mmorpg.Client.Items` prices offers and sales with the
shared `VendorPricing` rule and checks them before sending: an offered item, a
purchase of 1 up to one stack, a sale of 1 up to the stack, the price range, the
balance and its upper limit, the 140 percent hard cap, a carried location, a
category the vendor buys, protection and insurance policies, and quest items.
These checks are advisory. GameService recomputes every price, rejects a
different one with `vendor_price_changed`, and owns every final rule. The
worker's result or rejection appears in the vendor tab. The committed change of
a trade, which arrives before the result, already patched the inventory and the
balance, so `EnsureFullState` with the returned item-state revision reloads only
when no change patched it.

### Corpse Client State And Presentation

`CorpseClientController` is a persistent bootstrap-owned adapter over the corpse
messages of `RealtimeSimulationClient`, which it reaches through
`IRealtimeCorpseChannel` in `Mmorpg.Client.Networking` so EditMode tests can drive
it with a fake channel. It owns immutable nearby-presence and active-view
state, assembles complete multi-packet snapshots, applies only monotonic
targeted deltas, correlates one pending corpse operation, and requests a refresh
when a stale base or concurrency result requires it. It never writes inventory
or corpse custody locally. A disconnect clears uncertain transient state, and a
new joined session rebuilds presence and views from server authority.

The corpse protocol carries chunked presence, destination slot tags,
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

`CorpseClientState.BeginOperation` records the id, kind, and corpse of the one
pending operation, and `CorpseClientState.TryComplete` settles it with the
worker's result. A result whose operation id, kind, or corpse does not match the
pending operation, or that arrives with nothing pending, is a protocol conflict:
`TryComplete` returns false and the controller disconnects with
`corpse_operation_result_mismatch`. The state also remembers the corpse whose
view it holds or has asked to open. View-state chunks and closures of any other
corpse return `CorpseStateApplyResult.Ignored` or are dropped, so a refresh or
open snapshot that the worker sends after the result of an operation whose view
it already closed never reopens that view. A rejected open forgets its corpse.
View state that arrives while an operation is pending keeps the state busy.

`CorpseClientStatus` only says whether the state can start an operation:
`Unavailable` before the first presence snapshot or operation and after a
session reset, `Busy` while an operation is pending, and `Ready` otherwise. A
rejection is an outcome, not a broken state, matching
`InventoryClientState.SetServerRejection`. When the worker rejects an operation
on an open view without `RequiresCorpseRefresh`, for example
`carry_weight_limit_exceeded`, `item_stack_incompatible`, or
`corpse_out_of_range`, no view state follows, so `TryComplete` returns the state
to `Ready` and reports the rejection only in `Error`. `CanMutate` stays true and
the panel keeps every corpse slot draggable while it shows the error in red. The
error stays until the player's next operation, a closure, or a session reset;
view state caused by other viewers does not clear it.

The controller also refreshes the open view on its own. A rejection with
`RequiresCorpseRefresh`, which the worker sets for `item_already_looted`,
`item_quantity_changed`, `bag_state_changed`, `item_state_conflict`,
`item_slot_occupied`, and uncertain GameService outcomes, starts a refresh at
once. A targeted delta that goes stale starts one at once when nothing is
pending, and otherwise after the pending operation's result, success or
rejection. Such an automatic refresh begins through
`CorpseClientState.BeginAutomaticRefresh` instead of `BeginOperation`, and
`IsAutomaticRefreshPending` stays true until its result. It keeps the state
busy but tells the player nothing new, so `Error` and `Notice` stay while it
runs, when it succeeds, and when its snapshot arrives after the result: the
rejection that asked for the refresh stays in red next to the refreshed view. A
refresh the player asks for, through the Refresh button or the context refresh,
goes through `TryRefresh` and `BeginOperation` and clears the error like any
player operation. A rejected automatic refresh reports its own error, and the
controller starts no further refresh for it, so an unavailable GameService
cannot cause a refresh loop; the player can refresh by hand, and the next stale
delta refreshes again.

A view-closure message ends the active view through
`CorpseClientState.ApplyClosure`, which asks `CorpseViewClosureRule` in
`Mmorpg.Client.Items` how to present the stable closure code. The viewer's own
close (`corpse_view_closed`), the last item taken from a live Mob corpse
(`corpse_emptied`), expiry (`corpse_expired`), and death
(`combat_character_dead`) are intended ends: they set `CorpseClientState.Notice`
to the worker's message, or a fallback when it is blank, and leave `Error` null;
the own close sets no notice. Every other code, including
`corpse_invalidated`, `corpse_not_found`, and any code the client does not know,
becomes a `CorpseClientError`, so a real invalidation is never hidden. `Notice`
and `Error` are never set together, and a new operation, an error, or a session
reset clears the notice. The panel shows the error in red and otherwise the
notice in neutral text. The client's own confirmed close ends the view without a
message, and the `corpse_view_closed` closure that follows it is ignored because
no view is held any more.

A closure can arrive while an operation on that view is still in flight: another
player takes the last item of a live Mob corpse (`corpse_emptied` to every
viewer), the corpse expires, the viewer dies, or the worker notices an emptied
corpse before it finalizes the looter's own loot. The worker still sends exactly
one result for that operation, so the closure ends the view but keeps the
pending operation id, and the state stays busy. `CanBegin` in the corpse
controller and `WorldInteractionClientController` therefore keep blocking new
corpse and world work until the result arrives. The late result, success or
rejection, only settles the correlation: the closure's notice or error stays,
so an expected `corpse_not_found` or `combat_character_dead` rejection after an
emptied, expired, or death closure never replaces the neutral notice with a red
error. The controller still calls `InventoryClientController.EnsureFullState`
when the late result reports `RequiresInventoryRefresh`, and it never refreshes
a closed view; a stale delta noticed before the closure no longer asks for a
refresh.

`CorpsePresentationController` creates one presentation for every nearby
presence entry and updates it from server position, facing, and presentation
key. A player corpse, presentation key `corpse.remains`, shows the body lying in
the last pose of the handmade `Death` clip, turned by the presence's
`YawDegrees`, so it lies where and as the body fell. `PlayerCorpseBodies` in
`WorldSceneContext` bakes that body once, from the remote player prefab's body
playing the end of `Death`, into a static mesh that every player corpse shares,
so a field of corpses costs no animation or skinning; the corpse has a box
collider around it for the pointer. While a body that shows its character dead
lies within 1.5 m, the corpse stays hidden, so the dying body and its remains
never show twice; it appears when the character respawns at the World spawn. The
corpse shows the default look until looks are sent to other clients. Every
other corpse, such as a Mob corpse with presentation key `corpse.mob`, is a
generic capsule with a shared renderer property block on the runtime primitive
material, which never creates per-frame materials.
Loot in a corpse's world menu, also its left-click option, walks the player
into range and opens it through the shared [World Menu](#world-menu).
No authored scene or prefab object is required for the temporary presentation.

The Context module switches to Corpse when an open snapshot arrives. It renders
all three sections and reuses `InventoryDragPayload` for corpse sources. Dropping
a full or selected partial stack into Permanent inventory, the equipped Bag, or
Secure Container submits a typed corpse intent. Dropping a corpse item onto
another corpse slot submits an authoritative internal move, split, merge, or
complete swap. Equipment destinations show the catalog display name, and local
feedback rejects obvious type mismatches. Dropping
a corpse Bag onto the occupied player Bag slot submits one atomic aggregate
swap. Dropping any other whole corpse item onto a player equipment slot submits
`LootToEquipment` with the item now in that slot as its target:
`CorpseTargetAdvisor.CanLootIntoEquipment` shows green for an empty compatible
slot, a top-up of an equipped stack of the same definition, or a swap whose
equipped item fits the corpse slot the loot leaves, and red for a Bag, a partial
stack, or a blocked slot. `CorpseTargetAdvisor` in `Mmorpg.Client.Items` holds these corpse target
rules next to `InventoryTargetAdvisor`, and EditMode tests cover both. Green and
red target feedback remains advisory, while GameService owns all final slot,
equipment, policy, revision, weight, capacity, and hard-cap validation.

### Development Item Tools

`MMORPG > Tools > Inventory Item Grants` is an Editor-only local
development window. It discovers initialized characters and active definitions
through a short-lived GameService command, then invokes individual grants or
deterministic test packages through the authoritative `ItemTransactionService`.
The Editor owns only process orchestration and presentation. It does not connect
to PostgreSQL, reproduce catalog rules, mutate runtime client state, or add a
gameplay endpoint.

`DevelopmentItemToolCliProcess` runs every command in two steps. It first builds
GameService incrementally in Release through MSBuild, which stays the only
authority on whether the output is current, and then runs exactly the assembly
path that build reports in one `dotnet` process. A failed or timed out build
runs nothing, so the window never uses an older build against a newer database.
`DevelopmentToolProcess` owns each external process: it captures output, and a
timeout, a closed window, or a script reload ends the process together with
every descendant. Windows uses `taskkill /T`; macOS and Linux stop every
descendant that `ps` reports and then kill them, so no process in the tree can
start another child meanwhile. When GameService stops
without a response, the window shows the unhandled exception message first and
logs the complete output to the Unity Console.

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
a selected Shard position. An empty source first receives the corpse seed package
of `content/development/item-packages.json`, then the normal system-death
adapter performs the death partition. The Editor does not manufacture corpse rows, custody, revisions, or
snapshots. SimulationWorker restores the committed corpse through its normal
runtime registration path. These Development commands add no remotely callable
gameplay route.

The window's Give Currency section shows the selected character's balance, an
`Amount` field, and a `Give Currency` button. The button is enabled only for an
offline character, a positive amount, and while no other command runs. It runs
`--dev-items-currency`, which credits the character through the GameService
wallet and writes a `development` ledger row. This replaces setting currency
through direct SQL.

SimulationWorker separately loads
`Config/appsettings.Development.json`. Its explicit
`DevelopmentItemInteractions:GlobalBankAndRecoveryAccess` option lets a joined
local Development character use Bank and Recovery Storage from any authoritative
world position. The option is rejected if enabled outside the Development
environment. It does not grant insurance access or weaken session, worker,
runtime, Shard, assignment, GameService transaction, item-rule, or hard-cap
authority. Production derives Bank and Recovery access from an open interaction with an
NPC that offers them.

All Unity Editor commands owned by the project use the shared
`MMORPG > Tools` root. The item catalog authoring window is at
`MMORPG > Tools > Item Catalog`, and collision baking is at
`MMORPG > Tools > World Collision > Bake Open Scene`. `Bake World Scenes`
validates the World catalog against the Addressables World scenes and bakes
every mapped scene in one pass. A reusable scene shared by several Worlds
is opened once and baked once for each of them; its authored World id must be
one of those Worlds.

For network movement, input is sampled at the server-provided tick rate and each
command receives an input sequence, a client tick, and the id of the path the
player follows, or 0 while it stands. The local state is predicted immediately
and up to four newest unacknowledged commands are sent in each batch, together
with the path of the newest command until an owner snapshot reports that path
id. On an authoritative owner snapshot, `ClientMovementPrediction` removes
acknowledged commands, starts from the server state including its path
progress, and replays the remaining commands. Client and worker follow the same
waypoints, so a replay reaches the same result.
Prediction and reconciliation replay share one reusable collision-query
workspace, avoiding per-step broadphase collection allocation without changing
the deterministic movement result.
`LocalMovementPresentation` interpolates consecutive predicted states at the
render frame rate, while `LocalPlayerController` smooths reconciliation
corrections smaller than three meters and applies larger corrections immediately.
The presentation interpolation never feeds positions back into input packets or
the shared simulation. The shared simulation source is installed as
`com.mmorpg.game-simulation` and contains no Unity dependencies.

`UnityWorldCollisionLoader` loads the selected world's manifest through
Addressables at `WorldCollision/<WorldId>/manifest.json`, an address that
mirrors the `Generated/WorldCollision` folder of the local
`com.mmorpg.world-data` package. `UnityWorldCollisionStream` then loads
synchronously and checksum-validates chunks at
`WorldCollision/<WorldId>/<chunk>.bytes` around the initial player and the
authoritative local owner snapshot. Remote snapshot scheduling cannot move the prediction collision
window. The stream retains a larger chunk ring before unloading, which prevents
boundary churn. The join response carries SimulationWorker's collision revision,
and `NetworkMovementSession` does not start when the local revision differs. A
missing, corrupt, or coordinate-mismatched streamed chunk closes the active
session through the normal structured client failure path.

`ClientMovementPrediction`, reconciliation, grounded presentation, and remote
presentation all query the same mutable `ChunkedStaticCollisionWorld`. Loading
changes which authored chunks are resident but never changes SimulationWorker
authority or feeds presentation positions back into prediction.

`WorldCollisionAuthoring` defines the world id, chunk size, collision root, and
layer mask on an authored scene object. The Editor command
`MMORPG > Tools > World Collision > Bake Open Scene` scans enabled, non-trigger
BoxColliders, non-convex MeshColliders, and TerrainColliders below that root and
writes neutral authoring JSON plus versioned binary resources into `WorldData`.
Mesh vertices are baked in world space and terrain heights as a heightfield,
both as exact base64 floats. Unsupported collider types fail the bake
explicitly. The shared compiler canonicalizes signed floating-point zero before
encoding, so Unity and .NET produce identical chunk hashes for equivalent
transforms. Runtime code never scans the Unity scene or treats PhysX as network
authority.

`RemotePlayerView` is presentation-only. It has no input, camera, audio listener,
rigidbody, or solid collider; the prefab has no collider at all. At runtime it
adds one trigger capsule of the movement capsule's size, so the pointer can pick
the player for combat, and it reports that height for health bars. A trigger
never blocks movement, the camera, or the pointer. It stores the network entity
id, persistent character id, display name, and presentation archetype supplied
by the spawn message. `WorldSceneContext` presents the archetypes
`player.default` and `player.development_bot`. The prefab shows the human body
described in [Player Bodies](#player-bodies); with the body tint setting on, the
view tints it orange for a person and blue for a development bot, so neither
looks like the purple LocalPlayer. Its `RemoteMovementPresenter`, which moving Mobs share, buffers server states in
`RemoteMovementInterpolation`, advances an adaptive monotonic frame-rate
`RemoteRenderClock` two snapshot intervals behind the server, four ticks or
about 133 ms at 30 ticks and 15 snapshots per second,
`RemoteMovementPresenter.InterpolationDelayTicks`, and applies the grounded
presentation described above. One interval covers the wait for the next
snapshot and the second a snapshot that arrives up to one interval late. The
clock measures that delay against an estimate of the server tick that advances
with real time between snapshots, rises with any snapshot that arrives earlier
than it expects, and moves 5 percent of the way toward a later one, or all the way
when it arrives later than the whole render delay, because the latency changed. Measured
against the newest received tick instead, the delay shrank between snapshots
and grew at each one, so remote characters sped up and slowed down by about 30
percent at the snapshot rate. The clock uses bounded catch-up and slow-down
corrections during normal delivery. After a larger network stall it restores
the intended buffer delay instead of retaining permanent extra latency. A reliable despawn removes the matching view immediately. Missing
unreliable snapshots never decide entity lifetime. `WorldSceneContext` routes
the snapshot entities of remote players; Mob entities go to the world actor
presentation, as described in [Mob Movement](#mob-movement), and every other id
is ignored.

`OrbitCameraController` lives with the Camera on the LocalPlayerCamera child
owned by the LocalPlayer prefab and orbits the single CameraTarget pivot.
Dragging with the middle mouse button turns and tilts it at 0.25 degrees per
pixel, and the arrow keys turn it at 120 and tilt it at 60 degrees per second.
Tilt is clamped from 10 to 80 degrees. The scroll wheel changes the zoom
distance by 15 percent per notch between 3 and 30 meters, and the applied
distance eases toward it. The camera starts 12 meters away at a 40 degree tilt
behind the player, with a 55 degree field of view. A non-allocating spherecast
from the pivot pulls the camera in front of obstructions at once and eases it
back out, and it filters the local player hierarchy. Scrolling over interface
does not zoom. The cursor is never locked or hidden.

`PresentationExecutionOrder` fixes the late presentation order of a frame.
Characters move in `Update`, `OrbitCameraController` follows them in
`LateUpdate` at order 100, and `TemporaryCombatHud` places health bars and
damage numbers at order 200. Without a fixed order Unity could place them with
the previous frame's camera, and a bar trailed the head it belongs to and
jittered with the frame time.

`MovementDestinationMarker` is replaceable presentation that the bootstrap adds
to every World scene. It shows a flat, collider-free disc at the destination of
the path the predicted player still follows.

The network simulation collides with every baked box and one-sided triangle of
the selected World, including rotated ramps, steps, cover, boundaries, terrain,
and tunnel meshes. The capsule
dimensions, slope limit, step height, ground snap distance, and substep budget
come from the validated server join settings. Movement never uses Unity
CharacterController or PhysX to decide network position.

## Player Bodies

The LocalPlayer and RemotePlayer prefabs show the male human base body as
`PlayerVisual/Body`, a nested `Assets/Prefabs/Player/HumanMaleBody.prefab`,
which is a Prefab Variant of the exported `Human_Male.fbx` (see
[Art Pipeline](ART_PIPELINE.md#prefabs)). Every player shows this body until
the body type is chosen at character creation. The body has no collider; the
movement capsule, the remote pointer trigger, and the health bar height stay as
they were.

The body's Animator uses its Humanoid avatar,
`Assets/Art/Animation/HumanLocomotion.controller`, no root motion, and
`Cull Update Transforms`, so a body off screen skips its pose. Every clip it plays
comes from `Assets/Art/Models/Animations/Human_Actions.fbx`, the
[animation library](ART_PIPELINE.md#animation-libraries) of the human bodies, as
Humanoid motion that fits every body; the body's own model has no clips. The
controller moves between two states: `Idle`, and `Locomotion`, a 1D blend tree on `Speed` with `Walk` at
1.4 m/s and `Jog` at 5.0 m/s, the speeds the clips were authored for, played at
the rate `LocomotionRate`. The bool `Moving` switches between them with short
fixed crossfades. `CharacterLocomotionAnimator` drives these parameters from how
far its body moved over the ground in the frame, so the local player, remote
players, and anything else that moves a body animate the same way without
knowing about each other. It runs after every character moved and before the
Animator evaluates (`PresentationExecutionOrder.Locomotion`), smooths the speed
over 0.08 s, and ignores a jump faster than 20 m/s, such as a teleport or a
correction. `HumanLocomotion` turns the speed into the parameters: below the walk
speed, as when a character is encumbered, the walk plays slower, down to 0.3 of
its rate; between walk and jog the rate makes the blended stride match the
speed, from the lengths of both cycles; and above the jog speed the jog plays
faster. `PlayerBodyAssetTests` checks that the two speeds match
`tools/blender/human_base/animate.py`.

The body's skeleton also has the cloth chains of
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#cloth-chains), which
`ClothChains` lists: eight skirt chains on the hips, five cloak chains on the
upper chest, and a hood chain on the head. `ClothChainSolver` moves the chains of
the kinds a garment activates, with a `ClothProfile` for its stiffness, damping,
gravity, and margin, in `LateUpdate` after the Animator posed the body
(`PresentationExecutionOrder.Cloth`). Each step puts the chain bones back to
their rest rotations, lets every chain particle spring toward hanging from its
animated parent with inertia and gravity, keeps the bone lengths, pushes the
particles out of capsules around the hips, spine, neck, head, thighs, and shins,
and turns the bones to point at them. The capsule radii are the middle distance
of the body vertices that each bone moves most, measured once from the body's
own mesh in the bind pose, so the cloth lies on every body type's shape. Within
25 m of the camera the springs run, to 60 m the chains follow their targets
without inertia, and beyond they rest; a root that jumps more than 2 m in one
step, as in a teleport, starts its chain again. With no garment the solver does
nothing. `ClothChainSolverTests` poses the body with its Animator step by step
and checks that a robe stays outside the legs while jogging and swings with
them, that the chains settle when the body stands, a teleport, the levels of
detail, and the capsule radii.

The controller's third state, `Death`, plays the handmade `Death` clip of the
library. An Any State transition crossfades into it in 0.15 s while the bool `Dead` is
true, from any state, and it holds its last frame, the body lying on its back.
`CombatDeathPresentation` in `Mmorpg.Client.Combat`, which the bootstrap creates
in every World scene, tells every player body in view each frame whether its
character is dead: the local player from `OwnerCombatState` and every other
player from its `CombatantState`. A player without combat state, such as a
development bot, is never shown dead. `CharacterLocomotionAnimator.ShowLifeState`
turns the life state into the Animator: a body whose character dies in view
falls, a body that first shows its character already dead, such as a player who
died before coming into view or a client that joins while dead, lies in the end
pose at once, and a body whose character lives again stands in `Idle` at once,
because the respawn teleports it to the World spawn. While dead, an Animator that
starts over, after `Rebind` or when it is enabled again, goes back to the end
pose. `HumanLocomotionTests` covers these decisions, `PlayerBodyAssetTests` the
state and its transitions, and `PlayerBodyLifePlayModeTests` the real body in
Play Mode.

A body is made of skinned parts, one per equipment zone plus the eyes, teeth,
and tongue, so equipment can hide a zone later. Drawn as parts, a crowd paid
for every part of every body: 200 bodies cost about 20 ms of main thread time
per frame in the Editor. When `WorldSceneContext` spawns a player,
`PlayerBodyAppearance.CombineParts` draws the visible parts as one skinned
renderer, `CombinedBody`, with one submesh per material, and hides the parts.
`PlayerBodyMeshes`, owned by `WorldSceneContext`, builds the combined mesh once
per model and set of visible parts and shares it between every body that has
them; each body binds it to its own bones. 200 combined bodies cost about 6 ms,
with skinning down from 4.3 to 0.3 ms and culling from 3.4 to 0.35 ms. The
context destroys the meshes with the scene. `PlayerBodyMeshesTests` checks that
a combined body skins every vertex where its parts do.

The World Debug panel (F2) has the setting
`Tint bodies: you purple, players orange, bots blue`, off by default.
`ClientBootstrap` owns the `PlayerBodyTintSetting`, keeps it in `PlayerPrefs`
between sessions, and passes it to the panel and to `WorldSceneContext`, which
tints the local and every remote body when it changes and every body that
spawns. A tint multiplies the skin materials of the body, which
`PlayerBodyAppearance` lists, by a faint version of the color (35 percent of the
way from white), and leaves the eyes, teeth, and tongue alone.

A body also shows a look: another face or eye color from the
[appearance variants](ART_PIPELINE.md#appearance-layers) of its model. The body's
`PlayerBodyAppearance` references the model's `BodyAppearanceCatalog`,
`Assets/Art/Appearance/Human_Male_Appearance.asset`, with the slots `Eye` (`Teal`,
`Brown`) and `Face` (`Stubble`, `Scarred`), the material's own image first. A
`BodyLook` is a value that names one option per slot, with a stable key such as
`Eye=Brown;Face=Scarred`; a slot it does not name shows the material's own
image. Until looks are chosen at character creation, saved with the character,
and sent to other clients, the local player's look comes from the World Debug
panel (F2), whose Presentation section steps through every slot of the local
body's catalog with `<` and `>`. `ClientBootstrap` owns this
`PlayerBodyLookSetting`, keeps its key in `PlayerPrefs`, and passes it to the
panel and to `WorldSceneContext`, which offers the setting the local body's
catalog and shows a new look at once. Other players show the default look, and a
development bot shows a look picked from its persistent id with a mixed FNV-1a
hash, `BodyLook.Varied`, so a crowd of bots shows every combination and each bot
keeps its look.

A look also names the paint layers its body wears, such as `LeatherJerkin`, which
the catalog lists for its body with their material, image, layer, smoothness,
and metallic. Until equipment shows its paint, the Presentation section of the
World Debug panel has `Try on paint: <Name>` for every paint layer of the local
body's catalog. The body materials that paint layers paint draw with the skin
shader `Mmorpg/Character Skin`, URP Lit with up to four paint layers; see
[Art Pipeline](ART_PIPELINE.md#paint-layers). A look's paint layers on such a
material go into its copy, from the lowest layer up, and a material with another
shader ignores them.

The look and the tint draw with copies of the body's materials, another image in
`_BaseMap`, paint layers, a tinted `_BaseColor`, or any of them together, that `PlayerBodyMaterials` in
`WorldSceneContext` creates once per material, image, paint layers, and tint and shares
between every body that looks the same, so a crowd stays in the SRP Batcher; a
property block per body kept every body out of it. The default look without a
tint draws with the body's own materials. The look is applied to the combined
renderer, so bodies with other faces still share one combined mesh.
`PlayerBodyAppearanceTests`, `BodyLookTests`, and `ArtAppearanceCatalogTests`
cover the looks, their materials, and the catalogs.

A look also names the garments its body wears, such as
`Garment=LeatherJerkin` in its key. A garment is a
[garment texture](ART_PIPELINE.md#garment-textures) on its
[template](ART_PIPELINE.md#garments), named after the texture. The catalog lists
every garment that fits the body with its template's name and model for that
body, the texture's material, the visual slot, layer, and the slots it hides,
the body faces it covers, the body face under each of its triangles, and its
pull-in, from the texture's own masks when it has transparent parts and from the
template's otherwise, and every body part with the face of the body's editable mesh that each
of its triangles comes from and the vertex each of its vertices is. When the
look's garments change, `PlayerBodyAppearance` puts the body together again as
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#putting-a-character-together-in-unity)
describes, in `PlayerBodyMeshes`:

1. The garments of a slot that a worn garment of a higher layer hides are left
   out, such as trousers under a floor-length robe, while leg armor over the robe
   stays; the others are drawn from the lowest layer up.
2. A body triangle is left out when a drawn garment covers its face, and a
   garment triangle when a garment of a higher layer covers the body face under
   it.
3. A pulled-in body vertex moves inward along its normal by its distance, the
   largest any drawn garment asks for.
4. The drawn garments' meshes join the body's in one mesh, with their bones
   bound by name to the body's skeleton, whose bind poses they share, each
   drawing with its texture's material instead of its template's, and the
   vertices no triangle uses are left out.

The combined mesh is shared by every body with the same parts, garments, and
garment materials. A body wears one texture of a template at a time: of two that
its look names, the first in the catalog is worn, and trying one on in F2 or the
Wardrobe takes off the others.
Garment materials draw with the garment shader `Mmorpg/Garment`, URP Lit with
three dye channels, cut at alpha 0.5 and drawn from both sides; see
[Art Pipeline](ART_PIPELINE.md#unity-import-rules). The body prefab carries a
`ClothChainSolver`, and a garment with loose cloth lets it move the chains of its
profile: a robe the skirt chains, a cloak the cloak chains, and a hood the hood
chain. In the Editor, `MMORPG > Tools > Art Pipeline > Wardrobe` shows a body in
chosen garments playing its clips, and `WardrobePreviewTests` covers it. Until items show garments, the Presentation section of the World
Debug panel has `Try on garment: <Name>` for every garment of the local body's
catalog. Other players and bots wear none, and a corpse shows the bare body.
`PlayerBodyGarmentTests` covers what a worn garment hides and adds, layers, hidden
slots, pull-in, and sharing.

## World Actor And Interaction Client

Status: Permanent client foundation implemented with replaceable presentation

The client keeps permanent state below replaceable presentation:

- `WorldActorClientController` owns immutable, monotonic actor presence and
  replicated state across scene transitions and reconnect cleanup.
- `WorldInteractionClientController` owns correlated operations, the active
  authoritative interaction session, capability summaries, server closure, and
  uncertain-state cleanup.
- `WorldMenuController` owns the right-click world menu and the left-click
  default option, described in [World Menu](#world-menu).
- `WorldInteractionTargetingController` offers the menu options of registered
  NPC, resource node, corpse, and future world-object views, approaches a
  chosen target, and opens it.
- `GatheringClientState`, owned by `WorldInteractionClientController`, mirrors
  the worker's gathering state for the one node the player gathers from, as
  described in [Gathering](#gathering).
- `WorldActorView` provides presentation identity and target registration.
  `NpcView`, `MobView`, and `ResourceNodeView` add kind-specific presentation
  hooks without actor authority. A `MobView` follows its snapshots while it
  moves, as described in [Mob Movement](#mob-movement). An inactive actor, such
  as a dead Mob, is hidden until it returns. A depleted resource node instead
  stays visible: an authored prefab switches from its `Available` child to its
  `Depleted` child, and the fallback primitive shrinks to 35 percent of its
  height. Its target and interaction collider stay disabled until the node
  respawns, so it cannot be clicked.
- A presentation registry maps `presentationArchetypeId` to authored prefabs.
  The asset is `Assets/Content/WorldActors/WorldActorPresentationRegistry.asset`
  and maps all five authored archetypes to collider-free prefabs in
  `Assets/Prefabs/WorldActors` with URP materials in
  `Assets/Art/Materials/WorldActors`. The NPC and Mob prefabs are placeholders
  built from primitives. `resource.iron_vein` maps to `Resource_IronVein.prefab`,
  a Prefab Variant of the Blender model
  `Assets/Art/Models/WorldActors/Resource_IronVein.fbx` from the art pipeline:
  faceted rocks with iron ore below `Available`, and the broken rocks with rubble
  below `Depleted`, with the URP Lit materials `M_Resource_IronVeinRock` and
  `M_Resource_IronVeinOre`. Each entry is an Addressables
  `AssetReferenceGameObject`. The registry loads when a World scene opens, a
  prefab loads the first time an actor of its archetype appears, and the actor
  is presented once its prefab is ready. All handles are released when the
  World scene closes. An authored prefab keeps its own look. Only an unmapped
  archetype or a prefab that fails to load falls back to a tinted primitive,
  with one warning per archetype, and a missing registry logs one warning
  instead. An EditMode test fails when an authored archetype has no prefab.
- `TemporaryWorldInteractionPanel` is a replaceable uGUI action list over the
  permanent controller and capability contracts. It grows with its content,
  labels Character Bank and Recovery Storage capabilities as `Open ...` because
  they open the inventory, shows a vendor capability such as `Trade` by its
  name, says `Walking to <name>...` while the player approaches a target, draws
  the gathering progress described below, and shares its colors and buttons
  with the inventory panel through `TemporaryUiKit`.

The persistent realtime client decodes the actor, interaction, and gathering
messages through the shared GameProtocol source and keeps the explicit mismatch
path for older or partially updated clients. Typed insurance apply or remove,
quest accept or abandon, vendor purchase and sale, and start gathering payloads
ride on the capability action instead of a second NPC or item interaction
transport. `TryPurchase` and `TrySell` send the expected price and the
character revision, together with the offer definition and quantity or with the
item, its revision, and the quantity. `TryStartGathering` sends no revision or
trade terms. A confirmed Character Bank, Recovery Storage, or Vendor capability
raises `InventoryCapabilityOpened`, which opens the complete inventory view with
that storage or the vendor.

Mob loot needs no parallel controller. The corpse presence and nullable source-character contracts represent both live Mob
and durable player or boss corpses. The same client view, pointer target,
operation journal, canonical three-section snapshot, committed delta, inventory
refresh, and reconnect cleanup paths remain authoritative.

### World Menu

Every world click goes through `WorldMenuController`, a runtime scene component
that `ClientBootstrap` creates and passes to its providers. Each frame in which
the pointer is over the world it builds the menu for the pointer ray:

- `WorldPointerPicker.PickAll` lists every registered target and remote player
  under the cursor within `100` metres of the camera that no solid collider
  hides, nearest first and each once: a direct ray through the cursor first,
  then a `0.15` metre spherecast tolerance when the ray found nothing active.
- Each `IWorldMenuProvider` adds `WorldMenuOption` values in `WorldMenuOrder`,
  whichever registered first: `CombatTargetingController` (Combat, first),
  `WorldInteractionTargetingController` (WorldInteraction), and the controller
  itself (WalkAndExamine). A provider may block the whole menu, as combat does
  while the character is dead.
- `WorldMenuBuilder.Finish` sorts the options stably: the target options by
  target, nearest first, then Walk here, then Examine for each target, so
  provider order decides between options of the same target.

A left click runs the top option at once, and a right click copies the options
into the open menu at the pointer. While the menu is open the controller takes
no world action: a press in the world, Escape, or the pointer leaving the menu
closes it, and a press over interface may choose a row. The controller runs
before the features, at execution order -50, so combat, world interaction, and
the inventory panel ignore an Escape whose frame reports
`CancelConsumedThisFrame`, the Escape that closed the menu. `RunDefault` and
`OpenMenu` run a left or right click for a given ray, for tools and harnesses.
Options are plain values with their provider, an action code, the pointer
candidate, an entity id, an argument such as a capability id, and the ray, so
building the menu every frame allocates nothing. A provider runs its own
option, checks that the target still exists, because an open menu can outlive
its target, and reports whether it acted. Only after an option acted does the
controller raise `OptionExecuted`; combat clears its target on every such
option except its own and Examine, so a stale option that did nothing ends
nothing either.

- Walk here casts the stored ray at the ground through
  `LocalPlayerController.TryFindGroundPoint` and walks there.
- Examine posts the actor's `ExamineText` from its spawn, or "The remains of
  <name>." for a corpse, to the `GameMessageLog` that `ClientBootstrap` owns,
  while the target is still active.
- `WorldInteractionTargetingController` adds one option per
  `WorldActorClientEntry.MenuOptions` entry of an NPC or resource node, in the
  authored order the spawn carries, and Loot for a corpse. A Mob is no
  interaction target: `OffersInteraction` accepts corpses, NPCs, and resource
  nodes by the authoritative actor kind. Choosing an option approaches the
  target: the player walks to `2.5` metres, and the controller sends the open
  intent with the chosen capability id once the authoritative owner position is
  within `2.75` metres, inside the worker's start range. The walk aims closer
  than the open check because a route ends within its range plus the waypoint
  arrival tolerance. When the
  interaction opens, `WorldInteractionClientController.ActivateCapability`
  starts that capability as its panel button would: gathering starts after the
  advisory tool check, dialogue, insurance, and quest offers only open, and every
  other kind executes. A rejected open drops the follow-up. When the target's
  interaction is already open, the capability starts without a walk. A newer
  option or stop cancels the approach. A target without a route gives up at once,
  and an approach that ends out of range gives up after 1.5 seconds, both with a
  game message. Choosing another target first closes the active interaction, and
  the cancel action, Escape, closes it.
- `CombatTargetingController` adds Attack for a target that
  `CombatClickRules.CanAttack` accepts, described in
  [Combat Targeting And Approach](#combat-targeting-and-approach).

`TemporaryWorldContextMenu` is the replaceable uGUI over the controller. It shows
the left-click option and the number of other options next to the pointer,
except over bare ground, and the open menu as a `Choose Option` header, one row
per option, and `Cancel`. Its canvas sorts above every panel and below the death
overlay. Target names are colored by `WorldMenuTargetStyle`: NPCs and Mobs
yellow, objects cyan, corpses orange, and players white. It closes the menu when
the pointer moves 24 reference pixels outside it. `TemporaryGameMessagePanel`
shows the newest five game messages in the bottom left corner without taking
raycasts and fades each out after 12 seconds.

The menu only chooses an intent. SimulationWorker independently owns the `3.0`
metre start range, `3.5` metre maintain range, closest-point bounds distance,
line of sight, target revision, capability availability, and session
validation.

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
insurance lineage id. Temporary slots show an `I` badge for insurance and a `P`
badge for protection on death, and the item tooltip names the policy and renders
`Insurance NPC`, `Quest grant`, or `Catalog rule` as the source. Recovery
headings translate the durable source kind into consumed insurance or
protected-on-death delivery labels.

Current corpse presentation registers with the shared target controller and
its open view consumes the shared one-active-interaction lease while retaining
`CorpseClientController`, the corpse protocol, and durable transaction
authority. Shared targeting changes selection and session coordination, not
corpse custody.

All new Editor tools remain under the established menu root:

- `MMORPG > Tools > Item Catalog` edits item definitions, including their
  `Value` and `Tool Kinds`, and its `Definitions | Vendors` toolbar toggle
  switches to the vendor profiles. Vendors mode lists them with `New` and
  `Remove` and edits the vendor id, display name,
  `Selling Price (basis points)`, `Buying Price (basis points)`,
  `Buys Categories`, and the ordered offers through an `Add Offer` dropdown of
  eligible definitions with `Up`, `Down`, and `Remove`. Live findings are mapped
  to the vendor and field. The economy settings and the tool kind identities
  stay hand-edited in `content/items/catalog.json`. The authoring contract is in
  [Item Catalog Authoring](../content/items/README.md).
- `MMORPG > Tools > Content > Actor Studio` owns searchable NPC, Mob, and
  resource node authoring, templates, safe duplication, capability composition,
  references, multi-selection bulk editing, Unity Undo, live validation,
  preview, compile, and verify. The `Resource Node` kind and the
  `New Resource Node` template create a neutral, invulnerable node with one
  gathering capability that names the first resource profile. Each capability
  has a `Profile` dropdown for the kinds that need one: the vendor profiles of
  the compiled item catalog for a vendor capability, and the resource profiles
  for a gathering capability. Resource profiles stay hand-edited in
  `content/actors/catalog.json`.
- `MMORPG > Tools > Content > Spawn Authoring` owns scene handles, ground
  snapping, spawn areas and groups, patrol paths, prefab preview,
  multi-selection bulk editing, Unity Undo, live validation, canonical change
  preview, save, compile, and verify. It places resource nodes like any other
  actor, and patrol paths stay limited to Mob spawns.

Editor scene components are authoring adapters. They import and export stable
neutral definitions, but only compiled WorldData creates runtime actors. Actor
and spawn content must remain buildable and verifiable without opening Unity.

The persistent bootstrap creates the actor, presentation, targeting,
interaction, and temporary panel controllers at runtime. A new archetype works
through the fallback presentation without scene edits until it is added to the
presentation registry asset. A content creator uses the Editor windows only
when changing definitions, spawn source, or the presentation registry asset.

### Mob Movement

A Mob that moves travels in the unreliable `SimulationSnapshot` like a remote
player: an entity with its world actor entity id and a `PlayerState`. The
worker includes it only while its pose changes, walking or turning to face its
target, and sends one reliable `WorldActorState` with the rest pose when it
stops. A Mob that never moves keeps its reliable pose as before.

`WorldActorPresentationController` subscribes to the snapshots of its realtime
client and hands every entity that names a presented `MobView` to that view
through `RouteSnapshot`. Players, NPCs, resource nodes, and ids without a
presentation, such as a Mob whose prefab still loads, are ignored. Each
`MobView` owns a `RemoteActorMovement` in `Mmorpg.Client.Gameplay`, configured
from the joined `NetworkMovementSession`. It keeps the Mob rules on top of the
shared `RemoteMovementPresenter`, so a Mob uses exactly the remote player
interpolation, render clock, and 100 ms delay:

- **Start.** The first snapshot after a rest anchors the reliable pose one
  render delay before it and restarts the render clock at the anchor, so the
  Mob stands where it was and then walks into the snapshot instead of jumping
  ahead.
- **Settle.** A `WorldActorState` that arrives while snapshots still play
  becomes their final state, one snapshot interval after the newest snapshot,
  so the Mob walks onto its rest pose without a jump. A later snapshot that is
  not newer than that state is ignored. A rest pose equal to the newest
  snapshot changes nothing.
- **Jump.** A `WorldActorState` without any snapshot, one after the snapshots
  played out at another pose, and one more than
  `RemoteActorMovement.TeleportDistance` (3 m) from the newest state, such as an
  evading Mob that the worker placed home, place the Mob at once, as before. A
  snapshot that far away restarts the interpolation at the snapshot.
- **Restart.** A Mob that settled at its rest pose does no work per frame, and
  its next snapshot starts again from the rest pose.
- **Cleanup.** A hidden Mob, such as a dead one, clears its interpolation and
  ignores snapshots, and it returns at its reliable pose. A despawn destroys the
  view, and a respawn creates a new view without interpolation.

The presentation controller applies every actor again on each actor change, so
a `MobView` moves on a reliable state only when its state revision is newer.
The grounded presentation of a Mob uses the session's character capsule,
because the protocol does not carry a Mob's locomotion capsule; on walkable
slopes its slope support offset therefore differs from the worker's by a few
centimetres for the current profiles. Mob presentation never loads collision
chunks; outside the resident chunks it shows the replicated height unchanged.

Combat presentation reads the presented `MobView` transform through
`CombatantViewLocator` every frame, so health bars, damage numbers, the target
frame, arrows, and the approach follow a moving Mob.

### Gathering

Mine, a resource node's first and left-click option, walks the player into
range and opens it like any other target, and `WorldInteractionClientController`
then starts gathering through the chosen gathering capability. It first checks
that an item with a tool kind is equipped in the Tool slot; otherwise the panel
says to equip a gathering tool. Mine again while that node already gathers does
nothing. The check is advisory and passes while the inventory is still loading,
because GameService checks the required tool kind on every yield.

`GatheringClientState` applies the worker's `GatheringState` messages. `Started`
must belong to the open interaction and starts a cycle timer. `Yielded` adds the
gathered quantity and restarts the timer, and every yield refreshes the
inventory to its committed item-state revision. `Stopped` keeps the gathered
count and the worker's stop message. `Yielded` and `Stopped` may still arrive
after the interaction closed, and a `Yielded` even after `Stopped`, because a
cycle in flight still completes; it adds to the gathered count. A message for
another gathering session is ignored, but a yield still refreshes the
inventory. A session end clears the state.

While gathering, the interaction panel shows the capability name with a
trailing ellipsis, such as `Mine...`, the gathered count, and a progress bar for
the current cycle. After a stop it shows the gathered count and the reason, and
the capability button, such as `Mine`, starts again. The worker owns every
cycle and every stop: moving, closing the interaction, a depleted node, or a
rejected yield, such as a missing tool or a full inventory.

## Combat Client

Status: Protocol handling, the combat state mirror, combat targeting and
approach, the temporary combat presentation, and Mob interpolation implemented,
see [Mob Movement](#mob-movement) and the
[Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md#unity-client).

Combat is server-authoritative and never predicted. `RealtimeSimulationClient`
decodes the four server messages of the Combat family and the Items-family
quantity change in the joined state and raises a typed event for each:
`CombatIntentCompleted`, `CombatantStateReceived`, `CombatEventBatchReceived`,
`OwnerCombatStateReceived`, and `OwnerItemQuantityChanged`. A decode failure
fails the protocol like every other control message, and so does a
`CombatantState` for an entity without reliable presence, because the worker
sends it right after every spawn of a combatant. `TrySendCombatIntent` sends a
typed `CombatIntent`, built with `RealtimeCombatIntent.CreateAttackTarget` or
`CreateClearTarget`, on the reliable ordered control channel.

`CombatClientController` is a persistent bootstrap-owned adapter that the
bootstrap initializes with the realtime client. Its `CombatClientState` keeps:

- One combatant state per visible player or Mob entity. A newer state revision
  replaces it, an older one is ignored, and an equal revision with different
  values is a conflict that disconnects with `combat_state_conflict`. The state
  of an entity or world actor is removed when it despawns, and a combatant that
  spawns again starts from the state that follows its spawn.
- The owner's combat state from `OwnerCombatState` with the same revision rules,
  where only the server tick of a repeated revision may differ.
- The combat intents that wait for a result, keyed by operation id, at most
  eight. `TryAttackTarget` and `TryClearTarget` check only what must fail, such
  as no joined session, the character itself, or a dead character, and the
  worker validates every rule. A result completes its own intent in any order;
  a result without a pending intent of its kind disconnects with
  `combat_intent_result_mismatch`. The latest result stays readable as
  `LastIntentResult`.

Combat event batches are forwarded through `EventsReceived` in simulation order
for presentation; health always comes from the combatant and owner states. The
state clears on `SessionJoined` and `SessionEnded`, so reconnect cleanup never
compares revisions across sessions.

`Died` fires when an owner state says dead and the previous one did not, or
belonged to an earlier life, so it fires once per death, also when the client
joins while dead. `Respawned` fires when an owner state's life sequence
advances and the life state is alive again, also when the dead state itself was
never received. The
worker then has moved the character to the World spawn with full health. The
owner's movement prediction needs no extra reset for that: the teleport arrives
as the next owner snapshot, the worker ignores movement inputs while the
character is dead and halts its path, and a reconciliation of 3 metres or more
is a hard correction that snaps the presentation to the reconciled state
instead of blending. A respawn within 3 metres of the death position blends
like any small correction.

### Combat Targeting And Approach

`CombatTargetingController` is the permanent world menu provider of combat,
created by the bootstrap in every World scene and registered with the
[World Menu](#world-menu) at `WorldMenuOrder.Combat`, before world interaction.
Its decisions live in pure classes that EditMode tests cover:

- `CombatClickRules.Describe` turns each `WorldPointerPicker` candidate into a
  Mob, a player, another target, or nothing, by the authoritative actor kind.
  While the owner state says dead, combat blocks the whole menu, so a click
  neither walks, attacks, nor interacts. Otherwise `CanAttack` decides which
  targets offer `Attack`: a living Mob, and a living player only when the
  joined Shard's `ShardRules.PlayerVersusPlayer` allows it. A Mob that cannot
  be attacked now, such as one walking home, offers no Attack, so a left click
  on it walks there. A player without combat state, such as a development bot,
  is never a target. `AttackableTarget` makes this decision for one candidate,
  and choosing a stored Attack asks it again, so a target that died or left, or
  a character that died, while the menu stayed open sends no attack.
  Every other option except Examine, such as Walk here, Talk-to, or Mine, sends
  `ClearTarget` when there is a target, and so does Escape.
- `CombatTargetSelection` names the target the client acts on. The worker owns
  the target, so it is the owner state's target, except while a request is on
  its way, when it is the requested target. A confirmed request keeps its target
  until a newer owner state arrives, because the result can arrive before the
  owner state that shows it. A rejected request falls back to the owner's
  target and keeps the worker's error for the HUD. A request that its session
  dropped is forgotten.
- `CombatApproachPlanner` walks the character into range through
  `LocalPlayerController.Approach`. The range is the weapon range minus
  `0.75` metres, at least `0.5` metres, because the worker measures to the
  target's bounds in three dimensions while the route stops on the planar
  distance. `CombatLoadoutRules.TryGetWeaponRange` reads the weapon range from
  the bundled item catalog for the item in the `main_hand` slot of the
  committed inventory, as the worker resolves the loadout; without a weapon the
  character does not walk, and the worker reports `WeaponMissing`. The first
  approach also stops a walk that is already in range. While walking, a target
  that moved more than `1.5` metres from the planned goal gets a new route.
  While standing, the character walks again when the target stands beyond the
  weapon range or the worker reports `OutOfRange`, a margin closer than its
  measured distance, and it walks `2` metres closer each time the worker
  reports `LineOfSightBlocked`. It never walks while the worker reports
  `Attacking`, and it starts at most one approach per second by itself. Losing
  the target stops only the planner's own approach, never a walk that a newer
  option started.

On `Died` the controller drops its requests and stops the navigator, and on
`Respawned` it stops it again, so a path planned before the death never runs
from the spawn. The worker clears the target on death.

`CombatantViewLocator` finds the presentation of a combatant by network entity
id: the local player from `WorldSceneContext.LocalPlayer`, another player from
`WorldSceneContext.TryGetRemotePlayer`, and a world actor from
`WorldActorPresentationController.TryGetView`, with the height for health bars.
Approach, arrows, health bars, and damage numbers use it and read the presented
position every frame, so they follow a moving Mob; the worker never sees these
positions.

`CombatServerClock` is the client's best estimate of the worker's current tick
for presentation: the tick of the newest owner snapshot,
`RealtimeSimulationClient.LatestServerTick`, plus the time since it arrived,
`LatestSnapshotAgeSeconds`, plus half of `RoundTripTimeMilliseconds`, at the
session's tick rate. The worker keeps sending owner snapshots while the
character is dead, so the clock keeps running for the respawn countdown.
`RoundTripTimeMilliseconds` reads LiteNetLib's round trip time; LiteNetLib's
`Ping` is only half of it.

### Combat Presentation

Status: Temporary. Every object is created at runtime, so no scene, prefab, or
asset changes are needed.

`TemporaryCombatHud` in `Mmorpg.Client.Ui` is a runtime uGUI canvas at sorting
order 20, below the interaction panel at 40 and the inventory at 100. Nothing on
it takes a raycast, so it never blocks a world click. It shows:

- An owner frame at the top left with health and a bar, the live arrow count
  from `OwnerCombatState`, and the attack status as a short label from
  `CombatHudPresentation.AttackStatusLabel`, such as `Out of range`,
  `No line of sight`, or `No weapon equipped`, plus `In combat`. The worker's
  error for a rejected request shows below it for four seconds.
- A target frame at the top centre for the target of
  `CombatTargetingController` with its name, health, and whether it is in
  combat or returning home.
- Health bars over every damaged living combatant in view within 60 metres:
  the local player from its owner state, other players and Mobs from their
  combatant states.
- A floating damage number at the hit target for every `Hit` event, white for
  Normal damage, rising and fading over 1.1 seconds.
- A death overlay while the owner state says dead, with a countdown from
  `RespawnTick` and `CombatServerClock` that rounds up to whole seconds. The
  overlay is a nested canvas that overrides the sorting order with
  `DeathOverlaySortingOrder`, 150, so an open inventory or corpse view never
  hides it.

`CombatDeathPresentation` is not temporary: it plays the handmade `Death` clip
on the body of every player who dies in view, as described in
[Player Bodies](#player-bodies).

`CombatProjectilePresentation` in `Mmorpg.Client.Combat` draws a simple arrow,
a collider-free stretched cube, from the attacker to the target of every
`AttackReleased` event whose impact tick lies after its batch tick, so a Mob
bite shows none. It follows the target and lands at the impact tick by
`CombatServerClock`; an arrow that already landed is not drawn.

The equipped ammunition slot of `TemporaryInventoryPanel` shows the live arrow
count from `OwnerCombatState` when its `AmmunitionItemInstanceId` names the
equipped stack, and the committed quantity otherwise, through
`CombatLoadoutRules.DisplayedAmmunitionQuantity`. A live count that differs
from the committed quantity always shows, even 0 or 1, and the tooltip says
how many are left. The drawn slot updates in place when that count changes, so
a shot never redraws the panel or cancels a drag.

## Test Architecture

EditMode tests cover client session cleanup, operation serialization, structured
Problem Details parsing, diagnostic prefix formatting, configuration loading,
PlayerInput action binding, invalid array rejection, local reconciliation,
redundant input batches with the unconfirmed path, background route planning
with intent and path ids, remote interpolation, collision resource loading,
authored player prefab and orbit camera contracts, and development-world scene
composition. They also execute the shared encumbrance reference points, movement
prediction, the movement and planner determinism fingerprints, and monotonic
carry-revision handling used by SimulationWorker. EditMode coverage also round-trips
typed Secure Container intents and committed item results without adding client
authority fields, loads the real bundled gameplay and
presentation catalogs, verifies cache reuse and mismatch rejection, validates
complete and focused snapshots, exercises stale and divergent revision handling,
correlates operation ids, and checks specialized slots, Secure Container rules,
non-empty Bags, split quantities, the exact hard cap, external corpse swaps, and
committed-revision refresh eligibility. Corpse coverage verifies presence assembly, duplicate and stale chunks, complete canonical
three-section views, targeted delta consistency, monotonic revisions, every
typed corpse intent, and corpse drag payload identity. `CorpseViewClosureRuleTests`
pins which closure codes end with a neutral notice and which stay errors,
including unknown and blank codes, and `CorpseClientStateTests` checks that an
emptied closure after the last loot leaves a notice, an invalidation an error,
and an own close no message. They also check that a closure during a pending
loot, refresh, deposit, move, open, or close keeps the operation correlated and
busy until its late result, that a late success or an expected late rejection
keeps the closure's notice or error, that a snapshot after the closure or after
a rejected open is ignored, that closures of other corpses are ignored, that a
result with the wrong id, kind, or corpse, or a second result, is a conflict,
and that a session reset drops an operation that outlived its view. They pin
that an automatic refresh keeps the rejection that asked for it through its
success and snapshot until the player's next operation, that its own rejection
replaces the error, and that a closure or session reset still ends it.
`CorpseClientControllerTests` drive the controller through a fake
`IRealtimeCorpseChannel`: a rejection that requires a refresh stays visible
through the automatic refresh until the player refreshes, a rejected automatic
refresh starts no other, a stale delta refreshes without clearing an earlier
rejection, also when it went stale during a rejected operation, and a closure
or session end stops the refresh. Inventory presentation
coverage pins the paper doll positions and the extra rows for unknown slots, the
carry meter fill, marker, and load levels including a death overflow, blocked
equipment slots, catalog display names for tags, slots, and the Secure Container
tier, quantity and policy badges, and the item tooltip text.

Combat coverage in `CombatClientTests` verifies monotonic combatant and owner
revisions and their conflicts, despawn removal, intent completion by operation
id and kind, the pending intent bound, reconnect cleanup, the respawn signal,
the death signal once per death, event forwarding, and that every combat
message, and every other reliable worker message outside the session
lifecycle, is ignored while leaving.
`WorldMenuTests` verify the menu order of target options, Walk here, and
Examine, that only an option that acted is announced, a blocked menu, the
capability options of NPCs and resource nodes in authored order and Loot for
corpses, that stale options do nothing, Walk here and Examine with their game
messages, the walk distance inside the open check, the activation of each
capability kind, the game message log, the colored labels and hover hint, and
the message fade. `WorldActorClientFoundationTests` also
verify that a spawn keeps its examine text and menu options and that a changed
list conflicts without a despawn.
`CombatTargetingTests` verify that combat adds its menu options before world
interaction whatever registered first, that a dead character attacks nothing,
that a stored Attack targets only what can be attacked when it is chosen,
the Mob and player Attack rule and the player versus player rule, that world
interaction no longer accepts Mobs, the nearest-first and once-per-target
candidate list, the remote player trigger, target selection with confirmed,
rejected, cleared, and stale requests, and the approach distance, re-approach,
line-of-sight, retry, and stop rules. `CombatPresentationTests` verify the
weapon range from the main hand item, the live arrow count and its slot label,
the server clock with tick wrap, the respawn countdown, a distinct label for
every attack status, health bar visibility, and projectile timing. A PlayMode
test draws the equipped arrow slot of the real inventory panel and follows the
owner state's live count without a redraw.
`InventoryQuantityChangeTests` cover the in-place arrow patch, a used-up stack,
a pending operation that stays busy, and the refresh fallback for a missed or
skipped revision, an unknown or wrongly placed stack, a growing quantity, and an
incoherent snapshot.
`InventoryPredictionTests` verify that a committed change patches the snapshot
to exactly what a reload returns, that a change that does not follow the known
revision, lacks a container revision, touches the equipped Bag or Recovery
Storage, or meets an occupied slot needs a reload, and that an item moving into
an unseen container leaves the view. They also pin that a predicted move shows
at once and its committed change confirms it, that the change of the sent
operation is not applied twice, so a swap never swaps back, that later moves
build on earlier predictions, that a rejection undoes every pending move and
names their items, that a newer state that no longer fits undoes a pending move
but keeps a sent one waiting, the split stack's temporary id, merge, swap, and
equip with its swap and weight, invalid moves, and the bound of eight pending
moves. `InventoryStoragePredictionTests` cover
claim and Bag patches, a claim that leaves an item behind, predicted Bag equips
and unequips with their capacity, claims into the first free slots, purchases
that top up stacks or take a free slot, an unaffordable purchase, sales, corpse
loot that merges or swaps, deposit swaps, and corpse view edits.

World actor coverage verifies monotonic actor presence and state,
reconnect cleanup, operation correlation, authoritative capability summaries,
direct-ray and spherecast target selection, unrelated-collider filtering, and
deterministic Actor Studio and Spawn Authoring compiler integration.

NPC lifecycle coverage round-trips typed lifecycle payloads and committed
item-revision messages, verifies safe policy-source mapping, and keeps the
temporary panel below the persistent inventory and interaction controllers.

Vendor and gathering coverage in `VendorAndGatheringClientTests` prices offers
with the shared rule in content order, checks purchases against the offer,
quantity, balance, and weight, rounds sales down and accepts partial stacks,
refuses quest, protected, equipped, and banked items, follows one gathering
session, keeps the tool check advisory until the inventory loads, and keeps a
depleted resource node visible but untargetable. Further EditMode tests map the
snapshot currency, edit vendor profiles, values, and tool kinds in the Item
Catalog window, create the Actor Studio resource node template, and count the
Iron Vein spawns.

Mob corpse coverage verifies the normal and boss corpse settings through
the neutral Actor Studio round trip. Existing corpse protocol and client tests
continue to cover the shared Mob presentation path, so no scene or prefab
fixture is added.

`MobMovementPresentationTests` verify that snapshot entities reach only
presented Mob views and that a despawned or unconfigured view ignores them, the
shared delay and snapshot interval, a walk that starts at the rest pose and
settles on the rest state without a frame step of a whole tick, that a late
older snapshot cannot pull the Mob back, the direct jump without recent
snapshots and for a teleport, that a settled rest does no per-frame work, the
restart from a settled rest, and that a Mob view ignores an unchanged state,
clears its movement while hidden, and returns at its reliable pose.

`ClientAssemblyArchitectureTests` pins the assembly graph described in
[Assemblies](#assemblies). `ClientAddressablesTests` compares the committed
Addressables settings with `ClientAddressablesLayout`, resolves every fixed
address, every generated collision manifest and chunk, every presentation icon,
and every registry prefab, loads content through `ClientAssets`, and fails on any
`Resources` folder or call in the client and shared packages.

PlayMode tests verify that loading LoginMenu creates the persistent client
bootstrap, realtime, inventory, corpse, actor, interaction, combat, targeting,
and presentation controllers plus the runtime login panel. World-scene coverage
loads `DevelopmentWorld1` through Addressables, opens and closes the runtime uGUI
inventory root, and finds the runtime corpse and actor presentation controllers,
the combat targeting controller without a target, the world menu without an
open menu or hover options, the context menu and game message panels with
nothing visible, the projectile presentation, and the combat HUD, which shows
nothing without a joined session and has no raycast target. Every panel canvas,
the context menu's included, sorts below the death overlay.
An inventory panel test applies a committed snapshot without a GameService and
checks that the complete view draws one slot per catalog equipment slot, the
blocked slot of a two-handed item, one shared slot size in every module, the
carry meter fill, a hover tooltip, and a character column that keeps its place
in the character-only view.
Further PlayMode tests verify that returning to a built-in scene releases the
World scene's Addressables handle and that an unknown or built-in scene is not
an available World scene. The PlayMode tests also pass with the "Use Existing
Build" play mode script against built bundles.

Manual flows and expected results are documented in
[Local Development](LOCAL_DEVELOPMENT.md).

## Extension Rules

- Put client code in the assembly of its area. A new reference between client
  assemblies must point to a lower layer and be added to
  `ClientAssemblyArchitectureTests`.
- Pass services from `ClientBootstrap` through `Initialize`; never add static
  service access or reference `Mmorpg.Client.App` from runtime code.
- Load runtime content only through Addressables. Put an asset in a content
  folder below `Assets/Content`, or a World actor prefab below
  `Assets/Prefabs/WorldActors`, refer to fixed addresses through
  `ClientAddresses`, and release every handle you own. Never add a `Resources`
  folder.
- Change scenes only through `ClientSceneLoader`. A new World scene needs its
  catalog entry and `MMORPG > Tools > Addressables > Apply Client Layout`.
- Keep HTTP serialization and failure mapping inside the API layer.
- Keep realtime transport state and packet handling inside
  `RealtimeSimulationClient`, not scene panels.
- Keep deterministic movement rules in `GameSimulation`, not in transport or
  presentation components.
- Wrap every float intermediate in `GameSimulation` in an explicit `(float)`
  cast. Mono keeps float intermediates in double precision, so an expression
  such as `(a * b) + c` without casts predicts differently in Unity than on the
  worker. The movement determinism tests catch a missing cast.
- Keep authored collision in `WorldData`; never duplicate scene geometry as
  hand-maintained server constants.
- Keep prediction and reconciliation separate from remote interpolation.
- Present every remote entity that moves through snapshots with
  `RemoteMovementPresenter`, so players and Mobs share one render clock, delay,
  and grounded presentation.
- Plan routes only through `LocalPlayerNavigator` and the shared planner, and
  offer world actions as `IWorldMenuProvider` options of `WorldMenuController`
  instead of reading the mouse in feature code.
- Register a world menu provider with a `WorldMenuOrder` value and never rely
  on registration order. Read pointer targets from the menu builder, which
  picks them once through `WorldPointerPicker` for every feature.
- Keep persistent cross-scene session state in `ClientSession` and
  item and corpse state in their bootstrap-owned controllers, not scene panels.
- Keep planned actor and world-interaction state in bootstrap-owned controllers,
  not actor prefabs or the temporary interaction panel.
- Keep combat state in `CombatClientController`. Presentation reads its state
  and never decides range, damage, or death.
- Keep combat decisions, such as the Attack rule, target selection, and
  the approach, in the pure classes of `Mmorpg.Client.Combat`, and keep its
  MonoBehaviours thin. Find combatant presentations through
  `CombatantViewLocator` instead of searching the scene.
- Keep one operation owner per panel until a more explicit navigation state
  machine replaces it.
- Give every type that holds static state a
  `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
  reset, so the client stays correct when Enter Play Mode Options disable domain
  reload. An EditMode test lists the types that must have one.
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

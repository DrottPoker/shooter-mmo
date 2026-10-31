# Unity Client Architecture

The Unity client in `src/Client` presents the world, takes the player's input,
predicts the local player's movement, and talks to GameService over HTTP and to
SimulationWorker over UDP. GameService and SimulationWorker own every
authoritative state; the client caches and presents it, as
[Unity Client](PROJECT_ARCHITECTURE.md#unity-client) describes.

This document covers how the client is built and how to extend it. What the
player sees and does is in [Game Features](GAME_FEATURES.md). How characters
look and why is in [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md), and
how art is made and imported is in [Art Pipeline](ART_PIPELINE.md). The world
actor contract that the client implements is in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).

## Project Boundary

The Editor version is pinned in `src/Client/ProjectSettings/ProjectVersion.txt`;
see [Requirements](LOCAL_DEVELOPMENT.md#requirements). Runtime code is split into
one assembly per area, described in [Assemblies](#assemblies), and all runtime
content loads through Addressables, described in
[Content Loading With Addressables](#content-loading-with-addressables).

Only the runtime UI is temporary while the custom UI is being designed. The
bootstrap, state ownership, API layer, networking, scene lifecycle, input,
camera, and gameplay systems are long-term foundations and are built to
production standards from the start. A temporary panel only presents state that
a permanent controller owns.

The World maps, the local and remote player prefabs, the camera, the input
actions asset, and the spawn points are authored Unity assets; runtime code never
generates them. Runtime presentation creates only replaceable visuals of its own,
such as the destination marker, arrows, corpse capsules, and fallback actor
models, through `RuntimePrimitives`; see [Configuration](#configuration).

How to open, run, and build the client is in
[Unity Client](LOCAL_DEVELOPMENT.md#unity-client).

## Folder Layout

`src/Client/Assets` holds:

| Folder | Holds |
| --- | --- |
| `Scripts` | Runtime code, one folder and assembly per area |
| `Editor` | The Editor-only assembly `Mmorpg.Client.Editor` |
| `Tests` | The EditMode and PlayMode test assemblies |
| `Content` | Runtime content loaded through Addressables, and `Player/PlayerBodyModels.asset` |
| `Prefabs` | `Player`: the player and body prefabs; `WorldActors`: actor presentation prefabs |
| `Scenes` | `LoginMenu`, `CharacterSelect`, and the World scenes |
| `Input` | The `PlayerControls` input actions asset |
| `Art` | What the art pipeline imports: models, Animator controllers, appearance catalogs, materials, shaders, textures, and generated terrain; see [Layout](ART_PIPELINE.md#layout) |
| `Settings` | The URP assets in `Settings/Rendering` |
| `AddressableAssetsData` | The committed Addressables settings |

Subfolders of an assembly folder only group files. Every type keeps the
namespace of its assembly, which `ClientAssemblyArchitectureTests` checks. Put a
new file in the folder of its area, and add a subfolder when an area grows a
group of files of its own.

### Scripts

Each folder below `Assets/Scripts` is the assembly of that name. `Gameplay`, the
largest, is split by area:

| Folder | Holds |
| --- | --- |
| `Gameplay` | `WorldSceneContext`, `GameMessageLog`, and `PresentationExecutionOrder` |
| `Gameplay/Player` | The local player: controller, input, route navigator, orbit camera, pointer focus, and destination marker |
| `Gameplay/Movement` | Prediction, local and grounded presentation, and the remote interpolation and render clock that players and Mobs share |
| `Gameplay/Bodies` | Character bodies and their looks, garments, attachments, and materials, the corpse bodies, the remote player view, and `CharacterBody` |
| `Gameplay/Animation` | Human and creature locomotion and their Animator drivers |
| `Gameplay/Cloth` | The cloth chains on the bodies with their solver and scheduler; the cloth math itself is the assembly `Mmorpg.Client.Cloth` |
| `Gameplay/Diagnostics` | The Debug menu's collision and path overlay and its settings |

### Editor

| Folder | Holds |
| --- | --- |
| `Editor` | `ClientAddressablesLayout` and `WorldCollisionBaker` |
| `Editor/Art` | The import side of the art pipeline: model import rules, appearance catalogs, project materials, topology maps, manifests, and `CreaturePresentationBuilder` |
| `Editor/Cloth` | Cloth measurement, the stress motions, and the cloth fixtures for the art pipeline's checks |
| `Editor/Wardrobe` | The Wardrobe window and its preview |
| `Editor/DevelopmentTools` | The Inventory Item Grants window and its process runner |
| `Editor/DevelopmentWorlds` | The development World builders; see [Development Worlds](DEVELOPMENT_WORLDS.md) |

The item catalog, Actor Studio, and Spawn Authoring windows live in the shared
WorldData package at `src/Shared/WorldData/Editor`.

### Tests

| Folder | Holds |
| --- | --- |
| `Tests/EditMode` | The EditMode assembly, with a folder per area: `Art`, `Bodies`, `Cloth`, `Combat`, `Core`, `Items`, `Movement`, `Protocol`, and `WorldActors` |
| `Tests/EditMode/Fixtures` | Test-owned fixtures: a collision world, an item catalog, and actor content |
| `Tests/EditMode/Shared` | Test sources that the .NET test projects compile too |
| `Tests/PlayMode` | The PlayMode assembly |

[Test Architecture](#test-architecture) says where a new test goes.

### Editor Commands

Every Editor command of the project sits below `MMORPG > Tools`, and a new one
goes there too:

| Command | Does | Described in |
| --- | --- | --- |
| `Addressables > Apply Client Layout` | Reapplies and validates the Addressables layout | [Content Loading With Addressables](#content-loading-with-addressables) |
| `World Collision > Bake Open Scene`, `Bake World Scenes` | Bakes World collision | [Rebuild And Bake Workflow](DEVELOPMENT_WORLDS.md#rebuild-and-bake-workflow) |
| `Development Worlds > Rebuild Development World 1 Environment`, `Rebuild Development World 2 Environment` | Rebuilds a development World scene | [Rebuild And Bake Workflow](DEVELOPMENT_WORLDS.md#rebuild-and-bake-workflow) |
| `Inventory Item Grants` | Development items, currency, and corpses for offline characters | [Development Item Tools](#development-item-tools) |
| `Item Catalog` | Edits item definitions and vendor profiles | [Unity Editor Workflow](../content/items/README.md#unity-editor-workflow) |
| `Content > Actor Studio`, `Content > Spawn Authoring` | Edits actors and their spawns | [Unity Content Tools](../content/README.md#unity-content-tools) |
| `Art Pipeline > Wardrobe`, `Update Appearance Catalogs`, `Create Missing Materials`, `Build Creature Presentations`, `Measure Cloth`, `Write Cloth Fixtures` | Art pipeline tools | [Art Pipeline](ART_PIPELINE.md) |

## Assemblies

Runtime code under `Assets/Scripts` is split into one assembly per area. Each
folder holds one asmdef, and every reference between client assemblies points to
a lower layer, so the compiler rejects a cycle or an upward dependency. The
layers from the bottom up:

| Assembly | Owns | Client references |
|---|---|---|
| `Mmorpg.Client.Cloth` | `ClothPose`, the cloth's target pose on plain data, `ClothSprings`, the springs that swing the cloth on it, `ClothWorld` and its Burst jobs, `ClothMath`, and the cloth's data types | none |
| `Mmorpg.Client.Api` | `ApiClient`, HTTP contracts, `ApiError` | none |
| `Mmorpg.Client.Core` | `ClientSession`, `ClientConfig`, `ClientLog`, `SceneNames`, `ClientAddresses`, `ClientAssets`, `RuntimePrimitives` | Api |
| `Mmorpg.Client.Worlds` | World scene catalog, `ClientSceneLoader`, collision loading and authoring | Core |
| `Mmorpg.Client.Networking` | `RealtimeSimulationClient`, `IRealtimeCorpseChannel`, `NetworkMovementSession`, `ClientSessionRecovery` | Api, Core, Worlds |
| `Mmorpg.Client.Items` | Inventory, corpse, and vendor client state, prediction, and rules, the item catalog, presentation catalog loading | Api, Core, Networking |
| `Mmorpg.Client.Gameplay` | Local and remote players, orbit camera, input, click-to-move navigation, pointer focus, game messages, prediction, remote player and Mob interpolation, player bodies and the garments their equipment shows, creature animation, `WorldSceneContext` | Cloth, Api, Core, Worlds, Networking |
| `Mmorpg.Client.WorldActors` | Actor and interaction state, the shared pointer picker, the world menu and its providers, interaction targeting, actor and corpse presentation | Api, Core, Networking, Items, Gameplay |
| `Mmorpg.Client.Combat` | `CombatClientController` and `CombatClientState`, the combat state mirror, combat targeting and approach, the combatant view locator, death and projectile presentation | Core, Networking, Items, Gameplay, WorldActors |
| `Mmorpg.Client.Ui` | Every panel including the combat HUD, drag and drop, `ClientOperationState` | Api, Core, Worlds, Networking, Items, Gameplay, WorldActors, Combat |
| `Mmorpg.Client.App` | `ClientBootstrap`, the composition root | The same as Ui, and Ui |

The client assemblies also reference the shared runtime packages they use:
`Mmorpg.GameProtocol`, `Mmorpg.GameSimulation`, `Mmorpg.WorldData`, and
`Mmorpg.WorldData.Client`, the item presentation catalog. Gameplay references
`Mmorpg.WorldData.Client` so a body reads the garments of the items it wears
without the inventory's assembly. Cloth references the packages `Unity.Burst`,
`Unity.Collections`, and `Unity.Mathematics`; Gameplay references Cloth,
`Unity.Mathematics`, and `Unity.Collections`.

Ui is the top feature area; only the App composition root, which creates the
panels, sits above it. Ui and App reference Combat, because the combat HUD and
the inventory's live arrow count read its state. Each namespace equals its
assembly name. Areas that read `ClientSession` reference Api as well, because
the session caches Api contracts.

The Editor-only `Mmorpg.Client.Editor` assembly references Core, Worlds,
Gameplay, and Cloth, the shared WorldData editor, the WorldData actor editor,
whose compiled World actors the collision bake adds resource nodes from, and the
Addressables editor assemblies. It also holds the import side of the art
pipeline, such as `ArtModelImportRules`, the import rules for every model the art
pipeline exports to `Assets/Art/Models`, and `ArtAppearanceCatalogs`, which
writes the appearance catalogs of the exported bodies; see
[Unity Import Rules](ART_PIPELINE.md#unity-import-rules).

`ClientAssemblyArchitectureTests` pins the exact declared references of every
client assembly, including shared and package assemblies, the layer order, and
the namespace rule. It reads the asmdef files because Unity adds
`UnityEngine.UI` to every compiled assembly. A new dependency is a deliberate
change: add the asmdef reference, keep it pointing down, and update the test.

## Packages And Rendering

`Packages/manifest.json` pins every package to an exact version, and the package
lock records what resolved. The manifest lists only packages the client uses; a
package is added when a feature needs it. The shared code comes in as local file
packages from `src/Shared`: `com.mmorpg.game-protocol`,
`com.mmorpg.game-simulation`, and `com.mmorpg.world-data`.

- The GameProtocol package's `Generated` folder is written by
  `tools/ProtocolGenerator` from the protocol schema, including stable `.meta`
  files, so a protocol change never needs a Unity Editor step; see
  [Schema And Generator](REALTIME_PROTOCOL.md#schema-and-generator).
- GameSimulation, the deterministic movement code that the worker runs too, has
  no Unity dependency.
- LiteNetLib comes from the OpenUPM scoped registry. Unity shows a
  missing-signature warning for it because the package is not signed by Unity;
  the warning is no compile or runtime failure, see
  [LiteNetLib Package Signature Warning](LOCAL_DEVELOPMENT.md#litenetlib-package-signature-warning).
  Every LiteNetLib upgrade still needs a source and changelog review.
- `com.unity.pipeline` is the Editor automation package that the Unity CLI and
  coding agents use to drive a running Editor. No client code references it. Its
  runtime and security rules are in
  [Unity Editor Automation](LOCAL_DEVELOPMENT.md#unity-editor-automation-with-unity-cli).
- The project has no project-wide Input System actions asset; gameplay input
  uses the `PlayerControls` asset referenced by the local player, see
  [Input Actions And Player Prefabs](#input-actions-and-player-prefabs).

The client renders with the Universal Render Pipeline. `PC_RPAsset` and
`Mobile_RPAsset` in `Assets/Settings/Rendering` each own one Universal Renderer
that uses the package default post-processing data. Graphics settings and the
`PC` quality level use `PC_RPAsset` (4x MSAA, HDR, 80 m shadow distance), and the
`Mobile` quality level uses `Mobile_RPAsset` (no MSAA, no HDR, 40 m shadow
distance). Every project material uses a URP shader or one of the project's
shaders built on URP Lit; the runtime fallback presentation sets both
`_BaseColor` and `_Color`, so it stays correct in any pipeline. There is no
post-processing Volume in the scenes and no baked lighting yet.

The URP global settings reference `Assets/DefaultVolumeProfile.asset` as the
default volume profile, with the neutral values URP creates, so a fresh Library
creates no new profile. The shader prefiltering data in `PC_RPAsset` and the
runtime settings list of the global settings are committed as the last Windows
development build wrote them, so the next build has nothing to change.

## Runtime Structure

```text
ClientBootstrap (Mmorpg.Client.App, the composition root, survives scene changes)
  +-- ClientConfig, its Addressables handle held while the bootstrap lives
  +-- ApiClient
  +-- RealtimeSimulationClient
  |   +-- NetworkMovementSession while a session is joined
  +-- InventoryClientController
  |   +-- ClientItemCatalog
  |   +-- InventoryClientState (confirmed and shown state, pending operations)
  |   +-- InventoryOperationPredictor
  |   +-- InventoryItemStateChangePatch
  |   +-- InventoryServerRevisionFollower
  +-- CorpseClientController
  |   +-- CorpseClientState
  +-- WorldActorClientController
  +-- WorldInteractionClientController
  |   +-- GatheringClientState
  +-- CombatClientController
  |   +-- CombatClientState
  +-- WorldPointerFocus and GameMessageLog
  +-- PlayerBodyLookSetting with PlayerLookLink, and PlayerEquipment
  +-- PlayerBodyTintSetting and WorldDebugViewSettings
  +-- account-session validation and scene lifecycle recovery

LoginMenu and CharacterSelect
  +-- LoginMenuPanel or CharacterSelectPanel (code-built uGUI on TemporaryEntryScreen)
      +-- ClientOperationState
      +-- API coroutine flow
      +-- ClientSessionRecovery

World scene
  +-- WorldSceneContext (scene-authored composition root of the scene)
  |   +-- LocalPlayer prefab, RemotePlayer prefab, and PlayerSpawn references
  |   +-- runtime LocalPlayer instance after an accepted join
  |   +-- runtime remote player views, each a root of the World scene
  |   +-- PlayerBodyMeshes, PlayerBodyMaterials, PlayerCorpseBodies
  +-- runtime components that ClientBootstrap adds:
      +-- WorldMenuController
      +-- WorldActorPresentationController and CorpsePresentationController
      +-- WorldInteractionTargetingController
      +-- CombatTargetingController, CombatProjectilePresentation,
      |   CombatDeathPresentation
      +-- MovementDestinationMarker
      +-- temporary uGUI: TemporaryInventoryPanel, TemporaryWorldInteractionPanel,
          TemporaryWorldContextMenu, TemporaryGameMessagePanel, TemporaryCombatHud,
          TemporaryDebugMenu, TemporaryAdminMenu

LocalPlayer prefab
  +-- CharacterController
  +-- CharacterBody
  +-- PlayerInput and LocalPlayerInput
  +-- LocalPlayerController
  +-- PlayerVisual with PlayerBodySlot (PlayerBodyModels, also in the RemotePlayer prefab)
      +-- runtime Body (HumanMaleBody or HumanFemaleBody prefab instance)
          +-- Animator with HumanLocomotion, CharacterLocomotionAnimator
          +-- PlayerBodyAppearance (combined parts, look, tint)
          +-- ClothChainSolver
  +-- CameraTarget
  +-- LocalPlayerCamera
      +-- Camera and AudioListener
      +-- OrbitCameraController

Realtime movement
  +-- NetworkMovementSession
  +-- UnityWorldCollisionLoader and UnityWorldCollisionStream
  +-- baked WorldData chunks
  +-- LocalPlayerNavigator
  |   +-- shared MovementPathPlanner on a background thread
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
  |   +-- WorldPointerPicker, every target under the pointer, nearest first
  |   +-- CombatTargetingController (Combat, first: Attack)
  |   +-- WorldInteractionTargetingController (WorldInteraction: capabilities, Loot)
  |   +-- itself (WalkAndExamine: Walk here, Examine)
  |   +-- TemporaryAdminMenu while it is open (Teleport here)
  +-- TemporaryWorldContextMenu (replaceable runtime uGUI)
  +-- GameMessageLog, owned by ClientBootstrap
  |   +-- TemporaryGameMessagePanel (replaceable runtime uGUI)
  +-- MovementDestinationMarker

Combat (World scene, created at runtime by ClientBootstrap)
  +-- CombatantViewLocator (local player, remote players, world actor views)
  +-- CombatTargetingController
  |   +-- CombatClickRules, CombatTargetSelection, CombatApproachPlanner
  |   +-- CombatLoadoutRules (weapon range from the bundled item catalog)
  +-- CombatProjectilePresentation (replaceable arrows and bolts)
  +-- CombatDeathPresentation
  +-- TemporaryCombatHud (replaceable runtime uGUI)
      +-- CombatHudPresentation (pure labels and values)
```

## Persistent Client Bootstrap

`ClientBootstrap` creates itself after the first scene loads, through
`RuntimeInitializeOnLoadMethod` with `AfterSceneLoad`, on an object that
survives every scene change. It loads the configuration, owns one
`RealtimeSimulationClient`, one `InventoryClientController`, and the corpse,
world actor, world interaction, and combat controllers, observes scene
transitions, and performs the fallback leave when an active World scene is left
outside the normal panel flow. A configuration without its runtime primitive
material, debug line material, or arrow model logs a warning, because markers
and fallback models then draw with the error shader in a player build, the Debug
menu draws no lines, and arrows fly as plain bars.

`ClientBootstrap` is the only composition root, and no runtime assembly
references it. It creates the persistent services and passes them explicitly to
every component that needs them; there is no static service access. Scene
controllers that it creates at runtime are added to an inactive GameObject, given
their dependencies through `Initialize`, and then activated, so `Awake` and
`OnEnable` already see them. Tests construct the same components with their own
dependencies.

When a scene loads, the bootstrap composes it. LoginMenu gets `LoginMenuPanel`
and CharacterSelect gets `CharacterSelectPanel`. In a World scene, the
scene-authored `WorldSceneContext` receives the realtime client, the pointer
focus, and the body settings before `Start` runs, and the bootstrap adds the
runtime components listed in [Runtime Structure](#runtime-structure). A
component the scene already has is kept.

The fallback release is bound to the exact simulation-session identity.
Completion of an older request cannot clear a newer local reconnect state. If
graceful leave fails after the scene already changed, the client closes UDP so
SimulationWorker can run disconnect cleanup.

## Configuration

`ClientConfig` is a ScriptableObject at
`Assets/Content/Config/ClientConfig.asset`, defined in `Assets/Scripts/Core`. The
bootstrap loads it through Addressables at startup, synchronously, and keeps its
handle until the bootstrap is destroyed, which also keeps the referenced gameplay
catalog loaded. A missing configuration logs a warning and uses local fallback
endpoints.

| Field | Checked-in value | Used for |
| --- | --- | --- |
| GameService base URL | `http://localhost:5000` | Every HTTP request |
| Request timeout | 10 s | Every HTTP request |
| Realtime timeout | 10 s | Realtime join and leave deadlines and the UDP disconnect timeout |
| Session validation interval | 5 s, clamped to 2 to 60 s | The periodic account-session check |
| Item gameplay catalog | `core.item-catalog.json` of the local WorldData package | `ClientItemCatalog` |
| Runtime primitive material | `Assets/Art/Materials/M_RuntimePrimitive.mat`, URP Lit | Primitives that runtime presentation creates |
| Debug line material | `Assets/Art/Materials/M_DebugLines.mat`, URP Unlit | The Debug menu's collision and path lines |
| Arrow model | `Assets/Art/Models/Projectiles/BronzeArrow.fbx` | Arrows in flight and on an archer's string |

The runtime primitive material references put the material and its shader into
the Client Content bundle. The bootstrap passes the material to the components
that create primitives, which create them through `RuntimePrimitives.Create`. A
player build has no default material for URP, so a bare
`GameObject.CreatePrimitive` falls back to the built-in Standard shader, which
URP draws with its pink error shader; only the Editor hides that.
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

This state is a client cache, not an authority. The persistent bootstrap
validates an authenticated account session at the configured interval, also
outside the active World scene. HTTP 401 clears all local session state before
loading LoginMenu.

## API Layer

`ApiClient` wraps UnityWebRequest in coroutines. It is responsible for:

- Request serialization and response deserialization.
- Bearer authentication headers.
- Configured request timeouts.
- Validation of expected response payloads.
- Structured mapping of HTTP, timeout, network, and invalid-response failures.
- Owned-character complete item-state, focused Bank, and focused Recovery
  Storage reads used by the persistent inventory controller.
- Tracking every request in flight. Unity stops a coroutine without disposing its
  iterator, so each owner disposes its client in `OnDestroy`, which aborts and
  releases any request that has not completed.

`ApiError` carries the failure kind, HTTP status, stable server code, message,
and correlation id. Panels display a safe message rather than raw JSON. Array
responses are validated through `JsonArrayUtility` before they reach UI state.

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

`RealtimeSimulationClient` is a persistent MonoBehaviour owned by the bootstrap.
It wraps one LiteNetLib `NetManager` and polls network events on Unity's main
thread. It owns an explicit connection state:

```text
Disconnected -> Connecting -> Joining -> Joined -> Leaving -> Disconnected
```

Join and leave are coroutine operations with configured deadlines, structured
errors, and one completion path. A join consumes the complete `JoinShardResponse`,
checks ticket expiry, protocol version, simulation revision, shard identity,
World identity, and exact worker runtime placement, then connects to the provided
UDP endpoint. It sends the short-lived join ticket in a versioned reliable ordered
packet. Join acceptance must match the placed character, shard, World, simulation
revision, and collision revision before non-secret metadata is copied into
`ClientSession`.

Unexpected disconnect clears the active shard view and returns an authenticated
player to CharacterSelect. `RealtimeSimulationClient` also raises `SessionEnded`
once whenever a joined session ends for any reason, including a graceful leave,
`Abort`, or a failure while leaving. The inventory, corpse, world actor, world
interaction, and combat controllers reset their session-scoped state on that
event, so a pending operation, open view, or corpse presence sequence never
blocks the next join. `SessionJoined` is raised when the worker accepts a join,
before any later message of that session is handled; the combat controller clears
its state there as well, because combat revisions start again in every session.
The accepted join also carries the Shard rules, which the client exposes as
`ShardRules` while the session is joined, such as whether player versus player
attacks are allowed. Join rejection stays in CharacterSelect and displays the
structured server code. Protocol decoding rejects wrong versions, invalid types,
oversized values, and trailing data.

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
  packets with receive channel 0, even when a channel number was given to the
  send overload. Server tick, snapshot sequence, message type, and remote chunk
  metadata provide application-level ordering without confusing snapshots with
  reliable control messages. The client recognizes them by the unreliable
  delivery in their generated message descriptor, see
  [Message Descriptors](REALTIME_PROTOCOL.md#message-descriptors), while reliable
  control messages still validate channel 0 explicitly.
- The owner snapshot is the only source for local input acknowledgement,
  prediction reconciliation, and collision-streaming anchors. It carries the
  worker's state after the acknowledged input, and the worker steps once per
  input like the prediction, so a replay of the later inputs lands where the
  prediction stands.
- `RealtimeSimulationClient.TrySendMovementInputs` wakes LiteNetLib's update
  thread after each batch, so the input leaves at once instead of on the
  transport's next 15 ms update. Remote chunks update admitted remote entities
  and may arrive at a lower fair cadence under dense worker-wide byte pressure.
- Correctly delivered snapshots that overtake join acceptance or remain in flight
  during and immediately after leave are ignored outside the `Joined` state. They
  are not protocol failures because LiteNetLib delivery methods do not provide
  ordering relative to each other.
- While a leave is pending, SimulationWorker keeps sending reliable presence,
  world actor, interaction, corpse, gathering, combat, and item-operation
  messages until it has released the session. The client ignores these in-flight
  messages in the `Leaving` state, so a graceful leave never turns into a
  protocol disconnect.
- After the session ended in one poll, later packets in the same poll are
  ignored, so the first failure reason is the one shown to the player.

Every accepted simulation session identifies the local player's server-assigned
nonzero network entity id. `RealtimeSimulationClient` owns an in-memory registry
of the reliable spawn baseline and subsequent spawn or despawn changes. The
registry survives the transition from CharacterSelect to the selected World
scene, so entities that spawned before scene loading are still presented.
Conflicting reuse of an active entity id is treated as a protocol failure.
Snapshots never create or remove entities. They update only ids already admitted
by the reliable lifecycle.

`NetworkMovementSession` is created only from a validated join response. It owns
the server-provided movement settings, initial state, and admission-fenced carry
tuple used by the client. The tuple contains the monotonic item-state revision,
unitless carried weight, and capacity. Reliable carry-state updates advance the
tuple only when their revision is newer. The client does not maintain a second
editable copy of movement speed, tick rate, gravity, bounds, snapshot frequency,
weight, or capacity for an active network session. The join response must also
match the client's compiled movement-simulation revision and baked collision
revision. Either mismatch aborts activation with a structured client error.

Client and worker must run the same protocol and simulation code, as
[Versioning](REALTIME_PROTOCOL.md#versioning) describes; what to restart and
rebuild after such a change is in [Unity Client](LOCAL_DEVELOPMENT.md#unity-client).

## Content Loading With Addressables

The client uses Addressables for all runtime content; there is no `Resources`
folder and no `Resources.Load` call. Every content folder that runtime code loads
by address is one Addressables folder entry whose address is its path below
`Assets/Content`, so an asset's address is its path below that root including the
file extension, and a new file in such a folder needs no settings change.
`Assets/Content/Player` is no entry: the player prefabs reference its body models
asset directly. `ClientAddresses` lists every fixed address:

| Group | Entries | Address | Packing |
|---|---|---|---|
| Client Content | `Assets/Content/Config`, `Worlds`, `Items/Presentation`, `WorldActors` | `Config/ClientConfig.asset`, `Worlds/world-scene-catalog.json`, `Items/Presentation/item-presentation-catalog.json`, `WorldActors/WorldActorPresentationRegistry.asset` | Together |
| Item Icons | `Assets/Content/Items/Icons` | `Items/Icons/<file>` | Together |
| World Actor Prefabs | `Assets/Prefabs/WorldActors` | `WorldActorPrefabs/<file>`, loaded by GUID through the registry | Together |
| World Collision | `Packages/com.mmorpg.world-data/Generated/WorldCollision` | `WorldCollision/<WorldId>/manifest.json` and `<chunk>.bytes` | Together |
| World Scenes | One entry per catalog World scene | The scene name | Separately, one bundle per scene |

`ClientAssets` in Core loads small content synchronously: configuration,
catalogs, icons, and collision chunks. It locates an address first, so a missing
address becomes a structured error instead of an Addressables exception, and the
caller releases every handle it owns. Scenes and actor prefabs load
asynchronously where they are used.

All groups build locally into the player. A normal player build builds the
Addressables content first. The build content state is written to
`Library/com.unity.addressables/ContentState`, because the client ships no remote
content updates. In the Editor, the default play mode script is "Use Asset
Database", so Play Mode and the tests need no content build, and pulling a change
to the committed settings in `Assets/AddressableAssetsData` needs no Editor step.

`ClientAddressablesLayout` in the Editor assembly defines the groups, folder
entries, World scene entries, and the Build Profiles scene list in code.
`MMORPG > Tools > Addressables > Apply Client Layout` reapplies it, and
`ClientAddressablesTests` fails on any difference, on a runtime address without
an entry, and on any `Resources` folder or call in the client and shared
packages.

### Working Rules For New Content

- Put new client content into a content folder below `Assets/Content`, or a World
  actor prefab below `Assets/Prefabs/WorldActors`. Those folders are folder
  entries, so a new file is addressable at once. A new folder that runtime code
  loads by address needs its entry in `ClientAddressablesLayout` and a fixed
  address in `ClientAddresses`.
- Map a new World scene in `Assets/Content/Worlds/world-scene-catalog.json` and
  register it with `MMORPG > Tools > Addressables > Apply Client Layout`.
  Expected result: the Console reports `[ADDRESSABLES] The client layout is
  applied and valid.`, the scene is an entry in the `World Scenes` group with its
  scene name as address, and Build Profiles still list only LoginMenu and
  CharacterSelect.
- To build the content alone, open `Window > Asset Management > Addressables >
  Groups` and select `Build > New Build > Default Build Script`. The first build
  in a new Library asks whether to turn on `Debug Build Layout`; either answer is
  fine. Expected result: the build finishes without errors, and its output and
  content state stay in the ignored `Library` folder.
- To exercise built bundles in the Editor, select `Play Mode Script > Use
  Existing Build` in the Groups window after a content build, and switch back to
  `Use Asset Database` afterwards. Expected result: Play Mode loads the World
  scenes from the built bundles. The choice is stored per user in `Library`.

The manual check of the project and its content loading is in
[Client Project And Content Loading](MANUAL_TESTING.md#client-project-and-content-loading).

## Scene And UI Flow

The build flow is:

```text
LoginMenu -> CharacterSelect -> WorldSceneCatalog[accepted WorldId]
     ^              ^                         |
     +--------------+-------------------------+
```

`Assets/Content/Worlds/world-scene-catalog.json` is the client-owned mapping from
authoritative `WorldId` to an authored Unity scene name. GameService and
SimulationWorker never send a scene path. CharacterSelect resolves the selected
shard's World before requesting a join ticket and rejects an unknown mapping or a
scene that is not an Addressables World scene. It resolves the authoritative
placement World again before opening UDP, so a shard list cached across an
offline rebind cannot load the previous scene. The checked-in catalog maps both
development Worlds to their scenes; see
[Checked-In World Registry](DEVELOPMENT_WORLDS.md#checked-in-world-registry).
World scenes are Addressables entries addressed by scene name, and Build Profiles
contain only LoginMenu and CharacterSelect.

Every scene change goes through `ClientSceneLoader`. LoginMenu and
CharacterSelect load as built-in scenes. After an accepted join, CharacterSelect
loads the World scene asynchronously and stays visible, with its controls
disabled, until the World scene replaces it. If the load fails, the panel shows
the error and leaves the joined simulation session gracefully. Addressables
releases the World scene's handle and bundles when the scene unloads, so a return
to a built-in scene needs no explicit release.

### Account Entry Screens

LoginMenu and CharacterSelect are built in code on `TemporaryEntryScreen`, a
full-screen canvas at sorting order 10 with a dark backdrop and cards on it. It
holds the keyboard rules of a form: `Tab` and `Shift+Tab` move between the
visible text fields in the order they were added, and `Enter` runs the screen's
main action. It clears the EventSystem selection whenever a button holds it, so
the UI module's `Submit` can never click a button a second time. Each screen
disables its controls through a `CanvasGroup` while its `ClientOperationState`
runs an operation. The texts, such as `Human_Male` read as `Human Male`, an
error with its code, and why Enter World cannot run, come from
`AccountEntryPresentation`, which EditMode tests cover without a scene.

`AccountEntryPreferences` keeps, in `PlayerPrefs`, the login name of the last
successful login and the character and shard of the last accepted join, never a
password or a token. A remembered character id of another account matches none
of the listed characters, so one entry serves every account.

### LoginMenu

`LoginMenuPanel` shows a card with a `Log In` and a `Create Account` page, runs
registration or login, and loads CharacterSelect after successful
authentication. `Log In` sends the login field as the `login` of
`POST /api/accounts/login`, which GameService matches against the email and the
username. Only the Editor and development builds, `Debug.isDebugBuild`, fill in
the local development account. A failed request puts the caret back in the
password field once the form is enabled again.

### CharacterSelect

`CharacterSelectPanel` loads the characters, the shards, and what creation
offers sequentially into `CharacterSelectState`, and draws the state again on its
`Changed` event; the widgets are in `CharacterSelectPanel.Layout.cs`. The state
keeps the chosen character and shard by id across a refresh, lets only an online
shard be chosen, and keeps creation open while the account has no character. A
list is built again only when its version changes, so a selection only recolors
rows, and a double click, read by `TemporaryDoubleClick` from the pointer's click
count, lands on the row it started on. The panel copies the chosen character and
shard to `ClientSession`, which the inventory controller observes. Creation uses
the appearance offers of [Character Looks](#character-looks). Join ticket
creation and the UDP handshake run inside one coroutine so the operation cannot
be partially overlapped by another click.

### World Scenes

A World scene holds the authored composition that
[World Scene Composition](DEVELOPMENT_WORLDS.md#world-scene-composition)
describes; everything else comes from `ClientBootstrap` at runtime. Every panel
of a World scene is runtime uGUI, so `WorldPointerFocus` can tell from the
EventSystem whether the pointer is over interface. No scene authors an
EventSystem: `TemporaryUiKit.EnsureEventSystem` makes one for the first panel
that needs it, with the Input System UI module, and keeps it across scene loads.
The canvases sort from the bottom up:

| Sorting order | Canvas |
| ---: | --- |
| 20 | `TemporaryCombatHud` |
| 35 | `TemporaryGameMessagePanel` |
| 40 | `TemporaryWorldInteractionPanel` |
| 100 | `TemporaryInventoryPanel` |
| 120 | `TemporaryDebugMenu` and `TemporaryAdminMenu` |
| 140 | `TemporaryWorldContextMenu` |
| 150 | The death overlay, a nested canvas of the combat HUD |

The Debug and Admin menus are built in code on `TemporaryMenuWindow`: a window
with a header that names its key and has a close button, a row of tabs, one
scrolling page per tab, and a footer with a status line. Pages are built once and
keep the `Text`, `Toggle`, and `Button` components that change, so a refresh sets
text and lays nothing out, and a closed menu draws nothing. Both start closed.
What they show is in [Debug Menu (F2)](GAME_FEATURES.md#debug-menu-f2) and
[Admin Menu (F3)](GAME_FEATURES.md#admin-menu-f3).

`TemporaryDebugMenu`, toggled by `ToggleDebugMenu`, refreshes four times a second
from client state only. Its Leave Shard action waits for an exact-session UDP
leave acknowledgement before loading CharacterSelect, and it owns the World
scene's session check, which sends a player build without a joined session back
to CharacterSelect. `RealtimeSimulationClient` counts application-level realtime
packets and bytes, owner snapshot sequences, remote snapshot chunks, and
estimated missing owner sequences, and `WorldDebugTelemetryTracker` derives
rolling frame percentiles and half-second network rates from those counters. The
menu labels payload rates as application payload rather than total socket
bandwidth. Its `Show in the world` settings live in `WorldDebugViewSettings`,
which `ClientBootstrap` owns, so they last across World scenes and are off
whenever the client starts.

`CollisionDebugOverlay` draws, while the Debug menu is open and its toggles are
on, the baked collision around the local player and the rest of its route.
`CollisionDebugLines` queries the movement session's `ChunkedStaticCollisionWorld`
in a square 48 m wide and turns every shape into line segments by kind: triangle
edges, lifted 3 cm along their normal, are walkable ground or too steep by the
character's maximum slope, and the twelve edges of a box are an obstacle, a
movement-only box such as a resource node, or a sight-only box by its layers. The
overlay builds the lines again when the player moved 2 m or the loaded chunks
changed, and draws one line mesh per kind every frame with `Graphics.RenderMesh`
and the configured debug line material, whose `_BaseColor` it sets per kind.
About 4,500 terrain triangles of the Development World 2 highlands draw in the
Editor at well over 100 FPS.

`TemporaryAdminMenu`, toggled by `ToggleAdminMenu`, holds the development admin
tools in three tabs, Look, Movement, and Combat. They are available while
`RealtimeSimulationClient.AdminState` is set, which a worker that allows the tools
sends after every join; otherwise the tabs are greyed out and the footer says
why. Look steps the stored body model and look through `PlayerBodyLookSetting`;
Movement sends `SetMovementSpeed` and `Teleport`; Combat sends `RestoreHealth`
and `SetInvulnerable`. The menu is a world menu provider: while it is open, the
right-click menu offers `Teleport here` after `Walk here`, and after `Pick a
spot` that option comes first, so a left click on the ground teleports and
Escape cancels. The footer shows the worker's answer to each request. What the
worker does with them is in
[Development Admin Tools](SERVICE_FEATURES.md#development-admin-tools).

The gameplay protocol does not carry SimulationWorker process diagnostics. Worker
CPU, memory, population, capacity, and internal timing stay in the worker metrics
and logs instead of being disclosed to every connected game client.

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

Logout is different from recovery. A normal `Log Out` action first asks
GameService to revoke the active account session, then clears local state.

## Local Player Lifecycle

`WorldSceneContext` is a scene composition root. It validates serialized scene
and prefab references plus the active session's exact World-to-scene mapping,
then creates one LocalPlayer prefab instance only when a catalog World scene
opens with an accepted realtime join and movement session. It connects that
runtime player to server-authoritative movement, consumes the cached reliable
entity baseline, routes entity snapshots, and connects the player-owned camera
to input and CameraTarget. Opening a World scene without an active joined session
creates no local player and reports no missing reference. The context never
selects a global camera and never generates a player asset, map object, material,
light, or collider.

Runtime instances of the authored RemotePlayer prefab are created only from
reliable player-entity spawn messages, each a root of the World scene, so Unity's
transform jobs, such as the cloth's, take the players' bones on their worker
threads side by side; under one parent they would take them one after another.
The context destroys them when a player leaves interest and when the context
goes, and the runtime local player, apart from them, is destroyed with the active
World scene.

`LocalPlayerInput` reads the `PlayerInput` instance owned by the LocalPlayer
prefab, whose input actions asset is described in
[Input Actions And Player Prefabs](#input-actions-and-player-prefabs), at
execution order -100, before anything reads its input. This keeps device
bindings out of gameplay and UI code and allows future rebinding and
control-scheme work without changing those systems. The cursor is never locked.

`ClientBootstrap` owns one `WorldPointerFocus` and passes it to the World scene,
the local player, and the camera. It reports whether the pointer is over
interface through the EventSystem, since every World scene panel is uGUI. A click
or scroll over a panel therefore never reaches the world.

`CharacterBody` is the reusable collision foundation for local players, remote
players, and NPCs. The character root is its logical ground point. CharacterBody
keeps skin width proportional to radius and offsets the CharacterController
center so the lower edge of its contact envelope is local Y zero. Presentation
objects are not moved to compensate for physics margins. Teleportation places the
root directly at the requested ground position. A future network or AI movement
driver can therefore reuse the same body without depending on local input.

Authenticated movement keeps a separate collision pose and rendered pose on
walkable slopes. The shared motor stores the slope-aware capsule support height
needed to avoid penetration and downhill drift. `GroundedMovementPresentation`
queries the same baked collision world and removes the geometric slope support
offset from local and remote render poses without replacing their interpolated
height. Its `GroundedVerticalPresentation` applies a bounded 100 ms blend to small
height changes on flat walkable support, covering steps and short grounded drops.
Ramps, airborne movement, teleports, and large corrections bypass this extra
blend. Presentation never feeds back into prediction, reconciliation, input
packets, or SimulationWorker state.

`LocalPlayerController` moves the player only in an authenticated World-scene
session; the prefab has no offline movement mode. It reads no clicks itself: the
[World Menu](#world-menu) does, and its Walk here casts the pointer ray through
`TryFindGroundPoint` against the same baked collision world that prediction uses,
so world actors and their trigger bounds never stop it, and the hit point becomes
the destination.

`LocalPlayerNavigator` plans the route with the shared `MovementPathPlanner` on a
background thread. The collision world is lock-protected, and only one plan runs
at a time, so a long search never stalls a frame while chunk streaming continues
on the main thread. Every walk, approach, or stop is a new intent with an id; only
the newest intent's plan is applied, and the previous path continues until it is
ready. The navigator gives every applied plan a new path id. A plan that stopped
at the 32 waypoint limit continues under the same intent once its last waypoint
is reached. An approach trims the route where it first comes within range of the
target. Only a start already within range counts as arrival and stops the player;
an approach without a route reports no route, and the previous path continues.
Normal movement faces its travel direction, and an airborne player keeps its
takeoff momentum and facing until it lands. `RefreshCharacterDimensions` remains
the runtime entry point when a future character system changes collider
dimensions.

Authenticated prediction also consumes the exact carry tuple owned by
`NetworkMovementSession`, so the shared simulation applies the encumbrance curve
of [Carry Weight And Encumbrance](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carry-weight-and-encumbrance),
including an involuntary death overflow above the hard cap. A reliable newer
revision is applied before subsequent prediction and reconciliation replay. The
Debug menu's Movement tab shows weight, capacity, item-state revision, and
movement percentage for observation without becoming inventory state or
authority.

A development admin walk speed reaches the client as `AdminStateChanged`.
`NetworkMovementSession.ApplyWalkSpeedPercent` derives `PredictionSettings`, the
joined settings with `MovementSimulationSettings.WithWalkSpeedPercent`, the same
shared method the worker steps with, and `LocalPlayerController` hands them to
`ClientMovementPrediction.ApplySettings` for the next step and every replay. The
change is not tick-stamped, as a carry revision is not: the worker walks faster
from its next tick, and reconciliation absorbs the few inputs between.

### Movement Prediction And Collision

For network movement, input is sampled at the server-provided tick rate and each
command receives an input sequence, a client tick, and the id of the path the
player follows, or 0 while it stands. The local state is predicted immediately
and up to four newest unacknowledged commands are sent in each batch, together
with the path of the newest command until an owner snapshot reports that path id.
On an authoritative owner snapshot, `ClientMovementPrediction` removes
acknowledged commands, starts from the server state including its path progress,
and replays the remaining commands. Client and worker follow the same waypoints,
so a replay reaches the same result. Prediction and reconciliation replay share
one reusable collision-query workspace, avoiding per-step broadphase collection
allocation without changing the deterministic movement result.

`LocalMovementPresentation` interpolates consecutive predicted states at the
render frame rate, while `LocalPlayerController` smooths reconciliation
corrections smaller than three meters and applies larger corrections immediately.
The presentation interpolation never feeds positions back into input packets or
the shared simulation. The shared simulation is installed as
`com.mmorpg.game-simulation` and contains no Unity dependencies.

`UnityWorldCollisionLoader` loads the selected World's manifest through
Addressables at `WorldCollision/<WorldId>/manifest.json`, an address that mirrors
the `Generated/WorldCollision` folder of the local `com.mmorpg.world-data`
package. `UnityWorldCollisionStream` then loads synchronously and
checksum-validates chunks at `WorldCollision/<WorldId>/<chunk>.bytes` around the
initial player and the authoritative local owner snapshot. Remote snapshot
scheduling cannot move the prediction collision window, and neither remote
players nor Mobs load chunks: beyond the local player's chunks they show their
replicated height. The stream retains a larger chunk ring before unloading, which
prevents boundary churn. The join response carries SimulationWorker's collision
revision, and `NetworkMovementSession` does not start when the local revision
differs. A missing, corrupt, or coordinate-mismatched streamed chunk closes the
active session through the normal structured client failure path.

`ClientMovementPrediction`, reconciliation, grounded presentation, and remote
presentation all query the same mutable `ChunkedStaticCollisionWorld`. Loading
changes which authored chunks are resident but never changes SimulationWorker
authority or feeds presentation positions back into prediction.

The network simulation collides with every baked box and one-sided triangle of
the selected World, including rotated ramps, steps, cover, boundaries, terrain,
and tunnel meshes. The capsule dimensions, slope limit, step height, ground snap
distance, and substep budget come from the validated server join settings.
Movement never uses Unity CharacterController or PhysX to decide network
position.

`WorldCollisionAuthoring` defines the World id, chunk size, collision root, and
layer mask on an authored scene object, and `WorldCollisionBaker` bakes the
enabled, non-trigger BoxColliders, non-convex MeshColliders, and
TerrainColliders below that root into neutral authoring JSON plus versioned
binary chunks in WorldData. Mesh vertices are baked in world space and terrain
heights as a heightfield, both as exact base64 floats. The shared compiler
canonicalizes signed floating-point zero before encoding, so Unity and .NET
produce identical chunk hashes for equivalent transforms. Runtime code never
scans the Unity scene or treats PhysX as network authority. The bake workflow and
the authoring rules are in
[Rebuild And Bake Workflow](DEVELOPMENT_WORLDS.md#rebuild-and-bake-workflow) and
[Terrain And Mesh Collision Authoring](DEVELOPMENT_WORLDS.md#terrain-and-mesh-collision-authoring).

### Remote Players

`RemotePlayerView` is presentation-only. It has no input, camera, audio listener,
rigidbody, or solid collider; the prefab has no collider at all. At runtime it
adds one trigger capsule of the movement capsule's size, so the pointer can pick
the player for combat, and it reports that height for health bars. A trigger
never blocks movement, the camera, or the pointer. It stores the network entity
id, persistent character id, display name, and presentation archetype supplied by
the spawn message. `WorldSceneContext` presents the archetypes `player.default`
and `player.development_bot`. The prefab shows the human body described in
[Player Bodies](#player-bodies); with the body tint setting on, the view tints it
for a person or a development bot.

Its `RemoteMovementPresenter`, which moving Mobs share, buffers server states in
`RemoteMovementInterpolation`, advances an adaptive monotonic frame-rate
`RemoteRenderClock` behind the server, and applies the grounded presentation
described above. `RemoteMovementPresenter.InterpolationDelayTicks` sets the
render delay to two snapshot intervals and at least 100 ms: four ticks, about
133 ms, at 30 ticks and 15 snapshots per second. One interval covers the wait for
the next snapshot and the second a snapshot that arrives up to one interval late.

The clock measures that delay against an estimate of the server tick that
advances with real time between snapshots. The estimate rises with any snapshot
that arrives earlier than it expects and moves 5 percent of the way toward a
later one, or all the way when it arrives later than the whole render delay,
because the latency changed. Measured against the newest received tick instead,
the delay would shrink between snapshots and grow at each one, and remote
characters would speed up and slow down by about 30 percent at the snapshot rate.
The clock uses bounded catch-up and slow-down corrections during normal delivery.
After a larger network stall it restores the intended buffer delay instead of
retaining permanent extra latency.

A reliable despawn removes the matching view immediately. Missing unreliable
snapshots never decide entity lifetime. `WorldSceneContext` routes the snapshot
entities of remote players; Mob entities go to the world actor presentation, as
described in [Mob Movement](#mob-movement), and every other id is ignored.

### Orbit Camera

`OrbitCameraController` lives with the Camera on the LocalPlayerCamera child
owned by the LocalPlayer prefab and orbits the single CameraTarget pivot. Dragging
with the middle mouse button turns and tilts it, the arrow keys turn and tilt it,
and the scroll wheel changes the zoom distance by a fraction per notch, toward
which the applied distance eases. Its serialized values are in
[Input Actions And Player Prefabs](#input-actions-and-player-prefabs). From the
pivot at 1.45 m, the 0.6 m minimum distance keeps the camera about 30 cm from
the body at every tilt, which an EditMode test checks against the baked meshes of
every body model, so it never clips into the head. The controller sets the
Camera's field of view at runtime.

A non-allocating spherecast from the pivot pulls the camera in front of
obstructions at once and eases it back out, and it filters the local player
hierarchy. Scrolling over interface does not zoom. The cursor is never locked or
hidden.

### Presentation Order

`PresentationExecutionOrder` fixes the late presentation order of a frame.
Characters move in `Update`. Then, in this order:

| Order | Constant | What runs |
| ---: | --- | --- |
| 50 | `Locomotion` | `CharacterLocomotionAnimator` reads how far its body moved, after every character moved and before the Animator evaluates |
| 60 | `Projectiles` | `CombatProjectilePresentation` places arrows and bolts in `LateUpdate`, after the bodies posed their hands and drew their strings |
| 100 | `Camera` | `OrbitCameraController` follows the player |
| 110 | `Cloth` | `ClothChainScheduler` lays the cloth of the bodies the camera now sees |
| 200 | `ScreenAnchoredInterface` | `TemporaryCombatHud` places health bars and damage numbers |

Without a fixed order Unity could place screen-anchored interface with the
previous frame's camera, and a bar would trail the head it belongs to and jitter
with the frame time.

`MovementDestinationMarker` is replaceable presentation that the bootstrap adds
to every World scene. It shows a flat, collider-free disc at the destination of
the path the predicted player still follows.

### Input Actions And Player Prefabs

The input actions asset and both player prefabs are committed and maintained in
place; they are never created again from scratch. Change them in the Editor,
keep this reference in step, and keep `AuthoredGameplayAssetTests` and
`PlayerBodyAssetTests`, which pin them, passing.

`Assets/Input/PlayerControls.inputactions` has one action map, `Player`, and no
control schemes:

| Action | Type | Binding |
| --- | --- | --- |
| `Select` | Button | `<Mouse>/leftButton` |
| `ContextMenu` | Button | `<Mouse>/rightButton` |
| `PointerPosition` | Value, Vector2 | `<Pointer>/position` |
| `CameraOrbit` | Button | `<Mouse>/middleButton` |
| `CameraOrbitDelta` | Value, Vector2 | `<Mouse>/delta` |
| `CameraRotate` | Value, Vector2 | A 2D Vector composite of the four arrow keys |
| `CameraZoom` | Value, Axis | `<Mouse>/scroll/y` |
| `Cancel` | Button | `<Keyboard>/escape` |
| `ToggleDebugMenu` | Button | `<Keyboard>/f2` |
| `ToggleAdminMenu` | Button | `<Keyboard>/f3` |
| `ToggleInventory` | Button | `<Keyboard>/i` |
| `ToggleCharacterInventory` | Button | `<Keyboard>/b` |
| `ToggleEquipmentInventory` | Button | `<Keyboard>/c` |

`Assets/Prefabs/Player/LocalPlayer.prefab`:

- The root `LocalPlayer`, at the origin with scale 1, is the logical ground
  point. It has `CharacterController` with radius `0.35`, height `2`, slope
  limit `45`, step offset `0.35`, skin width `0.035`, and minimum move distance
  `0`; `CharacterBody` with a controller skin width ratio of `0.1`, which keeps
  the controller center at `(0, 1.035, 0)` so the contact envelope's lower edge
  lies at local Y zero; `PlayerInput` with the actions `PlayerControls`, the
  default map `Player`, the behavior `Invoke C Sharp Events`, and the camera of
  `LocalPlayerCamera`; `LocalPlayerInput`; and `LocalPlayerController`, whose
  Camera Target is `CameraTarget` and whose Player Camera is the orbit controller
  of `LocalPlayerCamera`. `CharacterController` is the only collider on the
  player.
- `CameraTarget`, at local `(0, 1.45, 0)`, is the orbit pivot.
- `PlayerVisual`, at local Y zero, has `PlayerBodySlot` with
  `Assets/Content/Player/PlayerBodyModels.asset`; the body goes in at runtime.
  Every character model keeps its foot plane at local Y zero below
  `PlayerVisual`.
- `LocalPlayerCamera`, tagged `MainCamera`, has the prefab's only `Camera`, with
  near clip plane `0.05` and far clip plane `500`, its only `AudioListener`, the
  URP camera data, and `OrbitCameraController`:

| Orbit field | Value |
| --- | --- |
| Target | `CameraTarget` |
| Minimum, maximum, and initial distance | `0.6`, `15`, and `12` m |
| Minimum, maximum, and initial pitch | `10`, `80`, and `40` degrees |
| Drag degrees per pixel | `0.25` |
| Key yaw and key pitch degrees per second | `120` and `60` |
| Zoom step fraction | `0.15` |
| Zoom sharpness and obstruction release sharpness | `12` and `6` |
| Field of view | `55` |
| Collision radius, padding, and layers | `0.25`, `0.1`, and every layer |

`Assets/Prefabs/Player/RemotePlayer.prefab` has `RemotePlayerView` on its root
and `PlayerVisual`, at local Y zero, with `PlayerBodySlot` and the same body
models asset. It has no collider, rigidbody, `CharacterController`,
`PlayerInput`, camera, or audio listener; `RemotePlayerView` adds its pointer
trigger at runtime. Neither prefab holds a body of its own.

## Inventory Client

The persistent `InventoryClientController` owns the client's view of the
character's items, Bank, and Recovery Storage, predicts item operations, and
sends them through the realtime client. The item rules it mirrors are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), and what
the player sees is in
[Player Inventory Foundation](GAME_FEATURES.md#player-inventory-foundation).

### Item Operations And Catalog

`RealtimeSimulationClient.TrySendItemOperation` accepts only a typed protocol
intent while joined and sends it on the reliable ordered control path. Committed
or rejected results are decoded only in the joined state. A committed newer carry
revision advances `NetworkMovementSession` before `ItemOperationCompleted` is
raised. The transport does not own item collections. The persistent
`InventoryClientController` consumes the result and correlates its operation id
and kind. A duplicate completion for the last finalized operation is ignored
safely.

`ClientItemCatalog` compiles and revision-checks the bundled gameplay TextAsset
through the shared `ItemCatalogJsonDocument` shape that the Item Catalog editor
also uses, loads the presentation catalog once through Addressables and caches it
per gameplay revision, and indexes definitions and equipment slots by stable id.
Gameplay and presentation source revisions must match before item UI can render.
Every HTTP snapshot must also match the bundled gameplay revision. A mismatch
clears renderable item state and produces `item_catalog_update_required` instead
of accepting stale definitions. Icons, localization keys, fallback labels, and
optional prefab presentation keys resolve locally. An icon is a Sprite whose
Addressables address is stored in the catalog's `iconAddress` field, below
`Items/Icons/`; the client loads each icon once and keeps it for the process
lifetime. HTTP responses carry only stable definition ids and instance state.

Durable item-instance revisions are non-negative, and a newly granted or split
item begins at revision `0`. Unity `JsonUtility` can materialize an exact
all-default object for a JSON `null` in an optional item or equipped-Bag field.
The snapshot mapper normalizes only that exact optional placeholder back to no
item. Required Recovery delivery items and every partially populated malformed
item still fail validation.

The complete snapshot from `GET /api/characters/{characterId}/item-state` also
carries the character's `currency` balance. The mapper rejects a balance below 0
or above `VendorPricing.MaximumCurrencyBalance`. Every balance change while the
character is online is an item transaction, so the balance belongs to the
item-state revision and takes part in the same-revision coherence check. The
client never changes the confirmed balance locally.

### Snapshots And Revisions

`InventoryClientState` owns immutable complete and focused snapshots plus the
latest observed character, Bank, and Recovery revisions. A focused response may
advance its slice, but any newer focused revision marks the complete snapshot
incoherent and disables mutation until a full refresh reaches the newest known
revision. Same-revision content disagreement is an error. Older responses are
ignored. Initial join and reconnect trigger a complete refresh automatically. A
complete snapshot at the revision the client already holds, with equal content,
also proves the state and enables mutation again, so a leave and rejoin or a
reconnect that changed no item never leaves the inventory locked.

### Changes Without A Client Intent

Spent arrows commit in worker batches that no client intent caused. The worker
reports each committed batch with `OwnerItemQuantityChanged`: the item-state
revision it applies to, the new carry state, the equipped stack, its new
quantity, and its new revision, where quantity `0` means the stack was used up.
`RealtimeSimulationClient` first advances `NetworkMovementSession` with the carry
state, raising `CarryStateChanged` when it changed, and then raises
`OwnerItemQuantityChanged`. `InventoryClientController` patches the stack in the
`ammunition` equipment slot in place, or empties the slot at quantity `0`, and
takes the new carry values and revision, but only when the complete snapshot is
coherent at exactly the revision the change applies to and the change is the
single transaction after it, because every item transaction advances the
item-state revision by one. The patch keeps the inventory status, so a pending
operation stays busy. In every other case, such as a missed earlier change, an
incoherent snapshot, or a stack the client does not know as equipped ammunition,
it calls `EnsureFullState` with the new revision, which refreshes unless a
coherent snapshot already reached it.

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
already patched in place needs no refresh. When the account or the selected
character changes, the controller abandons a refresh that still waits for its
response, and the refresh queued after it: the routine ends at its next response
without touching the new character's state, so a slow or failing response of the
previous character never shows as an error of the next one.

Every committed transaction of the owner that the worker can describe whole
reaches the client as `OwnerItemStateChanged`: the client operation that caused
it, if any, the revision it applies to, the new carry state and balance, the new
revision of every touched container, and the final state of every changed item,
where a removed item carries only its id. The worker sends it before the carry
state and the operation result, for item operations, corpse loot and deposits,
NPC services such as vendor trades, and gathering yields; see
[Owner Item State Changes](REALTIME_PROTOCOL.md#owner-item-state-changes).
`InventoryItemStateChangePatch` applies it to the coherent confirmed snapshot it
follows: every changed item leaves its slot, then takes its new place, an item
that moved into a container the snapshot does not show, such as a corpse, leaves
the view, and every changed container must have its new revision. A Bag that
enters the Bag slot brings its contents container, laid out as its catalog Bag
with the items the change lists inside it; a Bag that leaves it takes its
contents out of view. A claim's deliveries leave Recovery Storage, and every one
of their items must take a place the change names. The patched snapshot equals
what `GET /api/characters/{characterId}/item-state` returns for the new revision,
so no reload follows. A change the patch cannot describe, such as an item
arriving in Recovery Storage, or one that does not follow the known revision,
calls `EnsureFullState` with the new revision instead. Opening the Bank or
Recovery tab reloads only an incoherent state; the Refresh button still reloads
on request.

### Predicted Operations

`InventoryClientState` keeps the confirmed snapshot and shows it with every
pending item operation applied in order. `FullSnapshot`, `Bank`,
`RecoveryStorage`, and `TryFindItem` return the shown state that the panel and
the target rules use; `ConfirmedSnapshot` and `TryFindConfirmedItem` return the
state the server confirmed. A drop creates an `InventoryPredictedOperation` that
names items and destinations only. `InventoryOperationPredictor` checks it with
the same `InventoryTargetAdvisor` rules as the drop targets and applies it the
way GameService commits it: a relocation, unequip, split, merge, container swap,
equip with its swap into the source slot, destroy, or Recovery claim, with the
carried weight adjusted for items that enter or leave carried storage.

A Bag equip shows the Bag's empty contents, laid out as its catalog Bag under a
temporary container id that nothing may move into until the server names its own
(a move or corpse loot into it is refused with a short wait message), and adds
the Bag's capacity bonus; unequipping an empty Bag removes both. A claim places
the delivery, in item order, in the first free slots of Permanent Inventory or
the Bank that accept each item, as GameService does. A split, a purchase, or a
partial corpse loot shows its new stack under a temporary id that nothing may act
on until the server confirms it. Up to `MaximumPendingOperations` (8) operations
may wait. Every predicted step, corpse and vendor steps included, must end within
the 140 percent hard cap that GameService checks on each voluntary transaction,
so a swap to a smaller Bag cannot show an overload.

Corpse and vendor operations join the same queue through
`IInventoryOperationQueue`, which `InventoryClientController` implements, as
operations that carry their own prediction and their own send.
`CorpseClientController` shows loot, loot into equipment, deposits, moves inside
the corpse, and Bag swaps at once: `CorpseInventoryPrediction` applies the
inventory side, merging into a compatible stack or swapping a whole item, and
`CorpseViewEdit`s show the corpse side on `CorpseClientState.ShownView`, the
confirmed view with the edits applied. The corpse side stays shown after a
successful result until a newer corpse view arrives, which the worker sends right
after the result, or for at most 2 seconds before a refresh. A corpse operation
waits in the queue while the corpse has a refresh in flight and fails once its
corpse is closed. `VendorPrediction` shows a purchase the way GameService
acquires it, topping up unprotected stacks of the definition in Permanent
Inventory and then the equipped Bag before taking the first free slot among
specialized Bag slots, Permanent Inventory, and general Bag slots, and a sale as
a smaller or removed stack; both change the shown balance at once.
`WorldInteractionClientController` sends a queued trade once the interaction is
idle.

Loot all is predicted too: `CorpseLootAllPrediction` feeds the shown inventory
and corpse to the shared `CorpseLootAllPlanner` in WorldData, which GameService
runs on the locked state, applies its steps to the inventory, and empties the
corpse slots the plan takes. The client cannot tell a live Mob corpse from a
durable one, so it plans whole items only for every Mob corpse presentation key,
as a live Mob corpse requires, until the server answers. It does not know the
character's level either, so the plan treats only a level requirement of 1 as
met and shows equipment that requires more going into the inventory. Every item
that arrives shows under an id the operation made until the committed change
names the server's ids.

Right-click moves are ordinary queued operations. `InventoryQuickMoves` in
`Mmorpg.Client.Items` picks the operation a right-click stands for, such as an
equip with the unequips of the items that block its slot, a merge, a relocate to
the Bank or back, a Recovery claim, or a corpse loot into equipment or carried
storage. It checks each candidate with the prediction its operation would make,
so it never offers a move the client knows the server refuses. The panel adds the
windows it shows: an open Bank tab takes the player's items, and an open vendor
tab puts a carried item into the `Sell` slot.

`InventoryClientController` sends the first pending operation once the confirmed
state is current, with the revisions the confirmed state expects, and sends the
next only after the previous one committed. A later operation thus always builds
on the committed result of the one before it. The committed change of the sent
operation names its operation id, so the client stops applying that prediction at
once and never applies a swap twice. When the result arrives first and no change
patched the state, the operation stays shown until a reload reaches its revision.

A rejection undoes every pending operation, because the others were shown on top
of the rejected one, sets the server's error, and raises `PredictionRolledBack`
with the items involved; a rejection that `RequiresInventoryRefresh` then reloads
its scope. A newer confirmed state that no longer fits a pending operation, such
as a gathered stack that lands in the target slot, undoes that operation and
every one after it with `item_move_superseded`; an operation already sent stays
pending without a prediction, because the server still answers it. Every
operation that leaves the queue without committing raises `PredictionsDiscarded`,
so the corpse and world interaction controllers drop what they showed.
`CanMutate` reports whether an operation that needs the confirmed state may
start, and `CanPredict` whether another item operation may be shown. A reload
after a committed operation runs behind the shown state, so the panel says
Loading only for a first load. A disconnect with a pending operation marks the
local result uncertain, discards every prediction, and relies on the reconnect
refresh to resolve the committed outcome.

### Inventory UI

`TemporaryInventoryPanel` is the only replaceable part of the inventory
implementation. It creates runtime uGUI below a World-scene controller and reads
the persistent state without embedding API, protocol, revision, or item-rule
ownership; it shows predicted item operations and never owns item custody. Its
partial files hold the widgets (modules, slot grids, slots, and the carry
meter), the right-click moves, and the vendor tab. Equipment, Context, and
Character Inventory each own a separate panel root at fixed anchors. The three
views that `B`, `C`, and `I` toggle change root visibility only and never
rewrite another module's layout, so the character column keeps its place in
every view; the layout itself is
[Inventory UI Layout](INVENTORY_AND_DEATH_LOOT_DESIGN.md#inventory-ui-layout).
Clicks on the panel never reach the world.

`EquipmentPaperDollLayout` owns the positions of the equipment slots and appends
any slot it does not know, such as one added to the catalog later, in extra rows.
Every slot in every module shares one size, fitted to five columns in the
character column. `InventorySlotPresentation` builds slot labels, badges, and
tooltip text as pure functions, and the panel draws them through
`InventorySlotView` and `InventoryTooltipView`; tooltips never show during a
drag. Bank and Recovery may be inspected through global owning-account reads,
and the context header names the NPC whose open interaction offers the shown
storage or trade. Every mutation still travels through the joined
SimulationWorker and is committed through GameService authority.

`InventoryDragCoordinator`, `InventoryDragSource`, typed `InventoryDragPayload`,
and `InventoryDropTarget` form the reusable uGUI interaction layer. Relocation,
equip, unequip, split, merge, atomic ordinary container-slot swap, and complete
Recovery claims start only from a drop. A drop onto an occupied slot merges
compatible stacks first and otherwise swaps only when both items pass the
opposite slot rules. On an occupied equipment slot, a stack of the equipped
definition tops up the equipped stack, and any other container item equips with
a swap that `InventoryTargetAdvisor.CanEquip` checks: the equipped item must fit
the slot the new item leaves, a non-empty equipped Bag stays, the new item's
blocked slots must be empty, and the carry cap must hold. Valid and invalid
targets give green or red feedback, then revalidate the current state before
sending an operation. A state change during a drag redraws the panel only when
the drag ends, so a confirmation never cancels the drag, and drops always judge
the current slot instead of the one drawn. An undone move marks its items for
`RollbackMarkSeconds` (1.5 s). Item clicks only select action controls for split
or allowed destruction. A Recovery item represents its complete delivery during a
drag because the durable claim remains atomic.

A UI-only target advisor reuses pure WorldData equipment, stack, Bag, Secure
Container, slot-tag, weight, and hard-cap rules to disable obvious invalid
targets and to predict item operations. Server authority always revalidates any
submitted action, and a prediction only shows what the confirmed state will
become: the committed change or a reload always replaces it.

World loot keeps a reserved context, `WorldLootPrepared`, without inventing
snapshots or operations. The corpse context is described in
[Corpse Client State And Presentation](#corpse-client-state-and-presentation).

### Vendor Context

The Context module has a Vendor tab, `InventoryContextKind.Vendor`. Choosing a
vendor capability opens it through the worker, and the confirmed result raises
`InventoryCapabilityOpened`, which opens the complete view on that tab. The
capability's `profileId` names a vendor profile of the bundled item catalog, and
an unknown profile asks for a client update instead of guessing prices. Without
an open vendor interaction the tab only says to talk to a vendor, because trading
needs the interaction to stay open. Every purchase and sale sends the total it
showed as the expected price, together with the known item-state revision and,
for a sale, the item revision.

`VendorClientRules` in `Mmorpg.Client.Items` prices offers and sales with the
shared `VendorPricing` rule and checks them before sending: an offered item, a
purchase of 1 up to one stack, a sale of 1 up to the stack, the price range, the
balance and its upper limit, the 140 percent hard cap, a carried location, a
category the vendor buys, protection and insurance policies, and quest items.
These checks are advisory. GameService recomputes every price, rejects a
different one with `vendor_price_changed`, and owns every final rule. The
worker's result or rejection appears in the vendor tab. The committed change of a
trade, which arrives before the result, already patched the inventory and the
balance, so `EnsureFullState` with the returned item-state revision reloads only
when no change patched it.

### Corpse Client State And Presentation

`CorpseClientController` is a persistent bootstrap-owned adapter over the corpse
messages of `RealtimeSimulationClient`, which it reaches through
`IRealtimeCorpseChannel` in `Mmorpg.Client.Networking` so EditMode tests can drive
it with a fake channel. It owns immutable nearby-presence and active-view state,
assembles complete multi-packet snapshots, applies only monotonic targeted deltas,
correlates one pending corpse operation, and requests a refresh when a stale base
or concurrency result requires it. It never writes inventory or corpse custody
locally. A disconnect clears uncertain transient state, and a new joined session
rebuilds presence and views from server authority.

Every corpse message travels on the reliable ordered path; the sections are in
[Corpse Sections](REALTIME_PROTOCOL.md#corpse-sections). Complete snapshots must
contain exactly the canonical general inventory, equipment, and Bag sections.
Chunk metadata, container revisions, slot capacities, ids, tags, equipment-slot
identities, and item revisions must remain coherent before state becomes visible.
A committed custody-changing result forces the inventory controller to refresh
until it reaches the returned item-state revision, even when the previous full
snapshot was internally coherent. Pure corpse rearrangement does not request a
character refresh because its response contains no character revision or carry
change.

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
`Unavailable` before the first presence snapshot or operation and after a session
reset, `Busy` while an operation is pending, and `Ready` otherwise. A rejection is
an outcome, not a broken state, matching `InventoryClientState.SetServerRejection`.
When the worker rejects an operation on an open view without
`RequiresCorpseRefresh`, for example `carry_weight_limit_exceeded`,
`item_stack_incompatible`, or `corpse_out_of_range`, no view state follows, so
`TryComplete` returns the state to `Ready` and reports the rejection only in
`Error`. `CanMutate` stays true and the panel keeps every corpse slot draggable
while it shows the error in red. The error stays until the player's next
operation, a closure, or a session reset; view state caused by other viewers does
not clear it.

The controller also refreshes the open view on its own. A rejection with
`RequiresCorpseRefresh`, which the worker sets for `item_already_looted`,
`item_quantity_changed`, `bag_state_changed`, `item_state_conflict`,
`item_slot_occupied`, and uncertain GameService outcomes, starts a refresh at
once. A targeted delta that goes stale starts one at once when nothing is
pending, and otherwise after the pending operation's result, success or
rejection. Such an automatic refresh begins through
`CorpseClientState.BeginAutomaticRefresh` instead of `BeginOperation`, and
`IsAutomaticRefreshPending` stays true until its result. It keeps the state busy
but tells the player nothing new, so `Error` and `Notice` stay while it runs,
when it succeeds, and when its snapshot arrives after the result: the rejection
that asked for the refresh stays in red next to the refreshed view. A refresh the
player asks for, through the Refresh button or the context refresh, goes through
`TryRefresh` and `BeginOperation` and clears the error like any player operation.
A rejected automatic refresh reports its own error, and the controller starts no
further refresh for it, so an unavailable GameService cannot cause a refresh
loop; the player can refresh by hand, and the next stale delta refreshes again.

A view-closure message ends the active view through
`CorpseClientState.ApplyClosure`, which asks `CorpseViewClosureRule` in
`Mmorpg.Client.Items` how to present the stable closure code. The viewer's own
close (`corpse_view_closed`), the last item taken from a live Mob corpse
(`corpse_emptied`), expiry (`corpse_expired`), and death
(`combat_character_dead`) are intended ends: they set `CorpseClientState.Notice`
to the worker's message, or a fallback when it is blank, and leave `Error` null;
the own close sets no notice. Every other code, including `corpse_invalidated`,
`corpse_not_found`, and any code the client does not know, becomes a
`CorpseClientError`, so a real invalidation is never hidden. `Notice` and `Error`
are never set together, and a new operation, an error, or a session reset clears
the notice. The panel shows the error in red and otherwise the notice in neutral
text. The client's own confirmed close ends the view without a message, and the
`corpse_view_closed` closure that follows it is ignored because no view is held
any more.

A closure can arrive while an operation on that view is still in flight: another
player takes the last item of a live Mob corpse (`corpse_emptied` to every
viewer), the corpse expires, the viewer dies, or the worker notices an emptied
corpse before it finalizes the looter's own loot. The worker still sends exactly
one result for that operation, so the closure ends the view but keeps the pending
operation id, and the state stays busy. `CanBegin` in the corpse controller and
`WorldInteractionClientController` therefore keep blocking new corpse and world
work until the result arrives. The late result, success or rejection, only
settles the correlation: the closure's notice or error stays, so an expected
`corpse_not_found` or `combat_character_dead` rejection after an emptied,
expired, or death closure never replaces the neutral notice with a red error.
The controller still calls `InventoryClientController.EnsureFullState` when the
late result reports `RequiresInventoryRefresh`, and it never refreshes a closed
view; a stale delta noticed before the closure no longer asks for a refresh.

`CorpsePresentationController` creates one presentation for every nearby
presence entry and updates it from server position, facing, and presentation key:

- A player corpse, presentation key `corpse.remains`, shows the dead character's
  body lying in the last pose of the handmade `Death` clip, turned by the
  presence's `YawDegrees`, so it lies where and as the body fell, in the body
  model, look, and headwear of the presence's `Appearance`, wearing the garments
  of the items still in the corpse. `PlayerCorpseBodies` in `WorldSceneContext`
  bakes one body per body model and dressed look, keyed by the model's name and
  the look's key, from the remote player prefab's body playing the end of
  `Death`, into a static mesh that every corpse of that look shares, so a field of
  corpses costs no animation or skinning; the corpse has a box collider around it
  for the pointer. The hidden copy of the body takes the look from the shared
  `PlayerBodyMaterials` before it is baked, since the underwear is paint over the
  skin. A corpse whose look changes, such as when someone loots its cloak, swaps
  to the body of its new look, and a baked body that no corpse in view shows any
  more is destroyed. While a body that shows its character dead lies within
  1.5 m, the corpse stays hidden, so the dying body and its remains never show
  twice; it appears when the character respawns at the World spawn.
- A Mob corpse whose presentation key names a Mob archetype, such as
  `corpse.mob.feral_wolf`, shows an instance of that archetype's prefab, loaded
  through the actor presentations, lying in the last pose of its `Death` clip at
  the presence's position, turned by its `YawDegrees`, with a box collider around
  the lying model that each prefab bakes once. The worker sends the corpse before
  the dying Mob leaves, so while a Mob of that archetype still falls dead within
  1.5 m the model stays hidden; once the Mob's view is gone, the corpse plays on
  the rest of its fall, as [Creature Animation](#creature-animation) describes,
  so the fall never jumps or shows twice. A corpse whose presentation registry or
  prefab is still loading is a capsule until both have loaded.
- Every other corpse, such as one with presentation key `corpse.mob`, is a
  generic capsule with a shared renderer property block on the runtime primitive
  material, which never creates per-frame materials.

Loot in a corpse's world menu, also its left-click option, walks the player into
range and opens it through the shared [World Menu](#world-menu). No authored
scene or prefab object is required for the corpse presentation.

The Context module switches to Corpse when an open snapshot arrives, and only the
complete view shows it, so the panel switches to that view and the character
views close the corpse. It renders all three sections and reuses
`InventoryDragPayload` for corpse sources:

- Dropping a full or selected partial stack into Permanent inventory, the
  equipped Bag, or Secure Container submits a typed corpse intent, and dropping a
  carried item into a compatible corpse slot submits a deposit.
- Dropping a corpse item onto another corpse slot submits an authoritative
  internal move, split, merge, or complete swap.
- Dropping a corpse Bag onto the occupied player Bag slot submits one atomic
  aggregate swap, and onto an empty Bag slot a `LootToEquipment` that takes the
  Bag with its contents. An empty corpse Bag drops into an empty carried slot
  like any item.
- Dropping any other whole corpse item onto a player equipment slot submits
  `LootToEquipment` with the item now in that slot as its target:
  `CorpseTargetAdvisor.CanLootIntoEquipment` shows green for an empty compatible
  slot, a top-up of an equipped stack of the same definition, or a swap whose
  equipped item fits the corpse slot the loot leaves, and red for a partial stack
  or a blocked slot.
- The `Loot all` button above the sections submits `LootAll`, and a right-click
  on a slot runs its `InventoryQuickMoves` move; only the left button drags.

Equipment destinations show the catalog display name, and local feedback rejects
obvious type mismatches. `CorpseTargetAdvisor` in `Mmorpg.Client.Items` holds the
corpse target rules next to `InventoryTargetAdvisor`. Green and red target
feedback remains advisory, while GameService owns all final slot, equipment,
policy, revision, weight, capacity, and hard-cap validation.

### Development Item Tools

`MMORPG > Tools > Inventory Item Grants`, `InventoryItemGrantWindow` in
`Editor/DevelopmentTools`, is an Editor-only local development window. It
discovers initialized characters, their balances, and active definitions through
a short-lived GameService command, then invokes individual grants, deterministic
test packages from `content/development/item-packages.json`, currency grants,
and corpse creation through the authoritative GameService services. The Editor
owns only process orchestration and presentation. It does not connect to
PostgreSQL, reproduce catalog rules, mutate runtime client state, or add a
gameplay endpoint. The commands and their guards are in
[Development Item Commands](SERVICE_FEATURES.md#development-item-commands), and
how to use the window for testing is in
[Inventory Item Grants Window](MANUAL_TESTING.md#inventory-item-grants-window).

`DevelopmentItemToolCliProcess` runs every command in two steps. It first builds
GameService incrementally in Release through MSBuild, with `-t:Build`, since
`-getProperty` alone only evaluates the project, and MSBuild stays the only
authority on whether the output is current. It then runs exactly the assembly
path that build reports in one `dotnet` process. A failed or timed out build runs
nothing, so the window never uses an older build against a newer database.
`DevelopmentToolProcess` owns each external process: it captures output, and a
timeout, a closed window, or a script reload ends the process together with every
descendant. Windows uses `taskkill /T`; macOS and Linux stop every descendant that
`ps` reports and then kill them, so no process in the tree can start another
child meanwhile. When GameService stops without a response, the window shows the
unhandled exception message first and logs the complete output to the Unity
Console.

The commands work only in the Development environment against a loopback,
non-production-like PostgreSQL database, and every target character must be
offline. This guard is required because the Editor command mutates durable state
directly and has no active SimulationWorker result path that can advance the
joined session's carry tuple and item revision; safe online grants would need a
future service-authenticated development intent routed through the owning
SimulationWorker. Individual grants can extend an existing character, while
packages require an empty character so their exact test state is reproducible.
Backend validation remains authoritative for stack limits, destination
eligibility, weight, the hard cap, revisions, policies, and container slots. The
response is machine-readable and refreshes the Editor view after a successful
mutation.

The window can create a durable corpse for an offline source character at a
selected Shard position: an empty source first receives the corpse seed package,
then the normal system-death adapter performs the death partition. The Editor
does not manufacture corpse rows, custody, revisions, or snapshots, and
SimulationWorker restores the committed corpse through its normal runtime
registration path. Its Give Currency section shows the selected character's
balance, an `Amount` field, and a `Give Currency` button, enabled only for an
offline character, a positive amount, and while no other command runs; it
credits the character through the GameService wallet with a `development` ledger
row instead of direct SQL.

## Player Bodies

The LocalPlayer and RemotePlayer prefabs show one of the human base bodies, the
body models that `Assets/Content/Player/PlayerBodyModels.asset` lists:
`Human_Male`, the default, with `Assets/Prefabs/Player/HumanMaleBody.prefab`, and
`Human_Female` with `HumanFemaleBody.prefab`, each a Prefab Variant of its
exported model with its own look catalog; see [Prefabs](ART_PIPELINE.md#prefabs).
The player prefabs hold no body of their own: `PlayerBodySlot` on `PlayerVisual`
references the models and puts in an instance of the chosen model's body as
`PlayerVisual/Body` when `WorldSceneContext` spawns the player, before the body
is combined and given its look and tint, so a player instantiates only the body
it shows. Showing another model replaces the body: the old one leaves the player
at once, is hidden, and is destroyed, and `LocalPlayerController.Body` and
`RemotePlayerView.Body` look the body up again once the one they found left the
player. `WorldSceneContext` refuses to start when either player prefab has no
slot with a default model.

Every player shows the body model the server stores with its character, chosen
at character creation; see [Character Looks](#character-looks). A model the client
does not know shows the default. The body has no collider; the movement capsule,
the remote pointer trigger, and the health bar height do not depend on it.

### Locomotion

The body's Animator uses its Humanoid avatar,
`Assets/Art/Animation/HumanLocomotion.controller`, no root motion, and
`Cull Update Transforms`, so a body off screen skips its pose. Every clip it plays
comes from `Assets/Art/Models/Animations/Human_Actions.fbx`, the
[animation library](ART_PIPELINE.md#animation-libraries) of the human bodies, as
Humanoid motion that fits every body; the body's own model has no clips. The base
layer moves between `Idle` and `Locomotion`, a 1D blend tree on `Speed` with
`Walk` at 1.4 m/s and `Jog` at 5.0 m/s, the speeds the clips were authored for,
played at the rate `LocomotionRate`. The bool `Moving` switches between them with
short fixed crossfades. The two speeds must match the gait model in
`tools/art/blender/human_base/animate.py`, which `PlayerBodyAssetTests` checks.

`CharacterLocomotionAnimator` drives these parameters from how far its body moved
over the ground in the frame, so the local player, remote players, and anything
else that moves a body animate the same way without knowing about each other. It
runs after every character moved and before the Animator evaluates
(`PresentationExecutionOrder.Locomotion`), smooths the speed over 0.08 s, and
ignores a jump faster than 20 m/s, such as a teleport or a correction.
`HumanLocomotion` turns the speed into the parameters: below the walk speed, as
when a character is encumbered, the walk plays slower, down to 0.3 of its rate;
between walk and jog the rate makes the blended stride match the speed, from the
lengths of both cycles; and above the jog speed the jog plays faster.

The controller's third base state, `Death`, plays the handmade `Death` clip of
the library. An Any State transition crossfades into it in 0.15 s while the bool
`Dead` is true, from any state, and it holds its last frame, the body lying on its
back. `CombatDeathPresentation` in `Mmorpg.Client.Combat`, which the bootstrap
creates in every World scene, tells every player body in view each frame whether
its character is dead: the local player from `OwnerCombatState` and every other
player from its `CombatantState`. A player without combat state, such as a
development bot, is never shown dead. `CharacterLocomotionAnimator.ShowLifeState`
turns the life state into the Animator: a body whose character dies in view
falls, a body that first shows its character already dead, such as a player who
died before coming into view or a client that joins while dead, lies in the end
pose at once, and a body whose character lives again stands in `Idle` at once,
because the respawn teleports it to the World spawn. While dead, an Animator that
starts over, after `Rebind` or when it is enabled again, goes back to the end
pose.

### Combined Bodies

A body is made of skinned parts, one per equipment zone plus the eyes, teeth, and
tongue. Drawn as parts, a crowd pays for every part of every body: 200 bodies
cost about 20 ms of main thread time per frame in the Editor. When
`WorldSceneContext` puts the body on a spawned player, a few players a frame as
[Crowd Performance](#crowd-performance) describes,
`PlayerBodyAppearance.CombineParts` draws the visible parts as one skinned
renderer, `CombinedBody`, with one submesh per material, and hides the parts.
`PlayerBodyMeshes`, owned by `WorldSceneContext`, builds the combined mesh once
per model and set of visible parts and shares it between every body that has
them; each body binds it to its own bones. 200 combined bodies cost about 6 ms,
with skinning down from 4.3 to 0.3 ms and culling from 3.4 to 0.35 ms. The
context destroys the meshes with the scene.

When the outfit changes, `CombinedBody` takes the new mesh while it is turned
off: Unity 6000.6 stops drawing an enabled skinned renderer whose mesh changes to
one of another size, with the error `does not match the expected mesh data size
and vertex stride`. A test renders every body after each change of its outfit; it
needs a graphics device, so it is in the `Graphics` category, see
[Test Architecture](#test-architecture).

The Debug menu's body tint setting is `PlayerBodyTintSetting`, which
`ClientBootstrap` owns, keeps in `PlayerPrefs` between sessions, and passes to the
menu and to `WorldSceneContext`. The context tints the local and every remote
body when the setting changes and every body that spawns. A tint multiplies the
skin materials of the body, which `PlayerBodyAppearance` lists, by a faint
version of the color, 35 percent of the way from white, and leaves the eyes,
teeth, and tongue alone. The colors are in `PlayerBodyColors`.

### Looks

A body also shows a look: the options of its model's
[appearance layers](ART_PIPELINE.md#appearance-layers), such as another face or
eye color, its paint layers, and its garments. The body's `PlayerBodyAppearance`
references the model's `BodyAppearanceCatalog`, such as
`Assets/Art/Appearance/Human_Male_Appearance.asset`, which the art pipeline
writes: one slot per material with variants, such as `Eye` and `Face`, with its
options, the material's own image first, and the body's paint layers and
garments. A `BodyLook` is a value that names one option per slot, with a stable
key such as `Eye=Brown;Face=Clean`; a slot it does not name shows the material's
own image.

Showing a new model puts in the other body, combines it, gives it the tint and
the look, and offers its catalog. One look serves both models: an option, paint
layer, or garment that the shown body's catalog lacks, such as a face of the
other body or the bra on the man, is not shown and comes back with the body that
has it, while what both bodies have carries over.

A look also names the paint layers its body wears, such as `Tattoo`, which the
catalog lists for its body with their category, slot, material, image, its crop
of the body's paint UVs, layer, smoothness, and metallic. The body materials that
paint layers paint draw with the skin shader `Mmorpg/Character Skin`, URP Lit
with up to four paint layers; see [Paint Layers](ART_PIPELINE.md#paint-layers). A
look's paint layers on such a material go into its copy, from the lowest layer
up, and a material with another shader ignores them.
`BodyAppearanceCatalog.ShownPaints` applies the rules of
[The Look On The Skin](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-skin) through
`AppearanceRules`: of each category and slot it takes the first the look names,
the first underwear of each slot when the look names none, and leaves out the
underwear of a slot whose drawn garment has `HidesUnderwear`.

The look and the tint draw with copies of the body's materials, another image in
`_BaseMap`, paint layers, a tinted `_BaseColor`, or any of them together, that
`PlayerBodyMaterials` in `WorldSceneContext` creates once per material, image,
paint layers, and tint and shares between every body that looks the same, so a
crowd stays in the SRP Batcher; a property block per body would keep every body
out of it. Every look draws its underwear, so even the default look draws the
body material with its paint, and a body draws as it should only once it has been
given its look; a body without a look shows the skin without the underwear. The
look is applied to the combined renderer, so bodies with other faces still share
one combined mesh, which keeps the parts' paint UVs, their second UV map, for the
skin shader.

### Putting A Character Together

A look also names the garments its body wears, such as `Garment=LeatherJerkin`
in its key. A garment is a [garment texture](ART_PIPELINE.md#garment-textures) on
its [template](ART_PIPELINE.md#garments), named after the texture. The catalog
lists every garment that fits the body with its template's name and model for
that body, the texture's material, the visual slot, layer, and the slots it
hides, the body faces it covers, the body face under each of its triangles, and
its pull-in, from the texture's own masks when it has transparent parts and from
the template's otherwise, and every body part with the face of the body's
editable mesh that each of its triangles comes from and the vertex each of its
vertices is. Unity imports a model's triangles in an order of its own, so these
faces are found again by position when the catalog is written in the Editor: every
body exports its editable mesh next to its model; see
[Unity Import Rules](ART_PIPELINE.md#unity-import-rules).

When the look's garments change, such as when equipment changes, and never per
frame, `PlayerBodyAppearance` puts the body together again in `PlayerBodyMeshes`,
following the layers of
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#layers-of-a-character):

1. The garments of a slot that a worn garment of a higher layer hides are left
   out, such as trousers under a floor-length robe, while leg armor over the robe
   stays, and so is the hair under headwear whose `Hair` is `hide`; the others are
   drawn from the lowest layer up.
2. A body triangle is left out when a drawn garment covers its face, and a
   garment triangle when a garment of a higher layer covers the body face under
   it. Loose parts, such as a robe's skirt, cover nothing, and the cloak covers
   nothing at all, so what lies under them stays drawn when they swing.
3. A pulled-in body vertex moves inward along its normal by its distance, the
   largest any drawn garment asks for.
4. The drawn garments' meshes join the body's in one mesh, with their bones bound
   by name to the body's skeleton, whose bind poses they share, each drawing with
   its texture's material instead of its template's, and the vertices no triangle
   uses are left out. Hair under headwear whose `Hair` is `compress`, such as the
   hood up or the helmet, takes its blend shape `UnderHeadwear` in full, its
   vertices, normals, and tangents, so it lies flat under the headwear at no cost
   per frame; under the hood down it stays as it is.

The combined mesh is shared by every body with the same parts, garments, garment
materials, and blend shapes taken, so every character with the same look shares
one mesh, as development bots do. A body wears one garment per visual slot, such
as one chest piece: of two of a slot that its look names, the first in the catalog
is worn, and trying one on in the Wardrobe takes off the other.

Garment materials draw with the garment shader `Mmorpg/Garment`, URP Lit with
three dye channels, cut at alpha 0.5 and drawn from both sides; see
[Unity Import Rules](ART_PIPELINE.md#unity-import-rules). In the Editor,
`MMORPG > Tools > Art Pipeline > Wardrobe` shows a body in chosen garments playing
its clips.

### Garments From Equipment

Items show their garments, as
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#items) describes. The item
presentation catalog names the garments of every item and their dye colors, and
`EquipmentGarments.Dress` dresses a look in the garments of the items it wears:
it keeps the look's hair, the one garment of the `hair` slot that the look itself
names, takes off every other garment the look names, and puts on the garments of
the equipped items in their dye colors, one per visual slot, the garment of the
item in the equipment slot of the visual slot's name first, and the lowered
garments of a lowered item.

The local player wears its equipment as its inventory shows it, with its
predicted moves, so its body changes as soon as an item moves: `ClientBootstrap`
keeps a `PlayerEquipment` of the equipped items and the presentation catalog up
to date from the inventory state and passes it to `WorldSceneContext`, which
dresses the local player's look in it whenever either changes. Other players,
bots, and corpses wear the garments of the items the server sends for them; see
[Character Looks](#character-looks).

A look names the dye colors of a dyed garment in its key, such as
`Garment=WoolCloak@2B4364`, and the garment then draws with a copy of its
texture's material in those colors, from `PlayerBodyMaterials`, shared by every
body in the same colors. A garment in its natural colors, or without dye
channels, draws with the material itself, so only the colors and not the mesh
decide the copy.

### Cloth Chains

The body's skeleton has the cloth chains of
[Cloth Chains](EQUIPMENT_VISUALS_DESIGN.md#cloth-chains), which `ClothChains`
lists: eight skirt chains on the hips, five cloak chains on the upper chest, and a
hood chain on the head. How the cloth hangs in its target pose, how the springs
swing it, how the body and the garments are measured, and what it costs are in
[Runtime Solver](EQUIPMENT_VISUALS_DESIGN.md#runtime-solver). The client builds it
from these parts:

- `ClothChainSolver`, on the body prefab, activates the chains of the kinds a
  worn garment hangs on, with a `ClothProfile` for its margin, slack, and
  springs: a robe the skirt chains and a cloak the cloak chains. A hood with a
  loose point would hang on the hood chain; the current hoods have none, so it
  stays unused. With no garment on a chain the solver does nothing. It measures
  the body from its own mesh in the bind pose, and each worn garment's model that
  `PlayerBodyAppearance` hands it, once, shares the measures per model, and
  describes them to a `ClothWorld`.
- `ClothChainScheduler` owns the one `ClothWorld` of every body in play and puts
  itself in play with the first solver. Once a frame after the camera moved
  (`PresentationExecutionOrder.Cloth`), it lays every body the camera sees within
  `ClothChainSolver.FarDistance` in its target pose and gives springs to the
  bodies largest on the screen, as many as `ClothChainScheduler.Budget` holds,
  keeping them against a slightly larger body so no body flickers between the
  levels. Three Burst jobs do the work: one reads the bodies' bones, one lays
  each body with `ClothPose` and swings its springs with `ClothSprings`, and one
  turns the chain bones. Out of play, as in the Wardrobe and the tests, each body
  lays its own chains in a world of its own.
- `Mmorpg.Client.Cloth` holds the cloth math on plain data, without memory from
  frame to frame in the target pose, so the art pipeline's `cloth_core.py` lays
  the same cases; see [Cloth Parity](ART_PIPELINE.md#cloth-parity).
- A jump carries the cloth along: the solver finds one on its own, and the game
  says so with `ClothChainSolver.NotifyDiscontinuity`, the local player on a hard
  correction or a teleport, and `RemotePlayerView` and `MobView` whenever
  `RemoteMovementPresenter` presents a new start or restarts its clock.

### Attachments, Tools, And Weapons

- **Attachments.** `EquipmentGarments.Dress` puts an item's
  [attachment](ART_PIPELINE.md#attachments), such as the Iron Sword, into the look
  when the item is in the slot of the attachment's socket. `PlayerBodyAppearance`
  takes the catalog's attachments that the look names, one per socket,
  `PlayerBodyMeshes` combines them rigidly on their socket bones into the body's
  one mesh, and the body turns on the animator layer `MainHandGrip` or
  `OffHandGrip` of each hand that holds one, which plays the clip `Grip` on that
  hand's fingers. An Animator that is enabled again takes the layer weights in the
  next `LateUpdate`. A corpse is baked with the attachments of the items still in
  its equipment.
- **Tools in use.** A tool, such as the Mining Pickaxe, is an attachment with a
  use clip, held only while its character uses it: `EquipmentGarments.Dress` holds
  the tool attachment of the item it is told is in use in place of the attachment
  on its socket, and, for a tool held with both hands, of the other hand's too, so
  a sword and a shield are put away. `PlayerBodyAppearance` tells
  `CharacterLocomotionAnimator` the tool's clip, which crossfades in 0.2 s into the
  base layer state of that name, `Mine` or `Chop`, while the body stands and
  lives, and back to `Idle`, or to `Locomotion` when the body moves, when it lets
  go of the tool or starts to move; its `UseWeight` is the share of the pose that
  comes from the tool's clip. A tool held with both hands closes the other hand
  too, and `PlayerBodyAppearance.LateUpdate` pins that hand on the tool after the
  Animator posed the body, by `UseWeight`: `OffHandToolGrip` puts the hand's
  socket on the tool's `OffHandGrip`, turned half around the main socket's
  forward axis, with analytic two-bone IK that keeps the elbow in the plane the
  clip bends it in, and turns the collar bone forward by up to 15 degrees where
  the haft lies beyond the arm's reach. The clips are keyed on the male body, so
  retargeting moves the female's other hand by up to about 8 cm, which the pin
  takes back. The Wardrobe plays `Mine` and `Chop` with the pin.
- **Weapons with clips.** The weapon of a body is the main hand's attachment that
  is no tool, such as the Oak Staff, or else one in the off hand with an attack
  clip, such as the Training Shortbow; it may name a hold clip, an attack clip
  with the seconds into it when it releases, and a launch point; see
  [Weapon Poses And Attack Clips](EQUIPMENT_VISUALS_DESIGN.md#weapon-poses-and-attack-clips).
  `PlayerBodyAppearance` tells `CharacterLocomotionAnimator` the hold clip of each
  hand, which plays in the state of its name on the layer `MainHandHold` or
  `OffHandHold`, masked to that arm, at full weight while the body lives and uses
  no tool; bodies without such a weapon never touch the layers.
- **Attack clips.** `PlayerBodyAppearance.PlayAttack` plays the weapon's attack
  clip, or `OneHandStrike` for a melee weapon in one main hand without a clip of
  its own, once on the layer `Action`, masked to the root, the spine, head, legs,
  and arms without the fingers, so the clip's steps and the turn of its hips play
  too. It starts the clip as if `HumanLocomotion.AttackClipTime` seconds of it had
  passed, so the clip comes to its release when the attack releases: a negative
  time waits that long, and a late start never passes the release. The clip fades
  in over 0.06 s from where it starts to show and out over 0.15 s; `StopAttack`
  ends it with the fade or drops one that waits, and a body that dies ends it at
  once, or starts to move during it ends it with the fade. `ActionWeight` is its
  share of the pose, and `IsPlayingAttack` says whether it plays and how far in.
  With the attack's target, `Face` turns the body below its character toward the
  target while it stands and the clip plays and for `FacingHoldSeconds` (1.5 s)
  after, at `FacingDegreesPerSecond` (720 degrees per second), and back once it
  moves. `TryGetLaunchPoint` gives where the weapon's projectile leaves it now,
  the launch point on its socket bone.
- **Two-handed swings.** While the attack clip of a weapon whose attack takes
  both hands plays, such as `GreataxeSwing`, `PinOffHand` holds the other hand on
  its `offHandGrip` wherever the clip brings that hand within
  `OffHandToolGrip.NearMeters` (6 cm), fading out by `FarMeters` (18 cm), and
  closes that hand by the same weight.
- **Skinned attachments and the bow.** A skinned attachment, such as the bow,
  keeps its own bones, which the body copies below the socket from the model,
  binds the combined mesh to, and destroys when it lets go of it. For the bow,
  `BowRig` drives them in `LateUpdate` while `BowShot` plays before its release:
  the drawing arm reaches for the resting string and draws it along the arrow's
  line with the arm solver of `OffHandToolGrip.Place`, the nock follows the main
  hand's socket, the limbs bend from the end pose of the bow's clip `Draw` by how
  far the string comes back toward its `drawLength`, each half of the string aims
  at the nock and stretches, and the main hand closes on the string; `Loose` lets
  the string spring back to brace. `TryGetNockedArrow` gives where an arrow lies
  on the string. The Wardrobe holds a weapon in its pose the same way and plays
  its attack clip with the motion `Cast`.

### Character Looks

The body model, the look, and the headwear of every character come from the
server, as [The Look On The Server](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server)
describes. `CharacterLooks` turns the choices the server sends into the
`BodyLook` a body shows, and back: `face` and `eyes` are options of the `Face` and
`Eye` slots, `underwear`, `scar`, and `tattoo` paint layers, and `hair` a garment
of the hair slot. `RealtimeSimulationClient` keeps the latest
`CharacterAppearance` of every spawned player and of the controlled one, from
`EntitySpawn` and `EntityAppearanceChanged`, and sends `AppearanceIntent`; see
[Character Appearance](REALTIME_PROTOCOL.md#character-appearance).

- **The local player.** `ClientBootstrap` owns the `PlayerBodyLookSetting`, the
  body model, look, and headwear the server stores for the joined character, and
  `PlayerLookLink` keeps it in step: it stores the look the worker sends for the
  controlled player, forgets it when the session ends, sends the changes the
  Admin menu and the inventory ask for, and shows a refused change in the game
  messages. `WorldSceneContext` shows the model and dresses the look in the
  inventory's equipment, with its predicted moves, and the stored headwear, and
  holding the tool of the equipment slot `PlayerEquipment.UsedSlot` names, which
  `ClientBootstrap` sets from `GatheringClientState.UsedEquipmentSlot`: the Tool
  slot while the character gathers.
- **Other players.** `RemotePlayerView.ApplyAppearance` shows the sent body model
  and dresses the look in the garments and attachments of the sent visible
  equipment with the sent headwear, holding the sent `ToolInUse`, from the item
  presentation of `PlayerEquipment`; a new presentation dresses every player
  again.
- **A dying body keeps its clothes.** While a body shows its character dead,
  `WorldSceneContext` keeps every new look for it and shows the last one once the
  body lives again, since its items lie in its corpse already.
- **The Admin menu (F3)**, in its Look tab, shows the stored body model and look
  with a row per body model, slot, paint category and slot, and hair, with `none`
  for hair, a scar, or a tattoo, and asks the worker for the stepped look; only a
  GameService in the Development environment stores it, and the new look shows
  when the worker sends it back.
- **The inventory** shows `Lower <item>` or `Raise <item>` in its footer while the
  character wears an item whose presentation has lowered garments, such as the
  Wool Hood, and asks for the other headwear state.
- **Character Select** loads what creation offers from
  `GET /api/character-appearance` into `CharacterCreationLook`: one button per
  body model and one row per kind and slot of the chosen body that steps through
  its options, starting at the first option of a required kind and at `none` for
  a kind a character may go without; another body model starts from its own
  defaults. `Create Character` sends the chosen body model and look, and the list
  names each character's body model.

`CharacterLookTests` holds `content/characters/appearance.json` to both bodies'
catalogs and to `AppearanceRules`.

### Crowd Performance

A crowd of dressed players costs the client in five places, and each is kept
bounded:

- **Cloth.** Burst jobs on the worker threads lay every body in view in its target
  pose and swing the cloth of the largest on the screen with springs, as
  [Cloth Chains](#cloth-chains) describes; a body behind the camera or beyond 60 m
  costs nothing. The measured cost of the cloth is in
  [Runtime Solver](EQUIPMENT_VISUALS_DESIGN.md#runtime-solver).
- **Bodies come into view a few per frame.** Putting a body on a spawned player
  takes about 1.75 ms, since the cloth measures are shared per model, and several
  players often come into interest in one frame, so `WorldSceneContext` puts
  bodies on for at most `BodyMillisecondsPerFrame` (3 ms) a frame, the nearest to
  the camera first and at least one. A player whose body waits already moves, is
  picked by its trigger, and gets the newest look the server sent when its body
  goes on. The first body in a new combination of garments still builds its
  combined mesh, about 10 ms once per combination.
- **Combined bodies and shared materials.** Every body draws as one combined
  renderer with shared meshes and materials, as
  [Combined Bodies](#combined-bodies) and [Looks](#looks) describe.
- **Remote players never load collision chunks**, like Mobs: a far spawn would
  load up to 25 chunks, about 1.7 ms, which the next owner snapshot unloads again.
- **The Debug menu** lays out nothing on a refresh and draws nothing while
  closed.

Hot paths allocate nothing: avoid `Mathf.Max` with three arguments, which
allocates an array, and a sort with a `Comparison`, which allocates a comparer.

The last measurement of a large crowd ran 400 ActiveSimulationBots on
`local-shard-1` (Development World 2), about 170 dressed players in interest, in
the Editor with the Debug panel open: about 83 FPS with the default camera and
79 FPS with the camera low over the crowd, frames of 12.1 ms on average, 16.5 ms
at p95, and 37 ms at most, and 54 to 70 KB of managed garbage per frame. Of that
frame, the Editor's own loop took about 3 ms, which a player build does not pay;
rendering about 4 ms of main thread, every body drawing about ten submeshes in
the camera and again in the shadow map, the shadows of the remote bodies about
1 ms of it; remote movement, animation, and skinning about 2.5 ms; the cloth 2 to
3 ms; and the Debug panel 1.4 ms. The GPU took under 1 ms. That measurement
predates the Burst cloth jobs and the current Debug menu, which both cost less.

The next levers are the levels of detail at half and a quarter of the triangles
that [Budgets](EQUIPMENT_VISUALS_DESIGN.md#budgets) plans, a shorter shadow
distance for bodies, animation that updates far bodies less often, and the
garment texture arrays that
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#putting-a-character-together-in-unity)
plans for fewer draw calls.

To measure, start the bots on `local-shard-1` as in
[Visual Unity Test](ACTIVE_SIMULATION_BOTS.md#visual-unity-test), join, and read
FPS and frame times on the Debug menu's Performance tab (F2), or record the Unity
Profiler with Deep Profile off. `ClothChainScheduler.LateUpdate` and
`WorldSceneContext.Update` show the costs above, and the profiler marker
`ClothChainScheduler.Step` shows the cloth; set `ClothChainScheduler.Budget` to
compare budgets.

## World Actor And Interaction Client

The client keeps permanent state below replaceable presentation:

- `WorldActorClientController` owns immutable, monotonic actor presence and
  replicated state across scene transitions and reconnect cleanup.
- `WorldInteractionClientController` owns correlated operations, the active
  authoritative interaction session, capability summaries, server closure, and
  uncertain-state cleanup.
- `WorldMenuController` owns the right-click world menu and the left-click
  default option, described in [World Menu](#world-menu).
- `WorldInteractionTargetingController` offers the menu options of registered
  NPC, resource node, corpse, and future world-object views, approaches a chosen
  target, and opens it.
- `GatheringClientState`, owned by `WorldInteractionClientController`, mirrors the
  worker's gathering state for the one node the player gathers from, as described
  in [Gathering](#gathering).
- `WorldActorView` provides presentation identity and target registration.
  `NpcView`, `MobView`, and `ResourceNodeView` add kind-specific presentation
  hooks without actor authority. A `MobView` follows its snapshots while it moves,
  as described in [Mob Movement](#mob-movement). An inactive actor, such as a dead
  Mob, is hidden until it returns. A depleted resource node instead stays visible:
  an authored prefab switches from its `Available` child to its `Depleted` child,
  and the fallback primitive shrinks to 35 percent of its height. Its target and
  interaction collider stay disabled until the node respawns, so it cannot be
  clicked.
- `TemporaryWorldInteractionPanel` is a replaceable uGUI action list over the
  permanent controller and capability contracts. It grows with its content,
  draws the approach and the gathering progress, and shares its colors and
  buttons with the inventory panel through `TemporaryUiKit`.

The presentation registry
`Assets/Content/WorldActors/WorldActorPresentationRegistry.asset` maps
`presentationArchetypeId` to collider-free prefabs in
`Assets/Prefabs/WorldActors`, each an Addressables `AssetReferenceGameObject`.
The NPC prefabs are placeholders built from primitives; the Mob and resource node
prefabs come from the art pipeline, see [Prefabs](ART_PIPELINE.md#prefabs). A Mob
prefab keeps the actor's scale on its root, so its interaction bounds stay in
meters, and its `Model` child carries the creature's Animator, driven by a
`CreatureAnimator` on the root as [Creature Animation](#creature-animation)
describes. A resource node prefab has the roots `Available` and `Depleted`. The
registry loads when a World scene opens, a prefab loads the first time an actor of
its archetype appears, and the actor is presented once its prefab is ready. All
handles are released when the World scene closes. An authored prefab keeps its
own look. Only an unmapped archetype or a prefab that fails to load falls back to
a tinted primitive, with one warning per archetype, and a missing registry logs
one warning instead. An EditMode test fails when an authored archetype has no
prefab.

The persistent realtime client decodes the actor, interaction, and gathering
messages through the shared GameProtocol source and keeps the explicit mismatch
path for older or partially updated clients. Typed insurance apply or remove,
quest accept or abandon, vendor purchase and sale, and start gathering payloads
ride on the capability action instead of a second NPC or item interaction
transport; see [Capability Actions](REALTIME_PROTOCOL.md#capability-actions).
`TryPurchase` and `TrySell` send the expected price and the character revision,
together with the offer definition and quantity or with the item, its revision,
and the quantity. `TryStartGathering` sends no revision or trade terms. A
confirmed Character Bank, Recovery Storage, or Vendor capability raises
`InventoryCapabilityOpened`, which opens the complete inventory view with that
storage or the vendor.

Mob loot needs no parallel controller. The corpse presence and nullable
source-character contracts represent both live Mob and durable player or boss
corpses. The same client view, pointer target, operation journal, canonical
three-section snapshot, committed delta, inventory refresh, and reconnect cleanup
paths remain authoritative. Corpse presentation registers with the shared target
controller, and its open view consumes the shared one-active-interaction lease
while retaining `CorpseClientController`, the corpse protocol, and durable
transaction authority. Shared targeting changes selection and session
coordination, not corpse custody.

The panel displays only the authoritative capability summary received after an
interaction opens. Insurance actions enumerate eligible items from the persistent
authoritative inventory snapshot and submit exact character and item revisions.
Quest item-lifecycle actions submit the exact character revision and Permanent
Inventory destination. The panel never applies an optimistic policy, grant,
removal, currency charge, or item deletion. A committed result triggers a full
inventory refresh at the returned item-state revision.

Inventory policy summaries carry a safe presentation source, not a raw grant or
insurance lineage id. The inventory panel shows that source in its item tooltip,
and Recovery headings translate the durable source kind into consumed insurance
or protected-on-death delivery labels.

Actor prefabs are presentation-only. There is no authoritative
`VendorMonoBehaviour`, `QuestGiverMonoBehaviour`, crafting component, insurance
component, or Mob AI component in Unity. Changing a prefab cannot change actor
kind, capability, faction, spawn, damage policy, or interaction rules.

The persistent bootstrap creates the actor, presentation, targeting, interaction,
and temporary panel controllers at runtime. A new archetype works through the
fallback presentation without scene edits until it is added to the presentation
registry asset. A content creator uses the Editor windows only when changing
definitions, spawn source, or the presentation registry asset. Editor scene
components are authoring adapters: they import and export stable neutral
definitions, but only compiled WorldData creates runtime actors, and actor and
spawn content must remain buildable and verifiable without opening Unity; see
[Unity Content Tools](../content/README.md#unity-content-tools).

### World Menu

Every world click goes through `WorldMenuController`, a runtime scene component
that `ClientBootstrap` creates and passes to its providers. Each frame in which
the pointer is over the world it builds the menu for the pointer ray:

- `WorldPointerPicker.PickAll` lists every registered target and remote player
  under the cursor within `WorldInteractionRules.ClientPointerRange` (100 m) of
  the camera that no solid collider hides, nearest first and each once: a direct
  ray through the cursor first, then a spherecast of
  `WorldInteractionRules.ClientSpherecastRadius` (0.15 m) when the ray found
  nothing active.
- Each `IWorldMenuProvider` adds `WorldMenuOption` values in `WorldMenuOrder`,
  whichever registered first: `CombatTargetingController` (Combat, first),
  `WorldInteractionTargetingController` (WorldInteraction), and the controller
  itself (WalkAndExamine). A provider may block the whole menu, as combat does
  while the character is dead.
- `WorldMenuBuilder.Finish` sorts the options stably: the target options by
  target, nearest first, then Walk here, then Examine for each target, so
  provider order decides between options of the same target.

A left click runs the top option at once, and a right click copies the options
into the open menu at the pointer. While the menu is open the controller takes no
world action: a press in the world, Escape, or the pointer leaving the menu closes
it, and a press over interface may choose a row. The controller runs before the
features, at execution order -50, so combat, world interaction, and the inventory
panel ignore an Escape whose frame reports `CancelConsumedThisFrame`, the Escape
that closed the menu. `RunDefault` and `OpenMenu` run a left or right click for a
given ray, for tools and harnesses. Options are plain values with their provider,
an action code, the pointer candidate, an entity id, an argument such as a
capability id, and the ray, so building the menu every frame allocates nothing. A
provider runs its own option, checks that the target still exists, because an
open menu can outlive its target, and reports whether it acted. Only after an
option acted does the controller raise `OptionExecuted`; combat clears its target
on every such option except its own and Examine, so a stale option that did
nothing ends nothing either.

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
  target: the player walks to `ApproachRange` (2.5 m), and the controller sends
  the open intent with the chosen capability id once the authoritative owner
  position is within `OpenRange` (2.75 m), inside the worker's 3.0 m start
  range. The walk aims closer than the open check because a route ends within its
  range plus the waypoint arrival tolerance. When the interaction opens,
  `WorldInteractionClientController.ActivateCapability` starts that capability as
  its panel button would: gathering starts after the advisory tool check,
  dialogue, insurance, and quest offers only open, and every other kind executes.
  A rejected open drops the follow-up. When the target's interaction is already
  open, the capability starts without a walk. A newer option or stop cancels the
  approach. A target without a route gives up at once, and an approach that ends
  out of range gives up after 1.5 seconds, both with a game message. Choosing
  another target first closes the active interaction, and the cancel action,
  Escape, closes it.
- `CombatTargetingController` adds Attack for a target that
  `CombatClickRules.CanAttack` accepts, described in
  [Combat Targeting And Approach](#combat-targeting-and-approach).

`TemporaryWorldContextMenu` is the replaceable uGUI over the controller: the hover
hint next to the pointer and the open menu that
[World Menu](GAME_FEATURES.md#world-menu) shows the player. Each option carries a
`WorldMenuTargetStyle`, which `TemporaryWorldContextMenu.NameColor` turns into the
color of the target's name, and the open menu closes when the pointer moves 24
reference pixels outside it. `TemporaryGameMessagePanel` shows the newest
messages of the `GameMessageLog` without taking raycasts and fades each out after
`VisibleSeconds`.

The menu only chooses an intent. SimulationWorker independently owns the start
and maintain ranges, closest-point bounds distance, line of sight, target
revision, capability availability, and session validation; see
[Interaction Targeting And Input](NPC_AND_MOB_SYSTEM_DESIGN.md#interaction-targeting-and-input).

### Mob Movement

A Mob that moves travels in the unreliable `SimulationSnapshot` like a remote
player: an entity with its world actor entity id and a `PlayerState`. The worker
includes it only while its pose changes, walking or turning to face its target,
and sends one reliable `WorldActorState` with the rest pose when it stops. A Mob
that never moves keeps its reliable pose.

`WorldActorPresentationController` subscribes to the snapshots of its realtime
client and hands every entity that names a presented `MobView` to that view
through `RouteSnapshot`. Players, NPCs, resource nodes, and ids without a
presentation, such as a Mob whose prefab still loads, are ignored. Each `MobView`
owns a `RemoteActorMovement` in `Mmorpg.Client.Gameplay`, configured from the
joined `NetworkMovementSession`. It keeps the Mob rules on top of the shared
`RemoteMovementPresenter`, so a Mob uses exactly the remote player
interpolation, render clock, and render delay:

- **Start.** The first snapshot after a rest anchors the reliable pose one render
  delay before it and restarts the render clock at the anchor, so the Mob stands
  where it was and then walks into the snapshot instead of jumping ahead.
- **Settle.** A `WorldActorState` that arrives while snapshots still play becomes
  their final state, one snapshot interval after the newest snapshot, so the Mob
  walks onto its rest pose without a jump. A later snapshot that is not newer than
  that state is ignored. A rest pose equal to the newest snapshot changes nothing.
- **Jump.** A `WorldActorState` without any snapshot, one after the snapshots
  played out at another pose, and one more than
  `RemoteActorMovement.TeleportDistance` (3 m) from the newest state, such as an
  evading Mob that the worker placed home, place the Mob at once. A snapshot that
  far away restarts the interpolation at the snapshot.
- **Restart.** A Mob that settled at its rest pose does no work per frame, and
  its next snapshot starts again from the rest pose.
- **Cleanup.** A hidden Mob, such as a dead one, clears its interpolation and
  ignores snapshots, and it returns at its reliable pose. A despawn destroys the
  view, and a respawn creates a new view without interpolation.

The presentation controller applies every actor again on each actor change, so a
`MobView` moves on a reliable state only when its state revision is newer. The
grounded presentation of a Mob uses the session's character capsule, because the
protocol does not carry a Mob's locomotion capsule; on walkable slopes its slope
support offset therefore differs from the worker's by a few centimetres, within
4 cm for the Feral Alpha on a 30 degree slope. Mob presentation never loads
collision chunks; outside the resident chunks it shows the replicated height
unchanged.

Combat presentation reads the presented `MobView` transform through
`CombatantViewLocator` every frame, so health bars, damage numbers, the target
frame, arrows, and the approach follow a moving Mob.

### Creature Animation

A Mob whose prefab has a `CreatureAnimator` in `Mmorpg.Client.Gameplay`, such as
the Feral Wolf and the Feral Alpha, plays clips of its own. The component
measures the smoothed ground speed of its root from frame to frame, ignoring a
move faster than 20 m/s as a placement, and drives the Animator of the prefab's
`Model` child on `Assets/Art/Animation/WolfLocomotion.controller`, which
`CreaturePresentationBuilder` builds with the names in `CreatureLocomotion`:

- **Idle** below 0.1 m/s.
- **Locomotion**, a 1D blend tree on `Speed` with `Walk` at 1.4 m/s and `Run` at
  4.5 m/s: the speeds the wolf's clips were made for at its own size, which are
  the patrol and chase speeds of its locomotion profile.
  `CreatureLocomotion.Evaluate` sets the blend parameter and the playback rate
  `LocomotionRate` so the blended stride covers the ground the Mob moves, and the
  paws stay planted. A larger model, such as the Feral Alpha at 1.3 times the
  size, takes longer strides and plays its clips slower at the same speed. The
  walk never plays slower than 0.3 times, so a Mob that slows to a stop does not
  move in slow motion.
- **Attack** on the `Attack` trigger, which `CombatProjectilePresentation` sets
  when the Mob's `AttackReleased` event arrives. It plays over whatever the Mob
  does and returns to it at its end. A dead creature does not attack.
- **Death** while `Dead` is set. `CombatDeathPresentation` tells every presented
  Mob with a `CreatureAnimator` its combatant life state each frame, so it falls
  when it dies and holds the last pose; Death returns to Idle if `Dead` clears.

The worker despawns a dead Mob once its corpse exists. When the view of a falling
Mob is destroyed, `WorldActorPresentationController` remembers its archetype,
position, time, and how far into Death it was for 3 s, and the Mob's corpse
within 1.5 m plays on that fall, as described under
[Corpse Client State And Presentation](#corpse-client-state-and-presentation).
The Animator culls its transforms off screen, and `CreatureAnimator.LieDead`
writes the corpse's pose at once anyway.

### Gathering

A resource node's gathering option, such as Mine, also its left-click option,
walks the player into range and opens it like any other target, and
`WorldInteractionClientController` then starts gathering through the chosen
gathering capability. It first checks that an item with a tool kind is equipped
in the Tool slot; otherwise the panel says to equip a gathering tool. The same
option while that node already gathers does nothing. The check is advisory and
passes while the inventory is still loading, because GameService checks the
required tool kind on every yield.

`GatheringClientState` applies the worker's `GatheringState` messages. `Started`
must belong to the open interaction and starts a cycle timer. `Yielded` adds the
gathered quantity and restarts the timer, and every yield calls `EnsureFullState`
with its committed item-state revision, which reloads only when the committed
change did not already patch the inventory. `Stopped` keeps the gathered count
and the worker's stop message. `Yielded` and `Stopped` may still arrive after the
interaction closed, and a `Yielded` even after `Stopped`, because a cycle in
flight still completes; it adds to the gathered count. A message for another
gathering session is ignored, but a yield still refreshes the inventory. A
session end clears the state.

While gathering, the interaction panel draws the cycle progress from the timer,
and the local body holds and swings the tool of its Tool slot, as
[Attachments, Tools, And Weapons](#attachments-tools-and-weapons) describes; other
clients learn it from the player's appearance. The worker owns every cycle and
every stop.

## Combat Client

Combat is server-authoritative and never predicted. `RealtimeSimulationClient`
decodes the four server messages of the Combat family and the Items-family
quantity change in the joined state and raises a typed event for each:
`CombatIntentCompleted`, `CombatantStateReceived`, `CombatEventBatchReceived`,
`OwnerCombatStateReceived`, and `OwnerItemQuantityChanged`. A decode failure fails
the protocol like every other control message, and so does a `CombatantState` for
an entity without reliable presence, because the worker sends it right after
every spawn of a combatant. `TrySendCombatIntent` sends a typed `CombatIntent`,
built with `RealtimeCombatIntent.CreateAttackTarget` or `CreateClearTarget`, on
the reliable ordered control channel. The messages are in
[Combat](REALTIME_PROTOCOL.md#combat), and what the worker decides is in
[Target-Based Combat](SERVICE_FEATURES.md#target-based-combat).

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
  `MaximumPendingIntents` (8). `TryAttackTarget` and `TryClearTarget` check only
  what must fail, such as no joined session, the character itself, or a dead
  character, and the worker validates every rule. A result completes its own
  intent in any order; a result without a pending intent of its kind disconnects
  with `combat_intent_result_mismatch`. The latest result stays readable as
  `LastIntentResult`.

Combat event batches are forwarded through `EventsReceived` in simulation order
for presentation; health always comes from the combatant and owner states. The
state clears on `SessionJoined` and `SessionEnded`, so reconnect cleanup never
compares revisions across sessions.

`Died` fires when an owner state says dead and the previous one did not, or
belonged to an earlier life, so it fires once per death, also when the client
joins while dead. `Respawned` fires when an owner state's life sequence advances
and the life state is alive again, also when the dead state itself was never
received. The worker then has moved the character to the World spawn with full
health. The owner's movement prediction needs no extra reset for that: the
teleport arrives as the next owner snapshot, the worker ignores movement inputs
while the character is dead and halts its path, and a reconciliation of 3 metres
or more is a hard correction that snaps the presentation to the reconciled state
instead of blending. A respawn within 3 metres of the death position blends like
any small correction.

### Combat Targeting And Approach

`CombatTargetingController` is the permanent world menu provider of combat,
created by the bootstrap in every World scene and registered with the
[World Menu](#world-menu) at `WorldMenuOrder.Combat`, before world interaction.
Its decisions live in pure classes:

- `CombatClickRules.Describe` turns each `WorldPointerPicker` candidate into a
  Mob, a player, another target, or nothing, by the authoritative actor kind.
  While the owner state says dead, combat blocks the whole menu, so a click
  neither walks, attacks, nor interacts. Otherwise `CanAttack` decides which
  targets offer `Attack`: a living Mob, and a living player only when the joined
  Shard's `ShardRules.PlayerVersusPlayer` allows it. A Mob that cannot be attacked
  now, such as one walking home, offers no Attack, so a left click on it walks
  there. A player without combat state, such as a development bot, is never a
  target. `AttackableTarget` makes this decision for one candidate, and choosing
  a stored Attack asks it again, so a target that died or left, or a character
  that died, while the menu stayed open sends no attack. Every other option except
  Examine, such as Walk here, Talk-to, or Mine, sends `ClearTarget` when there is
  a target, and so does Escape.
- `CombatTargetSelection` names the target the client acts on. The worker owns
  the target, so it is the owner state's target, except while a request is on its
  way, when it is the requested target. A confirmed request keeps its target until
  a newer owner state arrives, because the result can arrive before the owner
  state that shows it. A rejected request falls back to the owner's target and
  keeps the worker's error for the HUD. A request that its session dropped is
  forgotten.
- `CombatApproachPlanner` walks the character into range through
  `LocalPlayerController.Approach`. The range is the weapon range minus
  `RangeMargin` (0.75 m), at least `MinimumRange` (0.5 m), because the worker
  measures to the target's bounds in three dimensions while the route stops on
  the planar distance. `CombatLoadoutRules.TryGetWeaponRange` reads the weapon
  range from the bundled item catalog for the item in the `main_hand` slot of the
  committed inventory, as the worker resolves the loadout; without a weapon the
  character does not walk, and the worker reports `WeaponMissing`. The first
  approach also stops a walk that is already in range. While walking, a target
  that moved more than `ReplanDistance` (1.5 m) from the planned goal gets a new
  route. While standing, the character walks again when the target stands beyond
  the weapon range or the worker reports `OutOfRange`, a margin closer than its
  measured distance, and it walks `LineOfSightStep` (2 m) closer each time the
  worker reports `LineOfSightBlocked`; a search for a spot with line of sight is
  planned. It never walks while the worker reports `Attacking`, and it starts at
  most one approach per `RetrySeconds` (1 s) by itself. Losing the target stops
  only the planner's own approach, never a walk that a newer option started.

On `Died` the controller drops its requests and stops the navigator, and on
`Respawned` it stops it again, so a path planned before the death never runs from
the spawn. The worker clears the target on death.

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
session's tick rate. The worker keeps sending owner snapshots while the character
is dead, so the clock keeps running for the respawn countdown.
`RoundTripTimeMilliseconds` reads LiteNetLib's round trip time; LiteNetLib's
`Ping` is only half of it.

### Combat Presentation

The HUD and the projectiles are temporary presentation. Every object is created
at runtime, so no scene, prefab, or asset changes are needed. What the player
sees is in [Combat](GAME_FEATURES.md#combat).

`TemporaryCombatHud` in `Mmorpg.Client.Ui` is a runtime uGUI canvas below the
interaction panel and the inventory. Nothing on it takes a raycast, so it never
blocks a world click. It draws:

- The owner frame from `OwnerCombatState`: health, mana, the live arrow count, and
  the attack status as a short label from
  `CombatHudPresentation.AttackStatusLabel`, plus whether the character is in
  combat. The worker's error for a rejected request shows below it for four
  seconds.
- The target frame for the target of `CombatTargetingController`, from its
  combatant state.
- Health bars over every damaged living combatant in view within 60 metres: the
  local player from its owner state, other players and Mobs from their combatant
  states.
- A floating damage number at the hit target for every `Hit` event, rising and
  fading over 1.1 seconds.
- The death overlay while the owner state says dead, with a countdown from
  `RespawnTick` and `CombatServerClock` that rounds up to whole seconds. The
  overlay is a nested canvas that overrides the sorting order with
  `DeathOverlaySortingOrder`, so an open inventory or corpse view never hides it.

`CombatDeathPresentation` is not temporary: it plays the handmade `Death` clip on
the body of every player who dies in view, as described in
[Locomotion](#locomotion), and on every creature with clips of its own.

`CombatProjectilePresentation` in `Mmorpg.Client.Combat` presents every attack
event. For `AttackStarted` the attacker's body plays its weapon's attack clip,
timed by `CombatProjectileRules.SecondsUntilRelease`, the seconds from the event's
batch tick to its release tick, so the clip comes to its release as the batch of
the release arrives. An archer's `Ranged` attack gets a shot: while the drawing
hand holds the string, an arrow lies on it, and once the bow looses it waits there
for its release, briefly. `AttackCancelled` ends the clip and takes the arrow
away. For `AttackReleased` the body looses, a creature with clips of its own
bites, and for an event whose impact tick lies after its batch tick, so not for a
Mob bite, a projectile flies from the attacker to the target: for a `Ranged`
attack the arrow, from the string where it lies or else from the weapon's launch
point, the model `ClientConfig` names for every weapon that shoots arrows, and for
a `Magic` attack a bolt of three emissive spheres that leaves from the weapon's
launch point, such as the staff's crystal, when the thrust of `Cast` carries it
forward. It follows the target and lands at the impact tick by
`CombatServerClock`; a projectile that already landed is not drawn. The component
runs its `LateUpdate` after the bodies posed and drew their strings,
`PresentationExecutionOrder.Projectiles`.

The equipped ammunition slot of `TemporaryInventoryPanel` shows the live arrow
count from `OwnerCombatState` when its `AmmunitionItemInstanceId` names the
equipped stack, and the committed quantity otherwise, through
`CombatLoadoutRules.DisplayedAmmunitionQuantity`. A live count that differs from
the committed quantity always shows, even 0 or 1, and the tooltip says how many
are left. The drawn slot updates in place when that count changes, so a shot
never redraws the panel or cancels a drag.

## Test Architecture

The client has one EditMode test assembly, `Mmorpg.Client.EditModeTests` in
`Assets/Tests/EditMode`, and one PlayMode test assembly,
`Mmorpg.Client.PlayModeTests` in `Assets/Tests/PlayMode`. Their folders and the
namespace rule are in [Folder Layout](#folder-layout).

- **Where a test goes.** A new EditMode test goes into the folder of the area it
  tests, beside the tests of that area. Decisions live in pure classes, such as
  the combat rules, the world menu builder, the target advisors, the inventory
  prediction, and the locomotion rules, so EditMode tests cover them without a
  scene. EditMode tests also check authored assets, such as the player prefabs,
  the input actions, the World scenes, the body prefabs and catalogs, and the
  Addressables settings. PlayMode tests cover what needs the running bootstrap or
  a real body: the bootstrap and its controllers, a World scene loaded through
  Addressables without a joined session, the inventory panel drawn from a
  committed snapshot, the LoginMenu and CharacterSelect screens against an
  unreachable GameService, and real bodies dying, using tools, and attacking.
- **Fixtures.** Tests never depend on the layout of a development World:
  `FixtureCollisionWorld`, `FixtureItemCatalog`, and `FixtureWorldActorContent` in
  `Tests/EditMode/Fixtures` provide test-owned collision, items, and actors; see
  [World Layout Fixtures](../tests/README.md#world-layout-fixtures). Tests that
  check the development Worlds themselves name them.
- **Shared sources.** The files in `Tests/EditMode/Shared` compile into both the
  Unity EditMode tests and the .NET tests, so both runtimes run the same cases.
  `Mmorpg.Shared.Tests` compiles all of them and `Mmorpg.Tools.Tests` the
  protocol goldens; the protocol goldens are described in
  [Realtime Protocol Goldens](../tests/README.md#realtime-protocol-goldens).
- **Movement determinism.** `MovementDeterminismScenario` runs one movement
  scenario in both runtimes and pins `ExpectedSimulationRevision` and
  `ExpectedFingerprint`. An intended change to shared movement or collision
  behavior fails these tests in both the backend suite and Unity EditMode: bump
  `GameSimulationCompatibility.Revision`, then set both values to the new ones,
  because clients and workers with different revisions must not interoperate. The
  planner's `ExpectedPlannerFingerprint` changes without a revision bump, since
  paths travel as data, but both runtimes must still match it. If only Unity
  fails, a new float expression in `GameSimulation` is missing an explicit
  `(float)` cast around an intermediate result;
  `MovementDeterminismScenario.ComputeTrace()` returns the per-tick states for
  comparing the runtimes.
- **Graphics.** A test that draws with a camera needs a graphics device and is
  marked `[Category("Graphics")]`. The headless run leaves that category out; see
  [Traits](../tests/README.md#traits).
- **Batch mode.** CI runs the suites headless, where Unity never evokes
  `WaitForEndOfFrame`, so a test yields `null` instead; `tools/Test-Changes.ps1`
  fails a test that yields `WaitForEndOfFrame`.
- **Architecture guards.** `ClientAssemblyArchitectureTests` pins the assembly
  graph described in [Assemblies](#assemblies). `ClientAddressablesTests`
  compares the committed Addressables settings with `ClientAddressablesLayout`,
  resolves every fixed address, every generated collision manifest and chunk,
  every presentation icon, and every registry prefab, loads content through
  `ClientAssets`, and fails on any `Resources` folder or call in the client and
  shared packages. `RuntimePresentationTests` keeps runtime primitives on the
  configured material, and a test lists the types with static state that must
  reset on play mode entry, see [Extension Rules](#extension-rules).
- **Built bundles.** The PlayMode tests run against the Asset Database by default
  and also pass with the "Use Existing Build" play mode script against built
  bundles.

How to run the suites, in the Editor or headless, is in
[Run The Unity Tests](LOCAL_DEVELOPMENT.md#run-the-unity-tests).

## Extension Rules

- Put client code in the assembly and folder of its area. A new reference between
  client assemblies must point to a lower layer and be added to
  `ClientAssemblyArchitectureTests`.
- Pass services from `ClientBootstrap` through `Initialize`; never add static
  service access or reference `Mmorpg.Client.App` from runtime code.
- Load runtime content only through Addressables, following
  [Working Rules For New Content](#working-rules-for-new-content): refer to fixed
  addresses through `ClientAddresses`, release every handle you own, and never
  add a `Resources` folder.
- Change scenes only through `ClientSceneLoader`. A new World scene needs its
  catalog entry and `MMORPG > Tools > Addressables > Apply Client Layout`.
- Create runtime primitives only through `RuntimePrimitives` with the configured
  material.
- Keep HTTP serialization and failure mapping inside the API layer.
- Keep realtime transport state and packet handling inside
  `RealtimeSimulationClient`, not scene panels.
- Keep deterministic movement rules in `GameSimulation`, not in transport or
  presentation components.
- Wrap every float intermediate in `GameSimulation` in an explicit `(float)` cast.
  Mono keeps float intermediates in double precision, so an expression such as
  `(a * b) + c` without casts predicts differently in Unity than on the worker.
  The movement determinism tests catch a missing cast.
- Keep authored collision in `WorldData`; never duplicate scene geometry as
  hand-maintained server constants.
- Keep prediction and reconciliation separate from remote interpolation.
- Present every remote entity that moves through snapshots with
  `RemoteMovementPresenter`, so players and Mobs share one render clock, delay,
  and grounded presentation.
- Plan routes only through `LocalPlayerNavigator` and the shared planner, and
  offer world actions as `IWorldMenuProvider` options of `WorldMenuController`
  instead of reading the mouse in feature code.
- Register a world menu provider with a `WorldMenuOrder` value and never rely on
  registration order. Read pointer targets from the menu builder, which picks them
  once through `WorldPointerPicker` for every feature.
- Keep persistent cross-scene session state in `ClientSession` and item and corpse
  state in their bootstrap-owned controllers, not scene panels.
- Keep planned actor and world-interaction state in bootstrap-owned controllers,
  not actor prefabs or the temporary interaction panel.
- Keep combat state in `CombatClientController`. Presentation reads its state and
  never decides range, damage, or death.
- Keep combat decisions, such as the Attack rule, target selection, and the
  approach, in the pure classes of `Mmorpg.Client.Combat`, and keep its
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
- Temporary implementation shortcuts are permitted only inside the replaceable UI
  presentation layer. UI-independent state and behavior must not be embedded in
  temporary panels.
- Add player-facing behavior to [Game Features](GAME_FEATURES.md) when it becomes
  implemented, and update this document when client ownership, folders, scene
  flow, networking, input, or persistent runtime structure changes.

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

The maturity level of every client system is listed in
[Engineering Standard](PROJECT_OVERVIEW.md#engineering-standard). The panels
named `Temporary` are stand-ins: a temporary panel only presents state that a
controller owns, so replacing the panel never changes the controller.

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
| `Networking/Channels` | A channel per feature of the realtime client, each an interface with its implementation; see [Realtime Networking Layer](#realtime-networking-layer) |
| `Ui/Kit` | The themed widgets of the permanent UI; see [UI Kit And Theme](#ui-kit-and-theme) |
| `Ui/Inventory` | The inventory windows and the logic of what their actions send; see [Inventory UI](#inventory-ui) |

### Editor

| Folder | Holds |
| --- | --- |
| `Editor` | `ClientAddressablesLayout`, `ItemIconImportRules`, and `WorldCollisionBaker` |
| `Editor/Art` | The import side of the art pipeline: model import rules, appearance catalogs, project materials, topology maps, manifests, and `CreaturePresentationBuilder` |
| `Editor/Cloth` | Cloth measurement, the stress motions, and the cloth fixtures for the art pipeline's checks |
| `Editor/Wardrobe` | The Wardrobe window and its preview |
| `Editor/Build` | The player build: `ClientPlayerBuild`, the build method of `tools/Build-Client.ps1`, `ClientBuildKindSettings`, the player settings of each build kind, and `ClientBuildFile`, which bakes the environment and the build stamp into every player build |
| `Editor/DevelopmentTools` | The Inventory Item Grants window and its process runner, the GameService Address Override window, and the Ignore Bundled Content and Pseudo Locale switches |
| `Editor/DevelopmentWorlds` | The development World builders; see [Development Worlds](DEVELOPMENT_WORLDS.md) |
| `Editor/WorldBuilder` | The [World Builder](DEVELOPMENT_WORLDS.md#world-builder): `WorldBuilder`, which rebuilds and validates a scene, its window, its live rebuild, the rebuild from the saved scene after its changes are thrown away, the shapes it adds, the mesh assets it writes, the shared handles of an outline, and `TerrainCarver`, which carves the water's beds into a terrain and keeps what it changed in a hidden part of the terrain asset |
| `Editor/Water` | `WaterAssets`, which writes the water's textures and materials, and the water of the World Builder: the builders of the lake, river, sea, and waterfall meshes, the beds the water carves, the editors of their shapes, and the water validation |
| `Editor/Ui` | `UiSpriteImportRules` and `UiThemeBuilder`, the import and build side of [UI Art](ART_PIPELINE.md#ui-art) |

The item catalog, Actor Studio, Spawn Authoring, and Quest Studio windows live
in the shared WorldData package at `src/Shared/WorldData/Editor`.

### Tests

| Folder | Holds |
| --- | --- |
| `Tests/EditMode` | The EditMode assembly, with a folder per area: `Art`, `Bodies`, `Cloth`, `Combat`, `Core`, `Items`, `Movement`, `Networking`, `Protocol`, `Quests`, and `WorldActors` |
| `Tests/EditMode/Fixtures` | Test-owned fixtures: a collision world, an item catalog, actor content, the quest catalog, and a copy of the quest content |
| `Tests/Shared` | The assembly `Mmorpg.SharedTests`: test sources that the .NET test projects compile too and that the EditMode and PlayMode tests run, the PlayMode tests also in a built player |
| `Tests/PlayMode` | The PlayMode assembly; `Player` holds the goldens that run in a built player |

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
| `GameService Address Override` | Points Play Mode at another GameService than the local environment's | [Another GameService For Development](LOCAL_DEVELOPMENT.md#another-gameservice-for-development) |
| `Item Catalog` | Edits item definitions and vendor profiles | [Unity Editor Workflow](../content/items/README.md#unity-editor-workflow) |
| `Content > Actor Studio`, `Content > Spawn Authoring` | Edits actors and their spawns | [Unity Content Tools](../content/README.md#unity-content-tools) |
| `Content > Quest Studio` | Edits quests and quest chains | [Quest Studio](../content/README.md#quest-studio) |
| `Art Pipeline > Wardrobe`, `Update Appearance Catalogs`, `Create Missing Materials`, `Build Creature Presentations`, `Measure Cloth`, `Write Cloth Fixtures` | Art pipeline tools | [Art Pipeline](ART_PIPELINE.md) |
| `UI > Build UI Fonts`, `UI > Build UI Theme` | Makes the UI font assets and fills the UI theme from its sprites | [UI Art](ART_PIPELINE.md#ui-art) |
| `Water > Create Water Textures And Materials` | Writes the water's textures and materials | [Water](ART_PIPELINE.md#water) |
| `World Builder` | Adds and shapes lakes, rivers, seas, and waterfalls, lets them shape a terrain, lists their problems, and rebuilds and bakes | [World Builder](DEVELOPMENT_WORLDS.md#world-builder) |

## Assemblies

Runtime code under `Assets/Scripts` is split into one assembly per area. Each
folder holds one asmdef, and every reference between client assemblies points to
a lower layer, so the compiler rejects a cycle or an upward dependency. The
layers from the bottom up:

| Assembly | Owns | Client references |
|---|---|---|
| `Mmorpg.Client.Cloth` | `ClothPose`, the cloth's target pose on plain data, `ClothSprings`, the springs that swing the cloth on it, `ClothWorld` and its Burst jobs, `ClothMath`, and the cloth's data types | none |
| `Mmorpg.Client.Api` | `ApiClient`, HTTP contracts, `ApiError` | none |
| `Mmorpg.Client.Core` | `ClientSession` with `IClientSession` and `ClientSettings`, `ClientEnvironment`, `ClientBuildInfo` with `ClientBuildStamp`, `ClientAddressOverride`, `ClientConfig`, `ClientLog`, `SceneNames`, `ClientAddresses`, `ClientAssets`, `RuntimePrimitives`, `ClientTexts`, which reads the text tables, `ClientText`, a text that rule code names by key, and `ClientContentResolver` with its manifest, cache, and transport, which resolves the content set at start | Api |
| `Mmorpg.Client.Worlds` | World scene catalog, `ClientSceneLoader`, collision loading and authoring, the water: `WaterBody`, `WaterSurfaces`, `WaterWaves`, `WaterShaderGlobals`, and `WaterRendererFeature`, and the World Builder's shapes: `WorldBuilderShape`, `LakeShape`, `RiverShape`, `SeaShape`, `WaterfallShape`, and `WorldBuilderTerrain` | Core |
| `Mmorpg.Client.Networking` | `RealtimeSimulationClient` with its transport `IRealtimeTransport` and `LiteNetLibRealtimeTransport`, and its parts: `RealtimeSession` with `IRealtimeSession`, `RealtimeMessageTable`, `RealtimePresenceRegistry`, a channel per feature such as `IRealtimeCorpseChannel`, `RealtimeDiagnostics` with `IRealtimeDiagnostics`, `RealtimeSender`, and `RealtimeConnection`; `NetworkMovementSession`, `ClientSessionRecovery`, `WorldExit` | Api, Core, Worlds |
| `Mmorpg.Client.Items` | Inventory, corpse, and vendor client state, prediction, and rules, the item catalog, presentation catalog loading | Api, Core, Networking |
| `Mmorpg.Client.Quests` | The quest catalog, the joined character's quest log and its loading, quest progress, the quest dialog's entries, and quest notices, described in [Quest Client](#quest-client) | Api, Core, Networking, Items |
| `Mmorpg.Client.Gameplay` | Local and remote players with `LocalPlayerSource`, orbit camera, input, click-to-move navigation, pointer focus, game messages, prediction, remote player and Mob interpolation, player bodies and the garments their equipment shows, creature animation, `WorldSceneContext` | Cloth, Api, Core, Worlds, Networking |
| `Mmorpg.Client.WorldActors` | Actor and interaction state, the shared pointer picker, the world menu and its providers, interaction targeting, actor and corpse presentation, the water wakes | Api, Core, Worlds, Networking, Items, Gameplay |
| `Mmorpg.Client.Combat` | `CombatClientController` and `CombatClientState`, the combat state mirror, combat targeting and approach, the combatant view locator, death and projectile presentation | Core, Networking, Items, Gameplay, WorldActors |
| `Mmorpg.Client.Ui` | Every panel including the combat HUD, the UI kit and theme, the inventory and quest windows, drag and drop, `ClientOperationState` | Api, Core, Worlds, Networking, Items, Quests, Gameplay, WorldActors, Combat |
| `Mmorpg.Client.App` | `ClientBootstrap`, the composition root | The same as Ui, and Ui |

The client assemblies also reference the shared runtime packages they use:
`Mmorpg.GameProtocol`, `Mmorpg.GameSimulation`, `Mmorpg.WorldData`, and
`Mmorpg.WorldData.Client`, the item presentation catalog. Core and App
reference `Mmorpg.WorldData` for the text catalog, which Core loads and App
passes on, as [Content Text](#content-text) describes. Gameplay references
`Mmorpg.WorldData.Client` so a body reads the garments of the items it wears
without the inventory's assembly. Cloth references the packages `Unity.Burst`,
`Unity.Collections`, and `Unity.Mathematics`; Gameplay references Cloth,
`Unity.Mathematics`, and `Unity.Collections`. Worlds references
`Unity.RenderPipelines.Core.Runtime` and `Unity.RenderPipelines.Universal.Runtime`
for the water's renderer feature. Ui references `Unity.TextMeshPro`,
because every new text is TextMesh Pro, as [Text And Fonts](#text-and-fonts)
describes.

Ui is the top feature area; only the App composition root, which creates the
panels, sits above it. Ui and App reference Combat, because the combat HUD and
the inventory's live arrow count read its state. Each namespace equals its
assembly name. Areas that read `ClientSession` reference Api as well, because
the session caches Api contracts.

The Editor-only `Mmorpg.Client.Editor` assembly references Core, Worlds,
Gameplay, Cloth, and Ui, whose theme the UI builder fills, the shared WorldData editor, the WorldData actor editor,
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

Every project assembly, the client, Editor, and test assemblies as well as the
shared packages, has a `csc.rsp` with `-warnaserror+` next to its asmdef. Unity
applies a response file only to the assembly whose asmdef sits beside it, so a
compiler warning in project code fails compilation in the Editor, in
`tools/Run-UnityEditorTests.ps1`, and in the CI Unity gate, as
`TreatWarningsAsErrors` in `Directory.Build.props` fails the .NET build, while
package code keeps its warnings. A new asmdef needs the same file, and every
script must live in an asmdef, because the predefined `Assembly-CSharp`
assemblies have no response file; `ClientAssemblyArchitectureTests` fails
otherwise.

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
distance). Both assets provide the camera's depth and opaque textures, without
downsampling on PC, because the water shaders read the ground and the color
behind their surfaces, and both renderers carry `WaterRendererFeature`, the only
pass that draws the water surfaces: before the other transparent objects, after
which it copies the camera's color with the water to
`_WaterSceneColorTexture` for the waterfalls; see [Water](ART_PIPELINE.md#water). Every project
material uses a URP shader, one of the project's shaders built on URP Lit, or one
of the project's URP water shaders; the runtime fallback presentation sets both
`_BaseColor` and `_Color`, so it stays correct in any pipeline. There is no
post-processing Volume in the scenes and no baked lighting yet.

The URP global settings reference `Assets/DefaultVolumeProfile.asset` as the
default volume profile, with the neutral values URP creates, so a fresh Library
creates no new profile. A player build rewrites the shader prefiltering data in
`PC_RPAsset` and the runtime settings list of the global settings for its own
use; `tools/Build-Client.ps1` and `tools/Run-UnityTests.ps1` put both back
afterwards, as [Build The Client](LOCAL_DEVELOPMENT.md#build-the-client)
describes.

## Runtime Structure

```text
ClientBootstrap (Mmorpg.Client.App, the composition root, survives scene changes)
  +-- ClientConfig, its Addressables handle held while the bootstrap lives
  +-- ClientNotices, what the next entry screen tells the player
  +-- ClientSession with the ClientSettings of the environment, the build stamp, and the configuration
  |   +-- ClientSessionRecovery (forced sign-out) and WorldExit (every way out of a World)
  |   +-- WorldReconnect (a lost connection) with a WorldJoin on the bootstrap's ApiClient
  +-- LocalPlayerSource, which WorldSceneContext fills
  +-- ApiClient
  +-- RealtimeSimulationClient (owns the transport and the frame)
  |   +-- RealtimeConnection over LiteNetLibRealtimeTransport
  |   +-- RealtimeSession (IRealtimeSession)
  |   |   +-- NetworkMovementSession while a session is joined
  |   +-- RealtimeMessageTable, one entry per server message type
  |   +-- RealtimePresenceRegistry (spawned players, looks, world actors)
  |   +-- channels: movement, players, looks, items, corpses, world actors,
  |   |   world interactions, combat, quests, admin
  |   +-- RealtimeDiagnostics (IRealtimeDiagnostics) and RealtimeSender
  +-- ClientContentReport, the content set that ClientContentResolver resolved at start
  +-- created once the content is resolved:
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
  +-- account-session validation, and the release of a World scene left outside the panels

The active scene while the content check runs
  +-- ContentCheckPanel (one status line on TemporaryEntryScreen, no form)

LoginMenu and CharacterSelect
  +-- LoginMenuPanel or CharacterSelectPanel (code-built uGUI on TemporaryEntryScreen)
      +-- ClientOperationState
      +-- API coroutine flow
      +-- the bootstrap's ClientSession and the exit notice, and for CharacterSelect its
          ClientSessionRecovery, WorldExit, ClientNotices, and a WorldJoin of its own

World scene
  +-- WorldSceneContext (scene-authored composition root of the scene)
  |   +-- LocalPlayer prefab, RemotePlayer prefab, and PlayerSpawn references
  |   +-- runtime LocalPlayer instance after an accepted join, set in LocalPlayerSource
  |   +-- runtime remote player views, each a root of the World scene
  |   +-- PlayerBodyMeshes, PlayerBodyMaterials, PlayerCorpseBodies
  +-- runtime components that ClientBootstrap adds:
      +-- WorldMenuController
      +-- WorldActorPresentationController and CorpsePresentationController
      +-- WaterWakePresentation
      +-- WorldInteractionTargetingController
      +-- CombatTargetingController, CombatProjectilePresentation,
      |   CombatDeathPresentation
      +-- MovementDestinationMarker
      +-- WorldNameplates (runtime uGUI)
      +-- InventoryWindows (runtime uGUI of the UI kit, themed by UiTheme)
      +-- ReconnectNotice (runtime uGUI of the UI kit) over a World whose connection is
      |   restored
      +-- temporary uGUI: TemporaryWorldInteractionPanel, TemporaryWorldContextMenu,
          TemporaryGameMessagePanel, TemporaryCombatHud, TemporaryDebugMenu,
          TemporaryAdminMenu

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
  |   +-- CombatLoadoutRules (weapon range from the item catalog)
  +-- CombatProjectilePresentation (replaceable arrows and bolts)
  +-- CombatDeathPresentation
  +-- WorldNameplates (runtime uGUI nameplates over Mobs)
  |   +-- NameplatePresentation (pure rules, colors, and texts)
  |   +-- NameplateHealthSlide (the shown health and its slide)
  +-- TemporaryCombatHud (replaceable runtime uGUI)
      +-- CombatHudPresentation (pure labels and values)
```

## Persistent Client Bootstrap

`ClientBootstrap` creates itself after the first scene loads, through
`RuntimeInitializeOnLoadMethod` with `AfterSceneLoad`, on an object that
survives every scene change. It owns the `ClientSession`, one
`RealtimeSimulationClient`, one `InventoryClientController`, and the quest,
corpse, world actor, world interaction, and combat controllers, observes scene
transitions, and has `WorldExit` release the World when an active World scene is
left outside the normal panel flow. A configuration without its runtime
primitive material, debug line material, or arrow model logs a warning, because
markers and fallback models then draw with the error shader in a player build,
the Debug menu draws no lines, and arrows fly as plain bars.

The bootstrap starts in two steps, because the controllers need the content
that the client resolves from GameService:

1. `Awake` creates what does not depend on content: the configuration, the
   `ClientSession` with the `ClientSettings` of the environment, the build
   stamp, and the configuration, as
   [Environment And Build Stamp](#environment-and-build-stamp) describes, the materials
   and the arrow model, the UI theme, `ApiClient`, the body tint setting,
   `RealtimeSimulationClient`, which `Initialize` composes with those settings,
   `ClientNotices`, `ClientSessionRecovery`, `WorldExit`, and `WorldReconnect`,
   which runs its attempts as the bootstrap's coroutines and which the
   bootstrap's `Update` ticks. The account-session check starts in `OnEnable`.
2. `Start` runs one coroutine. It shows `ContentCheckPanel` in the active
   scene, the entry screen with the title and the status line
   `ui/login/checking-content` from the bundled `ui` table, and resolves the
   content set as [Content Resolution At Start](#content-resolution-at-start)
   describes. Then it builds the text catalog from the resolved tables, creates
   and initializes every controller and the look link, removes the panel, sets
   `IsContentReady`, and composes the active scene.

No controller and no panel that needs content exists before the check ends. A
scene that loads during the check is composed when the content is ready, and
the services stay passed explicitly; nothing finds content through a static or
a `Find` call. PlayMode tests wait for `IsContentReady` with
`PlayModeClient.WaitForContent` before they load a scene. They reach the running
client's session through `ClientBootstrap.Session`, to sign in and select as the
entry screens do, and `PlayModeClient.UseGameService` points it at another
GameService, such as an unreachable one, through an internal method of
`ClientSession` that only the test assemblies see.

`ClientBootstrap` is the only composition root, and no runtime assembly
references it. It creates the persistent services and passes them explicitly to
every component that needs them; there is no static service access. Scene
controllers that it creates at runtime are added to an inactive GameObject, given
their dependencies through `Initialize`, and then activated, so `Awake` and
`OnEnable` already see them. Tests construct the same components with their own
dependencies.

The local player exists only after `WorldSceneContext` spawned it in `Start`,
after the bootstrap initialized everything else, so nobody can be handed the
player at `Initialize`. The bootstrap hands every component that needs it the
one `LocalPlayerSource` instead, which tells who the local player is right now,
as [Local Player Lifecycle](#local-player-lifecycle) describes. No runtime code
searches the scene for a service or the player, or asks for `Camera.main`, except
in four places that `ClientServiceAccessTests` lists with their reasons: the
bootstrap's guard against a second bootstrap, its `AddSceneComponentIfMissing`,
which keeps a component that a scene already has, `TemporaryUiKit`, which adds
the EventSystem that uGUI needs when the scene has none, and
`ClothChainScheduler`, which serves every body, also in tools and tests without
the bootstrap, and measures from the camera that renders the game.

When a scene loads, the bootstrap composes it. LoginMenu gets `LoginMenuPanel`
and CharacterSelect gets `CharacterSelectPanel`, each with the text of the code
that `ClientNotices` holds, which it takes so the notice shows once, as
[Entry Screen Notices](#entry-screen-notices) describes. In a World scene, the
scene-authored `WorldSceneContext` receives the client session, the realtime
session and its movement, player, look, and admin channels, the local player
source, the pointer focus, and the body settings before `Start` runs, and the
bootstrap adds the runtime components listed in
[Runtime Structure](#runtime-structure). A component the scene already has is
kept.

When a World scene is replaced by another scene while the client session still
has a World, the bootstrap starts `WorldExit.Release` for that World, which
[Client Session State](#client-session-state) describes. The release is bound to
the exact simulation session, so it never clears a newer World, and if the
graceful leave fails after the scene already changed, it closes UDP so
SimulationWorker can run its disconnect cleanup.

## Content Resolution At Start

The client resolves its rules data and texts once at start and runs with them
until it exits; it never swaps content while it runs. The content set is the
item catalog `core`, the quest catalog `core`, and the text tables of the
source locale `en` that `ClientConfig` bundles, by their table id. The item
presentation catalog, icons, models, World scenes, and collision stay in the
build. Which agreements between a client build and the servers this leaves is
in [Client And Server Agreement](PROJECT_ARCHITECTURE.md#client-and-server-agreement).

`ClientContentResolver` in `Mmorpg.Client.Core` does the work in plain C#. It
takes the GameService base URL, an `IClientContentTransport`, a
`ClientContentCache` folder, and one `ClientContentRequest` per file: kind, id,
locale, the format version this build reads, the bundled text, and the
loader's validator. `ClientItemCatalog.ContentRequest`,
`ClientQuestCatalog.ContentRequest`, and `ClientTexts.ContentRequests` build
the requests, and the bootstrap wires the real `UnityWebContentTransport` and
the cache below `Application.persistentDataPath/content`. EditMode tests drive
it with a fake transport and a temporary folder.

1. It fetches `GET /api/content` with a 3 s limit and a 1 MiB cap; a refused
   connection fails at once. The whole check ends within 20 s: a download gets
   at most 10 s and never more than what is left of the 20 s.
2. Every file of the set must be listed in the manifest, in the format version
   this build reads, and the manifest itself in format 1.
3. For each file, in order: the bundled file when its revision is the
   manifest's; otherwise the cached file of that revision; otherwise a download
   from the manifest `url`, which is relative to the GameService base URL when
   it starts with one `/` and otherwise must be an absolute http or https URL.
   A `sizeBytes` or a body above 16 MiB is refused, and the transport stops
   reading at the cap.
4. A cached or downloaded file is used only when its length is the manifest's
   `sizeBytes`, it is UTF-8, the loader's own check accepts it, which computes
   the revision from the content again, and it states the manifest's revision,
   the expected id and locale, and the format version this build reads. A
   cached file that fails is deleted and downloaded once.
5. A downloaded file is cached only after it validated, through a temporary
   file that is then moved to
   `<kind>/<id>/<revision>.json` or `text-table/<locale>/<id>/<revision>.json`.
   After a successful resolution the other revisions and leftover temporary
   files of each file are deleted, so the cache keeps one revision per file. A
   cache that cannot be read or written is never fatal; its problem is logged
   as a warning.

It is all or nothing. When the manifest cannot be fetched or read, does not list
a file of the set, or a download or a check fails, the client runs with its
complete bundled set and logs the reason as a warning with the code
`content_unavailable`; a stale bundled set then meets the
`item_catalog_update_required` and `quest_catalog_update_required` checks after
login. A manifest or a file in a format newer than this build reads gives
`client_update_required`: the client still starts with its bundled set, and the
login screen shows the text of the code above its form without blocking it.

Trust: the manifest comes from the configured GameService like every API
answer, and a file is trusted because its content hashes to the revision the
manifest names. The catalogs feed prediction and UI only; the servers stay the
authority for every rule.

Two development aids show it working:

- The F2 Debug menu's Network tab lists each resolved file with the first 8
  characters of its revision and where it came from, `bundled`, `cache`, or
  `downloaded`, and the fallback code and reason. The log has one line per file.
- `MMORPG > Tools > Ignore Bundled Content` sets the `PlayerPrefs` key
  `ClientContentResolver.IgnoreBundledContentPreference`, read at the next
  start of Play Mode in the Editor and development builds. No bundled file then
  counts as current, so every file comes from the cache or a download; the
  bundled set stays the fallback. In the Editor the bundled files always equal
  the served ones, so without it the download path never runs.

## Configuration

`ClientConfig` is a ScriptableObject at
`Assets/Content/Config/ClientConfig.asset`, defined in `Assets/Scripts/Core`. The
bootstrap loads it through Addressables in `Awake`, synchronously, and keeps its
handle until the bootstrap is destroyed, which also keeps the referenced bundled
content loaded. A missing configuration logs a warning and uses local fallback
endpoints.

| Field | Checked-in value | Used for |
| --- | --- | --- |
| Request timeout | 10 s | Every HTTP request |
| Realtime timeout | 10 s | Realtime join and leave deadlines and the UDP disconnect timeout |
| Session validation interval | 5 s, clamped to 2 to 60 s | The periodic account-session check |
| Reconnect window | 20 s, clamped to 1 to 120 s | How long the client tries to [restore a lost connection](#restoring-a-lost-connection). It mirrors the 20 s that the worker keeps a body; the worker decides how long the body stays |
| Item gameplay catalog | `core.item-catalog.json` of the local WorldData package | The bundled item catalog of the [content set](#content-resolution-at-start) |
| Quest catalog | `core.quest-catalog.json` of the local WorldData package | The bundled quest catalog of the content set |
| Text tables | Every table of `Text/en/` of the local WorldData package | The bundled text tables of the content set |
| Runtime primitive material | `Assets/Art/Materials/M_RuntimePrimitive.mat`, URP Lit | Primitives that runtime presentation creates |
| Debug line material | `Assets/Art/Materials/M_DebugLines.mat`, URP Unlit | The Debug menu's collision and path lines |
| Arrow model | `Assets/Art/Models/Projectiles/BronzeArrow.fbx` | Arrows in flight and on an archer's string |
| Water ring material | `Assets/Art/Water/M_Water_Ring.mat`, URP Particles Unlit | The rings of the [water wakes](#water-wakes) |
| Water spray material | `Assets/Art/Water/M_Water_Spray.mat`, URP Particles Unlit | The spray of the water wakes |

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
duplicated in client configuration. The GameService address is not part of the
configuration either: the environment owns it.

### Environment And Build Stamp

An environment is a checked-in file of `src/Client/Environments`, such as
`local.json`, with its `name` and its `gameServiceBaseUrl`, the one place that
names where a client connects; how to add one is in
[Environments](LOCAL_DEVELOPMENT.md#environments). `ClientEnvironment` reads
and checks it: the name is lowercase letters, digits, and hyphens and equals
the file name, and the address is an absolute http or https URL without user
information, query, or fragment, kept without its trailing slash.

`ClientBuildInfo.Load` gives the bootstrap the environment of the running
client and its `ClientBuildStamp`: the kind, `Editor`, `Development`, or
`Release`, the commit, whether the working tree had changes, and the UTC time
of the build.

- The Editor reads `Environments/local.json` of its project itself, so it
  always runs `local` without a build step, and its stamp is the Editor's.
- A player reads only `Config/client-build.json` of the Client Content bundle:
  the environment and the stamp that its build baked in. `ClientBuildFile`, a
  build processor of the Editor, writes that file into
  `Assets/Content/Config` before the Addressables content build of every player
  build and deletes it afterwards, so it never exists in the project
  otherwise, and Git ignores it. `ClientPlayerBuild` names the environment and
  the stamp that `tools/Build-Client.ps1` asks for; any other build, from
  `File > Build Profiles` or a test player, bakes `local` with the commit of the
  checkout, and its kind follows the build's Development flag. Nothing a player
  does on its machine changes it short of changing the game files.
- A client without a valid environment logs an error and runs with the
  environment `missing`, whose address never resolves, so every request fails
  with an error that names it.

`ClientAddressOverride` decides the development override of the GameService
address once at start: a development player takes `-gameServiceBaseUrl <url>`
from its command line, the Editor takes the PlayerPrefs key that `MMORPG >
Tools > GameService Address Override` sets, and a release build ignores both
and logs that it did. An override that is no absolute http or https URL is
ignored with a warning.

`ClientSettings` carries all of it, read once: `Environment`, `Build`,
`GameServiceOverride`, and `GameServiceBaseUrl`, the override or else the
environment's address, which every request uses. The bootstrap logs the
environment, the stamp, and the address at start. The login screen's footer
shows the environment and the stamp from the `ui` texts `ui/login/build-*`, and
a warning line with `ui/login/address-override` while an override is active,
and logs the same lines as `Login screen shown`; the F2 Debug menu's Network
tab shows them under Build. `ClientEnvironmentTests`, `ClientBuildInfoTests`,
`ClientAddressOverrideTests`, and `ClientBuildKindSettingsTests` cover the
files, the Editor's environment, the baked file, the override rule per kind,
and the build kinds.

### What A Release Build Leaves Out

`ClientBuildStamp.HasDevelopmentTools` is false only in a release build. A
release build:

- creates no F3 Admin menu at all, so it also offers no `Teleport here`;
- ignores the override of the GameService address;
- fills in no development login, and offers neither the switch that records
  the client measurements nor `Ignore Bundled Content`, through
  `Debug.isDebugBuild`, which is false in exactly the release builds.

The F2 Debug menu stays in every build, because Leave Shard lives in it: it is
the only way a player leaves a World without closing the client. That is a
known gap until a game menu takes Leave Shard over.

### Content Text

The client shows the names and texts of items, quests, and world actors from
one `TextCatalog`, never from the gameplay catalogs or the realtime protocol. `ClientTexts.TryLoad` reads
the text tables that the [content check](#content-resolution-at-start)
resolved, checks each against its revision, and joins them; [Text Tables](../content/README.md#text-tables) describes the
tables and their keys. The bootstrap loads the catalog once and passes it to
the inventory, quest, world actor, corpse, and world interaction controllers,
and to the look link, the combat HUD, the login and character panels, and the
F2 Debug menu, which show messages.

- `ClientItemCatalog` answers every name from it: `GetDisplayName`,
  `GetEquipmentSlotName`, `GetCategoryName`, `GetTagName`, `GetVendorName`, and
  `CurrencyName`. An id that the gameplay catalog does not know is shown as it
  is.
- `ClientQuestCatalog` owns its copy of the compiled quest catalog, which
  carries no text. Once the copy validates, it fills every title, offer,
  progress, and completion text, objective text, and line in it from the text
  catalog, so the quest windows and notices read a quest definition whole.
- `WorldActorClientEntry` names an actor when it spawns: its `DisplayName`
  and `ExamineText` by the actor definition id of the spawn, and
  `MenuOptionLabel` and `CapabilityName` by that id and a capability id.
  `WorldInteractionClientState` keeps the actor of the open interaction as
  `ActiveTarget`, so the panels ask it for `ActiveDisplayName` and
  `CapabilityName`. An id from the network that cannot be part of a key is
  shown as it is.
- `CorpseSourceText.Name` names who left a corpse: the character name that a
  character's corpse carries, or the Mob's name by the actor definition that a
  Mob's corpse carries. `CorpseClientState` names the corpses near the player
  and the open corpse view with it, and `ClientQuestCatalog.ActorName` names
  the Mob of a quest drop message.
- `ClientMessages.Text` gives the text for a code: one that a server sent
  with an error, a closure, or the notice of a committed action, or one that
  the client gave a failure it found itself, as
  [Messages](../content/README.md#messages) describes. It fills the
  placeholders of the text with the named values of the message, writes the
  English diagnostic to the log, and shows the unlisted text with the code for
  a code that has no text. The inventory, corpse, quest, world interaction,
  and gathering states store that text, never a diagnostic, and a request
  that GameService refused shows the text of the code in its problem details.
- A failure that the client finds itself follows the same rule: it has a
  stable code and an English diagnostic, the player reads the text of the
  code, and the diagnostic goes to the log and never to the screen. A request
  that got no usable answer has the code `request_timeout`, `network_error`,
  or `invalid_response`, or `invalid_inventory_response` and
  `quest_log_invalid` for a snapshot or quest log that cannot be read; a
  realtime failure carries its code in `RealtimeClientError`; a World that the
  World scene catalog cannot resolve is `world_scene_unavailable`, and a World
  scene that fails to load is `world_scene_load_failed`; a catalog that cannot
  load is `item_catalog_invalid` or `quest_catalog_invalid`, an outdated one
  `item_catalog_update_required` or `quest_catalog_update_required`, whose
  texts ask the player to restart the client, and served content in a newer
  format than the build reads is `client_update_required`.
  `ClientMessages.Text(texts, ApiError)` and
  `AccountEntryPresentation.ErrorText` resolve every kind of failure by its
  code, except that the login and character screens name the GameService
  address for a request that never reached it. The creators of
  `InventoryClientError`, `CorpseClientError`, and `QuestClientState.SetFailed`
  pass the text of the code; an error without a text shows the text of its
  code, and `InventoryClientError.ToDisplayMessage` adds the correlation id of
  a failed request with the client text `ui/inventory/error-correlation`.
  `ApiError.ToDiagnostic` and `RealtimeClientError.ToDiagnostic` build log
  lines only. `ClientFailureTextTests` and `AccountEntryTests` pin the rule.
- A text that the catalog lacks shows its key in brackets, such as
  `[item/weapon.iron_sword/name]`, so a missing text is seen on screen. When
  the tables cannot be read at all, the client logs an error and runs with an
  empty catalog.

`ClientTextsTests` fails when the configuration does not list every table of
`TextTableIds.All`, so a new table cannot be forgotten.

The client's own labels and sentences come from the `ui` table, as
[Client Texts](../content/README.md#client-texts) describes. A window gets the
text catalog from the bootstrap and asks it for a text by key, with
`TextCatalog.Get`, or with `TextCatalog.Format` and named `MessageArgument`
values for a text with placeholders. Rule code that explains itself to the
player, such as `QuestRewardFit`, reads the texts of the catalog it already
takes: `ClientItemCatalog.Texts` and `ClientQuestCatalog.Texts`.

The item rules return why a move, a loot, or a trade is refused as a
`ClientText`, never as a sentence: `ClientText.Of` with a key such as
`ui/items/inventory-full` and named `ClientTextArgument` values, a plain text,
a number, a text key such as an equipment slot or currency name, or a nested
`ClientText` for a reason inside a sentence. `ClientText.Literal` wraps a
sentence that is finished already, such as the error of a controller.
`InventoryTargetAdvisor`, `CorpseTargetAdvisor`, the predictions,
`VendorClientRules`, `InventoryQuickMoves`, and `InventoryDropPlanner` pass the
reason on, and a test compares keys; `VendorClientRules.FormatAmount` returns
an amount of the currency as a `ClientText` too. Only the code that shows it
calls `Resolve`: the inventory windows for a drop, right-click, or trade they
refuse, and the inventory and corpse controllers and `InventoryClientState`,
which turn the reason into the error text of an operation they do not send or a
prediction they undo. The controllers write their own errors from the table
too, with the text catalog they hold, so an error leaves them as the text the
player reads.

The inventory windows take the text catalog from the bootstrap in
`InventoryWindows.Initialize` and hand it to the Equipment, Inventory, and
storage windows, `InventorySlotPresentation`, and `UiDialog`. A window joins
texts only with a text that names both parts, such as
`{place} slot {number}` or `{first} or {second}` for a list of slots; rich text
tags, line breaks between tooltip lines, and numbers stay in the code. An item
operation that was sent says nothing, so `InventoryInteractionResult` carries a
text only for an operation that was not sent.

The quest windows, the quest tracker, the quest messages, the item rule
reasons, the inventory, equipment, storage, Bank, Recovery Storage, corpse, and
vendor windows, the errors of the item, corpse, and world interaction
controllers, the login and character selection panels with
`AccountEntryPresentation` and their notices, the reconnect notice over the
World, the status texts of the combat HUD from
`CombatHudPresentation`, and the world menu with its Walk here, Examine,
Attack, and Loot options, corpse names, and game messages are converted.
`WorldMenuController`, `WorldInteractionTargetingController`, and
`CombatTargetingController` take the text catalog from the bootstrap in
`Initialize`, and `ItemSlotView` and `ItemSlotGrid` take it from the inventory
windows. The literals inside the `Temporary` panels and the F2 Debug and F3
Admin menus stay English by decision. A technical failure that the client finds
itself shows the text of its code, as [Content Text](#content-text) describes.
`ClientTexts.PseudoLocalePreference` makes the bootstrap load the
pseudo-locale, which `PseudoText` builds from the resolved tables.

## Client Session State

`ClientSession`, in `Mmorpg.Client.Core`, is the client's in-memory view of its
account session: a cache, not an authority. `ClientBootstrap` creates one in
`Awake` and passes it through `Initialize` to every component that needs it.
Its facts fall into four groups by who may change them, and each fact changes
only through a transition with one owner:

| Facts | Members | Transitions and their owners |
| --- | --- | --- |
| Client settings | `Settings`, a `ClientSettings` with the environment, the build stamp, the GameService base URL and its override, the request and realtime timeouts, the interval of the account-session check, and the reconnect window | None: the bootstrap reads them once at start, as [Environment And Build Stamp](#environment-and-build-stamp) describes |
| Account sign-in | `Auth`, `SessionToken`, `IsAuthenticated` | `SignIn`, by `LoginMenuPanel` after a login or registration. `SignOut`, by `CharacterSelectPanel` on Log Out and when it finds no account session, and by `ClientSessionRecovery` |
| Selection | `SelectedCharacter`, `SelectedShard` | `Select`, by `CharacterSelectPanel` whenever its state changes, and with the shard of the join ticket |
| World | `ActiveSimulationSession`, the simulation session of the World the client is in | `EnterWorld`, by `CharacterSelectPanel` after the worker accepted the join, and by `WorldReconnect` after a join restored a lost connection. `LeaveWorld`, by `WorldExit` |

Each transition raises its one event when it changed something: `SignedIn`,
`SignedOut`, `SelectionChanged`, `WorldEntered`, and `WorldLeft`. `SignIn`
also empties the selection and the World, and `SignOut` empties everything, so
a reader of the selection or the World listens to those two as well. Readers
take `IClientSession`, which has the facts and the events but no transition. The
inventory and quest controllers listen to the sign-in, sign-out, and selection
events: a new account or character empties their state, and a session that is
already joined then reads the inventory and the quest log at once, as a join
does. `EnterWorld` replaces the session of a World the client is in, so a join
after a lost connection can keep the World.

The World and the realtime connection are two facts with one owner each.
`IRealtimeSession.JoinedSession` is the session that the worker accepted, while
the connection is joined or leaving, and it is gone at every end of the
connection; what sends on the connection, such as a world interaction, reads
it. `ActiveSimulationSession` is the World the client is in; it outlives the
connection until a way out of the World leaves it, and that way out names it by
its simulation session.

`WorldExit`, in `Mmorpg.Client.Networking`, owns every way out of a World but a
forced sign-out. Each leaves the client session's World by the exact simulation
session id, so an exit that completes late never clears a newer World:

| Way out | Started by | What happens |
| --- | --- | --- |
| Leave Shard | The Debug menu | `Leave`: an exact-session UDP leave. A refused leave that keeps the connection joined stays in the World; otherwise the World is left and CharacterSelect loads. An accepted leave keeps its `BodySeconds` in `ClientNotices` for the countdown on CharacterSelect. While a lost connection is restored the connection is not joined to the World's session, and a leave would name that session on another connection, so nothing is sent: a join that the reconnect runs is aborted, the World is left, and CharacterSelect loads |
| A World scene left outside the panel flow | `ClientBootstrap`, when another scene replaces a World scene while the World is set | `Release`: a graceful leave while the connection is joined to that session, UDP closed when it fails, so the worker runs its disconnect cleanup, and then the World of that session is left. A connection joined to another session stays. An accepted leave keeps its `BodySeconds` as `Leave` does |
| A joined World whose scene fails to load | `CharacterSelectPanel` | `Release`, as above, and CharacterSelect stays |
| A World scene that starts without a joined connection | The Debug menu's session check | `LeaveUnjoinedWorld`: the World is left and CharacterSelect loads |
| A connection that ended in a way that does not reconnect | `WorldReconnect`, which listens to `IRealtimeSession.UnexpectedlyDisconnected`, decides by `ReconnectRules`, and hands such an end to `LeaveEndedWorld` | The World is left and the code is posted as the notice; a World scene goes to CharacterSelect, or to LoginMenu without an account session. `account_session_replaced` goes to `ClientSessionRecovery` instead |
| A lost connection that could not be restored | `WorldReconnect`, when its window ends or an attempt is refused for good | `LeaveLostWorld`: the World of the lost session is left, the notice is `reconnect_failed`, and a World scene goes to CharacterSelect |

A forced sign-out, after a 401 from GameService or `account_session_replaced`
from the worker, goes through `ClientSessionRecovery`, as
[Unauthorized Recovery](#unauthorized-recovery) describes. The persistent
bootstrap validates an authenticated account session at the configured
interval, also outside the active World scene.

### Restoring A Lost Connection

`WorldReconnect`, in `Mmorpg.Client.Networking`, is the one owner of a lost
connection. `ClientBootstrap` creates it in `Awake`, ticks it in `Update`, and
runs its attempts as the bootstrap's coroutines; tests pass their own clock and
runner. It listens to `IRealtimeSession.UnexpectedlyDisconnected` and decides
by `ReconnectRules`, the one place that names which endings reconnect:

| Ending | Codes | Reconnects | Where the player lands, with which text |
| --- | --- | --- | --- |
| The line dropped, which the server did not ask for | `connection_lost` (the transport timed out or closed), `udp_socket_error`, and `realtime_timeout`, of the network and timeout kinds that only the transport and the clock produce | Yes | The World stays; on giving up, CharacterSelect with `reconnect_failed` |
| A login of the account on another client | `account_session_replaced` | No | LoginMenu with its text |
| Anything else that the worker decided | Every other `ServerDisconnect` code, such as `session_reconnected`, `session_revoked`, `session_expired`, `udp_rate_limited`, `invalid_packet`, and `internal_error`; a server that sends a code of a dropped line decided too | No | CharacterSelect, or LoginMenu without an account session, with the text of the code |
| A protocol or state failure that the client found | Every protocol code, such as `invalid_join_response`, `unexpected_message`, `placement_mismatch`, and `collision_stream_failed` from `DisconnectForClientFailure` | No | The same, mostly with the text for a code without one of its own |

A leave and an abort raise no `UnexpectedlyDisconnected`, so they never
reconnect. `ReconnectRulesTests` names every code that the client makes with a
kind of its own and fails for a new one without a row, fails for a network or
timeout code that is neither a dropped line nor a failed connect, and checks
every protocol code of the client and every disconnect code of the worker
that it reads from the sources.

An ending that does not reconnect, and a dropped line while no World scene is
shown or without an account session, goes to `WorldExit.LeaveEndedWorld`. A
dropped line in a shown World starts a window of `ReconnectWindowSeconds` from
the loss, and the reconnect moves through explicit states:

```text
Idle -> Pausing -> RequestingTicket -> Joining -> Idle
           ^                                |
           +---------- a failed attempt ----+
```

- **Pausing.** The first attempt starts on the next tick; every later one 1 s
  (`PauseSeconds`) after the last failed.
- **RequestingTicket.** One attempt at a time asks GameService for a join
  ticket for the lost session's character and shard, through `WorldJoin` on the
  bootstrap's `ApiClient`.
- **Joining.** A ticket for the lost World joins the worker with
  `RealtimeSession.Join`. A ticket of another World is refused for good.
- **Success.** The join enters the World of the new session with `EnterWorld`,
  so the World scene goes on, as
  [Local Player Lifecycle](#local-player-lifecycle) describes. The client does
  not tell a resumed body from a new entity beyond `isReconnect`.
- **Failure.** `ReconnectRules.ClassifyAttempt` decides. A dropped line, a
  connection that did not open or closed before its join completed, a worker
  that could not reach GameService (`auth_service_timeout`,
  `auth_service_unavailable`), a request that got no usable answer, a 5xx, and
  a shard or session worker that is not available right now
  (`shard_unavailable`, `simulation_session_worker_unavailable`) pause and try
  again. A 401 signs out through `ClientSessionRecovery`. Every other answer is
  a definite refusal and gives up at once.
- **Giving up.** At the end of the window, also during a join, which is
  aborted, and after a definite refusal, the window ends and
  `WorldExit.LeaveLostWorld` leaves the lost World. A join that the worker
  accepted before the deadline completes.

An attempt that is over never acts again: every attempt has a number, and the
window's end, a success, and leaving the World raise the current number, so a
late ticket of an earlier attempt starts no join and completes nothing.
Leaving the World or the account in any other way, through `WorldExit`, the
recovery, or a sign-in, stops the window and aborts a join in progress. A loss
right after a success starts a window of its own.

While the window runs, the World scene stays loaded. `WorldSceneContext`
stops the local player's movement at `SessionEnded`, so input neither moves it
nor sends, and the camera still turns. Every channel refuses to send outside a
joined session, and the inventory, corpse, quest, world interaction, and combat
controllers reset their session state at `SessionEnded` as at any end; they
read again when their `Update` sees the session joined. The look link keeps the
local look until the client leaves its World or its account, so the body keeps
its look. `ReconnectNotice` shows the window's seconds over the World.

### Entry Screen Notices

`ClientNotices`, in `Mmorpg.Client.Core`, holds what the next entry screen
tells the player, so it outlives the scene change. `WorldExit` posts the code
of a World it leaves for an ending, or `reconnect_failed`, and
`ClientSessionRecovery` posts the code of the 401 or of
`account_session_replaced`. The bootstrap takes the code when it composes
LoginMenu or CharacterSelect and passes the panel its text from the messages
table, the same way it passes the login card the notice that the client is
out of date, so a notice shows once.

An accepted leave keeps the character and its `BodySeconds` there too.
`ClientNotices.TryGetBody` gives the whole seconds left, rounded up, against
the client's realtime clock, and forgets the body when they are up. The count
is a lower bound, since hits extend a body on the worker without telling the
client, so the text goes at zero and nothing more is assumed; it never blocks
`Enter World`. A lost connection gets no number and no countdown.

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

`ApiError` carries the failure kind, HTTP status, stable code, message, and
correlation id. The code is the server's, or the client's own for a request
that got no usable answer; the message is the diagnostic for the log, and
panels display the text of the code. Array
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

### Client Measurements

`ClientMeasurements`, in `Gameplay/Diagnostics`, is the one recorder of what the
client measures about itself. The composition root creates it in `Awake` with
the hub's `IRealtimeDiagnostics` and movement channel and the
`LocalPlayerSource`, adds `ClientMeasurementDriver`, which hands it every frame
in `LateUpdate` at `PresentationExecutionOrder.Measurements`, after every
presentation moved, and gives it the remote presentations of each World scene
it composes, `WorldSceneContext` for the other players and
`WorldActorPresentationController` for the Mobs, through `IRemoteMotionSource`.
Nothing searches the scene for it.

It always keeps the numbers that the Debug menu shows, and the menu reads them
through `DebugMenuReadout` and computes none of its own: the frame times of
`ClientTelemetryWindow`, the round trip, the age, rate, and estimated loss of
the owner snapshots, the packet and byte rates, and the local player's
corrections, their count, the hard snaps of 3 m and more, and the latest
distance.

On request it records what a network test needs, and nothing of it is measured
or costs time without a recording:

| Value | How |
| --- | --- |
| Frame times and the round trip | Every frame as distributions, the round trip only while the local player is in the World |
| Time in the World | The seconds the local player was in the World, which the rates per minute count, so time at the entry screens dilutes none |
| Owner snapshots | The frames received and missing, the largest age, and the silences: a newest snapshot older than 1 s that a new one ends, with their longest |
| Corrections | Every correction of the prediction, which `LocalPlayerController.PositionCorrected` reports, as a distribution of meters, and the hard snaps per minute |
| Rubber banding | `RenderedMotionTracker` reads where the local player is drawn, its transform after the presentation, and counts the part of a frame's horizontal movement that the walk speed does not explain; a frame without a bound player or with another player starts over, so a join or a reconnect counts no jump. The largest jump, the frames with a jump of 0.25 m or more, and the total |
| After a silence | The largest correction and jump in the 2 s after a silence ended, such as an outage of the line |
| Remote entities | Every frame each source reports, for the other players and the Mobs that snapshots move, whether `RemoteMovementPresenter.IsStarved`: the render clock reached the newest state while that state still moves, so the entity stands where the next snapshot should have moved it on. The entity-seconds seen and starved, the starvations, and the longest |
| Reliable intents | The intents sent and the seconds to their results, over all and per kind of `RealtimeIntentKinds`. A recording turns `IRealtimeDiagnostics.IntentTimingEnabled` on, the channels report each intent's operation id through `RealtimeSender` when it leaves and when its result arrives, and the timing is off again when the recording ends |

`StartRecording(label)` opens
`<persistentDataPath>/measurements/<yyyyMMdd-HHmmss>-<label>.jsonl`, and
`StopRecording()` writes the last interval and the summary and returns the
summary. The file holds one `ClientMeasurementRecord` as JSON per interval of
five seconds, `kind` `interval`, and the summary of the whole recording, `kind`
`summary`, as its last line. Tests call the methods; in the Editor and
development builds the Debug menu's Network tab has the switch `Record
measurements to a file`, which shows the file and what the running recording
measured so far. `ClientMeasurementsTests` covers the statistics, the jump
detector on constructed tracks, a recording that writes only on request, and the
Debug menu's numbers. The network part of the live stack test shows on the real
client that the detector reads what is drawn: a development teleport of 8.5 m,
which the presentation shows as a hard snap, must come out as a jump of the
same size in one frame.

## Realtime Networking Layer

`RealtimeSimulationClient` is the client's one network entry point: a
persistent MonoBehaviour owned by the bootstrap that owns the transport and the
frame. It composes the parts below, all in `Mmorpg.Client.Networking`, polls the
transport once per `Update`, and exposes the parts. A feature never takes the
hub: `ClientBootstrap` hands each feature controller the narrow interfaces it
needs through `Initialize`, usually its channel and `IRealtimeSession`.

The hub reaches the network only through `IRealtimeTransport`: start and stop,
connect, send bytes on a channel with a delivery method, poll, wake the send
thread, the round trip time, and the four events it handles: connected,
disconnected with the reason, received bytes with their channel and delivery,
and socket error. `LiteNetLibRealtimeTransport` implements it with one
LiteNetLib `NetManager` on the protocol's channels with synchronized events, so
every event fires inside the poll that the hub's `Update` runs on Unity's main
thread, and it returns each packet's reader to LiteNetLib's pool once the hub
handled the packet. The transport only moves bytes. `ClientBootstrap` composes
the hub's parts with `Initialize` and the client settings, on the LiteNetLib
transport and Unity's realtime clock; the session takes its join and leave
deadlines and the transport's disconnect timeout from the settings' realtime
timeout. EditMode tests and the live stack test pass their own transport and
clock to the internal `Initialize`. A part read before `Initialize` throws.

| Part | Owns | Features read it through |
| --- | --- | --- |
| `RealtimeSession` | The connection state machine: join, leave, abort, their deadlines, every way a session ends and the events that announce it, the validation of a join acceptance, the joined session, the Shard rules, and the movement session. It handles the five session messages: `JoinAccepted`, `JoinRejected`, `LeaveAccepted`, `LeaveRejected`, and `ServerDisconnect` | `IRealtimeSession`: `State`, `IsJoined`, `JoinedSession`, `ShardRules`, `SessionJoined`, `SessionEnded`, `UnexpectedlyDisconnected`, and the two ways a feature ends a session, `DisconnectForClientFailure` and `Abort`. `WorldJoin` takes `RealtimeSession` itself for `Join`, for CharacterSelect and the reconnect, and `WorldExit` for `Leave` |
| `RealtimeMessageTable` | One entry per server message type: its handler, the states it runs in, the states it is dropped in, and the failure code of every other state | The hub only |
| `RealtimePresenceRegistry` | The spawned players with the latest look of each and of the controlled player, the spawned world actors with their newest state revision, the checks between them, the copies handed to late readers, and the clearing at the end of a session with its despawn events | The player, look, world actor, and combat channels |
| A channel per feature in `Networking/Channels` | The handlers of the feature's message types: decode, checks, and its events, and the feature's send | Its interface, in the table below |
| `RealtimeDiagnostics` | The packet and byte counters, the owner snapshot sequence and its loss estimate, the newest server tick, the snapshot age, the round trip time, the endpoint, and, while a measurement asks for it, the timing of reliable intents to their results | `IRealtimeDiagnostics`, which the [client measurements](#client-measurements) and `CombatServerClock` read |
| `RealtimeSender` | The one send path: a joined session with a connected server, the intent's cached encoder, and reliable ordered delivery on the control channel; movement input has its own channel and wakes the transport | The channels |
| `RealtimeConnection` | The transport and what is known of its one connection: whether it runs, whether the server connected, and the endpoint | The session, the sender, and the diagnostics |

| Channel interface | Server messages | Events and reads | Send |
| --- | --- | --- | --- |
| `IRealtimeMovementChannel` | `SimulationSnapshot`, `OwnerSimulationSnapshot`, `CarryStateChanged` | `SimulationSnapshotReceived`, `OwnerSimulationSnapshotReceived`, `CarryStateChanged`, `MovementSession` | `TrySendMovementInputs` |
| `IRealtimePlayerChannel` | `EntitySpawn`, `EntityDespawn` | `EntitySpawned`, `EntityDespawned`, `SpawnedEntities`, `SpawnedEntityCount` | none |
| `IRealtimeLookChannel` | `EntityAppearanceChanged`, `AppearanceIntentResult` | `EntityAppearanceChanged`, `AppearanceIntentCompleted`, `TryGetAppearance` | `TrySendAppearanceIntent` |
| `IRealtimeItemChannel` | `ItemOperationResult`, `OwnerItemQuantityChanged`, `OwnerItemStateChanged` | `ItemOperationCompleted`, `OwnerItemQuantityChanged`, `OwnerItemStateChanged`, `CarryItemStateRevision` | `TrySendItemOperation` |
| `IRealtimeCorpseChannel` | `CorpsePresenceSnapshotChunk`, `CorpseInteractionResult`, `CorpseViewStateChunk`, `CorpseViewClosed` | `CorpsePresenceChunkReceived`, `CorpseInteractionCompleted`, `CorpseViewStateChunkReceived`, `CorpseViewClosed` | `TrySendCorpseInteraction` |
| `IRealtimeWorldActorChannel` | `WorldActorSpawn`, `WorldActorState`, `WorldActorDespawn` | `WorldActorSpawned`, `WorldActorStateReceived`, `WorldActorDespawned`, `SpawnedWorldActors` | none |
| `IRealtimeWorldInteractionChannel` | `WorldInteractionOpened`, `WorldInteractionResult`, `WorldInteractionClosed`, `GatheringState` | `WorldInteractionOpened`, `WorldInteractionCompleted`, `WorldInteractionClosed`, `GatheringStateReceived` | `TrySendWorldInteraction` |
| `IRealtimeCombatChannel` | `CombatIntentResult`, `CombatantState`, `CombatEventBatch`, `OwnerCombatState` | `CombatIntentCompleted`, `CombatantStateReceived`, `CombatEventBatchReceived`, `OwnerCombatStateReceived` | `TrySendCombatIntent` |
| `IRealtimeQuestChannel` | `QuestIntentResult`, `QuestLogChanged`, `QuestDropInCorpse` | `QuestIntentCompleted`, `QuestLogChanged`, `QuestDropInCorpse` | `TrySendQuestIntent` |
| `IRealtimeAdminChannel` | `AdminIntentResult`, `AdminStateChanged` | `AdminIntentCompleted`, `AdminStateChanged`, `AdminState` | `TrySendAdminIntent` |

The session owns an explicit connection state:

```text
Disconnected -> Connecting -> Joining -> Joined -> Leaving -> Disconnected
```

Join and leave are coroutine operations with configured deadlines, structured
errors, and one completion path. A join consumes the complete `JoinShardResponse`,
checks ticket expiry, protocol version, simulation revision, shard identity,
World identity, and exact worker runtime placement, then connects to the provided
UDP endpoint. It sends the short-lived join ticket in a versioned reliable ordered
packet. Join acceptance must match the placed character, shard, World, simulation
revision, and collision revision before its non-secret metadata becomes the
joined session, which `CharacterSelectPanel`, or the reconnect, then enters as
the World of `ClientSession`.

`SessionJoined` is raised when the worker accepts a join, before any later
message of that session is handled; the combat controller clears its state
there, because combat revisions start again in every session. The accepted join
also carries the Shard rules, which the session exposes as `ShardRules` while it
is joined, such as whether player versus player attacks are allowed. Join
rejection stays in CharacterSelect and displays the structured server code.
Protocol decoding rejects wrong versions, invalid types, oversized values, and
trailing data.

### One Frame

One `Update` polls the transport. Every connection event and packet of the poll
is handled in the order it arrived, and every handler raises its events at once,
so the events of a poll keep the order of their packets across channels:

1. A connection event goes to the session.
2. A packet is counted by the diagnostics and dropped when the session already
   ended in this poll, so the first failure reason is the one shown to the
   player.
3. The table reads the message type and checks the channel and delivery that the
   generated descriptor names, before anything else, so even a message that the
   state would drop fails on the wrong channel. A snapshot needs unreliable
   delivery on its descriptor's channel, every other message reliable ordered
   delivery on channel 0.
4. The table's entry decides by the state: the handler runs, the message is
   dropped, or it fails the session with the entry's code. A client message, or a
   type without an entry, is `unexpected_message`.
5. The handler of the message's channel decodes it, runs its checks, changes the
   state it owns, and raises its events. A message that carries a carry state
   advances the movement session first, and raises `CarryStateChanged` before its
   own event.

After the poll the session fails a join or leave whose deadline passed, and then
finishes a session that ended since the last frame: it stops the transport and
raises `UnexpectedlyDisconnected` for an unexpected end, then `SessionEnded`.

### How A Session Ends

A session ends through a lost connection, a socket error, a deadline, a protocol
failure, a client failure that a feature reports with
`DisconnectForClientFailure`, a `ServerDisconnect`, an accepted leave, or an
`Abort`. Every ending clears the presence, despawning every player and then
every world actor with the ending's code, at a point that each ending pins: a
lost connection and `Abort` clear it after the state changed, so a despawn
listener sees `Disconnected` without a movement session; a protocol failure, a
client failure, and a server disconnect clear it before, in `Joined` with the
movement session; an accepted leave clears it in `Leaving` with the movement
session. The registries are empty when `SessionEnded` fires, and nothing is
replayed.

The session raises `SessionEnded` once whenever a joined session ends for any
reason, including a graceful leave, `Abort`, or a failure while leaving, with a
null error for a leave and an abort. The inventory, quest, corpse, world actor,
world interaction, and combat controllers reset their session-scoped state on
that event, so a pending operation, open view, or corpse presence sequence never
blocks the next join. An end while joined that is no leave and no abort raises
`UnexpectedlyDisconnected` first; `WorldReconnect` then restores a dropped line
or hands the end to `WorldExit`, as
[Restoring A Lost Connection](#restoring-a-lost-connection) describes. An ending
that a poll finds raises its events at the end of the same `Update`, after the
transport stopped; a `DisconnectForClientFailure` outside `Update` raises them
at the end of the next frame; `Abort` raises `SessionEnded` at once. Two rules
hold whatever is called after an ending:

- **No ending loses its events.** A session that was joined ends with exactly
  one `SessionEnded`, and an unexpected end raises exactly one
  `UnexpectedlyDisconnected` before it. An `Abort` while an ended session still
  owes its events raises them at once and adds no `SessionEnded` of its own.
- **A join right after a failure works.** A join first finishes the pending end
  of the previous session: it stops the transport and raises the owed events in
  order, and then starts. A listener that joins again from
  `UnexpectedlyDisconnected`, as a reconnect does, sees `SessionEnded` of the
  lost session before the new transport starts.

A leave rejection keeps the session joined, without what the `Leaving` state
dropped.

If another client logs into the same account, SimulationWorker can send
`account_session_replaced` over the reliable control path. The session ends
and stops its transport, and `WorldExit` hands the end to
`ClientSessionRecovery`, which signs the client session out, logs an `[AUTH]`
error, and loads LoginMenu, whose card says why. The periodic GameService
validation provides the same recovery when the displaced client is not
connected to a shard.

### Adding A Message Type Or A Channel

A new server message type costs its generated protocol code, one handler in the
channel of its feature, and one table entry, which the channel registers in its
constructor with `RealtimeMessageTable.Register`: the handler, the states it runs
in, the states it is dropped in, and its `unexpected_` code and message. The hub
seals the table when it composes, and a server message type without an entry
fails that composition, so every hub test fails until the type has one;
`EveryMessageTypeHasACase` then asks for its dispatch case.

A new feature channel is an internal class with a public interface in
`Networking/Channels`, in the style of the existing ones. `RealtimeSimulationClient`
creates it in `Compose` with the session, the table, and the sender or the parts
it reads, and exposes its interface. Its constructor registers its message
types; its handlers decode, check, and raise its events; its sends go through
`RealtimeSender.TrySendIntent` with a cached encoder delegate, so a send
allocates only its packet. A channel that keeps state of one session implements
`IRealtimeSessionScoped` and registers with `RealtimeSession.Track`, which clears
it wherever the session drops the session. `ClientBootstrap` hands the interface
to the feature's controller through `Initialize`.

### Delivery

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
- `IRealtimeMovementChannel.TrySendMovementInputs` wakes LiteNetLib's update
  thread after each batch, so the input leaves at once instead of on the
  transport's next 15 ms update. Remote chunks update admitted remote entities
  and may arrive at a lower fair cadence under dense worker-wide byte pressure.
- Correctly delivered snapshots that overtake join acceptance or remain in flight
  during and immediately after leave are ignored outside the `Joined` state. They
  are not protocol failures because LiteNetLib delivery methods do not provide
  ordering relative to each other.
- While a leave is pending, reliable presence, world actor, interaction,
  corpse, gathering, combat, and item-operation messages that SimulationWorker
  sent before it took the leave can still arrive. Their table entries drop these
  in-flight messages in the `Leaving` state, so a graceful leave never turns
  into a protocol disconnect. A carry state still advances the movement session
  while leaving, without its event. The worker answers the leave at once with
  `LeaveAccepted`, and `Leave` hands its `BodySeconds` to the caller, which
  CharacterSelect counts down.

### Presence And The Movement Session

Every accepted simulation session identifies the local player's server-assigned
nonzero network entity id. `RealtimePresenceRegistry` holds the reliable spawn
baseline and subsequent spawn or despawn changes. It survives the transition
from CharacterSelect to the selected World scene, so entities that spawned
before scene loading are still presented through `SpawnedEntities` and
`SpawnedWorldActors`. Conflicting reuse of an active entity id, a player and a
world actor on one id, a world actor state before its spawn, and a combatant
state without presence are protocol failures. Snapshots never create or remove
entities. They update only ids already admitted by the reliable lifecycle.

`NetworkMovementSession` is created only from a validated join response, by the
session, and the movement channel exposes it while the session is joined or
leaving. It owns the server-provided movement settings, initial state, and
admission-fenced carry tuple used by the client. The tuple contains the
monotonic item-state revision, unitless carried weight, and capacity. Reliable
carry-state updates advance the tuple only when their revision is newer. The
client does not maintain a second editable copy of movement speed, tick rate,
gravity, bounds, snapshot frequency, weight, or capacity for an active network
session. The join response must also match the client's compiled
movement-simulation revision and baked collision revision. Either mismatch
aborts activation with a structured client error.

Client and worker must run the same protocol and simulation code, as
[Versioning](REALTIME_PROTOCOL.md#versioning) describes; what to restart and
rebuild after such a change is in [Unity Client](LOCAL_DEVELOPMENT.md#unity-client).

## Content Loading With Addressables

The client uses Addressables for all runtime content; no client code calls
`Resources.Load`. The only `Resources` folder is TextMesh Pro's own,
`Assets/TextMesh Pro/Resources`, because TextMesh Pro loads its settings from
there itself; see [Text And Fonts](#text-and-fonts). Every content folder that runtime code loads
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

A player build also builds `Config/client-build.json` into Client Content, the
environment and build stamp that only exist while the build runs, as
[Environment And Build Stamp](#environment-and-build-stamp) describes.

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
an entry, on any `Resources` folder other than TextMesh Pro's, and on any
`Resources` call in the client and shared packages.

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
the error and `WorldExit` releases the joined simulation session gracefully.
Addressables releases the World scene's handle and bundles when the scene
unloads, so a return to a built-in scene needs no explicit release.

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
password field once the form is enabled again. When the served content is in a
newer format than the build reads, the card shows the text of
`client_update_required` below its subtitle, and the form stays usable. Below
it the card shows the exit notice, why the client returned here, such as the
text of `account_session_replaced`, as
[Entry Screen Notices](#entry-screen-notices) describes. The footer shows the
GameService connection, the environment and the build, and a warning while the
GameService address is overridden, as
[Environment And Build Stamp](#environment-and-build-stamp) describes.

### CharacterSelect

`CharacterSelectPanel` loads the characters, the shards, and what creation
offers sequentially into `CharacterSelectState`, and draws the state again on its
`Changed` event; the widgets are in `CharacterSelectPanel.Layout.cs`. The state
keeps the chosen character and shard by id across a refresh, lets only an online
shard be chosen, and keeps creation open while the account has no character. A
list is built again only when its version changes, so a selection only recolors
rows, and a double click, read by `TemporaryDoubleClick` from the pointer's click
count, lands on the row it started on. The panel selects the chosen character
and shard in `ClientSession`, whose `SelectionChanged` the inventory and quest
controllers listen to. Creation uses
the appearance offers of [Character Looks](#character-looks). Join ticket
creation and the UDP handshake run inside one coroutine so the operation cannot
be partially overlapped by another click. The ticket and the join are
`WorldJoin`, which the reconnect shares: the panel checks the ticket's World
against the scene catalog before the join, selects the ticket's shard, and
enters the World and loads its scene after it.

Between the top bar and the cards the panel shows the exit notice, why the
client left its last World, in the warning color. While the body of a
character that left stands, the enter page counts its seconds down from
`ClientNotices`: on that character's page that Enter World takes its body over,
and on another character's page that the account can enter another character
once the body is gone. The text goes at zero, and it never disables
`Enter World`; GameService refuses another character with
`account_character_already_active`, whose text the status line shows.

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
| 10 | `WorldNameplates` |
| 20 | `TemporaryCombatHud` |
| 35 | `TemporaryGameMessagePanel` |
| 40 | `TemporaryWorldInteractionPanel` |
| 100 | `InventoryWindows` |
| 120 | `TemporaryDebugMenu` and `TemporaryAdminMenu` |
| 130 | `ReconnectNotice`, without a raycaster, so it takes no click |
| 140 | `TemporaryWorldContextMenu` |
| 150 | The death overlay, a nested canvas of the combat HUD |

The Debug and Admin menus are built in code on `TemporaryMenuWindow`: a window
with a header that names its key and has a close button, a row of tabs, one
scrolling page per tab, and a footer with a status line. Pages are built once and
keep the `Text`, `Toggle`, and `Button` components that change, so a refresh sets
text and lays nothing out, and a closed menu draws nothing. Both start closed.
The Admin menu's Quests tab, `TemporaryAdminQuestTab`, is the exception: it
builds its rows again on a quest state change while the open menu shows the
tab, and once it shows after a change while it was hidden. It sends its tools
through `QuestClientController.TryRunAdminTool`, which shares the one pending
quest intent with abandoning, and reports their refusals itself.
What they show is in [Debug Menu (F2)](GAME_FEATURES.md#debug-menu-f2) and
[Admin Menu (F3)](GAME_FEATURES.md#admin-menu-f3).

`TemporaryDebugMenu`, toggled by `ToggleDebugMenu`, refreshes four times a second
from client state only. Its Leave Shard action runs `WorldExit.Leave`, which
waits for an exact-session UDP leave acknowledgement before loading
CharacterSelect, and it runs the World scene's session check, which sends a
player build without a World back to CharacterSelect and leaves a World whose
connection is no longer joined through `WorldExit`. `IRealtimeDiagnostics`
counts application-level realtime packets and bytes, owner snapshot sequences,
remote snapshot chunks, and estimated missing owner sequences, and
`WorldDebugTelemetryTracker` derives rolling frame percentiles and half-second
network rates from those counters. The menu labels payload rates as application
payload rather than total socket bandwidth. Its `Show in the world` settings live
in `WorldDebugViewSettings`, which `ClientBootstrap` owns, so they last across
World scenes and are off whenever the client starts.

`CollisionDebugOverlay` draws, while the Debug menu is open and its toggles are
on, the baked collision around the local player and the rest of its route.
`CollisionDebugLines` queries the movement session's `ChunkedStaticCollisionWorld`
in a square 48 m wide and turns every shape into line segments by kind: triangle
edges, lifted 3 cm along their normal, are walkable ground or too steep by the
character's maximum slope, or a deep water edge by its layer, water surface
edges are water, and the twelve edges of a box are an obstacle, a movement-only
box such as a resource node, or a sight-only box by its layers. The
overlay builds the lines again when the player moved 2 m or the loaded chunks
changed, and draws one line mesh per kind every frame with `Graphics.RenderMesh`
and the configured debug line material, whose `_BaseColor` it sets per kind.
About 4,500 terrain triangles of the Development World 2 highlands draw in the
Editor at well over 100 FPS.

`TemporaryAdminMenu`, toggled by `ToggleAdminMenu`, holds the development admin
tools in three tabs, Look, Movement, and Combat. They are available while
`IRealtimeAdminChannel.AdminState` is set, which a worker that allows the tools
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

`ClientSessionRecovery` owns the forced sign-out. The bootstrap creates one and
passes it to every component that reads GameService: the inventory and quest
controllers, `CharacterSelectPanel`, and its own account-session check. On HTTP
401 it:

1. Aborts the realtime connection.
2. Signs the client session out, which clears the account, the selection, and
   the World.
3. Posts the code of the 401 to `ClientNotices`, so LoginMenu shows its text,
   and loads LoginMenu, and the failed panel flow stops instead of continuing
   with stale data.

The stable `account_session_replaced` code identifies a login from another
client, from GameService as a 401 or from the worker as the reason it closed
the connection, which `WorldExit` hands over. Recovery writes a specific
temporary Unity Console message rather than presenting it as an ordinary expired
session.

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

The first entry is `TryEnterWorld`, which `Start` runs once. The World scene then
outlives a lost connection: at `SessionEnded` the context turns the local
player's server-authoritative movement off, so the player stands where it was,
its navigator and prediction are gone, a click does not move it, and nothing is
sent, while its camera still turns. The presence of the ended session already
despawned the other players and the world actors. When a later join of the same
World raises `SessionJoined`, as the reconnect's join does, the context binds
the same `LocalPlayerController` to the new movement session with
`EnableServerAuthoritativeMovement`, which starts the prediction at the state
the worker sent, the resumed body or a new entity at the saved place, and moves
the player there; its cloth is told the body jumped. The player object, its
camera, and the scene stay, so `LocalPlayerSource` raises nothing, and the
other players and actors spawn again as the worker sends them.
`WorldReconnectPlayModeTests` pins this on Development World 1.

Once the spawned player has its input and camera, the context sets it in the
bootstrap's `LocalPlayerSource`, and clears it again when it destroys the player
or the scene goes, also when the scene destroyed the player first. The source
answers who the local player is right now: `Controller`, its `Input` for the
components that only read keys, `HasPlayer`, and `Changed`, raised when a player
appears and when it goes. It reads a destroyed player as none, and a clear of an
older player never clears a newer one. The Debug and Admin menus, the collision
overlay, the destination marker, the inventory and quest windows, the world
menu, and world interaction targeting read the player there; nothing searches
the scene for it. The context orders the bodies that wait to be put on by their
distance from the local player's own camera.

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
the local player, the camera, and the inventory windows. It reports whether the
pointer is over interface through the EventSystem, since every World scene panel
is uGUI, or held by a capture that a panel adds, such as the inventory windows
while a stack part rides on the pointer. A click or scroll over a panel, or a
click that puts down or puts back a carried part, therefore never reaches the
world.

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
tunnel meshes, and the deep water edges. Each tick that a grounded character
follows a route, `WaterRules` measures the water above its feet from the baked
water surfaces and slows the walk from the wading depth on. The capsule
dimensions, slope limit, step height, ground snap distance, and substep budget
come from the validated server join settings; the water depths and the wading
speed are shared simulation rules. Movement never uses Unity CharacterController
or PhysX to decide network position.

The route planner treats the deep water edges like any wall. Because a one-sided
wall is thin, the planner also refuses a grid step, or a sample of a straight
segment that skipped the clearance test, when a ray between the two points at
the height of the clearance capsule meets a wall from its front. A route costs
the time it takes: a step costs its length at the mean pace of its ends, and
`WaterRules.Pace` makes a meter of wading water cost as much as 1/0.6 m of dry
land. A straight walk is planned at once only when it never wades; one that
wades competes with the searched route, and string pulling takes a shortcut
through wading water only when it is at most a quarter meter of dry land
slower than the route it replaces, the error of its samples at a water's edge.
Over dry land every step costs its length, so a route there is the shortest.

`WorldCollisionAuthoring` defines the World id, chunk size, collision root, and
layer mask on an authored scene object, and `WorldCollisionBaker` bakes the
enabled, non-trigger BoxColliders, non-convex MeshColliders, and
TerrainColliders below that root, and the surface meshes of its `WaterBody`
components, into neutral authoring JSON plus versioned binary chunks in
WorldData. Mesh vertices and water surfaces are baked in world space and terrain
heights as a heightfield, all as exact base64 floats. The shared compiler derives
the deep water edges from the water surfaces and the ground below them and
canonicalizes signed floating-point zero before encoding, so Unity and .NET
produce identical chunk hashes for equivalent transforms. `WaterBody` itself does
nothing at runtime; its mesh renders the water. Runtime code never scans the
Unity scene or treats PhysX as network authority. The bake workflow and the
authoring rules are in
[Rebuild And Bake Workflow](DEVELOPMENT_WORLDS.md#rebuild-and-bake-workflow),
[Terrain And Mesh Collision Authoring](DEVELOPMENT_WORLDS.md#terrain-and-mesh-collision-authoring),
and [Water Authoring](DEVELOPMENT_WORLDS.md#water-authoring).

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
| 110 | `Cloth` | `ClothChainScheduler` lays the cloth of the bodies the main camera or the character preview now sees |
| 200 | `ScreenAnchoredInterface` | `WorldNameplates` places the Mob nameplates, and `TemporaryCombatHud` places health bars and damage numbers |

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
sends them through `IRealtimeItemChannel`. The item rules it mirrors are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), and what
the player sees is in
[Player Inventory Foundation](GAME_FEATURES.md#player-inventory-foundation).

### Item Operations And Catalog

`IRealtimeItemChannel.TrySendItemOperation` accepts only a typed protocol
intent while joined and sends it on the reliable ordered control path. Committed
or rejected results are decoded only in the joined state. A committed newer carry
revision advances `NetworkMovementSession` before `ItemOperationCompleted` is
raised. The transport does not own item collections. The persistent
`InventoryClientController` consumes the result and correlates its operation id
and kind. A duplicate completion for the last finalized operation is ignored
safely.

`ClientItemCatalog` compiles and revision-checks the gameplay catalog JSON that
the [content check](#content-resolution-at-start) resolved through the shared `ItemCatalogJsonDocument` shape that the Item Catalog editor
also uses, loads the presentation catalog once through Addressables and caches
it, and indexes definitions and equipment slots by stable id. The presentation
catalog is valid with any gameplay catalog: the client checks only its format
version and that its stored revision matches its entries. An item without a
presentation entry shows the placeholder icon and no garment, attachment, or
tool, and an entry for an unknown id is ignored, so a rules change never
blocks item UI. Exact coverage is a repository rule that
[Items content](../content/items/README.md#unity-editor-workflow) describes.
Every HTTP snapshot must also match the gameplay revision the client runs. A
mismatch clears renderable item state and produces `item_catalog_update_required`
instead of accepting stale definitions; its text asks the player to restart the
client, which fetches the new catalog at its next start. Icons and optional prefab presentation keys
resolve locally, and every name comes from the text catalog, as
[Content Text](#content-text) describes. An icon is a Sprite whose
Addressables address is stored in the catalog's `iconAddress` field, below
`Items/Icons/`; the client loads each icon once and keeps it for the process
lifetime. An item without an icon of its own, or whose icon is missing, shows
the shared placeholder `ItemPresentationCatalogLoader.PlaceholderIconAddress`,
`Items/Icons/placeholder.png`, so every item has an icon to draw.
`ItemIconImportRules` in the Editor assembly imports every image below
`Assets/Content/Items/Icons` as one UI Sprite: mipmapped and trilinear, clamped,
at most 256 texels a side, and compressed in high quality. HTTP responses carry only stable definition ids and instance state.

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
The item channel first advances `NetworkMovementSession` with the carry
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
`IRealtimeItemChannel.CarryItemStateRevision` exposes. Every frame,
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

`InventoryWindows` in `Scripts/Ui/Inventory` creates the inventory windows as
runtime uGUI below a World-scene controller and reads the persistent state
without embedding API, protocol, revision, or item-rule ownership; it shows
predicted item operations and never owns item custody. It owns the canvas, the
window keys, the tooltip, the drag coordinator, the dialog, and the storage
context, and it builds three windows on the [UI Kit](#ui-kit-and-theme):
`EquipmentWindow`, `CharacterInventoryWindow`, and `StorageWindow` with its
vendor partial. Each window opens and closes on its own and sits at a fixed
anchor from the theme's measures; the layout is
[Inventory UI Layout](INVENTORY_AND_DEATH_LOOT_DESIGN.md#inventory-ui-layout).
The canvas root only groups the windows and draws no graphic, so
`WorldPointerFocus` sees the pointer over interface only above a shown window,
and world input works beside and between them. A key typed into the search field
is text, so `B`, `C`, and `I` do nothing while it has focus.

`StorageWindow` shows one context of `InventoryContextKind`. What opened it,
`InventoryStorageSource`, decides its tabs and what closes it: the capabilities
of the open NPC interaction, which close the window when the interaction ends;
an opened corpse, whose closing closes the window and puts its reason in the
game messages; or the Development Admin menu, which opens the Bank and Recovery
Storage through `InventoryWindowRequests`. The window's title names the NPC whose
open interaction offers the shown storage or trade. Bank and Recovery are read
through global owning-account reads, and every mutation still travels through
the joined SimulationWorker and is committed through GameService authority.

What a player's action sends lives outside the windows: `InventoryDropPlanner`
decides as a pure function what a drop onto a container, equipment, or corpse
slot sends, an `InventoryDropPlan`, or why it sends nothing, and
`InventoryInteractions` sends a plan, a right-click move from
`InventoryQuickMoves`, Loot all, a destruction, or a vendor trade through the
controllers and returns an `InventoryInteractionResult` with the message for the
player. A drop may move part of the dragged stack, which its payload carries: a
split of a carried or equipped stack, or a partial corpse loot, deposit, or
move. `InventoryQuickMoves.ForItemPart` and `ForCorpseItemPart` give the
Shift+right-click moves: half of the stack, `InventoryQuickMoves.HalfOf`, goes
where the right-click move would send the stack: a partial merge into a stack of
its kind first, else a split into the first free slot, a partial equip of
arrows into the empty ammunition slot, or a partial corpse loot into the
equipped arrows, the empty ammunition slot, or carried storage. A dragged part
follows the same rules: it tops up a compatible stack, enters an empty slot, or
equips into an empty slot, and never swaps. A Mob corpse, which
`WorldActorCorpseRules.IsMobCorpsePresentationKey` names, gives whole items
only, so the windows offer no part of its stacks. A message of an
action that sent nothing, and the reason of an undone move, go to the
`GameMessageLog` that `TemporaryGameMessagePanel` shows.

`EquipmentPaperDollLayout` owns where the equipment slots go: a column on each
side of the character and rows below, and any slot it does not know, such as
one added to the catalog later, in further rows below. It hides the `back` slot,
since no item equips to it until cloaks come from achievements, and the window's
count of equipped slots leaves it out. `EquipmentWindow` places the columns
symmetrically, as far from both window edges, around the theme's
`EquipmentCenterWidth`, where it shows the
[Character Preview](#character-preview) on the theme's pedestal, and centres
each row below with the same gap as the columns; a slot draws no name, and its
tooltip names it. While a drag or a carried part is in progress, every
equipment slot whose drop validator accepts the payload glows with the theme's
`SlotGlow` sprite, breathing through `UiPulse`. Every slot is a square of the
theme's slot size; the Inventory and storage windows show their grids in five
columns, and Permanent Inventory draws no heading.
An occupied slot draws the item's icon from `ClientItemCatalog.LoadIcon` in a
frame of `ItemRarityPresentation.FrameColor`, which is the theme's common color
for every item until items have a rarity; only an item whose icon cannot load
falls back to its name as text. `InventorySlotPresentation` builds slot labels,
badges, and tooltip text as pure functions, and `InventorySearch` decides which
items the search dims. The windows bind slot specifications to `ItemSlotView`
and draw the tooltip through `InventoryTooltipView`; tooltips never show during
a drag.

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
targets light the slot's highlight green or red, then revalidate the current
state before sending an operation. A payload carries an optional quantity, the
part of the stack it moves. A drag started with `Shift` carries half the stack,
rounded down, and targets judge and send that part. `Ctrl`+click on a slot asks
for a quantity in the dialog, then `InventoryDragCoordinator.BeginCarry` puts
that part on the pointer without a held button: `InventorySlotClicks` puts it
on a clicked slot through `InventoryDropTarget.DropCarried`, a refusing slot
says why and keeps it carried, and a right-click, a click beside the windows,
or `Escape` puts it back. While a part is carried, and in the frame its carry
ended, the coordinator's `CapturesPointer` holds `WorldPointerFocus`, so the
click never walks or targets. The coordinator raises `DragStarted` and
`DragEnded` for both kinds. A held drag that ends over no slot and no interface
raises `DroppedOutside`, which offers a whole carried or equipped stack for
destruction behind a confirmation. A state change during a held drag redraws
the windows only when the drag ends, so a confirmation never cancels the drag,
and drops always judge the current slot instead of the one drawn; during a carry
the windows redraw under the part, which is put back when its stack is gone. An
undone move marks its items for `RollbackMarkSeconds` (1.5 s). A Recovery item
represents its complete delivery during a drag because the durable claim remains
atomic.

A UI-only target advisor reuses pure WorldData equipment, stack, Bag, slot-tag,
weight, and hard-cap rules to disable obvious invalid
targets and to predict item operations. Server authority always revalidates any
submitted action, and a prediction only shows what the confirmed state will
become: the committed change or a reload always replaces it.

World loot keeps a reserved context, `WorldLootPrepared`, without inventing
snapshots or operations. The corpse context is described in
[Corpse Client State And Presentation](#corpse-client-state-and-presentation).

### UI Kit And Theme

`Scripts/Ui/Kit` holds the widgets the permanent windows are built from, and
every one of them draws its sprites, fonts, colors, and measures from one
`UiTheme`, so another design replaces the theme's sprites and values without a
code change; what goes where stays in the windows' code.

- `UiTheme` is a ScriptableObject at `Assets/Content/Config/UiTheme.asset`,
  loaded through Addressables as `Config/UiTheme.asset`. `ClientBootstrap` loads
  it with `UiTheme.Load` and passes it on; without it the windows draw with
  defaults. It holds the title and body fonts, text sizes, text and element
  colors, the rarity colors, measures such as the slot size and the windows'
  distance from the screen edges, and the sprites: frames, slot states, controls,
  icons, and a silhouette per equipment slot id and per accepted Bag tag.
- `UiFactory` builds themed texts, headings, buttons, icon buttons, fields, rows,
  and section headings. A heading's text holds its capitals, so layout measures
  what it draws.
- `UiWindow` is a framed window with a header, a body that grows with its content
  up to a maximum and scrolls beyond it, and a footer that shows only with
  content.
- `ItemSlotView` is one slot, built once and bound again from an `ItemSlotSpec`
  whenever what it shows changes, and `ItemSlotGrid` keeps a grid of them and
  adds or hides slots only when the count changes.
- `UiDialog` asks for a quantity or a confirmation; `Enter` confirms and
  `Escape` cancels through `InventoryWindows`.
- `UiPulse` breathes a graphic's alpha between a floor and its color's alpha,
  for the drop target glow with the theme's `DropTargetGlowColor`,
  `DropTargetPulseSeconds`, and `DropTargetGlowFloor`.

The theme's sources are in `Assets/Art/Ui/Sprites`, as
[UI Art](ART_PIPELINE.md#ui-art) describes, and `UiThemeBuilder` in the Editor
assembly fills the theme from them.

### Vendor Context

The storage window has a Trade tab, `InventoryContextKind.Vendor`. Choosing a
vendor capability opens it through the worker, and the confirmed result raises
`InventoryCapabilityOpened`, which opens the storage window on that tab. The
capability's `profileId` names a vendor profile of the item catalog, and
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
worker's result or rejection appears in the Trade tab. The committed change of a
trade, which arrives before the result, already patched the inventory and the
balance, so `EnsureFullState` with the returned item-state revision reloads only
when no change patched it.

### Corpse Client State And Presentation

`CorpseClientController` is a persistent bootstrap-owned adapter over the corpse
messages of the realtime client, which it reaches through
`IRealtimeCorpseChannel` and `IRealtimeSession` in `Mmorpg.Client.Networking` so
EditMode tests can drive it with a fake. It owns immutable nearby-presence and active-view state,
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
  skin. A corpse whose look changes, such as when someone loots its robe, swaps
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

An open snapshot opens the storage window on the corpse, beside the Inventory
window, and closing the window closes the corpse. It renders all three sections
and reuses `InventoryDragPayload` for corpse sources:

- Dropping a full stack, or a part from a `Shift` drag or a `Ctrl`+click carry,
  into Permanent inventory or the equipped Bag submits a typed corpse intent,
  and dropping a
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
- The `Loot All` button below the sections submits `LootAll`, and a right-click
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

### Character Preview

The Equipment window shows the local character in 3D. `CharacterPreview`, which
`WorldSceneContext` creates in `Initialize` below itself, is a stage at
`(0, -10000, 0)` with a pivot, a camera of its own, and three short-range point
lights. The camera looks level at the pivot from 4.4 m with a 28 degree field of
view and draws into a 320 x 640 ARGB32 render texture with 4x MSAA, cleared to
transparent. The player cameras end at 500 m and the stage camera at 12 m, so
neither sees the other; `CharacterPreviewTests` holds both limits. Without a
graphics device, as in the headless test run, the stage makes no texture, its
camera never draws, and the window shows no figure.

- `WorldSceneContext` shows a copy of the body model the local player shows on
  the pivot (`CharacterPreview.Show`), combined with the shared
  `PlayerBodyMeshes` and drawn with the shared `PlayerBodyMaterials`, each time
  it dresses the local body: at the spawn, when the equipment changes, and when
  the look or the model changes. The copy wears the same
  `EquipmentGarments.Dress` look but holds no gathering tool, and it waits while
  the local body lies dead, as the local body does. Another model puts in another
  copy.
- `ClientBootstrap` passes the preview to `InventoryWindows`, whose Equipment
  window shows the texture in a `RawImage` over the pedestal, as tall as the slot
  columns, with an `AspectRatioFitter`. The camera is enabled only while the
  Equipment window is open (`CharacterPreview.Visible`). The copy's Animator
  culls completely, so it plays its idle only while the camera draws it, and the
  camera is a view of `ClothChainScheduler`, so the copy's cloth swings then.
  Otherwise the preview costs nothing.
- `CharacterPreviewDrag` on the picture turns the pivot by 1.2 degrees per
  pixel of a left drag (`CharacterPreview.Yaw`); the copy faces the camera at 0.

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
  levels. Besides the main camera it looks through every enabled camera given to
  `AddView`, such as the [Character Preview](#character-preview)'s, and measures
  each body from the camera it looks largest in (`Measure`). Three Burst jobs do the work: one reads the bodies' bones, one lays
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
of the hair slot. The realtime client keeps the latest
`CharacterAppearance` of every spawned player and of the controlled one, from
`EntitySpawn` and `EntityAppearanceChanged`, and `IRealtimeLookChannel` reads it
and sends `AppearanceIntent`; see
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

`tools/Measure-ClientCrowd.ps1` measures the client with 100, 200, and 400
dressed bots in the stress arena, where every bot is in interest, in an IL2CPP
test player that compiles like a release build: the time until the whole crowd
is shown, frame times, owner snapshots, starvation of the remote players, and
memory, as
[Measure The Client With A Crowd](LOCAL_DEVELOPMENT.md#measure-the-client-with-a-crowd)
describes. The bots, the worker, and GameService run on the same machine as the
client, so a large crowd measures that machine as a whole: when the worker
falls behind its ticks or drops snapshots, which its log in the run's folder
says, the client sees fewer owner snapshots and starves its remote players for
reasons outside the client.

The baseline, measured on 2026-11-14 on an AMD Ryzen 7 5800 with an NVIDIA
GeForce RTX 3070 (Direct3D 12), at 1920 by 1080 with the quality level `PC`,
no VSync, and no frame rate limit, 60 seconds per size:

| Bots in view | Whole crowd shown after | Frame p50 | p95 | p99 | Longest | Owner snapshots per s | Remote players starved | Allocated / reserved / managed memory | Player private memory |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: |
| 100 | 1.7 s | 4.5 ms | 7.2 ms | 9.6 ms | 14.6 ms | 15.0 | never | 312 / 809 / 14 MB | 1842 MB |
| 200 | 3.4 s | 7.9 ms | 11.0 ms | 13.9 ms | 26.4 ms | 15.0 | never | 420 / 1067 / 18 MB | 2174 MB |
| 400 | 6.7 s | 13.1 ms | 17.3 ms | 20.3 ms | 35.7 ms | 15.0 | 18.7 % of the time, 0.28 s at the longest | 615 / 1330 / 26 MB | 2653 MB |

What the baseline says:

- **The frame grows by about 3 ms per 100 dressed bodies in view**, and the
  built client holds 60 frames per second with 400 of them on this machine,
  with about 3 ms to spare at the median and none at the 95th percentile.
- **No frame was longer than 36 ms**, also while 400 bodies came into view.
- **The whole crowd is shown about 1.7 s per 100 bodies** after the World
  scene has loaded.
- **Remote players starve at 400 in view, and not at 200.** The owner
  snapshots kept their 15 per second, the worker held its tick (p99 at most
  16 ms) and dropped no snapshot while the client recorded, so the starvation
  is the worker spreading the updates of a dense crowd under its snapshot
  budget, as
  [Remote Update Rate](STACK_STRESS_TESTING.md#remote-update-rate) describes,
  while the presentation of a remote player stops at its newest state. This
  cause is the likely one, not measured per entity.
- **Private memory grows by about 3 MB per body in view.**

Every other program used 0.6 processors and none of the graphics card while
the client recorded; the bots, the worker, and GameService on the same machine
kept it 64 to 71 percent busy. The test runner marks its player as a
development build (`Debug.isDebugBuild`), although it compiles with the release
settings and the `Release` code variant; a shipped release build is not
measured. The baseline of the Editor's Play Mode is not recorded either; it
needs an open Editor, as
[Measure The Client With A Crowd](LOCAL_DEVELOPMENT.md#measure-the-client-with-a-crowd)
describes.

To look by hand, start the bots on `local-shard-1` as in
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

The world actor and world interaction channels of the realtime client decode
the actor, interaction, and gathering messages through the shared GameProtocol
source and keeps the explicit mismatch
path for older or partially updated clients. Typed insurance apply or remove,
quest actions, vendor purchase and sale, and start gathering payloads ride on
the capability action instead of a second NPC or item interaction transport;
see [Capability Actions](REALTIME_PROTOCOL.md#capability-actions). The client
raises every `QuestLogChanged` and `QuestIntentResult` as an event and sends a
`QuestIntent` with `TrySendQuestIntent`; no quest window uses them yet, so the
panel's `Quests` section only says that the quest dialog is not available yet.
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
The panel never applies an optimistic policy, grant, removal, currency charge,
or item deletion. A committed result triggers a full
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
windows ignore an Escape whose frame reports `CancelConsumedThisFrame`, the Escape
that closed the menu. The inventory windows run at -40, and an Escape that
dismisses their dialog calls `ConsumeCancel`, so it neither closes the open
interaction nor drops the combat target. `UiDialogEscape` runs just before them,
at -41, and does the same for a dialog that another window owns, so that Escape
leaves the inventory windows open too. `RunDefault` and `OpenMenu` run a left or right click for a
given ray, for tools and harnesses. Options are plain values with their provider,
an action code, the pointer candidate, an entity id, an argument such as a
capability id, and the ray, so building the menu every frame allocates nothing. A
provider runs its own option, checks that the target still exists, because an
open menu can outlive its target, and reports whether it acted. Only after an
option acted does the controller raise `OptionExecuted`; combat clears its target
on every such option except its own and Examine, so a stale option that did
nothing ends nothing either.

- Walk here casts the stored ray at the ground through
  `LocalPlayerController.TryFindGroundPoint` and walks there. When a left click
  ran it, `WalkSteering` keeps steering the walk while `SelectHeld` stays true:
  each frame the controller casts the pointer ray at the ground, and once the
  steered intent's plan applied, `WalkSteering.RouteInterval` (0.1 s) has
  passed, and the point lies `WalkSteering.MinimumShift` (0.25 m) or more from
  the steered destination, it walks there as a new intent, without raising
  `OptionExecuted`. The navigator discards a finished plan that a newer intent
  replaced, so waiting for the applied intent keeps a plan slower than the
  interval from being replaced again and again before it applies. A route at most
  every three inputs keeps the newest path in the input batches until the worker
  confirms it, which the worker follows as quick clicks. Releasing the button,
  opening the menu, a blocked menu, or a navigation intent from another feature,
  such as an attack approach or the stop on death, ends the steering; a pointer
  over interface or without ground keeps the current route.
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
  dialogue, insurance, and quests only open, and every other kind executes.
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

### Water Wakes

`WaterWakePresentation` in `Assets/Scripts/WorldActors` shows where the local
player, the other players, and the active Mobs stand in water. Each frame
`WaterWakeTracker` asks `WaterRules.TryGetDepth` of the session's collision
world, the same baked water as the movement rules, how deep the water is at a
body's feet; from 5 cm deep a body leaves wakes:

- A ring spreads on the surface from its feet every 1.6 s while it stands, of
  1.2 m, and every 0.22 s while it moves faster than 0.4 m/s, of 1.4 to 2.2 m
  with its speed. A body that starts to move gets its next ring soon.
- Droplets of spray fly up and forward around its legs, 9 per meter it wades
  in water at least 0.4 m deep and fewer in shallower water, and fall back under
  gravity.
- A body that moves 2 m or more in one frame was placed anew and starts still.

One particle system draws every ring and another every droplet, with the ring
and spray materials of the configuration, so a crowd in the water costs two
draws; without the materials the component shows nothing. Neither system moves
its particles by itself, so a ring grows where it formed and only the spray gets
a velocity.

On a sea the wakes follow the waves. The depth query also names the water body
at the feet, and `WaterSurfaces`, which the bootstrap builds from the World
scene's `WaterBody` components when the scene loads, knows how far the waves
lift each body's drawn surface: it finds the triangle of the body's mesh below
a point and lifts its corners by `WaterWaves` times the green of their colors,
as the water shader does. A wake starts at that height, and every ring on moving
water is kept by its particle seed and moved to the lifted surface each frame
until it fades. Still water costs nothing extra. The wakes are presentation
only. `WaterWakeTrackerTests` checks the tracker on a test pond,
`WaterWakePresentationTests` the particle systems, and `WaterSurfacesTests` the
lifted surface and the water's clock.

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

Combat is server-authoritative and never predicted. `IRealtimeCombatChannel`
decodes the four server messages of the Combat family in the joined state and
raises a typed event for each: `CombatIntentCompleted`, `CombatantStateReceived`,
`CombatEventBatchReceived`, and `OwnerCombatStateReceived`; the Items-family
quantity change arrives as `OwnerItemQuantityChanged` of `IRealtimeItemChannel`. A decode failure fails
the protocol like every other control message, and so does a `CombatantState` for
an entity without reliable presence, because the worker sends it right after
every spawn of a combatant. `TrySendCombatIntent` sends a typed `CombatIntent`,
built with `RealtimeCombatIntent.CreateAttackTarget` or `CreateClearTarget`, on
the reliable ordered control channel. The messages are in
[Combat](REALTIME_PROTOCOL.md#combat), and what the worker decides is in
[Target-Based Combat](SERVICE_FEATURES.md#target-based-combat).

`CombatClientController` is a persistent bootstrap-owned adapter that the
bootstrap initializes with the session and the combat, movement, player, and
world actor channels. Its `CombatClientState` keeps:

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
  range from the item catalog for the item in the `main_hand` slot of the
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
`IsMob` and `TryGetMob` tell a Mob from a player and give its `MobView`, which
holds the level and aggression of its spawn. Approach, arrows, health bars,
nameplates, and damage numbers use it and read the presented position every
frame, so they follow a moving Mob; the worker never sees these positions.

`CombatServerClock` is the client's best estimate of the worker's current tick
for presentation: the tick of the newest owner snapshot,
`IRealtimeDiagnostics.LatestServerTick`, plus the time since it arrived,
`LatestSnapshotAgeSeconds`, plus half of `RoundTripTimeMilliseconds`, at the
movement session's tick rate. The worker keeps sending owner snapshots while the character
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
- Health bars over every damaged living player in view within 60 metres: the
  local player from its owner state, other players from their combatant states.
  Mobs show their health on their nameplates instead.
- A floating damage number at the hit target for every `Hit` event, a
  `WorldLabels` text rising and fading over 1.1 seconds. On a Mob it starts above
  the nameplate, at `WorldNameplates.AnchorLift` over the head plus
  `WorldNameplates.Height` at the nameplate's scale, so it never covers the
  health numbers.
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

The equipped ammunition slot of the Equipment window shows the live arrow
count from `OwnerCombatState` when its `AmmunitionItemInstanceId` names the
equipped stack, and the committed quantity otherwise, through
`CombatLoadoutRules.DisplayedAmmunitionQuantity`. A live count that differs from
the committed quantity always shows, even 0 or 1, and the tooltip says how many
are left. The drawn slot updates in place when that count changes, so a shot
never redraws the panel or cancels a drag.

### Text And Fonts

Every text of the client is TextMesh Pro: the panels, the HUD, the menus, the
entry screens, the world labels, and the signs of the development Worlds. The
legacy uGUI `Text` renders a bitmap font that blurs when its canvas scales, so
the client has none.

- TextMesh Pro's Essential Resources are in `Assets/TextMesh Pro`: the shaders,
  the LiberationSans font file, and `Resources` with `TMP Settings`, its default
  font, sprite and style sheets, and line breaking rules. TextMesh Pro loads
  `TMP Settings` from that folder itself, so it is the client's only `Resources`
  folder.
- The default font in `TMP Settings` is the project's UI font,
  `Assets/Art/Fonts/LiberationSans UI SDF.asset`: LiberationSans sampled at 48
  points with a padding of 10 in a 1024 by 1024 SDFAA atlas, static, with the
  printable ASCII and Latin-1 characters, so `å`, `ä`, and `ö` are in it, the
  dashes, quotes, bullet, ellipsis, and per mille sign, and TextMesh Pro's
  dynamic `LiberationSans SDF - Fallback` for every other character. A
  character that reaches the dynamic fallback in Play Mode, such as the ellipsis
  that the Unity CLI's Play Mode overlay draws, changes the fallback asset on
  disk, so the UI font holds every character the client and the Editor draw. Its distance field is about twice as wide for its size as that of
  TextMesh Pro's own `LiberationSans SDF`, so a 12 to 14 pixel label still gets
  an outline of about a pixel. A static atlas never changes while the game runs,
  so Play Mode leaves the asset as it is. To make it again, use `Window >
  TextMeshPro > Font Asset Creator` with Source Font File `LiberationSans`,
  Sampling Point Size 48, Padding 10, Packing Method Optimum, Atlas Resolution
  1024 by 1024, Character Set Unicode Range (Hex) `20-7E,A0-FF`, and Render Mode
  SDFAA, then set the asset's Atlas Population Mode to Static and its fallback to
  `LiberationSans SDF - Fallback`, and run `MMORPG > Tools > UI > Build UI Fonts`,
  which adds the punctuation.
- The permanent windows draw with the theme's fonts: Cinzel for titles and
  headings, and Alegreya Sans with its bold face for everything else. Both are
  SIL Open Font License fonts in `Assets/Art/Fonts/Cinzel` and
  `Assets/Art/Fonts/AlegreyaSans`, each with its `OFL.txt`.
  `MMORPG > Tools > UI > Build UI Fonts` (`UiThemeBuilder.BuildFonts`) makes their
  static assets again from the font files with the same settings and characters
  as the UI font, which is their fallback. It widens a space to at least a quarter
  em, since Alegreya Sans draws a sixth of an em, which closes up words at UI
  sizes, and links the bold face in the regular asset's font weight table, so
  `<b>` draws the real bold. `UiThemeTests` pins that every UI font is static and
  holds these characters.
- `WorldLabels` creates every text drawn over the world, such as nameplates and
  damage numbers: TextMesh Pro's default font, no wrapping, no raycast, no rich
  text, and one shared material, the font's own with a black outline of 0.3 and
  a face dilation of 0.15. Outline and dilation together stay within the font's
  distance field, or every letter shows a dark box. The outline is a shader
  feature of the mobile SDF shader; TextMesh Pro's own outline preset in its
  `Resources` folder keeps that variant in every build.
  `WorldLabels.FontSizeForCapHeight` gives the font size whose capitals and
  digits have a given height.
- `TemporaryUiKit` builds every panel text as a `TextMeshProUGUI` in the default
  font that wraps at its width and may overflow its height, as a uGUI text did,
  and every field as a `TMP_InputField` whose text and placeholder sit in a
  masked `Text Area` inset from its edges, which also bounds the caret and the
  selection. A text over the world, such as the combat message, the death
  overlay, and the world menu's hover text, uses the outlined material of
  `WorldLabels` instead of a uGUI `Shadow`, which TextMesh Pro ignores.
- TextMesh Pro measures a text only while it is active: under an inactive
  parent, `preferredWidth` and `GetPreferredValues` return the width of a
  fraction of the text. Activate a text before anything reads its size, as the
  world menu does with its rows and the inventory tooltip with its label.
- `WorldLabelTests` pins the default font, its settings and characters, the
  outlined material, the build variant, and the digit height of a label.
  `LegacyUiTextTests` fails on a client type that holds a uGUI `Text`,
  `InputField`, or `Font`, on client code that creates or finds a uGUI `Text`
  or `InputField` or loads the legacy runtime font, and on a scene or prefab
  that contains either component.

### Mob Nameplates

`WorldNameplates` in `Mmorpg.Client.Ui` draws a nameplate over every living Mob
within `NameplatePresentation.MobRangeMetres` (40) of the local character: the
display name over a 13 pixel health bar, the health as `current/maximum` in the
bar, and the level to the right of it, its digits as tall as the bar. Its texts
are `WorldLabels`, centered on the bar by their geometry. It is not temporary:
its rules are the foundation for every world label, and only its look waits for
the final interface art.

- Data: it keeps the entity ids of the combatants in view, read from
  `CombatClientState.Combatants` on `Changed` instead of every frame, and each
  frame reads the combatant state by id. `CombatantViewLocator.TryGetMob` gives
  the Mob's view, whose `Level` and `Aggression` come from the
  `WorldActorSpawn` through `WorldActorClientEntry`. A Mob without a combatant
  state, a hidden Mob, and a dead one show nothing.
- Color: `NameplatePresentation.MobHealthColor` is red for `Aggressive` and
  yellow for `Defensive`; an unknown aggression shows red.
- Slide: every nameplate owns a `NameplateHealthSlide` that slides the shown
  health to each new value over `NameplatePresentation.HealthSlideSeconds`
  (0.3), easing out, and on from where the bar is when a value arrives mid-slide.
  The first value and a new maximum show at once. Texts change only when the
  shown whole number does, so a frame allocates no strings.
- Pooling: nameplates belong to an entity while it shows and go back to a pool
  when it dies, despawns, or leaves the range, so a Mob that returns shows its
  health at once.
- Placement: the bottom of the nameplate sits `AnchorLift` (0.35 m) above the
  top of the Mob's interaction bounds, projected after the camera moved. The
  nearest nameplate is the last sibling, so it draws over farther ones.
- Size: `NameplatePresentation.MobNameplateScale` scales a nameplate around its
  bottom edge with the distance from the local character, from `FarScale` (0.55)
  at the edge of the range to full size within `FullSizeDistanceMetres` (10).
- Canvas: its own screen-space canvas at sorting order 10, below the combat HUD,
  so damage numbers and frames draw over it. Nothing on it takes a raycast.

## Quest Client

The client shows quests and never decides one: the servers check every action,
as [Quest System Design](QUEST_SYSTEM_DESIGN.md#authority) describes.
`Mmorpg.Client.Quests` holds the state and the rules it reads, and the quest
windows in `Mmorpg.Client.Ui` present it.

- `ClientQuestCatalog` reads the resolved `core.quest-catalog.json` through the
  camelCase `QuestCatalogJsonDocument` of the WorldData package, which Unity's
  `JsonUtility` needs, and accepts it only when `QuestCatalogCompiler.ValidateRuntime`
  computes the same fingerprints and revision. It then replaces the texts of
  its copy with the text catalog's, as [Content Text](#content-text) describes.
  `QuestsGivenBy` lists the quests of one giver by title, so the dialog and the
  marker of an NPC never walk the whole catalog.
- `QuestClientController` reads `GET /api/characters/{characterId}/quests` when
  the character joins, up to three times, and refuses a log of another quest
  catalog revision with `quest_catalog_update_required`, whose text asks the
  player to restart the client. It applies every
  `QuestLogChanged` whose previous revision is the log's; a change the log
  already holds is stale, and one after a gap, or one with `ReloadRequired`,
  reads the log again. A change with `ReloadRequired` also reads the whole
  inventory again through `IInventoryOperationQueue.RefreshFullState`, because
  the change it stands for, such as a turn-in behind lost answers, may have
  moved items. Changes that arrive during a read wait and follow it in
  revision order. When all three tries of a read fail, `QuestLogRetry` reads it
  again after 2 s, doubling up to 30 s; a quest change or an opened quest dialog
  reads it at once. The pending quest intent and the refusal of the last one end
  with the session and with a change of character.
- `QuestClientState` keeps the active quests with their current step and the
  stored progress of the step's counted objectives, the completion counts, and
  the character's level from the character list. It raises `QuestsChanged` with
  each quest a change touched, before and after. A catalog that is
  invalid leaves it `Failed` with the reason for every character, and the quest
  windows show that reason instead of reading quests.
- `QuestProgress` counts a collect objective by the carried items, as
  `QuestCarriedItems` counts them from the confirmed inventory: the permanent
  inventory and the equipped Bag, an ordinary item without an active policy and
  a quest item with a `quest_grant` policy. The client does not see which quest
  a lineage names, but a quest item belongs to one quest, so each of its quest
  grants carries that quest's lineage.
- `QuestDialogEntries` lists what an NPC can do for the player now: ready quests
  it completes, talk-to and deliver objectives that name it, the quests it gives
  that `QuestRules.Evaluate` allows, start items of its quests that the player
  owns nowhere the inventory shows, carried, equipped, in the Bank, or in
  Recovery Storage, and active quests it completes later.
  `QuestDialogEntries.CanHandOver` holds a delivery back until the player
  carries the whole count, which the dialog's `Hand over` hands over at once.
- `QuestRewardFit` checks a turn-in's rewards by the rules GameService places
  them with: the turn-in takes the collect objectives' items and removes the
  quest's other quest items first, then each reward tops up carried stacks
  without a policy and takes a free slot that accepts it, matching specialized
  Bag slots, the permanent inventory, then general Bag slots; the carried weight
  must end within the hard cap and the balance within its maximum. Which stacks
  a collect objective empties is GameService's choice, so every slot that one
  could empty counts as free for any reward, and the check refuses only what
  GameService would refuse too.
- `QuestMarkers` derives the marker over an NPC from its dialog entries, the
  most urgent first: a yellow `?` for a ready quest, a talk-to objective, or a
  delivery the player can hand over, a yellow `!` for an offer or lost start
  items, and a grey `?` for a quest in progress or a delivery without enough
  carried. When none of these waits, a quest that the NPC gives and that
  `QuestRules.Evaluate` holds back only for the level shows a grey `!`.
- `QuestNotices` writes the game messages of a committed change: an accepted
  quest, counted progress such as `Feral Wolves slain 3/5`, a step that moved on
  after the full counts of the objectives the change finished, and a completed or
  abandoned quest. `QuestClientController.QuestDropped`
  passes on every `QuestDropInCorpse`, and `QuestNotices.ForDrop` writes it as
  `Feral Wolf dropped 2 x Wolf Fang for Pelts and Fangs.` `QuestProgressWatcher` follows every
  quest change and every inventory change and writes the progress that carried
  items make, such as `Wolf Pelts 2/3`, and a quest that became ready to turn in,
  whatever made it ready, once. A load of the quest log starts it over without
  messages, and so do the carried items of an inventory that loads after the
  log, since the player carried them before. `QuestNoticeFeed` connects both to
  the quest state and the inventory and starts from them as they are when it is
  created, which the quest windows do after a login loaded the log, so the first
  change after that tells as much as any later one.

`QuestLogModel` lists the active quests by title with the current step's
objectives and whether each is ready, and the completed quests with their
counts, for the quest log and the tracker. `QuestClientController.TryAbandon`
sends a `QuestIntent` to abandon an active quest, one at a time; the quest
change and the removed items arrive before its result, whose refusal the log
shows, and a committed result with an item revision brings the inventory up to
it.

Choosing the `quests` capability raises
`WorldInteractionClientController.QuestCapabilityOpened`, and `QuestWindows`
opens the quest dialog for the NPC of the open interaction and clears the
message of an earlier interaction action. The dialog stands at the screen's
left edge, or beside the Equipment window while that is open, because the
Equipment window stands there too: the quest canvas has sorting order 90, above
the interaction panel and below the inventory, whose windows would cover it.
The dialog lists the entries; an entry shows its text, the
current step's objectives, an offer through `QuestProgress.OfferedObjectives`
with the carried items of its collect objectives, and the rewards, with `Accept`, `Complete` after a
reward choice, `Continue`, `Hand over` once enough is carried, or `Ask again`.
`Complete` also waits while `QuestRewardFit` says the rewards cannot fit, and
the dialog says why in red. Each action goes through `TryExecuteQuestAction`
with the quest, objective, and reward choice, and the dialog follows the quest
log the change patches. A rejection shows its message in the dialog, and the
dialog closes with its interaction. The temporary interaction panel's `Quests`
button opens it again.

`QuestWindows` also owns the quest log, `QuestLogWindow` beside the dialog,
which the `ToggleQuestLog` action on `L` opens and closes unless a text field
has focus and which shows only the refusals of abandoning, and `QuestTracker`,
a panel at the right edge of the screen that takes no raycasts and keeps its
lines while the same character's log is read again. `Abandon` asks through a
`UiDialog`, and its
`UiDialogEscape` closes that dialog alone on Escape, before the inventory
windows see the key. Otherwise an Escape closes the log with the other windows.
The quest windows run right after the inventory windows, so an Escape that an
inventory dialog took reaches them as taken.

`WorldQuestMarkers` draws the markers over the NPCs within 40 m as TextMesh
Pro world labels on a canvas of its own with the nameplates' sorting order,
and nothing on it takes a raycast. It runs at `ScreenAnchoredInterface`, after
the camera moved, places each marker 0.3 m above the NPC's interaction bounds,
and shrinks it with distance like a nameplate. It reads the actor definition id
from each `NpcView`, derives that NPC's marker once, and keeps it until the
quest state or the inventory changes, so a frame derives nothing and rebuilds
no text mesh.

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
  unreachable GameService, real bodies dying, using tools, and attacking, and a
  World scene that keeps its player across a lost connection and the join that
  restores it, on the hub of a `ScriptedRealtimeTransport`.
- **Fixtures.** Tests never depend on the layout of a development World:
  `FixtureCollisionWorld`, `FixtureItemCatalog`, and `FixtureWorldActorContent` in
  `Tests/EditMode/Fixtures` provide test-owned collision, items, and actors; see
  [World Layout Fixtures](../tests/README.md#world-layout-fixtures). Tests that
  check the development Worlds themselves name them.
- **Shared sources.** The files in `Tests/Shared`, the test assembly
  `Mmorpg.SharedTests`, compile into the Unity tests and the .NET tests, so
  every runtime runs the same cases.
  `Mmorpg.Shared.Tests` compiles all of them and `Mmorpg.Tools.Tests` the
  protocol goldens; the protocol goldens are described in
  [Realtime Protocol Goldens](../tests/README.md#realtime-protocol-goldens).
- **Realtime hub.** The tests in `Tests/EditMode/Networking` pin what
  `RealtimeSimulationClient` and its parts do through `FakeRealtimeTransport`,
  which records every call and raises queued connect, disconnect, socket error,
  and packet events only inside the poll, as LiteNetLib does, and a clock the
  test sets. `RealtimeHubHarness` scripts a session: `Frame()` runs one `Update`,
  and one timeline holds every event of the session and the channels and every
  transport call with a `frame` marker, so a test pins the order of events and
  the frame they fire on. The harness is the one place that subscribes to the
  parts; a test reads a part through `harness.Hub`, such as
  `harness.Hub.Session.State`. The packets come from the generated encoders, with
  the samples in `RealtimeHubMessages`. The connection tests pin the join, leave,
  timeouts, every way a session ends, and the two rules of
  [How A Session Ends](#how-a-session-ends); the dispatch tests pin, for every
  message type, the event it raises with its decoded content while joined, the
  failure code before the join and of a malformed payload, what the `Leaving`
  state does with it, and the channel and delivery it needs; the ordering tests
  pin the rules between handlers; the send tests pin every `TrySend*` and the
  diagnostics counters. A server message type without an entry in the message
  table fails the composition of every hub test, and one without a dispatch case
  fails `EveryMessageTypeHasACase` until `RealtimeHubDispatchTests` gets a case
  for it: a sample in `RealtimeHubMessages`, the event it raises, its codes, and
  its `Leaving` reaction. A new client message needs an entry in the client cases
  there. Tests of other areas read the table through
  `RealtimeMessageTable.IsDropped` of a harness's hub. `WorldExitTests` and
  `WorldReconnectTests` run the ways out of a World and the reconnect on a
  harness's hub, with `FakeJoinTickets` as GameService's ticket call and
  `TestRoutine` running the coroutines by hand, and `ReconnectRulesTests` pins
  the table of endings and attempt failures of
  [Restoring A Lost Connection](#restoring-a-lost-connection).
- **Live stack.** `LiveStackPlayModeTests` plays one character through the real
  `ApiClient`, `RealtimeSimulationClient`, and LiteNetLib transport against a
  running local stack. It composes the hub with `LiveStackTransport`, which wraps
  `LiteNetLibRealtimeTransport` to read `BodySeconds` and to drop the line without
  a disconnect message, quietly or so that the hub finds the loss at once, so the
  Networking assembly shows its internals to the PlayMode tests too, and with the
  settings of the running client's session. One step composes the client's
  `WorldReconnect` on that hub and lets it restore a dropped line by itself.
  The test is explicit and has the `LiveStack` category, so
  only `tools/Test-LiveStack.ps1` runs it, as
  [Live Stack Test](../tests/README.md#live-stack-test) describes, in the
  headless Editor or in an IL2CPP test player.
  `LiveStackCrowdPlayModeTests`, explicit in the category `ClientCrowd`,
  measures the running client with a crowd in the stress arena for
  [Crowd Performance](#crowd-performance).
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
- **Player goldens.** `PlayerRuntimeGoldenTests`, in the `Player` category of
  the PlayMode assembly, runs the pinned simulation revision, movement and
  planner fingerprints, and protocol goldens of `Mmorpg.SharedTests` in the
  runtime that executes the PlayMode tests: Mono in the Editor, and IL2CPP in
  the test player that
  [Run The Unity Tests](LOCAL_DEVELOPMENT.md#run-the-unity-tests) builds with
  the C++ settings of a release build, so the IL2CPP client is checked to
  predict and encode as the worker does. Each
  test writes the value it computed as a `[PLAYER]` line into the result file.
- **Graphics.** A test that draws with a camera needs a graphics device and is
  marked `[Category("Graphics")]`. The headless run leaves that category out; see
  [Traits](../tests/README.md#traits).
- **Batch mode.** CI runs the suites headless, where Unity never evokes
  `WaitForEndOfFrame`, so a test yields `null` instead; `tools/Test-Changes.ps1`
  fails a test that yields `WaitForEndOfFrame`. Headless Unity also has no
  graphics device, where making a render texture logs an error that fails the
  test, so client code checks `SystemInfo.graphicsDeviceType` first, as
  `CharacterPreview` does, and `tools/Test-Changes.ps1` fails code that does not.
- **Architecture guards.** `ClientAssemblyArchitectureTests` pins the assembly
  graph described in [Assemblies](#assemblies). `ClientAddressablesTests`
  compares the committed Addressables settings with `ClientAddressablesLayout`,
  resolves every fixed address, every generated collision manifest and chunk,
  every presentation icon, and every registry prefab, loads content through
  `ClientAssets`, and fails on any `Resources` folder other than TextMesh Pro's
  and on any `Resources` call in the client and shared packages. `RuntimePresentationTests` keeps runtime primitives on the
  configured material. `ClientServiceAccessTests` reads the sources below
  `Assets/Scripts` and fails on a scene search, `FindAnyObjectByType`,
  `FindFirstObjectByType`, `FindObjectsByType`, `FindObjectOfType`,
  `GameObject.Find`, a find by tag, or `Camera.main`, outside its four allowed
  places, each with its reason, and on an allowed place that is gone. It reads
  the client's runtime assemblies and fails on a type that keeps state in
  statics, an assignable static field or a static collection, outside its list
  of such types, each of which resets at subsystem registration, see
  [Extension Rules](#extension-rules).
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
  service access or reference `Mmorpg.Client.App` from runtime code. Never
  search the scene for a service or the local player, or ask for `Camera.main`:
  take the service through `Initialize` and read the player from
  `LocalPlayerSource`. `ClientServiceAccessTests` fails on a new search.
- Load runtime content only through Addressables, following
  [Working Rules For New Content](#working-rules-for-new-content): refer to fixed
  addresses through `ClientAddresses`, release every handle you own, and never
  add a `Resources` folder.
- Draw every text with TextMesh Pro, never with the legacy uGUI `Text` or
  `InputField`, panel texts through `TemporaryUiKit` and every text over the
  world through `WorldLabels`, as [Text And Fonts](#text-and-fonts) describes.
- Change scenes only through `ClientSceneLoader`. A new World scene needs its
  catalog entry and `MMORPG > Tools > Addressables > Apply Client Layout`.
- Create runtime primitives only through `RuntimePrimitives` with the configured
  material.
- Keep HTTP serialization and failure mapping inside the API layer.
- Keep realtime transport state and packet handling inside the parts of
  `RealtimeSimulationClient`, not scene panels: the connection state in
  `RealtimeSession`, the routing in `RealtimeMessageTable`, presence in
  `RealtimePresenceRegistry`, and a feature's messages and send in its channel,
  as [Adding A Message Type Or A Channel](#adding-a-message-type-or-a-channel)
  describes. A feature takes the interfaces of the parts it needs through
  `Initialize`, never the hub.
- Keep deterministic movement rules in `GameSimulation`, not in transport or
  presentation components.
- Wrap every float intermediate in `GameSimulation` in an explicit `(float)` cast.
  Mono keeps float intermediates in double precision, so an expression such as
  `(a * b) + c` without casts predicts differently in Unity than on the worker.
  The movement determinism tests catch a missing cast.
- Keep authored collision in `WorldData`; never duplicate scene geometry as
  hand-maintained server constants.
- Make a part of a World that a few values describe, such as a lake, a
  [World Builder](DEVELOPMENT_WORLDS.md#world-builder) shape: a
  `WorldBuilderShape` in `Mmorpg.Client.Worlds` that holds only its values and
  does nothing in the game, a builder in `Mmorpg.Client.Editor` that
  `WorldBuilder.Rebuild` runs for the whole scene and that writes only what
  changes, its checks in `WorldBuilder.Validate`, and an editor with its
  handles, such as `OutlineShapeEditor` for a closed outline. Change a shape's
  values through `SerializedObject`, so every change can be undone and the World
  Builder follows it. A builder that shapes something it does not own, such as a
  terrain, remembers what it changed with that thing and takes it back before
  it shapes it again, as `TerrainCarver` does.
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
- Keep persistent cross-scene account state in the bootstrap's `ClientSession`,
  change it only through its transitions with the owners that
  [Client Session State](#client-session-state) names, and listen to its events
  instead of polling it. Keep item and corpse state in their bootstrap-owned
  controllers, not scene panels.
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
- Keep state in objects that the bootstrap passes, not in statics. A type that
  must hold static state, such as a cache of loaded assets, gets a
  `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
  reset, so the client stays correct when Enter Play Mode Options disable domain
  reload, and an entry in the list of `ClientServiceAccessTests`.
- Route every way out of a World through `WorldExit`, which leaves by an
  exact-session UDP leave or the disconnect fallback and clears the client
  session's World only for that exact simulation session.
- Decide whether an ending reconnects only in `ReconnectRules`: only a dropped
  line does. A new network or timeout code of the client needs its place there,
  and a new code that the client makes with a kind of its own a row in
  `ReconnectRulesTests`, which fails until it has one.
- Temporary implementation shortcuts are permitted only inside the replaceable UI
  presentation layer. UI-independent state and behavior must not be embedded in
  temporary panels.
- Add player-facing behavior to [Game Features](GAME_FEATURES.md) when it becomes
  implemented, and update this document when client ownership, folders, scene
  flow, networking, input, or persistent runtime structure changes.

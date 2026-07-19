# Development Worlds

Last updated: 2026-07-19

## Purpose

This document defines the checked-in development World identities and the
manual Unity workflow for authoring their scenes. A World is reusable content.
A shard references one World at a time and may change that reference only while
the shard is fully offline and drained.

## Checked-In World Registry

| World ID | Unity scene | Status | Worker bounds | Authoritative spawn |
| --- | --- | --- | --- | --- |
| `development-world-1` | `DevelopmentWorld1` | Complete baseline | X/Z `-14` to `14` | `(0, 0, -1)` |
| `development-world-2` | `DevelopmentWorld2` | Complete greybox baseline | X/Z `-254` to `254` | `(0, 0, -16)` |

`local-shard-1` remains bound to `development-world-1`. Development World 2 now
has an authored scene and checked-in collision data, but registering and
building a World does not bind or start it.

The client catalog maps `development-world-2` to `DevelopmentWorld2`, and both
development scenes are enabled in Build Profiles.

## Development World 2 Layout Contract

Development World 2 is a 512 by 512 meter greybox with a 508 by 508 meter
authoritative playable area. Unity units are meters.

| Element | Position or area | Notes |
| --- | --- | --- |
| Ground | center `(0, -0.25, 0)`, size `(512, 0.5, 512)` | One BoxCollider is valid across multiple collision chunks |
| North boundary | center `(0, 3, 255)`, size `(512, 6, 2)` | Interior face aligns with Z `254` |
| South boundary | center `(0, 3, -255)`, size `(512, 6, 2)` | Interior face aligns with Z `-254` |
| East boundary | center `(255, 3, 0)`, size `(2, 6, 512)` | Interior face aligns with X `254` |
| West boundary | center `(-255, 3, 0)`, size `(2, 6, 512)` | Interior face aligns with X `-254` |
| Player spawn | `(0, 0, -16)` | Yaw `0` |
| Central service plaza | X `-32` to `32`, Z `-8` to `32` | Keep the three service points reachable |
| North traversal course | X `-64` to `64`, Z `64` to `192` | Ramps, stairs, elevation, and cover |
| East sightline lane | X `80` to `224`, Z `-32` to `32` | Long-range movement and interest testing |
| Southwest wilderness | X/Z `-224` to `-80` | Initial wolf spawn and patrol region |
| Southeast expansion pad | X `80` to `224`, Z `-224` to `-80` | Leave mostly empty for future systems |

The worker profile and actor content already reserve these coordinates:

| Content | Position or area |
| --- | --- |
| Bank access | `(-8, 0, 4)`, radius `3` |
| Recovery storage access | `(0, 0, 4)`, radius `3` |
| Insurance NPC access | `(8, 0, 4)`, radius `2` |
| City Services NPC | `(8, 0, 4)` |
| City Guard group | centered at `(-16, 0, 12)` |
| Feral Wolf spawn area | X/Z `-190` to `-130` |

Do not move these authored landmarks without updating the matching worker
profile and actor spawn content in the same change.

## Manual Unity Authoring Workflow

Perform these steps only after the repository verification for the preparation
change is green:

1. Open `shooter-mmorpg-unity-client` in Unity and allow asset import to finish.
2. In the Project window, select `Assets/Scenes/DevelopmentWorld1.unity` and duplicate
   it with `Ctrl+D`.
3. Rename the duplicate to `DevelopmentWorld2.unity` and open it.
4. Keep the existing gameplay, scene context, lighting, camera, UI bootstrap,
   `PlayerSpawn`, `EntityPresentationRoot`, and collision-authoring objects.
5. Replace the duplicated environment geometry with a root named `Environment`.
   Organize it with `Ground`, `Boundaries`, and descriptive area roots such as
   `CentralHub`, `NorthTraversal`, `EastSightline`, and
   `SouthwestWilderness`.
6. Build the ground and four boundaries with the exact transforms in the layout
   table. Use enabled, non-trigger BoxColliders.
7. Move `PlayerSpawn` to `(0, 0, -16)` with Y rotation `0`.
8. Greybox the five layout areas. Keep all authoritative collision under
   `Environment`. Use BoxColliders only. Decorative objects may omit colliders.
9. Select the object with `WorldCollisionAuthoring`. Set World Id to
   `development-world-2`, Chunk Size to `32`, and Collision Root to
   `Environment`.
10. Save the scene. Select `Shooter MMO > Tools > World Collision > Bake Open
    Scene`.
11. Confirm the Console reports a successful bake for
    `development-world-2`. The bake must create
    `WorldData/Authoring/development-world-2.collision-authoring.json` and
    `WorldData/Runtime/Resources/ShooterMmo/WorldCollision/development-world-2`.
12. Open `File > Build Profiles`, add `DevelopmentWorld2` to the scene list,
    and keep `LoginMenu` and `CharacterSelect` before the World scenes.
13. Save the project and return to Codex for verification before changing any
    shard binding.

Expected result: Development World 1 remains playable, Development World 2 has
its own scene and collision content, and both scenes are selected exclusively
through their authoritative World IDs.

## Post-Authoring Verification

From the repository root, verify the new collision outputs:

```powershell
dotnet run --project Tools/WorldCollisionCompiler `
  --configuration Release `
  --no-build -- `
  WorldData/Authoring/development-world-2.collision-authoring.json `
  WorldData/Runtime/Resources/ShooterMmo/WorldCollision/development-world-2 `
  --verify
powershell -ExecutionPolicy Bypass -File Tools/Run-UnityTests.ps1
```

Expected result: the collision verifier reports `development-world-2` with 256
chunks and a deterministic revision, and all Unity EditMode and PlayMode tests
pass. `Bake Build World Scenes` also verifies every catalog-to-Build Profiles
mapping before writing collision output.

The authored baseline has passed review. Do not rebind `local-shard-1` as part
of map authoring; a rebind remains a separate offline operational step.

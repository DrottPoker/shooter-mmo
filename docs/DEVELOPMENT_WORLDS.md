# Development Worlds

Last updated: 2026-07-20

## Purpose

This document defines the checked-in development World identities and the
manual Unity workflow for authoring their scenes. A World is reusable content.
A shard references one World at a time and may change that reference only while
the shard is fully offline and drained.

## Checked-In World Registry

| World ID | Unity scene | Status | Worker bounds | Authoritative spawn |
| --- | --- | --- | --- | --- |
| `development-world-1` | `DevelopmentWorld1` | Complete baseline | X/Z `-14` to `14` | `(0, 0, -1)` |
| `development-world-2` | `DevelopmentWorld2` | Expanded gameplay greybox | X/Z `-254` to `254` | `(0, 0, -16)` |

The checked-in SimulationWorker configuration requests `development-world-2`
for `local-shard-1` during broader gameplay testing. AuthService accepts and
persists that binding only when the shard is offline and drained. Both Worlds
remain reusable content.

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
| Central service plaza | X `-32` to `32`, Z `-8` to `32` | Service halls, four watch structures, entry pillars, and low cover leave the service points reachable |
| North traversal course | X `-64` to `64`, Z `64` to `192` | Two raised platforms, ramps, stairs, an observation deck, towers, and mixed cover |
| East sightline lane | X `80` to `228`, Z `-32` to `32` | Long-range lane with alternating cover, side walls, an observation post, and a backstop |
| Southwest wilderness | X/Z `-232` to `-98` | Sparse rock perimeter keeps the wolf spawn area and patrol points open |
| Southeast expansion pad | X `80` to `224`, Z `-224` to `-80` | Mostly empty visual pad with corner beacons and one small cargo cluster |

The canonical World manifest and actor content already reserve these coordinates:

| Content | Position or area |
| --- | --- |
| Bank access | `(-8, 0, 4)`, radius `3` |
| Recovery storage access | `(0, 0, 4)`, radius `3` |
| Insurance NPC access | `(8, 0, 4)`, radius `2` |
| City Services NPC | `(8, 0, 4)` |
| City Guard group | centered at `(-16, 0, 12)` |
| Feral Wolf spawn area | X/Z `-190` to `-130` |

Do not move these authored landmarks without updating the matching `world.json`
manifest and actor spawn content in the same change.

Development World 2 currently contains 86 enabled, non-trigger BoxColliders.
The broad ground collider still guarantees all 256 collision chunks exist.
Thin cyan service and spawn guides are visual only and have no Collider. They
show the canonical bank, recovery, insurance, guard group, and wolf spawn
coordinates without creating or changing actor content.

## Environment Palette

| Color | Meaning |
| --- | --- |
| Dark green | Ground |
| Charcoal | Routes and plaza surfaces |
| Blue grey | Buildings, platforms, walls, and towers |
| Orange | Traversal elements, cover, and cargo |
| Brown | Wilderness rock formations |
| Teal | Reserved southeast expansion area |
| Cyan | Visual-only actor placement guides |

The palette builder selects its shader from the active render pipeline. It uses
Unity's Standard shader while the current checked-in render configuration falls
back to the Built-in Render Pipeline, and switches to URP/Lit only when a valid
URP Pipeline Asset is active. The installed URP package alone does not make URP
active, and URP/Lit materials render magenta without an active pipeline asset.

## Rebuild and Authoring Workflow

The checked-in editor builder reproduces the complete environment without
touching `Gameplay`, actor authoring, or actor runtime content.

1. Open `shooter-mmorpg-unity-client` in Unity and allow asset import to finish.
2. Open `Assets/Scenes/DevelopmentWorld2.unity`.
3. To restore the complete checked-in layout, select `Shooter MMO > Tools >
   Development Worlds > Rebuild Development World 2 Environment` and confirm
   `Rebuild`. This intentionally replaces every child under `Environment`.
4. To customize the greybox, edit the generated children under `Environment`.
   Keep authoritative geometry as enabled, non-trigger BoxColliders. Decorative
   route surfaces and cyan guides must remain collider-free.
5. Keep `PlayerSpawn`, all service markers, the guard marker, the wolf spawn
   guide, and the documented wolf patrol points free of blocking geometry.
6. Save the scene. Select `Shooter MMO > Tools > World Collision > Bake Open
   Scene`.
7. Confirm the Console reports a successful bake for
    `development-world-2`. The bake must create
    `WorldData/Worlds/development-world-2/Authoring/collision.json` and
    `WorldData/Worlds/development-world-2/Runtime/Resources/ShooterMmo/WorldCollision/development-world-2`.
8. Keep `DevelopmentWorld2` enabled in `File > Build Profiles`, after
   `LoginMenu` and `CharacterSelect`.

Expected result: Development World 1 remains playable, Development World 2 has
86 authoritative boxes in 256 chunks, and actor authoring files remain
unchanged. Both scenes are selected exclusively through their authoritative
World IDs.

## Post-Authoring Verification

From the repository root, verify the new collision outputs:

```powershell
dotnet run --project Tools/WorldCollisionCompiler `
  --configuration Release `
  --no-build -- `
  WorldData/Worlds/development-world-2/Authoring/collision.json `
  WorldData/Worlds/development-world-2/Runtime/Resources/ShooterMmo/WorldCollision/development-world-2 `
  --verify
powershell -ExecutionPolicy Bypass -File Tools/Run-UnityTests.ps1
```

Expected result: the collision verifier reports `development-world-2` with 86
boxes, 256 chunks, and a deterministic revision, and all Unity EditMode and
PlayMode tests pass. `Bake Build World Scenes` also verifies every
catalog-to-Build Profiles mapping before writing collision output.

The authored baseline has passed review. The local worker configuration now
requests Development World 2 for `local-shard-1`. Future changes between
development Worlds remain separate offline operational steps that AuthService
validates and commits on the replacement worker's first heartbeat.

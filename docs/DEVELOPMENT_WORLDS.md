# Development Worlds

Last updated: 2026-10-16

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
for `local-shard-1` during broader gameplay testing. GameService accepts and
persists that binding only when the shard is offline and drained. Both Worlds
remain reusable content.

The client catalog maps `development-world-2` to `DevelopmentWorld2`, and both
development scenes are Addressables World scenes. Build Profiles contain only
LoginMenu and CharacterSelect.

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
| Northeast highlands | X `96` to `224`, Z `96` to `224` | Unity Terrain with rolling hills, a 20 m peak, and an arched tunnel through the foot of the peak |

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

Every raised surface on the north traversal course is reachable by walking,
because the character never jumps and steps up at most 0.35 m:

| Course element | Top height | Walking route |
| --- | --- | --- |
| West platform | `4` | A 10 degree ramp from the south, centered on X `-30`, ends flush with the platform's south face at Z `89` |
| East platform | `6` | A 10 degree ramp from the south, centered on X `30`, ends flush with the platform's south face at Z `127` |
| Observation bridge | `8` | 25 observation steps of 0.32 m with 1 m treads climb from Z `115` to Z `140`, where the bridge starts |
| Observation deck | `8` | The bridge runs level from Z `140` onto the deck's south edge at Z `165` |

The bridge leaves 7.2 m of headroom, so the ground below it stays walkable. When
the course changes, keep each ramp top flush with its platform and every rise
below the step height, then rebake. `MovementPathPlannerTests` plans from the
ground to each platform, the bridge, and the deck against the baked collision
and follows each route with the shared movement simulation.

## Northeast Highlands

The highlands are the terrain test area. They are a Unity Terrain baked as a
collision heightfield, so hills, a mountain, and a tunnel use the same
authoritative movement and route planning as the boxes. The builder computes
every height from a fixed function of the position, so a rebuild reproduces the
same terrain.

| Element | Position or area | Notes |
| --- | --- | --- |
| Terrain | origin `(96, 0, 96)`, size `(128, 40, 128)` | Heightmap resolution `129`, one sample per meter, at least 0.02 m above the ground box |
| Rolling hills | four smooth hills up to 4 m high | Steepest slopes below 19 degrees |
| Peak | summit `(154, 20, 188)` | Faces of about 31 degrees. The east face is the only way up |
| Cliffs | north, south, and west sides of the peak | About 61 degrees, too steep to climb |
| Tunnel | X `143` to `149`, Z `170` to `206` | Arched tunnel 6 m wide and 6.5 m high, along Z through the foot of the peak |
| South portal | Z `170` to `178` | Terrain holes at Z `172` to `177` open the south cliff |
| North portal | Z `198` to `206` | Terrain holes at Z `199` to `204` open the north cliff |

Terrain layers paint grass where the ground is 40 degrees or flatter and rock
where it is steeper. Rock means a slope the character cannot walk up, because
the walkable limit is 45 degrees.

Collision triangles are one-sided. The terrain faces up, so a walker inside the
tunnel passes below it. The tunnel arch faces in, so a walker on the peak passes
above the arch. The arch walls are the only collision that keeps a walker in the
tunnel. Each portal has two wing walls and a crown of boxes around the arch.
Together they seal the edges of the terrain holes, so the space inside the peak
cannot be reached. The terrain, the arch mesh, and the terrain layers are
generated assets below `Assets/Art/Terrain/DevelopmentWorld2`.

`MovementPathPlannerTests` plans against the baked collision to a hilltop, to
the summit from the east and from the southeast around the south cliff, through
the tunnel, and down the west face onto the terrain above the tunnel. It also
checks that the tunnel walls keep the inside of the peak unreachable.

## Collision Inventory

Development World 2 currently contains 112 enabled, non-trigger BoxColliders,
one non-convex MeshCollider for the tunnel arch, and one TerrainCollider. They
bake to 112 boxes and 32,676 one-sided triangles: 128 by 128 terrain cells less
60 portal holes, two triangles per cell, plus 28 arch triangles.
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
| Grass green | Walkable highlands terrain |
| Rock grey | Highlands slopes too steep to walk |

The client renders with the Universal Render Pipeline, so the palette builder
creates `Universal Render Pipeline/Lit` materials. It still selects the shader
from the active render pipeline and would fall back to the Standard shader only
if no URP Pipeline Asset were active.

## Rebuild and Authoring Workflow

The checked-in editor builder reproduces the complete environment without
touching `Gameplay`, actor authoring, or actor runtime content.

1. Open `src/Client` in Unity and allow asset import to finish.
2. Open `Assets/Scenes/DevelopmentWorld2.unity`.
3. To restore the complete checked-in layout, select `MMORPG > Tools >
   Development Worlds > Rebuild Development World 2 Environment` and confirm
   `Rebuild`. This intentionally replaces every child under `Environment`. If
   an open scene has unsaved changes, Unity first asks whether to save them,
   and cancelling stops the rebuild.
4. To customize the greybox, edit the generated children under `Environment`.
   Keep authoritative geometry as enabled, non-trigger BoxColliders, non-convex
   MeshColliders, or TerrainColliders, and follow the rules in Terrain And Mesh
   Collision Authoring. Decorative route surfaces and cyan guides must remain
   collider-free.
5. Keep `PlayerSpawn`, all service markers, the guard marker, the wolf spawn
   guide, and the documented wolf patrol points free of blocking geometry.
6. Save the scene. Select `MMORPG > Tools > World Collision > Bake Open
   Scene`.
7. Confirm the Console reports a successful bake for
    `development-world-2`. The bake must create
    `content/worlds/development-world-2/collision.json` and
    `src/Shared/WorldData/Generated/WorldCollision/development-world-2`.
8. Keep `DevelopmentWorld2` an entry with the address `DevelopmentWorld2` in the
   `World Scenes` Addressables group, and out of `File > Build Profiles`. `MMORPG
   > Tools > Addressables > Apply Client Layout` restores both.

Expected result: Development World 1 remains playable, the Console reports
Development World 2 with 112 boxes, 1 mesh, and 1 heightfield in 256 chunks, and
actor authoring files remain unchanged. Both scenes are selected exclusively through their authoritative
World IDs.

## Terrain And Mesh Collision Authoring

These rules apply to every World scene, including a future production map.

- Sculpt terrain with a Unity Terrain below the collision root. Keep the Terrain
  unrotated and unscaled. The bake reads every heightmap sample and every
  terrain hole.
- Hills and mountains need no extra collision. Ground steeper than 45 degrees
  blocks like a wall, so paint it so players can tell it apart from walkable
  ground.
- Model caves, overhangs, and tunnel interiors as MeshColliders with Convex
  cleared. Each triangle collides only from its front, the side whose rendered
  face is visible. Point every face toward where players walk.
- Where a tunnel enters terrain, cut terrain holes for its opening. Then seal
  every gap between the hole edges and the tunnel with boxes or front-facing
  mesh walls. The space below terrain is otherwise open, because terrain only
  collides from above.
- Leave the world ground box or terrain below tunnel floors, or give the tunnel
  mesh its own floor that faces up.

## Post-Authoring Verification

From the repository root, verify the new collision outputs:

```powershell
dotnet run --project tools/WorldCollisionCompiler --configuration Release --no-build -- --verify
powershell -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result: the collision verifier checks every World and reports
`development-world-2` with 256 chunks and a deterministic revision, and all Unity EditMode and
PlayMode tests pass. `Bake World Scenes` also verifies every catalog mapping
to an Addressables World scene before writing collision output.

The authored baseline has passed review. The local worker configuration now
requests Development World 2 for `local-shard-1`. Future changes between
development Worlds remain separate offline operational steps that GameService
validates and commits on the replacement worker's first heartbeat.

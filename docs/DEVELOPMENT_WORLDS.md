# Development Worlds

Last updated: 2026-10-26

## Purpose

This document defines the two checked-in development Worlds, what each one is
for, their layout, and how their scenes are rebuilt and baked. A World is
reusable content. A shard references one World at a time and may change that
reference only while the shard is fully offline and drained.

## Checked-In World Registry

| World ID | Unity scene | Purpose | Local shard | Playable bounds | Authoritative spawn |
| --- | --- | --- | --- | --- | --- |
| `development-world-1` | `DevelopmentWorld1` | Stress arena: many bots and clients on a small area | `local-shard-2`, `Local Stress Arena` | X/Z `-31` to `31` | `(0, 0, 0)`, yaw `0` |
| `development-world-2` | `DevelopmentWorld2` | Test World: one zone for every developed feature | `local-shard-1`, `Local Shard 1` | X/Z `-190` to `190` | `(0, 0, -12)`, yaw `0` |

The checked-in SimulationWorker configuration requests `development-world-2`
for `local-shard-1`, and its `StressArena` launch profile requests
`development-world-1` for `local-shard-2` on UDP port `27016`, so both Worlds
run side by side; see
[Run The Stress Arena Shard](LOCAL_DEVELOPMENT.md#run-the-stress-arena-shard).
GameService accepts and persists a shard's World binding only when the shard is
offline and drained.

The client catalog maps each World id to its scene, and both scenes are
Addressables World scenes. Build Profiles contain only LoginMenu and
CharacterSelect.

Both scenes are built by checked-in editor builders, so neither is edited by
hand. `world.json` is the only source of the spawn and the playable bounds: the
builders read it to place `PlayerSpawn` and the boundary walls, and
SimulationWorker reads it to spawn characters and clamp movement.
`actor-spawns.json` is the only source of NPC, Mob, and resource node
positions; the layouts only keep those positions clear. A resource node blocks
movement with its collision box, which the collision compilers add to the
World's collision from the compiled actors.

## Shared Look

The Worlds are a readable greybox, not final art. The colors mean the same
thing in both:

| Color | Meaning |
| --- | --- |
| Green with a grid | Ground. Thin lines every meter, a strong line every 4 meters |
| Light brown | Paths, roads, and zone floors that are only paint |
| Light grey | Town square, booth, and arena floors |
| Blue grey | Buildings, platforms, walls, and pillars |
| Brick red | Roofs |
| Orange | Ramps, stairs, cover, crates, and the bridge |
| Bright green | A movement test the character gets through |
| Bright red | A movement test the character must not get through |
| Brown grey | Rock |
| Dark brown and dark green | Tree trunks and crowns |
| Teal | The sandbox pad reserved for new features |
| Cyan | Spawn pads, range stripes, and interest markers |
| Charcoal | Boundary walls |

Signs on two posts name every zone and test station, and floor labels give the
measured values, such as `0.34 m` or `42 deg`. Labels are world-space uGUI text
with the built-in font: they hide behind walls like any other surface and never
take pointer input. Sign posts, buildings, rocks, and tree trunks collide;
roads, floor paint, sign boards, labels, and tree crowns do not.

## Development World 1: Stress Arena

A 64 by 64 meter walled floor whose diagonal of about 88 m stays inside the
128 m interest enter radius, so every player, bot, NPC, and Mob is visible to
everyone at once. This is the worst case for replication, interest, and Mob
AI load.

| Element | Position or area | Notes |
| --- | --- | --- |
| Ground | `(0, -0.25, 0)`, size `(64, 0.5, 64)` | Four collision chunks |
| Boundary walls | 3 m high, inner faces on X/Z `±31` | |
| Spawn and loot hotspot | `(0, 0, 0)`, 8 by 8 m cyan pad | All bots spawn here; the stress loot hotspot is the spawn |
| Open floor | The center | The 10 m square that stress bots walk from the spawn, X and Z `0` to `10`, stays free |
| Pillars | `(±14, 0, ±14)`, 2 by 3 by 2 m | Give bot route planning detours |
| Low cover | `(±8, 0, -12)`, 6 by 1.2 by 0.8 m | |
| High cover | `(±20, 0, 17)`, 4 by 3 by 0.8 m | |
| Service booth | Mira at `(0, 0, 25)` facing the spawn | Trade, bank, recovery, insurance, and quests |
| Gathering field | Nine Iron Veins, 3 by 3, 4 m apart, centered at `(24, 0, 0)` | Shared veins for gathering contention |
| Wolf pen | X `-31` to `-17`, Z `-7` to `7`, 2 m walls with a 5 m opening to the east | Four Feral Wolves patrol inside and attack players near the opening |
| Platform | 1 m high, X `4` to `16`, Z `-27` to `-21` | A 10 degree ramp from the north and four 0.25 m stairs from the south |

## Development World 2: Test World

A 384 by 384 meter World with a walled town in the center and one test zone in
each direction, 30 to 110 m from the town, so every zone is a short walk away.

```text
                         North (+Z)
+------------------+------------------+------------------+
| Sandbox          | Movement course  | Highlands        |
| reserved pad     | steps, slopes,   | hills, peak,     |
|                  | gaps, stairs,    | cliffs, tunnel   |
|                  | bridge, maze,    |                  |
|                  | camera tests     |                  |
+------------------+------------------+------------------+
| Interest road    | Town             | Combat range     |
| to the outpost   | spawn, Mira,     | PvP arena,       |
| 172 m away       | guards, houses   | range lane       |
+------------------+------------------+------------------+
| Wolf woods       | Meadow           | Quarry           |
| wolves and the   | open ground,     | seven Iron Veins |
| alpha lair       | oak grove        |                  |
+------------------+------------------+------------------+
```

### Town

| Element | Position or area | Notes |
| --- | --- | --- |
| Town walls | 3 m high, inner faces on X/Z `±36`, 10 m gates in the middle of each side, corner towers | |
| Spawn | `(0, 0, -12)`, cyan pad | Faces Mira |
| Mira the Quartermaster | `(0, 0, 6)`, facing south | The service hall stands behind her |
| Town guards | `(-4, 0, -33)` and `(4, 0, -33)` | Inside the south gate |
| Service hall | X `-9` to `9`, Z `12` to `20`, 6 m high | Routes around it; a click on its roof ends beside it |
| Northwest house | X `-28` to `-16`, Z `17` to `27` | Solid |
| Arcade | Four pillars around X/Z `16` to `28` with a roof at 4 m | Walk under a roof; camera under a roof |
| Open house | X `-27.5` to `-16.5`, Z `-27.5` to `-16.5`, doorway on the east side at Z `-23.2` to `-20.8` | Walk into a room through a door |
| Crates | Around `(23, 0, -23)` | Small obstacles |

### Movement Course

North of the town, X `-60` to `50`, Z `64` to `144`. Each station sits just
inside or just outside a movement limit of the shared simulation: the 0.35 m
step height, the 45 degree slope limit, and the 0.7 m body width.

| Station | Position | What it shows |
| --- | --- | --- |
| Step heights | 3 by 3 m pads at X `-20`, `-12`, `-4`, `4`, `12`, south faces at Z `76.5` | Rises of 0.15, 0.25, and 0.34 m are climbed; 0.36 and 0.50 m are not |
| Slopes | Ramps at the same X onto separate 2 m pads, pad south faces at Z `96` | 20, 35, and 42 degrees are climbed; 48 and 60 degrees are not |
| Gaps | A 2.5 m wall at Z `112` from X `0` to `24` with gaps at X `4.25`, `12.25`, `20.25` | 1.5 and 1.0 m gaps are walked through; a 0.6 m gap is walked around |
| Stairs and ledge | Ten 0.30 m steps at X `-32` from Z `108` to a 3 m platform | Routes never step off the ledge: they only drop as far as the 0.4 m ground snap distance, so the way down is the stairs |
| Bridge | 4 m platforms at X `-20` and `20`, Z `136` to `144`, joined by a 4 m wide bridge | The west platform has a 10 degree ramp, the east one 0.25 m stairs; the bridge leaves 3.5 m of headroom below |
| Maze | X `-58` to `-34`, Z `76` to `100`, 6 m lanes | In from the south, out to the north |
| Camera tests | A corridor at X `44`, Z `80` to `92`, with a 2.6 m ceiling, and a 2 m alley between 8 m walls at X `40` to `42`, Z `100` to `116` | Camera collision in tight spaces |

The route planner searches a 0.5 m grid whose cell centers lie on odd
multiples of 0.25 m, and it keeps 0.4 m of clearance from walls. A gap narrower
than 1.3 m is therefore routed only where a cell center has that clearance on
both sides, which is why the station gaps are centered on cell centers.

### Highlands

The terrain test area. A Unity Terrain baked as a collision heightfield, so
hills, a mountain, and a tunnel use the same authoritative movement and route
planning as the boxes. The builder computes every height from a fixed function
of the position, so a rebuild reproduces the same terrain.

| Element | Position or area | Notes |
| --- | --- | --- |
| Terrain | origin `(60, 0, 60)`, size `(128, 40, 128)` | Heightmap resolution `129`, one sample per meter, at least 0.02 m above the ground box |
| Rolling hills | four smooth hills up to 4 m high | Steepest slopes below 19 degrees |
| Peak | summit `(118, 20, 152)` | Faces of about 31 degrees. The east face is the only way up |
| Cliffs | north, south, and west sides of the peak | About 61 degrees, too steep to climb |
| Tunnel | X `107` to `113`, Z `134` to `170` | Arched tunnel 6 m wide and 6.5 m high, along Z through the foot of the peak |
| South portal | Z `134` to `142` | Terrain holes at Z `136` to `141` open the south cliff |
| North portal | Z `162` to `170` | Terrain holes at Z `163` to `168` open the north cliff |

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

### Combat Range

| Element | Position or area | Notes |
| --- | --- | --- |
| PvP arena | 40 by 40 m around `(100, 0, 0)` | A 1.2 m wall that eyes see over, 6 m openings to the west and east, four 4 m full-cover pillars, and two 1 m low covers |
| Range lane | X `130` to `165` at Z `24` to `36` | A cyan start stripe and a stripe every 5 m, labeled up to 35 m, for measuring attack range |

Wolves are the combat targets in Development World 2, and other players are
the targets in the arena when the Shard allows player versus player.

### Interest Road And Outpost

A road leads west from the town to an outpost at `(-172, 0, 0)` with a guard at
`(-170, 0, 0)`. Cyan markers and signs stand at 64 m (the collision streaming
radius of two 32 m chunks), 128 m (the interest enter radius), and 144 m (the
interest exit radius) from the town center. The outpost is more than 160 m from
the spawn and every town actor, so walking out removes the town from the
client and brings the guard in, and walking back reverses it.

### Wolf Woods

| Element | Position or area | Notes |
| --- | --- | --- |
| Feral Wolves | Five in the spawn area X/Z `-120` to `-90` | They patrol `(-116, -116)`, `(-94, -110)`, `(-100, -92)`, and `(-118, -96)` |
| Trees | 16 trees around the wolf area | Trunks collide; crowns only block the view |
| Alpha lair | A ring of seven rocks around `(-160, 0, -160)`, open to the northeast | The Feral Alpha boss stands in the middle |

### Quarry

Seven Iron Veins: a 2 by 2 group 6 m apart around `(84, 0, -84)` and single
veins at `(104, 0, -76)`, `(76, 0, -104)`, and `(106, 0, -106)`, inside rock
walls on the south and east sides.

### Meadow And Sandbox

The meadow south of the town is open ground with six greybox trees, for crowds
of bots and players. East of them, toward the quarry, the oak grove holds four Oak Trees, the
resource nodes that a Woodcutting Axe chops down, at `(38, 0, -80)`,
`(48, 0, -88)`, `(32, 0, -92)`, and `(42, 0, -100)`, with an `OAK GROVE` sign at
`(28, 0, -74)`. The sandbox is a 100 by 100 m teal pad centered at
`(-120, 0, 120)`, kept free for features that need space of their own.

### Resource Node Placements

Resource nodes are actors, so `actor-spawns.json` places them; the builders only
keep their ground clear and sign their zones. Each one blocks movement with the
collision box of its definition, aligned to the World axes, which
`WorldCollisionCompiler` and the Unity bake add to the World's collision on the
movement layer only: characters and Mobs walk around a vein or an oak's trunk,
while sight and arrows pass it.

| World | Resource node | Placement |
| --- | --- | --- |
| `development-world-1` | Iron Vein | Nine in the gathering field, 3 by 3, 4 m apart, centered at `(24, 0, 0)` |
| `development-world-2` | Iron Vein | Seven in the quarry: a 2 by 2 group 6 m apart around `(84, 0, -84)` and single veins at `(104, 0, -76)`, `(76, 0, -104)`, and `(106, 0, -106)` |
| `development-world-2` | Oak Tree | Four in the oak grove: `(38, 0, -80)`, `(48, 0, -88)`, `(32, 0, -92)`, and `(42, 0, -100)`, turned 0, 70, 140, and 215 degrees |

## Rebuild And Bake Workflow

The checked-in editor builders reproduce each environment completely. They
never touch `Gameplay` content other than the `PlayerSpawn` transform, and they
never change actor content. A builder writes a palette material only when its
color, texture, or smoothness changes, and then in the form the shader's
Inspector keeps, since URP Lit derives the legacy `_Color` from `_BaseColor`
with slightly different floats whenever it validates a material; a rebuild with
the same palette leaves every material file as it is.

1. Open `src/Client` in Unity and allow asset import to finish.
2. Select `MMORPG > Tools > Development Worlds > Rebuild Development World 1
   Environment` or `Rebuild Development World 2 Environment` and confirm
   `Rebuild`. The builder opens the scene, replaces every child under
   `Environment`, moves `PlayerSpawn` to the manifest spawn, and saves the
   scene. If an open scene has unsaved changes, Unity first asks whether to save
   them, and cancelling stops the rebuild.
3. Select `MMORPG > Tools > World Collision > Bake World Scenes`.
4. Confirm the Console reports `development-world-1` with 30 boxes, 0 meshes,
   and 0 heightfields in 4 chunks, and 9 resource node boxes, and
   `development-world-2` with 201 boxes, 1 mesh, and 1 heightfield in 144
   chunks, and 11 resource node boxes. The bake compiles the World actors from
   their authoring and stops when they differ from the compiled ones, so save
   actor changes in Actor Studio or Spawn Authoring first.

To change a layout, edit the builder in
`src/Client/Assets/Editor/DevelopmentWorlds` instead of the scene, then rebuild
and bake. To move the spawn or the bounds, edit `world.json` first. To move an
actor, edit `actor-spawns.json` and run the World Actor Compiler. Keep every
actor position, patrol point, and the spawn on clear, flat ground; the layout
tests check it.

Keep both scenes entries with their scene names as addresses in the `World
Scenes` Addressables group, and out of `File > Build Profiles`. `MMORPG > Tools
> Addressables > Apply Client Layout` restores both.

## Layout Tests

`tests/Shared/Mmorpg.Shared.Tests/DevelopmentWorldLayoutTests.cs` checks the
baked collision of both Worlds with the shared route planner and movement
simulation:

- Every actor spawn, patrol point, and the World spawn lies inside the bounds
  on ground, with a free 0.6 m radius column above the step height in the
  scene's own collision.
- The tests read the compiled collision chunks, as the server and the client
  do. Every resource node has its box there, on the movement layer only, and a
  character walking straight at the north oak of the grove stops at its trunk,
  while a planned route walks around it.
- The stress arena fits inside one interest radius, and the stress bot square
  from its spawn is free.
- The stress arena hotspots are reachable from its spawn.
- Every movement course station behaves as its label says.
- The town, bridge, maze, camera tests, arena, and highlands routes work, and
  the inside of the peak stays unreachable.
- The outpost lies beyond the interest exit radius from the town.

The Unity `AuthoredGameplayAssetTests` check that both scenes keep the World
scene composition, that `PlayerSpawn` and the boundary walls match the
manifest, that the labels are world-space text without raycasts, that every
development World material is used, that every project material is in the form
its shader's Inspector keeps, so selecting one never changes its file, and that
the checked-in bake holds exactly
the scene's boxes, terrain cells, and mesh triangles, and one movement-only box
per placed resource node.

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
dotnet test tests/Shared/Mmorpg.Shared.Tests --configuration Release --filter "FullyQualifiedName~DevelopmentWorldLayoutTests"
```

Expected result: the collision verifier reports `development-world-1` with 4
chunks and `development-world-2` with 144 chunks and deterministic revisions,
and every layout test passes. Then run the Unity EditMode and PlayMode tests as
described in [Local Development](LOCAL_DEVELOPMENT.md). `Bake World Scenes`
also verifies every catalog mapping to an Addressables World scene before
writing collision output.

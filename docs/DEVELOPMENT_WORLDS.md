# Development Worlds

The repository has two development Worlds: a test World with a zone for every
developed feature, and a stress arena that keeps every player and actor in one
interest radius. This document describes what each one is for, their layouts,
how their scenes are composed, rebuilt, and baked, the World Builder, which makes
a World's water from shapes, and the rules for authoring World collision and
water.

A World is reusable content. A shard references one World at a time and may
change that reference only while the shard is fully offline and drained; see
[Rebind An Offline Shard](LOCAL_DEVELOPMENT.md#rebind-an-offline-shard-to-another-world).

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

The client's World scene catalog, `Assets/Content/Worlds/world-scene-catalog.json`,
maps each World id to its scene, and both scenes are Addressables World scenes.
Build Profiles contain only LoginMenu and CharacterSelect; see
[Scene And UI Flow](UNITY_CLIENT_ARCHITECTURE.md#scene-and-ui-flow).

Both scenes are built by checked-in editor builders, so neither is edited by
hand. Each World's content lives in `content/worlds/<WorldId>`:

- `world.json` is the only source of the spawn and the playable bounds. The
  builders read it to place `PlayerSpawn` and the boundary walls, and
  SimulationWorker reads it to spawn characters and clamp movement.
- `actor-spawns.json` is the only source of NPC, Mob, and resource node
  positions; the layouts only keep those positions clear. A resource node blocks
  movement with its collision box, which the collision compilers add to the
  World's collision from the compiled actors.
- `collision.json` is the collision the bake writes from the scene; see
  [Rebuild And Bake Workflow](#rebuild-and-bake-workflow).

## Shared Look

The Worlds are a readable greybox, not final art. The colors mean the same thing
in both:

| Color | Meaning |
| --- | --- |
| Green with a grid | Ground. Thin lines every meter, a strong line every 4 meters |
| Light brown | Paths, roads, and zone floors that are only paint |
| Light grey | Town square, booth, and arena floors |
| Blue grey | Buildings, platforms, walls, and pillars |
| Brick red | Roofs |
| Orange | Ramps, stairs, cover, crates, and the bridges |
| Bright green | A movement test the character gets through |
| Bright red | A movement test the character must not get through |
| Brown grey | Rock |
| Dark brown and dark green | Tree trunks and crowns |
| Water | The stylized water materials of [Water](ART_PIPELINE.md#water) |
| Sand | Beaches, banks, and the ground under water |
| Cyan | Spawn pads, range stripes, and interest markers |
| Charcoal | Boundary walls |

Signs on two posts name every zone and test station, and floor labels give the
measured values, such as `0.34 m` or `42 deg`. Labels are world-space TextMesh
Pro text in the UI font, sharp at any distance: they hide behind walls like any
other surface and never take pointer input. Sign posts, buildings, rocks, and tree trunks collide;
roads, floor paint, sign boards, labels, and tree crowns do not.

## Development World 1: Stress Arena

A 64 by 64 meter walled floor whose diagonal of about 88 m stays inside the
128 m interest enter radius, so every player, bot, NPC, and Mob is visible to
everyone at once. This is the worst case for replication, interest, and Mob AI
load.

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
| Waterside        | Movement course  | Highlands        |
| lake, river,     | steps, slopes,   | hills, peak,     |
| ford, bridge,    | gaps, stairs,    | cliffs, tunnel   |
| sea at the       | bridge, maze,    |                  |
| World's edge     | camera tests     |                  |
+------------------+------------------+------------------+
| Interest road    | Town             | Combat range     |
| to the outpost   | spawn, Mira,     | PvP arena,       |
| 172 m away       | guards, houses   | range lane       |
+------------------+------------------+------------------+
| Wolf woods       | Meadow           | Quarry           |
| wolves and the   | Grey Wolves,     | seven Iron Veins |
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

The route planner searches a 0.5 m grid whose cell centers lie on odd multiples
of 0.25 m, and it keeps 0.4 m of clearance from walls. A gap narrower than 1.3 m
is therefore routed only where a cell center has that clearance on both sides,
which is why the station gaps are centered on cell centers.

### Highlands

The terrain test area. A Unity Terrain baked as a collision heightfield, so
hills, a mountain, and a tunnel use the same authoritative movement and route
planning as the boxes. The builder computes every height from a fixed function of
the position, so a rebuild reproduces the same terrain.

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
where it is steeper. Rock means a slope the character cannot walk up, because the
walkable limit is 45 degrees.

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

Wolves are the combat targets in Development World 2, and other players are the
targets in the arena when the Shard allows player versus player.

### Interest Road And Outpost

A road leads west from the town to an outpost at `(-172, 0, 0)` with a guard at
`(-170, 0, 0)`. Cyan markers and signs stand at 64 m (the collision streaming
radius of two 32 m chunks), 128 m (the interest enter radius), and 144 m (the
interest exit radius) from the town center. The outpost is more than 160 m from
the spawn and every town actor, so walking out removes the town from the client
and brings the guard in, and walking back reverses it.

### Wolf Woods

| Element | Position or area | Notes |
| --- | --- | --- |
| Feral Wolves | Five in the spawn area X/Z `-120` to `-90` | They patrol `(-116, -116)`, `(-94, -110)`, `(-100, -92)`, and `(-118, -96)` |
| Trees | 16 trees around the wolf area | Trunks collide; crowns only block the view |
| Alpha lair | A ring of seven rocks around `(-160, 0, -160)`, open to the northeast | The Feral Alpha boss stands in the middle |

### Quarry

Seven Iron Veins: a 2 by 2 group 6 m apart around `(84, 0, -84)` and single veins
at `(104, 0, -76)`, `(76, 0, -104)`, and `(106, 0, -106)`, inside rock walls on
the south and east sides.

### Meadow

The meadow south of the town is open ground with six greybox trees, for crowds of
bots and players. Three Grey Wolves, defensive Mobs that only fight back, stand
in its spawn area X `-20` to `-4`, Z `-100` to `-84`, so a yellow nameplate can
be seen and a Mob can be fought without being attacked first. East of them, toward the quarry, the oak grove holds four Oak
Trees, the resource nodes that a Woodcutting Axe chops down, at `(38, 0, -80)`,
`(48, 0, -88)`, `(32, 0, -92)`, and `(42, 0, -100)`, with an `OAK GROVE` sign at
`(28, 0, -74)`.

### Waterside

The water test area in the northwest: a Unity Terrain baked as a heightfield,
four water bodies, a waterfall, a bridge, and the sea where the World ends. The
builder computes every height from a fixed function of the position, so a
rebuild reproduces the same terrain and water. A `WATERSIDE` sign stands at
`(-60, 0, 60)`, and signs name the lake, the waterfall, the ford, the bridge,
and the sea.

| Element | Position or area | Notes |
| --- | --- | --- |
| Terrain | origin `(-192, 0, 64)`, size `(128, 8, 128)` | A 4 m plateau with one sample per meter that ramps down to the ground over 16 m at its east and south edges |
| Lake `waterside-lake` | center `(-108, 112)`, surface at 3.6 m | The shore lies 13 m from the center and the floor falls 0.3 m per meter, so the water is 0.5 m deep at 11.3 m, 1.2 m deep at 9 m, and at most 3 m deep; the bank rises to the plateau over 2 m |
| Waterfall outcrop | lip at `(-103.5, 124.5)` on the lake's north shore, facing the lake | Rock 10 m wide and 9 m deep with its top at 7.6 m, 4 m above the lake, and a ramp onto it from behind; a channel 3 m wide runs along its top from a spring under a boulder to the lip |
| Stream `waterside-stream` | along the outcrop's channel, surface at 7.5 m | 0.3 m deep, flowing toward the lip ever faster and foaming where it tips over; its surface reaches 0.1 m under the walls up to the lip and 5 cm past it, 2 cm inside the channel |
| Waterfall and pool | the water falls 3.9 m from the lip and lands 1.4 m out, at `(-103.9, 123.2)` | A column that starts 0.6 m before the lip with its top in the stream's surface and fades in over it, as wide as the channel, 3 m, and 0.3 m thick and nearly square where the stream's surface ends, that rounds and grows to 3.7 by 0.9 m as it falls; the lake is 1 m deep in a pool around the landing, so it is waded under the fall; the water churns and foams there, white water boils up, mist rises, and droplets fly |
| River `waterside-river` | from `(-113, 119)` in the lake to `(-149, 150)` at the sea | 8 m wide and 2 m deep; its surface stays level with the lake until it leaves it, then falls to the sea's level, which it reaches at its mouth |
| Ford | `(-127.5, 132.8)`, 20 m down the river | 0.8 m deep for 6 m of the river, so it is waded across at the wading speed |
| Bridge | `(-140, 142.8)`, 36 m down the river | A 16 by 4 m deck with its top at 4.2 m, 1 m above the water, from plateau to plateau |
| Sea `waterside-sea` | a quarter disc around the corner `(-192, 192)`, surface at 3 m | The shore lies 60 m from the corner and the floor falls 0.15 m per meter, so the water is 1.2 m deep 52 m from the corner |
| Boundary walls | the north wall starts at X `-146` and the west wall ends at Z `146` | Both end out in deep water, so the sea is the edge of the World between them |
| Sea beyond the World | west and north of the bounds, out to 400 m | Shows only, over a sand floor that does not collide |

Terrain layers paint rock where the ground is steeper than 40 degrees, sand on
the beds and banks of the water, and grass elsewhere; each cell of the 0.5 m
layer map takes the share of its 16 samples that lie on a water bed, so the sand
blends into the grass. The terrain and its layers are generated assets below
`Assets/Art/Terrain/DevelopmentWorld2`.

The water is [World Builder](#world-builder) shapes below `Waterside/Water`: the
lake a `LakeShape` with a round shore of 48 points, the river and the stream
`RiverShape`s, the sea a `SeaShape` whose outline is the quarter disc and whose
horizon sides are west and north, and the waterfall a `WaterfallShape` from the
stream into the lake. The builder sets their values, and the World Builder
writes their surface meshes, the sea beyond the World and its seabed, and the
waterfall's column into the mesh assets next to the terrain. All water surfaces
draw with `M_Water` and the waterfall with `M_Water_Waterfall`, and the surface
meshes carry the data of the [water shader](ART_PIPELINE.md#water): the river
and the stream their flow, faster in the middle and fastest over the ford and
toward the lip, and their foam over the ford and at the lip; the sea its waves
and the foam lines that roll onto its beach; and the lake, a grid of 1 m cells,
the churn and spread around the waterfall's landing. The river's water is still
where it reaches into the lake and into the sea, and the sea's waves and foam
lines calm down at the river's mouth and toward the sea beyond the World, so
every pair of surfaces meets without a seam, and the waterfall's column covers
the end of the stream's surface with its flat top, as `WatersideJunctionTests`
checks.

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

## World Scene Composition

Every World scene has three root objects:

- `Environment`, everything the builder generates: the ground, the boundary
  walls, the zones, signs, and labels. It carries the scene's
  `WorldCollisionAuthoring`, and every collider below it that the bake reads is
  World collision.
- `Gameplay`, with `PlayerSpawn`, placed at the spawn of the World's
  `world.json`, and a `WorldSceneContext` object whose component references the
  `LocalPlayer.prefab` and `RemotePlayer.prefab` assets and `PlayerSpawn`. The
  builders change nothing in `Gameplay` apart from the `PlayerSpawn` transform.
- One `Directional Light`.

A World scene contains no LocalPlayer instance and no camera. After an accepted
join, `WorldSceneContext` creates one LocalPlayer from the assigned prefab at
`PlayerSpawn`, connects it to the authoritative movement state, and connects its
camera, the only gameplay camera and AudioListener. The views of other players
are roots of the World scene, never children of the LocalPlayer. Opening a World
scene directly without a joined session creates no player and reports no
missing reference. Everything else in a running World scene comes from the
client's bootstrap at runtime; see
[Local Player Lifecycle](UNITY_CLIENT_ARCHITECTURE.md#local-player-lifecycle) and
[World Scenes](UNITY_CLIENT_ARCHITECTURE.md#world-scenes).

## Rebuild And Bake Workflow

The builders in `src/Client/Assets/Editor/DevelopmentWorlds` reproduce each
environment completely. A builder opens its scene, configures
`WorldCollisionAuthoring` on the `Environment` root (the World id, chunk size
`32`, the root itself as collision root, and layer mask `3`), replaces every
child of `Environment`, moves `Gameplay/PlayerSpawn` to the spawn and facing in
the World's `world.json`, marks the environment static, the water bodies without
`Batching Static`, and saves the scene. It
never touches other `Gameplay` content and never changes actor content. A
builder writes a palette material only when its color, texture, or smoothness
changes, and then in the form the shader's Inspector keeps, since URP Lit
derives the legacy `_Color` from `_BaseColor` with slightly different floats
whenever it validates a material; a rebuild with the same palette leaves every
material file as it is.

To rebuild and bake both Worlds:

1. Open `src/Client` in Unity, let asset import finish, and exit Play Mode; the
   builders and the bake do not run in Play Mode.
2. Select `MMORPG > Tools > Development Worlds > Rebuild Development World 1
   Environment` and confirm `Rebuild`, then the same for Development World 2. If
   an open scene has unsaved changes, Unity first asks whether to save them, and
   cancelling stops the rebuild. Expected result: the Console reports
   `[DEVELOPMENT WORLD 1] Rebuilt the environment with ...` and
   `[DEVELOPMENT WORLD 2] Rebuilt the environment with ...`, each with its
   collision boxes, meshes, terrains, and water bodies, and `Actor content was
   not changed.`
3. Select `MMORPG > Tools > World Collision > Bake World Scenes` and wait for
   asset import and script compilation to finish.

Expected result: the Console reports `[WORLD COLLISION] Baked world
'development-world-1' at revision <revision> with ... boxes, ... meshes, ...
heightfields, 0 water bodies with 0 deep water edge triangles in 4 chunks, and
... resource node boxes.`, and the same for `development-world-2` with 4 water
bodies and their deep water edges in 144 chunks; the revision is a SHA-256 of
the World's collision. A rebuild and bake without a layout or actor change bakes
the same revisions, so the collision shows no change in Git. A rebuild still
rewrites the tunnel arch mesh and the highlands terrain data with serialization
noise only; restore those two files from Git.

`Bake World Scenes` validates the World scene catalog against the Addressables
World scenes and bakes every mapped World in one deterministic pass, then
restores the scenes that were open. When several Worlds share one reusable
scene, the scene is opened once and baked once for each of those Worlds, and its
`WorldCollisionAuthoring` World id must be one of them.
`MMORPG > Tools > World Collision > Bake Open Scene` bakes only the open scene.

Before it reads a scene, the bake rebuilds the scene's water from its
[World Builder](#world-builder) shapes, so it bakes what the shapes describe, and
it stops with a red error at every error the World Builder's validation finds.
`Bake World Scenes` saves a scene whose water it had to rebuild; after `Bake Open
Scene`, save the scene to keep the rebuilt water.

The bake reads the enabled, non-trigger BoxColliders, non-convex MeshColliders,
and TerrainColliders below the collision root; meshes and terrains bake as
one-sided triangles that collide only from their front. It stops with a red
error for any other collider type, a convex MeshCollider, a MeshCollider without
a mesh, a zero scale, and a rotated or scaled Terrain. It also reads the mesh of
every active water body below the collision root as that body's surface, as
[Water Authoring](#water-authoring) describes, and the shared compiler derives
the deep water edges from the surfaces and the ground below them. It adds one
movement-only box per resource node from the World actors, which it compiles
from their authoring; it stops when they differ from the compiled actors, so
save actor changes in Actor Studio or Spawn Authoring, or run the World Actor
Compiler, first. For each World it writes:

- `content/worlds/<WorldId>/collision.json`, the neutral authoring of the scene.
- `src/Shared/WorldData/Generated/WorldCollision/<WorldId>/manifest.json`.
- One `chunk_*.bytes` file per 32 m chunk in the same generated directory.

The bake writes every file to a temporary `.tmp` sibling first and then swaps
them in with a backup of each previous file. If any write or swap fails, the
previous files are restored, so a failed bake never leaves a manifest whose
checksums do not match its chunks. Unity ignores the `.tmp` files.

To change a layout, edit the builder instead of the scene, then rebuild and
bake. To move the spawn or the bounds, edit `world.json` first. To move an actor,
edit `actor-spawns.json` and run the World Actor Compiler, as
[Compile And Verify](../content/README.md#compile-and-verify) describes. Keep
every actor position, patrol point, and the spawn on clear, flat ground; the
layout tests check it.

Keep both scenes as entries with their scene names as addresses in the
`World Scenes` Addressables group, and out of `File > Build Profiles`.
`MMORPG > Tools > Addressables > Apply Client Layout` restores both; see
[Working Rules For New Content](UNITY_CLIENT_ARCHITECTURE.md#working-rules-for-new-content).

## Layout Tests

`tests/Shared/Mmorpg.Shared.Tests/DevelopmentWorldLayoutTests.cs` checks the
baked collision of both Worlds with the shared route planner and movement
simulation:

- Every actor spawn, patrol point, and the World spawn lies inside the bounds on
  ground, with a free 0.6 m radius column above the step height in the scene's
  own collision.
- The tests read the compiled collision chunks, as the server and the client do.
  Every resource node has its box there, on the movement layer only, and a
  character walking straight at the north oak of the grove stops at its trunk,
  while a planned route walks around it.
- The stress arena fits inside one interest radius, and the stress bot square
  from its spawn is free.
- The stress arena hotspots are reachable from its spawn.
- Every movement course station behaves as its label says.
- The town, bridge, maze, camera tests, arena, and highlands routes work, and the
  inside of the peak stays unreachable.
- The waterside lake stops a walker where it gets too deep and plans a click into
  its middle to the edge, a click across it walks around it instead of wading
  along the deep water, its shore is waded at the wading speed and its bank
  rises from the water, the river is crossed over the bridge without touching
  water and through the ford by wading, never through deep water, the sea is
  deep along the open stretch of both bounds, the waterfall's pool is waded and
  its stream is 0.3 m deep, and the four water bodies are baked with their ids.
- The outpost lies beyond the interest exit radius from the town.

The Unity `AuthoredGameplayAssetTests` check that both scenes keep the World
scene composition, that `PlayerSpawn` and the boundary walls match the manifest,
that the labels are world-space text without raycasts, that every development
World material is used, that every project material is in the form its shader's
Inspector keeps, so selecting one never changes its file, that the waterside has
its lake, river, sea, and stream without colliders, out of static batching, and
with the shared water material, that the waterfall's column and the sea beyond
the World only show, that the World Builder finds no problem in the scene and a
rebuild of its water changes nothing, and that the checked-in bake holds
exactly the scene's boxes, terrain cells, mesh triangles, and water surfaces,
deep water edges where there is water, and one movement-only box per placed
resource node.

## Terrain And Mesh Collision Authoring

These rules apply to every World scene, including a future production map.

- Sculpt terrain with a Unity Terrain below the collision root. Keep the Terrain
  unrotated and unscaled. The bake reads every heightmap sample and every terrain
  hole.
- Hills and mountains need no extra collision. Ground steeper than 45 degrees
  blocks like a wall, so paint it so players can tell it apart from walkable
  ground.
- Model caves, overhangs, and tunnel interiors as MeshColliders with Convex
  cleared. Each triangle collides only from its front, the side whose rendered
  face is visible. Point every face toward where players walk. A mirrored
  transform is allowed; the bake keeps the rendered faces in front.
- Where a tunnel enters terrain, cut terrain holes for its opening. Then seal
  every gap between the hole edges and the tunnel with boxes or front-facing mesh
  walls. The space below terrain is otherwise open, because terrain only collides
  from above.
- Leave the world ground box or terrain below tunnel floors, or give the tunnel
  mesh its own floor that faces up.

## World Builder

The World Builder makes parts of a World from shapes, a few values each, that
the Editor turns into what the World shows and bakes. Its shapes are the water:
lakes, rivers, seas, and waterfalls. A shape is a component in the scene that
does nothing in the game; only what it made takes part, such as a water body's
surface mesh. It works in every World scene, and the Development World 2 builder
makes the waterside's water with it.

Open it with `MMORPG > Tools > World Builder`. The window works on the active
scene:

- `Add` puts a new lake, river, or sea at the middle of the Scene view, at that
  height, below the `Water` group of the scene's collision root, which it
  creates when needed. The new water gets a name and a body id of its own, such
  as `lake-2`, the water material, and the static flags of water, without
  `Batching Static`. `Waterfall from the selected river` lets the selected river
  fall from its last point into the highest lake or sea below it.
- `Shapes` lists the scene's shapes; choose one to select and frame it.
- `Problems` lists what the validation finds; choose one to select its object.
- `Rebuild` rebuilds the scene's water, `Validate` checks it again, and `Bake
  Collision` bakes the open scene, as `World Collision > Bake Open Scene` does.

Shape the selected shape with its handles in the Scene view and its values in
the Inspector:

| Shape | In the Scene view | Values |
| --- | --- | --- |
| `LakeShape` | Drag a point of the shore, Shift+click the shore to add a point there, and Ctrl+click a point to remove it | The shore, a closed outline in the object's X and Z, and the size of the surface's cells; the lake lies at the object's height |
| `SeaShape` | The same for its outline | The outline inside the World, the cell size, how much it waves, over how much water the waves calm down, the horizon sides, how far it runs on beyond the World, and the seabed's material and depth; the sea lies at the object's height |
| `RiverShape` | Click a point to move it with a position handle and drag the cubes on its banks to set its width there, Shift+click the river's line to add a point there or a + past an end to lengthen it, and Ctrl+click a point to remove it | The points from its start to its end, each with the height of the surface, the width, depth, speed, and foam there; a curve or straight lines through them; the spacing of the surface's rows; and how much slower the water flows at the banks |
| `WaterfallShape` | Follows its river's last point | The river it falls from, the water body it falls into, which `Fall Into The Water Below` chooses, the speed at which the water leaves the lip, and how the fall fades in, spreads, foams, and makes the water below churn |

Every lake, river, and sea also has a bank reach: how far its surface reaches
past the shore, under the bank that hides it. Move, turn, or lift a shape's
object with the usual tools, but keep it level and unscaled.

The water follows every change a moment later, also when a shape is moved,
added, or removed, when the ground's colliders change, and after an undo. The
World Builder rebuilds all of a scene's water at once, because water shapes the
water it meets:

- A river whose first or last point lies within 0.5 m of the level of a lake or
  sea whose surface covers it starts or ends in that water: its surface lies at
  that level there and stands still, so they meet without a seam, and a sea's
  waves and foam lines calm down over the river's water.
- A waterfall makes its river's last point a lip exactly as wide as the river,
  falls along the path water takes at its speed, and makes the water it lands in
  churn and spread. White water, mist, and droplets are particle systems below
  it.
- On its horizon sides, a sea runs on beyond the World's ground, where the
  colliders below the collision root end, as a still surface over a seabed that
  only shows, and its waves calm down toward that edge. Its surface ends exactly
  at that edge, cutting the cells the edge crosses, however the sea is moved or
  turned. Draw its outline out to that edge there.
- A shape that is not complete, such as a waterfall without a landing or a
  river with one point, shows nothing until it is. A disabled shape builds
  nothing, and what it made stays as it was.

A rebuild writes a mesh only when it changes, so a rebuild without a change
marks nothing dirty and opening a scene changes nothing. A shape keeps the mesh
asset its MeshFilter holds while no other shape and no other scene uses it. A new
shape in a saved scene, or one whose mesh another shape or scene uses too, such
as a duplicated lake, a shape copied from another scene, or a scene saved under
a new name, gets a mesh asset of its own below `Assets/Art/Worlds/<scene>`, so an
edit never changes another scene's water; a scene that was never saved keeps its
meshes in memory until it is saved and its water rebuilt. A removed shape leaves its mesh
behind: the validation warns about every mesh below `Assets/Art/Worlds/<scene>`
that no scene uses any more, and `Remove Unused Meshes` moves them to the trash.

The validation finds errors, which stop the collision bake, and warnings:

- Errors: a body id that is invalid or used twice; a water body with a collider,
  marked `Batching Static`, without the water shader, or with a mesh that is
  empty or lacks the shader's data; two water bodies that overlap within 0.25 m
  in height but do not agree there; a lake or sea with fewer than three points
  and a river with fewer than two; and a waterfall without a river or a landing
  that makes water, such as a disabled shape, a second one from the same river,
  one into its own river, or one whose landing lies less than 0.1 m below its
  lip.
- Warnings: a disabled shape, a shape that is tilted or scaled, water outside
  the collision root, which the bake leaves out, and unused meshes.

A shape, its handles, and what builds from it make one kind of shape;
[Extension Rules](UNITY_CLIENT_ARCHITECTURE.md#extension-rules) says how a new
kind joins the World Builder.

## Water Authoring

Make lakes, rivers, seas, and waterfalls with the [World Builder](#world-builder):
the water it makes follows these rules in every World scene, and its validation
checks them before every bake.

- A water body is a GameObject below the collision root with a MeshFilter, a
  MeshRenderer, and a `WaterBody` component that names its body id and its kind,
  `Lake`, `River`, or `Sea`. Give it no collider. The body id is unique in its
  World and uses lowercase letters, digits, dots, dashes, and underscores, so
  content such as fishing can refer to it.
- Keep a water body static but out of static batching: clear `Batching Static`.
  Static batching merges surfaces into one mesh that the client cannot read, and
  the client reads each surface to put wakes on its waves. The bake stops with
  an error for a water body that is Batching Static.
- The mesh is the water surface. Every triangle faces up, tilted at most about
  26 degrees, so a river can fall toward the sea. Let the surface reach under
  the banks: the terrain hides it there, and water under terrain or inside a box
  counts as dry land. Water inside a cave below terrain is therefore not
  supported yet.
- Shape the beds and banks with the terrain. The shared compiler samples the
  water every 0.25 m, measures the depth from the surface down to the first
  ground below it, and places one-sided walls, the deep water edges, along the
  line where the water gets deeper than 1.2 m. They face the shallow side, reach
  from 0.5 m below that line to 0.3 m above the surface, and only block
  movement. A character that fell into deep water walks out through their backs.
- Keep the walking surface of a bridge or pier over deep water at least 0.3 m
  above the water, so it passes over the edges.
- Where water bodies overlap, the highest surface counts. Water beyond the ground
  of the World gets no edges, so a sea can reach to the horizon as a mesh
  without a `WaterBody`, which only shows.
- Give the renderer `M_Water`, or one variant of it for all of a zone's water,
  and give the mesh the data the water shader reads: the flow in the second
  texture coordinate, and in the vertex color red where the water foams, green
  where waves lift it, and blue where foam lines roll toward the shore; see
  [Water](ART_PIPELINE.md#water).
- Where two water bodies meet, overlap their surfaces a little and make their
  vertices agree there: the same level, still water, and no waves or foam
  lines. They then meet without a seam.
- A waterfall is a column mesh with the waterfall material and no `WaterBody`,
  from the lip of the water above down to the surface below, where the water
  below foams; shape the ground so a pool lets characters wade under the fall.
  The column starts on the water above, with its top in that surface, and fades
  in over the last half meter or so before the lip. It is as wide as the channel
  the water leaves, never wider, and the water above's surface past the lip lies
  within the column's flat top, where the column has faded in fully. The water
  above then turns into the fall without a line; see
  [Water](ART_PIPELINE.md#water). Make the channel as wide as the river at its
  last point.

## Post-Authoring Verification

From the repository root, verify the checked-in collision against its sources
and run the layout tests:

```powershell
dotnet run --project tools/WorldCollisionCompiler -- --verify
dotnet test tests/Shared/Mmorpg.Shared.Tests --configuration Release --filter "FullyQualifiedName~DevelopmentWorldLayoutTests"
```

Expected result: the compiler reports `Collision data verified for world
development-world-1 at revision <revision> with 4 chunks.` and the same for
`development-world-2` with 144 chunks, at the revisions the bake reported, and
every layout test passes. The compiler exits with code 1 when a World's
collision source and its generated data differ. Then run the Unity tests as
[Run The Unity Tests](LOCAL_DEVELOPMENT.md#run-the-unity-tests) describes.

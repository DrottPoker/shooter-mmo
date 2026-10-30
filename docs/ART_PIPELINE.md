# Art Pipeline

Last updated: 2026-10-30

This document describes how 3D models travel from Blender to the Unity client:
where the files live, the review stages an asset passes before it may reach the
game, the rules the tools enforce, and how Unity imports the result. The
commands, the one-time setup, and the manual test are in
[Local Development](LOCAL_DEVELOPMENT.md#art-pipeline-with-blender).

## Flow

```text
art/<Category>/<Name>/<Name>.blend      source, edited in Blender (Git LFS)
        |  Render-ArtPreview.ps1        review sheet, or live review in Blender
        v
reviewer approves each review stage
        |  Approve-ArtAsset.ps1         records the date and the SHA-256 of the .blend
        v
        |  Export-ArtAsset.ps1          refuses unapproved or changed sources
        v
src/Client/Assets/Art/Models/<Category>/<Name>.fbx       with its clips, if skinned
src/Client/Assets/Art/Textures/<Category>/<Image>.png    painted materials only
        |  ArtModelImportRules          fixed import settings, materials remapped by name,
        |                               Humanoid avatar and clips for a skinned asset
        v
Assets/Prefabs/... Prefab Variant of the model -> presentation registry or player -> game
```

Nothing reaches Unity without an approval of the exact source file that was
exported, and CI checks the whole chain without Blender.

Every asset's `.blend` file is its source, and a person edits it in Blender.
Scripts only write what follows from the source, such as the body parts of the
[human base characters](#human-base-characters) from their editable bodies, and
starting points that a person then edits, such as the gait model's clips in an
[animation library](#animation-libraries). No script replaces handmade work.

## What To Do

Every model is built by the [modeling rules](#modeling-rules), and every kind of
art has one short recipe in
[Local Development](LOCAL_DEVELOPMENT.md#art-pipeline-with-blender). After an
export, Unity creates the project materials an export lacks and updates the
appearance catalogs by itself when it gets focus, so no menu command stands
between the export and the game.

| To | Start with | Recipe |
| --- | --- | --- |
| Add a model for the world, such as a resource node or a prop | `New-ArtAsset.ps1 -Kind static` | [Add A New Asset](LOCAL_DEVELOPMENT.md#add-a-new-asset) |
| Add an attachment, a model held on a socket, such as a weapon or a shield | `New-ArtAsset.ps1 -Kind static -Socket main_hand` | [Make An Attachment](LOCAL_DEVELOPMENT.md#make-an-attachment) |
| Change a human body: its shape, skin texture, or weights | the body's `.blend` file, then `Sync-HumanBase.ps1` | [Edit The Human Base Characters](LOCAL_DEVELOPMENT.md#edit-the-human-base-characters) |
| Add a face or an eye color | an image packed into the body's `.blend` file | [Add A Face Or Eye Variant](LOCAL_DEVELOPMENT.md#add-a-face-or-eye-variant) |
| Change a clip or add one | the library's `.blend` file, or `Sync-AnimationLibrary.ps1 -Generate` for a gait | [Make A Clip](LOCAL_DEVELOPMENT.md#make-a-clip) |
| Start an animation library | `New-ArtAsset.ps1 -Kind animations` | [Make A Clip](LOCAL_DEVELOPMENT.md#make-a-clip) |
| Paint onto the skin, such as a tattoo or scars | `New-ArtAsset.ps1 -Kind paint`, then `Sync-PaintAsset.ps1` | [Make A Paint Layer](LOCAL_DEVELOPMENT.md#make-a-paint-layer) |
| Move a paint layer's canvas to another body, such as a new base model | `Sync-PaintAsset.ps1 -Canvas`, then `Test-PaintProjection.ps1` | [Make A Paint Layer](LOCAL_DEVELOPMENT.md#make-a-paint-layer) |
| Fit the garment cage to the bodies, or move a standard line | `Sync-CageAsset.ps1` | [Fit The Garment Cage](LOCAL_DEVELOPMENT.md#fit-the-garment-cage) |
| Make a garment template, the form of clothes or armor, from a recipe on the cage | `New-ArtAsset.ps1 -Kind garment`, then `Sync-GarmentAsset.ps1` | [Make A Garment](LOCAL_DEVELOPMENT.md#make-a-garment) |
| Paint the look of a garment template, with holes where the skin shows | `New-ArtAsset.ps1 -Kind texture`, then `Sync-TextureAsset.ps1` | [Make A Garment Texture](LOCAL_DEVELOPMENT.md#make-a-garment-texture) |
| Review, approve, and export any of them | `Render-ArtPreview.ps1`, `Approve-ArtAsset.ps1`, `Export-ArtAsset.ps1` | [Review, Approve, And Export An Asset](LOCAL_DEVELOPMENT.md#review-approve-and-export-an-asset) |

Weapons, shields, quivers, and backpacks are [attachments](#attachments): static
models held on a socket of the skeleton.

## Layout

| Path | Responsibility |
| --- | --- |
| `art/<Category>/<Name>/<Name>.blend` | Source of truth for one asset, stored in Git LFS. The folder name is the asset name. |
| `art/<Category>/<Name>/asset.json` | Asset manifest: kind, export target, root objects, triangle budget, skeleton and clips of a skinned asset, approvals, and the export record. |
| `art/Animations/<Name>/<Name>.blend` | An [animation library](#animation-libraries): handmade clips of one skeleton, keyed on the rig of one character, with the bodies that show them. |
| `art/Paint/<Name>/<Name>.blend` | A [paint layer](#paint-layers): transparent paint over the skin of bodies, painted on the layer's own canvas, a copy of a body, and moved to every body by projection. |
| `art/Garments/<Name>/<Name>.blend` | A [garment template](#garments): the form of clothes or armor, built from a recipe on the garment cage and fitted to every body of the cage, one version per body. |
| `art/GarmentTextures/<Name>/<Name>.blend` | A [garment texture](#garment-textures): the look of one template, its image in the template's UVs. |
| `art/Attachments/<Name>/<Name>.blend` | An [attachment](#attachments): a static model below its root `Grip`, held on a socket, such as a sword or a shield. |
| `art/Cages/<Skeleton>/<Skeleton>.blend` | The [garment cage](#garment-cage) of a skeleton: the net of fixed topology that garments are made on, fitted to every body of the skeleton, with its standard lines. |
| `art/Skeletons/<Name>.json` | A shared [skeleton definition](#skeletons): the bones every skinned asset of that skeleton has, their parents, their Unity Humanoid bones, and whether they deform the skin. |
| `src/Client/Assets/Art/Models/<Category>/<Name>.fbx` | Exported model. It is never edited by hand; CI rejects a model without an asset or with a changed file. A body with an editable mesh exports its topology `<Name>.json` next to it, a garment template one model per body, `Models/Garments/<Name>_<Label>.fbx`, with its data `<Name>_<Label>.json` next to it, and a garment texture with transparent parts its own data per body, `Models/Garments/<Name>_<Label>.json`. |
| `src/Client/Assets/Art/Textures/<Category>/<Image>.png` | Exported image of a painted material, byte for byte as it is packed in the `.blend` file. Never edited by hand; CI rejects a texture without an export record or with a changed file. Paint layers export to `Textures/Paint`, each image cropped to its paint, and garment textures to `Textures/Garments`. |
| `src/Client/Assets/Art/Shaders` | `CharacterSkin.shader`, `CharacterSkinInput.hlsl`, `CharacterSkinForwardPass.hlsl`, and `CharacterSkinGBufferPass.hlsl`, the skin shader `Mmorpg/Character Skin`: URP Lit with paint layers read through the paint UV map; `Garment.shader`, `GarmentInput.hlsl`, and `GarmentForwardPass.hlsl`, the garment shader `Mmorpg/Garment`: URP Lit with dye channels, drawing both faces. |
| `src/Client/Assets/Editor/WardrobePreview.cs`, `WardrobeWindow.cs` | `MMORPG > Tools > Art Pipeline > Wardrobe`: a body dressed in chosen garments, playing its clips or a cloth stress motion on a moving ground with the cloth solver running. |
| `src/Client/Assets/Editor/ClothStressMotion.cs`, `ClothMotionMeter.cs`, `ClothMeasurement.cs` | The cloth stress motions, what is measured of the cloth in them, and `MMORPG > Tools > Art Pipeline > Measure Cloth`, which writes `artifacts/cloth-measurements.md`; see [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#measuring-the-cloth). |
| `src/Client/Assets/Editor/ClothKinematicFixtures.cs` | `MMORPG > Tools > Art Pipeline > Write Cloth Fixtures`: writes the kinematic fixtures of [Cloth Parity](#cloth-parity). |
| `src/Client/Assets/Art/Materials` | Unity URP Lit materials. A Blender material maps to the Unity material of the same name. |
| `src/Client/Assets/Art/Animation` | Animator controllers that play the exported clips, such as `HumanLocomotion.controller`. |
| `tools/blender/art_pipeline` | Python that runs inside Blender: validation, FBX export, the review sheet, in `bodies.py` the body parts of a skinned character written from its editable body, and in `poses.py` the test poses of the `Humanoid` skeleton that the rig reviews use. |
| `tools/blender/art_pipeline/cloth.py`, `cloth_core.py` | The cloth chains laid in the target pose that every visible body in the game shows, for the review sheets, review files, and checks: `cloth.py` measures in Blender, `cloth_core.py` lays on plain data with the standard library only; see [Cloth Parity](#cloth-parity). |
| `tools/blender/tests` | Python tests of the art pipeline that run without Blender, and their fixtures. |
| `tools/blender/art_pipeline_cli.py` | Blender entry point for the scripts below. |
| `tools/New-ArtAsset.ps1` | Creates a new static model, animation library, paint layer, garment cage, garment template, or garment texture: its folder, `asset.json`, and first `.blend` file. |
| `tools/Render-ArtPreview.ps1`, `tools/Approve-ArtAsset.ps1`, `tools/Export-ArtAsset.ps1` | Review, approval, and export commands. They share `tools/ArtPipelineCommon.ps1`. |
| `tools/Verify-ArtAssets.ps1` | CI check of every asset without Blender. |
| `tools/Sync-AnimationLibrary.ps1`, `tools/blender/art_pipeline/library.py` | Builds an animation library file from its rig, or updates it when the rig changed, and keeps its clips. |
| `tools/Sync-PaintAsset.ps1`, `tools/blender/art_pipeline/paint.py` | Builds a paint layer file from its bodies, or updates it, keeps its canvas, and moves the canvas's paint to every body by projection; `-Canvas` moves the canvas to another body. |
| `tools/Test-PaintProjection.ps1` | Checks the move that a body of another topology than a paint layer's canvas gets, through the garment cage, on subdivided copies of the bodies. |
| `tools/Sync-CageAsset.ps1`, `tools/blender/art_pipeline/cage.py` | Fits the garment cage to every body from its reference, writes its review sheet, and checks it. |
| `tools/blender/art_pipeline/body_files.py` | Appends the bodies a garment or the cage is fitted to from their own files, and appends them again when they change. |
| `tools/Sync-GarmentAsset.ps1`, `tools/blender/art_pipeline/garment.py`, `coverage.py` | Builds a garment template from its recipe on the cage, fits it to every body through the cage, gives every version the weights of its body, computes what it covers on every body, writes its review sheet and review file, and checks it. |
| `tools/Sync-TextureAsset.ps1`, `tools/blender/art_pipeline/texture.py` | Moves a garment texture to its template's new UVs, computes the masks of a texture with transparent parts, writes its review sheet, and checks it. |
| `tools/Set-TextureDyes.ps1` | Makes a garment texture dyeable: its dye mask and the natural color of each channel, with its image divided so that it looks undyed as it was painted. |
| `tools/Sync-HumanBase.ps1`, `tools/blender/human_base`, `tools/blender/human_base_cli.py` | The tools of the [human base characters](#human-base-characters): their body parts written from their editable bodies, their rig and weights, their checks and review sheets, and the gait model that starts clips in an animation library. |
| `src/Client/Assets/Editor/ArtModelImportRules.cs` | Unity import rules for every model below `Assets/Art/Models` and every texture below `Assets/Art/Textures`. |
| `src/Client/Assets/Editor/ArtAssetManifests.cs` | Reads `asset.json` and the skeleton definitions for the import rules, the material command, and the tests. |
| `src/Client/Assets/Editor/ArtMaterialCreator.cs` | `MMORPG > Tools > Art Pipeline > Create Missing Materials`: creates the project material of every exported material that has none, from its export record, and gives every garment texture's material the dye mask and natural colors of its record. |
| `src/Client/Assets/Editor/ArtExportFollowUp.cs` | After an import of exported models, their data, or textures, creates the missing project materials and updates the appearance catalogs, once, outside batch mode. |
| `.mcp.json` | The Blender MCP server for live work in Blender from Claude Code. |

## Asset Manifest

`asset.json` sits next to the `.blend` file. The scripts write it; only `kind`,
`target`, `rootObjects`, `triangleBudget`, and for a skinned asset `skeleton` and
any `clips` of its own are written by hand when an asset is created.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | `2` for a garment template, which has a `cage` and a `recipe`; `1` for every other kind. |
| `kind` | `static` for models without a skeleton, `skinned` for characters and creatures that get a rig, `animations` for an [animation library](#animation-libraries), `paint` for a [paint layer](#paint-layers), `garment` for a [garment template](#garments), `texture` for a [garment texture](#garment-textures), `cage` for the [garment cage](#garment-cage). A paint layer has no `target`, `rootObjects`, or `triangleBudget`, a garment template no `target`, `rootObjects`, or `bodies`: its models and roots follow from its cage's bodies, a texture none of them, and a cage no `target`, `rootObjects`, `triangleBudget`, or `export`. |
| `target` | `src/Client/Assets/Art/Models/<Category>/<Name>.fbx`, where `<Name>` is the folder name. |
| `rootObjects` | The exact names of the top-level objects in the `Export` collection. They become the direct children of the Unity model, so they carry the contract with the game code, such as `Available` and `Depleted` for a resource node. The one root object of an animation library is its armature, named after its skeleton, and that of an attachment is `Grip`. |
| `socket` | Attachments only: the socket the model is held on, as a bone of a skeleton definition names it in its `socket`, such as `main_hand` for `Socket_MainHand`. A static asset has a socket exactly when its one root object is `Grip`. |
| `useClip` | Tools only, attachments held while a body uses them: the clip of an animation library of the socket's skeleton that the body plays meanwhile, such as `Mine`; see [Tools](#tools). |
| `offHandGrip` | Tools held with both hands only, on `main_hand`: where the other hand's socket holds the model, as `[x, y, z]` in meters in the socket's axes in Blender, such as `[0.0, -0.33, 0.0]`, at most 2 m from `Grip` in each axis. |
| `triangleBudget` | The maximum number of exported triangles, after modifiers. An animation library exports no meshes and has none. A garment's budget holds for each body's version. |
| `skeleton` | Skinned assets and animation libraries: the name of the skeleton definition in `art/Skeletons`, such as `Humanoid`. |
| `rig` | Animation libraries only: the skinned asset whose rig the clips are keyed on, such as `art/Characters/Human_Male`. |
| `previews` | Animation libraries only: other skinned assets of the skeleton whose bodies show the clips in the library, such as `["art/Characters/Human_Female"]`. |
| `clips` | Animation libraries, and skinned assets with clips of their own: the names of the animation clips the asset exports, such as `["Death", "Idle", "Walk", "Jog"]`. Each is one Blender action. A skinned asset whose clips come from animation libraries, such as the human bodies, leaves `clips` out. |
| `category` | Paint layers only: `underwear`, `scar`, or `tattoo`, the kind of look it is, which says how many a body shows, when a player chooses it, and the layers it is drawn at; see [The Look On The Skin](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-skin). |
| `part` | Paint layers only: the part of the painted material `M_<Part>_<Label>`, such as `Body`. |
| `bodies` | Paint layers: the skinned assets the paint fits, the one a new layer's canvas is copied from first, such as `["art/Characters/Human_Male", "art/Characters/Human_Female"]`. They may have any topology; a body of another topology than the canvas gets the paint through the garment cage, whose bodies it must be among. The cage: the skinned assets it is fitted to, the one it was made from first, of any topology; its garment templates fit them. |
| `cage` | Garment templates only: the garment cage it is made on, such as `art/Cages/Humanoid`. |
| `recipe` | Garment templates only: its `regions`, `cuts`, `over`, `hang`, `ease`, and `hem`, as [Garments](#garments) describes, or `"modeled"`. |
| `garment` | Garment textures only: the template it is the look of, such as `art/Garments/Shirt`. |
| `figure` | The cage only: the figure scale in meters, the radius of the ball that rolls over the skin, from 0.01 to 0.2. |
| `lines` | The cage only: the standard lines, each with its `name`, its `region` (`neck`, `trunk`, `arms`, or `legs`), its `bone`, without the side `_L` or `_R` for the arms and legs, and its `share` of the bone's length, from 0 to 1; a line of the neck may also have a `rise`, from above 0 to 20 per meter, by which it climbs toward the sides, the rise times the square of the distance to the side. |
| `fittedTo` | The cage only, written by its approval: every body with the `fitSha256` of the body's export the cage was approved on, the fingerprint of the body's rig, surface, and weights, so a change of only a body's images leaves the cage as it is. |
| `layer` | Paint layers: the order paint layers are drawn in, the lowest first, within the layers of their `category`: 100 to 199 for a scar, 200 to 299 for a tattoo, and 900 to 999 for underwear, such as `200` for the tattoo. Garments: the order of a character's garments from the inside out, from 0 to 1000, such as `150` for a shirt over trousers at `100`. |
| `slot` | Garments: the visual slot, `head`, `body`, `legs`, `hands`, `feet`, `back`, or `hair`. Underwear: the visual slot it is worn in, `body` for a bra or `legs` for briefs. |
| `band` | Garments only: how far from the garment cage the garment lies, `tight` (0 to 8 mm), `mid` (5 to 25 mm), `outer` (10 to 60 mm), `loose` (from 10 mm), or `hair` (from 2 mm). |
| `hides` | Garments only: the other visual slots it hides, such as `["legs"]` for a floor-length robe, or `[]`. |
| `hidesUnderwear` | Garment templates only, optional: `true` when it hides the underwear of its slot while it is drawn, such as a top that bares the shoulders, `false` by default. |
| `covers` | Garment templates only, optional: `false` when it hides nothing under it, neither skin nor inner garments, such as a cloak that lies on top and may swing open, so its cover mask is empty; `true` by default. |
| `hair` | Garments only: for headwear, what happens to the hair under it, `hide`, `compress`, or `show`; `null` for every other slot. |
| `cloth` | Garments only: the cloth profile of its loose parts, `robe`, `cloak`, or `hood`, or `null`. |
| `dyes` | Garment templates only: the number of dye channels, 0 to 3, that its textures may use. `Cloak`, `Robe`, `HoodUp`, and `HoodDown` have one, for their wool. |
| `variants` | Skinned assets only, optional: the [appearance variants](#appearance-layers), each a painted material `M_<Part>_<Label>` with the other `images` `T_<Part>_<Label>_<Option>` it may show in the game, such as `{ "material": "M_Face_Male", "images": ["T_Face_Male_Clean"] }`. The material's own image is named `T_<Part>_<Label>_<Option>` as well. |
| `approvals` | One entry per approved review stage: the `date` and the `sourceSha256` of the `.blend` file that was approved. A garment texture's approval also records the `garmentSourceSha256` of its template's file, since it is reviewed on the template. |
| `export` | The `sourceSha256` of the exported `.blend` file, the `targetSha256` of the written FBX, for a body with an editable mesh the `dataSha256` of its topology, the `fitSha256`, the fingerprint of what the garment cage and the garments read from it, its rig, its surface with its faces and their body parts, and its weights, and the `paintSha256`, the fingerprint of what paint layers read from it, its meshes joined with their faces, materials, zones, digits, and paint UV map, and its rig's landmarks, and `materials`: the approved look of every exported material as URP Lit values, `baseColor` (sRGB `#RRGGBB`), `metallic`, and `smoothness` (1 - Roughness). A painted material also names its image in `baseMap`, and its `baseColor` is white. `textures` lists every exported image, those the materials name and the variants, with its `name`, `target` PNG, and `sha256`, present only when there are textures, and `clips` lists every clip of a skinned asset with its `name`, length in `frames`, and whether it should `loop`. The export of an animation library has no materials or textures; it records its `clips` and the `rigSourceSha256`, the `export.sourceSha256` of its rig when it was exported. The export of a paint layer records its `textures`, one `T_Paint_<Name>_<Label>` per body below `src/Client/Assets/Art/Textures/Paint`, each with its `layout`, the width and height of its body's full paint layout, its `rect`, where the image lies in that layout as x, y, width, and height in texels from the bottom left, x and y in whole blocks of 4 texels, its `move`, `topology` for the exact move through the shared topology or `cage` for the move through the garment cage, and the `bodyPaintSha256` of the body's export it was moved to; the `cageSourceSha256` of the cage file when a body was moved through the cage; and the `smoothness` and `metallic` of the paint, without a `targetSha256`. The export of a garment template records the `cageSourceSha256` of the approved cage file it was fitted to; its `models`, one per body of the cage in order with its `body` label, the `bodyFitSha256` of the body's export it was fitted to, the model's `target` and `sha256`, and its `data` file and `dataSha256`; its `clipSources`, every animation library of its skeleton with the `sourceSha256` of the export whose clips it was checked in; its `pairSources`, every template of another visual slot and a lower layer of its cage with the `sourceSha256` of the export it was checked over, when there are any; its one material `M_Garment_<Name>` without an image; and no `textures`. The export of a garment texture records the `garmentSourceSha256` of its template's export, its `masks` with their `body`, `data`, and `dataSha256` and their `clipSources` when it has transparent parts, its material `M_Garment_<Name>`, and its image `T_Garment_<Name>` below `src/Client/Assets/Art/Textures/Garments`; a dyeable texture's material also names its dye mask in `dyeMap`, `T_Garment_<Name>_Dye`, exported next to the image, and the natural color of each channel in `dyeColors` (sRGB `#RRGGBB`). `null` before the first export. |

## Review Stages

An asset passes its review stages in order. Each stage ends with a reviewer
looking at the asset, on the review sheet or in Blender, and approving it with
`Approve-ArtAsset.ps1`. The approval stores the SHA-256 of the `.blend` file, so
saving the file again, even without a visible change, needs a new approval.

| Kind | Stages | Status |
| --- | --- | --- |
| `static` | `model` | Implemented. |
| `skinned` (characters, creatures) | `model`, then `rig`, then `animation` when it has clips of its own | Implemented. |
| `animations` (animation libraries) | `animation` | Implemented. |
| `paint` (paint layers) | `paint` | Implemented. |
| `garment` (garment templates) | `model`, then `rig` | Implemented. |
| `texture` (garment textures) | `texture` | Implemented. |
| `cage` (the garment cage) | `model` | Implemented. It has no export. |

The `rig` review looks at the skeleton and the weights: test poses that raise
the arms, bend the elbows and knees, stride, sit, close the hands, open the jaw,
and turn the eyes, with close-ups of the shoulders, hips, knees, and hands and a
weight view. The `animation` review looks at every clip in motion, in the library
file on a checkered ground and on its review sheet, and
`Sync-AnimationLibrary.ps1` reports how far a foot on the ground slides in the
`Idle`, `Walk`, and `Jog` clips when the game moves the body at their speeds.

Stages are approved in order, and a new approval of an earlier stage reopens
every later stage. `Export-ArtAsset.ps1` exports only when every stage is
approved and the last approval covers the current `.blend` file. It then records
the hashes of the source and the FBX.

An asset is in review while a later stage still waits, such as a skinned model
before its rig. It has no export record and no FBX yet, and its latest approval
must cover the committed `.blend` file, so the repository only holds approved
states: work on the next stage stays on its branch until that stage is approved.

`Verify-ArtAssets.ps1` runs in the CI backend job and fails when an approval is
missing or stale, when an animation library was exported against another export
of its rig, when an asset in review has an export or an FBX, when the garment
cage's approval does not cover its committed file or one of its bodies has
another export than the cage was approved on, when a garment template was
fitted to another file of its cage or exported for other bodies than its cage
has, when a garment texture was exported against another export of its template
or has masks for other bodies than its template, when two assets export a
material of the same name, when the
committed FBX or a committed texture differs from the recorded export, or when a
model below `src/Client/Assets/Art/Models` or a texture below
`src/Client/Assets/Art/Textures` has no asset. It reads the SHA-256 of a `.blend`
file from its Git LFS pointer, so CI does not download the sources.

## Blender Rules

The pipeline needs Blender 5.2 LTS. Approval and export run Blender in the
background with factory settings, so add-ons and user preferences never change
the result. Validation runs before every approval and export and rejects an
asset that breaks one of these rules:

- An `Export` collection holds everything that goes to Unity and is included in
  the view layer. Other collections, such as `Reference`, stay in Blender.
- A `static` asset, and the model stage of a `skinned` asset, export only
  meshes and empties. The armature of a skinned asset comes with its rig stage.
- Object names use only letters, digits, and underscores. Blender names become
  Unity GameObject names.
- Every exported object has its scale applied.
- The top-level objects are exactly the manifest's `rootObjects` and sit at the
  origin without rotation. The origin is the actor position in the game.
- Every mesh has faces and at least one material, and no material slot is empty
  or unused.
- Material names start with `M_` and match the Unity material they map to, such
  as `M_Resource_IronVeinRock`.
- Every material is one Principled BSDF, connected straight to the Surface of
  its one Material Output, with a flat Base Color, Metallic, and Roughness,
  without textures or other nodes feeding them. A glowing material, such as the
  crystal of the Oak Staff, also has a flat Emission Color and Emission Strength;
  the export records their product as `emission`, and Unity turns on the
  material's emission in that color. Textures for static
  assets come with the planned palette texture. A `skinned` asset may feed the
  Base Color straight from one Image Texture whose image is packed into the
  `.blend` file, such as a painted body, read through the mesh's active UV map,
  which every mesh with that material must have.
- The exported triangles stay within `triangleBudget`.
- Every variant image is packed into the file as a PNG of the same size as the
  image of its material, so it fits the material's UV layout.
- The root `Grip` of an attachment is an empty with the meshes below it.

The `rig` stage of a skinned asset adds these rules:

- The `Export` collection holds exactly one armature, the only root object, with
  every mesh as its child. Its bones are exactly those of the skeleton
  definition, with the same parents and deform flags.
- The armature is in its rest pose and shows it (Pose Position), without
  constraints, an active action, or NLA tracks.
- Every mesh has exactly one modifier, an Armature modifier on that armature
  that deforms with vertex groups only, and no shape keys, except a hair
  garment, which has exactly the basis and `UnderHeadwear`. Its vertex groups
  are deforming bones only.
- Every vertex has one to four weights that add up to 1, and vertices of
  different meshes at the same place have the same weights, so the cuts between
  body parts never open.

The `animation` stage adds these:

- The scene runs at 30 frames per second, and the actions are exactly the
  manifest's clips.
- Each action has one slot, for the armature, a manual frame range from frame 0
  to a whole end frame, and the custom property `loop`. A looping clip ends on
  the pose it starts with.
- A clip only turns bones, with all four quaternion channels keyed, and only the
  root bone may move.

An animation library has its own rules, from its only stage on:

- The `Export` collection holds exactly one armature, its only root object,
  without meshes. Its bones are exactly those of the skeleton definition, and it
  is in its rest pose without constraints, an active action, or NLA tracks.
- The armature is the current rig of the library's `rig` asset: every bone has
  the same parent, deform flag, length, and rest matrix, to a hundredth of a
  millimeter. Approval and export compare it with the rig asset's `.blend` file.
- The clips follow the animation rules above.

A paint layer has these rules:

- The file holds the canvas `Canvas` with the paint UV map `UVMap_Paint`, its
  cage version `Canvas_Cage`, and the custom properties `canvas_body` and
  `landmarks`, and the canvas image `T_Paint_<Name>`, packed as a PNG with alpha
  of the paint size of its material: the size of its skin texture, twice as wide
  when the body mirrors that texture. It has the custom properties `roughness`
  and `metallic` from 0 to 1.
- The file holds the mesh `Paint_<Label>` and the image `T_Paint_<Name>_<Label>`
  of every body, and every body has `UVMap_Paint`. Every body's image is the move
  of the canvas's paint to that body, cropped to its paint, with its place in the
  custom properties `rect` and `layout` and the canvas's roughness and metallic,
  so the file shows what the game will. A file that a painter changed without
  the sync stops with `is not the move of`.

Modeling conventions: one Blender unit is one meter, Z is up, and the ground is
at Z = 0; rocks and similar props may sink a few centimeters into it. The front
of an asset faces Blender's **+Y** axis, so Blender's Back view (`Ctrl+Numpad 1`)
shows the front. Blender +Y becomes Unity +Z, the forward direction of an actor
at yaw 0, Blender +X stays Unity +X, and Blender +Z becomes Unity +Y. A model
made in Blender's usual orientation, facing -Y, is turned 180 degrees around Z
with the rotation applied before review.

## Skeletons

A skeleton definition in `art/Skeletons/<Name>.json` lists the bones that every
skinned asset of that skeleton has, so equipment, animations, and game code can
rely on them across bodies. Each bone has a `name`, its `parent` (null for the
one root), the Unity `human` bone it maps to (a `HumanBodyBones` name), whether
it should `deform` the skin, and for a bone of a cloth chain its `chain`.

`Humanoid` is the skeleton of the playable bodies: 55 deforming body bones, from
`Hips` through `Spine`, `Chest`, `UpperChest`, `Neck`, and `Head` with `Jaw` and
the eyes `Eye_L` and `Eye_R`, a `Shoulder`, `UpperArm`, `LowerArm`, and `Hand`
with three bones per finger and thumb on each side, and an `UpperLeg`,
`LowerLeg`, `Foot`, and `Toes` on each side, all mapped to the Unity Humanoid
avatar. Three sockets deform nothing and carry equipment: `Socket_MainHand` on
the right hand, `Socket_OffHand` on the left, and `Socket_Back` on the upper
chest. In Unity a socket's local Z is the forward direction of the item it holds
and its local Y is up, which are Blender's bone Y and Z axes.

Fifty-nine more bones form the cloth chains that loose garments hang on, outside
the Humanoid avatar, so Humanoid clips never move them; the body is not weighted
to them, and the runtime cloth solver moves them for the garments that use them
(see [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#cloth-chains)):

| Chain | Bones | Parent | Placed |
| --- | --- | --- | --- |
| `skirt` | `SkirtFront1` to `4`, `SkirtFrontSide1_L` to `4_L` and `_R`, `SkirtSide`, `SkirtBackSide`, and `SkirtBack`, eight chains of four | `Hips` | On a ring just above the hip joints, 1 cm out from the body, hanging to the ankles. |
| `cloak` | `CloakCenter1` to `5`, `CloakInner1_L` to `5_L` and `_R`, and `CloakOuter`, five chains of five | `UpperChest` | Across the shoulder blades, 4 cm below the base of the neck and 1.5 cm behind the body, hanging to the middle of the calves, halfway from the knees to the ankles. |
| `hood` | `Hood1`, `Hood2` | `Head` | Behind the head at eye height, stepping down and back. |

A chain bone's Z axis points away from the body, the way its cloth faces.

The skeleton's `cloth` section holds the constants of the cloth on its chains,
which the Unity client and the art pipeline share: the body bones the cloth
collides with, where their capsules end, and what each is around, the trunk, a
leg, or an arm; the directions and groove passes of a capsule's radius and the
share of a limb's vertices its round capsule reaches; how a garment's reach is
measured; how a chain follows the legs; how the target pose swings a bone out of
the body, its samples, its steps, its largest swing, the contact band, how far
an arm lifts a cloak, and the share of a swing that moves a sample along a
capsule rather than out of it; the gap of a cloak over a robe; and per kind of
chain, in the order their cloth lies on the body, its parent, its bones, the part
of the legs beside each bone, its profile's margin and slack, the kind it lies
over, what it collides with, the bone its frame is, and its chains with their
angles around the hips. `ClothPoseTests` checks the client's constants against
it, and `cloth_core.py` reads it; see [Cloth Parity](#cloth-parity).

The character faces +Y and its right is +X, so the `_R` bones sit at +X. Limbs
bend about their bone X axis, arms rise about Z, and every bone twists about Y.

## Export Settings

`tools/blender/art_pipeline/export.py` writes FBX with fixed settings: the
active collection only (`Export`), Blender's own axes (Forward Y, Up Z) without
Blender's `Apply Transform`, unit scale applied with `FBX Units Scale`,
modifiers applied, face smoothing exported, no animation for `static` assets,
and no custom properties, leaf bones, or embedded textures. Objects hidden in
Blender's viewport, such as a `Depleted` state, are exported too.

A skinned asset also exports its armature with every bone, also the sockets,
with bone Y as the primary axis, and every action as its own take, sampled at
every frame without simplification. Blender names each take
`<armature>|<action>`. The images of its painted materials are written below
`src/Client/Assets/Art/Textures/<Category>` as the PNG bytes packed in the
`.blend` file, and a texture that an earlier export wrote and this one does not
is removed. The variant images are written the same way. An animation library
exports its armature and its clips the same way, without meshes.

The export is reproducible: the same `.blend` file gives the same FBX bytes in
every checkout. Blender's exporter writes the time of the export and the path of
the `.blend` file into the FBX header and derives its object ids from Python's
string hashes, which Python randomizes per process. `export.py` fixes the header
time to 2000-01-01 and leaves the path empty, and `Invoke-ArtBlender` in
`tools/ArtPipelineCommon.ps1` runs Blender with `PYTHONHASHSEED=0` and
`--python-use-system-env`, after clearing every other Python variable, so no
Python setup of the machine reaches the pipeline. An export without the fixed
seed stops. Exporting an unchanged asset again leaves its files unchanged.

These settings were chosen with an axis probe imported into Unity. Exporting
with Forward -Z and Up Y, the usual advice for Unity, leaves a 90 degree
correction rotation on the model root, and the game sets that root's rotation
from the actor yaw. `Apply Transform` with Forward -Y gives the right
direction, but leaves a 180 degree rotation on every mesh and does not support
armatures, which characters need.

## Unity Import Rules

`ArtModelImportRules` is an `AssetPostprocessor` in the `Mmorpg.Client.Editor`
assembly. It finds the `asset.json` whose target is the model, and every import
of an FBX below `Assets/Art/Models` gets these settings, whatever the importer
said before:

- Scale factor 1 with the file scale, and the Blender axis conversion baked
  into the meshes, so every object keeps the rotation it has in Blender.
- `Preserve Hierarchy`, so Unity always creates an explicit model root and every
  Blender root object is a direct child of it, also when there is only one.
- No cameras, lights, visibility, blend shapes, or colliders, and no animation
  for a static model. Presentation never takes part in physics. Only a hair
  garment keeps its shape key `UnderHeadwear` as a blend shape, with calculated
  normals, which Unity takes in full under headwear that compresses the hair.
- Mesh data is uncompressed, with imported normals and calculated tangents, and
  not readable for a static model, except an attachment, which the body that
  holds it combines.
- Materials are imported from the model and then remapped by name to the one
  project material of the same name below `Assets/Art/Materials`. The remap is
  stored in the model's `.meta` file.

A skinned model also gets:

- A Unity Humanoid avatar created from the model, with every bone of its
  skeleton definition that names a human bone mapped explicitly, and Unity's
  default muscle limits, twist, and stretch.
- `Optimize Game Objects` off, so every bone and socket stays a transform that
  equipment can attach to, and readable meshes, because the game combines the
  visible body parts into one mesh.
- One clip per record of `export.clips`, named like its action and cut from frame
  0 to its last frame, looping as recorded, without keyframe compression. A clip
  plays in place: the body's turn, height, and sway stay in the pose, and the
  game moves the character.

An animation library gets the same Humanoid avatar and clip settings, from its
own armature, and imports no materials. Its clips become Humanoid muscle motion,
so an Animator plays them on every body of the skeleton, next to the clips of
the body's own model. A library has no mesh and so no bind pose, and Unity would
make its avatar from the first frame of its first clip, such as the bent knees
at the start of `Death`, which shifted every clip on every body: the feet stood
wide and slid. So the library's avatar is made on the pose that the avatar of its
`rig`, whose armature the library's armature is, was made on: the rig's rest
pose with the arms raised to a T-pose, as Unity makes a body's avatar. The rig
then plays every clip exactly as the library's own armature has it, and a new
import of the rig's model imports the library again.

A garment's model gets a generic rig without an avatar of its own and without
clips, because the character it is put on animates it: its armature is the
body's, and the game moves it with the character's skeleton. `Optimize Game
Objects` is off and its mesh is readable, because the game combines it with the
visible body parts.

Every PNG below `Assets/Art/Textures` imports as an sRGB texture with mipmaps,
without alpha, not readable, clamped, trilinear, at most 2048 px, with high
quality compression. The images of paint layers and garments, below
`Textures/Paint` and `Textures/Garments`, keep their alpha, but a garment's dye
mask, `T_Garment_<Name>_Dye`, imports without alpha and as linear data, not sRGB,
because the shader reads how far each channel dyes as it is stored. A paint layer's image
keeps its size, up to 4096 px and also when that is not a power of two, because
it is cropped to its paint in a body's paint UV map, which is twice as wide as
the skin texture of a mirrored body.

`MMORPG > Tools > Art Pipeline > Update Appearance Catalogs` writes the
appearance catalog of every exported skinned model with `variants` or a paint
layer or a garment that fits it, as `Assets/Art/Appearance/<Model>_Appearance.asset`,
when it is missing or differs; see [Appearance Layers](#appearance-layers). For a
body with an editable mesh, the catalog maps every triangle of its parts to the
face of the editable mesh it comes from, and every garment's triangles to their
underlay: `ArtTopologyMaps` finds each Unity vertex at the position of a vertex
of the exported topology, Blender's (x, y, z) being Unity's (x, z, y), and each
triangle in the one face that holds its three corners, so the order of Unity's
vertices and triangles does not matter.
`ArtAppearanceCatalogTests` also fails when a face of an editable body is not
drawn by its size less two triangles.
`ArtAppearanceCatalogTests` fails when a catalog differs from what the command
writes from `asset.json`.

A project material is never changed by the import, and every project material
is saved in the form its shader's Inspector keeps: URP Lit validates a material
whenever the Inspector shows it and then derives the legacy `_Color` from
`_BaseColor`, so a script that writes `_Color` itself leaves a file that changes
the first time someone selects it. `EveryProjectMaterialIsInTheFormItsInspectorKeeps`
in `AuthoredGameplayAssetTests` fails on such a material. When an export adds a
material, `MMORPG > Tools > Art Pipeline > Create Missing Materials` creates it
as a URP Lit material from its export record, with its texture for a painted
material, below `Assets/Art/Materials/<Category>`, and reimports the models that
use it; a garment texture has no model, and its material goes to
`Assets/Art/Materials/Garments`. The material of a garment template or texture
draws with the garment shader `Mmorpg/Garment`,
URP Lit with three dye channels, and cuts its texture at alpha 0.5 and draws both
faces, so holes, lace, and torn edges show sharp and the inside of a sleeve is
drawn. Its forward pass turns the normal on the back faces in
`GarmentForwardPass.hlsl`, so the inside of a cloak or a skirt takes the light
that falls on it instead of the light of its outside. Like the skin shader, it is
a copy of URP's `Lit.shader` and `LitInput.hlsl` in which only the include, the
dye properties `_DyeMask`, `_DyeColor1` to `_DyeColor3`, and `_DyeCount`, the
dyeing of the albedo, and the forward pass's fragment differ; when URP is
upgraded, it is made again from the new files with the same changes.

`ArtExportFollowUp` runs both commands once after Unity imports exported models,
their data, or textures, when the Editor next updates, so an export reaches the
game without a menu command, also when only a garment's masks change. It does
nothing in batch mode, so the CI tests check the
committed materials and catalogs as they are. When a Blender material changes, set the Unity material from its new
record by hand; the tests below compare them.

`ArtModelImportTests` fails when a model has other import settings than its
kind needs, embeds a material because Unity has no material of that name, uses a
material outside `Assets/Art/Materials`, or has a model root or Blender root
object with a position, rotation, or scale. It also reads every `asset.json`
and fails when the model renders with a material that has no export record, or
a record names a material the model does not use, or a Unity material's
`_BaseColor`, `_Metallic`, `_Smoothness`, or `_BaseMap` texture differs from the
approved Blender material recorded at export, so the game shows the colors and
the painted textures the reviewer approved. For a skinned model and an animation
library it fails when the avatar is not a valid Humanoid, a human bone maps to another bone than the
skeleton definition says, a bone or socket is missing or has another parent, or
a clip is missing, has another length, or loops differently than recorded. For
an animation library it also fails when its avatar is not made on the pose of
its rig's avatar, or when the rig's model shows a clip with the hips, the head, a
foot, or a hand more than 2 mm from where the library's own armature has it. For a
garment it fails when a model lacks its body's armature or a skeleton bone, has
another renderer than `<Name>_<Label>`, a vertex without weights or with more
than four bones, or a bone whose bind pose differs from the body's, and when
its material does not cut at alpha 0.5 from both sides, and when a garment's data
or a garment texture's masks are missing, belong to another garment or body, or
hold a face or vertex out of range, an unsorted list, or a pull-in beyond 3 mm.
`ArtAppearanceCatalogTests` fails when a body's garment lacks its template or
does not draw with its texture's material. A
texture below `Assets/Art/Textures` without an export record or with other
import settings fails as well. `Verify-ArtAssets.ps1` rejects an export record
with a missing, malformed, or duplicate material, texture, or clip entry.

## Prefabs

A world actor presentation prefab that uses an exported model is a Prefab
Variant of the model. The prefab keeps its path, GUID, and Addressables entry, so
the presentation registry needs no change, and a new export updates the prefab
through the variant. The materials come from the remap, so the variant has no
material overrides. A resource node model has the roots `Available` and
`Depleted`, which `ResourceNodeView` switches when the node depletes; an
EditMode test checks every resource node prefab for them.

`Assets/Prefabs/Player/HumanMaleBody.prefab` and `HumanFemaleBody.prefab` are
the Prefab Variants of `Human_Male.fbx` and `Human_Female.fbx` that the local and
remote player prefabs show as `PlayerVisual/Body`, in place of their old
capsule. Each adds the Animator with
`Assets/Art/Animation/HumanLocomotion.controller`, the locomotion driver, the
body's look with its skin materials and its model's appearance catalog, and the
cloth chain solver. `Assets/Content/Player/PlayerBodyModels.asset` lists them as
the body models `Human_Male`, the default, and `Human_Female`, and a player
prefab puts in the body of its model when it spawns; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies). A new
body model is a new Prefab Variant with the same components and an entry in
that list. `PlayerBodyAssetTests` checks both variants, the controller, the
models, and both player prefabs.

## Animation Libraries

Clips live in animation libraries: art assets of kind `animations` below
`art/Animations`, keyed on the rig of one character and played on every body of
the skeleton. `art/Animations/Human_Actions` holds every clip of the human bodies,
`Idle`, `Walk`, `Jog`, `Death`, the hand pose `Grip`, the tools' strokes
`Mine` and `Chop`, and the staff's hold pose `HoldStaff` and attack `Cast`, and
the bodies hold none:

- The `Export` collection holds the armature `Humanoid`, a copy of the rig of
  `Human_Male` named after its skeleton. Every clip is an action keyed on it, at
  30 frames per second.
- The `Reference` collection, which is never exported, holds what shows the
  clips: the male body bound to `Humanoid`, so it moves with every key, the
  female body 2 m to its right on an armature of her own whose bones copy the
  pose of `Humanoid` in local space, with the hips' movement scaled to her hip
  height as Unity retargets a Humanoid clip, and a checkered ground of
  quarter-meter squares, so a body that slides or sinks shows at once.

`tools/Sync-AnimationLibrary.ps1` builds the library file from the manifest's
`rig` and `previews`. When the rig changes, such as after
`Sync-HumanBase.ps1 -Rig` or new bones in the skeleton, it replaces the armature
and the bodies and keeps every action, so the clips play on the new rig and are
reviewed and approved again. It names every clip's slot after the armature, so
Blender binds a clip to it when a person picks the clip in the Action Editor. A library whose armature is already the current rig is left as it is.
The approval and the export refuse a library whose armature is not the current
rig, and CI refuses one that was exported against an older export of its rig;
exporting it again is enough when the rig itself did not change.

A clip is made by hand in the library file, in the reviewer's Blender, through
the [Blender MCP server](#live-work-with-the-blender-mcp-server) or the
reviewer's own keys: key poses on `Humanoid`, smoothed by Blender's curves, with
the custom property `loop` and a manual frame range from frame 0. A locomotion
clip may start from the gait model instead: `Sync-AnimationLibrary.ps1
-Generate Walk` adds the model's `Walk` when the library lacks it, and the same
for `Idle` and `Jog`, and never replaces a clip the library has.

The gait model, `tools/blender/human_base/animate.py`, makes its clips with the
strides matched to the game: `Walk` at 1.4 m/s and `Jog` at 5.0 m/s, the
movement speed of players, and a 4 s breathing `Idle` with a slow weight shift
and a look around. Each foot follows a contact model with a heel strike, a roll
over the ball, and a planned swing, and the legs reach it with analytic two-bone
IK while the hips sink where a leg cannot reach. The pelvis turns, drops, and
tilts with the steps, the lower back takes up the tilt, the shoulders turn
against the pelvis, and the arms, forearms, hands, and fingers follow the swing a
little later. A planted foot slips at most 8 to 13 mm in the walk and about 1 mm
in the jog. The clips of `Human_Actions` are the model's clips as approved; edit
them by hand from there. Before the
approval, the file is saved with `Humanoid` in its rest pose and without an
active action. The library's review sheet shows every clip as eight frames on
both bodies, from the front right and from the back left.

A clip may key only some bones: `Grip` keys only the fingers and thumbs of both
hands, closed around a handle of 29 mm on each hand's socket, and the game plays
it over the other clips on the fingers of a hand that holds an
[attachment](#attachments). Its finger angles were fitted so that every segment
of every finger touches that handle without going into it, the distal joint bent
by a share of the middle one, as a hand closes, and were approved on 2026-10-25.
A clip added by hand is named in `clips` in `asset.json`; until the library is
exported again, the record of its last export lists other clips, which the
verification reports as a stale export. A clip may instead come from a script,
as `Mine` and `Chop` did: the tool's grip frame follows a swing in a plane, both
hands reach its haft with analytic two-bone IK, and every frame is keyed. The
library's review sheet shows a tool held in the rows of the clip it is used
with, as [Tools](#tools) describes.

In Unity, `Human_Actions.fbx` brings its clips as Humanoid motion, and
`HumanLocomotion.controller` plays them on every player body; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).
Adding a clip to the library changes its export, which every garment template
and every garment texture with masks records as a clip source they were checked
in, so they are exported again after it: `Export-ArtAsset.ps1` checks them in
the new clip, and their approvals stay, since their files do not change.

## Appearance Layers

Players change how their bodies look in the style of World of Warcraft: other
paint on the same model, such as another face or eye color, equipment painted
onto the skin, often together with a modeled part, hair as a mesh with paint on
the scalp, and later skin colors. As with clips, each layer is made by hand in
an asset of its own or in the character's file, so a change to one layer leaves
the others as they are.

| Layer | Made as | In the game | Status |
| --- | --- | --- | --- |
| Texture variants | Other images of a painted material, packed in the character's `.blend` file and named in its manifest's `variants`, such as the faces `T_Face_Male_Stubble` and `T_Face_Male_Clean` and the eye colors `T_Eye_Male_Teal` and `T_Eye_Male_Brown`. They are reviewed and approved with the model. | The material shows the chosen image. | Implemented. |
| Paint layers | Transparent paint over the skin in the bodies' paint UV map, of a category, `underwear`, `scar`, or `tattoo`, such as the briefs, a scar, or a tattoo, in a [paint layer](#paint-layers) asset of its own. Painted once on the layer's canvas and moved to every body by projection, also to a body of another topology, and one-sided where it is painted so. | The skin shader lays it over the skin when the body is drawn, by the rules of its category. | Implemented. |
| Garments | Meshes skinned to the shared skeleton, such as clothes, armor, and hair: [garment templates](#garments) built on the [garment cage](#garment-cage) and fitted to every body of it, each worn with a [garment texture](#garment-textures) of its own asset. | Combined with the visible body parts into the body's one mesh; see [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md). | Implemented, with the Wardrobe preview and trying garments on in the Admin menu (F3). |
| Attachments | Static models held on a socket, such as a sword or a shield, in an [attachment](#attachments) asset of its own. | Combined rigidly on its socket bone into the body's one mesh, with the hand closed around it. | Implemented, with the Wardrobe preview. |

The faces and eyes of the human base characters are variants: images packed into
the character's file, shown by the model review, and exported with the model
through `variants` in `asset.json`. Which kinds of look a body shows and when a
player chooses them is in
[The Look On The Skin](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-skin). In Unity, the body's `BodyAppearanceCatalog` lists one slot per
material with variants, such as `Face` and `Eye`, with its options, the
material's own image first, and a look picks one option per slot; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).

## Paint Layers

A paint layer below `art/Paint` is paint over the skin of the bodies of a
category, such as the tattoos `art/Paint/Tattoo` and `art/Paint/ForearmRunes`, the
scar `art/Paint/Scar` on the faces, or the underwear `art/Paint/Bra` and
`art/Paint/Briefs`. The paint does not depend on the bodies' topology: it is kept
on the layer's own canvas and moved to every body by projection, so a body of any
topology, such as a race body or a new base model, gets it.

### The Canvas

The file's collection `Canvas` holds the canvas `Canvas`, a copy of the body the
paint is painted on, its parts joined in its rest pose, with materials of its own,
`Canvas_<Part>_<Label>`, that body's version of the garment cage, `Canvas_Cage`,
and its rig's landmarks in the custom property `landmarks`, and the image
`T_Paint_<Name>`, the paint. A person paints the image on the canvas, by hand in
Texture Paint mode or by a script, and the image carries the custom properties
`roughness` and `metallic`. The canvas is the layer's own: it never changes when
the bodies change, and a new layer's canvas is a copy of its first body.

The canvas is painted in its paint UV map, `UVMap_Paint`, which every body has
next to the skin's `UVMap`: the skin's UVs, with every material whose faces share
their texels with their mirror image laid out twice as wide, the left side in the
left half and the right side in the right half. The human bodies' skin texture
`T_Body_<Label>` is mirrored, so the paint of `M_Body` is twice as wide as the
skin, 4096 by 2048 texels for both sides at the skin's own density, and paint on
one side stays on that side, as the left forearm's runes of `ForearmRunes` do.
The face texture `T_Face_<Label>` is not mirrored, so `M_Face`'s paint, such as
the scar, keeps its UVs and its size. Texture Paint's X mirror paints both sides
at once. `Sync-HumanBase.ps1` writes `UVMap_Paint` on every body part from the
source body's `UVMap`; a body of another base model brings a map of its own
without mirroring, so its `UVMap_Paint` is its `UVMap`.

### Moving The Paint To The Bodies

The file's collection `Reference` holds per body the mesh `Paint_<Label>`, the
body's parts joined in its rest pose and drawn with the paint over its skin, and
the image `T_Paint_<Name>_<Label>`, the canvas's paint in the body's own
`UVMap_Paint`. `tools/Sync-PaintAsset.ps1` builds it: every texel of the body's
painted material takes the paint at its point of the canvas, filtered between the
canvas's texels with their colors weighted by their alpha, and the painted edges
are extended over the empty texels around them, so filtering at a UV seam takes
paint. The texel's point of the canvas is found in one of two ways:

- A body with the canvas's topology, such as `Human_Female` for a canvas of
  `Human_Male`, takes the point at the same place of the same face. This move is
  exact.
- A body of another topology takes it through the garment cage: every canvas
  vertex is bound to the nearest face of `Canvas_Cage` on its own region, side,
  and finger, placed at the same place and height over the body's cage version,
  moved further by the difference between the rigs' landmarks, such as the eye
  rims, the lips, the nose, the chin, the wrists, and the finger tips, and laid
  onto the body's skin of the same region, side, and finger. Each of the body's
  texels then takes the nearest point of the canvas so fitted. The file keeps the
  body's cage version as `Cage_<Label>`, and the export records the cage's file.

The cage keeps the paint's place along the bones: an armband that a woman gets
from a man's canvas through the cage lies at 0.42 to 0.61 of the way from the
shoulder to the elbow, as on the man, where the shared topology puts it at 0.47 to
0.65. Where one body has a form the other lacks, the cage spans it and the paint
stretches: the rune emblem on the man's upper breastbone comes out 94 mm wide on
the woman instead of 72, since her cleavage has no counterpart on the man.
`tools/Test-PaintProjection.ps1` checks this move on subdivided copies of the
bodies, another topology of the same surfaces: the canvas's own body must take
the paint within 0.5 mm (median) and 1 mm (95%) of its place, and for the other
bodies the script reports how far the cage places the paint from where their
shared topology does, or, for a body of another topology than the canvas, from
its own move through the cage.

`Sync-PaintAsset.ps1 -Canvas <body>` moves the canvas to another body of the
layer, with the paint moved to it, so the paint is painted on that body from then
on. When a new base model replaces the human bodies, each layer's canvas is moved
to the new first body this way, its paint is checked and touched up there, and
every body of the new model's topology then gets the exact move from it.

Each body's image is cropped to its paint, to whole blocks of 4 texels with a
block of empty texels around it, and keeps its place in the custom properties
`rect`, x, y, width, and height in texels from the bottom left, and `layout`, the
width and height of the body's full paint layout, so a small tattoo costs little
memory. The approval and the export check that every body's image is the current
move of the canvas, and the export writes every image as it is packed, with its
crop and the fingerprint of its body's export, so CI rejects a layer whose body
changed what the move reads from it.

### In Unity

In Unity, a paint image imports with its alpha, and its transparent texels take
the color of their neighbors, so filtering at the edges never darkens the skin.
The skin shader `Mmorpg/Character Skin` is URP Lit with up to four paint layers:
only the include of `CharacterSkinInput.hlsl`, a copy of URP's `LitInput.hlsl`
with the paint properties in the material constant buffer, the includes of
`CharacterSkinForwardPass.hlsl` and `CharacterSkinGBufferPass.hlsl`, copies of
URP's `LitForwardPass.hlsl` and `LitGBufferPass.hlsl` that take the mesh's second
UV map, the paint UVs, where Lit takes the lightmap UVs, the lightmap keywords
those passes leave out, since a skinned character is never lightmapped, and the
paint properties differ from Lit. Each layer is a `_PaintMap<N>` with
`_PaintSurface<N>` (smoothness, metallic) and `_PaintRect<N>`, where its image
lies in the paint UVs, its corner in xy and its size in zw; it lays its color,
tinted like the base map, its smoothness, and its metallic over the surface by
its alpha inside its rect, in order; `_PaintCount` says how many layers are drawn.
The body materials that a paint layer paints, such as `M_Body_Male`, draw with
this shader, and without a layer they draw like Lit. `Create Missing Materials`
creates them with it. When URP is upgraded, the shader is made again from the new
`Lit.shader`, `LitInput.hlsl`, `LitForwardPass.hlsl`, and `LitGBufferPass.hlsl`
with the same changes. The combined body mesh carries the parts' paint UVs, and
garments and the eyes, which have none, take zero. The appearance catalog of
every body lists the paint layers that fit it with their category, slot, image,
and rect, in the order of their `layer`, and a body shows them by the rules of
their categories.

### Review

The review sheet of a paint layer shows it on every body as the game does, over
and under the underwear the body always wears, in the order of their layers: the
bodies side by side from four sides, then every piece of the paint up close on
each body, such as the emblem and each armband, from up to four sides it is seen
from, framed on the part the camera sees, and then the canvas's image over grey
with the outlines of its faces in `UVMap_Paint`.

## Garment Cage

`art/Cages/Humanoid` is the garment cage of the `Humanoid` skeleton: a sparse net
of fixed topology that lies over every body of the skeleton, smooth over the
body's pits and following its figure, on which garments are made and fitted; the
design is in
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#the-garment-cage). It has
3,424 faces in seven regions: 1,576 over the neck, the trunk, the arms to the
wrists, and the legs to the ankles, in `neck`, `trunk`, `arms`, and `legs`, and
since 2026-10-24 760 over the hands, in `hands`, 544 over the feet, in `feet`,
and 544 over the head, in `head`.

Its file holds:

- The reference in the `CageReference` collection: the armature `CageRig`, a
  copy of the male body's rig when the cage was made, and on it the mesh `Cage`,
  the male body's faces of its `Neck`, `Torso`, `Pelvis`, `UpperArms`,
  `Forearms`, `Thighs`, and `Shins` parts, with their weights and the face
  attribute `region`. Its faces never change: a new base model changes the
  bodies, not the cage. Regions are only added to it, with their vertices and
  faces after its own: `hands`, the male body's faces of its `Hands` part, met at
  the wrist rings, with the face attribute `digit`, 1 to 5 for the thumb and the
  fingers by the bone their corners weigh most and 0 for the palm; and `feet`, a
  net of the cage's own on each side, 16 rings of 16 vertices from the ankle
  ring to the toe tip, closed by a fan of 16 triangles there, with the point
  attributes `sweep_ring` and `sweep_column` that place each vertex on its ring;
  and `head`, a net of the cage's own from the neck's top ring to the crown, 16
  rings of 32 vertices on the convex hulls of level sections of the head, so it
  spans the ears, the nose, and the eye sockets smoothly, closed by a fan of 32
  triangles at the crown, with the same point attributes.
  `Sync-CageAsset.ps1` adds a region the reference lacks, built on the first
  body. The figure scale and the lines it was last fitted with are custom
  properties of `Cage`, so changing them in `asset.json` changes the file and
  needs a new approval.
- Per body, the collection `Fit_<Label>` with the body's armature and the cage's
  version `Cage_<Label>` on it, and in the `Reference` collection the body's
  source body, eyes, teeth, and tongue. The file opens with the male and its
  cage shown and the others hidden, because they stand at the same place.

`tools/Sync-CageAsset.ps1` fits every version from the reference alone, so a body
of any topology gets one:

1. The reference moves with the rig from `CageRig` to the body's armature, every
   bone with its length, blended by the reference's weights.
2. Every vertex is drawn to the skin of its region: to the nearest skin within
   2 mm, otherwise along the cage's normal to whichever side meets the skin
   first, within 80 mm and at most 10 mm farther than the nearest skin.
3. A ball whose radius is the figure scale, 60 mm, rolls over the skin of the
   region along normals smoothed 25 times: a vertex where the ball does not fit,
   in a pit narrower than the ball, is lifted until it does, by at most 0.6 of
   the radius. The waist and the small of the back keep the ball touching the
   skin and are followed. The lift fades in over three edges from the cage's
   open ends and from the borders between its regions, so an arm's cage never
   webs into the trunk and the crotch stays a crotch.
4. The rings around the trunk, the neck, the arms, and the legs are drawn taut
   like a band, outward only and in the ring's plane, 200 steps: each edge counts
   toward a ring by the fourth power of the sine of its angle to the ring's axis,
   which is up for the trunk and the bone a vertex follows most for the neck and
   the limbs. A groove across a ring is spanned, such as the buttock cleft, the
   spine groove, the navel, and the cleavage, while the figure from top to
   bottom stays. The tension fades in over three edges from the open ends.
5. Eight Taubin steps fair the cage, and no vertex and no face centre stays
   closer to the skin than 1 mm. A face that cuts across a curve of the skin
   between its corners, such as a woman's shoulder, would let the skin come out
   through it, and every garment on it: a ray goes in from every skin vertex of
   the net's body parts, and of the hands and feet next to the wrist and ankle
   rings, along the skin's normal, up to 20 mm, and where it meets a face from
   outside, or the skin lies closer to a face than 1 mm, the face moves out
   along its normal, all its corners alike, until the skin lies 1 mm inside it.

These steps fit the neck, the trunk, the arms, and the legs as one net. The
hands and the feet are fitted after them as a second net that holds the wrist
and ankle rings where the first left them, so the regions added to the cage
changed none of its other vertices:

- A hand's vertex moves with the rig like the rest, then to the nearest skin of
  its own finger or thumb and the palm, the body's hand faces that the finger's
  or thumb's bones move most, or of the whole hand where its faces belong to
  more than one; the gaps between the fingers stay open, and the hands are not
  faired.
- A foot is laid on rings: half-planes that turn about an axis across the foot
  in the ankle ring's plane, 25 mm in front of the ankle, from that plane down
  in eight steps until they stand across the foot's forward direction, then
  planes across it at 15, 30, 45, 60, 72, 83, 92, and 97 percent of the way to the
  toe tip. Each ring follows the convex hull of its cross section of the foot's
  skin, which spans the toes and the arch and makes the sole flat: its first
  vertex lies where the ray from the hull's centre at the angle of the ankle
  ring's first vertex meets the hull, and the others follow at the shares of
  the outline at which the ankle ring's vertices lie. The last ring closes at the
  toe tip. The feet are faired in eight Taubin steps.
- No vertex and no face centre of either stays closer to the skin than 1 mm,
  and the skin of the hands and the feet stays 1 mm inside their faces,
  settled in 16 passes, since around a slender fingertip moving one face moves
  the next one back. A face whose point over the skin the held wrist or ankle
  ring carries for more than half stays, so the skin next to a ring may come
  closer than 1 mm, but the first net keeps it from coming out.

A version corrected by hand keeps its corrections, because the point attribute
`fitted` holds the shape the last fitting gave, and the difference to it is
applied again. A file that a sync leaves as it was is not saved, so its approval
stays valid.

Its `model` stage checks that the bodies are the current ones of their files,
that the cage was fitted with the figure scale and the lines of `asset.json` and
is fitted as the fitting gives it now, that every version has the reference's
topology, that no vertex or face centre lies closer to the skin than 1 mm, that
the skin comes out through no face between its corners, that no two faces cross
each other, and that every line crosses its region on every body. The report names how far each version lies from the skin and how many
vertices span pits more than 5 mm deep.

The standard lines are planes at a `share` of a bone's length, at right angles
to the bone, or level across the trunk, which stands upright at rest while its
bones lean:

| Line | Region | Bone and share | Where |
| --- | --- | --- | --- |
| `CollarStand` | `neck` | `Neck` 0.3 | a stand collar's top, about 3 cm up the neck |
| `Neckline` | `neck` | `Neck` 0.19, rise 5.3 | a round neckline that hugs the neck: just above the hollow of the throat, up at the sides of the neck where the shoulders meet it, and at the nape |
| `CloakNape` | `trunk` | `UpperChest` 0.85 | a few centimeters below the base of the neck at the back, where a cloak's back hangs from |
| `CloakYoke` | `trunk` | `UpperChest` 0.55 | a cloak's yoke, a few centimeters below the collarbones and above the armpits, where its front ends |
| `HemCrop` | `trunk` | `Chest` 0.5 | under the bust |
| `SkirtWaist` | `trunk` | `Spine` 0.45 | a robe's or a dress's waist seam, just below the navel: 2 to 3 cm on the male, about 1 cm on the female |
| `Waist` | `trunk` | `Spine` 0.1 | the trousers' waistband, at the hip bones |
| `HemHip` | `trunk` | `Hips` 0.72 | a top's hem, about 5 cm under `Waist` |
| `SleeveShort` | `arms` | `UpperArm` 0.6 | a short sleeve, about 9 cm long under the arm |
| `SleeveElbow` | `arms` | `LowerArm` 0.05 | just below the elbow |
| `GloveCuff` | `arms` | `LowerArm` 0.65 | a glove's cuff, about 5 cm up from `SleeveLong` |
| `SleeveLong` | `arms` | `LowerArm` 0.92 | a long sleeve, at the wrist |
| `LegShorts` | `legs` | `UpperLeg` 0.5 | shorts |
| `LegKnee` | `legs` | `LowerLeg` 0.1 | below the knee |
| `BootCalf` | `legs` | `LowerLeg` 0.55 | a boot's top |
| `LegAnkle` | `legs` | `LowerLeg` 0.9 | a trouser hem |

The review sheet `artifacts/art-previews/Humanoid.png` shows each body in three
rows: the cage from the front, the right side, the back, and the back left,
colored by its distance from the skin, blue at the skin, yellow at 15 mm, and
red from 30 mm, with the lines drawn on it in colors; the cage in one cloth
color from the same sides; and the torso up close in the cloth color from the
front right, the back left, the back, and the back right; and the left hand from
outside and from the front and the left foot from outside and from the front,
up close in the distance colors. The cage is drawn 4 mm off its place, so the
skin does not shine through.

## Garments

A garment template below `art/Garments` is the form of a garment kind without
its look, a mesh skinned to the shared skeleton, such as the short-sleeved top
`art/Garments/Shirt`. It is built from its recipe on the
[garment cage](#garment-cage) named in `cage` and fitted to every body of the
cage, and it is worn with a [garment texture](#garment-textures); the design is
in [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#templates).

Its file holds, per body, the collection `Export_<Label>` with the body's
armature `<Label>` and the template's version `<Name>_<Label>`, a child of it
with an Armature modifier and the material `M_Garment_<Name>`, a neutral grey.
The bodies' source bodies, eyes, teeth, and tongues are in the `Reference`
collection, unselectable, so they show the body and the fitting reads them, and
the cage's versions `Cage_<Label>` are in the `Cage` collection, taken from the
cage's file, whose SHA-256 the file keeps. The first body's version is the one a
person shapes; the file opens with the other bodies hidden, because they stand
at the same place.

The recipe in `asset.json` says what the pipeline builds:

| Field | Meaning |
| --- | --- |
| `regions` | The cage regions it covers, of `neck`, `trunk`, `arms`, `legs`, `hands`, `feet`, and `head`, such as `["trunk", "arms"]` for the shirt and `["legs", "feet"]` for the boots. A region without a cut is covered whole. |
| `cuts` | The standard lines where it ends, each with the side it keeps: `head` toward the head of the line's bone, `tail` toward its tail. The shirt keeps the `tail` of `HemHip`, the trunk above the hem, and the `head` of `SleeveShort`, the arm toward the shoulder; the trousers keep the `head` of `Waist` and of `LegAnkle`; the boots keep the `tail` of `BootCalf` and the gloves of `GloveCuff`. |
| `over` | Optional: where it lies over a garment of another slot, each a line of its regions, the side of it the inner garment keeps, and the inner garment's band. The shirt lies over `tight` trousers on the `head` side of `Waist`: there its band is measured from the inner garment's surface, the inner band's distance off the cage, and it rises to it over 30 mm before the line, also in the regions that hang from the line's region, such as a hand from its arm. The boots lie over `tight` trousers on the `head` side of `LegAnkle`, the gloves over a `tight` sleeve on the `head` side of `SleeveLong`, and the cloak's yoke over a `mid` top on the `tail` side of `CloakYoke` and the `head` side of `Neckline`, so it leaves room for a padded top. A point is over the region of the template it lies nearest, so a hem turned in at the border of the template's region, such as the cloak's neckline over a shirt's, keeps above the inner garment. |
| `hang` | Optional: its loose parts, which hang on the [cloth chains](#skeletons), each the trunk line it hangs from, which the recipe also cuts at, its `side`, `all` around the trunk for a skirt or `back` across the back for a cloak, its `chains`, `skirt` or `cloak`, and its `flare`, how much wider it grows toward its hem, from 0 to 0.1 m. A part across the back may also have a `top` and a `width`, shares above 0 and up to 1.5 of how far the shoulder joints lie from the middle, so they fit every body: with a `top` the template keeps only its middle within the top, cut straight down there, and its back only above the part's line, so a cloak's yoke lies on the trapezius and not on the shoulders and its back hangs from the nape, with a cut that need not be a cage line; the part hangs from the template's open edge at its line behind the bone there. With a `width`, no smaller than the top, the part fans out to that width by the first joint of its chains. The robe hangs a skirt `all` around from `SkirtWaist` on the `skirt` chains with a flare of 0.08; the cloak hangs its back from `CloakNape` on the `cloak` chains with a flare of 0.03, a top of 0.72, and a width of 0.8. A template with loose parts names the matching `cloth` profile. |
| `ease` | More room at the front and the back of the lower armholes, in meters, fading out over 60 mm. The proof set has none, since ease there let more skin come through in the clips. |
| `hem` | How deep every open edge is turned inward, in meters, such as `0.002`. |

A recipe of `"modeled"` instead makes a template whose first version a person
models by hand over the cage, such as a hairstyle or a helmet. A hair template's
first version also has the shape key `UnderHeadwear`, the hair pressed flat under
headwear, shaped by hand like the hair; every other version gets it fitted the
same way as its basis, and a hand correction of it is kept the same way too, in
the point attribute `fitted_under_headwear`.

`tools/Sync-GarmentAsset.ps1` brings the file up to date:

- A body or a cage version that changed in its own file is taken in again. The
  first version is bound to the old cage and placed on the new one, so it
  follows; where its cage did not move, such as when only other regions were
  added to the cage, it stays exactly where it was, so every other version is
  fitted as before.
- A new template, or one synced with `-Rebuild` after its recipe changed, gets
  its first version built from the first body's cage: the cage's faces of its
  regions, cut at its lines, every cage vertex within 8 mm of a line first moved
  onto it, so the edge runs straight; seams down the sides of the trunk, under
  the upper arms and along the inside of the forearms, each measured around its
  own bone, along the inside of the legs, along the borders between regions, so
  a hand or a foot meets its limb at a seam, between a glove's palm and its back,
  by the side of the hand each face looks to, so the seam runs along the sides
  of the fingers, and around a boot's sole, where its faces look down, and up
  the back of its heel, and UVs unfolded along them;
  an offset from the cage into the band, 2 mm for `tight`, so clothes lie as
  close to the body as they can, 10 mm for `mid`, and 20 mm for `outer` and
  `loose`, and more where `over` lifts it over an inner garment; the ease; every open edge turned inward by the
  `hem` depth with a 12 mm flap, whose faces take the look of the edge in the
  texture; and every vertex and face centre settled into the band from the cage,
  the band taken again where a point ends up until it holds. Its loose parts
  hang in rings from the open edge at their line down to the tips of their
  chains, on a scaffold that does not follow the figure: every column is
  stretched like a string from the edge over the body's convex outline to where
  the body reaches out farthest and hangs straight down from there, a skirt's
  around the belly, the hips, and the legs 30 mm outside the outline, and below
  where its chains hang from at least as far out as they, so it never narrows
  toward the hem, and a cloak's across the back 20 mm behind it and 40 mm, a
  skirt's margin and 10 mm, behind the hips and the legs, so it hangs clear of a
  skirt from the waist that bulges over the hips above its chains, where the
  runtime solver does not lay the cloak over it; both widen by the flare toward
  the hem, and a cloak with a width fans out to it by the first joint of its
  chains. They have no band and take the weights of their chains, blended
  from the edge's over the first ring, or down to where the chains hang from
  when the edge lies higher, such as a skirt from the waist, so the cloth above
  the chains moves with the body and not with a chain that turns below it. The point attribute `hang` marks the
  vertices of each loose part, `hang_top` and `hang_ring` their column and ring,
  and the face attribute `hang_face` its faces; every other version places them
  on its own body's scaffold.
  The point attribute `built` holds the shape the recipe gave, so a rebuild
  carries a shape made by hand over: each new vertex takes the difference at its
  place on the cage.
- Every other version is fitted from the first through the cage: each vertex is
  bound to a triangle of the first body's cage, of every region but the hands
  and the feet that the template does not cover, so a sleeve's or a trouser
  leg's hem follows its limb, by its barycentric coordinates
  and its offset in the triangle's frame, and placed at the same triangle and
  offset of the other body's cage. A modeled template instead moves with a
  smooth field of the cage's moves: each vertex by the moves of the cage's
  vertices, weighted by a Gaussian of their distance beyond the nearest one, 15
  mm wide plus 0.7 times the vertex's distance from the cage, so hair or a hood
  that stands far off the cage moves as one and does not crumple where its
  nearest cage face changes. The band is measured from the same regions. Every body's cage has the same faces, so this
  needs nothing of the bodies' own topology. A vertex or a face centre that then
  leaves the band moves back into it. A version corrected by hand keeps its
  corrections, because the point attribute `fitted` holds the shape the last
  fitting gave, and the difference to it is applied again.
- Every vertex gets the weights of the nearest skin of the body part its cage
  face lies on, the cage's region and side, and on a hand the face's finger or
  thumb with the palm, interpolated from the corners of the
  skin's triangle, limited to four bones, and not smoothed, so it moves like the
  skin it covers: a sleeve's inside at the armpit moves with the arm, not with
  the trunk next to it, and a glove's finger with its own finger. The point attribute `transferred_weights` holds a
  checksum of the weights the last transfer gave each vertex of the first
  version, so a vertex whose weights were painted by hand keeps them, also in
  the other versions. `-Weights` transfers every weight again.
- A file that this leaves as it was is not saved, so its approvals stay valid.

The sync then writes the review sheet `artifacts/art-previews/<Name>.png`. Per
body it shows three rows: the body in the template from four sides with its
torso from the front and the back, or for a template shorter than 40 percent of
the body, such as a glove or a boot, the one on the body's left up close from
outside and from the front, the template showing a grid in its UVs, so
the sheet shows how they stretch; the template in the test poses of `poses.py`,
with the cloth chains of its loose parts laid in the target pose that the game
shows (`cloth.py`), so a skirt follows the legs; and the body without it, its covered faces orange, the skin kept visible at the
template's edges yellow, the pulled-in vertices blue, and the skin that comes
through it in motion red. The review file `artifacts/art-previews/<Name>_poses.blend`
places the bodies side by side with an action that steps through the test
poses. Last, the sync checks every rule and reports what the template covers.
The `model` stage checks that the bodies and the cage are the current ones of
their files and the cage the approved one, that every body has a version with
the first version's topology and one material, within the triangle budget and
with every vertex and every face centre in its band from the cage, a hair
template's also in its shape key `UnderHeadwear`, and that the fitting and the
weights are as a sync leaves them. A point is too close by its straight distance
to the cage's nearest point and too far by its distance along the normal of the
cage's nearest face, as the recipes build the cloth: in front of a sharp edge of
the cage, such as the nose, the distance along either face's normal falls far
short of the true one. The `rig` stage adds the rig
rules of skinned assets for each version on its armature, the motion check, and
the pair check: every exported template of another visual slot and a lower
layer of the cage, such as the trousers under a shirt, is put on the bodies from
its own file, and of its faces that stay drawn, those whose body face this
template leaves visible at its openings, every vertex that this template is over
at rest, or that already lies outside it, is followed through the rest pose, the
test poses, and the clips. Only vertices that face away from the cage are
followed: a turned edge's rim and flap face the body and lie under the inner
template's outer side, which is followed instead. The report names how many come
out through this template and the deepest place, as the motion check does. A
body wears one garment per visual slot, so templates of the same slot, such as a
shirt and a jerkin, are not checked against each other, and a template is not
checked over a slot it hides, such as the robe over trousers. Hair is checked
under headwear in the form the headwear gives it: in its shape key
`UnderHeadwear` under `"hair": "compress"`, as it is under `"show"`, and not at
all under `"hide"`. Both checks follow
loose parts with their cloth chains laid in every pose and frame in the target
pose that the game shows (`cloth.py`), so they report a leg that comes through a
skirt or a cloak; the swing of the springs is reviewed in the Wardrobe.
A template is approved and exported only on an approved cage whose bodies still
have the exports the cage was approved on, so it is never fitted to an obsolete
cage.

`coverage.py` computes what a template covers on each body, in the faces and
vertices of the body's editable mesh:

- **The cover mask**, the body faces the garment hides. From five points on a
  face, seven rays go out in a cone of 25 degrees around its normal. The face is
  covered when every ray meets the garment where its texture is opaque, or the
  body, within the garment's reach: the distance from the skin to the cage there
  and the band's reach above it, 30, 50, 100, or 300 mm, so rays from the bottom
  of a pit that the cage spans reach the cloth over it. At least one ray must
  meet the garment. A hole, an open hem, or a transparent part of the texture
  lets a ray out and leaves the face visible; a face that only the body hides,
  such as the inside of the mouth, stays visible too. The skin at the garment's
  edges stays visible as well, since a look into a sleeve or under a hem sees
  past the cone: a face whose centre or a corner lies within the edge reach of
  an open edge of the garment is never covered. The edge reach is the band's,
  20, 40, 80, or 120 mm, or four times the gap between the skin and the edge
  where the cloth hangs farther off, up to 120 mm. The open edges are the
  borders of its mesh, the lines its loose parts hang from, and the lines where
  its texture is cut, sampled every 5 mm where the garment is opaque. Loose
  parts cover nothing, since they swing off what they hang over at rest, and a
  template with `"covers": false`, such as the cloak, covers no face at all.
- **The underlay**, the body face under each garment face, along its inward
  normal, or the nearest one.
- **The pull-in**: a visible body vertex closer to the garment than the band's
  smallest distance, and at least 3 mm, moves inward by the difference, at most
  3 mm.
- **Motion**: every visible vertex that the garment is over at rest keeps the
  point of the garment over it, and so does one that already lies outside the
  garment at rest, with the point of the garment just inward of it, at most 50
  mm inward. A point of the garment only counts when no body lies between it
  and the vertex, outward or inward, so the hair that the ray from an eye's
  corner meets beyond the face is not over the eye. The body,
  with the pull-in applied, is measured at rest and takes every test pose and
  every frame of every clip of the skeleton's animation libraries.
  A vertex comes through the garment when it lies more than 1 mm outside the
  cloth at its point with the cloth between it and the inside; skin that passes
  a hem's edge has no cloth there and does not come through. Skinning blends the
  bones of a fold, such as an armpit, so the gap between skin and cloth closes
  there in motion whatever the weights. A vertex that comes through lies at an
  opening that shows it, so it is not hidden: the report names how many come
  through and the deepest place, the review sheet shows their faces red, and the
  template's shape or weights fix them. Skin that lies through the garment
  already at rest shows in every pose, so it breaks the rule, and so does an
  inner template's vertex that lies through it at rest in the pair check; the
  cage keeps the skin inside it, so a template on it has none. The rule needs
  the clips, so it runs at the template's `rig` stage, which its export runs,
  and a texture with transparent parts checks its own masks the same way before
  its export writes anything.

`New-ArtAsset.ps1 -Kind garment` starts a template from its recipe on the cage,
with `-Slot`, `-Band`, `-Regions`, `-Cuts` as `<Line>:head` or `<Line>:tail`,
`-Over` as `<Line>:<head|tail>:<band>`, `-Hang` as
`<Line>:<all|back>:<skirt|cloak>:<flare>`, or across the back
`<Line>:back:cloak:<flare>:<top>:<width>`, `-Cloth`, `-Hides`,
`-CoversNothing`, `-Ease`, and `-Hem`, or `-Modeled`; `-Cage` is
`art/Cages/Humanoid` unless it says otherwise.

The export writes one model per body of the cage,
`Models/Garments/<Name>_<Label>.fbx`, from its `Export_<Label>` collection, the
template skinned to that body's armature, and its data
`Models/Garments/<Name>_<Label>.json`. The data holds `bodyFaces`,
`bodyVertices`, and `garmentFaces`, the `covered` body faces, the `underlay` of
every garment face, the `pullInVertices` with their `pullInDistances` in meters,
and the template's own `garmentVertices`, `garmentFaceSizes`, and
`garmentFaceVertices`, by which Unity finds the face of each triangle. The faces
and vertices it names are those of the body's topology, which the body's export
writes next to its model: its editable mesh's `vertices` in Blender's world
space, `faceSizes`, `faceVertices`, and `faceZones`. A template is exported after
its cage is approved and after its bodies and the animation libraries of its
skeleton are exported from their current files, and it records the cage's approved file and their
exports, so `Verify-ArtAssets.ps1` fails when the cage's file changed or a body
or a library has a newer export than the one the template was fitted to and
checked in: sync, review, and export the template again. It also records the
exports of the templates it was checked over in the pair check, so templates
that are exported together, such as after a change of the cage, are exported in
the order of their layers, the trousers before the boots before the shirts
before the gloves; a texture of a template exported before a template of a lower
layer stops with `was checked over other templates of a lower layer than the
current exports of its cage's templates` until its template is exported again.
Its material has no image, so the game shows a template only with a texture.

Garments of different layers check each other when a character wears them
together, which comes with the pair check of the first pair of templates that
overlap.

Cloth in motion is reviewed in Unity with `MMORPG > Tools > Art Pipeline >
Wardrobe`: pick a body, tick the garments it wears, and play `Idle`, `Walk`,
`Jog`, `Death`, or a tool's `Mine` or `Chop`, with the other hand pinned on a
ticked tool's haft as in the game, or a stress motion such as a spin, a sharp turn, a stop, a
zig-zag, the zig-zag as the network shows a remote body, jittery frames, or a
jump. The body is put together as the game puts it together, walks and jogs
forward at the clips' speeds over a checkered ground that the camera follows, so
loose cloth lags as in the game, and the cloth solver runs with its springs, as
on the bodies nearest the camera in the game; untick `Springs` to see the target
pose alone, as every other body shows it. `Playback` slows it down to a tenth,
and `Step 1/60 s` steps a paused body a frame at a time. Beside it the window
shows what `ClothMotionMeter` measures: the solver's level, how far its springs
are faded in and whether its cloth sleeps, how many leg and arm vertices come
through the loose cloth now and at most, a robe through a cloak, the chains'
jerk, how far a spin flares them and how long their swing takes to settle, and
how often the legs push a cloak's lower half. `Tune the springs` has a slider
for every spring value of every profile the body wears, which applies at once
and keeps while other garments are worn, `Reset` puts a profile back, and
`Log profiles` writes the tuned profiles to the console, from which they go
into `ClothProfile`. Drag in the view to turn the camera and scroll to come
closer. `MMORPG > Tools > Art Pipeline > Measure Cloth` measures every motion on
both bodies in the robe, the cloak, and both, and a zig-zag at other frame times
playing the clips and with the pose held, in about a minute and a half, and
writes the table to `artifacts/cloth-measurements.md`.

### Cloth Parity

The review sheets, review files, and checks lay a garment's cloth chains in the
target pose that every visible body in the game shows, without springs. The
laying is `cloth_core.py`, which uses only the standard library and works on
plain data, and the Unity client's `ClothPose` is its source: `cloth.py`
measures in Blender what the laying needs, the capsules from the body's vertices
as posed and in the bind pose, the legs' turns against the hips, the chains as
they hang from their posed parents and in the bind pose with their neighbours,
and the garment's reach about each particle, hands it to `cloth_core.lay`, and
turns the chain bones to point at the laid particles. Both read their constants
from the `cloth` section of the skeleton; see
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#runtime-solver) for how
the target pose lays the chains.

`MMORPG > Tools > Art Pipeline > Write Cloth Fixtures` captures both bodies in
the robe, the cloak, and both, in frames of their clips, in leg poses set by
hand, and in seeded random leg poses, each with what the client's Burst jobs
laid, and writes them to `tools/blender/tests/fixtures/cloth_kinematics.json`,
one case per line. `ClothPoseTests` lays every case again with `ClothPose` and
captures the bodies again, and fails with the menu to run when the target pose,
a body, or a garment changed; the Python tests lay every case with
`cloth_core.py` and must come within 0.01 mm of it, and check that the bind pose
moves nothing but a cloak over a robe. They run without Blender, in CI and in
`tools/Test-Changes.ps1` for a change below `art/` or `tools/blender/`:

```powershell
python -m unittest discover -s tools/blender/tests
```

Expected result: `Ran 3 tests` and `OK`. After a change of the cloth in the
client or of a body or garment that it hangs on, run the menu, commit the
fixtures, and run the Python tests again.

## Garment Textures

A garment texture below `art/GarmentTextures` is the look of one template, named
in `garment`, such as the leather `art/GarmentTextures/LeatherJerkin` of
`art/Garments/Shirt`; the design is in
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#textures). Its file holds
the image `T_Garment_<Name>`, packed, 512, 1024, or 2048 px square, with the
custom properties `roughness` and `metallic`, and the mesh `Preview`, a copy of
the template's first version with the material `M_Garment_<Name>` that shows the
image, for painting in Texture Paint mode. `Preview` keeps the UVs the image is
painted in, and its property `template_uvs` the fingerprint of the template's
UVs then. Where the image's alpha is below 0.5, the garment is cut: the skin
shows through, with its paint. A texture and a template both export the material
`M_Garment_<Name>`, so a texture never takes a template's name: `New-ArtAsset.ps1`
refuses it, and `Verify-ArtAssets.ps1` fails when two assets export a material of
the same name.

- `New-ArtAsset.ps1 -Kind texture -Garment art/Garments/<Template>` starts a
  texture of `-Size` pixels in a light grey, or with `-ProjectFrom
  <file.blend>=<Object>` with the look of another mesh, its color and its alpha
  moved to the template's UVs by projection: Cycles bakes them from `Preview`'s
  surface, looking 60 mm both ways for the other mesh.
- `tools/Sync-TextureAsset.ps1` moves the texture to the template's new UVs the
  same way, from the old `Preview`, when the template's UVs changed, such as
  after a rebuild of its recipe, or when `Preview`'s own faces or UVs were
  edited, and leaves the file as it was otherwise. It then writes the review
  sheet and checks every rule.
- The `texture` stage checks that the image is packed as a PNG, of a known
  size, with its roughness and metallic, that `Preview` shows it, and that it was painted in the
  template's current UVs, which `Preview` still has. A texture with transparent parts, such as the
  jerkin's laced V, also gets masks of its own on every body, because its holes
  change what the body shows: the template's file is opened with the texture on
  every version, and `coverage.py` computes the cover mask, underlay, and
  pull-in and runs the motion check with the template's rules. An opaque
  texture uses the template's masks.
- The review sheet `artifacts/art-previews/<Name>.png` shows the template with
  the texture on every body from four sides with the torso up close and in the
  test poses, and for a texture with transparent parts the body without it in
  the colors of the template's sheet, with the texture's own mask.
- The export writes the image to `Textures/Garments/T_Garment_<Name>.png` and the
  material `M_Garment_<Name>` with it as `baseMap`, and for a texture with
  transparent parts its masks per body, `Models/Garments/<Name>_<Label>.json`, in
  the format of the template's data. The masks are computed in the template's
  file with its bodies, cage, and clips, so the export runs only when the
  template is approved and exported from its current file, its export still
  matches its cage, its bodies, and the clips of its skeleton, and the texture
  was approved on that file of the template, since its fit and masks change with
  the template's shape and not only with its UVs. An animation library that is
  not exported from its current file stops only a texture with transparent
  parts, before anything is written. The export records the `export.sourceSha256`
  of the template's export it was checked against, so `Verify-ArtAssets.ps1`
  fails when the template has a newer export: sync, review, and export the
  texture again.

A texture may be dyeable, as the wool of `WoolCloak`, `WoolRobe`, `WoolHoodUp`, and
`WoolHoodDown` is since 2026-10-25: the image `T_Garment_<Name>_Dye` in its file is
its dye mask, whose red, green, and blue channels mark how far each dye channel
tints a texel, read as data, with the custom property `dye_colors`, the natural
color of each channel it uses, `#RRGGBB` joined by `;`. The garment shader
multiplies the painted color by a channel's color as far as the channel reaches,
so a dyeable region is painted in a light neutral, and its natural color gives
back its painted look: the undyed garment wears its natural colors. Every dyeable
region's middle tone lies at the same linear level, 0.7, about 85 percent light,
so one dye color gives the same cloth on every dyeable texture: its middle tone is
0.7 times the dye color, in linear values.

- `tools/Set-TextureDyes.ps1 -Asset art/GarmentTextures/<Name> [-Regions <png>]`
  makes a painted texture dyeable without painting it again. `-Regions` is a PNG
  of the texture's size whose red, green, and blue mark the texels each channel
  tints, such as the cloak's wool without its bronze clasps; without it the first
  channel tints every texel. A channel's natural color is its region's middle
  tone over the level, and the image is divided by what the shader multiplies it
  by, so the texture undyed looks as it was painted, within one step of 255 on
  the wool set. A texel too light to be divided so, such as a light stitch on dark
  wool, keeps part of its painted color: the mask reaches only as far as its light
  allows. The template must allow the channels in its `dyes`. The script then
  writes the review sheet and checks every rule, as `Sync-TextureAsset.ps1` does,
  and a template whose UVs change moves the dye mask with the image.
- The `texture` stage checks a dye mask too: packed as a PNG of the image's
  size, read as Non-Color data, a natural color for each channel it uses and no
  more channels than its template's `dyes`, and shown by `Preview`. The review
  sheet shows the texture in its natural colors.
- The export writes the dye mask as `Textures/Garments/T_Garment_<Name>_Dye.png`
  and names it and the natural colors in the material's record.

In Unity, `Create Missing Materials` creates
`Assets/Art/Materials/Garments/M_Garment_<Name>.mat` with the garment shader,
gives every garment texture's material the dye mask, the number of its channels
in `_DyeCount`, and its natural colors in `_DyeColor1` to `_DyeColor3` from its
record, also when the material exists, and
the appearance catalog of every body lists every exported texture of an exported
template that fits the body as a garment named after the texture: the
template's model for the body and its slot, layer, and hides, the texture's
material, which the template's model draws with instead of its own, and the
texture's masks, or else the template's. A character wears a garment by the
texture's name, such as `LeatherJerkin`, in the Admin menu (F3) and in the Wardrobe, and one
garment per visual slot: trying one on takes off the other garment of its slot,
such as another chest piece, and of two of a slot that a look names, the first
in the catalog is worn.

## Attachments

An attachment is a static model that a socket of a skeleton holds, such as a
sword in the right hand or a shield in the left: an asset of kind `static` below
`art/Attachments` with a `socket` in `asset.json` and one root object, the empty
`Grip`, at the origin. Its meshes lie below `Grip` in the socket's own axes, so
the model stands on the socket bone as it stands on the origin. The socket bones
of `Humanoid` place every attachment on every body; an attachment never moves
them, and a place that suits only one item is that item's own geometry.

The axes of a hand's socket, in the model's file:

| Axis | `Socket_MainHand`, the right hand | `Socket_OffHand`, the left hand |
| --- | --- | --- |
| Origin | The middle of the fist: the knuckles' centre, 28 mm toward the palm, where a handle of 29 mm lies. | The same on the left hand. |
| +Y | Out of the thumb side of the fist, forward when the arm hangs: a blade or the point of a spear. | The same. |
| +Z | Out of the palm, toward the body when the arm hangs. | The same. |
| +X | Toward the wrist. | Toward the knuckles, down when the arm hangs. |

A sword's grip therefore runs along Y through the origin with the blade toward
+Y, its edges along X and its flats facing Z. A shield held by a handle and a
strap on the forearm lies against the back of the hand and the forearm, toward
-Z, along the forearm. The fingers close around the handle in the clip `Grip`,
so every handle that a hand holds is about 29 mm thick at the origin.

`New-ArtAsset.ps1 -Kind static -Asset art/Attachments/<Name> -Socket main_hand
-TriangleBudget <n>` creates it. Its review sheet shows it alone from four sides,
from above and from below in turn, and up close around `Grip`, and then a row per
body of the animation library of its socket's skeleton that has `Idle`: each
body in the first frame of `Idle` with the fingers of `Grip`, holding the model
on its socket, whole, from outside the arm, and its hand up close from the front,
from inside with only the arms shown, and from behind.

In Unity the model imports readable. The appearance catalog of every body of its
socket's skeleton lists it as an attachment with its socket and socket bone,
`PlayerBodyMeshes` combines it into the body's one mesh with every vertex
weighted fully to the socket bone, which stands on its parent as in the model's
bind pose, and the animator layer of that hand plays `Grip`; see
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#attachments).
`ItemAttachmentTests` fails when an item names an attachment that a body's
catalog lacks or that its slot does not hold, when a model has another root than
`Grip` at its origin, or when a body in a jog pose draws an attachment's vertex
more than 1 mm from where its model puts it on the socket.

### Weapons With Clips

A weapon held on `main_hand` that is no tool may name a `holdClip`, the pose its
arm holds it in, and an `attackClip`, the clip its body plays once whenever its
attack releases, both clips of an animation library of its socket's skeleton,
and with an attack clip a `launchPoint`, the point of the model where its
projectile leaves, in meters in the socket's axes. The Oak Staff names
`"holdClip": "HoldStaff"`, `"attackClip": "Cast"`, and
`"launchPoint": [0.0, 0.7, 0.0]`, its crystal. The review sheet of the weapon
shows the bodies with the arm in the hold clip, and the sheet of the library shows
the weapon held in the rows of both clips. In Unity the appearance catalog lists
the clips and the launch point, converted to the model's axes like an
`offHandGrip`, and the controller has a state of each clip's name on the layers
`MainHandHold` and `Action`; `WeaponClipTests` checks the layers, their masks,
the states, the launch point, and that both bodies hold the staff upright and
thrust it forward.

### Tools

A tool is an attachment that a body holds only while it uses it, such as a
pickaxe while its character gathers. Its `asset.json` names its `useClip`, a clip
of an animation library of its socket's skeleton, and a tool held with both hands
on `main_hand` names its `offHandGrip`, the point of the model where the other
hand holds it in the socket's axes; that hand's socket is turned 180 degrees
about the socket's Y axis there, so both thumbs point along the haft toward the
head. The manifest refuses a `useClip` that no library of the skeleton has. The
review sheet of the library shows every tool held on its socket by every body in
the rows of its clip, and `Render-ArtPreview.ps1` finds the tools by their
`useClip`.

In Unity the appearance catalog lists the tool's `UseClip` and its `OffHandGrip`,
converted to the model's axes as the import converts the model, Blender's
`(x, y, z)` to Unity's `(x, z, y)`, and the controller has a looping base layer
state of the clip's name. `ToolInUseTests` fails when a tool item can be
equipped elsewhere than the Tool slot, when a tool's clip has no such state,
when its other hand's grip lies off the model, or when a body's other hand does
not end up on the grip through the swing; see
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#tools-in-use).

## Live Work With The Blender MCP Server

The Blender Lab MCP server lets Claude Code inspect and change the Blender file
that is open on the desktop, while the reviewer watches. It has two parts:

- The `MCP` add-on from Blender Lab, version 1.0.3, runs inside Blender and
  listens on `localhost:9876` once Blender starts. It needs Blender 5.1 or newer
  and the preference `Allow Online Access`.
- `.mcp.json` starts the `blender-mcp` server with `uvx`, pinned to the `v1.0.3`
  tag of `projects.blender.org/lab/blender_mcp`. The server offers 26 tools,
  among them `execute_blender_code`, viewport screenshots, scene summaries, and
  searches in the bundled Blender Python API reference and user manual.

The add-on runs any Python code it receives, without a sandbox, so it listens on
localhost only. The pipeline never depends on it: approval and export always run
Blender in the background from the scripts.

## Modeling Rules

Every model follows these rules, whoever or whatever builds it, a person in
Blender, Claude Code through the Blender MCP server, or a script. They come from
the reviewer's feedback on the human base characters and the first wolf, and they
hold until the reviewer changes them here.

- **Agree on the method first.** How a model is built is agreed with the
  reviewer before the work starts. Changing the method needs the reviewer's
  approval. When a method gives a poor result, fix the result within the method
  or ask; never switch to another method on your own.
- **One part at a time.** Build and finish one part of the model, such as the
  head, the legs with the paws, or the trunk, and show it to the reviewer before
  starting the next. Never change several parts in one round, and leave a part
  the reviewer accepted as it is while working on the next.
- **Follow the reference closely.** When the reviewer gives a reference, such as
  a concept picture or a reference mesh, put it into the `.blend` file at the
  model's scale: pictures as reference images in the views they show, such as the
  front and the side, and meshes in the `Reference` collection. Match the model
  to it view by view, first the silhouette, then the planes, then the colors,
  and compare the two side by side in the same views before showing a part.
- **Polygon modeling only.** Build the model polygon by polygon, by hand in
  Blender or with a script that places every vertex and face on purpose: quads
  and triangles that follow the form, with edge loops around the joints so they
  bend cleanly. Never sculpt, never generate the form from volumes, metaballs, or
  distance fields, and never remesh or decimate a model automatically.
- **Few, clean polygons.** Use as few polygons as the form needs, as clean and
  regular as possible, and keep it simple: the simplest geometry that reads well
  from the game camera.
- **Even density.** Spread the polygons evenly over the whole model, with only as
  many more as a detail needs where it matters, such as the face, the paws, or
  the joints.
- **Exact mirror symmetry.** Model one half and mirror it, so both sides are
  identical. A model is only asymmetric where its design asks for it.
- **Look before showing.** Look at every render critically, up close and from
  every side, before sending it for review: wrong shapes, gaps, faces poking
  through, stray colors, and anything that differs from the reference.
- **Fix, do not restart.** When the reviewer points out flaws in a part that is
  otherwise accepted, fix exactly those, without rebuilding the part.

## Art Direction For Models And Textures

The setting asks for stylized low to mid poly art with exaggerated proportions,
strong silhouettes, and clear colors, so actors stay readable from a zoomed-out
camera that turns freely ([Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md)).
For models and their textures this means:

- Fantasy before realism. Every new model and texture is a creative design for
  this world, not a 1:1 copy of something real: shapes, proportions, colors,
  materials, and details lean toward fantasy, such as a sword with a sweeping
  guard and a carved blade, ore that breaks out of the rock in large crystals,
  or a cloak with a bold trim and embroidered patterns, while the object stays
  easy to recognize for what it is. The
  [human base characters](#human-base-characters) are the exception: their
  bodies, faces, eyes, and skin stay realistic unless a task asks for something
  else. What is made for them, such as clothes, armor, weapons, and tattoos,
  follows the rule.
- What identifies an actor, such as the ore of a vein, is visible from every
  side and large enough to read at the default camera distance.
- Flat-shaded facets with hard edges and flat material colors, with a few
  materials per asset, also on Mobs and other creatures. Playable characters are
  the exception: smooth shading with the form painted into their textures, as on
  the human base characters.
- The model fills the actor's `interactionBounds` from the content catalog, so
  the click area matches what the player sees.
- Triangle budgets, the most a model may have: 2,000 for a prop, such as a
  resource node or an attachment; 5,000 for a Mob or another creature; 10,000
  for a playable character with its eyeballs, teeth, and tongue. The budget is a
  ceiling, not a target, and each asset sets its own `triangleBudget` within it.

## Human Base Characters

`art/Characters/Human_Male` and `art/Characters/Human_Female` are the base bodies
of the playable humans, rigged with the `Humanoid` skeleton. Their clips come
from the [animation library](#animation-libraries) `art/Animations/Human_Actions`.
Hair, brows, beards, clothes, and equipment are modeled on top of them later, and
faces are painted on them. A script first built both in October 2026 from the
realistic bodies and the Generic Topology head of the
[Human Base Meshes](https://www.blender.org/download/demo-files/) bundle v1.4.1
by Blender Studio (CC0). Since 2026-10-22 their files are the source: a person
edits them in Blender, and `tools/Sync-HumanBase.ps1` keeps everything that
follows from them up to date.

Each file holds one character:

- The armature (`Male` or `Female`) at the origin, the only root object of the
  `Export` collection, with the bones of the `Humanoid` skeleton. It carries
  landmarks as custom properties, such as the eyes, lips, wrists, finger tips,
  and toes, which placing the joints again uses and attachments may use.
- The source body `<Label>_Body` in the `Source` collection, bound to the
  armature: the whole body as one mesh, the surface a person edits, with smooth
  shading, the ear rims as sharp edges, the face attribute `zone` that names the
  body part of every face, and the skin weights. It shows in the viewport, and
  renders and the export leave it out.
- Ten body parts in the `Export` collection, named `<Label>_<Zone>`: `Head`,
  `Neck`, `Torso`, `Pelvis`, `UpperArms`, `Forearms`, `Hands`, `Thighs`,
  `Shins`, and `Feet`, so equipment can hide whole parts. The sync writes them
  from the source body, with its corner normals as custom normals, so they meet
  without visible seams, with its weights, and with the paint UV map
  `UVMap_Paint`, the source body's `UVMap` with the body texture's faces laid out
  twice as wide, the left side in the left half and the right side in the right
  half, so [paint layers](#paint-layers) can differ between the sides. They are hidden in the viewport
  and never edited by hand; the approval and the export refuse parts that differ
  from the source body.
- Two eyeballs (`<Label>_Eye_L` and `_R`) for gaze animation, and upper and lower
  teeth and a tongue inside a closed mouth cavity for jaw animation, edited where
  they are.
- 9,202 triangles: 8,692 for the body and 510 for the eyeballs, teeth, and
  tongue. Both bodies are exactly mirror symmetric and have identical topology,
  so paint moves from one body to the other exactly, face by face.
- Smooth shading, with hard edges only on the ear rims, and the form painted into
  the textures: `M_Body_<Label>` uses the 2048 px body texture
  `T_Body_<Label>`, plain skin under the underwear, which is paint of its own,
  `M_Face_<Label>` a 1024 px face texture,
  `M_Eye_<Label>` a 512 px iris texture, and `M_Teeth_<Label>` and
  `M_Tongue_<Label>` flat colors. The face and eye variants for character
  creation are packed into the file and exported as
  [variants](#appearance-layers): `T_Face_Male_Stubble`, `T_Face_Male_Clean`,
  `T_Eye_Male_Teal`, and `T_Eye_Male_Brown` for the male, `T_Face_Female_Blush`,
  `T_Face_Female_Freckles`, `T_Eye_Female_Teal`, and `T_Eye_Female_Hazel` for
  the female. Another face is the face or eye material with another image.

An edit keeps both bodies alike: the same change of topology on both, so they keep
identical topology, made with X mirror, so they stay symmetric, and with every new
face in the zone of its neighbours. Moving vertices changes neither the topology
nor the weights; new vertices need weights, painted by hand on the source body or
solved with `-Weights`.

`tools/Sync-HumanBase.ps1` brings both characters up to date after an edit, one
Blender process per step:

1. `sync`: the body parts of each character from its source body. With
   `-Weights` the skin weights are first solved again from the joints of the
   rig, which replaces weights painted by hand, and with `-Rig` the joints are
   first placed again from the model. A new rig needs a new sync and review of
   the animation libraries keyed on it.
2. `check-rig`: the step changes no mesh at rest, the art pipeline's rig rules
   hold, and the weights are mirror symmetric.
3. `check-mesh`, `check-seams`, and `check-parts`: no open or over-shared edges,
   tiny faces, slivers, or loose pieces on the source bodies, exact mirror
   symmetry, identical topology of both bodies and of every part, at most 10,000
   triangles per character, matching cuts and normals between the parts, no texel
   of `UVMap_Paint` that both sides of a body share, and a report of the color
   difference across the UV seams.
4. `compare`: the content of each updated character against the committed file
   (objects with their modifiers, constraints, and visibility, the evaluated
   meshes with UVs, normals, and edge flags, vertex weights, the armature with
   its bones and pose, materials with their node links, textures with whether
   they are packed, and custom properties). Blender's files differ in their bytes
   from run to run, so the script replaces a committed file only when its
   content changed, and an unchanged file keeps its approval.
5. The review files `Human_Base.blend`, both characters side by side, and
   `Human_Base_Rig.blend`, with the test poses as an animation, and the review
   sheets of the models and the rigs, with the cloth chains on a sheet of their
   own.

Placing the joints puts every joint where the model has it: the hips from the
crotch and the leg cut, the spine at fixed shares between the hips and the neck,
the shoulder in the deltoid cap found by ray casts from where the rig had it, the
elbows, knees, wrists, and ankles at the cuts between the parts, the finger and
toe joints from rings of the digit meshes, the jaw hinge from the lip line
landmarks, and the cloth chains from the body's surface, so they fit every body.
The sync adds bones the skeleton has and a rig lacks, such as new cloth chains,
and leaves every other bone as it is. Bone heat gives the first weights,
limited per body zone and per digit, and anatomical rules shape them where it
matters: the shoulder blends over the deltoid and leaves the chest in place; the
thigh takes the skin below a boundary that runs through the groin crease, over
the hip, along the gluteal fold, and through the crotch, with wide blends over
the buttock and the underwear line and a radial fade at the crotch, so a raised
thigh leaves no notch; and the knee and elbow blend across the joint. The jaw
takes the lower face, the eyes, the upper teeth, the lower teeth, and the tongue
are rigid on their bones, and the weights are mirrored so both sides match
exactly. Solving the weights again on an unchanged character gives the same
weights.

## Assets

| Asset | Kind | Roots | Notes |
| --- | --- | --- | --- |
| `art/Animations/Human_Actions` | `animations` | `Humanoid` | Every clip of the human bodies, keyed on the rig of `Human_Male` and shown on both bodies. `Idle`, 4 s, `Walk`, 28 frames at 1.4 m/s, and `Jog`, 19 frames at 5.0 m/s, looping, from the gait model. `Death`, 2 s, made by hand, not looping: a hit snaps the head back, the right knee buckles first, the body tips over to its right side and backward, lands on its back with the legs swinging up, the head hits a moment later, the legs fall with the left knee outward, and the body lies still on its back with the arms out and the head turned aside. Approved 2026-10-22. `Grip`, one frame, looping, keys only the fingers and thumbs: both hands closed around a handle of 29 mm on their sockets. Approved 2026-10-25. `Mine` and `Chop`, 45 frames, looping, both hands closed on a tool's haft, the right hand 33 cm above the left: `Mine` strikes a pickaxe down in front of the body at about knee height, lifts it over the right shoulder with the head behind the shoulder, and strikes again; `Chop` lifts an axe over the right shoulder and hews diagonally down to the left at waist height. Both keep every arm inside the humanoid muscle ranges and turn the torso, bend the knees, and look at the stroke. Approved 2026-10-26. |
| `art/Attachments/MiningPickaxe` | `static` | `Grip` | The tool of the Starter Pickaxe, held on `main_hand` with both hands while mining, 960 triangles, 86 cm: an ash haft with a leather wrap in seven bands where the left hand holds it and a brass butt cap, and a curved iron head, 43 cm across, with a long point on the striking side and a flared chisel, both curving toward the hands, on a chamfered eye boss with a blue faceted ore crystal in a brass bezel on each cheek, over a brass collar inlaid in blue with the rune Fehu on its front and back. `useClip` `Mine`, `offHandGrip` 33 cm down the haft. Approved 2026-10-26. |
| `art/Attachments/WoodcuttingAxe` | `static` | `Grip` | The tool of the Woodcutting Axe, held on `main_hand` with both hands while chopping, 1,090 triangles, 90 cm: an oak haft with a leather wrap in seven bands where the left hand holds it and a golden acorn as its butt knob, and a bearded iron head with a bright honed edge 21 cm long, its top horn sweeping up and its beard hanging toward the hands, with a lobed oak leaf inlaid in gold on each cheek. `useClip` `Chop`, `offHandGrip` 33 cm down the haft. Approved 2026-10-26. |
| `art/Attachments/HeaterShield` | `static` | `Grip` | A small heater shield held on `off_hand`, 1,436 triangles: a board 56 cm wide and 70 cm from the straight top to the point, 12 mm thick and bowed 4 cm toward the arm at its sides, its face painted blue, its back plain wood, an iron edging bent over the edge, a leather-wrapped handle in the fist on two straps, a loose leather strap around the forearm 20 cm toward the elbow, and iron rivets through both faces where the straps hold. It lies 76 mm behind the handle along the forearm, turned 12 degrees toward the little finger, as the wrist of `Idle` turns the hand, so it hangs outward at the side with its point down. Approved 2026-10-25. |
| `art/Attachments/IronSword` | `static` | `Grip` | A plain iron arming sword held on `main_hand`, 1,008 triangles, 94 cm: a disc pommel with a peened tang end, a leather grip of 10 cm swelling slightly in the middle, a straight cross guard of 19 cm, and a blade of 74 cm, 5 cm wide at the guard, with a fuller over 70 % of it, tapering to the point. Approved 2026-10-25. |
| `art/GarmentTextures/DarkBrownHair` | `texture` | none | Dark brown hair on `ShoulderHair`, 1024 px, painted by a script in the template's UVs, which run around the head through the locks and down along the hair: lighter strands of random width along the hair, deep grooves between the locks, a soft sheen around the crown, darker roots, and slightly lighter tips. Roughness 0.55. Approved 2026-10-24. |
| `art/GarmentTextures/IronNasalHelm` | `texture` | none | Matte wrought iron on `NasalHelm`, 1024 px, painted by a script from each texel's point and from the part of the helmet its face is, which the template marks in the face attribute `paint_part`: hammered plates darkened by patina with a few rust specks toward the rim and into the corners beside the bands, the bands a little lighter and worn bright along their edges, domed rivets down every band, round the brow band, at the cap, and two on the nasal, the iron's dark underside at the rim, and the brown leather lining below it. Roughness 0.55, metallic 0.45. Approved 2026-10-24. |
| `art/GarmentTextures/LeatherBoots` | `texture` | none | Dark brown leather on `Boots`, 1024 px, painted by a script from each texel's point on the template: fine grain and blotches, a darker band with stitches along the top of the shaft, stitched seams around the ankle and up the inside of the shaft and the back of the heel, a burnished toe cap and heel counter, and a near-black sole with a darker welt stitched around it. Roughness 0.55. Approved 2026-10-24. |
| `art/GarmentTextures/LeatherGloves` | `texture` | none | Soft, lighter brown leather on `Gloves`, 1024 px, painted the same way: fine grain and blotches, stitched seams between the palm and the back along the sides of the fingers, around the wrist, and along the inside of the cuff, and a darker band with stitches at the cuff's edge. Roughness 0.6. Approved 2026-10-24. |
| `art/GarmentTextures/LeatherJerkin` | `texture` | none | The leather jerkin on `Shirt`, 1024 px: warm brown leather with blotches and grain, darker trim with stitches along the neckline, the hem, and the sleeve ends, and an open V at the front, laced across, where the skin shows between the laces. It has masks of its own. Roughness 0.62. Moved from the first leather jerkin garment and then from the template `Jerkin`, which became `Shirt`, by projection; when `Shirt` got its round neckline on 2026-10-24, the new part of the neckline took the leather from just below it, the V ran on up along its edges with its trim, and a new trim with stitches was painted along the neckline. |
| `art/GarmentTextures/LinenLongShirt` | `texture` | none | Plain oatmeal linen on `LongShirt`, 1024 px, with a fine weave, soft wear, stitched hems at the neckline, the hem, and the cuffs, and stitched seams. Roughness 0.82. |
| `art/GarmentTextures/LinenShirt` | `texture` | none | The same linen on `Shirt`. |
| `art/GarmentTextures/NetShirt` | `texture` | none | A coarse hemp-colored net on `Shirt`, 1024 px: round cords of 2.4 mm in diamond meshes 25 mm apart around the trunk and each arm, transparent between them, with solid hems 14 mm wide at the neckline, the hem, and the sleeve ends. It has masks of its own, so the skin and its paint show through the meshes. Roughness 0.88. |
| `art/GarmentTextures/WoolCloak` | `texture` | none | Thick dark brown wool on `Cloak`, 1024 px, painted by a script from each texel's point on the template: fibres and mottling, vertical folds that deepen down the back, and a darker turned hem with stitches along every edge. In front, as Victor asked on 2026-10-25, the cloth fans out like a triangle from a large cast bronze clasp at the bottom of each side of the yoke, 68 mm across, with a beaded rim, a rosette of eight petals, and a domed boss: its edges run from the clasp up to the side of the neck and out past the yoke's side, both reached at the top of the shoulder, with no cloth around the clasp, so the throat and the chest stay free. The alpha follows the cut by its distance from the triangle's edges, smooth across the texels. Roughness 0.82. Dyeable: one dye channel over the wool, not the clasps, natural color `#4E3422`; its image is the neutral wool, which the natural color gives back within one step of 255. Approved 2026-10-25. |
| `art/GarmentTextures/WoolHoodDown` | `texture` | none | The wool of `WoolHoodUp` on `HoodDown`: hems with stitches along the collar's hem and the turned band's edge, the seam down the middle of the folded hood and the collar, and the folded hood's own side folds a little darker, without hems. Approved 2026-10-24. Dyeable like `WoolRobe`, natural color `#8D806E`. |
| `art/GarmentTextures/WoolHoodUp` | `texture` | none | The undyed grey-brown wool of `WoolRobe` on `HoodUp`, 1024 px, painted by a script from each texel's point on the template: fibres and mottling, a stitched seam down the middle from the face opening over the crown to the collar's hem and down the bib, and a darker turned hem with stitches along the face opening and the collar's hem. Roughness 0.86. Approved 2026-10-24. Dyeable like `WoolRobe`, natural color `#8D806E`. |
| `art/GarmentTextures/WoolRobe` | `texture` | none | Undyed grey-brown wool on `Robe`, 1024 px, painted the same way: fibres and mottling, folds that deepen down the skirt from the waist, stitched seams, among them the waist seam where the skirt starts, and darker turned hems with stitches at the neckline, the cuffs, and the skirt's hem. Roughness 0.86. Dyeable: one dye channel over all of it, natural color `#8D806E`; its image is the neutral wool, which the natural color gives back within one step of 255. Painted again 2026-10-25 for the skirt from the waist. |
| `art/GarmentTextures/WoolTrousers` | `texture` | none | Brown wool on `Trousers`, 1024 px, with a coarse weave, a darker waistband, stitched hems at the ankles, and stitched seams, among them a yoke across the hips. Roughness 0.9. |
| `art/Garments/Boots` | `garment` | none | Boots in the `mid` band, layer 120, from the recipe `legs` and `feet`, cut at `BootCalf`, over `tight` trousers below `LegAnkle`, turned edges 4 mm deep, 1,376 triangles per body: a shaft to the middle of the calf 10 mm off the cage and a foot over the cage's smooth feet, without toes, with a flat sole. Approved 2026-10-24. |
| `art/Garments/Cloak` | `garment` | none | A cloak in the `back` slot, layer 500, `tight` band, from the recipe `neck` and `trunk`, cut at `Neckline` and `CloakYoke`, over a `mid` top above `CloakYoke` and below `Neckline`, so it lies about 1 cm off the shirt and leaves room for a padded top, turned edges 4 mm deep: a yoke over the trapezius, not the shoulder muscle, from the round neckline down to below the collarbones in front, kept within a top of 0.72 of the shoulders' half width, and a loose back hanging taut from `CloakNape`, just below the base of the neck, between the arms on the `cloak` chains to the middle of the calves, fanning out to a width of 0.8 with a flare of 0.03, 920 triangles per body, 243 vertices of them on the chains. It covers nothing (`"covers": false`). Reshaped and approved 2026-10-25. |
| `art/Garments/Gloves` | `garment` | none | Gloves with five fingers in the `tight` band, layer 350, from the recipe `arms` and `hands`, cut at `GloveCuff`, over a `tight` sleeve below `SleeveLong`, turned edges 2 mm deep, 1,808 triangles per body, the cuff about 7 cm up the forearm. Approved 2026-10-24. |
| `art/Garments/HoodDown` | `garment` | none | The gugel of `HoodUp` pushed back, modeled, in the `head` slot, layer 600, `loose` band, `"hair": "show"`, 1,036 triangles per body: the collar around the neck over the hair and the cloak, its top rolled over into a band that hangs outside it, and the hood folded on the upper back as a rounded bag with soft folds, from the roll down 20 cm. Two sheets meet at the roll's top, the collar and the band with the folded hood, each with its outside out. The collar's weights move with the neck and blend into those of the cloak where it lies on it. Its collar is open around the neck, so it covers nothing (`"covers": false`). Approved 2026-10-24. |
| `art/Garments/HoodUp` | `garment` | none | A gugel worn up, modeled, in the `head` slot, layer 600, `loose` band, `"hair": "compress"`, 1,186 triangles per body: over the head and the compressed hair with an opening for the face from the brows to under the chin, and a short collar over the neck and the tops of the shoulders that ends above the cloak's clasps, over the cloak, built on rings over the head, the hair, the body, and the cloak. It moves with the head down to the chin and blends over the neck into the upper chest and the shoulders, and where the collar lies on the cloak into the cloak's own weights, halfway where the hair lies close under it. Approved 2026-10-24. |
| `art/Garments/LongShirt` | `garment` | none | `Shirt` with long sleeves, cut at `SleeveLong`, 2,284 triangles per body. |
| `art/Garments/NasalHelm` | `garment` | none | A nasal helmet, modeled, in the `head` slot, layer 600, `outer` band, `"hair": "compress"`, 2,092 triangles per body, all on `Head`: a pointed spangenhelm over the head and the compressed hair, four bands raised 2.5 mm from the front, the sides, and the back to the point, a brow band 28 mm high above the brows and the ears, the leather lining 4.5 mm below the iron, and a nasal riveted to the brow band that bends forward over the nose's root and runs down the bridge. The female version's nasal is shaped by hand, a rigid copy of the male's with its brow band. Approved 2026-10-24. |
| `art/Garments/Robe` | `garment` | none | A floor-length robe in the `body` slot, layer 300, `tight` band, from the recipe `neck`, `trunk`, and `arms`, cut at `Neckline`, `SkirtWaist`, and `SleeveLong`, turned edges 3 mm deep: `LongShirt`'s top down to just below the navel with a loose skirt hanging all around from `SkirtWaist` on the `skirt` chains to the ankles, 30 mm off the belly, the hips, and the legs and with a flare of 0.08, so it does not follow the figure, 2,860 triangles per body, 416 vertices of them on the chains. It hides the `legs` slot, so the boots show below it. Its skirt starts at the waist since 2026-10-25. |
| `art/Garments/Shirt` | `garment` | none | A short-sleeved top in the `tight` band, layer 150, from the recipe `neck`, `trunk`, and `arms`, cut at `Neckline`, a round neckline that hugs the neck, `HemHip`, and `SleeveShort`, over `tight` trousers below `Waist`, turned edges 2 mm deep, without ease or collar, 1,644 triangles per body, on `art/Cages/Humanoid`. |
| `art/Garments/ShoulderHair` | `garment` | none | Shoulder-length hair, modeled, in the `hair` slot, layer 550, `hair` band, 2,298 triangles per body: straight, thick stylized locks that fall from the crown, a middle parting, a short blunt fringe, the ears covered, the ends where they land on the shoulders, and the shape key `UnderHeadwear`, the hair pressed flat under hoods and helmets with the fringe and the neck hair showing. Its weights are painted by hand. Approved 2026-10-24. |
| `art/Garments/Trousers` | `garment` | none | Trousers in the `tight` band, layer 100, from the recipe `trunk` and `legs`, cut at `Waist` and `LegAnkle`, turned edges 2 mm deep, 1,496 triangles per body, with seams along the inside of the legs. |
| `art/Cages/Humanoid` | `cage` | none | The garment cage of the `Humanoid` skeleton, made from the male body on 2026-10-23 and fitted to the male and the female, with a figure scale of 60 mm and the fifteen standard lines of [Garment Cage](#garment-cage); its regions `hands` and `feet` were added on 2026-10-24, the line `CloakYoke` the same day, which moves none of its faces, and the region `head` after them, 3,424 faces in all, and the lines `CloakNape` and `SkirtWaist` on 2026-10-25, which move none either. Approved 2026-10-25. |
| `art/Paint/Bra` | `paint` | none | The woman's `underwear` in the `body` slot over `M_Body`, layer 910, on a canvas of the female: the pale blue bra with its cups, band, and straps over the middle of the shoulders, lifted out of her body texture with its shaded edge. Roughness 0.8. |
| `art/Paint/Briefs` | `paint` | none | `underwear` in the `legs` slot over `M_Body`, layer 900, on a canvas of the male, moved to the female: pale blue briefs with a darker waistband, lifted out of the male body texture. Roughness 0.8. |
| `art/Paint/ForearmRunes` | `paint` | none | A one-sided `tattoo` over `M_Body`, layer 210, painted by a script on a canvas of the male and moved to the female: the runes of the fuþark, f u þ a r k, in a column down the outside of the left forearm between two thin lines, 30 mm apart, in the ink of `Tattoo`. Roughness 0.6. |
| `art/Paint/Scar` | `paint` | none | A `scar` over `M_Face`, layer 100, on a canvas of the male, moved to the female, lifted out of the former male face `Scarred`: a pale scar with a darker outline over the brow, the outer corner of the right eye, and down the cheek. Roughness 0.5. |
| `art/Paint/Tattoo` | `paint` | none | A `tattoo` over `M_Body`, layer 200, painted on a canvas of the male and moved to the female: a Nordic armband of two thin rings with interlocking chevrons and dots around each upper arm, and a round rune emblem of eight spokes, rings, and ticks on the upper breastbone, in dark blue-black, slightly faded ink. |
| `art/Characters/Human_Female` | `skinned` | `Female` | The female human base body, 1.70 m, with its source body, ten body parts, eyeballs, teeth, and tongue, 9,202 triangles, and the `Humanoid` rig. Its skin is plain under the underwear, which is paint of its own, painted again from the mesh with its baked occlusion; the faces `Blush` and `Freckles`. Model and rig approved 2026-10-24. The game shows it for the local player who chooses it in the Admin menu (F3) and for about half of the development bots. |
| `art/Characters/Human_Male` | `skinned` | `Male` | The male human base body, 1.79 m, with the same topology as the female, its source body, 9,202 triangles, and the `Humanoid` rig. Its skin is plain under the underwear, which is paint of its own, painted again from the mesh with its baked occlusion; the faces `Stubble` and `Clean`. Model and rig approved 2026-10-24. The game shows it for every player until a body model is chosen, and for every corpse. |
| `art/WorldActors/Resource_IronVein` | `static` | `Available`, `Depleted` | Three faceted rocks with chunky rust-orange iron ore in two crossing veins, 684 triangles; the depleted state keeps the rocks with broken flat tops and loose rubble, without ore, 194 triangles. The available state is about 1.5 m wide, 1.25 m deep, and 1.0 m high, inside the vein's 1.6 by 1.2 by 1.6 m interaction bounds; the depleted rubble spreads to about 1.9 by 1.7 m. Approved 2026-10-20. |
| `art/WorldActors/Resource_OakTree` | `static` | `Available`, `Depleted` | A fantasy oak, 1,456 triangles in all: a thick trunk twisted about 70 degrees on five flaring roots, three boughs into a broad crown of eight faceted leaf clumps, dark green with light green where a face looks up, and four golden acorns of 41 cm on stalks below the crown's rim, so the tree reads from every side; the depleted state is the stump cut slanted at about 0.8 m with a pale heartwood face, two splinters on its high side, the same roots, and five wood chips. The tree is about 5.6 m high and 4 m wide, inside its 4 by 5.6 by 4 m interaction bounds. Five materials: bark, leaves, light leaves, acorn, and heartwood. Approved 2026-10-26. |

## Planned

- The body type at character creation, chosen in the Admin menu (F3) until then, and the look
  chosen there, saved with the character and sent to every client.
- Eyelid bones for blinking and forearm twist bones.
- A model fix of the low armpits, where the triceps meet the torso, and of the
  female's bra straps, which are painted up her neck, above the collar of chest
  paint.
- Hold poses for attachments, such as a sword raised or a shield in guard, and
  weapons sheathed at the hip or on the back while out of combat.
- A shared palette texture for stylized models, level-of-detail meshes, and
  per-asset collision proxies if presentation ever needs them.

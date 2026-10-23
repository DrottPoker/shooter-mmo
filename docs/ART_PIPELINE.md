# Art Pipeline

Last updated: 2026-10-23

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

Every kind of art has one short recipe in
[Local Development](LOCAL_DEVELOPMENT.md#art-pipeline-with-blender). After an
export, Unity creates the project materials an export lacks and updates the
appearance catalogs by itself when it gets focus, so no menu command stands
between the export and the game.

| To | Start with | Recipe |
| --- | --- | --- |
| Add a model for the world, such as a resource node or a prop | `New-ArtAsset.ps1 -Kind static` | [Add A New Asset](LOCAL_DEVELOPMENT.md#add-a-new-asset) |
| Change a human body: its shape, skin texture, or weights | the body's `.blend` file, then `Sync-HumanBase.ps1` | [Edit The Human Base Characters](LOCAL_DEVELOPMENT.md#edit-the-human-base-characters) |
| Add a face or an eye color | an image packed into the body's `.blend` file | [Add A Face Or Eye Variant](LOCAL_DEVELOPMENT.md#add-a-face-or-eye-variant) |
| Change a clip or add one | the library's `.blend` file, or `Sync-AnimationLibrary.ps1 -Generate` for a gait | [Make A Clip](LOCAL_DEVELOPMENT.md#make-a-clip) |
| Start an animation library | `New-ArtAsset.ps1 -Kind animations` | [Make A Clip](LOCAL_DEVELOPMENT.md#make-a-clip) |
| Paint onto the skin, such as a tattoo or scars | `New-ArtAsset.ps1 -Kind paint`, then `Sync-PaintAsset.ps1` | [Make A Paint Layer](LOCAL_DEVELOPMENT.md#make-a-paint-layer) |
| Fit the garment cage to the bodies, or move a standard line | `Sync-CageAsset.ps1` | [Fit The Garment Cage](LOCAL_DEVELOPMENT.md#fit-the-garment-cage) |
| Make a garment template, the form of clothes or armor, from a recipe on the cage | `New-ArtAsset.ps1 -Kind garment`, then `Sync-GarmentAsset.ps1` | [Make A Garment](LOCAL_DEVELOPMENT.md#make-a-garment) |
| Paint the look of a garment template, with holes where the skin shows | `New-ArtAsset.ps1 -Kind texture`, then `Sync-TextureAsset.ps1` | [Make A Garment Texture](LOCAL_DEVELOPMENT.md#make-a-garment-texture) |
| Review, approve, and export any of them | `Render-ArtPreview.ps1`, `Approve-ArtAsset.ps1`, `Export-ArtAsset.ps1` | [Review, Approve, And Export An Asset](LOCAL_DEVELOPMENT.md#review-approve-and-export-an-asset) |

Modeled clothes, armor, and hair come next, as [attachments](#appearance-layers).

## Layout

| Path | Responsibility |
| --- | --- |
| `art/<Category>/<Name>/<Name>.blend` | Source of truth for one asset, stored in Git LFS. The folder name is the asset name. |
| `art/<Category>/<Name>/asset.json` | Asset manifest: kind, export target, root objects, triangle budget, skeleton and clips of a skinned asset, approvals, and the export record. |
| `art/Animations/<Name>/<Name>.blend` | An [animation library](#animation-libraries): handmade clips of one skeleton, keyed on the rig of one character, with the bodies that show them. |
| `art/Paint/<Name>/<Name>.blend` | A [paint layer](#paint-layers): transparent paint over the skin of bodies, painted on the first body and moved to the others. |
| `art/Garments/<Name>/<Name>.blend` | A [garment template](#garments): the form of clothes or armor, built from a recipe on the garment cage and fitted to every body of the cage, one version per body. |
| `art/GarmentTextures/<Name>/<Name>.blend` | A [garment texture](#garment-textures): the look of one template, its image in the template's UVs. |
| `art/Cages/<Skeleton>/<Skeleton>.blend` | The [garment cage](#garment-cage) of a skeleton: the net of fixed topology that garments are made on, fitted to every body of the skeleton, with its standard lines. |
| `art/Skeletons/<Name>.json` | A shared [skeleton definition](#skeletons): the bones every skinned asset of that skeleton has, their parents, their Unity Humanoid bones, and whether they deform the skin. |
| `src/Client/Assets/Art/Models/<Category>/<Name>.fbx` | Exported model. It is never edited by hand; CI rejects a model without an asset or with a changed file. A body with an editable mesh exports its topology `<Name>.json` next to it, a garment template one model per body, `Models/Garments/<Name>_<Label>.fbx`, with its data `<Name>_<Label>.json` next to it, and a garment texture with transparent parts its own data per body, `Models/Garments/<Name>_<Label>.json`. |
| `src/Client/Assets/Art/Textures/<Category>/<Image>.png` | Exported image of a painted material, byte for byte as it is packed in the `.blend` file. Never edited by hand; CI rejects a texture without an export record or with a changed file. Paint layers export to `Textures/Paint` and garment textures to `Textures/Garments`. |
| `src/Client/Assets/Art/Shaders` | `CharacterSkin.shader` and `CharacterSkinInput.hlsl`, the skin shader `Mmorpg/Character Skin`: URP Lit with paint layers; `Garment.shader` and `GarmentInput.hlsl`, the garment shader `Mmorpg/Garment`: URP Lit with dye channels. |
| `src/Client/Assets/Editor/WardrobePreview.cs`, `WardrobeWindow.cs` | `MMORPG > Tools > Art Pipeline > Wardrobe`: a body dressed in chosen garments, playing its clips on a moving ground with the cloth solver running. |
| `src/Client/Assets/Art/Materials` | Unity URP Lit materials. A Blender material maps to the Unity material of the same name. |
| `src/Client/Assets/Art/Animation` | Animator controllers that play the exported clips, such as `HumanLocomotion.controller`. |
| `tools/blender/art_pipeline` | Python that runs inside Blender: validation, FBX export, the review sheet, in `bodies.py` the body parts of a skinned character written from its editable body, and in `poses.py` the test poses of the `Humanoid` skeleton that the rig reviews use. |
| `tools/blender/art_pipeline_cli.py` | Blender entry point for the scripts below. |
| `tools/New-ArtAsset.ps1` | Creates a new static model, animation library, paint layer, garment cage, garment template, or garment texture: its folder, `asset.json`, and first `.blend` file. |
| `tools/Render-ArtPreview.ps1`, `tools/Approve-ArtAsset.ps1`, `tools/Export-ArtAsset.ps1` | Review, approval, and export commands. They share `tools/ArtPipelineCommon.ps1`. |
| `tools/Verify-ArtAssets.ps1` | CI check of every asset without Blender. |
| `tools/Sync-AnimationLibrary.ps1`, `tools/blender/art_pipeline/library.py` | Builds an animation library file from its rig, or updates it when the rig changed, and keeps its clips. |
| `tools/Sync-PaintAsset.ps1`, `tools/blender/art_pipeline/paint.py` | Builds a paint layer file from its bodies, or updates it, and moves the paint of the first body to the others. |
| `tools/Sync-CageAsset.ps1`, `tools/blender/art_pipeline/cage.py` | Fits the garment cage to every body from its reference, writes its review sheet, and checks it. |
| `tools/blender/art_pipeline/body_files.py` | Appends the bodies a garment or the cage is fitted to from their own files, and appends them again when they change. |
| `tools/Sync-GarmentAsset.ps1`, `tools/blender/art_pipeline/garment.py`, `coverage.py` | Builds a garment template from its recipe on the cage, fits it to every body through the cage, gives every version the weights of its body, computes what it covers on every body, writes its review sheet and review file, and checks it. |
| `tools/Sync-TextureAsset.ps1`, `tools/blender/art_pipeline/texture.py` | Moves a garment texture to its template's new UVs, computes the masks of a texture with transparent parts, writes its review sheet, and checks it. |
| `tools/Sync-HumanBase.ps1`, `tools/blender/human_base`, `tools/blender/human_base_cli.py` | The tools of the [human base characters](#human-base-characters): their body parts written from their editable bodies, their rig and weights, their checks and review sheets, and the gait model that starts clips in an animation library. |
| `src/Client/Assets/Editor/ArtModelImportRules.cs` | Unity import rules for every model below `Assets/Art/Models` and every texture below `Assets/Art/Textures`. |
| `src/Client/Assets/Editor/ArtAssetManifests.cs` | Reads `asset.json` and the skeleton definitions for the import rules, the material command, and the tests. |
| `src/Client/Assets/Editor/ArtMaterialCreator.cs` | `MMORPG > Tools > Art Pipeline > Create Missing Materials`: creates the project material of every exported material that has none, from its export record. |
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
| `rootObjects` | The exact names of the top-level objects in the `Export` collection. They become the direct children of the Unity model, so they carry the contract with the game code, such as `Available` and `Depleted` for a resource node. The one root object of an animation library is its armature, named after its skeleton. |
| `triangleBudget` | The maximum number of exported triangles, after modifiers. An animation library exports no meshes and has none. A garment's budget holds for each body's version. |
| `skeleton` | Skinned assets and animation libraries: the name of the skeleton definition in `art/Skeletons`, such as `Humanoid`. |
| `rig` | Animation libraries only: the skinned asset whose rig the clips are keyed on, such as `art/Characters/Human_Male`. |
| `previews` | Animation libraries only: other skinned assets of the skeleton whose bodies show the clips in the library, such as `["art/Characters/Human_Female"]`. |
| `clips` | Animation libraries, and skinned assets with clips of their own: the names of the animation clips the asset exports, such as `["Death", "Idle", "Walk", "Jog"]`. Each is one Blender action. A skinned asset whose clips come from animation libraries, such as the human bodies, leaves `clips` out. |
| `part` | Paint layers only: the part of the painted material `M_<Part>_<Label>`, such as `Body`. |
| `bodies` | Paint layers: the skinned assets the paint fits, the body it is painted on first, such as `["art/Characters/Human_Male", "art/Characters/Human_Female"]`. They must have the same topology. The cage: the skinned assets it is fitted to, the one it was made from first, of any topology; its garment templates fit them. |
| `cage` | Garment templates only: the garment cage it is made on, such as `art/Cages/Humanoid`. |
| `recipe` | Garment templates only: its `regions`, `cuts`, `ease`, and `hem`, as [Garments](#garments) describes, or `"modeled"`. |
| `garment` | Garment textures only: the template it is the look of, such as `art/Garments/Shirt`. |
| `figure` | The cage only: the figure scale in meters, the radius of the ball that rolls over the skin, from 0.01 to 0.2. |
| `lines` | The cage only: the standard lines, each with its `name`, its `region` (`neck`, `trunk`, `arms`, or `legs`), its `bone`, without the side `_L` or `_R` for the arms and legs, and its `share` of the bone's length, from 0 to 1. |
| `fittedTo` | The cage only, written by its approval: every body with the `fitSha256` of the body's export the cage was approved on, the fingerprint of the body's rig, surface, and weights, so a change of only a body's images leaves the cage as it is. |
| `layer` | Paint layers: the order paint layers are drawn in, from 0 to 1000, the lowest first, such as `30` for a scar over the tattoo at `10`. Garments: the order of a character's garments from the inside out, from 0 to 1000, such as `150` for a shirt over trousers at `100`. |
| `slot` | Garments only: the visual slot, `head`, `body`, `legs`, `hands`, `feet`, `back`, or `hair`. |
| `band` | Garments only: how far from the garment cage the garment lies, `tight` (0 to 8 mm), `mid` (5 to 25 mm), `outer` (10 to 60 mm), or `loose` (from 10 mm). |
| `hides` | Garments only: the other visual slots it hides, such as `["legs"]` for a floor-length robe, or `[]`. |
| `hair` | Garments only: for headwear, what happens to the hair under it, `hide`, `compress`, or `show`; `null` for every other slot. |
| `cloth` | Garments only: the cloth profile of its loose parts, `robe`, `cloak`, or `hood`, or `null`. |
| `dyes` | Garments only: the number of dye channels. It is `0` until the garment shader brings dyes. |
| `variants` | Skinned assets only, optional: the [appearance variants](#appearance-layers), each a painted material `M_<Part>_<Label>` with the other `images` `T_<Part>_<Label>_<Option>` it may show in the game, such as `{ "material": "M_Face_Male", "images": ["T_Face_Male_Scarred"] }`. The material's own image is named `T_<Part>_<Label>_<Option>` as well. |
| `approvals` | One entry per approved review stage: the `date` and the `sourceSha256` of the `.blend` file that was approved. A garment texture's approval also records the `garmentSourceSha256` of its template's file, since it is reviewed on the template. |
| `export` | The `sourceSha256` of the exported `.blend` file, the `targetSha256` of the written FBX, for a body with an editable mesh the `dataSha256` of its topology and the `fitSha256`, the fingerprint of what the garment cage and the garments read from it, its rig, its surface with its faces and their body parts, and its weights, and `materials`: the approved look of every exported material as URP Lit values, `baseColor` (sRGB `#RRGGBB`), `metallic`, and `smoothness` (1 - Roughness). A painted material also names its image in `baseMap`, and its `baseColor` is white. `textures` lists every exported image, those the materials name and the variants, with its `name`, `target` PNG, and `sha256`, present only when there are textures, and `clips` lists every clip of a skinned asset with its `name`, length in `frames`, and whether it should `loop`. The export of an animation library has no materials or textures; it records its `clips` and the `rigSourceSha256`, the `export.sourceSha256` of its rig when it was exported. The export of a paint layer records its `textures`, one `T_Paint_<Name>_<Label>` per body below `src/Client/Assets/Art/Textures/Paint`, and the `smoothness` and `metallic` of the paint, without a `targetSha256`. The export of a garment template records the `cageSourceSha256` of the approved cage file it was fitted to; its `models`, one per body of the cage in order with its `body` label, the `bodyFitSha256` of the body's export it was fitted to, the model's `target` and `sha256`, and its `data` file and `dataSha256`; its `clipSources`, every animation library of its skeleton with the `sourceSha256` of the export whose clips it was checked in; its `pairSources`, every template of another visual slot and a lower layer of its cage with the `sourceSha256` of the export it was checked over, when there are any; its one material `M_Garment_<Name>` without an image; and no `textures`. The export of a garment texture records the `garmentSourceSha256` of its template's export, its `masks` with their `body`, `data`, and `dataSha256` and their `clipSources` when it has transparent parts, its material `M_Garment_<Name>`, and its image `T_Garment_<Name>` below `src/Client/Assets/Art/Textures/Garments`. `null` before the first export. |

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
  without textures or other nodes feeding them. Textures for static
  assets come with the planned palette texture. A `skinned` asset may feed the
  Base Color straight from one Image Texture whose image is packed into the
  `.blend` file, such as a painted body, read through the mesh's active UV map,
  which every mesh with that material must have.
- The exported triangles stay within `triangleBudget`.
- Every variant image is packed into the file as a PNG of the same size as the
  image of its material, so it fits the material's UV layout.

The `rig` stage of a skinned asset adds these rules:

- The `Export` collection holds exactly one armature, the only root object, with
  every mesh as its child. Its bones are exactly those of the skeleton
  definition, with the same parents and deform flags.
- The armature is in its rest pose and shows it (Pose Position), without
  constraints, an active action, or NLA tracks.
- Every mesh has exactly one modifier, an Armature modifier on that armature
  that deforms with vertex groups only, and no shape keys. Its vertex groups are
  deforming bones only.
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

- The file holds the mesh `Paint_<Label>` and the image `T_Paint_<Name>_<Label>` of every
  body. Every image is packed as a PNG with alpha, of the size of its body
  texture, with the custom properties `roughness` and `metallic` from 0 to 1.
- The paint of every body but the first is the move of the first body's paint
  and has its roughness and metallic, so the file shows what the game will. A
  file that a painter changed without the sync stops with `is not the move of`.

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
| `cloak` | `CloakCenter1` to `5`, `CloakInner1_L` to `5_L` and `_R`, and `CloakOuter`, five chains of five | `UpperChest` | Across the shoulder blades, 4 cm below the base of the neck and 1.5 cm behind the body, hanging to the knees. |
| `hood` | `Hood1`, `Hood2` | `Head` | Behind the head at eye height, stepping down and back. |

A chain bone's Z axis points away from the body, the way its cloth faces.

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
  for a static model. Presentation never takes part in physics.
- Mesh data is uncompressed, with imported normals and calculated tangents, and
  not readable for a static model.
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
the body's own model.

A garment's model gets a generic rig without an avatar of its own and without
clips, because the character it is put on animates it: its armature is the
body's, and the game moves it with the character's skeleton. `Optimize Game
Objects` is off and its mesh is readable, because the game combines it with the
visible body parts.

Every PNG below `Assets/Art/Textures` imports as an sRGB texture with mipmaps,
without alpha, not readable, clamped, trilinear, at most 2048 px, with high
quality compression. The images of paint layers and garments, below
`Textures/Paint` and `Textures/Garments`, keep their alpha.

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

A project material is never changed by the import. When an export adds a
material, `MMORPG > Tools > Art Pipeline > Create Missing Materials` creates it
as a URP Lit material from its export record, with its texture for a painted
material, below `Assets/Art/Materials/<Category>`, and reimports the models that
use it; a garment texture has no model, and its material goes to
`Assets/Art/Materials/Garments`. The material of a garment template or texture
draws with the garment shader `Mmorpg/Garment`,
URP Lit with three dye channels, and cuts its texture at alpha 0.5 and draws both
faces, so holes, lace, and torn edges show sharp and the inside of a sleeve is
drawn. Like the skin shader, it is a copy of URP's `Lit.shader` and
`LitInput.hlsl` in which only the include, the dye properties `_DyeMask`,
`_DyeColor1` to `_DyeColor3`, and `_DyeCount`, and the dyeing of the albedo
differ; when URP is upgraded, it is made again from the new files with the same
changes.

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
a clip is missing, has another length, or loops differently than recorded. For a
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

`Assets/Prefabs/Player/HumanMaleBody.prefab` is the Prefab Variant of
`Human_Male.fbx` that the local and remote player prefabs show as
`PlayerVisual/Body`, in place of their old capsule. It adds the Animator with
`Assets/Art/Animation/HumanLocomotion.controller`, the locomotion driver, and
the body's look; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).
`PlayerBodyAssetTests` checks the variant, the controller, and both player
prefabs.

## Animation Libraries

Clips live in animation libraries: art assets of kind `animations` below
`art/Animations`, keyed on the rig of one character and played on every body of
the skeleton. `art/Animations/Human_Actions` holds every clip of the human bodies,
`Idle`, `Walk`, `Jog`, and `Death`, and the bodies hold none:

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

In Unity, `Human_Actions.fbx` brings its clips as Humanoid motion, and
`HumanLocomotion.controller` plays them on every player body; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).

## Appearance Layers

Players change how their bodies look in the style of World of Warcraft: other
paint on the same model, such as another face or eye color, equipment painted
onto the skin, often together with a modeled part, hair as a mesh with paint on
the scalp, and later skin colors. As with clips, each layer is made by hand in
an asset of its own or in the character's file, so a change to one layer leaves
the others as they are.

| Layer | Made as | In the game | Status |
| --- | --- | --- | --- |
| Texture variants | Other images of a painted material, packed in the character's `.blend` file and named in its manifest's `variants`, such as the faces `T_Face_Male_Stubble` and `T_Face_Male_Scarred` and the eye colors `T_Eye_Male_Teal` and `T_Eye_Male_Brown`. They are reviewed and approved with the model. | The material shows the chosen image. | Implemented. |
| Paint layers | Transparent paint over the skin in the body texture's UV layout, such as a tattoo or a scalp, in a [paint layer](#paint-layers) asset of its own. Painted once on the male body and moved to every body of the same topology. | The skin shader lays it over the skin when the body is drawn. | Implemented. |
| Garments | Meshes skinned to the shared skeleton, such as clothes, armor, and hair: [garment templates](#garments) built on the [garment cage](#garment-cage) and fitted to every body of it, each worn with a [garment texture](#garment-textures) of its own asset. | Combined with the visible body parts into the body's one mesh; see [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md). | Implemented, with the Wardrobe preview and trying garments on in F2. |

The faces and eyes of the human base characters are variants: images packed into
the character's file, shown by the model review, and exported with the model
through `variants` in `asset.json`. In Unity, the body's `BodyAppearanceCatalog` lists one slot per
material with variants, such as `Face` and `Eye`, with its options, the
material's own image first, and a look picks one option per slot; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).

## Paint Layers

A paint layer below `art/Paint` is paint over the skin of the bodies, such as the
tattoo `art/Paint/Tattoo`. Its file holds, per body, the
mesh `Paint_<Label>`, the body's parts joined in its rest pose, and the image
`T_Paint_<Name>_<Label>` in the layout of the body texture `T_<Part>_<Label>`,
transparent where the skin shows. A preview material lays the image over the
skin by its alpha, with its roughness, so Blender's Material Preview shows the
look. The paint is made on the first body, by hand in Texture Paint mode or by a
script, and its image carries the custom properties `roughness` and `metallic`.
The bodies' UVs are mirrored, so both sides of a body share their texels and
paint on the skin is the same on both sides, and it lies over the underwear,
which is painted into the body texture. A one-sided tattoo comes with paint
layers that are independent of the bodies' topology, in delivery step 5d of
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#delivery-plan).

Bodies of the same topology have the same faces in the same order, so
`tools/Sync-PaintAsset.ps1` moves the paint from the first body to every other
one: each other body gets the first body's UV map as `UVMap_Paint`, and Cycles
bakes the first body's paint, read through that map, into the other body's own
UV layout. A texel takes the paint at the same place of the same face, and the
colors and the alpha bake separately. The approval and the export check that the
moved paint is current, and the export writes every image as it is packed.

In Unity, a paint image imports with its alpha, and its transparent texels take
the color of their neighbors, so filtering at the edges never darkens the skin.
The skin shader `Mmorpg/Character Skin` is URP Lit with up to four paint layers:
only the include of `CharacterSkinInput.hlsl`, a copy of URP's `LitInput.hlsl`
with the paint properties in the material constant buffer, and the paint
properties differ from Lit. Each layer is a `_PaintMap<N>` with
`_PaintSurface<N>` (smoothness, metallic) and lays its color, tinted like the
base map, its smoothness, and its metallic over the surface by its alpha, in
order; `_PaintCount` says how many layers are drawn. The body materials that a
paint layer paints, such as `M_Body_Male`, draw with this shader, and without a
layer they draw like Lit. `Create Missing Materials` creates them with it. When
URP is upgraded, the shader is made again from the new `Lit.shader` and
`LitInput.hlsl` with the same changes. The appearance catalog of every body lists
the paint layers that fit it, in the order of their `layer`.

## Garment Cage

`art/Cages/Humanoid` is the garment cage of the `Humanoid` skeleton: a sparse net
of fixed topology that lies over every body of the skeleton, smooth over the
body's pits and following its figure, on which garments are made and fitted; the
design is in
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#the-garment-cage). It has
1,576 faces over the neck, the trunk, the arms to the wrists, and the legs to the
ankles, in four regions: `neck`, `trunk`, `arms`, and `legs`.

Its file holds:

- The reference in the `CageReference` collection: the armature `CageRig`, a
  copy of the male body's rig when the cage was made, and on it the mesh `Cage`,
  the male body's faces of its `Neck`, `Torso`, `Pelvis`, `UpperArms`,
  `Forearms`, `Thighs`, and `Shins` parts, with their weights and the face
  attribute `region`. It never changes: a new base model changes the bodies, not
  the cage. The figure scale and the lines it was last fitted with are custom
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
   closer to the skin than 1 mm.

A version corrected by hand keeps its corrections, because the point attribute
`fitted` holds the shape the last fitting gave, and the difference to it is
applied again. A file that a sync leaves as it was is not saved, so its approval
stays valid.

Its `model` stage checks that the bodies are the current ones of their files,
that the cage was fitted with the figure scale and the lines of `asset.json` and
is fitted as the fitting gives it now, that every version has the reference's
topology, that no vertex or face centre lies closer to the skin than 1 mm, that
no two faces cross each other, and that every line crosses its region on every
body. The report names how far each version lies from the skin and how many
vertices span pits more than 5 mm deep.

The standard lines are planes at a `share` of a bone's length, at right angles
to the bone, or level across the trunk, which stands upright at rest while its
bones lean:

| Line | Region | Bone and share | Where |
| --- | --- | --- | --- |
| `CollarStand` | `neck` | `Neck` 0.3 | a stand collar's top, about 3 cm up the neck |
| `HemCrop` | `trunk` | `Chest` 0.5 | under the bust |
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
front right, the back left, the back, and the back right. The cage is drawn 4 mm
off its place, so the skin does not shine through.

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
| `regions` | The cage regions it covers, of `neck`, `trunk`, `arms`, and `legs`, such as `["trunk", "arms"]` for the shirt. A region without a cut is covered whole. |
| `cuts` | The standard lines where it ends, each with the side it keeps: `head` toward the head of the line's bone, `tail` toward its tail. The shirt keeps the `tail` of `HemHip`, the trunk above the hem, and the `head` of `SleeveShort`, the arm toward the shoulder; the trousers keep the `head` of `Waist` and of `LegAnkle`. |
| `over` | Optional: where it lies over a garment of another slot, each a line of its regions, the side of it the inner garment keeps, and the inner garment's band. The shirt lies over `tight` trousers on the `head` side of `Waist`: there its band is measured from the inner garment's surface, the inner band's distance off the cage, and it rises to it over 30 mm before the line. |
| `ease` | More room at the front and the back of the lower armholes, in meters, fading out over 60 mm. The proof set has none, since ease there let more skin come through in the clips. |
| `hem` | How deep every open edge is turned inward, in meters, such as `0.002`. |

A recipe of `"modeled"` instead makes a template whose first version a person
models by hand over the cage, such as a hairstyle or a helmet.

`tools/Sync-GarmentAsset.ps1` brings the file up to date:

- A body or a cage version that changed in its own file is taken in again. The
  first version is bound to the old cage and placed on the new one, so it
  follows.
- A new template, or one synced with `-Rebuild` after its recipe changed, gets
  its first version built from the first body's cage: the cage's faces of its
  regions, cut at its lines, every cage vertex within 8 mm of a line first moved
  onto it, so the edge runs straight; seams down the sides of the trunk, under
  the arms, and along the borders between regions, and UVs unfolded along them;
  an offset from the cage into the band, 2 mm for `tight`, so clothes lie as
  close to the body as they can, 10 mm for `mid`, and 20 mm for `outer` and
  `loose`, and more where `over` lifts it over an inner garment; the ease; every open edge turned inward by the
  `hem` depth with a 12 mm flap, whose faces take the look of the edge in the
  texture; and every vertex and face centre settled into the band from the cage.
  The point attribute `built` holds the shape the recipe gave, so a rebuild
  carries a shape made by hand over: each new vertex takes the difference at its
  place on the cage.
- Every other version is fitted from the first through the cage: each vertex is
  bound to a triangle of the first body's cage, by its barycentric coordinates
  and its offset in the triangle's frame, and placed at the same triangle and
  offset of the other body's cage. Every body's cage has the same faces, so this
  needs nothing of the bodies' own topology. A vertex or a face centre that then
  leaves the band moves back into it. A version corrected by hand keeps its
  corrections, because the point attribute `fitted` holds the shape the last
  fitting gave, and the difference to it is applied again.
- Every vertex gets the weights of the nearest skin of the body part its cage
  face lies on, the cage's region and side, interpolated from the corners of the
  skin's triangle, limited to four bones, and not smoothed, so it moves like the
  skin it covers: a sleeve's inside at the armpit moves with the arm, not with
  the trunk next to it. The point attribute `transferred_weights` holds a
  checksum of the weights the last transfer gave each vertex of the first
  version, so a vertex whose weights were painted by hand keeps them, also in
  the other versions. `-Weights` transfers every weight again.
- A file that this leaves as it was is not saved, so its approvals stay valid.

The sync then writes the review sheet `artifacts/art-previews/<Name>.png`. Per
body it shows three rows: the body in the template from four sides with its
torso from the front and the back, the template showing a grid in its UVs, so
the sheet shows how they stretch; the template in the test poses of `poses.py`;
and the body without it, its covered faces orange, the skin kept visible at the
template's edges yellow, the pulled-in vertices blue, and the skin that comes
through it in motion red. The review file `artifacts/art-previews/<Name>_poses.blend`
places the bodies side by side with an action that steps through the test
poses. Last, the sync checks every rule and reports what the template covers.
The `model` stage checks that the bodies and the cage are the current ones of
their files and the cage the approved one, that every body has a version with
the first version's topology and one material, within the triangle budget and
with every vertex and every face centre in its band from the cage, and that the
fitting and the weights are as a sync leaves them. The `rig` stage adds the rig
rules of skinned assets for each version on its armature, the motion check, and
the pair check: every exported template of another visual slot and a lower
layer of the cage, such as the trousers under a shirt, is put on the bodies from
its own file, and of its faces that stay drawn, those whose body face this
template leaves visible at its openings, every vertex that this template is over
at rest is followed through the test poses and the clips. The report names how
many come out through this template and the deepest place, as the motion check
does. A body wears one garment per visual slot, so templates of the same slot,
such as a shirt and a jerkin, are not checked against each other.
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
  borders of its mesh and the lines where its texture is cut, sampled every
  5 mm where the garment is opaque.
- **The underlay**, the body face under each garment face, along its inward
  normal, or the nearest one.
- **The pull-in**: a visible body vertex closer to the garment than the band's
  smallest distance, and at least 3 mm, moves inward by the difference, at most
  3 mm.
- **Motion**: every visible vertex that the garment is over at rest keeps the
  point of the garment over it. The body, with the pull-in applied, takes every
  test pose and every frame of every clip of the skeleton's animation libraries.
  A vertex comes through the garment when it lies more than 1 mm outside the
  cloth at its point with the cloth between it and the inside; skin that passes
  a hem's edge has no cloth there and does not come through. Skinning blends the
  bones of a fold, such as an armpit, so the gap between skin and cloth closes
  there in motion whatever the weights. A vertex that comes through lies at an
  opening that shows it, so it is not hidden: the report names how many come
  through and the deepest place, the review sheet shows their faces red, and the
  template's shape or weights fix them. The rule needs the clips, so it runs at
  the template's `rig` stage, which its export runs.

`New-ArtAsset.ps1 -Kind garment` starts a template from its recipe on the cage,
with `-Slot`, `-Band`, `-Regions`, `-Cuts` as `<Line>:head` or `<Line>:tail`,
`-Ease`, and `-Hem`, or `-Modeled`; `-Cage` is `art/Cages/Humanoid` unless it
says otherwise.

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
checked in: sync, review, and export the template again. Its material has no
image, so the game shows a template only with a texture.

Garments of different layers check each other when a character wears them
together, which comes with the pair check of the first pair of templates that
overlap.

Cloth in motion is reviewed in Unity with `MMORPG > Tools > Art Pipeline >
Wardrobe`: pick a body, tick the garments it wears, and play `Idle`, `Walk`,
`Jog`, or `Death`. The body is put together as the game puts it together, walks
and jogs forward at the clips' speeds over a checkered ground that the camera
follows, so loose cloth lags as in the game, and the cloth solver runs. Drag in
the view to turn the camera and scroll to come closer.

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

In Unity, `Create Missing Materials` creates
`Assets/Art/Materials/Garments/M_Garment_<Name>.mat` with the garment shader, and
the appearance catalog of every body lists every exported texture of an exported
template that fits the body as a garment named after the texture: the
template's model for the body and its slot, layer, and hides, the texture's
material, which the template's model draws with instead of its own, and the
texture's masks, or else the template's. A character wears a garment by the
texture's name, such as `LeatherJerkin`, in F2 and in the Wardrobe, and one
garment per visual slot: trying one on takes off the other garment of its slot,
such as another chest piece, and of two of a slot that a look names, the first
in the catalog is worn.

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

## Art Direction For Models

The setting asks for stylized low to mid poly art with exaggerated proportions,
strong silhouettes, and clear colors, so actors stay readable from a zoomed-out
camera that turns freely ([Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md)).
For models this means:

- What identifies an actor, such as the ore of a vein, is visible from every
  side and large enough to read at the default camera distance.
- Flat-shaded facets and flat material colors, with a few materials per asset.
  Characters are the exception: smooth shading with the form painted into their
  textures, as on the human base characters.
- The model fills the actor's `interactionBounds` from the content catalog, so
  the click area matches what the player sees.
- A world prop stays within about 1,500 triangles. A playable character stays
  within 10,000 triangles with its eyeballs, teeth, and tongue.

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
  without visible seams, and with its weights. They are hidden in the viewport
  and never edited by hand; the approval and the export refuse parts that differ
  from the source body.
- Two eyeballs (`<Label>_Eye_L` and `_R`) for gaze animation, and upper and lower
  teeth and a tongue inside a closed mouth cavity for jaw animation, edited where
  they are.
- 9,202 triangles: 8,692 for the body and 510 for the eyeballs, teeth, and
  tongue. Both bodies are exactly mirror symmetric and have identical topology,
  so paint, armor, and clothes fitted to one body transfer to the other.
- Smooth shading, with hard edges only on the ear rims, and the form painted into
  the textures: `M_Body_<Label>` uses the 2048 px body texture
  `T_Body_<Label>` with the underwear, `M_Face_<Label>` a 1024 px face texture,
  `M_Eye_<Label>` a 512 px iris texture, and `M_Teeth_<Label>` and
  `M_Tongue_<Label>` flat colors. The face and eye variants for character
  creation are packed into the file and exported as
  [variants](#appearance-layers): `T_Face_Male_Stubble`, `T_Face_Male_Scarred`,
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
   triangles per character, matching cuts and normals between the parts, and a
   report of the color difference across the UV seams.
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
| `art/Animations/Human_Actions` | `animations` | `Humanoid` | Every clip of the human bodies, keyed on the rig of `Human_Male` and shown on both bodies. `Idle`, 4 s, `Walk`, 28 frames at 1.4 m/s, and `Jog`, 19 frames at 5.0 m/s, looping, from the gait model. `Death`, 2 s, made by hand, not looping: a hit snaps the head back, the right knee buckles first, the body tips over to its right side and backward, lands on its back with the legs swinging up, the head hits a moment later, the legs fall with the left knee outward, and the body lies still on its back with the arms out and the head turned aside. Approved 2026-10-22. |
| `art/GarmentTextures/LeatherJerkin` | `texture` | none | The leather jerkin on `Shirt`, 1024 px: warm brown leather with blotches and grain, darker trim with stitches along the neckline, the hem, and the sleeve ends, and an open V at the front, laced across, where the skin shows between the laces. It has masks of its own. Roughness 0.62. Moved from the first leather jerkin garment and then from the template `Jerkin`, which became `Shirt`, by projection. |
| `art/GarmentTextures/LinenLongShirt` | `texture` | none | Plain oatmeal linen on `LongShirt`, 1024 px, with a fine weave, soft wear, stitched hems at the neckline, the hem, and the cuffs, and stitched seams. Roughness 0.82. |
| `art/GarmentTextures/LinenShirt` | `texture` | none | The same linen on `Shirt`. |
| `art/GarmentTextures/NetShirt` | `texture` | none | A coarse hemp-colored net on `Shirt`, 1024 px: round cords of 2.4 mm in diamond meshes 25 mm apart around the trunk and each arm, transparent between them, with solid hems 14 mm wide at the neckline, the hem, and the sleeve ends. It has masks of its own, so the skin and its paint show through the meshes. Roughness 0.88. |
| `art/GarmentTextures/WoolTrousers` | `texture` | none | Brown wool on `Trousers`, 1024 px, with a coarse weave, a darker waistband, stitched hems at the ankles, and stitched seams, among them a yoke across the hips. Roughness 0.9. |
| `art/Garments/LongShirt` | `garment` | none | `Shirt` with long sleeves, cut at `SleeveLong`, 2,176 triangles per body. |
| `art/Garments/Shirt` | `garment` | none | A short-sleeved top in the `tight` band, layer 150, from the recipe `trunk` and `arms`, cut at `HemHip` and `SleeveShort`, over `tight` trousers below `Waist`, turned edges 2 mm deep, without ease or collar, 1,536 triangles per body, on `art/Cages/Humanoid`. |
| `art/Garments/Trousers` | `garment` | none | Trousers in the `tight` band, layer 100, from the recipe `trunk` and `legs`, cut at `Waist` and `LegAnkle`, turned edges 2 mm deep, 1,496 triangles per body, with seams along the inside of the legs. |
| `art/Cages/Humanoid` | `cage` | none | The garment cage of the `Humanoid` skeleton, made from the male body on 2026-10-23 and fitted to the male and the female, with a figure scale of 60 mm and the twelve standard lines of [Garment Cage](#garment-cage). |
| `art/Paint/Tattoo` | `paint` | none | A tattoo over `M_Body`, painted on the male and moved to the female, layer 10: a Nordic armband of two thin rings with interlocking chevrons and dots around each upper arm, and a round rune emblem of eight spokes, rings, and ticks on the upper breastbone, in dark blue-black, slightly faded ink. |
| `art/Characters/Human_Female` | `skinned` | `Female` | The female human base body, 1.70 m, with its source body, ten body parts, eyeballs, teeth, and tongue, 9,202 triangles, and the `Humanoid` rig. Model and rig approved 2026-10-22. |
| `art/Characters/Human_Male` | `skinned` | `Male` | The male human base body, 1.79 m, with the same topology as the female, its source body, 9,202 triangles, and the `Humanoid` rig. Model and rig approved 2026-10-22. The game shows it for every player. |
| `art/WorldActors/Resource_IronVein` | `static` | `Available`, `Depleted` | Three faceted rocks with chunky rust-orange iron ore in two crossing veins, 684 triangles; the depleted state keeps the rocks with broken flat tops and loose rubble, without ore, 194 triangles. The available state is about 1.5 m wide, 1.25 m deep, and 1.0 m high, inside the vein's 1.6 by 1.2 by 1.6 m interaction bounds; the depleted rubble spreads to about 1.9 by 1.7 m. Approved 2026-10-20. |

## Planned

- The body type at character creation, which shows the female body for female
  characters with the clips of the library, and the look chosen there, saved
  with the character and sent to every client.
- Eyelid bones for blinking and forearm twist bones.
- A model fix of the low armpits, where the triceps meet the torso, and of the
  female's bra straps, which are painted up her neck, above the collar of chest
  paint.
- Attachments: hair, beards, and the modeled parts of armor as meshes skinned to
  the shared skeleton, combined with the body's parts; the first is the leather
  jerkin's collar and shoulder flaps, together with the jerkin as an item that
  shows its paint when equipped.
- A shared palette texture for stylized models, level-of-detail meshes, and
  per-asset collision proxies if presentation ever needs them.

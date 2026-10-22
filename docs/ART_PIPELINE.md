# Art Pipeline

Last updated: 2026-10-22

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
| Make clothes or armor as a 3D garment | `New-ArtAsset.ps1 -Kind garment`, then `Sync-GarmentAsset.ps1` | [Make A Garment](LOCAL_DEVELOPMENT.md#make-a-garment) |
| Review, approve, and export any of them | `Render-ArtPreview.ps1`, `Approve-ArtAsset.ps1`, `Export-ArtAsset.ps1` | [Review, Approve, And Export An Asset](LOCAL_DEVELOPMENT.md#review-approve-and-export-an-asset) |

Modeled clothes, armor, and hair come next, as [attachments](#appearance-layers).

## Layout

| Path | Responsibility |
| --- | --- |
| `art/<Category>/<Name>/<Name>.blend` | Source of truth for one asset, stored in Git LFS. The folder name is the asset name. |
| `art/<Category>/<Name>/asset.json` | Asset manifest: kind, export target, root objects, triangle budget, skeleton and clips of a skinned asset, approvals, and the export record. |
| `art/Animations/<Name>/<Name>.blend` | An [animation library](#animation-libraries): handmade clips of one skeleton, keyed on the rig of one character, with the bodies that show them. |
| `art/Paint/<Name>/<Name>.blend` | A [paint layer](#paint-layers): transparent paint over the skin of bodies, painted on the first body and moved to the others. |
| `art/Garments/<Name>/<Name>.blend` | A [garment](#garments): clothes or armor made on the first body and fitted to the others, one version per body. |
| `art/Skeletons/<Name>.json` | A shared [skeleton definition](#skeletons): the bones every skinned asset of that skeleton has, their parents, their Unity Humanoid bones, and whether they deform the skin. |
| `src/Client/Assets/Art/Models/<Category>/<Name>.fbx` | Exported model. It is never edited by hand; CI rejects a model without an asset or with a changed file. A garment exports one model per body, `Models/Garments/<Name>_<Label>.fbx`, with its data `<Name>_<Label>.json` next to it. |
| `src/Client/Assets/Art/Textures/<Category>/<Image>.png` | Exported image of a painted material, byte for byte as it is packed in the `.blend` file. Never edited by hand; CI rejects a texture without an export record or with a changed file. Paint layers export to `Textures/Paint` and garments to `Textures/Garments`. |
| `src/Client/Assets/Art/Shaders` | `CharacterSkin.shader` and `CharacterSkinInput.hlsl`, the skin shader `Mmorpg/Character Skin`: URP Lit with paint layers. |
| `src/Client/Assets/Art/Materials` | Unity URP Lit materials. A Blender material maps to the Unity material of the same name. |
| `src/Client/Assets/Art/Animation` | Animator controllers that play the exported clips, such as `HumanLocomotion.controller`. |
| `tools/blender/art_pipeline` | Python that runs inside Blender: validation, FBX export, the review sheet, in `bodies.py` the body parts of a skinned character written from its editable body, and in `poses.py` the test poses of the `Humanoid` skeleton that the rig reviews use. |
| `tools/blender/art_pipeline_cli.py` | Blender entry point for the scripts below. |
| `tools/New-ArtAsset.ps1` | Creates a new static model, animation library, paint layer, or garment: its folder, `asset.json`, and first `.blend` file. |
| `tools/Render-ArtPreview.ps1`, `tools/Approve-ArtAsset.ps1`, `tools/Export-ArtAsset.ps1` | Review, approval, and export commands. They share `tools/ArtPipelineCommon.ps1`. |
| `tools/Verify-ArtAssets.ps1` | CI check of every asset without Blender. |
| `tools/Sync-AnimationLibrary.ps1`, `tools/blender/art_pipeline/library.py` | Builds an animation library file from its rig, or updates it when the rig changed, and keeps its clips. |
| `tools/Sync-PaintAsset.ps1`, `tools/blender/art_pipeline/paint.py` | Builds a paint layer file from its bodies, or updates it, and moves the paint of the first body to the others. |
| `tools/Sync-GarmentAsset.ps1`, `tools/blender/art_pipeline/garment.py`, `coverage.py` | Fits a garment to every body from the version on the first body, gives every version the weights of its body, computes what it covers on every body, writes its review sheet and review file, and checks it. |
| `tools/Sync-HumanBase.ps1`, `tools/blender/human_base`, `tools/blender/human_base_cli.py` | The tools of the [human base characters](#human-base-characters): their body parts written from their editable bodies, their rig and weights, their checks and review sheets, and the gait model that starts clips in an animation library. |
| `src/Client/Assets/Editor/ArtModelImportRules.cs` | Unity import rules for every model below `Assets/Art/Models` and every texture below `Assets/Art/Textures`. |
| `src/Client/Assets/Editor/ArtAssetManifests.cs` | Reads `asset.json` and the skeleton definitions for the import rules, the material command, and the tests. |
| `src/Client/Assets/Editor/ArtMaterialCreator.cs` | `MMORPG > Tools > Art Pipeline > Create Missing Materials`: creates the project material of every exported material that has none, from its export record. |
| `src/Client/Assets/Editor/ArtExportFollowUp.cs` | After an import of exported art, creates the missing project materials and updates the appearance catalogs, once, outside batch mode. |
| `.mcp.json` | The Blender MCP server for live work in Blender from Claude Code. |

## Asset Manifest

`asset.json` sits next to the `.blend` file. The scripts write it; only `kind`,
`target`, `rootObjects`, `triangleBudget`, and for a skinned asset `skeleton` and
any `clips` of its own are written by hand when an asset is created.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Always `1`. |
| `kind` | `static` for models without a skeleton, `skinned` for characters and creatures that get a rig, `animations` for an [animation library](#animation-libraries), `paint` for a [paint layer](#paint-layers), `garment` for a [garment](#garments). A paint layer has no `target`, `rootObjects`, or `triangleBudget`, and a garment no `target` or `rootObjects`: its models and roots follow from its bodies. |
| `target` | `src/Client/Assets/Art/Models/<Category>/<Name>.fbx`, where `<Name>` is the folder name. |
| `rootObjects` | The exact names of the top-level objects in the `Export` collection. They become the direct children of the Unity model, so they carry the contract with the game code, such as `Available` and `Depleted` for a resource node. The one root object of an animation library is its armature, named after its skeleton. |
| `triangleBudget` | The maximum number of exported triangles, after modifiers. An animation library exports no meshes and has none. A garment's budget holds for each body's version. |
| `skeleton` | Skinned assets and animation libraries: the name of the skeleton definition in `art/Skeletons`, such as `Humanoid`. |
| `rig` | Animation libraries only: the skinned asset whose rig the clips are keyed on, such as `art/Characters/Human_Male`. |
| `previews` | Animation libraries only: other skinned assets of the skeleton whose bodies show the clips in the library, such as `["art/Characters/Human_Female"]`. |
| `clips` | Animation libraries, and skinned assets with clips of their own: the names of the animation clips the asset exports, such as `["Death", "Idle", "Walk", "Jog"]`. Each is one Blender action. A skinned asset whose clips come from animation libraries, such as the human bodies, leaves `clips` out. |
| `part` | Paint layers only: the part of the painted material `M_<Part>_<Label>`, such as `Body`. |
| `bodies` | Paint layers and garments: the skinned assets the paint or the garment fits, the body it is made on first, such as `["art/Characters/Human_Male", "art/Characters/Human_Female"]`. They must have the same topology. |
| `layer` | Paint layers: the order paint layers are drawn in, from 0 to 1000, the lowest first, such as `30` for a chest piece over a shirt at `10`. Garments: the order of a character's garments from the inside out, from 0 to 1000, such as `200` for a jerkin over a shirt at `100`. |
| `slot` | Garments only: the visual slot, `head`, `body`, `legs`, `hands`, `feet`, `back`, or `hair`. |
| `band` | Garments only: how far from the skin the garment lies, `tight` (0 to 8 mm), `mid` (5 to 25 mm), `outer` (10 to 60 mm), or `loose` (from 10 mm). |
| `hides` | Garments only: the other visual slots it hides, such as `["legs"]` for a floor-length robe, or `[]`. |
| `hair` | Garments only: for headwear, what happens to the hair under it, `hide`, `compress`, or `show`; `null` for every other slot. |
| `cloth` | Garments only: the cloth profile of its loose parts, `robe`, `cloak`, or `hood`, or `null`. |
| `dyes` | Garments only: the number of dye channels. It is `0` until the garment shader brings dyes. |
| `variants` | Skinned assets only, optional: the [appearance variants](#appearance-layers), each a painted material `M_<Part>_<Label>` with the other `images` `T_<Part>_<Label>_<Option>` it may show in the game, such as `{ "material": "M_Face_Male", "images": ["T_Face_Male_Scarred"] }`. The material's own image is named `T_<Part>_<Label>_<Option>` as well. |
| `approvals` | One entry per approved review stage: the `date` and the `sourceSha256` of the `.blend` file that was approved. |
| `export` | The `sourceSha256` of the exported `.blend` file, the `targetSha256` of the written FBX, and `materials`: the approved look of every exported material as URP Lit values, `baseColor` (sRGB `#RRGGBB`), `metallic`, and `smoothness` (1 - Roughness). A painted material also names its image in `baseMap`, and its `baseColor` is white. `textures` lists every exported image, those the materials name and the variants, with its `name`, `target` PNG, and `sha256`, present only when there are textures, and `clips` lists every clip of a skinned asset with its `name`, length in `frames`, and whether it should `loop`. The export of an animation library has no materials or textures; it records its `clips` and the `rigSourceSha256`, the `export.sourceSha256` of its rig when it was exported. The export of a paint layer records its `textures`, one `T_Paint_<Name>_<Label>` per body below `src/Client/Assets/Art/Textures/Paint`, and the `smoothness` and `metallic` of the paint, without a `targetSha256`. The export of a garment records its `models`, one per body in the order of `bodies` with its `body` label, the `bodySourceSha256` of the body's export it was fitted to, the model's `target` and `sha256`, and its `data` file and `dataSha256`; its `clipSources`, every animation library of its skeleton with the `sourceSha256` of the export whose clips completed its masks; its one material `M_Garment_<Name>`; and its image below `src/Client/Assets/Art/Textures/Garments`. `null` before the first export. |

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
| `garment` (garments) | `model`, then `rig` | Implemented. |

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
of its rig, when an asset in review has an export or an FBX, when the
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
layer that fits it, as `Assets/Art/Appearance/<Model>_Appearance.asset`, when it
is missing or differs; see [Appearance Layers](#appearance-layers).
`ArtAppearanceCatalogTests` fails when a catalog differs from what the command
writes from `asset.json`.

A project material is never changed by the import. When an export adds a
material, `MMORPG > Tools > Art Pipeline > Create Missing Materials` creates it
as a URP Lit material from its export record, with its texture for a painted
material, below `Assets/Art/Materials/<Category>`, and reimports the models that
use it. A garment's material cuts its texture at alpha 0.5 and draws both faces,
so holes, lace, and torn edges show sharp and the inside of a sleeve is drawn.

`ArtExportFollowUp` runs both commands once after Unity imports exported models
or textures, when the Editor next updates, so an export reaches the game without
a menu command. It does nothing in batch mode, so the CI tests check the
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
is missing, belongs to another garment or body, or holds a face or vertex out of
range, an unsorted list, or a pull-in beyond 3 mm. A
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
| Paint layers | Transparent paint over the skin in the body texture's UV layout, such as the paint of a leather jerkin or a scalp, in a [paint layer](#paint-layers) asset of its own. Painted once on the male body and moved to every body of the same topology. | The skin shader lays it over the skin when the body is drawn. | Implemented. |
| Garments | Meshes skinned to the shared skeleton, such as clothes, armor, and hair, in [garment](#garments) assets of their own, made on the male body and fitted to every body type. | Combined with the visible body parts into the body's one mesh; see [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md). | The asset kind is implemented; putting garments on in the game comes with delivery step 4 of the design. |

The faces and eyes of the human base characters are variants: images packed into
the character's file, shown by the model review, and exported with the model
through `variants` in `asset.json`. In Unity, the body's `BodyAppearanceCatalog` lists one slot per
material with variants, such as `Face` and `Eye`, with its options, the
material's own image first, and a look picks one option per slot; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).

## Paint Layers

A paint layer below `art/Paint` is paint over the skin of the bodies, such as the
painted leather jerkin `art/Paint/LeatherJerkin`. Its file holds, per body, the
mesh `Paint_<Label>`, the body's parts joined in its rest pose, and the image
`T_Paint_<Name>_<Label>` in the layout of the body texture `T_<Part>_<Label>`,
transparent where the skin shows. A preview material lays the image over the
skin by its alpha, with its roughness, so Blender's Material Preview shows the
look. The paint is made on the first body, by hand in Texture Paint mode or by a
script, and its image carries the custom properties `roughness` and `metallic`.

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

## Garments

A garment below `art/Garments` is clothes, armor, or hair as a mesh of its own,
skinned to the shared skeleton, such as the leather jerkin
`art/Garments/LeatherJerkin`. It is made once, on the first body of `bodies`, and
fitted to every other body of the same topology; the design is in
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md).

Its file holds, per body, the collection `Export_<Label>` with the body's
armature `<Label>` and the garment's version `<Name>_<Label>`, a child of it with
an Armature modifier and the material `M_Garment_<Name>`. The bodies' source
bodies, eyes, teeth, and tongues are in the `Reference` collection, unselectable,
so they show the body and the fitting reads them. The first body's version is
the one a person shapes; the file opens with the other bodies hidden, because
they stand at the same place.

`tools/Sync-GarmentAsset.ps1` brings the file up to date:

- A body whose rig, surface, or weights changed in its own file is appended
  again, and the garment stays on it.
- Every other version is fitted from the first: each vertex is bound to the
  nearest triangle of the first body, by its barycentric coordinates and its
  offset in the triangle's frame, and placed at the same triangle and offset of
  the other body. A vertex or a face centre that then leaves the garment's band,
  because the other body curves differently, moves back into it along the skin's
  normal; this is part of the fitting. A version corrected by hand keeps its
  corrections, because
  the point attribute `fitted` holds the shape the last fitting gave, and the
  difference to it is applied again to the new fitting.
- Every vertex gets the weights of the body under it, interpolated from the
  corners of its triangle and limited to four bones, and not smoothed, so it
  moves like the skin it covers: smoothed weights let the skin under a sleeve
  come through further. The point attribute
  `transferred_weights` holds a checksum of the weights the last transfer gave
  each vertex of the first version, so a vertex whose weights were painted by
  hand keeps them, also in the other versions. `-Weights` transfers every
  weight again. Weights are painted by hand only on the first version.
- A file that this leaves as it was is not saved, so its approvals stay valid.

The sync then writes the review sheet `artifacts/art-previews/<Name>.png`. Per
body it shows three rows: the body in the garment from four sides with its torso
from the front and the back, the garment in the test poses of `poses.py`, and
the body without the garment, its covered faces orange, the faces hidden for
motion red, and the pulled-in vertices blue. The review file
`artifacts/art-previews/<Name>_poses.blend` places the bodies side by side with an
action that steps through the test poses. Last, the sync checks every rule and
reports what the garment covers. The `model` stage checks that every body has a
version with the first version's topology and one material, within the triangle
budget and with every vertex and every face centre in its band from the skin, so
a large flat face over a round shoulder cannot dip under the skin between its
corners, and that the bodies, the
fitting, and the weights are as a sync leaves them. The `rig` stage adds the rig
rules of skinned assets for each version on its armature.

`coverage.py` computes what a garment covers on each body, in the faces and
vertices of the body's editable mesh, which every body of the topology numbers
alike:

- **The cover mask**, the body faces the garment hides. From five points on a
  face, seven rays go out in a cone of 25 degrees around its normal. The face is
  covered when every ray meets the garment where its texture is opaque, or the
  body, within the band's reach (30, 50, 100, or 300 mm), and at least one meets
  the garment. A hole, an open hem, or a transparent part of the texture lets a
  ray out and leaves the face visible; a face that only the body hides, such as
  the inside of the mouth, stays visible too.
- **The underlay**, the body face under each garment face, along its inward
  normal, or the nearest one.
- **The pull-in**: a visible body vertex closer to the garment than the band's
  smallest distance, and at least 3 mm, moves inward by the difference, at most
  3 mm.
- **Motion**: every visible vertex that the garment is over at rest keeps the
  point of the garment over it. The body, with the pull-in applied, takes every
  test pose and every frame of every clip of the skeleton's animation libraries,
  and every face of a vertex that comes more than 1 mm out of its point is hidden
  as well. Skinning blends the bones of a fold, such as an armpit, so the gap
  between skin and cloth closes there in motion whatever the weights; the
  hidden faces lie under the garment, and only a look into an opening sees past
  them, at the garment's inside. The rule needs the clips, so a garment's masks
  are complete only at its `rig` stage, which its export runs.

`New-ArtAsset.ps1 -Kind garment` starts a garment with a shell on the first body,
so there is something to shape: the body faces of the slot's body parts, or
with `-FromPaint` the faces a paint layer covers, with the paint image as the
garment's texture in the body's UV layout. The shell lies in the garment's band,
4 mm from the skin for `tight`, 10 mm for `mid`, and 20 mm for `outer` and
`loose`, smoothed so the body's muscles do not show through, and keeps between
2 and 7 mm, 7.5 and 22 mm, or 15 and 50 mm, and at least 15 mm for `loose`, at
its vertices and at the centres of its faces.

The export writes one model per body, `Models/Garments/<Name>_<Label>.fbx`, from
its `Export_<Label>` collection, the garment skinned to that body's armature, its
data `Models/Garments/<Name>_<Label>.json`, and its image to `Textures/Garments`.
The data holds `bodyFaces`, `bodyVertices`, and `garmentFaces`, the `covered`
body faces, the `underlay` of every garment face, and the `pullInVertices` with
their `pullInDistances` in meters. A garment is exported after its bodies and the
animation libraries of its skeleton and records their exports, so
`Verify-ArtAssets.ps1` fails when a body or a library has a newer export than
the one the garment was fitted to and checked in: sync, review, and export the
garment again.

Garments of different layers check each other when a character wears them
together, which comes with putting characters together in Unity.

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
| `art/Paint/LeatherJerkin` | `paint` | none | The paint of the leather jerkin over `M_Body`, painted on the male and moved to the female, layer 30: warm brown leather with large soft blotches, fine grain, and worn lighter high points, darker trim with stitches along every edge, side and shoulder seams, short sleeves to a little less than half the upper arm, a round collar that stands 2 cm up the neck, and a V at the front laced across. Smoothness 0.38. Approved 2026-10-21; the female's bra straps are painted up her neck, above any collar, until the base body is fixed. |
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

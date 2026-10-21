# Art Pipeline

Last updated: 2026-10-21

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

Scripted assets, such as the [human base characters](#human-base-characters),
are rebuilt from their scripts, and a rebuild replaces their `.blend` files.
Everything made by hand lives in assets of its own that no build writes, such as
the handmade clips of an [animation library](#animation-libraries), so a rebuild
never overwrites handmade work.

## Layout

| Path | Responsibility |
| --- | --- |
| `art/<Category>/<Name>/<Name>.blend` | Source of truth for one asset, stored in Git LFS. The folder name is the asset name. |
| `art/<Category>/<Name>/asset.json` | Asset manifest: kind, export target, root objects, triangle budget, skeleton and clips of a skinned asset, approvals, and the export record. |
| `art/Animations/<Name>/<Name>.blend` | An [animation library](#animation-libraries): handmade clips of one skeleton, keyed on the rig of one character, with the bodies that show them. |
| `art/Skeletons/<Name>.json` | A shared [skeleton definition](#skeletons): the bones every skinned asset of that skeleton has, their parents, their Unity Humanoid bones, and whether they deform the skin. |
| `src/Client/Assets/Art/Models/<Category>/<Name>.fbx` | Exported model. It is never edited by hand; CI rejects a model without an asset or with a changed file. |
| `src/Client/Assets/Art/Textures/<Category>/<Image>.png` | Exported image of a painted material, byte for byte as it is packed in the `.blend` file. Never edited by hand; CI rejects a texture without an export record or with a changed file. |
| `src/Client/Assets/Art/Materials` | Unity URP Lit materials. A Blender material maps to the Unity material of the same name. |
| `src/Client/Assets/Art/Animation` | Animator controllers that play the exported clips, such as `HumanLocomotion.controller`. |
| `tools/blender/art_pipeline` | Python that runs inside Blender: validation, FBX export, and the review sheet. |
| `tools/blender/art_pipeline_cli.py` | Blender entry point for the scripts below. |
| `tools/Render-ArtPreview.ps1`, `tools/Approve-ArtAsset.ps1`, `tools/Export-ArtAsset.ps1` | Review, approval, and export commands. They share `tools/ArtPipelineCommon.ps1`. |
| `tools/Verify-ArtAssets.ps1` | CI check of every asset without Blender. |
| `tools/Sync-AnimationLibrary.ps1`, `tools/blender/art_pipeline/library.py` | Builds an animation library file from its rig, or updates it when the rig changed, and keeps its clips. |
| `tools/Build-HumanBase.ps1`, `tools/blender/human_base`, `tools/blender/human_base_cli.py` | The scripted build of the [human base characters](#human-base-characters) from the Human Base Meshes bundle. |
| `src/Client/Assets/Editor/ArtModelImportRules.cs` | Unity import rules for every model below `Assets/Art/Models` and every texture below `Assets/Art/Textures`. |
| `src/Client/Assets/Editor/ArtAssetManifests.cs` | Reads `asset.json` and the skeleton definitions for the import rules, the material command, and the tests. |
| `src/Client/Assets/Editor/ArtMaterialCreator.cs` | `MMORPG > Tools > Art Pipeline > Create Missing Materials`: creates the project material of every exported material that has none, from its export record. |
| `.mcp.json` | The Blender MCP server for live work in Blender from Claude Code. |

## Asset Manifest

`asset.json` sits next to the `.blend` file. The scripts write it; only `kind`,
`target`, `rootObjects`, `triangleBudget`, and for a skinned asset `skeleton` and
`clips` are written by hand when an asset is created.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Always `1`. |
| `kind` | `static` for models without a skeleton, `skinned` for characters and creatures that get a rig, `animations` for an [animation library](#animation-libraries). |
| `target` | `src/Client/Assets/Art/Models/<Category>/<Name>.fbx`, where `<Name>` is the folder name. |
| `rootObjects` | The exact names of the top-level objects in the `Export` collection. They become the direct children of the Unity model, so they carry the contract with the game code, such as `Available` and `Depleted` for a resource node. The one root object of an animation library is its armature, named after its skeleton. |
| `triangleBudget` | The maximum number of exported triangles, after modifiers. An animation library exports no meshes and has none. |
| `skeleton` | Skinned assets and animation libraries: the name of the skeleton definition in `art/Skeletons`, such as `Humanoid`. |
| `rig` | Animation libraries only: the skinned asset whose rig the clips are keyed on, such as `art/Characters/Human_Male`. |
| `previews` | Animation libraries only: other skinned assets of the skeleton whose bodies show the clips in the library, such as `["art/Characters/Human_Female"]`. |
| `clips` | Skinned assets and animation libraries: the names of the animation clips the asset exports, such as `["Idle", "Walk", "Jog"]`. Each is one Blender action. |
| `approvals` | One entry per approved review stage: the `date` and the `sourceSha256` of the `.blend` file that was approved. |
| `export` | The `sourceSha256` of the exported `.blend` file, the `targetSha256` of the written FBX, and `materials`: the approved look of every exported material as URP Lit values, `baseColor` (sRGB `#RRGGBB`), `metallic`, and `smoothness` (1 - Roughness). A painted material also names its image in `baseMap`, and its `baseColor` is white. `textures` lists every exported image with its `name`, `target` PNG, and `sha256`, present only when there are textures, and `clips` lists every clip of a skinned asset with its `name`, length in `frames`, and whether it should `loop`. The export of an animation library has no materials or textures; it records its `clips` and the `rigSourceSha256`, the `export.sourceSha256` of its rig when it was exported. `null` before the first export. |

## Review Stages

An asset passes its review stages in order. Each stage ends with a reviewer
looking at the asset, on the review sheet or in Blender, and approving it with
`Approve-ArtAsset.ps1`. The approval stores the SHA-256 of the `.blend` file, so
saving the file again, even without a visible change, needs a new approval.

| Kind | Stages | Status |
| --- | --- | --- |
| `static` | `model` | Implemented. |
| `skinned` (characters, creatures) | `model`, then `rig`, then `animation` | Implemented. |
| `animations` (animation libraries) | `animation` | Implemented. |

The `rig` review looks at the skeleton and the weights: test poses that raise
the arms, bend the elbows and knees, stride, sit, close the hands, open the jaw,
and turn the eyes, with close-ups of the shoulders, hips, knees, and hands and a
weight view. The `animation` review looks at every clip in motion, on a floor
that moves at the clip's speed, so a sliding foot shows at once.

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
one root), the Unity `human` bone it maps to (a `HumanBodyBones` name), and
whether it should `deform` the skin.

`Humanoid` is the skeleton of the playable bodies: 55 deforming bones, from
`Hips` through `Spine`, `Chest`, `UpperChest`, `Neck`, and `Head` with `Jaw` and
the eyes `Eye_L` and `Eye_R`, a `Shoulder`, `UpperArm`, `LowerArm`, and `Hand`
with three bones per finger and thumb on each side, and an `UpperLeg`,
`LowerLeg`, `Foot`, and `Toes` on each side, all mapped to the Unity Humanoid
avatar. Three sockets deform nothing and carry equipment: `Socket_MainHand` on
the right hand, `Socket_OffHand` on the left, and `Socket_Back` on the upper
chest. In Unity a socket's local Z is the forward direction of the item it holds
and its local Y is up, which are Blender's bone Y and Z axes.

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
is removed. An animation library exports its armature and its clips the same
way, without meshes.

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

Every PNG below `Assets/Art/Textures` imports as an sRGB texture with mipmaps,
without alpha, not readable, clamped, trilinear, at most 2048 px, with high
quality compression.

A project material is never changed by the import. When an export adds a
material, `MMORPG > Tools > Art Pipeline > Create Missing Materials` creates it
as a URP Lit material from its export record, with its texture for a painted
material, below `Assets/Art/Materials/<Category>`, and reimports the models that
use it. When a Blender material changes, set the Unity material from its new
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
a clip is missing, has another length, or loops differently than recorded. A
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

The clips of the human base characters are made by script, so a rebuild makes
them again. A clip made by hand, such as `Death`, lives in an animation library
instead: an art asset of kind `animations` below `art/Animations` that no build
writes. `art/Animations/Human_Actions` is the library of the human bodies:

- The `Export` collection holds the armature `Humanoid`, a copy of the rig of
  `Human_Male` named after its skeleton. Every handmade clip is an action keyed
  on it, at 30 frames per second, like the scripted clips.
- The `Reference` collection, which is never exported, holds what shows the
  clips: the male body bound to `Humanoid`, so it moves with every key, the
  female body 2 m to its right on an armature of her own whose bones copy the
  pose of `Humanoid` in local space, with the hips' movement scaled to her hip
  height as Unity retargets a Humanoid clip, and a checkered ground of
  quarter-meter squares, so a body that slides or sinks shows at once.

`tools/Sync-AnimationLibrary.ps1` builds the library file from the manifest's
`rig` and `previews`. When the rig changes, such as after a rebuild of the
characters with another rig, it replaces the armature and the bodies and keeps
every action, so the clips play on the new rig and are reviewed and approved
again. A library whose armature is already the current rig is left as it is.
The approval and the export refuse a library whose armature is not the current
rig, and CI refuses one that was exported against an older export of its rig;
exporting it again is enough when the rig itself did not change.

A clip is made by hand in the library file, in the reviewer's Blender, through
the [Blender MCP server](#live-work-with-the-blender-mcp-server) or the
reviewer's own keys: key poses on `Humanoid`, smoothed by Blender's curves, with
the custom property `loop` and a manual frame range from frame 0. Before the
approval, the file is saved with `Humanoid` in its rest pose and without an
active action. The library's review sheet shows every clip as eight frames on
both bodies, from the front right and from the back left.

In Unity, `Human_Actions.fbx` brings its clips as Humanoid motion, and
`HumanLocomotion.controller` plays `Death` on every player body; see
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#player-bodies).

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
of the playable humans, rigged with the `Humanoid` skeleton and animated with
`Idle`, `Walk`, and `Jog`. Their handmade clips, such as `Death`, come from the
[animation library](#animation-libraries) `art/Animations/Human_Actions`. Hair, brows, beards, clothes, and equipment are
modeled on top of them later, and faces are painted on them. A script builds both from the realistic bodies and the
Generic Topology head of the
[Human Base Meshes](https://www.blender.org/download/demo-files/) bundle v1.4.1
by Blender Studio (CC0), so they can be rebuilt and refined.

Each file holds one character in the `Export` collection:

- The armature (`Male` or `Female`) at the origin as the only root object, with
  the bones of the `Humanoid` skeleton. It carries the build's landmarks as
  custom properties, such as the eyes, lips, wrists, finger tips, and toes, for
  placing attachments.
- Ten body parts as children of the armature, named `<Label>_<Zone>`: `Head`,
  `Neck`, `Torso`, `Pelvis`, `UpperArms`, `Forearms`, `Hands`, `Thighs`,
  `Shins`, and `Feet`, so equipment can hide whole parts. The parts keep the
  whole body's corner normals as custom normals and meet without visible seams.
- Two eyeballs (`<Label>_Eye_L` and `_R`) for gaze animation, and upper and lower
  teeth and a tongue inside a closed mouth cavity for jaw animation.
- 9,202 triangles: 8,692 for the body and 510 for the eyeballs, teeth, and
  tongue. Both bodies are exactly mirror symmetric and have identical topology
  part by part, so armor and clothes fitted to one body transfer to the other.
- Smooth shading, with hard edges only on the ear rims, and the form painted into
  the textures: `M_Body_<Label>` uses the 2048 px body texture
  `T_Body_<Label>` with the underwear, `M_Face_<Label>` a 1024 px face texture,
  `M_Eye_<Label>` a 512 px iris texture, and `M_Teeth_<Label>` and
  `M_Tongue_<Label>` flat colors. The face and eye variants for character
  creation are packed into the file: `T_Face_Male_Stubble`,
  `T_Face_Male_Scarred`, `T_Eye_Male_Teal`, and `T_Eye_Male_Brown` for the male,
  `T_Face_Female_Blush`, `T_Face_Female_Freckles`, `T_Eye_Female_Teal`, and
  `T_Eye_Female_Hazel` for the female. Another face is the face or eye material
  with another image.

`tools/Build-HumanBase.ps1` runs the build, one Blender process per step:

1. `references`: the realistic bodies at their full multires level, centered and
   facing +Y, and the Generic Topology head in its realistic male and female
   shapes.
2. `geometry`: per body a designed coarse quad cage of the torso, arms, legs, and
   neck base, joined with hand and foot cages fitted to the digits traced on the
   reference, subdivided once and cast onto the reference from ray origins inside
   the body, with the armpits softened; the head opened at the eyes, the mouth,
   and the neck, with eye pockets and a mouth cavity; heroic proportions, about
   1.79 m with broader shoulders for the male and 1.70 m with a fuller bust for
   the female. Topology decisions are made on the first body and reused, so both
   bodies match.
3. `textures`: Cycles bakes the surface position, normals, the detail normals of
   the reference sculpt, and ambient occlusion per texel, and the textures are
   painted from the 3D surface, so colors match across UV seams. Sculpt detail
   fades where the build moved the surface off the sculpt on purpose, where the
   bake rays would miss it, and where a ray met the far wall of a narrow fold.
4. `check-mesh`, `check-seams`, and `check-parts`: no open or over-shared edges,
   tiny faces, slivers, or loose pieces on the bodies, exact mirror symmetry,
   identical topology, at most 10,000 triangles per character, matching cuts and
   normals between the parts, and a report of the color difference across the UV
   seams.
5. `characters` and `asset`: the split into parts and one model file per
   character.
6. `rig` and `check-rig`: the skeleton placed from the model, its weights, and
   the binding, then the rig rules, an unchanged rest pose, and exact mirror
   symmetry of the weights.
7. `animate` and `check-animation`: the clips, then the animation rules and the
   largest slip of a planted foot per clip, which the quality report lists.
8. `compare` and the reviews: the content comparison with the committed asset
   (objects with their modifiers, constraints, and visibility, the evaluated
   meshes with UVs, normals, and edge flags, vertex weights, the armature with
   its bones and pose, the actions with their curves, materials with their node
   links, textures with whether they are packed, and custom properties), and
   the review files and sheets of the model, the rig, and the animations, with
   both characters side by side.

`Build-HumanBase.ps1 -SkipModel` builds only steps 6 to 8 again, on the
committed models, without the bundle.

The rig places every joint from the model: the hips from the crotch and the leg
cut, the spine at fixed shares between the hips and the neck, the shoulder in
the deltoid cap found by ray casts, the elbows, knees, wrists, and ankles at the
cuts between the parts, the finger and toe joints from rings of the digit
meshes, and the jaw hinge from the lip line. Bone heat gives the first weights,
limited per body zone and per digit, and anatomical rules shape them where it
matters: the shoulder blends over the deltoid and leaves the chest in place; the
thigh takes the skin below a boundary that runs through the groin crease, over
the hip, along the gluteal fold, and through the crotch, with wide blends over
the buttock and the underwear line and a radial fade at the crotch, so a raised
thigh leaves no notch; and the knee and elbow blend across the joint. The jaw
takes the lower face, the eyes, the upper teeth, the lower teeth, and the tongue
are rigid on their bones, and the weights are mirrored so both sides match
exactly.

The clips are made by script from one gait model, with the strides matched to
the game: `Walk` at 1.4 m/s, `Jog` at 5.0 m/s, the movement speed of players,
and a 4 s breathing `Idle` with a slow weight shift and a look around. Each foot
follows a contact model with a heel strike, a roll over the ball, and a planned
swing, and the legs reach it with analytic two-bone IK while the hips sink where
a leg cannot reach. The pelvis turns, drops, and tilts with the steps, the lower
back takes up the tilt, the shoulders turn against the pelvis, and the arms,
forearms, hands, and fingers follow the swing a little later. A planted foot
slips at most 8 to 13 mm in the walk and about 1 mm in the jog.

The build is deterministic: the same bundle and scripts give the same geometry,
UVs, normals, and texture pixels. Blender's files still differ in their bytes
from run to run, so the build compares the content of each rebuilt character
with the committed file and replaces it only when something changed; a rebuild
without changes keeps the files and their approvals. No file of the build
records the folder it ran in. The build is pinned to the SHA-256 of
`human_base_meshes_bundle.blend` in v1.4.1,
`3c121505651140ceb4d69fd1d8923f7788ffadd81672f5be14845a5f2c75c137`. The bundle is
a download: keep it in a folder of its own outside the repository. The build
opens it as data only, with factory settings and its scripts disabled, and it is
never committed.

## Assets

| Asset | Kind | Roots | Notes |
| --- | --- | --- | --- |
| `art/Animations/Human_Actions` | `animations` | `Humanoid` | The handmade clips of the human bodies, keyed on the rig of `Human_Male` and shown on both bodies. `Death`, 2 s, not looping: a hit snaps the head back, the right knee buckles first, the body tips over to its right side and backward, lands on its back with the legs swinging up, the head hits a moment later, the legs fall with the left knee outward, and the body lies still on its back with the arms out and the head turned aside. Approved 2026-10-21. |
| `art/Characters/Human_Female` | `skinned` | `Female` | The female human base body, 1.70 m, with ten body parts, eyeballs, teeth, and tongue, 9,202 triangles, the `Humanoid` rig, and the clips `Idle`, `Walk`, and `Jog`, built by `tools/Build-HumanBase.ps1`. Model, rig, and animations approved 2026-10-21. |
| `art/Characters/Human_Male` | `skinned` | `Male` | The male human base body, 1.79 m, with the same topology as the female, 9,202 triangles, the `Humanoid` rig, and the clips `Idle`, `Walk`, and `Jog`, built by `tools/Build-HumanBase.ps1`. Model, rig, and animations approved 2026-10-21. The game shows it for every player. |
| `art/WorldActors/Resource_IronVein` | `static` | `Available`, `Depleted` | Three faceted rocks with chunky rust-orange iron ore in two crossing veins, 684 triangles; the depleted state keeps the rocks with broken flat tops and loose rubble, without ore, 194 triangles. The available state is about 1.5 m wide, 1.25 m deep, and 1.0 m high, inside the vein's 1.6 by 1.2 by 1.6 m interaction bounds; the depleted rubble spreads to about 1.9 by 1.7 m. Approved 2026-10-20. |

## Planned

- The body type at character creation, which shows the female body for female
  characters, through an `AnimatorOverrideController` with her own clips.
- Eyelid bones for blinking and forearm twist bones.
- A model fix of the low armpits, where the triceps meet the torso.
- A shared palette texture for stylized models, level-of-detail meshes, and
  per-asset collision proxies if presentation ever needs them.

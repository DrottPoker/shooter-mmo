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
src/Client/Assets/Art/Models/<Category>/<Name>.fbx
        |  ArtModelImportRules          fixed import settings, materials remapped by name
        v
Assets/Prefabs/... Prefab Variant of the model -> presentation registry -> game
```

Nothing reaches Unity without an approval of the exact source file that was
exported, and CI checks the whole chain without Blender.

## Layout

| Path | Responsibility |
| --- | --- |
| `art/<Category>/<Name>/<Name>.blend` | Source of truth for one asset, stored in Git LFS. The folder name is the asset name. |
| `art/<Category>/<Name>/asset.json` | Asset manifest: kind, export target, root objects, triangle budget, approvals, and the export record. |
| `src/Client/Assets/Art/Models/<Category>/<Name>.fbx` | Exported model. It is never edited by hand; CI rejects a model without an asset or with a changed file. |
| `src/Client/Assets/Art/Materials` | Unity URP Lit materials. A Blender material maps to the Unity material of the same name. |
| `tools/blender/art_pipeline` | Python that runs inside Blender: validation, FBX export, and the review sheet. |
| `tools/blender/art_pipeline_cli.py` | Blender entry point for the scripts below. |
| `tools/Render-ArtPreview.ps1`, `tools/Approve-ArtAsset.ps1`, `tools/Export-ArtAsset.ps1` | Review, approval, and export commands. They share `tools/ArtPipelineCommon.ps1`. |
| `tools/Verify-ArtAssets.ps1` | CI check of every asset without Blender. |
| `tools/Build-HumanBase.ps1`, `tools/blender/human_base`, `tools/blender/human_base_cli.py` | The scripted build of the [human base characters](#human-base-characters) from the Human Base Meshes bundle. |
| `src/Client/Assets/Editor/ArtModelImportRules.cs` | Unity import rules for every model below `Assets/Art/Models`. |
| `.mcp.json` | The Blender MCP server for live work in Blender from Claude Code. |

## Asset Manifest

`asset.json` sits next to the `.blend` file. The scripts write it; only `kind`,
`target`, `rootObjects`, and `triangleBudget` are written by hand when an asset
is created.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Always `1`. |
| `kind` | `static` for models without a skeleton, `skinned` for characters and creatures that get a rig. |
| `target` | `src/Client/Assets/Art/Models/<Category>/<Name>.fbx`, where `<Name>` is the folder name. |
| `rootObjects` | The exact names of the top-level objects in the `Export` collection. They become the direct children of the Unity model, so they carry the contract with the game code, such as `Available` and `Depleted` for a resource node. |
| `triangleBudget` | The maximum number of exported triangles, after modifiers. |
| `approvals` | One entry per approved review stage: the `date` and the `sourceSha256` of the `.blend` file that was approved. |
| `export` | The `sourceSha256` of the exported `.blend` file, the `targetSha256` of the written FBX, and `materials`: the approved look of every exported material as URP Lit values, `baseColor` (sRGB `#RRGGBB`), `metallic`, and `smoothness` (1 - Roughness). `null` before the first export. |

## Review Stages

An asset passes its review stages in order. Each stage ends with a reviewer
looking at the asset, on the review sheet or in Blender, and approving it with
`Approve-ArtAsset.ps1`. The approval stores the SHA-256 of the `.blend` file, so
saving the file again, even without a visible change, needs a new approval.

| Kind | Stages | Status |
| --- | --- | --- |
| `static` | `model` | Implemented. |
| `skinned` (characters, creatures) | `model`, then `rig`, then `animation` | The `model` stage is implemented. `rig` and `animation` are planned, and `Approve-ArtAsset.ps1` refuses them until Blender can check their rules. |

Stages are approved in order, and a new approval of an earlier stage reopens
every later stage. `Export-ArtAsset.ps1` exports only when every stage is
approved and the last approval covers the current `.blend` file. It then records
the hashes of the source and the FBX.

An asset is in review while a later stage still waits, such as a skinned model
before its rig. It has no export record and no FBX yet, and its latest approval
must cover the committed `.blend` file, so the repository only holds approved
states: work on the next stage stays on its branch until that stage is approved.

`Verify-ArtAssets.ps1` runs in the CI backend job and fails when an approval is
missing or stale, when an asset in review has an export or an FBX, when the
committed FBX differs from the recorded export, or when a model below
`src/Client/Assets/Art/Models` has no asset. It reads the SHA-256 of a `.blend`
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

Modeling conventions: one Blender unit is one meter, Z is up, and the ground is
at Z = 0; rocks and similar props may sink a few centimeters into it. The front
of an asset faces Blender's **+Y** axis, so Blender's Back view (`Ctrl+Numpad 1`)
shows the front. Blender +Y becomes Unity +Z, the forward direction of an actor
at yaw 0, Blender +X stays Unity +X, and Blender +Z becomes Unity +Y. A model
made in Blender's usual orientation, facing -Y, is turned 180 degrees around Z
with the rotation applied before review.

## Export Settings

`tools/blender/art_pipeline/export.py` writes FBX with fixed settings: the
active collection only (`Export`), Blender's own axes (Forward Y, Up Z) without
Blender's `Apply Transform`, unit scale applied with `FBX Units Scale`,
modifiers applied, face smoothing exported, no animation for `static` assets,
and no custom properties, leaf bones, or embedded textures. Objects hidden in
Blender's viewport, such as a `Depleted` state, are exported too.

These settings were chosen with an axis probe imported into Unity. Exporting
with Forward -Z and Up Y, the usual advice for Unity, leaves a 90 degree
correction rotation on the model root, and the game sets that root's rotation
from the actor yaw. `Apply Transform` with Forward -Y gives the right
direction, but leaves a 180 degree rotation on every mesh and does not support
armatures, which characters need.

## Unity Import Rules

`ArtModelImportRules` is an `AssetPostprocessor` in the `Mmorpg.Client.Editor`
assembly. Every import of an FBX below `Assets/Art/Models` gets these settings,
whatever the importer said before:

- Scale factor 1 with the file scale, and the Blender axis conversion baked
  into the meshes, so every object keeps the rotation it has in Blender.
- `Preserve Hierarchy`, so Unity always creates an explicit model root and every
  Blender root object is a direct child of it, also when there is only one.
- No cameras, lights, visibility, blend shapes, animation, or colliders.
  Presentation never takes part in physics.
- Mesh data is not readable, uncompressed, with imported normals and calculated
  tangents.
- Materials are imported from the model and then remapped by name to the one
  project material of the same name below `Assets/Art/Materials`. The remap is
  stored in the model's `.meta` file.

`ArtModelImportTests` fails when a model has other import settings, embeds a
material because Unity has no material of that name, uses a material outside
`Assets/Art/Materials`, or has a model root or Blender root object with a
position, rotation, or scale. It also reads every `asset.json` and fails when
the model renders with a material that has no export record, or a record names
a material the model does not use, or a Unity material's `_BaseColor`,
`_Metallic`, or `_Smoothness` differs from the approved Blender material
recorded at export, so the game shows the colors the reviewer approved.
`Verify-ArtAssets.ps1` rejects an export record with a missing, malformed, or
duplicate material entry. A Unity material is never changed by the import; set its
values from the export record when a material is added or its Blender color
changes.

## Prefabs

A world actor presentation prefab that uses an exported model is a Prefab
Variant of the model. The prefab keeps its path, GUID, and Addressables entry, so
the presentation registry needs no change, and a new export updates the prefab
through the variant. The materials come from the remap, so the variant has no
material overrides. A resource node model has the roots `Available` and
`Depleted`, which `ResourceNodeView` switches when the node depletes; an
EditMode test checks every resource node prefab for them.

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
of the playable humans. Hair, brows, beards, clothes, and equipment are modeled
on top of them later, faces are painted on them, and the rig and animations are
their next review stages. A script builds both from the realistic bodies and the
Generic Topology head of the
[Human Base Meshes](https://www.blender.org/download/demo-files/) bundle v1.4.1
by Blender Studio (CC0), so they can be rebuilt and refined.

Each file holds one character in the `Export` collection:

- The character collection (`Male` or `Female`) with the root empty of the same
  name at the origin, which becomes the root of the rig. The root carries the
  build's landmarks as custom properties, such as the eyes, lips, wrists, finger
  tips, and toes, for placing the rig and attachments.
- Ten body parts as children of the root, named `<Label>_<Zone>`: `Head`,
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
5. `characters`, `asset`, `compare`, and `review`: the split into parts, one
   asset file per character, the content comparison with the committed asset
   (objects, meshes, UVs, normals, edge flags, materials with their node links,
   textures with whether they are packed, and custom properties), a review file
   with both characters side by side, and the review sheets.

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
| `art/Characters/Human_Female` | `skinned` | `Female` | The female human base body, 1.70 m, with ten body parts, eyeballs, teeth, and tongue, 9,202 triangles, built by `tools/Build-HumanBase.ps1`. Model approved 2026-10-21; in review until its rig and animations. |
| `art/Characters/Human_Male` | `skinned` | `Male` | The male human base body, 1.79 m, with the same topology as the female, 9,202 triangles, built by `tools/Build-HumanBase.ps1`. Model approved 2026-10-21; in review until its rig and animations. |
| `art/WorldActors/Resource_IronVein` | `static` | `Available`, `Depleted` | Three faceted rocks with chunky rust-orange iron ore in two crossing veins, 684 triangles; the depleted state keeps the rocks with broken flat tops and loose rubble, without ore, 194 triangles. The available state is about 1.5 m wide, 1.25 m deep, and 1.0 m high, inside the vein's 1.6 by 1.2 by 1.6 m interaction bounds; the depleted rubble spreads to about 1.9 by 1.7 m. Approved 2026-10-20. |

## Planned

- The `rig` and `animation` stages, their Blender rules, the export of skinned
  assets, and the Unity import rules for skinned characters and creatures,
  including Unity Humanoid avatars. The human base characters are the first.
- A shared palette texture for stylized models, level-of-detail meshes, and
  per-asset collision proxies if presentation ever needs them.

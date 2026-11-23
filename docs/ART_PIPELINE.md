# Art Pipeline

This document is the home of everything about making art for the game: how 3D
models, clips, skin paint, and garments travel from Blender to the Unity client,
the review stages every asset passes before it may reach the game, the rules the
tools enforce, how Unity imports the result, and every how-to with its commands
and expected results. Why characters and their equipment look as they do is in
[Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md), and how to check art in
the running game is in [Manual Testing](MANUAL_TESTING.md).

The scripts are in `tools/art`, and the Python that runs inside Blender is in
`tools/art/blender`. Run every command from the repository root.

## Flow

```text
art/<Category>/<Name>/<Name>.blend      source, edited in Blender (Git LFS)
        |  Render-ArtPreview.ps1        review sheet, or live review in Blender
        v
reviewer approves each review stage
        |  Approve-ArtAsset.ps1         records the date and the content fingerprint of the stage
        v
        |  Export-ArtAsset.ps1          refuses unapproved or changed content; several at once
        |  Build-Art.ps1                all of it in one run: records, syncs, the approvals that
        v                               wait, exports, the Unity follow-up, and the verification
src/Client/Assets/Art/Models/<Category>/<Name>.fbx       with its clips, if skinned
src/Client/Assets/Art/Textures/<Category>/<Image>.png    painted materials only
        |  ArtModelImportRules          fixed import settings, materials remapped by name,
        |                               Humanoid avatar and clips for a skinned asset
        v
Assets/Prefabs/... Prefab Variant of the model -> presentation registry or player -> game
```

The reviewer approves every asset before it is exported. An approval binds to the
content of its review stage, what the stage shows and the export writes, read
from the `.blend` file as a fingerprint, not to the file's bytes; see
[Review Stages](#review-stages). Nothing reaches Unity without an approval of
the exact content that was exported, and CI checks the whole chain without
Blender.

Every asset's `.blend` file is its source, and a person edits it in Blender.
Scripts only write what follows from the source, such as the body parts of the
[human base characters](#human-base-characters) from their editable bodies, and
starting points that a person then edits, such as the gait model's clips in an
[animation library](#animation-libraries). No script replaces handmade work.
After an export, an open Editor creates the project materials the export lacks
and updates the appearance catalogs, the content assets of garments and
attachments, and their Addressables entries by itself when it gets focus, and
`tools/art/Update-UnityArt.ps1` runs the whole [Unity follow-up](#unity-import-rules)
without a focused Editor, the cloth fixtures included, so no menu command
stands between the export and the game. `tools/art/Build-Art.ps1` takes
every asset from changed files to exports in Unity in one run and stops only
where a person must approve; see [Build The Art](#build-the-art).

## What To Do

Every model is built by the [modeling rules](#modeling-rules), and every kind of
art has one how-to:

| To | Start with | How-to |
| --- | --- | --- |
| Set up Blender, and the MCP server for live work from Claude Code | Blender 5.2 LTS, Git LFS, and `BLENDER_PATH` | [Set Up Blender](#set-up-blender), [Set Up The Blender MCP Server](#set-up-the-blender-mcp-server) |
| Add a model for the world, such as a resource node or a prop | `New-ArtAsset.ps1 -Kind static` | [Add A New Asset](#add-a-new-asset) |
| Add an attachment, a model held on a socket, such as a weapon, a shield, or a tool | `New-ArtAsset.ps1 -Kind static -Socket main_hand` | [Make An Attachment](#make-an-attachment) |
| Change a human body: its shape, skin texture, or weights | the body's `.blend` file, then `Sync-HumanBase.ps1` | [Edit The Human Base Characters](#edit-the-human-base-characters) |
| Add a face or an eye color | an image packed into the body's `.blend` file | [Add A Face Or Eye Variant](#add-a-face-or-eye-variant) |
| Change a clip or add one | the library's `.blend` file, or `Sync-AnimationLibrary.ps1 -Generate` for a gait | [Make A Clip](#make-a-clip) |
| Look at clips in motion, with numbers that show a sinking body, a whipping bone, a foot under the ground, or a loop that jumps | `Render-ArtClips.ps1` | [Render The Clips As Videos](#render-the-clips-as-videos) |
| Start a clip from a clip of another skeleton, such as one of a clip library in a `.glb` file | `Import-ArtClip.ps1` with a map in `tools/art/retarget` | [Start A Clip From Another Skeleton](#start-a-clip-from-another-skeleton) |
| Start an animation library | `New-ArtAsset.ps1 -Kind animations` | [Make A Clip](#make-a-clip) |
| Paint onto the skin, such as a tattoo or a scar | `New-ArtAsset.ps1 -Kind paint`, then `Sync-PaintAsset.ps1` | [Make A Paint Layer](#make-a-paint-layer) |
| Move a paint layer's canvas to another body, such as a new base model | `Sync-PaintAsset.ps1 -Canvas`, then `Test-PaintProjection.ps1` | [Make A Paint Layer](#make-a-paint-layer) |
| Fit the garment cage to the bodies, or move a standard line | `Sync-CageAsset.ps1` | [Fit The Garment Cage](#fit-the-garment-cage) |
| Make a garment template, the form of clothes or armor, from a recipe on the cage | `New-ArtAsset.ps1 -Kind garment`, then `Sync-GarmentAsset.ps1` | [Make A Garment](#make-a-garment) |
| Paint the look of a garment template, with holes where the skin shows | `New-ArtAsset.ps1 -Kind texture`, then `Sync-TextureAsset.ps1` | [Make A Garment Texture](#make-a-garment-texture) |
| Review, approve, and export any of them | `Render-ArtPreview.ps1`, `Approve-ArtAsset.ps1`, `Export-ArtAsset.ps1` | [Review, Approve, And Export An Asset](#review-approve-and-export-an-asset) |
| Bring every asset from its changed file to the game in one run: records, syncs, the approvals that wait, exports, Unity, and the verification | `Build-Art.ps1` | [Build The Art](#build-the-art) |
| Update the materials, catalogs, Addressables entries, and cloth fixtures after an export without a focused Editor | `Update-UnityArt.ps1` | [Unity Import Rules](#unity-import-rules) |
| Record the content of a file saved by hand and see which approvals still hold | `Update-ArtContent.ps1` | [Update The Content Record](#update-the-content-record) |
| Confirm that every source, approval, and export is current, such as after an edit of the bodies | the sync scripts and their review sheets | [Check That The Art Sources Are Current](#check-that-the-art-sources-are-current) |
| Measure how closely a model follows its reference pictures | `Compare-ArtReference.ps1` | [Compare A Model With Its Reference Pictures](#compare-a-model-with-its-reference-pictures) |
| Look at clothes and cloth in motion and tune the cloth's springs | `MMORPG > Tools > Art Pipeline > Wardrobe` in Unity | [The Wardrobe](#the-wardrobe) |
| Change the look of the windows: frames, slots, icons, silhouettes, fonts | the SVG files in `Assets/Art/Ui/Sprites`, then `MMORPG > Tools > UI > Build UI Theme` | [UI Art](#ui-art) |

Weapons, shields, quivers, and backpacks are [attachments](#attachments):
models held on a socket of the skeleton.

## Setup

### Set Up Blender

1. Install Blender 5.2 LTS and Git LFS. Run `git lfs install` once per machine;
   a clone or `git lfs pull` then fetches the `.blend` files below `art/`. A
   script that needs a file that is not downloaded stops with
   `... is a Git LFS pointer; run git lfs pull first.`
2. Tell the scripts where Blender is. Replace the path with your own:

   ```powershell
   [Environment]::SetEnvironmentVariable("BLENDER_PATH", "D:\Program\Blender\blender.exe", "User")
   ```

   Expected result: in a new terminal, `& $env:BLENDER_PATH --version` prints
   `Blender 5.2.2 LTS` or a later 5.2 release. Programs that were already
   running, such as the Claude desktop app, see the variable after a restart.
   Without it the scripts take `blender` from `PATH`, or else on Windows
   `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`, and stop with
   `Blender 5.2 was not found. Set BLENDER_PATH to blender.exe.` when that does
   not exist. Another version stops them with
   `The art pipeline needs Blender 5.2; ...`. Every script that runs Blender
   also takes `-BlenderPath`.

### Set Up The Blender MCP Server

The Blender Lab MCP server lets Claude Code inspect and change the Blender file
that is open on the desktop while the reviewer watches. Only live work in
Blender from Claude Code needs it; the pipeline never depends on it, since
approval and export always run Blender in the background from the scripts. It
has two parts:

- The `MCP` add-on from Blender Lab, version 1.0.3, runs inside Blender and
  listens on `localhost:9876` once Blender starts. It needs Blender 5.1 or newer
  and the preference `Allow Online Access`.
- `.mcp.json` starts the `blender` server, `blender-mcp`, with `uvx`, pinned to
  the `v1.0.3` tag of `projects.blender.org/lab/blender_mcp`. It offers 26
  tools, among them `execute_blender_code`, viewport screenshots, scene
  summaries, and searches in the bundled Blender Python API reference and user
  manual.

To set it up:

1. Close Blender, download `mcp-1.0.3.zip` from the
   [v1.0.3 release](https://projects.blender.org/lab/blender_mcp/releases/tag/v1.0.3)
   of Blender Lab, and install it:

   ```powershell
   & $env:BLENDER_PATH -c extension install-file -r user_default -e .\mcp-1.0.3.zip
   ```

   Expected result: `STATUS Installed "mcp"`.
2. Start Blender, open `Edit > Preferences > System`, and enable
   `Allow Online Access`. The add-on refuses to start without it because it
   opens a network port, although it listens on `localhost:9876` only. Under
   `Add-ons > MCP`, keep `Auto Start` on, then save the preferences and restart
   Blender. Expected result: the `MCP` add-on preferences show
   `Server is running`.
3. Install [uv](https://docs.astral.sh/uv/), which provides `uvx`. Claude Code
   asks once to approve the repository's `blender` server. The first start
   downloads and builds the server in about three minutes; later starts take
   seconds. Expected result: a new Claude Code session in this repository lists
   the `blender` server with 26 tools, and `get_objects_summary` returns the
   collections of the file open in Blender.

The add-on runs every Python request it receives inside Blender, without a
sandbox, which is why it listens on localhost only. Keep it on a machine where
only you run Claude Code, and save your work before a live session.

## Layout

| Path | Responsibility |
| --- | --- |
| `art/<Category>/<Name>/<Name>.blend` | Source of truth for one asset, stored in Git LFS. The folder name is the asset name. |
| `art/<Category>/<Name>/asset.json` | The [asset manifest](#asset-manifest): kind, export target, root objects, triangle budget, skeleton and clips of a skinned asset, the content record, approvals, and the export record. |
| `art/Animations/<Name>/<Name>.blend` | An [animation library](#animation-libraries): handmade clips of one skeleton, keyed on the rig of one character, with the bodies that show them. |
| `art/Paint/<Name>/<Name>.blend` | A [paint layer](#paint-layers): transparent paint over the skin of bodies, painted on the layer's own canvas, a copy of a body, and moved to every body by projection. |
| `art/Garments/<Name>/<Name>.blend` | A [garment template](#garments): the form of clothes or armor, built from a recipe on the garment cage and fitted to every body of the cage, one version per body. |
| `art/GarmentTextures/<Name>/<Name>.blend` | A [garment texture](#garment-textures): the look of one template, its image in the template's UVs, with the preview it is painted on and the cage version that preview lies on. |
| `art/Attachments/<Name>/<Name>.blend` | An [attachment](#attachments): a static model below its root `Grip`, held on a socket, such as a sword or a shield, or a [skinned one](#skinned-attachments) whose armature is `Grip`, such as a bow. |
| `art/Projectiles/<Name>/<Name>.blend` | A [projectile](#projectiles): a static model that flies, such as an arrow, its point at the origin. |
| `art/Cages/<Skeleton>/<Skeleton>.blend` | The [garment cage](#garment-cage) of a skeleton: the net of fixed topology that garments are made on, fitted to every body of the skeleton, with its standard lines. |
| `art/Skeletons/<Name>.json` | A shared [skeleton definition](#skeletons): the bones every skinned asset of that skeleton has, their parents, their Unity Humanoid bones, whether they deform the skin, and the constants of the cloth on its chains. |
| `src/Client/Assets/Art/Models/<Category>/<Name>.fbx` | Exported model. It is never edited by hand; CI rejects a model without an asset or with a changed file. A body with an editable mesh exports its topology `<Name>.json` next to it, a garment template one model per body, `Models/Garments/<Name>_<Label>.fbx`, with its data `<Name>_<Label>.json` next to it, and a garment texture with transparent parts its own data per body, `Models/Garments/<Name>_<Label>.json`. |
| `src/Client/Assets/Art/Textures/<Category>/<Image>.png` | Exported image of a painted material, byte for byte as it is packed in the `.blend` file. Never edited by hand; CI rejects a texture without an export record or with a changed file. Paint layers export to `Textures/Paint`, each image cropped to its paint, and garment textures to `Textures/Garments`. |
| `src/Client/Assets/Art/Materials` | Unity URP Lit materials. A Blender material maps to the Unity material of the same name. |
| `src/Client/Assets/Art/Appearance` | The appearance catalog of every body model, `<Model>_Appearance.asset`; see [Unity Import Rules](#unity-import-rules). |
| `src/Client/Assets/Content/Bodies` | The content asset of every garment per body, `Garments/<Model>/<Garment>.asset`, and of every attachment, `Attachments/<Attachment>.asset`, which the client loads on demand; written with the catalogs, never by hand. |
| `src/Client/Assets/Art/Fonts` | TextMesh Pro font assets of the interface, such as `LiberationSans UI SDF.asset`, TextMesh Pro's default font; how it is made is in [Text And Fonts](UNITY_CLIENT_ARCHITECTURE.md#text-and-fonts). |
| `src/Client/Assets/Art/Animation` | Animator controllers that play the exported clips, `HumanLocomotion.controller` and `WolfLocomotion.controller`, and the avatar masks of their layers. |
| `src/Client/Assets/Art/Shaders` | `CharacterSkin.shader`, `CharacterSkinInput.hlsl`, `CharacterSkinForwardPass.hlsl`, and `CharacterSkinGBufferPass.hlsl`, the skin shader `Mmorpg/Character Skin`: URP Lit with paint layers read through the paint UV map; `Garment.shader`, `GarmentInput.hlsl`, and `GarmentForwardPass.hlsl`, the garment shader `Mmorpg/Garment`: URP Lit with dye channels, drawing both faces; `Water.shader`, the water surface shader `Mmorpg/Water`, `Waterfall.shader`, the falling water shader `Mmorpg/Waterfall`, and `WaterCommon.hlsl`, the water's clock and the sea's waves that both share; see [Water](#water). |
| `src/Client/Assets/Art/Water` | The textures and materials of the water, made by code; see [Water](#water). |
| `artifacts/art-previews`, `artifacts/human-base` | The review sheets, review files, and reports the scripts write. Git ignores `artifacts`. |
| `artifacts/art-build` | The output of every Blender run of an export, `export/<Name>.log`, and of a content run, `content/<Name>.log`, the seconds of each asset's last export with its checks computed, `export/seconds.json`, which puts the longest export first, the Unity log of the follow-up, `unity/follow-up.log`, the marker `unity/pending`, which an export leaves until the Unity follow-up succeeds, and `synced.json`, per asset the SHA-256 of its file as the build's last sync of it left it and the fingerprints of what it followed then, so the build does not sync it again while nothing changed. |
| `artifacts/art-cache`, `tools/art/blender/art_pipeline/check_cache.py` | The [cache of the garment checks](#cache-of-the-garment-checks): their results by the content of what they read, so a sync, an approval, and an export compute each once. Safe to delete at any time. |
| `tools/art/New-ArtAsset.ps1` | Creates a new static model, animation library, paint layer, garment cage, garment template, or garment texture: its folder, `asset.json`, and first `.blend` file. |
| `tools/art/Render-ArtPreview.ps1`, `Approve-ArtAsset.ps1`, `Export-ArtAsset.ps1` | Review, approval, and export commands. They share `tools/art/ArtPipelineCommon.ps1`, which also runs several Blender processes at once. |
| `tools/art/Build-Art.ps1` | The [build command](#build-the-art): every content record, sync, and export that changed files need, the approvals that wait, the Unity follow-up, and the verification, in one run. |
| `tools/art/Update-UnityArt.ps1` | Runs the Unity follow-up of an export in the Editor that has the project open, or headless; see [Unity Import Rules](#unity-import-rules). |
| `tools/art/Verify-ArtAssets.ps1` | CI check of every asset without Blender. |
| `tools/art/Update-ArtContent.ps1`, `tools/art/blender/art_pipeline/content.py`, `content_core.py` | Reads the [content of the review stages](#content-of-a-stage) from a `.blend` file into the content record of `asset.json`, for a file saved by hand: `content.py` reads the parts in Blender, `content_core.py` rounds, hashes, and chains them without Blender. |
| `tools/art/Test-ArtContent.ps1` | Proves the content fingerprints on copies of the files: a file saved again keeps every fingerprint, and scripted edits change exactly the parts they touch. It changes no file below `art/` and is not part of CI. |
| `tools/art/Sync-AnimationLibrary.ps1`, `tools/art/blender/art_pipeline/library.py` | Builds an animation library file from its rig, or updates it when the rig changed, and keeps its clips. |
| `tools/art/Compare-ArtReference.ps1`, `tools/art/blender/art_pipeline/reference.py` | Compares a model with the reference pictures in its file: the overlap of their silhouettes per picture and an overlay image. It changes no file. |
| `tools/art/Render-ArtClips.ps1`, `tools/art/blender/art_pipeline/clip_video.py` | Renders the clips of an animation library or a skinned asset as videos and measures their motion. It changes no file. |
| `tools/art/Import-ArtClip.ps1`, `tools/art/blender/human_base/retarget.py`, `tools/art/retarget/<Source>.json` | Adds a clip to an animation library on the `Humanoid` skeleton from clips of another skeleton, with a map from the `Humanoid` bones to the source's bones, such as `UniversalAnimationLibrary.json`. |
| `tools/art/Sync-PaintAsset.ps1`, `tools/art/blender/art_pipeline/paint.py` | Builds a paint layer file from its bodies, or updates it, keeps its canvas, and moves the canvas's paint to every body by projection; `-Canvas` moves the canvas to another body. |
| `tools/art/Test-PaintProjection.ps1` | Checks the move that a body of another topology than a paint layer's canvas gets, through the garment cage, on subdivided copies of the bodies. It changes no file. |
| `tools/art/Sync-CageAsset.ps1`, `tools/art/blender/art_pipeline/cage.py` | Fits the garment cage to every body from its reference, writes its review sheet, and checks it. |
| `tools/art/blender/art_pipeline/body_files.py` | Appends the bodies a garment or the cage is fitted to from their own files, and appends them again when they change. |
| `tools/art/Sync-GarmentAsset.ps1`, `tools/art/blender/art_pipeline/garment.py`, `hang.py`, `coverage.py` | Builds a garment template from its recipe on the cage, with its loose parts on their scaffolds (`hang.py`), fits it to every body through the cage, gives every version the weights of its body, computes what it covers on every body (`coverage.py`), writes its review sheet and review file, and checks it. |
| `tools/art/Sync-TextureAsset.ps1`, `tools/art/blender/art_pipeline/texture.py` | Moves a garment texture to its template when the template's faces or UVs changed, by correspondence, computes the masks of a texture with transparent parts, writes its review sheet, and checks it. |
| `tools/art/Set-TextureDyes.ps1` | Makes a garment texture dyeable: its dye mask and the natural color of each channel, with its image divided so that it looks undyed as it was painted. |
| `tools/art/Sync-HumanBase.ps1`, `tools/art/blender/human_base`, `tools/art/blender/human_base_cli.py` | The tools of the [human base characters](#human-base-characters): their body parts written from their editable bodies, their rig and weights, their checks and review sheets, and the gait model that starts clips in an animation library. |
| `tools/art/blender/human_build` | The scripts that build the human base bodies from their reference, vertex by vertex; see [Rebuild The Human Base Characters](#rebuild-the-human-base-characters). Their work files go to `artifacts/human-build`. |
| `tools/art/blender/art_pipeline` | Python that runs inside Blender: validation, FBX export, the review sheet, in `bodies.py` the body parts of a skinned character written from its editable body, and in `poses.py` the test poses of the `Humanoid` skeleton that the rig reviews use. |
| `tools/art/blender/art_pipeline/cloth.py`, `cloth_core.py` | The cloth chains laid in the target pose that every visible body in the game shows, for the review sheets, review files, and checks: `cloth.py` measures in Blender, `cloth_core.py` lays on plain data with the standard library only; see [Cloth Parity](#cloth-parity). |
| `tools/art/blender/art_pipeline_cli.py` | Blender entry point for the scripts. |
| `tools/art/blender/tests` | Python tests of the art pipeline that run without Blender, their fixtures, and `blender_source.py`, which reads the pure logic of a Blender module from its source; see [Python Tests Of The Pipeline](#python-tests-of-the-pipeline). |
| `src/Client/Assets/Editor/Art/ArtModelImportRules.cs` | Unity import rules for every model below `Assets/Art/Models` and every texture below `Assets/Art/Textures`. |
| `src/Client/Assets/Editor/Art/ArtAssetManifests.cs` | Reads `asset.json` and the skeleton definitions for the import rules, the material command, and the tests. |
| `src/Client/Assets/Editor/Art/ArtMaterialCreator.cs` | `MMORPG > Tools > Art Pipeline > Create Missing Materials`: creates the project material of every exported material that has none, from its export record, and gives every garment texture's material the dye mask and natural colors of its record. |
| `src/Client/Assets/Editor/Art/ArtAppearanceCatalogs.cs`, `ArtTopologyMaps.cs` | `MMORPG > Tools > Art Pipeline > Update Appearance Catalogs`, with the content assets and their Addressables entries, and the maps from Unity's triangles to the faces of a body's editable mesh. |
| `src/Client/Assets/Editor/Art/ArtBodyContentEntries.cs` | The entries of the `Body Content` and `Body Content Shared` Addressables groups, and the assets two bundles would share. |
| `src/Client/Assets/Editor/Art/EditorBodyContent.cs` | Body content read from the asset database at once, for the Wardrobe and the tests. |
| `src/Client/Assets/Editor/Art/ArtExportFollowUp.cs` | The Unity follow-up of an export, `Apply`: imports the exports, creates the missing project materials, gives the garment materials their dyes, updates the appearance catalogs with their content and entries, and writes the cloth fixtures when they changed; `ApplyFromCommandLine` is its batch-mode entry. After an import of exported models, their data, or textures, an open Editor runs the materials, the dyes, and the catalogs by itself, without the refresh and the cloth fixtures. |
| `src/Client/Assets/Editor/Art/CreaturePresentationBuilder.cs` | `MMORPG > Tools > Art Pipeline > Build Creature Presentations`; see [Prefabs](#prefabs). |
| `src/Client/Assets/Editor/Wardrobe/WardrobePreview.cs`, `WardrobeWindow.cs` | `MMORPG > Tools > Art Pipeline > Wardrobe`; see [The Wardrobe](#the-wardrobe). |
| `src/Client/Assets/Editor/Cloth/ClothStressMotion.cs`, `ClothMotionMeter.cs`, `ClothMeasurement.cs` | The cloth stress motions, what is measured of the cloth in them, and `MMORPG > Tools > Art Pipeline > Measure Cloth`, which writes `artifacts/cloth-measurements.md`; see [Measuring The Cloth](EQUIPMENT_VISUALS_DESIGN.md#measuring-the-cloth). |
| `src/Client/Assets/Editor/Cloth/ClothKinematicFixtures.cs` | `MMORPG > Tools > Art Pipeline > Write Cloth Fixtures`: writes the fixtures of [Cloth Parity](#cloth-parity); `Update-UnityArt.ps1` and `Build-Art.ps1` write them only when they changed. |
| `src/Client/Assets/Editor/Water/WaterAssets.cs`, `TileableNoise.cs` | `MMORPG > Tools > Water > Create Water Textures And Materials`: the water's textures from tileable noise and its materials; see [Water](#water). |
| `.mcp.json` | The Blender MCP server for live work in Blender from Claude Code. |

## Asset Manifest

`asset.json` sits next to the `.blend` file. The scripts write it; only `kind`,
`target`, `rootObjects`, `triangleBudget`, and for a skinned asset `skeleton` and
any `clips` of its own are written by hand when an asset is created.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | `2` for a garment template, which has a `cage` and a `recipe`; `1` for every other kind. |
| `kind` | `static` for models without a skeleton, `skinned` for characters, creatures, and attachments with moving parts that get a rig, `animations` for an [animation library](#animation-libraries), `paint` for a [paint layer](#paint-layers), `garment` for a [garment template](#garments), `texture` for a [garment texture](#garment-textures), `cage` for the [garment cage](#garment-cage). A paint layer has no `target`, `rootObjects`, or `triangleBudget`, a garment template no `target`, `rootObjects`, or `bodies`: its models and roots follow from its cage's bodies, a texture none of them, and a cage no `target`, `rootObjects`, `triangleBudget`, or `export`. |
| `target` | `src/Client/Assets/Art/Models/<Category>/<Name>.fbx`, where `<Name>` is the folder name. |
| `rootObjects` | The exact names of the top-level objects in the `Export` collection. They become the direct children of the Unity model, so they carry the contract with the game code, such as `Available` and `Depleted` for a resource node. The one root object of an animation library is its armature, named after its skeleton, and that of an attachment is `Grip`. |
| `socket` | Attachments only: the socket the model is held on, as a bone of a skeleton definition names it in its `socket`, such as `main_hand` for `Socket_MainHand`. A static asset has a socket exactly when its one root object is `Grip`; a skinned attachment, such as a bow, has a socket and its armature `Grip`. |
| `holdClip`, `attackClip` | Weapons held on `main_hand` or `off_hand` that are no tools: the pose clip the arm holds the weapon in and the clip the body plays once whenever its attack starts, clips of an animation library of the socket's skeleton, such as `HoldStaff` and `Cast`; see [Weapons With Clips](#weapons-with-clips). |
| `attackRelease` | Weapons with an `attackClip`, which must name it: the seconds into the clip when its blow lands or its projectile leaves, above 0 and at most 10, such as `0.35`. |
| `launchPoint` | Weapons with an `attackClip` only, optional: where the projectile of its attack leaves the model, as `[x, y, z]` in meters in the socket's axes in Blender, at most 2 m from `Grip` in each axis, such as `[0.0, 0.7, 0.0]`. |
| `drawLength` | Skinned weapons with a `launchPoint` only, such as a bow: how far behind the launch point the nock of its string comes at the end of its clip `Draw`, which such a weapon must have, in meters, above 0 and at most 2, such as `0.62`. |
| `useClip` | Tools only, attachments held while a body uses them: the clip of an animation library of the socket's skeleton that the body plays meanwhile, such as `Mine`; see [Tools](#tools). |
| `offHandGrip` | Tools held with both hands, and weapons with an `attackClip` that takes them in both hands, only on `main_hand`: where the other hand's socket holds the model, as `[x, y, z]` in meters in the socket's axes in Blender, such as `[0.0, -0.33, 0.0]`, at most 2 m from `Grip` in each axis. |
| `triangleBudget` | The maximum number of exported triangles, after modifiers. An animation library exports no meshes and has none. A garment's budget holds for each body's version. |
| `skeleton` | Skinned assets and animation libraries: the name of the skeleton definition in `art/Skeletons`, such as `Humanoid`. |
| `rig` | Animation libraries only: the skinned asset whose rig the clips are keyed on, such as `art/Characters/Human_Male`. |
| `previews` | Animation libraries only: other skinned assets of the skeleton whose bodies show the clips in the library, such as `["art/Characters/Human_Female"]`. |
| `clips` | Animation libraries, and skinned assets with clips of their own: the names of the animation clips the asset exports, such as `["Death", "Idle", "Walk", "Jog"]`. Each is one Blender action. A skinned asset whose clips come from animation libraries, such as the human bodies, leaves `clips` out. |
| `category` | Paint layers only: `underwear`, `scar`, or `tattoo`, the kind of look it is, which says how many a body shows, when a player chooses it, and the layers it is drawn at; see [The Look On The Skin](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-skin). |
| `part` | Paint layers only: the part of the painted material `M_<Part>_<Label>`, such as `Body`. |
| `bodies` | Paint layers: the skinned assets the paint fits, the one a new layer's canvas is copied from first, such as `["art/Characters/Human_Male", "art/Characters/Human_Female"]`. They may have any topology; a body of another topology than the canvas gets the paint through the garment cage, whose bodies it must be among. The cage: the skinned assets it is fitted to, the one it was made from first, of any topology; its garment templates fit them. |
| `cage` | Garment templates only: the garment cage it is made on, such as `art/Cages/Humanoid`. |
| `recipe` | Garment templates only: its `regions`, `cuts`, `over`, `hang`, `ease`, and `hem`, as [Recipes](#recipes) describes, or `"modeled"`. |
| `garment` | Garment textures only: the template it is the look of, such as `art/Garments/Shirt`. |
| `figure` | The cage only: the figure scale in meters, the radius of the ball that rolls over the skin, from 0.01 to 0.2. |
| `lines` | The cage only: the [standard lines](EQUIPMENT_VISUALS_DESIGN.md#standard-lines), each with its `name`, its `region` (`neck`, `trunk`, `arms`, or `legs`), its `bone`, without the side `_L` or `_R` for the arms and legs, and its `share` of the bone's length, from 0 to 1; a line of the neck may also have a `rise`, from above 0 to 20 per meter, by which it climbs toward the sides, the rise times the square of the distance to the side. |
| `fittedTo` | The cage only, written by its approval: every body with the `fitSha256` of the body's export the cage was approved on, the fingerprint of the body's rig, surface, and weights, so a change of only a body's images leaves the cage as it is. |
| `layer` | Paint layers: the order paint layers are drawn in, the lowest first, within the layers of their `category`: 100 to 199 for a scar, 200 to 299 for a tattoo, and 900 to 999 for underwear, such as `200` for the tattoo. Garments: the order of a character's garments from the inside out, from 0 to 1000, such as `150` for a shirt over trousers at `100`. |
| `slot` | Garments: the visual slot, `head`, `body`, `legs`, `hands`, `feet`, `back`, or `hair`. Underwear: the visual slot it is worn in, `body` for a bra or `legs` for briefs. |
| `band` | Garments only: how far from the garment cage the garment lies, `tight`, `mid`, `outer`, `loose`, or `hair`, with the distances of [Layer Bands](EQUIPMENT_VISUALS_DESIGN.md#layer-bands). |
| `hides` | Garments only: the other visual slots it hides, such as `["legs"]` for a floor-length robe, or `[]`. |
| `hidesUnderwear` | Garment templates only, optional: `true` when it hides the underwear of its slot while it is drawn, such as a top that bares the shoulders, `false` by default. |
| `covers` | Garment templates only, optional: `false` when it hides nothing under it, neither skin nor inner garments, such as a cloak that lies on top and may swing open, so its cover mask is empty; `true` by default. |
| `hair` | Garments only: for headwear, what happens to the hair under it, `hide`, `compress`, or `show`; `null` for every other slot. |
| `cloth` | Garments only: the cloth profile of its loose parts, `robe` for the skirt chains, `cloak` for the cloak chains, or `hood`, or `null`. |
| `dyes` | Garment templates only: the number of dye channels, 0 to 3, that its textures may use. `Cloak`, `Robe`, `HoodUp`, and `HoodDown` have one, for their wool. |
| `variants` | Skinned assets only, optional: the [appearance variants](#appearance-layers), each a painted material `M_<Part>_<Label>` with the other `images` `T_<Part>_<Label>_<Option>` it may show in the game, such as `{ "material": "M_Face_Male", "images": ["T_Face_Male_Clean"] }`. The material's own image is named `T_<Part>_<Label>_<Option>` as well. |
| `content` | The [content of the review stages](#content-of-a-stage), written only by the scripts that ran Blender on the file, `null` for a new asset whose file was never read: its `version`, the version of the fingerprints; the `sourceSha256` of the `.blend` file it was read from; `stages`, one fingerprint per review stage in order; and `parts`, the fingerprints of the parts the stages are made of, by name. It is current when its `sourceSha256` is the hash of the committed file, which CI reads from the Git LFS pointer, its version is the pipeline's, and it holds the stages, clips, and bodies of `asset.json`. |
| `approvals` | One entry per approved review stage: the `date` and the `contentSha256`, the fingerprint of the stage's content that was approved. An approval holds while the content record is current and the stage's fingerprint is the approved one. An `asset.json` from before approvals recorded the content of their stage, whose approvals name the `sourceSha256` of a file or whose export names files' hashes, is rejected: such an approval is removed and the stage approved again with `tools/art/Approve-ArtAsset.ps1`, and such an export is set to `null` and the asset exported again with `tools/art/Export-ArtAsset.ps1`. |
| `export` | The record of the last export, `null` before the first; see below. |

The `export` record holds the `contentSha256` of the exported content, the
fingerprint of the asset's last review stage at the export, the
`targetSha256` of the written FBX, and `materials`: the approved look of every
exported material as URP Lit values, `baseColor` (sRGB `#RRGGBB`), `metallic`,
and `smoothness` (1 - Roughness), and for a glowing material its `emission`. A
painted material also names its image in `baseMap`, and its `baseColor` is
white. `textures` lists every exported image, those the materials name and the
variants, with its `name`, `target` PNG, and `sha256`, present only when there
are textures, and `clips` lists every clip of a skinned asset with its `name`,
length in `frames`, and whether it should `loop`. Beyond that:

- A body with an editable mesh records the `dataSha256` of its topology; the
  `fitSha256`, the fingerprint of what the garment cage and the garments read
  from it, its rig, its surface with its faces and their body parts, and its
  weights; and the `paintSha256`, the fingerprint of what paint layers read from
  it, its meshes joined with their faces, materials, zones, digits, and paint UV
  map, and its rig's landmarks.
- An animation library has no materials or textures; it records its `clips` and
  the `rigContentSha256`, the content of its rig's last review stage when it was
  exported, from the rig's content record.
- A paint layer records its `textures`, one `T_Paint_<Name>_<Label>` per body
  below `src/Client/Assets/Art/Textures/Paint`, each with its `layout`, the width
  and height of its body's full paint layout, its `rect`, where the image lies in
  that layout as x, y, width, and height in texels from the bottom left, x and y
  in whole blocks of 4 texels, its `move`, `topology` for the exact move through
  the shared topology or `cage` for the move through the garment cage, and the
  `bodyPaintSha256` of the body's export it was moved to; the
  `cageContentSha256`, the content of the garment cage's `model` stage, when a
  body was moved through the cage; and the `smoothness` and `metallic` of the
  paint, without a `targetSha256`.
- A garment template records the `cageContentSha256` of the approved cage, the
  content of its `model` stage that the template was fitted to; its `models`,
  one per body of the cage in order with its `body` label, the `bodyFitSha256`
  of the body's export it was fitted to, the model's `target` and `sha256`, and
  its `data` file and `dataSha256`; its `clipSources`, every animation library
  of its skeleton with the `contentSha256` of the content whose clips it was
  checked in; its `pairSources`, every exported template
  of another visual slot and a lower layer of its cage with the `contentSha256` of
  the content it was checked over, its `rig` stage, when there are any; its one
  material `M_Garment_<Name>` without an image; and no textures.
- A garment texture records the `garmentContentSha256`, the content of its
  template's last stage, its `masks` with their `body`, `data`, and `dataSha256` and their
  `clipSources` and `pairSources` when it has transparent parts, the
  `pairSources` as a template's, for the templates of lower layers worn under its
  template, whose faces its masks hide, its material `M_Garment_<Name>`,
  and its image `T_Garment_<Name>` below `src/Client/Assets/Art/Textures/Garments`.
  A dyeable texture's material also names its dye mask in `dyeMap`,
  `T_Garment_<Name>_Dye`, exported next to the image, and the natural color of
  each channel in `dyeColors` (sRGB `#RRGGBB`).
- A record names the content of the other assets it was made with, from their
  content records, never their exports, except the fit and the paint layout of a
  body, which only the body's export computes. So an export needs the approvals
  of those assets to hold for their content, not their exports, and assets
  exported together may run in any order and at once. When every asset is
  exported, the content a record names is that of the other asset's export.

## Review Stages

An asset passes its review stages in order. Each stage ends with the reviewer
looking at the asset, on the review sheet or in Blender, and approving it with
`Approve-ArtAsset.ps1`. The approval binds to the content of its stage, a
fingerprint of what the stage shows and the export writes, which
[Content Of A Stage](#content-of-a-stage) describes. A file saved again with the
same content, or rebuilt to the same content, keeps its approvals; a change of
the content, however small, needs a new approval. Only an asset that meets the
rules of the stage can be approved.

| Kind | Stages |
| --- | --- |
| `static` | `model` |
| `skinned` (characters, creatures, attachments with moving parts) | `model`, then `rig`, then `animation` when it has clips of its own |
| `animations` (animation libraries) | `animation`, once it has a clip |
| `paint` (paint layers) | `paint` |
| `garment` (garment templates) | `model`, then `rig` |
| `texture` (garment textures) | `texture`, reviewed on its template |
| `cage` (the garment cage) | `model`; it has no export, since Unity never needs it |

The `rig` review looks at the skeleton and the weights: test poses that raise
the arms, bend the elbows and knees, stride, sit, close the hands, open the jaw,
and turn the eyes, with close-ups of the shoulders, hips, knees, and hands and a
weight view. The `animation` review looks at every clip in motion, in the library
file on a checkered ground and on its review sheet, and
`Sync-AnimationLibrary.ps1` reports how far a foot on the ground slides in the
`Idle`, `Walk`, and `Jog` clips when the game moves the body at their speeds.

Stages are approved in order: `Approve-ArtAsset.ps1` approves a stage only while
the approval of every earlier stage holds. A stage's fingerprint is chained to
the earlier stage's, so a change of the content of an earlier stage changes every
later stage too, and their approvals no longer hold until they are approved
again; a new approval of an earlier stage whose content did not change leaves
the later approvals holding. After an approval the script says which later
stages still hold. `Export-ArtAsset.ps1` exports only when every stage is
approved and every approval holds for the content of the current `.blend` file.
It then records the content it exported and the hashes of the written files.

An asset is in review while a later stage still waits, such as a skinned model
before its rig. It has no export record and no FBX yet, and every approval it has
must hold for the committed `.blend` file, so the repository only holds approved
states: work on the next stage stays on its branch until that stage is approved.

`Verify-ArtAssets.ps1` runs in the CI backend job and in `tools/Test-Changes.ps1`
for a change below `art/` or `tools/art/`. It fails when a content record was
read from another version of the committed `.blend` file, with another version
of the fingerprints, or for other stages, clips, or bodies than `asset.json`
names, when an approval is missing or the content of any approved stage changed
after its approval, when an `asset.json` still records file hashes from before
content records, when an export comes from other content than the current one,
when an animation library was exported against other content of its rig, when
an asset in review has an export or an FBX, when one of the garment cage's bodies
has another export than the cage was approved on, when a paint layer was moved
to another export of a body or through other content of the cage, when a
garment template was fitted to other content of its cage or exported for other
bodies than its cage has, or was checked in or over other content of the
animation libraries or of the templates of lower layers than theirs now, when a
garment texture was exported against other content of its template, is painted
in other UVs than its template's current ones, or has masks for other bodies
than its template, when two assets export a
material of the same name, when the committed FBX, data, or a committed texture differs from
the recorded export, or when a model below `src/Client/Assets/Art/Models` or a
texture below `src/Client/Assets/Art/Textures` has no asset. Every problem names
the script to run, such as `Wolf.blend was saved after its content was read;
run tools/art/Update-ArtContent.ps1 -Asset art/Creatures/Wolf` or `the 'rig'
content of Wolf.blend changed after its approval`. It reads the SHA-256 of a
`.blend` file from its Git LFS pointer, so CI does not download the sources.

### Content Of A Stage

The content of an asset is its own data: what the export writes, what the
review shows, and what another asset reads from the file. Blender reads it with
`tools/art/blender/art_pipeline/content.py` as parts, each a SHA-256 of plain
data, and `content_core.py` chains them into one fingerprint per review stage:

```text
stage[k] = SHA-256("art-content/<version>/<kind>/<stage>\n" + stage[k-1] + "\n"
                   + "<part>=<fingerprint>\n" for each part of the stage, by name)
```

So a change of a model's shape changes the `model` stage and every later one, a
change of only the weights leaves `model` as it is and changes `rig` and
`animation`, and a change of one clip changes only `animation`. The record in
`asset.json` keeps the parts too, so a report can say what changed and other
assets can depend on one part, as a garment texture does on its template's `uv`.

| Kind | Stage | Parts |
| --- | --- | --- |
| `static` | `model` | `shape`: every object of the `Export` collection with its type, data name, parent, world matrix, rotation mode, its switch `Disable in Viewports`, which the FBX writes as its visibility, its custom properties, its modifiers, an armature modifier too, since a static asset has no `weights` part, its constraints, the drivers of the object and its data, and its material slots, with every input of a modifier, constraint, or driver as [Modifier Inputs](#modifier-inputs) reads it; every mesh with its vertices, edges, faces, and corners in their order, its corner normals, every attribute but the UV maps and the seams, its shape keys, with the evaluation time and the frames of absolute keys, its custom properties, its vertex groups with their weights when a modifier reads one or an armature modifier deforms it, and the mesh once more as the export writes it, with its modifiers and shape keys applied, so whatever shapes the exported mesh changes the part; and the scene settings the FBX export reads: the unit system and unit scale, which the export applies and Unity takes as the file's scale, and the Simplify switch with its subdivision levels. `uv`: the UV maps of every mesh in their order, with which is active and which renders. `materials`: the materials of the meshes by name, each node with its image, the values of its unlinked inputs, and the settings that say what it does, every link, and the pixels of every image a node uses. |
| `skinned` | `model` | `shape`, `uv`, and `materials` as for a static asset, of the `Export` collection and the editable source body in the `Source` collection, whose zones, paint UVs, and weights the export reads; an armature's custom properties belong to its skeleton and an armature modifier to its `weights`, so the `shape` holds no mesh with the armature applied, and `materials` also holds every variant image `T_<Part>_<Label>_<Option>` of a painted material. |
| `skinned` | `rig` | `skeleton`: every bone by name with its parent, deform and connect flags, rest matrix, length, how it inherits its parent's motion, and its custom properties, every pose bone's rotation mode and constraints, the armature's pose position, and the armature's custom properties, such as the landmarks a body's rig keeps. `weights`: every mesh's vertex groups by name with every vertex's weight in them, and its armature modifiers with the vertex group they are limited to; the armature by name, the asset's own, whose rest pose is its `skeleton`. |
| `skinned` | `animation` | `clip:<Name>` for every clip of `clips`: the action's curves by slot, path, and channel with their keys, handles, interpolation, easing with its back, amplitude, and period, extrapolation, and modifiers, the samples of a curve converted to samples, its frame range, its custom properties, such as `loop`, and the frame rate the scene plays it at. |
| `animations` | `animation` | `skeleton`: the armature the clips are keyed on, placed as the export places it, with its rest pose as a skinned asset's `skeleton` without its custom properties, and the scene settings the export reads, as in a static asset's `shape`; and `clip:<Name>` for every clip. |
| `paint` | `paint` | `canvas`: the canvas, `Canvas`, with its UV maps, the rig's landmarks it keeps, and the body it was copied from, and its cage version `Canvas_Cage`. `paint`: the canvas's image `T_Paint_<Name>` with its roughness and metallic. `body:<Label>` for every body: the paint moved to it, `T_Paint_<Name>_<Label>`, with its crop and layout, as the export writes it. |
| `garment` | `model` | `shape`: every body's version `<Name>_<Label>` with its object, mesh, and attributes as a static asset's, such as what the sync fitted it to, every UV map but the first version's active one, and the scene settings the export reads. `uv`: the fingerprint of the first version's faces and UVs, exactly the `template_uvs` that a texture painted in them records. `materials`: the template's material. |
| `garment` | `rig` | `weights`: every version's vertex groups and weights and its armature modifier, whose armature, the body's rig that the garment keeps a copy of, counts by name. |
| `cage` | `model` | `net`: the reference `Cage` with its attributes, weights, figure scale, and lines, and its rig `CageRig` with its rest pose and landmarks. `fit:<Label>` for every body: the cage's version `Cage_<Label>` fitted to it, with its attributes. |
| `texture` | `texture` | `image`: the pixels of `T_Garment_<Name>` and of its dye mask, each with its size, channels, color space, and alpha mode. `material`: the roughness, metallic, and dye colors the export reads, and the material that shows them. `templateUv`: the `template_uvs` the texture is painted in. A texture holds for its template while its `templateUv` is the template's `uv`. |

What is not content changes nothing:

- Copies of other assets that a sync keeps current: the bodies of a garment, of
  the cage, and of a paint layer with their armatures, the cage versions in a
  garment file, the template's preview and its cage in a texture file, and the
  rig and preview bodies of an animation library, beyond the rest pose its clips
  are keyed on.
- Bookkeeping of another asset's state, such as the scene property
  `cage_content` of a garment, the content of the cage it was fitted to, or the
  `paint_layout` of a paint layer's body.
- View and editor state: the selection, the active object, hiding in the
  viewport, display types, the current frame, the 3D cursor, and the seams,
  which only unwrapping reads.
- Datablocks nothing uses, the order of named things such as objects, bones,
  vertex groups, materials, images, and clips, file paths, Blender's version,
  and dates.

The order of a mesh's vertices, faces, and corners is content, since the game's
data files name faces and vertices by index. Numbers are rounded before they
are hashed, so noise far below what anyone could see changes nothing: positions,
matrices, normals, UVs, key values, and handles to 6 decimals, weights to 5,
with negative zero made positive. An 8-bit image counts by its 8-bit values and
a float image by its values rounded to 6 decimals.

#### Modifier Inputs

What a modifier, a constraint, or a driver of an own object reads shapes the
export, so it is content too, and the reader never leaves one out:

- An object it points to counts by name when it is one of the asset's own
  objects; any other object counts with its record and, for a mesh or a curve,
  its data, and a collection with every object in it.
- A Geometry Nodes modifier counts with its node group, every node with its
  settings and the values of its unlinked inputs and of its outputs, every link,
  the group's interface, and the modifier's own value or attribute for every
  input and output; a group node counts with its group.
- A material, an image, a custom profile, such as a bevel's, and a curve
  mapping, such as a falloff curve, count with what they hold.
- A mesh whose modifier is limited to a vertex group counts with its vertex
  groups and weights in its `shape`.
- An armature modifier belongs to the `weights`, with its armature by name.
- Anything else a setting points to, such as a texture of a displacement, and a
  list setting with entries, such as the targets of an armature constraint,
  stops the reader with an error that names the object, the modifier or
  constraint, and the setting, such as `Object Blade, modifier Displace points
  to a CloudsTexture in its setting texture, which the content fingerprint has
  no rule for.` Give the reader a rule for it, in `content.py`, before such an
  asset can be approved.

When the reader got these rules, the assets had no modifier but armature
modifiers on their own objects, and no constraint or driver there; the only
constraints were those of the animation library's preview body, which is a
copy.

The fingerprints have one version, `VERSION` in `content_core.py` and
`$script:ArtContentVersion` in `tools/art/ArtPipelineCommon.ps1`, which a Python
test holds equal. A record of another version is not current, so its approvals
cannot hold. A change of how any part is read raises the version and ships with
a conversion that first proves every record under the old version, reading each
committed file, and only then binds the existing approvals to the new
fingerprints.

`tools/art/Test-ArtContent.ps1 -All` proves the fingerprints with Blender on
copies of the files: every file opened and saved again has another file hash
and the same parts and stages, and on one asset of each kind a moved vertex, a
changed UV, material value, pixel, weight, bone, key, key's easing, or unit
scale changes exactly the part it touches with its stage and every later one,
while a selection, an active
object, hiding, another current frame, an unused datablock, or an edit of a
copied body changes nothing. Expected result: a `PASS` line per asset and edit,
`Resave: <n> of <n> asset(s) keep every fingerprint.`, and `Proved the content of
<n> art asset(s).`, after several minutes; it is not part of CI. With `-Asset` it
proves the named assets and the edits on the first of each kind among them, and
skips an edit the file has nothing for, such as a pixel of a model without an
image, with a `SKIP` line.

### Update The Content Record

Every script that saves a `.blend` file reads its content and writes the record
itself, and `Approve-ArtAsset.ps1` and `Export-ArtAsset.ps1` do so before they
check anything, for the asset, for the cage of a garment, and for the other
assets whose content an export records, so a file saved by hand in Blender
needs no step before them. An export of several assets makes these records
current, and writes them, before any Blender of a wave starts.

A record pairs the content Blender read with the hash of the file it read, so a
script takes the file's hash before the Blender run whose content it records and
again after it, and stops when they differ: `<Name>.blend was saved while it was
read; run the command again.` An approval binds to the content its own
validation run reported, and stops with `<Name>.blend changed while it was
approved; run the command again.` when that is not the record's. Every export
run reports the content it exported too; when that is not the content the gate
accepted, or the file's hash changed during the run, the export stops with
`<Name>.blend changed while it was exported; run the export again.` before it
writes its record, so the files it wrote match no record and
`Verify-ArtAssets.ps1` fails until the export runs again.

`Verify-ArtAssets.ps1` cannot read a file's content without Blender; after a
file was saved by hand, record its content and see which approvals hold:

```powershell
.\tools\art\Update-ArtContent.ps1 -Asset art/WorldActors/Resource_IronVein
```

Expected result: `art/WorldActors/Resource_IronVein: recorded its content.` or
`... its content record is current.`, then a line per stage, such as
`Resource_IronVein: the 'model' approval holds.` for a file saved without a
change, or `Resource_IronVein: the 'model' content changed after its approval;
review it and approve it again with tools/art/Approve-ArtAsset.ps1 ...` after a
real edit. `-All` updates every asset.

### Review, Approve, And Export An Asset

Render the review sheet:

```powershell
.\tools\art\Render-ArtPreview.ps1 -Asset art/WorldActors/Resource_IronVein
```

Expected result: `Review sheet for Resource_IronVein:` followed by the path of
`artifacts\art-previews\Resource_IronVein.png`, and `Resource_IronVein took
<n> s.` The sheet of a static model has one row per root object, here
`Available` and then `Depleted`, each seen from four sides at the game camera
pitch and once next to a 1.8 m mannequin, on a dark green ground. Every other
kind has a sheet of its own, described with the kind. The sheet of a garment
template or a masked texture takes what it covers through the
[cache of the checks](#cache-of-the-garment-checks), and `-NoCache` computes it.

To look around freely, open the source in Blender:

```powershell
& $env:BLENDER_PATH art\WorldActors\Resource_IronVein\Resource_IronVein.blend
```

Hold the middle mouse button to turn the view, scroll to zoom, and use the eye
icons in the Outliner to switch between `Available` and `Depleted`. The
`Reference` collection holds a 1.8 m mannequin and a wireframe of the vein's
interaction bounds; it is never exported. Saving the file keeps its approvals as
long as its content is the same: switching the eye icons, selecting, or moving
to another frame changes no content, so closing Blender without saving is no
longer needed for the approvals.

When the reviewer approves the model, record the approval, export, and verify:

```powershell
.\tools\art\Approve-ArtAsset.ps1 -Asset art/WorldActors/Resource_IronVein -Stage model
.\tools\art\Export-ArtAsset.ps1 -Asset art/WorldActors/Resource_IronVein
.\tools\art\Verify-ArtAssets.ps1
```

Expected result: `Approved the 'model' stage of Resource_IronVein
(triangles=878).`, `Exported Resource_IronVein to
src/Client/Assets/Art/Models/WorldActors/Resource_IronVein.fbx
(triangles=878).`, and `Verified <n> art asset(s).` Each asset of an approval
or an export ends with the seconds it took, such as `Resource_IronVein took
3.9 s.`; see [Cache Of The Garment Checks](#cache-of-the-garment-checks). Unity
imports the model the next time it gets focus, or at once with
`.\tools\art\Update-UnityArt.ps1`; see [Unity Import Rules](#unity-import-rules).
Commit the `.blend` file, `asset.json`, the FBX, and its `.meta` file together,
with the materials and catalogs Unity creates for them.

`-Stage` is `model` unless it names another stage. `Export-ArtAsset.ps1` takes
several assets at once, or `-All` for every asset but the cage, and runs several
Blender processes at once, `-Jobs <n>` of them, by default half the logical
processors and at most 8. It plans every export of a wave before the wave
starts, so the content records of the other assets an export names are made
current and written first, and runs the bodies and the other skinned and static
assets as the first wave, since the cage, the garments, and the paint layers
read a body's fit and paint layout from its export, and everything else as the
second, the longest export first, by the seconds each asset's last export took
with its checks computed, which `artifacts/art-build/export/seconds.json` keeps.
A record names the content of the other assets, not their exports, so the order
within a wave changes no byte, and the script alone writes each `asset.json`,
when that asset's Blender ended, whole, to a file beside it that then takes its
place. Windows refuses to open the file for that moment, so the Blender runs
read every `asset.json` through `art_pipeline/manifest_io.py`, which tries
again for a few seconds. Each asset's lines are printed together, as its
Blender ends, and its Blender's full output is kept in
`artifacts/art-build/export/<Name>.log`. An export that fails leaves the others
of its wave running, and the script then stops with every failure before the
next wave. `-Jobs 1` exports one asset after the other in the order of the
records: the models and paint layers, the animation libraries, the garment
templates from the lowest layer up, and the textures.

To see the guard work, open the `.blend` file, move one vertex of `Available` a
little in Edit Mode, save, and export once more. Expected result: the export
records the new content first and then stops with `Resource_IronVein cannot be
exported: the 'model' content of Resource_IronVein.blend changed after its
approval. ...`, and `Verify-ArtAssets.ps1` reports the same problem. Saving the
file again without a change instead keeps the approval: the export records the
same content and goes through. Restore the file and its record with
`git checkout -- art/WorldActors/Resource_IronVein`.

### Check That The Art Sources Are Current

Run these checks after a change that other assets follow, such as an edit of
the human bodies or the garment cage, or to confirm a clean state. They need
[Blender](#set-up-blender) and confirm that the committed art sources,
approvals, and exports agree and that every review sheet shows what the
reviewer approved. `Verify-ArtAssets.ps1` checks the records without Blender,
and [Build The Art](#build-the-art) runs the syncs that the records show are
needed, with the exports and the Unity follow-up, in one command.

1. Run `.\tools\art\Sync-HumanBase.ps1`, `.\tools\art\Sync-CageAsset.ps1`, and
   `.\tools\art\Sync-GarmentAsset.ps1 -Asset art/Garments/<Name>` and
   `.\tools\art\Sync-TextureAsset.ps1 -Asset art/GarmentTextures/<Name>` for
   each garment and texture. Expected result: the human base reports each body
   as unchanged with its committed file kept and every approval holding, and
   every other asset is up to date and meets every rule with its stages approved
   for this content, such as `Shirt: the 'rig' approval holds.` and
   `Shirt meets every rule, and both stages are approved for this content.` The
   garment reports also state how much skin comes through in motion; skin that
   comes through a garment at rest stops the sync.
2. Open the review sheets that the scripts write below `artifacts`, such as
   `artifacts\art-previews\Humanoid.png` for the garment cage and
   `artifacts\human-base\review\Human_Male_rig_chains.png` for the cloth chains.
   Expected result: each sheet shows the asset as
   [Assets](#assets) describes it, with masks applied,
   and the mask rows of a garment show orange where it covers the body and a
   yellow band of visible skin along its edges.
3. Render the animation library's review sheet:

   ```powershell
   .\tools\art\Render-ArtPreview.ps1 -Asset art/Animations/Human_Actions
   ```

   Expected result: two rows of frames per clip on both bodies, among them
   `Death`, in which the body tips over to its right and backward and lies on
   its back with the arms out. Open the library in Blender to play `Idle`,
   `Walk`, and `Jog`: the male and the female stand, walk, and jog in place,
   the pelvis turning and the arms and hands following the swing. Close Blender
   without saving.
4. Open `art\Characters\Human_Male\Human_Male.blend`. Expected result: the
   Outliner holds `Source > Male_Body`, shown in the viewport, and `Export >
   Male`, the armature, with `Male_Body`, the ten hidden body parts from
   `Male_Head` to `Male_Feet`, and the eyes, teeth, and tongue as its children,
   and the file has no actions. Select `Male_Torso`, open the UV Editor, and
   switch the UV map from `UVMap` to `UVMap_Paint` in the mesh data. Expected
   result: both maps are the same: the chest is one island, the trunk's front,
   with both of its halves side by side, and no island lies over another.
   Close Blender without saving.

### Build The Art

`tools/art/Build-Art.ps1` takes the art from changed `.blend` files to exported
assets in Unity in one run and stops only where a person must look. It never
records an approval. Run it from the repository root after editing art, or to
confirm a clean state:

```powershell
.\tools\art\Build-Art.ps1
```

1. It makes every content record current, one `content` run per file whose
   hash differs from its record, several Blender processes at once: the cage
   and every other asset first, then the garment templates, whose content run
   takes the cage's content.
2. It plans what each asset needs, in the order the assets follow each other:
   the bodies, the cage, the animation libraries, the garment templates from the
   lowest layer up, the textures, the paint layers, and the rest. An asset
   needs:
   - `sync`, with its kind's sync script, when something it follows changed: a
     body's fit for the cage, the cage or a body for a garment template, its
     template's `uv` for a texture, its rig for a library, a body's paint layout
     or the cage for a paint layer, or when its own content differs from its
     approval, which the sync fits again and shows on a new review sheet. The
     human bodies have no sync here; `Sync-HumanBase.ps1` stays a step of its
     own;
   - `approval` when the content of a review stage differs from its approval,
     or the cage's bodies have another fit than it was approved on;
   - `export` when its approvals hold and its export record is missing, older
     than its content or its files, or names other content of what it follows
     than the current one;
   - `waits` when it needs an export but follows an asset that waits for an
     approval, also through other assets, so nothing that depends on an
     unapproved change is exported;
   - `later` when it follows an asset that this build syncs first, or a body
     that it exports first, whose new fit or paint layout decides what it needs;
   - `nothing` otherwise.

   `-Plan` makes the content records current, prints the plan with the
   approvals that wait, and stops.
3. It runs the syncs, one asset at a time, each at most once, and plans again.
   After a sync it records in `artifacts/art-build/synced.json` the SHA-256 of
   the asset's file and what it followed: the content of every asset it follows
   and the fit and paint layout of the export of every body it follows. A sync
   reason does not make an asset `sync` while its file and everything it follows
   are exactly what that record holds, so a run after a sync does not sync again
   what an approval or an export still waits for, such as a garment approved
   after a change of the cage, whose export record names the old cage until its
   export; the asset goes on to `approval` or `export`. A missing or broken file
   costs one sync.
4. It exports every asset that needs an export, with `Export-ArtAsset.ps1` and
   `-Jobs` Blender processes at once, and plans again, since a body's new export
   may give the cage, the garments, and the paint layers a sync.
5. It runs the Unity follow-up, `Update-UnityArt.ps1`, while the marker
   `artifacts/art-build/unity/pending` is there, which `Export-ArtAsset.ps1`
   leaves once it wrote a record and `Update-UnityArt.ps1` removes when it
   succeeds, so a follow-up that failed, such as in Play Mode or after its time,
   runs again with the next build, also without an export.
6. It runs `Verify-ArtAssets.ps1 -Waiting` with every asset whose need is not
   `nothing`: their problems are listed apart and do not fail it, while every
   other problem does, also one that belongs to no asset, such as a stray file or
   a material of two assets. Without `-Waiting`, as CI runs it, every problem
   fails it.
7. It lists, once and from its last plan, every review stage that waits for an
   approval, in the order to approve them: the asset, which parts of its
   content differ from the last committed `asset.json`, its review sheet, and
   the exact `Approve-ArtAsset.ps1` command. Then it prints the seconds of each
   phase and of each asset's content run, sync, and export, how many check
   units of the exports came from the cache, the assets it synced, and what is
   left for a person.

Exit code 0 means every asset is exported and verified, 2 that review stages
wait for an approval and everything else that could be exported was, and 1 an
error, such as a sync or an export that breaks a rule, a failed Unity
follow-up, or a problem of the verification that no approval explains.
`-Jobs <n>` sets how many Blender processes run at once, and `-NoCache`
computes every check of a garment template or a masked texture.

When the reviewer rejects a change after the build synced what follows it, such
as a texture moved to a template's new UVs or garments fitted to a changed
cage, the syncs changed those files too. The build ends with `Synced by this
run, so their files changed: ...`, which names every asset it synced; restore
the changed asset and those from Git, such as
`git checkout -- art/Garments/Trousers art/GarmentTextures/WoolTrousers`, and run
the build again.

Expected result on a clean checkout: the plan lists every asset with
`nothing` and ends with `In all: 44 nothing.`, then `Verified 44 art
asset(s).`, the seconds per phase, `Left for a person: nothing.`, and `Result:
every asset is exported and verified.`, exit code 0, in about a minute;
`git status` lists nothing.

To see the build stop at an approval, move one UV of a garment template's first
version in Blender, such as `Trousers_Male` in
`art\Garments\Trousers\Trousers.blend`, save, and run
`.\tools\art\Build-Art.ps1 -Plan`. Expected result: `art/Garments/Trousers
sync: its 'model' content differs from its approval`, every template and
texture that follows it `later: it follows art/Garments/Trousers, which this
build syncs or exports first`, and its `model` and `rig` stages under `Review
stages that wait for an approval` with `the parts uv changed`. Without `-Plan`,
the build syncs the template, then its texture `WoolTrousers` for the new UVs,
lists both for an approval with their review sheets and commands, marks the
templates of higher layers worn over it and the masked textures on them
`waits`, exports nothing, names both under `Synced by this run`, and ends with
exit code 2 after a few minutes. Run it again: it syncs nothing, since both
files and what they follow are as its syncs left them, and ends with the same
list and exit code 2. Restore the files and records with
`git checkout -- art/Garments/Trousers art/GarmentTextures/WoolTrousers`.

### Compare A Model With Its Reference Pictures

A model built from concept pictures keeps them in its file as image empties in
the `Reference` collection, each placed where it shows the model, such as
`Concept_Side` and `Concept_Front` of the wolf. `Compare-ArtReference.ps1`
measures how closely the model follows each of them, for a `static` or
`skinned` asset:

```powershell
.\tools\art\Compare-ArtReference.ps1 -Asset art/Creatures/Wolf
```

Expected result: one line per picture, such as `Concept_Side: 77.2 % overlap;
the model misses 20.6 % of the picture's figure and adds 3.0 % outside it:
<path>` and `Concept_Front: 85.9 % overlap; ...`, and per picture an
overlay `artifacts\art-previews\Wolf_reference_Concept_Side.png`: green where
the model and the picture agree, red where only the picture has the figure,
and blue where only the model has it. Both shares are of the picture's figure.
The script never fails on a number and never saves the file; it is a measure
for the reviewer, not a gate. An asset without a picture stops with
`Wolf has no reference picture in its Reference collection.`

How it measures:

- An orthographic camera sees each picture as it faces: it has the empty's
  world rotation, sits in front of the picture's middle along the empty's +Z,
  and spans the picture's larger side, its display size times the empty's
  scale. The render has the picture's pixel size, at most 2048 pixels on its
  larger side.
- The model is every mesh of the `Export` collection below the manifest's first
  root object, or below `-Root`, such as `-Root Depleted` for a resource node.
  It is rendered as it lies in the file, on a transparent film, and its pixels
  above half alpha are its silhouette.
- The picture's figure is its opaque part when it has transparency. On an opaque
  picture it is every pixel whose color differs by more than 0.045 from the
  background, both from that of its top corners and from that of its row's left
  and right edges, so a soft gradient stays background; the bottom 2 % of the
  rows, often a ground line, is left out.
- The overlap is the intersection of both figures over their union.
- A picture that is scaled unevenly or mirrored stops the script with `The
  picture <name> is stretched, ...` or `... is mirrored; give it one scale, as
  the model has.`, since the model would be measured against a figure the
  picture does not show.

## Modeling Rules

Every model follows these rules, whoever or whatever builds it: a person in
Blender, Claude Code through the Blender MCP server, or a script. They hold
until the reviewer changes them here.

- **A reference mesh and a designed cage.** Unless the reviewer says otherwise,
  every model is built the way the [human base characters](#human-base-characters)
  were:
  1. The reference the reviewer gives: pictures of how the model should look,
     such as a concept in a front and a side view, or a reference mesh, such as
     the realistic bodies of Blender Studio's Human Base Meshes. Only when the
     reviewer gives no reference is a reference mesh looked for, from a source
     the reviewer approves; a download needs the reviewer's approval first.
  2. A coarse cage of quads designed for the model, its topology decided once:
     rings of a fixed number of sides around the trunk, the neck, and each limb,
     joined at the coarse level, with parts such as the hands, the paws, and the
     head fitted on their own.
  3. The cage fitted to the reference, subdivided once first where the budget
     allows: matched to the outlines of the reference pictures view by view, or
     cast onto a reference mesh from points inside it, such as the joints; one
     half built and mirrored to the other; and then relaxed along the surface.
  4. When the whole model is built: unwrapped along its part borders and
     hand-painted on its UVs.

  Only player characters and NPCs get eyeballs of their own, with eyelids; a
  Mob's eyes are part of its mesh and its colors.
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
  model's scale: pictures as reference images in the views they show, such as
  the front and the side, and meshes in the `Reference` collection. Match the
  model to it view by view, first the silhouette, then the planes, then the
  colors, and compare the two side by side in the same views before showing a
  part.
- **Polygon modeling only.** Build the model polygon by polygon, by hand in
  Blender or with a script that places every vertex and face on purpose: quads
  and triangles that follow the form, with edge loops around the joints so they
  bend cleanly. Never sculpt, never generate the form from volumes, metaballs,
  or distance fields, and never remesh or decimate a model automatically.
- **Clean polygons on the form.** Use about the model's triangle budget from
  [Art Direction](#art-direction-for-models-and-textures), never far below it,
  and spend it on the form: more rings and loops where they round the silhouette
  and show the planes of the reference, never ones that only add triangles to a
  flat area. Keep every polygon as clean and regular as possible, so the model
  reads well from the game camera.
- **Easy to split, clean UVs.** Design the cage so the model splits easily into
  its parts, such as the head, the neck, the trunk, each leg, the paws, the
  tail, and the ears: an edge loop runs along every border between two parts,
  and every face belongs to one part. The UV seams follow those loops and lines
  the camera rarely sees, such as the middle of the belly, the inside of the
  legs, and under the tail, and hands and paws open like gloves, in a back and a
  palm half with strips for the digits. Each part unwraps into clean islands
  without stretching, folds, or slivers; never with an automatic projection such
  as Smart UV Project.
- **Hand-painted on the UVs, last.** Color a model with a texture hand-painted
  on its UVs, never face by face: no color per face or per material, so the
  color borders follow the design and not the polygons. Hand-painted means
  painted stroke by stroke, by a person in Texture Paint mode or by a script
  that places every stroke on purpose, never filled from rules over the faces.
  Paint only when the whole model is built, so the painting is done once, over
  the final UVs: the model is still built one part at a time, each part
  accepted before the next, and painted when the last part is accepted.
- **Even density.** Spread the polygons evenly over the whole model, with only
  as many more as a detail needs where it matters, such as the face, the paws,
  or the joints.
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
camera that turns freely; see [Game Design](GAME_DESIGN.md#setting). For models
and their textures this means:

- The [water](#water) is the reference for the style of everything: fantasy
  realism with a slightly cartoony touch, never photorealism. Materials and light
  behave believably, with depth, transparency, reflections, and glints of the
  sun, but colors are clear and saturated, shapes are simplified and a little
  exaggerated, and details are painted and readable instead of measured from
  reality. Compare every new model, texture, and effect with the water at the
  waterside of Development World 2 before it goes to review.
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
- Flat-shaded facets with hard edges, also on Mobs and other creatures, with
  their colors hand-painted into a texture on their UVs (see Hand-painted on the
  UVs, last, in [Modeling Rules](#modeling-rules)). Playable characters are the
  exception: smooth shading with the form painted into their textures, as on
  the human base characters. Props made before textures became the rule, with
  flat material colors, such as the Iron Vein, the Oak Tree, the sword, the
  shield, the staff, and the gathering tools, keep them until the reviewer asks
  for textures; every new prop has its hand-painted texture.
- The model fills the actor's `interactionBounds` from the content catalog, so
  the click area matches what the player sees.
- Triangle budgets, about what a model uses: about 2,000 for a prop, such as a
  resource node or an attachment; about 200 for a projectile, such as an arrow,
  because many fly at once; about 6,000 for a Mob or another creature; about
  14,000 for a playable character with its eyeballs, teeth, and tongue. The
  budget is a target, not a ceiling to stay far below: a new model ends within
  about 10 % of it, above or below, and spends the triangles on the form, the
  silhouette, and the details of the reference. Each asset's `triangleBudget`,
  the most its export may have, allows the upper end, such as 6,600 for a Mob.
  Models approved before these budgets, such as the props above and the human
  base characters, keep their triangles until the reviewer asks for more.
  Garments have budgets of their own per visual slot; see
  [Budgets](EQUIPMENT_VISUALS_DESIGN.md#budgets).

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
  crystal of the Oak Staff, also has a flat Emission Color and Emission
  Strength; the export records their product as `emission`, and Unity turns on
  the material's emission in that color. A `static` or `skinned` asset may
  instead feed the Base Color straight from one Image Texture whose image is
  packed into the `.blend` file, such as a painted body or a hand-painted prop,
  read through the mesh's active UV map, which every mesh with that material
  must have.
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
  of every body, and every body has `UVMap_Paint`. Every body's image is the
  move of the canvas's paint to that body, cropped to its paint, with its place
  in the custom properties `rect` and `layout` and the canvas's roughness and
  metallic, so the file shows what the game will. A file that a painter changed
  without the sync stops with `is not the move of`.

The rules of the garment cage, garment templates, and garment textures are with
[Garment Cage](#garment-cage), [Garments](#garments), and
[Garment Textures](#garment-textures).

Modeling conventions: one Blender unit is one meter, Z is up, and the ground is
at Z = 0; rocks and similar props may sink a few centimeters into it. The front
of an asset faces Blender's **+Y** axis, so Blender's Back view
(`Ctrl+Numpad 1`) shows the front. Blender +Y becomes Unity +Z, the forward
direction of an actor at yaw 0, Blender +X stays Unity +X, and Blender +Z
becomes Unity +Y. A model made in Blender's usual orientation, facing -Y, is
turned 180 degrees around Z with the rotation applied before review.

## Export Settings

`tools/art/blender/art_pipeline/export.py` writes FBX with fixed settings: the
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
`tools/art/ArtPipelineCommon.ps1` runs Blender with `PYTHONHASHSEED=0` and
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
  default muscle limits, twist, and stretch, when its skeleton maps human bones,
  such as `Humanoid`. A skeleton without human bones, such as `Quadruped` or
  `Bow`, gets a Generic avatar created from the model instead, without a root
  motion node, so its clips turn its bones and the game moves the model.
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
at the start of `Death`, which shifts every clip on every body, so the feet
stand wide and slide. So the library's avatar is made on the pose that the
avatar of its `rig`, whose armature the library's armature is, was made on: the
rig's rest pose with the arms raised to a T-pose, as Unity makes a body's
avatar. The rig then plays every clip exactly as the library's own armature has
it, and a new import of the rig's model imports the library again.

A garment's model gets a generic rig without an avatar of its own and without
clips, because the character it is put on animates it: its armature is the
body's, and the game moves it with the character's skeleton. `Optimize Game
Objects` is off and its mesh is readable, because the game combines it with the
visible body parts.

Every PNG below `Assets/Art/Textures` imports as an sRGB texture with mipmaps,
without alpha, not readable, clamped, trilinear, at most 2048 px, with high
quality compression. The images of paint layers and garments, below
`Textures/Paint` and `Textures/Garments`, keep their alpha, but a garment's dye
mask, `T_Garment_<Name>_Dye`, imports without alpha and as linear data, not
sRGB, because the shader reads how far each channel dyes as it is stored. A
paint layer's image keeps its size, up to 4096 px and also when that is not a
power of two, because it is cropped to its paint in a body's paint layout: the
size of the skin texture, 4096 by 2048 texels for the human bodies' `M_Body`, and
twice as wide for a body that mirrors its skin texture.

`MMORPG > Tools > Art Pipeline > Update Appearance Catalogs` writes the
appearance catalog of every exported skinned model with `variants` or a paint
layer or a garment that fits it, as
`Assets/Art/Appearance/<Model>_Appearance.asset`, when it is missing or differs;
see [Appearance Layers](#appearance-layers). For a body with an editable mesh,
the catalog maps every triangle of its parts to the face of the editable mesh it
comes from, every garment's triangles to their underlay, and the faces each
garment hides of every template worn under it to that template's triangles:
`ArtTopologyMaps`
finds each Unity vertex at the position of a vertex of the exported topology,
Blender's (x, y, z) being Unity's (x, z, y), and each triangle in the face that
holds its three corners, so the order of Unity's vertices and triangles does not
matter. Where several vertices lie at one place, such as where the lips meet or
where a hood's collar meets the band turned down over it, the faces of a
vertex's triangles tell which one it is. A triangle that several faces hold goes
to the one that faces its way, or else to the one that holds the nearest vertex
of each of its corners. `ArtAppearanceCatalogTests` fails when a catalog differs
from what the command writes from `asset.json`, or when a face of an editable
body is not drawn by its size less two triangles.

A catalog holds data and addresses, not content, so a client loads only the looks
in view; see [Body Content](UNITY_CLIENT_ARCHITECTURE.md#body-content). The same
command writes the content they address and its entries:

- a `BodyPieceContent` per garment and body,
  `Assets/Content/Bodies/Garments/<Model>/<Garment>.asset`, with the template's
  model for that body and the texture's material, addressed
  `Bodies/Garments/<Model>/<Garment>.asset`;
- a `BodyPieceContent` per attachment,
  `Assets/Content/Bodies/Attachments/<Attachment>.asset`, with its model and the
  clips of a skinned one, addressed `Bodies/Attachments/<Attachment>.asset`, and
  the local rotation each clip leaves every bone it turns in at its end, sampled
  on a copy of the model in the Editor, since a player samples no clip outside an
  Animator: the bow's limbs and tips at the end of `Draw`;
- the image of every paint layer and of every option other than a material's own
  image as an entry of its own, addressed `Bodies/Paints/<file>` and
  `Bodies/Options/<file>`;
- these as the entries of the Addressables group `Body Content`, one bundle each,
  and every asset two bundles would otherwise both hold, such as a garment
  texture's material that both bodies draw with, a template model that several
  textures share, the template materials, the garment shader, and URP Lit, as an
  entry of `Body Content Shared`, one bundle each; the bundles of the World scenes
  and the other groups count, and an asset that only one shared entry holds stays
  in that entry's bundle.

It removes the content assets and entries that no catalog names, writes only what
differs, so a second run changes nothing, and uses the Addressables Editor API
for every entry. A new garment, texture, paint layer, or attachment gets its
content and entries with the catalogs after its export, without a manual step.
`ArtAppearanceCatalogTests` also fails when a content asset or an entry differs
from what the command writes or when a second run would write anything, and
`BodyContentValidationTests` when an address does not load content of its type,
when the player prefabs or a World scene hold a garment's or an attachment's
model, material, or image, or when two bundles share an asset that is no
entry.

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
`Assets/Art/Materials/Garments`. When a Blender material changes, set the Unity
material from its new record by hand; the tests below compare them.

The material of a garment template or texture draws with the garment shader
`Mmorpg/Garment`, URP Lit with three dye channels, and cuts its texture at alpha
0.5 and draws both faces, so holes, lace, and torn edges show sharp and the
inside of a sleeve is drawn. Its forward pass turns the normal on the back faces
in `GarmentForwardPass.hlsl`, so the inside of a cloak or a skirt takes the
light that falls on it instead of the light of its outside. Like the skin shader
of [Paint Layers](#in-unity), it is a copy of URP's `Lit.shader` and
`LitInput.hlsl` in which only the include, the dye properties `_DyeMask`,
`_DyeColor1` to `_DyeColor3`, and `_DyeCount`, the dyeing of the albedo, and the
forward pass's fragment differ; when URP is upgraded, it is made again from the
new files with the same changes.

`ArtExportFollowUp.Apply` is the follow-up of an export: it refreshes the asset
database, so the exported files are imported, creates the missing materials,
gives the garment materials their dyes, updates the appearance catalogs with
their content and Addressables entries, writes the
[cloth fixtures](#cloth-parity) when they changed, and saves the assets. It
writes only what differs, so a run with nothing to do changes no file. When a
step throws, what the steps before it wrote is saved, and the exception
reaches the caller. An open Editor runs the light part of it by itself once
after Unity imports exported models, their data, or textures, when the Editor
next updates, also in Play Mode: the materials, the dyes, and the catalogs,
saved the same way, without a forced refresh and without capturing the cloth,
so an export reaches the game without a menu command, also when only a
garment's masks change. The cloth fixtures are written by `Update-UnityArt.ps1`,
`Build-Art.ps1`, which runs it, or the menu. Any other batch-mode run, such as
the CI tests, leaves the committed materials and catalogs as they are.

Without a focused Editor, run it from the repository root:

```powershell
.\tools\art\Update-UnityArt.ps1
```

When an Editor has this checkout's project open, the script runs `Apply` there
through the Unity CLI, after a refresh and a compile check; otherwise it starts
Unity headless with `-executeMethod
Mmorpg.Client.Editor.ArtExportFollowUp.ApplyFromCommandLine`, at most
`-TimeoutMinutes` (30 by default), with its log in
`artifacts/art-build/unity/follow-up.log`. Expected result: a line per thing it
wrote, or `The materials, appearance catalogs, Addressables entries, and cloth
fixtures are up to date.`, and `Result: the follow-up ran in a headless Unity in
<n> s.`, exit code 0; a failure prints the lines of the log that say why, such as
the exception of the step that failed or a compile error, with exit code 1, and
exit code 2 means it could not run, such as while another Unity process has the
project open. `git status` lists only what the follow-up wrote.

The Addressables settings keep a hash of themselves in the serialized field
`m_currentHash` of `AddressableAssetSettings.asset`. Their own `SetDirty`
empties it, and the first read computes it again, as finding the entries of the
body content does, and the asset is saved with whatever the field holds. The
committed asset holds it empty, so settings marked dirty in another way, such as
with `EditorUtility.SetDirty`, after the hash was read would be saved with the
hash although no setting changed, the change a headless Unity run can leave in
that file. When the settings are about to be saved and the saved asset holds the
hash empty, `Apply` empties the field again, which is right whether a setting
changed or not, and says so in a line of its own.

`ArtModelImportTests` fails when a model has other import settings than its
kind needs, embeds a material because Unity has no material of that name, uses a
material outside `Assets/Art/Materials`, or has a model root or Blender root
object with a position, rotation, or scale. It also reads every `asset.json`
and fails when the model renders with a material that has no export record, or
a record names a material the model does not use, or a Unity material's
`_BaseColor`, `_Metallic`, `_Smoothness`, or `_BaseMap` texture differs from the
approved Blender material recorded at export, so the game shows the colors and
the painted textures the reviewer approved. For a skinned model and an animation
library it fails when the avatar is not a valid Humanoid, or a valid Generic
avatar for a skeleton without human bones, a human bone maps to another bone
than the skeleton definition says, a bone or socket is missing or has another
parent, or a clip is missing, has another length, or loops differently than
recorded. For an animation library it also fails when its avatar is not made on
the pose of its rig's avatar, or when the rig's model shows a clip with the
hips, the head, a foot, or a hand more than 2 mm from where the library's own
armature has it. For a garment it fails when a model lacks its body's armature
or a skeleton bone, has another renderer than `<Name>_<Label>`, a vertex without
weights or with more than four bones, or a bone whose bind pose differs from the
body's, when its material does not cut at alpha 0.5 from both sides, and when a
garment's data or a garment texture's masks are missing, belong to another
garment or body, or hold a face or vertex out of range, an unsorted list, or a
pull-in beyond 3 mm. `ArtAppearanceCatalogTests` fails when a body's garment
lacks its template or does not draw with its texture's material. A texture below
`Assets/Art/Textures` without an export record or with other import settings
fails as well. `Verify-ArtAssets.ps1` rejects an export record with a missing,
malformed, or duplicate material, texture, or clip entry.

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
remote player prefabs show as `PlayerVisual/Body`. Each adds the Animator with
`Assets/Art/Animation/HumanLocomotion.controller`, the locomotion driver, the
body's look with its skin materials and its model's appearance catalog, and the
cloth chain solver. `Assets/Content/Player/PlayerBodyModels.asset` lists them as
the body models `Human_Male`, the default, and `Human_Female`, and a player
prefab puts in the body of its model when it spawns; see
[Player Bodies](UNITY_CLIENT_ARCHITECTURE.md#player-bodies). A new body model is
a new Prefab Variant with the same components, an entry in that list, and a body
model with its options in `content/characters/appearance.json`, which
`CharacterLookTests` holds to the list and the catalogs. `PlayerBodyAssetTests`
checks both variants, the controller, the models, and both player prefabs.

A Mob with a creature model of its own is built by
`MMORPG > Tools > Art Pipeline > Build Creature Presentations`, which
`CreaturePresentationBuilder` also runs in batch mode with
`-executeMethod Mmorpg.Client.Editor.CreaturePresentationBuilder.Build`. It
builds the creature's Animator controller from the clips of its model, such as
`Assets/Art/Animation/WolfLocomotion.controller`, and rebuilds each world actor
prefab of the creature in place, so it keeps its GUID and Addressables entry. It
writes the controller and a prefab only when they differ from what it builds, so
a build without a change rewrites nothing, which `CreaturePresentationTests`
checks. The prefab is not a Prefab Variant of the model, because its root keeps
the actor's own scale while the model may be larger: the model is a nested
prefab instance in the child `Model`, with the controller on its Animator, no
root motion, and transforms culled off screen, and a `CreatureAnimator` on the
root drives it; see
[Creature Animation](UNITY_CLIENT_ARCHITECTURE.md#creature-animation).
`Mob_FeralWolf.prefab` shows the wolf at its size in `M_Wolf`, and
`Mob_FeralAlpha.prefab` at 1.3 times in `M_Mob_FeralAlpha` below
`Assets/Art/Materials/WorldActors`, `M_Wolf`'s texture with a darker
`_BaseColor`, the one material override a creature prefab may have. Run the
command again after an export changes a creature's clips.
`CreaturePresentationTests` checks the controller, both prefabs, the darker
coat, and that the clips' speeds are the wolves' patrol and chase speeds.

### Add A New Asset

A model for the world, such as a resource node or a prop, is a `static` asset
with the root objects the game code expects.

1. Create the asset with its root objects and its triangle budget, the upper end
   of its budget in [Art Direction](#art-direction-for-models-and-textures):

   ```powershell
   .\tools\art\New-ArtAsset.ps1 -Kind static -Asset art/WorldActors/Resource_CopperVein -Roots Available, Depleted -TriangleBudget 2200
   ```

   Expected result: `Created art/WorldActors/Resource_CopperVein with asset.json
   and Resource_CopperVein.blend.`, the command that opens it in Blender, and
   the next step. `asset.json` names the target
   `src/Client/Assets/Art/Models/WorldActors/Resource_CopperVein.fbx`, and the
   file holds the `Export` collection with an empty root object per name at the
   origin. An existing folder stops the command with `... exists already; a new
   asset needs a folder of its own.`
2. Open the file in Blender and model below the roots by the
   [modeling rules](#modeling-rules), with the front toward Blender's +Y, then
   unwrap the model and paint its texture by hand. Give every material a name
   that starts with `M_` and that no other asset uses, such as
   `M_Resource_CopperVein`, and build it as [Blender Rules](#blender-rules)
   says: one Principled BSDF whose Base Color comes from one packed Image
   Texture, with a flat Metallic and Roughness. Save.
3. Render the review sheet, review, approve, export, and verify as in
   [Review, Approve, And Export An Asset](#review-approve-and-export-an-asset).
   When Unity gets focus, it imports the model and creates one URP Lit material
   per exported material below `Assets/Art/Materials/<Category>`, with the
   `baseColor`, `baseMap`, `metallic`, and `smoothness` that the export recorded
   under `export.materials` in `asset.json`. Expected result: the Console lists
   the created materials and the model renders with them.
   `MMORPG > Tools > Art Pipeline > Create Missing Materials` does the same by
   hand.
4. In Unity, select the imported model in `Assets/Art/Models/<Category>`, choose
   `Assets > Create > Prefab Variant`, move the variant to its prefab folder, and
   reference it from the presentation registry, as [Prefabs](#prefabs)
   describes. To replace an existing placeholder prefab and keep its GUID and
   registry entry, save an instance of the model over the old prefab path with
   `PrefabUtility.SaveAsPrefabAsset` through `unity command run_script`; see
   [Unity CLI automation](LOCAL_DEVELOPMENT.md#unity-editor-automation-with-unity-cli).
   Then run the tests that check the import and the prefabs:

   ```powershell
   .\tools\Run-UnityEditorTests.ps1 -Mode EditMode -Filter ArtModelImportTests,AuthoredGameplayAssetTests
   ```

   Expected result: both fixtures pass.

## Skeletons

A skeleton definition in `art/Skeletons/<Name>.json` lists the bones that every
skinned asset of that skeleton has, so equipment, animations, and game code can
rely on them across bodies. Each bone has a `name`, its `parent` (null for the
one root), the Unity `human` bone it maps to (a `HumanBodyBones` name), whether
it should `deform` the skin, and for a bone of a cloth chain its `chain`. A
skeleton with eyelids has an `eyelids` section.

`Humanoid` is the skeleton of the playable bodies: 55 deforming body bones, from
`Hips` through `Spine`, `Chest`, `UpperChest`, `Neck`, and `Head` with `Jaw` and
the eyes `Eye_L` and `Eye_R`, a `Shoulder`, `UpperArm`, `LowerArm`, and `Hand`
with three bones per finger and thumb on each side, and an `UpperLeg`,
`LowerLeg`, `Foot`, and `Toes` on each side, all mapped to the Unity Humanoid
avatar. Three sockets deform nothing and carry equipment: `Socket_MainHand` on
the right hand, `Socket_OffHand` on the left, and `Socket_Back` on the upper
chest. In Unity a socket's local Z is the forward direction of the item it holds
and its local Y is up, which are Blender's bone Y and Z axes. The character
faces +Y and its right is +X, so the `_R` bones sit at +X. Limbs bend about
their bone X axis, arms rise about Z, and every bone twists about Y.

Fifty-nine more bones form the cloth chains that loose garments hang on, outside
the Humanoid avatar, so Humanoid clips never move them; the body is not weighted
to them, and the runtime solver moves them for the garments that use them, as
[Cloth Chains](EQUIPMENT_VISUALS_DESIGN.md#cloth-chains) describes:

| Chain | Bones | Parent | Placed | For |
| --- | --- | --- | --- | --- |
| `skirt` | `SkirtFront1` to `SkirtFront4`, `SkirtBack1` to `SkirtBack4`, and on each side, with `_L` and `_R`, `SkirtFrontSide`, `SkirtSide`, and `SkirtBackSide` 1 to 4: eight chains of four | `Hips` | On a ring just above the hip joints, 1 cm out from the body, hanging to the ankles. | skirts, robes, coat tails, tabards |
| `cloak` | `CloakCenter1` to `CloakCenter5`, and on each side, with `_L` and `_R`, `CloakInner` and `CloakOuter` 1 to 5: five chains of five | `UpperChest` | Across the shoulder blades, 4 cm below the base of the neck and 1.5 cm behind the body, hanging to the middle of the calves, halfway from the knees to the ankles. | cloaks and capes |
| `hood` | `Hood1`, `Hood2` | `Head` | Behind the head at eye height, stepping down and back. | the back and tip of a hood |

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
chain, in the order their cloth lies on the body, its parent, its bones, the
part of the legs beside each bone, its profile's margin and slack, the kind it
lies over, what it collides with, the bone its frame is, and its chains with
their angles around the hips. `ClothPoseTests` checks the client's constants
against it, and `cloth_core.py` reads it; see [Cloth Parity](#cloth-parity).

`Humanoid` also has four eyelid bones, `EyelidUpper_L`, `EyelidLower_L`,
`EyelidUpper_R`, and `EyelidLower_R`, deforming children of `Head` beside the
eyes and outside the Humanoid avatar. Each sits at its eyeball's centre and
points forward with its Z axis up, as the eye bones do, so it turns its lid
about the eye's centre around its X axis. The `eyelids` section names per eye
its eye bone and its two lid bones, and the turns about X that close the lids,
`upperClosedDegrees` -46 for the upper lids and `lowerClosedDegrees` 11 for the
lower ones, just more than the male's lids need: the rig weights each body's
lids so they meet at these turns, and a share of them closes the eyes that far;
see [Rig The Human Base Characters](#rig-the-human-base-characters).
[Eyelids](UNITY_CLIENT_ARCHITECTURE.md#eyelids) describes how the client turns
the lid bones, and `BodyEyelidsTests` holds the client's copy of the section to
this file.

`Quadruped` is the skeleton of four-legged creatures, such as the wolf: 30
deforming bones outside any Unity avatar, so its assets import with a Generic
rig and their own clips. `Hips` is the root, with `Spine`, `Chest`, `Neck`, and
`Head` along the back, `Jaw` and the ears `Ear_L` and `Ear_R` on the head, and
the tail `Tail1` to `Tail4` behind the hips. Each front leg runs from `Chest`
through `Shoulder`, `UpperArm`, `Forearm`, and `FrontFoot` to `FrontToes`, and
each hind leg from `Hips` through `Thigh`, `Shin`, and `HindFoot` to
`HindToes`. It has no sockets yet.

`Bow` is the skeleton of a bow, a skinned attachment whose armature is its root
object `Grip`: 8 deforming bones outside any Unity avatar, so it imports with a
Generic rig. `Bow_Riser` is the root, with `Bow_LimbUpper` and its
`Bow_TipUpper`, which carries `Bow_StringUpper`, and the same three bones below,
`Bow_LimbLower`, `Bow_TipLower`, and `Bow_StringLower`, and `Bow_Nock`. Each
straight half of the string hangs on its string bone from where it leaves the
recurve toward the nocking point, which lies on `Bow_Nock`; its clip `Draw`
turns only the four limb bones, from brace to full draw. The game aims each
string bone at the nock and stretches it along its length, and moves the nock
with the drawing hand; see [Skinned Attachments](#skinned-attachments).

## Human Base Characters

`art/Characters/Human_Male` and `art/Characters/Human_Female` are the base bodies
of the playable humans, rigged with the `Humanoid` skeleton. Their clips come
from the [animation library](#animation-libraries) `art/Animations/Human_Actions`.
Hair, clothes, and equipment are [garments](#garments) and
[attachments](#attachments) on top of them, and faces are painted on them. Both
are built on the realistic bodies and the Generic Topology head of the
[Human Base Meshes](https://www.blender.org/download/demo-files/) bundle v1.4.1
by Blender Studio (CC0), by the scripts in `tools/art/blender/human_build`; see
[Rebuild The Human Base Characters](#rebuild-the-human-base-characters). Their
files are the source: a person edits them in Blender, and
`tools/art/Sync-HumanBase.ps1` keeps everything that follows from them up to
date.

Each file holds one character:

- The armature (`Male` or `Female`) at the origin, the only root object of the
  `Export` collection, with the bones of the `Humanoid` skeleton. It carries
  landmarks as custom properties, such as the eyes, lips, wrists, finger tips,
  and toes, which placing the joints again uses and attachments and paint
  layers may use.
- The source body `<Label>_Body` in the `Source` collection, bound to the
  armature: the whole body as one mesh, the surface a person edits, with smooth
  shading, the face attribute `zone` that names the body part of every face, and
  the skin weights. It shows in the viewport, and renders and the export leave
  it out.
- Ten body parts in the `Export` collection, named `<Label>_<Zone>`: `Head`,
  `Neck`, `Torso`, `Pelvis`, `UpperArms`, `Forearms`, `Hands`, `Thighs`,
  `Shins`, and `Feet`, so equipment can hide whole parts. The sync writes them
  from the source body, with its corner normals as custom normals, so they meet
  without visible seams, with its weights, and with the paint UV map
  `UVMap_Paint`, the source body's `UVMap`: no island of the bodies is mirrored
  and no two share texels, so [paint layers](#paint-layers) can differ between
  the sides. They are hidden in the viewport and never edited by hand; the
  approval and the export refuse parts that differ from the source body.
- Two eyeballs (`<Label>_Eye_L` and `_R`) for gaze animation, and upper and lower
  teeth and a tongue inside a closed mouth cavity for jaw animation, edited where
  they are.
- 15,084 triangles with the eyeballs, teeth, and tongue. Both bodies are
  exactly mirror symmetric and have identical topology, so paint moves from one
  body to the other exactly, face by face.
- Smooth shading and textures: `M_Body_<Label>` uses the 4096 x 2048 px body
  texture `T_Body_<Label>`, plain skin under the underwear, which is paint of
  its own, `M_Face_<Label>` a 2048 px face texture, `M_Eye_<Label>` a 512 px
  iris texture, and `M_Teeth_<Label>` and `M_Tongue_<Label>` flat colors. The
  skin is plain, in one colour, until it is painted. The face and eye variants
  for character creation are packed into the file and exported
  as [variants](#appearance-layers): `T_Face_Male_Stubble`, `T_Face_Male_Clean`,
  `T_Eye_Male_Teal`, and `T_Eye_Male_Brown` for the male,
  `T_Face_Female_Blush`, `T_Face_Female_Freckles`, `T_Eye_Female_Teal`, and
  `T_Eye_Female_Hazel` for the female. Another face is the face or eye material
  with another image.

An edit keeps both bodies alike: the same change of topology on both, so they
keep identical topology, made with X mirror, so they stay symmetric, and with
every new face in the zone of its neighbours. Moving vertices changes neither
the topology nor the weights; new vertices need weights, painted by hand on the
source body or solved with `-Weights`.

`tools/art/Sync-HumanBase.ps1` brings both characters up to date after an edit,
one Blender process per step:

1. `sync`: the body parts of each character from its source body. With
   `-Weights` the skin weights are first solved again from the joints of the
   rig, which replaces weights painted by hand, and with `-Rig` the joints are
   first placed again from the model. A new rig needs a new sync and review of
   the animation libraries keyed on it.
2. `check-rig`: the step changes no mesh at rest, the art pipeline's rig rules
   hold, and the weights are mirror symmetric. On a skeleton with eyelids it
   closes the lids by the skeleton's turns: at each place across the eye the
   lids' inner edges lie at most 0.3 mm apart around the eyeball and 0.8 mm
   toward its axis, and no skin of a lid lies more than 0.3 mm inside its
   eyeball. It turns the head 40 degrees, nods it 25, and leans it back 25 on
   the neck: the skin at the face's landmarks, from the forehead over the eyes'
   rims and the lips to the chin, moves at most 0.3 mm against the skull.
3. `check-mesh`, `check-seams`, and `check-parts`: no open or over-shared edges,
   tiny faces, slivers, or loose pieces on the source bodies, exact mirror
   symmetry, identical topology of both bodies and of every part, at most 15,400
   triangles per character, the upper end of its budget of about 14,000,
   matching cuts and normals between the parts, no texel of `UVMap_Paint` that
   both sides of a body share, and a report of the color difference across the
   UV seams.
4. `compare`: the content of each updated character against the committed file
   (objects with their modifiers, constraints, and visibility, the evaluated
   meshes with UVs, normals, and edge flags, vertex weights, the armature with
   its bones and pose, materials with their node links, textures with whether
   they are packed, and custom properties). Blender's files differ in their
   bytes from run to run, so the script replaces a committed file only when its
   content changed, and an unchanged file stays as it is. The script then
   records the content of both files and says which approvals hold, which
   depends only on the [content of each stage](#content-of-a-stage).
5. The review files `Human_Base.blend`, both characters side by side, and
   `Human_Base_Rig.blend`, with the test poses as an animation that ends by
   closing the eyes on a skeleton with eyelids, and the review
   sheets of the models, such as the faces sheet `Human_Base_faces.png`, and of
   the rigs, such as the cloth chains on `<Asset>_rig_chains.png`, the
   shoulders through their reach on `<Asset>_rig_arms.png`, and the hips
   through theirs on `<Asset>_rig_hips.png`. All sheets are lit by an even sky
   and a key light without shadows, so the forms of a body in one colour show;
   the light follows the camera, so every side is lit alike. The models' sheets
   stand the two characters 0.8 m to each side of the middle and show one alone
   in every close-up, and their mouth sheet opens the mouth by the jaw bone, a
   little, well open from three sides, and wide open. The rigs' sheets show the
   whole body, the mesh a person edits: Blender shades the parts of a posed body
   with a seam at every cut between two parts, which the game does not show. The
   face sheet opens the jaw, turns the eyes, turns, nods, and leans back the
   head on the neck, and closes the lids of a skeleton with eyelids.

Placing the joints puts every joint where the model has it: the hips from the
crotch and the leg cut, the spine at fixed shares between the hips and the neck,
the shoulder in the deltoid cap found by ray casts from where the rig had it,
the elbows, knees, wrists, and ankles at the cuts between the parts, the finger
and toe joints from rings of the digit meshes, the jaw hinge from the lip line
landmarks, and the cloth chains from the body's surface, so they fit every body.
The sync adds bones the skeleton has and a rig lacks, such as new cloth chains,
placed by these rules, and leaves every other bone as it is. Bone heat gives the
first weights, limited per body zone and per digit, and anatomical rules shape
them where it matters: the shoulder blends over the deltoid and leaves the chest
in place; the thigh takes the skin below a boundary that runs through the groin
crease, over the hip, along the gluteal fold, and through the crotch, with wide
blends over the buttock and the underwear line and a radial fade at the crotch,
so a raised thigh leaves no notch; and the knee and elbow blend across the
joint. The jaw takes the lower face, the eyelids' bones take the lids on a
skeleton that has them, the eyes, the upper teeth, the lower teeth, and the
tongue are rigid on their bones, and the weights are mirrored so both sides
match exactly. Solving the weights again on an unchanged character gives the
same weights. The tools tell a body's vertices apart by their places, and two
vertices of one part at one place, such as the bodies' lips where they meet, by
their order.

The test poses of the rig review turn the bones from the bind pose. Their upper
arms were made for arms that hang about 68 degrees below the horizontal, as the
bodies before the rebuild had them; on a rig whose arms hang more than 10
degrees otherwise, such as the bodies' at 45 degrees, a pose that turns the
upper arms first turns them to that angle, so it shows the same arms on every
body. With
the arms overhead the collarbones rise 38 degrees, about a third of the way, as
a shoulder girdle does; with them low, the arms would rise out of a flat shelf
beside the neck.

### Edit The Human Base Characters

1. Open a character in Blender:

   ```powershell
   & $env:BLENDER_PATH art\Characters\Human_Male\Human_Male.blend
   ```

   Expected result: the viewport shows `Male_Body` from the `Source` collection,
   the eyes, teeth, and tongue, and the armature `Male`; the ten parts
   `Male_Head` to `Male_Feet` in `Export > Male` are hidden.
2. Edit `Male_Body`: shape it in Edit Mode with X mirror on, paint `T_Body_Male`
   in Texture Paint, or paint its weights in Weight Paint. Make every change of
   topology on `Female_Body` in `Human_Female.blend` too. Never edit the hidden
   parts. Save.
3. Write the parts from the bodies and check both characters:

   ```powershell
   .\tools\art\Sync-HumanBase.ps1
   ```

   Add `-Weights` to solve the skin weights again from the joints, for example
   after adding vertices without weights; it replaces weights painted by hand.
   Add `-Rig` to place the joints again from the model first, after a change of
   the proportions. `-WorkDirectory` puts the review files elsewhere than
   `artifacts/human-base`.

   Expected result: one line per step with its time, about two minutes in all,
   then the paths of the review files `artifacts\human-base\Human_Base.blend`
   and `artifacts\human-base\Human_Base_Rig.blend`, the review sheets in
   `artifacts\human-base\review`, and `artifacts\human-base\report.txt`, and for
   each asset `Human_Male: unchanged, the committed file is kept.` or
   `Human_Male: updated.` with a line per stage, such as `Human_Male: the 'model'
   approval holds.` or `Human_Male: the 'rig' content changed after its approval;
   review it and approve it again ...`: an edit of only the weights leaves the
   `model` approval holding. A step that finds a broken rule stops the script
   and names the rule and the step's log, such as `bodies differ in topology`
   or `Male_Body: mirror error ...`.
4. Review the sheets and the review files; press Space in Blender to play the
   test poses:

   ```powershell
   & $env:BLENDER_PATH artifacts\human-base\Human_Base_Rig.blend
   ```

   When the reviewer approves the changed characters, record both stages in
   order, then export the characters and the animation library and verify:

   ```powershell
   foreach ($asset in "art/Characters/Human_Male", "art/Characters/Human_Female") {
       .\tools\art\Approve-ArtAsset.ps1 -Asset $asset -Stage model
       .\tools\art\Approve-ArtAsset.ps1 -Asset $asset -Stage rig
   }
   .\tools\art\Export-ArtAsset.ps1 -Asset art/Characters/Human_Male, art/Characters/Human_Female, art/Animations/Human_Actions
   .\tools\art\Verify-ArtAssets.ps1
   ```

   Expected result: `Approved the 'model' stage of Human_Male (triangles=15084).`
   and the same for `rig` and `Human_Female`, `Exported Human_Male to
   src/Client/Assets/Art/Models/Characters/Human_Male.fbx (triangles=15084).` and
   the same for the female, `Exported Human_Actions to
   src/Client/Assets/Art/Models/Animations/Human_Actions.fbx
   (clips=Death,Idle,Walk,...).`, and `Verified <n> art asset(s).` The library is
   exported with the characters because it records the content of its rig;
   `Verify-ArtAssets.ps1` otherwise stops with `was exported against other
   content of its rig`. After `-Rig`, sync the library first and review its clips
   on the new rig, as [Make A Clip](#make-a-clip) describes.

The export also writes the textures below
`src/Client/Assets/Art/Textures/Characters`, the face and eye variants that
`variants` in `asset.json` names among them. When Unity gets focus it imports the
models, creates any material the export added, and updates the appearance
catalogs. Expected result: the Console lists created materials and each changed
catalog with its slots, such as `Eye (Teal, Brown), Face (Stubble, Clean)`, and
nothing when nothing changed. Commit the `.blend` files, `asset.json`, the FBX
and texture files, the catalogs, and their `.meta` files together. An export of
unchanged sources leaves every file as it is.

An edit that changes what the garment cage, a paint layer, or a garment reads
from a body changes the body's fingerprints, and `Verify-ArtAssets.ps1` then
names every asset that must follow, with the script to run: the cage, as
[Fit The Garment Cage](#fit-the-garment-cage) describes, every paint layer of
the body ([Make A Paint Layer](#make-a-paint-layer)), every garment template in
the order of its layers ([Make A Garment](#make-a-garment)), and their textures
([Make A Garment Texture](#make-a-garment-texture)). A change of only a body's
images changes none of them.

### Add A Face Or Eye Variant

A face or an eye color is another image of a body's face or eye material; see
[Appearance Layers](#appearance-layers).

1. Open the body, such as `art\Characters\Human_Male\Human_Male.blend`, in
   Blender. In the Image Editor, duplicate the material's own image, such as
   `T_Face_Male_Stubble`, name the copy `T_Face_Male_<Option>`, such as
   `T_Face_Male_Bearded`, paint it in Texture Paint on the face with the copy
   shown, then show the material's own image again, pack the copy into the file
   (`Image > Pack`), and save.
2. Add the image to the material's entry in `variants` in `asset.json`, such as
   `{ "material": "M_Face_Male", "images": ["T_Face_Male_Clean",
   "T_Face_Male_Bearded"] }`.
3. Run `.\tools\art\Sync-HumanBase.ps1`. Expected result: every check passes,
   and the faces sheet `artifacts\human-base\review\Human_Base_faces.png` shows
   the new option next to the others.
4. When the reviewer approves, approve the `model` and `rig` stages of the body,
   export it with the animation library, and verify, as in step 4 of
   [Edit The Human Base Characters](#edit-the-human-base-characters). Expected
   result: the export writes `T_Face_Male_Bearded.png` below
   `src/Client/Assets/Art/Textures/Characters`, and when Unity gets focus the
   Console lists the catalog with `Face (Stubble, Clean, Bearded)`. In the game,
   the `Face` row of the Admin menu's `Look` tab (F3) offers the new face.
5. Add the option to the body model's `face` or `eyes` options in
   `content/characters/appearance.json`, named and ordered as the catalog lists
   them, as [Character Appearance](../content/README.md#character-appearance)
   describes.
   Expected result: `CharacterLookTests`, which requires the content to offer
   exactly what every body's catalog has, passes, and character creation offers
   the new face.

### Rebuild The Human Base Characters

The human base bodies are built from scratch, with the method of the
[Modeling Rules](#modeling-rules), by the scripts in
`tools/art/blender/human_build`, which place every vertex and face on purpose.
They write the files of `art/Characters/Human_Male` and
`art/Characters/Human_Female` at the `model` stage, without a rig, and
`Sync-HumanBase.ps1 -Rig` rigs them on the `Humanoid` skeleton; see
[Rig The Human Base Characters](#rig-the-human-base-characters).

The reference is the bundle the bodies have always come from, the realistic
male and female of Blender Studio's Human Base Meshes v1.4.1 (CC0), kept outside
Git in `artifacts/art-reference/human-base-meshes-v1.4.1` of the main checkout;
`reference.py` names its SHA-256. Both references are shaped as the reviewer
asked for, by smooth, mirrored fields before the net is laid onto it:

- **The female figure.** Hips 7.5 % wider, a waist 6.5 % narrower, and a bust
  that sits close together, a little larger and round: each breast swells
  forward 22 mm, flat over its middle so it moves forward whole, swells 5 mm
  out in every direction from a point inside it, which rounds its lower half
  and its sides, and lies 14 mm nearer the middle, at 1.70 m. The bust measures
  95 cm around against 76 cm under the breasts.
- **The male's groin is smooth.** The bundle's male has a bulge in the groin that
  stands out in the game. The two bodies of the bundle are sculpted on one net,
  so his groin takes the female's smooth form there, laid onto his body by the
  move, turn, and scale that lay the edge of that patch onto his, fading to his
  own form at the patch's edge. A fifth of his own form stays, a hint of a male
  groin: the front of the centre line now rises evenly from the crotch, where
  it stood out 4 mm beyond the belly above it.
- **Both bodies fuller.** The bundle's bodies are slim and read as thin in the
  game. Each limb, the round of the shoulder, the chest, and the neck grow by a
  share of their width where each is widest, fading out toward the joints, so
  the elbows, the wrists, the knees, the ankles, the hands, the feet, and the
  head keep their size. The limbs grow about their own lines, and the chest and
  the neck about the trunk's middle. The round of the shoulder grows away from
  the shoulder joint: out to the side, to the front and the back, and down
  along the arm, but neither upward, where the bone lies under the skin, nor
  toward the neck, so the line of the shoulder stays. The female grows most in
  her arms, which were the thinnest part of her: her upper arm by 19 %, her
  forearm by 14 %, the round of her shoulder by 18 %, her thigh by 10 %, her
  calf by 9 %, her chest by 3 %, and her neck by 2 %. The male grows by 8 to
  10 % in his limbs, 15 % in the round of his shoulder, 4 % in his chest, and
  5 % in his neck. `FULLER` in `reference.py` holds the shares.
- **The male's waist filled.** His waist narrowed to four fifths of the width
  of his hips and of his chest, and his side swelled out again fast below the
  armpit, so the muscle of his back stood out there as a bulge over a thin
  waist. `reference.py` measures the outline of his trunk and fills it. The
  waist grows 8 % wider, from 31.0 to 33.5 cm. Above it the side is filled
  out to the straight line from the wider waist up to the foot of the chest's
  side, where the opened armpit's own straight line begins, so one even line
  runs from his waist to his armpit. Below the waist the side is filled
  straight down, to where his hips are that wide of their own. Each height
  grows about the trunk's middle by the share that its outline needs, in
  width, and by 0.4 of that share from front to back, so his belly does not
  stand out with it. `WAIST` holds the share; the female's is nothing, and her
  waist is as it was.

The bodies stand in an A-pose. The bundle's bodies stand with their arms
hanging, the line from the shoulder joint to the wrist 19 degrees from straight
down, which leaves the armpit a narrow gap and puts an arm that is raised over
the head 160 degrees from the pose its skin is bound in. So `reference.py` turns
the arms out about the shoulder joints until that line is 45 degrees from
straight down, the middle of what the arms do. It turns the bundle's own coarse
net under the sculpt, so the sculpt's detail follows. Unity raises the arms to
a T-pose itself when it makes a body's avatar, and the import of a clip takes
the difference between two rest poses out of the motion, so neither needs
another pose. The elbows keep the bundle's slight bend, and the legs their
stance.

Each vertex takes a share of the turn, and where the shares lie decides whether
the body looks like one that holds its arms out or like one whose skin was
pulled:

- **The round of the shoulder turns whole.** It is round about the joint, so
  turning it about the joint keeps its shape, where a part of the turn shears
  it flat until the line from the neck slopes straight into the arm.
- **The trunk stays.** The side of the chest from 2.5 cm below the armpit, and
  the chest and the back inside the joint, 6.5 cm inside it at the armpit's
  height and 2 cm over the top of the shoulder, take none of the turn. When
  more of the trunk takes part, its side is drawn out to the arm like a web and
  the armpit lies far out on the arm.
- **The passage between them evens out.** A band over the top of the shoulder,
  the folds of the chest's and the back's muscles, and the hollow of the armpit
  take the share that evens out along the net. Their skin below the joint is
  stretched to several times its width, with the sculpt's folds of a hanging
  arm standing in it as creases, so it is smoothed, most in the middle.

The turn alone leaves the armpit closed. The bundle's arms hang against the
chest, so the arm and the trunk meet where their skins touch, low and out from
the chest, and after the turn that seam is the top of the gap between them, with
the arm grown into the trunk above it. On a body that holds its arms out, the
underside of the arm runs in to the side of the chest, and a hollow lies there
between the fold of the chest's muscle in front and the fold of the back's
muscle behind. So `reference.py` opens the armpit by the measures of a standing
body:

- **Where the armpit lies.** The top of the gap is moved up and in, to 13.5 cm
  below the top of the shoulder and 1 cm out from the foot of the chest's side,
  which is its outline 6 cm below the armpit: 3.3 cm up and 2.8 cm in on the
  male, 2.7 cm up and 3.9 cm in on the female.
- **The side of the chest keeps its place.** Below that foot nothing moves.
  Above it the side comes to run straight from the foot to the new top of the
  gap. Each piece of the body takes a share of the move, and across the line of
  the arm that share is what the measured outline of the chest's side needs to
  land on the straight line. So only what stood out beyond that line is drawn
  in, the skin of the arm that lay against the chest. A move that only fades
  out with the distance draws the whole side of the chest in under the armpit
  and leaves it bulging below.
- **What moves with it.** Along the line of the arm the share falls evenly over
  12 cm, so the underside of the arm is drawn in along itself. The move is
  whole through the whole depth of the body there, so the two folds move as
  they are, and it ends 5 cm behind and in front of that depth.
- **The hollow.** Between the folds the skin is lifted 2.5 cm more, to a hollow
  that runs out along the underside of the arm.
- **What the script refuses.** It measures the least room that the opening
  leaves a piece of the body and refuses under a tenth, where skin would be
  laid over skin. It measures the side of the chest again from below its foot
  up to the armpit and refuses a side that narrows by more than 2 mm on the
  way, which is a bulge under the armpit. Both bodies keep 0.13 of their room
  or more, and neither side narrows at all.

The reference names the opened armpit, and the cage's ring around the shoulder
stands upright in its plane, which lies 3 to 4 cm nearer to the neck than the
shoulder joint. That ring is the border between the torso and the upper arms,
so the upper arms' part begins on the shoulder, inside the joint. A ring that
leaned out to the joint would be the seam of a sleeve, but its plane cuts into
the side of the chest below the armpit, where its section is no ring. The
armpit is the part of the model to look at most closely: from in front, behind,
and below, beside the bundle's own pose, and with the arm raised to the side
and over the head.

What is built:

- **One cage for both bodies.** The coarse cage of the right half, 793 faces, is
  designed once and laid out on each body by measures of that body's own
  reference: rows of sixteen around the trunk, one of them through the navel
  and twice as close over the chest, with the side column at the body's widest
  point up to the navel and making for the armpit above it, as the side seam of
  a shirt does, an arm hole of eight that starts in the top of the armpit's
  hollow and has the lowest points of the two folds as its next corners, with
  four rings fanning under the arm, arms of eight around, legs of ten, hands
  with a ring
  per finger joint, and feet with rings around the corner of the heel and five
  toes of their own, each built like a finger with two rings. It is subdivided
  once, laid onto the reference, relaxed along the surface, and mirrored: 7,292
  faces, and 15,084 triangles with the eyeballs, the teeth, and the tongue.
  Every border between two body parts is a ring of the cage, and both bodies
  have the same faces in the same order.
- **What is finer than the faces.** The subdivided net gets two things that its
  faces are too coarse for. The navel, a dent of about 7 mm that is found on the
  reference, is a pocket at the vertex of the centre line that lies in it: a
  ring on the dent's edge, a ring halfway down, and the vertex in its depth.
  Over the chest a line of vertices beside the centre line parts the faces at
  the centre line in two, for the flat bottom of the valley between the
  breasts, which else is a V of two faces.
- **The head.** The head is the bundle's own head cage, which is the coarse cage
  of the female body's head. The bundle's two bodies are sculpted on one net, so
  a place on her skin is the same feature on his: every vertex of the head cage
  gets on him the place it has on her, on skin that is seen from outside, and
  the loops around the eyes, the mouth, the nose, and the ears lie on the same
  features of both. The cage is a head alone, with a short neck of its own that
  rises behind the chin where the body's throat begins further forward: the
  last two vertices of its centre line under the chin, one of them on the ring
  under the head, would land on the floor of the reference's mouth, so they are
  laid along the skin instead, around the corner under the chin onto the
  throat, at the cage's own spacing. The cage's eyes are too coarse for lids
  that close, so they are built on their own; see the next point. The line
  between the lips is parted into two lips with a mouth
  cavity behind them, and it runs along edges all the way to the mouth's
  corner: the one face with which the cage closes the corner is parted along
  the line into a face of each lip. The cage has no nostrils, which are finer
  than its faces: the two faces under the nose that a nostril lies across are
  opened into a ring of twelve vertices on the sharp edge of the nostril's
  opening, found on the reference as the nose is seen from below. Where an edge
  of those two faces lies nearer to that ring than 0.8 mm, on the nose's wing,
  it moves out along the skin instead of cutting the nostril. The pocket behind
  the ring runs nearly straight in, as a nostril does, so its wall is not seen
  from in front, and it is an island of its own in the UV map.
- **The lids.** The head cage closes each eye with a ring at the edge of its
  opening and one band of faces out to the crease and the cheek, which
  subdividing makes two rings: lids without the thickness of their margin,
  which leave a slit when they close. The band leaves the cage, and `eye.py`
  builds the lids in its place on the subdivided net, ring by ring, on 24
  spokes, half planes through the eyeball's centre that hold the line of
  sight, twelve steps of even length along each lid from one corner to the
  other. The opening's outline is measured first in 96 directions around the
  line of sight, as the spokes see it: its corners are where it reaches
  furthest around the eyeball, beside the nose and on the outer side, where it
  lies further round than the eye seen from in front shows. On each spoke the
  reference's lid is the skin that rays from outside toward the eyeball's
  centre meet first, and the rings lie on it: the inner edge on the eyeball,
  0.4 mm off its surface; the lash line 1.5 degrees further out, where the
  reference's margin turns into the front of the lid, so the margin between
  the two has the reference's thickness, about 1 mm, thinning out over the
  two spokes beside each corner, where the lids meet; two rings evenly on the
  lid; the crease, where the lid has risen 1.2 mm above the lash line and at
  least 8 degrees further out, below the fold of the brow over the upper lid
  and where the lower lid meets the cheek, with the lid's width to it evened
  out with the neighbouring spokes', so the rings on the lid do not wave; and a
  ring between the crease and
  the ring around the hole, laid onto the skin with the rest of the net. That
  ring has two vertices fewer on each lid than the spokes and takes them in
  with a triangle beside each corner, where the lids move least. A spoke whose
  inner edge stands out beyond both its neighbours, where the lids part around
  the eyeball's side in a corner, is laid between them. A pocket behind the
  inner edge holds the eyeball, hidden under the lids. Closed by a turn about
  the eyeball's centre, the lids meet margin to margin and leave no slit.
- **The ears.** The head cage's ear is a box of sixteen faces, too coarse for an
  ear's folds. The ear is taken from the body's own control cage instead, the
  coarse net under the bundle's sculpt, which is built for the folds and has
  even rings of faces on the skin around the ear: 250 of its faces take the
  place of the box and of the 17 faces of the head cage around it, with their
  vertices where the bundle has them. A band of 53 triangles on plain skin joins
  the two: of all ways to close the band, the one whose triangles are least
  thin on both bodies. A face of the control cage that is smaller than a body's
  faces may be loses its shortest edge, and one that is twisted too far to be
  cut into two triangles is evened out when the bodies are assembled. The ear
  and its band, the band around each eye's lids, and each eye's pocket are the
  parts of the net with faces of three corners.
- **Eyeballs, teeth, and tongue** of their own: a ball of the size of the
  reference's eyeball per eye, a band of teeth per jaw, and a tongue. A band of
  teeth is 6 mm thick at the gums and 2.5 mm at its biting edge and 10.5 mm
  high. The upper row stands right behind the inner lips, 6 mm behind the lips'
  front, with its edge 3 mm under the line between the lips, so the teeth show
  under the upper lip when the mouth opens. The lower row's edge lies 1 mm
  under that line, just under the lower lip's, and the row 3.5 mm inside the
  upper row all the way around, so the two pass without touching. The tongue
  fills the lower row: its rounded tip lies behind the lower front teeth and
  its top a little under their edges, so an open mouth shows the edges of the
  lower teeth and the tongue, never the inside of the row. Lengths are at
  1.79 m and follow the body's height.
- **The UV map.** Unwrapped island by island along seams the cage names, never
  projected: the trunk's front and back, the neck opened down the nape, each arm
  and leg opened along its inner line, each hand as a back and a palm, each foot
  as a top and a sole, and the head as one island with the face in its middle,
  opened up the back of the head, with the back of each ear and the pocket of
  each nostril as islands. No island is mirrored and no two share texels, so
  `UVMap_Paint` is `UVMap`. The body's islands lie on an image twice as wide as
  high at one texel density, about 1,600 texels per metre at 4096 x 2048, and
  the head's on a square image of its own, the image of `M_Face_<Label>`, at
  about 4,000 texels per metre at 2048 x 2048.
- **Plain skin until it is painted.** `skin.py` makes the images of the skin and
  the eyes, and the assembly packs them into the file: `T_Body_<Label>` and one
  `T_Face_<Label>_<Face>` per face the game offers, all in one skin colour, and
  one `T_Eye_<Label>_<Colour>` per eye colour the game offers, an eyeball seen
  from in front with an iris of that colour and a pupil. On every face the
  inside of the mouth, the island `mouth` with the inner lips and the cavity, is
  red, 4 texels past the island's edge. Each material shows the first of its
  images, and the others lie in the file as its variants, by the names
  `content/characters/appearance.json` offers, which a test compares. The teeth
  and the tongue are flat colours.

Each file that the build writes follows the layout of a body file above, at the
`model` stage: the root `<Label>` is an empty where the armature will be, until
the first rig puts the armature there, the body parts are written from the
source body as the sync writes them, the faces inside the head carry the hidden
parts 7 and 8 of the face attribute `part`, each eyeball's origin lies at its
centre, and the reference mesh lies in the `Reference` collection, which
renders leave out. The root carries the rig's landmarks as custom properties
`lmw_<name>`, which `landmarks.py` writes from vertices that the build names:
the keys that the bodies before the rebuild carried, with as many points in the
same order and with the same meaning, so paint layers that were made on those
bodies pair them, and `shoulder_joint`, `finger_bases`, `finger_joints`, and
`toe_ball`, by which the rig places the shoulders, the digits, and the toes.

To build the bodies again, with Blender on `BLENDER_PATH`:

```powershell
$blender = $env:BLENDER_PATH
$work = "artifacts/human-build"
$bundle = "<main checkout>/artifacts/art-reference/human-base-meshes-v1.4.1/human-base-meshes-bundle-v1.4.1/human_base_meshes_bundle.blend"
& $blender --background --factory-startup --disable-autoexec $bundle --python tools/art/blender/human_build/reference.py -- "$work/reference.blend"
foreach ($label in "Male", "Female") {
    & $blender --background --factory-startup --disable-autoexec "$work/reference.blend" --python tools/art/blender/human_build/build.py -- $label "$work/work_$label.blend"
}
& $blender --background --factory-startup --disable-autoexec --python tools/art/blender/human_build/assemble.py -- $work art/Characters
```

Expected result: `reference.py` prints the height of both references, how far
each body's arms hung and that they are turned out to 45 degrees, how much
each part of each body grew, how wide the male's waist becomes and between
which heights his trunk is filled, how far each armpit is moved, how far out the
side of the chest lies below it, by how much that side narrows on its way up,
and how much room the opening leaves the body, how
near the head cage lies to the female one, a median under 1 mm, and how many
faces of the control cage and triangles of the band set each ear into the head
cage; `build.py` prints the measures it found, the navel, the nostril, and the
lids at the eye's corners and in the middle of each lid among them, and no line
that starts with `HUMAN_BUILD_FAILED`, which names a face
that is wound the wrong way, a sliver, a fold, or an edge at which the net is
tucked into itself, as it is where a vertex has landed on skin inside the
reference's head; and `assemble.py` prints the triangles of each body and how
much of each image its islands fill, and writes both files. The same input
gives the same bodies. The files replace the committed ones without a rig, so
rig them next, as [Rig The Human Base Characters](#rig-the-human-base-characters)
describes.

| Script | What it does |
| --- | --- |
| `reference.py` | Takes the two bodies with their eyeballs and their control cages from the bundle, turns them to face +Y, stands them on the ground at 1.79 m and 1.70 m, turns their arms out to the A-pose and names the shoulder joint they were turned about, smooths the male's groin, shapes the female, grows both a little fuller, opens their armpits and names them, lays the head cage onto both heads, and takes each ear with the skin around it from the control cage, with the band that sets it into the head cage. |
| `build.py` | Builds one body's net from its reference into a work file, with its zones, hidden parts, UV islands, and seams, and reports its quality. |
| `assemble.py` | Writes both bodies' asset files from their work files: every quad cut from the corner that leaves the better triangles on the worse of the two bodies, after the faces that this cut would leave as slivers are evened out, and a bent quad cut along its side of the reference's skin, so a fold shows as a hollow and not as a ridge; the UV map, the body parts, the inner parts, and the reference. |
| `surface.py`, `net.py` | The reference surface with its sections, rays, and nearest points, and the net: its faces, holds, islands, seams, subdivision, fitting, mirror image, its check for edges at which it is tucked into itself, and its checks for slivers and folds, which it mends without moving the lids' edge, the line between the lips, or a border between two body parts out of its plane. |
| `body.py`, `hand.py`, `digits.py`, `foot.py`, `head.py`, `eye.py`, `parts.py`, `islands.py` | The cage of the trunk, the arms, the neck, and the legs, with the navel and the line over the chest; of the hand and its fingers; of the foot and its toes; of the head; the lids of the eyes, measured on the reference spoke by spoke; the eyeballs, teeth, and tongue; and the islands that follow from the net's shape, the hand's back and palm, the top and the sole of the toes, and the back of each ear. |
| `landmarks.py` | The rig's landmarks from the vertices the cage names, through the subdivided net: the rings of each digit and of the wrist, the ball of the foot, the line between the lips, the lids' inner edge, and the points of the face's middle line, found as the furthest forward or back within heights set by the ones before them. |

### Rig The Human Base Characters

`tools/art/Sync-HumanBase.ps1 -Rig` rigs the bodies on the `Humanoid` skeleton
with the steps and checks of [Human Base Characters](#human-base-characters),
the joints placed from the landmarks of their files, and the eyelids weighted
by rule. A file that the build wrote has no armature yet, and `-Rig` gives it
its first one.

- **Joints.** The shoulder joint lies where the reference's arm was turned,
  `shoulder_joint`: the ray casts that found it on the bodies before the
  rebuild look for the joint of a hanging arm and would land 4 cm below it and
  2 cm outside it on an arm turned out to 45 degrees. The hip joint lies 9 cm
  above the crotch, where the joint is, level with the roundest of the buttock;
  on the bodies before the rebuild it lay 4.5 cm above it. Only about a joint
  that high can the fold of the groin run up toward the hip bone: about a low
  joint the skin under the hip bone would have to swing far down and forward
  with the thigh, so the fold can only run
  level, straight across the top of the thigh. The spine's joints keep their
  shares of the way from the hip joints to the neck, so they lie higher too. A
  finger's middle and end joints lie in the
  middle of the rings around each, and its knuckle 2.2 cm inside the palm from
  the middle of its ring at the web, along its first bone, where the build put
  the palm's ring of knuckles; the thumb's joints lie at its web and between the
  rings around its joint, and its base 0.4 of the way from the wrist to its web.
  A digit's skin is what lies beyond the ring it grows from, `finger_bases`. The
  toes bend at the middle of the ring over the ball of the foot, `toe_ball`.
- **Shoulders.** The shoulders take the shaping that the bodies before the
  rebuild had. Of the bone heat the upper arm keeps at most half on the torso
  at the cut between the torso and the arm, fading out over 6 cm; the
  collarbone keeps the skin over it and, at the back, over the shoulder blade;
  and the weights around the cut are smoothed. The bodies' cut lies 3 to 4 cm
  inside the shoulder joint, and their armpit is a hollow of its own in the
  model, so an arm that
  rises or hangs leaves the side of the chest in place without a rule of its
  own. A rule that gave the arm the shares the build turned the arms out with
  was tried on these bodies and left out: with the arms overhead it left the
  upper arm thick over a narrow root.
- **Hips.** The legs meet the trunk in a V, as the groin of a body does. The
  boundary below which the thigh takes the skin rises in front from the crotch
  on the inner side, past the front of the thigh, to its highest place under
  the hip bone, 6 cm above the joint and 20 degrees outside the front. From
  there it comes down to 1 cm above the joint at the outer side, with a blend
  of 20 cm, where the skin of the hip stretches and does not fold. A thigh that
  rises, as when sitting, so folds along the groin and not straight across its
  top. At the back the boundary lies just below the fold under the buttock,
  10 cm below the joint, with a blend of 24 cm: about the higher joint the
  underside of the buttock swings further with the thigh, and a boundary
  halfway up the buttock, as the bodies before the rebuild had it, draws that
  underside in under the pelvis when the thigh rises. At the crotch it lies
  where it lay on the bodies before the rebuild. The crotch itself does not
  move when the legs do: the thighs have no share within 1.5 cm of its middle
  line, which runs under the
  trunk between the legs and up its front to the hip joints' height, and reach
  their full share 7 cm from it. With a share there, raised thighs push the
  skin above the crotch into a fold under the belly. The weights are then
  evened out over the body's edges four times, within 22 cm above and below
  the joints, and what has no share is left as it is: the boundary crosses the
  body's level rows of
  faces at a slant, and unevened, each row folds at a place of its own, which
  cuts teeth into the fold. Lengths are at 1.785 m and follow the body's
  height.
- **Eyelids.** The lids' inner edge is the line where their skin meets the pocket
  behind them, with its corners nearest to and furthest from the nose. The lids
  meet on a line a fifth of the way from the lower lid's edge to the upper
  lid's, around the eyeball's centre, at each place across the eye. A turn about
  the X axis keeps a vertex's place across, so each vertex of the edge takes the
  weight at which the skin, which blends its place at rest and turned by the
  closed angle in a straight line, reaches that line; the corners stay. The rings
  of skin outward from the edge take the weight of their nearest vertex of the
  edge times their ring's share, on the upper lid 1, 1, 1, 0.9, 0.55, and 0.15 out
  to the brow, on the lower lid 1, 1, 1, 0.7, 0.35, and 0.1 out to the cheek, and
  the pocket behind the edge follows the edge. The step reports how much of the
  skeleton's closed turns each lid needs: the male's upper lids 45.9 of 46
  degrees and his lower lids 10.9 of 11, the female's 41.7 and 10.4.
- **The face on the skull.** The face and the skull are rigid on the head: the
  head's skin follows the neck only near the cut between the two parts, as much
  as bone heat gave it within 5 mm of the cut and not at all from 35 mm on.
  Bone heat alone leaves a third of the chin, a quarter of the mouth, and a
  tenth of the skin around the eyes to the neck. A head that turns or nods on
  the neck then twists the face, pushes the eyeballs through the lids, and
  opens the lips, and an opening jaw leaves the lower lip behind the lower
  teeth.
- **The mouth.** The jaw opens the mouth to an oval, measured from the middle
  of the mouth to its corner, the landmark `mouth_corner`. The corner follows
  the jaw by 0.4, less than half, so the upper lip stays nearly level and the
  lower lip rounds the opening. The lower lip follows the jaw wholly in the
  middle and by less toward the corner, and the upper lip not at all in the
  middle and by more toward the corner, so the two part by `(1 - x * x) ** 0.6`
  of the opening at `x` of the way to the corner, between an ellipse and two
  arcs that meet in a point. Above and below the line between the lips the skin
  passes from the head to the jaw over a band: 0.6 mm in the middle of the
  mouth, 12 mm more at its corner, so the lips and the cheek round the corner,
  and 0.6 mm more for every millimetre beyond the corner, so the cheeks
  stretch. The bodies' lips meet: the upper lip's vertices on the line
  between them lie where the lower lip's do, and each follows its own lip, which
  the side its other neighbours lie on names.

To rig the bodies after a build, write their files with the landmarks first,
with the commands of
[Rebuild The Human Base Characters](#rebuild-the-human-base-characters), then:

```powershell
.\tools\art\Sync-HumanBase.ps1 -Rig
```

Expected result: a line per step with its time, and the script names the review
file `artifacts/human-base/Human_Base_Rig.blend`, the sheets in
`artifacts/human-base/review`, and the report, and says of each body that it
was updated and which approvals no longer hold, such as `Human_Male: updated.`
and `Human_Male: the 'rig' content changed after its approval; ...`. The
report gives each lid's closed turn and how far the closed lids lie apart. Look
closest at `<Asset>_rig_face.png`, with the jaw opening and the lids closing,
`<Asset>_rig_arms.png`, `<Asset>_rig_hips.png`, `<Asset>_rig_hands.png`, and
the lids on `<Asset>_rig_weights.png`.

The command writes the rigged bodies over the two model files. When the
reviewer approves them, sync the animation library on the new rig, approve the
`model` and `rig` stages, export the bodies with the library, and verify, as
step 4 of [Edit The Human Base Characters](#edit-the-human-base-characters)
describes.

## Animation Libraries

Clips live in animation libraries: art assets of kind `animations` below
`art/Animations`, keyed on the rig of one character and played on every body of
the skeleton. `art/Animations/Human_Actions` holds every clip of the human
bodies, and the bodies hold none; [Assets](#assets) lists them.

- The `Export` collection holds the armature `Humanoid`, a copy of the rig of
  `Human_Male` named after its skeleton. Every clip is an action keyed on it, at
  30 frames per second.
- The `Reference` collection, which is never exported, holds what shows the
  clips: the male body bound to `Humanoid`, so it moves with every key, the
  female body 2 m to its right on an armature of her own whose bones copy the
  pose of `Humanoid` in local space, with the hips' movement scaled to her hip
  height as Unity retargets a Humanoid clip, and a checkered ground of
  quarter-meter squares, so a body that slides or sinks shows at once.

`tools/art/Sync-AnimationLibrary.ps1` builds the library file from the
manifest's `rig` and `previews`. When the rig changes, such as after
`Sync-HumanBase.ps1 -Rig` or new bones in the skeleton, it replaces the armature
and the bodies and keeps every action, so the clips play on the new rig and are
reviewed and approved again. It names every clip's slot after the armature, so
Blender binds a clip to it when a person picks the clip in the Action Editor. A
library whose armature is already the current rig is left as it is, unless
`-Force` rebuilds it. The approval and the export refuse a library whose
armature is not the current rig, and CI refuses one that was exported against
other content of its rig; exporting it again is enough when the rig's armature
did not change. For a library on the `Humanoid` skeleton the sync then reports how far a
foot on the ground slides in `Idle`, `Walk`, and `Jog` when the game moves the
body at their speeds, and stops when one slides more than 15 mm.

A rig whose joints moved rests in another pose, and a clip's keys, which turn
each bone from its rest, would then show another motion. `-Refit` moves every
clip to the new rig at the sync that replaces the armature: on every frame each
deforming bone of the new rig turns from its rest as the old rig's bone turns
from its rest, in world space, the way a clip of
[another skeleton](#start-a-clip-from-another-skeleton) is moved. The arms and
the fingers are aligned to the old rest pose, each hand
takes the old hand's frame, the hips move as far as before for the legs'
length, and the legs are solved again so each ankle is where it was. A foot
keeps its turn from the ground, and a bone that deforms nothing, such as a
socket, keeps its own turn. A clip keys the bones it keyed, now on every frame,
loops if it looped, and keeps its name; the step reports per clip its lowest
point and how low the hips sink, before and after. The clips named in
`-Generate` are not moved: the gait model makes them again on the new rig, with
strides for its legs. The library as it was is kept in
`artifacts/animation-libraries/<Name>_before_refit.blend`. A rig that rests as
before, such as one with new weights only, leaves the clips as they are. No
clip keys the cloth chains, which the runtime cloth solver moves, or the
eyelids, which the game closes.

A clip is made by hand in the library file, in the reviewer's Blender, through
the [Blender MCP server](#set-up-the-blender-mcp-server) or the reviewer's own
keys: key poses on `Humanoid`, smoothed by Blender's curves, as
[Make A Clip](#make-a-clip) describes. A locomotion clip may start from the gait
model instead, and the hand pose `Grip` from the fit above:
`Sync-AnimationLibrary.ps1 -Generate Walk` adds the model's
`Walk` when the library lacks it, and the same for `Idle` and `Jog`, and never
replaces a clip the library has.

The gait model, `tools/art/blender/human_base/animate.py`, makes its clips with
the strides matched to the game: `Walk` at 1.4 m/s and `Jog` at 5.0 m/s, the
movement speed of players, and a 4 s breathing `Idle` with a slow weight shift
and a look around. Each foot follows a contact model with a heel strike, a roll
over the ball, and a planned swing, and the legs reach it with analytic two-bone
IK while the hips sink where a leg cannot reach. The pelvis turns, drops, and
tilts with the steps, the lower back takes up the tilt, the shoulders turn
against the pelvis, and the arms, forearms, hands, and fingers follow the swing a
little later. A planted foot slips at most 8 to 13 mm in the walk and about 1 mm
in the jog. The clips of `Human_Actions` are the model's clips as approved; edit
them by hand from there. A Unity test checks that the controller's walk and jog
speeds match the model's.

A clip may key only some bones: `Grip` keys only the fingers and thumbs of both
hands, closed around a handle of 29 mm on each hand's socket, and the game plays
it over the other clips on the fingers of a hand that holds an
[attachment](#attachments). `Sync-AnimationLibrary.ps1 -Generate Grip` fits it
to the hand of the body the library shows, with
`tools/art/blender/human_base/grip.py`:

- The fingers of a hand at rest are spread and each turned a little about
  itself. Every finger first turns at its knuckle, to the side and about itself,
  until the axis it closes about lies along the handle, so it closes in a plane
  across the handle, beside its neighbours.
- A finger closes joint by joint from its knuckle outward: each joint turns
  until the pad of its segment touches the handle, or as near to it as it
  comes. A pad is the skin along the middle of the outer half of a segment's
  palm side: the handle lies in the palm at the fingers' roots and presses into
  the skin there.
- The fingers beside the middle finger, and then the little finger beside the
  ring finger, turn to the side until each lies against its neighbour, by at
  most 20 degrees from where it closes across the handle, and the fingers close
  again.
- The thumb closes over the handle and the fingers: its base turns toward the
  palm and to the side and its outer joints close until the pads of its two
  outer segments lie on them and not in them.

Both hands take the right hand's pose, mirrored. The step reports each joint's
turn and how far each pad lies from the handle, and it stops when a pad lies more
than 2 mm inside the handle or 4 mm off it, when two neighbouring fingers lie
more than 3 mm apart or overlap by more than 2 mm, or when the thumb lies more
than 1 mm inside the handle or a finger. Every later sync measures the library's
`Grip` on its body's hand by the same rules. A clip may also come from a
script that places every key on purpose: the tools' `Mine` and `Chop` follow the
tool's grip frame through a swing in a plane, with both hands reaching its haft
by analytic two-bone IK, and the weapons' hold and attack clips are keyed with
the same scripted arm and leg rig from searched grip frames that keep the wrists
and forearms near their neutral range and every arm inside the humanoid muscle
ranges. The library's review sheet shows every clip as eight frames on both
bodies, from the front right and from the back left, with a tool held in the
rows of the clip it is used with and a weapon in the rows of its hold and attack
clips.

In Unity, `Human_Actions.fbx` brings its clips as Humanoid motion, and
`HumanLocomotion.controller` plays them on every player body; see
[Player Bodies](UNITY_CLIENT_ARCHITECTURE.md#player-bodies). Adding a clip to the
library changes its content, which every garment template and every garment
texture with masks records as a clip source it was checked in, so they are
exported again, in the same run as the library or after it:
`Export-ArtAsset.ps1` checks them in the new clip, and their approvals stay,
since their content does not change.

### Make A Clip

1. Bring the library up to date with the rig it is keyed on:

   ```powershell
   .\tools\art\Sync-AnimationLibrary.ps1 -Asset art/Animations/Human_Actions
   ```

   Expected result: `Human_Actions is up to date: its armature is the current
   rig of art/Characters/Human_Male.` and `Human_Actions: the 'animation'
   approval holds.`, or after a rig change `Synced Human_Actions from
   art/Characters/Human_Male: ...` followed by `Human_Actions: the 'animation'
   content changed after its approval; ...` and the reminder to review and
   approve the clips again, and one line per locomotion clip with
   how far a foot on the ground slides, such as `Humanoid Walk: 28 frames,
   1.4 m/s, hips 0.810 to 0.824 m, largest slip of a foot on the ground
   8.4 mm`. To start a locomotion clip from the gait model instead of an empty
   action, add `-Generate Walk` (or `Idle`, `Jog`). Expected result: `Added Walk
   to Human_Actions and its asset.json; ...` when the library lacks it, or
   `Walk: the library has the clip already` when it has it, which leaves the
   clip as it is. A new library starts with
   `.\tools\art\New-ArtAsset.ps1 -Kind animations -Asset art/Animations/<Name>`,
   keyed on `art/Characters/Human_Male` and shown on
   `art/Characters/Human_Female` unless `-Rig` and `-Previews` say otherwise; it
   takes `-Generate` too. A library cannot be approved before it has a clip.
2. Open the library in Blender:

   ```powershell
   & $env:BLENDER_PATH art\Animations\Human_Actions\Human_Actions.blend
   ```

   Expected result: the male body stands at the origin and the female body 2 m
   to his right on a checkered ground. To see a clip, select `Humanoid`, switch
   the Timeline at the bottom to the Dope Sheet's Action Editor, pick the clip,
   such as `Walk`, set the Timeline's End to its last frame, and press Space. To
   make a clip, select `Humanoid`, enter Pose Mode, create an action for the new
   clip in the Action Editor, or change the keys of an existing one, and key
   poses on its bones. Both bodies follow every pose.
3. Give the action the custom property `loop` (true or false), a manual frame
   range from frame 0 in the Action properties, and a fake user (the shield
   button in the Action Editor), so Blender keeps it when no armature plays it,
   and add its name to `clips` in `asset.json`. Then clear the armature's active
   action, reset its pose to the rest pose (`Alt+G`, `Alt+R` on every bone), and
   save. The approval in the next step records the library's new content; until
   then `Verify-ArtAssets.ps1` reports that the file was saved after its content
   was read, and until the library is exported again, its export record as
   stale.
4. Render the review sheet, review it and the clip in Blender, then approve,
   export, and verify:

   ```powershell
   .\tools\art\Render-ArtPreview.ps1 -Asset art/Animations/Human_Actions
   .\tools\art\Approve-ArtAsset.ps1 -Asset art/Animations/Human_Actions -Stage animation
   .\tools\art\Export-ArtAsset.ps1 -Asset art/Animations/Human_Actions
   .\tools\art\Verify-ArtAssets.ps1
   ```

   Expected result: `Review sheet for Human_Actions:` with the path of
   `artifacts\art-previews\Human_Actions.png`, two rows of eight frames per
   clip, `Approved the 'animation' stage of Human_Actions
   (clips=Death,Idle,Walk,...).`, `Exported Human_Actions to
   src/Client/Assets/Art/Models/Animations/Human_Actions.fbx
   (clips=Death,Idle,Walk,...).`, and then the problems of the garment
   templates and textures that the next step fixes. Unity imports the clips as
   Humanoid motion when it gets focus. A library whose armature is not the
   current rig stops the approval and the export with `The armature "Humanoid"
   is not the current rig "Male" ...`; sync it first. Commit the `.blend` file,
   `asset.json`, the FBX, and its `.meta` file together.
5. Export every garment template and texture again, since they record the
   library export they were checked in; their content does not change, so their
   approvals stay:

   ```powershell
   .\tools\art\Export-ArtAsset.ps1 -Asset (Get-ChildItem art\Garments -Directory | ForEach-Object { "art/Garments/$($_.Name)" })
   .\tools\art\Export-ArtAsset.ps1 -Asset (Get-ChildItem art\GarmentTextures -Directory | ForEach-Object { "art/GarmentTextures/$($_.Name)" })
   .\tools\art\Verify-ArtAssets.ps1
   ```

   Expected result: one `Exported <Name> ...` line per template and texture,
   the models and data unchanged and only their `asset.json` files rewritten,
   and then `Verified <n> art asset(s).` A template that comes through a lower
   layer in the new clip stops its export with the check that failed; fix it in
   its `.blend` file and review it again.

When the characters' content changes, for example after an edit, export the
library again too; `Verify-ArtAssets.ps1` otherwise stops with `was exported
against other content of its rig`. If the edit changed the rig, the sync
changes the library, and it needs a new review and approval first. A clip that
a weapon or a tool names needs a state in the controller as well; see
[Make An Attachment](#make-an-attachment).

### Render The Clips As Videos

`Render-ArtClips.ps1` renders the clips of an animation library, or of a
skinned asset with clips of its own such as the wolf, as videos to review in
motion next to the review sheet, and measures each clip:

```powershell
.\tools\art\Render-ArtClips.ps1 -Asset art/Animations/Human_Actions -Clip SwordSlash,Walk
.\tools\art\Render-ArtClips.ps1 -Asset art/Animations/Human_Actions -Clip Walk -Speed 1.4
.\tools\art\Render-ArtClips.ps1 -Asset art/Creatures/Wolf -Clip Walk -Speed 1.4
```

Expected result: per clip one line, such as `SwordSlash: 1.00 s; root travels
0.18 m and sinks to 91 %; fastest Hand_R at 6.7 m/s; 29 % of the joints turn;
lowest point 0.015 m: <path>`, and the video
`artifacts\art-previews\Human_Actions_SwordSlash.mp4`: every body the file
shows, for a library its rig's body and its preview bodies, from the front
right on a checkered ground of quarter-meter squares, the clip played three
times, a looping clip without its last frame, which is its first again, so the
loop runs on evenly, 960x540 at 30 frames per second in H.264. A tool or a weapon is held in
the clips the review sheet holds it in, such as the iron sword in
`SwordSlash`. Without `-Clip` every clip of the asset is rendered. With
`-Speed` the ground moves back at that speed in m/s, the speed the game moves
the body at, such as 1.4 for `Walk` and 5.0 for `Jog`, so a foot on the ground
that slides shows against the checkers. The script never saves the file.

The numbers are taken from the deforming bones of the armature, without the
bones of cloth chains, in world space on every frame:

- the clip's length in seconds;
- how far the root bone's head travels horizontally, and the lowest it sinks as
  a share of its rest height;
- the bone whose head moves fastest between two frames, and its speed in m/s,
  without the bones shorter than 5 cm, such as fingers and toes, which ride on
  a hand or a foot and would always outrun it;
- the share of the bones that turn in their joints, against their parents, by
  more than 5 degrees from the clip's first frame, which is low when only a
  part of the body moves, such as 29 % in `SwordSlash`, where the arm strikes
  and the body stands, against 80 % in `Death`;
- the lowest head or tail of those bones, below 0 under the ground;
- for a looping clip that does not end where it starts, `the loop jumps` and
  the largest distance of a bone head between its first and last frame.

### Start A Clip From Another Skeleton

`Import-ArtClip.ps1` adds a clip to an animation library on the `Humanoid`
skeleton from clips of another skeleton, such as a clip of a clip library in a
`.glb`, `.gltf`, `.fbx`, or `.bvh` file, as a starting point that a person then
edits by hand, like the gait model's clips. A map in `tools/art/retarget` names
the source bone of each `Humanoid` bone and the degrees the source turns about
Z to face +Y; `UniversalAnimationLibrary.json` maps the Universal Animation
Library (CC0), fingers included:

```powershell
.\tools\art\Import-ArtClip.ps1 -Asset art/Animations/Human_Actions -Source <clips>.glb -Map tools/art/retarget/UniversalAnimationLibrary.json -Clip Sword_Block -Name SwordBlock -Grip R
```

Expected result: `SwordBlock: 37 frames at 30 fps from Sword_Block of
<clips>.glb, 52 bones mapped, 42 aligned to the source's rest pose, the fingers
of R in Grip -> <library file>`, `Added SwordBlock to Human_Actions and its
asset.json; edit it in Blender as you like.`, the clip's motion numbers as
[Render The Clips As Videos](#render-the-clips-as-videos) prints them, and the
reminder to review and approve. `-Clip` names one or more of the source's
clips, played one after another, such as `-Clip
Sword_Regular_B,Sword_Regular_B_Rec`. `-Grip` (`L`, `R`, or `LR`) gives the
fingers of those hands the library's `Grip` clip, for a hand that holds a
weapon or a tool. The script never replaces a clip: a library that has the
clip stops it with `SwordBlock: Human_Actions has the clip already, and a clip
is never replaced. Choose another -Name.` A library of another skeleton stops
it as `-Generate` does, and a map that names a bone neither skeleton has, or
lacks the hips, a thigh, a shin, or a foot, stops it with a message that names
the bone.

How the clip is made:

- The source is imported into the library at 30 frames per second, so its
  clips are sampled per frame, and the first frame of a later clip is left out.
- Each mapped bone turns from its rest as its source bone turns from its rest,
  in world space, after the source is turned by the map's `facing`. Unmapped
  bones stay at rest.
- The arms, legs, feet, and fingers are first aligned along their length to the
  source's rest pose, and each hand takes the source hand's whole frame, from
  the wrist and the bases of the index, middle, and little fingers, so a source
  that rests in another pose, such as a T pose, moves as it does.
- The hips move as the source's hips move, scaled by the ratio of the leg
  lengths, and the legs are solved again with the gait model's two-bone IK, so
  each ankle is where the source's is from the place its hips rest at, scaled,
  wherever the source stands in its file, the knee toward the front of the
  retargeted leg.
- Every bone but the cloth chains is keyed on every frame, linear, with the
  hips' location, and the action gets a fake user, a frame range from 0, and
  `loop` false; set `loop` true by hand for a clip that loops.

The script adds the clip to `clips` in `asset.json` and clears the export
record, as `-Generate` does. Review the clip in Blender and as a video, edit it
by hand where it needs it, then approve and export the library as
[Make A Clip](#make-a-clip) describes from step 4.

## Appearance Layers

Each layer of a character's look is made by hand in an asset of its own or in the
character's file, so a change to one layer leaves the others as they are. How
the layers combine on a body is in
[Layers Of A Character](EQUIPMENT_VISUALS_DESIGN.md#layers-of-a-character).

| Layer | Made as |
| --- | --- |
| Texture variants | Other images of a painted material, packed in the character's `.blend` file and named in its manifest's `variants`, such as the faces `T_Face_Male_Stubble` and `T_Face_Male_Clean` and the eye colors `T_Eye_Male_Teal` and `T_Eye_Male_Brown`. They are reviewed and approved with the model. |
| Paint layers | Transparent paint over the skin in the bodies' paint UV map, of a category, `underwear`, `scar`, or `tattoo`, in a [paint layer](#paint-layers) asset of its own, painted once on the layer's canvas and moved to every body by projection. |
| Garments | Meshes skinned to the shared skeleton, such as clothes, armor, and hair: [garment templates](#garments) built on the [garment cage](#garment-cage) and fitted to every body of it, each worn with a [garment texture](#garment-textures) of its own asset. |
| Attachments | Models held on a socket, such as a sword or a shield, in an [attachment](#attachments) asset of its own. |

The faces and eyes of the human base characters are variants: images packed into
the character's file, shown by the model review, and exported with the model
through `variants` in `asset.json`. In Unity, the body's `BodyAppearanceCatalog`
lists one slot per material with variants, such as `Face` and `Eye`, with its
options, the material's own image first, and a look picks one option per slot;
the catalog also lists the paint layers, garments, and attachments that fit the
body; see [Player Bodies](UNITY_CLIENT_ARCHITECTURE.md#player-bodies). The
Wardrobe and the Admin menu (F3) show every layer of the catalogs.

## Paint Layers

A paint layer below `art/Paint` is paint over the skin of the bodies, of a
category, such as the tattoos `art/Paint/Tattoo` and `art/Paint/ForearmRunes`,
the scar `art/Paint/Scar` on the faces, or the underwear `art/Paint/Bra` and
`art/Paint/Briefs`. The paint does not depend on the bodies' topology: it is kept
on the layer's own canvas and moved to every body by projection, so a body of
any topology, such as a race body or a new base model, gets it.

### The Canvas

The file's collection `Canvas` holds the canvas `Canvas`, a copy of the body the
paint is painted on, its parts joined in its rest pose, with materials of its
own, `Canvas_<Part>_<Label>`, that body's version of the garment cage,
`Canvas_Cage`, and its rig's landmarks in the custom property `landmarks`, and
the image `T_Paint_<Name>`, the paint. A person paints the image on the canvas,
by hand in Texture Paint mode or by a script, and the image carries the custom
properties `roughness` and `metallic`. The canvas is the layer's own: it never
changes when the bodies change, and a new layer's canvas is a copy of its first
body.

The canvas is painted in its paint UV map, `UVMap_Paint`, which every body has
next to the skin's `UVMap`: the skin's UVs, with every material whose faces share
their texels with their mirror image laid out twice as wide, the left side in the
left half and the right side in the right half. The human bodies mirror none of
their skin: every island of their `UVMap` has texels of its own, the left
forearm's apart from the right one's, so their `UVMap_Paint` is their `UVMap`,
the paint of `M_Body` has the size of the body texture `T_Body_<Label>`, 4096 by
2048 texels, and that of `M_Face`, such as the scar, the size of the face
texture `T_Face_<Label>`. Paint on one side so stays on that side, as the left
forearm's runes of `ForearmRunes` do: `check-parts` of `Sync-HumanBase.ps1`
fails a body in which a texel of `UVMap_Paint` belongs to both sides, and
`ArtAppearanceCatalogTests` holds the runes' crop within the paint UVs of the
male's left forearm and apart from those of the right one.
`Sync-HumanBase.ps1` writes `UVMap_Paint` on every body part from the source
body's `UVMap`; a body of another base model brings a map of its own without
mirroring, so its `UVMap_Paint` is its `UVMap`.

### Moving The Paint To The Bodies

The file's collection `Reference` holds per body the mesh `Paint_<Label>`, the
body's parts joined in its rest pose and drawn with the paint over its skin, and
the image `T_Paint_<Name>_<Label>`, the canvas's paint in the body's own
`UVMap_Paint`. `tools/art/Sync-PaintAsset.ps1` builds it: every texel of the
body's painted material takes the paint at its point of the canvas, filtered
between the canvas's texels with their colors weighted by their alpha, and the
painted edges are extended over the empty texels around them, so filtering at a
UV seam takes paint. The texel's point of the canvas is found in one of two
ways:

- A body with the canvas's topology, such as `Human_Female` for a canvas of
  `Human_Male`, takes the point at the same place of the same face. This move is
  exact.
- A body of another topology takes it through the garment cage: every canvas
  vertex is bound to the nearest face of `Canvas_Cage` on its own region, side,
  and finger, placed at the same place and height over the body's cage version,
  moved further by the difference between the rigs' landmarks, such as the eye
  rims, the lips, the nose, the chin, the wrists, and the finger tips, and laid
  onto the body's skin of the same region, side, and finger. Each of the body's
  texels then takes the nearest point of the canvas so fitted. The file keeps
  the body's cage version as `Cage_<Label>` and the content of the cage it was
  taken from in the scene property `cage_content`, and the export records that
  content.

The cage keeps the paint's place along the bones: an armband that the woman
would get from the man's canvas through the cage, as a body of another topology
does, lies at 0.41 to 0.61 of the way from the shoulder to the elbow, where the
man has it at 0.43 to 0.61 and the shared topology puts it on her at 0.42 to
0.61. Where one body has a form the other lacks, the cage spans it and the paint
stretches: the rune emblem, 91 mm wide on the man's upper breastbone, would come
out 94 mm wide on the woman through the cage instead of the 73 mm that the shared
topology gives her, since her cleavage has no counterpart on the man.
`tools/art/Test-PaintProjection.ps1` checks this move on subdivided copies of
the bodies, another topology of the same surfaces: the canvas's own body must
take the paint within 0.5 mm (median) and 1 mm (95%) of its place, and for the
other bodies the script reports how far the cage places the paint from where
their shared topology does, or, for a body of another topology than the canvas,
from its own move through the cage.

`Sync-PaintAsset.ps1 -Canvas <body>` moves the canvas to another body of the
layer, with the paint moved to it, so the paint is painted on that body from then
on. When a new base model replaces the human bodies, each layer's canvas is moved
to the new first body this way, its paint is checked and touched up there, and
every body of the new model's topology then gets the exact move from it.

Each body's image is cropped to its paint, to whole blocks of 4 texels with a
block of empty texels around it, and keeps its place in the custom properties
`rect`, x, y, width, and height in texels from the bottom left, and `layout`,
the width and height of the body's full paint layout, so a small tattoo costs
little memory. The approval and the export check that every body's image is the
current move of the canvas, and the export writes every image as it is packed,
with its crop and the fingerprint of its body's export, so CI rejects a layer
whose body changed what the move reads from it.

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
its alpha inside its rect, in order; `_PaintCount` says how many layers are
drawn. The body materials that a paint layer paints, such as `M_Body_Male`, draw
with this shader, and without a layer they draw like Lit. `Create Missing
Materials` creates them with it. When URP is upgraded, the shader is made again
from the new `Lit.shader`, `LitInput.hlsl`, `LitForwardPass.hlsl`, and
`LitGBufferPass.hlsl` with the same changes. The combined body mesh carries the
parts' paint UVs, and garments and the eyes, which have none, take zero. The
appearance catalog of every body lists the paint layers that fit it with their
category, slot, image, and rect, in the order of their `layer`, and a body shows
them by the rules of their categories, as
[The Look On The Skin](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-skin)
describes.

### Make A Paint Layer

`art/Paint/Tattoo` is a tattoo on the chest and the upper arms, and
`art/Paint/ForearmRunes` a one-sided tattoo on the left forearm.

1. Create a new paint layer of a category, `scar`, `tattoo`, or `underwear`,
   which says how many a body shows and when a player chooses it, as
   [The Look On The Skin](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-skin)
   describes. It is drawn at the first layer of its category unless `-Layer`
   names another of them, and underwear needs `-Slot body` or `-Slot legs`:

   ```powershell
   .\tools\art\New-ArtAsset.ps1 -Kind paint -Asset art/Paint/Serpent -PaintCategory tattoo
   ```

   Expected result: `Synced Serpent: ...` and `Created art/Paint/Serpent with
   asset.json and Serpent.blend.` Its canvas is a copy of
   `art/Characters/Human_Male`, it paints the body material `M_Body`, and the
   paint moves to `art/Characters/Human_Female`, unless `-Part` and `-Bodies`
   say otherwise, such as `-Part Face` for a scar on the face. An existing paint
   layer is built or updated with:

   ```powershell
   .\tools\art\Sync-PaintAsset.ps1 -Asset art/Paint/Tattoo
   ```

   Expected result: `Tattoo is up to date: every body is there and shows the
   current paint of the canvas.`, or `Synced Tattoo: ...` for a new or changed
   file, then whether the approval holds for the content, such as `Tattoo: the
   'paint' approval holds.`, and the command that approves it when it does not.
   `-Force` rebuilds the bodies even when the file is current.
2. Open the file in Blender. Expected result: the Outliner holds `Canvas >
   Canvas`, to the left of the bodies, and `Reference` with `Paint_Male`,
   `Paint_Female`, and the ground. Select `Canvas`, switch to Texture Paint, and
   paint into `T_Paint_Tattoo`, or change its custom properties `roughness` and
   `metallic`. A stroke on one side stays on that side; turn on X mirror in the
   tool settings to paint both sides at once. Save, then run the sync again.
   Expected result: `Synced Tattoo`, and `Paint_Male` and `Paint_Female` show
   the new paint when the file is opened again.
3. Render the review sheet, review it and the file, then approve, export, and
   verify:

   ```powershell
   .\tools\art\Render-ArtPreview.ps1 -Asset art/Paint/Tattoo
   .\tools\art\Approve-ArtAsset.ps1 -Asset art/Paint/Tattoo -Stage paint
   .\tools\art\Export-ArtAsset.ps1 -Asset art/Paint/Tattoo
   .\tools\art\Verify-ArtAssets.ps1
   ```

   Expected result: a review sheet that shows the layer on every body as the
   game does, over and under the underwear the body always wears, in the order
   of their layers: the bodies side by side from four sides, then every piece of
   the paint up close on each body, such as the emblem and each armband, from up
   to four sides it is seen from, framed on the part the camera sees, and then
   the canvas's image over grey with the outlines of its faces in
   `UVMap_Paint`, both sides of the body apart; `Approved the 'paint' stage of
   Tattoo (paint).`, `Exported Tattoo to src/Client/Assets/Art/Textures/Paint
   (T_Paint_Tattoo_Male, T_Paint_Tattoo_Female).`, and the verification without
   problems. When Unity gets focus, it updates the appearance catalogs. Expected
   result: the Console lists `Paint Tattoo` in both catalogs when the paint
   layer is new. Commit the `.blend` file, `asset.json`, the PNG files, the
   catalogs, the changed files of `Assets/AddressableAssetsData/AssetGroups`,
   and their `.meta` files together. Add a new layer to the options of
   its kind, and for underwear its slot, of every body model it fits in
   `content/characters/appearance.json`, as
   [Character Appearance](../content/README.md#character-appearance) describes;
   `CharacterLookTests` requires the content to offer exactly what every body's
   catalog has.
4. After a change of the move or the garment cage, check the move that a body of
   another topology than the canvas gets:

   ```powershell
   .\tools\art\Test-PaintProjection.ps1 -Asset art/Paint/Tattoo
   ```

   Expected result: about `Male: through the cage 0.02 mm (median) and 0.23 mm
   (95%) from the exact move ...` and `Female: through the cage 4.89 mm
   (median) and 26.89 mm (95%) ...`, without an error: the canvas's own body
   takes the paint within a millimeter of its place, and the woman shows how
   far the cage and the landmarks place the paint from where the shared
   topology does.
5. To paint a layer on another body from then on, such as a new base model that
   joined its `bodies`, move its canvas there, review the paint on it, and touch
   it up on the canvas:

   ```powershell
   .\tools\art\Sync-PaintAsset.ps1 -Asset art/Paint/Tattoo -Canvas Human_Female
   ```

   Expected result: `Synced Tattoo: ...`, and the canvas is a copy of the female
   with the paint she showed. Revert it with `git checkout -- art/Paint/Tattoo`
   when it was only a try.

## Garment Cage

`art/Cages/Humanoid` is the garment cage of the `Humanoid` skeleton: a sparse net
of fixed topology that lies over every body of the skeleton, smooth over the
body's pits and following its figure, on which garments are made and fitted; the
design is in
[The Garment Cage](EQUIPMENT_VISUALS_DESIGN.md#the-garment-cage). It has 3,424
faces in seven regions: 1,576 over the neck, the trunk, the arms to the wrists,
and the legs to the ankles, in `neck`, `trunk`, `arms`, and `legs`, 760 over the
hands, in `hands`, 544 over the feet, in `feet`, and 544 over the head, in
`head`.

Its file holds:

- The reference in the `CageReference` collection: the armature `CageRig`, a
  copy of the male body's rig when the cage was made, and on it the mesh `Cage`,
  the male body's faces of its `Neck`, `Torso`, `Pelvis`, `UpperArms`,
  `Forearms`, `Thighs`, and `Shins` parts, with their weights and the face
  attribute `region`. Its faces never change: a new base model changes the
  bodies, not the cage. Regions are only added to it, with their vertices and
  faces after its own: `hands`, the male body's faces of its `Hands` part, met at
  the wrist rings, with the face attribute `digit`, 1 to 5 for the thumb and the
  fingers by the bone their corners weigh most and 0 for the palm; `feet`, a net
  of the cage's own on each side, 16 rings of 16 vertices from the ankle ring to
  the toe tip, closed by a fan of 16 triangles there, with the point attributes
  `sweep_ring` and `sweep_column` that place each vertex on its ring; and
  `head`, a net of the cage's own from the neck's top ring to the crown, 16 rings
  of 32 vertices on the convex hulls of level sections of the head, so it spans
  the ears, the nose, and the eye sockets smoothly, closed by a fan of 32
  triangles at the crown, with the same point attributes. `Sync-CageAsset.ps1`
  adds a region the reference lacks, built on the first body. The figure scale
  and the lines it was last fitted with are custom properties of `Cage`, so
  changing them in `asset.json` changes the cage's content after a sync and
  needs a new approval.
- Per body, the collection `Fit_<Label>` with the body's armature and the cage's
  version `Cage_<Label>` on it, and in the `Reference` collection the body's
  source body, eyes, teeth, and tongue. The file opens with the male and its
  cage shown and the others hidden, because they stand at the same place.

`tools/art/Sync-CageAsset.ps1` fits every version from the reference alone, so a
body of any topology gets one, in the steps that
[The Garment Cage](EQUIPMENT_VISUALS_DESIGN.md#the-garment-cage) explains:

1. The reference moves with the rig from `CageRig` to the body's armature, every
   bone with its length, blended by the reference's weights.
2. Every vertex is drawn to the skin of its region: to the nearest skin within
   2 mm, otherwise along the cage's normal to whichever side meets the skin
   first, within 80 mm and at most 10 mm farther than the nearest skin. The
   border between a limb and the trunk then moves into the fold where the
   limb's skin meets the trunk's, the edges between a face of the limb and a
   face of the trunk, wherever it would span a hollow: a vertex of the border
   lies on the limb and its neighbors in the trunk lie on the trunk, and where
   the limb stands off the trunk, as an arm does at the armpit, the edges
   between them pass through the air and the faces would hang there like a web.
   The vertex moves toward the nearest point of the fold by the share of 10 mm
   that the middle of such an edge lies outside the skin, so the net runs along
   the arm on one side of the armpit and along the chest on the other. Over a
   shoulder or a hip, where the skin does not fold, the border stays where the
   rig carried it.
3. A ball whose radius is the figure scale rolls over the skin of the region
   along normals smoothed 25 times: a vertex where the ball does not fit is
   lifted until it does, by at most 0.6 of the radius. The waist and the small of
   the back keep the ball touching the skin and are followed. The lift fades in
   over three edges from the cage's open ends and from the borders between its
   regions.
4. The rings around the trunk, the neck, the arms, and the legs are drawn taut
   like a band, outward only and in the ring's plane, 200 steps: each edge counts
   toward a ring by the fourth power of the sine of its angle to the ring's axis,
   which is up for the trunk and the bone a vertex follows most for the neck and
   the limbs. The tension fades in over three edges from the open ends.
5. Eight Taubin steps fair the cage. A vertex that was moved more than 1 mm
   into a fold stays where it is, and the fairing fades in over three edges from
   it, since rounding the net off would lift it out of the fold again. No vertex
   and no face centre stays closer to the skin than 1 mm, nor does the middle
   or a quarter point of an edge of an open end, such as an ankle ring: skin
   that came out past such an
   edge would come out through the faces the second net hangs from it. A ray
   goes in from every skin vertex of the net's body parts, and of the hands and
   feet next to the wrist and ankle rings, along the skin's normal, up to 20 mm,
   and where it meets a face from outside before it leaves the body again, or
   the skin lies closer to a face than 1 mm, the face moves out along its
   normal, all its corners alike, until the skin lies 1 mm inside it. Through a
   thin part, such as an ear, the ray leaves the body first, and the face it
   then meets lies beyond the part, not in it. In each of the four passes all
   moves are found first and a vertex takes the largest one asked of it, so the
   result does not depend on the order of the vertices.

Two rules hold through all five steps:

- A vertex of the neck or the trunk keeps to the side of the body's middle
  plane that it has in the reference, and one in the plane stays in it. A
  vertex beside the plane is drawn to the skin of its own half, and neither the
  ball nor the tension moves a vertex toward the plane: the part of its
  direction that points there is left out, and the vertex moves by what is left
  of it. The two halves of a body face each other in the crotch and the buttock
  cleft, so a vertex lifted out along its normal there would cross to the other
  half, and the faces of the two halves would cut through each other.
- Where every vertex of the body's skin has a mirror image within 0.01 mm and
  every vertex of the net one within 1 mm in the reference, the two sides are
  evened out after every step: a vertex and its mirror image move to the middle
  of where they lie, and in the last step each takes the larger of their two
  moves. A mirrored body so gets a mirrored cage; what would differ between the
  sides comes from points that lie as near to one face of the skin as to the
  next. A body that is not mirrored is fitted side by side.

These steps fit the neck, the trunk, the arms, and the legs as one net. The
hands, the feet, and the head are fitted after them as a second net that holds
the wrist and ankle rings and the neck's top ring where the first left them, so
the regions added to the cage changed none of its other vertices:

- A hand's net moves with the rig like the rest and is then drawn to the skin
  as a whole, each vertex toward the nearest skin of its own finger or thumb
  and the palm, the body's hand faces that the finger's or thumb's bones move
  most, or of the whole hand where its faces belong to more than one. It is
  drawn in six rounds: in each, every vertex's move to the skin is evened out
  with its neighbors' moves, 24, 12, 6, 3, 1, and 0 times, before the net
  moves, so the net slides into place in one piece, then in ever smaller
  stretches, and in the last round every vertex takes its place on the skin. A
  hand whose thumb and fingers stand at other angles than the reference's would
  otherwise fold the net over itself. The vertices of the palm and between the
  digits then spread over the skin, 30 times halfway toward the middle of their
  neighbors and back onto the skin. The gaps between the fingers stay open, and
  the hands are not faired.
- A foot is laid on rings: half-planes that turn about an axis across the foot
  in the ankle ring's plane, 25 mm in front of the ankle, from that plane down
  in eight steps until they stand across the foot's forward direction, then
  planes across it at 15, 30, 45, 60, 72, 83, 92, and 97 percent of the way to
  the toe tip. Each ring follows the convex hull of its cross section of the
  foot's skin, which spans the toes and the arch and makes the sole flat: its
  first vertex lies where the ray from the hull's centre at the angle of the
  ankle ring's first vertex meets the hull, and the others follow at the shares
  of the outline at which the ankle ring's vertices lie. The last ring closes at
  the toe tip. The feet are faired in eight Taubin steps.
- No vertex and no face centre of either stays closer to the skin than 1 mm,
  and the skin of the hands and the feet stays 1 mm inside their faces, settled
  in 16 passes, since around a slender fingertip moving one face moves the next
  one back. A face whose point over the skin the held wrist or ankle ring carries
  for more than half stays, so the skin next to a ring may come closer than
  1 mm, but the first net keeps it from coming out.

A version corrected by hand keeps its corrections, because the point attribute
`fitted` holds the shape the last fitting gave, and the difference to it is
applied again. A file that a sync leaves as it was is not saved, and a sync that
saves the file says whether the approval still holds for its content.

Its `model` stage checks that the bodies are the current ones of their files,
that the cage was fitted with the figure scale and the lines of `asset.json` and
is fitted as the fitting gives it now, that every version has the reference's
topology, that no vertex or face centre lies closer to the skin than 1 mm, that
the skin comes out through no face between its corners, that no two faces cross
each other, and that every line crosses its region on every body. The report
names how far each version lies from the skin and how many vertices span pits
more than 5 mm deep. The approval records in `fittedTo` the bodies' exports it
was approved on, and garment templates are approved and exported only on an
approved cage whose bodies still have those exports.

The standard lines are planes at a `share` of a bone's length, at right angles
to the bone, or level across the trunk, which stands upright at rest while its
bones lean; their names, bones, shares, and places are in
[Standard Lines](EQUIPMENT_VISUALS_DESIGN.md#standard-lines).

The review sheet `artifacts/art-previews/Humanoid.png` shows each body in five
rows: the cage from the front, the right side, the back, and the back left,
colored by its distance from the skin, blue at the skin, yellow at 15 mm, and
red from 30 mm, with the lines drawn on it in colors; the cage in one cloth
color from the same sides; the torso up close in the cloth color from the front
right, the back left, the back, and the back right; the left hand from outside
and from the front and the left foot from outside and from the front, up close
in the distance colors; and the head up close in the distance colors from the
front, the right side, the back, and the front left from above. The cage is
drawn 4 mm off its place, so the skin does not shine through.

### Fit The Garment Cage

`art/Cages/Humanoid` exists already; `New-ArtAsset.ps1 -Kind cage -Asset
art/Cages/<Skeleton>` makes the cage of a new skeleton from the first of its
`-Bodies`, with a `-Figure` of 0.06 m and the standard lines.

1. Fit it to the bodies and check it, after a body, the figure scale, or a line
   in `asset.json` changed, or after a region was added to the pipeline:

   ```powershell
   .\tools\art\Sync-CageAsset.ps1
   ```

   Expected result: one report line per body, such as `Cage_Male: <n> faces,
   <near> to <far> mm from the skin, median <m> mm, <k> vertices span pits more
   than 5 mm deep`, `Synced Humanoid: ...` or `Humanoid is up to date: ...`, the
   path of the review sheet, whether the approval holds, such as `Humanoid: the
   'model' approval holds.`, and `Humanoid meets every rule, and its stage is
   approved for this content.` or the command that approves it. A cage that lacks a
   region the pipeline has first prints `Added the regions <regions> to the
   cage, built on Male.`; its other vertices stay exactly where they were. A
   second run prints `Humanoid is up to date: ...` and leaves the file as it is.
   A broken rule stops it with the list of rules, such as a line that does not
   cross its region, faces that cross each other, or skin that comes out through
   the cage between its corners.
2. Look at `artifacts\art-previews\Humanoid.png`, the five rows per body that
   [Garment Cage](#garment-cage) describes. Expected result: the distance colors
   are mostly blue, yellow over the spanned pits, with the standard lines drawn
   as colored rings at the neck, across the upper chest, under the bust, at the
   waist and the hips, on the arms, and on the legs. The cloth follows the waist,
   the small of the back, the bust, and the buttocks, and shows no buttock cleft
   above the hip line, no spine groove, no navel, and no abdominal muscles. The
   hand follows each finger with the gaps between them open; the foot is smooth,
   without toes, with a flat sole and a fold at the front of the ankle; and the
   head is a smooth net from the neck to the crown that spans the ears, the
   nose, and the eye sockets without following them in.
3. Open the file in Blender to look closer:

   ```powershell
   & $env:BLENDER_PATH art\Cages\Humanoid\Humanoid.blend
   ```

   Expected result: the male body in `Cage_Male`; the female and `Cage_Female`
   are hidden in the Outliner below `Fit_Female`, and the reference below
   `CageReference`. Correct a version in Edit Mode only where the fitting needs
   help, save, and run the sync again; it keeps the correction and records the
   new content. Close Blender without saving otherwise, so the file stays as it
   is.
4. Approve it and verify:

   ```powershell
   .\tools\art\Approve-ArtAsset.ps1 -Asset art/Cages/Humanoid -Stage model
   .\tools\art\Verify-ArtAssets.ps1
   ```

   Expected result: `Approved the 'model' stage of Humanoid (faces=<n>).` and
   then the problems of the garment templates fitted to the old content of the
   cage. The cage has no export; commit the `.blend` file and `asset.json`
   together. Every template on the cage records the content of the cage's
   `model` stage in `cageContentSha256`, so after a change of that content sync,
   review, approve, and export its templates again, and their textures, as
   `Build-Art.ps1` does up to the approvals, as [Make A Garment](#make-a-garment) and
   [Make A Garment Texture](#make-a-garment-texture) describe; a template whose
   regions did not move keeps its shape, its weights, and its exported files.
   Expected result: `Verified <n> art asset(s).` once they are all exported. A
   cage file saved again without a change of its content keeps its approval
   and leaves every template and paint layer as it is.

## Garments

A garment template below `art/Garments` is the form of a garment kind without its
look, a mesh skinned to the shared skeleton, such as the short-sleeved top
`art/Garments/Shirt`. It is built from its recipe on the
[garment cage](#garment-cage) named in `cage`, fitted to every body of the cage,
and worn with a [garment texture](#garment-textures); why templates are made
this way is in [Templates](EQUIPMENT_VISUALS_DESIGN.md#templates).

Its file holds, per body, the collection `Export_<Label>` with the body's
armature `<Label>` and the template's version `<Name>_<Label>`, a child of it
with an Armature modifier and the material `M_Garment_<Name>`, a neutral grey.
The bodies' source bodies, eyes, teeth, and tongues are in the `Reference`
collection, unselectable, so they show the body and the fitting reads them, and
the cage's versions `Cage_<Label>` are in the `Cage` collection, taken from the
cage's file, and the scene property `cage_content` keeps the content of the
cage's `model` stage they were taken from, which the scripts pass to Blender
from the cage's content record after making it current. The first body's
version is the one a person shapes; the file opens with the other bodies hidden,
because they stand at the same place.

### Recipes

The recipe in `asset.json` says what the pipeline builds:

| Field | Meaning |
| --- | --- |
| `regions` | The cage regions it covers, of `neck`, `trunk`, `arms`, `legs`, `hands`, `feet`, and `head`, such as `["trunk", "arms"]` for the shirt and `["legs", "feet"]` for the boots. A region without a cut is covered whole. |
| `cuts` | The standard lines where it ends, each with the side it keeps: `head` toward the head of the line's bone, `tail` toward its tail. The shirt keeps the `tail` of `HemHip`, the trunk above the hem, and the `head` of `SleeveShort`, the arm toward the shoulder; the trousers keep the `head` of `Waist` and of `LegAnkle`; the boots keep the `tail` of `BootCalf` and the gloves of `GloveCuff`. |
| `splits` | Optional: standard lines of its regions where it gets an edge loop without being cut there, each optionally moved by an `offset` in meters, above 0 and up to 0.05, `toward` the `head` or the `tail` of the line's bone along the line's normal. A template of a higher layer that ends at that line hides the template's faces beyond the [hide margin](#what-a-template-covers) of its edge face by face, so the recipes split at the line and inside the outer template up to just beyond its largest margin, as far as the margin reaches into the inner template on the first body, rounded up to a millimeter: the trousers at `HemHip` and 11, 13, 15, 17, and 19 mm toward its `tail`, inside a top's hem, whose margin is 10 mm at the back of the hip and up to 16.5 mm at its front, so the faces between go where the margin around them allows, the loops at 13 and 15 mm where the trousers come through the back of a top in motion, and the loop at 19 mm because a top is fitted 1.5 mm outside the trousers worn under it, which that loop holds closer to them there, so they come through it least in motion; and at `BootCalf` and 40 mm toward its `tail`, inside a boot's shaft, whose rim stands off them; the long shirt and the robe at `GloveCuff` and 11 mm toward its `tail`, inside a glove. A split comes after the UVs are unfolded and moves no vertex: it runs through a vertex within 1 mm of its plane, and its new vertices take UVs interpolated along the edges they cut, so a texture keeps its place. A wrong entry fails with a message that names the entry and the key; `toward` and `offset` come together. |
| `over` | Optional: where it lies over a garment of another slot, each a line of its regions, the side of it the inner garment keeps, and the inner garment's band. The shirt lies over `tight` trousers on the `head` side of `Waist`: there it is built, and may lie, the inner band's distance farther off the cage, rising to it over 30 mm before the line, also in the regions that hang from the line's region, such as a hand from its arm, and its cloth keeps 1.5 mm outside the real surface of the templates of lower layers it lies over there, their triangles as the game draws them, instead of the inner band's nominal distance, so a later build of an inner template that comes closer fails the outer one's sync. The boots lie over `tight` trousers on the `head` side of `LegAnkle`, the gloves over a `tight` sleeve on the `head` side of `SleeveLong`, and the cloak's yoke over a `mid` top on the `tail` side of `CloakYoke` and the `head` side of `Neckline`, so it leaves room for a padded top. A point is over the region of the template it lies nearest. A [turned edge](#turned-edges) takes its band from the cloth it is turned from: it may lie under the inner garment's surface, down to the cage, inside its own cloth. |
| `hang` | Optional: its [loose parts](EQUIPMENT_VISUALS_DESIGN.md#loose-parts), which hang on the [cloth chains](#skeletons), each the trunk line it hangs from, which the recipe also cuts at, its `side`, `all` around the trunk for a skirt or `back` across the back for a cloak, its `chains`, `skirt` or `cloak`, and its `flare`, how much wider it grows toward its hem, from 0 to 0.1 m. A part across the back may also have a `top` and a `width`, shares above 0 and up to 1.5 of how far the shoulder joints lie from the middle: with a `top`, the template keeps only its middle within the top, cut straight down there, and its back only above the part's line, with a cut that need not be a cage line, and the part hangs from the template's open edge at its line behind the bone there; with a `width`, no smaller than the top, the part fans out to that width by the first joint of its chains. A part may name a line of the legs as `to`, its hem: it then hangs down to that line's height on every body, between the body's two sides, instead of to the tips of its chains, and the sync stops when that line lies below the tips. The robe hangs a skirt `all` around from `SkirtWaist` on the `skirt` chains with a flare of 0.04 down to `SkirtHem`; the cloak hangs its back from `CloakNape` on the `cloak` chains with a flare of 0.03, a top of 0.72, and a width of 0.8. A template with loose parts names the matching `cloth` profile. |
| `ease` | More room at the front and the back of the lower armholes, in meters, up to 0.03, fading out over 60 mm. The templates have none; see [Templates](EQUIPMENT_VISUALS_DESIGN.md#templates). |
| `hem` | How deep every open edge is turned inward, in meters, up to 0.01, such as `0.002`. |

A recipe of `"modeled"` instead makes a template whose first version a person
models by hand over the cage, such as a hairstyle or a helmet. A hair template's
first version also has the shape key `UnderHeadwear`, the hair pressed flat under
headwear, shaped by hand like the hair; every other version gets it fitted the
same way as its basis, and a hand correction of it is kept the same way too, in
the point attribute `fitted_under_headwear`.

### Building A Template

`tools/art/Sync-GarmentAsset.ps1` brings the file up to date:

- A body that changed in its own file is taken in again, and so are the cage
  versions when the cage's content changed. The first version is bound to the
  old cage and placed on the new one, so it follows; where its cage did not
  move, such as when only other regions were added to the cage, it stays exactly where it was, so every other version is
  fitted as before.
- A new template, or one synced with `-Rebuild` after its recipe changed, gets
  its first version built from the first body's cage: the cage's faces of its
  regions, cut at its lines, every cage vertex within 8 mm of a line first moved
  onto it, so the edge runs straight; seams where clothes have them, between the
  front and the back of the trunk and the neck, down the sides and over the
  shoulders, along the underside of each arm, under the upper arm and on along
  the inside of the forearm, each measured around its own bone, along the inside
  of each leg, where an arm or a leg meets the trunk and where a hand or a foot
  meets its limb, between a glove's palm and its back, by the side of the hand
  each face looks to, so the seam runs along the sides of the fingers, and around
  a boot's sole, where its faces look down, and up the back of its heel, while a
  neckline runs on from the trunk up the neck without a seam; UVs unfolded along
  them; edge loops at its `splits`, which interpolate the UVs and move no
  vertex; an offset from the cage into the band, 10 mm for `mid` and 20 mm for
  `outer` and `loose`, and for `tight` as close as
  [Layer Bands](EQUIPMENT_VISUALS_DESIGN.md#layer-bands) says, and more where
  `over` lifts it over an inner garment; the ease; every vertex and face centre
  settled into the band from the cage, the band taken again where a point ends up
  until it holds, and where it lies over an inner garment kept 1.5 mm outside the
  real surface of the templates of lower layers it is worn over, which the sync
  puts on the bodies from their own files and never saves; every open edge
  turned inward with a 12 mm flap, whose faces take the look of the edge in the
  texture, laid from the settled cloth as [Turned Edges](#turned-edges)
  describes; and last the corners of every quad turned, with their UVs, so that
  it splits from its first corner to its third, as the export and the game split
  it, across the diagonal it folds outward across on the first body. A quad and
  its mirror image across the middle plane are decided together, from both
  their bends, so they split along mirrored diagonals and the cloth folds alike
  on both sides; a quad that bends by less than 0.01 mm takes its shorter
  diagonal, and two of the same length are decided by their geometry. A rebuild
  decides the diagonals again on the shape the first version ends with, the
  handmade shape carried over and the turned edges laid, so no quad splits along
  a valley that shape folds it into. The build
  goes over the cage's vertices, edges, and faces by their indices, so building a
  recipe again gives every vertex, face, and corner the same order. Its loose
  parts hang in rings from the open edge at their line down to the tips of their
  chains, on the scaffold and with the weights that
  [Loose Parts](EQUIPMENT_VISUALS_DESIGN.md#loose-parts) describes, and a cloak
  with a width fans out to it by the first joint of its chains. The point
  attribute `hang` marks the vertices of each loose part, `hang_top` and
  `hang_ring` their column and ring, and the face attribute `hang_face` its
  faces; every other version places them on its own body's scaffold. The point
  attribute `built` holds the shape the recipe gave, so a rebuild carries a
  shape made by hand over: each new vertex takes the difference at the nearest
  vertex of the old cloth; the old turned edges carry none, since they are laid
  from their cloth.
- Every other version is fitted from the first through the cage, as
  [Fitting Every Body Type](EQUIPMENT_VISUALS_DESIGN.md#fitting-every-body-type)
  describes: each vertex is bound to a triangle of the first body's cage, of
  every region but the hands and the feet that the template does not cover, by
  its barycentric coordinates and its offset in the triangle's frame, and placed
  at the same triangle and offset of the other body's cage. A modeled template
  instead moves with a smooth field of the cage's moves: each vertex by the
  moves of the cage's vertices, weighted by a Gaussian of their distance beyond
  the nearest one, 15 mm wide plus 0.7 times the vertex's distance from the
  cage. The band is measured from the same regions. A vertex or a face centre of
  the cloth that then leaves the band moves back into it, and the turned edges
  are laid again from the cloth where it now lies, also from a cloth corrected
  by hand. A version corrected by hand keeps its corrections, because the point
  attribute `fitted` holds the shape the last fitting gave, and the difference
  to it is applied again.
- The first version's turned edges are laid again from its cloth where it lies,
  in place, after the cloth vertices they are turned from keep their room off
  the cage: only positions change, never the order of the vertices, faces, or
  corners, so the textures stay in their UVs. `built` follows for them.
- Every vertex gets the weights of the nearest skin of the body part its cage
  face lies on, the cage's region and side, and on a hand the face's finger or
  thumb with the palm, interpolated from the corners of the skin's triangle,
  limited to four bones, and not smoothed. The point attribute
  `transferred_weights` holds a checksum of the weights the last transfer gave
  each vertex of the first version, so a vertex whose weights were painted by
  hand keeps them, also in the other versions. `-Weights` transfers every weight
  again. A turned vertex takes its weights from its cloth instead, as
  [Turned Edges](#turned-edges) describes.
- At the crotch the fixed cloth, never a loose part, then follows the skin's
  crotch rule of `human_base/rig.py`, which holds a rebuilt body's crotch with
  the pelvis. Its hold, the share of its thighs' weights, of both upper and lower
  legs, that moves to its other bones in their proportions, or to `Hips` where
  it has none, goes by its distance across the body from the middle plane: whole
  within the skin's held half width of 15 mm and nothing from the skin's
  `CROTCH_RADIUS` of 70 mm on, smoothly between. It holds within a band about the
  crotch, the lowest vertex of the body's middle line under the trunk within
  50 mm of the hip joints' depth: wholly from 120 mm behind it to 60 mm in front
  and from 40 mm below it to 30 mm above, fading smoothly to nothing at 170 mm
  behind, 100 mm in front, 90 mm below, and 60 mm above. The lengths are those
  of a body of the rig's reference height, 1.785 m, and scale with a body's
  height, as the skin's do. The hold is then evened out over the cloth's edges
  three times, each time halfway to the mean of its neighbours, as the rig evens
  the skin's weights, and the weights keep four bones. The rule reaches only the
  trousers today; every other template's fixed cloth lies outside the band.
- A file that this leaves as it was is not saved. A sync that saves the file
  records its content and says which approvals still hold: an edit of only the
  weights, for example, leaves the `model` approval holding.

### Turned Edges

A turned edge is always derived from its cloth, on every version and at every
sync, and never settled into the band on its own: a settle of its own pushed it
out through the cloth where the body leans over the hem, such as a belly over a
shirt's hem, and turned its faces up, which lit as a bright strip in the game.

- The cloth vertices a turned edge is turned from keep at least 0.5 mm off the
  cage, where their band lets them come closer: the settle moves one that lies
  closer, on every version and at every sync, so its fold has room above the
  cage. A loose part's hem hangs on its scaffold and is never settled.
- The rim lies under the cloth's vertex it is turned from, along the cloth's
  normal there, the recipe's `hem` deep, or as deep as the cloth lies off the
  cage there if that is less, so it never lies under the cage.
- The flap runs 12 mm back across the cloth from the edge and lies at the same
  depth inside the cloth along its normal at the flap's top, or as deep as the
  cloth lies off the cage there if that is less.
- A turned vertex takes its skin weights from its cloth, so its fold moves with
  the cloth in every pose: the rim the weights of the cloth vertex it is turned
  from, the flap the weights of the cloth over it, interpolated on the cloth's
  face, also where the cloth's weights were painted by hand, and on a loose part
  the weights its scaffold gave the cloth.
- The pipeline finds a template's turned edges from its faces alone: the tops
  of the flaps are the border of the mesh, each joins its rim, and the rim joins
  the cloth's vertex. Nothing is stored for them, so they survive a fitting, a
  rebuild, and an export.
- A hand edit of a turned vertex, of its position or its weights, does not
  survive a sync, and until the next sync the `model` stage fails. Shape the
  cloth instead; the turned edge follows it.

### Checks And Review Of A Template

The sync then writes the review sheet `artifacts/art-previews/<Name>.png`. Per
body it shows three rows: the body in the template from four sides with its
torso from the front and the back, or for a template shorter than 40 percent of
the body, such as a glove or a boot, the one on the body's left up close from
outside and from the front, the template showing a grid in its UVs, so the sheet
shows how they stretch; the template in the test poses of `poses.py`, with the
cloth chains of its loose parts laid in the target pose that the game shows
(`cloth.py`), so a skirt follows the legs; and the body without it, its covered
faces orange, the skin kept visible at the template's edges yellow, the
pulled-in vertices blue, and the skin that comes through it in motion red. The
review file `artifacts/art-previews/<Name>_poses.blend` places the bodies side by
side with an action that steps through the test poses. Last, the sync checks
every rule and reports what the template covers.

The `model` stage checks that the bodies are the current ones of their files,
the cage versions those of the cage's current content, and the cage the approved
one, that every body has a version with the first version's topology and one
material, within the triangle budget and with every vertex and every face
centre in its band from the cage, a hair
template's also in its shape key `UnderHeadwear`, and that the fitting, the
weights, and the turned edges of every version, their positions and their
weights, are as a sync leaves them. Where the recipe says the template lies over
an inner garment, its band's least distance is 1.5 mm outside the real surface
of the templates of lower layers it is worn over there, their triangles as the
game draws them, which every stage puts on the bodies from their own files, so
a later build of an inner template that comes closer fails the outer one's
sync. Of a recipe template, it checks that every quad and its mirror image
across the middle plane of the first version split from their first corners
along mirrored diagonals; the message names how many pairs do not and one of
them. A quad that is its own mirror image, across the middle, is no pair. A
point is too close by its straight
distance to the cage's nearest point and too far by its distance along the
normal of the cage's nearest face, as the recipes build the cloth. A turned
edge's vertices may come down to the cage, and the centres of the faces that
touch them, which are flat between points that may lie on a round cage, may cut
under it by less than the `hem` depth; neither lies farther from the cage than
its cloth may. The cloth vertices a turned edge is turned from keep 0.5 mm off
the cage. Of a template whose recipe turns its edges, the stage also checks on
every version that no turned vertex lies outside the cloth it is turned from and
that every rim lies at least 0.3 mm inside it; the message names the cloth
vertex at the thinnest fold, which is the one to shape, and says when the cloth
there lies too close to the cage for a fold. It also checks that every corner
normal the export writes at the hem's outer edge leans out of the cloth and out
of the opening: down and out at a bottom hem, up and out at a neckline, out
along the arm at a cuff. A normal that leans back into the garment breaks the
rule, and the message names the version, how many hem vertices break it, and the
worst one. Neither rule is decided by the rounding of float32 positions and
normals.

The `rig` stage adds the rig rules of skinned assets for each version on its
armature, the motion check, and the pair check:

- The motion check follows the drawn skin under the template, at every visible
  body vertex and inside every drawn body face, through the rest pose, the test
  poses, and every frame of every clip of the skeleton's animation libraries, as
  [Cover Masks](EQUIPMENT_VISUALS_DESIGN.md#cover-masks) describes and
  [What A Template Covers](#what-a-template-covers) measures. A face whose skin
  lies through at rest breaks the rule; skin that comes through in motion is
  reported per phase, the rest pose, the test poses, and each clip, as the pair
  check reports, so a fault in the jog is not hidden behind a deeper one in
  `Death`, with how many faces come through by more than 1 mm and the deepest
  place of all, such as `Trousers_Female: 143 drawn faces of the body lie under
  it, with 147 visible vertices; the deepest their skin comes through it in 518
  poses and clip frames: the rest pose 0.00 mm, the test poses 5.87 mm (sit),
  Death 10.39 mm (frame 28), Idle 0.00 mm, Walk 0.00 mm, Jog 6.31 mm (frame 5),
  ...`, and the review sheet shows those faces red.
- The pair check puts every exported template of another visual slot and a
  lower layer of the cage, such as the trousers under a shirt, on the bodies from
  its own file, works out which of its faces this template hides, as
  [What A Template Covers](#what-a-template-covers) describes, and follows its
  drawn triangles, those the game draws: of the faces whose body face this
  template leaves visible and that it does not hide, split from their first
  corner as the game splits them, of the cloth that faces away from the cage,
  since a turned edge's rim and flap lie under it. Each triangle is followed at
  its corners, along its edges and inside it every 3 mm, and where the edges of
  this template's triangles cross it or their corners lie over it, of every
  triangle of this template within the inner triangle's radius and the band's
  reach of its centre, the places where this template's cloth, without its own
  turned edges, can lie deepest
  under it, wherever this template is over it at rest or it already lies outside
  it, with this template's outer side at most 10 mm inward of it, since a
  crossing of the two starts at a depth of nothing and farther in lies cloth
  elsewhere, such as a hood's brim seen from the hair in its opening, through the
  rest pose, the test poses, and the clips. At rest, any depth
  above a rounding of 0.05 mm breaks the rule, and the message names the inner
  template, the version, so the body, how many triangles lie through, the
  deepest place in millimeters, and its position. The report names, per phase,
  the rest pose, the test poses, and each clip, the deepest any of them comes
  through and where, and how many come through by more than 1 mm, such as
  `Trousers under Shirt_Male: 193 drawn triangles of it lie under Shirt_Male; the
  deepest they come through it in 518 poses and clip frames: the rest pose 0.00
  mm, the test poses 1.04 mm (sit), Death 6.53 mm (frame 25), Idle 0.00 mm, ...`.
  The faces this template hides, but for their turned edges, are followed too,
  by the cover rule that hid them: in the rest pose with the chains laid, every
  test pose, and every clip frame, from each of a face's samples, held where they
  lie on the face at rest, a ray of its cone that gets out past this template's
  opaque cloth and what else the game draws there within the band's reach shows
  a hole when the look along it, which comes in from outside and passes the face
  the game leaves out, goes on past the sample without first meeting the front
  of this template's opaque cloth within the reach, the side that faces the
  look. Both the ray and the look take this template's cloth as the game draws
  it, the rims and flaps of its turned edges too, which a look under a hem meets.
  Only what the game draws blocks the ray: the inner template's faces but those
  this template may hide, and the body's faces but those the game removes under
  the pair, the ones this template covers and the ones the inner template's own
  cover mask covers on that body. A hidden face that has come out through the
  cloth has that front behind it, so the look ends on the cloth and sees no
  hole. A face with a hole counts, with its triangles. The rig stage first
  chooses the hidden faces, one list for every body: those the rest rule gives
  on the first body, without every face that shows a hole on any body at rest or
  in any frame of any clip of the animation libraries, followed with all of those
  faces left out of what blocks, so a face that is then drawn only blocks more;
  such a face stays drawn and is followed like every other drawn face, and the
  report names it, its body, the first frame, and its place, such as
  `Shirt_Female would show a hole at face 694 of Trousers in Death frame 15 at
  (...) m, so it leaves that face drawn`. The test poses are reported, not chosen by. The
  choice and the report follow the same poses and frames in the same run, so
  the report adds, per phase, the most hidden triangles that show a hole in one
  pose or frame, which only the test poses can have, such as `...; of the 242
  triangles it hides, the most that show a hole: the rest pose 0, the test
  poses 0, Death 0, Idle 0, ...`. A list that went stale, chosen over another
  build of a template of a lower layer, is caught by the export's records, as
  [Export Of A Template](#export-of-a-template) describes: the template's `pairSources`, which
  `Verify-ArtAssets.ps1` compares with the current content of the templates of
  lower layers, and the data's `innerFaceCounts`, which the catalog compares
  with the inner template's model before it hides any face of it.
  A body wears one garment per visual slot, so templates of the same slot,
  such as a shirt and a jerkin, are not checked against each other, and a
  template is not checked over a slot it hides, such as the robe over trousers.
  Hair is checked under headwear in the form the headwear gives it: in its shape
  key `UnderHeadwear` under `"hair": "compress"`, as it is under `"show"`, and
  not at all under `"hide"`.
- Two templates that cannot meet cost no frame. They cannot meet when the rest
  rule hides no face of the inner template and on no body, at rest with the
  chains of both laid as the pair check lays them, any triangle of the inner
  template, split from its first corner as the game splits it, has the outer
  template's cloth within its radius and the band's reach of its centre: the
  test the pair check makes before it follows a triangle, here over every
  triangle, also those it leaves out. Then the pair check follows no place and
  no hidden face, so it computes no cover of the inner template, steps through
  no pose or frame, and reports what stepping reports, such as `Boots under
  HoodUp_Male: none of its drawn triangles lies under HoodUp_Male in 518 poses
  and clip frames`, with the same data. Such pairs are, for example, the boots,
  the gloves, and the trousers under the hoods, the helmet, and the hair. The
  boots under the cloak do meet by this test: the cloak's hem at the back hangs
  about 1 cm above the boots' shafts at rest, whose triangles are about 40 mm
  wide, so it lies within their radius and the 30 mm reach of its `tight` band,
  although none of their triangles lies under it. The motion check and the pair
  check of any pair step through no frame either when they find nothing to
  follow at rest, and count the poses and frames instead. They still lay the
  cloth chains of the last frame: laying a chain leaves the float rounding of
  its turn in its bones' scale, which `poses.rest` keeps and the export writes
  into the FBX, so the bones end as stepping through every frame leaves them.
- Both checks follow loose parts with their cloth chains laid in every pose and
  frame in the target pose that the game shows (`cloth.py`), so they report a leg
  that comes through a skirt or a cloak; the swing of the springs is reviewed in
  [the Wardrobe](#the-wardrobe).

A template is approved and exported only on an approved cage whose bodies still
have the exports the cage was approved on, so it is never fitted to an obsolete
cage.

### What A Template Covers

`coverage.py` computes the masks that
[Cover Masks](EQUIPMENT_VISUALS_DESIGN.md#cover-masks) describes, for each body
in the faces and vertices of the body's editable mesh:

- **The cover mask**: from the centre of a face and a point toward each of its
  corners, seven rays go out, one along its normal and six in a cone of 25
  degrees around it, through up to four transparent layers of the texture. The
  face is covered when every ray meets the garment where its texture is opaque,
  its alpha at least 0.5, or the body, within the garment's reach: the distance
  from the skin to the cage there and the band's reach above it, 30 mm for
  `tight`, 50 mm for `mid`, 100 mm for `outer` and `hair`, and 300 mm for
  `loose`. At least one ray must meet the garment. A face that only the body
  hides, such as the inside of the mouth, stays visible. A face whose centre or
  a corner lies within the edge reach of an open edge is never covered; the open
  edges are the borders of the mesh, the lines its loose parts hang from, and
  the lines where its texture is cut, sampled every 5 mm where the garment is
  opaque. Loose parts cover nothing where they swing, below where their chains
  hang from, and a template with `"covers": false`, such
  as the cloak, covers no face at all.
- **The underlay**: the body face under each garment face, along its inward
  normal, or the nearest one.
- **The pull-in**: a visible body vertex closer to the garment than the band's
  smallest distance, and at least 3 mm, moves inward by the difference, at most
  3 mm. Inside a drawn face, split as the game splits it, points every 3 mm are
  measured too, and the closest of them counts for all three corners of its
  triangle, which move inward by what it lacks, at most 3 mm, so the middle of a
  face under a hem keeps its room as its corners do. The pull-in is measured at
  rest only; skin that comes through in motion is reported by the motion check
  below, not pulled in. Unity's import splits every exported quad from its first
  corner to its third: a check in the Editor found every quad of both bodies, the
  trousers, and the shirt split that way and none the other way.
- **The hidden faces**: per template of a lower layer worn under the garment,
  the ones the pair check takes, the faces of it that the garment hides, by the
  cover rule with that template as the surface. From the centre of a face and a
  point toward each of its corners, seven rays go out 0.1 mm off it, as above,
  within the garment's band's reach; the face is hidden when every ray meets the
  garment's cloth where it is opaque, or the body or the inner template itself,
  and at least one meets the cloth, and the face, its outline and its inside,
  lies beyond the hide margin of every place of the open edges of the cloth:
  the borders of its cloth without its turned edges, where its hem shows, the
  line where its loose parts begin to swing, and the lines where its texture is
  cut, found every 2 mm. A look into an opening reaches about four times the gap
  between its edge and the surface under it, so the margin of a place is four
  times its gap, how far it lies from the nearer of the inner template's surface
  and the body, and at least 10 mm; a segment of a border takes the larger gap
  of its two ends. A top's hem that stands up to about 4 mm off the trousers
  keeps a margin of up to 16.5 mm over them, a boot's rim about 10 mm off them one
  of about 41 mm, and a glove's cuff over a sleeve the least, 10 mm, while hair
  and headwear, which stand far off what lies under them, hide little of it.
  The margins are measured on every body, each with that body's versions at
  rest, and a face is hidden only when it lies beyond them on every body. The
  recipes lay their `splits` just beyond the margins on the first body; every
  other body's versions are fitted from it through the cage, which spaces the
  split and the outer template's edge closer on the smaller female body, where a
  face the split bounds may come within the margin and stays drawn. A face that
  comes within a margin by less than 1 mm, as far as a split may run off its
  plane, still counts as beyond it. A rim or a flap of the inner template's
  turned edges goes with the cloth face it is turned from. Cloth of the inner
  template that swings, such as a cloak's back, is never hidden, and neither is
  anything under cloth of the garment that swings; a template with
  `"covers": false` hides nothing at all. The checks report the margins on every
  body, such as `Shirt_Male keeps a hide margin of 10.0 to 16.5 mm over Trousers
  where its open edges lie within it of them`. The rest rule is applied on the
  first body, and the rig stage leaves out of that list every face that shows a
  hole on any body at rest or in a clip, as the pair check above says, so the
  list is one for every body and every body's data names the same faces. These
  rules hold three things: on every body at rest, no hidden face lies within the
  hide margin of an open edge; at rest and in every frame of every clip, no ray
  of a hidden face's cone, from its samples and within the band's reach, shows a
  hole past what the game draws; and in the test poses such holes are reported,
  not ruled out. A look from outside the cone, or one that reaches farther than
  the band's reach, is not checked.
- **Motion**: the skin is followed at every visible vertex, along its normal,
  and at places inside every drawn face, split as the game splits it, along the
  triangle's normal: points every 3 mm along its edges and inside it, and the
  places where the corners and the edges of the garment's triangles near it lie
  over it, where the cloth can lie deepest, as the pair check follows a drawn
  triangle. Every such place that the garment is over at rest keeps the point of
  the garment over it, and so does one that already lies outside the garment at
  rest, with the point of the garment just inward of it, at most 50 mm inward
  for a vertex and 10 mm for a place inside a face, as the pair check takes a
  drawn triangle: farther in, the outer side belongs to cloth elsewhere, such as
  a hood's side seen from behind an ear.
  A point of the garment only counts when no body lies between it and the place,
  outward or inward, so the hair that the ray from an eye's corner meets beyond
  the face is not over the eye. The body, with the pull-in applied, is measured
  at rest and in every test pose and every frame of every clip, a place inside
  a face where the game's triangle puts it between its corners, and a face comes
  through the garment when a place of it, or a vertex of it, lies more than 1 mm
  outside the cloth at its point with the cloth between it and the inside. The
  report names the deepest place per phase, and a face that lies through by more
  than 1 mm at rest breaks the rule. The rule needs the clips, so it runs at the
  template's `rig` stage,
  which its export runs, and a texture with transparent parts checks its own
  masks the same way before its export writes anything.

The faces that only the cone of rays leaves visible are the garment's openings,
and the review sheet shows the mask with the skin kept visible at the garment's
edges, so a mask that is too small or too large shows there; a check that the
faces under opaque cloth are covered would only repeat the rays.

### Export Of A Template

The export writes one model per body of the cage,
`Models/Garments/<Name>_<Label>.fbx`, from its `Export_<Label>` collection, the
template skinned to that body's armature, and its data
`Models/Garments/<Name>_<Label>.json`. The data holds `bodyFaces`,
`bodyVertices`, and `garmentFaces`, the `covered` body faces, the `underlay` of
every garment face, the `pullInVertices` with their `pullInDistances` in meters,
the hidden faces of the templates of lower layers worn under it, the
`innerTemplates` by name in order, `innerFaceCounts`, how many faces each had
when its hidden faces were found, so the catalog refuses a model of it with
other faces, `innerHiddenCounts`, how many faces of each it hides, and
`innerHiddenFaces`, those faces in each template's own faces, one template after
the other, and the template's own `garmentVertices`,
`garmentFaceSizes`, and `garmentFaceVertices`, by which Unity finds the face of
each triangle. The body faces and vertices it names are those of the body's
topology, which the body's export writes next to its model: its editable mesh's
`vertices` in Blender's world space, `faceSizes`, `faceVertices`, and
`faceZones`. Its `schemaVersion` is 3. Its material has no image, so the game
shows a template only with a texture.

A template is exported after its cage is approved and its bodies are exported,
while the approvals of the animation libraries of its skeleton hold for their
content, and it records the cage's content, the fit of the bodies' exports, and
the content of the libraries, so `Verify-ArtAssets.ps1` fails when the cage's
content changed, a body has a newer fit, or a library other content than the
one the template was checked in: sync, review, and export the template again.
While the cage's content record is not current, it asks to run
`tools/art/Update-ArtContent.ps1` for the cage first. It also records the
content of the templates it was checked over in the pair check, whose faces its
data names, and needs their approvals to hold for that content, not their
exports, so templates exported together, such as after a change of the cage,
run in any order and at once and write the same files; a template that the
same run exports for the first time counts as exported for the others. A
template or a texture checked over another content of a template of a lower
layer than it has now stops the verification with `was checked over other
templates of a lower layer than the current content of its cage's exported
templates` until it is exported again.

### Cache Of The Garment Checks

The checks of what a template covers take most of a sync, an approval, and an
export, so their results are cached below `artifacts/art-cache/checks`, which
Git ignores and which may be deleted at any time; `check_cache.py` keeps it. A
result computed once, by a sync or an approval, is taken by every later run
until something it depends on changes, so the export of unchanged content
computes nothing. Two kinds of unit are cached:

- A template's own check on one body: what it covers, the underlay, and the
  pull-in, and at the `rig` stage the motion check. Every review sheet of a
  template or of a masked texture, a sync's and one that `Render-ArtPreview.ps1`
  renders, takes its masks through the same unit, so a sync computes them once
  for its sheet and its checks.
- A pair, the template over one template of a lower layer, on every body: the
  hide margins, the faces it hides, one list for every body, with the faces
  left drawn where a look past it shows a hole, and at the `rig` stage the pair
  check on every body.

A masked texture's units are those of its template with the texture on it, so
its masks, motion check, and pair checks are cached the same way. An entry is
`<key>.json`, which names everything the unit read and holds its plain data,
and `<key>.npz`, its arrays, which are read without pickle. The key is the
SHA-256 of what the unit reads:

- the content of the template, its `rig` stage, which Blender reads from the
  file it checks as the content record does, and of a texture its `image` part;
- the `rig` stage of every template of a lower layer, from its content record;
- the fit of every body, the `fitSha256` of its export while that export is
  current;
- the content of the cage, from its record, and the content of every
  animation library with its clips, from its record, at the `rig` stage;
- the fields of the templates' `asset.json` that the checks read, `band`,
  `recipe`, `covers`, `hair`, `slot`, `hides`, and `layer`;
- the review stage and the kind of unit;
- one fingerprint of the pipeline's code, every file of
  `tools/art/blender/art_pipeline` and `art_pipeline_cli.py`, the skeleton
  definition, and Blender's version, so any change of the code misses every
  entry.

The PowerShell scripts make the content records of these assets current first
and pass the fingerprints; Blender checks that the bodies in the template's
file have the fit given and that the template was fitted to the cage content
given. A unit whose fingerprint is missing or does not hold is computed and not
cached. The cache never stops a command: when the record of an asset cannot be
read or made current, or a body's export is not current, its fingerprint is left
out with a line such as `The checks that read art/Characters/Human_Female are
not cached: <reason>.`, and the asset's own commands report its problems.

Blender reads the clip libraries, the templates of lower layers, and for a
texture's sheet the texture from their files on disk while the checks run, so
before it writes any entry it hashes each such file again: its SHA-256 must be
the `sourceSha256` of its content record and that record's fingerprint the one
given. A texture's checks also read its own file again, which must still have
the bytes it had when the run began. A file saved with other content during the
run stops every further entry of that run, whose line then says so, such as
`Not cached, since Human_Actions changed during the run.`; the run still uses
what it computed for its own report and export, which its own records guard.

Several exports run at once, so each file of an entry is written to a temporary
name and moved into place, the arrays before the data, and the data names the
hash of its arrays: a run reads a whole entry or none, and a write that another
run holds open is left out. A cached result is the computation's own: the
report lines are made from it as from a computed one, and the export writes the
same data. The checks leave the
bones of the cloth chains with the float rounding of their last laying in their
scale, which the FBX export writes, so every unit keeps that scale too, and the
export puts it back before it writes the models.

`Export-ArtAsset.ps1`, `Approve-ArtAsset.ps1`, `Sync-GarmentAsset.ps1`,
`Sync-TextureAsset.ps1`, and `Render-ArtPreview.ps1` take `-NoCache`, which
computes every unit and neither reads nor writes the cache. Each ends an asset
with a line of the seconds it took and how many units came from the cache, such
as `Shirt took 21.4 s; 6 of 6 check units came from the cache.`, with each
reason once when a unit could not be cached, such as `Not cached, since the body
Female in the file of Shirt has another fit than its export.` An asset without
such checks gets only its seconds.

To see the cache work, export a template twice:

```powershell
.\tools\art\Export-ArtAsset.ps1 -Asset art/Garments/Robe
.\tools\art\Export-ArtAsset.ps1 -Asset art/Garments/Robe
git status --porcelain -- art src
```

Expected result: both runs print the same report lines and `Exported Robe to
...`; the second ends with `Robe took <n> s; 3 of 3 check units came from the
cache.` in a fraction of the first one's time, and `git status` lists nothing,
since both wrote the same files. With `-NoCache` the run takes as long as the
first and writes the same files again.

### Cloth Parity

The review sheets, review files, and checks lay a garment's cloth chains in the
target pose that every visible body in the game shows, without springs, as
[Target Pose And Parity](EQUIPMENT_VISUALS_DESIGN.md#target-pose-and-parity)
explains. The laying is `cloth_core.py`, which uses only the standard library and
works on plain data, and the Unity client's `ClothPose` is its source: it follows
`ClothPose` step by step with Unity's own vector and rotation arithmetic, such as
`Quaternion.Slerp`, which above a dot of 0.95 blends straight and normalizes.
`cloth.py` measures in Blender what the laying needs, the capsules from the
body's vertices as posed and in the bind pose, the legs' turns against the hips,
the chains as they hang from their posed parents and in the bind pose with their
neighbours, and the garment's reach about each particle, hands it to
`cloth_core.lay`, and turns the chain bones to point at the laid particles. Both
read their constants from the `cloth` section of the skeleton; see
[Skeletons](#skeletons).

`MMORPG > Tools > Art Pipeline > Write Cloth Fixtures` captures both bodies in
the robe, the cloak, and both, in frames of their clips, in leg poses set by
hand, and in seeded random leg poses, each with what the client's Burst jobs
laid and the capsules as posed and in the bind pose, and writes them to
`tools/art/blender/tests/fixtures/cloth_kinematics.json`, one case per line.
`ClothPoseTests` lays every case again with `ClothPose` and captures the bodies
again, and fails with the menu to run when the target pose, a body, or a garment
changed; the Python tests lay every case with `cloth_core.py` and must come
within 0.01 mm of it, and check that the bind pose moves nothing but a cloak over
a robe. They run without Blender, in CI and in `tools/Test-Changes.ps1` for a
change below `art/` or `tools/art/blender/`:

```powershell
python -m unittest discover -s tools/art/blender/tests
```

Expected result: `Ran <n> tests` and `OK`. `tools/art/Update-UnityArt.ps1`,
which `tools/art/Build-Art.ps1` runs after an export, captures the cases again
and writes the fixtures when a case or a name differs from the file or a number
by more than `5e-6`, so every point it keeps lies within `5e-6 * sqrt(3) =
8.7e-6` of the file, inside the `1e-5` of distance that `ClothPoseTests`
allows; an export of a body or a garment that the cloth hangs on rewrites them,
and an export of unchanged ones does not. The open Editor's own follow-up of an
export does not capture them. After a change of the cloth in the client, run the
menu. Commit the fixtures and run the Python tests again.

### Python Tests Of The Pipeline

`tools/art/blender/tests` holds the Python tests of the pipeline's code. They
use `unittest` and the standard library, so they run without Blender, in CI and
in `tools/Test-Changes.ps1` as the check `blender-python` for a change below
`art/` or `tools/art/blender/`:

```powershell
python -m unittest discover -s tools/art/blender/tests
```

Expected result: `Ran <n> tests` and `OK`, in a few seconds. What the tests pin
is the logic that needs no Blender, and most of the pipeline's modules start
with `import bpy` although most of their functions never use it, so
`tests/blender_source.py` reads such a module the way it is written: `load()`
takes the named definitions from the module's source, with the constants,
functions, and standard library imports of the same file that they use, and
runs only those. It runs the module's own text, with no copy and no stand-in for
`bpy`; a definition that needs Blender fails in the test that reaches it with a
`NameError` naming `bpy`, `bmesh`, or `mathutils`. A function that comes to need
Blender is moved, with its test, into a module of the standard library of its
own, as `cloth_core.py` is, and the old module imports it back.

| Test file | Pins |
| --- | --- |
| `test_cloth_core.py` | The kinematic cloth against the Unity fixtures; see [Cloth Parity](#cloth-parity). |
| `test_blender_source.py` | The loader itself. |
| `test_skeleton.py` | The reading and rules of `art/Skeletons/*.json`, the Humanoid's avatar bones, sockets, and chains, the eyelids of `Humanoid`, and the bones no clip keys. |
| `test_garment.py` | The bands, offsets, slots, names, recipe text, recipe regions, and manifest reading of garment templates, checked against every `art/Garments/*/asset.json`; the turned edges and the border read from a mesh's faces, the fold rule, its message, its least fold, and the room of a hem, and that the build and every function of `hang.py` go over their elements by index, never over a set. |
| `test_cage.py` | The cage's regions, stages, zones, rings, digits, names, and manifest reading, checked against `art/Cages` and the exported body topologies, and the order of the fitting's rounds and reaches. |
| `test_export_validate.py` | The variants argument, the sRGB conversion, the name and transform rules, and the kinds and review stages, checked against every `asset.json`. |
| `test_texture_paint.py` | Dye colors, texture and paint image names, which faces serve a point, and the recorded crops. |
| `test_coverage_records.py` | The data written next to every garment model: the committed `Models/Garments/*.json` are written again and must be the same bytes, and the faces each hides of a template worn under it are faces of that template, whose face count it records; the sentences of the motion and pair reports, each with the deepest place per phase, the pair's with the most hidden triangles that show a hole per phase, and the rules of the skin and of a pair at rest. |
| `test_rig_tables.py` | The test poses, the rig's zone, chain, and hinge tables, and the gait model against the Humanoid skeleton, the exported clips, and the Unity locomotion speeds. |
| `test_geometry_helpers.py` | The cloth chain order, radii, the hanging of loose parts, rounding keys, and preview colors. |
| `test_powershell_contract.py` | The tables that `tools/art/ArtPipelineCommon.ps1` keeps and the Blender code keeps again: regions, slots, bands, stages, patterns, the version of the content fingerprints and the parts of every stage, and that every content record and approval of an `asset.json` has the parts, stages, and fields of its kind. |
| `test_content_core.py` | The content fingerprints that approvals bind to: rounding, with negative zero, noise below the rounding, and a change above it; named things in any order; the chain of the stages, a shape that changes every stage, weights that keep the model, and a clip that changes only the animation. It needs numpy. |
| `test_cli.py` | The arguments of `art_pipeline_cli.py` and the steps of `human_base_cli.py` against how the PowerShell scripts call them and read their result lines, and the PowerShell rules that tie a texture's masks to the content of the templates under its template and that name a wrong split's entry and key; the fingerprints and `-NoCache` that the scripts pass to the cache of the garment checks, and their line of seconds; that an export's records name the content of the other assets, not their exports; the Blender processes that `Invoke-ArtBlenderJobs` runs at once, with the environment and the result handling of `Invoke-ArtBlender` and their arguments quoted, and an export that plans a wave before it runs it and writes each `asset.json` whole; and that `Build-Art.ps1` records no approval and starts only the pipeline's own scripts, and `Update-UnityArt.ps1` the follow-up's method. |
| `test_check_cache.py` | The [cache of the garment checks](#cache-of-the-garment-checks): keys that change with the content of the template and of a template of a lower layer, a body's fit, a clip, `band`, `recipe`, the cage, a texture, the stage, and a source file of the pipeline, and none when a fingerprint is missing; entries that read back as exactly what was written and none when broken, also while several runs write and read one entry at once; the count of poses and clip frames, 518 with the human library's clips, that a check with nothing to follow reports without stepping; and `coverage.checks` with stand-ins for Blender: the order of the checks, a pair that cannot meet, which computes no cover of the inner template and follows nothing, and a second run that takes every unit from the cache. It needs numpy. |
| `test_manifest_io.py` | `manifest_io.read_json`, which every reader of an `asset.json` in the pipeline uses: it reads the file again while Windows refuses it during a replace, for a few seconds, then raises, and raises any other error at once. |
| `test_retarget_map.py` | The rules of a clip import's map, which `Humanoid` bones the retarget aligns, what a refit reads from a clip and which bones it aligns, and every map in `tools/art/retarget` against the `Humanoid` skeleton. |
| `test_reference.py`, `test_clip_motion.py` | The figure of a reference picture, the overlap and overlay of two figures, and where a picture's middle lies; the motion numbers of a clip. They need numpy, as `test_numeric_logic.py` does. |
| `test_human_build.py` | The human build's net, its subdivision, mirror image, islands, seams, slivers, and folds, the hand's and the ear's islands, the cutting of faces on both bodies, and the layout of the UV map. It needs numpy. |
| `test_rig_landmarks.py` | The bodies' landmarks from the build's named vertices, the eyelids' weights, the lips that meet, the jaw's share of the skin at the mouth, the neck's share of the head's skin, and the turn of the test poses' arms for another bind pose. It needs numpy. |
| `test_skin.py` | The plain skin of the bodies: its flat images, the inside of the mouth, the eyes' iris and pupil, and that its faces and eye colours are the ones the game offers. It needs numpy. |
| `test_grip.py` | The hand pose `Grip`: its distances to a handle and to a finger, its searches, a hand posed by its bones, and its fit on a small hand whose fingers must close onto the handle and lie against each other. It needs numpy. |
| `test_inner_garments.py` | What keeps a garment worn under another from showing through it: the split of a quad across the diagonal it folds outward across, alike for a quad and its mirror image, and the rule that finds mirrored quads split differently, which leaves out a quad that is its own mirror image; the recipes' splits just beyond the hide margin inside where an outer template ends; the hide margin of a place, four times its gap and at least 10 mm, and how far a face lies beyond the margins of the open edges, a face under a hem, at the margin, and near a place with a wider margin; the faces of a turned edge that go with their cloth; the one hidden list for every body; the body faces the game removes under a pair, which do not block a look; where the pair check follows a drawn triangle; the body's drawn faces split as the game splits them, which the pull-in and the motion check follow inside; the phases of its report; and a texture whose template only turned the corners of its faces or cut its UV faces into smaller ones, each inside one old face. It needs numpy. |
| `test_numeric_logic.py` | The cage's net and polygons, the fitting's middle plane, mirror images, and draw of a whole net, the weights of garments and bodies, the cloth's crotch rule with the skin's distances, its band and fades, the crotch it is measured from, the shares it moves to the pelvis, and the hold evened over the cloth's edges, the cloth's normals and the direction into it across a turned edge, the band of turned edges taken where their cloth lies, the room of a hem, the weights a turned edge takes from its cloth, the rule of the normals at a hem, a rebuild's carry-over that reads no turned vertex, dyes, the paint crop, the coverage report, and the move of a texture: its feature curves, the even spread of their move, and how a texel reads and grows its paint. It needs numpy, which CI installs from `tools/art/blender/tests/requirements.txt` in the version that Blender bundles. Without numpy the ten files that need it are skipped and the run says `skipped=10`: install it with `python -m pip install -r tools/art/blender/tests/requirements.txt`, or run the tests in Blender's own Python with `& (Join-Path (Split-Path $env:BLENDER_PATH) "5.2\python\bin\python.exe") -m unittest discover -s tools/art/blender/tests`. |

A test says what must hold, with small inputs whose answer follows from the
input, and where a table in Python must agree with the client, a content file,
an `asset.json`, or a PowerShell script, it reads the other side from the
repository and compares. New logic that needs no Blender gets a test in the same
change. What drives Blender itself, such as the rules that read objects, nodes,
and modifiers, and the preview and review rendering, is checked by
`Approve-ArtAsset.ps1` and `Export-ArtAsset.ps1` in Blender.

### The Wardrobe

`MMORPG > Tools > Art Pipeline > Wardrobe` is the Unity Editor window where
garments are tried on and cloth in motion is reviewed; the reviewer approves the
motion of loose cloth there, not on a review sheet. It shows the exported
garments, paint layers, and attachments of the appearance catalogs, so a
template appears in it once it and a texture of it are exported, and
`Reload catalogs` reads the catalogs again after an export:

- Pick a body in `Body`, tick the garments it wears, one per visual slot, so
  ticking one takes off the other garment of its slot, choose its paint per
  category and slot, and tick the attachments it holds, one per socket. `Drawn`,
  `Painted`, and `Held` list what the body shows.
- Play `Idle`, `Walk`, `Jog`, `Death`, a tool's `Mine` or `Chop`, with the other
  hand pinned on a ticked tool's haft as in the game, or `Cast`, which plays the
  held weapon's attack clip over its hold pose again and again. Or play a stress
  motion of [Measuring The Cloth](EQUIPMENT_VISUALS_DESIGN.md#measuring-the-cloth),
  such as a spin, a sharp turn, a stop, a zig-zag, the zig-zag as the network
  shows a remote body, jittery frames, or a jump.
- The body is put together as the game puts it together, walks and jogs forward
  at the clips' speeds over a checkered ground that the camera follows, so loose
  cloth lags as in the game, and the cloth solver runs with its springs, as on
  the bodies nearest the camera in the game; untick `Springs` to see the target
  pose alone, as every other body shows it. `Playback` slows it down to a tenth,
  and `Step 1/60 s` steps a paused body a frame at a time. Drag in the view to
  turn the camera and scroll to come closer.
- Beside it the window shows what `ClothMotionMeter` measures: the solver's
  level, how far its springs are faded in and whether its cloth sleeps, how many
  leg and arm vertices come through the loose cloth now and at most, a robe
  through a cloak, the chains' jerk, how far a spin flares them and how long
  their swing takes to settle, and how often the legs push a cloak's lower half.
- `Tune the springs` has a slider for every spring value of every profile the
  body wears, which applies at once and keeps while other garments are worn,
  `Reset` puts a profile back, and `Log profiles` writes the tuned profiles to
  the console, from which they go into `ClothProfile`.

`MMORPG > Tools > Art Pipeline > Measure Cloth` measures every motion on both
bodies in the robe, the cloak, and both, and a zig-zag at other frame times
playing the clips and with the pose held, in about a minute and a half, and
writes the table to `artifacts/cloth-measurements.md`.

### Make A Garment

`art/Garments/Shirt` is a short-sleeved top that lies over
`art/Garments/Trousers` at the waist. A template shows in the game only with a
[texture](#make-a-garment-texture), and a body wears one garment per visual
slot, so a shirt and a jerkin replace each other.

1. Create a new template with its visual slot, its band from the cage, and its
   recipe: `-Regions`, the cage regions it covers, and `-Cuts`, the standard
   lines where it ends, each as `<Line>:head` or `<Line>:tail` for the side it
   keeps. `-Splits` adds edge loops where it is not cut, each `<Line>` or
   `<Line>:<head|tail>:<offset>`, such as `HemHip,HemHip:tail:0.011,HemHip:tail:0.017` for trousers
   under a top. `-Over` says where it lies over a garment of another slot, as
   `<Line>:<head|tail>:<band>` for the side of the line the inner garment keeps
   and its band, such as `Waist:head:tight` for a top over trousers. `-Hang`
   adds a loose part, as `<Line>:<all|back>:<skirt|cloak>:<flare>`, or across
   the back `<Line>:back:cloak:<flare>:<top>:<width>`, with `-Cloth` naming its
   profile. One more part at the end names the line of the legs it ends at,
   such as `SkirtWaist:all:skirt:0.08:SkirtHem` for a skirt that ends above the
   ankle. `-Hides` names the slots it hides, `-HidesUnderwear` and
   `-CoversNothing` set `hidesUnderwear` and `"covers": false`, `-Ease` adds
   ease, and `-Hem` sets the depth of the turned edges, 4 mm unless it says
   otherwise. Its layer and triangle budget default to the slot's values in
   [Slots And Layers](EQUIPMENT_VISUALS_DESIGN.md#slots-and-layers) and
   [Budgets](EQUIPMENT_VISUALS_DESIGN.md#budgets), 200 for a `body` garment,
   between a top and a robe, so pass `-Layer` for one; headwear also needs
   `-Hair`. `-Cage` is `art/Cages/Humanoid` unless it says otherwise. `-Modeled`
   makes a template whose first version you model by hand instead:

   ```powershell
   .\tools\art\New-ArtAsset.ps1 -Kind garment -Asset art/Garments/Tunic -Slot body -Band tight -Layer 150 -Regions trunk,arms -Cuts HemHip:tail,SleeveElbow:head -Over Waist:head:tight
   ```

   Expected result: one report line per body, `Tunic_Male: <n> triangles,
   <near> to <far> mm from the cage, median <m> mm`, and `Created
   art/Garments/Tunic with asset.json and Tunic.blend.` A template may not take
   the name of a garment texture, nor the reverse, since both export the
   material `M_Garment_<Name>`.
2. Open the file in Blender. Expected result: the male body in `Tunic_Male`,
   with the cage's versions in the `Cage` collection; the female and
   `Tunic_Female` are hidden in the Outliner. Shape `Tunic_Male` in Edit Mode
   where it needs more than the recipe gives, such as folds or a flared hem, and
   paint its weights by hand if it needs it. Shape the cloth, not the rims and
   flaps of its turned edges: the sync lays them again from the cloth. Correct
   `Tunic_Female` only where the fitting needs help, as the helmet's nasal over
   the woman's smaller nose.
   A hairstyle, a template of `-Slot hair -Band hair -Modeled`, also has the
   shape key `UnderHeadwear` on its first version, the hair pressed flat under
   hoods and helmets, shaped in Edit Mode with that key active. Save, then fit
   and check it:

   ```powershell
   .\tools\art\Sync-GarmentAsset.ps1 -Asset art/Garments/Tunic
   ```

   Expected result: the report lines, `Synced Tunic: ...`, the paths of the
   review sheet and the review file, how much skin comes through it in motion,
   what it covers on each body, such as `Tunic_Male: covers <n> of 7292 body
   faces, leaves <m> at its edges visible, and pulls in <k> visible vertices`,
   what it hides of every exported template of another slot and a lower layer,
   such as `Tunic_Male hides <h> of the <f> faces of Trousers worn under it`,
   the pair check over them, such as `Trousers under Tunic_Male: <t> drawn
   triangles of it lie under Tunic_Male; the deepest they come through it in
   <k> poses and clip frames: the rest pose 0.00 mm, the test poses ...`, a line
   per stage, such as `Tunic: the 'model' stage is not approved yet.`,
   `Tunic meets every rule. Review it, ...`, and `Tunic took <n> s; <c> of <u>
   check units came from the cache.` A second run prints `Tunic is up to date:
   ...`, leaves the file as it is, and takes every unit from the
   [cache](#cache-of-the-garment-checks). A broken rule stops
   it with the list of rules, such as a version that leaves its band, with the
   body parts where it does. After a change of the recipe in `asset.json`, run
   it with `-Rebuild`, which builds the first version again and keeps its
   handmade shape. `-Weights` transfers every weight from the bodies again, also
   the weights painted by hand.
3. Look at `artifacts\art-previews\Tunic.png`, the sheet that
   [Checks And Review Of A Template](#checks-and-review-of-a-template)
   describes, and open `artifacts\art-previews\Tunic_poses.blend` in Blender and
   play the timeline. Expected result: on the sheet, the grid in the template's
   UVs is square where they do not stretch, and the body without the template is
   orange in the template's shape, yellow along its edges, and red only where
   skin comes through it in motion; in the review file, both bodies side by side
   in the template, stepping through the test poses, with the template following
   the arms, the torso, and the legs without the body showing through. Close the
   review file without saving.
4. Approve both stages, export, and verify:

   ```powershell
   .\tools\art\Approve-ArtAsset.ps1 -Asset art/Garments/Tunic -Stage model
   .\tools\art\Approve-ArtAsset.ps1 -Asset art/Garments/Tunic -Stage rig
   .\tools\art\Export-ArtAsset.ps1 -Asset art/Garments/Tunic
   .\tools\art\Verify-ArtAssets.ps1
   ```

   Expected result: `Approved the 'model' stage of Tunic (triangles=...)`, the
   same for `rig`, `Exported Tunic to
   src/Client/Assets/Art/Models/Garments/Tunic_Male.fbx,
   src/Client/Assets/Art/Models/Garments/Tunic_Female.fbx with their data
   (...)`, and `Verified <n> art asset(s).` The `rig` approval and the export
   take what the sync checked from the cache, so the export ends with `Tunic
   took <n> s; <u> of <u> check units came from the cache.` When Unity gets
   focus, it creates `Assets/Art/Materials/Garments/M_Garment_Tunic.mat`.
   Commit the `.blend` file, `asset.json`, the FBX and JSON files, the material,
   and their `.meta` files together. A template is exported again whenever its
   cage, a body, or an animation library changes, with the templates of higher
   layers, as [Export Of A Template](#export-of-a-template) describes;
   `Build-Art.ps1` finds every such export, as [Build The Art](#build-the-art)
   describes.
5. Give it a texture, as [Make A Garment Texture](#make-a-garment-texture)
   describes, and once both are exported, try it on in
   [the Wardrobe](#the-wardrobe) and play its clips; a template with loose parts
   has its motion reviewed there.

## Garment Textures

A garment texture below `art/GarmentTextures` is the look of one template, named
in `garment`, such as the leather `art/GarmentTextures/LeatherJerkin` of
`art/Garments/Shirt`; the design is in
[Textures](EQUIPMENT_VISUALS_DESIGN.md#textures). Its file holds the image
`T_Garment_<Name>`, packed, 512, 1024, or 2048 px square, with the custom
properties `roughness` and `metallic`, and the mesh `Preview`, a copy of the
template's first version with the material `M_Garment_<Name>` that shows the
image, for painting in Texture Paint mode. `Preview` keeps the UVs the image is
painted in, and its property `template_uvs` the fingerprint of the template's
UVs then. The file also keeps `Preview_Cage`, hidden, the version of the garment
cage that `Preview` lies on. Where the image's alpha is below 0.5, the garment
is cut: the skin
shows through, with its paint. A texture and a template both export the material
`M_Garment_<Name>`, so a texture never takes a template's name:
`New-ArtAsset.ps1` refuses it, and `Verify-ArtAssets.ps1` fails when two assets
export a material of the same name.

- `New-ArtAsset.ps1 -Kind texture -Garment art/Garments/<Template>` starts a
  texture of `-Size` pixels, 1024 unless it says otherwise, in a light grey, or
  with `-ProjectFrom <file.blend>=<Object>` with the look of another mesh, its
  color and its alpha moved to the template's UVs by projection: Cycles bakes
  them from `Preview`'s surface, looking 60 mm both ways for the other mesh.
- `tools/art/Sync-TextureAsset.ps1` moves the texture to the template's new UVs
  when the template's faces or UVs changed, such as after a rebuild of its
  recipe, or when `Preview`'s own faces or UVs were edited, and leaves the file
  as it was otherwise. When the template keeps `Preview`'s faces and their UVs
  and only their corners start elsewhere, as when a rebuild turns its quads to
  split across the diagonal they fold outward across, when its UVs are
  `Preview`'s cut by edge loops that moved none of them, as its `splits` leave
  them, every old corner's UV still there, every new face inside one old face in
  UV space, and as much of the UVs covered, or when only the template's shape
  changed, the image stays as it is, byte for byte, and `Preview` takes the
  template's faces and shape. It then writes the
  review sheet and checks every rule.
  The move goes by correspondence, not by rays, because a body may come to rest
  in another pose, where rays from the new sleeve never meet the old one, and a
  template built again from its recipe has other faces:
  - `Preview` first follows the template. When the template still has its
    faces, it takes the template's shape vertex by vertex. Otherwise it moves
    with the cage, from `Preview_Cage` to the template's cage as it is now, as a
    template's first version moves with its cage: cloth on the cage face by
    face, a loose part as a whole with the edge it hangs from, and a modeled
    form with the cage's smooth field.
  - Then its feature curves, its open edges and the lines its loose parts hang
    from, are each drawn onto the template's nearest curve of its kind, when
    that lies within 100 mm on average, and the move is spread evenly over the
    cloth between the curves. The standard lines a template is cut at are
    measured on the bones, and the cage's net slides along the body from one fit
    to the next, so the cage alone leaves a hem off its place: when the human
    bodies were rebuilt, the shirt's hem lay 49 mm from where the cage put it.
  - Every texel of the new UVs then takes the color and the alpha of the nearest
    point of the moved `Preview`, each channel filtered on its own, from a face
    that faces the texel's way when one lies within 30 mm beyond the nearest of
    all, so the inside of a turned edge never paints the outside. The paint
    grows 8 texels beyond the UV islands, and a dye mask moves the same way as
    the image.
  - The script prints how far `Preview` moved with the cage, how far each curve
    lay from the template's, and from how far away the texels took their paint.
    A texture file from before the sync kept `Preview_Cage` has none: its
    `Preview` is taken to lie on the template's cage as it is now, and the
    script says so.
- The `texture` stage checks that the image is packed as a PNG, of a known size,
  with its roughness and metallic, that `Preview` shows it, and that it was
  painted in the template's current UVs, which `Preview` still has. A texture
  with transparent parts, such as the jerkin's laced V, also gets masks of its
  own on every body, because its holes change what the body and the garments
  under it show: the template's file is opened with the texture on every
  version, and `coverage.py` computes the cover mask, underlay, pull-in, and the
  faces it hides of every template of a lower layer worn under it, with the
  margins of its own cut lines, so the net shirt hides nothing of the trousers
  under its holes, and runs the motion check with the template's rules and, for a
  template that covers, the pair check of every such template with the texture's
  masks. An opaque texture uses the template's masks.
- The review sheet `artifacts/art-previews/<Name>.png` shows the template with
  the texture on every body from four sides with the torso up close and in the
  test poses, and for a texture with transparent parts the body without it in
  the colors of the template's sheet, with the texture's own mask and the skin
  that comes through it in motion.
- The export writes the image to `Textures/Garments/T_Garment_<Name>.png` and
  the material `M_Garment_<Name>` with it as `baseMap`, and for a texture with
  transparent parts its masks per body, `Models/Garments/<Name>_<Label>.json`, in
  the format of the template's data. The masks are computed in the template's
  file with its bodies, cage, and clips, so the export runs only when the
  template's approvals hold for its current content, the texture is painted in
  the template's current UVs, the `templateUv` part of its content being the
  template's `uv` part, and, unless the same run exports the template, the
  template's export comes from its current content and still matches its cage
  and its bodies; otherwise it stops with `cannot be exported before its
  template ... is exported again: ...` and asks to export the template in the
  same run or first. An animation library whose approval
  does not hold for its current content stops only a texture with transparent
  parts, before anything is written. The export records the content of the
  template's last stage in `garmentContentSha256`, so `Verify-ArtAssets.ps1`
  fails when the template's content changed: sync, review, and export the
  texture again. A texture and its template may be exported in the same run, at
  once. A change of the template's shape that
  keeps its UVs leaves the texture's approval holding and needs only a new export
  of the texture, whose masks follow the shape; a change of its UVs needs
  `Sync-TextureAsset.ps1`, and a new review and approval when the sync changes
  the texture.

In Unity, `Create Missing Materials` creates
`Assets/Art/Materials/Garments/M_Garment_<Name>.mat` with the garment shader,
and the appearance catalog of every body lists every exported texture of an
exported template that fits the body as a garment named after the texture: the
template's model for the body and its slot, layer, and hides, the texture's
material, which the template's model draws with instead of its own, and the
texture's masks, or else the template's. A character wears a garment by the
texture's name, such as `LeatherJerkin`, in the Admin menu (F3), in the
Wardrobe, and in an item's presentation, and one garment per visual slot:
trying one on takes off the other garment of its slot, such as another chest
piece, and of two of a slot that a look names, the first in the catalog is worn.

### Dyeable Textures

A texture may be dyeable, as the wool of `WoolCloak`, `WoolRobe`, `WoolHoodUp`,
and `WoolHoodDown` is, with the design of
[Dye Channels](EQUIPMENT_VISUALS_DESIGN.md#dye-channels). The image
`T_Garment_<Name>_Dye` in its file is its dye mask, whose red, green, and blue
channels mark how far each dye channel tints a texel, read as data, with the
custom property `dye_colors`, the natural color of each channel it uses,
`#RRGGBB` joined by `;`. A dyeable region is painted in a light neutral whose
middle tone lies at the linear level 0.7, about 85 percent light, and its
natural color gives back its painted look.

- `tools/art/Set-TextureDyes.ps1 -Asset art/GarmentTextures/<Name>
  [-Regions <png>]` makes a painted texture dyeable without painting it again.
  `-Regions` is a PNG of the texture's size whose red, green, and blue mark the
  texels each channel tints, such as the cloak's wool without its bronze clasps;
  without it the first channel tints every texel. A channel's natural color is
  its region's middle tone over the level, and the image is divided by what the
  shader multiplies it by, so the texture undyed looks as it was painted, within
  one step of 255 on the wool set. A texel too light to be divided so, such as a
  light stitch on dark wool, keeps part of its painted color: the mask reaches
  only as far as its light allows. `-Level` changes the level from 0.7, which
  every dyeable texture shares. The template must allow the channels in its
  `dyes`. The script then writes the review sheet and checks every rule, as
  `Sync-TextureAsset.ps1` does, and a template whose UVs change moves the dye
  mask with the image.
- The `texture` stage checks a dye mask too: packed as a PNG of the image's
  size, read as Non-Color data, a natural color for each channel it uses and no
  more channels than its template's `dyes`, and shown by `Preview`. The review
  sheet shows the texture in its natural colors.
- The export writes the dye mask as `Textures/Garments/T_Garment_<Name>_Dye.png`
  and names it and the natural colors in the material's record, and
  `Create Missing Materials` gives the material the dye mask, the number of its
  channels in `_DyeCount`, and its natural colors in `_DyeColor1` to
  `_DyeColor3` from its record, also when the material exists.

### Make A Garment Texture

`art/GarmentTextures/LeatherJerkin` is the leather of the jerkin, with an open V
at the front where the skin shows between the laces.

1. Create a texture for a template, in a light grey, or with `-ProjectFrom` with
   the look of another mesh moved over by projection:

   ```powershell
   .\tools\art\New-ArtAsset.ps1 -Kind texture -Asset art/GarmentTextures/WoolTunic -Garment art/Garments/Tunic -Size 1024
   ```

   Expected result: `Created art/GarmentTextures/WoolTunic with asset.json and
   WoolTunic.blend.`
2. Open the file in Blender and paint `T_Garment_WoolTunic` on `Preview` in
   Texture Paint mode, with its custom properties `roughness` and `metallic`.
   Paint the alpha below 0.5 where the skin should show, such as a net or a laced
   opening; an opaque texture has none. Save, then check it:

   ```powershell
   .\tools\art\Sync-TextureAsset.ps1 -Asset art/GarmentTextures/WoolTunic
   ```

   Expected result: the path of the review sheet, `WoolTunic: the 'texture'
   stage is not approved yet.`, `WoolTunic meets every rule. Review it, then
   record the approval with tools/art/Approve-ArtAsset.ps1 -Asset
   art/GarmentTextures/WoolTunic -Stage texture.`, and `WoolTunic took <n> s.`,
   for a texture with transparent parts with how many units of its masks came
   from the [cache](#cache-of-the-garment-checks). After the template's UVs
   changed, such as after a rebuild, it first prints `Moved WoolTunic to the new
   UVs of Tunic: ...` and the notes of the move. A move that keeps the image as it
   is, because only the template's shape changed, leaves the approval holding.
3. Look at `artifacts\art-previews\WoolTunic.png`, the sheet that
   [Garment Textures](#garment-textures) describes. Expected result: the
   texture lies on both bodies as painted, with its trim at the template's hems,
   and for a texture with transparent parts the body without it shows the skin
   under the holes and along their edges visible.
4. To make the texture dyeable, give its template dye channels in its
   `asset.json`, such as `"dyes": 1`, and give the texture its dye mask, with
   the regions PNG of [Dyeable Textures](#dyeable-textures), or without it to
   tint every texel with the first channel:

   ```powershell
   .\tools\art\Set-TextureDyes.ps1 -Asset art/GarmentTextures/WoolTunic -Regions <regions.png>
   ```

   Expected result: `WoolTunic: natural colors #RRGGBB; <n> texels too light to
   be dyed in full.`, then the output of `Sync-TextureAsset.ps1` above. The
   review sheet shows the texture as before, since the undyed texture wears its
   natural colors.
5. Approve, export, and verify:

   ```powershell
   .\tools\art\Approve-ArtAsset.ps1 -Asset art/GarmentTextures/WoolTunic -Stage texture
   .\tools\art\Export-ArtAsset.ps1 -Asset art/GarmentTextures/WoolTunic
   .\tools\art\Verify-ArtAssets.ps1
   ```

   Expected result: `Approved the 'texture' stage of WoolTunic
   (texture=T_Garment_WoolTunic).`, `Exported WoolTunic to
   src/Client/Assets/Art/Textures/Garments/T_Garment_WoolTunic.png with its
   material M_Garment_WoolTunic.`, for a texture with transparent parts with
   `and its masks src/Client/Assets/Art/Models/Garments/WoolTunic_Male.json,
   ...` and the report of what it covers and how much skin comes through it in
   motion, which the export takes from the cache that the sync and the approval
   filled, and `Verified <n> art asset(s).` When Unity gets focus, it creates
   `Assets/Art/Materials/Garments/M_Garment_WoolTunic.mat` and updates the
   appearance catalogs: the Console lists `Garment WoolTunic` in both. Commit the
   `.blend` file, `asset.json`, the PNG and JSON files, the material, the
   catalogs, the content assets below `Assets/Content/Bodies/Garments`, the
   changed files of `Assets/AddressableAssetsData/AssetGroups`, and their
   `.meta` files together. A dyeable texture also exports
   `T_Garment_WoolTunic_Dye.png`, and Unity gives the material its dye mask and
   natural colors. A texture is exported again after every change of its
   template's content.
6. Try it on in [the Wardrobe](#the-wardrobe), and give it an item to wear it in
   the game: in `MMORPG > Tools > Item Catalog`, select an item of the
   template's equipment slot, enter `WoolTunic` in `Garments` under
   `Client Presentation`, with its dye colors in `Dye Colors` for a dyeable
   texture, and `Save And Bake`, as
   [Item Catalog Authoring](../content/items/README.md#unity-editor-workflow)
   describes. Expected result: `ItemGarmentTests` passes, and an item of the
   template's slot shows the garment on both bodies; how to check it in the game
   is in [Manual Testing](MANUAL_TESTING.md). A texture of a `hair` template is
   part of the look, not of an item: add it to the `hair` options of every body
   model in `content/characters/appearance.json` instead, which
   `CharacterLookTests` requires.

## Attachments

An attachment is a model that a socket of a skeleton holds, such as a sword in
the right hand or a shield in the left: an asset of kind `static` below
`art/Attachments` with a `socket` in `asset.json` and one root object, the empty
`Grip`, at the origin. Its meshes lie below `Grip` in the socket's own axes, so
the model stands on the socket bone as it stands on the origin. An attachment
with moving parts of its own, such as a bow, is `skinned` instead; see
[Skinned Attachments](#skinned-attachments). The socket bones of `Humanoid` place
every attachment on every body; an attachment never moves them, and a place that
suits only one item is that item's own geometry, as
[Attachments](EQUIPMENT_VISUALS_DESIGN.md#attachments) explains.

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

The review sheet of an attachment shows it alone from four sides, from above and
from below in turn, and up close around `Grip`, and then a row per body of the
animation library of its socket's skeleton that has `Idle`: each body in the
first frame of `Idle` with the fingers of `Grip`, holding the model on its
socket, whole, from outside the arm, and its hand up close from the front, from
inside with only the arms shown, and from behind.

In Unity the model imports readable. The appearance catalog of every body of its
socket's skeleton lists it as an attachment with its socket and socket bone,
`PlayerBodyMeshes` combines it into the body's one mesh with every vertex
weighted fully to the socket bone, which stands on its parent as in the model's
bind pose, and the animator layer of that hand plays `Grip`.
`ItemAttachmentTests` fails when an item names an attachment that a body's
catalog lacks or that its slot does not hold, when a model has another root than
`Grip` at its origin, or when a body in a jog pose draws an attachment's vertex
more than 1 mm from where its model puts it on the socket.

### Weapons With Clips

A weapon held on `main_hand` or, like a bow in the left hand, on `off_hand`, that
is no tool may name a `holdClip`, the pose its arm holds it in, and an
`attackClip`, the clip its body plays once whenever its attack starts, both clips
of an animation library of its socket's skeleton. With an attack clip it names
its `attackRelease`, the seconds into the clip when its blow lands or its arrow
or bolt leaves, which the server's windup of its weapon family matches and the
client times the clip to, and may name a `launchPoint`, the point of the model
where its projectile leaves, and on `main_hand` an `offHandGrip` when its attack
takes it in both hands. The Oak Staff names `"holdClip": "HoldStaff"`,
`"attackClip": "Cast"`, `"attackRelease": 0.15`, and
`"launchPoint": [0.0, 0.7, 0.0]`, its crystal; the other weapons are in
[Assets](#assets), and what each clip does is in
[Weapon Poses And Attack Clips](EQUIPMENT_VISUALS_DESIGN.md#weapon-poses-and-attack-clips).

`WeaponClipTests` fails when an attack clip does not release where its weapon
family's windup ends in the item catalog. The review sheet of the weapon shows
the bodies with the arm in the hold clip, and the sheet of the library shows the
weapon held in the rows of both clips. In Unity the appearance catalog lists the
clips and the launch point, converted to the model's axes like an
`offHandGrip`, and the controller has a state of the hold clip's name on the
layer of its hand, `MainHandHold` or `OffHandHold`, and a state of the attack
clip's name on `Action`; `WeaponClipTests` checks the layers, their masks, the
states, the launch point, that both bodies hold the staff upright and thrust it
forward, that both draw the bow, and that the other hand takes the greataxe's
haft.

### Skinned Attachments

An attachment whose parts move, such as the limbs and the string of a bow, is a
`skinned` asset below `art/Attachments` with a `socket`, a `skeleton` of its own,
such as `Bow`, whose armature is its one root object `Grip` at the origin in the
socket's axes, and its `clips`. It goes through the stages of every skinned
asset, model, rig, and animation, each approved by the reviewer. The Training
Shortbow, held in the left hand on `off_hand`, is one; see [Assets](#assets).

In Unity the model imports with a Generic rig and readable meshes, and the
appearance catalog lists it with its skeleton, its clips, and for a bow its
`drawLength`. `PlayerBodyMeshes` combines its mesh into the body's one mesh
skinned to its own bones, and `PlayerBodyAppearance` holds a copy of the model's
bones below the socket, as they stand below the model's root, so the combined
renderer binds to them. `BowRig` moves a bow's bones while the body draws, as
[Weapon Poses And Attack Clips](EQUIPMENT_VISUALS_DESIGN.md#weapon-poses-and-attack-clips)
describes. `ItemAttachmentTests` fails when a skinned model's mesh is not
readable or the body does not hold its bones below the socket.

### Projectiles

A projectile is a static model that flies, such as an arrow: an asset of kind
`static` below `art/Projectiles` with one root object at the origin, its point,
flying toward +Y, which is the model's forward in Unity. The Bronze Arrow,
`art/Projectiles/BronzeArrow`, is the arrow of every weapon that shoots arrows,
not part of a bow: `ClientConfig` names its model, and the combat presentation
lays it on an archer's string and flies it to the target.

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

In Unity the appearance catalog lists the tool's `UseClip` and its
`OffHandGrip`, converted to the model's axes as the import converts the model,
Blender's `(x, y, z)` to Unity's `(x, z, y)`, and the controller has a looping
base layer state of the clip's name. `ToolInUseTests` fails when a tool item can
be equipped elsewhere than the Tool slot, when a tool's clip has no such state,
when its other hand's grip lies off the model, or when a body's other hand does
not end up on the grip through the swing; see
[Tools In Use](EQUIPMENT_VISUALS_DESIGN.md#tools-in-use).

### Make An Attachment

1. Create the asset with its socket and triangle budget:

   ```powershell
   .\tools\art\New-ArtAsset.ps1 -Kind static -Asset art/Attachments/IronAxe -Socket main_hand -TriangleBudget 2200
   ```

   Expected result: `Created art/Attachments/IronAxe with asset.json and
   IronAxe.blend.` `asset.json` names the socket and the one root object `Grip`,
   and the file holds the `Export` collection with the empty `Grip` at the
   origin. A socket no skeleton has stops the command with `No skeleton in
   art/Skeletons has the socket ...`. An attachment with moving parts, such as a
   bow, is a skinned asset instead, made like the
   [Training Shortbow](#skinned-attachments).
2. Model below `Grip` in the socket's axes by the
   [modeling rules](#modeling-rules), with the handle along Y through the origin
   and about 29 mm thick, unwrap it, paint its texture by hand, name its
   material `M_IronAxe`, and save.
3. Render the review sheet:

   ```powershell
   .\tools\art\Render-ArtPreview.ps1 -Asset art/Attachments/IronAxe
   ```

   Expected result: `Review sheet for IronAxe:` with the path of
   `artifacts\art-previews\IronAxe.png`, the sheet that
   [Attachments](#attachments) describes, with each body's hand closed around
   the handle and the model standing on the socket as it stands on the origin.
4. When the reviewer approves, approve the `model` stage, export, and verify as
   in [Review, Approve, And Export An Asset](#review-approve-and-export-an-asset).
   Expected result: when Unity gets focus, it creates the materials below
   `Assets/Art/Materials/Attachments` and the Console lists both appearance
   catalogs with `Attachment IronAxe`. Commit the `.blend` file, `asset.json`,
   the FBX, the texture, the materials, the catalogs, the content asset
   `Assets/Content/Bodies/Attachments/IronAxe.asset`, the changed files of
   `Assets/AddressableAssetsData/AssetGroups`, and their `.meta` files together.
5. Let an item show it: in `MMORPG > Tools > Item Catalog`, select an item of
   the socket's equipment slot, such as `main_hand`, enter `IronAxe` in
   `Attachment` under `Client Presentation`, and `Save And Bake`. Expected
   result: `ItemAttachmentTests` passes, and `MMORPG > Tools > Art Pipeline >
   Wardrobe` lists `IronAxe (main_hand)` under `Attachments`.

A weapon may name the pose its arm holds it in and the clip its body plays when
it attacks, as [Weapons With Clips](#weapons-with-clips) describes: add both
clips to the animation library first, as [Make A Clip](#make-a-clip) describes,
then name them in the weapon's `asset.json` with its `attackRelease`, the windup
of its weapon family, and a `launchPoint` where a projectile leaves it, as the
Oak Staff's example there shows. Render the weapon's review sheet and the
library's again. Expected result: the weapon's review sheet shows the bodies
holding it in the hold clip, and the library's review sheet shows it in the rows
of both clips. In Unity, add a state of the hold clip's name to the layer of its
hand, `MainHandHold` or `OffHandHold`, and a state of the attack clip's name to
the layer `Action` of `Assets/Art/Animation/HumanLocomotion.controller`;
`WeaponClipTests` checks them.

A tool, such as the Mining Pickaxe, is an attachment that a body holds only while
it uses it; [Tools](#tools) describes it. Add its clip to the animation library
first, named in the library's `clips`, then name it in the tool's `asset.json`
with `"useClip": "Mine"`, and for a tool held with both hands
`"offHandGrip": [0.0, -0.33, 0.0]`, where the other hand holds the haft. Render
the library's review sheet with `.\tools\art\Render-ArtPreview.ps1 -Asset
art/Animations/Human_Actions`. Expected result: every body holds the tool in the
rows of its clip. In Unity, add a looping base layer state of the clip's name to
`Assets/Art/Animation/HumanLocomotion.controller` and give a Tool slot item the
attachment; `ToolInUseTests` checks both.

## UI Art

The look of the permanent windows comes from SVG files and one theme, not from
Blender: frames, slot states, buttons, fields, the carry meter, icons, and the
silhouettes of empty slots are vector drawings in
`src/Client/Assets/Art/Ui/Sprites`, and `Assets/Content/Config/UiTheme.asset`
holds them together with the fonts, colors, and measures; see
[UI Kit And Theme](UNITY_CLIENT_ARCHITECTURE.md#ui-kit-and-theme). The reviewer
approves a new look from screenshots of the running windows before it is merged.

- Every SVG imports as one textured UI Sprite through `UiSpriteImportRules`, with
  Unity's built-in SVG importer. `ui-sprites.json` in the same folder names the
  texture size and the nine-slice border, left, bottom, right, and top in texture
  pixels, of every sprite that stretches, such as `WindowFrame.svg` at 128 by 128
  with a border of 32; any other SVG imports square at the default size of 128
  without a border. The importer keeps the SVG's whole view box, so an icon keeps
  its margins.
- Draw an icon or silhouette in white on a 128 by 128 view box, since the theme
  tints it: the window icons and the close button in the title color, the
  silhouettes in a faint grey. A frame that shows a color of the game, such as
  the item frame of the rarity color, is white too. The coin and the pedestal
  keep their own colors.
- Fill a gradient that a frame stretches as a plain rectangle, over a solid
  rounded one where the frame has round corners, as `WindowFrame.svg` does. The
  importer cracks a rounded shape of a gradient along a triangle edge, and the
  stretched middle of the window then shows a faint diagonal line.
- Draw a soft glow as one-pixel strokes that fade inward, as `SlotGlow.svg`
  does, instead of a gradient; strokes two or more pixels wide show as steps at
  the slot size.
- A sprite's file name is its theme field, such as `ItemFrame.svg` for
  `ItemFrame`. `Silhouette_<slot id>.svg` is the empty picture of an equipment
  slot, such as `Silhouette_head.svg`, `Tag_<tag id>.svg` that of a specialized
  Bag slot, such as `Tag_potion.svg`, and `FallbackSilhouette.svg` that of a slot
  without its own.
- After adding or changing an SVG or `ui-sprites.json`, run
  `MMORPG > Tools > UI > Build UI Theme` in Unity. It reimports the sprites,
  creates the theme if it is missing, and fills every sprite field, the
  silhouettes, and the fonts from their files; it never changes a color or
  measure, so values tuned in the theme's Inspector stay. `UiThemeTests` fails on
  a missing sprite, a frame without a border, or an equipment slot or Bag tag
  without a silhouette.
- The fonts are in `src/Client/Assets/Art/Fonts`; after changing a font file or
  the character set, run `MMORPG > Tools > UI > Build UI Fonts`, then
  `Build UI Theme`. [Text And Fonts](UNITY_CLIENT_ARCHITECTURE.md#text-and-fonts)
  has their settings.

Expected result: the windows show the changed sprite at its place, sharp at
1920 by 1080, and `tools/Run-UnityEditorTests.ps1 -Mode EditMode -Filter
UiThemeTests` passes.

## Water

Water is stylized and bright, with clear colors and visible foam: clear and
light where it is shallow, so the ground and light patterns show through, and
deep blue further out. Its look comes from two shaders and from textures and
materials that code makes, not from Blender. `WaterAssets` writes them below
`src/Client/Assets/Art/Water` with `MMORPG > Tools > Water > Create Water
Textures And Materials`, and the Development World 2 builder runs it before it
builds the waterside. Every value of the look is in `WaterAssets.cs`, so it is
reviewed with the code and a rebuild writes the same files. The reviewer
approves a new look from screenshots before it is merged.

`Mmorpg/Water` draws lakes, rivers, and seas:

- The color comes from the depth below the surface. Shallow water shows the
  ground through it, bent by ripples, with light patterns on it, and turns to
  the deep color with depth; a glancing view shows the sky and the horizon, and
  the sun glints and sparkles on the ripples.
- Foam gathers along the shore, around everything that stands in the water, in
  lines that roll in toward a sea's shore, and in drifting patches where the
  water runs fast; churning water, such as at the foot of a waterfall, foams all
  over.
- Every lake, river, and sea draws with one material, so what sets them apart
  comes from their surface meshes, which the
  [World Builder](DEVELOPMENT_WORLDS.md#world-builder) makes from their shapes:
  the second texture coordinate is the flow in
  meters per second, along which the ripples and the foam move, the red of the
  vertex color how fast and foaming the water is, the green how much the waves
  lift the vertex, and the blue how strongly foam lines roll in toward the
  shore, as on a sea's beach. A lake leaves all of them at zero, and its foam
  drifts slowly with the material's drift.
- A light wind carries the large and the small ripples of all water on in two
  directions of their own, the large ones at about 0.18 m/s and the small ones
  at about 0.29 m/s, so still water keeps changing instead of looking like a
  still image. The wind is the same everywhere in the World, so water bodies
  that meet still draw alike. Its speeds are `Large ripple wind` and `Small
  ripple wind` in `ApplyLook`.
- Where water bodies meet, their surfaces overlap, and the overlap draws once
  per pixel through a stencil bit, so it never doubles its tint. They meet
  without a seam when their vertices agree there: the same level, still water,
  and no waves or foam lines, as the waterside's river does where it reaches
  into the lake and the sea.
- The shader reads the camera's depth and opaque textures, which every quality
  level's render pipeline asset turns on. The surfaces draw in a pass of their
  own, `WaterRendererFeature` on both URP renderers, before the other
  transparent objects, and the feature then copies the camera's color with the
  water in it, so what draws later, such as a waterfall, shows the water behind
  it. Without the feature no water surface draws.
- Every animation runs on the water's clock, `_WaterClock` in
  `WaterCommon.hlsl`, which `WaterShaderGlobals` wraps at an hour in double
  precision before every frame, so the water moves the same after a day of play
  as after a minute. The rate of each animation rounds to whole cycles per hour,
  so nothing jumps when the clock wraps. The sea's mix of waves comes from
  `WaterWaves` the same way, so the client puts the wakes on the waves where the
  shader draws them.

`Mmorpg/Waterfall` draws the falling water of a waterfall as a column with a
body of its own, lit the same way: the rock behind and the water of the pool
below show through clear water, bent by ripples that rush down, the sky and the
sun glint in it, and thin
streaks and clumps of foam fall with the water. The water slides glassy over the
lip, breaks into streaks, and turns white at the foot. Its outline frays and
fades where the column turns away around its cross section, so it has no hard
edge from any side, while the top of the water over the lip stays a surface
even at a glancing view along the stream. Clear water looks denser where the
view passes through it at a slant. The column starts on the water above it,
with its top in that surface, and fades in over it before the lip, so the water
above turns into the fall without a line, a bright edge, or a step in color.
The World Builder makes the column from a `WaterfallShape` and the river it
falls from. The column's mesh holds the meters around it from its front, the
seconds the water has fallen (negative before it falls), how far down the fall
the vertex lies, and how far the column has faded in over the water above in
its four texture coordinates, its normals facing out of the column and its
tangents running around it. Because the pattern keeps to the seconds the water
has fallen, its streaks stretch as the water speeds up.

| File | Content |
| --- | --- |
| `T_Water_Ripples.png` | Ripple normals: soft swells bent by slower noise, with small ripples on top. |
| `T_Water_Masks.png` | Red is the foam pattern, low where foam forms first, green the light lines of the shallows, and blue soft noise for the sparkles and the foam patches. |
| `T_Water_Falls.png` | Red is long thin streaks down a waterfall, green clumps of foam, and blue the noise of its sparkles. |
| `T_Water_Ring.png`, `T_Water_Droplet.png` | The ring and the droplet of the wakes in water. |
| `M_Water.mat` | Every water surface: turquoise and clear over sand, deep blue further out. |
| `M_Water_Waterfall.mat` | The falling water of a waterfall. |
| `M_Water_Ring.mat`, `M_Water_Spray.mat` | URP's unlit particle shader, one step after the water in the transparent queue, for the wakes and the waterfall's spray. |

The textures live in their own folder because the
[import rules](#unity-import-rules) of exported art own every texture below
`Assets/Art/Textures` and import it as a color image; the water's textures are
data, imported linear, and the ripples as a normal map.

### Water Looks

A zone that needs other water, such as a dark mountain lake or a green bog,
gives its water bodies a look of their own without changing the code's
materials:

1. In the Project window, right-click `M_Water` or `M_Water_Waterfall` in
   `Assets/Art/Water` and choose `Create > Material Variant`. Name it
   `M_Water_<Look>` or `M_Water_Waterfall_<Look>`, such as
   `M_Water_Mountain`, and keep it in `Assets/Art/Water`.
2. In its Inspector, change only the colors: `Shallow color`, `Deep color`,
   `Depth of the deep color (m)`, `Visibility through the water (m)`, and
   `Reflected horizon color`, or for a waterfall `Clear water color` and
   `Shaded water color`.
3. Put the variant on the MeshRenderer of every water body of the zone that
   meets another, so they meet without a seam.

Expected result: the water shows the new colors with the same ripples, foam,
and light, and a later run of the water command changes everything but the
overridden colors, as `WaterLookTests` checks. A dark mountain lake uses about
`(0.10, 0.38, 0.42)` shallow, `(0.01, 0.06, 0.14)` deep, a deep-color depth of
1.4 m, and a visibility of 1.6 m; a green bog about `(0.24, 0.30, 0.14)`
shallow, `(0.05, 0.08, 0.03)` deep, 1.2 m, and 0.9 m.

### Change The Water's Look

1. Change the values in `ApplyLook` or `ApplyWaterfallLook` of
   `src/Client/Assets/Editor/Water/WaterAssets.cs`, or the noise of a texture.
2. Run `MMORPG > Tools > Water > Create Water Textures And Materials`.
3. Look at the water of Development World 2's waterside from the shore, from
   above, and up close, and take screenshots for review.

Expected result: only the changed textures and materials are written, and
`tools/Run-UnityEditorTests.ps1 -Mode EditMode -Filter
AuthoredGameplayAssetTests,WaterLookTests,WatersideJunctionTests,RuntimePresentationTests`
passes.

## Assets

| Asset | Kind | Roots | Notes |
| --- | --- | --- | --- |
| `art/Animations/Human_Actions` | `animations` | `Humanoid` | Every clip of the human bodies, keyed on the rig of `Human_Male` and shown on both bodies. From the gait model, looping: `Idle`, 4 s, `Walk`, 28 frames at 1.4 m/s, and `Jog`, 19 frames at 5.0 m/s. `Death`, 2 s, made by hand, not looping: a hit snaps the head back, the right knee buckles first, the body tips over to its right side and backward, lands on its back with the legs swinging up, the head hits a moment later, the legs fall with the left knee outward, and the body lies still on its back with the arms out and the head turned aside. `Grip`, one frame, looping, keys only the fingers and thumbs: both hands closed around a handle of 29 mm on their sockets. The tools' strokes `Mine` and `Chop`, 45 frames each, looping, both hands closed on a tool's haft, the right hand 33 cm above the left; see [Tools In Use](EQUIPMENT_VISUALS_DESIGN.md#tools-in-use). The weapons' hold poses, one frame each and looping, and attack clips, not looping: `HoldStaff` and `Cast`, 27 frames; `HoldGreataxe` and `GreataxeSwing`, 40 frames; `HoldBow` and `BowShot`, 48 frames; `SwordSlash`, 30 frames; and `OneHandStrike`, 32 frames; see [Weapon Poses And Attack Clips](EQUIPMENT_VISUALS_DESIGN.md#weapon-poses-and-attack-clips). |
| `art/Attachments/HeaterShield` | `static` | `Grip` | A small heater shield held on `off_hand`, 1,436 triangles: a board 56 cm wide and 70 cm from the straight top to the point, 12 mm thick and bowed 4 cm toward the arm at its sides, its face painted blue, its back plain wood, an iron edging bent over the edge, a leather-wrapped handle in the fist on two straps, a loose leather strap around the forearm 20 cm toward the elbow, and iron rivets through both faces where the straps hold. It lies 76 mm behind the handle along the forearm, turned 12 degrees toward the little finger, as the wrist of `Idle` turns the hand, so it hangs outward at the side with its point down. Flat material colors. |
| `art/Attachments/IronGreataxe` | `static` | `Grip` | A two-handed iron greataxe held on `main_hand`, 2,162 triangles, 1.33 m with its top spike: a crescent blade with a polished edge of 41 cm, an arc window slot, and a brass Tiwaz rune, a curved back spike, an eye with collars and riveted langets, a red cloth band with tails, an iron knob, and leather wraps where the right hand holds it, 38 cm from the butt, and the left, 28 cm further down, its `offHandGrip`, which its swing brings that hand to. Hand-painted on one texture. `holdClip` `HoldGreataxe`, `attackClip` `GreataxeSwing`, released at 0.6 s. |
| `art/Attachments/IronSword` | `static` | `Grip` | A plain iron arming sword held on `main_hand`, 1,008 triangles, 94 cm: a disc pommel with a peened tang end, a leather grip of 10 cm swelling slightly in the middle, a straight cross guard of 19 cm, and a blade of 74 cm, 5 cm wide at the guard, with a fuller over 70 % of it, tapering to the point. Flat material colors. `attackClip` `SwordSlash`, released at 0.35 s, without a hold clip. |
| `art/Attachments/MiningPickaxe` | `static` | `Grip` | The tool of the Starter Pickaxe, held on `main_hand` with both hands while mining, 960 triangles, 86 cm: an ash haft with a leather wrap in seven bands where the left hand holds it and a brass butt cap, and a curved iron head, 43 cm across, with a long point on the striking side and a flared chisel, both curving toward the hands, on a chamfered eye boss with a blue faceted ore crystal in a brass bezel on each cheek, over a brass collar inlaid in blue with the rune Fehu on its front and back. Flat material colors. `useClip` `Mine`, `offHandGrip` 33 cm down the haft. |
| `art/Attachments/OakStaff` | `static` | `Grip` | The Oak Staff, the first magic weapon, held on `main_hand`, about 1.7 m: a knotted oak staff with a silver-shod foot, a leather grip between two silver bands, a twig with two oak leaves, and four roots that hold a glowing blue crystal, whose material glows. Flat material colors. `holdClip` `HoldStaff`, `attackClip` `Cast`, released at 0.15 s, its `launchPoint` the crystal. |
| `art/Attachments/TrainingShortbow` | `skinned` | `Grip` | The bow of the Training Shortbow, held on `off_hand` in the left hand: an ash recurve with a dark laminated back, a dark oak riser with a brass oak leaf, a cross-laced leather grip, a sight window with a shelf on the left, horn tips with a carved spiral, and a linen string with a red serving, 1.23 m from tip to tip, 1,860 triangles, hand-painted on one UV layout. On the `Bow` skeleton with its clip `Draw`, 15 frames from brace to full draw. `holdClip` `HoldBow`, `attackClip` `BowShot`, released at 0.8 s, its `launchPoint` at the arrow pass 7 cm above the grip, and `drawLength` 0.62 m. |
| `art/Attachments/WoodcuttingAxe` | `static` | `Grip` | The tool of the Woodcutting Axe, held on `main_hand` with both hands while chopping, 1,090 triangles, 90 cm: an oak haft with a leather wrap in seven bands where the left hand holds it and a golden acorn as its butt knob, and a bearded iron head with a bright honed edge 21 cm long, its top horn sweeping up and its beard hanging toward the hands, with a lobed oak leaf inlaid in gold on each cheek. Flat material colors. `useClip` `Chop`, `offHandGrip` 33 cm down the haft. |
| `art/Cages/Humanoid` | `cage` | none | The garment cage of the `Humanoid` skeleton, made from the male body and fitted to the male and the female, with a figure scale of 60 mm and the standard lines; see [Garment Cage](#garment-cage). |
| `art/Characters/Human_Female` | `skinned` | `Female` | The female human base body, built by `tools/art/blender/human_build`; see [Human Base Characters](#human-base-characters). 1.70 m, in an A-pose with the arms 45 degrees out, with its source body, ten body parts, eyeballs, teeth, and tongue, 15,084 triangles, an unmirrored UV map, and the `Humanoid` rig with eyelids. Its skin is plain, in one colour, under the underwear, which is paint of its own; the faces `Blush` and `Freckles` and the eyes `Teal` and `Hazel`. |
| `art/Characters/Human_Male` | `skinned` | `Male` | The male human base body, built like the female, 1.79 m, with the same pose and topology as the female, its source body, 15,084 triangles, and the `Humanoid` rig with eyelids. Its skin is plain, in one colour, under the underwear, which is paint of its own; the faces `Stubble` and `Clean` and the eyes `Teal` and `Brown`. It is the default body model. |
| `art/Creatures/Wolf` | `skinned` | `Wolf` | The wolf of the Feral Wolf and the Feral Alpha Mobs, after the reviewer's concept picture, which the file holds as its front and side reference images: a grey low poly wolf with a charcoal saddle, mane, and tail top, a cream mask, chest, belly, and legs, blue eyes, a large mane, a long bushy tail, and dark paws with four toes each, 6,044 triangles, 1.78 m from the nose to the tail's tip and 1.13 m to the tips of the ears. Built by the [modeling rules](#modeling-rules) part by part, each a cage of quads subdivided once and fitted to the concept, the parts meeting at shared cage points without a seam: the head with modeled eyes, a dark lid around a domed eyeball, and an opening mouth, the legs with their paws, the trunk with the neck and the mane, and the tail. Hand-painted after the concept on one shared UV layout, 1024 px, `M_Wolf` with `T_Wolf`, the eyeballs at six times the texel density for the iris, the pupil, and the highlight. Rigged on the `Quadruped` skeleton as one mesh, `Wolf_Mesh`, its jaw, ears, and tail turning on their own bones. Its clips are its own, keyed by hand with the legs placed by their paws: `Idle` (120 frames, breathing, looking about, an ear flick, and a tail sway), `Walk` (16 frames, a lateral walk at 1.4 m/s, the Mobs' patrol speed), `Run` (12 frames, a rotary gallop at 4.5 m/s, their chase speed, with the spine flexing), `Attack` (30 frames, a lunge with the bite at frame 8), and `Death` (60 frames, a fall onto its side that lies on the ground). The game shows it with `Mob_FeralWolf.prefab` and, 1.3 times larger and darker, `Mob_FeralAlpha.prefab`; see [Prefabs](#prefabs). |
| `art/GarmentTextures/DarkBrownHair` | `texture` | none | Dark brown hair on `ShoulderHair`, 1024 px, painted by a script in the template's UVs, which run around the head through the locks and down along the hair: lighter strands of random width along the hair, deep grooves between the locks, a soft sheen around the crown, darker roots, and slightly lighter tips. Roughness 0.55. |
| `art/GarmentTextures/IronNasalHelm` | `texture` | none | Matte wrought iron on `NasalHelm`, 1024 px, painted by a script from each texel's point and from the part of the helmet its face is, which the template marks in the face attribute `paint_part`: hammered plates darkened by patina with a few rust specks toward the rim and into the corners beside the bands, the bands a little lighter and worn bright along their edges, domed rivets down every band, round the brow band, at the cap, and two on the nasal, the iron's dark underside at the rim, and the brown leather lining below it. Roughness 0.55, metallic 0.45. |
| `art/GarmentTextures/LeatherBoots` | `texture` | none | Dark brown leather on `Boots`, 1024 px, painted by a script from each texel's point on the template: fine grain and blotches, a darker band with stitches along the top of the shaft, stitched seams around the ankle and up the inside of the shaft and the back of the heel, a burnished toe cap and heel counter, and a near-black sole with a darker welt stitched around it. Roughness 0.55. |
| `art/GarmentTextures/LeatherGloves` | `texture` | none | Soft, lighter brown leather on `Gloves`, 1024 px, painted the same way: fine grain and blotches, stitched seams between the palm and the back along the sides of the fingers, around the wrist, and along the inside of the cuff, and a darker band with stitches at the cuff's edge. Roughness 0.6. |
| `art/GarmentTextures/LeatherJerkin` | `texture` | none | The leather jerkin on `Shirt`, 1024 px: warm brown leather with blotches and grain, darker trim with stitches along the round neckline, the hem, and the sleeve ends, and an open V at the front, laced across and running up to the neckline with its trim, where the skin shows between the laces. It has masks of its own. Roughness 0.62. |
| `art/GarmentTextures/LinenLongShirt` | `texture` | none | Plain oatmeal linen on `LongShirt`, 1024 px, with a fine weave, soft wear, stitched hems at the neckline, the hem, and the cuffs, and stitched seams. Roughness 0.82. |
| `art/GarmentTextures/LinenShirt` | `texture` | none | The same linen on `Shirt`. |
| `art/GarmentTextures/NetShirt` | `texture` | none | A coarse hemp-colored net on `Shirt`, 1024 px: round cords of 2.4 mm in diamond meshes 25 mm apart around the trunk and each arm, transparent between them, with solid hems 14 mm wide at the neckline, the hem, and the sleeve ends. It has masks of its own, so the skin and its paint show through the meshes. Roughness 0.88. |
| `art/GarmentTextures/WoolCloak` | `texture` | none | Thick dark brown wool on `Cloak`, 1024 px, painted by a script from each texel's point on the template: fibres and mottling, vertical folds that deepen down the back, and a darker turned hem with stitches along every edge. In front the cloth fans out like a triangle from a large cast bronze clasp at the bottom of each side of the yoke, 68 mm across, with a beaded rim, a rosette of eight petals, and a domed boss: its edges run from the clasp up to the side of the neck and out past the yoke's side, both reached at the top of the shoulder, with no cloth around the clasp, so the throat and the chest stay free. The alpha follows the cut by its distance from the triangle's edges, smooth across the texels. Roughness 0.82. Dyeable: one dye channel over the wool, not the clasps, natural color `#4E3422`; its image is the neutral wool, which the natural color gives back within one step of 255. |
| `art/GarmentTextures/WoolHoodDown` | `texture` | none | The wool of `WoolHoodUp` on `HoodDown`: hems with stitches along the collar's hem and the turned band's edge, the seam down the middle of the folded hood and the collar, and the folded hood's own side folds a little darker, without hems. Dyeable like `WoolRobe`, natural color `#8D806E`. |
| `art/GarmentTextures/WoolHoodUp` | `texture` | none | The undyed grey-brown wool of `WoolRobe` on `HoodUp`, 1024 px, painted by a script from each texel's point on the template: fibres and mottling, a stitched seam down the middle from the face opening over the crown to the collar's hem and down the bib, and a darker turned hem with stitches along the face opening and the collar's hem. Roughness 0.86. Dyeable like `WoolRobe`, natural color `#8D806E`. |
| `art/GarmentTextures/WoolRobe` | `texture` | none | Undyed grey-brown wool on `Robe`, 1024 px, painted the same way: fibres and mottling, folds that deepen down the skirt from the waist, stitched seams, among them the waist seam where the skirt starts, and darker turned hems with stitches at the neckline, the cuffs, and the skirt's hem. Roughness 0.86. Dyeable: one dye channel over all of it, natural color `#8D806E`; its image is the neutral wool, which the natural color gives back within one step of 255. |
| `art/GarmentTextures/WoolTrousers` | `texture` | none | Brown wool on `Trousers`, 1024 px, with a coarse weave, a darker waistband, stitched hems at the ankles, and stitched seams, among them a yoke across the hips. Roughness 0.9. |
| `art/Garments/Boots` | `garment` | none | Boots in the `mid` band, layer 120, from the recipe `legs` and `feet`, cut at `BootCalf`, over `tight` trousers below `LegAnkle`, turned edges 4 mm deep, 1,376 triangles per body: a shaft to the middle of the calf 10 mm off the cage and a foot over the cage's smooth feet, without toes, with a flat sole. |
| `art/Garments/Cloak` | `garment` | none | A cloak in the `back` slot, layer 500, `tight` band, from the recipe `neck` and `trunk`, cut at `Neckline` and `CloakYoke`, over a `mid` top above `CloakYoke` and below `Neckline`, so it lies about 1 cm off the shirt and leaves room for a padded top, turned edges 4 mm deep: a yoke over the trapezius, not the shoulder muscle, from the round neckline down to below the collarbones in front, kept within a top of 0.72 of the shoulders' half width, and a loose back hanging taut from `CloakNape`, just below the base of the neck, between the arms on the `cloak` chains to the middle of the calves, fanning out to a width of 0.8 with a flare of 0.03, 892 triangles per body, 243 vertices of them on the chains. It covers nothing (`"covers": false`). One dye channel. |
| `art/Garments/Gloves` | `garment` | none | Gloves with five fingers in the `tight` band, layer 350, from the recipe `arms` and `hands`, cut at `GloveCuff`, over a `tight` sleeve below `SleeveLong`, turned edges 2 mm deep, 1,840 triangles per body, the cuff about 7 cm up the forearm. |
| `art/Garments/HoodDown` | `garment` | none | The gugel of `HoodUp` pushed back, modeled, in the `head` slot, layer 600, `loose` band, `"hair": "show"`, 1,036 triangles per body: the collar around the neck over the hair and the cloak, its top rolled over into a band that hangs outside it, and the hood folded on the upper back as a rounded bag with soft folds, from the roll down 20 cm. Two sheets meet at the roll's top, the collar and the band with the folded hood, each with its outside out. The collar's weights move with the neck and blend into those of the cloak where it lies on it. Its collar is open around the neck, so it covers nothing (`"covers": false`). One dye channel. |
| `art/Garments/HoodUp` | `garment` | none | A gugel worn up, modeled, in the `head` slot, layer 600, `loose` band, `"hair": "compress"`, 1,186 triangles per body: over the head and the pressed hair with an opening for the face from the brows to under the chin, and a short collar over the neck and the tops of the shoulders that ends above the cloak's clasps, over the cloak, built on rings over the head, the hair, the body, and the cloak. It moves with the head down to the chin and blends over the neck into the upper chest and the shoulders, and where the collar lies on the cloak into the cloak's own weights, halfway where the hair lies close under it. One dye channel. |
| `art/Garments/LongShirt` | `garment` | none | `Shirt` with long sleeves, cut at `SleeveLong`, 2,468 triangles per body. |
| `art/Garments/NasalHelm` | `garment` | none | A nasal helmet, modeled, in the `head` slot, layer 600, `outer` band, `"hair": "compress"`, 2,092 triangles per body within a budget of 2,500, all on `Head`: a pointed spangenhelm over the head and the pressed hair, four bands raised 2.5 mm from the front, the sides, and the back to the point, a brow band 28 mm high above the brows and the ears, the leather lining 4.5 mm below the iron, and a nasal riveted to the brow band that bends forward over the nose's root and runs down the bridge to 2 mm below the tip of the nose. The nasal is its own island of the mesh and stays stiff: the fitting's smooth field bends a thin bar that stands off the face, so the female version's nasal is shaped by hand, a rigid copy of the male's laid on her brow band, with its shaft as long as her nose asks for and leaning 8 degrees more forward from the bend, because the rings of the cage's head stand up to 15 mm off the bridge of her nose and the band holds from the cage. |
| `art/Garments/Robe` | `garment` | none | An ankle-length robe in the `body` slot, layer 300, `tight` band, from the recipe `neck`, `trunk`, and `arms`, cut at `Neckline`, `SkirtWaist`, and `SleeveLong`, turned edges 3 mm deep: `LongShirt`'s top down to just below the navel with a loose skirt hanging all around from `SkirtWaist` on the `skirt` chains down to `SkirtHem`, just above the ankle: it leaves the body evenly from the waist down to the hip joints, where it lies 30 mm off the hips and the legs, and falls straight from there with a flare of 0.04, so it does not puff out at the waist, 2,980 triangles per body, 384 vertices of them on the chains, 320 of its cloth and 64 of the turned edge of its hem. It hides the `legs` slot, so the boots show below it. One dye channel. |
| `art/Garments/Shirt` | `garment` | none | A short-sleeved top in the `tight` band, layer 150, from the recipe `neck`, `trunk`, and `arms`, cut at `Neckline`, a round neckline that hugs the neck, `HemHip`, and `SleeveShort`, over `tight` trousers below `Waist`, turned edges 2 mm deep, without ease or collar, 1,704 triangles per body. |
| `art/Garments/ShoulderHair` | `garment` | none | Shoulder-length hair, modeled, in the `hair` slot, layer 550, `hair` band, 2,298 triangles per body: straight, thick stylized locks that fall from the crown, a middle parting, a short blunt fringe, the ears covered, the ends where they land on the shoulders, and the shape key `UnderHeadwear`, the hair pressed flat under hoods and helmets with the fringe and the neck hair showing. Its weights are painted by hand. |
| `art/Garments/Trousers` | `garment` | none | Trousers in the `tight` band, layer 100, from the recipe `trunk` and `legs`, cut at `Waist` and `LegAnkle`, turned edges 2 mm deep, 1,904 triangles per body, with seams along the inside of the legs. |
| `art/Paint/Bra` | `paint` | none | The woman's `underwear` in the `body` slot over `M_Body`, layer 910, painted on a canvas of the female after her form: a pale blue bra with a darker band about 2.5 cm high under the bust, a cup over each breast with one smooth upper edge down to a centre piece between the breasts, and darker straps about 1.2 cm wide from the cups over the shoulders, midway between the neck and the shoulder joint, and straight down the back to the band. Roughness 0.8. |
| `art/Paint/Briefs` | `paint` | none | `underwear` in the `legs` slot over `M_Body`, layer 900, on a canvas of the male, moved to the female: pale blue briefs with a darker waistband, lifted out of the male body texture. Roughness 0.8. |
| `art/Paint/ForearmRunes` | `paint` | none | A one-sided `tattoo` over `M_Body`, layer 210, painted by a script on a canvas of the male and moved to the female: the runes of the fuþark, f u þ a r k, in a column down the outside of the left forearm between two thin lines, 30 mm apart, in the ink of `Tattoo`. Roughness 0.6. |
| `art/Paint/Scar` | `paint` | none | A `scar` over `M_Face`, layer 100, on a canvas of the male, moved to the female: a pale scar with a darker outline over the brow, the outer corner of the right eye, and down the cheek. Roughness 0.5. |
| `art/Paint/Tattoo` | `paint` | none | A `tattoo` over `M_Body`, layer 200, painted on a canvas of the male and moved to the female: a Nordic armband of two thin rings with interlocking chevrons and dots around each upper arm, and a round rune emblem of eight spokes, rings, and ticks on the upper breastbone, in dark blue-black, slightly faded ink. |
| `art/Projectiles/BronzeArrow` | `static` | `Arrow` | The arrow of every weapon that shoots arrows: a flat bronze broadhead on a six-sided shaft with three fletching vanes, the red cock vane up along +Z, and its nock 76 cm behind the point, 202 triangles, hand-painted; see [Projectiles](#projectiles). |
| `art/WorldActors/Resource_IronVein` | `static` | `Available`, `Depleted` | Three faceted rocks with chunky rust-orange iron ore in two crossing veins, 684 triangles; the depleted state keeps the rocks with broken flat tops and loose rubble, without ore, 194 triangles. The available state is about 1.5 m wide, 1.25 m deep, and 1.0 m high, inside the vein's 1.6 by 1.2 by 1.6 m interaction bounds; the depleted rubble spreads to about 1.9 by 1.7 m. Flat material colors. |
| `art/WorldActors/Resource_OakTree` | `static` | `Available`, `Depleted` | A fantasy oak, 1,456 triangles in all: a thick trunk twisted about 70 degrees on five flaring roots, three boughs into a broad crown of eight faceted leaf clumps, dark green with light green where a face looks up, and four golden acorns of 41 cm on stalks below the crown's rim, so the tree reads from every side; the depleted state is the stump cut slanted at about 0.8 m with a pale heartwood face, two splinters on its high side, the same roots, and five wood chips. The tree is about 5.6 m high and 4 m wide, inside its 4 by 5.6 by 4 m interaction bounds. Five flat materials: bark, leaves, light leaves, acorn, and heartwood. |

## Planned

- Forearm twist bones.
- Making a body type for a new race from a human body, with the human base tools
  working for every body of the race; see
  [Future Races And Base Models](EQUIPMENT_VISUALS_DESIGN.md#future-races-and-base-models).
- A shared palette texture for stylized models, level-of-detail meshes as
  [Budgets](EQUIPMENT_VISUALS_DESIGN.md#budgets) plans, and per-asset collision
  proxies if presentation ever needs them.

# Game Content

This directory holds every authored gameplay definition. It is the only place
where designers and tools change content by hand or through the Unity content
tools. Compiled data is generated from it and never edited directly.

## Layout

```text
content/
  items/
    catalog.json                    categories, tags, equipment slots, tiers
    definitions/<definition-id>.json one item definition per file
  actors/
    catalog.json                    factions, presentation, activity and respawn profiles
    definitions/<actor-id>.json     one NPC or mob definition per file
  worlds/<world-id>/
    world.json                      World manifest read by both services
    actor-spawns.json               spawn points, groups, areas, and patrol paths
    collision.json                  collision source baked from the World scene
```

One file per definition keeps reviews small and lets several people add or
change different definitions without merge conflicts in one large file.

## Rules

- A definition file is named after its id: `definitions/weapon.training_rifle.json`
  must contain `"id": "weapon.training_rifle"`. Ids use lowercase letters,
  digits, `.`, `_`, and `-`, start and end with a letter or digit, and never use
  a Windows device name such as `con` or `nul` before the first dot.
- A definitions directory contains only `<id>.json` files. Hidden files are
  ignored; any other file or subdirectory fails compilation.
- `actor-spawns.json` must declare the `worldId` of its directory, and the World
  directory name must equal the manifest `worldId`.
- Every compiler parses strictly: unknown properties, duplicate properties,
  comments, trailing commas, and missing required values fail.
- Files are written in the canonical format of the Unity content tools, so a save
  without changes produces no diff. Item files use ordered DataContract JSON with
  two-space indentation. Actor and spawn files use JsonUtility JSON with
  four-space indentation and the shortest text for each float. EditMode tests
  fail when a committed file is not canonical.
- Released definition ids are permanent. Item ids that reached runtime JSON are
  locked by the Item Catalog tool, and persisted state refers to them.

The item authoring contract is described in [items/README.md](items/README.md).
The actor and spawn contract is described in
[../docs/NPC_AND_MOB_SYSTEM_DESIGN.md](../docs/NPC_AND_MOB_SYSTEM_DESIGN.md).

## Generated Data

The compilers write deterministic runtime data into the Unity World data package
at `src/Shared/WorldData/Generated/`:

| Generated file | Source | Compiler |
| --- | --- | --- |
| `Items/core.item-catalog.json` | `items/` | `tools/ItemCatalogCompiler` |
| `Worlds/<world-id>/world-actors.json` | `actors/` and the World's `actor-spawns.json` | `tools/WorldActorCompiler` |
| `Resources/WorldCollision/<world-id>/` | the World's `collision.json` | `tools/WorldCollisionCompiler` |

Generated data is committed, so the services and the client run without a
content build step, and CI verifies that it matches its sources. The client
loads collision through `Resources.Load("WorldCollision/<world-id>/manifest")`.
GameService and SimulationWorker copy `world.json` and the generated files into
their output `WorldData` folders at build time.

Every World's `world-actors.json` embeds the shared actor catalog, so an actor
change recompiles every World. The Unity Actor Studio does this on save.

## Compile And Verify

Each compiler works on every World at once. From the repository root:

```powershell
dotnet run --project tools/ItemCatalogCompiler
dotnet run --project tools/WorldActorCompiler
dotnet run --project tools/WorldCollisionCompiler
```

Add `-- --verify` to check that the committed generated data matches the
content without writing anything; this is what CI runs. A stale or missing
generated file, or generated data for content that no longer exists, returns
exit code 1. Compile mode deletes such orphaned generated data together with its
Unity `.meta` file.

`--content-root <directory>` and `--generated-root <directory>` point a compiler
at other directories. The Unity content tools use them to validate candidate
content in a temporary directory before anything is written.

## Unity Content Tools

`MMORPG > Tools > Item Catalog`, `MMORPG > Tools > Content > Actor Studio`, and
`MMORPG > Tools > Content > Spawn Authoring` load these files, validate every
change with the same command-line compilers that CI runs, and write all affected
source and generated files in one transaction that is rolled back on failure.
`MMORPG > Tools > World Collision > Bake Open Scene` bakes collision from the
open World scene into `worlds/<world-id>/collision.json` and the generated
chunks.

# Game Content

This directory holds every authored gameplay definition. It is the only place
where designers and tools change content by hand or through the Unity content
tools. Compiled data is generated from it and never edited directly.

## Layout

```text
content/
  items/
    catalog.json                    character and economy settings, categories, tags, tool kinds,
                                    equipment slots, tiers, retired ids
    definitions/<definition-id>.json one item definition per file
    vendors/<vendor-id>.json        one vendor profile per file
  development/
    item-packages.json              item states for the development item tool
  actors/
    catalog.json                    factions, presentation, and activity, respawn, combat,
                                    brain, locomotion, loot, and resource profiles
    definitions/<actor-id>.json     one NPC, Mob, or resource node definition per file
  worlds/<world-id>/
    world.json                      World manifest read by both services
    actor-spawns.json               spawn points, groups, areas, and patrol paths
    collision.json                  collision source baked from the World scene
```

One file per definition keeps reviews small and lets several people add or
change different definitions without merge conflicts in one large file.

## Rules

- A definition file is named after its id: `definitions/weapon.training_shortbow.json`
  must contain `"id": "weapon.training_shortbow"`. Ids use lowercase letters,
  digits, `.`, `_`, and `-`, start and end with a letter or digit, and never use
  a Windows device name such as `con` or `nul` before the first dot.
- A definitions directory contains only `<id>.json` files. Hidden files are
  ignored; any other file or subdirectory fails compilation. `items/vendors`
  follows the same rules: `vendors/vendor.city_general_store.json` must contain
  `"id": "vendor.city_general_store"`. A catalog without vendor profiles may
  omit the directory.
- Item content uses catalog format version 5, set by `formatVersion` in
  `items/catalog.json`. The actor catalog and every `actor-spawns.json` use
  format version 3. The compilers reject any other version.
- Every directory below `worlds/` except hidden ones is a World and must contain
  `world.json`, `actor-spawns.json`, and `collision.json`. Both World compilers
  fail on an incomplete World instead of skipping it, because the
  SimulationWorker cannot start a World without all three.
- `actor-spawns.json` must declare the `worldId` of its directory, and the World
  directory name must equal the manifest `worldId`.
- Every compiler parses strictly: unknown properties, duplicate properties,
  comments, trailing commas, and missing required values fail.
- Files are written in the canonical format of the Unity content tools, so a save
  without changes produces no diff. Item files, including vendor profiles, use
  ordered DataContract JSON with two-space indentation. Actor and spawn files use
  JsonUtility JSON with four-space indentation and the shortest text for each
  float. EditMode tests fail when a committed file is not canonical.
- Released definition ids are permanent. Item ids that reached runtime JSON are
  locked by the Item Catalog tool, and persisted state refers to them. A released
  item id that is removed moves to the catalog's retired ids and can never be
  reused.
- `development/item-packages.json` names item definitions for the development
  item tool. Its packages are checked against the compiled catalog by
  `ContentPackagesFitTheCompiledItemCatalog`, so a content change that breaks a
  package fails a test.

The item authoring contract is described in [items/README.md](items/README.md).
The actor and spawn contract is described in
[../docs/NPC_AND_MOB_SYSTEM_DESIGN.md](../docs/NPC_AND_MOB_SYSTEM_DESIGN.md).

## Resource Nodes And Item References

Actor content has three actor kinds: `npc`, `mob`, and `resource_node`. A
resource node is a world object that players gather items from, such as an ore
vein. These rules connect actor content to the item catalog:

- Every capability has a `profileId`. A `vendor` capability names a vendor
  profile of the item catalog, and a `gathering` capability names a resource
  profile of the actor catalog. Every other capability kind leaves it empty.
- `resourceProfiles` in `actors/catalog.json` is required and may be empty. Each
  profile has an `id`, the `requiredToolKind` an item catalog tool kind must
  provide, the `yieldDefinitionId` and `yieldQuantity` gathered per cycle, the
  `cycleSeconds` of one cycle from 0.5 to 60, the `charges` a node holds from 1
  to 1000, and the `respawnSeconds` from 1 to 86400 after which a depleted node
  returns with full charges.
- A resource node has exactly one capability, of kind `gathering`, and gathering
  exists only on resource nodes. Mobs have no capabilities. NPCs and resource
  nodes are invulnerable and leave every activity, respawn, combat, brain,
  locomotion, loot, and corpse field empty; a resource node's charges and
  respawn come from its resource profile. Its presentation archetype must name
  the actor kind `resource_node`.
- Spawn points, groups, and areas place resource nodes like any other actor. A
  patrol path can only be assigned to a Mob spawn.

The shipped resource profile `resource.iron_vein` needs the `pickaxe` tool kind
and yields one `material.iron_ore` every 3 seconds, with 5 charges and a 30
second respawn. The actor `resource.iron_vein`, "Iron Vein", is a neutral,
invulnerable resource node in the `world` faction with the presentation
archetype `resource.iron_vein` and one capability `resource.mine` ("Mine")
that names that profile. Its placements are listed in
[Development Worlds](../docs/DEVELOPMENT_WORLDS.md#resource-node-placements).
The Trade capability of `npc.city_services` names the vendor profile
`vendor.city_general_store`.

`WorldActorItemReferences` checks every compiled World's actor data against the
compiled item catalog:

- Mob loot names existing definitions, never a quest item, and at most one
  stack of each.
- Every vendor capability names an existing vendor profile.
- Every resource profile's tool kind exists, and at least one item definition
  provides it.
- Every yield exists, is not a quest item, a Bag, or an item with default
  policies, and its quantity fits one stack.

## Generated Data

The compilers write deterministic runtime data into the Unity World data package
at `src/Shared/WorldData/Generated/`:

| Generated file | Source | Compiler |
| --- | --- | --- |
| `Items/core.item-catalog.json` | `items/` | `tools/ItemCatalogCompiler` |
| `Worlds/<world-id>/world-actors.json` | `actors/` and the World's `actor-spawns.json`, with loot, vendor capabilities, and resource profiles checked against `Items/core.item-catalog.json` | `tools/WorldActorCompiler` |
| `WorldCollision/<world-id>/` | the World's `collision.json` | `tools/WorldCollisionCompiler` |

Generated data is committed, so the services and the client run without a
content build step, and CI verifies that it matches its sources. The client
loads collision through Addressables at `WorldCollision/<world-id>/manifest.json`
and `WorldCollision/<world-id>/<chunk>.bytes`; the generated folder is one
Addressables folder entry.
GameService and SimulationWorker copy `world.json` and the generated files into
their output `WorldData` folders at build time.

Every World's `world-actors.json` embeds the shared actor catalog, so an actor
change recompiles every World. The Unity Actor Studio does this on save.

## Compile And Verify

Each compiler works on every World at once. Run the item catalog compiler
before the actor compiler, because actor content refers to item content: the
actor compiler reads `Items/core.item-catalog.json` from the generated root and
checks Mob loot, vendor capabilities, and resource profiles against it. From the
repository root:

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
The Item Catalog window edits item definitions and vendor profiles, and Actor
Studio edits NPCs, Mobs, and resource nodes. The economy settings, the tool kind
identities, and the resource profiles are edited by hand in their `catalog.json`
files.
`MMORPG > Tools > World Collision > Bake Open Scene` bakes collision from the
open World scene into `worlds/<world-id>/collision.json` and the generated
chunks.

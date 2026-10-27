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
  combat/
    combat-rules.json               player combat rules, arrow batching, and Shard rule sets
  characters/
    appearance.json                 the kinds of a character's look, the body models with their
                                    options, and the equipment slots other players see
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
  format version 4. The compilers reject any other version.
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

## Examine Text And Menu Options

Actor format version 4 added the player-facing text of the right-click world
menu, described in [Game Features](../docs/GAME_FEATURES.md#world-menu):

- Every actor has an `examineText`, one trimmed line of 1 to 200 characters
  without control characters, which Examine shows, such as "A lean, hungry
  wolf. It eyes you like a meal."
- Every capability has a `menuOption`, one trimmed line of 1 to 32 characters,
  the verb the menu shows before the actor's name, such as `Talk-to`, `Trade`,
  or `Mine`. `displayName` still names the service in the interaction panel.
- Capabilities keep their authored order, which is the menu order: the first
  capability is the left-click action. Put a dialogue capability first for a
  `Talk-to` default. Actor Studio's `Move Up` and `Move Down` change the order.

Both limits fit the realtime protocol limits that `WorldActorSpawn` carries.

## Collision Bounds

Actor format version 5 added `collisionBounds` to every actor definition, the
box that blocks character movement, with the same fields as `interactionBounds`:
a center offset from the actor's position and a size, aligned to the World axes
whatever the spawn's yaw.

- A resource node has a box of positive size up to 16 m on each axis. Resource
  nodes never move, and a depleted node keeps its footprint, such as a stump or
  rubble, so one box serves both states.
- NPCs and Mobs set every value to zero. They do not block movement.
- `tools/WorldCollisionCompiler` adds one box per placed resource node from the
  compiled World actors to the World's collision chunks, on the movement layer
  only, so characters and Mobs walk around a node while sight, interaction line
  of sight, and arrows pass it. The Unity bake adds the same boxes, so compile
  the actors before the collision, as the commands below do. Saving in Actor
  Studio or Spawn Authoring recompiles the collision by itself.

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
that names that profile, and a collision box of 1.2 by 1.0 by 1.2 m on the
ground. The resource profile `resource.oak_tree` needs the
`woodcutting_axe` tool kind and yields one `material.oak_log` at the same pace,
charges, and respawn. The actor `resource.oak_tree`, "Oak Tree", is a resource
node like the vein, with the presentation archetype `resource.oak_tree`, the
tags `resource` and `woodcutting`, and one capability `resource.chop` ("Chop",
menu option "Chop down") that names that profile. The tree is tall, so its
interaction bounds of 4 by 5.6 by 4 m, centered 2.8 m up, cover its crown, and
its collision box of 1.0 by 2.4 by 1.0 m is its trunk.
Their placements are listed in
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

## Combat Rules

`combat/combat-rules.json` holds the combat values that belong to no item or
actor. It has no compiler: the SimulationWorker build copies it to
`WorldData/Combat/combat-rules.json` in its output, and the worker validates it
strictly at startup, with the same parser rules as the compilers. Edit it by
hand and restart the worker.

| Field | Shipped value | Allowed |
| --- | --- | --- |
| `formatVersion` | 2 | 2 |
| `player.maximumHealth` | 100 | 1 to 1,000,000 |
| `player.maximumMana` | 100, the mana that magic attacks spend; 0 leaves characters without magic | 0 to 1,000,000 |
| `player.respawnDelaySeconds` | 5 | 0 to 300 |
| `player.combatTimeoutSeconds` | 5, the time after the last attack or hit until a combatant leaves combat and starts to regenerate | 0 to 600 |
| `player.healthRegenerationPerSecond` | 1, the health a character out of combat regains every second | 0 to `maximumHealth` |
| `player.manaRegenerationPerSecond` | 1, the mana a character out of combat regains every second | 0 to `maximumMana` |
| `player.equipmentLockoutSeconds` | 1 | 0 to 30 |
| `ammunition.batchIntervalSeconds` | 5, the time after the first uncommitted shot until the worker commits the spent arrows | 0.5 to 60 |
| `ruleSets` | `mvp-open-risk` with `playerVersusPlayer` `true` | 1 to 64 rule sets with unique, lowercase, sorted ids |

Every Shard in the GameService topology names a rule set, and the worker stops
when its Shard names one that this file does not define.
`EveryConfiguredShardRuleSetIsDefined` keeps the checked-in topology and
this file in step.

## Character Appearance

`characters/appearance.json` names what a character's look can be, as
[Equipment Visuals Design](../docs/EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server)
describes. It has no compiler: the GameService and SimulationWorker builds copy it
to `WorldData/Characters/appearance.json` in their output, and both validate it
strictly at startup, with the same parser rules as the compilers. GameService
checks every look it stores against it and refuses to start when a stored look
names an option the file no longer offers; remove an option only after a data
migration moved the looks that use it.

| Field | Meaning |
| --- | --- |
| `formatVersion` | 1 |
| `kinds` | Each kind once, one of `face`, `eyes`, `scar`, `tattoo`, `underwear`, and `hair`, with `required`, exactly one per offered slot instead of at most one, and `changesInGame`, changed in the game after creation |
| `bodyModels` | 1 to 16 body models, the default first, each with its `offers`: a listed kind, a visual slot for a kind worn per slot such as `legs` for underwear or empty, and 1 to 32 options, the first the default |
| `visibleEquipmentSlots` | At most 8 equipment slot ids, each once, whose items other players see, in the order the worker sends them |

Body model ids and options are named as the body models and their appearance
catalogs in the Unity project name them, such as `Human_Female` and `Freckles`;
`CharacterLookTests` fails when the file and the catalogs disagree. Restart both
services after a change.

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

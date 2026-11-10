# Game Content

This directory holds every authored gameplay definition. It is the only place
where designers and tools change content by hand or through the Unity content
tools. Compiled data is generated from it and never edited directly.

## Layout

```text
content/
  items/
    catalog.json                    character and economy settings, categories, tags, tool kinds,
                                    equipment slots, families, tiers, retired ids
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
  quests/
    catalog.json                    the active quest limit and the retired quest ids
    definitions/<quest-id>.json     one quest definition per file
  worlds/<world-id>/
    world.json                      World manifest read by both services: id, display name,
                                    client scene, ground height, playable bounds, and spawn
    actor-spawns.json               spawn points, groups, areas, and patrol paths
    collision.json                  collision source baked from the World scene
```

One file per definition keeps reviews small and lets several people add or
change different definitions without merge conflicts in one large file. The
development Worlds themselves, their scenes, and their layouts are described in
[Development Worlds](../docs/DEVELOPMENT_WORLDS.md).

## Rules

- A definition file is named after its id: `definitions/weapon.training_shortbow.json`
  must contain `"id": "weapon.training_shortbow"`. Ids have at most 128
  characters, use lowercase letters, digits, `.`, `_`, and `-`, start and end
  with a letter or digit, and never use a Windows device name such as `con` or
  `nul` before the first dot.
- A definitions directory contains only `<id>.json` files. Hidden files are
  ignored; any other file or subdirectory fails compilation. `items/vendors`
  follows the same rules: `vendors/vendor.city_general_store.json` must contain
  `"id": "vendor.city_general_store"`. A catalog without vendor profiles may
  omit the directory.
- Every content file carries a format version, and its reader accepts only the
  version it implements. The item catalog format is described in
  [Item Catalog Authoring](items/README.md#authoring-contract). The actor
  catalog and every `actor-spawns.json` share one format, whose version is
  `WorldActorDataFormat.Version` in the WorldData runtime, and the quest catalog
  version is `QuestCatalogFormat.Version`.
- Every directory below `worlds/` except hidden ones is a World and must contain
  `world.json`, `actor-spawns.json`, and `collision.json`. Both World compilers
  fail on an incomplete World instead of skipping it, because the
  SimulationWorker cannot start a World without all three.
- `actor-spawns.json` must declare the `worldId` of its directory, and the World
  directory name must equal the manifest `worldId`.
- The item, actor, and quest compilers, and the services that load
  `combat-rules.json` and `appearance.json`, parse strictly: unknown properties, duplicate
  properties, comments, trailing commas, and missing required values fail.
  `collision.json` is written only by the Unity collision bake.
- Files are written in the canonical format of the Unity content tools, so a save
  without changes produces no diff. Item files, including vendor profiles, use
  ordered DataContract JSON with two-space indentation. Actor and spawn files use
  JsonUtility JSON with four-space indentation and the shortest text for each
  float. EditMode tests fail when a committed item, actor, or spawn file is not
  canonical. Quest files are edited by hand in the same four-space JSON.
- Released definition ids are permanent. Item ids that reached runtime JSON are
  locked by the Item Catalog tool, and persisted state refers to them. A released
  item id that is removed moves to the catalog's retired ids and can never be
  reused.
- `development/item-packages.json` names item definitions for the development
  item tool. Its packages are checked against the compiled catalog by
  `ContentPackagesFitTheCompiledItemCatalog`, so a content change that breaks a
  package fails a test.

The item authoring contract is described in [items/README.md](items/README.md).
The actor and spawn model is described in
[NPC And Mob System Design](../docs/NPC_AND_MOB_SYSTEM_DESIGN.md).

## Examine Text And Menu Options

Every actor carries the player-facing text of the right-click world menu,
described in [Game Features](../docs/GAME_FEATURES.md#world-menu):

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

Every actor definition has `collisionBounds`, the box that blocks character
movement, with the same fields as `interactionBounds`: a center offset from the
actor's position and a size, aligned to the World axes whatever the spawn's yaw.

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

## Quests

`quests/catalog.json` holds `maximumActiveQuests`, how many quests a character
can have active at once, and `retiredQuestIds`. Every file in
`quests/definitions` is one quest of the model in
[Quest System Design](../docs/QUEST_SYSTEM_DESIGN.md#quest-model). Quest Studio
edits them, as [Quest Studio](#quest-studio) describes:

| Field | Meaning |
| --- | --- |
| `id`, `title` | The permanent quest id and the title |
| `giverActorId`, `turnInActorId` | The NPC definitions that offer and complete the quest, which may be the same |
| `minimumLevel`, `requiredQuestIds` | The character level and the quests completed first that the quest needs |
| `repeatable` | Whether the quest can be accepted again after its turn-in |
| `tags` | Content tags for authoring and filters |
| `offerText`, `progressText`, `completionText` | What the giver says to offer the quest, what the turn-in NPC says while it is not ready, and what it says at the turn-in |
| `startItems` | Quest items granted at acceptance, each a `definitionId` and a `quantity` |
| `steps` | The steps in order, each an `id` and its `objectives` |
| `rewardGold`, `rewardItems`, `rewardChoiceItems` | The rewards; the player picks one of the choice items |

Every objective has an `id`, a `kind`, its line of `text`, a `count`, and the
fields of its kind, and leaves every other field empty:

| Kind | Fields |
| --- | --- |
| `kill` | `actorIds`, the Mobs that count, or `actorTag`, a tag that the Mobs carry |
| `collect` | `itemId`, and for a quest item that Mobs drop, `dropSources`: each an `actorId`, a `chanceBasisPoints`, and a `quantity` |
| `deliver` | `itemId` and `npcActorId`, the NPC who takes the item |
| `talk_to` | `npcActorId` and the `line` that the NPC says; the count is 1 |
| `gather` | `actorIds`, the resource nodes whose yields count |

The compiler checks every quest against the compiled item catalog and the
compiled actors of every World and rejects what
[Validation](../docs/QUEST_SYSTEM_DESIGN.md#validation) lists, within the
[limits](../docs/QUEST_SYSTEM_DESIGN.md#limits). A quest NPC is an NPC with
the `quests` capability, listed first so that a left click opens the quest
dialog. Each quest gets a structural
fingerprint over its steps and its objectives' ids, kinds, and targets, which
stored progress depends on, and the catalog gets a revision over everything.

The checked-in quests are a development chain in Development World 2 between
Mira the Quartermaster in the town and Sergeant Hale, `npc.outpost_sergeant`, at
the outpost:

| Quest | Giver and turn-in | Requires | Objectives | Rewards |
| --- | --- | --- | --- | --- |
| `quest.wolves_at_the_gate`, "Wolves at the Gate" | Mira | | Kill 5 Feral Wolves | 20 Gold |
| `quest.pelts_and_fangs`, "Pelts and Fangs" | Mira | Wolves at the Gate | Collect 3 Wolf Pelts and 2 Wolf Fangs, which Feral Wolves drop for the quest at a 50 % chance | 10 Gold and Leather Gloves or Leather Boots |
| `quest.letter_for_the_outpost`, "A Letter for the Outpost" | Mira to Sergeant Hale | | Bring the Sealed Letter, a start item | 15 Gold |
| `quest.sergeants_request`, "The Sergeant's Request" | Sergeant Hale | A Letter for the Outpost | Gather 5 ore from Iron Veins, then talk to Mira | 30 Gold |
| `quest.ore_for_the_quartermaster`, "Ore for the Quartermaster" | Sergeant Hale | The Sergeant's Request | Deliver 3 Iron Ore to Mira | 15 Gold |
| `quest.the_alpha`, "The Alpha" | Sergeant Hale | The Sergeant's Request and Wolves at the Gate | Kill the Feral Alpha and collect the Alpha's Fang, which it always drops for the quest | 50 Gold and a choice of the Iron Sword, the Iron Greataxe, the Oak Staff, or the Training Shortbow |

The Wolf Fang and the Alpha's Fang are the quest items `quest_item.wolf_fang`
and `quest_item.alpha_fang`; a quest item belongs to one quest. `QuestContentTests`
checks that the chain keeps its requirements, uses every objective kind, a quest
drop, a reward choice, a start item, a delivery to another NPC, several steps,
and a follow-up that requires two quests, and that Development World 2 places
everything it needs.

## Combat Rules

`combat/combat-rules.json` holds the combat values that belong to no item or
actor. It has no compiler: the SimulationWorker build copies it to
`WorldData/Combat/combat-rules.json` in its output, and the worker validates it
strictly at startup. Edit it by hand and restart the worker.

| Field | Shipped value | Allowed |
| --- | --- | --- |
| `formatVersion` | The current format | `CombatRulesFormat.Version` in the WorldData runtime |
| `player.maximumHealth` | 100 | 1 to 1,000,000 |
| `player.maximumMana` | 100, the mana that magic attacks spend; 0 leaves characters without magic | 0 to 1,000,000 |
| `player.respawnDelaySeconds` | 5 | 0 to 300 |
| `player.combatTimeoutSeconds` | 5, the time after the last attack or hit until a combatant leaves combat and starts to regenerate | 0 to 600 |
| `player.healthRegenerationPerSecond` | 1, the health a character out of combat regains every second | 0 to `maximumHealth` |
| `player.manaRegenerationPerSecond` | 1, the mana a character out of combat regains every second | 0 to `maximumMana` |
| `player.equipmentLockoutSeconds` | 1 | 0 to 30 |
| `ammunition.batchIntervalSeconds` | 5, the time after the first uncommitted shot until the worker commits the spent arrows | 0.5 to 60 |
| `mobs.killCreditRangeMeters` | 100, the distance from a dying Mob within which its tagger gets the kill credit and the loot | 1 to 1,000 |
| `ruleSets` | `mvp-open-risk` with `playerVersusPlayer` `true` | 1 to 64 rule sets with unique, lowercase, sorted ids |

Every Shard in the GameService topology names a rule set, and the worker stops
with `shard_rule_set_unknown` when its Shard names one that this file does not
define. `EveryConfiguredShardRuleSetIsDefined` keeps the checked-in topology and
this file in step.

## Character Appearance

`characters/appearance.json` names what a character's look can be, as
[Equipment Visuals Design](../docs/EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server)
describes. It has no compiler: the GameService and SimulationWorker builds copy it
to `WorldData/Characters/appearance.json` in their output, and both validate it
strictly at startup. GameService checks every look it stores against it and
refuses to start when a stored look names an option the file no longer offers;
remove an option only after a data migration moved the looks that use it.
SimulationWorker also picks the looks and outfits of development bots from it.

| Field | Meaning |
| --- | --- |
| `formatVersion` | `CharacterAppearanceFormat.Version` in the WorldData runtime |
| `kinds` | Each kind once, one of `face`, `eyes`, `scar`, `tattoo`, `underwear`, and `hair`, with `required`, exactly one per offered slot instead of at most one, and `changesInGame`, changed in the game after creation |
| `bodyModels` | 1 to 16 body models, the default first, each with at most 16 `offers`: a listed kind, a visual slot for a kind worn per slot such as `legs` for underwear or empty, and 1 to 32 options, the first the default |
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
| `WorldCollision/<world-id>/` | the World's `collision.json` and the boxes of its placed resource nodes from `world-actors.json` | `tools/WorldCollisionCompiler` |
| `Quests/core.quest-catalog.json` | `quests/`, checked against `Items/core.item-catalog.json` and every World's `world-actors.json` | `tools/QuestCatalogCompiler` |
| `Text/en/items.text-table.json` | the names in `items/` | `tools/ItemCatalogCompiler` |
| `Text/en/actors.text-table.json` | the names, examine texts, and capability texts in `actors/` | `tools/WorldActorCompiler` |
| `Text/en/quests.text-table.json` | the texts in `quests/` | `tools/QuestCatalogCompiler` |

Generated data is committed, so the services and the client run without a
content build step, and CI verifies that it matches its sources. The client
loads collision through Addressables at `WorldCollision/<world-id>/manifest.json`
and `WorldCollision/<world-id>/<chunk>.bytes`; the generated folder is one
Addressables folder entry.

The builds copy what each process reads into its output `WorldData` folder:
GameService gets the item catalog, `appearance.json`, and every `world.json`,
and SimulationWorker additionally gets `combat-rules.json` and every World's
`world-actors.json` and collision chunks. GameService also copies
`development/item-packages.json`, and ActiveSimulationBots copies the collision
chunks to plan its walks.

Every World's `world-actors.json` embeds the shared actor catalog, so an actor
change recompiles every World. The Unity Actor Studio does this on save.

## Text Tables

Player-facing text is written where it is today: the `displayName`, the
`examineText`, the `menuOption`, and the quest texts stay in the content files,
in English. Each content compiler also collects the text of its domain into a
text table, a sorted list of keys and texts with a revision over all of them.
A change to one domain rewrites only that domain's table.

Nobody writes a key. The compiler derives it from the id of what the text
describes, as a path whose segments are separated by `/`:

| Text | Key |
| --- | --- |
| An item's name | `item/<definition-id>/name` |
| A category, tag, tool kind, equipment slot, weapon family, or ammunition family name | `item-category/<id>/name`, `item-tag/<id>/name`, `tool-kind/<id>/name`, `equipment-slot/<id>/name`, `weapon-family/<id>/name`, `ammunition-family/<id>/name` |
| A vendor's name and the currency's name | `vendor/<vendor-id>/name`, `currency/name` |
| An actor's name and examine text | `actor/<actor-id>/name`, `actor/<actor-id>/examine` |
| A capability's name and menu option | `actor/<actor-id>/capability/<capability-id>/name` and `.../menu` |
| A faction's name | `faction/<faction-id>/name` |
| A quest's title, offer, progress, and completion text | `quest/<quest-id>/title`, `.../offer`, `.../progress`, `.../completion` |
| An objective's text and the line of a talk-to objective | `quest/<quest-id>/step/<step-id>/objective/<objective-id>/text` and `.../line` |

A text that content leaves empty has no entry. A text is at most 2000
characters, has no whitespace around it, and holds no control character but a
line feed; the content rules of each field are stricter.

The client shows item and quest text from the tables, as
[Content Text](../docs/UNITY_CLIENT_ARCHITECTURE.md#content-text) describes, so
a table that is not current shows a stale or missing text in the game. The
compiled catalogs and the realtime protocol still hold the same texts, and
actor text still reaches the client through the protocol. English, `en`, is the
only locale; a table names its locale so that translated tables can sit beside
it in `Text/<locale>/`.

## Compile And Verify

The actor and collision compilers work on every World at once. Every compiler
finds the repository root from the current directory, so it runs from any
directory of the repository. Run the item catalog compiler before the actor
compiler, because actor content refers to item content: the actor compiler reads
`Items/core.item-catalog.json` from the generated root and checks Mob loot,
vendor capabilities, and resource profiles against it. Run the collision and
quest compilers after the actor compiler, because they read the compiled World
actors; the quest compiler also reads the compiled item catalog. From the
repository root:

```powershell
dotnet run --project tools/ItemCatalogCompiler
dotnet run --project tools/WorldActorCompiler
dotnet run --project tools/WorldCollisionCompiler
dotnet run --project tools/QuestCatalogCompiler
```

Add `-- --verify` to check that the committed generated data matches the
content without writing anything; this is what CI runs. Verify mode fails when
authoring, runtime JSON, references, bounds, transforms, fingerprints, ordering,
or a revision diverge, when a World directory lacks one of its three files, or
when generated data exists for content that no longer exists. A failure returns
exit code 1 and reports what is stale or invalid. Compile mode deletes orphaned
generated data together with its Unity `.meta` file.

Expected result: the item compiler prints the catalog revision with its number
of definitions, the actor compiler prints every World
with its revision and its numbers of definitions and runtime instances, the
collision compiler prints every World with its revision and chunk count, and the
quest compiler prints the quest catalog revision with its number of quests. The
item, actor, and quest compilers each also print their text table with its
revision and its number of texts.

`--content-root <directory>` and `--generated-root <directory>` point a compiler
at other directories. The Unity content tools use them to validate candidate
content in a temporary directory before anything is written.

## Unity Content Tools

`MMORPG > Tools > Item Catalog`, `MMORPG > Tools > Content > Actor Studio`,
`MMORPG > Tools > Content > Spawn Authoring`, and
`MMORPG > Tools > Content > Quest Studio` load these files, validate every
change with the same command-line compilers that CI runs, and write all affected
source and generated files in one transaction that is rolled back on failure.
The tools need `dotnet` on the path, because they run the compilers with
`dotnet run`. The Item Catalog window edits item definitions and vendor profiles,
as [Item Catalog Authoring](items/README.md#unity-editor-workflow) describes.
Actor Studio edits NPCs, Mobs, and resource nodes, Spawn Authoring places
them in a World, and Quest Studio edits the quests and the quest catalog.

Everything else is edited by hand in its `catalog.json`: the item catalog
settings and identities, and the actor catalog's factions, presentation
archetypes, and profiles. The one exception is a respawn profile's population
limit and respawn delay, which Spawn Authoring edits.

`MMORPG > Tools > World Collision > Bake Open Scene` bakes collision from the
open World scene into `worlds/<world-id>/collision.json` and the generated
chunks, and `Bake World Scenes` bakes every World scene, as
[Rebuild And Bake Workflow](../docs/DEVELOPMENT_WORLDS.md#rebuild-and-bake-workflow)
describes.

The saved files are the only source: SimulationWorker never reads a scene, and
the command-line compilers rebuild every compiled actor and spawn without Unity.

Actor Studio, Spawn Authoring, and Quest Studio share these behaviors:

- A searchable list with filters and a `Problems` filter. Hold Ctrl or Shift to
  select several entries; one editor then edits all of them, a field whose
  values differ shows a mixed value, a field applies only to the selected
  entries that have it, and a vector edit changes only the edited axis of each
  entry.
- References, such as profiles, presentation archetypes, actors, patrol paths,
  items, and quests, are chosen from dropdowns of the existing entries. A
  reference to a removed entry stays visible, marked as unknown, and fails
  validation.
- Live validation with the shared compiler. A field with a problem turns red,
  the list marks its entry, and the problems panel below names the field path,
  such as `interactionBounds.sizeY`; clicking a problem selects the entry and
  focuses the field. A clean workspace shows `No problems found.`.
- Undo and redo with Ctrl+Z and Ctrl+Y while no text field has keyboard focus;
  every edit, such as a whole drag in the Scene view, is one step. Unsaved
  edits survive script reloads, `Reload` discards them after a confirmation, and
  closing the window asks whether to save them. The tools remember the files
  the edits were made on: when a script reload follows a change of the files on
  disk, such as a `git pull`, the pulled files load as they are. Quest Studio
  then applies the unsaved edits on top of them, quest by quest, and names the
  quests whose edits gave way because the files changed them too; the other
  tools keep the pulled files and say that the edits were not restored.
- A save, and `Preview Changes`, stop when the tool's content files changed on
  disk since it loaded them, list the changed files, and write nothing, so a
  save never deletes or writes over a file it did not load. `Reload` loads
  them.
- `Preview Changes` compiles with the content compiler and lists every canonical
  file, with changed files in bold, or reports `No canonical changes detected.`.
  `Save + Compile` lists the runtime changes for confirmation with
  `Write files`, writes the authoring files and the compiled files in one
  transaction, and deletes the definition file of a removed entry. The actor
  tools write the actor catalog, one file per actor, the spawn file, and the
  runtime JSON of every World, and then recompile the World collision; when
  that last step fails, the status says so, and the collision stays stale until
  `tools/WorldCollisionCompiler` runs. `Verify Canonical` runs the compiler's
  verify mode. A save without changes leaves every file byte for byte the same.
- Only the list and the editor shrink with the window; the problems panel, the
  status line, and the action buttons keep their height. A long status is cut
  at a third of the window, and its tooltip shows all of it.

### Actor Studio

Actor Studio edits the shared actor catalog that every World compiles against.
Its list filters by actor kind, faction, capability, tag, and problems. With
several actors selected, kind, faction, disposition, presentation, tags, Mob
lifecycle settings, and interaction and collision bounds edit every selected
actor at once; id, display name, examine text, and capabilities are edited one
actor at a time. A capability's `Profile` dropdown lists the vendor profiles of
the compiled item catalog for a `vendor` capability and the resource profiles
for a `gathering` capability.

1. Open `MMORPG > Tools > Content > Actor Studio`. Create an actor from a
   template with `New NPC`, `New Mob`, or `New Resource Node`, or select actors
   and click `Duplicate`, which copies each with a new unique stable id, such as
   `npc.city_services.copy`.
2. Fill in the `Actor Definition` section: kind, faction, disposition,
   `Stable Actor Id`, `Display Name`, and `Examine Text`. Choose the
   `Presentation Archetype` and add tags with `Add Tag`.
3. For a Mob, set its `Level`, which its nameplate shows, and choose the
   profiles under `Combat And Lifecycle`: damage policy, activity, respawn,
   combat, brain, locomotion, and loot profile, corpse persistence (`Live` or
   `Durable`), corpse lifetime, and `Kill Credit`: `Tagger`, whose first
   attacker alone gets the credit and the loot, or `Contributors` for a world
   boss, such as the Feral Alpha, which credits everyone who damaged it nearby
   and gives each a corpse of their own. NPCs and resource nodes show a note
   instead, because they are always invulnerable.
4. Set the `Authoritative Interaction Bounds`, `Local Center` and `Size`, which
   a front-view preview draws. For a resource node also set `Collision Center`
   and `Collision Size`, the box that blocks walking; for NPCs and Mobs both stay
   zero and cannot be edited.
5. Under `Composable Capabilities`, use `Add Capability`, then set each
   capability's `Stable Capability Id`, `Kind`, `Display Name`, `Menu Option`,
   and, for a vendor or gathering capability, its `Profile`. `Move Up`,
   `Move Down`, and `Remove` change the list, whose order is the menu order.
6. Click `Preview Changes`, then `Save + Compile`, then `Verify Canonical`.

Expected result: the problems panel shows `No problems found.`, the preview
marks the changed files, the save writes them, and verification reports the
same revisions as the save.

Renaming and deleting protect the spawns. A changed `Stable Actor Id` of an
actor that only the edited World places renames the actor and its spawns in one
undo step; an actor that another World places keeps its id, and the status
names that World. `Delete` removes only actors that no spawn places and
otherwise lists the spawns that place them.

Each presentation archetype maps to a prefab in
`Assets/Content/WorldActors/WorldActorPresentationRegistry.asset`. Assign a
prefab in the archetype's `Replaceable Prefab` field; it must be stored below
`Assets/Prefabs/WorldActors`, an Addressables folder entry, and the field
rejects any other prefab. The registry stores it as an Addressables reference
and changes presentation only. An archetype without a prefab shows a tinted
fallback, with one warning, until one is assigned. `Create Presentation
Registry` appears only when the registry asset is missing. How the client shows
actors is described in
[Unity Client Architecture](../docs/UNITY_CLIENT_ARCHITECTURE.md).

### Spawn Authoring

Spawn Authoring edits the spawn points, groups, areas, and patrol paths of
`development-world-1`, the World the actor tools open, in one list that filters
by type, actor, and problems. With several entries selected, actor, patrol path,
position, yaw, group layout, area settings, and Mob lifecycle settings edit
every selected entry that has them. A new entry gets a generated stable id, such
as `development_world_1.new_point`. Edit the `actor-spawns.json` of another
World by hand, then run the actor and collision compilers as
[Compile And Verify](#compile-and-verify) shows. Keep every position and patrol
point on clear, flat ground; the
[Layout Tests](../docs/DEVELOPMENT_WORLDS.md#layout-tests) check it.

1. Open `Assets/Scenes/DevelopmentWorld1.unity`, a Scene view, and
   `MMORPG > Tools > Content > Spawn Authoring`. With `Scene Preview` on, the
   Scene view draws every entry and the handles of the selected ones.
2. Create an entry with `New Point`, `New Group`, `New Area`, or
   `New Patrol Path`, which places it at the Scene view pivot, or select one in
   the list or by clicking its marker in the Scene view; Ctrl-click adds to the
   selection. `Duplicate` copies the selection one unit along X, and `Frame`
   frames it in the Scene view.
3. Set the `Stable Id`, the `Actor`, and for a Mob spawn the `Patrol Path`. Move
   the selection with its handle in the Scene view or set `Position` and
   `Yaw Degrees`, and click `Ground Snap` to put it onto the scene collision
   below it. A group has `Rows`, `Columns`, and `Spacing`; an area has
   `Minimum XZ`, `Maximum XZ`, `Count`, and `Seed`; a patrol path has patrol
   points with `Wait Seconds`, edited with `Add Patrol Point`, `Move Up`,
   `Move Down`, and `Remove`.
4. Check `Mob Lifecycle` when the entry places a Mob. Its activity and respawn
   profile and the respawn profile's `Population Limit` and
   `Respawn Delay Seconds` belong to the Mob definition and its respawn profile,
   so a change applies wherever that Mob spawns. `Presentation Preview` shows the
   prefab mapped to the placed actor.
5. Click `Preview Changes`, then `Save + Compile`, then `Verify Canonical`.

Expected result: the preview shows the exact neutral JSON of the spawn file,
the save writes it with the runtime JSON of every World and recompiles the
collision, and verification reports the same revisions. `Ground Snap` reports
when no collider lies below an entry.

Renaming a patrol path updates the spawns that follow it in the same undo step.
`Delete` keeps a patrol path that a remaining spawn still follows and says so.
How to check the result in the running game is in
[Manual Testing](../docs/MANUAL_TESTING.md).

### Quest Studio

Quest Studio edits the files in `quests` and writes the compiled quest catalog,
`src/Shared/WorldData/Generated/Quests/core.quest-catalog.json`. Its list
filters by giver, turn-in NPC, objective kind, tag, and problems. With several
quests selected, giver, turn-in NPC, minimum level, repeatable, tags, and reward
Gold edit every selected quest at once; id, title, texts, requirements, items,
and steps are edited one quest at a time. With no quest selected, the editor
shows the quest catalog's `Maximum Active Quests` and its retired ids.

The dropdowns offer what the compiler accepts: NPCs with the `quests`
capability for the giver, the turn-in NPC, a delivery, and a conversation;
placed Mobs and the tags of placed Mobs for a kill objective; placed resource
nodes for a gathering objective; protected quest items that no other quest
uses for start items, and every item but another quest's quest items for a
collect or delivery objective; ordinary items without policies for rewards;
and for a requirement every quest that does not already require this one,
directly or through others, which would form a cycle. They come from the
compiled items and actors, so after a save in the Item Catalog or Actor Studio,
click `Reload References`, which keeps the quest edits.

`Reload` with unsaved edits asks whether to keep them on top of the files on
disk, quest by quest, or to discard them. A quest file that is not in the form
Quest Studio writes, for example because of a field that the content model does
not know, shows as a warning, because the quest compiler rejects it; a save
writes it in that form. `Verify Canonical` checks the quest files this way too,
like the Unity tests, besides the compiler's verify mode.

1. Open `MMORPG > Tools > Content > Quest Studio`. Create a quest with
   `New Quest`, which offers `Kill Quest`, `Collect Quest`, `Delivery`,
   `Talk-To Quest`, `Gathering Quest`, and `Several Steps`, or select quests and
   click `Duplicate`, which copies each with a new id, such as
   `quest.wolves_at_the_gate_copy`. A template names the first quest NPCs, Mob,
   resource node, and material, and compiles as it is; `Delivery` hands three of
   the material to the second quest NPC. A quest item belongs to one quest, so a
   quest that needs its own start item gets a new quest item from the Item
   Catalog window.
2. Under `Quest`, set the `Stable Quest Id`, `Title`, `Giver`, `Turn-In NPC`,
   `Minimum Level`, and `Repeatable`. Under `Dialog`, write the `Offer Text`,
   `Progress Text`, and `Completion Text`; a line break typed into a text becomes
   a space, because every quest text is one line.
3. Under `Required Quests`, use `Add Requirement` and choose the quests that must
   be completed first, and add tags under `Content Tags`.
4. Under `Start Items`, add the quest items the giver hands out at acceptance.
5. Under `Steps`, each step has its `Step Id` and objectives. Each objective has
   an `Objective Id`, a `Kind`, its tracker `Text`, a `Count`, and the fields
   of its kind: `Targets` with `Listed Mobs` or `Mobs With A Tag` for a kill
   objective, the `Item` and, for a quest item, `Quest Drops` with `Mob`,
   `Chance (basis points)`, and `Quantity` for a collect objective, the `Item`
   and `Deliver To` for a delivery, `Talk To` and the NPC's `Line` for a
   conversation, and the resource nodes for a gathering objective. Changing the
   kind clears the fields the new kind does not use and fills the ones it needs.
   `Add Objective`, `Add Step`, `Move Up`, `Move Down`, `Move Step Up`,
   `Move Step Down`, `Remove`, and `Remove Step` change the order.
6. Under `Rewards`, set the `Reward Gold`, and add `Reward Items` and the
   `Reward Choice`, none or two to six items of which the player picks one.
7. `Quest Chain` shows the selected quest's chain in rows, each row needing
   quests from the rows above it, and `NPC View` lists every quest the chosen
   NPC gives, receives, or appears in. Clicking a quest in either selects it.
8. Click `Preview Changes`, then `Save + Compile`, then `Verify Canonical`.
   The save lists the changes for confirmation, such as
   `Added quest: quest.new_kill`, and writes the quest files and the compiled
   quest catalog.

How to check Quest Studio by hand is in
[Manual Testing](../docs/MANUAL_TESTING.md#quest-studio).

A saved quest's id is permanent, as
[Changing Released Quests](../docs/QUEST_SYSTEM_DESIGN.md#changing-released-quests)
describes. `Delete` removes only quests that no remaining quest requires, says
which ones it kept, and moves the id of a saved quest to the retired ids;
changing the `Stable Quest Id` of a saved quest retires the old id after a
confirmation and updates the quests that require it. A quest that was never
saved is deleted or renamed without a trace. A save also retires every saved
quest that left the catalog and keeps every retired id, even when undo across
a save took the retirement away, and the problems panel names a retired id that
undo brought back. Saved means as Quest Studio last loaded or saved the files:
a new quest or a rename never takes the id of a saved quest, also one that undo
took out of the catalog. To try Quest Studio without
keeping the result, discard the changed files with
`git restore content/quests src/Shared/WorldData/Generated/Quests` and delete
any new definition file before GameService starts, because GameService never
forgets a quest id that it has mirrored.

# Quest System Design

This document is the source of truth for quests: what a quest is made of, the
objectives it can ask for, how chains and availability work, the quest log,
turn-in and rewards, how quests are authored, and how the services share the
authority over them. Who gets credit for a kill and who may loot a Mob are
rules of Mobs, in [Mobs And Encounters](GAME_DESIGN.md#mobs-and-encounters) and
[Mob Corpse Lifecycle](INVENTORY_AND_DEATH_LOOT_DESIGN.md#mob-corpse-lifecycle).
Quest givers are NPCs of the world actor model in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md), and quest items
follow the item rules of
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).

## Status

Implemented:

- The `quests` capability kind of NPCs and the `quest_item` item category.
- Protected quest items with a quest-grant lineage, as
  [Protected On Death](INVENTORY_AND_DEATH_LOOT_DESIGN.md#protected-on-death)
  describes.
- Quest content in `content/quests/`, its compiler `tools/QuestCatalogCompiler`
  with the [validation](#validation) and the [limits](#limits) below, and the
  shared availability and step rules in WorldData. The checked-in quests are the
  development chain that [Game Content](../content/README.md#quests) lists.
- A quest NPC is an NPC with the `quests` capability.
- The GameService Quests module: the quest catalog loaded at startup with the
  checks of [Changing Released Quests](#changing-released-quests), the stored
  [quest state](#quest-state), and the owning account's read of it, as
  [Service Features](SERVICE_FEATURES.md#quests) describes.
- Every quest command as an item transaction on the worker quest route:
  accepting, abandoning, granting start items again, talking, delivering,
  counting credited kills, and the turn-in with its rewards, and the count of
  gathering yields in the gathering transaction.
- SimulationWorker loads the quest catalog and reports its revision, and
  placement requires it to match the mirrored revision. The worker reads every
  player's quest log when the player joins and keeps it from the committed
  results. It runs accepting, granting start items again, talking, delivering,
  and the turn-in at a quest NPC through the open interaction, and abandoning
  from anywhere as a quest intent, and sends every committed quest change to the
  client, as [Service Features](SERVICE_FEATURES.md#quests) describes.
- The worker counts the credited kills and the gathering yields that the
  current steps ask for, as [Service Features](SERVICE_FEATURES.md#quests)
  describes.
- The worker rolls [quest drops](#quest-drops) into the corpses of dying Mobs,
  and GameService grants them from live Mob loot and keeps them in durable Mob
  corpses with the quest's lineage, up to what the quest still needs, as
  [Service Features](SERVICE_FEATURES.md#quests) describes. The worker tells
  the owner about every quest drop in a new corpse.
- The client keeps the quest log of the joined character and opens the quest
  dialog at a quest NPC, where the player accepts, talks, hands over, asks for
  lost start items, and turns in with a reward choice. The quest log on `L`
  lists the active and completed quests and abandons a quest after a
  confirmation, the tracker shows the active quests' objectives, markers over
  the quest NPCs tell what waits there, and the game messages report every
  committed quest change, as
  [Quest Client](UNITY_CLIENT_ARCHITECTURE.md#quest-client) describes.
- The Admin menu's quest tools, as [Development Tools](#development-tools)
  describes.
- [Quest Studio](#quest-studio), whose steps are in
  [Game Content](../content/README.md#quest-studio).

Planned: everything else in this document.

## Goals

- Quests and quest chains are easy to create, find, change, and check: one file
  per quest, a Unity Editor tool with templates and live validation, and the
  same compiler in the Editor, in the services, and in CI.
- A quest can ask for many kinds of things and combine them freely. A new kind
  of objective adds one rule and its presentation, not a new structure.
- The servers own every quest decision. The client shows quest state and sends
  intents.
- Progress is never lost or counted twice, and nothing is granted without its
  cost: a turn-in takes the items and pays the rewards in one transaction.
- Quest state is global per character, across every Shard, like items and Gold.

## Decisions

| Question | Choice |
| --- | --- |
| How a quest is structured | Ordered steps. Each step holds one or more objectives, done in any order, and the quest is turned in at its turn-in NPC after the last step. |
| What an objective can ask for | Kill Mobs, collect items, deliver items to an NPC, talk to an NPC, and gather from resource nodes. More kinds follow; see [Planned Objective Kinds](#planned-objective-kinds). |
| Where quests are made | One content file per quest, edited in Quest Studio or by hand and checked by one compiler. |
| Who gets credit for a kill | The player who tagged the Mob, or, for a Mob with contributor credit, every player it credits, as [Mobs And Encounters](GAME_DESIGN.md#mobs-and-encounters) decides. |
| How quest drops reach a player | As a personal entry in the Mob's corpse that only that player sees and loots. |
| What a quest rewards | Gold, items, and one item of the player's choice. Experience joins once it exists. |
| Who owns quest state | GameService, in the same transactions as items and Gold. |
| How players see quests | Markers over NPCs, a quest dialog at the NPC, a quest log, a tracker, and notices. |

## Quest Model

Every quest definition has:

- A stable quest id, such as `quest.wolves_at_the_gate`, permanent once
  released; see [Changing Released Quests](#changing-released-quests).
- A title.
- A giver: the actor definition id of the NPC that offers the quest.
- A turn-in NPC: the actor definition id of the NPC that completes it, which may
  be the giver. A quest given by one NPC and turned in at another is a delivery
  between them.
- Requirements: the quests that must be completed first and a minimum character
  level.
- One or more steps with their objectives.
- Start items, granted when the quest is accepted.
- Rewards: Gold, items, and a list of items of which the player chooses one.
- Texts: the offer, the progress text that the turn-in NPC says while the quest
  is not ready, the completion text, and one line per objective, such as
  `Feral Wolves slain`.
- Whether the quest is repeatable.
- Content tags for authoring and filters, such as the area a quest belongs to.

Every NPC that a quest names is a definition of its own, such as
`npc.outpost_sergeant`: quests refer to actor definitions, so two NPCs that
share a definition are the same quest NPC. Quests are global content shared by
every World. A quest NPC offers its quests wherever a World places it.

### Steps And Objectives

- A quest has one or more steps in order, and each step has one or more
  objectives. Only the current step counts: its objectives can be done in any
  order, and when all of them are complete, the next step starts. After the last
  step, the quest is ready to turn in.
- Work for a later step counts only once that step is current. A wolf killed
  before the step that asks for wolves counts for nothing.
- Every objective has an id that is stable within its quest, because stored
  progress refers to it, a kind with the fields of that kind, a required count,
  and its line of text.

A short quest has one step, such as `Kill 5 Feral Wolves`. A longer one walks
the player through a small story: `Talk to the guard`, then `Kill 5 Feral
Wolves`, then return to the giver.

### Requirements And Availability

A quest is available to a character when all of these hold:

1. The character's level is at least the quest's minimum level.
2. Every required quest is completed.
3. The quest is not active, and it was never completed unless it is repeatable.
4. The character has fewer active quests than the active quest limit.

One shared rule decides availability for GameService, SimulationWorker, and the
client, so the client offers exactly what the servers accept. Profession,
reputation, and item requirements are planned.

### Start Items

- Start items are quest items: the `quest_item` category with the
  `protected_on_death` default policy, never destroyable by the player. They
  carry the quest's grant lineage, which the quest's abandonment removes, as
  [Protected On Death](INVENTORY_AND_DEATH_LOOT_DESIGN.md#protected-on-death)
  describes.
- Accepting a quest whose start items do not fit carried storage is refused and
  changes nothing.
- When a start item of an active quest is gone, for example because its Recovery
  Storage delivery expired, the giver's quest dialog offers to grant it again.
  A grant never duplicates an item the character still owns.

### Rewards

- Gold is credited through the character wallet with a `quest_reward` ledger
  row.
- Reward items are ordinary items without default policies and never quest
  items. They are placed by
  [carried acquisition](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carried-acquisition),
  each quantity at most one stack.
- A quest can offer two or more items of which the player chooses one at the
  turn-in. The turn-in names the choice, and the servers check it against the
  quest.
- When the reward items do not fit carried storage, the turn-in is refused with
  a clear message and nothing changes, so no reward is ever lost.
- Experience rewards join once experience exists, with no change to the quest
  model beyond a reward field.

### Repeatable Quests

A repeatable quest can be accepted again once it has been turned in, and the
quest state counts how often the character completed it. A quest that is not
repeatable is completed once per character. Daily and weekly quests, which
reset on a schedule, are planned.

### Limits

These limits keep quests readable in the quest dialog, the log, and the
tracker, and keep every quest message within the realtime packet limit:

| Field | Limit |
| --- | --- |
| Title | 1 to 64 characters |
| Offer and completion text | 1 to 1,000 characters each |
| Progress text and a talk-to line | 1 to 500 characters each |
| Objective line | 1 to 64 characters |
| Steps | 1 to 8 |
| Objectives per step | 1 to 8 |
| Required count of an objective | 1 to 1,000 |
| Actors that an objective names, and its quest drop sources | Up to 8 each |
| Start items and reward items | Up to 4 each |
| Reward choices | None, or 2 to 6 |
| Gold reward | 0 to 1,000,000 |
| Required quests | Up to 8 |
| Content tags | Up to 16 |
| Active quests per character | 25, a setting of the quest catalog from 1 to 100 |
| Quests in the catalog | Up to 10,000, which bounds every list of a character's quests |

Texts are one paragraph of plain text without control characters, trimmed, and
written in English like the other content.

## Objective Kinds

| Kind | Asks for | Counted by |
| --- | --- | --- |
| `kill` | Credited kills of Mobs of the listed actor definitions or with one actor tag | SimulationWorker, at the Mob's death |
| `collect` | A quantity of an item in carried storage, taken at the turn-in | The shared rule over committed items, checked by GameService at the turn-in |
| `deliver` | A quantity of an item handed to a named NPC | GameService, when the player hands it over at that NPC |
| `talk_to` | A conversation with a named NPC | GameService, when the player talks to that NPC |
| `gather` | Items gathered from resource nodes of the listed actor definitions | SimulationWorker, for every committed yield |

### Kill

- A kill objective lists Mob actor definition ids, such as `mob.feral_wolf`, or
  names one actor tag, such as `wolf`, which counts every Mob with that tag.
- Every kill that credits the character counts once, wherever the Mob dies.
  Credit follows the tagging rules of
  [Mobs And Encounters](GAME_DESIGN.md#mobs-and-encounters): the tagger, or
  every credited player of a Mob with contributor credit.
- Players are never targets, and a player kill counts for no quest.

### Collect

- A collect objective names an item definition and a quantity. It counts the
  quantity of that item in carried storage: the permanent inventory and the
  equipped Bag, never the Bank, Recovery Storage, or equipment. Ordinary items
  count when they carry no policy, and quest items count when they carry this
  quest's lineage.
- The count follows the items: selling or banking them lowers it again.
- The turn-in takes the required quantity in the same transaction that pays the
  rewards. A collect objective therefore belongs to the last step.
- An objective for a quest item names the item's drop sources; see
  [Quest Drops](#quest-drops). Ordinary items come from anywhere: Mob loot,
  gathering, vendors, or other players.

### Quest Drops

- A collect objective for a quest item lists its drop sources: Mob actor
  definition ids, each with a chance in basis points and a quantity.
- When a source Mob dies, every character it credits whose current step holds
  the objective rolls the chance once. A success adds a personal entry for that
  character to the Mob's corpse, at most the quantity the character still
  needs, and none when it needs no more. Only that character sees and loots the
  entry, as [Mob Corpse Lifecycle](INVENTORY_AND_DEATH_LOOT_DESIGN.md#mob-corpse-lifecycle)
  describes. The entries follow the Mob's own loot, and only the ones the
  character still needs fill the room that the loot leaves in the corpse's 24
  entries, so a drop that is not needed never takes the place of one that is.
  A quest drop leaves a corpse even when the Mob's own loot roll is empty.
- The looted item is a protected quest item with the quest's grant lineage, so
  abandoning the quest removes it. An entry left in the corpse disappears with
  the corpse.
- Mob loot profiles still never contain quest items, and gathering never yields
  them. Quest drops from resource nodes are planned.

### Deliver

- A deliver objective names an item, a quantity, and the NPC that receives it.
  The player hands the items over in that NPC's quest dialog, which takes them
  and completes the objective in one transaction.
- A deliver objective hands items to an NPC on the way. Items for the turn-in
  NPC use a collect objective in the last step instead, because the turn-in
  takes them.

### Talk To

- A talk-to objective names an NPC and the line that NPC says. The player chooses
  the quest in that NPC's quest dialog, reads the line, and continues, which
  completes the objective.
- The last step never talks to the turn-in NPC, because the turn-in already
  does.

### Gather

- A gather objective lists resource node actor definition ids, such as
  `resource.iron_vein`, and counts the items that their committed yields grant
  to the character, so `Mine 5 Iron Ore` needs five Iron Ore from Iron Veins.
- The items stay with the player. To ask for the items themselves, a quest
  uses a collect objective.

### Planned Objective Kinds

Each of these adds a kind to the objective set, its compiler rules, the
authority that counts it, and its presentation:

- Reach an area of a World, which needs quest areas authored in the World.
- Use an item at a place or on a target, once consume and use intents exist.
- Escort an NPC.
- Craft an item, equip an item, or reach a character or profession level.
- Timed objectives, with quests that can fail.

## Quest Chains

- A chain is a set of quests linked by their required quests. A quest can
  require several quests and several quests can require the same one, so chains
  branch and join.
- Turning in a quest at an NPC that now offers an available follow-up shows the
  follow-up in the same dialog at once.
- The compiler rejects a cycle of required quests, so every chain has a start.
- Quest Studio shows a quest's chain as a graph, with the NPC that gives and
  receives each quest.
- Mutually exclusive branches, where accepting one quest closes another, are
  planned.

## Quest State

For every character, GameService stores:

- Each active quest: its id, its current step, the progress of every counted
  objective of that step, and when it was accepted. Collect objectives count the
  carried items instead, so they store no progress.
- Each completed quest: its id, how many times it was completed, and when it was
  last completed.
- A quest log revision that every change raises, so a client can tell whether it
  missed a change.

Availability and readiness are derived, never stored: a quest is available by
[Requirements And Availability](#requirements-and-availability), and an active
quest is ready to turn in when its last step is complete.

- Progress survives death, disconnect, Shard switches, and restarts of either
  service. Death costs no quest progress, and quest items are protected on death.
- Abandoning a quest works anywhere in the world from the quest log. It drops
  the quest's progress and removes every item with its lineage, also from
  Recovery Storage, and the quest can be accepted again. Ordinary items that the
  player collected stay.
- A character takes its quests to every Shard, because quest state is global.

## Quest NPCs And The Quest Dialog

- An NPC takes part in quests through one `quests` capability. Its menu option, such as
  `Quests`, opens the quest dialog. A quest NPC lists its `quests` capability
  first, so a left click opens the dialog.
- The dialog lists, for the player, what this NPC can do now: quests to turn in,
  talk-to and deliver objectives, and the quests it offers that are available.
  Active quests it will receive later show their progress text. Choosing an
  entry shows its text, its objectives, and its rewards with `Accept`,
  `Complete` with the reward choice, `Continue` for a talk-to objective, or
  `Hand over` for a delivery.
- Every quest action at an NPC runs through the open interaction, so range,
  line of sight, and the one-active-interaction rule of
  [Authoritative Interaction Validation](NPC_AND_MOB_SYSTEM_DESIGN.md#authoritative-interaction-validation)
  apply.
- A marker over an NPC tells the player what waits there: a yellow `!` for an
  available quest or lost start items to ask for again, a grey `!` for a quest
  that only needs a higher level, a yellow `?` for something to turn in, talk
  about, or hand over once the player carries all of it, and a grey `?` for an
  active quest that it completes once the quest is ready or a delivery the
  player does not carry enough for yet. The most urgent one shows. The client
  derives the marker from the quest catalog, the actor definition of the NPC, and
  its own quest state, so the actor spawn carries nothing new.

## Authoring

### Content And Compilation

```text
content/
  quests/
    catalog.json                    quest settings, such as the active quest limit,
                                    and the retired quest ids
    definitions/<quest-id>.json     one quest per file
src/Shared/WorldData/Generated/
  Quests/
    core.quest-catalog.json         the compiled quest catalog
```

One compiler, `tools/QuestCatalogCompiler`, serves the command line, Quest
Studio, the services at startup, the tests, and CI. It runs after the item and
actor compilers, because quests refer to items, actors, and the Worlds that
place them. The result is deterministic, with a catalog revision and a
structural fingerprint per quest, and equal input produces byte-for-byte equal
output, as for the other content.

### Validation

The compiler rejects:

- An invalid or duplicate id, a file not named after its id, and a retired id in
  use.
- A giver, turn-in NPC, talk-to NPC, or deliver NPC that is not an NPC with a
  `quests` capability.
- A kill objective that names both Mobs and a tag, or neither, a kill target
  that is not a Mob, and a tag that no Mob carries.
- A gather target that is not a resource node.
- An unknown item, start items and quest drops that are not protected quest
  items, reward items that are quest items or carry default policies, and a
  quantity above one stack.
- A collect objective outside the last step, a quest drop source that is not a
  Mob, and a chance outside 1 to 10,000 basis points.
- Two collect objectives for the same item, which would count the same items.
- A quest item that the quest's deliveries and its collect objective take more
  often than its start items grant it, unless the collect objective drops the
  item, because the player could never get it.
- A talk-to objective with a count other than 1, and a talk-to or deliver
  objective for the turn-in NPC in the last step, whose turn-in already talks
  and takes the items.
- A field that the objective's kind does not use but that is not empty.
- An unknown required quest, a cycle of required quests, and a minimum level
  outside 1 to the character level cap.
- Every NPC, Mob, and resource node a quest needs that no World places, because
  such a quest could never be finished.
- Empty or overlong texts, texts with control characters, and anything beyond
  the [limits](#limits).

### Quest Studio

`MMORPG > Tools > Content > Quest Studio` edits quests on the shared content
tool foundation of [Unity Content Tools](../content/README.md#unity-content-tools):

- A searchable list with filters by giver, turn-in NPC, objective kind, tag, and
  problems, with several entries edited at once where their fields agree.
- New quests from templates: a kill quest, a collect quest, a delivery, a
  talk-to quest, a gathering quest, and a quest with several steps, and
  `Duplicate` for an existing one.
- Dropdowns for every reference: NPCs with a `quests` capability, Mobs and actor
  tags, resource nodes, items, with only quest items where a quest item is
  needed, and quests for the requirements.
- Steps and objectives that are added, removed, and moved with buttons.
- A chain view of the selected quest's chain in rows, each row needing quests
  from the rows above it, and an NPC view of every quest an NPC gives, receives,
  or appears in. Clicking an entry selects it.
- `Delete` and a changed id of a saved quest move its id to the retired ids, as
  [Changing Released Quests](#changing-released-quests) requires; a quest that
  was never saved leaves no trace. A save checks the edits against the saved
  files, so undo across a save can neither remove a saved quest without retiring
  its id nor bring a retired id back.
- Live validation, undo, `Preview Changes`, `Save + Compile`, and
  `Verify Canonical`, as in the other content tools.

New quest items are made in the Item Catalog window like every other item.

### Changing Released Quests

- A released quest id is permanent. Removing a quest moves its id to the quest
  catalog's retired ids, where it can never be reused.
- Objective ids are stable within their quest, because stored progress refers to
  them.
- Texts, counts, rewards, requirements, and tags may change freely. A lowered
  count that stored progress already exceeds completes the objective.
- A structural change, to the steps or to an objective's id, kind, or targets,
  of a quest that characters have active needs a data migration. GameService
  refuses to start with such a change instead of guessing, as it does for an
  item definition with live items.

## Authority

### Content And Shared Rules

WorldData owns the quest content model, the compiler, its runtime validator,
and the shared quest rules: availability, step progression, objective
completion, the carried count of collect objectives, and the check that rewards
fit. GameService, SimulationWorker, and Unity run the same rules.

### GameService

- A Quests module owns the quest tables and the mirrored quest catalog revision.
  It sits between Placement and Items in the module order, and its contract runs
  inside the caller's transaction, like the character wallet.
- Every quest change is a command of the item transaction kernel, because most
  of them move items or Gold: accepting grants start items, abandoning removes
  lineage items, a delivery and a turn-in take items, and a turn-in grants
  rewards and credits Gold. Each runs once per operation id, and a retry replays
  its stored result.
- SimulationWorker reports its quest catalog revision, and placement requires it
  to match the mirrored revision, as for the item catalog.
- The owning account reads its character's quest state over HTTP, which the
  client does once when it joins a Shard.

### SimulationWorker

- It keeps the active quests of every character in its sessions, loaded when the
  character joins and updated from committed results.
- It commits every credited kill that a current step asks for to GameService as
  a kill credit, which counts for the kill objectives of current steps, with one
  operation id per credited character that it keeps for retries, so a retry
  never counts twice. A credit keeps the place in the character's lane that it
  took at the kill, and whether a step asks for it is decided when its turn
  comes. Pending credits commit before a leave and retry until they commit,
  like a death. A gathering yield that a current step asks for names the
  node's actor definition, and GameService counts it for gather objectives in
  the same transaction that places the yield. While the worker's quest log may
  miss a change, it sends every credited kill and names every node, and
  GameService decides what counts.
- It rolls quest drops at a Mob's death for the owner of each corpse, by the
  quest log it keeps, and GameService decides how much of them the owner still
  needs: for a live corpse in a read before the worker lays it out, for a
  durable corpse in its creation.
- It handles the `quests` capability: accepting, talking, delivering, and
  turning in, each validated through the open interaction. Abandoning from the
  quest log is an intent outside any interaction.
- It sends every committed quest change to the owning client.

### Unity Client

The client loads its quest state once per join and patches it from the realtime
quest changes. It derives availability, markers, and collect counts with the
shared rules, its inventory, and the bundled quest catalog, which holds every
quest text, so realtime messages carry only ids and counts. It never decides a
quest outcome.

## Presentation

The quest windows are built on the permanent UI kit of the inventory windows,
as [UI And UX Principles](GAME_DESIGN.md#ui-and-ux-principles) describes:

- The quest dialog opens at a quest NPC, as
  [Quest NPCs And The Quest Dialog](#quest-npcs-and-the-quest-dialog) describes.
- The quest log opens and closes with `L`. It lists the active quests with the
  current step's objectives and their progress, shows the selected quest's text,
  offers `Abandon` with a confirmation, and lists the completed quests.
- The tracker on the right side of the screen shows the objectives of the active
  quests, such as `Feral Wolves slain 3/5`, and `Ready to turn in` for a quest
  that is done.
- The game message panel reports every change: an accepted quest, progress such
  as `Feral Wolf slain 3/5`, a completed step, a quest ready to turn in, a
  completed or abandoned quest, and a quest drop in a corpse.

## Development Tools

- The Admin menu (F3) has quest tools that only the Development environment
  allows: it lists every quest with the character's state, accepts a quest
  around its giver, level, and required quests, completes an objective or the
  current step, makes a quest ready to turn in, and resets a quest, a quest
  with the quests that require it, or the whole quest state. Completing a
  collect objective grants the items it lacks, and a reset forgets the quest
  with its completions and takes every item of its lineage. The tools keep the
  quest log valid, so the normal quest actions go on from wherever they leave a
  quest.
- The development Worlds hold a test chain whose quests together use every
  objective kind, a quest drop, a reward choice, a delivery between two NPCs,
  several steps, and a follow-up that requires two quests.
- Synthetic development bots never take part in quests.

## Security And Abuse Controls

- Clients send only intents. The servers check the NPC, range, line of sight,
  the quest state, the current step, the counts, and the inventory for every
  action.
- Unknown quest ids, objective ids, and reward choices are rejected, and every
  identifier and list is bounded.
- Progress comes only from the worker's own authoritative events and committed
  results, never from a client.
- Quest intents share the per-peer interaction rate limits, and a character has
  one durable authority operation in flight at a time.
- Logs and metrics may name low-cardinality kinds and outcomes but never use
  account, character, quest, or actor ids as metric labels.

## Deferred Work

- Experience rewards, together with the experience system, which also gives Mob
  kills experience by the same credit rules.
- Reputation and profession requirements and rewards.
- Groups: tags owned by a group and quests shared within it.
- Daily and weekly quests.
- The [planned objective kinds](#planned-objective-kinds) and quest drops from
  resource nodes.
- Mutually exclusive branches, and items that start a quest.
- Quest markers on a world map, once a map exists.
- Localized quest texts.
- Final art for the quest windows and markers.

## Open Questions

- Gold and item reward values per quest level, once more than the development
  quests exist.

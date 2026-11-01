# Inventory And Death Loot Design

This document is the product and domain source of truth for items: item
definitions, instances and stacks, the permanent inventory, equipment, Bags,
the Bank, Recovery Storage, carry weight, item policies,
currency, vendor trade, death, and corpses. How GameService implements these
rules, with its tables, transaction kernel, and lock order, is in
[GameService Modules](GAME_SERVICE_MODULES.md). The HTTP surface and the stable
error codes are in [Service Features](SERVICE_FEATURES.md), and the client is in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md). NPCs, Mobs, resource
nodes, and generic world interaction are in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md), and combat is in
[Game Design](GAME_DESIGN.md).

## Status

Every rule in this document is implemented unless it is marked as planned or
listed in [Planned](#planned), [Proposed](#proposed), or
[Open Questions](#open-questions). The main planned work is consume and use
intents for potions and food, combat data for armor, resistances, and healing,
quest progression, and the final inventory and corpse loot interface, which is
temporary uGUI today.

## Canonical Item Terminology

| Term | Meaning |
| --- | --- |
| Item definition | Shared content that defines identity, category, tags, weight, stack limit, equipment compatibility, and other immutable rules |
| Item instance | A durable, uniquely identified quantity of one item definition |
| Carried storage | The permanent inventory and the equipped Bag's contents: what a character carries and what counts toward its carry weight |
| General slot | A slot that accepts any item not explicitly forbidden by location or policy rules |
| Specialized slot | A Bag slot that accepts definitions with one or more configured tags |
| Bag | A physical item that can be equipped in the character's Bag equipment slot and can provide item slots and carry-capacity bonuses |
| Recovery Storage | A per-character, system-write-only delivery queue reached through any NPC that offers it |
| Recovery delivery | Items that one system event delivered to Recovery Storage together and that are claimed together |
| Corpse | A bidirectional loot container backed by durable custody for players and selected persistent Mobs |
| Snapshot | Non-interactive corpse presentation metadata that never grants ownership or references a lootable item instance |
| Vendor profile | Item catalog content that names what an NPC vendor sells, its prices, and the categories it buys |

`Bag` is the canonical equipment and item term. A Bag may be presented as a
backpack, satchel, case, or another visual form without changing the domain.
There is no protected carried storage: everything a character carries can be
lost on death unless an item policy protects it.

## Authority And Data Ownership

- GameService owns persistent item definitions mirrored from shared content,
  item instances, slot assignments, equipment assignments, policies, the bank,
  Recovery Storage, durable corpse custody, all durable
  item transactions, and character currency with its ledger.
- PostgreSQL is the source of truth for persistent item ownership and custody.
- SimulationWorker owns live interaction validation, proximity, active corpse
  presentation, the one-active-interaction rule, combat state, and the
  authoritative effect of encumbrance for its assigned Shard.
- SimulationWorker requests durable mutations through an authenticated
  GameService service boundary. It never writes item tables directly.
- Unity sends intents and displays authoritative results. It never supplies
  trusted weight, capacity, policy, quantity, slot compatibility, or ownership.
  It may show a move before the server answers, but only a committed result
  changes custody, and a rejected move is undone.
- Redis is not an item, inventory, corpse-custody, or transaction authority.

Persistent character items are global and are not owned by a Fleet, Node,
SimulationWorker, SimulationAssignment, or Shard. A durable player corpse
records the Shard where the death happened only so the assigned
SimulationWorker can restore its live representation. No Zone or Layer identity
is stored, because those systems do not exist.

## Item Definitions, Categories, And Tags

Every item definition has one primary category. The categories are catalog
content: `weapon`, `ammunition`, `armor`, `clothing`, `jewelry`, `tool`, `bag`,
`material`, `potion`, `food`, and `quest_item`. Code gives meaning only to the
ids its rules need, such as the `quest_item` category and the `bag` equipment
slot. Every other category, tag, equipment slot, and tool kind is content, as
[Item Catalog Authoring](../content/items/README.md#authoring-contract)
describes.

Definitions may also carry zero or more tags. Tags provide many-to-many
compatibility without forcing an item into only one gameplay use. The
specialized Bag slot tags are `potion`, `food`, `material`, and `ammunition`. A
supplies slot accepts both `potion` and `food`.

A specialized slot accepts one or more configured tags. A matching item may
still use a general slot.

Item definitions also own:

- Unitless integer weight.
- Maximum stack size.
- Equipment-slot compatibility and the equipment slots it blocks while
  equipped.
- The character level required to equip it.
- Weapon combat data for weapons and ammunition combat data for ammunition, as
  [Weapons And Ammunition](GAME_DESIGN.md#weapons-and-ammunition) describes.
- Whether the item is a Bag, with the Bag's slots and carry bonus.
- Whether the item may be destroyed by the player.
- Optional default server policies.
- A value in whole currency units, from which vendors derive their prices. A
  value of `0` means no vendor buys or sells the item.
- Optional tool kinds, such as `pickaxe`, which only items for the `tool`
  equipment slot may have. A resource node requires a tool kind, so a better
  tool later needs content only.

The game does not use weapon attachments.

### Structural Changes And Retired Ids

Structural changes to weight, stack limits, Bag layouts, or equipment
compatibility require an explicit data migration when existing
instances could become invalid. Content reload must never silently create an
invalid character state. Values, tool kinds, combat data, and level
requirements are not structural, so changing them needs no data migration.

Released ids are permanent. A released definition is never deleted or reused;
it is retired by listing its id in the catalog's retired ids, and a retired id
can never return with any meaning. Categories, tags, and equipment slots retire
the same way. The authoring workflow is in
[Item Catalog Authoring](../content/items/README.md).

### Item Data Ownership

The neutral authoring JSON in `content/items` is the only source of item
definitions. The Unity Item Catalog window edits it and validates every change
with the same deterministic compiler that the command line and CI use. The
window is an authoring interface, not an item authority: Unity assets never
become a second catalog, and compiling the catalog never requires Unity.

Item data is split by responsibility:

- WorldData item definitions own the gameplay rules: category, tags, unitless
  weight, stack rules, equipment compatibility, policies, Bag layout, value,
  tool kinds, and combat data.
- The Unity client owns presentation: icons, localization keys, visual prefabs,
  the garments an item shows on a body with their dye colors, and other
  non-authoritative presentation metadata; see
  [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#items).
- GameService owns durable instance state such as identity, quantity, custody,
  policy state, and revision.

### Client Presentation And Catalog Caching

The client presentation catalog is keyed by stable definition id and bundled
with the client. It never duplicates or overrides gameplay fields. Server
snapshots and mutation results send definition ids and changed instance
state. They never send icons, complete definitions, or Unity asset references,
and GameService never serves image bytes as inventory state. The client must
run the server's catalog revision: a mismatch requires a client update instead
of showing stale rules or missing presentation. A later remote presentation
catalog may download presentation separately, but it must keep the same
definition ids and server contract. How the client loads and caches its
catalogs is in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#inventory-ui).

## Item Instances And Stacks

- Every extant item instance has exactly one current custody location.
- One non-stackable instance occupies one slot.
- A stack occupies one slot and stores an authoritative positive quantity.
- Quantity cannot exceed the definition's maximum stack size.
- Stack weight is unit weight multiplied by quantity.
- Splitting, merging, consuming, granting, moving, and destroying items are
  atomic operations.
- Stacks may merge only when definition, effective policy fingerprint, and all
  other stack-relevant state are identical.
- Protected and unprotected quantities cannot share a stack.
- Insurance initially applies only to non-stackable equipment. Insured stack
  semantics are deferred.
- Item ids are never reused.

### Occupied Slots

Dropping an item onto an occupied container slot merges compatible stacks. When
the items cannot merge, the server may atomically swap their complete slot
assignments only if each item is valid in the other's container and slot type.
Both item revisions, both containers, the character revision, live service
access, policy, Bag, and hard-cap rules are validated in the same
transaction. Non-empty Bag aggregates never use this ordinary item swap.

## Permanent Character Inventory

- Every character has a permanent slot-based inventory independent of the
  equipped Bag.
- The slot count is catalog content, `character.inventorySlotCount`, which is
  20 today. Transaction logic never hard-codes it.
- Weapons use normal general slots when not equipped.
- There are no dedicated weapon-storage slots.
- Empty Bags may occupy compatible general slots as ordinary items.
- Carried inventory contributes to carry weight.

## Equipment

The character equipment slots are catalog content. They are listed in display
order:

- `head`
- `neck`
- `shoulders`, for pauldrons and mantles
- `back`, for a cloak
- `body`
- `legs`
- `hands`
- `feet`
- `main_hand`
- `off_hand`
- `ammunition`
- `ring_1`
- `ring_2`
- `tool`
- `bag`

Every slot has a maximum quantity in content. The `ammunition` slot holds one
stack of up to `1000` units; every other slot holds one item. The ammunition
cap is the same for every ammunition family and tier, and it keeps the
weightless slot from being used to haul tradeable ammunition past the
carry-weight system; as content data it can be tuned after playtests. A
definition may list blocked equipment slots that stay empty while it is
equipped, so a two-handed bow in `main_hand` keeps `off_hand` free.

An equipped item does not occupy a permanent inventory slot and has no carried
weight. Equipment remains normal item ownership plus a mutually exclusive slot
assignment. It is not a parallel non-item system.

### Equipping

- Equipping moves a whole stack only when it fits the slot limit, which is the
  smaller of the slot's maximum quantity and the definition's stack size. A
  compatible stack dropped on the equipped stack tops it up to that limit, and
  a split from the equipped stack moves part of it into an empty container
  slot.
- Equipping an item onto an occupied slot swaps in one transaction: the
  equipped item moves into the container slot the new item came from. That slot
  must accept the equipped item as an unequip into it would, so a specialized
  Bag slot refuses an item without its tags and a non-empty equipped Bag cannot
  leave its slot, and the carry cap must hold afterwards.
- A stack of the equipped definition tops up the equipped stack through a merge
  instead of swapping, and an equip request for such a stack is refused.
- Equipping into a blocked slot, or equipping an item that would block an
  occupied slot, is refused. The item a swap replaces no longer blocks
  anything.
- An item whose required character level is above the character's level cannot
  be equipped by any path, including a corpse Bag swap and a loot straight into
  equipment.
- The server validates definition compatibility, the level requirement, policy
  restrictions, prospective carry weight, and all dependent Bag-capacity rules
  before it commits an equipment change.
- A change of the item in an equipment slot holds attacks for a moment, as
  [Equipment Changes](GAME_DESIGN.md#equipment-changes) describes.

### Corpse Loot Into Equipment

- At death an equipped stack follows the normal death rules and keeps its
  quantity in the matching corpse equipment slot, which applies the same limit.
- A whole corpse item can be looted straight into an equipment slot under the
  same equip rules, including the level requirement and blocked slots. It tops
  up an equipped stack of the same stackable definition and is refused when the
  two cannot merge, it equips into an empty slot, and it swaps any other
  equipped item into the corpse slot it leaves, as a deposit into that slot
  would.
- Bags swap only as whole aggregates. A corpse Bag with contents, which can only
  be the corpse's equipped Bag, swaps with the looter's equipped Bag or takes
  the looter's empty Bag slot whole, with every item in it, and the corpse keeps
  a new empty Bag section. An empty corpse Bag, such as a spare Bag in the
  corpse inventory or the equipped one after its contents left, loots like any
  item into an empty slot.
- A live Mob corpse, which the worker holds in memory, never swaps: its loot
  fills an empty slot or tops up a stack of the same item, in carried storage or
  in the equipment.

### Loot All

Loot all takes everything that fits from a corpse in one transaction, at the
corpse revision the looter saw.

- Corpse equipment comes first, then the corpse inventory, then the Bag
  contents, each in slot order.
- An equippable item takes a free compatible equipment slot under the equip
  rules, including blocked slots and the level requirement, and never replaces
  an equipped item. A stack of the equipped kind, such as arrows, tops up the
  equipped stack.
- The rest tops up carried stacks, the permanent inventory before the equipped
  Bag, and then takes free slots in the order a purchase fills them: matching
  specialized Bag slots, the permanent inventory, general Bag slots.
- A stack that does not fit whole moves in part and leaves the rest in the
  corpse; a live Mob corpse gives only whole items.
- A corpse Bag with items takes an empty Bag slot whole, and its free slots then
  take later loot. When the looter has a Bag, the corpse Bag's contents are
  looted and the emptied Bag follows them.
- Nothing passes the 140 percent hard cap; an item that is too heavy stays while
  lighter ones still fit.
- A Loot all that takes nothing is refused as a full inventory.

One shared planner decides the steps, so the client shows the same result
before the server answers, and GameService executes each step under the rules
of the single loot it stands for. A live Mob corpse grants its entries in one
transaction too, and every committed live grant is recorded, so neither
another Loot all nor a single loot can grant an entry twice after an uncertain
outcome.

### Quick Moves

A right-click in the inventory moves an item to where it belongs by the same
placement rules. A corpse item takes a free compatible equipment slot or else
carried storage, and never replaces an equipped item. Every quick move is
listed in
[Player Inventory Foundation](GAME_FEATURES.md#player-inventory-foundation).

### Ammunition Consumption

Every shot consumes one unit of the equipped ammunition stack for good, also
when the arrow misses because its target died in flight, as
[Weapons And Ammunition](GAME_DESIGN.md#weapons-and-ammunition) decides. One
PostgreSQL transaction per shot would be too heavy, so SimulationWorker counts
shots against the committed stack in memory and commits them in batches, each
one idempotent transaction:

- A batch commits a few seconds after the first uncommitted shot, at the batch
  interval of the [combat rules](../content/README.md#combat-rules), and always
  before any other item or corpse operation of the character, before a leave,
  and before a disconnected session is released. A death carries the shots that
  are still uncommitted, and the death transaction consumes them before it
  partitions the remaining stack.
- A batch retried after a lost answer replays its stored result, so no shot is
  ever spent twice.
- A failure can only leave arrows with the player. A worker crash forgets at
  most one batch interval of shots, and a batch that keeps failing, or that
  GameService rejects, is dropped, so its arrows stay with the player. A worker
  that stops cleanly commits the shots it can still name exactly.
- The owner sees the live count, the committed stack minus the uncommitted
  shots, and a committed batch sends the new stack quantity and item-state
  revision.

The retry limits are in
[Target-Based Combat](SERVICE_FEATURES.md#target-based-combat).

## Bag Rules

A Bag is a physical item. A Bag definition may provide:

- General item slots.
- Specialized slots with one or more accepted tags.
- A carry-capacity bonus while equipped.
- Future Bag-specific stats.

Each Bag instance owns its own contents. Its normal contents follow the Bag when
the complete Bag aggregate moves between valid Bag slots.

### Empty Bags

An empty Bag behaves as an ordinary one-slot item. It may be placed in any
compatible general storage slot, including:

- Permanent character inventory.
- Another active Bag's general slot.
- Character bank.
- Recovery Storage when delivered by the system.
- Future trade, auction, mail, or loot locations when no item policy forbids
  that action.

An empty Bag with a value may also be sold to a vendor from carried storage.

An empty Bag cannot use the supplies, material, or ammunition specialized
slots because it does not have those tags.

The nested Bag's content storage must be empty and inactive while the Bag is held
as an ordinary item. This allows empty Bag items without allowing active
container nesting or cycles.

### Non-Empty Bags

A Bag with contents is locked as an aggregate. It may move only:

- From one character or corpse Bag slot to another valid Bag slot through one
  atomic swap or transfer.
- Through a server-owned death partition that first removes every child item and
  therefore makes the real Bag empty before another custody assignment.

A non-empty Bag cannot be placed in permanent inventory, another Bag, bank,
trade, auction, mail, or an ordinary Recovery Storage slot, and it cannot be
sold to a vendor.

A Bag and its contents form one aggregate. Every operation that touches a Bag or
one of its child items is serialized with every other such operation, so a Bag
swap never races a change to one of its items: the later operation waits,
rereads the aggregate, and is refused when it changed.

## Character Bank

- The bank belongs to one character, not the account.
- The bank is slot-based and does not contribute to carry weight.
- The base slot count is catalog content, `character.bankSlotCount`, which is
  40 today.
- Additional bank capacity is unlocked later through explicit progression or
  entitlement state.
- Only empty Bags may be deposited in the bank.
- Bank access requires an open interaction with an NPC that offers the Bank,
  such as a city quartermaster.
- Bank transactions remain durable PostgreSQL item transactions.

Exact base capacity, unlock increments, and unlock sources remain balance data.

## Recovery Storage

Recovery Storage is a global per-character system delivery queue. Every NPC that
offers Recovery Storage gives access to the same queue.

- Players cannot deposit items into Recovery Storage.
- System deliveries can always be appended. Death processing must never fail
  because Recovery Storage is full.
- The UI may paginate or present deliveries as slots, but persistent system
  capacity is not a hard blocker.
- A delivery is claimed as a whole into character inventory or the bank, so
  its items move together in one transaction.
- A claim into carried storage validates available slots and the 140 percent
  carry-weight cap.
- Recovery contents do not contribute to carry weight.
- Every delivery stores source, source event id, creation time, optional
  availability time, optional expiry time, and claim state.
- The sources are death protection, insurance, and a protected Bag.
  Insufficient respawn capacity and future system restoration are reserved as
  sources and have no producer yet.
- An unclaimed delivery whose expiry time passed is destroyed by the system with
  audit records. No current source sets an expiry, and the expiry and retention
  policy is still open.

## Carry Weight And Encumbrance

Carry weight uses unitless, non-negative integers. Physical measurement units,
decimal weight values, and floating-point comparisons are not part of the
authoritative model. The baseline balance scale is:

- One ammunition unit has weight `1`.
- A one-handed weapon has weight of about `100`.
- A character has base carry capacity `2000`, set in catalog content, so 1000
  spare arrows use half of it.

The shipped catalog uses the same scale. Its Training Shortbow has weight `80`,
and its Traveler's Pack grants a `500` carry-capacity bonus.

Carried weight includes:

- Permanent character inventory.
- Equipped Bag contents.
- Empty Bag items carried in another compatible slot.
- Every stack quantity at definition unit weight.

Carried weight excludes:

- Every item assigned to an equipment slot, including the equipped Bag item
  itself. An equipped Bag's contents still count and its authored capacity bonus
  still applies.
- Character bank.
- Recovery Storage.
- Corpse contents.
- Auction, mail, trade escrow, and other non-carried future custody.

Vendors hold no custody: a sold item is destroyed, and a purchased item is
created in carried storage.

Carry capacity is primarily character-based: the base capacity from catalog
content plus the bonus of the currently equipped Bag. Only the equipped Bag grants
its bonus.

### Encumbrance Curve

- At or below 100 percent capacity, movement uses 100 percent of base speed.
  Running with run energy that drains faster with weight is proposed in
  [Movement And Camera](GAME_DESIGN.md#movement-and-camera).
- From 100 to 140 percent, movement speed decreases linearly.
- At 140 percent, movement uses 20 percent of base speed.
- Exactly 140 percent is allowed.
- No action may increase carried weight beyond 140 percent.
- Death never fails on carry weight. It empties carried storage today, but the
  kernel lets it commit whatever carry state it leaves. A character above 140
  percent, such as one from older data, may only make changes that do not
  increase carried weight or worsen the exact load ratio until it is back within
  the hard cap.

With base capacity `2000`, normal capacity ends at carried weight `2000` and the
140 percent hard cap is carried weight `2800`.

The intended linear reference points are:

| Load | Base movement multiplier |
| --- | --- |
| 100% | 1.00 |
| 105% | 0.90 |
| 110% | 0.80 |
| 120% | 0.60 |
| 130% | 0.40 |
| 140% | 0.20 |

The authoritative multiplier is clamped between 0.20 and 1.00. The shared
simulation applies it to the walk speed.

Every operation computes the prospective numerator and denominator. Equipping a
different Bag removes the Bag root's own weight, activates its carried contents,
and changes capacity in the same transaction. A structural content change that
could create an over-cap state requires an explicit migration.

## Inventory UI Layout

The inventory presentation uses a stable three-column layout:

```text
+--------------------+-----------------------------+------------------------+
| Character          | Context container           | Character inventory    |
| equipment          | Middle                      | Right                  |
| Left               | Bank, corpse, Recovery      | Carry meter, Permanent |
| Paper doll of the  | Storage, vendor, or another | slots, and equipped    |
| equipment slots    | opened loot or storage      | Bag contents           |
|                    | container                   | Selected-item footer   |
+--------------------+-----------------------------+------------------------+
```

- Character equipment remains on the left, laid out like a paper doll.
- Character-owned inventory remains in the right column and keeps its place
  and size in every view.
- The middle column presents the currently relevant external or contextual
  container, including bank, corpse, Recovery Storage, vendor, and world loot
  containers.
- The character column remains visible while another container is open so
  authoritative transfers have a clear source and destination.
- There are three views: character storage only, equipment with character
  storage, and the complete view with equipment, the context container, and
  character storage. Opening the Bank, Recovery Storage, or vendor capability of
  an NPC opens the complete view with that context, and an open corpse always
  shows in the complete view. Each column has fixed bounds, so hiding or
  showing one never moves or resizes another. The keys that open the views are
  in [Player Inventory Foundation](GAME_FEATURES.md#player-inventory-foundation).
- While the character wears an item that can be lowered, such as the Wool Hood,
  the footer of the character column offers to lower or raise it. The server
  stores the choice with the character's look and every client shows it, as
  [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#items) describes.

This is a presentation contract only. Server authority, revisions, slot rules,
and transaction behavior do not depend on screen layout. The current panels
are replaceable uGUI over permanent client state, as
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#inventory-ui)
describes.

## Item Policies

Policies are server-owned item-instance state. Clients may request actions but
cannot grant, remove, or alter a policy.

### Protected On Death

- The actual item does not enter lootable corpse custody.
- The item is delivered to Recovery Storage at death.
- Protected items cannot be traded, auctioned, or sold to a vendor.
- Protected items are not revealed to looters.
- A non-quest protected item may be explicitly destroyed only when its
  definition permits player destruction.
- Protected quest items cannot be destroyed directly by the player.
- Accepting a quest from its NPC grants the protected quest items with one exact
  quest-grant lineage, and repeating the acceptance never grants duplicates.
- Abandoning the owning quest removes its associated quest items through an
  idempotent quest transaction.
- Reaccepting that quest can grant the required items again according to quest
  rules.

Most quest items are expected to use this policy, but category and policy remain
separate concepts.

### Insured

Insurance is one-death protection:

- The actual insured item is delivered to Recovery Storage at death.
- The insurance policy is consumed by the first death where it protects an item.
- A non-interactive snapshot may remain on the corpse so looters can see that
  the character carried the insured equipment.
- The snapshot is not an item, cannot be looted, and has no ownership link to the
  recovered instance.
- An insured item cannot be traded, auctioned, or sold to a vendor.
- The insurance NPC charges a server-owned price atomically with the policy.
- Insurance must be removed through the same insurance NPC service that grants
  it before normal transfer or sale rules resume. Removal keeps the item
  instance.
- Insurance initially targets non-stackable equipment.

Insurance does not consume a claim when the item was already in the bank or
Recovery Storage because the death did not require the policy to protect it.

### Bag Policy Scope

A Bag policy protects only the Bag item. Child items are evaluated independently.
Any complete Bag aggregate transfer that changes the owning character evaluates
the Bag and every child. An active protected or insured policy on any aggregate
member rejects the transfer atomically.
At death, normal child items remain lootable, protected child items move to
Recovery Storage, and insured child items move to Recovery Storage with their
insurance consumed. The protected or insured real Bag is moved only after its
child items have been partitioned, so the recovered Bag is empty.

## Character Currency

Currency is a balance on the character, not an item:

- It has no weight, never enters a corpse, cannot be looted, and is global
  across every Shard.
- The balance is a whole number between `0` and `1,000,000,000,000`. A debit
  the balance does not cover, and a credit above the maximum, is refused without
  changing the balance.
- Every change writes one append-only ledger row with its source kind, source
  id, the item operation that committed it when there is one, the signed
  delta, and the balance after, in the same transaction as the change. The
  source kinds are the starting balance, vendor purchases and sales, insurance,
  and Development grants; balances that existed before the ledger opened it
  with one opening row. Each character's ledger deltas therefore sum to its
  balance.
- Currency is one integer unit. Its display name and the starting balance are
  catalog content, today Gold and 100, enough for a starter tool and some
  arrows. A new character receives the starting balance when it is created, and
  a later change of the starting balance leaves existing characters' balances
  alone. Splitting the unit later, such as into gold, silver, and copper coins,
  would need a one-time migration of balances and prices.
- Every in-world currency change runs inside an item transaction, so it
  advances the character's item-state revision, and the item-state snapshot
  shows the balance from the same snapshot as the items.
- Death never reads or writes the balance or its ledger.
- Local development adds currency through the Development item tool, which
  writes a Development ledger row, never through direct SQL.

## Vendor Trades

A vendor is an NPC capability whose vendor profile is item catalog content: an
ordered list of offers, a purchase price and a sale price in basis points of
item value, and the categories the vendor buys. One profile can serve several
NPCs. The authoring rules and the shipped General Store of Mira the
Quartermaster are in
[Vendor Profiles](../content/items/README.md#vendor-profiles).

### Prices

- Every item has a base value. A vendor sells at a share of the value and buys
  at a smaller share. The starting rates are 100 percent for what players buy
  and 40 percent for what they sell.
- Prices are named from the player's side. The purchase total is rounded up and
  the sale total down, by one shared rule that the client and GameService both
  use, so a shown price and a charged price cannot differ.
- Every vendor's sale rate stays below every vendor's purchase rate, so buying
  from one vendor and selling to the same or another vendor never creates
  currency. The catalog compiler refuses content that breaks this.
- An offered item needs a positive value and no default policy and is never a
  quest item. A vendor never buys quest items or items without value.
- Stock is unlimited and prices are fixed. There is no buyback.

### Trading

- A trade requires an open interaction with the vendor NPC. SimulationWorker
  passes the vendor profile of the capability it validated; the client never
  names the vendor.
- Every trade carries the total price the client showed, and a different
  current price refuses the trade, so a player is never charged a price they did
  not see. Prices, offers, and totals are server-owned; the shown price only
  protects the player from a stale display.
- A purchase buys up to one stack of an offered item. The currency is debited
  and the items are created through carried acquisition in one transaction.
- A sale takes all or part of one stack from carried storage. Equipped, banked,
  and Recovery Storage items must move into carried storage first. Protected,
  insured, and quest items, items without value, non-empty Bags, and categories
  the vendor does not buy are refused, and so is a sale whose total rounds down
  to nothing, so a vendor never takes an item for no payment.
- A sold item is destroyed with a destruction record, and selling part of a
  stack reduces its quantity. The currency is credited in the same transaction.
- Every trade is idempotent by operation id, so a retry never charges or pays
  twice, and an item and its currency always move together.

## Carried Acquisition

Vendor purchases and gathering create items without a client-chosen slot. One
deterministic, all-or-nothing rule places them in carried storage:

1. Top up existing stacks of the same definition without an item policy: the
   permanent inventory slots in order, then the equipped Bag slots in order.
2. Put the rest into empty compatible slots: equipped Bag specialized slots
   that accept the item first, then the permanent inventory, then equipped Bag
   general slots.

If the whole quantity does not fit, the transaction is refused as a full
inventory and changes nothing. The 140 percent hard cap still applies.
Definitions with default policies are never acquired this way.

Gathering grants its yield through the same rule. The yield is granted only
while an item with the resource node's required tool kind is equipped in the
`tool` slot, and a Bag or quest item is never a gathering yield. Resource nodes
themselves are described in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md#resource-nodes-and-gathering).

## Player Death Partition

Player death uses one idempotent PostgreSQL transaction identified by a unique
death event id and an expected character revision. The death event id fences
replays while its corpse is retained, and the revision, which every partition
advances, fences replays after that. The transaction partitions every relevant
item exactly once:

1. Currency remains on the character. Death never reads or writes the balance
   or its ledger, so the balance is unchanged after respawn.
2. Protected-on-death items move to Recovery Storage.
3. Insured items move to Recovery Storage and consume insurance.
4. Remaining permanent inventory, equipment, Bag, and Bag contents move into
   durable corpse custody.
5. Non-interactive snapshots are created where required.
6. Character and corpse inventory revisions advance together.

Death leaves no carried storage behind, and the equipped Bag capacity bonus is
removed when the Bag leaves equipment. The death transaction never fails on
carry weight, because rejecting death would violate the authoritative event and
custody partition. SimulationWorker respawns a dead character only after its
death committed and the committed carry state was applied.

The corpse exposes separate sections for:

- Permanent general inventory.
- Equipment.
- Bag and Bag contents.

Insured equipment snapshots may show the original definition and insured state.
Protected items do not receive individual visible placeholders.

If the Bag itself is protected or insured, the corpse receives a non-interactive
Bag layout snapshot. Normal child items remain in the corresponding corpse Bag
section even though the real Bag has moved to Recovery Storage.

## Player Corpse Lifecycle

- Player corpse custody is durable in PostgreSQL.
- The corpse records its Shard, position, orientation, death time, absolute
  expiry, source character, revisions, and presentation metadata.
- The default player corpse lifetime is five minutes.
- The expiry time is based on database time and never restarts after a process
  restart.
- A player corpse remains for its full lifetime even when empty.
- The dead character may loot the corpse under the same rules as another player.
- Multiple players may inspect and loot the same corpse concurrently.
- Each player may have only one active loot interaction at a time.
- When the assigned SimulationWorker restarts, it restores every unexpired
  player corpse at the recorded location and facing, with the same durable
  corpse id and custody.
- A player corpse shows the dead character's body lying in the last pose of its
  Death clip, with its look and headwear, wearing the items still in its
  equipment section that others see, so looting a garment takes it off the
  lying body; see
  [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server).
  The dying body keeps its clothes until the character respawns.
- When expiry wins the corpse lock, all remaining loot is destroyed with audit
  records and the live representation is removed.
- An in-flight committed loot transaction wins before cleanup. A request that
  arrives after expiry receives a stable corpse-expired result.

## Mob Corpse Lifecycle

Normal Mob corpses are live SimulationWorker state by default:

- Default lifetime is approximately two minutes.
- They do not need to survive a SimulationWorker restart.
- Unclaimed live loot may disappear on restart.
- Loot materialized into persistent player custody uses an idempotent grant id so
  a retry cannot duplicate it.

Mob content definitions may override corpse lifetime and persistence. Bosses may
use durable corpse custody and restart restoration. Persistent Mob corpse
behavior reuses the player-corpse transaction and expiry foundation without
changing the topology model.

Everyone may loot a Mob corpse. The player who dealt the most damage has no
loot right, because the open world is full loot anyway.

A Mob corpse shows its Mob's own model lying in the last pose of its Death clip,
turned as the Mob died. A Mob without a model of its own shows a generic
stand-in.

Live Mob contents never enter PostgreSQL. The worker derives each loot entry and
grant id from one authoritative death event and materializes a claimed whole
item through the exact-session item transaction boundary. Selected durable Mobs
use the same durable corpse record, three-section custody, restoration,
interaction, and expiry paths as players. The corpse is created from loot that
SimulationWorker already rolled from the Mob's loot profile when combat killed
it, and an empty roll creates no corpse.

## Corpse Container Transfers And Bag Swaps

Inspecting a corpse is read-only and does not acquire a long-lived database
lock. Each mutation is a short transaction, and database locks are never held
while waiting for a client network round trip.

### Item And Partial-Stack Transfers

1. Unity sends a loot, deposit, or internal corpse-move intent to the assigned
   SimulationWorker.
2. SimulationWorker validates connection, active session, corpse identity,
   proximity, and the one-active-interaction rule.
3. The worker calls GameService with an idempotent operation id and expected item
   state.
4. GameService rereads the authoritative state and validates both slot
   directions, stack compatibility, policy, prospective weight, and the 140
   percent cap in the same transaction.
5. The item or requested quantity moves atomically. Compatible stacks with
   remaining capacity merge. Dropping a complete item that cannot merge onto an
   occupied slot swaps both item assignments only when each item is valid in
   the other's original slot.
6. A concurrent loser receives a stable stale, unavailable, or quantity-changed
   result and refreshes its view.

Only carried storage participates in deposits. Ordinary equipped items must
first move into carried storage, except through the equipment swap of a loot
into equipment. Bank and Recovery Storage are excluded so a corpse interaction
cannot bypass their separate service authority. Protected or insured items that
cannot change owning character are rejected before they can enter public corpse
custody.

Partial transfers require an empty destination or a compatible stack with
remaining capacity. An incompatible occupied destination can only use a complete
item swap. The server recomputes authoritative carried weight and capacity for
both loot and swap outcomes. A weight-increasing result above 140 percent is
rejected, while a deposit that reduces carried weight remains allowed.

Corpse sections are not read-only category displays. They are slots in the same
authoritative corpse custody boundary. An item already in corpse custody may move
to an empty corpse slot, split or merge a stack, or swap with an occupied corpse
slot. The move may cross the general, equipment, and equipped-Bag-content
sections when both slots accept the final items. Because custody and carried
state do not change, an internal corpse move advances corpse, container, and item
revisions without advancing the interacting character's item-state revision or
forcing a character inventory refresh.

Every corpse equipment slot retains its canonical equipment-slot id. The server
validates item-definition compatibility against that id, and the corpse view
names each slot instead of showing a generic slot number. Death presentation
snapshots remain immutable historical metadata and do not change when live
corpse items are rearranged. A non-empty Bag remains an aggregate root and
cannot use an ordinary internal move; the atomic Bag aggregate operation remains
its only move boundary.

### Bag Aggregate Swaps

A corpse Bag swap atomically:

1. Validates that both Bag aggregates are unchanged, with the looter's item
   state, the corpse, both Bags, and their child items held against concurrent
   change.
2. Computes prospective carried weight and capacity after the swap.
3. Rejects the swap if the looter would exceed 140 percent capacity or any
   policy or compatibility rule fails.
4. Exchanges the complete Bag aggregates between Bag slots.
5. Advances character, corpse, Bag, container, and item revisions.

Other players may continue viewing the corpse, but mutations against the Bag
wait and then reread state.

## In-World Service Flow

While a character has an active simulation session, every durable mutation goes
from Unity through the assigned SimulationWorker to one authenticated
GameService transaction. The committed carry state then reaches the worker,
which applies its movement effect, and Unity. The flow and its authentication
are described in
[Project Architecture](PROJECT_ARCHITECTURE.md#authoritative-in-world-item-mutation).

- GameService validates the exact active simulation session, character, worker,
  runtime, and Shard before it accepts an in-world command.
- The Bank and Recovery Storage need an open interaction with an NPC that offers
  them, which SimulationWorker validates. Offline account routes can never reach
  the Bank or Recovery Storage, because both are city services that need a live
  character in such an interaction, so Recovery is claimed only in the world.
  The owning account may still inspect its Bank and Recovery Storage from
  anywhere, but that read access authorizes no mutation.
- For local testing, a Development option makes Bank and Recovery access
  global. It never includes insurance, never bypasses GameService authority,
  and is refused outside the Development environment.
- Vendor trades need an open interaction with an NPC whose vendor capability is
  available, and the trade uses that capability's vendor profile. Gathering
  needs an open interaction with a resource node.
- The generic world-interaction session is the boundary of every service
  capability. Insurance, Bank, Recovery Storage, vendor, quest, and gathering
  handlers, and later crafting handlers, reuse its authoritative target, range,
  line-of-sight, revision, and session checks instead of adding
  capability-specific target authority. The corpse view and its transaction
  boundary stay authoritative for corpse contents, while corpse target selection
  and the one-active-interaction lease join the shared interaction.
- When the character has no active simulation session, account-authenticated
  routes may move, split, merge, and destroy items in carried storage without
  inventing a live SimulationWorker.

## Transactional Invariants

The implementation must enforce all of the following:

1. Every extant item instance has exactly one current custody assignment.
2. One slot or equipment assignment contains at most one item instance.
3. One item instance cannot be in a slot and equipment simultaneously.
4. Quantity is positive and within the definition stack limit and, in an
   equipment slot or corpse equipment slot, within that slot's limit.
5. Stack merges preserve total quantity and identical policy state.
6. A Bag with child items can only occupy a valid Bag slot.
7. An ordinary slot may contain a Bag only when its child container is empty.
8. Specialized Bag eligibility is server validated.
9. Carried weight never exceeds 140 percent after a weight-increasing action.
10. Death partition, corpse transfer, Bag swap, policy consumption, and recovery
    delivery either commit completely or leave all prior state unchanged.
11. Operation ids are idempotent and cannot be reused with a different payload.
12. Character, Bag, corpse, container, and item locks use one stable ordering.
13. Corpse expiry and a container transfer cannot both claim or destroy the same
    quantity.
14. Internal corpse moves preserve custody, carry state, immutable death
    presentation snapshots, and typed corpse equipment-slot compatibility.
15. Redis state can never override committed PostgreSQL custody.
16. Every currency change writes exactly one ledger row in the same
    transaction, and the balance never leaves its range.
17. A vendor trade moves its items and its currency together or not at all.

## Planned

These rules are decided and wait for later work, mostly the combat work in
[Game Design](GAME_DESIGN.md#combat-model):

- Combat item data on definitions: armor type and stats, elemental
  resistances, and healing multipliers.
- Consume and use intents for potions, food, and other consumables, with the
  consumable cooldowns from the combat design.
- Quest progression as a producer that calls the existing item boundaries.
  Today one configured quest grants its protected quest item.
- Saved equipment loadouts, proposed in
  [Attacks And Abilities](GAME_DESIGN.md#attacks-and-abilities), which swap as
  one equipment change.
- Loot containers in the world, shown in the context column like a corpse.
- The final inventory and corpse loot interface. The corpse already shows the
  dead body; the inventory and loot panels are temporary uGUI.

## Proposed

- **Batched potion and food consumption.** Consumables could use the same
  worker-batched consumption as arrows once consume and use intents exist.
- **Insurance price as content.** The insurance price moves from GameService
  configuration into content and can derive from the insured item's value.

## Open Questions

These values are content or balance data and are not embedded in transaction
control flow:

- Bag slot counts and carry bonuses beyond the development content.
- Bank unlock sizes and sources.
- Recovery Storage expiry duration and retention.
- Insurance price and which NPCs offer insurance.
- Item values, vendor price rates, and the starting balance beyond the
  development content.
- Corpse interaction duration and Mob corpse lifetime overrides.

These rules are not decided yet:

- Whether a Recovery delivery may be claimed item by item instead of as a whole.
- How insurance applies to stacks; today it covers non-stackable equipment only.
- Whether items can ever be dropped on the ground. Today no ground items exist.
- How run energy interacts with the encumbrance curve.
- Whether vendors later get limited stock that restocks, dynamic or
  reputation-based prices, or a buyback list of recent sales. A later capability
  state can carry dynamic stock or prices without changing the trade rules.

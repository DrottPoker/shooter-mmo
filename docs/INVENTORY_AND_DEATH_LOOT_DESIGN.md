# Inventory And Death Loot Design

Last updated: 2026-10-20

Status: Decided. Every rule in the sections below is decided and implemented
unless it says planned. Planned work, proposals, and open questions are collected
in [Planned](#planned), [Proposed](#proposed), and [Open Questions](#open-questions).

## Purpose

This document is the product and domain source of truth for the item,
slot inventory, equipment, Bag, Secure Container, carry-weight, death-loot,
corpse, insurance, and Recovery Storage systems, and for how character currency,
vendor trades, and gathering interact with them.

The system must extend the existing canonical runtime hierarchy:

```text
Global Services -> Fleet -> Node -> SimulationWorker -> SimulationAssignment -> Shard
```

World remains shared game content. There are no Realms. Zone and Layer are future
topology systems and are not introduced by this design.

The delivered phases are recorded in
[Items And Inventory Delivery History](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md), and
the item tables, transaction kernel, and lock order in
[GameService Modules](GAME_SERVICE_MODULES.md).
NPC, Mob, actor-spawn, and generic world-interaction terminology and ownership
are defined in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).

## Canonical Item Terminology

| Term | Meaning |
| --- | --- |
| Item definition | Shared content that defines identity, category, tags, weight, stack limit, equipment compatibility, and other immutable rules |
| Item instance | A durable, uniquely identified quantity of one item definition |
| General slot | A slot that accepts any item not explicitly forbidden by location or policy rules |
| Specialized slot | A Bag slot that accepts definitions with one or more configured tags |
| Bag | A physical item that can be equipped in the character's Bag equipment slot and can provide item slots and carry-capacity bonuses |
| Secure Container | Permanent per-character protected storage whose tier and slot capacity are selected by an account-level entitlement |
| Recovery Storage | A per-character, system-write-only delivery queue reached through any NPC that offers it |
| Corpse | A bidirectional loot container backed by durable custody for players and selected persistent Mobs |
| Snapshot | Non-interactive corpse presentation metadata that never grants ownership or references a lootable item instance |

`Bag` is the canonical equipment and item term. A Bag may be presented as a
backpack, satchel, case, or another visual form without changing the domain.
`Secure Container` is not a Bag item and does not use the Bag equipment slot.

## Authority And Data Ownership

- GameService owns persistent item definitions mirrored from shared content,
  item instances, slot assignments, equipment assignments, policies, bank,
  Secure Container, Recovery Storage, durable corpse custody, all durable
  item transactions, and character currency with its ledger.
- PostgreSQL is the source of truth for persistent item ownership and custody.
- SimulationWorker owns live interaction validation, proximity, active corpse
  presentation, one-active-loot-interaction enforcement, combat state, and
  authoritative encumbrance effects for its assigned shard.
- SimulationWorker requests durable mutations through an authenticated
  GameService service boundary. It never writes item tables directly.
- Unity sends intents and displays authoritative results. It never supplies
  trusted weight, capacity, policy, quantity, slot compatibility, or ownership.
- Redis is not an item, inventory, corpse-custody, or transaction authority.

Persistent character items are global and are not owned by a Fleet, Node,
SimulationWorker, SimulationAssignment, or Shard. A durable player corpse records
the Shard where the death happened only so the assigned SimulationWorker can
restore its live representation.

## Item Definitions, Categories, And Tags

Every item definition has one primary category. The categories are catalog
content:

- `weapon`
- `ammunition`
- `armor`
- `jewelry`
- `tool`
- `bag`
- `material`
- `potion`
- `food`
- `quest_item`

Definitions may also carry zero or more tags. Tags provide many-to-many
compatibility without forcing an item into only one gameplay use. The
specialized Bag slot tags are:

- `potion`
- `food`
- `material`
- `ammunition`

A supplies slot accepts both `potion` and `food`.

A specialized slot accepts one or more configured tags. A matching item may
still use a general slot. A definition must explicitly be allowed in the Secure
Container. Weapons are not allowed in the Secure Container. Other allowed and
forbidden definitions remain content-controlled.

Item definitions also own:

- Unitless integer weight.
- Maximum stack size.
- Equipment-slot compatibility and the equipment slots it blocks while
  equipped.
- The character level required to equip it.
- Weapon combat data for weapons and ammunition combat data for ammunition.
- Whether the item is a Bag.
- Whether the item may be destroyed by the player.
- Location restrictions such as Secure Container eligibility.
- Optional default server policies.
- A value in whole currency units, from which vendors derive their prices. A
  value of `0` means no vendor buys or sells the item.
- Optional tool kinds, such as `pickaxe`, which only items for the `tool`
  equipment slot may have. A resource node requires a tool kind, so a better
  tool later needs content only.

Structural changes to weight, stack limits, Bag layouts, location eligibility,
or equipment compatibility require an explicit data migration when existing
instances could become invalid. Content reload must never silently create an
invalid character state. Value and tool kinds are not structural, so changing
them needs no data migration.

The game does not use weapon attachments in the current design.

### Catalog Authoring Workflow

The Unity Item Catalog editor operates on the canonical authoring JSON under
`content/items`, one file per definition, and invokes the same deterministic
catalog compiler used by command-line verification and CI.

The editor is a content-authoring interface, not an item authority. Unity assets
must not become a second catalog, and backend compilation must not require Unity.
The editor creates, duplicates, edits, validates, and bakes definitions. A baked
definition is never deleted or reused; it is retired by listing its id in the
catalog's retired ids, and a retired id can never return with any meaning. The
authoring contract is in [Item Catalog Authoring](../content/items/README.md).

### Client Presentation And Catalog Caching

Item data is split by responsibility:

- WorldData gameplay definitions own category, tags, unitless weight, stack
  rules, equipment compatibility, location eligibility, policies, and Bag
  layout.
- Unity client presentation owns icons, localization keys, visual prefabs, and
  other non-authoritative presentation metadata.
- GameService owns durable instance state such as identity, quantity, custody,
  policy state, and revision.

The client presentation catalog is keyed by stable item definition id and is
bundled with the MVP Unity client. It records the WorldData catalog revision it
was baked against plus its own deterministic presentation revision. An icon-only
change advances the presentation revision without changing gameplay structural
fingerprints. Unity loads both local catalogs once into a definition-id lookup
and reuses the result across scenes and inventory refreshes.

Server snapshots and mutation results send definition ids and changed instance
state. They do not repeatedly send icons, complete definitions, or Unity asset
references. GameService never serves image bytes as part of inventory state.

The implemented account read boundary exposes the current catalog separately
from one complete owned-character inventory snapshot. Character reads require
exact account ownership and use a read-only repeatable-read PostgreSQL
transaction. An owning account may inspect its bank and Recovery Storage, but
that read access does not authorize any mutation or bypass later city-service
validation.

At session or inventory bootstrap, the server provides its authoritative catalog
revision. The MVP client requires its bundled gameplay catalog and presentation
catalog to match that revision. A mismatch produces an update-required error
instead of allowing stale rules or missing presentation. A future remote
Addressables catalog may download and cache presentation by presentation
revision, but it must preserve the same definition ids and server contract.

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
stack of up to `1000` units; every other slot holds one item. A definition may
list blocked equipment slots that stay empty while it is equipped, so a
two-handed bow in `main_hand` keeps `off_hand` free.

Equipping moves a whole stack only when it fits the slot limit, which is the
smaller of the slot maximum quantity and the definition stack size. A compatible
stack dropped on the equipped stack tops it up to that limit, and a split from
the equipped stack moves part of it into an empty container slot. An equipped
stack has zero carried weight like every other equipped item. Equipping an item
onto an occupied slot swaps in one transaction: the equipped item moves into the
container slot the new item came from. That slot must accept the equipped item
as an unequip into it would, so a Secure Container slot refuses an ineligible
item and a non-empty equipped Bag cannot leave its slot, and the carry cap must
hold afterwards. A stack of the equipped definition tops up the equipped stack
through a merge instead of swapping, and an equip request for such a stack is
rejected with `equipment_slot_occupied`. Equipping into a blocked slot, or equipping an item that
would block an occupied slot, is rejected with `equipment_slot_blocked`; the
item a swap replaces no longer blocks anything. At death an equipped stack follows the
normal death rules and keeps its quantity in the matching corpse equipment slot,
which applies the same limit. A whole corpse item can also be looted straight
into an equipment slot under the same equip rules, including the level
requirement and blocked slots: it tops up an equipped stack of the same
stackable definition, and is rejected when the two cannot merge, equips into an
empty slot, or swaps any other equipped item into the corpse slot it leaves, as
a deposit into that slot would. Bags still swap only
as whole aggregates, and a live Mob corpse, which the worker holds in memory,
never swaps: its loot fills an empty slot or tops up a stack of the same item,
in carried storage or in the equipment (decided 2026-10-19 and 2026-10-20,
replacing the earlier rule that corpse loot enters carried storage only).

An equipped item does not occupy a permanent inventory slot. Equipment remains
normal item ownership plus a mutually exclusive slot assignment. It is not a
parallel non-item system.

The server validates definition compatibility, the level requirement, policy
restrictions, prospective carry weight, and all dependent Bag-capacity rules
before committing equipment changes. An item whose `requiredCharacterLevel` is
above the character's level is rejected with `equipment_level_requirement_not_met`,
also when a corpse Bag swap would equip it.

### Ammunition Consumption And Equipment Lockout

Every shot consumes one unit of the equipped ammunition stack, and used
ammunition is never recovered, also when the arrow misses because its target
died in flight. One PostgreSQL transaction per shot would be too heavy, so
SimulationWorker counts shots against the committed stack in memory and commits
them in batches through one idempotent kernel command per batch:

- A batch starts 5 seconds after the first uncommitted shot, the
  `batchIntervalSeconds` of the combat rules, before any other item or corpse
  operation of the character, before a leave, and before a disconnected session
  is released. A death carries the shots that are still uncommitted, and the
  death transaction consumes them before it partitions the remaining stack.
- The batch id is a random version 4 operation id, so a retry replays the
  stored result. An uncertain batch retries up to 8 times; after that, and
  after a rejection, its shots are lost and the arrows stay with the player.
- A worker crash loses the uncommitted shots of at most one interval, which
  then stay with the player. A worker that stops cleanly commits the shots it
  can still name exactly, also behind a batch whose answer arrived while it
  stopped.
- The owner sees the live count, the committed stack minus the uncommitted
  shots, and a committed batch sends the new stack quantity and item-state
  revision.

A different item in any equipment slot holds attacks for the equipment lockout
of 1 second, and so does an item or corpse operation that may change equipment
while it runs. A changed arrow count keeps the same item and does not start the
lockout.

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

Every operation that touches a Bag or one of its child items participates in the
same Bag-level transaction lock. A Bag swap is rejected while another transaction
is changing one of its contents. An item request is rejected or retried if the
Bag is already being moved.

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

## Secure Container

The Secure Container is permanent per-character storage. It is not a physical
Bag item, cannot move, cannot be traded, cannot be looted, and cannot be assigned
to an equipment slot.

### Ownership And Tier

- Secure Container contents belong to the character.
- The account owns the entitlement that selects the Secure Container name, tier,
  and slot capacity for its characters.
- The base tier starts with four slots.
- Future premium or progression systems may grant larger tiers. A player may
  only return to the base tier; a larger tier is granted only by a trusted
  entitlement source.
- The Secure Container itself has no item weight, but every item inside it
  contributes to the character's carried weight.

### Access And Eligibility

- The character may move eligible items into and out of the Secure Container
  while in the world.
- The server validates all location eligibility, slot, stack, weight, and policy
  rules.
- Weapons are not eligible.
- Other eligibility is definition-driven.
- A move that would make carried weight exceed the hard cap is rejected.

### Tier Reduction

If the account's Secure Container tier loses slots:

1. The change runs through the item transaction service.
2. Items in removed slots are selected deterministically by descending slot
   index.
3. Those items move atomically to Recovery Storage with source
   `secure_capacity_reduction`.
4. No item is deleted.
5. Items do not automatically return if the tier later increases.
6. Any active SimulationWorker receives the resulting carry-state revision.

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
- Initial sources include death protection, insurance, protected Bag,
  insufficient respawn capacity, secure-capacity reduction, and future system
  restoration.

Exact expiry and retention policies are deferred. The schema must support them
without requiring a custody redesign.

## Carry Weight And Encumbrance

Carry weight uses unitless, non-negative integers. Physical measurement units,
decimal weight values, and floating-point comparisons are not part of the
authoritative model. The baseline balance scale is:

- One ammunition unit has weight `1`.
- A one-handed weapon has weight of about `100`.
- A character has base carry capacity `2000`, set in catalog content.

The development catalog uses the same scale. Its Training Shortbow has weight
`80`, and its Traveler's Pack grants a `500` carry-capacity bonus.

Carried weight includes:

- Permanent character inventory.
- Equipped Bag contents.
- Empty Bag items carried in another compatible slot.
- Secure Container contents.
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
  Running with run energy that drains faster with weight is planned in
  [Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md).
- From 100 to 140 percent, movement speed decreases linearly.
- At 140 percent, movement uses 20 percent of base speed.
- Exactly 140 percent is allowed.
- No action may increase carried weight beyond 140 percent.
- An involuntary capacity loss during death may leave retained Secure Container
  weight above 140 percent. Death still commits. Until the character returns
  within the hard cap, only operations that do not increase carried weight or
  worsen the exact load ratio may commit.

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

The Unity inventory presentation uses a stable three-column layout:

```text
+--------------------+-----------------------------+------------------------+
| Character          | Context container           | Character inventory    |
| equipment          | Middle                      | Right                  |
| Left               | Bank, corpse, Recovery      | Carry meter, Permanent |
| Paper doll of the  | Storage, or another opened  | slots, equipped Bag    |
| equipment slots    | loot or storage container   | contents, and Secure   |
|                    |                             | Container              |
|                    |                             | Selected-item footer   |
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
- The character-only and equipment views hide columns and never move the
  character column.

This is a presentation contract only. Server authority, revisions, slot rules,
and transaction behavior do not depend on screen layout.

The client keeps this layout in replaceable uGUI while its
catalog cache, immutable snapshots, revision coherence, operation journal,
structured errors, and refresh orchestration live in a persistent controller.
Item transfers use typed reusable drag sources and targets. Item clicks select
only split and destruction actions, and dropping any Recovery item withdraws its
complete delivery atomically. Bank and Recovery Storage may be inspected globally
by the owning account, but their mutations still require SimulationWorker's live
access evaluation. Local Development can explicitly evaluate Bank and Recovery
as globally accessible for testing. Production requires an open interaction with an NPC
whose Bank or Recovery Storage capability is available, and the Development
option never includes insurance access or bypasses GameService authority.
The corpse context adapter exposes durable corpse custody as a bidirectional
container without changing custody optimistically. Carried items may be
deposited into corpse slots, and corpse items may be looted into carried slots.
Bank and Recovery Storage never participate in a corpse transfer.

The maintained temporary presentation has three explicit view modes. `B` opens
only character storage, `C` opens equipment together with character storage, and
`I` opens the complete view with equipment, the Bank, Recovery Storage, or corpse
context, and character storage. Opening the Bank or Recovery Storage capability
of an NPC opens the complete view with that context. Equipment, context, and character storage
are separate module roots with fixed bounds. Hiding or showing one module never
moves or resizes either of the others.

Dropping an item onto an occupied container slot merges compatible stacks. When
the items cannot merge, the server may atomically swap their complete slot
assignments only if each item is valid in the other's container and slot type.
Both item revisions, both containers, the character revision, live service
access, policy, Bag, Secure Container, and hard-cap rules are validated in the
same transaction. Non-empty Bag aggregates do not use this ordinary item swap.

## Item Policies

Policies are server-owned item-instance state. Clients may request actions but
cannot grant, remove, or alter a policy.

### Protected On Death

- The actual item does not enter lootable corpse custody.
- The item is delivered to Recovery Storage at death unless it was already in
  the Secure Container.
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

- The actual insured item is delivered to Recovery Storage at death unless it
  was already in the Secure Container.
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

Insurance does not consume a claim when the item was already in Secure
Container, bank, or Recovery Storage because the death did not require the
policy to protect it.

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
  the balance does not cover fails with `currency_insufficient`, and a credit
  above the maximum fails with `currency_limit_exceeded`, without changing the
  balance.
- Every change writes one append-only ledger row with its source kind, source
  id, the item operation that committed it when there is one, the signed
  delta, and the balance after, in the same transaction as the change. The
  source kinds are the starting balance, vendor purchases and sales, insurance,
  and Development grants; balances that existed before the ledger opened it
  with one opening row. Each character's ledger deltas therefore sum to its
  balance.
- The currency display name and the starting balance are catalog content,
  today Gold and 100. A new character receives the starting balance when it is
  created. Existing characters keep their balance.
- Every in-world currency change runs inside an item transaction, so it
  advances the character's item-state revision, and the item-state snapshot
  shows the balance from the same snapshot as the items.
- Local development adds currency through the Development item tool, which
  writes a Development ledger row, never through direct SQL.

## Vendor Trades

A vendor is an NPC capability whose vendor profile is item catalog content: an
ordered list of offers, a purchase price and a sale price in basis points of
item value, and the categories the vendor buys. Prices are named from the
player's side. The purchase total is rounded up and the sale total down, by one
shared rule that the client and GameService both use. The catalog compiler
rejects vendor content whose sale rate is not below every purchase rate, so
buying and selling back never creates currency.

- A trade requires an open interaction with the vendor NPC. SimulationWorker
  passes the vendor profile of the capability it validated; the client never
  names the vendor.
- Every trade carries the total price the client showed. A different current
  price rejects the trade with `vendor_price_changed`, so a player is never
  charged a price they did not see.
- A purchase buys up to one stack of an offered item. The currency is debited
  and the items are created through carried acquisition in one transaction.
- A sale takes all or part of one stack from carried storage: the permanent
  inventory, the equipped Bag, or the Secure Container. Equipped, banked, and
  Recovery Storage items must move into carried storage first. Protected,
  insured, and quest items, items without value, non-empty Bags, and
  categories the vendor does not buy are refused.
- A sold item is destroyed with a destruction record with reason `vendor_sale`,
  and selling part of a stack reduces its quantity. The currency is credited
  in the same transaction. There is no buyback.
- Stock is unlimited and prices are fixed.
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
3. The Secure Container is never filled automatically.

If the whole quantity does not fit, the transaction rejects with
`inventory_full` and changes nothing. The 140 percent hard cap still applies.
Definitions with default policies are never acquired this way.

Gathering grants its yield through the same rule. The yield is granted only
while an item with the resource node's required tool kind is equipped in the
`tool` slot, otherwise the grant is rejected with `gathering_tool_required`, and
a Bag or quest item is never a gathering yield. Resource nodes themselves are
described in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).

## Player Death Partition

Player death uses one idempotent PostgreSQL transaction identified by a unique
death event id and an expected character revision. The death event id fences
replays while its corpse is retained, and the revision, which every partition
advances, fences replays after that. The transaction partitions every relevant
item exactly once:

1. Currency remains on the character. Death never reads or writes the balance
   or its ledger, so the balance is unchanged after respawn.
2. Secure Container contents remain in place.
3. Protected-on-death items move to Recovery Storage.
4. Insured items move to Recovery Storage and consume insurance.
5. Remaining permanent inventory, equipment, Bag, and Bag contents move into
   durable corpse custody.
6. Non-interactive snapshots are created where required.
7. Character and corpse inventory revisions advance together.

An equipped Bag capacity bonus is removed when the Bag leaves equipment. If
retained Secure Container contents then place the character above 140 percent,
the death transaction still commits because rejecting death would violate the
authoritative event and custody partition. The item transaction kernel accepts
only non-worsening remediation from that state and rejects any added weight or
worse load ratio. SimulationWorker respawns a dead character only after its
death committed and the committed carry state was applied.

The corpse exposes separate sections for:

- Permanent general inventory.
- Equipment.
- Bag and Bag contents.
- Secure Container name and tier snapshot.

The Secure Container snapshot is non-interactive and contains no item contents.
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
  player corpse. It presents a restored corpse with the player corpse key
  `corpse.remains`, which the client shows as the body lying in the last pose of
  Death, at the recorded location and facing while preserving the same durable
  corpse id and custody. A Mob corpse uses `corpse.mob` and keeps the generic
  capsule.
- When expiry wins the corpse lock, all remaining loot is destroyed with audit
  records and the live representation is removed.
- An in-flight committed loot transaction wins before cleanup. A request that
  arrives after expiry receives a stable corpse-expired result.

No Zone or Layer identity is stored because those systems do not exist.

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

Live Mob contents never enter PostgreSQL.
The worker derives each loot entry and grant id from one authoritative death
event and materializes a claimed whole item through the exact-session item
transaction boundary. Selected durable Mobs use the same durable corpse record,
three-section custody, restoration, interaction, and expiry paths as players.
The lifecycle input contains already resolved loot seeds. SimulationWorker
rolls them from the Mob's loot profile when combat kills it, and an empty roll
creates no corpse.

## Corpse Container Transfers And Bag Swaps

Inspecting a corpse is read-only and does not acquire a long-lived database lock.
Each mutation is a short transaction.

### Item And Partial-Stack Transfers

1. Unity sends a loot, deposit, or internal corpse-move intent to the assigned
   SimulationWorker.
2. SimulationWorker validates connection, active session, corpse identity,
   proximity, and the one-active-interaction rule.
3. The worker calls GameService with an idempotent operation id and expected item
   state.
4. GameService locks the character, corpse, source item, optional occupied
   target, and both containers, then rereads authoritative state.
5. The transaction validates both slot directions, stack compatibility, policy,
   prospective weight, and the 140 percent cap.
6. The item or requested quantity moves atomically. Compatible stacks with
   remaining capacity merge. Dropping a complete item that cannot merge onto an
   occupied slot swaps both item assignments only when each item is valid in
   the other's original slot.
7. A concurrent loser receives a stable stale, unavailable, or quantity-changed
   result and refreshes its view.

Only carried container custody participates in deposits: permanent inventory,
equipped Bag contents, and Secure Container. Ordinary equipped items must first
move into carried storage. Bank and Recovery Storage are excluded so a corpse
interaction cannot bypass their separate service authority. Protected or
insured items that cannot change owning character are rejected before they can
enter public corpse custody.

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
validates item-definition compatibility against that id, and Unity presents the
catalog display name and id instead of a generic slot number. Death presentation
snapshots remain immutable historical metadata and do not change when live corpse
items are rearranged. A non-empty Bag remains an aggregate root and cannot use an
ordinary internal move; the existing atomic Bag aggregate operation remains its
only move boundary.

Database locks are never held while waiting for a client network round trip.

### Bag Aggregate Lock

Every operation involving a Bag or its child items locks the Bag aggregate first.
This prevents a Bag swap from racing a child-item loot request.

A corpse Bag swap atomically:

1. Locks the looter item state, corpse, both Bag instances, and affected child
   items in stable id order.
2. Validates that both Bag aggregates are unchanged.
3. Computes prospective carried weight and capacity after the swap.
4. Rejects the swap if the looter would exceed 140 percent capacity or any
   policy or compatibility rule fails.
5. Exchanges the complete Bag aggregates between Bag slots.
6. Advances character, corpse, Bag, container, and item revisions.

Other players may continue viewing the corpse, but mutations against the locked
Bag wait and then reread state.

## In-World Service Flow

While a character has an active simulation session, durable mutations follow:

```text
Unity intent
  -> assigned SimulationWorker
  -> authenticated GameService item transaction
  -> PostgreSQL commit
  -> authoritative inventory and carry-state revision
  -> SimulationWorker encumbrance update
  -> Unity result and state update
```

SimulationWorker authenticates with its logical worker id and runtime id.
GameService validates the exact active simulation session, character, worker,
runtime, and Shard before accepting an in-world command.

Bank and Recovery Storage access additionally require an open NPC interaction
that SimulationWorker validates. Offline account routes can never reach the bank or
Recovery Storage, because both are city services that need a live character in
an open interaction with an NPC that offers them. Recovery is therefore claimed
only in the world. Secure Container operations are allowed in the world but
remain category, slot, policy, weight, and session validated.

Vendor trades additionally require an open interaction with an NPC whose vendor
capability is available, and the trade uses that capability's vendor profile.
Gathering requires an open interaction with a resource node.

The generic world-interaction session is the boundary of every service
capability. Insurance, Bank, Recovery Storage, vendor, quest, and gathering
handlers, and later crafting handlers, reuse its authoritative target, range,
line-of-sight, revision, and session checks instead of adding
capability-specific target authority. The
existing corpse view and transaction boundary remains authoritative for corpse
contents while its client target selection and one-active-interaction lease join
the shared interaction UX.

When no simulation session is active, account-authenticated APIs may perform
appropriate non-world mutations without inventing a live SimulationWorker.

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
8. Secure Container and specialized Bag eligibility is server validated.
9. Carried weight never exceeds 140 percent after a weight-increasing action.
10. Death partition, corpse transfer, Bag swap, policy consumption, and recovery delivery
    either commit completely or leave all prior state unchanged.
11. Operation ids are idempotent and cannot be reused with a different payload.
12. Character, Bag, corpse, container, and item locks use one stable ordering.
13. Corpse expiry and a container transfer cannot both claim or destroy the same
    quantity.
14. Internal corpse moves preserve custody, carry state, immutable death
    presentation snapshots, and typed corpse equipment-slot compatibility.
15. Account-tier reduction cannot lose Secure Container contents.
16. Redis state can never override committed PostgreSQL custody.
17. Every currency change writes exactly one ledger row in the same
    transaction, and the balance never leaves its range.
18. A vendor trade moves its items and its currency together or not at all.

## Planned

These rules are decided and wait for later work, mostly the combat work in
[Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md):

- Combat item data on definitions: armor type and stats, elemental
  resistances, and healing multipliers. Weapon and ammunition combat data and
  the level requirement are catalog content since format 4, as described in
  [Item Catalog Authoring](../content/items/README.md), and GameService enforces
  the level requirement on every equip.
- Consume and use intents for potions, food, and other consumables, with the
  consumable cooldowns from the combat design.
- The equipment lockout for a future two-loadout swap.
- Quest progression as a producer that calls the existing item boundaries.
  Vendors, gathering, combat death, and Mob loot tables are implemented.
- Final corpse presentation art.

## Proposed

- **Batched potion and food consumption.** Consumables could use the same
  worker-batched consumption as arrows once consume and use intents exist.

## Open Questions

These values are content or balance data and are not embedded in transaction
control flow:

- Bag slot counts and carry bonuses beyond the development content.
- Secure Container upgrade prices and progression sources.
- Bank unlock sizes and sources.
- Recovery Storage expiry duration and retention.
- Which non-weapon definitions are Secure Container eligible.
- Insurance price and which NPCs offer insurance.
- Item values, vendor price rates, and the starting balance beyond the
  development content.
- Corpse interaction duration and Mob corpse lifetime overrides.

These rules are not decided yet:

- Whether a Recovery delivery may be claimed item by item instead of as a whole.
- How insurance applies to stacks; today it covers non-stackable equipment only.
- Whether items can ever be dropped on the ground. Today no ground items exist.
- How run energy interacts with the encumbrance curve.

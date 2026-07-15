# Inventory And Death Loot Design

Last updated: 2026-07-15

Status: Planned and not implemented

## Purpose

This document is the product and domain source of truth for the planned item,
slot inventory, equipment, Bag, Secure Container, carry-weight, death-loot,
corpse, insurance, and Recovery Storage systems.

The system must extend the existing canonical runtime hierarchy:

```text
Global Services -> Fleet -> Node -> SimulationWorker -> SimulationAssignment -> Shard
```

World remains shared game content. There are no Realms. Zone and Layer are future
topology systems and are not introduced by this design.

Implementation sequencing, proposed database tables, service contracts, and test
gates are defined in
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

## Canonical Item Terminology

| Term | Meaning |
| --- | --- |
| Item definition | Shared content that defines identity, category, tags, weight, stack limit, equipment compatibility, and other immutable rules |
| Item instance | A durable, uniquely identified quantity of one item definition |
| General slot | A slot that accepts any item not explicitly forbidden by location or policy rules |
| Specialized slot | A Bag slot that accepts definitions with one or more configured tags |
| Bag | A physical item that can be equipped in the character's Bag equipment slot and can provide item slots and carry-capacity bonuses |
| Secure Container | Permanent per-character protected storage whose tier and slot capacity are selected by an account-level entitlement |
| Recovery Storage | A per-character, system-write-only delivery queue accessible from every major city |
| Corpse | A lootable live representation backed by durable custody for players and selected persistent NPCs |
| Snapshot | Non-interactive corpse presentation metadata that never grants ownership or references a lootable item instance |

`Bag` is the canonical equipment and item term. A Bag may be presented as a
backpack, satchel, case, or another visual form without changing the domain.
`Secure Container` is not a Bag item and does not use the Bag equipment slot.

## Authority And Data Ownership

- AuthService owns persistent item definitions mirrored from shared content,
  item instances, slot assignments, equipment assignments, policies, bank,
  Secure Container, Recovery Storage, durable corpse custody, and all durable
  item transactions.
- PostgreSQL is the source of truth for persistent item ownership and custody.
- SimulationWorker owns live interaction validation, proximity, active corpse
  presentation, one-active-loot-interaction enforcement, combat state, and
  authoritative encumbrance effects for its assigned shard.
- SimulationWorker requests durable mutations through an authenticated
  AuthService service boundary. It never writes item tables directly.
- Unity sends intents and displays authoritative results. It never supplies
  trusted weight, capacity, policy, quantity, slot compatibility, or ownership.
- Redis is not an item, inventory, corpse-custody, or transaction authority.

Persistent character items are global and are not owned by a Fleet, Node,
SimulationWorker, SimulationAssignment, or Shard. A durable player corpse records
the Shard where the death happened only so the assigned SimulationWorker can
restore its live representation.

## Item Definitions, Categories, And Tags

Every item definition has one primary category. Initial categories include:

- `material`
- `medical`
- `weapon`
- `ammunition`
- `armor`
- `tool`
- `bag`
- `quest_item`

Definitions may also carry zero or more tags. Tags provide many-to-many
compatibility without forcing an item into only one gameplay use. The first
specialized Bag slot tags are:

- `medical`
- `material`
- `ammunition`

A specialized slot accepts one or more configured tags. A matching item may
still use a general slot. A definition must explicitly be allowed in the Secure
Container. Weapons are not allowed in the Secure Container. Other allowed and
forbidden definitions remain content-controlled.

Item definitions also own:

- Unit weight in integer grams.
- Maximum stack size.
- Equipment-slot compatibility.
- Whether the item is a Bag.
- Whether the item may be destroyed by the player.
- Location restrictions such as Secure Container eligibility.
- Optional default server policies.

Structural changes to weight, stack limits, Bag layouts, location eligibility,
or equipment compatibility require an explicit data migration when existing
instances could become invalid. Content reload must never silently create an
invalid character state.

The game does not use weapon attachments in the current design.

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
- The initial target is approximately 20 general slots. The exact number is
  content or progression data and must not be hard-coded into transaction logic.
- Weapons use normal general slots when not equipped.
- There are no dedicated weapon-storage slots.
- Empty Bags may occupy compatible general slots as ordinary items.
- Carried inventory contributes to carry weight.

## Equipment

The initial character equipment slots are:

- `head`
- `body_armor`
- `primary_weapon`
- `secondary_weapon`
- `tool`
- `ring_1`
- `ring_2`
- `bag`

An equipped item does not occupy a permanent inventory slot. Equipment remains
normal item ownership plus a mutually exclusive slot assignment. It is not a
parallel non-item system.

The server validates definition compatibility, policy restrictions, prospective
carry weight, and all dependent Bag-capacity rules before committing equipment
changes.

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
- Future trade, auction, mail, vendor, or loot locations when no item policy
  forbids that action.

An empty Bag cannot use the initial medical, material, or ammunition specialized
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
trade, auction, mail, vendor storage, or an ordinary Recovery Storage slot.

Every operation that touches a Bag or one of its child items participates in the
same Bag-level transaction lock. A Bag swap is rejected while another transaction
is changing one of its contents. An item request is rejected or retried if the
Bag is already being moved.

## Character Bank

- The bank belongs to one character, not the account.
- The bank is slot-based and does not contribute to carry weight.
- The initial target is approximately 40 base slots.
- Additional bank capacity is unlocked later through explicit progression or
  entitlement state.
- Only empty Bags may be deposited in the bank.
- Bank access is a major-city service.
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
- Future premium or progression systems may grant larger tiers.
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

Recovery Storage is a global per-character system delivery queue. Every major
city provides access to the same queue.

- Players cannot deposit items into Recovery Storage.
- System deliveries can always be appended. Death processing must never fail
  because Recovery Storage is full.
- The UI may paginate or present deliveries as slots, but persistent system
  capacity is not a hard blocker.
- Items can be withdrawn to character inventory or bank.
- A withdrawal to carried storage validates available slots and the 140 percent
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

Carry weight uses integer grams. Floating-point values are not authoritative.

Carried weight includes:

- Permanent character inventory.
- Equipped weapons, armor, tools, rings, and Bag.
- Equipped Bag contents.
- Empty Bag items carried in another compatible slot.
- Secure Container contents.
- Every stack quantity at definition unit weight.

Carried weight excludes:

- Character bank.
- Recovery Storage.
- Corpse contents.
- Vendor, auction, mail, trade escrow, and other non-carried future custody.

Carry capacity is primarily character-based. An equipped Bag may add a bonus.
Only the currently equipped Bag grants its carry-capacity bonus.

### Encumbrance Curve

- At or below 100 percent capacity, movement uses 100 percent of base speed and
  sprint remains available.
- Above 100 percent, sprint is disabled.
- From 100 to 140 percent, movement speed decreases linearly.
- At 140 percent, movement uses 20 percent of base speed.
- Exactly 140 percent is allowed.
- No action may increase carried weight beyond 140 percent.

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
simulation applies it to normal base movement after action restrictions are
resolved. Server authority decides whether sprint is allowed.

Every operation computes the prospective numerator and denominator. Equipping a
different Bag can change item weight, carried contents, and capacity in the same
transaction. A structural content change that could create an over-cap state
requires an explicit migration.

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
- Insurance must be removed through the same insurance NPC service that grants
  it before normal transfer or sale rules resume.
- Insurance initially targets non-stackable equipment.

Insurance does not consume a claim when the item was already in Secure
Container, bank, or Recovery Storage because the death did not require the
policy to protect it.

### Bag Policy Scope

A Bag policy protects only the Bag item. Child items are evaluated independently.
At death, normal child items remain lootable, protected child items move to
Recovery Storage, and insured child items move to Recovery Storage with their
insurance consumed. The protected or insured real Bag is moved only after its
child items have been partitioned, so the recovered Bag is empty.

## Player Death Partition

Player death uses one idempotent PostgreSQL transaction identified by a unique
death event id. The transaction partitions every relevant item exactly once:

1. Currency remains on the character.
2. Secure Container contents remain in place.
3. Protected-on-death items move to Recovery Storage.
4. Insured items move to Recovery Storage and consume insurance.
5. Remaining permanent inventory, equipment, Bag, and Bag contents move into
   durable corpse custody.
6. Non-interactive snapshots are created where required.
7. Character and corpse inventory revisions advance together.

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
  player corpse. It may present a restored corpse as a generic loot crate at the
  recorded location while preserving the same durable corpse id and custody.
- When expiry wins the corpse lock, all remaining loot is destroyed with audit
  records and the live representation is removed.
- An in-flight committed loot transaction wins before cleanup. A request that
  arrives after expiry receives a stable corpse-expired result.

No Zone or Layer identity is stored because those systems do not exist.

## NPC Corpse Lifecycle

Normal NPC corpses are live SimulationWorker state by default:

- Default lifetime is approximately two minutes.
- They do not need to survive a SimulationWorker restart.
- Unclaimed live loot may disappear on restart.
- Loot materialized into persistent player custody uses an idempotent grant id so
  a retry cannot duplicate it.

NPC content definitions may override corpse lifetime and persistence. Bosses may
use durable corpse custody and restart restoration. Persistent NPC corpse
behavior reuses the player-corpse transaction and expiry foundation without
changing the topology model.

## Corpse Looting And Bag Swaps

Inspecting a corpse is read-only and does not acquire a long-lived database lock.
Each mutation is a short transaction.

### Item Or Partial-Stack Loot

1. Unity sends a loot intent to the assigned SimulationWorker.
2. SimulationWorker validates connection, active session, corpse identity,
   proximity, and the one-active-interaction rule.
3. The worker calls AuthService with an idempotent operation id and expected item
   state.
4. AuthService locks the corpse and target item, then rereads authoritative
   state.
5. The transaction validates destination slots, stack compatibility, policy,
   prospective weight, and the 140 percent cap.
6. The item or requested quantity moves atomically.
7. A concurrent loser receives a stable stale, unavailable, or quantity-changed
   result and refreshes its view.

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
  -> authenticated AuthService item transaction
  -> PostgreSQL commit
  -> authoritative inventory and carry-state revision
  -> SimulationWorker encumbrance update
  -> Unity result and state update
```

SimulationWorker authenticates with its logical worker id and runtime id.
AuthService validates the exact active simulation session, character, worker,
runtime, and Shard before accepting an in-world command.

Bank and Recovery Storage access additionally require a city-service validation
from SimulationWorker. Secure Container operations are allowed in the world but
remain category, slot, policy, weight, and session validated.

When no simulation session is active, account-authenticated APIs may perform
appropriate non-world mutations without inventing a live SimulationWorker.

## Transactional Invariants

The implementation must enforce all of the following:

1. Every extant item instance has exactly one current custody assignment.
2. One slot or equipment assignment contains at most one item instance.
3. One item instance cannot be in a slot and equipment simultaneously.
4. Quantity is positive and within the definition stack limit.
5. Stack merges preserve total quantity and identical policy state.
6. A Bag with child items can only occupy a valid Bag slot.
7. An ordinary slot may contain a Bag only when its child container is empty.
8. Secure Container and specialized Bag eligibility is server validated.
9. Carried weight never exceeds 140 percent after a weight-increasing action.
10. Death partition, loot, Bag swap, policy consumption, and recovery delivery
    either commit completely or leave all prior state unchanged.
11. Operation ids are idempotent and cannot be reused with a different payload.
12. Character, Bag, corpse, container, and item locks use one stable ordering.
13. Corpse expiry and loot cannot both claim or destroy the same quantity.
14. Account-tier reduction cannot lose Secure Container contents.
15. Redis state can never override committed PostgreSQL custody.

## Deferred Balance And Content Values

The architecture does not depend on locking these values now:

- Exact permanent inventory slot count around the current target of 20.
- Exact bank base capacity around the current target of 40.
- Bag slot counts and carry bonuses.
- Secure Container upgrade prices and progression sources.
- Bank unlock sizes and sources.
- Recovery expiry duration.
- Which non-weapon definitions are Secure Container eligible.
- Insurance price and NPC availability.
- Corpse presentation art and interaction duration.
- NPC corpse lifetime overrides.

These remain content or balance data and must not be embedded in transaction
control flow.

## Explicitly Not Implemented Yet

This document is a locked design target, not a feature claim. The current
repository does not yet implement item definitions, item instances, inventory,
equipment, Bags, Secure Container, bank, Recovery Storage, carry weight,
encumbrance, insurance, death partition, persistent corpses, or corpse looting.


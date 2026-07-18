# Inventory And Death Loot Design

Last updated: 2026-07-18

Status: Locked design target; Phases 1 through 15 content, authoring, schema,
character bootstrap, authoritative reads, policy lifecycle, internal durable
transaction kernel, offline account APIs, carry-state delivery, and shared
encumbrance, realtime item mutation, Unity inventory foundation, death
partition, durable player-corpse custody, concurrent corpse looting, shared
world actors, authoritative interaction, insurance NPC lifecycle, quest item
  grant lifecycle, content-controlled Mob corpse variants, and operational
  hardening implemented

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
| Recovery Storage | A per-character, system-write-only delivery queue accessible from every major city |
| Corpse | A bidirectional loot container backed by durable custody for players and selected persistent Mobs |
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

- Unitless integer weight.
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

### Catalog Authoring Workflow

Phase 2 provides a custom Unity Editor window for item authoring. It operates on
the canonical authoring JSON under `WorldData/Authoring/Items` and invokes the
same deterministic catalog compiler used by command-line verification and CI.

The Editor is a content-authoring interface, not an item authority. Unity assets
must not become a second catalog, and backend compilation must not require Unity.
The first Editor version supports creation, duplication, editing, validation,
and baking. It does not permit destructive removal or id reuse for definitions
that already exist in the baked catalog.

### Client Presentation And Catalog Caching

Item data is split by responsibility:

- WorldData gameplay definitions own category, tags, unitless weight, stack
  rules, equipment compatibility, location eligibility, policies, and Bag
  layout.
- Unity client presentation owns icons, localization keys, visual prefabs, and
  other non-authoritative presentation metadata.
- AuthService owns durable instance state such as identity, quantity, custody,
  policy state, and revision.

The client presentation catalog is keyed by stable item definition id and is
bundled with the MVP Unity client. It records the WorldData catalog revision it
was baked against plus its own deterministic presentation revision. An icon-only
change advances the presentation revision without changing gameplay structural
fingerprints. Unity loads both local catalogs once into a definition-id lookup
and reuses the result across scenes and inventory refreshes.

Server snapshots and mutation results send definition ids and changed instance
state. They do not repeatedly send icons, complete definitions, or Unity asset
references. AuthService never serves image bytes as part of inventory state.

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

Carry weight uses unitless, non-negative integers. Physical measurement units,
decimal weight values, and floating-point comparisons are not part of the
authoritative model. The baseline balance scale is:

- One ammunition unit has weight `1`.
- A pistol has weight `10`.
- A character has base carry capacity `200`.

The development catalog uses the same scale. Its representative training rifle
has weight `25`, and its Field Pack grants a `50` carry-capacity bonus.

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
- Vendor, auction, mail, trade escrow, and other non-carried future custody.

Carry capacity is primarily character-based. The base character capacity is
`200`. An equipped Bag may add a bonus. Only the currently equipped Bag grants
its carry-capacity bonus.

### Encumbrance Curve

- At or below 100 percent capacity, movement uses 100 percent of base speed and
  sprint remains available.
- Above 100 percent, sprint is disabled.
- From 100 to 140 percent, movement speed decreases linearly.
- At 140 percent, movement uses 20 percent of base speed.
- Exactly 140 percent is allowed.
- No action may increase carried weight beyond 140 percent.
- An involuntary capacity loss during death may leave retained Secure Container
  weight above 140 percent. Death still commits. Until the character returns
  within the hard cap, only operations that do not increase carried weight or
  worsen the exact load ratio may commit.

With base capacity `200`, normal capacity ends at carried weight `200` and the
140 percent hard cap is carried weight `280`.

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
different Bag removes the Bag root's own weight, activates its carried contents,
and changes capacity in the same transaction. A structural content change that
could create an over-cap state requires an explicit migration.

## Inventory UI Layout

The Phase 9 Unity inventory presentation uses a stable three-area layout:

```text
+--------------------------+------------------------------------------+
| Character equipment      | Context container                       |
| Left side                | Upper right                              |
|                          | Bank, corpse, Recovery Storage, or       |
|                          | another opened loot or storage container |
|                          +------------------------------------------+
|                          | Character inventory                      |
|                          | Lower right                              |
|                          | Permanent slots, equipped Bag contents,  |
|                          | and Secure Container                     |
+--------------------------+------------------------------------------+
```

- Character equipment remains on the left.
- The right side is split vertically.
- Character-owned inventory remains in the lower-right area.
- The upper-right area presents the currently relevant external or contextual
  container, including bank, corpse, Recovery Storage, and world loot
  containers.
- The lower-right character area remains visible while another container is
  open so authoritative transfers have a clear source and destination.

This is a presentation contract only. Server authority, revisions, slot rules,
and transaction behavior do not depend on screen layout.

The implemented Phase 9 client keeps this layout in replaceable uGUI while its
catalog cache, immutable snapshots, revision coherence, operation journal,
structured errors, and refresh orchestration live in a persistent controller.
Item transfers use typed reusable drag sources and targets. Item clicks select
only split and destruction actions, and dropping any Recovery item withdraws its
complete delivery atomically. Bank and Recovery Storage may be inspected globally
by the owning account, but their mutations still require SimulationWorker's live
access evaluation. Local Development can explicitly evaluate Bank and Recovery
as globally accessible for testing. Production continues to evaluate authored
major-city service points, and the Development option never includes insurance
access or bypasses AuthService authority.
The corpse context adapter exposes durable corpse custody as a bidirectional
container without changing custody optimistically. Carried items may be
deposited into corpse slots, and corpse items may be looted into carried slots.
Bank and Recovery Storage never participate in a corpse transfer.

The maintained temporary presentation has three explicit view modes. `B` opens
only character storage, `C` opens equipment together with character storage, and
`I` retains the complete Development view with equipment, contextual Bank or
Recovery Storage, and character storage until those contextual services receive
their permanent world interaction UI. Equipment, context, and character storage
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
Any complete Bag aggregate transfer that changes the owning character evaluates
the Bag and every child. An active protected or insured policy on any aggregate
member rejects the transfer atomically.
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

An equipped Bag capacity bonus is removed when the Bag leaves equipment. If
retained Secure Container contents then place the character above 140 percent,
the death transaction still commits because rejecting death would violate the
authoritative event and custody partition. The item transaction kernel accepts
only non-worsening remediation from that state and rejects any added weight or
worse load ratio. Live combat activation must resolve the dead character's
respawn and carry-state transition before returning it to movement simulation.

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

Phase 14 implements this boundary. Live Mob contents never enter PostgreSQL.
The worker derives each loot entry and grant id from one authoritative death
event and materializes a claimed whole item through the exact-session item
transaction boundary. Selected durable Mobs use the same durable corpse record,
three-section custody, restoration, interaction, and expiry paths as players.
The lifecycle input contains already resolved loot seeds. Combat, damage, death
detection, loot-table generation, and respawn remain separate producers.

## Corpse Container Transfers And Bag Swaps

Inspecting a corpse is read-only and does not acquire a long-lived database lock.
Each mutation is a short transaction.

### Item And Partial-Stack Transfers

1. Unity sends a loot, deposit, or internal corpse-move intent to the assigned
   SimulationWorker.
2. SimulationWorker validates connection, active session, corpse identity,
   proximity, and the one-active-interaction rule.
3. The worker calls AuthService with an idempotent operation id and expected item
   state.
4. AuthService locks the character, corpse, source item, optional occupied
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

Phase 12 adds the generic NPC and world-interaction session that later service
capabilities use. Insurance, bank, Recovery Storage, vendor, quest, and crafting
handlers must reuse its authoritative target, range, line-of-sight, revision,
and session checks instead of adding capability-specific target authority. The
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
4. Quantity is positive and within the definition stack limit.
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
- Mob corpse lifetime overrides.

These remain content or balance data and must not be embedded in transaction
control flow.

## Explicitly Not Implemented Yet

This document is primarily a locked design target, not a complete feature
claim. Phases 1 through 15 now implement the neutral catalog, structural
fingerprints, strict validation, pure rules, Unity authoring, transactional
PostgreSQL definition mirror, constrained custody schema, canonical equipment
slots, account Secure Container entitlement foundation, and complete empty item
state for every active character. AuthService also exposes authenticated,
read-only current-catalog and owned-character inventory snapshots with stable
definition references and active policy summaries. Its internal transaction
kernel now creates and mutates durable item instances atomically, owns canonical
idempotency and lock order, maintains item, container, Bag, character, carry, and
entitlement revisions, and appends relational audit changes.

Phase 6 also implements pure policy capability evaluation, system-only insurance
application and removal, exact quest-grant cleanup and reaccept, conditionally
cached catalog responses, focused bank and Recovery reads, and offline account
mutation APIs. Those account mutations reject active simulation ownership under
the same character row lock used by session admission.

Phase 7 carries each admission-fenced weight, capacity, and monotonic item-state
revision into the active SimulationWorker session and Unity movement state. A
session heartbeat applies later committed revisions monotonically. SimulationWorker
and Unity use the same GameSimulation encumbrance rules for sprint eligibility
and movement speed, while AuthService remains the durable item and hard-cap
authority.

Phase 8 adds the live SimulationWorker item-mutation path over the same durable
kernel. A versioned reliable intent is bound to the exact account, character,
simulation session, worker runtime, assignment, and Shard. The worker validates
its authoritative position against configured service points, while AuthService
revalidates the complete live authority inside the mutation transaction. Bank
and Recovery Storage require their matching live access. Secure Container
operations do not require city access. Only a committed result can advance the
worker and Unity carry tuple.

Phase 9 adds a persistent Unity catalog, complete and focused snapshots,
monotonic revision coherence, operation-id journaling, authoritative refresh,
reconnect restoration, structured errors, and temporary uGUI presentation. The
client uses local definition presentation and disables obvious invalid targets,
but it neither owns custody nor applies optimistic item changes.

Phase 10 adds service-authenticated and system death-event processing over the
same durable kernel. PostgreSQL now owns five-minute player corpse identity,
three real item-custody sections, non-interactive snapshots, Recovery delivery
partitioning, and idempotent expiry destruction. SimulationWorker restores only
unexpired rows for its exact runtime and Shard, using the database-time deadline
and generic presentation key. Empty player corpses remain through that deadline.

Phase 11 adds exact-session corpse reads and bidirectional container mutations
over that durable custody.
SimulationWorker owns bounded presentation, one active view per player,
three-dimensional proximity and lifetime validation, and viewer fanout. Full and
partial transfers, internal corpse rearrangement, ordinary occupied-slot swaps,
and atomic Bag aggregate swaps
reuse the AuthService transaction kernel with targeted revisions and stable lock
ordering. Unity assembles protocol-v11 presence, complete snapshots, slot tags,
and committed deltas without applying optimistic custody. The generic capsule
and uGUI are replaceable presentation.

Phase 13 adds the live insurance and quest item-lifecycle bridge on the Phase 12
NPC interaction. Insurance pricing is server-owned and charged atomically with
the policy. Explicit removal preserves item identity. Quest accept, abandon,
and reaccept use one exact protected grant lineage. Safe item and Recovery
source labels expose lifecycle meaning without exposing raw lineage ids.

Phase 14 adds content-controlled live or durable Mob corpse variants. Normal
Mob contents are worker memory and disappear on restart. Deterministic grant
ids make a committed player claim retry-safe. Selected bosses reuse durable
corpse custody and expiry without creating a second corpse architecture.

Phase 15 bounds the existing transaction kernel with statement, lock, canonical
command, and HTTP body limits. Identity-free metrics cover transaction latency,
lock waits, conflicts, stale revisions, death partition, policy actions, corpse
counts, Recovery backlog, and cleanup. System-authority maintenance expires
Recovery deliveries transactionally, retains durable destruction evidence,
removes only empty closed corpses after policy, and deletes only unreferenced old
operation rows. Load tests preserve custody across concurrent claims and repeated
Bag swaps. No new item owner, location, or authority is introduced.

No vendor, gathering, quest progression, combat death producer, damage system,
or Mob loot-table producer calls these boundaries yet. Final corpse art remains
a later phase. Phase 13 routes insurance and quest item
lifecycle actions through the Phase 12 world actor, shared crosshair targeting,
one-active-interaction lease, and authoritative capability dispatch. AuthService
owns the atomic insurance charge, policy mutation, exact quest grant lineage,
and audit. No Zone or Layer identity was introduced.

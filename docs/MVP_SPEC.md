# Shooter MMO MVP Spec

Last updated: 2026-07-15

## Purpose

This document defines the current MVP direction for Shooter MMO.

The spec is a working agreement, not a permanent design lock. Decisions can
change when implementation, playtesting, or better design ideas expose a better
path. The goal is to keep the first build focused enough to become playable
without losing the long-term game identity.

Detailed planned item, inventory, carry-weight, corpse, insurance, and recovery
rules are defined in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md). The
dependency-ordered delivery plan is
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

## Core MVP Goal

The MVP should prove the core loop:

1. Player creates or selects a character.
2. Player selects and joins a shard running the shared World content.
3. Player spawns in a safe city.
4. Player prepares inventory, equipment, Bag, Secure Container, and bank.
5. Player leaves the city into an unsafe open-world area.
6. Player gathers or loots a resource.
7. Player sells to a simple NPC vendor or retains the item for later crafting.
8. Player fights a simple mob or another player.
9. Player death applies transactional protection and corpse-loot rules.
10. Player returns to a city to claim Recovery Storage, bank, sell, equip, and
    prepare again.

The MVP should avoid becoming a large feature collection before this loop works.

## Account And Character Rules

- Accounts support multiple characters from the beginning.
- Maximum characters per account in the MVP: 5.
- Character data is persistent and stored in PostgreSQL.
- Each character owns its inventory, equipment, Bag custody, bank, Secure
  Container contents, Recovery Storage, currency, profession progress, quest
  state, and reputation state.
- The account owns the entitlement that selects Secure Container name, tier, and
  slot capacity for its characters.
- Bank contents and capacity state are per character, not account-wide.
- One account and one character may have only one active simulation session at a
  time across every Fleet and Shard.
- Shard switching is allowed only after the character has fully left its current
  simulation session.

## World Model

- The game is one large open world, not a set of extraction instances.
- `World` means shared map, collision, and game content data. It is not a server,
  process, region, or player-selectable runtime.
- A `Shard` is a player-selectable copy of the shared World simulation and may
  have its own rule set.
- A `Fleet` groups regional or operational compute. A `Node` is one machine or
  container host inside a Fleet.
- A `SimulationWorker` is one headless authoritative process. Its active
  `SimulationAssignment` determines which Shard it simulates.
- The current safe scale unit is one active SimulationWorker per Shard and one
  active Shard per SimulationWorker.
- Accounts, characters, persistent items, inventory, progression, and the
  future economy are global across every Fleet and Shard.
- Shard-specific live state includes movement, combat, mobs, interactions, and
  live corpse presentation.
- Durable player corpse custody remains global PostgreSQL data with a Shard
  reference used for restoration by the assigned SimulationWorker.
- There are no Realms. Topological Zones and Layers are future scaling systems
  and are not implemented or faked by inventory or corpse ownership.

## Gameplay Rule Areas

Gameplay rule areas describe content rules inside the authored World. They are
not the future topological `Zone` scaling unit and must not be represented as
Zones, SimulationAssignments, or worker ownership boundaries.

Implementation of gameplay rule areas is deferred until the project has a
larger authored map with meaningful locations and boundaries. The current small
movement test map does not define these areas.

When the map is ready, the first playable version should keep rule areas simple:

- Safe City Rule Area.
- Open Risk Rule Area.

Civilized and wilderness rule areas can be split into separate policies later.
For the first implementation, everything outside the safe city should be treated
as unsafe.

### Safe City Rule Area

- No PvP.
- Instant logout.
- NPC vendor, bank, Recovery Storage, and insurance NPC access.
- Equipment and inventory management.
- Future location for crafting stations, quest NPCs, and social services.

### Open Risk Rule Area

- PvP is allowed.
- Simple mobs can exist.
- Gathering and loot containers can exist.
- The character can manage carried inventory and eligible Secure Container
  items, subject to server-authoritative action and policy rules.
- Player death creates durable corpse custody and applies the current protection
  rules.
- Logout leaves the character in the world for a risk timer.

## Logout And Disconnect Rules

Logout behavior will depend on the character's current gameplay rule area.

- Safe City Rule Area: instant logout.
- Open Risk Rule Area in MVP: the character body remains in the Shard simulation
  for 5 minutes.
- Future Civilized Rule Area: the character body remains for about 30 seconds.
- Future Wilderness Rule Area: the character body remains for about 5 minutes.

If the player reconnects while the character body is still active, the player
should resume control of that same character body.

Combat should reset or extend the logout timer so players cannot use logout or
disconnect to avoid danger.

## Item And Inventory Rules

- Inventory is slot-based, not grid-based.
- One non-stackable item occupies one slot.
- A compatible stack occupies one slot up to the definition's server-controlled
  stack maximum.
- Every character has a permanent general inventory independent of the equipped
  Bag. The initial target is approximately 20 slots.
- Weapons use normal general slots when not equipped.
- There are no weapon attachments in the current design.
- Item definitions provide one primary category, zero or more tags, unitless
  integer weight, stack rules, equipment compatibility, location eligibility,
  and optional default policy behavior.
- The first specialized Bag slot tags are medical, material, and ammunition.
- Matching items may use specialized or general Bag slots.
- Every item instance has exactly one current custody assignment.
- Item quantity, policy, ownership, slot assignment, and weight are always
  server-authoritative.
- Item icons, localization keys, and visual prefabs are client presentation
  assets keyed by stable definition id. They are bundled with the MVP client and
  are not transferred with each inventory response.
- Unity loads the gameplay and presentation catalogs once per matching revision.
  Server inventory state carries definition ids and instance state rather than
  repeated definitions or image data.
- Persistent item ownership and every important transfer are transactional in
  PostgreSQL.
- Redis is never the source of truth for persistent item custody.

## Equipment And Bags

Required initial equipment slots:

- Head.
- Body armor.
- Primary weapon.
- Secondary weapon.
- Tool.
- Ring 1.
- Ring 2.
- Bag.

Equipment is item ownership plus a mutually exclusive slot assignment. An
equipped item does not also occupy permanent inventory.

A physical Bag may provide general slots, specialized slots, and a carry-
capacity bonus. A Bag instance owns its contents.

- An empty Bag acts as an ordinary one-slot item and may use compatible general
  inventory, Bag, bank, recovery, and future economy locations.
- A Bag with contents may move only between valid Bag slots through an atomic
  aggregate swap or through a server-owned death partition that first removes
  every child item.
- A non-empty Bag cannot be stored inside permanent inventory, another Bag,
  bank, trade, auction, mail, or vendor storage.
- Active container nesting and cycles are forbidden.
- A Bag policy protects only the Bag item. Every child item is evaluated
  independently.

## Character Bank

- Bank ownership is per character.
- Bank storage is slot-based and contributes no carry weight.
- The initial target is approximately 40 base slots.
- Further slots can be unlocked by later progression or entitlement systems.
- Only empty Bags may be stored in bank.
- Bank access requires a major-city service.

## Secure Container

- Secure Container contents belong to the character.
- The account-selected tier defines name and slot capacity for its characters.
- The base tier starts with four slots.
- Secure Container is not a physical Bag item and cannot be dropped, traded, or
  looted.
- Its contents contribute to carried weight.
- Eligible items may be moved into and out of Secure Container while in the
  world.
- Eligibility is definition-driven. Weapons are not allowed.
- A move is rejected when slots, policy, stack, or the 140 percent weight cap
  would be violated.
- A corpse exposes only a non-interactive snapshot of Secure Container name and
  tier. It never exposes the contents.
- If an account tier loses slots, items in removed slots move transactionally
  and deterministically to Recovery Storage. They are never deleted.

## Recovery Storage

- Recovery Storage is one global per-character queue accessible from every major
  city.
- Players can withdraw but cannot deposit.
- System deliveries can always be appended, so death processing cannot fail due
  to storage capacity.
- Recovery contents contribute no carry weight.
- Withdrawals target inventory or bank and validate slot and weight rules.
- Every delivery records source and source event id.
- Initial sources include death protection, insurance, protected Bag,
  insufficient respawn capacity, secure-tier reduction, and future restoration.

## Carry Weight And Encumbrance

Carried weight includes permanent inventory, equipment, equipped Bag, Bag
contents, carried empty Bags, Secure Container contents, and full stack
quantities. Bank, Recovery Storage, corpse, and future non-carried economy
custody do not count.

Weight is a unitless non-negative integer gameplay value. The baseline scale is
ammunition `1`, pistol `10`, and base character carry capacity `200`. The
equipped Bag may add a capacity bonus.

- At or below 100 percent, movement uses base speed and sprint is available.
- Above 100 percent, sprint is disabled.
- Movement speed decreases linearly from 100 percent base speed at 100 percent
  load to 20 percent base speed at 140 percent load.
- Exactly 140 percent is allowed.
- No action may increase carried weight beyond 140 percent.
- Weight and capacity use unitless integers and exact integer comparisons.
- Base capacity `200` reaches the 140 percent hard cap at weight `280`.
- Structural content changes that could create an invalid over-cap state require
  an explicit data migration.

## Inventory UI Layout

- Character equipment occupies the left side.
- The right side is split vertically.
- Character inventory occupies the lower-right area and includes permanent
  inventory, equipped Bag contents, and Secure Container access.
- The upper-right area presents another active container such as bank, corpse,
  Recovery Storage, or a world loot container.
- Character inventory remains visible while another container is open so item
  transfers have clear source and destination areas.

This layout does not grant Unity authority over item or slot rules.

## Item Policies

Category and policy are separate. Policies are server-owned item-instance state.

### Protected On Death

- The actual item moves to Recovery Storage at death unless it was already in
  Secure Container.
- It cannot be traded, auctioned, or sold to a vendor.
- It is hidden from looters.
- Protected quest items cannot be destroyed by the player.
- Abandoning the owning quest removes associated quest items transactionally,
  and reaccepting the quest may grant them again.
- A non-quest protected item may be explicitly destroyed only if its definition
  permits it.

### Insured

- Insurance is one-death protection.
- The actual item moves to Recovery Storage when insurance protects it.
- The insurance policy is consumed by that death.
- A non-interactive insured snapshot may remain on the corpse to show equipment
  the character carried.
- An insured item cannot be traded, auctioned, or sold to a vendor.
- The player must remove insurance through the insurance NPC before those
  actions become legal again.
- Initial insurance targets non-stackable equipment.

## Combat Direction

- Combat is third-person and projectile based.
- SimulationWorker is authoritative for combat outcomes.
- The client sends fire intent.
- The server validates weapon state, ammo, cooldown, position, direction, and
  hit results.
- MVP combat should avoid advanced realism.

The first combat implementation should include:

- One simple ranged weapon.
- Projectile spawning and travel.
- Basic hit detection.
- Basic health and damage.
- Reload or ammo consumption.
- Authoritative death event generation.

Advanced ballistics, limb damage, armor penetration, and complex recoil should
wait until the basic combat loop works.

## Mob MVP

The first mob needs enough behavior to test combat and loot:

- Spawn.
- Idle or patrol.
- Detect nearby players.
- Move toward the target.
- Attack.
- Take damage.
- Die.
- Drop simple loot.
- Respawn after a timer.

Normal NPC corpses default to approximately two minutes of live
SimulationWorker state and may disappear on restart. NPC content can override
lifetime and persistence. Bosses may use the durable corpse path.

## Economy MVP

The first economy should be simple and NPC-driven:

- One NPC vendor.
- Player can sell basic loot or gathered resources.
- Player can buy basic supplies.
- Currency is stored persistently and remains with the character on death.
- One insurance NPC can grant and remove one-death insurance later in the
  death-loot milestone.

Auction house, direct player trading, regional markets, and advanced crafting
should wait until core item and economy transactions are stable.

## Gathering MVP

Recommended first gathering loop:

- Equip a tool in the tool slot.
- Interact with a resource node in the Open Risk Rule Area.
- SimulationWorker validates the required tool and live interaction.
- AuthService grants a resource item transactionally into a compatible slot when
  capacity and weight allow it.
- Resource can be sold to the NPC vendor.

## Death And Loot Rules

Player death is an idempotent transaction:

- Currency remains with the character.
- Secure Container contents remain in place.
- Protected-on-death items move to Recovery Storage.
- Insured items move to Recovery Storage and consume insurance.
- Remaining permanent inventory, equipment, Bag, and Bag contents become
  lootable corpse custody.
- The corpse has separate general inventory, equipment, and Bag sections.
- Multiple players, including the dead player, may loot the corpse concurrently.
- One player may have only one active loot interaction at a time.
- Partial-stack looting is supported.
- Bag swaps are atomic and lock both Bag aggregates.

Player corpse custody persists in PostgreSQL for an absolute five-minute lifetime
even when empty. SimulationWorker restores unexpired player corpses after a
restart, optionally with a generic loot-crate presentation. At expiry, remaining
loot is destroyed with audit records.

## Backend And Service Scope

The intended architecture remains:

- Unity client.
- ASP.NET Core AuthService for global durable services and HTTP APIs.
- Headless .NET SimulationWorker for authoritative Shard simulation.
- LiteNetLib UDP for gameplay networking.
- PostgreSQL as the persistent source of truth.
- Redis only for temporary or operational state where it has a clear benefit.

Planned item service split:

- AuthService owns item definitions mirrored from shared content, item
  instances, policies, slot and equipment assignments, bank, Secure Container,
  Recovery Storage, durable player corpse custody, audit, and all durable item
  transactions.
- SimulationWorker owns live inventory and corpse interaction validation,
  proximity, combat, death-event production, active corpse presentation,
  one-active-loot-interaction enforcement, and authoritative encumbrance.
- While a character is active, SimulationWorker requests durable mutations over
  a service-authenticated AuthService boundary fenced to exact session, worker,
  runtime, character, and Shard.
- SimulationWorker never writes item tables directly.
- Unity sends intents and displays committed state.

Current implementation note:

- AuthService currently stores identity, topology, exact-runtime join tickets,
  and global simulation-session leases in PostgreSQL.
- SimulationWorker currently owns movement, interest management, connection
  quotas, and collision against checksummed WorldData chunks.
- Unity currently owns input, prediction, reconciliation, interpolation,
  presentation, and temporary UI.
- WorldData owns the deterministic Phase 1 item catalog and pure stack, slot,
  equipment, Secure Container, Bag, integer-weight, and encumbrance rules.
- Phase 2 adds an Editor-only Unity catalog window and a bundled client
  presentation catalog with exact gameplay-revision pairing and local caching.
  It does not make Unity authoritative for item rules.
- PostgreSQL item mirrors, persistent item instances, inventory custody,
  equipment assignments, Bag instances, bank, Secure Container contents,
  Recovery Storage, carry-state integration, policy lifecycle, combat, mobs,
  death, corpses, and loot are not implemented.

## Persistence Principles

- PostgreSQL is the source of truth for persistent gameplay data.
- Redis is only for fast, temporary, or lease-based state.
- Inventory, bank, Secure Container, Recovery Storage, equipment, Bag, death,
  corpse, loot, vendor, insurance, trade, and auction mutations must prevent
  duplication and loss.
- Every extant item has exactly one current custody assignment.
- Important mutations use idempotent operation ids, stable lock order, expected
  revisions, complete rollback, and audit records.
- Database identifiers use snake_case.
- JSON over the wire uses camelCase.

## Suggested Implementation Phases

### Phase 1: Project Foundation

Status: Completed

- Repository structure, backend solution, local infrastructure, configuration,
  and canonical topology.

### Phase 2: Account, Character, And Shard Join

Status: Completed

- Registration, login, characters, placement, exact-runtime tickets, and global
  one-active-session rules.

### Phase 3: Unity Connection And Movement

Status: Completed

- Unity API and UDP flow, entity lifecycle, spatial interest, shared
  authoritative movement, prediction, reconciliation, interpolation, and
  checksummed collision.

### Phase 3.5: SimulationWorker Performance Baseline

Status: Completed

- External headless stress clients, active bots, optimized visibility and packet
  reuse, bounded backpressure, process sampling, and repeatable baseline tools.

### Phase 4: Items, Inventory, Equipment, And Carry Weight

Status: In progress, item-plan Phases 1 and 2 complete

- Deterministic item catalog, categories, tags, equipment compatibility, Bag
  layouts, Secure Container tiers, structural fingerprints, and pure rules are
  complete.
- Unity catalog authoring, deterministic baking, and separately revisioned
  client icon and presentation mapping are complete.
- PostgreSQL definition mirror, item instances, stacks, and policy state remain
  planned.
- Slot-based permanent inventory.
- Equipment and Bag aggregates.
- Per-character bank.
- Per-character Secure Container with account-selected tier.
- Recovery Storage.
- Transaction kernel, idempotency, revisions, audit, and PostgreSQL race tests.
- Integer carry weight, 140 percent hard cap, and shared encumbrance.
- Account and in-world service boundaries.
- Initial Unity inventory presentation.

The complete subphase order and exit gates are defined in
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

### Phase 5: Vendor And Gathering

- Add one NPC vendor.
- Add one resource node type.
- Add one tool item.
- Gather into inventory through the item transaction service.
- Sell a gathered resource to the vendor.

### Phase 6: Projectile Combat And Simple Mob

- Add one simple ranged weapon.
- Add projectile simulation.
- Add health and damage.
- Add authoritative death event generation.
- Add one simple mob and live NPC loot.

### Phase 7: Gameplay Rule Areas And Presence Lifecycle

Status: Deferred until a larger authored map exists

- Define content-authored Safe City and Open Risk rule areas without creating
  topological Zones or Layers.
- Apply instant logout in the Safe City Rule Area.
- Keep the character body active for 5 minutes after disconnect in the Open Risk
  Rule Area.
- Support reconnect to the same active simulation entity and state.
- Keep connection, simulation-session, entity, logout-body, and corpse lifetime
  as explicit separate concepts.

### Phase 8: Death, Recovery, And Corpse Looting

- Partition player items transactionally from an authoritative death event.
- Apply protected-on-death and one-death insurance policies.
- Create Recovery Storage deliveries.
- Create durable five-minute player corpse custody and presentation snapshots.
- Restore player corpses after SimulationWorker restart.
- Allow concurrent item and partial-stack looting.
- Support atomic Bag swaps.
- Add configurable live or durable NPC corpse behavior.
- Add insurance NPC lifecycle.

## Deferred Features

These should not block the first playable MVP:

- Civilized and wilderness gameplay rule-area split.
- Topological Zones, cross-zone handoff, and Layers.
- Criminal reputation and bounty hunting.
- Guilds and SocialService chat.
- Auction house and direct player trading.
- Complex crafting and multiple professions.
- Advanced mobs and bosses beyond the first persistence test.
- Complex quests.
- Insured stack quantities.
- Detailed armor and penetration systems.

## Current Open Design Questions

- Should character deletion be available in the MVP?
- Should Shard switching have a cooldown after logout?
- Should resource nodes be per-Shard live state only, or persisted with respawn
  timestamps?
- Should the first tool be a pickaxe, axe, or generic starter tool?
- Should the first mob be hostile by default or only aggressive when attacked?
- Which non-weapon definitions are initially eligible for Secure Container?
- What progression unlocks additional bank slots?
- What expiry policy should Recovery Storage use after the initial unlimited
  system-delivery implementation is stable?

# Shooter MMO MVP Spec

Last updated: 2026-07-18

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
The implemented scalable world-actor, NPC, Mob, spawn-authoring, and interaction
foundation and its remaining design contract are defined in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md).

## Core MVP Goal

The MVP should prove the core loop:

1. Player creates or selects a character.
2. Player selects and joins a shard running the shared World content.
3. Player spawns in a safe city.
4. Player prepares inventory, equipment, Bag, Secure Container, and bank.
5. Player leaves the city into an unsafe open-world area.
6. Player gathers or loots a resource.
7. Player sells to a simple NPC vendor or retains the item for later crafting.
8. Player fights a simple Mob or another player.
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
- Shard-specific live state includes movement, combat, NPCs, Mobs, interactions,
  and live corpse presentation.
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
- NPC interaction uses the crosshair and `E`, with SimulationWorker validating
  target identity, range, line of sight, state, and capability.

### Open Risk Rule Area

- PvP is allowed.
- Simple Mobs can exist.
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

Carried weight includes permanent inventory, equipped Bag contents, carried empty
Bags, Secure Container contents, and full stack quantities. Items assigned to
equipment slots, including the equipped Bag item itself, do not count. The
equipped Bag's contents still count and its capacity bonus still applies. Bank,
Recovery Storage, corpse, and future non-carried economy custody do not count.

Weight is a unitless non-negative integer gameplay value. The baseline scale is
ammunition `1`, pistol `10`, and base character carry capacity `200`. The
equipped Bag may add a capacity bonus.

- At or below 100 percent, movement uses base speed and sprint is available.
- Above 100 percent, sprint is disabled.
- Movement speed decreases linearly from 100 percent base speed at 100 percent
  load to 20 percent base speed at 140 percent load.
- Exactly 140 percent is allowed.
- No action may increase carried weight beyond 140 percent.
- Authoritative death may involuntarily reduce capacity below retained Secure
  Container weight. Death still commits, after which only non-worsening
  remediation may proceed until the character returns within the hard cap.
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
- `B` opens character storage only. `C` opens equipment together with character
  storage. During Development, `I` retains the complete equipment, contextual
  storage, and character-storage view until Bank and Recovery receive permanent
  world interaction UI.
- Equipment, contextual storage, and character storage are separate fixed
  modules. Toggling one module never repositions or resizes another module.
- Dropping onto an occupied slot merges compatible stacks. Otherwise, compatible
  ordinary container items swap slots atomically after both opposite slot
  assignments and all authority rules pass. Non-empty Bag aggregates remain on
  their dedicated atomic transfer path.

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

## NPC And Mob Foundation

Item-plan Phase 12 establishes this implemented foundation before service NPCs
and the first combat Mob are implemented:

- `NPC` means a social or service actor. Dialogue, vendor, quest, crafting,
  insurance, trainer, bank, and Recovery Storage roles are freely composable
  capabilities.
- `Mob` means a combat actor with future AI, aggro, combat, loot, corpse, and
  respawn behavior.
- Actor kind is separate from faction and disposition. Guards are NPCs and all
  city NPCs are invulnerable in the first version.
- NPC and Mob definitions plus spawn content are canonical neutral WorldData.
  Unity provides visual authoring and presentation, not authority.
- Normal actors are reconstructed from WorldData after worker restart and do
  not require one PostgreSQL row per instance.
- NPCs are event-driven. Mobs use centrally scheduled dormant and active tiers.
- The player points the crosshair at an actor and presses `E`. The server owns
  the `3.0` metre start range, `3.5` metre maintain range, line of sight, target
  revision, and one-active-interaction rule.
- The one-active-interaction rule includes the existing corpse view, while many
  players may still interact with one target.

Phase 12 does not implement vendor transactions, quest progression, crafting,
combat, complete Mob AI, Mob loot, or Mob corpses.

## Mob MVP

The first Mob needs enough behavior to test combat and loot:

- Spawn.
- Idle or patrol.
- Detect nearby players.
- Move toward the target.
- Attack.
- Take damage.
- Die.
- Drop simple loot.
- Respawn after a timer.

Normal Mob corpses default to approximately two minutes of live
SimulationWorker state and may disappear on restart. Mob content can override
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
- Corpse sections are bidirectional containers for carried inventory items.
- Full-item and partial-stack transfers are supported in both directions.
- Compatible stacks with remaining capacity merge, while complete items that
  cannot merge atomically swap only when both original slots accept the
  opposite item.
- Items may move, split, merge, and swap between corpse slots and sections
  without changing character custody or carry state.
- Corpse equipment slots expose canonical equipment-slot ids and accept only
  compatible item definitions.
- Bank, Recovery Storage, and ordinary equipped items cannot be deposited into a
  corpse.
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
- SimulationWorker owns live world actors, spawn lifecycle, inventory and corpse
  interaction validation, proximity, combat, death-event production, active
  corpse presentation, one-active-interaction enforcement, and authoritative
  encumbrance when those planned systems are implemented.
- While a character is active, SimulationWorker requests durable mutations over
  a service-authenticated AuthService boundary fenced to exact session, worker,
  runtime, character, and Shard.
- SimulationWorker never writes item tables directly.
- Unity sends intents and displays committed state.

Current implementation note:

- AuthService currently stores identity, topology, exact-runtime join tickets,
  global simulation-session leases, the relational item catalog mirror, and
  complete empty per-character item-state and container identities in
  PostgreSQL.
- SimulationWorker currently owns movement, interest management, connection
  quotas, and collision against checksummed WorldData chunks.
- Unity currently owns input, prediction, reconciliation, interpolation,
  presentation, and temporary UI.
- WorldData owns the deterministic Phase 1 item catalog and pure stack, slot,
  equipment, Secure Container, Bag, integer-weight, and encumbrance rules.
- Phase 2 adds an Editor-only Unity catalog window and a bundled client
  presentation catalog with exact gameplay-revision pairing and local caching.
  It does not make Unity authoritative for item rules.
- Phase 3 adds the transactional PostgreSQL catalog mirror, constrained item,
  slot, container, equipment, policy, recovery, operation, and audit schema,
  canonical equipment-slot seed, active-character backfill, and atomic new
  character bootstrap.
- Phase 4 adds account-authenticated current-catalog and complete
  owned-character inventory reads. Queries use coherent PostgreSQL read-only
  snapshots and return definition ids instead of repeated definitions or client
  presentation data.
- Item-plan Phase 5 adds the internal AuthService transaction kernel. Its typed
  commands atomically create and mutate durable item instances, equipment, Bag
  aggregates, Recovery deliveries, Secure Container tiers, carried state,
  revisions, idempotency results, and audit rows under canonical PostgreSQL
  locks.
- Item-plan Phase 6 adds pure policy capability evaluation, auditable insurance
  removal and quest-grant cleanup, conditionally cached catalog reads, focused
  bank and Recovery reads, and offline-safe account mutation routes. Active
  simulation ownership is fenced under the same character row lock as session
  admission.
- Item-plan Phase 7 adds admission-fenced and heartbeat-refreshed carry state to
  SimulationWorker, protocol version `7`, and identical GameSimulation
  encumbrance behavior in authoritative movement and Unity prediction.
- Item-plan Phase 8 adds protocol version `8`, exact-session worker mutation,
  authoritative Bank and Recovery service access, world-available Secure
  Container operations, and committed carry propagation.
- Item-plan Phase 9 adds persistent Unity catalog and snapshot state, monotonic
  revision and operation handling, authoritative refresh, reconnect restoration,
  and the first temporary uGUI inventory loop.
- Item-plan Phase 10 adds idempotent player-death partition, Recovery delivery,
  durable five-minute player corpse custody and snapshots, exact worker restart
  restoration, and audited expiry cleanup.
- Item-plan Phase 11 adds protocol-v11 corpse presence and bidirectional
  interaction, exact proximity and lifetime validation, concurrent full and
  partial transfers, internal corpse rearrangement, typed equipment slots,
  ordinary occupied-slot swaps, atomic Bag aggregate swaps, committed viewer
  deltas, immutable Unity state, and a generic replaceable corpse presentation.
- Item-plan Phase 12 adds deterministic NPC, Mob, capability, and spawn content,
  visual Unity authoring, SimulationWorker actor runtime and interest
  integration, presentation-only actor prefabs, authoritative crosshair
  interaction, and a shared corpse interaction lease. The foundation is
  implemented.
- Gameplay-created item grants, insurance NPC pricing, authoritative combat
  death production, Mobs, final corpse art, and configurable Mob corpse
  persistence are not implemented. Insurance consumption is implemented only
  inside the durable death transaction.

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

Status: In progress, item-plan Phases 1 through 12 complete

- Deterministic item catalog, categories, tags, equipment compatibility, Bag
  layouts, Secure Container tiers, structural fingerprints, and pure rules are
  complete.
- Unity catalog authoring, deterministic baking, and separately revisioned
  client icon and presentation mapping are complete.
- The PostgreSQL definition mirror, exact custody schema, character item state,
  top-level empty containers, operation and audit foundation, and structural
  compatibility startup fence are complete.
- Read-only catalog and complete owned-character inventory snapshots plus
  test-only development fixtures are complete.
- The internal mutation transaction kernel, canonical idempotency, optimistic
  revisions, stable lock order, relational audit, PostgreSQL race tests, and
  authoritative carried-state recomputation are complete.
- Policy capability evaluation, protected and insured lifecycle records,
  insurance removal, exact quest-grant cleanup, offline account APIs, stable
  Problem Details, ETag catalog caching, and no-store character responses are
  complete.
- Shared carry state, exact-session join and heartbeat propagation,
  authoritative sprint and movement effects, Unity prediction, reconnect
  restoration, and protocol compatibility fencing are complete.
- The exact-session worker mutation boundary, service-authenticated durable
  authority, live bank and Recovery access, world-available Secure Container
  operations, idempotent reliable intents, and committed carry propagation are
  complete.
- Persistent Unity catalog and inventory state, complete and focused revision
  coherence, operation journaling, authoritative refresh, reconnect restoration,
  and the temporary three-area uGUI presentation are complete.
- Idempotent player-death partition, protected and effective insured Recovery
  delivery, durable three-section player corpses, presentation-only snapshots,
  exact worker and Shard restart restoration, empty-corpse lifetime, and audited
  absolute expiry are complete.
- Exact-session corpse reads and mutations, concurrent viewers, proximity and
  lifetime validation, bidirectional full and partial transfers, ordinary slot
  swaps, atomic Bag swaps, committed
  deltas, generic Unity presentation, and the reusable typed drag path are
  complete. Live death activation waits for the authoritative combat producer.
- Deterministic world-actor content, Actor Studio and Spawn Authoring, strict
  worker startup validation, reconstructable NPC and Mob runtime identity,
  reliable interest presence, central Mob scheduling, typed capability
  dispatch, authoritative interaction, shared corpse targeting, and permanent
  Unity state beneath temporary presentation are complete.

The complete subphase order and exit gates are defined in
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).
The next item-plan subphase is Phase 13 insurance and quest lifecycle behavior
on the shared actor and interaction foundation.

### Phase 5: Vendor And Gathering

- Build on the item-plan Phase 12 actor, capability, target, and interaction
  contracts.
- Add one NPC vendor capability handler.
- Add one resource node type.
- Add one tool item.
- Gather into inventory through the item transaction service.
- Sell a gathered resource to the vendor.

### Phase 6: Projectile Combat And Simple Mob

- Add one simple ranged weapon.
- Add projectile simulation.
- Add health and damage.
- Add authoritative death event generation.
- Add one simple Mob and live Mob loot.

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

Status: In progress, durable and interactive player-corpse foundation complete

- Completed item subphase: partition player items transactionally from a unique
  authoritative death event.
- Completed item subphase: apply protected-on-death and one-death insurance
  policies and create Recovery Storage deliveries.
- Completed item subphase: create durable five-minute player corpse custody and
  presentation snapshots.
- Completed item subphase: restore player corpses after SimulationWorker restart
  without resetting their database deadline.
- Completed item subphase: present nearby durable corpses, enforce proximity and
  lifetime, and support concurrent full and partial item looting.
- Completed item subphase: support atomic Bag aggregate swaps and committed
  deltas to all current viewers.
- Connect the prepared death boundary to the future authoritative combat event
  producer and replace the generic corpse presentation with final content.
- Add configurable live or durable Mob corpse behavior.
- Add insurance NPC lifecycle.

## Deferred Features

These should not block the first playable MVP:

- Civilized and wilderness gameplay rule-area split.
- Topological Zones, cross-zone handoff, and Layers.
- Criminal reputation and bounty hunting.
- Guilds and SocialService chat.
- Auction house and direct player trading.
- Complex crafting and multiple professions.
- Advanced Mobs and bosses beyond the first persistence test.
- Complex quests.
- Insured stack quantities.
- Detailed armor and penetration systems.

## Current Open Design Questions

- Should character deletion be available in the MVP?
- Should Shard switching have a cooldown after logout?
- Should resource nodes be per-Shard live state only, or persisted with respawn
  timestamps?
- Should the first tool be a pickaxe, axe, or generic starter tool?
- Should the first Mob be hostile by default or only aggressive when attacked?
- Which non-weapon definitions are initially eligible for Secure Container?
- What progression unlocks additional bank slots?
- What expiry policy should Recovery Storage use after the initial unlimited
  system-delivery implementation is stable?

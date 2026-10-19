# MMORPG MVP Spec

Last updated: 2026-10-19

## Purpose

This document defines the current MVP direction for MMORPG.

The spec is a working agreement, not a permanent design lock. Decisions can
change when implementation, playtesting, or better design ideas expose a better
path. The goal is to keep the first build focused enough to become playable
without losing the long-term game identity.

The item, inventory, carry-weight, corpse, insurance, and recovery rules are
defined in [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).
The implemented scalable world-actor, NPC, Mob, spawn-authoring, and interaction
foundation and its remaining design contract are defined in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md). Setting, movement,
camera, and combat decisions are defined in
[Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md).

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
- A Shard references exactly one World at a time, but the binding is not
  permanent. Operators may change it between worker process generations after
  the Shard is fully offline and all World-local runtime and durable corpse
  state is drained. A running Shard never hot-swaps its World.
- A `Fleet` groups regional or operational compute. A `Node` is one machine or
  container host inside a Fleet.
- A `SimulationWorker` is one headless authoritative process. Its active
  `SimulationAssignment` determines which Shard it simulates.
- The current safe scale unit is one active SimulationWorker per Shard and one
  active Shard per SimulationWorker.
- Accounts, characters, persistent items, inventory, currency, progression, and
  the future economy are global across every Fleet and Shard.
- Shard-specific live state includes movement, combat, NPCs, Mobs, resource node
  charges, interactions, and live corpse presentation.
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
- NPC interaction uses a pointer click that walks into range, with SimulationWorker validating
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

## Items And Inventory

The item, equipment, Bag, Bank, Secure Container, Recovery Storage, carry
weight, policy, death, and corpse rules are decided in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), and the
MVP uses them as they are:

- Slot-based inventory with stacks, a permanent character inventory, thirteen
  equipment slots with a stack-holding ammunition slot, and physical Bags with
  general and specialized slots.
- A per-character Bank, a protected Secure Container that works anywhere in the
  world, and the system-written Recovery Storage. The Bank and Recovery Storage
  are reached through NPCs that offer them.
- Unitless integer weight with base carry capacity `2000`. Movement slows
  linearly above 100 percent load, and nothing may push carried weight past the
  140 percent hard cap.
- Protected-on-death items, one-death insurance from an insurance NPC, and
  protected quest items.
- Player death keeps currency and the Secure Container, sends protected and
  insured items to Recovery Storage, and leaves everything else in a durable
  five-minute corpse that several players can loot at once.
- PostgreSQL owns custody, and every transfer is one idempotent, audited
  transaction. Unity shows the temporary three-area inventory with `B`, `C`, and
  `I` and never owns item rules.

## Combat Direction

The complete setting and combat direction is defined in
[Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md). It replaces the
earlier third-person projectile shooter direction.

- Combat is RuneScape-inspired and target based, with click-to-move movement
  and a freely rotatable camera with zoom.
- A character must stop moving before attacking.
- SimulationWorker is authoritative for movement and combat outcomes. The
  client plans a path, and the worker validates every step against collision.
- The client sends move-to-destination and attack-target intent.
- The server validates range, target, weapon state, ammunition, mana, cooldown,
  and hit results.

Click-to-move movement is implemented, and the first combat implementation in
[Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md) added:

- One simple Ranged weapon with consumed ammunition.
- Target selection and auto attack.
- Basic health and Normal damage.
- Authoritative death event generation.

Abilities, the combat triangle, elemental damage, magic and mana, threat, and
boss mechanics should wait until the basic combat loop works.

## NPC And Mob Foundation

The implemented world-actor foundation provides:

- `NPC` means a social or service actor. Dialogue, vendor, quest, crafting,
  insurance, trainer, bank, and Recovery Storage roles are freely composable
  capabilities.
- `Mob` means a combat actor with AI, aggro, combat, loot, corpse, and respawn
  behavior.
- Actor kind is separate from faction and disposition. Guards are NPCs and all
  city NPCs are invulnerable in the first version.
- NPC and Mob definitions plus spawn content are canonical neutral WorldData.
  Unity provides visual authoring and presentation, not authority.
- Normal actors are reconstructed from WorldData after worker restart and do
  not require one PostgreSQL row per instance.
- NPCs are event-driven. Mobs use centrally scheduled dormant and active tiers.
- The player clicks an actor, walks into range, and opens it. The server owns
  the `3.0` metre start range, `3.5` metre maintain range, line of sight, target
  revision, and one-active-interaction rule.
- The one-active-interaction rule includes the existing corpse view, while many
  players may still interact with one target.

A third actor kind, the resource node, carries exactly one gathering capability,
and the vendor capability trades through GameService. Both are described in
[Economy MVP](#economy-mvp) and [Gathering MVP](#gathering-mvp).

The foundation does not implement quest progression or crafting. The combat
slice in [Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md) added
target-based combat, Mob brains, Mob loot rolls, and the deaths that fill live
and durable corpses.

## Mob MVP

The first Mob has enough behavior to test combat and loot, as implemented by
the combat slice:

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

The first economy is simple and NPC-driven. It is implemented with the
decisions V1 to V10 in [Vendor And Gathering Plan](VENDOR_AND_GATHERING_PLAN.md):

- One NPC vendor, the `Trade` capability of Mira the Quartermaster.
- One integer currency called Gold. The name is catalog content, so a rename is
  content only.
- New characters start with `100` Gold, set as content. Existing characters keep
  their balance.
- Currency is stored persistently in the Characters module, not as an item, with
  a ledger row for every change, and remains with the character on death.
- Every item has a base value. The vendor sells at 100 percent of the value and
  buys at 40 percent, rounded down on the total.
- Mira sells Bronze Arrow (`1`), Minor Healing Potion (`25`), Cooked Trout
  (`10`), and Starter Pickaxe (`50`). The other values are Iron Ore `10`, Wolf
  Pelt `15`, Training Shortbow `100`, Leather Jerkin `80`, Copper Ring `40`,
  Traveler's Pack `200`, and Sealed Letter `0`.
- Mira buys every item with a value from carried storage, including part of a
  stack. Protected, insured, quest, equipped, banked, and non-empty Bag items
  are refused. Stock is unlimited, prices are fixed, and there is no buyback;
  sold items are destroyed with an audit record.
- One insurance NPC grants and explicitly removes one-death insurance through
  the implemented authoritative interaction lifecycle.

Auction house, direct player trading, regional markets, and advanced crafting
should wait until core item and economy transactions are stable.

## Gathering MVP

The first gathering loop is implemented with the decisions in
[Vendor And Gathering Plan](VENDOR_AND_GATHERING_PLAN.md):

- Equip a tool in the tool slot. The Starter Pickaxe provides the `pickaxe` tool
  kind that the Iron Vein requires.
- Interact with a resource node. One Iron Vein type is placed in both
  development Worlds; the Open Risk Rule Area does not exist yet.
- One click mines continuously: one guaranteed Iron Ore every 3 seconds until
  the vein depletes, carried storage is full, the weight cap is reached, the
  tool is unequipped, or the player moves or closes the interaction.
- Nodes are shared. An Iron Vein gives 5 ore, then stays depleted for 30
  seconds, and everyone mining it stops when it depletes.
- Resource node state is live Shard state in SimulationWorker, which owns the
  charges, cycle timing, depletion, and respawn. A worker restart restores every
  node to full. A durable opt-in can come later, as for boss corpses.
- GameService checks the required tool kind in the tool slot and grants the
  resource item transactionally into carried storage when capacity and weight
  allow it.
- Resource can be sold to the NPC vendor.
- There is no Mining experience or level in this slice. Profession progression
  becomes its own slice.

## Backend And Service Scope

The intended architecture remains:

- Unity client.
- ASP.NET Core GameService for global durable services and HTTP APIs.
- Headless .NET SimulationWorker for authoritative Shard simulation.
- LiteNetLib UDP for gameplay networking.
- PostgreSQL as the persistent source of truth.
- Redis only for temporary or operational state where it has a clear benefit,
  including bounded account-session validation caching and distributed
  authentication rate limits.

Item service split:

- GameService owns item definitions and vendor profiles mirrored from shared
  content, item instances, policies, slot and equipment assignments, bank,
  Secure Container, Recovery Storage, durable player corpse custody, audit, all
  durable item transactions, and the character currency wallet and ledger.
- SimulationWorker owns live world actors, spawn lifecycle, inventory, vendor,
  and corpse interaction validation, resource node charges and gathering cycles,
  proximity, combat, death-event production, active corpse presentation,
  one-active-interaction enforcement, and authoritative encumbrance.
- While a character is active, SimulationWorker requests durable mutations over
  a service-authenticated GameService boundary fenced to exact session, worker,
  runtime, character, and Shard.
- SimulationWorker never writes item tables directly.
- Unity sends intents and displays committed state.

The current implementation status is in [Project Overview](PROJECT_OVERVIEW.md).

## Persistence Principles

- PostgreSQL is the source of truth for persistent gameplay data.
- Redis is only for fast, temporary, or lease-based state. PostgreSQL remains
  authoritative for account sessions, and revocation tombstones prevent stale
  cached authorization after a successful application-controlled revocation.
- Inventory, bank, Secure Container, Recovery Storage, equipment, Bag, death,
  corpse, loot, vendor, insurance, trade, and auction mutations must prevent
  duplication and loss.
- Every extant item has exactly one current custody assignment.
- Important mutations use idempotent operation ids, stable lock order, expected
  revisions, complete rollback, and audit records.
- Database identifiers use snake_case.
- JSON over the wire uses camelCase.

## MVP Slices

The MVP is built in these slices. They are named, not numbered, so they never
collide with the delivered item phases in
[Items And Inventory Delivery History](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

| Slice | Status | Scope |
| --- | --- | --- |
| Project foundation | Complete | Repository structure, backend solution, local infrastructure, configuration, and canonical topology |
| Account, character, and Shard join | Complete | Registration, login, characters, placement, exact-runtime tickets, and one active session per character |
| Unity connection and movement | Complete | Unity API and UDP flow, entity lifecycle, spatial interest, click-to-move with client-planned paths, prediction, reconciliation, interpolation, and checksummed collision |
| SimulationWorker performance baseline | Complete | Headless stress clients, active bots, visibility and packet reuse, bounded backpressure, and repeatable baselines |
| Items, inventory, equipment, and carry weight | Complete | Everything in [Items And Inventory](#items-and-inventory), with the temporary Unity inventory UI |
| Vendor and gathering | Complete | One NPC vendor capability, persistent Gold, one shared resource node type with the starter pickaxe, gathering through the item transaction service, and buying from and selling to the vendor, as decided in [Vendor And Gathering Plan](VENDOR_AND_GATHERING_PLAN.md) |
| Target-based combat and simple Mob | Complete | One Ranged weapon with consumed ammunition, target selection and auto attack, health and damage, authoritative death events, and one simple Mob with live loot, as delivered and accepted in [Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md) |
| Gameplay rule areas and presence lifecycle | Deferred until a larger authored map exists | Content-authored Safe City and Open Risk rule areas without Zones or Layers, instant logout in the Safe City, a five-minute body after disconnect in the Open Risk area, and reconnect to the same body |
| Death, Recovery, and corpse looting | Foundation complete | The death partition, Recovery Storage, durable and live corpses, concurrent looting, Bag swaps, insurance, and the combat death producer are done. The final corpse presentation remains |

## Deferred Features

These should not block the first playable MVP:

- Civilized and wilderness gameplay rule-area split.
- Topological Zones, cross-zone handoff, and Layers.
- Criminal reputation and bounty hunting.
- Guilds and SocialService chat.
- Auction house and direct player trading.
- Crafting and professions beyond the first gathering loop.
- Advanced Mobs and bosses beyond the first persistence test.
- Complex quests.
- Insured stack quantities.
- Armor types and armor stats.

## Current Open Design Questions

- Should character deletion be available in the MVP?
- Should Shard switching have a cooldown after logout?
- The item balance questions, such as Secure Container eligibility, Bank
  unlocks, and Recovery Storage expiry, are in the
  [open questions of the inventory design](INVENTORY_AND_DEATH_LOOT_DESIGN.md#open-questions).

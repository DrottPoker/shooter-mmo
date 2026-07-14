# Shooter MMO MVP Spec

Last updated: 2026-07-12

## Purpose

This document defines the current MVP direction for the shooter MMO project.

The spec is a working agreement, not a permanent design lock. Decisions here can
change when implementation, playtesting, or better design ideas expose a better
path. The goal is to keep the first build focused enough to become playable
without losing the long-term game identity.

## Core MVP Goal

The MVP should prove the core loop:

1. Player creates or selects a character.
2. Player selects and joins a shard running the shared World content.
3. Player spawns in a safe city.
4. Player leaves the city into an unsafe open-world area.
5. Player gathers or loots a resource.
6. Player sells to a simple NPC vendor or uses the item later for crafting.
7. Player fights a simple mob or another player.
8. Player death applies the current loot rules.
9. Player returns to the city to bank, sell, equip, and prepare again.

The MVP should avoid becoming a large feature collection before this loop works.

## Account And Character Rules

- Accounts support multiple characters from the beginning.
- Maximum characters per account in the MVP: 5.
- Character data is persistent and stored in PostgreSQL.
- Each character owns its own bank, inventory, equipment, secure bag, currency,
  profession progress, quest state, and reputation state.
- The bank is per character, not account-wide.
- One account and one character may have only one active simulation session at a
  time across every fleet and shard.
- Shard switching is allowed only after the character has fully left its current
  simulation session.

## World Model

- The game is one large open world, not a set of extraction instances.
- `World` means shared map, collision, and game content data. It is not a server,
  process, region, or player-selectable runtime.
- A `Shard` is a player-selectable copy of the shared World simulation and may
  have its own rule set.
- A `Fleet` groups regional or operational compute. A `Node` is one machine or
  container host inside a fleet.
- A `SimulationWorker` is one headless authoritative process. Its active
  `SimulationAssignment` determines which shard it simulates.
- The current safe scale unit is one active SimulationWorker per shard and one
  active shard per SimulationWorker.
- Accounts, characters, progression, inventory, and the future economy are
  global and use the same persistent services across every fleet and shard.
- Shard-specific live state includes movement, combat, mobs, loot containers,
  gameplay rule state, and active player presence.
- There are no Realms. Topological Zones and Layers are future scaling systems
  and are not implemented in the current MVP foundation.

## Gameplay Rule Areas

Gameplay rule areas describe content rules inside the authored World. They are
not the future topological `Zone` scaling unit and must not be represented as
Zones, SimulationAssignments, or worker ownership boundaries.

Implementation of gameplay rule areas is deferred until the project has a
larger authored map with meaningful locations and boundaries. The current small
movement test map does not define these areas.

When the map is ready, the first playable version should keep rule areas simple:

- Safe City Rule Area
- Open Risk Rule Area

Civilized and wilderness rule areas can be split into separate policies later.
For the first implementation, everything outside the safe city should be treated
as unsafe.

### Safe City Rule Area

- No PvP.
- Instant logout.
- NPC vendor access.
- Bank access.
- Equipment and inventory management.
- Future location for crafting stations, quest NPCs, and social services.

### Open Risk Rule Area

- PvP is allowed.
- Simple mobs can exist.
- Gathering and loot containers can exist.
- Player death can drop loot based on the current death rules.
- Logout leaves the character in the world for a risk timer.

## Logout And Disconnect Rules

Logout behavior will depend on the character's current gameplay rule area.

- Safe City Rule Area: instant logout.
- Open Risk Rule Area in MVP: the character body remains in the shard simulation
  for 5 minutes.
- Future Civilized Rule Area: the character body remains for about 30 seconds.
- Future Wilderness Rule Area: the character body remains for about 5 minutes.

If the player reconnects while the character body is still active, the player
should resume control of that same character body.

Combat should reset or extend the logout timer so players cannot use logout or
disconnects to avoid danger.

## Inventory Rules

- Inventory is grid or cell based, similar in spirit to Escape from Tarkov.
- Items have width and height.
- Item rotation should be supported from the beginning.
- Item placement must be validated server-side.
- Item ownership must be stored transactionally in PostgreSQL.
- Redis must not be the source of truth for persistent item ownership.

The inventory system should support these container types:

- Character inventory
- Character bank
- Secure bag
- Loot containers
- Vendor inventory

## Secure Bag

- Secure bag exists in the MVP.
- Secure bag is a small protected grid container.
- Items inside the secure bag are protected from normal death loot drops.
- Secure bag capacity should be small enough that it does not remove the risk of
  unsafe zones.
- Secure bag placement follows the same grid and rotation rules as inventory.

## Equipment Slots

Characters should support equipment slots from the beginning.

Required MVP equipment slots:

- Head
- Body armor
- Primary weapon
- Secondary weapon
- Tool slot
- Ring slot 1
- Ring slot 2

The tool slot is used for tools such as a pickaxe or axe so the player can
gather resources.

Equipment should be represented as item ownership plus slot assignment, not as a
separate non-item system.

## Combat Direction

- Combat is third-person and projectile based.
- The server should be authoritative for combat outcomes.
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
- Death handling.

Advanced ballistics, limb damage, armor penetration, complex recoil, and detailed
weapon attachment systems should wait until the basic combat loop works.

## Mob MVP

The MVP should include one simple mob type.

The mob only needs enough behavior to test combat and loot:

- Spawn.
- Idle or patrol.
- Detect nearby players.
- Move toward the target.
- Attack.
- Take damage.
- Die.
- Drop simple loot.
- Respawn after a timer.

## Economy MVP

The first economy should be simple and NPC-driven.

Required MVP economy features:

- One NPC vendor.
- Player can sell basic loot or gathered resources.
- Player can buy basic supplies.
- Currency is stored persistently.

Auction house, direct player trading, regional markets, and advanced crafting
should wait until the core item and economy rules are stable.

## Gathering MVP

The MVP should include at least one gathering interaction.

Recommended first gathering loop:

- Equip a tool in the tool slot.
- Interact with a resource node in the Open Risk Rule Area.
- Server validates the required tool.
- Server grants a resource item into inventory if space exists.
- Resource can be sold to the NPC vendor.

## Death And Loot Rules

MVP death rules:

- Currency does not drop.
- Quest-critical items do not drop.
- Secure bag contents do not drop.
- Equipped items can be protected or dropped depending on the first balance pass.
- Normal inventory items drop in unsafe areas.
- Dropped items appear in a loot container in the world.

For the first implementation, the simplest recommended rule is:

- Safe City Rule Area: no PvP deaths.
- Open Risk Rule Area: normal inventory drops, while secure bag contents and
  currency are kept.

Equipment drop behavior can be tuned after the basic death and loot container
flow works.

## Backend And Service Scope

The intended architecture remains:

- Unity client.
- ASP.NET Core AuthService for global durable services and HTTP APIs.
- Headless .NET SimulationWorker for authoritative shard simulation.
- LiteNetLib UDP for gameplay networking.
- PostgreSQL as the persistent source of truth.
- Redis for operational readiness today and future transient coordination where
  it provides a clear benefit.

For MVP, the practical service split should be:

- AuthService handles accounts, login, characters, topology, shard placement,
  join tickets, simulation-session leases, inventory, bank, secure bag,
  equipment, vendor, and persistent item transactions.
- SimulationWorker handles realtime admission, movement, combat, mobs, gameplay
  rule areas, logout timers, death, and live loot containers for its assigned
  shard.
- SocialService can exist in the repository but does not need to be part of the
  first playable loop.

Current implementation note:

- AuthService stores account sessions, topology, exact-runtime join tickets, and
  global simulation-session leases in PostgreSQL.
- The canonical runtime hierarchy is Global Services, Fleet, Node,
  SimulationWorker, SimulationAssignment, and Shard. World remains shared
  content, and there are no Realms.
- SimulationWorker registers and heartbeats its exact runtime generation through
  an authenticated service channel. Assignment loss or lease expiry fences the
  process.
- Unity requests shard placement through AuthService and then connects directly
  to the assigned SimulationWorker over LiteNetLib UDP.
- Join, leave, reconnect, reliable entity lifecycle, movement input, and
  simulation snapshots use the shared versioned GameProtocol contract.
- SimulationWorker owns fixed-step authoritative movement, interest management,
  connection quotas, and collision against checksummed WorldData chunks.
- Unity owns input, local prediction, reconciliation, remote interpolation,
  presentation, and temporary UI. It does not own authoritative gameplay state.
- Persistent items, inventory, bank, secure bag, equipment, vendor, combat,
  mobs, gameplay rule areas, logout bodies, death, and loot are not implemented.

## Persistence Principles

- PostgreSQL is the source of truth for persistent gameplay data.
- Redis is only for fast, temporary, or lease-based state.
- Inventory, bank, secure bag, equipment, loot transfers, and vendor
  transactions must be designed to prevent dupes.
- Database identifiers should use snake_case.
- JSON over the wire should use camelCase.

## Suggested Implementation Phases

### Phase 1: Project Foundation

Status: Completed

- Confirm repository structure.
- Create the backend solution and service boundaries.
- Add local Docker Compose for PostgreSQL and Redis.
- Add baseline configuration conventions.
- Establish the canonical topology and terminology.

### Phase 2: Account, Character, And Shard Join

Status: Completed

- Account registration, login, logout, and revocable opaque sessions.
- Character create, list, and select.
- Enforce the configured character limit.
- Capacity-aware shard placement and exact-runtime join tickets.
- One active account login and one active simulation session per account and
  character.

### Phase 3: Unity Connection And Movement

Status: Completed

- Unity connects to Auth/API.
- Unity requests shard placement.
- Unity connects directly to SimulationWorker over UDP.
- Server-assigned entity lifecycle and spatial interest management.
- Shared server-authoritative movement, prediction, reconciliation, remote
  interpolation, and checksummed collision.

### Phase 4: Items, Inventory, Secure Bag, And Equipment

Status: Next

- Item definitions.
- Item instances.
- Grid inventory with rotation.
- Secure bag grid.
- Per-character bank.
- Equipment slots.
- Server-side validation for item placement and slot compatibility.
- Transactional ownership and movement rules that prevent duplication across
  inventory, bank, secure bag, and equipment.

### Phase 5: Vendor And Gathering

- Add one NPC vendor.
- Add one resource node type.
- Add one tool item.
- Gather into inventory.
- Sell gathered resource to vendor.

### Phase 6: Projectile Combat And Simple Mob

- Add one simple ranged weapon.
- Add projectile simulation.
- Add health and damage.
- Add one simple mob.
- Add mob loot.

### Phase 7: Gameplay Rule Areas And Presence Lifecycle

Status: Deferred until a larger authored map exists

- Define content-authored Safe City and Open Risk rule areas without creating
  topological Zones or Layers.
- Apply instant logout in the Safe City Rule Area.
- Keep the character body active for 5 minutes after disconnect in the Open Risk
  Rule Area.
- Support reconnect to the same active simulation entity and state.
- Keep connection lifetime, simulation-session lifetime, and entity lifetime as
  explicit separate concepts.

### Phase 8: Death And Loot Containers

- Apply death rules in the Open Risk Rule Area.
- Create loot containers from dropped inventory.
- Allow players to loot containers.
- Preserve secure bag and currency.

## Deferred Features

These should not block the first playable MVP:

- Civilized and wilderness gameplay rule-area split.
- Topological Zones, cross-zone handoff, and Layers.
- Criminal reputation.
- Bounty hunting.
- Guilds.
- Auction house.
- Direct player trading.
- Complex crafting.
- Multiple professions.
- Advanced mobs.
- Bosses.
- Complex quests.
- SocialService chat.
- Insurance.
- Advanced weapon attachments.
- Detailed armor and penetration systems.

## Current Open Design Questions

- Should equipped items drop in the Open Risk Rule Area MVP, or only inventory
  items?
- Should character deletion be available in the MVP?
- Should shard switching have a cooldown after logout?
- Should resource nodes be per-shard live state only, or persisted with respawn
  timestamps?
- Should the first tool be a pickaxe, axe, or generic starter tool?
- Should the first mob be hostile by default or only aggressive when attacked?

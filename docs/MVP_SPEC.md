# Shooter MMO MVP Spec

Last updated: 2026-07-09

## Purpose

This document defines the current MVP direction for the shooter MMO project.

The spec is a working agreement, not a permanent design lock. Decisions here can
change when implementation, playtesting, or better design ideas expose a better
path. The goal is to keep the first build focused enough to become playable
without losing the long-term game identity.

## Core MVP Goal

The MVP should prove the core loop:

1. Player creates or selects a character.
2. Player joins a world.
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
- A character must not be logged into multiple worlds at the same time.
- World switching is allowed, but only after the character has fully logged out
  from its current world.

## World Model

- The game is one large open world, not a set of extraction instances.
- The first implementation can run one world server, but the data model should
  not block multiple worlds later.
- All worlds use the same persistent database for account and character data.
- Worlds may have different rule sets later.
- World-specific live state includes movement, combat, mobs, loot containers,
  zone state, and active player presence.

## MVP Zone Model

The first playable version should keep zones simple:

- Safe City Zone
- Open Risk Zone

Civilized zones and wilderness zones can be split into separate rule sets later.
For the MVP, everything outside the city is treated as unsafe.

### Safe City Zone

- No PvP.
- Instant logout.
- NPC vendor access.
- Bank access.
- Equipment and inventory management.
- Future location for crafting stations, quest NPCs, and social services.

### Open Risk Zone

- PvP is allowed.
- Simple mobs can exist.
- Gathering and loot containers can exist.
- Player death can drop loot based on the current death rules.
- Logout leaves the character in the world for a risk timer.

## Logout And Disconnect Rules

Logout behavior depends on the character's current zone.

- Safe City Zone: instant logout.
- Open Risk Zone in MVP: character remains on the server for 5 minutes.
- Future Civilized Zone: character remains on the server for about 30 seconds.
- Future Wilderness Zone: character remains on the server for about 5 minutes.

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
- Interact with a resource node in the Open Risk Zone.
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

- Safe City Zone: no PvP deaths.
- Open Risk Zone: normal inventory drops, secure bag and currency are kept.

Equipment drop behavior can be tuned after the basic death and loot container
flow works.

## Backend And Service Scope

The intended architecture remains:

- Unity client.
- ASP.NET Core Auth/API service.
- .NET WorldServer.
- LiteNetLib UDP for gameplay networking.
- PostgreSQL as the persistent source of truth.
- Redis for sessions, join tickets, presence, world registry, locks, and cache.

For MVP, the practical service split should be:

- Auth/API handles accounts, login, characters, world join tickets, inventory,
  bank, secure bag, equipment, vendor, and persistent item transactions.
- WorldServer handles movement, combat, mobs, zones, logout timers, death, and
  live loot containers.
- SocialService can exist in the repository but does not need to be part of the
  first playable loop.

Current implementation note:

- Auth sessions and world join tickets are stored in PostgreSQL for the first
  backend phase.
- Active character world sessions use PostgreSQL leases so ticket consumption and
  the global single-world claim can commit in one transaction.
- WorldServer rotates the lease token on reconnect, heartbeats active leases, and
  releases them during a normal leave or graceful shutdown.
- Redis runs locally, but join tickets can move to Redis later when multiple
  live WorldServers need faster short-lived coordination.
- WorldServer currently validates join tickets through a temporary HTTP debug
  endpoint before the real Unity and UDP join handshake exists.
- Unity currently has temporary scene UI that uses AuthService HTTP and
  WorldServer HTTP debug endpoints before the real UDP gameplay client exists.
- Unity WorldScene currently creates a local placeholder environment, player,
  and third-person camera at runtime so movement can be tested before UDP
  gameplay networking exists.

## Persistence Principles

- PostgreSQL is the source of truth for persistent gameplay data.
- Redis is only for fast, temporary, or lease-based state.
- Inventory, bank, secure bag, equipment, loot transfers, and vendor
  transactions must be designed to prevent dupes.
- Database identifiers should use snake_case.
- JSON over the wire should use camelCase.

## Suggested Implementation Phases

### Phase 1: Project Foundation

- Confirm repository structure.
- Create backend solution structure.
- Create WorldServer structure.
- Add local Docker Compose for PostgreSQL and Redis.
- Add baseline configuration conventions.

### Phase 2: Account, Character, And World Join

- Account registration and login.
- JWT or session token.
- Character create, list, select, and delete if needed.
- Enforce 5 characters per account.
- World join ticket flow.
- Single active world session per character.

### Phase 3: Unity Connection And Movement

- Unity connects to Auth/API.
- Unity requests world join.
- Unity connects to WorldServer over UDP.
- Spawn character in Safe City Zone.
- Basic movement replication.

### Phase 4: Zones And Logout Timers

- Define Safe City Zone and Open Risk Zone.
- Apply instant logout in city.
- Apply 5 minute logout body in Open Risk Zone.
- Support reconnect to active body.

### Phase 5: Items, Inventory, Secure Bag, And Equipment

- Item definitions.
- Item instances.
- Grid inventory with rotation.
- Secure bag grid.
- Per-character bank.
- Equipment slots.
- Server-side validation for item placement and slot compatibility.

### Phase 6: Vendor And Gathering

- Add one NPC vendor.
- Add one resource node type.
- Add one tool item.
- Gather into inventory.
- Sell gathered resource to vendor.

### Phase 7: Projectile Combat And Simple Mob

- Add one simple ranged weapon.
- Add projectile simulation.
- Add health and damage.
- Add one simple mob.
- Add mob loot.

### Phase 8: Death And Loot Containers

- Apply death rules in Open Risk Zone.
- Create loot containers from dropped inventory.
- Allow players to loot containers.
- Preserve secure bag and currency.

## Deferred Features

These should not block the first playable MVP:

- Civilized zone and wilderness split.
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

- Should equipped items drop in the Open Risk Zone MVP, or only inventory items?
- Should character deletion be available in the MVP?
- Should world switching have a cooldown after logout?
- Should resource nodes be per-world live state only, or persisted with respawn
  timestamps?
- Should the first tool be a pickaxe, axe, or generic starter tool?
- Should the first mob be hostile by default or only aggressive when attacked?

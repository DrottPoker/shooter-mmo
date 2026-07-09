# MMO Project Brief for Codex

_Last updated: 2026-07-06_

## Purpose

This document gives Codex enough context to understand the game direction, technical architecture, and near-term development philosophy. The design is intentionally not locked down in every detail. Many systems will change as the project evolves.

The project is a **classless, profession/economy-driven open-world MMO** with a hybrid setting leaning toward a **modern third-person shooter**, inspired by parts of RuneScape, World of Warcraft city hubs, DayZ, and Escape from Tarkov.

The key idea is:

> A RuneScape-like persistent MMO world with safe cities, professions, quests, crafting, player economy, and dangerous full-loot wilderness, but with a more modern shooter-style combat direction.

## Core Game Identity

The game should not be treated as a pure shooter or pure fantasy MMORPG.

It should feel like:

- A large persistent open world.
- Safe cities with NPCs, quests, banks, crafting, trade, and social systems.
- Classless character progression.
- Professions and economy as core progression pillars.
- Modern/hybrid weapon types rather than only medieval fantasy weapons.
- Third-person combat.
- Low/mid-poly visual direction.
- PvP as one path to progression, not the entire game.
- Wilderness as a high-risk/high-reward area with full loot and social tension.

The design philosophy:

> Civilization protects progression. Wilderness accelerates progression. Crime enables shortcuts, but creates long-term consequences.

## Setting Direction

The setting is **hybrid**, but currently leans more toward **modern shooter** than classic medieval fantasy.

Think:

- RuneScape-like world structure, but more modern.
- Different weapon types and tiers.
- Guns, melee weapons, crafted equipment, and possibly magic/tech/fantasy-inspired items later.
- Not hyper-realistic military simulation.
- Progression and economy matter more than ultra-realistic ballistics.

The game can include fantasy/magic elements later, but the current direction is more modern and survival/MMO-oriented.

## World Structure

The game world should support multiple RuneScape-style worlds/servers.

Players can choose a world to join, but key character data is shared globally.

Shared across worlds:

- Account
- Characters
- Inventory
- Bank
- Auction house / marketplace
- Currency
- Professions
- Quest state
- Friends
- Guilds
- Social systems

World-specific/live state:

- Player position
- Live combat
- Mobs/NPC simulation
- Wilderness encounters
- Local world events
- Loot containers in the world
- Resource node state, depending on implementation

The player should be able to join different worlds with the same character, but the same character must not be logged into multiple worlds at the same time.

## Zone Model

The world should be divided into legal/safety zones rather than only simple PvP-on/PvP-off areas.

### 1. City Zones

Cities are safe hubs.

Expected features:

- No PvP.
- NPC quest givers.
- Bank/stash.
- Crafting stations.
- Marketplace / auction house.
- Profession trainers or vendors.
- Social areas.
- Guards.
- Guild/player services later.

Large cities should restrict or deny access to heavily criminal players.

### 2. Normal / Civilized Zones

Normal zones are safer open-world areas outside cities.

Current design direction:

- PvP may be technically possible.
- Attacking/killing players here should cause severe criminal penalties.
- These zones should not encourage random killing.
- They should contain regular quests, professions, gathering, mobs, and medium-tier progression.

The goal is that killing someone in a normal/civilized zone should require a strong reason because the consequences are severe.

### 3. Wilderness

Wilderness is a permanent open-world high-risk area, not only an extraction instance.

Expected features:

- Full PvP.
- Full loot on death, with exceptions.
- Rare resources.
- Dangerous mobs.
- High-value loot.
- World bosses or contested content later.
- Criminal activity.
- Outlaw camps and black markets.
- Reasons to cooperate, not only kill-on-sight.

Instances may exist later, but the main wilderness should be a permanent explorable area.

## PvP and Criminal System

PvP should be possible, but the game should encourage interesting social decisions rather than constant kill-on-sight behavior.

### Hostile Flag

A player should receive a hostile flag when they attack or damage another non-criminal player first.

Initial design:

- Hostile flag duration: around 15 minutes.
- Hostile players cannot safely enter larger cities.
- Other players can attack hostile players without penalty.
- Guards may stop or attack hostile players near lawful areas.

### Criminal Reputation

Criminal status should not be a simple boolean.

It should be reputation-based with multiple levels.

Possible levels:

- Lawful / Trusted
- Neutral
- Suspicious
- Criminal
- Outlaw
- Notorious / Most Wanted

The exact names can change.

Criminal status should deepen when a player repeatedly commits crimes, such as attacking or killing neutral/lawful players.

### Criminal Consequences

Consequences can include:

- Some NPCs refuse to trade or help.
- Higher prices/taxes.
- Restricted access to large cities.
- Guards attack or block entry.
- Reduced or more expensive insurance.
- Bounty hunter gameplay.
- Other players can kill criminals without penalty.
- Main city services may become unavailable at higher criminal levels.

### Criminal Recovery

Players should be able to reduce criminal status over time or through payment/atonement.

Possible recovery methods:

- Pay fines.
- Complete atonement tasks.
- Wait out lower-level reputation damage.
- Turn in contraband or bounty targets.
- Use special NPCs.

### Criminal Gameplay

Criminal status should be punishing, but still playable.

Criminal players should have access to alternate systems:

- Outlaw camps.
- Black markets.
- Fence NPCs.
- Smuggling quests.
- Illegal vendors.
- Criminal factions later.

The goal is to make criminal play a dangerous lifestyle, not a dead end.

## Death and Loot Rules

Core direction:

- In dangerous PvP contexts, the player drops everything.
- Secure bag contents are protected.
- Insurance may protect or return some items.
- Quest items do not drop.
- Currency is kept.

### Secure Bag

The secure bag works similarly in spirit to Escape from Tarkov's secure container.

Design direction:

- Small capacity.
- Protects selected items from being looted on death.
- Should not be large enough to remove the risk of full loot.
- Likely limited to small valuables, keys, special materials, or similar items.

### Insurance

Insurance may protect some items, but should not remove risk entirely.

Preferred direction:

- Insured items may be returned only if no other player loots them.
- Insurance may be worse or more expensive for criminals.
- Insurance should not make PvP loot meaningless.

### Quest Items and Currency

- Quest-critical items should not drop.
- Currency is kept on death.

This prevents death from completely blocking progression or griefing important quests.

## Professions and Economy

The game should be a **profession/economy MMO where combat and PvP are part of the world**.

Professions should be as important as combat progression.

Possible professions:

- Mining
- Engineering
- Weaponsmithing
- Armorsmithing
- Medicine
- Alchemy/Chemistry
- Cooking
- Hunting
- Farming
- Trading
- Scavenging
- Crafting specializations

Professions should create real interdependence between players.

Examples:

- Miners gather rare materials.
- Engineers craft attachments or tools.
- Medics create healing items.
- Traders move goods between cities.
- Criminals may smuggle or rob.
- Bounty hunters hunt criminals.
- Guilds may control resource areas.

The economy should eventually be player-driven, but early versions can use NPC vendors and simple crafting.

## Combat Direction

Current combat direction:

- Third-person.
- Low/mid-poly visuals.
- Modern shooter leaning.
- Not hyper-realistic.
- Progression-focused.
- PvP is one route to progression, not the only purpose of the game.

Avoid overbuilding realism early.

For MVP, combat can start simple:

- Basic third-person aiming.
- Simple ranged weapons.
- Simple melee if needed.
- Basic armor/damage rules.
- Server-authoritative validation where practical.
- No complex limb damage or advanced ballistics at first.

## Technical Architecture

The project currently uses / should continue toward:

- Unity client.
- C#.
- ASP.NET Core / .NET backend.
- .NET WorldServer.
- LiteNetLib UDP for gameplay networking.
- PostgreSQL as source of truth.
- Redis for sessions, presence, world registry, cache, and short-lived state.
- Docker Compose for local/professional dev infrastructure.
- Linux VPS for online testing/staging.

### Current/Preferred Service Split

#### AuthService

Responsible for:

- Accounts
- Login/register
- JWT tokens
- Character list/select
- World join ticket flow
- Session validation

#### WorldServer

Responsible for:

- Movement
- Combat
- Mobs
- PvP
- Zones
- Death handling
- Live world simulation
- Loot containers/world objects

WorldServer should not permanently own inventory, auction house, or social systems.

#### SocialService

Should be separate from WorldServer so chat/friends remain online even if a world server restarts.

Responsible for:

- Chat
- Friends
- Guild chat
- Party chat
- Private messages
- Presence
- Cross-world communication
- Moderation basics

Preferred client connections:

- HTTPS for Auth/API.
- WebSocket/WSS for SocialService.
- UDP for WorldServer gameplay.

#### Inventory/Economy Backend

May start as modules inside Auth/API, but should be designed so it can become its own service later.

Responsible for:

- Inventory
- Bank
- Item instances
- Secure bag
- Insurance
- Loot transfers
- Crafting
- Auction house
- Trades

Important principle:

> Inventory, bank, auction house, and item ownership must be transactional and backed by PostgreSQL to prevent dupes/exploits.

## Database Principles

Use PostgreSQL as source of truth.

Important persistent data:

- Users/accounts
- Characters
- Character sessions
- Item instances
- Inventory containers
- Bank containers
- Secure bag containers
- Auction listings
- Currency balances
- Quest state
- Profession XP
- Criminal reputation
- Friends/guilds/social data

Use Redis only for fast/temporary state:

- Online presence
- Active world registry
- Join tickets
- Rate limits
- Cache
- Short-lived locks/leases

Do not use Redis as the only storage for important persistent inventory/economy data.

## Development Preferences

The user prefers:

- Responses in Swedish.
- Code and code comments in English.
- Short, informative code comments.
- Full-file patches when code is updated.
- Minimal, surgical fixes without unnecessary restructuring.
- JSON over the wire should use camelCase.
- Database identifiers should use snake_case.

## Local Development Setup

Preferred professional setup:

- Run .NET services in Visual Studio/Rider for debugging.
- Run PostgreSQL and Redis via Docker Compose.
- Use DBeaver to inspect PostgreSQL.
- Use migrations instead of manual DB changes.
- Use environment variables/User Secrets for sensitive config.

Local example:

- AuthService runs locally on HTTP.
- WorldServer runs locally with UDP.
- PostgreSQL runs in Docker.
- Redis runs in Docker.
- Unity connects to local AuthService and WorldServer.

## Online Testing / Staging Setup

A single Linux VPS is acceptable for early online tests.

Example staging stack:

- Ubuntu Server LTS.
- Docker Compose.
- Nginx for HTTPS/WSS reverse proxy.
- AuthService container.
- SocialService container.
- WorldServer 1 container.
- WorldServer 2 container later.
- PostgreSQL container.
- Redis container.

Only expose necessary ports:

- 22 TCP for SSH.
- 80/443 TCP for HTTP/HTTPS/WSS.
- World UDP ports such as 27015, 27016, etc.

PostgreSQL and Redis should not be publicly exposed.

## Near-Term MVP Direction

A good first playable loop:

1. Player logs in.
2. Player selects/creates a character.
3. Player selects a world.
4. Player spawns in a safe city.
5. Player gets a simple NPC quest.
6. Player gathers a resource in a normal zone.
7. Player crafts a basic item in the city.
8. Player enters wilderness.
9. Player finds higher-value loot/resource.
10. Player encounters another player.
11. Players can cooperate, flee, or fight.
12. Attacker gets hostile flag if they damage first.
13. Death drops loot according to rules.
14. Player returns to city or outlaw camp.
15. Player sells/crafts/progresses.

Do not overbuild too many systems before this loop works.

## Important Design Reminder

The game should avoid becoming only a kill-on-sight shooter.

Wilderness should be dangerous, but players should have reasons to cooperate, trade, escort, negotiate, hunt criminals, or form temporary alliances.

PvP should create risk and stories, but the long-term glue of the game is:

- Character progression
- Professions
- Crafting
- Economy
- Social systems
- Exploration
- Risk/reward decisions


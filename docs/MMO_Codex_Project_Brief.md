# MMO Project Brief for Codex

_Original brief: 2026-07-06. Current-design alignment: 2026-07-18._

> This is the original broad vision document. Current terminology and
> architecture are defined in `PROJECT_ARCHITECTURE.md`. Current item,
> inventory, carry-weight, insurance, corpse, and death-loot decisions are
> defined in `INVENTORY_AND_DEATH_LOOT_DESIGN.md` and supersede conflicting
> historical ideas below. Current World actor, NPC, Mob, spawn-authoring, and
> interaction terminology is defined in `NPC_AND_MOB_SYSTEM_DESIGN.md`.

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

The game should support multiple player-selectable Shards running shared World
content.

Players can choose a world to join, but key character data is shared globally.

Global across Fleets and Shards:

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

Shard-specific live state:

- Player position
- Live combat
- Mob and NPC simulation
- Wilderness encounters
- Local world events
- Loot containers in the world
- Resource node state, depending on implementation

The player should be able to join different Shards with the same character, but
the same account and character must not have more than one active simulation
session.

## Gameplay Rule Areas

These are authored gameplay policies. They are not the future topological Zone
or Layer scaling systems.

The world should be divided into legal/safety zones rather than only simple PvP-on/PvP-off areas.

### 1. City Rule Areas

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

### 2. Normal / Civilized Rule Areas

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

Current direction:

- Inventory is slot-based rather than grid-based.
- Currency remains with the character.
- Secure Container contents remain with the character and are hidden from
  looters.
- Protected-on-death items move to Recovery Storage and remain hidden.
- Insurance provides one-death protection, moves the actual item to Recovery
  Storage, consumes the policy, and may leave a non-interactive corpse snapshot.
- Remaining permanent inventory, equipment, Bag, and Bag contents become
  lootable corpse custody.
- Player corpse custody remains durable for an absolute five-minute lifetime,
  including when the corpse is empty.
- Multiple players can loot the same corpse through transactional item requests.

The complete current rules are maintained in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).

### Quest Items and Currency

- Protected quest items do not enter lootable corpse custody.
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
- Engineers craft tools or other equipment components.
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
- Headless .NET SimulationWorker.
- LiteNetLib UDP for gameplay networking.
- PostgreSQL as source of truth.
- Redis for readiness, bounded account-session validation caching, distributed
  authentication rate limits, and future transient coordination where it has a
  clear benefit, never as durable item or session authority.
- Docker Compose for local/professional dev infrastructure.
- Linux VPS for online testing/staging.

### Current/Preferred Service Split

#### AuthService

Responsible for:

- Accounts
- Login/register
- Revocable opaque account sessions
- Character list/select
- Shard placement and exact-runtime join-ticket flow
- Session validation

#### SimulationWorker

Responsible for:

- Movement
- Combat
- Mobs
- PvP
- Authored gameplay rule areas when implemented
- Death handling
- Live Shard simulation
- Loot containers/world objects

SimulationWorker must not permanently own player inventory, auction house, or
social systems.

#### SocialService

Should be separate from SimulationWorker so chat and friends remain online when
a simulation process restarts.

Responsible for:

- Chat
- Friends
- Guild chat
- Party chat
- Private messages
- Presence
- Cross-Shard communication
- Moderation basics

Preferred client connections:

- HTTPS for Auth/API.
- WebSocket/WSS for SocialService.
- UDP for SimulationWorker gameplay.

#### Inventory And Economy Boundary

Starts as focused AuthService feature modules behind a durable transaction
boundary. A later service extraction must preserve the same authority and
idempotency contracts.

Responsible for:

- Inventory
- Bank
- Item instances
- Bag and Secure Container
- Recovery Storage and one-death insurance
- Carry weight and item policies
- Durable player corpse custody
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
- Bag, Secure Container, and Recovery Storage state
- Item policies and transaction audit
- Durable player corpse custody
- Auction listings
- Currency balances
- Quest state
- Profession XP
- Criminal reputation
- Friends/guilds/social data

Use Redis only for fast/temporary state:

- Online presence
- Bounded account-session validation cache and future transient coordination
- Short-lived operational observations
- Rate limits
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
- SimulationWorker runs locally with UDP.
- PostgreSQL runs in Docker.
- Redis runs in Docker.
- Unity connects to local AuthService and SimulationWorker.

## Online Testing / Staging Setup

A single Linux VPS is acceptable for early online tests.

Example staging stack:

- Ubuntu Server LTS.
- Docker Compose.
- Nginx for HTTPS/WSS reverse proxy.
- AuthService container.
- SocialService container.
- SimulationWorker 1 container.
- Additional SimulationWorker containers later.
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

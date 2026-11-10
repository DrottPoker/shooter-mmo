# Project Overview

MMORPG is an early classless, profession-driven, open-world MMORPG with a World of Warcraft
style fantasy setting and RuneScape-inspired click-to-move combat. The direction is defined in
[Game Design](GAME_DESIGN.md). The project builds a stable, server-authoritative foundation first
and adds gameplay in small vertical slices on top of it.

## What You Can Do Today

1. Register or log in, then create a character with a body model and a look, or select one of up
   to five characters.
2. Pick a shard. GameService places the character on the SimulationWorker that runs it, and the
   client enters that shard's World over UDP.
3. Walk by clicking: the client plans a route around obstacles and predicts it, and the worker
   follows the same route with authoritative collision. Wade slower through water; deep water
   stops you. An orbit camera turns and zooms.
4. Right-click anything for the world menu of every option under the cursor, such as Attack,
   Trade, Mine, Chop, Loot, Walk here, and Examine; a left click runs the top option.
5. Trade with the NPC vendor for Gold: buy tools, supplies, clothing, armor, and weapons, and sell
   carried items back.
6. Mine Iron Ore from shared Iron Veins with a pickaxe and chop Oak Logs from shared Oak Trees
   with a Woodcutting Axe. Nodes deplete for everyone and respawn.
7. Fight wolves and, on a shard that allows it, other players with a bow and arrows, a sword, a
   greataxe, or a staff whose bolts cost mana. Die, leave a corpse that others can loot, and
   respawn.
8. Manage a slot inventory with equipment, Bags, a Bank, Recovery Storage, and carry weight
   that slows movement. What a character wears shows on its body.
9. Use NPC services such as the Bank, Recovery Storage, and insurance.
10. Accept quests at quest NPCs, follow them in the quest log and the tracker, and turn them in
    for a reward.

The UI shows all of this. The development Worlds are test maps: Development World 2 has
a zone for every feature, and Development World 1 is a small stress arena for many bots. See
[Development Worlds](DEVELOPMENT_WORLDS.md) and, for the controls, [Game Features](GAME_FEATURES.md).

## How It Fits Together

| Part | Owns |
| --- | --- |
| Unity client | Presentation, input, movement prediction and reconciliation, remote interpolation, scene flow, and the UI. It never owns game rules |
| GameService | Accounts, sessions, characters, topology, worker registration, shard placement, join tickets, items, corpses, and currency, over HTTP |
| SimulationWorker | The authoritative realtime simulation of one shard: UDP transport, players and world actors, movement, interaction, gathering, combat, Mob brains, deaths, and respawns |
| PostgreSQL | The durable authority for everything GameService owns |
| Redis | Account-session acceleration and distributed rate limits; it holds no game state |
| GameProtocol, GameSimulation, WorldData | Code shared by the server and Unity: the binary protocol, the movement and collision simulation, and the domain rules and content data |
| `content` and `art` | The authored game content and the Blender sources of the 3D models |

The terms, boundaries, and flows are defined in [Project Architecture](PROJECT_ARCHITECTURE.md).
Accounts, characters, and items are global: a character is not bound to one region or shard.

## Status By Area

| Area | Implemented | Next or deferred |
| --- | --- | --- |
| Accounts and characters | Registration, login, one active session per account, characters with looks | Character deletion is an open question |
| Topology and placement | Fleets, Nodes, workers, Shards, Worlds, heartbeat leases, failover, exact-runtime join tickets, offline Shard-to-World rebinding | Production orchestration |
| Movement and collision | Click-to-move with client-planned routes, prediction, reconciliation, shared checksummed collision with terrain, lakes, rivers, and sea that are waded and stop at deep water, stylized water with foam, waterfalls, and wakes | Fishing, dynamic collision |
| World actors | NPCs with composable capabilities, Mobs, resource nodes, visual spawn authoring, the world menu, server-validated interaction | Durable unique actors, dialogue, crafting, and trainer operations |
| Items and inventory | Item catalog, slot inventory, equipment, Bags, Bank, Recovery Storage, policies, carry weight, predicted inventory moves | Consume and use, final inventory UI |
| Death and corpses | Death partition, durable player corpses, live and durable Mob corpses, concurrent looting, Loot all | Final corpse presentation |
| Economy | Gold with a ledger, one NPC vendor with fixed prices | More vendors, player trading, auction house |
| Gathering and professions | Mining and woodcutting on shared nodes | Profession levels, crafting |
| Combat and Mobs | Target-based combat with four weapon families, windups, arrows, mana, wolves with brains, death events, respawn, Mob tagging with loot rights, world bosses that give every contributor their own loot | Abilities, the combat triangle, threat, Mob abilities |
| Quests | Quest content with steps, objectives, chains, and rewards, quest NPCs, kill, collect, deliver, talk, and gather objectives, quest drops, the quest log and tracker, and Quest Studio | Experience rewards, more objective kinds, and repeatable schedules |
| Character visuals | Human bodies with looks, garments, hair, cloth, and carried weapons and tools, all from the art pipeline | More races and body types, final art |
| Operations | OpenTelemetry metrics, logs, and traces, local monitoring stack, stress tools, active bots | Alert notifications, staging and production deployment |

This table is the only place that states the status of the whole project. Each design document
states the status of its own rules, and no document states the status of another document's
area. The detailed behavior is in [Game Features](GAME_FEATURES.md) and
[Service Features](SERVICE_FEATURES.md), and the planned rules in the design documents.

## Engineering Standard

Every system has one of three maturity levels. The level says how settled its design is, never
how carefully its code is written: tests, zero warnings, formatting, documentation in the same
pull request, server authority, and the trust boundaries apply at every level.

| Level | Means | Rule |
| --- | --- | --- |
| Foundation | The boundaries and contracts are settled, and other systems build on them | A contract change needs a design decision and a path for the stored data, content, and clients that depend on it |
| Provisional | It works and is tested, but the game has not yet shown what it needs from it | Its design, formats, and numbers may change without a compatibility path while there are no live players. Build what the next system on top needs, not more |
| Temporary | A stand-in that will be replaced | It carries `Temporary` in its name where it is code, is replaced instead of extended, and nothing depends on it beyond the seam it sits behind |

A new system starts as Provisional. It becomes a Foundation in a pull request that says why,
when three things are true: gameplay content uses it, someone who did not build it has reviewed
it and the findings are fixed, and, where it carries load, a stress workload measures it against
the [scale target](PROJECT_ARCHITECTURE.md#scale-target). A Foundation whose contract must be
redesigned goes back to Provisional in the pull request that starts the redesign.

### Maturity By System

| System | Level | Still open |
| --- | --- | --- |
| Accounts, account sessions, and authentication | Foundation | Password change and reset, email verification |
| Topology, placement, runtime fencing, and leases | Foundation | |
| Item custody: the transaction kernel, idempotency, the death partition, and corpse custody | Foundation | |
| The realtime threading contract and the authority lanes | Foundation | |
| The protocol schema, its generator, and the codec checks | Foundation | |
| Shared movement simulation, prediction, and reconciliation | Foundation | |
| The content compilers and their verification | Foundation | |
| Module and assembly boundaries with their guard tests, and the quality gate | Foundation | |
| Protocol versioning and snapshot encoding | Provisional | Exact-match versions and full-state snapshots |
| Content delivery to the client | Provisional | The catalogs ship inside the client build and must match exactly |
| Player-facing text | Provisional | The text tables exist, but the client still reads English text from the catalogs and protocol fields |
| Player presence: join, leave, and disconnect | Provisional | Nothing about a live character but its items is stored |
| The collision data format and its streaming | Provisional | |
| Database migrations, retention, and maintenance | Provisional | |
| Worker and GameService resilience: lease durations, heartbeats, and backpressure | Provisional | |
| World actors, interaction, and capabilities | Provisional | |
| Combat, Mobs, and their numbers | Provisional | |
| Quests | Provisional | |
| Economy, vendors, and gathering | Provisional | |
| Character visuals: bodies, garments, cloth, and appearance | Provisional | |
| The art pipeline | Provisional | |
| The content tools and the World Builder | Provisional | |
| The client's realtime hub, session state, and scene flow | Provisional | |
| The UI kit and the windows built on it | Provisional | |
| Monitoring, stress tools, and bots | Provisional | |
| The panels named `Temporary` | Temporary | Move onto the UI kit |
| The development Worlds | Temporary | Test maps, replaced by the first real World |
| Placeholder item icons | Temporary | |

## Where To Read Next

The [documentation map](README.md) lists every document. Most readers continue with
[Project Architecture](PROJECT_ARCHITECTURE.md), [Game Design](GAME_DESIGN.md), and
[Local Development](LOCAL_DEVELOPMENT.md).

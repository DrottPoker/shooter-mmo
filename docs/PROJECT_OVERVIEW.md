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
11. Leave or lose the connection: the character's body stays in the world for 20 seconds, longer
    while it is attacked, and entering the same character again in time takes it over where it
    stands.

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
| Accounts and characters | Registration, login, one active session per account, characters with looks that keep their place, health, and mana across a logout, and a body that stays 20 seconds after a leave or a lost connection, longer while attacked, which its player resumes by coming back in time; the client keeps the World through a lost connection and joins again by itself for those 20 seconds, says why it returned to character select or login, and counts down a body after a leave | Body times per gameplay rule area; character deletion is an open question |
| Topology and placement | Fleets, Nodes, workers, Shards, Worlds, heartbeat leases, failover, exact-runtime join tickets that the client proves without sending their key, encrypted and authenticated UDP traffic, offline Shard-to-World rebinding | Production orchestration |
| Movement and collision | Click-to-move with client-planned routes, prediction, reconciliation, shared checksummed collision with terrain, lakes, rivers, and sea that are waded and stop at deep water, stylized water with foam, waterfalls, and wakes | Fishing, dynamic collision |
| World actors | NPCs with composable capabilities, Mobs, resource nodes, visual spawn authoring, the world menu, server-validated interaction | Durable unique actors, dialogue, crafting, and trainer operations |
| Items and inventory | Item catalog, slot inventory, equipment, Bags, Bank, Recovery Storage, policies, carry weight, predicted inventory moves | Consume and use, final inventory UI |
| Death and corpses | Death partition, durable player corpses, live and durable Mob corpses, concurrent looting, Loot all | Final corpse presentation |
| Economy | Gold with a ledger, one NPC vendor with fixed prices, and an audit of items and gold over the whole database | More vendors, player trading, auction house |
| Gathering and professions | Mining and woodcutting on shared nodes | Profession levels, crafting |
| Combat and Mobs | Target-based combat with four weapon families, windups, arrows, mana, wolves with brains, death events, respawn, Mob tagging with loot rights, world bosses that give every contributor their own loot | Abilities, the combat triangle, threat, Mob abilities |
| Character progression | A durable character level that gates equipment, and the levelling rules as tunable content: the level curve, Mob experience, the level difference rails, the highest level, a Mob's experience percent, and a quest's experience reward. GameService stores the experience inside the level and grants it for every credited Mob kill and for a quest turn-in | Showing experience in the client: the experience bar and the level-up; talents, Gear Power |
| Quests | Quest content with steps, objectives, chains, and rewards, quest NPCs, kill, collect, deliver, talk, and gather objectives, quest drops, the quest log and tracker, and Quest Studio | Experience rewards, more objective kinds, and repeatable schedules |
| Character visuals | Human bodies with looks, garments, hair, cloth, and carried weapons and tools, all from the art pipeline | More races and body types, final art |
| Operations | OpenTelemetry metrics, logs, and traces, local monitoring stack, stress tools, a network impairment proxy with four network profiles, active bots, server images, and a production-like stack with a smoke test and a runbook | Alert notifications, a real host with a domain, an image registry, and automated deployment |

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
| Accounts, account sessions, and authentication | Foundation | Password change and reset, email verification, and logging out everywhere: postponed by decision on 2026-11-17, so an account that loses its password cannot be recovered yet |
| Topology, placement, runtime fencing, and leases | Foundation | |
| Item custody: the transaction kernel, idempotency, the death partition, and corpse custody | Foundation | |
| The realtime threading contract and the authority lanes | Foundation | |
| The protocol schema, its generator, and the codec checks | Foundation | |
| Shared movement simulation, prediction, and reconciliation | Foundation | |
| The content compilers and their verification | Foundation | |
| Module and assembly boundaries with their guard tests, and the quality gate | Foundation | |
| Snapshot encoding and replication scheduling: full-state records, the interest set with its entry budget, the far refresh, and the missed states | Foundation | Full-state snapshots without delta coding. No load scenario enters a crowd on a slow line, which a worker test covers; a hook that fails after a spawn was sent is not isolated |
| Protocol versioning: the exact match of protocol, simulation, and collision versions between client, worker, and GameService | Provisional | Exact match with no window of versions and no update channel, so replacing a worker takes its Shard away from every client that has not updated. The Shard list shows no version, a frame of another version after a worker was replaced between the ticket and the connect shows as a lost connection, and the fingerprint does not cover the names of enum members |
| The sealed realtime transport: the join proof, the key schedule, and the sealed datagrams | Provisional | One cipher and no new keys within a session; the service channel between worker and GameService must be private by deployment |
| Content delivery to the client | Provisional | The client fetches the item and quest catalogs and the text tables of one locale by revision once at start, caches them, and falls back to its bundled set as a whole; no swap while it runs, one locale, and the item presentation catalog and binary assets still ship in the build |
| Player-facing text | Provisional | Item, quest, and actor text and the text for every code that a server sends or the client gives a failure it finds itself come from the text tables; no compiled content and no server holds a text of the content, only the codes of normal play have a text of their own, and of the client's own texts the quest windows, the item rule reasons, the inventory, storage, corpse, and vendor windows, the login and character screens, the combat HUD status texts, and the world menu with its messages take theirs from a table; the literals of the `Temporary` panels and the F2 and F3 menus stay English by decision, and a technical failure that the client finds shows the text of its code while its English diagnostic goes to the log |
| Player presence: join, leave, and disconnect | Provisional | Of a live character only its items, place, facing, health, and mana are stored. A saved place counts only for the identical collision revision of the whole World, so a change of any part of the collision puts every character at the spawn, and one slot per character holds the place of its latest session only, whatever World it was in. A body stays 20 seconds everywhere until gameplay rule areas give each area its own time, there is no leave that takes the body at once, and the client learns no reconnect window from the server. Deleting a character while its session is active is not decided, and nothing confirms that the client has entered the World before its entity can be attacked |
| The collision data format and its streaming | Provisional | |
| Database migrations: the runner, the frozen history, the expand and contract rule with its checks, and the `migrate` command | Foundation | The contract that drops `ticket_hash` of `202611171400_join_ticket_transport_key` is open and listed. A seeder's plan tolerates a missing table but not a missing column. The baseline puts the local Fleet, Node, and Shard rows into every database |
| Retention and scheduled maintenance | Provisional | Not measured at a table that holds 14 days of history: the soak judges two tables at four minutes of depth. Retention deletes and has no archive, and nothing covers vacuum for the history tables |
| The economy audit: its checks, the scheduled run, the `audit` command, and its use in the load tool and the tests | Provisional | It reports and repairs nothing: no repair tools, admin screens, or history browser. Conservation is checked only per retained operation and only for the operation kinds of Items |
| Worker and GameService resilience: lease durations, heartbeats, and backpressure | Provisional | Reviewed with the findings fixed, but only one fault is measured: a GameService outage of 60 seconds at 100 players. At 500 players the generator fell behind, so the run does not count, and a PostgreSQL restart, a Redis stall, a worker kill with players, a partition, and a slow GameService have no scenario. A replaced runtime can evict its live replacement under gross clock skew, as [Architecture](PROJECT_ARCHITECTURE.md) describes |
| World actors, interaction, and capabilities | Provisional | |
| Combat, Mobs, and their numbers | Provisional | |
| Quests | Provisional | |
| Character progression: levels, experience, and the progression rules | Provisional | Every number is a first draft for balancing |
| Economy, vendors, and gathering | Provisional | |
| Character visuals: bodies, garments, cloth, and appearance | Provisional | With the rebuilt human bodies, 400 bodies in view miss 60 frames per second at the 95th percentile and have arrival frames over 33.3 ms, and why `development` measured slower than on 2026-11-17 is not found, as [Crowd Performance](UNITY_CLIENT_ARCHITECTURE.md#crowd-performance) records. In the jog, the trousers come through a top at the back of the hip, where they stay drawn, by up to 1.9 mm on the woman and 0.9 mm on the man through a shirt, 2.2 mm and 1.1 mm through the leather jerkin, and 2.7 mm and 1.1 mm through the net shirt, which hides nothing: there they lie within the hem's hide margin on one of the bodies, or hiding them would show a hole on the woman, whose top lifts off her back, in the jog or in the fall of Death. In the woman's jog, skin shows at the back of her hip between the leather jerkin's hem and the trousers' waistband, since each garment leaves the skin near its own edge drawn; that needs a cover rule for garments worn together. The cloth on the rebuilt bodies is not tuned: [Measuring The Cloth](EQUIPMENT_VISUALS_DESIGN.md#measuring-the-cloth) holds what it does now |
| The art pipeline | Provisional | |
| The content tools and the World Builder | Provisional | |
| The client's realtime hub, session state, and scene flow | Provisional | The reconnect window mirrors the worker's 20 s body time in the client's configuration instead of coming from the worker |
| The UI kit and the windows built on it | Provisional | |
| The UI window manager: the layer table, the window stack, Escape, the keys of the interface, and the typing guard | Provisional | No dragged windows, saved positions, or docking |
| Monitoring, the client measurements, stress tools, the network impairment proxy, and bots | Provisional | |
| The deploy shape: the server images, the production-like stack, its smoke test, and the runbook | Provisional | One GameService and one worker on one machine with a local certificate authority and the local topology; no real host, domain, certificate, image registry, secret store, automated deployment, scheduled backup, or account recovery, as [Deployment](DEPLOYMENT.md#not-there-yet) lists |
| The client's player build, its environments, and the build stamp | Provisional | One target, Windows x64 with IL2CPP; one GameService address per environment file; no signing, installer, or update channel; the F2 Debug menu with Leave Shard stays in a release build until a game menu exists |
| The panels named `Temporary` | Temporary | Move onto the UI kit |
| The development Worlds | Temporary | Test maps, replaced by the first real World |
| Placeholder item icons | Temporary | |

## Where To Read Next

The [documentation map](README.md) lists every document. Most readers continue with
[Project Architecture](PROJECT_ARCHITECTURE.md), [Game Design](GAME_DESIGN.md), and
[Local Development](LOCAL_DEVELOPMENT.md).

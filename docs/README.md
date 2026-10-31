# Documentation

Every document has one responsibility, and every fact lives in exactly one document. Other
documents link to it instead of repeating it, so nothing drifts apart. Start with
[Project Overview](PROJECT_OVERVIEW.md).

## Start Here

- [Project Overview](PROJECT_OVERVIEW.md): what the game is today, how the parts fit together,
  and the status of every area.
- [Local Development](LOCAL_DEVELOPMENT.md): set up, run the stack, run the checks, and carry a
  change through a pull request.
- [Manual Testing](MANUAL_TESTING.md): try every feature by hand in the running game.

## Product Design

What the game should be and why. Each design document marks what is implemented and what is
planned.

- [Game Design](GAME_DESIGN.md): vision, setting, progression, movement, combat, magic, PvP,
  professions, the MVP scope and its slices, and the open questions.
- [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md): items, equipment,
  storage, carry weight, currency, vendor trade, death loot, corpses, and Recovery Storage.
- [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md): the world actor model, NPC
  capabilities, Mobs, resource nodes, spawns, and interaction.
- [Equipment Visuals Design](EQUIPMENT_VISUALS_DESIGN.md): how bodies, garments, hair, cloth,
  and carried equipment look.

## Architecture

How the system is built.

- [Project Architecture](PROJECT_ARCHITECTURE.md): terminology, topology, process boundaries,
  data ownership, trust boundaries, and the core runtime flows.
- [GameService Modules](GAME_SERVICE_MODULES.md): the GameService module projects, their
  contracts, tables, lock order, migrations, and guard tests.
- [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md): the client assemblies, folders,
  runtime structure, networking, content loading, and client flows.
- [Realtime Protocol](REALTIME_PROTOCOL.md): the binary wire format, the schema and its
  generator, the messages, and versioning.

## Implemented Features

What exists today.

- [Game Features](GAME_FEATURES.md): what a player can do and see, with the controls.
- [Service Features](SERVICE_FEATURES.md): the server behavior seen from outside: the HTTP API,
  error codes, limits, defaults, and configuration.

## Guides

How to work on a part of the project.

- [Local Development](LOCAL_DEVELOPMENT.md) and [Manual Testing](MANUAL_TESTING.md).
- [Art Pipeline](ART_PIPELINE.md): 3D models from Blender to Unity, with every how-to.
- [Development Worlds](DEVELOPMENT_WORLDS.md): the two development maps and how they are
  rebuilt.
- [Monitoring](MONITORING.md): metrics, logs, traces, dashboards, alerts, and the local
  monitoring stack.
- [Stack Stress Testing](STACK_STRESS_TESTING.md): repeatable load tests of a worker or the full
  stack.
- [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md): a long-running bot population on a local
  shard.
- [Game Content](../content/README.md) and [Item Catalog Authoring](../content/items/README.md):
  the authored content, its rules, and its compilers.
- [Backend Tests](../tests/README.md): the test projects, where a new test goes, and the test
  databases.
- [Tools](../tools/README.md): every script and tool.

## Where Things Belong

| To document | Write it in |
| --- | --- |
| A game rule, its intent, or a planned feature | The matching design document |
| A boundary, ownership, threading, or flow between processes | [Project Architecture](PROJECT_ARCHITECTURE.md) |
| GameService module structure, tables, locks, or migrations | [GameService Modules](GAME_SERVICE_MODULES.md) |
| A Unity class structure, client flow, or extension rule | [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md) |
| A realtime message or the current protocol version | [Realtime Protocol](REALTIME_PROTOCOL.md) |
| What the player sees and the controls | [Game Features](GAME_FEATURES.md) |
| An endpoint, error code, limit, default, or configuration option | [Service Features](SERVICE_FEATURES.md) |
| A metric, log, trace, dashboard, or alert | [Monitoring](MONITORING.md) |
| How to check a feature by hand | [Manual Testing](MANUAL_TESTING.md) |
| Setup, running the stack, checks, pull requests, Unity Editor automation, or operations | [Local Development](LOCAL_DEVELOPMENT.md) |
| Making or changing art | [Art Pipeline](ART_PIPELINE.md) |
| Authoring content or adding a kind of content | [Game Content](../content/README.md) |
| A test project, test rule, or test database | [Backend Tests](../tests/README.md) |
| A new script or tool | [Tools](../tools/README.md) and the guide of its area |

## Documentation Rules

- Document a change in the same pull request as the code. A new feature usually touches its
  design document (the rule), a features document (the behavior), Manual Testing (how to check
  it), and, when they change, the architecture, protocol, and guide documents.
- Write each fact once, in the document that owns it, and link to it from elsewhere. Do not
  repeat version numbers, revisions, or counts that change with every feature, such as the
  protocol version or the number of item definitions.
- Describe the current state. History lives in Git: no "Last updated" lines, dates of decisions,
  pull request numbers, delivery phases, or decision ids. State a decision as the rule it is.
- Document implemented behavior as implemented and mark planned or deferred work clearly.
- Use World, Fleet, Node, SimulationWorker, SimulationAssignment, Shard, Zone, and Layer exactly
  as [Project Architecture](PROJECT_ARCHITECTURE.md#canonical-terminology) defines them. There
  are no realms, and World never names a process.
- Every change states how to test it by hand, with commands and the expected result, and whether
  it needs manual Unity Editor steps. Lasting manual checks belong in
  [Manual Testing](MANUAL_TESTING.md); exact Unity Editor steps belong in the guide of the area.
- Only the current UI may be documented or implemented as temporary. Every other system is a
  maintainable foundation.
- Keep this map, the root `README.md`, and the links current when a document or heading is added,
  renamed, or removed. Code comments refer to some headings; update them when a heading changes.
- Write plain English in the present tense, never use the em dash, use Title Case headings, and
  wrap prose at about 80 columns.

# MMORPG

MMORPG is an early classless, profession-driven, open-world MMORPG with a World of Warcraft
style fantasy setting and RuneScape-inspired click-to-move combat. This repository holds the
Unity client, the .NET backend, the code and content they share, and the Blender sources of the
3D art. What the game is meant to become is in [Game Design](docs/GAME_DESIGN.md), and what
exists today in [Project Overview](docs/PROJECT_OVERVIEW.md).

## How It Fits Together

```text
Unity client --HTTP--> GameService --> PostgreSQL, Redis
     |
     +------UDP------> SimulationWorker (one per shard) --HTTP--> GameService
```

- **GameService** is the ASP.NET Core service for everything durable and global: accounts,
  characters, shard placement, items, and currency, stored in PostgreSQL.
- **SimulationWorker** is the headless authoritative simulation of one shard: movement, combat,
  NPCs, Mobs, and interaction, with clients connected over LiteNetLib UDP.
- **Unity client** presents the game, predicts the player's own movement, and never owns game
  rules.

A World is shared map and content data, not a server. There are no realms. The terms and
boundaries are defined in [Project Architecture](docs/PROJECT_ARCHITECTURE.md).

## Requirements

- .NET SDK 10.0.301 or a newer .NET 10 SDK (`global.json`).
- Docker Desktop with Linux containers.
- Unity Editor 6000.6.7f1.
- Optional: the Unity CLI for Editor automation, and Blender 5.2 for art work.

## Quick Start

Create the ignored local environment file once and replace its placeholder credentials:

```powershell
Copy-Item .env.example .env
```

Start PostgreSQL and Redis, then run the two backend processes in separate terminals:

```powershell
docker compose up -d --wait
```

```powershell
dotnet run --project src/Server/GameService
```

```powershell
dotnet run --project src/Server/SimulationWorker
```

Open `src/Client` in Unity and enter Play Mode from `Assets/Scenes/LoginMenu.unity`. Register,
create a character, and join `Local Shard 1`.

Expected result: GameService listens on `http://localhost:5000`, SimulationWorker binds UDP
`27015` and brings `local-shard-1` online, and the character spawns in Development World 2, the
test World with a zone for every feature. A second worker for the stress arena, the optional
monitoring stack, and every other setup detail are in
[Local Development](docs/LOCAL_DEVELOPMENT.md). How to try each feature by hand is in
[Manual Testing](docs/MANUAL_TESTING.md).

## Checks

Before every push, run the checks that the changes of the branch can affect:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Changes.ps1
```

CI runs the full quality gate for every pull request. Its command list is in
[Repository Quality Checks](docs/LOCAL_DEVELOPMENT.md#repository-quality-checks), next to the
checks for a change and the pull request workflow, and the test projects are described in
[Backend Tests](tests/README.md).

## Repository Layout

| Path | Contents |
| --- | --- |
| `src/Server/GameService` | GameService host: startup, configuration, database startup, health |
| `src/Server/GameService.*` | GameService modules (Platform, Accounts, Characters, Placement, Items) that reach each other only through published contracts. See [GameService Modules](docs/GAME_SERVICE_MODULES.md) |
| `src/Server/SimulationWorker` | The headless authoritative shard simulation |
| `src/Server/Common`, `src/Server/Telemetry` | Helpers and OpenTelemetry setup shared by the server processes |
| `src/Shared/GameProtocol` | The realtime protocol: schema, generated code, and validation rules, as a Unity package |
| `src/Shared/GameSimulation` | Movement and collision code shared by the server and client prediction, as a Unity package |
| `src/Shared/WorldData` | Shared domain rules, content compilers, Unity content tools, and the data generated from `content`, as a Unity package |
| `src/Shared/DotNet` | .NET projects that compile the shared packages for the server |
| `src/Client` | The Unity project and its tests. See [Unity Client Architecture](docs/UNITY_CLIENT_ARCHITECTURE.md) |
| `content` | Authored game content: items, actors, Worlds, combat rules. See [Game Content](content/README.md) |
| `art` | Blender sources of the 3D models with their review records. See [Art Pipeline](docs/ART_PIPELINE.md) |
| `tests` | Backend tests. See [Backend Tests](tests/README.md) |
| `tools` | Checks, compilers, generators, load tools, and the art pipeline. See [Tools](tools/README.md) |
| `observability` | Configuration of the local monitoring stack. See [Monitoring](docs/MONITORING.md) |
| `docs` | Design, architecture, features, and guides. See [Documentation](docs/README.md) |

Repository-wide Compose, SDK, solution, build, and environment files stay in the root. Service
settings live in each service's `Config` folder.

## Documentation And Rules

Start with [Project Overview](docs/PROJECT_OVERVIEW.md); the [documentation map](docs/README.md)
lists every document and what it owns. Repository rules for every contributor and coding agent
are in [AGENTS.md](AGENTS.md); [CLAUDE.md](CLAUDE.md) adds the workflow notes for Claude Code.

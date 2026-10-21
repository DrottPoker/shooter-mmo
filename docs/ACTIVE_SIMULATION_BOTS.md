# Active Simulation Bots

Last updated: 2026-10-21

## Purpose

`tools/ActiveSimulationBots` is a long-running local development population
driver. It keeps configurable headless bots connected to the real GameService
and SimulationWorker while a Unity player can use the same shard. The bots use
the normal LiteNetLib UDP connection, one-time join ticket, server-provided
simulation tick rate, sequenced movement input, entity lifecycle, snapshots,
session heartbeat, and graceful leave flow.

Use this tool to inspect a populated World, remote interpolation, reliable
spawn and despawn, interest behavior, collision, movement, and connection churn.
By default the bots join `local-shard-2`, the stress arena in Development
World 1, where every bot stays inside one interest radius.
The bots behave like Unity clients on the wire: each one sends one movement
input per simulation tick, plans its walks with the same planner and collision as
the client, and decodes every message a client receives.
Bots are not combatants, because they have no durable character that could die,
so a Unity player cannot attack them.
It is not a capacity benchmark. Use
[Stack Stress Testing](STACK_STRESS_TESTING.md) for repeatable worker-only or
full-stack ramp, soak, resource, and latency measurements.

## Architecture And Safety Boundary

```text
ActiveSimulationBots -- protected loopback HTTP --> GameService
ActiveSimulationBots -------- LiteNetLib UDP ----> SimulationWorker
Unity client ----------------- LiteNetLib UDP ----> SimulationWorker

GameService and SimulationWorker: normal local processes
PostgreSQL: topology and real players only
Development bot identities, tickets, and sessions: GameService memory only
```

The login bypass ends at the development ticket endpoint. A bot never bypasses
SimulationWorker admission or UDP session validation. GameService generates an
in-memory account id, character id, and name such as `Active Bot 1`, binds a
short-lived one-time ticket to the exact Shard, worker, and runtime, and validates
the in-memory session token for every worker heartbeat and release.

The development ticket endpoint has four independent guards:

- GameService must run in the `Development` environment.
- `DevelopmentSimulationBots:Enabled` must be explicitly true.
- The request must originate over loopback.
- The request must carry the separate `ACTIVE_SIMULATION_BOTS_SECRET`, which
  must contain at least 32 characters and cannot be the checked-in placeholder.

The endpoint is not mapped when the feature is disabled. GameService refuses to
start if it is enabled outside `Development` or if its secret is too short.
Bot identities, tickets, and sessions are never inserted into PostgreSQL and
disappear when GameService stops.

GameService counts the worker's reported connections, durable active sessions,
durable pending tickets, in-memory bot sessions, and in-memory pending bot
tickets before admitting another bot, and keeps `ReservedPlayerConnections` free
for real players. Player placement counts the same in-memory bot sessions and
pending bot tickets, so a bot ticket that has not connected yet still holds its
connection. SimulationWorker `MaxConnections` is the single server-side
connection limit. A bot ticket and a player ticket issued at the same moment
can both pass, because bot tickets are not database rows; the reserved player
connections absorb that overlap.

## Configuration

Authority controls live in
`src/Server/GameService/Config/appsettings.Development.json`:

| Setting | Default | Behavior |
| --- | ---: | --- |
| `Enabled` | `false` | Maps the loopback development ticket endpoint |
| `JoinTicketLifetimeSeconds` | `30` | Lifetime of an unconsumed one-time bot ticket |
| `SessionLeaseLifetimeSeconds` | `30` | Worker-renewed in-memory bot session lease |
| `CharacterNamePrefix` | `Active Bot` | Server-owned visible bot name prefix |
| `ReservedPlayerConnections` | `8` | Worker connections bots never use, so real local players can still join |

Population and behavior controls live in
`tools/ActiveSimulationBots/appsettings.json`:

| Setting | Default | Behavior |
| --- | ---: | --- |
| `AuthorityUrl` | `http://127.0.0.1:5000` | Real local GameService URL, loopback only |
| `AuthoritySecret` | empty | Optional local override; prefer the ignored `.env` value |
| `ShardId` | `local-shard-2` | Shard requested for every bot: the stress arena. Use `local-shard-1` for a population spread over Development World 2 |
| `MinimumActiveBots` | `300` | Lower bound for randomly selected population targets |
| `MaximumActiveBots` | `400` | Upper bound and number of stable bot slots |
| `StartupBotsPerSecond` | `20` | Maximum ticket and connection ramp rate |
| `PopulationChangeIntervalSeconds` | `30` | Interval between random target changes |
| `MinimumOnlineSeconds`, `MaximumOnlineSeconds` | `120`, `240` | Random joined lifetime before graceful logout |
| `MinimumOfflineSeconds`, `MaximumOfflineSeconds` | `5`, `25` | Random rest before the same slot may log in again |
| `MinimumBehaviorSeconds`, `MaximumBehaviorSeconds` | `2`, `6` | Random behavior duration; a walk heads for a destination about that many seconds of walking away |
| `IdleChance` | `0.15` | Chance that a new behavior interval is idle |
| `JoinAndLeaveTimeoutSeconds` | `10` | UDP handshake and graceful leave deadline |
| `MinimumRetrySeconds`, `MaximumRetrySeconds` | `2`, `30` | Exponential reconnect range with jitter |
| `ShutdownTimeoutSeconds` | `15` | Ctrl+C graceful leave deadline |
| `StatusIntervalSeconds` | `5` | Console population and traffic report interval |
| `Seed` | `1337` | Repeatable population and movement random source |

The tool validates all ranges before opening a connection. Real environment
variables and command-line configuration keys override the checked-in JSON.
Keep the tool's `MaximumActiveBots` at or below SimulationWorker
`MaxConnections`, allowing room for any real players you want to join during the
test. The tool setting controls the requested population and is not a separate
server-side admission limit. Higher targets are rejected safely by worker
capacity admission but cannot be reached.
For example:

```powershell
dotnet run --project tools/ActiveSimulationBots -- `
  --ActiveSimulationBots:MinimumActiveBots 10 `
  --ActiveSimulationBots:MaximumActiveBots 25
```

## Run The Local Population

Create `.env` from `.env.example` and set these entries to local values:

```dotenv
DevelopmentSimulationBots__Enabled=true
ACTIVE_SIMULATION_BOTS_SECRET=replace-with-a-separate-secret-of-at-least-32-characters
```

Do not commit a real secret. The same ignored `.env` is discovered by
GameService and ActiveSimulationBots.

Start the normal local stack in separate PowerShell terminals:

```powershell
docker compose up -d --wait
```

```powershell
dotnet run --project src/Server/GameService
```

```powershell
dotnet run --project src/Server/SimulationWorker --launch-profile StressArena
```

The `StressArena` launch profile runs the stress arena worker for
`local-shard-2` on UDP port `27016`, as described in
[Run The Stress Arena Shard](LOCAL_DEVELOPMENT.md#run-the-stress-arena-shard).
Start the default worker as well, with `dotnet run --project
src/Server/SimulationWorker`, to keep `local-shard-1` online beside it.

Then start the active population:

```powershell
dotnet run --project tools/ActiveSimulationBots --configuration Release
```

Expected result:

- The tool ramps to at least the configured minimum target.
- GameService logs successful development ticket requests without creating bot
  account or character rows.
- ActiveSimulationBots logs joined characters named `Active Bot 1`,
  `Active Bot 2`, and so on. SimulationWorker reports their aggregate count
  without emitting one information log per successful synthetic join.
- Every metrics interval, SimulationWorker's worker status line separates the
  joined synthetic bot count from joined real players and reports worker CPU,
  working set, traffic rates, snapshot drops, and quota rejections.
- The performance line reports join queue delay, join-finalization latency, and
  the current and maximum completed-operation backlog. Queue delay separates
  time waiting for the simulation loop from finalization work. A healthy ramp
  returns the backlog to zero instead of allowing joins to wait behind growing
  lifecycle work.
- Bots send one movement input per tick at the tick rate returned by
  SimulationWorker. The tool polls every bot every 3 ms with a high-resolution
  timer, and a bot sends a due input in the same poll, so inputs reach the
  worker about as evenly as from a Unity client. `Task.Delay` used to wait a
  whole 15.6 ms Windows timer tick, and the input left only on the next poll, so
  bots sent their inputs in bursts.
- A walking bot heads for a random destination inside the World bounds, about
  one behavior interval of walking and at most 60 meters away. The tool loads
  the World's compiled collision, which the build copies to its output below
  `WorldData/Worlds`, and plans the walk around obstacles with the shared
  `MovementPathPlanner` on up to four background threads, as the Unity client
  plans a click. The bot keeps its previous path until the plan arrives, and it
  stands when no route exists or the planners are busy. When the collision
  cannot be loaded, or the worker reports another collision revision, a walk is
  a straight one-waypoint line of at most 200 meters that the worker's collision
  stops. The startup log says which applies. The path travels with the input
  batches until an owner snapshot confirms it, exactly as from the Unity
  client.
- The target population changes within the configured range.
- Individual bots issue a normal reliable leave, disappear, wait for a random
  offline interval, request a fresh one-time ticket, and rejoin.
- Intentional target changes and churn do not log out a bot when doing so would
  take the joined population below `MinimumActiveBots`. Connection admission
  counts leaving bots until their leave completes, so churn does not exceed
  `MaximumActiveBots`. Worker failures and rejected admission can still cause a
  temporary population below the requested minimum.
- Console status reports joined, joining, leaving, ticket request, lifetime
  login, clean logout, failure, rejection, and packet counts.
- When SimulationWorker `MaxConnections` would be crossed, GameService returns
  `development_bot_worker_capacity_reached` and the affected slot retries with
  exponential backoff and jitter.

Press Ctrl+C once. Expected result: joined bots request graceful leave, the tool
waits up to `ShutdownTimeoutSeconds`, prints its final status, and exits.

## Visual Unity Test

No Unity scene, prefab, package, Inspector, or build-setting changes are
required.

1. Start GameService, the stress arena SimulationWorker, and ActiveSimulationBots
   as described above.
2. Open `src/Client` in Unity.
3. Enter Play Mode from `Assets/Scenes/LoginMenu.unity`.
4. Log in with a normal account, select a character, and join
   `Local Stress Arena` (`local-shard-2`).
5. Move around the arena while bots log in, move, and log out. Walk to the wolf
   pen opening to fight wolves inside the crowd.
6. Press F2 and verify the client-observed frame, prediction, snapshot, and
   payload metrics while the crowd moves.
7. Keep the Unity Console visible for protocol, collision revision, or
   disconnect errors.

Expected result: the local player uses the normal authenticated flow and is
purple. SimulationWorker spawns every bot with the presentation archetype
`player.development_bot`, so remote `Active Bot N` instances are blue, while a
second Unity client's character is orange. Bots appear only after reliable
spawn messages, movement is
interpolated from SimulationWorker snapshots, and graceful bot logout removes
the matching remote instance through a reliable despawn. The 64 m stress arena
keeps every client, bot, and actor within the same interest area, so it is a
dense visual crowd test.

The F2 panel intentionally does not reveal the server-wide real-player count,
bot count, CPU, memory, or internal tick timing. Verify those values in the
SimulationWorker terminal status and performance lines.

## Scope Boundary

ActiveSimulationBots is development-only synthetic population. It does not
measure GameService or PostgreSQL capacity, create persistent accounts, exercise
password authentication, validate final AI behavior, or establish production
concurrency limits. It must never be enabled in staging or production.

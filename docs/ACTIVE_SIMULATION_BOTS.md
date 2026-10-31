# Active Simulation Bots

## Purpose

`tools/ActiveSimulationBots` is a long-running local development population
driver. It keeps configurable headless bots connected to the real GameService
and SimulationWorker while a Unity player uses the same Shard. The bots use the
normal LiteNetLib UDP connection, one-time join ticket, server-provided
simulation tick rate, sequenced movement input, entity lifecycle, snapshots,
session heartbeat, and graceful leave flow.

Use it to inspect a populated World, remote interpolation, reliable spawn and
despawn, interest behavior, collision, movement, connection churn, and how the
client copes with a crowd. By default the bots join `local-shard-2`, the stress
arena in Development World 1, where every bot stays inside one interest radius.
The bots behave like Unity clients on the wire: each one sends one movement
input per simulation tick, plans its walks with the same planner and collision
as the client, and decodes every message a client receives. Bots are not
combatants, because they have no durable character that could die, so a Unity
player cannot attack them.

The tool is not a capacity benchmark. Use
[Stack Stress Testing](STACK_STRESS_TESTING.md) for repeatable worker-only or
full-stack ramp, soak, resource, and latency measurements.

## Protocol Handling

Every bot, here and in the stack stress generator, runs one
`SimulationBotSession` from `tools/SimulationBotClient`: the protocol side of
the connection, apart from the LiteNetLib transport in `SimulationBotClient`.
The session runs on the timestamps its caller passes in, so tests drive it
without a network.

- Every message must arrive on the channel and with the delivery that its
  descriptor in the protocol schema names, and must decode. The bot also sends
  every message with the channel and delivery of its descriptor.
- A snapshot can overtake the reliable join acceptance, so one that arrives
  before it is dropped. Any other session message before the acceptance fails
  the bot.
- While a leave is pending, the bot accepts every message of a joined session,
  because the worker keeps sending until it has released the session.
- The bot acts only on owner snapshots and on the results of its own item and
  corpse operations. It decodes and drops everything else, such as the look the
  worker sends every player when it joins, and the looks of other players.
- A server-to-client message type the session does not handle fails the bot
  with `unexpected_message`. `SimulationBotSessionTests` in
  `tests/Tools/Mmorpg.Tools.Tests` feeds the golden vector of every message type
  through the session, so a new message type fails that test until the session
  handles it, instead of failing every bot at runtime.

## Architecture And Safety Boundary

```text
ActiveSimulationBots -- protected loopback HTTP --> GameService
ActiveSimulationBots -------- LiteNetLib UDP ----> SimulationWorker
Unity client ----------------- LiteNetLib UDP ----> SimulationWorker

GameService and SimulationWorker: normal local processes
PostgreSQL: topology and real players only
Development bot identities, tickets, and sessions: GameService memory only
```

The login bypass ends at the development ticket endpoint,
`POST /api/development/simulation-bots/join-tickets`. A bot never bypasses
SimulationWorker admission or UDP session validation. GameService generates an
in-memory account id, character id, and name such as `Active Bot 1`, binds a
short-lived one-time ticket to the exact Shard, worker, and runtime, and
validates the in-memory session token for every worker heartbeat and release.

The development ticket endpoint has four independent guards:

- GameService must run in the `Development` environment.
- `DevelopmentSimulationBots:Enabled` must be explicitly true.
- The request must originate over loopback.
- The request must carry the separate `ACTIVE_SIMULATION_BOTS_SECRET` in the
  `X-Active-Simulation-Bots-Key` header. The secret must contain at least 32
  characters and cannot be the checked-in placeholder.

The endpoint is not mapped when the feature is disabled. GameService refuses to
start if it is enabled outside `Development` or if its secret is too short or a
placeholder. Bot identities, tickets, and sessions are never inserted into
PostgreSQL and disappear when GameService stops.

GameService counts the worker's reported connections, durable active sessions,
durable pending tickets, in-memory bot sessions, and in-memory pending bot
tickets before admitting another bot, and keeps `ReservedPlayerConnections` free
for real players. Player placement counts the same in-memory bot sessions and
pending bot tickets, so a bot ticket that has not connected yet still holds its
connection. SimulationWorker `MaxConnections` is the single server-side
connection limit. A bot ticket and a player ticket issued at the same moment
can both pass, because bot tickets are not database rows; the reserved player
connections absorb that overlap.

A bot asks for a new ticket only after it gave up its previous connection, so a
new request supersedes the bot's own unconsumed ticket, as a player's new join
supersedes the account's pending ticket. A ticket of a bot whose join timed out
therefore neither blocks the bot's next attempt nor holds a connection until it
expires. A bot whose session is still active gets
`development_bot_already_active` instead.

## Configuration

The authority settings live in the `DevelopmentSimulationBots` section of
`src/Server/GameService/Config/appsettings.Development.json`:

| Setting | Default | Allowed | Behavior |
| --- | ---: | --- | --- |
| `Enabled` | `false` | | Maps the loopback development ticket endpoint |
| `JoinTicketLifetimeSeconds` | `30` | 5 to 300 | Lifetime of an unconsumed one-time bot ticket |
| `SessionLeaseLifetimeSeconds` | `30` | 10 to 300 | Worker-renewed in-memory bot session lease |
| `CharacterNamePrefix` | `Active Bot` | 1 to 32 characters | Server-owned visible bot name prefix |
| `ReservedPlayerConnections` | `8` | 0 to 1000 | Worker connections bots never use, so real local players can still join |

The population and behavior settings live in the `ActiveSimulationBots` section
of `tools/ActiveSimulationBots/appsettings.json`:

| Setting | Default | Allowed | Behavior |
| --- | ---: | --- | --- |
| `AuthorityUrl` | `http://127.0.0.1:5000` | Loopback HTTP URL | Real local GameService URL |
| `AuthoritySecret` | empty | At least 32 characters | Optional local override; prefer the ignored `.env` value of `ACTIVE_SIMULATION_BOTS_SECRET` |
| `ShardId` | `local-shard-2` | Shard id | Shard requested for every bot: the stress arena. Use `local-shard-1` for a population spread over Development World 2 |
| `MinimumActiveBots` | `300` | 0 to 10,000 | Lower bound for randomly selected population targets |
| `MaximumActiveBots` | `400` | 1 to 10,000 | Upper bound and number of stable bot slots |
| `StartupBotsPerSecond` | `20` | 0.1 to 1000 | Maximum ticket and connection ramp rate; every request that is due starts in the same poll, so rates above one per poll hold |
| `PopulationChangeIntervalSeconds` | `30` | 1 to 3600 | Interval between random target changes |
| `MinimumOnlineSeconds`, `MaximumOnlineSeconds` | `120`, `240` | 1 to 86,400 | Random joined lifetime before graceful logout |
| `MinimumOfflineSeconds`, `MaximumOfflineSeconds` | `5`, `25` | 0 to 86,400 | Random rest before the same slot may log in again |
| `MinimumBehaviorSeconds`, `MaximumBehaviorSeconds` | `2`, `6` | 1 to 3600 | Random behavior duration; a walk heads for a destination about that many seconds of walking away |
| `IdleChance` | `0.15` | 0 to 1 | Chance that a new behavior interval is idle |
| `JoinAndLeaveTimeoutSeconds` | `10` | 1 to 300 | UDP handshake and graceful leave deadline |
| `MinimumRetrySeconds`, `MaximumRetrySeconds` | `2`, `30` | 1 to 3600 | Exponential reconnect range with jitter; only a clean logout resets it, so a bot that fails right after every join backs off |
| `ShutdownTimeoutSeconds` | `15` | 1 to 300 | Ctrl+C graceful leave deadline |
| `StatusIntervalSeconds` | `5` | 1 to 3600 | Console population and traffic report interval |
| `Seed` | `1337` | Any integer | Repeatable population and movement random source; the same seed gives every bot the same walks in every run |

Each minimum must not exceed its maximum. The tool validates every setting
before it opens a connection and exits with code 2 when one is invalid. `.env`
overrides the checked-in JSON, real environment variables override `.env`, and
command-line keys override both, for example:

```powershell
dotnet run --project tools/ActiveSimulationBots -- `
  --ActiveSimulationBots:MinimumActiveBots 10 `
  --ActiveSimulationBots:MaximumActiveBots 25
```

Keep `MaximumActiveBots` at or below SimulationWorker `MaxConnections`, with
room for the real players you want to join. The tool setting controls the
requested population and is not a separate server-side admission limit: worker
capacity admission rejects higher targets safely, so they cannot be reached.

## Run The Local Population

Set these entries in `.env`, as
[Local Environment File](LOCAL_DEVELOPMENT.md#local-environment-file) describes,
and never commit a real secret. GameService and ActiveSimulationBots both read
the same ignored `.env`.

```dotenv
DevelopmentSimulationBots__Enabled=true
ACTIVE_SIMULATION_BOTS_SECRET=replace-with-a-separate-secret-of-at-least-32-characters
```

Start the local infrastructure, GameService, and the stress arena worker with
the `StressArena` launch profile, as
[Local Infrastructure](LOCAL_DEVELOPMENT.md#local-infrastructure),
[Backend Services](LOCAL_DEVELOPMENT.md#backend-services), and
[Run The Stress Arena Shard](LOCAL_DEVELOPMENT.md#run-the-stress-arena-shard)
describe. Start the default worker as well to keep `local-shard-1` online beside
it. Then start the population:

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
  returns the backlog to zero instead of letting joins wait behind growing
  lifecycle work.
- Bots send one movement input per tick at the tick rate returned by
  SimulationWorker. The tool polls every bot every 3 ms with a high-resolution
  timer, and a bot sends a due input in the same poll, so inputs reach the
  worker about as evenly as from a Unity client.
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
- A ticket response the bot cannot use counts as a ticket rejection, with
  `invalid_authority_response`, instead of stopping the tool. Like the Unity
  client, a bot never connects to a worker on another protocol version, and the
  slot reports `protocol_version_mismatch` instead.
- A bot whose session failed or completed releases its UDP connection in the
  same poll, so the worker frees its character at once. The transport drops the
  connection after 10 seconds without any packet, the Unity client's default;
  `JoinAndLeaveTimeoutSeconds` only bounds the join and the leave.

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
5. Move around the arena while bots log in, move, and log out. Walk to the
   opening of the wolf pen to fight wolves inside the crowd.
6. Press F2 to open the Debug menu, check the client-observed frame,
   prediction, snapshot, and payload metrics on its tabs while the crowd moves,
   and turn on `Tint bodies: you purple, players orange, bots blue` on its
   `Overview` tab.
7. Keep the Unity Console visible for protocol, collision revision, or
   disconnect errors.

Expected result: the local player uses the normal authenticated flow. Every bot
shows a body model, look, and outfit that SimulationWorker picks from its
character id, so the crowd shows both bodies and varied faces, hair, and
clothes, and each bot keeps its look while it stays. Every body walks and jogs
with its speed. With `Tint bodies` on, the local player is tinted purple, every
`Active Bot N` blue, because SimulationWorker spawns bots with the presentation
archetype `player.development_bot`, and a second Unity client's character
orange. Bots appear only after reliable spawn messages, movement is
interpolated from SimulationWorker snapshots, and a graceful bot logout removes
the matching remote instance through a reliable despawn. Bots that come into
interest at once, as at a join, get their bodies over a few frames, the nearest
first, so the frame rate does not drop while they appear. The 64 m stress arena
keeps every client, bot, and actor within the same interest area, so it is a
dense visual crowd test.

For a crowd spread over a large World, run the population with
`--ActiveSimulationBots:ShardId local-shard-1` and join `Local Shard 1` in
Development World 2. That is the crowd that
[Crowd Performance](UNITY_CLIENT_ARCHITECTURE.md#crowd-performance) measures.

The Debug menu intentionally does not reveal the server-wide real-player count,
bot count, CPU, memory, or internal tick timing. Read those values in the
SimulationWorker terminal status and performance lines.

## Scope Boundary

ActiveSimulationBots is development-only synthetic population. It does not
measure GameService or PostgreSQL capacity, create persistent accounts, exercise
password authentication, validate final AI behavior, or establish production
concurrency limits. It must never be enabled in staging or production.

# Active Simulation Bots

Last updated: 2026-07-14

## Purpose

`Tools/ActiveSimulationBots` is a long-running local development population
driver. It keeps configurable headless bots connected to the real AuthService
and SimulationWorker while a Unity player can use the same shard. The bots use
the normal LiteNetLib UDP connection, one-time join ticket, server-provided
simulation tick rate, sequenced movement input, entity lifecycle, snapshots,
session heartbeat, and graceful leave flow.

Use this tool to inspect a populated test map, remote interpolation, reliable
spawn and despawn, interest behavior, collision, movement, and connection churn.
It is not a capacity benchmark. Use
[Simulation Stress Testing](SIMULATION_STRESS_TESTING.md) for repeatable hotspot,
ramp, soak, resource, and latency measurements.

## Architecture And Safety Boundary

```text
ActiveSimulationBots -- protected loopback HTTP --> AuthService
ActiveSimulationBots -------- LiteNetLib UDP ----> SimulationWorker
Unity client ----------------- LiteNetLib UDP ----> SimulationWorker

AuthService and SimulationWorker: normal local processes
PostgreSQL: topology and real players only
Development bot identities, tickets, and sessions: AuthService memory only
```

The login bypass ends at the development ticket endpoint. A bot never bypasses
SimulationWorker admission or UDP session validation. AuthService generates an
in-memory account id, character id, and name such as `Active Bot 1`, binds a
short-lived one-time ticket to the exact Shard, worker, and runtime, and validates
the in-memory session token for every worker heartbeat and release.

The development ticket endpoint has four independent guards:

- AuthService must run in the `Development` environment.
- `DevelopmentSimulationBots:Enabled` must be explicitly true.
- The request must originate over loopback.
- The request must carry the separate `ACTIVE_SIMULATION_BOTS_SECRET`, which
  must contain at least 32 characters and cannot be the checked-in placeholder.

The endpoint is not mapped when the feature is disabled. AuthService refuses to
start if it is enabled outside `Development` or if its secret is too short.
Bot identities, tickets, and sessions are never inserted into PostgreSQL and
disappear when AuthService stops.

AuthService also reserves `ReservedPlayerSlots` on the selected worker. It
counts the worker's reported connections, durable active sessions, durable
pending tickets, and in-memory pending bot tickets before admitting another bot.
SimulationWorker's own `MaxConnections` remains the final connection limit.

## Configuration

Authority controls live in
`AuthService/Config/appsettings.Development.json`:

| Setting | Default | Behavior |
| --- | ---: | --- |
| `Enabled` | `false` | Maps the loopback development ticket endpoint |
| `MaximumActiveBots` | `200` | Bounds in-memory bot identities in one AuthService process |
| `ReservedPlayerSlots` | `8` | Capacity that development bots cannot consume |
| `JoinTicketLifetimeSeconds` | `30` | Lifetime of an unconsumed one-time bot ticket |
| `SessionLeaseLifetimeSeconds` | `30` | Worker-renewed in-memory bot session lease |
| `CharacterNamePrefix` | `Active Bot` | Server-owned visible bot name prefix |

Population and behavior controls live in
`Tools/ActiveSimulationBots/appsettings.json`:

| Setting | Default | Behavior |
| --- | ---: | --- |
| `AuthorityUrl` | `http://127.0.0.1:5000` | Real local AuthService URL, loopback only |
| `AuthoritySecret` | empty | Optional local override; prefer the ignored `.env` value |
| `ShardId` | `local-shard-1` | Shard requested for every bot |
| `MinimumActiveBots` | `5` | Lower bound for randomly selected population targets |
| `MaximumActiveBots` | `15` | Upper bound and number of stable bot slots |
| `StartupBotsPerSecond` | `3` | Maximum ticket and connection ramp rate |
| `PopulationChangeIntervalSeconds` | `20` | Interval between random target changes |
| `MinimumOnlineSeconds`, `MaximumOnlineSeconds` | `45`, `120` | Random joined lifetime before graceful logout |
| `MinimumOfflineSeconds`, `MaximumOfflineSeconds` | `5`, `25` | Random rest before the same slot may log in again |
| `MinimumDirectionSeconds`, `MaximumDirectionSeconds` | `2`, `6` | Random movement behavior duration |
| `IdleChance` | `0.15` | Chance that a new behavior interval is idle |
| `SprintChance` | `0.45` | Chance that a non-aim behavior requests sprint |
| `AimChance` | `0.20` | Chance that a behavior interval requests aim |
| `JumpChancePerSecond` | `0.08` | Tick-rate-adjusted jump request chance |
| `JoinAndLeaveTimeoutSeconds` | `10` | UDP handshake and graceful leave deadline |
| `MinimumRetrySeconds`, `MaximumRetrySeconds` | `2`, `30` | Exponential reconnect range with jitter |
| `ShutdownTimeoutSeconds` | `15` | Ctrl+C graceful leave deadline |
| `StatusIntervalSeconds` | `5` | Console population and traffic report interval |
| `Seed` | `1337` | Repeatable population and movement random source |

The tool validates all ranges before opening a connection. Real environment
variables and command-line configuration keys override the checked-in JSON.
Keep the tool's `MaximumActiveBots` at or below AuthService's
`MaximumActiveBots` and within SimulationWorker `MaxConnections` after reserved
real-player slots. Higher targets are rejected safely but cannot be reached.
For example:

```powershell
dotnet run --project Tools/ActiveSimulationBots -- `
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
AuthService and ActiveSimulationBots.

Start the normal local stack in separate PowerShell terminals:

```powershell
docker compose up -d --wait
```

```powershell
dotnet run --project AuthService
```

```powershell
dotnet run --project SimulationWorker
```

Then start the active population:

```powershell
dotnet run --project Tools/ActiveSimulationBots --configuration Release
```

Expected result:

- The tool ramps to at least the configured minimum target.
- AuthService logs successful development ticket requests without creating bot
  account or character rows.
- SimulationWorker logs joined characters named `Active Bot 1`, `Active Bot 2`,
  and so on.
- Bots send movement input at the tick rate returned by SimulationWorker.
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
- When capacity reserved for real players would be crossed, AuthService returns
  `development_bot_capacity_reserved` and the affected slot retries with
  exponential backoff and jitter.

Press Ctrl+C once. Expected result: joined bots request graceful leave, the tool
waits up to `ShutdownTimeoutSeconds`, prints its final status, and exits.

## Visual Unity Test

No Unity scene, prefab, package, Inspector, or build-setting changes are
required.

1. Start AuthService, SimulationWorker, and ActiveSimulationBots as described
   above.
2. Open `shooter-mmorpg-unity-client` in Unity.
3. Enter Play Mode from `Assets/Scenes/LoginMenu.unity`.
4. Log in with a normal account, select a character, and join
   `local-shard-1`.
5. Move around the test map while bots log in, move, and log out.
6. Keep the Unity Console visible for protocol, collision revision, or
   disconnect errors.

Expected result: the local player uses the normal authenticated flow, remote
`Active Bot N` instances appear only after reliable spawn messages, movement is
interpolated from SimulationWorker snapshots, and graceful bot logout removes
the matching remote instance through a reliable despawn. The small current map
keeps these clients within the same interest area, so it is useful as a dense
visual crowd test.

## Scope Boundary

ActiveSimulationBots is development-only synthetic population. It does not
measure AuthService or PostgreSQL capacity, create persistent accounts, exercise
password authentication, validate final AI behavior, or establish production
concurrency limits. It must never be enabled in staging or production.

# Stack Stress Testing

## Purpose

`tools/StackStressGenerator` is the repeatable load harness for the realtime
stack. It has two explicit modes so SimulationWorker capacity can be measured
separately from account, placement, session, GameService, and PostgreSQL load.

| Mode | Real GameService | PostgreSQL | Realtime UDP | Primary use |
| --- | :---: | :---: | :---: | --- |
| `worker-only` | No | No | Yes | Isolate SimulationWorker hotspots and capacity |
| `full-stack` | Yes | Yes | Yes | Measure the complete login-to-game lifecycle and shared stack pressure |

Mode selects the stack boundary. Workload selects what every admitted client
does after joining:

| Workload | Supported mode | Durable item traffic | Movement pattern | Primary use |
| --- | --- | :---: | --- | --- |
| `lifecycle` | `worker-only`, `full-stack` | No | Square walking loop | Preserve the comparable admission and realtime baseline |
| `inventory` | `full-stack` | Yes | Square walking loop | Relocate one item between Permanent Inventory and Secure Container |
| `loot-hotspot` | `full-stack` | Yes | Stand at the shared corpse | Contend on one durable Mob corpse with deposit and partial loot |
| `mixed-gameplay` | `full-stack` | Yes | Step back and forth beside the corpse | Combine movement, inventory relocation, corpse deposit, and corpse loot |

Both modes run independent headless LiteNetLib clients through the real join,
movement, snapshot, entity lifecycle, simulation-session, and graceful-leave
paths. Each bot owns an independent UDP endpoint, and the clients use LiteNetLib
manual polling, so the harness does not create one transport thread per bot.
The generator polls every bot every 3 ms with a high-resolution timer and sends
a due input in the same poll, so inputs reach the worker once per tick like a
Unity client's instead of in bursts. A square loop has legs of two seconds of
walking.

Runs target `local-shard-2` and Development World 1, the 64 m stress arena, by
default, and every bot starts on the World spawn, so all bots stay inside the
same interest radius. This intentionally measures a worst-case local crowd, not the
distributed capacity of a future larger World.

Run the stress flow before accepting a new capacity baseline and after changing
the realtime loop, collision, interest, snapshots, session heartbeats, accounts,
placement, or database behavior. Use
[Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) when the goal is a
long-running visible population beside a real local Unity player. That tool is
not a repeatable capacity benchmark.

## Architecture

### Worker-only Mode

```text
StackStressGenerator
  in-memory stress authority <--- authenticated HTTP ---> SimulationWorker
  headless stress bots        -------- LiteNetLib UDP ---> SimulationWorker

GameService: not running
PostgreSQL and Redis: not used
Unity: not used
```

The in-memory authority implements only the service contracts that
SimulationWorker needs: readiness, worker heartbeat and offline registration,
one-time ticket consumption, simulation-session heartbeat and release, and
empty durable-corpse restoration. Identities, tickets, and sessions exist only
for the lifetime of the generator process.

### Full-stack Mode

```text
StackStressGenerator ---- player HTTP APIs ----> GameService ---- SQL ----> PostgreSQL
SimulationWorker ------- service HTTP APIs ----> GameService
StackStressGenerator ---- LiteNetLib UDP ------> SimulationWorker
GameService -------- session cache and auth rate limits ------------> Redis
GameService and SimulationWorker health checks ---------------------> Redis
```

Each full-stack bot completes the normal public lifecycle:

1. Register an account.
2. Create a character. A gameplay workload then receives its initial fixture
   items.
3. Log out the registration session.
4. Log in with the created account.
5. Optionally repeat authenticated session validation requests to isolate the
   Redis cache and PostgreSQL fallback cost.
6. List characters and shards.
7. Request a normal join ticket through GameService placement. Like the Unity
   client, the bot refuses a placement whose worker runs another protocol
   version, with `protocol_version_mismatch`.
8. Join SimulationWorker over UDP and run the same realtime workload as
   worker-only mode.
9. Leave the simulation cleanly and log out the account session.

All bots use production account authentication, placement, join tickets, UDP
item commands, worker authority checks, GameService transactions, and
PostgreSQL custody. Gameplay workloads use guarded Development-only fixture
routes only to create each bot's initial items and, for the loot workloads, the
shared durable corpse, which one extra fixture account sets up before the ramp.
The measured gameplay operations never call those routes.

### What A Dense Run Exercises

A crowd in one interest radius drives two overload paths of the worker:

- Snapshots. Every joined client receives its own owner snapshot at the full
  15 Hz, while remote entities share the rest of the worker-wide snapshot byte
  budget in rotating fair chunks, so remote cadence drops before owner cadence
  does. The worker counts the remote work it put off in
  `simulation_worker.simulation.snapshot.entity_updates_deferred`; that is
  scheduled degradation, not a dropped owner frame. The report's
  `estimatedMissingSnapshots` counts owner sequence gaps only. The rules are in
  [Entity Registry And Replication](SERVICE_FEATURES.md#entity-registry-and-replication)
  and [UDP Resilience And Quotas](SERVICE_FEATURES.md#udp-resilience-and-quotas).
- The shared corpse. One committed corpse mutation is encoded once for every
  viewer, open and refresh state go only to the requester, and the mutations of
  one corpse enter GameService one at a time through a worker-local coordinator
  while different corpses stay parallel, as
  [World Actors And Interaction](SERVICE_FEATURES.md#world-actors-and-interaction)
  describes. The corpse mutation backlog and active aggregates are published as
  metrics, and the interval log keeps the deepest queue it saw.

## Full-stack Database Safety

Full-stack mode is restricted to a disposable local database. The generator
refuses to start unless all of these checks pass:

- GameService and PostgreSQL use loopback addresses.
- The PostgreSQL database name contains `stress` or `test`.
- `--confirm-disposable-database` exactly matches the connected database name.
- GameService has initialized the schema.
- The `accounts` table is empty before the run.

After the run, the account row count must equal the accounts the run
registered, including the loot fixture account. GameService reads `.env` like in
normal development, so set its connection strings in its terminal exactly as
[Run Full-stack Mode](#run-full-stack-mode) shows; this final check fails when
GameService wrote to another database.

Gameplay fixtures add another safety boundary. GameService refuses to start
with `StackStressFixtures:Enabled` set to true unless:

- The hosting environment is `Development`.
- `STACK_STRESS_FIXTURE_SECRET` holds a non-placeholder secret of at least 32
  characters.
- GameService uses a loopback PostgreSQL host and a database whose name
  contains `stress` or `test`.
- `StackStressFixtures:InventoryDefinitionId` and
  `StackStressFixtures:LootDefinitionId` name the stackable definitions that
  the fixtures grant to bots and place on loot corpses. The checked-in
  Development settings use `potion.minor_healing` and `material.iron_ore`. The
  fixture responses carry both ids, so the generator and its invariant sampler
  never name game content.

Every fixture request must also come from loopback, be authenticated as a real
account, and carry the fixture secret in the `X-Stack-Stress-Fixture-Key`
header.

Fixture creation uses `ItemTransactionService` and the durable corpse service.
It does not write inventory rows directly. The generator validates final item
quantity, slot uniqueness, custody, and pending-operation invariants through its
separate read-only PostgreSQL sampler.

The generator does not delete accounts or other durable rows. Recreate the
disposable stack before every full-stack run. Never point this mode at a normal
developer, staging, or production database. Prefer the `STACK_STRESS_POSTGRES`
environment variable instead of `--postgres-connection-string`, so the password
is not exposed through process arguments. Database credentials and session
secrets are not written to the JSON report.

`docker-compose.stack-stress.yml` provides PostgreSQL on `127.0.0.1:56432` and
Redis on `127.0.0.1:56379`, apart from the development and test containers.
PostgreSQL keeps its data on a temporary in-memory filesystem, so
`docker compose down` discards the complete stress database.

GameService caps its Npgsql pool at `Database:MaximumPoolSize`, 64 in the
checked-in configuration. Do not raise it merely to make a stress tier pass.
Without the cap, one GameService process can open as many connections as
PostgreSQL allows and fail requests with `too many clients already`. The
combined pool limits of all GameService replicas plus worker, health,
monitoring, and administrative clients must stay below PostgreSQL's
non-reserved connection capacity, so pool saturation queues bounded work. A
transient database connection failure returns `503 database_unavailable`.

## Build

Run from the repository root:

```powershell
dotnet restore Mmorpg.slnx --locked-mode
dotnet build Mmorpg.slnx --configuration Release --no-restore
```

Expected result: the complete solution, including `StackStressGenerator`,
builds with zero warnings and zero errors.

Do not record stress measurements under the debugger. Record the current
commit, CPU, logical core count, memory, operating system, bot count, seed, and
service configuration with every accepted baseline.

## Run Worker-only Mode

GameService, PostgreSQL, Redis, and Unity are not required. The worker binds UDP
port `27016` by default, the port of the local stress arena worker, so stop that
worker first or choose another `--worker-udp-port`.

In the first PowerShell terminal, start the generator:

```powershell
dotnet run --project tools/StackStressGenerator `
  --configuration Release `
  --no-build `
  -- `
  --mode worker-only `
  --bots 100 `
  --ramp-step 25 `
  --ramp-interval-seconds 10 `
  --duration-seconds 300 `
  --report-interval-seconds 10
```

The tool binds its in-memory authority to `http://127.0.0.1:5099`, creates an
ephemeral worker secret, prints the exact SimulationWorker environment
commands, and waits for the worker heartbeat.

In the second PowerShell terminal, paste the printed environment commands, add
a ten-second metrics interval, and start the worker:

```powershell
$env:SimulationWorker__NetworkMetricsLogSeconds = "10"
dotnet run --project src/Server/SimulationWorker --configuration Release --no-build --no-launch-profile
```

Expected result:

- SimulationWorker registers its exact runtime with the in-memory authority.
- Bots ramp in groups of 25 until all 100 have started.
- The steady interval starts after every bot has joined or failed.
- Joined bots send movement at the worker-provided simulation rate.
- Bots check that every message arrives with the channel and delivery the
  protocol names and decodes, as
  [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md#protocol-handling)
  describes, and act on owner snapshots and the results of their own item and
  corpse operations. A bot that fails releases its worker connection at once,
  so its character does not stay joined until the run ends.
- Every joined bot requests a graceful leave after the steady interval.
- The worker log shows no `Movement simulation exceeded its catch-up budget`
  warning.
- The command exits with code 0 only when every requested bot joined and left
  cleanly.
- A JSON report is written under `artifacts/stress`, which Git ignores.

Stop SimulationWorker when the generator completes. Clear the printed overrides
and the metrics override before normal local development:

```powershell
Remove-Item Env:GAME_SERVICE_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_SERVICE_SECRET -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_MAX_CONNECTIONS -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_FLEET_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_NODE_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_SHARD_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORLD_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_HOST -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SimulationWorker__NetworkMetricsLogSeconds -ErrorAction SilentlyContinue
```

## Run Full-stack Mode

This workflow uses three PowerShell terminals plus the disposable Docker stack.
Use the shown local-only worker secret in both service terminals, or replace it
with another identical value of at least 32 characters. The worker uses UDP port
`27025`, so the normal local workers can keep running.

First, recreate and start the disposable dependencies:

```powershell
docker compose -f docker-compose.stack-stress.yml down
docker compose -f docker-compose.stack-stress.yml up -d --wait
```

In terminal 1, start GameService against the disposable database. The high
authentication limit is a load-test override: the normal local limit is five
authentication requests per minute per client address, and GameService accepts
at most 1,000 per window, which covers the registration and login of 100 bots
in one minute.

```powershell
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only"
$env:ConnectionStrings__Redis = "127.0.0.1:56379"
$env:SIMULATION_WORKER_SERVICE_SECRET = "stack-stress-local-worker-secret-2026"
$env:DevelopmentSimulationBots__Enabled = "false"
$env:RateLimiting__Authentication__PermitLimit = "1000"
$env:RateLimiting__Authentication__WindowSeconds = "60"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5500"
dotnet run --project src/Server/GameService --configuration Release --no-build --no-launch-profile
```

For `inventory`, `loot-hotspot`, or `mixed-gameplay`, also set the guarded
fixture configuration in terminal 1 before starting GameService. The fixture
secret is local test data, but it must contain at least 32 characters and match
the value the generator uses.

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
$env:StackStressFixtures__Enabled = "true"
$env:STACK_STRESS_FIXTURE_SECRET = "stack-stress-local-fixture-secret-2026"
```

Expected result: migrations complete and GameService listens on
`http://127.0.0.1:5500`.

In terminal 2, start SimulationWorker with the matching secret and the
disposable Redis endpoint:

```powershell
$env:GAME_SERVICE_BASE_URL = "http://127.0.0.1:5500"
$env:ConnectionStrings__Redis = "127.0.0.1:56379"
$env:SIMULATION_WORKER_SERVICE_SECRET = "stack-stress-local-worker-secret-2026"
$env:SIMULATION_WORKER_UDP_PORT = "27025"
$env:SIMULATION_WORKER_ADVERTISED_HOST = "127.0.0.1"
$env:SIMULATION_WORKER_ADVERTISED_UDP_PORT = "27025"
$env:SIMULATION_WORKER_ID = "local-simulation-worker-2"
$env:SIMULATION_SHARD_ID = "local-shard-2"
$env:SIMULATION_WORLD_ID = "development-world-1"
$env:SimulationWorker__NetworkMetricsLogSeconds = "10"
dotnet run --project src/Server/SimulationWorker --configuration Release --no-build --no-launch-profile
```

For `loot-hotspot` or `mixed-gameplay`, also set a one-second durable corpse
reconciliation interval in terminal 2 before starting the worker, so the new
stress corpse becomes visible quickly:

```powershell
$env:SimulationWorker__ItemInteraction__DurableCorpseRefreshSeconds = "1"
```

Expected result: the worker registers `local-shard-2` and
`development-world-1`, the stress arena, with GameService and advertises UDP
port `27025`.

In terminal 3, run the complete lifecycle. Set the connection string in the
environment instead of passing it as an argument:

```powershell
$env:STACK_STRESS_POSTGRES = "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only"
dotnet run --project tools/StackStressGenerator `
  --configuration Release `
  --no-build `
  -- `
  --mode full-stack `
  --workload lifecycle `
  --game-service-url http://127.0.0.1:5500 `
  --confirm-disposable-database shooter_mmo_stack_stress_test `
  --bots 100 `
  --http-concurrency 32 `
  --session-validation-requests-per-bot 20 `
  --ramp-step 25 `
  --ramp-interval-seconds 10 `
  --duration-seconds 300 `
  --report-interval-seconds 10
```

Expected result:

- All 100 accounts register and receive one character each.
- Every account logs in and obtains a normal GameService placement and ticket.
- Every account performs the configured authenticated session checks, reported
  separately as `account_session_validate`.
- All 100 bots join, run the UDP workload, and leave cleanly.
- Every active account session is logged out.
- PostgreSQL reports no expired unreleased simulation sessions or deadlocks.
- The final database account count is exactly 100.
- The command exits with code 0 and writes a full-stack JSON report.

### Run Gameplay Workloads

Start the same disposable stack with the fixture configuration, then select one
workload in terminal 3. This example runs the complete mixed path:

```powershell
$env:STACK_STRESS_POSTGRES = "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only"
$env:STACK_STRESS_FIXTURE_SECRET = "stack-stress-local-fixture-secret-2026"
dotnet run --project tools/StackStressGenerator `
  --configuration Release `
  --no-build `
  -- `
  --mode full-stack `
  --workload mixed-gameplay `
  --game-service-url http://127.0.0.1:5500 `
  --confirm-disposable-database shooter_mmo_stack_stress_test `
  --bots 100 `
  --http-concurrency 32 `
  --ramp-step 25 `
  --ramp-interval-seconds 10 `
  --duration-seconds 300 `
  --inventory-operation-interval-seconds 3 `
  --loot-operation-interval-seconds 4 `
  --report-interval-seconds 10
```

Replace `mixed-gameplay` with `inventory` or `loot-hotspot` to isolate one item
path. Recreate the Docker stack before every workload, because the database
must start empty.

Expected result:

- Every gameplay bot receives only the minimal initial fixture items.
- Loot workloads create one shared durable Mob corpse at the configured hotspot.
- Item and corpse commands travel over UDP and are authorized by the worker.
- GameService commits every accepted change through its normal item transaction
  boundary.
- Concurrent revision conflicts are reported as expected conflicts, not hidden
  as successes or counted as infrastructure failures.
- In `mixed-gameplay`, `corpse_out_of_range` also counts as an expected
  conflict: the bots start crowded on the corpse and keep moving, so the worker
  correctly rejects a request from a bot that drifted past the 3 m interaction
  radius, for up to about one percent of corpse requests. The stationary
  `loot-hotspot` bots never leave the radius, so there it stays unexpected.
- The run exits with code 0 only when lifecycle checks, gameplay operations, and
  final PostgreSQL item invariants pass.

### Clean Up

Stop GameService and SimulationWorker with Ctrl+C, then remove the disposable
stack and every override:

```powershell
docker compose -f docker-compose.stack-stress.yml down

Remove-Item Env:ConnectionStrings__Postgres -ErrorAction SilentlyContinue
Remove-Item Env:ConnectionStrings__Redis -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_SERVICE_SECRET -ErrorAction SilentlyContinue
Remove-Item Env:DevelopmentSimulationBots__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:RateLimiting__Authentication__PermitLimit -ErrorAction SilentlyContinue
Remove-Item Env:RateLimiting__Authentication__WindowSeconds -ErrorAction SilentlyContinue
Remove-Item Env:RedisAcceleration__SessionCacheEnabled -ErrorAction SilentlyContinue
Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
Remove-Item Env:GAME_SERVICE_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_HOST -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_SHARD_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORLD_ID -ErrorAction SilentlyContinue
Remove-Item Env:SimulationWorker__NetworkMetricsLogSeconds -ErrorAction SilentlyContinue
Remove-Item Env:SimulationWorker__ItemInteraction__DurableCorpseRefreshSeconds -ErrorAction SilentlyContinue
Remove-Item Env:STACK_STRESS_POSTGRES -ErrorAction SilentlyContinue
Remove-Item Env:STACK_STRESS_FIXTURE_SECRET -ErrorAction SilentlyContinue
Remove-Item Env:StackStressFixtures__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:DOTNET_ENVIRONMENT -ErrorAction SilentlyContinue
```

No manual Unity Editor steps are required for either mode.

## Redis Session-cache A/B Baseline

The A/B method measures what the Redis account-session cache saves. Run the
same full-stack `lifecycle` profile twice with many
`--session-validation-requests-per-bot`, so session validation dominates:
once with `$env:RedisAcceleration__SessionCacheEnabled = "false"` set in
terminal 1 before GameService starts, and once without it, since the cache is on
by default. Recreate the disposable stack between the runs and change nothing
else, so both runs keep the distributed Redis authentication rate limit. Compare
the `account_session_validate` latency and the PostgreSQL committed
transactions of the two reports. The cache itself is described in
[Redis Account-session Acceleration](SERVICE_FEATURES.md#redis-account-session-acceleration).

The current baseline used 100 bots on Development World 2, HTTP concurrency 64,
and 50 validation requests per bot:

| Metric | Cache off | Cache on | Change |
| --- | ---: | ---: | ---: |
| Session validation requests | 5,000 | 5,000 | Same |
| Average validation latency | 26.9 ms | 13.1 ms | 51.2% lower |
| Validation p50 | 15.7 ms | 4.5 ms | 71.1% lower |
| Validation p95 | 66.2 ms | 46.6 ms | 29.6% lower |
| Validation p99 | 282.5 ms | 76.0 ms | 73.1% lower |
| PostgreSQL committed transactions | 13,259 | 2,633 | 80.1% lower |
| Complete run duration | 32.9 s | 29.0 s | 11.9% lower |
| Maximum PostgreSQL connections | 66 | 66 | Unchanged |
| Maximum waiting PostgreSQL connections | 61 | 61 | Unchanged |

Both runs admitted and cleanly logged out 100 of 100 clients with no HTTP
failures, deadlocks, expired unreleased sessions, or item invariant violations.
After the cache-on run, Redis held 200 temporary revocation tombstones and no
token cache entries, one tombstone for each bot's registration session and one
for its final session.

Repeated authentication therefore no longer needs one PostgreSQL lookup per
request. The cache does not remove the admission-wave connection spike:
registration, BCrypt password work, character creation, login replacement,
placement, join-ticket creation, and simulation session setup still reach
PostgreSQL, so profile them separately before changing pool limits or service
boundaries. A check on the same stack confirmed the failure contract: with
Redis stopped, session validation keeps working through PostgreSQL and logout
returns 503, and after Redis returns, the session is still valid and logout
succeeds.

## Ramp And Soak Strategy

Use these initial tiers on one recorded machine:

1. 10 bots for a transport and tooling smoke test.
2. 25 bots.
3. 50 bots.
4. 75 bots.
5. 100 bots.

Run each steady tier for at least five minutes. After a clean 100-bot result,
try 150, 250, and 500 bots. The checked-in worker connection ceiling is 1000,
but that ceiling is not a supported capacity claim. Do not change a production
default based on a single local machine.

Use worker-only mode first to establish the realtime boundary. Repeat accepted
tiers in full-stack mode to identify when GameService, HTTP admission, or
PostgreSQL becomes the limiting component. Run the highest clean tier for 30 to
60 minutes to detect memory growth, GC pressure, session-heartbeat instability,
database contention, and late disconnects.

The generator and services share one machine, so each result includes local
process contention. Use the separate process metrics to reject a run where the
generator becomes the limiting process. Distributed load generation is outside
the current security boundary.

## Measurements

Every JSON report contains:

- Run mode, topology, seed, timing, and bot counts.
- Requested, joined, completed, and failed bot totals plus stable failure codes.
- Join and input acknowledgement latency percentiles.
- Sent and received packets and bytes.
- Owner and remote snapshot packet counts plus estimated missing owner
  sequences.
- Reliable spawn and despawn counts.
- SimulationWorker and generator CPU, working set, private memory, and thread
  summaries, including steady-state memory trend windows.

Worker-only reports also contain ticket, simulation-session, heartbeat, and
release counters from the in-memory authority.

Full-stack reports additionally contain:

- Registered, fixture, provisioned, logged-in, admitted, and logged-out account
  totals.
- Per-operation HTTP request, success, failure, status-code, stable failure-code,
  and latency summaries.
- Per-gameplay-operation request, success, expected-conflict,
  unexpected-rejection, timeout, incomplete-request, stable result-code, and
  latency summaries.
- GameService process CPU, memory, and thread summaries when exactly one local
  GameService process can be identified.
- PostgreSQL connection, active wait, idle-in-transaction, transaction, row,
  temporary-file, I/O timing, and deadlock statistics.
- Maximum active and expired unreleased simulation sessions, minimum observed
  lease headroom, and session-table insert and update counts.
- Final expected and actual item quantities, duplicate occupied slots, invalid
  item custody, and pending item-operation counts.

SimulationWorker logs interval timing for network polling, asynchronous
completion handling, join queue and finalization, simulation ticks and lag,
movement, collision streaming, interest work, snapshot construction and send,
and fixed-tick resynchronization. The same values are published as metrics of
the `Mmorpg.SimulationWorker.Performance` and `Mmorpg.SimulationWorker.Realtime`
meters, listed in [Monitoring](MONITORING.md#simulationworker). Set
`Telemetry__OtlpEndpoint` for GameService and SimulationWorker and start the
[monitoring stack](MONITORING.md) to watch a run live in Grafana; the
generator's own report remains the measurement of record.

## Quality Boundary

A tier is not supported merely because every process remains alive. Require:

- Every requested bot joins and leaves successfully.
- No unexpected disconnect, protocol error, or normal-input quota rejection.
- No fixed-tick resynchronization during the steady interval.
- Simulation tick p99 stays below the 33.34 ms fixed-tick budget of a 30 Hz
  tick.
- Owner snapshot gaps are zero, snapshot drops are zero, and input
  acknowledgement latency remains stable.
- Remote entity cadence and deferred-update rate match the tier's documented
  quality target. A process remaining alive is not enough if remote presentation
  falls below that target.
- Worker, GameService, and generator working sets stabilize after warmup.
- Worker registration and simulation-session leases remain valid.
- Full-stack HTTP operations complete without unexpected failure codes.
- Gameplay profiles complete without unexpected item or corpse rejection and
  without operation timeouts. Expected concurrent revision conflicts remain
  visible as a separate result.
- Full-stack mode has no expired unreleased sessions or PostgreSQL deadlocks.
- Gameplay profiles preserve exact item quantities, unique occupied slots,
  valid custody, and zero pending item operations.

Record the first boundary that fails and the phase whose duration grows with
load. Optimize only after a repeatable run identifies the limiting phase, then
rerun the same seed, mode, and tier for comparison. The
[Realtime Performance Contract](PROJECT_ARCHITECTURE.md#realtime-performance-contract)
requires every new high-frequency gameplay feature to add or extend a workload
before a higher tier is accepted. Before the full-stack load targets are raised,
measure what the item-state lock and the session heartbeats cost in extra
GameService round trips with a full-stack run.

## Current Worker-only Baseline

These are the latest accepted worker-only numbers. They were measured on an AMD
Ryzen 7 5800 with 8 cores, 16 logical processors, and 31.9 GiB of memory under
Windows, with the generator, the worker, and the Unity Editor on one machine.
The worker ran 30 simulation ticks and 15 snapshots per second, every bot stayed
inside one interest radius, and the runs used seed `1337`, ramp steps of 50 or
100 bots five seconds apart, and a 60-second steady interval. They measured the
current realtime loop, with its high-resolution tick timer, staggered interest
refresh, and budget-sized packet pool, as
[Realtime UDP Transport](SERVICE_FEATURES.md#realtime-udp-transport) and
[Interest Management](SERVICE_FEATURES.md#interest-management) describe, and
bots that send one input per tick.

| Bots | Tick lag avg / max | Simulation tick avg | Interest avg | Snapshot broadcast avg | Allocated per 10 s | Gen0 / gen1 per 10 s | Input ack p50 / p95 / p99 | Worker average single-core CPU |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 200 | 0.3 / 1.2 ms | 1.2 ms | 0.3 ms | 1.2 ms | 29 MiB | 1 / 0 | 57.9 / 90.4 / 105.2 ms | 44.1% |
| 500 | 0.5 / 2.0 ms | 4.3 ms | 1.9 ms | 4.7 ms | 70 MiB | 4 / 0 | 78.6 / 119.7 / 140.3 ms | 87.8% |

Every run joined and left every bot cleanly with zero owner snapshot gaps and
drops. The timing and allocation columns come from the worker's interval log of
one steady interval, and the latencies and CPU from the generator's run summary.

What the numbers mean:

- Ticks start within a few milliseconds of their schedule and stay far inside
  the tick budget, and almost no garbage survives into generation 1.
- Most of the remaining input acknowledgement latency is structural: an input
  waits for the next tick, and its acknowledgement for the next owner snapshot,
  which goes out every second tick.
- At 500 bots most of the worker CPU is LiteNetLib's update thread, which sends
  about 45,000 packets per second. LiteNetLib's experimental `UseNativeSockets`
  option raised the worker to 96 percent of a core in the same run, so it stays
  off. Fewer and smaller remote records, through quantization or larger chunks,
  are the next lever and need a protocol change.
- Full 15 Hz replication of 400 mutually visible players would need about
  100 MB/s before transport overhead, against the 38 MiB/s aggregate snapshot
  budget, so remote cadence drops in a dense crowd while owner state stays at
  15 Hz. With 400 mutually visible players the average remote entity cadence
  was about 5.6 Hz. Raising the budget is not the default fix: larger Worlds
  should reduce the visible set, and future protocol work may add measured
  quantization or delta compression.
- Single runs on a shared machine vary; one 200-bot run measured an input
  acknowledgement p99 of 188 ms. All of these are short runs, and a 30 to 60
  minute soak is still required before a tier counts as supported.

## Current Full-stack Baseline

These are the latest accepted full-stack numbers, from the `mixed-gameplay`
workload on development `1eeebca` on the same machine. They used the normal
account and placement APIs, real PostgreSQL, worker ticket consumption and
session leases, one shared durable corpse, UDP item and corpse commands,
GameService transactions, and the final database invariant checks, with
three-second inventory and four-second corpse operations. The 100-client run
used 32-way HTTP admission, four 25-client ramp steps ten seconds apart, and a
120-second steady interval. The 250-client run used 64-way admission, five
50-client ramp steps five seconds apart, and a 60-second steady interval.

The local development stack ran beside the disposable stack with 300
ActiveSimulationBots, about one of the 16 logical processors. Because two
GameService and two SimulationWorker processes ran, the generator could not
attribute process metrics: the worker CPU below comes from the stress worker's
interval log, and the GameService process metrics are missing.

| Clients | Complete lifecycle | Join failures | Input ack p50 / p95 / p99 | Login p50 / p95 | Join ticket p50 / p95 | PostgreSQL max connections / active waits | Rollbacks | Expired unreleased sessions | Corpse refresh p95 | Worker average single-core CPU |
| ---: | :---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 100/100 | 0 | 47.2 / 78.2 / 85.2 ms | 0.57 / 0.84 s | 0.28 / 0.58 s | 67 / 12 | 0 | 0 | 50 ms | about 30% |
| 250 | 250/250 | 0 | 73.2 / 114.4 / 139.2 ms | 1.03 / 1.38 s | 2.23 / 4.15 s | 66 / 60 | 0 | 0 | 115 ms | about 90% |

Both runs had zero deadlocks, zero unexpected gameplay rejections, zero
operation timeouts, and exact item quantity, slot, custody, and operation
invariants. The 100-client run completed 4,369 inventory relocations, 541
corpse deposits, and 497 partial loots with a deposit p95 of 352 ms; the
250-client run completed 3,729 relocations and 1,679 corpse refreshes.

What the numbers mean:

- PostgreSQL connections stay at about 66, GameService's pool of 64 plus a few
  other clients such as the generator's sampler, so pool saturation queues work
  instead of failing it.
- Admission is the slow phase. At 250 clients join tickets wait seconds while
  password hashing, character creation, login, placement, and ticket creation
  queue for the pool; the `lifecycle` workload at 250 clients and 64-way
  admission showed registration and login p95 of about 5.5 seconds for the same
  reason. That is bounded admission backpressure, not a steady-state gameplay
  delay.
- The shared corpse is deliberately pathological. One corpse is one serialized
  custody aggregate, so with 250 clients almost every deposit and loot is a
  stale-revision conflict, the queue in front of the corpse, up to 142 waiting
  mutations, sets the latency, and deposit p95 reached 8.5 seconds. That is
  bounded backpressure instead of database pool exhaustion. A workload with
  several separate corpse hotspots is still needed to show that the per-corpse
  coordinator keeps parallel throughput.
- No full-stack run at 400 clients exists with the current snapshot scheduler.
  Run one before claiming a 400-client full-stack tier.
- These are short stability runs, not a production capacity declaration.

## Scope Boundary

Full-stack mode measures account registration, character creation, login, shard
discovery, placement, tickets, simulation-session leases, UDP gameplay traffic,
leave, and logout. Explicit gameplay workloads also measure durable inventory
relocation and shared corpse deposit and loot through the real worker and
GameService transaction path.

Trading, combat-driven deaths, insurance claims, quest item policies, Bank,
Recovery Storage, equipment, Bag nesting, and multiple geographically separated
loot hotspots are not generated. Add those as explicit future workload profiles
so the stable lifecycle baseline does not silently change.

Distributed load generation and remote service targets are not implemented:
every URL and the PostgreSQL host must be loopback.

## Command Reference

List every option with its default:

```powershell
dotnet run --project tools/StackStressGenerator -- --help
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--mode` | `worker-only` | `worker-only` or `full-stack` |
| `--workload` | `lifecycle` | `lifecycle`, `inventory`, `loot-hotspot`, or `mixed-gameplay`; every workload except `lifecycle` needs `full-stack` |
| `--authority-url` | `http://127.0.0.1:5099` | Loopback URL of the worker-only in-memory authority |
| `--game-service-url` | `http://127.0.0.1:5000` | Loopback URL of the full-stack GameService |
| `--postgres-connection-string` | `STACK_STRESS_POSTGRES` | Full-stack metrics and safety connection; prefer the variable |
| `--confirm-disposable-database` | none | The exact disposable database name, required in full-stack mode |
| `--run-id` | random | 1 to 12 ASCII letters or digits in the names of the full-stack accounts and characters |
| `--http-timeout-seconds` | `30` | Full-stack HTTP timeout |
| `--http-concurrency` | `32` | Concurrent full-stack account lifecycles, 1 to 512 |
| `--session-validation-requests-per-bot` | `0` | 0 to 1000 authenticated session checks after each login, full-stack only |
| `--worker-host` | `127.0.0.1` | Worker-only: the UDP host the bots use and the worker advertises |
| `--worker-udp-port` | `27016` | Worker-only: the worker's UDP port, the stress arena port |
| `--worker-id` | `local-simulation-worker-2` | The worker the run expects; full-stack mode checks it in every join response |
| `--fleet-id`, `--node-id` | `local-fleet`, `local-node-1` | Worker-only: the identity the worker must register |
| `--shard-id` | `local-shard-2` | The Shard the bots join |
| `--world-id` | `development-world-1` | The World that Shard must run |
| `--worker-secret` | random | Worker-only: the ephemeral worker secret, at least 32 characters |
| `--bots` | `100` | Total bots, 1 to 10,000 |
| `--bot-start-index` | `1` | Index of the first bot; the same seed and indices repeat the same movement |
| `--ramp-step` | `25` | Bots added per ramp interval, at most `--bots` |
| `--ramp-interval-seconds` | `10` | Delay between ramp steps |
| `--duration-seconds` | `300` | Steady interval after the ramp |
| `--worker-wait-seconds` | `120` | How long to wait until the worker or the full stack is ready |
| `--join-timeout-seconds` | `10` | Per-bot join timeout |
| `--inventory-operation-interval-seconds` | `8` | Inventory mutation cadence |
| `--loot-operation-interval-seconds` | `3` | Corpse mutation cadence |
| `--gameplay-operation-timeout-seconds` | `30` | Timeout of one realtime item or corpse operation |
| `--loot-hotspot-x`, `-y`, `-z` | `0`, `0`, `0` | Position of the shared corpse, the stress arena spawn |
| `--report-interval-seconds` | `10` | Console report interval |
| `--seed` | `1337` | Movement seed |
| `--output` | `artifacts/stress/stack-stress-<mode>-<workload>-<time>.json` | Path of the JSON report |

`STACK_STRESS_FIXTURE_SECRET` supplies the fixture secret of the gameplay
workloads; it is intentionally not accepted as a command-line argument. The
same seed and bot indices reproduce movement patterns, while tickets, account
session tokens, and generated passwords stay random for every run.

Exit codes: 0 when every requested bot joined and left cleanly, every admitted
account logged out, no gameplay operation failed unexpectedly, and the item
invariants held; 1 for any other result or failure; 2 for an invalid option;
130 when the run was cancelled with Ctrl+C.

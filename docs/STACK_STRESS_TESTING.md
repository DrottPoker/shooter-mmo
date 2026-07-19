# Stack Stress Testing

Last updated: 2026-07-19

## Purpose

`Tools/StackStressGenerator` is the repeatable load harness for the realtime
stack. It has two explicit modes so SimulationWorker capacity can be measured
separately from account, placement, session, AuthService, and PostgreSQL load.

| Mode | Real AuthService | PostgreSQL | Realtime UDP | Primary use |
| --- | :---: | :---: | :---: | --- |
| `worker-only` | No | No | Yes | Isolate SimulationWorker hotspots and capacity |
| `full-stack` | Yes | Yes | Yes | Measure the complete login-to-game lifecycle and shared stack pressure |

Both modes run independent headless LiteNetLib clients through the real join,
movement, snapshot, entity lifecycle, simulation-session, and graceful-leave
paths. The current small map keeps every bot inside the same interest radius.
This intentionally measures a worst-case local crowd, not the distributed
capacity of a future larger World.

Use [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) when the goal is a
long-running visible population beside a real local Unity player. That tool is
not a repeatable capacity benchmark.

## Architecture

### Worker-only mode

```text
StackStressGenerator
  in-memory stress authority <--- authenticated HTTP ---> SimulationWorker
  headless stress bots        -------- LiteNetLib UDP ---> SimulationWorker

AuthService: not running
PostgreSQL: not used
Unity: not used
```

The in-memory authority implements only the service contracts required by
SimulationWorker: readiness, worker heartbeat and offline registration,
one-time ticket consumption, simulation-session heartbeat and release, and
empty durable-corpse restoration. Identities, tickets, and sessions exist only
for the lifetime of the generator process.

### Full-stack mode

```text
StackStressGenerator ---- player HTTP APIs ----> AuthService ---- SQL ----> PostgreSQL
SimulationWorker ------- service HTTP APIs ----> AuthService
StackStressGenerator ---- LiteNetLib UDP ------> SimulationWorker
AuthService and SimulationWorker health checks ---------------------> Redis
```

Each full-stack bot completes the normal public lifecycle:

1. Register an account.
2. Create a character.
3. Log out the registration session.
4. Log in with the created account.
5. List characters and shards.
6. Request a normal join ticket through AuthService placement.
7. Join SimulationWorker over UDP and run the same realtime workload as
   worker-only mode.
8. Leave the simulation cleanly and log out the account session.

The generator never bypasses production authentication or adds a stress-only
route to AuthService or SimulationWorker. Each bot owns an independent UDP
endpoint. Clients use LiteNetLib manual polling so the harness does not create
one transport thread per bot.

## Full-stack Database Safety

Full-stack mode is intentionally restricted to a disposable local database.
It refuses to start unless all of these checks pass:

- AuthService and PostgreSQL use loopback addresses.
- The PostgreSQL database name contains `stress` or `test`.
- `--confirm-disposable-database` exactly matches the connected database name.
- AuthService has initialized the schema.
- The `accounts` table is empty before the run.
- The final account row count matches the number of successful registrations.

The generator does not delete accounts or other durable rows. Recreate the
disposable stack before every full-stack run. Never point this mode at a normal
developer, staging, or production database. Prefer the `STACK_STRESS_POSTGRES`
environment variable instead of a command-line connection string so the
password is not exposed through process arguments. Database credentials and
session secrets are not written to the JSON report.

`docker-compose.stack-stress.yml` provides isolated PostgreSQL and Redis ports.
PostgreSQL uses a temporary in-memory filesystem, so `docker compose down`
discards the complete stress database.

AuthService caps its Npgsql pool at `Database:MaximumPoolSize`, currently `64`.
Do not raise this value merely to make a stress tier pass. The combined pool
limits of all AuthService replicas plus worker, health, monitoring, and
administrative clients must remain below PostgreSQL's non-reserved connection
capacity. Pool saturation should queue bounded work. A transient database
connection failure returns `503 database_unavailable`.

## Build

Run from the repository root:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
dotnet build ShooterMmo.slnx --configuration Release --no-restore
```

Expected result: the complete solution, including `StackStressGenerator`,
builds with zero warnings and zero errors.

Do not record stress measurements under the debugger. Record the current
commit, CPU, logical core count, memory, operating system, bot count, seed, and
service configuration with every accepted baseline.

## Run Worker-only Mode

AuthService, PostgreSQL, Redis, and Unity are not required.

In the first PowerShell terminal, start the generator:

```powershell
dotnet run --project Tools/StackStressGenerator `
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

The tool binds an in-memory authority to `http://127.0.0.1:5099`, creates an
ephemeral worker secret, prints the exact SimulationWorker environment
commands, and waits for the worker heartbeat.

In the second PowerShell terminal, copy the printed environment commands, then
start the worker. The following metrics override is also useful:

```powershell
$env:SimulationWorker__NetworkMetricsLogSeconds = "10"
dotnet run --project SimulationWorker --configuration Release --no-build
```

Expected result:

- SimulationWorker registers its exact runtime with the in-memory authority.
- Bots ramp in groups of 25 until all 100 have started.
- The steady interval starts after every bot has joined or failed.
- Joined bots send movement at the worker-provided simulation rate.
- Bots validate reliable player, actor, corpse-presence, and carry-state
  packets plus unreliable snapshots.
- Every joined bot requests a graceful leave after the steady interval.
- The command exits zero only when every requested bot joined and left cleanly.
- A JSON report is written under `artifacts/stress`.

Stop SimulationWorker when the generator completes. Clear the overrides printed
by the generator and this metrics override before normal local development:

```powershell
Remove-Item Env:AUTH_SERVICE_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_SERVICE_SECRET -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_MAX_CONNECTIONS -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORLD_ID -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_HOST -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SimulationWorker__NetworkMetricsLogSeconds -ErrorAction SilentlyContinue
```

## Run Full-stack Mode

This workflow uses three PowerShell terminals plus the isolated Docker stack.
Use the shown local-only worker secret in both service terminals, or replace it
with another identical value of at least 32 characters.

First, recreate and start the disposable dependencies:

```powershell
docker compose -f docker-compose.stack-stress.yml down
docker compose -f docker-compose.stack-stress.yml up -d --wait
```

In terminal 1, start AuthService against the disposable database. The high
authentication limit is a load-test override because the normal local limit is
five authentication requests per minute per client address.

```powershell
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only"
$env:ConnectionStrings__Redis = "127.0.0.1:56379"
$env:SIMULATION_WORKER_SERVICE_SECRET = "stack-stress-local-worker-secret-2026"
$env:DevelopmentSimulationBots__Enabled = "false"
$env:RateLimiting__Authentication__PermitLimit = "100000"
$env:RateLimiting__Authentication__WindowSeconds = "60"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5500"
dotnet run --project AuthService --configuration Release --no-build
```

Expected result: migrations complete and AuthService listens on
`http://127.0.0.1:5500`.

In terminal 2, start SimulationWorker with the matching secret and isolated
Redis endpoint:

```powershell
$env:AUTH_SERVICE_BASE_URL = "http://127.0.0.1:5500"
$env:ConnectionStrings__Redis = "127.0.0.1:56379"
$env:SIMULATION_WORKER_SERVICE_SECRET = "stack-stress-local-worker-secret-2026"
$env:SIMULATION_WORKER_UDP_PORT = "27025"
$env:SIMULATION_WORKER_ADVERTISED_HOST = "127.0.0.1"
$env:SIMULATION_WORKER_ADVERTISED_UDP_PORT = "27025"
$env:SIMULATION_WORLD_ID = "development-world-2"
$env:SimulationWorker__NetworkMetricsLogSeconds = "10"
dotnet run --project SimulationWorker --configuration Release --no-build
```

Expected result: the worker registers `local-shard-1` and
`development-world-2` with AuthService and advertises UDP port `27025`.

In terminal 3, run the complete lifecycle. Set the connection string in the
environment instead of passing it as an argument:

```powershell
$env:STACK_STRESS_POSTGRES = "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only"
dotnet run --project Tools/StackStressGenerator `
  --configuration Release `
  --no-build `
  -- `
  --mode full-stack `
  --auth-service-url http://127.0.0.1:5500 `
  --confirm-disposable-database shooter_mmo_stack_stress_test `
  --bots 100 `
  --http-concurrency 32 `
  --ramp-step 25 `
  --ramp-interval-seconds 10 `
  --duration-seconds 300 `
  --report-interval-seconds 10
```

Expected result:

- All 100 accounts register and receive one character each.
- Every account logs in and obtains a normal AuthService placement and ticket.
- All 100 bots join, run the UDP workload, and leave cleanly.
- Every active account session is logged out.
- PostgreSQL reports no expired unreleased simulation sessions or deadlocks.
- The final database account count is exactly 100.
- The command exits zero and writes a full-stack JSON report.

Stop AuthService and SimulationWorker with Ctrl+C, then clean up:

```powershell
docker compose -f docker-compose.stack-stress.yml down

Remove-Item Env:ConnectionStrings__Postgres -ErrorAction SilentlyContinue
Remove-Item Env:ConnectionStrings__Redis -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_SERVICE_SECRET -ErrorAction SilentlyContinue
Remove-Item Env:DevelopmentSimulationBots__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:RateLimiting__Authentication__PermitLimit -ErrorAction SilentlyContinue
Remove-Item Env:RateLimiting__Authentication__WindowSeconds -ErrorAction SilentlyContinue
Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
Remove-Item Env:AUTH_SERVICE_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_HOST -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORLD_ID -ErrorAction SilentlyContinue
Remove-Item Env:SimulationWorker__NetworkMetricsLogSeconds -ErrorAction SilentlyContinue
Remove-Item Env:STACK_STRESS_POSTGRES -ErrorAction SilentlyContinue
```

No manual Unity Editor steps are required for either mode.

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
tiers in full-stack mode to identify when AuthService, HTTP admission, or
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
- Snapshot counts and estimated missing snapshot sequences.
- Reliable spawn and despawn counts.
- SimulationWorker and generator CPU, working set, private memory, and thread
  summaries, including steady-state memory trend windows.

Worker-only reports also contain ticket, simulation-session, heartbeat, and
release counters from the in-memory authority.

Full-stack reports additionally contain:

- Registered, provisioned, logged-in, admitted, and logged-out account totals.
- Per-operation HTTP request, success, failure, status-code, stable failure-code,
  and latency summaries.
- AuthService process CPU, memory, and thread summaries when the local process
  can be identified.
- PostgreSQL connection, active wait, idle-in-transaction, transaction, row,
  temporary-file, I/O timing, and deadlock statistics.
- Maximum active and expired unreleased simulation sessions, minimum observed
  lease headroom, and session-table insert and update counts.

SimulationWorker logs interval timing for network polling, asynchronous
completion handling, join queue and finalization, simulation ticks and lag,
movement, collision streaming, interest work, snapshot construction and send,
and fixed-tick resynchronization. Timing metrics are also published through the
`ShooterMmo.SimulationWorker.Performance` meter. Network counters remain under
`ShooterMmo.SimulationWorker.Realtime`.

## Quality Boundary

A tier is not supported merely because every process remains alive. Require:

- Every requested bot joins and leaves successfully.
- No unexpected disconnect, protocol error, or normal-input quota rejection.
- No fixed-tick resynchronization during the steady interval.
- Simulation tick p99 remains below the 33.34 ms fixed-tick budget.
- Snapshot gaps and input acknowledgement latency remain stable.
- Worker, AuthService, and generator working sets stabilize after warmup.
- Worker registration and simulation-session leases remain valid.
- Full-stack HTTP operations complete without unexpected failure codes.
- Full-stack mode has no expired unreleased sessions or PostgreSQL deadlocks.

Record the first boundary that fails and the phase whose duration grows with
load. Optimize only after a repeatable run identifies the limiting phase, then
rerun the same seed, mode, and tier for comparison.

## Current Worker-only Baseline

These measurements were recorded on 2026-07-14 with the generator and worker
sharing one Windows development machine:

- AMD Ryzen 7 5800, 8 cores and 16 logical processors.
- 31.9 GiB physical memory.
- 30 simulation ticks and 15 snapshots per second.
- Seed 1337 and the current small map, where all bots are mutually visible.

| Run | Bots | Steady interval | Clean join and leave | Snapshot gaps | Input ack p95 | Worker average single-core CPU | Simulation tick p99 bucket | Maximum working set | Result |
| --- | ---: | ---: | :---: | ---: | ---: | ---: | ---: | ---: | --- |
| Before optimization | 200 | 30 s | 200/200 | 0 | 144.0 ms | 69.2% | at most 50 ms | 111.9 MiB | Outside tick budget |
| Snapshot and interest optimization | 200 | 30 s | 200/200 | 0 | 137.4 ms | 54.9% | at most 8 ms | 112.5 MiB | Clean short run |
| Final clean tier | 250 | 30 s | 250/250 | 0 | 148.9 ms | 70.8% | at most 16 ms | 129.8 MiB | Clean short run |
| 2026-07-19 validation | 250 | 60 s | 250/250 | 0 | 144.2 ms | 61.8% | at most 16 ms | 107.9 MiB | Clean short run |
| Controlled overload | 400 | 120 s | 400/400 | 461,263 | 427.0 ms | 95.6% | at most 50 ms | 239.5 MiB | Not a supported quality tier |

At 200 bots, reusable interest state and visibility-set packet sharing reduced
steady process allocation from about 1,690 MiB to 258 MiB per ten-second metrics
interval. Snapshot-pipeline average fell from 11.9 ms to 2.4 ms and worker CPU
fell by about 21 percent.

The 400-bot overload run remained available by dropping unreliable snapshots,
but it fails the quality boundary because snapshot gaps grow and simulation tick
p99 crosses 33.34 ms. The 250-bot result is only a short clean run. A 30 to 60
minute soak is still required before declaring supported capacity. No comparable
full-stack production capacity baseline has been accepted yet.

## Current Full-stack Baseline

These local runs were recorded on 2026-07-19 on the same Ryzen 7 5800 machine.
They used the normal AuthService account and placement APIs, real PostgreSQL,
real worker ticket consumption and session leases, and the realtime UDP load.

| Run | Bots | HTTP concurrency | Steady interval | Complete lifecycle | HTTP failures | PostgreSQL max connections | Snapshot gaps | Input ack p95 | Result |
| --- | ---: | ---: | ---: | :---: | ---: | ---: | ---: | ---: | --- |
| Initial baseline | 100 | 32 | 60 s | 100/100 | 0 | 34 | 0 | 126.2 ms | Clean short run |
| Before pool bound | 250 | 64 | 60 s | 198/250 | 48 | 100 | 0 | 133.7 ms | PostgreSQL connection exhaustion |
| After pool bound | 250 | 64 | 60 s | 250/250 | 0 | 66 | 0 | 148.4 ms | Clean short run |
| Controlled overload | 400 | 64 | 30 s | 400/400 | 0 | 66 | 128,529 | 479.5 ms | Worker snapshot quality boundary failed |

Before the pool bound, one AuthService process could grow its implicit Npgsql
pool to PostgreSQL's complete 100-connection limit. PostgreSQL logged repeated
`too many clients already` failures across registration, character, login,
placement, and worker ticket-consumption requests. This caused 52 bot failures.

With `Database:MaximumPoolSize` set to `64`, the exact same 250-bot and
64-concurrency profile completed every lifecycle. Database connections remained
at or below 66 including non-AuthService clients, with no rollback, deadlock, or
expired unreleased session. Registration and login p95 were 5.46 and 5.33
seconds under the intentional password-hashing and pool queue burst. This is
bounded admission backpressure, not a steady-state gameplay delay.

The 400-bot run proves the pool boundary also holds during controlled overload.
Every account and session lifecycle completed, but the dense map exceeded the
worker-wide 38 MiB/s snapshot budget. SimulationWorker deliberately dropped
about 2.52 million unreliable snapshots to remain available. Simulation tick
p99 reached the 50 ms bucket, snapshot gaps grew, and input acknowledgement p95
reached 479.5 ms. This is a SimulationWorker bandwidth and dense-interest
quality boundary, not an AuthService or PostgreSQL failure. Raising the snapshot
budget would remove protection rather than fix the all-to-all workload.

## Scope Boundary

Full-stack mode currently measures account registration, character creation,
login, shard discovery, placement, tickets, simulation-session leases, UDP
gameplay traffic, leave, and logout. It does not generate mixed inventory,
trading, death, or corpse-loot HTTP workloads. Those should be added as explicit
workload profiles rather than hidden inside this baseline.

Distributed load generation and remote service targets are not implemented.

## Command Reference

List every supported option:

```powershell
dotnet run --project Tools/StackStressGenerator -- --help
```

Important mode-specific options are:

- `--mode worker-only|full-stack`
- `--authority-url` for the worker-only in-memory authority
- `--auth-service-url` for full-stack AuthService
- `--confirm-disposable-database` for the exact full-stack database name
- `--http-concurrency` for concurrent full-stack account lifecycles
- `--run-id` for durable full-stack stress identity names

Common load options include `--bots`, `--bot-start-index`, `--ramp-step`,
`--ramp-interval-seconds`, `--duration-seconds`, `--join-timeout-seconds`,
`--report-interval-seconds`, `--seed`, and `--output`.

The same seed and bot indices reproduce movement patterns. Tickets, account
session tokens, and generated passwords remain random for every run.

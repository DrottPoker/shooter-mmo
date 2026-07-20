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

Mode selects the stack boundary. Workload selects what every admitted client
does after joining:

| Workload | Supported mode | Durable item traffic | Movement pattern | Primary use |
| --- | --- | :---: | --- | --- |
| `lifecycle` | `worker-only`, `full-stack` | No | Existing square path | Preserve the comparable admission and realtime baseline |
| `inventory` | `full-stack` | Yes | Existing square path | Relocate one item between Permanent Inventory and Secure Container |
| `loot-hotspot` | `full-stack` | Yes | Hold at the shared corpse | Contend on one durable Mob corpse with deposit and partial loot |
| `mixed-gameplay` | `full-stack` | Yes | Bounded movement near the corpse | Combine movement, inventory relocation, corpse deposit, and corpse loot |

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
AuthService -------- session cache and auth rate limits ------------> Redis
AuthService and SimulationWorker health checks ---------------------> Redis
```

Each full-stack bot completes the normal public lifecycle:

1. Register an account.
2. Create a character.
3. Log out the registration session.
4. Log in with the created account.
5. Optionally repeat authenticated session validation requests to isolate the
   Redis cache and PostgreSQL fallback cost.
6. List characters and shards.
7. Request a normal join ticket through AuthService placement.
8. Join SimulationWorker over UDP and run the same realtime workload as
   worker-only mode.
9. Leave the simulation cleanly and log out the account session.

All bots still use production account authentication, placement, join tickets,
UDP item commands, worker authority checks, AuthService transactions, and
PostgreSQL custody. Non-lifecycle workloads use guarded Development-only fixture
routes only to create each bot's initial items and the shared durable corpse.
The measured gameplay operations do not call those fixture routes. Each bot owns
an independent UDP endpoint. Clients use LiteNetLib manual polling so the
harness does not create one transport thread per bot.

### Dense Replication And Shared Corpse Scheduling

Protocol version `14` separates local authority from degradable remote
presentation. Every joined client receives a small owner snapshot at the full
configured 15 Hz. That packet carries the authoritative local state and input
acknowledgement. Remote visible entities use MTU-safe chunks of at most 24
records and a rotating fair scheduler. When a dense visibility set cannot fit
inside the worker-wide byte budget, remote entity cadence decreases while owner
cadence remains fixed.

`SimulationWorker:SnapshotReplication:OverloadTargetUtilizationBasisPoints`
defaults to `9500`. The scheduler targets 95 percent of each recipient's fair
share, leaving token-bucket margin for tick jitter. The worker reports remote
work not sent in the current frame through
`simulation_worker.simulation.snapshot.entity_updates_deferred`. This is
scheduled quality degradation, not a dropped owner frame. Stress report
`estimatedMissingSnapshots` counts owner sequence gaps only. At the current
38 MiB/s aggregate budget, 400 mutually visible players receive owner state at
15 Hz and about 5.6 Hz average remote entity state. A larger World should reduce
the visible set before this dense fallback is needed.

Shared corpse traffic has two separate bounds. One committed mutation is
encoded once and reuses the same packet bytes for every viewer. Open and refresh
state targets only the requester. Mutations for one corpse enter AuthService one
at a time through a worker-local coordinator, while different corpses remain
parallel. This retains AuthService idempotency and final PostgreSQL validation
without occupying the complete connection pool with waiters for one corpse row.
The performance meter publishes
`simulation_worker.corpse.mutation.backlog` and
`simulation_worker.corpse.mutation.active_aggregates`, and the interval log
retains the maximum observed queue depth.

## Full-stack Database Safety

Full-stack mode is intentionally restricted to a disposable local database.
It refuses to start unless all of these checks pass:

- AuthService and PostgreSQL use loopback addresses.
- The PostgreSQL database name contains `stress` or `test`.
- `--confirm-disposable-database` exactly matches the connected database name.
- AuthService has initialized the schema.
- The `accounts` table is empty before the run.
- The final account row count matches the number of successful registrations.

Gameplay fixtures add another safety boundary. AuthService accepts fixture
creation only when all of these conditions hold:

- The hosting environment is `Development`.
- `StackStressFixtures:Enabled` is explicitly true.
- AuthService is connected to loopback PostgreSQL whose database name contains
  `stress` or `test`.
- The request is authenticated as a real account, originates from loopback, and
  supplies the configured fixture secret.

Fixture creation uses `ItemTransactionService` and the durable corpse service.
It does not write inventory rows directly. The generator validates final item
quantity, slot uniqueness, custody, and pending-operation invariants through its
separate read-only PostgreSQL sampler.

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
dotnet run --project SimulationWorker --configuration Release --no-build --no-launch-profile
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
dotnet run --project AuthService --configuration Release --no-build --no-launch-profile
```

Expected result: migrations complete and AuthService listens on
`http://127.0.0.1:5500`.

For `inventory`, `loot-hotspot`, or `mixed-gameplay`, also set the guarded
fixture configuration before starting AuthService:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
$env:StackStressFixtures__Enabled = "true"
$env:STACK_STRESS_FIXTURE_SECRET = "stack-stress-local-fixture-secret-2026"
```

The fixture secret is local test data, but it must contain at least 32
characters and match the value used by the generator.

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
dotnet run --project SimulationWorker --configuration Release --no-build --no-launch-profile
```

Expected result: the worker registers `local-shard-1` and
`development-world-2` with AuthService and advertises UDP port `27025`.

For `loot-hotspot` or `mixed-gameplay`, use a one-second durable corpse
reconciliation interval so a newly provisioned stress corpse becomes visible
quickly:

```powershell
$env:SimulationWorker__ItemInteraction__DurableCorpseRefreshSeconds = "1"
```

In terminal 3, run the complete lifecycle. Set the connection string in the
environment instead of passing it as an argument:

```powershell
$env:STACK_STRESS_POSTGRES = "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only"
dotnet run --project Tools/StackStressGenerator `
  --configuration Release `
  --no-build `
  -- `
  --mode full-stack `
  --workload lifecycle `
  --auth-service-url http://127.0.0.1:5500 `
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
- Every account logs in and obtains a normal AuthService placement and ticket.
- Every account performs the configured authenticated session checks, reported
  separately as `account_session_validate`.
- All 100 bots join, run the UDP workload, and leave cleanly.
- Every active account session is logged out.
- PostgreSQL reports no expired unreleased simulation sessions or deadlocks.
- The final database account count is exactly 100.
- The command exits zero and writes a full-stack JSON report.

### Run Gameplay Workloads

After starting the same disposable stack with the fixture configuration, select
one explicit workload in terminal 3. This example runs the complete mixed path:

```powershell
$env:STACK_STRESS_POSTGRES = "Host=127.0.0.1;Port=56432;Database=shooter_mmo_stack_stress_test;Username=shooter_mmo_stack_stress;Password=stack_stress_local_only"
$env:STACK_STRESS_FIXTURE_SECRET = "stack-stress-local-fixture-secret-2026"
dotnet run --project Tools/StackStressGenerator `
  --configuration Release `
  --no-build `
  -- `
  --mode full-stack `
  --workload mixed-gameplay `
  --auth-service-url http://127.0.0.1:5500 `
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
path. Recreate the Docker stack before every workload because the database must
start empty.

Expected result:

- Every gameplay bot receives only the minimal initial fixture items.
- Loot workloads create one shared durable Mob corpse at the configured hotspot.
- Item and corpse commands travel over UDP and are authorized by the worker.
- AuthService commits every accepted change through its normal item transaction
  boundary.
- Concurrent revision conflicts are reported as expected conflicts, not hidden
  as successes or counted as infrastructure failures.
- The run exits zero only when lifecycle checks, gameplay operations, and final
  PostgreSQL item invariants pass.

Stop AuthService and SimulationWorker with Ctrl+C, then clean up:

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
Remove-Item Env:AUTH_SERVICE_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_UDP_PORT -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_HOST -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_ADVERTISED_UDP_PORT -ErrorAction SilentlyContinue
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

The 2026-07-19 local A/B run used `development-world-2`, 100 full-stack
lifecycle bots, HTTP concurrency 64, and 50 explicit session validation
requests per bot. Both runs used the distributed Redis authentication rate
limit. Only `RedisAcceleration:SessionCacheEnabled` changed. The disposable
PostgreSQL and Redis stack was recreated between runs.

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
After the cache-on run completed, Redis contained 200 temporary revocation
tombstones and zero token cache entries, matching the registration-session and
final-session logout paths.

This result validates the narrow optimization: repeated authentication no
longer needs one PostgreSQL lookup per request. It does not remove the current
admission-wave connection spike. Registration, BCrypt password work, character
creation, login replacement, placement, join-ticket creation, and simulation
session setup still reach PostgreSQL and should be profiled separately before
changing pool limits or service boundaries.

A cache-on `mixed-gameplay` regression then admitted and cleanly logged out 100
of 100 clients on `development-world-2`. It completed 978 inventory relocations,
431 corpse deposits, 198 partial corpse-loot attempts, 100 corpse opens, and 470
corpse refreshes. Revision conflicts were classified as expected contention.
There were no unexpected rejections, timeouts, incomplete operations, deadlocks,
expired unreleased sessions, or item invariant violations.

The live dependency-failure check stopped Redis after creating an active cached
session. `GET /api/accounts/session` still returned 204 through PostgreSQL
fallback. Logout returned 503 because its tombstone could not be stored. After
Redis restarted, the same session still returned 204, proving the failed logout
did not commit a durable revocation, and the next logout returned 204. This is
the required safety contract for cache availability failures.

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
- AuthService process CPU, memory, and thread summaries when the local process
  can be identified.
- PostgreSQL connection, active wait, idle-in-transaction, transaction, row,
  temporary-file, I/O timing, and deadlock statistics.
- Maximum active and expired unreleased simulation sessions, minimum observed
  lease headroom, and session-table insert and update counts.
- Final expected and actual item quantities, duplicate occupied slots, invalid
  item custody, and pending item-operation counts.

SimulationWorker logs interval timing for network polling, asynchronous
completion handling, join queue and finalization, simulation ticks and lag,
movement, collision streaming, interest work, snapshot construction and send,
and fixed-tick resynchronization. Timing metrics are also published through the
`ShooterMmo.SimulationWorker.Performance` meter. Network counters remain under
`ShooterMmo.SimulationWorker.Realtime`. Performance counters also expose shared
snapshot packet encoding, sent packets, and deferred remote entity updates.

## Quality Boundary

A tier is not supported merely because every process remains alive. Require:

- Every requested bot joins and leaves successfully.
- No unexpected disconnect, protocol error, or normal-input quota rejection.
- No fixed-tick resynchronization during the steady interval.
- Simulation tick p99 remains below the 33.34 ms fixed-tick budget.
- Owner snapshot gaps are zero, snapshot drops are zero, and input
  acknowledgement latency remains stable.
- Remote entity cadence and deferred-update rate match the tier's documented
  quality target. A process remaining alive is not enough if remote presentation
  falls below that target.
- Worker, AuthService, and generator working sets stabilize after warmup.
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
rerun the same seed, mode, and tier for comparison.

Every future high-frequency gameplay feature must add or extend a workload that
captures its fan-out, database operations, allocation rate, backlog, and encoded
bytes before a higher tier is accepted. Preserve owner feedback and session
leases before degrading remote presentation work.

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
| Protocol 13 identical baseline | 400 | 60 s | 400/400 | 226,962 | 425.0 ms | 76.5% | at most 24 ms | 176.7 MiB | Snapshot quality failed |
| Protocol 14 owner-priority scheduling | 400 | 60 s | 400/400 | 0 | 160.1 ms | 78.9% | at most 24 ms | 155.1 MiB | Clean owner path, remote cadence about 5.6 Hz |

At 200 bots, reusable interest state and visibility-set packet sharing reduced
steady process allocation from about 1,690 MiB to 258 MiB per ten-second metrics
interval. Snapshot-pipeline average fell from 11.9 ms to 2.4 ms and worker CPU
fell by about 21 percent.

The identical 400-bot comparison used seed `1337`, four 100-bot ramp steps five
seconds apart, and a 60-second steady interval. Protocol 13 attempted the full
400-entity set for every recipient at 15 Hz. That required about 100 MB/s before
transport overhead while the checked-in worker budget is 38 MiB/s. Aggregate
backpressure therefore created 226,962 snapshot sequence gaps and raised input
acknowledgement p95 to 425.0 ms.

Protocol 14 preserves a dedicated 15 Hz owner packet, applies the 95 percent
fair-share target, and rotates 149 remote entity records per dense frame. The
same tier completed with zero owner sequence gaps, zero snapshot drops, input
acknowledgement p95 of 160.1 ms, and no tick resynchronization. Steady worker
traffic was about 36.9 MiB/s. This establishes a clean local authority path for
400 co-located players, not 15 Hz full-state replication of all 399 remote
players. The current average remote entity cadence is about 5.6 Hz. Raising the
budget is not the default fix. Larger maps should reduce interest cardinality,
and future protocol work may add measured quantization or delta compression.
The 250- and 400-bot results are short runs. A 30 to 60 minute soak is still
required before declaring a supported production tier.

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

The historical 400-bot full-stack run proves the pool boundary also held during
protocol 13 controlled overload. Its lifecycle completed, but dense replication
failed the old worker quality boundary. The protocol 14 worker-only comparison
above fixes owner starvation. A protocol 14 full-stack 400-bot lifecycle rerun
is still required before replacing this historical row.

### Mixed Gameplay Results

The following profiles used one shared durable corpse, real UDP item and corpse
commands, AuthService transactions, PostgreSQL custody, and final database
invariant checks. The 250-bot comparisons used the same seed, 64-way HTTP
admission, five 50-bot ramp steps five seconds apart, three-second inventory
operations, four-second corpse operations, and a 60-second steady interval.

| Run | Bots | Complete lifecycle | Unexpected gameplay rejections | Transaction timeout codes | PostgreSQL max connections | Maximum active waits | Input ack p95 | Worker average single-core CPU | Maximum working set | Result |
| --- | ---: | :---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Clean mixed baseline | 100 | 100/100 | 0 | 0 | 55 | 17 | 135.5 ms | 39.4% | 122.5 MiB | Clean short run |
| Before corpse fan-out optimization | 250 | 250/250 | 322 | 10 | 66 | 62 | 662.0 ms | 115.6% | 602.7 MiB | Failed gameplay quality |
| Encoded reuse and targeted refresh | 250 | 250/250 | 3 | 3 | 67 | 61 | 190.3 ms | 73.7% | 168.1 MiB | Database hotspot remained |
| Per-corpse mutation coordination | 250 | 250/250 | 0 | 0 | 66 | 39 | 186.5 ms | 68.9% | Clean short run |

Before optimization, every successful open or refresh rebuilt a full corpse
view and broadcast it to every viewer. Every viewer also re-encoded identical
chunks. The resulting multiplicative fan-out drove worker allocation above
2 GiB per ten-second metrics interval, grew the completion backlog to 188, and
produced hundreds of range and transaction failures as acknowledgements fell
behind.

The final implementation sends open and refresh state only to the requester,
encodes each committed update once for all viewers, reserves snapshot burst
headroom, and serializes only mutations that target the same corpse. Different
corpses and ordinary inventory operations remain parallel. The final run had
zero snapshot drops, zero operation timeouts, zero unexpected gameplay results,
zero deadlocks, zero expired unreleased sessions, and exact item quantity, slot,
custody, and operation-journal invariants. It completed 5,056 successful
inventory relocations and 2,514 successful corpse refreshes.

The intentionally pathological shared corpse still exposes aggregate latency:
2,407 stale deposit attempts were reported as expected conflicts and deposit
p95 was 3.44 seconds because one corpse is one serialized custody aggregate.
This is now bounded backpressure instead of database pool exhaustion. Multiple
geographically separate corpse hotspots need a future workload to verify that
the per-corpse coordinator preserves parallel throughput. The clean result is a
short stability run, not a production capacity declaration.

## Scope Boundary

Full-stack mode measures account registration, character creation, login, shard
discovery, placement, tickets, simulation-session leases, UDP gameplay traffic,
leave, and logout. Explicit gameplay workloads also measure durable inventory
relocation and shared corpse deposit and loot through the real worker and
AuthService transaction path.

Trading, combat-driven deaths, insurance claims, quest item policies, Bank,
Recovery Storage, equipment, Bag nesting, and multiple geographically separated
loot hotspots are not generated. Add those as explicit future workload profiles
so the stable lifecycle baseline does not silently change.

Distributed load generation and remote service targets are not implemented.

## Command Reference

List every supported option:

```powershell
dotnet run --project Tools/StackStressGenerator -- --help
```

Important mode-specific options are:

- `--mode worker-only|full-stack`
- `--workload lifecycle|inventory|loot-hotspot|mixed-gameplay`
- `--authority-url` for the worker-only in-memory authority
- `--auth-service-url` for full-stack AuthService
- `--confirm-disposable-database` for the exact full-stack database name
- `--http-concurrency` for concurrent full-stack account lifecycles
- `--session-validation-requests-per-bot` for 0 to 1000 repeated authenticated
  session checks after each login
- `--run-id` for durable full-stack stress identity names

Common load options include `--bots`, `--bot-start-index`, `--ramp-step`,
`--ramp-interval-seconds`, `--duration-seconds`, `--join-timeout-seconds`,
`--report-interval-seconds`, `--seed`, and `--output`.

Gameplay options include `--inventory-operation-interval-seconds`,
`--loot-operation-interval-seconds`,
`--gameplay-operation-timeout-seconds`, and `--loot-hotspot-x`,
`--loot-hotspot-y`, and `--loot-hotspot-z`. Supply the fixture secret through
`STACK_STRESS_FIXTURE_SECRET`; it is intentionally not accepted as a command-line
argument.

The same seed and bot indices reproduce movement patterns. Tickets, account
session tokens, and generated passwords remain random for every run.

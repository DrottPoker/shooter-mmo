# Simulation Stress Testing

Last updated: 2026-07-14

## Purpose

`Tools/SimulationStressGenerator` establishes a repeatable SimulationWorker
performance baseline before persistent gameplay systems increase server load.
It runs headless LiteNetLib bots through the real realtime join, entity,
movement, snapshot, leave, and session flows without creating accounts,
characters, tickets, or simulation sessions in PostgreSQL.

The current small test map keeps every bot inside the same interest radius. This
is intentionally a hotspot test with near all-to-all visibility. It measures a
worst-case local crowd, not the distributed capacity of a future larger World.

This finite benchmark deliberately replaces AuthService with its isolated
authority and cannot share the worker with the normal local Unity flow. Use
[Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) when the goal is a
long-running visible population beside real local players.

## Architecture

```text
SimulationStressGenerator
  in-memory stress authority <--- authenticated HTTP ---> SimulationWorker
  headless stress bots        -------- LiteNetLib UDP ---> SimulationWorker

AuthService: not running
PostgreSQL: not used
Unity: not used
```

The in-memory authority implements only the service contracts required by
SimulationWorker:

- Readiness.
- Worker heartbeat and graceful offline registration.
- Exact-runtime one-time ticket consumption.
- Simulation-session heartbeat and release.

Each bot receives a unique in-memory account id, character id, and display name
such as `Stress Bot 1`. Tickets are random, hash-indexed, short lived, one use,
and bound to the exact worker runtime and shard. Session tokens remain in memory
and are validated for every heartbeat and release. Nothing survives the tool
process.

Each bot owns an independent UDP client and endpoint. The clients run in
LiteNetLib manual mode and are polled by the coordinator loop, avoiding one or
more background threads per bot while preserving separate network connections.

The authority binds only to an HTTP loopback address. SimulationWorker itself
contains no stress admission mode and no production authentication bypass.

## Build

Run from the repository root:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
dotnet build ShooterMmo.slnx --configuration Release --no-restore
```

Expected result: the complete solution, including
`SimulationStressGenerator`, builds with zero warnings and zero errors.

Do not run stress measurements under the debugger. Record the current commit,
CPU, logical core count, memory, operating system, bot count, and worker
configuration with every accepted baseline.

## Run A Hotspot Stress Test

AuthService, PostgreSQL, Redis, and Unity are not required for this
SimulationWorker-only test.

In the first PowerShell terminal, start the generator:

```powershell
dotnet run --project Tools/SimulationStressGenerator `
  --configuration Release `
  --no-build `
  -- `
  --bots 100 `
  --ramp-step 25 `
  --ramp-interval-seconds 10 `
  --duration-seconds 300 `
  --report-interval-seconds 10
```

The tool binds `http://127.0.0.1:5099` by default, creates an ephemeral worker
secret, prints the exact SimulationWorker environment commands, and waits for
the worker heartbeat. The secret is not written to the JSON report.

In the second PowerShell terminal, copy the printed environment commands. Also
shorten the metrics interval for the run:

```powershell
$env:SimulationWorker__NetworkMetricsLogSeconds = "10"
dotnet run --project SimulationWorker --configuration Release --no-build
```

Expected result:

- SimulationWorker registers its exact runtime with the stress authority.
- Bots start in groups of 25 until all 100 have started.
- The steady interval starts only after every bot has either joined or failed,
  so admission bursts are not mislabeled as steady-state simulation load.
- Worker logs show characters named `Stress Bot 1` through `Stress Bot 100`.
- Joined bots send movement input at the worker-provided simulation rate.
- Bots receive reliable entity lifecycle packets and unreliable snapshots.
- The generator reports join state, failures, snapshot counts, estimated
  sequence gaps, and input acknowledgement p95 every ten seconds.
- After the steady interval, every joined bot requests a graceful leave.
- The command exits zero only when every requested bot joined and left without
  failure.
- A JSON report is written under `artifacts/stress`.

Stop SimulationWorker after the generator completes. Clear terminal-specific
overrides before returning to ordinary local development:

```powershell
Remove-Item Env:AUTH_SERVICE_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_SERVICE_SECRET -ErrorAction SilentlyContinue
Remove-Item Env:SIMULATION_WORKER_MAX_CONNECTIONS -ErrorAction SilentlyContinue
Remove-Item Env:SimulationWorker__NetworkMetricsLogSeconds -ErrorAction SilentlyContinue
```

No manual Unity Editor steps are required.

## Ramp And Soak Strategy

Use these initial tiers on one recorded machine:

1. 10 bots for a transport and tooling smoke test.
2. 25 bots.
3. 50 bots.
4. 75 bots.
5. 100 bots, which is the checked-in worker capacity.

Run each steady tier for at least five minutes. After a clean 100-bot result,
raise the load-test-only worker capacity and try 150, 250, and 500 bots. Do not
change the checked-in production default based on a single local machine.

Run the highest clean tier for 30 to 60 minutes to detect memory growth, GC
pressure, session-heartbeat instability, and late disconnects.

The stress authority deliberately binds only to loopback, so this tool runs the
generator and SimulationWorker on the same machine. The reported limit includes
contention from both processes. Use the separate process metrics to reject a run
where the generator becomes the limiting process. A future distributed load
driver must keep authority credentials on a private test network and is outside
this tool's current security boundary.

## Measurements

The JSON report contains:

- Worker runtime, protocol, simulation revision, and collision revision.
- Requested, joined, completed, and failed bot counts.
- Join latency percentiles.
- Input acknowledgement latency percentiles. Console progress uses an
  interval-only bounded reservoir, while the final JSON uses a separate bounded
  reservoir for the complete run. Sample count, minimum, maximum, and average
  remain exact.
- Sent and received packets and bytes from the bot side.
- Snapshot packets and estimated missing snapshot sequences.
- Reliable spawn and despawn packet counts.
- Stress authority ticket and session counters.
- SimulationWorker single-core-equivalent CPU, whole-machine CPU, working set,
  private memory, and thread count when exactly one native `SimulationWorker`
  process can be discovered. Working-set and private-memory summaries include
  initial, average, maximum, final, and steady-state boundary values captured
  before graceful leave cleanup begins.
- Stress-generator CPU, working set, private memory, and thread count so a local
  generator bottleneck is not mistaken for the worker capacity boundary.

SimulationWorker logs interval timing for:

- Network polling.
- Completed asynchronous operations.
- Complete simulation ticks.
- Simulation tick lag.
- Collision streaming.
- Player movement simulation.
- Interest rebuild and refresh.
- The complete snapshot pipeline, including the separately reported interest
  phase, snapshot construction, encoding, and send work.
- Fixed-tick resynchronizations.

The interval log also reports process allocation, GC collections by generation,
managed heap size and fragmentation, live managed memory, distinct snapshot
visibility groups, encoded snapshot packets, and sent snapshot packets. Network
metrics report total dropped snapshot packets and the subset dropped by the
worker-wide aggregate snapshot budget.

Timing metrics are also published through the
`ShooterMmo.SimulationWorker.Performance` meter. Existing network counters remain
under `ShooterMmo.SimulationWorker.Realtime`.

## Initial Quality Boundary

A tier is not considered supported merely because the process remains alive.
The initial local boundary is:

- Every requested bot joins successfully.
- Every joined bot leaves successfully.
- No unexpected disconnect or protocol error occurs.
- No normal-input UDP quota rejection occurs.
- No fixed-tick resynchronization occurs during the steady interval.
- Simulation tick p99 remains below the 33.34 ms fixed-tick budget.
- Snapshot sequence gaps do not grow continuously.
- Input acknowledgement latency remains stable as the tier runs.
- Working set reaches a stable range after warmup instead of growing
  continuously.
- Worker registration and simulation-session leases remain valid.

Record the first boundary that fails and the phase whose duration grows with the
load. Optimize only after a repeatable run identifies the limiting phase, then
rerun the same seed and tier for comparison.

## Current Local Baseline

The baseline and optimization runs were measured on 2026-07-14 with the
generator and worker sharing one Windows development machine:

- AMD Ryzen 7 5800, 8 cores and 16 logical processors.
- 31.9 GiB physical memory.
- 30 simulation ticks and 15 snapshots per second.
- Seed 1337 and the current small map, where all bots are mutually visible.

| Run | Bots | Steady interval | Clean join and leave | Snapshot gaps | Input ack p95 | Worker average single-core CPU | Simulation tick p99 bucket | Maximum working set | Result |
| --- | ---: | ---: | :---: | ---: | ---: | ---: | ---: | ---: | --- |
| Before optimization | 200 | 30 s | 200/200 | 0 | 144.0 ms | 69.2% | at most 50 ms | 111.9 MiB | Outside tick budget |
| Snapshot and interest optimization | 200 | 30 s | 200/200 | 0 | 137.4 ms | 54.9% | at most 8 ms | 112.5 MiB | Clean short run |
| Final clean tier | 250 | 30 s | 250/250 | 0 | 148.9 ms | 70.8% | at most 16 ms | 129.8 MiB | Clean short run |
| Controlled overload | 400 | 120 s | 400/400 | 461,263 | 427.0 ms | 95.6% | at most 50 ms | 239.5 MiB | Not a supported quality tier |

At 200 bots with the same seed, reusable interest state and visibility-set
packet sharing reduced steady process allocation from about 1,690 MiB to 258
MiB per ten-second metrics interval. The complete snapshot pipeline average fell
from 11.9 ms to 2.4 ms, its interest phase fell from 3.5 ms to 1.4 ms, worker CPU
fell by about 21%, and simulation-tick p99 moved back inside the fixed-tick
budget.

Completed join and leave operations now have a per-loop time budget. This
removed the repeatable admission-completion burst that previously caused a tick
clock resynchronization at 250 bots. The generator also uses separate bounded
latency reservoirs for interval and cumulative summaries, preventing a long run
from retaining every input acknowledgement sample.

Snapshot output now has two levels of protection. Per-peer quotas remain in
place, while a worker-wide token bucket admits complete snapshot chunk batches
at 38 MiB per second with a 4 MiB burst. The recipient start advances past the
admitted group after every broadcast. This keeps overload fair and prevents the
same peers from waiting several seconds for their next snapshot.

Before aggregate backpressure, a 30-second 400-bot run joined every bot but only
4 left cleanly. The other 396 timed out, input acknowledgement p95 reached 7.19
seconds, and worker working set reached 2.84 GiB. With the final budget and fair
rotation, a two-minute run completed all 400 joins and leaves, kept input
acknowledgement p95 at 427 ms, recorded no tick resynchronization, and kept
working set below 240 MiB. The worker remained available by deliberately
dropping unreliable snapshots.

The 400-bot tier still fails the quality boundary because snapshot gaps grow,
simulation-tick p99 crosses 33.34 ms, and steady working-set windows rose from
173 MiB to 234 MiB during the two-minute sample. It is a controlled overload
result, not a production capacity claim. The 250-bot result is also only a short
clean run. A 30 to 60 minute soak is still required before declaring any
production capacity. The checked-in 100-connection worker default remains
unchanged and has substantial margin in this local hotspot test.

## Scope Boundary

This tool isolates SimulationWorker. It does not measure AuthService or
PostgreSQL capacity because the authority, tickets, identities, and leases are
in memory. A later full-stack stress suite must use an isolated disposable
PostgreSQL database and the real AuthService. It must never use the developer or
production database. Distributed load generation is also not implemented.

## Command Reference

List every supported option:

```powershell
dotnet run --project Tools/SimulationStressGenerator -- --help
```

Useful overrides include:

- `--authority-url`
- `--worker-host`
- `--worker-udp-port`
- `--bots`
- `--bot-start-index`
- `--ramp-step`
- `--ramp-interval-seconds`
- `--duration-seconds`
- `--join-timeout-seconds`
- `--seed`
- `--output`

The same seed and bot indices produce the same identity ids and movement
patterns, while ticket and session secrets remain random for every run.

# Stack Stress Testing

## Purpose

`tools/StackStressGenerator` is the repeatable load harness for the realtime
stack. It has two explicit modes so SimulationWorker capacity can be measured
separately from account, placement, session, GameService, and PostgreSQL load.
`tools/Test-Load.ps1` runs a scenario with it from start to finish and judges
it, as [Run A Scenario](#run-a-scenario) describes.

| Mode | Real GameService | PostgreSQL | Realtime UDP | Primary use |
| --- | :---: | :---: | :---: | --- |
| `worker-only` | No | No | Yes | Isolate SimulationWorker hotspots and capacity |
| `full-stack` | Yes | Yes | Yes | Measure the complete login-to-game lifecycle and shared stack pressure |

Mode selects the stack boundary. Workload selects what every admitted client
does after joining:

| Workload | Supported mode | Durable item traffic | Movement pattern | Primary use |
| --- | --- | :---: | --- | --- |
| `lifecycle` | `worker-only`, `full-stack` | No | Square walking loop | Preserve the comparable admission and realtime baseline |
| `inventory` | `full-stack` | Yes | Square walking loop | Relocate one item between Permanent Inventory slots |
| `loot-hotspot` | `full-stack` | Yes | Stand at the shared corpse | Contend on one durable Mob corpse with deposit and partial loot |
| `mixed-gameplay` | `full-stack` | Yes | Step back and forth beside the corpse | Combine movement, inventory relocation, corpse deposit, and corpse loot |
| `playing` | `full-stack` | Yes | Planned walks over the World, like players | Play like players in roles: fight Mobs, loot them, die and recover, gather resource nodes, trade at the vendor, and walk, as [Playing Workload](#playing-workload) describes |

Both modes run independent headless LiteNetLib clients through the real join,
movement, snapshot, entity lifecycle, simulation-session, and graceful-leave
paths. Each bot owns an independent UDP endpoint, and the clients use LiteNetLib
manual polling, so the harness does not create one transport thread per bot.
The generator polls every bot every 3 ms with a high-resolution timer and sends
a due input in the same poll, so inputs reach the worker once per tick like a
Unity client's instead of in bursts. The bots are shared over one or more poll
threads, `--poll-threads`, and nothing else runs on a poll thread: admissions,
reports, the process and PostgreSQL samplers, and the bots' HTTP calls run
elsewhere, and whatever touches a bot from outside is posted to its thread. A
square loop has legs of two seconds of walking.

Runs target `local-shard-2` and Development World 1, the 64 m stress arena, by
default, and every bot starts on the World spawn, so all bots stay inside the
same interest radius. This intentionally measures a worst-case local crowd, not the
distributed capacity of a future larger World. The `playing` workload runs
Development World 2 instead, with every bot on its own home ground.

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
empty durable-corpse restoration. Its bots are synthetic, so the worker sends
it no character checkpoint and no final state with a release. Identities,
tickets, and sessions exist only for the lifetime of the generator process.

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

The `playing` workload registers, equips, and logs in every account before the
first bot joins, because players have accounts and characters before they play:
steps 1 to 4 and the kit of
[Kits](#kits) run for all bots first, with the same HTTP concurrency, and the
ramp then measures only the way into the game, steps 6 to 9. This keeps the
registration and login burst, whose BCrypt work kept GameService busy for
seconds in `mixed-100`, out of the measured gameplay.

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
normal development, so its connection strings must come from its environment,
as [What The Script Sets](#what-the-script-sets) does; this final check fails
when GameService wrote to another database.

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
- `StackStressFixtures:Kits` names the kits of the `playing` workload, each a
  list of `DefinitionId` and `Quantity`, as [Kits](#kits) describes. A kit
  name is lowercase letters, digits, and dashes, and a kit grants one to eight
  stacks of 1 to 10,000; startup fails on a malformed kit. A test checks that
  every definition the checked-in settings name exists in the item catalog.

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
`docker compose down` discards the complete stress database. Both ports lie
below 49152, where the dynamic port range of Windows starts; when Windows
reserves one anyway, `docker compose up` fails with a bind error, and both
ports move together in the compose file and in `StressLoadPlan`.

GameService caps its Npgsql pool at `Database:MaximumPoolSize`, 64 in the
checked-in configuration. Do not raise it merely to make a stress tier pass.
Without the cap, one GameService process can open as many connections as
PostgreSQL allows and fail requests with `too many clients already`. The
combined pool limits of all GameService replicas plus worker, health,
monitoring, and administrative clients must stay below PostgreSQL's
non-reserved connection capacity, so pool saturation queues bounded work. A
transient database connection failure returns `503 database_unavailable`.

## Run A Scenario

`tools/Test-Load.ps1` runs one load scenario from start to finish and says
whether it passed the [Quality Boundary](#quality-boundary). Run it from the
repository root with Docker Desktop running:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Load.ps1 -Scenario smoke
```

The script:

1. Builds StackStressGenerator in Release and asks it for the scenario's plan
   with `StackStressGenerator plan`, which validates the scenario and the
   quality boundary file and returns every setting below.
2. Refuses to start when another process uses a port the run needs: TCP 5500
   and UDP 27025 in full-stack mode, TCP 5099 and UDP 27016 in worker-only
   mode, and under a [network profile](#load-runs-under-a-network-profile) the
   proxy's UDP 27035 or 27026, so it never meets a stack it did not start.
3. Builds SimulationWorker, GameService in full-stack mode, and the network
   impairment proxy under a network profile, in Release.
4. In full-stack mode, recreates the disposable stack with
   `docker compose -f docker-compose.stack-stress.yml down` and
   `up -d --wait`. Under a network profile it starts the proxy next and waits
   until it listens. In full-stack mode it starts GameService and waits for
   `/health/ready`, then starts the worker and waits until it listens on UDP
   and its Shard is online.
   In worker-only mode it starts the generator first, because the generator is
   the worker's authority, and then the worker; no container and no
   GameService run.
5. Runs the generator and shows its output, judges the run, and writes the
   verdict.
6. Always stops what it started, also when the run fails or the script is
   interrupted with `Ctrl+C`: the generator, the worker, and GameService with
   `Ctrl+C`, so the worker saves its characters, releases their sessions, and
   goes offline as on a normal shutdown, then the proxy, so the worker's last
   packets still pass and the proxy writes its counts, then keeps the logs of
   the PostgreSQL and Redis containers in the run folder, and then removes the
   containers with `docker compose ... down`. In worker-only mode the generator keeps its
   in-memory authority up after it wrote the summary, and the script stops the
   worker first and the generator after it, so the worker's shutdown reaches
   its authority. A process that does not stop within 30 seconds of its
   `Ctrl+C` is ended, and the script says which.
7. Once everything stopped, runs `StackStressGenerator judge`, which counts the
   lines of the service logs, as [Service Log Classes](#service-log-classes)
   describes, adds the proxy's counts under a network profile, judges the run
   again with them, and rewrites the report and the summary; the judgement
   decides the exit code.
8. Prints what the soak measures, as [Soak](#soak) describes, the proxy's
   counts under a network profile, a table of every rule with its limit, the
   measured value, and pass or fail, the difference to the checked-in baseline
   of the scenario and the profile when there is one, and the result line.

A process inherits whether it ignores `Ctrl+C` from the process that starts
it, and a script started from Git Bash, or by a host that starts it in a new
process group, ignores `Ctrl+C`. Its services then ignored the `Ctrl+C` that
the script sent, so every stop waited its 30 seconds and ended the process
without its shutdown, after a passed run as after a failed one. The script
therefore turns `Ctrl+C` handling on for itself before it starts a process, in
`Start-StackProcess` of `tools/LocalStackCommon.ps1`, which
`tools/Test-LiveStack.ps1` shares.

It starts the apphost executables, such as `Mmorpg.SimulationWorker.exe`, so
the generator's process sampler finds the worker and GameService by name.

| Parameter | Meaning |
| --- | --- |
| `-Scenario <name>` | The file `tools/load/scenarios/<name>.json` |
| `-Bots <count>` | Overrides the scenario's bot count; the ramp step shrinks to it when it is larger |
| `-DurationSeconds <seconds>` | Overrides the scenario's steady interval |
| `-Network <profile>` | Runs the scenario through the network impairment proxy with a profile of `tools/load/network-profiles`, or with `none` directly; overrides the scenario's `network` |
| `-UpdateBaseline` | Copies the run's `summary.json` to `tools/load/baselines/<scenario>.json`, or `<scenario>.<profile>.json` under a network profile |
| `-Plan` | Prints the services, their settings, and the generator arguments, and runs nothing |
| `-ReadyTimeoutSeconds <seconds>` | How long the services may take to become ready, 180 by default |

Exit codes: 0 the run passed the quality boundary, 1 it ran and failed the
boundary or the generator's own checks, 2 it could not run, for example
because of a busy port, an invalid scenario, a failed build, a World variant
whose area has no walkable ground, or a service that ended. A run in which the
generator or the network proxy itself fell behind, which the
[validity rules](#validity) judge, measured nothing: the script prints
`could not measure: the generator or the network proxy fell behind` and also
exits with 2.

Every run writes `artifacts/load/<yyyyMMdd-HHmmss>-<scenario>/`, which Git
ignores:

| File | Content |
| --- | --- |
| `generator-report.json` | The generator's full JSON report, with the folded worker numbers and the verdict |
| `summary.json` | The small summary described in [Baselines](#baselines) |
| `worker-performance.jsonl` | The worker's performance file, one JSON object per metrics interval |
| `generator.log`, `game-service.log`, `simulation-worker.log` | The output of each process, with `.err.log` files for standard error |
| `network-proxy.json`, `network-proxy.log` | Under a network profile: the proxy's counts, which it writes when it stops, and its output with a progress line every 10 seconds |
| `postgres.log`, `redis.log` | The logs of the PostgreSQL and Redis containers, kept before the containers are removed; `postgres.log` holds both statements of every deadlock and every lock wait longer than the deadlock timeout of one second, because the compose file starts PostgreSQL with `log_lock_waits=on` |
| `build-*.log`, `docker-compose-*.log` | The build and container output |
| `world-variant/`, `world-variant.log` | The scenario's [World Variant](#world-variant) and the output of the content compilers that wrote it |

Run `smoke` and `playing-smoke` after changing the load tooling, and the tiers
when [Purpose](#purpose) says. What each of the two shows when it works is the
manual check [Load Tooling](MANUAL_TESTING.md#load-tooling).

The load tests do not run in CI, and `tools/Test-Changes.ps1` runs none: they
need an idle machine, and their numbers depend on it.

Do not run a scenario under the debugger. The generator, the services, and
anything else on the machine share its processors, so record what else ran
with every baseline.

### Scenarios

A scenario is a JSON file in `tools/load/scenarios`; adding one needs no script
change. The generator validates it strictly: an unknown property, an unknown
generator option, an option that the plan owns, or a setting that the plan
sets is an error.

| Property | Meaning |
| --- | --- |
| `description` | What the scenario measures |
| `mode` | `worker-only` or `full-stack` |
| `workload` | `lifecycle`, `inventory`, `loot-hotspot`, `mixed-gameplay`, or `playing`; worker-only runs only `lifecycle` |
| `shardId`, `worldId` | The Shard the worker registers and its World |
| `generator` | Generator options without their dashes and their integer values: `bots` and `duration-seconds` are required, and `ramp-step`, `ramp-interval-seconds`, `http-concurrency`, `session-validation-requests-per-bot`, the operation intervals and timeouts, `report-interval-seconds`, `seed`, and the other shape options of the [Command Reference](#command-reference) are allowed |
| `workerSettings` | Extra worker environment variables, such as `SimulationWorker__ItemInteraction__DurableCorpseRefreshSeconds` |
| `gameServiceSettings` | Extra GameService environment variables, full-stack only |
| `playing` | The roles, home grounds, and churn of the `playing` workload, which needs it, as [Playing Scenarios](#playing-scenarios) describes |
| `worldVariant` | Extra Mob areas and node groups of a [World Variant](#world-variant), full-stack only |
| `network` | The [network profile](#load-runs-under-a-network-profile) the bots' traffic goes through, `none` by default; `-Network` overrides it |

| Scenario | Mode and workload | Shape |
| --- | --- | --- |
| `smoke` | full-stack `mixed-gameplay` | 10 bots in one step, 40 s steady; a tooling check |
| `worker-only-200` | worker-only `lifecycle` | 200 bots in steps of 50 five seconds apart, 60 s steady, 2 poll threads |
| `worker-only-500` | worker-only `lifecycle` | 500 bots in steps of 100 five seconds apart, 60 s steady, 4 poll threads |
| `mixed-100` | full-stack `mixed-gameplay` | 100 bots, 32-way admission, steps of 25 ten seconds apart, 120 s steady |
| `mixed-250` | full-stack `mixed-gameplay` | 250 bots, 64-way admission, steps of 50 five seconds apart, 60 s steady, 2 poll threads; it does not complete at present, as [What Limits The Next Tier](#what-limits-the-next-tier) describes |
| `session-cache-on`, `session-cache-off` | full-stack `lifecycle` | The [Redis Session-cache A/B Baseline](#redis-session-cache-ab-baseline) |
| `playing-smoke` | full-stack `playing` | 20 bots on Development World 2 in all four roles: 8 fighters, 2 of them with bows, in two hunting grounds, 4 gatherers at 8 Iron Veins, 6 walkers, and 2 traders; a variant with 10 Feral Wolves, 10 Grey Wolves, and 8 Iron Veins more; steps of 10 five seconds apart, 90 s steady; a tooling check of the playing workload |
| `playing-100` | full-stack `playing` | 100 bots of the reference mix on the reference variant, steps of 10 six seconds apart, 300 s steady |
| `playing-250` | full-stack `playing` | 250 bots of the reference mix on the reference variant, steps of 25 six seconds apart, 300 s steady, 2 poll threads |
| `playing-500` | full-stack `playing` | 500 bots of the reference mix on the reference variant, steps of 25 five seconds apart, 300 s steady, 4 poll threads; the Scale Target |
| `hotspot-100` | full-stack `playing` | 100 traders in the town of Development World 2 at its one vendor, steps of 20 five seconds apart, 120 s steady |
| `soak-10min` | full-stack `playing` | The rehearsal of the soak: 100 bots of the reference mix on the reference variant with [churn](#churn) of 10 percent a minute, steps of 10 six seconds apart, 600 s steady; long enough for the [memory rule](#quality-boundary), whose limit it set |
| `soak-30min` | full-stack `playing` | The [Soak](#soak): 250 bots of the reference mix on the reference variant with churn of 10 percent a minute, steps of 25 six seconds apart, 1,800 s steady, 2 poll threads |

The tier scenarios use seed `1337`, three-second inventory and four-second
corpse operations, and a ten-second metrics interval; their results are in
[Measured Tiers](#measured-tiers). Scenarios with 200 bots or more name their
poll threads, because one thread cannot drive them, as
[Validity](#validity) describes. All of them keep the product's defaults otherwise, including
the 20-second lingering body.

The reference mix of the playing scenarios is 40 percent fighters, a quarter of
them with bows, 20 percent gatherers, 30 percent walkers, and 10 percent
traders. The reference variant adds 296 wolves to Development World 2, 28 Grey
Wolves and 9 Feral Wolves in each of eight 40 by 40 m hunting grounds spread
over its open land, and 120 resource nodes, four fields of 20 Iron Veins and two
of 20 Oak Trees, away from the hunting grounds, the town, and the spawn. The
gatherers share the fields in turn, so `playing-500` has 100 gatherers for 120
nodes, at least one node per gatherer. The town ground holds Mira, the vendor;
traders stay there, and fighters and gatherers walk there to sell.

### What The Playing Scenarios Are For

| Scenario | What it is for | What a pass means for the Scale Target |
| --- | --- | --- |
| `playing-smoke` | A tooling check after a change to the generator, the bot client, or the fixtures: every role plays, every operation of the workload runs once or more, and the report is complete | Nothing; 20 bots say nothing about capacity |
| `playing-100` | The first tier of the reference workload, which a change to combat, Mobs, interest, items, or the economy runs to see whether 100 players spread over a World still meet the boundary | A tier on the way: the workload that the target names passes at 100 players |
| `playing-250` | The middle tier, which shows how the stack scales between 100 and 500 and which component grows first | A tier on the way at 250 players |
| `playing-500` | The Scale Target itself: 500 players of the reference mix on one Shard, spread over the World | A pass on the reference machine is the claim that the Shard holds the target with the workloads it names, for that machine and commit; a 30-minute soak at the same tier must pass too before the claim is recorded |
| `hotspot-100` | Local density: 100 players in one interest radius, all trading at one NPC, which loads the vendor's interactions, the Economy and Items commits behind them, and the snapshots of a crowd at once | None for the population; it checks the dense case the target sets apart, where the remote cadence may fall by design, and that the vendor path holds a crowd |
| `soak-10min` | The rehearsal of the soak, and with `-Bots 20 -DurationSeconds 180` the tooling check of the churn | None; it sets the memory rule's limit and shows that the soak's numbers are complete |
| `soak-30min` | What slowly goes wrong at 250 players with players coming and going: memory, table growth, drift, and log errors over 30 minutes | A tier holds only when its 30-minute soak passes too; its table growth orders the database work of phase 5 |

### What The Bots Check

- Bots check that every message arrives with the channel and delivery the
  protocol names and decodes, as
  [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md#protocol-handling)
  describes, and act on owner snapshots and the results of their own item and
  corpse operations.
- A bot that fails releases its worker connection at once, so its character
  does not stay joined until the run ends; like a player's, its body stays in
  the world for 20 seconds, as
  [Lingering Bodies](SERVICE_FEATURES.md#lingering-bodies) describes. After the
  steady interval every joined bot requests a graceful leave, and the bodies
  stay 20 seconds, also in full-stack mode, where the account logout that
  follows does not shorten them. The simulation sessions of the bodies stay
  active and keep their leases, so they are neither released nor expired when
  the generator checks PostgreSQL, and the worker releases them when it stops.
- Concurrent revision conflicts are reported as expected conflicts, not hidden
  as successes or counted as failures. In `mixed-gameplay`,
  `corpse_out_of_range` also counts as an expected conflict: the bots start
  crowded on the corpse and keep moving, so the worker correctly rejects a
  request from a bot that drifted past the 3 m interaction radius, for up to
  about one percent of corpse requests. The stationary `loot-hotspot` bots
  never leave the radius, so there it stays unexpected.
- The shared corpse of the loot workloads is a durable Mob corpse, and the bots
  loot part of its stacks. The Unity client offers only whole items from a Mob
  corpse, but no server refuses a part of a durable Mob corpse's stack, so the
  workload's partial loot does not follow the client rule.

### What The Script Sets

The plan sets these for every run; `-Plan` prints them, with the secrets
masked. The local secrets and the connection string are the test values that
`docker-compose.stack-stress.yml` and this document use.

- Both modes: the worker's identity `local-simulation-worker-2`, the scenario's
  Shard and World, its UDP and advertised endpoint on `127.0.0.1`,
  `SimulationWorker:NetworkMetricsLogSeconds`, and
  `SimulationWorker:PerformanceReportPath` in the run folder. The worker
  interval is ten seconds, and a quarter of the steady interval when that is
  shorter, because the tick rules judge only worker intervals that lie entirely
  inside the steady interval and the worker's reporter does not start with it.
  A scenario may set the interval itself; the plan refuses a scenario whose
  steady interval is shorter than four worker intervals, since it could never
  be judged.
- Worker-only: the generator's in-memory authority on `http://127.0.0.1:5099`
  as the worker's GameService, a random worker secret that only this run knows,
  and as many worker connections as bots, at least 100. The worker uses its
  configured Redis only for its health check.
- Full-stack: GameService on `http://127.0.0.1:5500` with the disposable
  PostgreSQL and Redis, the shared worker secret, no development bots, and an
  authentication limit of 1,000 requests per minute instead of the normal five. Gameplay workloads add
  the Development environment, `StackStressFixtures:Enabled`, and the fixture
  secret to GameService, and the scenarios with a shared corpse set a
  one-second durable corpse reconciliation, so the stress corpse becomes
  visible quickly. GameService counts registration and login against two
  separate limits per remote address, `register` and `login`, both with the
  `RateLimiting:Authentication` permit limit, so a run may register 1,000 bots
  and log in 1,000 bots in a minute. The generator provisions inside the limit
  instead of raising it further: when GameService answers 429, it waits the
  answer's `Retry-After`, five seconds without one and at most 60, and sends
  again, up to ten times, and the report counts every wait as the operation's
  `rateLimitWaits`. The playing workload provisions at most eight accounts at
  once, whatever `http-concurrency` says: registration and login hash the
  password, and 32 at once took about eight processors of the reference machine
  and once delayed the worker's registry heartbeat past its five-second timeout
  twice, so the worker stopped itself when its registration lease expired. The generator
  gets the connection string in `STACK_STRESS_POSTGRES` and the fixture secret
  in `STACK_STRESS_FIXTURE_SECRET`, never as arguments. For the [Soak](#soak)
  numbers it gets `--redis-endpoint` with the disposable Redis, and
  `--database-pool-size` and `--worker-heartbeat-timeout-seconds` with
  GameService's `Database:MaximumPoolSize` and
  `Simulation:WorkerHeartbeatTimeoutSeconds`, read from its checked-in
  `Config/appsettings.json` unless the scenario's `gameServiceSettings` set
  them.
- A `playing` scenario with `teleportToGrounds`: the worker gets
  `DOTNET_ENVIRONMENT=Development`, `DevelopmentAdmin__Enabled=true`, and
  `DevelopmentItemInteractions__GlobalBankAndRecoveryAccess=false`, as
  [The Teleport To The Home Ground](#the-teleport-to-the-home-ground)
  explains, and the generator gets `--scenario-file` with the scenario.
- A run under a network profile: the proxy listens on the worker's port plus
  10, UDP 27035 in full-stack and 27026 in worker-only mode, and forwards to the
  worker's own port; the worker advertises the proxy's port, the generator gets
  `--network-profile` with the profile file and, in worker-only mode, the
  proxy's port as `--worker-udp-port`, and the proxy gets the generator's seed
  and `--counts` in the run folder.
- A scenario with a `worldVariant`: before the worker starts, the script runs
  `StackStressGenerator world-variant` and prints whether the variant keeps
  the checked-in collision revision; the worker gets
  `SimulationWorker__WorldDataPath` and the generator `--world-data-path`, both
  the variant's `world-variant/worker` folder.

The generator still runs on its own with the options of the
[Command Reference](#command-reference) when a service is started by hand. In
worker-only mode it then prints the environment commands for the worker.

## Playing Workload

The `playing` workload makes the bots play like players, so a run exercises
combat, Mobs, corpses, deaths, and travel over a World instead of a crowd in one
spot. It runs only in full-stack mode, because synthetic sessions never take
part in combat and are refused by the item and admin paths.

### Roles And Home Grounds

Every bot plays one role, and the scenario gives the share of each in percent.
The counts follow the shares by the largest remainder, and a shuffle by the
seed decides which bot plays which role. Each bot gets a home ground of its
role's kind, in turn over the grounds of that kind, and a share of the fighters,
also chosen by the seed, carries a bow instead of a sword. The same scenario,
seed, and bot indices give every bot the same role, ground, kit, and decisions;
no decision draws on the wall clock.

| Role | Ground kind | Kit | What it does |
| --- | --- | --- | --- |
| Fighter | `hunting` | `sword`, or `bow` | Hunts the Mobs of its ground, as [Fighter](#fighter) describes |
| Gatherer | `nodes` | The ground's `kit`, such as `pickaxe` or `woodcutting-axe` | Gathers the resource nodes of its field, as [Gatherer](#gatherer) describes |
| Walker | `walking` | None | Starts on its ground, then walks to random reachable destinations over the whole World with pauses, as the [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) do |
| Trader | `town` | `trader` | Trades at the town's vendor, as [Trader](#trader) describes |

A role is a small state machine that thinks at human speed: between two
decisions it waits 250 to 650 ms, drawn per bot from the seed, and it sends at
most one intent at a time. A rejected intent, expected or not, is followed by a
pause of two to four seconds, so no bot repeats an intent in a tight loop.
Before it plays, a bot equips its kit over UDP like a player, one item at a
time, and is teleported once to its home ground. Tests drive every role on a
fake body and a fake clock.

### Fighter

A fighter pulls one Mob at a time, like a careful player. It picks among the
living Mobs of its ground, up to 12 m outside the rectangle, that no other
player's attack owns or busies, as the combatant state tells it: the nearest
one, after a penalty of 25 m for every other living aggressive Mob within that
Mob's detection range and 4 m of it, which would join the fight. It still fights
a crowded Mob when nothing better is in reach, so deaths still happen, and the
report counts every Mob it passed over for a less crowded one. It walks into
range of its weapon, stands, and attacks. A Mob that attacks the fighter is
fought first. It attacks until the Mob dies and walks back into range when the
Mob moved away. Below 35 percent health, or below 60 percent while more than one
Mob attacks it, it stops attacking, walks 30 m away, at most 15 seconds, and
rests. When it stands in range and the worker reports that cover blocks its line
of sight, it waits three seconds for the Mob to move and then gives that Mob up
for 30 seconds and takes another one, because walking into range again would
leave it standing where it is. When the Mob died and its corpse is lootable for
the fighter, it walks to the corpse, opens it, loots all, and closes it; a
corpse that another player's attack owns is left and counted. Before the next
pull it rests below 85 percent health until 95 percent, at most 60 seconds.

When its slots or carry capacity run low, or a loot all was refused with
`inventory_full`, the fighter makes [the trip to the vendor](#the-trip-to-the-vendor)
before the next pull. A bow fighter makes it too when it owns fewer than 100
arrows, and buys arrows up to 500, as many as its currency pays for; the vendor
sells them at their value of 1. When its arrows are gone and it carries no more
to equip, it makes the trip once to buy arrows, and it goes on as a walker when
it then still has none, because it had neither arrows nor the currency for
them; the report counts it as `arrowsRanOut`.

After a death the fighter waits for the respawn at the World spawn, walks back
to its death spot in legs of at most one planned route, opens its own corpse,
loots it all, which puts the kit back into the equipment, and hunts on. When
the corpse is not there within five seconds of arriving, the walk makes no
progress, or four minutes pass, the bot goes on as a walker, and the report
counts the lost corpse. A fighter whose weapon is gone goes on as a walker too.

### Gatherer

A gatherer equips the tool of its kit and works the resource nodes of its node
field, up to 4 m outside the rectangle, whose gathering capability needs a tool
kind its tool has, as the compiled actor catalog tells it. It picks the nearest
node with charges, finds a spot with walkable ground from which a character
reaches the node's interaction box and sees its center, walks there, opens the
node, starts gathering, and stands still while the yields arrive; moving would
stop the gathering. When the node depletes, the worker stops the gathering and
closes the interaction, and the gatherer picks the next node. It checks the
line of sight before it walks, with the worker's rule: a ray through the combat
query layer of the same collision from the eye, 1.6 m above the stand point, to
the center of the node's interaction box.

- A node that depleted while the gatherer walked to it, or that the worker
  reports as depleted at the open or the start, counts as found empty and is
  passed over for 10 seconds.
- A node without a spot in reach and in sight, or one the worker reports out of
  sight or reach, is given up for 60 seconds and counted, so the gatherer never
  stands still in front of cover.
- When no node of the field has charges, the gatherer waits for their respawn
  and the report counts the empty field once per wait.
- When its slots or carry capacity run low, it closes the node and makes
  [the trip to the vendor](#the-trip-to-the-vendor), which sells its yields
  and keeps its tool.
- A Mob that attacks it makes it close the node and walk 30 m away, at most 15
  seconds, before it looks for a node again.
- After a death it waits for the respawn, walks to its corpse, loots it back,
  equips its tool again, and gathers on; a lost corpse makes it a walker, as
  for a fighter.

The share of node choices that found the node empty, `nodesFoundEmpty` of
`gatherAttempts`, shows whether the fields hold enough nodes for their
gatherers.

### Trader

A trader stays in the town. It walks up to the vendor, opens it, makes two to
five trades 1.5 to 4 seconds apart, closes it, and then idles 8 to 25 seconds,
standing or strolling to a random point of the town, before the next visit. A
trade is one of these, drawn per trade: a purchase of a few arrows or another
cheap supply that leaves the trader at least 20 currency, a sale of one to
three of a stack it carries, such as the 50 Wolf Pelts of its kit or what it
bought before, or the move of a carried item to a free slot of its inventory.
When the drawn trade is impossible, it makes another kind. A trader whose slots
or carry capacity run low sells first. After a death it recovers its corpse
like a gatherer.

### The Trip To The Vendor

The role base makes the trip for every role that needs it, so a fighter and a
gatherer sell the same way:

1. The bot walks toward the center of the scenario's town ground until the
   vendor, the NPC whose menu offers the vendor capability, is in its view.
2. It picks a spot with walkable ground within 0.8 to 2.2 m of the vendor's
   interaction box, inside the worker's start range of 3 m, from which it sees
   the box's center, walks there, and opens the vendor.
3. One trade every one to 2.5 seconds, it sells every carried stack that is not
   part of its kit and that the vendor buys for more than nothing, and then
   buys what its role needs, such as a bow fighter's arrows. It prices every
   trade with the catalog's `VendorPricing`, as the Unity client does, and
   sends the price it expects.
4. It closes the vendor and walks back to the center of its home ground.

A trip that ends with the bot still short of room, because nothing it carries
sells, or that found no vendor, is counted, and the next trip waits five
minutes. So does the next trip after a visit that could not buy what the role
still lacks, such as arrows a bow fighter cannot pay for: it hunts on with the
arrows it has. A Mob that attacks the bot ends the trip, so the role can fight or
flee, and the need starts a new trip later. A scenario with fighters,
gatherers, or traders needs a ground of kind `town`.

### What A Playing Bot Sees And Sends

Each playing bot keeps a world view in its `SimulationBotSession` from the
messages it decodes anyway, and the session decides nothing:

- The world actors in interest, with the Mob positions of the remote snapshots,
  and the remote players.
- Every combatant's state, with its health, target, and the player whose
  attack owns it; the bot's own combat state; and the combat events.
- The corpses of the newest complete presence snapshot, with `IsLootable`, and
  the revision of the open corpse view.
- World interaction and gathering state, and the admin state.
- Its own items, from the kit or a full read, patched by
  `OwnerItemStateChanged` and `OwnerItemQuantityChanged`, with its currency
  and, from every carry state, its carried weight and carry capacity. When a
  committed change reached the bot only as a newer carry state, such as a
  death, or a result reports a revision its items do not have, the bot reads
  the full item state over HTTP, as the Unity client does; it never polls.

A remote snapshot travels unreliably, so on a real network it can arrive twice
or after a newer one; the view keeps every remote player and Mob at the
position of the newest snapshot that carried it.

It sends only what a Unity client can send: movement inputs with planned paths,
`ItemOperationIntent` equip and relocate, `CombatIntent`,
`CorpseInteractionIntent`, `WorldInteractionIntent` open, capability action,
and close, and the one `AdminIntent` teleport. It reads what a client reads:
the compiled item catalog for equipment slots, weapon ranges, tool kinds, and
vendor prices, and the compiled actor catalog for the tool a node needs, the
interaction box of an actor, and how far an aggressive Mob detects a player. Routes come from the same planner and collision as
the Unity client's clicks, through `SimulationBotNavigation` in
`tools/SimulationBotClient`, which the active simulation bots share. The worker
drops the movement inputs of a dead character until it respawns, so their
acknowledgement waits for the respawn; the inputs a bot sends while dead, and
the ones still unacknowledged when its death arrives, are left out of the
input acknowledgement latency.

### Kits

The kit fixture route,
`POST /api/development/stack-stress/characters/{characterId}/kit`, grants a new
character the kit of its role into its permanent inventory, one stack per slot
from the first, through `ItemTransactionService`, with the guards of the other
fixture routes. The kit names are the generator's, and the definitions come
from `StackStressFixtures:Kits`:

| Kit | Checked-in Development definitions |
| --- | --- |
| `sword` | `weapon.iron_sword` |
| `bow` | `weapon.training_shortbow` and 500 `ammunition.bronze_arrow` |
| `pickaxe` | `tool.starter_pickaxe` |
| `woodcutting-axe` | `tool.woodcutting_axe` |
| `trader` | 50 `material.wolf_pelt`, goods that the vendor buys |

The bot reads the equipment slot of every kit item and the range of its weapon
from the compiled item catalog, as the client does, and equips the items itself.
Wolves drop arrows, which a bow fighter equips when its stack runs out, and the
vendor sells more, so archers keep shooting in long runs. Every character also
starts with the economy's starting balance of 100.

### The Teleport To The Home Ground

Every character joins at the World spawn, so walking out during the ramp would
put the whole population in one place for the first minute. With
`teleportToGrounds`, each bot sends one development admin teleport right after
its join to the ground under the center of its home ground; everything after
that is walking, also the way back after a death.

The worker carries out admin intents only with `DevelopmentAdmin:Enabled`,
which it accepts only in the Development environment, so the plan sets both on
the worker. The Development environment also changes this on the worker:

- It loads `Config/appsettings.Development.json`, which turns on
  `DevelopmentItemInteractions:GlobalBankAndRecoveryAccess`; the plan turns it
  off again, so Bank and Recovery Storage still need their NPC.
- The host validates its service registrations when it starts.
- The worker logs the warning `Development admin tools are enabled` at startup,
  sends every joining player its admin state, one small reliable message, and
  accepts the quest tools of the admin menu, which the bots never send.
- The telemetry resource names the Development environment when metrics are
  exported.

None of this changes what a run measures beyond the one admin state message
per join.

### Expected Conflicts

A rejection that a correct client can meet when other players act at the same
time is an expected conflict: it stays visible in the report and does not fail
the run. Every other rejection is unexpected and fails the run, and a rejection
nobody can explain is a finding, not a new entry. `PlayingExpectedConflicts` in
the generator holds this table, and the report repeats it:

| Operation | Code | Reason |
| --- | --- | --- |
| `equip` | `item_state_conflict` | The item state changed between the intent and its handling: spent arrows commit in batches without an intent, and a death or a loot moves items too |
| `attack` | `combat_target_dead` | The Mob died, to this bot or another player, after the bot chose it and before the intent arrived |
| `attack` | `combat_target_not_found` | The Mob left the bot's view, by despawning after its death or by walking out of interest, before the intent arrived |
| `attack` | `combat_target_not_attackable` | The Mob began to evade, walking home after it lost its target, before the intent arrived |
| `attack` | `combat_character_dead` | A Mob killed the bot after it decided to attack and before the intent arrived |
| `attack_clear` | `combat_character_dead` | A Mob killed the bot after it decided to stop attacking |
| `loot_open` | `corpse_not_found` | The corpse decayed or was emptied and removed after the bot saw it |
| `loot_open`, `loot_all`, `loot_close` | `combat_character_dead` | A Mob killed the bot during the loot; a death closes every view |
| `loot_all` | `corpse_not_found` | The corpse decayed between the open and the loot |
| `loot_all` | `inventory_full` | The fighter's slots or carry capacity are used up by what it looted; a player meets the same answer until it sells or drops something |
| `loot_close` | `corpse_not_found` | The looted corpse was emptied and removed, or decayed, before the close arrived |
| `gather_open` | `world_actor_unavailable` | Another gatherer took the node's last charge after the bot chose it, and the depleted node waits for its respawn |
| `gather_open` | `world_interaction_target_changed` | The node depleted or respawned, which changes its interaction revision, after the bot saw it |
| `gather_start` | `resource_node_depleted`, `world_actor_unavailable`, `world_interaction_target_changed` | Other gatherers took the node's last charges between the open and the start |
| `gather_start` | `world_interaction_session_invalid` | The worker closed the interaction, as it does once the node depletes, before the start arrived |
| `gather_close` | `world_actor_unavailable`, `world_interaction_target_changed`, `world_interaction_session_invalid` | The node depleted before the close arrived, and the worker closes that interaction itself |
| `vendor_sale`, `vendor_purchase` | `item_state_conflict` | Spent arrows of a bow fighter commit in batches without an intent, between the trade and its handling |
| `item_move` | `item_state_conflict` | The item state changed between the move and its handling, such as by a trade whose change had not reached the bot yet |
| `equip`, `item_move`, `gather_open`, `gather_start`, `vendor_open`, `vendor_sale`, `vendor_purchase` | `combat_character_dead` | A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses item operations and every world interaction but a close from a dead character |

`teleport` and `vendor_close` have no expected conflict, and
`inventory_full` on `loot_all` should stay near zero, because a fighter makes
the trip to the vendor before its room runs out.

The [churn](#churn)'s `leave`, `rejoin_body`, and `rejoin_saved` have none
either. A bot leaves only joined, alive, out of combat, and without an intent
in flight, and the worker accepts the leave of a session that already ended,
with no body time. GameService never refuses the way back to the character's
own session: it places the ticket on the worker that holds the body, without a
capacity check, and once the body is gone it is a normal join from the saved
place. A body that a Mob killed just before the bot came back is no rejection
either: the join enters from the saved place, and the bot judges it as such by
the join's own reconnect flag, which wins over the ticket's.

### Playing Scenarios

The `playing` section of a scenario:

| Property | Meaning |
| --- | --- |
| `roles` | The percent of every role, `fighter`, `gatherer`, `walker`, and `trader`, adding up to 100 |
| `fighterBowPercent` | The percent of the fighters that carry a bow, 0 by default |
| `teleportToGrounds` | Whether every bot is teleported to its home ground once after its join |
| `grounds` | The home grounds: a `name`, a `kind` of `hunting`, `nodes`, `town`, or `walking`, and a rectangle of `minimumX`, `minimumZ`, `maximumX`, and `maximumZ`. A ground of kind `nodes` also names the `kit` its gatherers work its nodes with, and only it does. Fighters need a hunting ground, gatherers a node field, and fighters, gatherers, and traders a town |
| `churn` | Optional: `percentPerMinute`, 1 to 50, the share of the bots that leaves and comes back every minute of the steady interval, as [Churn](#churn) describes |

A run fails before the first bot joins when a ground has no ground under its
center, and a bot fails its teleport when the worker finds no walkable ground
there. A test checks that every kit a shipped scenario hands out is one the
GameService kit fixture grants and that its node fields hold a node for every
gatherer.

### Churn

Real populations change: players log out and come back. With `churn`, a share
of the bots leaves the game every minute of the steady interval and comes back
through the paths a player takes, half of them while their body still stands,
which they take over, and half after it is gone, so they enter from their
saved place.

The schedule comes from the seed alone, by `PlayingChurnSchedule`: the leaves
are spread evenly over the steady interval with a seeded jitter, the bots take
turns in a seeded order, so a bot leaves again only after every other bot did,
the kind alternates, and the account logout alternates per pair of leaves, so
any four aligned leaves hold every combination of kind and logout. No leave is
due in the last 75 seconds of the steady interval, so every bot is back before
it ends; a bot that is still away from its last leave skips the next one, and
the report counts it.

A leave is the leave a player makes:

1. The bot plays its role until it may leave safely: alive, out of combat, no
   Mob after it, no intent in flight, its kit worn and its teleport done, and no
   living aggressive Mob within 12 m, so its body is not attacked in the first
   seconds, in which the check could not tell a hit from lost health. A bot
   that is calm but too close to aggressive Mobs first walks away from the
   nearest one, to a point 32 m from it, at most twice. A bot that finds no
   safe moment within 30 seconds skips the leave, and the report counts it.
2. It stands until its owner state shows no speed on the ground for 0.4
   seconds, records where it stands and its vitals, and sends the leave. The
   operation `leave` measures the leave until its acceptance, which names the
   body's time.
3. Its way back runs apart from the poll threads: the account logout right
   after the leave or none, by the schedule; the wait, 2 to 8 seconds for the
   body kind and the body's time and 4 to 10 seconds more for the saved kind;
   the login after a logout; the character list; the Shards; and the join
   ticket. A new connection with a new world view joins with it, like a client
   that starts again from character select, and reads the full item state.
4. The bot checks what it finds, measures the way back from its first request
   to the join's acceptance as `rejoin_body` or `rejoin_saved`, by the join's
   own reconnect flag, and plays its role on: the role ends what it was in the
   middle of, such as a trip to the vendor, a fight, a loot, or a node, and
   decides again; a bot on its way back to its corpse goes on with that.

The check is what a player would notice, by `PlayingRejoinCheck`:

- A body that the bot takes over stands where it left it, since a body never
  moves, within 5 cm, and has at most the health it can have regenerated since,
  by the combat rules. It may have been hit while it stood, which the bot cannot
  see, so its health has no lower bound.
- A character that enters from its saved place stands where it left within
  5 cm and has the health it had, plus what its body regenerated until it was
  gone. Once the join ticket showed that the body's session ended, the
  generator reads from the disposable database when the session was released,
  the character's latest death, and the saved state. A body that died brings
  the character back at the World spawn with full health. Every hit keeps a
  body its full time after the hit, so a body released more than 1.5 seconds
  after its time was hit, and then only the place and the most health it can
  have regenerated count. The join must also give the bot exactly the saved
  state, and that state must be the one the body's own session wrote.
- The 5 cm cover the rounding of a float and a standing character's ground
  contact; the bot stands still before it leaves, and both the body and the
  saved state keep the simulation's floats.

A mismatch is an unexpected rejection of its operation with the code
`rejoin_place_mismatch`, `rejoin_health_mismatch`, `rejoin_restore_mismatch`,
or `rejoin_state_not_saved`, so `gameplay_unexpected_failures` fails the run,
and the report keeps the evidence of the first 20 under
`playing.rejoinMismatches`. A failed way back, such as a refused ticket or a
join that does not complete, fails the bot.

The counters `churnRequests`, `churnSkippedBusy`, `churnSkippedUnsafe`,
`churnSkippedLate`, `churnRetreats`, `leaves` and the leaves of every role,
`leavesWithLogout`, `rejoinsBody`, `rejoinsSaved`, `rejoinsSavedFoundBody`
for a saved-kind return whose body hits still kept, `rejoinsBodyFoundGone` for
a body-kind return whose body died, `rejoinBodiesHit`, `rejoinBodiesDied`, and
`rejoinChecksFailed` show what the churn did, and the full-stack summary counts
the churn's logouts and logins apart from the run's own.

## World Variant

A scenario with a `worldVariant` runs a load variant of its World, with more
Mobs and resource nodes, while the game content stays untouched.
`StackStressGenerator world-variant --scenario <file> --run-folder <dir>`,
which `tools/Test-Load.ps1` runs before the worker starts, writes it into
`<dir>/world-variant`:

1. It copies the actor definitions and the World's `world.json`,
   `collision.json`, and `actor-spawns.json` from `content/` into
   `world-variant/content`, and adds the scenario's areas and groups to the
   spawn authoring, with the spawn ids `<world>.load_<name>`. A Mob is put on
   the ground by the worker, but a resource node stands at the height of its
   spawn and its collision is baked there. So each node of a group becomes its
   own spawn point, `<world>.load_<name>.node<n>`, at the height of the ground
   under it on the World without the variant's nodes.
2. It runs the real World actor and World collision compilers, their own entry
   points, on that content into `world-variant/generated`, beside a copy of the
   compiled item catalog that the actor compiler reads.
3. It lays the compiled World out as the worker reads it, in
   `world-variant/worker/<world>`, and the worker and the bots use that folder.
4. It checks every added actor: an actor of an area must stand on walkable
   ground that a route from the area's center reaches, as the worker places a
   Mob on the ground under its spawn, and a Mob must be at least 12 m from the
   World spawn; a node of a group is checked on the World without the
   variant's nodes, because a node blocks movement where it stands, and its
   compiled height must be the ground's. A failure names the area and the
   point.

`content/` and `src/Shared/WorldData/Generated` are only read, and a test proves
that both are unchanged after a variant. The variant's actor catalog must equal
the checked-in one, because the worker keeps loading its own.

| Property | Meaning |
| --- | --- |
| `areas` | Rectangles with a `name`, an `actorId`, `minimumX`, `minimumZ`, `maximumX`, `maximumZ`, and a `count` of 1 to 256, placed by the compiler's seeded area spawns |
| `groups` | Grids with a `name`, an `actorId`, `centerX`, `centerZ`, `rows` and `columns` of 1 to 16, and a `spacing` of at least 2 m |

Mobs do not change the collision, so a variant that adds only Mobs keeps the
checked-in collision revision, and a Unity client can join the run and watch
it. Resource nodes block movement with their collision boxes, so a variant with
nodes has another collision revision, and a Unity client refuses it. The script
prints which applies.

## Redis Session-cache A/B Baseline

The A/B method measures what the Redis account-session cache saves. It runs
the same full-stack `lifecycle` profile twice with many session validations
per bot, so session validation dominates: `session-cache-off` sets
`RedisAcceleration__SessionCacheEnabled` to `false` for GameService, and
`session-cache-on` keeps the default, the cache on. Every run recreates the
disposable stack and both keep the distributed Redis authentication rate limit:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Load.ps1 -Scenario session-cache-off
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Load.ps1 -Scenario session-cache-on
```

Compare the `account_session_validate` latency and the PostgreSQL committed
transactions of the two `generator-report.json` files. The cache itself is
described in
[Redis Account-session Acceleration](SERVICE_FEATURES.md#redis-account-session-acceleration).

The current baseline was measured by hand before the scenarios existed, with
100 bots on Development World 2, HTTP concurrency 64, and 50 validation
requests per bot; the scenarios run the stress arena, Development World 1:

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
default based on a single local machine. The tiers work toward the
[Scale Target](PROJECT_ARCHITECTURE.md#scale-target). A run counts toward it
only when it passes the [Quality Boundary](#quality-boundary) with the
workloads that the target names. [Measured Tiers](#measured-tiers) says which
tiers do.

Use worker-only mode first to establish the realtime boundary. Repeat accepted
tiers in full-stack mode to identify when GameService, HTTP admission, or
PostgreSQL becomes the limiting component. Run the highest clean tier for 30 to
60 minutes to detect memory growth, GC pressure, session-heartbeat instability,
database contention, and late disconnects: `soak-30min` does that for the
playing workload at 250 players, with players who come and go, as [Soak](#soak)
describes; change its bot count to the tier.

The generator and services share one machine, so each result includes local
process contention. Use the separate process metrics to reject a run where the
generator becomes the limiting process. Distributed load generation is outside
the current security boundary.

## Soak

A short run shows whether the stack keeps up; a long one shows what slowly goes
wrong: memory that grows, tables that grow without bound, sessions and leases
that drift, and errors that appear after twenty minutes. `soak-30min` is the
soak, `soak-10min` its rehearsal, and both play the reference workload with
[churn](#churn). Every full-stack run reports the numbers below, and a
worker-only run its memory and log classes; the generator keeps sums, extremes,
and histograms for them, never one entry per sample, so a long run does not
grow its own memory with its measurements. The remote update rate, too, adds up
the visits of one remote player instead of keeping one entry per visit.

### Storage Growth

At both ends of the steady interval the generator counts every user table of
the disposable database with an exact `count(*)` and takes its total size with
`pg_total_relation_size`, which holds the table, its indexes, and its TOAST
data; the database of a run holds at most a few hundred thousand rows per
table, so the count stays cheap, and the report says how many milliseconds it
took. It reads the key count and the used memory of Redis with `DBSIZE` and
`INFO memory` at the same moments. `soak.storage` of the report lists every
table with its rows and bytes at both ends and its growth per hour and per
player hour, sorted by bytes per hour, and the summary carries the top ten.

Read the list from the top: a table that grows by bytes per player hour grows
with play and needs a retention or archiving rule; a table whose growth stays
near zero at a steady population, such as `accounts` or `characters`, grows
only with new players. Rows per player hour show which operation writes it, and
bytes per row show whether the payloads or the indexes dominate. A player hour
is one bot playing for an hour; the players are the bots joined when the steady
interval began. No rule judges growth yet: phase 5 of the hardening plan, the
database's retention and indexes, decides what is acceptable from these lists.

### Memory And GC Trend

For the worker, GameService, and the generator, `soak.memory` holds the
least-squares slope of the working set over the steady interval in MiB per
hour and the average of each fifth of the interval. For the worker it adds,
from its own performance file, the slope and fifths of its managed heap, its
generation 2 collections per hour, and its allocation per second, and for the
generator its own managed heap. The rule `process_memory_growth` of the
[Quality Boundary](#quality-boundary) judges the last fifth against the second.

### Memory Limit

`process_memory_growth` compares the last fifth with the second, because caches,
pools, and the JIT warm up in the first. Its limit of 20 MiB comes from two
`soak-10min` rehearsals on the reference machine, 100 bots and 600 s with
churn, on 2026-11-14:

| Process | Fifths of the working set, first and second rehearsal | Second to last fifth | Slope |
| --- | --- | ---: | ---: |
| Worker | 147.4, 148.1, 148.2, 149.8, 152.9 MiB; 148.4, 148.5, 147.7, 148.6, 151.1 MiB | +4.8 and +2.6 MiB | +40 and +18 MiB/h |
| GameService | 207.6, 215.5, 224.0, 234.2, 243.3 MiB; 207.5, 215.9, 226.3, 232.9, 238.6 MiB | +27.8 and +22.8 MiB | +270 and +240 MiB/h |
| Generator | 136.8, 133.0, 140.6, 140.6, 144.8 MiB; 135.5, 132.8, 141.0, 140.6, 142.1 MiB | +11.7 and +9.3 MiB | +68 and +60 MiB/h |

The worker and the generator settle: the generator's managed heap steps up once
in the middle fifth and stays, with 24 generation 2 collections an hour, and a
fifth's average moved by up to 4 MiB in either direction from garbage
collection alone, such as the generator's first to second fifth. GameService
rose through every fifth in both rehearsals. The limit leaves the settled
processes two such moves of room above the generator's 11.7 MiB and flags a
climb like GameService's, so the rule errs on the side of a false alarm over a
missed leak: a ten-minute run that fails it is a reason for the 30-minute soak,
whose fifths lie 18 minutes apart instead of six, to show whether the growth
levels off. In a 30-minute soak the limit catches a steady growth of about 67
MiB per hour, in a ten-minute run of about 200 MiB per hour. GameService runs
the server garbage collector, whose heaps grow with the load they see, and the
soak cannot see its managed heap, so a climb that goes on through a 30-minute
soak is the reason to look at GameService's heap with `dotnet-counters` or a
heap dump before the limit moves.

### Drift Checks

Every report interval the generator checks, and the console shows: the active
simulation sessions against the bots, the sessions within 10 seconds of their
lease's end, one session heartbeat, the least lease left, the age of the
worker's registry heartbeat against GameService's
`Simulation:WorkerHeartbeatTimeoutSeconds`, the pending item operations,
GameService's connections and the ones in use against its pool, the
`Database:MaximumPoolSize` the plan passes, and the oldest transaction that
waits idle.

Every bot says whether it certainly holds a simulation session and whether it
may hold one. A joined bot that plays, also one that waits for a safe moment to
leave, certainly holds one. A bot on its way out of or back into the game, from
its leave to the check after its join, may hold one but not certainly: its body
may still stand, or its new session may already exist. A bot that failed or
never joined holds none. The sessions are compared with both numbers:

- `maximumSessionsWithoutBot`, the most active sessions above the bots that may
  hold one, are sessions that no bot explains, such as the body of a bot that
  failed or a session that was never released.
- `maximumBotsWithoutSession`, the most bots that certainly hold a session above
  the active sessions, are bots whose session was lost.

A churning bot counts on neither side, so both stay 0 in a healthy run, also
while a bot waits out its body's time before it comes back from its saved
place. `soak.drift` keeps the worst of every check over the steady interval:
these two, the most sessions close to expiry, the least lease left, the least
registration headroom, the most pending operations and connections, and the
oldest idle transaction. The existing rules judge where one fits:
`expired_unreleased_sessions` the leases, `deadlocks` the contention, and
`item_invariant_violations` the pending operations at the end.

### Service Log Classes

Once every process stopped, `StackStressGenerator judge` counts the warning,
error, and critical lines of `game-service.log` and `simulation-worker.log`,
and every first line of their `.err.log` files and of the network proxy's
`network-proxy.err.log` as an error, by message class:
the service, the level, the logger category, and the message's first line with
ids, long hexadecimal values, and numbers replaced, cut to 120 characters. The
report keeps the classes under `serviceLogs` and the summary under
`soak.logClasses`. The rule `service_log_errors` fails on every error or
critical line whose class is not on the allow list of the boundary file, which
names the service, level, category, start of the message, and the reason the
class is expected in every run. The list starts empty.

The warning `Invalidated simulation session ... with code session_revoked after
GameService returned 401` is a race of a body's end with a session heartbeat
that was already on its way: the heartbeat service lists the sessions, a body
whose death committed releases its session meanwhile, and GameService answers
the heartbeat of the released session with 401. The worker logs the warning
even when the session had already left its store, so the line changes nothing
then; it is a warning, which no rule judges, and it shows up in runs where
bodies die, more often with churn.

### Deaths Per Role

The report's `playing.deathsPerBotPerTenMinutes` and the summary give the
deaths of every role per bot of the role and ten minutes of the steady
interval, the number the scenarios are tuned to, from the counters of the
steady interval, `playing.steadyCounters`.

### Where To Look For A Deadlock

The `deadlocks` rule names `postgres.log` of the run folder, which the script
keeps before it removes the containers. PostgreSQL reports a deadlock as
`ERROR:  deadlock detected` with both processes and their statements in the
`DETAIL` lines, and the compose file's `log_lock_waits=on` adds a `still waiting
for` line for every lock wait longer than the one-second deadlock timeout, so
the other transaction of a contention shows by its process id even when it
committed. The judge counts both kinds of lines.

## Measurements

Every JSON report contains:

- Run mode, topology, seed, timing, and bot counts.
- Requested, joined, completed, and failed bot totals plus stable failure codes.
- Join and input acknowledgement latency percentiles.
- Sent and received packets and bytes.
- Owner and remote snapshot packet counts plus estimated missing owner
  sequences.
- The remote update rate, how often the bots received the state of the remote
  players they saw during the steady interval, as
  [Remote Update Rate](#remote-update-rate) describes: the average, the fifth
  percentile, and the minimum over bots, the fifth percentile over every pair
  of bot and remote player, and the records of entities without a player spawn.
- The steady interval's start and end.
- Reliable spawn and despawn counts.
- SimulationWorker and generator CPU, working set, private memory, and thread
  summaries, including steady-state memory trend windows, the first and the
  last fifth of the planned steady interval.
- `soak`: the growth of every table and of Redis over the steady interval, the
  worst drift values, and the memory trend of every process, as [Soak](#soak)
  describes.
- After `StackStressGenerator judge`: `serviceLogs`, the service log lines by
  message class, and `postgresLog`, the kept PostgreSQL log with its deadlock
  and lock wait reports.
- `network`, under a network profile: the profile, its description, the seed,
  its round trip and jitter, the allowances and the rules it takes out, and
  after the judge, `proxy`, the proxy's counts of
  [What The Proxy Counts](LOCAL_DEVELOPMENT.md#what-the-proxy-counts).
- `generatorTiming`: how late the latest bot's poll ran against the 3 ms poll
  schedule in every poll round of every poll thread in the steady interval, the
  number of poll threads, how long one step of the process and PostgreSQL
  samplers took, which run apart from the poll threads, and, for the `playing`
  workload, how long its route plans of the steady interval waited for a
  planner thread, which the [validity rules](#validity) judge.

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
  item custody, and pending item-operation counts. The `playing` workload has
  no fixed quantity to expect, because Mobs drop loot and arrows are spent, so
  only the slot, custody, and pending-operation invariants judge it.
- `playing`, for the `playing` workload: the role and kit counts; the counters
  `kills`, `mobDamageTaken`, `botDeaths` and the deaths of every role,
  `corpsesRecovered`, `corpsesLost`, `lootRefusedOwned`, `gatherAttempts`,
  `nodesFoundEmpty`, `nodeFieldsEmpty`, `gatherYields`, the gathering stops by
  depletion and otherwise, `nodesBlockedFromSight`, `vendorTrips`,
  `vendorSales`, `vendorPurchases`, `currencyEarned`, `currencySpent`,
  `vendorTripsWithoutRoom`, `vendorTripsFailed`, `arrowsBought`,
  `arrowsRanOut`, `fighterFlees`, `crowdedMobsSkipped`, `nonFighterFlees`,
  `traderItemMoves`, `routePlans`, `routePlansBusy`, `routePlansWithoutPath`,
  `walksFailed`, `itemStateReloads`, and `rejectionPauses`, all over the whole
  run; the timings `attack_first_hit`, from an attack intent to the bot's first
  hit on its target, `gather_yield_lateness`, how much later than the cycle
  time the worker announced a yield reached the gatherer, which holds the
  GameService commit of the yield, and `route_plan` and `route_queue` of the
  steady interval; and the table of [Expected Conflicts](#expected-conflicts).
  `steadyCounters` holds the same counters over the steady interval alone, and
  `deathsPerBotPerTenMinutes` the deaths of every role per bot of the role and
  ten minutes of the steady interval, which the summary carries too.
  Its gameplay operations are `equip`, `teleport`, `attack`, `attack_clear`,
  `loot_open`, `loot_all`, `loot_close`, `gather_open`, `gather_start`,
  `gather_close`, `vendor_open`, `vendor_sale`, `vendor_purchase`,
  `vendor_close`, and `item_move`, each with its latency from intent to result
  and its result codes.

SimulationWorker logs interval timing for network polling, asynchronous
completion handling, join queue and finalization, simulation ticks and lag,
movement, collision streaming, interest work, snapshot construction and send,
and fixed-tick resynchronization. The same values are published as metrics of
the `Mmorpg.SimulationWorker.Performance` and `Mmorpg.SimulationWorker.Realtime`
meters, listed in [Monitoring](MONITORING.md#simulationworker). Set
`Telemetry__OtlpEndpoint` for GameService and SimulationWorker and start the
[monitoring stack](MONITORING.md) to watch a run live in Grafana; the
generator's own report remains the measurement of record.

With `--worker-performance-file`, the generator also reads the worker's
performance file, which `SimulationWorker:PerformanceReportPath` turns on, as
[Monitoring](MONITORING.md#periodic-and-routine-logs) describes. It waits after
the run until the file holds an interval that ends after the run, at most 75
seconds, and adds a `workerPerformance` section to the report:

- `steadyIntervals`: the worker intervals that lie entirely inside the steady
  interval, and their first start and last end.
- `durations`: every timed phase over those intervals, with its samples, the
  sample-weighted average, the worst interval average, p95, and p99, and the
  longest sample. The p95 and p99 are bucket upper bounds, as in the log.
- `counters`: traffic, drops, deferrals, quota rejections, ticks over budget,
  resynchronizations, allocation, and GC collections, each as its total and its
  largest interval.
- `levels`: players, peers, entities, CPU, working set, backlogs, the managed
  heap, and the worker's Mob numbers, `mobs`, `engagedMobs` that chase,
  attack, or evade, `movingMobs` that moved in the interval's last tick, and
  `mobPathQueue`, the Mob routes waiting for the planning budget, each as its
  average and highest value. The Mob routes planned, `mobPathPlans`, are a
  counter.
- `runIntervals`, `runSnapshotDrops`, and `runQuotaRejections`: the intervals
  that overlap the whole run, ramp and leave included, and their drops and
  quota rejections.
- `runCoverageGap` and `steadyCoverageGap`: empty when the worker intervals
  cover the whole run and follow each other without a gap, else what is
  missing. The worker writes one interval right after the other, so a gap means
  that the worker stopped writing or lost a line. The drop and quota rules need
  the whole run and the tick rules need the steady intervals without a gap; a
  partial measurement fails the rule as not evaluated, never as zero.
- `note`: why a part is missing, such as no interval inside a short steady
  interval.

### Remote Update Rate

The remote update rate measures what a player sees: how often its client
receives the state of each remote player in its interest set. Every remote
snapshot frame carries every player the recipient sees, unless the worker
deferred that player's update under its snapshot budget, so a received record
is one update. Each bot's `SimulationBotSession` counts, for every remote player
from its reliable spawn to its despawn, the snapshot frames that carry its
state, once per snapshot sequence, inside the steady interval, and how long it
saw the player there. A bot's rate is its updates divided by those seconds over
all the players it saw; a duplicated or reordered frame never counts twice, and
records of entities without a player spawn, such as a moving Mob, stay apart as
`untrackedRecords`. The rule judges the fifth percentile over bots, so the
players who see the least decide, not the average.

The earlier figure of about 5.6 Hz at 400 mutually visible players was not a
measurement: it is 15 Hz scaled by 95 percent of the 38 MiB/s aggregate
snapshot budget over the roughly 100 MB/s that full replication would need. The
`worker-only-500` scenario measures it.

## Quality Boundary

A tier is not supported merely because every process remains alive. The limits
are data: `tools/load/quality-boundary.json` names every rule with its limit,
its unit, and its meaning, and is the source of the numbers. The generator
judges a run against it with `--quality-boundary` and adds a `verdict` with one
entry per rule: the rule, the comparison, the limit, the unit, the measured
value, whether it passed, and a note. The file must name every rule that the
generator knows exactly once, with the rule's unit; an unknown rule, a missing
rule, a wrong unit, or an unknown property is an error. A rule that cannot be
evaluated, such as a tick rule without the worker's performance file, fails and
says why in its note; a rule that does not apply, such as a PostgreSQL rule in
worker-only mode, passes and says so.

| Rule | Limit | Why |
| --- | --- | --- |
| `simulation_tick_p99_ms` | p99 <= 33.34 ms in every steady worker interval | A tick must fit the 33.34 ms budget of a 30 Hz tick. The worker's p99 is the upper bound of a histogram bucket, and 33.34 ms is one of its bounds, so the rule passes at p99 <= 33.34 ms |
| `fixed_tick_resynchronizations` | 0 in the steady interval | A resynchronization drops simulation time, which players see as a jump |
| `input_acknowledgement_p99_ms` | p99 <= 150 ms over the run | Click-to-move must answer quickly; much of the time is structural, the wait for the next tick and the next owner snapshot |
| `remote_update_rate_p5_hz` | >= 10 Hz at the fifth percentile over bots | Other players must move smoothly for the players who see the least, not only on average |
| `gameplay_operation_p95_ms` | p95 <= 1,000 ms for every operation kind of the workload | An item or corpse action must answer within a second |
| `bot_failures` | 0 | Every bot joins and leaves cleanly and every account logs out; a disconnect, protocol error, timeout, or admission failure counts |
| `gameplay_unexpected_failures` | 0 | No unexpected item or corpse rejection, timeout, incomplete operation, or failed inventory refresh; expected revision conflicts stay visible in the report |
| `http_unexpected_failures` | 0 | No failed GameService request |
| `quota_rejections` | 0 over the run | Normal input must never hit a UDP quota |
| `snapshot_drops` | 0 over the run | Overload must degrade through deferred remote updates, never through dropped frames |
| `owner_snapshot_gaps` | 0 | A player's own state stays at the full rate |
| `expired_unreleased_sessions` | 0 | Worker registration and session leases remain valid |
| `deadlocks` | 0 | PostgreSQL contention stays bounded |
| `item_invariant_violations` | 0 | Exact item quantities, unique occupied slots, valid custody, and no pending item operation |
| `process_memory_growth` | Last fifth's average working set at most 20 MiB above the second fifth's, for the worker, GameService, and the generator, when the planned steady interval lasts ten minutes or more; not applicable otherwise. The plan decides, so a measured interval that ends a little early cannot take the rule away from `soak-10min` | A leak grows the working set steadily after the warm-up, which the first fifth holds. The limit is set from the `soak-10min` rehearsal, as [Memory Limit](#memory-limit) explains, and errs on the side of catching a leak |
| `service_log_errors` | 0 error and critical lines outside the allow list | An error line is something an operator must look at; the allow list in the boundary file names every class that is expected in every run, with its reason, and starts empty |

### Validity

A run in which the generator itself fell behind measures nothing: its bots sent
inputs late and stood when they meant to walk, whatever the stack did. Nor does
a run whose network proxy sent its packets late. The validity rules judge these
tools, not the stack:

| Rule | Limit | Why |
| --- | --- | --- |
| `generator_poll_lateness_p99_ms` | p99 <= 16.67 ms over the poll rounds of every poll thread in the steady interval | Within half a 30 Hz tick, every bot sends its input in the tick it is due. `playing-smoke` runs on the reference machine measured a p99 between 2.8 and 4.6 ms. Their single rounds of up to about 100 ms came from the process and PostgreSQL samplers, whose step takes 25 to 100 ms and then ran on the poll loop; apart from it, the worst round fell to 14 ms. One poll thread held `playing-100` at a p99 of 6 ms, but 500 playing bots took 86 percent of its core and a p99 of 101 ms, so `playing-500` shares its bots over four poll threads, which measured 13.7 ms |
| `generator_route_queue_p99_ms` | p99 <= 1,000 ms over the route plans of the steady interval | A bot that waits longer than a second for its route stands instead of walking. `playing-smoke` runs measured a p99 below 0.5 ms with 4 planner threads, and `playing-500` 0.3 ms; a workload without routes skips it |
| `network_proxy_lateness_p99_ms` | p99 <= 2 ms over the packets of the whole run in the worse direction, and no packet dropped because the proxy fell behind | Under a network profile, a proxy that sends late measures itself instead of the profile; 2 ms is what its [self-test](LOCAL_DEVELOPMENT.md#delay-accuracy) proves. The judge reads the proxy's counts once it stopped, so the generator's own verdict leaves the rule to it; a run without a profile skips it |

A failed validity rule makes the verdict invalid: the generator exits with 4,
the summary's result is `could not measure`, and `tools/Test-Load.ps1` exits
with 2. The quality rules keep their own result in the verdict, for reading,
but judge nothing.

Every other number, such as join and ticket latency, CPU, memory, and
bandwidth, is reported and compared with the baseline, not judged. Watch the
worker, GameService, and generator working sets for growth after warmup, which
a soak judges.

Record the first boundary that fails and the phase whose duration grows with
load. Optimize only after a repeatable run identifies the limiting phase, then
rerun the same seed, mode, and tier for comparison. The
[Realtime Performance Contract](PROJECT_ARCHITECTURE.md#realtime-performance-contract)
requires every new high-frequency gameplay feature to add or extend a workload
before a higher tier is accepted. Before the full-stack load targets are raised,
measure what the item-state lock and the session heartbeats cost in extra
GameService round trips with a full-stack run.

## Baselines

Every run writes a small `summary.json` beside its full report: the scenario,
the commit and whether the tree had changes, the date, the machine (CPU model,
logical processors, memory, operating system, and .NET version), the mode,
workload, bots, and steady seconds, the network profile with the proxy's counts
or none, the verdict entries, the result, and
`keyNumbers`: join p95, input acknowledgement p50, p95, and p99, the remote
update rate average and fifth percentile, the p95 of every gameplay operation
kind, the worst steady tick p99, the worker's average single-core CPU and
highest working set from its own intervals, the generator's average CPU, and
the payload bytes per bot and second that the worker received and sent.

`-UpdateBaseline` copies the summary to `tools/load/baselines/<scenario>.json`,
or under a network profile to `<scenario>.<profile>.json`, such as
`playing-100.normal.json`, which is checked in. A run under a profile never
replaces or compares with the baseline of the direct connection, and the other
way round. Without it, the script prints the difference of every key number to
the checked-in baseline of the scenario and the profile. The difference is informative and never
part of the exit code, because machines and their load differ. Record a
baseline only from a run on an idle machine, and name what else ran in the
pull request that checks it in. [Measured Tiers](#measured-tiers) lists the
baselines that are checked in.

## Measured Tiers

These are the checked-in baselines in `tools/load/baselines`, recorded on
2026-11-14 on an AMD Ryzen 7 5800 with 16 logical processors and 31.9 GiB of
memory under Windows, with the generator, the worker, GameService, PostgreSQL,
and Redis on that one machine. The machine was otherwise idle except for about
twenty containers of another project that used about 15 percent of one core.
The worker ran 30 simulation ticks and 15 snapshots per second. Each baseline
file names its commit.

| Scenario | Result | Tick p99 | Input ack p50 / p99 | Updates per second seen, p5 | Slowest operation p95 | Worker CPU, one core | Sent per player |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `smoke` | passed | 4 ms | 38 / 79 ms | 15.0 | partial loot 430 ms | 21% | 10 KiB/s |
| `worker-only-200` | passed | 8 ms | 55 / 96 ms | 15.0 | none | 47% | 126 KiB/s |
| `worker-only-500` | failed | 16 ms | 63 / 115 ms | **3.5** | none | 87% | 74 KiB/s |
| `mixed-100` | passed | 4 ms | 45 / 82 ms | 15.0 | corpse deposit 416 ms | 28% | 67 KiB/s |
| `hotspot-100` | passed | 4 ms | 42 / 79 ms | 15.0 | vendor sale 95 ms | 20% | 62 KiB/s |
| `playing-100` | passed | 8 ms | 45 / 98 ms | 15.0 | equip 222 ms | 21% | 27 KiB/s |
| `playing-250` | passed | 24 ms | 56 / 111 ms | 15.0 | equip 218 ms | 66% | 64 KiB/s |
| `playing-500` | failed | **50 ms** | 72 / 133 ms | **6.6** | equip 266 ms | 144% | 77 KiB/s |
| `soak-30min` | failed | 24 ms | 54 / 110 ms | 15.0 | rejoin 470 ms | 64% | 60 KiB/s |

The tick p99 is a bucket upper bound, and bold numbers fail their rule.
`playing-500` also had two PostgreSQL deadlocks and `soak-30min` one, which is
its only failed rule. `mixed-250` has no baseline: see
[What Limits The Next Tier](#what-limits-the-next-tier).

What the tiers say about the [Scale Target](PROJECT_ARCHITECTURE.md#scale-target):

- **250 playing players pass a five-minute run** on this machine with every
  rule, at two thirds of a core for the worker. 100 players in one place pass
  too.
- **No tier has passed a 30-minute soak yet.** At 250 players with churn the
  soak holds every rule for the tick, the update rate, the operations, the
  memory, and the logs, and fails on one deadlock that the worker's retry
  recovered.
- **500 playing players fail** the tick budget and the update rate, and the
  worker needs more than one core, which its one loop thread does not have.

In `playing-100` to `playing-500` every gameplay operation succeeded: no
unexpected rejection and no timeout in 280, 718, and 1,626 kills, their loots,
the gathering, and the vendor trades, and 0.5 to 0.75 deaths per fighter and
ten minutes.

### The 30 Minute Soak

`soak-30min` ran 250 playing bots for 1,800 seconds with ten percent of them
leaving and returning per minute.

- **Churn**: 714 leaves, half of them with an account logout, 360 returns to a
  standing body and 354 from the saved place. Every return kept its place and
  health; five bodies died while their players were away and came back at the
  spawn, as designed.
- **Memory**: the working set levels off. Between the second and the last fifth
  of the run the worker grew 0.8 MiB, to 173 MiB, and GameService 3.3 MiB, to
  247 MiB. The worker's managed heap stayed at 64 MiB with 34 generation 2
  collections per hour and 32 MiB allocated per second. The generator itself
  grew 13 MiB.
- **Drift**: no session without a bot, no playing bot without a session, no
  session within ten seconds of its lease expiry, no pending item operation,
  at most 6 of 64 pooled connections in use.
- **Storage**: the database grew 184 MiB per hour, about 750 KiB per player
  and hour.

| Table | Rows per hour | Growth per hour | Per player and hour |
| --- | ---: | ---: | ---: |
| `item_operation_changes` | 102,000 | 65 MiB | 267 KiB |
| `item_operations` | 47,000 | 107 MiB | 439 KiB |
| `item_container_slots` | 17,000 | 3.2 MiB | 13 KiB |
| `live_mob_loot_grants` | 10,000 | 2.2 MiB | 9 KiB |
| `character_currency_ledger` | 7,300 | 1.9 MiB | 8 KiB |

The two operation tables are 94 percent of the growth. `playing-500` grew 326
MiB per hour, which is about 8 GiB per day and Shard at the Scale Target.

- **Logs**: no error or critical line. The warnings were ten
  `session_revoked` lines of the session heartbeat, which a heartbeat on its
  way logs when it meets the release of the same session and which change
  nothing, and the one death that the deadlock delayed.

## Load Runs Under A Network Profile

Every tier above ran on loopback. The game is played over the internet, so any
scenario also runs through the network impairment proxy with a network profile,
which delays, jitters, drops, duplicates, and cuts off the packets between the
bots and the worker, as
[Run On A Bad Network](LOCAL_DEVELOPMENT.md#run-on-a-bad-network) describes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Load.ps1 -Scenario playing-100 -DurationSeconds 180 -Network poor
```

A scenario may name its profile with `network`, and `-Network` overrides it,
`none` included. No scenario file exists per profile: `playing-100` under each
profile is the measurement. The run folder keeps the proxy's counts, the report
and the summary name the profile and carry the counts, and the script prints
them as a table with one row per direction. A baseline is per scenario and
profile, as [Baselines](#baselines) describes.

### What A Profile Changes In The Verdict

The quality boundary was decided for loopback. A profile's `verdict` section,
data like the boundary, says what changes under it; every rule it does not name
stays as it is, and its notes say what changed:

- An allowance raises a maximum rule's limit. Every profile raises
  `input_acknowledgement_p99_ms` by its round trip plus its jitter, 35, 95, 240,
  and 500 ms, since an input and its owner snapshot each cross the network once.
- A rule that the profile's impairment decides by itself passes as not
  applicable, with the reason and the measured value in its note. Every profile
  that loses packets takes out `owner_snapshot_gaps`: owner snapshots travel
  unreliably, so a lost one is a gap by design, and `snapshot_drops` still
  judges whether the worker sent them all. `terrible` also takes out
  `remote_update_rate_p5_hz`, because its outages are the test.
- The validity rules judge the tools and stay; a profile may not name them.
  `network_proxy_lateness_p99_ms` adds the proxy, as [Validity](#validity)
  describes.

The load runner refuses a profile whose verdict names an unknown rule or a
validity rule, or gives a minimum rule an allowance.

### What The Bots Tolerate

- **A reliable message that arrives late.** A bot keeps one intent in flight
  and waits for its result up to the gameplay operation timeout of 30 seconds,
  far longer than any profile delays a message, an outage and its resends
  included, so a late result only lengthens the operation's latency. The
  roles' own waits, such as 6 seconds for a corpse to show or 5 seconds for a
  gathering to start, end in a decision, such as giving the corpse up, which
  their counters show, never in a failure.
- **A duplicate.** LiteNetLib drops a duplicated reliable or sequenced packet,
  but an unreliable snapshot can arrive twice, or after a newer one when the
  jitter is wider than the 66 ms between two snapshots, as under `terrible`.
  The session takes an owner snapshot only when its sequence is newer, the
  remote update rate counts every frame once, and the world view keeps each
  remote player and Mob at the position of the newest snapshot that carried
  it.
- **An outage of 3 seconds.** The bots' and the worker's LiteNetLib connections
  time a silent peer out after 10 seconds, so the connection stays. The inputs
  of the outage stay unacknowledged, about 90 of the 256 the session tracks,
  and each batch repeats only the newest four, so the worker steps three ticks
  ahead, pauses the character, and skips the gap when the inputs come back. The
  acknowledgement of those inputs measures the outage, and a walk whose
  deadline, twice its walking time plus 3 seconds, runs out counts in
  `walksFailed` before the role decides again.

Under every profile the bots met no unexpected rejection and no operation
timeout; the expected conflicts rose a little with the delay.

### Measured Under The Profiles

`playing-100` with `-DurationSeconds 180` on 2026-11-14 on the reference
machine of [Measured Tiers](#measured-tiers), directly and under each profile;
the CI runner of the same machine may have run jobs at the same time. The
proxy used about one core in every run. A first `normal` run, with a spin of
1 ms that kept two cores busy, passed every rule with the same latencies and a
tick p99 of 16 ms.

| | Direct | `normal` | `poor` | `terrible` |
| --- | --- | --- | --- | --- |
| Result | passed | failed | failed | failed |
| Failed rules | none | `bot_failures` | `gameplay_operation_p95_ms`, `bot_failures`, `deadlocks` | `input_acknowledgement_p99_ms`, `gameplay_operation_p95_ms`, `bot_failures` |
| Input acknowledgement p50 / p99 | 46 / 101 ms | 128 / 181 ms | 249 / 377 ms | 457 / 3,029 ms |
| Operations: p50 range; slowest p95 | 14 to 84 ms; equip 248 ms | 93 to 169 ms; loot close 425 ms | 204 to 301 ms; loot close 1,609 ms | 462 to 1,299 ms; item move 7,379 ms |
| Remote updates per second, p5 | 15.0 | 14.8 | 14.1 | 11.9 |
| Bots that failed | none | 2 `connection_closed` | 25 `connection_closed` | 58 `connection_closed` |
| Join p95 | 293 ms | 468 ms | 947 ms | 1,361 ms |
| First hit after an attack, p95 | 1.1 s | 1.2 s | 1.7 s | 3.1 s |
| Kills, bot deaths | 171, 24 | 174, 18 | 140, 24 | 102, 38 |
| Worker tick p99, CPU of one core | 8 ms, 22 % | 8 ms, 25 % | 16 ms, 25 % | 16 ms, 26 % |
| Worker payload in / out per player | 0.89 / 28.8 KiB/s | 1.62 / 28.2 KiB/s | 1.58 / 29.8 KiB/s | 1.12 / 32.5 KiB/s |
| Dropped by the proxy, to the worker / to the bots | none | 1.0 / 1.0 % | 5.0 / 5.0 % | 12.9 / 13.8 % |
| Proxy lateness p99, its CPU of one core | none | 0.54 ms, 98 % | 0.52 ms, 99 % | 0.52 ms, 99 % |

What the profiles show:

- **`normal` holds every rule but the leave.** Every reliable operation answers
  about one round trip later than directly, with its p95 below 430 ms, and the
  input acknowledgement p99 rises by 80 ms, within its allowance of 95 ms; one
  of the two runs lost 2 leaves, as the last point but one describes.
- **The worker receives about 80 percent more payload per player under
  `normal` and `poor`.** A movement batch repeats every unacknowledged input up
  to four, and with a longer round trip more of them are unacknowledged when a
  batch leaves. It sends up to 13 percent more payload under `terrible`, which
  these runs do not explain.
- **Reliable operations slow down with loss far beyond the round trip.** Under
  `poor` the operations' p50 is about one round trip, 220 to 300 ms, but their
  p95 is 0.4 to 1.6 seconds, so `gameplay_operation_p95_ms` fails; under
  `terrible` the p50 is about 1.1 seconds and the p95 2 to 7 seconds. The
  likely cause, not yet proven: every control message shares the one reliable
  ordered channel, so one lost packet holds back every later message until
  LiteNetLib resends it, and under `terrible` only the operations at the start
  of a session, the equip and the teleport, before combat traffic fills the
  channel, keep a p50 near one round trip.
- **A graceful leave fails as a lost connection on a lossy network.** 2 of 100
  bots in one of two `normal` runs, 25 under `poor`, and 58 under `terrible`
  failed with `connection_closed` at their leave after the steady interval,
  and none before it: the worker disconnects a peer 250 ms after it sent
  `LeaveAccepted` (`RealtimePeerContext.GracefulDisconnectDelayMilliseconds`),
  and when that reliable message is lost or held back behind a lost one, the
  disconnect arrives first. The Unity client's `RealtimeSession.Leave` fails
  the same way but names it `connection_lost`, `The realtime connection
  closed: RemoteConnectionClose.`, because the disconnect sets the operation's
  error first;
  [The Client On A Bad Network](LOCAL_DEVELOPMENT.md#the-client-on-a-bad-network)
  measures how often. The same 250 ms follow every `ServerDisconnect` and
  protocol rejection, whose reason can be lost the same way. This is a finding
  about the worker, not the bots; it is not fixed here.
- **An outage costs the inputs it spans.** Under `terrible` the input
  acknowledgement p99 is 3 seconds, the outage itself, so the rule fails as
  the profile predicts; the remote update rate, which the profile takes out,
  stays at 11.9 per second at the fifth percentile.

## What Limits The Next Tier

Each entry is a measured limit or defect with its evidence. None of them is
tuned away in the load tooling; the run folders of 2026-11-14 hold the
reports.

1. **The worker's one loop thread.** At 500 playing players the worker uses 144
   percent of a core (LiteNetLib's threads included), its tick p99 leaves the
   33.34 ms budget, and the update rate of the players who see the least falls
   to 6.6 per second, although the players are spread over eight hunting
   grounds, six node fields, and a town. At 250 it uses 66 percent and holds
   15 updates per second.
2. **Bandwidth per player.** The worker sends 60 to 77 KiB per second to each
   of 250 to 500 spread players, about half a megabit, and 126 KiB per second
   when 200 stand in one place. With 500 in one place the aggregate snapshot
   budget of 38 MiB per second caps the rate at 3.5 updates per second
   (`worker-only-500`). Entity state is sent unquantized and in full.
3. **A deadlock between a player death and the worker's registry heartbeat.**
   The death's transaction holds the Shard's row in `simulation_assignments`
   and inserts into `corpses`, whose foreign key needs a key-share lock on the
   Shard's row in `shards`. The registry heartbeat holds that row and then asks
   for the assignment row with `for update`. PostgreSQL aborts the death after
   its one-second deadlock timeout, and the worker's retry commits it. Two in
   five minutes at 500 players, one in thirty minutes at 250; both statements
   are in each run's `postgres.log`.
4. **A registration or login burst starves everything else in GameService.**
   Password hashing takes seven to ten of sixteen cores while 32 to 64
   accounts register and log in at once. In `mixed-250`, which provisions its
   accounts 64 at a time during the ramp, the worker's calls to GameService then time out after five
   seconds: joins are refused with `auth_service_timeout`, two registry
   heartbeats in a row fail, the worker's registration lease expires, and the
   worker stops itself. The scenario did not complete on the idle machine, so
   it has no baseline. The playing scenarios provision eight accounts at a
   time before the ramp and are not affected.
5. **A logout burst overruns the Redis session cache.** When the 250 bots of
   `mixed-250` logged out together, six logouts failed with
   `session_cache_invalidation_unavailable`: GameService could not store the
   revocation tombstones in time and, correctly, refused to commit the
   revocation.
6. **One corpse is one queue.** With 250 clients on the shared corpse of
   `mixed-250`, a deposit's p95 was 4.6 seconds. That is the deliberate worst
   case of the mixed workload, not a spread of corpses.
7. **Storage growth**, as [The 30 Minute Soak](#the-30-minute-soak) shows: the
   two operation tables are never trimmed.
8. **The generator needs several poll threads** for 200 and more bots. With one
   thread `worker-only-500` and `mixed-250` could not be measured, and the
   input acknowledgement p99 that earlier runs reported at 500 bots, 140 to 170
   ms, was partly the generator's own lateness: with four threads it is 115 ms.

Smaller things the bots met are in
[What A Playing Bot Sees And Sends](#what-a-playing-bot-sees-and-sends) and
[Churn](#churn).

## Scope Boundary

Full-stack mode measures account registration, character creation, login, shard
discovery, placement, tickets, simulation-session leases, UDP gameplay traffic,
leave, and logout. Explicit gameplay workloads also measure durable inventory
relocation and shared corpse deposit and loot through the real worker and
GameService transaction path.

The `playing` workload adds combat with Mobs, Mob loot, deaths and corpse
recovery, equipment, the admin teleport, walks over a World, gathering at
resource nodes, and vendor sales and purchases with item moves. Insurance
claims, quests and their item policies, Bank, Recovery Storage, and Bag nesting
are not generated yet. Add those as explicit workload profiles or roles so the
stable lifecycle baseline does not silently change.

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
| `--workload` | `lifecycle` | `lifecycle`, `inventory`, `loot-hotspot`, `mixed-gameplay`, or `playing`; every workload except `lifecycle` needs `full-stack` |
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
| `--quality-boundary` | none | The limits file to judge the run against; adds the `verdict` |
| `--worker-performance-file` | none | The worker's `SimulationWorker:PerformanceReportPath` file to fold into `workerPerformance` |
| `--summary-output` | none | Also writes the summary of [Baselines](#baselines) |
| `--scenario-name`, `--source-commit`, `--source-tree-dirty` | none | The scenario, commit, and `true` or `false` for the summary |
| `--authority-hold-seconds` | `0` | Worker-only: how long the in-memory authority stays up at most after the report, until `Ctrl+C` |
| `--scenario-file` | none | `playing`: the load scenario whose `playing` section the bots play; the workload needs it |
| `--world-data-path` | `WorldData/Worlds` below the generator's output | `playing`: the World data folder, laid out as the worker's, whose collision the bots plan on; the build copies the checked-in collision there |
| `--item-catalog-path` | `WorldData/Items/core.item-catalog.json` below the output | `playing`: the compiled item catalog the bots read like a client |
| `--actor-catalog-path` | `WorldData/Actors/core.actor-catalog.json` below the output | `playing`: the compiled actor catalog the bots read like a client |
| `--poll-threads` | `1` | The threads that share the bots and poll each of them every 3 ms, 1 to 16; a scenario may set it |
| `--combat-rules-path` | `WorldData/Combat/combat-rules.json` below the output | `playing`: the combat rules the worker loads, whose regeneration the churn's rejoin check uses; the build copies `content/combat/combat-rules.json` there |
| `--redis-endpoint` | none | Full-stack: the loopback `host:port` of the disposable Redis, whose keys and used memory the soak reads |
| `--database-pool-size` | none | Full-stack: GameService's `Database:MaximumPoolSize`, which the drift checks compare its connections with |
| `--worker-heartbeat-timeout-seconds` | none | Full-stack: GameService's `Simulation:WorkerHeartbeatTimeoutSeconds`, against which the drift checks give the worker's registration headroom |
| `--network-profile` | none | The network profile file the bots' traffic goes through, by the proxy that `tools/Test-Load.ps1` starts; the report names it and the verdict applies its allowances |

`STACK_STRESS_FIXTURE_SECRET` supplies the fixture secret of the gameplay
workloads; it is intentionally not accepted as a command-line argument. The
same seed and bot indices reproduce movement patterns, while tickets, account
session tokens, and generated passwords stay random for every run.

Exit codes: 0 when every requested bot joined and left cleanly, every admitted
account logged out, no gameplay operation failed unexpectedly, the item
invariants held, and the run passed the quality boundary when one was given; 1
for any other result or failure of those own checks; 2 for an invalid option; 3
when the run passed its own checks but failed the quality boundary; 4 when a
[validity rule](#validity) failed, so the run could not measure, whatever the
other checks say; 130 when the run was cancelled with Ctrl+C.

`StackStressGenerator plan --scenario <file> --run-folder <folder>` prints the
plan of a scenario as JSON for `tools/Test-Load.ps1` and runs nothing. It takes
`--bots`, `--duration-seconds`, `--quality-boundary`, `--source-commit`,
`--source-tree-dirty`, and `--network <profile or none>`, validates the scenario, the generator arguments it
results in, and the quality boundary file, and exits with 2 and the reason when
one is invalid.

`StackStressGenerator judge --run-folder <folder> [--quality-boundary <file>]`
reads `generator-report.json` and `summary.json` of a run whose services
stopped, adds the [service log classes](#service-log-classes), the kept
`postgres.log`, and under a network profile the proxy's `network-proxy.json`,
judges the run again, and rewrites both files. It exits with 0
when the run passed, 1 when it failed, 4 when it could not measure, and 2 when
the run cannot be judged; `tools/Test-Load.ps1` runs it after every run.

`StackStressGenerator world-variant --scenario <file> --run-folder <folder>`
writes the scenario's [World Variant](#world-variant) into
`<folder>/world-variant`, with the compilers' output in
`<folder>/world-variant.log`, and prints whether it keeps the checked-in
collision revision. It runs from the repository root, refuses a folder that is
not empty, and exits with 2 and the reason when the scenario has no variant or
an added actor has no walkable ground.

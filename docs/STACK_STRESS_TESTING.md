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
| `loot-hotspot` | `full-stack` | Yes | Stand at the shared corpse | Contend on one player's corpse with deposit and partial loot |
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
one-time ticket consumption, the session heartbeat, which it answers per
session as GameService does, the session release, and empty durable-corpse
restoration. Its bots are synthetic, so the worker sends
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
  15 Hz, while its remote records share the rest of its byte budget, nearest
  first, so remote cadence drops before owner cadence does, and far before
  near. The worker counts the remote work it put off in
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
It does not write inventory rows directly. After the run the generator checks the
fixture quantities through its separate read-only PostgreSQL sampler and runs
GameService's [economy audit](GAME_SERVICE_MODULES.md#economy-audit) over the
whole database: the same checks that GameService runs, which the generator calls
through `CharactersAudit.Checks` and `ItemsAudit.Checks` instead of copying their
queries, with no operation allowed to be pending.

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
| `outage-request.json`, `outage-window.json`, `game-service-2.log` | A [GameService outage](#gameservice-outage-scenario): the generator's request, the times the script measured, and the output of the restarted GameService, with its `.err.log` |

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
| `outage` | A [GameService outage](#gameservice-outage-scenario): `atSeconds` into the steady interval and `seconds` long; full-stack `playing` without churn only |

| Scenario | Mode and workload | Shape |
| --- | --- | --- |
| `smoke` | full-stack `mixed-gameplay` | 10 bots in one step, 40 s steady; a tooling check |
| `worker-only-200` | worker-only `lifecycle` | 200 bots in steps of 50 five seconds apart, 60 s steady, 2 poll threads |
| `worker-only-500` | worker-only `lifecycle` | 500 bots in steps of 100 five seconds apart, 60 s steady, 4 poll threads |
| `mixed-100` | full-stack `mixed-gameplay` | 100 bots, 32-way admission, steps of 25 ten seconds apart, 120 s steady |
| `mixed-250` | full-stack `mixed-gameplay` | 250 bots, 64-way admission, steps of 50 five seconds apart, 60 s steady, 2 poll threads; it completes and fails the operation time of its one shared corpse, as [What Limits The Next Tier](#what-limits-the-next-tier) describes |
| `session-cache-on`, `session-cache-off` | full-stack `lifecycle` | The [Redis Session-cache A/B Baseline](#redis-session-cache-ab-baseline) |
| `playing-smoke` | full-stack `playing` | 20 bots on Development World 2 in all four roles: 8 fighters, 2 of them with bows, in two hunting grounds, 4 gatherers at 8 Iron Veins, 6 walkers, and 2 traders; a variant with 10 Feral Wolves, 10 Grey Wolves, and 8 Iron Veins more; steps of 10 five seconds apart, 90 s steady; a tooling check of the playing workload |
| `playing-100` | full-stack `playing` | 100 bots of the reference mix on the reference variant, steps of 10 six seconds apart, 300 s steady |
| `playing-250` | full-stack `playing` | 250 bots of the reference mix on the reference variant, steps of 25 six seconds apart, 300 s steady, 2 poll threads |
| `playing-500` | full-stack `playing` | 500 bots of the reference mix on the reference variant, steps of 25 five seconds apart, 300 s steady, 4 poll threads; the Scale Target |
| `hotspot-100` | full-stack `playing` | 100 traders in the town of Development World 2 at its one vendor, steps of 20 five seconds apart, 120 s steady |
| `soak-10min` | full-stack `playing` | The rehearsal of the soak: 100 bots of the reference mix on the reference variant with [churn](#churn) of 10 percent a minute, steps of 10 six seconds apart, 600 s steady; long enough for the [memory rule](#quality-boundary), whose limit it set, and for the [retention rules](#storage-growth) |
| `soak-30min` | full-stack `playing` | The [Soak](#soak): 250 bots of the reference mix on the reference variant with churn of 10 percent a minute, steps of 25 six seconds apart, 1,800 s steady, 2 poll threads |
| `soak-500-30min` | full-stack `playing` | The soak at the Scale Target: the 500 bots, variant, ramp, and 4 poll threads of `playing-500` with churn of 10 percent a minute, 1,800 s steady |
| `login-burst` | full-stack `inventory` | 20 bots that move items in their inventories, 90 s steady, and a [login burst](#burst-scenarios) of 400 logged-in accounts 10 s into it, each with 60 s to end logged in again |
| `logout-burst` | full-stack `inventory` | 20 bots that move items in their inventories, 90 s steady, and a [logout burst](#burst-scenarios) of 500 logged-in accounts 10 s into it, each with 60 s to end logged out |
| `outage-60s` | full-stack `playing` | `playing-100` with a [GameService outage](#gameservice-outage-scenario): 60 s into the steady interval GameService stops, and it starts again 60 s after the stop |

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

### Burst Scenarios

A burst measures what many players who log in or out at the same moment do to
GameService, without the World: the generator option `login-burst-accounts`
or `logout-burst-accounts` names how many accounts take part,
`burst-at-seconds` when in the steady interval the burst starts, 10 by
default, and `burst-deadline-seconds` how long every account has to end logged
in or out, 60 by default; the burst and its deadline must fit the steady
interval. A run has at most one burst, and only in full-stack mode.

1. Before the ramp, the generator registers the burst's accounts, eight at a
   time like the provisioning of the playing workload, so every one of them is
   logged in with its registration session.
2. At its time, the burst's requests all wait at one gate and leave together;
   the report says within how many milliseconds the first requests left. A
   login burst logs every account in again, so each login replaces a session
   and stores its revocation tombstone in Redis; a logout burst logs every
   account out, each with its own tombstone.
3. A login that GameService refuses with `auth_busy`, because its
   [password hashing lane](SERVICE_FEATURES.md#password-hashing-lane) is
   full, waits for its `Retry-After` and is sent again, as every registration
   and login of the generator does; a refusal is counted and expected, never a
   failure. The time of an account runs from the burst's start to its success,
   so it holds its waits.
4. When the run ends, every burst account that still holds a session logs out,
   and the account count of the disposable database includes the burst's
   accounts.

The bots of the scenario play meanwhile, so the worker's registry heartbeats
and the bots' operations are measured during the burst. The rule
`burst_incomplete_accounts` judges the burst, `worker_heartbeat_failures` and
`auth_service_timeouts` the worker's calls to GameService, and
`http_unexpected_failures` every request, so a `session_cache_invalidation_unavailable`
fails the run. The report's `fullStack.burst` holds the accounts that completed
and those within the deadline, the refusals and retries and how many accounts
were refused, the median and longest seconds, the failure codes, and the
accounts that logged out at the end; the summary carries it as `burst` and the
key numbers `authBusyRefusals`, `burstRefusals`, `burstMedianSeconds`, and
`burstLongestSeconds`.

`login-burst` expects the lane to refuse part of its 400 logins: with eight
lane threads and a hash of a quarter to half a second, the queue holds 160 to
320 logins within its 10 seconds. It has 400 and not more accounts because
all bots share one address, and their logins with every retry stay below the
authentication limit of 1,000 per minute that the plan sets; with 500 accounts
the retries passed it, and the logins that waited out the limit missed their
deadline.

### GameService Outage Scenario

`outage-60s` measures what players meet when GameService goes away for a minute
while the worker plays on, as
[GameService Outage](SERVICE_FEATURES.md#gameservice-outage) describes. A
scenario's `outage` section names when and for how long; the generator options
`outage-at-seconds`, `outage-seconds`, and `outage-folder` carry it, and the
plan owns them. An outage needs the full-stack `playing` workload without
churn, whose way back needs GameService, lasts 10 to 100 seconds, below the
worker's outage tolerance of 120 seconds, and leaves a minute of the steady
interval after it.

1. `atSeconds` into the steady interval the generator writes
   `outage-request.json` into the run folder.
2. The script sees it, stops GameService with `Ctrl+C`, and writes the time it
   sent the `Ctrl+C` and the time the process ended into `outage-window.json`.
   That stop is a normal shutdown, which waits for the requests it is serving.
3. `seconds` after the stop the script starts GameService again with the same
   settings, its output in `game-service-2.log`, and writes the time it started
   it and the time `/health/ready` answered. The outage window runs from the
   stop to that readiness. A restart that is not ready within
   `-ReadyTimeoutSeconds` ends the run, which then could not run.
4. When the run ends, the generator reads the window. Both services write the
   UTC time of every log entry in a run with an outage, so the judge can tell
   the outage's own log lines from the others.

The bots read `GameServiceAvailabilityChanged` and count both kinds of notice.
Inside the outage what the outage explains is expected and counted, not a
failure: a refusal with `game_service_unavailable` between a bot's two notices
or inside the window, a failure with `auth_service_timeout` or
`auth_service_unavailable` of a call that was on its way when GameService
stopped, received inside the window, a failed HTTP read of a bot's items inside
the window, and every failed request inside it. The same answers outside the
window count for `gameplay_unexpected_failures` and `http_unexpected_failures`
and the generator's own checks, and the report's `outage` names them by
operation and code. The operations' latencies, which `gameplay_operation_p95_ms`
judges, leave out the answers that the outage explains; the report keeps their
times as `outage.expectedAnswerLatency`, since a call that GameService stopped
answering takes about two seconds on Windows before the worker refuses it. The worker's heartbeat warnings, its timed out calls, and
the error lines that the outage causes are allowed only inside the window, from
the stop to the latest return that `outage_return_seconds` accepts, by allow
list entries with `duringOutage`, as [Service Log Classes](#service-log-classes)
describes; the same lines outside it count as in any run.

The outage's own verdict, each rule not applicable to a run without an outage
and failed when the script reported no window:

- `outage_notice_mismatches`: every bot that was joined across the outage, a
  completed bot that joined before the stop, heard exactly one notice that
  GameService does not answer and one that it answers again.
- `outage_kill_corpses_missing`: a bot that killed a Mob while its saving was
  paused saw that Mob's corpse within 10 seconds: a corpse of the Mob's kind
  near the place, that the bot first saw after the kill and that stands for no
  other kill. The bots have no quests, so a
  kill makes no quest credit, and the wolves' live corpses need GameService
  only for quest drops; the report counts the kills and their corpses.
- `outage_return_seconds`: the worker logged that GameService answers again at
  most 5 seconds after GameService was ready; the note also says how long
  GameService was down and how long after the stop the worker logged that it
  does not answer.
- `outage_durable_recovery_seconds`: the first durable action of a bot, an
  equip, item move, loot, trade, or gathering yield, committed at most 15
  seconds after GameService was ready. One that committed before the script's
  readiness poll saw GameService counts as zero seconds.

The item invariant holds at the end as in every full-stack run. The report and
the summary carry the outage as `outage`, and the script prints it after the
verdict.

The scenario found a defect on its first runs: a client operation that reached
the worker in the seconds before it noticed the outage waited in the player's
lane behind durable work, such as a bow fighter's arrow batch, that keeps the
lane until GameService returns, and was answered a minute late. The worker now
refuses every durable client operation that waits in a lane at the moment
GameService stops answering. The run of 2026-11-16 on the idle machine of
[Measured Tiers](#measured-tiers) passed every rule with 100 of 100 bots:
GameService was down 62.0 seconds, the worker noticed the outage 4.7 seconds
after the stop and the return 0.04 seconds after the readiness, 297 refusals,
7 calls that the stop cut off, and 4 failed item reads were expected, none fell
outside the window, the 49 kills while saving was paused all left their
corpses, and the first durable action committed 1.5 seconds after the return.
The worker logged 28 failed registry heartbeats and its one notice of the
outage, all inside the window. GameService's process numbers cover only the
process before the stop, because the sampler follows one process.

A variant whose outage outlasts the worker's tolerance, which must end with
every bot disconnected with `game_service_outage` and the worker stopped in
order, is not a scenario: the script treats a worker that ends during the run
as a failed run, and every disconnected bot is a bot failure, so it needs its
own verdict, not a parameter.

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
| `soak-500-30min` | The same at the Scale Target's 500 players | The Scale Target is claimed only by a tier whose 30-minute soak passes |

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
- The shared corpse of the loot workloads is a player's corpse: the fixture's
  own character receives as many stacks of five loot items as it can carry and
  dies through the death transaction of every player. A creature's corpse would
  refuse the bots' deposits and partial loot. Each bot deposits its one item
  before it loots one, so the corpse stays filled. A player's corpse lasts five
  minutes, so the fixture refuses a run whose ramp and steady interval, plus a
  minute for the bots' accounts, need the corpse longer.

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
- Full-stack: GameService in the Development environment on
  `http://127.0.0.1:5500` with the disposable PostgreSQL and Redis,
  `Database:RunMigrationsOnStartup`, the shared worker secret, no development
  bots, an authentication limit of 1,000 requests per minute instead of the
  normal five, and a public limit of 100,000 requests per minute instead of
  300, because every bot reads the Shard list from the same address. Every
  full-stack run, the lifecycle workload too, uses the Development environment,
  because GameService accepts the published passwords of
  `docker-compose.stack-stress.yml`, a Redis without a password, and the shared
  worker secret only there, as
  [Hosts, Secrets, And Passwords Outside Development](SERVICE_FEATURES.md#hosts-secrets-and-passwords-outside-development)
  describes. The account limits count per account, so they need no setting for
  a run. Gameplay workloads add `StackStressFixtures:Enabled` and the fixture
  secret to GameService, and the scenarios with a shared corpse set a
  one-second durable corpse reconciliation, so the stress corpse becomes
  visible quickly. GameService counts registration and login against two
  separate limits per client address, `register` and `login`, both with the
  `RateLimiting:Authentication` permit limit, so a run may register 1,000 bots
  and log in 1,000 bots in a minute. The generator provisions inside the limit
  instead of raising it further: when GameService answers 429, it waits the
  answer's `Retry-After`, five seconds without one and at most 60, and sends
  again, up to ten times, and the report counts every wait as the operation's
  `rateLimitWaits`. Every registration and login of the generator waits out a
  `503 auth_busy` of the password hashing lane the same way, the answer's
  `Retry-After` kept between one and 30 seconds and five seconds without one,
  up to ten times, and the report counts every wait as the operation's
  `busyWaits`; a refusal after the last wait is a failure with the code
  `auth_busy`. The playing workload provisions at most eight accounts at
  once, whatever `http-concurrency` says: registration and login hash the
  password, which GameService's
  [password hashing lane](SERVICE_FEATURES.md#password-hashing-lane) runs for
  at most half the logical processors at a time, eight on the reference
  machine, so more at once would only wait in its queue. The generator
  gets the connection string in `STACK_STRESS_POSTGRES` and the fixture secret
  in `STACK_STRESS_FIXTURE_SECRET`, never as arguments. For the [Soak](#soak)
  numbers it gets `--redis-endpoint` with the disposable Redis, and
  `--database-pool-size`, `--worker-heartbeat-timeout-seconds`,
  `--retention-item-history-seconds`, and `--retention-interval-seconds` with
  GameService's `Database:MaximumPoolSize`,
  `Simulation:WorkerHeartbeatTimeoutSeconds`, `Retention:ItemHistory`, and
  `Retention:IntervalSeconds`, read from its checked-in
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
- A scenario with an `outage`: the generator gets `--outage-at-seconds`,
  `--outage-seconds`, and `--outage-folder` with the run folder, and both
  services get `Logging__Console__FormatterName` with `simple`, because the
  console logger reads the formatter options only for a formatter named
  explicitly, `Logging__Console__FormatterOptions__TimestampFormat` with
  `yyyy-MM-ddTHH:mm:ss.fffZ `, and `Logging__Console__FormatterOptions__UseUtcTimestamp`,
  so every log entry starts with its UTC time. The script stops and restarts
  GameService as [GameService Outage Scenario](#gameservice-outage-scenario)
  describes.
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
- A join that GameService placed as the reconnect of the body but that gives
  the bot another entity than it had at its leave did not take the body over:
  the body went, because it died or its time ended, before GameService released
  its session, and the worker placed a new entity from the state the session
  captured, as [Lingering Bodies](SERVICE_FEATURES.md#lingering-bodies)
  describes. The generator then reads the database as for a return from the
  saved place. When the character's latest death lies after the leave, less
  one second, the character must stand at the World spawn of the World's
  manifest within 5 cm on the ground plane, alive and with full health, the
  outcome `respawned_after_death_before_release`; otherwise the return is
  judged like one from the saved place.
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
a body-kind return whose body died, `rejoinBodiesHit`, `rejoinBodiesDied`,
`rejoinBodiesDiedBeforeRelease` for a reconnect that found a new entity at the
World spawn because its body died before its release, and `rejoinChecksFailed`
show what the churn did, and the full-stack summary counts
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
   spawn authoring, with the spawn ids `<world>.load_<name>`, and the patrol of
   an area as the patrol path `<world>.load_<name>.patrol`, each point at the
   height of the highest ground under it. A Mob is put on
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
   World spawn. Every point of an area's patrol must stand on walkable ground
   that a route from the area's center reaches too, as the worker walks a Mob
   to the ground under each point. A node of a group is checked on the World
   without the variant's nodes, because a node blocks movement where it stands,
   and its compiled height must be the ground's. A failure names the area and
   the point.

`content/` and `src/Shared/WorldData/Generated` are only read, and a test proves
that both are unchanged after a variant. The variant's actor catalog must equal
the checked-in one, because the worker keeps loading its own.

| Property | Meaning |
| --- | --- |
| `areas` | Rectangles with a `name`, an `actorId`, `minimumX`, `minimumZ`, `maximumX`, `maximumZ`, and a `count` of 1 to 256, placed by the compiler's seeded area spawns, and an optional `patrol` of 2 to 256 points with `x`, `z`, and `waitSeconds`, which every Mob of the area walks in order and around again; without a patrol the Mobs stand |
| `groups` | Grids with a `name`, an `actorId`, `centerX`, `centerZ`, `rows` and `columns` of 1 to 16, and a `spacing` of at least 2 m |

Mobs do not change the collision, so a variant that adds only Mobs keeps the
checked-in collision revision, and a Unity client can join the run and watch
it. Resource nodes block movement with their collision boxes, so a variant with
nodes has another collision revision, and a Unity client refuses it. The script
prints which applies.

`tools/load/client-crowd-mobs.json` is no load run: it holds the walking Grey
Wolves that `tools/Measure-ClientCrowd.ps1` writes as a variant of the stress
arena to measure the client with Mobs, as
[Measure The Client With A Crowd](LOCAL_DEVELOPMENT.md#measure-the-client-with-a-crowd)
describes, and lies outside `tools/load/scenarios`, so `tools/Test-Load.ps1`
does not offer it. A tool test writes it and checks that every area walks its
patrol and that the collision revision stays.

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
soak, `soak-10min` its rehearsal, and `soak-500-30min` the soak at the Scale
Target; all three play the reference workload with [churn](#churn). Every full-stack run reports the numbers below, and a
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
with play and needs a [retention](GAME_SERVICE_MODULES.md#retention) task; a
table whose growth stays near zero at a steady population, such as `accounts`
or `characters`, grows only with new players. Rows per player hour show which
operation writes it, and bytes per row show whether the payloads or the indexes
dominate. A player hour is one bot playing for an hour; the players are the
bots joined when the steady interval began.

GameService keeps its history 14 days, which no run lasts, so both soak
scenarios give it retention windows of four minutes and a pass every 15
seconds, `Retention__ItemHistory`, `Retention__SessionHistory`, and
`Retention__IntervalSeconds` in their `gameServiceSettings`. Once the window and
two passes went by in the steady interval, every row written before it is due,
and the generator counts `item_operations` and `item_operation_changes` again:
the retention sample. The counts leave out the operations of deaths with their
journal rows, which stay until their corpse is gone, the corpse's lifetime and
a second window later. While retention keeps up, both tables hold about one
window of operations from then on and stay level. The rule
`retained_table_growth` of the [Quality Boundary](#quality-boundary) judges how
much they grew from the retention sample to the end of the steady interval, and
`retention_lag` the longest that a [drift check](#drift-checks) saw a completed
operation past the window and not held by a death event, the lag that
GameService's own metric `game_service.retention.lag` reports. Both leave out
the operations of deaths, kind `process_player_death`, the only kind a death
event holds: such an operation becomes due only when its death event leaves with
its closed corpse, the corpse's lifetime and a second window after it
completed, and retention deletes it in that same pass, so its age is no lag.
Once its death event left, `retained_table_growth` counts it like any other
operation, so operations of deaths that retention leaves behind still show
there. `soak.retention`
of the report and the summary holds both counts, their growth, and the worst
lag, and the script prints them. A run whose window and two passes leave less
than two minutes of the planned steady interval, every scenario but the soaks,
judges neither rule.

### Memory And GC Trend

For the worker, GameService, and the generator, `soak.memory` holds the
least-squares slope of the working set over the steady interval in MiB per
hour and the average of each fifth of the interval. For the worker it adds,
from its own performance file, the slope and fifths of its managed heap, its
generation 2 collections per hour, and its allocation per second, and for the
generator its own managed heap. The rule `process_memory_growth` of the
[Quality Boundary](#quality-boundary) judges the last fifth against the second:
the working set of the worker and GameService, which is what a host pays for,
and the managed heap of the generator. The generator runs with the server
garbage collector, whose working set follows the collector's decisions to
commit and release memory even while the heap stays flat: a 600 s run of
`soak-500-30min` on 2026-11-16 held the generator's heap at 161, 159, 161, 163,
and 162 MiB while its working set went 308, 343, 300, 349, and 377 MiB. Its
working set therefore says nothing about a leak in the generator, while its heap
showed the one that grew it from 199 to 294 MiB in the first 30-minute soak at
500 players. The generator samples its own heap in every mode, and the report
and the rule's note keep its working set beside it.

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
`Database:MaximumPoolSize` the plan passes, the oldest transaction that waits
idle, and the retention lag of the item operations against the
`Retention:ItemHistory` the plan passes.

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
`item_invariant_violations` the pending operations at the end, through the
economy audit's `item_operation_pending_too_long`.

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
class is expected in every run. No class is allowed in every run.

An entry with `"duringOutage": true` holds only in a run with a
[GameService outage](#gameservice-outage-scenario) and only for lines whose
time lies inside its window, which the timed logs of such a run show; it may
also name warning lines, and `worker_heartbeat_failures` and
`auth_service_timeouts` leave the warning lines it allows out and name them in
their notes. In any other run, and for the same lines outside the window, the
entry allows nothing. A run with an outage also reads `game-service-2.log` and
its `.err.log`, the restarted GameService's output.

The warning `Invalidated simulation session ... with code session_revoked after
a refused heartbeat: invalid_simulation_session` is a race of a body's end with
a session heartbeat that was already on its way: the
heartbeat service lists the sessions, a body whose death committed releases its
session meanwhile, and GameService refuses the released session in its entry of
the heartbeat's answer. The worker logs the warning even when the session had
already left its store, so the line changes nothing then; it is a warning,
which no rule judges, and it shows up in runs where bodies die, more often with
churn.

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
  players that walked near them during the steady interval, as
  [Remote Update Rate](#remote-update-rate) describes: the average, the fifth
  percentile, and the minimum over bots, the fifth percentile over every visit
  of a remote player to a bot in which the player walked near, the rate over
  every visible player for comparison, and the records of entities without a
  player spawn.
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
release counters from the in-memory authority: `sessionHeartbeats` counts the
renewed sessions and `sessionHeartbeatRequests` the session heartbeat requests,
one per batch of sessions.

Full-stack reports additionally contain:

- Registered, fixture, provisioned, logged-in, admitted, and logged-out account
  totals.
- Per-operation HTTP request, success, failure, status-code, stable failure-code,
  and latency summaries, with the 429 answers that a paced request waited out
  as `rateLimitWaits` and the `auth_busy` answers that a registration or login
  waited out as `busyWaits`.
- `burst`, for a run with a [burst](#burst-scenarios): what the burst did.
- Per-gameplay-operation request, success, expected-conflict,
  unexpected-rejection, timeout, incomplete-request, stable result-code, and
  latency summaries.
- GameService process CPU, memory, and thread summaries when exactly one local
  GameService process can be identified.
- PostgreSQL connection, active wait, idle-in-transaction, transaction, row,
  temporary-file, I/O timing, and deadlock statistics.
- Maximum active and expired unreleased simulation sessions, minimum observed
  lease headroom, and session-table insert and update counts.
- Final expected and actual fixture quantities and `audit`, the economy audit
  at the end: its outcome, its violations, its time, and every check with what
  it examined, its violations, its time, the error of a check that could not
  run, and up to three examples by id. The summary prints the violated checks
  with their examples. The `playing` workload has no fixed quantity to expect,
  because Mobs drop loot and arrows are spent, so only the audit judges it.
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
and fixed-tick resynchronization, and the share of the interval that its loop
thread was busy with the garbage collection pauses. Its performance file also
times every tick phase and every step of a loop iteration by name: a phase only
on the ticks on which it did its work, so the snapshots, which run on every
second tick, have half as many samples as the tick. The loop thread's busy
share is the time of its ticks and loop steps over the interval's length; the
process CPU beside it also holds the transport's own threads, which send what
the loop queued. The same values are published as metrics of
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
  `phases`, `loopSteps`, and `phaseParts` fold every tick phase, every loop
  step, and every part of a phase that times itself, such as
  `snapshots: remote plans`, by name the same way, in tick order.
- `loopBusyPercent`, `gcPauseMsPerSecond`, and `gcLongestPauseMs`: the loop
  thread's busy share, the garbage collection pauses per second, and the
  longest single pause of an interval, each as its average and highest
  interval; null for a file of a worker that does not write them.
- `worstInterval`: the steady interval with the highest tick p99, the higher
  tick average breaking a tie, with its loop busy share, process CPU, pauses,
  and its three costliest tick phases by milliseconds per second.
- `counters`: traffic, drops, deferrals, quota rejections, ticks over budget
  and those still over it without their garbage collection pauses,
  resynchronizations, allocation, and GC collections, each as its total and its
  largest interval. The traffic holds the messages, `packetsIn` and
  `packetsOut` with their bytes, and the UDP datagrams, `datagramsIn`,
  `datagramBytesIn`, `datagramsOut`, and `datagramBytesOut`, with
  `sealingOverheadBytes`, the bytes that the seal of
  [Transport Security](REALTIME_PROTOCOL.md#transport-security) added in both
  directions, and `droppedDatagrams`; the datagram counters are 0 for a file of
  a worker that does not write them.
- `sentByMessage` and `receivedByMessage`: every message kind that the worker
  sent or received in the steady intervals, by its `RealtimeMessageType` name,
  the most bytes first, with its messages and bytes, both per second, and both
  per second and player, over the same average peers as
  `bytesOutPerBotPerSecond`. A packet without a readable type counts under
  `0`. Both are empty for a file of a worker that does not write them. The
  generator prints them as the tables "Sent messages by kind" and "Received
  messages by kind", every kind and a total row; they are diagnostics that no
  rule judges.
- `levels`: players, peers, entities, CPU, working set, backlogs, the managed
  heap, and the worker's Mob numbers, `mobs`, `engagedMobs` that chase,
  attack, or evade, `movingMobs` that moved in the interval's last tick, and
  `mobPathQueue`, the Mob routes waiting for the planning budget, each as its
  average and highest value. The Mob routes planned, `mobPathPlans`, are a
  counter, and so are the realtime loop's failed steps and the failures it
  isolated to one player or peer, `loopStepFailures` and `loopIsolatedFailures`.
- `runIntervals`, `runSnapshotDrops`, and `runQuotaRejections`: the intervals
  that overlap the whole run, ramp and leave included, and their drops and
  quota rejections; `runLoopStepFailures` and `runLoopIsolatedFailures` are the
  loop's failures over the same intervals, which `worker_loop_failures` judges.
- `runCoverageGap` and `steadyCoverageGap`: empty when the worker intervals
  cover the whole run and follow each other without a gap, else what is
  missing. The worker writes one interval right after the other, so a gap means
  that the worker stopped writing or lost a line. The drop and quota rules need
  the whole run and the tick rules need the steady intervals without a gap; a
  partial measurement fails the rule as not evaluated, never as zero.
- `note`: why a part is missing, such as no interval inside a short steady
  interval.

The worker's traffic counters count messages: `packetsIn`, `packetsOut`, and
the other names with "packets" in the worker's file, its log lines, and its
metrics count the messages that the worker received or handed to the
transport, one per realtime message. LiteNetLib may merge several of them into
one UDP datagram, and sends acknowledgements and pings of its own, so they are
not datagrams on the wire. The datagram counters count those, as the worker's
sealing layer sees every one; under a network profile, the
[network proxy](LOCAL_DEVELOPMENT.md#what-the-proxy-counts) counts them too.

### Remote Update Rate

The remote update rate measures what a player sees: how often its client
receives the state of the remote players that walk near it. The worker sends a
record only when it is due, as
[Entity Registry And Replication](SERVICE_FEATURES.md#entity-registry-and-replication)
describes: a standing player about once a second, a far one that walks about
five times a second, and a near one that walks in every snapshot unless the
budget deferred it. A rate over every visible player would therefore mostly
count who stands, so the rate judges the near walkers alone.

Each bot's `SimulationBotSession` follows every remote player from its reliable
spawn to its despawn, once per snapshot sequence, so a duplicated or reordered
frame never counts twice. A record counts as one near moving update when it is
inside the steady interval, its position differs from the record before, the
record before also moved, and the player is within 50 m of the bot; it adds
the seconds since the record before. The 50 m is the constant
`SimulationBotRemoteCadence.DefaultNearRadius`, which the bot cannot read from
the worker: it must equal the worker's `InterestManagement:NearRadius`, so a
change of either changes both. A bot's rate is those updates over those seconds, the rate at
which its near walkers' records arrived while they walked. A bot that saw no
player walk near it has no rate. Records of entities without a player spawn,
such as a moving Mob, stay apart as `untrackedRecords`. The rule judges the
fifth percentile over bots, so the players who see the least decide, not the
average.

A visit is a remote player's stay in a bot's interest, from its spawn to its
despawn. When a visit ends, it folds into the bot's sums and, if the player
walked near, its rate goes into a histogram in bins of 0.05 Hz, from which the
report takes `visitP5Hz`, the fifth percentile over every visit. A bot
therefore keeps the players in its interest at that moment and a fixed
histogram, nothing per player it met: at 500 players with churn, every return
from the saved place brings a new entity, and a record per remote entity grew
the generator's live heap by about 2.4 MiB a minute.

A pair is a bot and a remote character, which the spawn names by its character
id: a player that leaves the bot's interest and comes back, that comes back into
the game with a new entity, or that the bot sees again after its own leave and
return stays one pair. What a bot keeps therefore grows with the characters it
saw, at most the population, and not with the length of the run or the churn.

Until changed-only replication, the rate counted every record of every visible
player over the seconds it saw the player, which equalled the near moving rate
because every snapshot carried every visible player. The report keeps that
rate as `remoteAllEntitiesUpdateRateAverageHz` and
`remoteAllEntitiesUpdateRateP5Hz`, for comparison only.

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
| `item_invariant_violations` | 0 fixture quantities that differ and 0 violations of the [economy audit](GAME_SERVICE_MODULES.md#economy-audit), whose every check must run; the note names the violated checks | A duplicated, lost, or invalid item, gold that does not match its ledger, or an operation left pending is a broken economy, whatever the load |
| `process_memory_growth` | Last fifth's average at most 20 MiB above the second fifth's, of the working set for the worker and GameService and of the managed heap for the generator, whose server GC working set moves on a flat heap, as [Memory And GC Trend](#memory-and-gc-trend) explains, when the planned steady interval lasts ten minutes or more; not applicable otherwise. The plan decides, so a measured interval that ends a little early cannot take the rule away from `soak-10min` | A leak grows the working set steadily after the warm-up, which the first fifth holds. The limit is set from the `soak-10min` rehearsal, as [Memory Limit](#memory-limit) explains, and errs on the side of catching a leak |
| `retained_table_growth` | `item_operations` and `item_operation_changes`, without the operations of deaths, which stay until their corpse is gone, grow at most 25 percent from the retention sample to the end of the steady interval, the worse table judged, when GameService's retention window and two passes leave two minutes of the planned steady interval; not applicable otherwise | While retention keeps up, the tables hold one window of operations and stay level, apart from how play varies; without it they grow with every operation, by more than half over the rest of `soak-10min` |
| `retention_lag` | At most 60 s in every drift check of the steady interval, for the same runs | An operation becomes due at most one pass interval, 15 s in the soaks, before the next pass deletes it; a lag beyond a minute means the passes do not drain what is due |
| `service_log_errors` | 0 error and critical lines outside the allow list | An error line is something an operator must look at; the allow list in the boundary file names every class that is expected in every run, with its reason, and holds none; its entries for a GameService outage hold only inside one |
| `worker_heartbeat_failures` | 0 warning lines of a failed worker registry heartbeat over the run, counted once the services stopped; inside a [GameService outage](#gameservice-outage-scenario) the lines its allowance names are left out | A failed heartbeat means that GameService did not answer the worker, which then refuses durable actions until it answers again and stops itself after its outage tolerance |
| `auth_service_timeouts` | 0 warning, error, or critical lines of the worker log that name `auth_service_timeout` over the run; inside a GameService outage the lines its allowance names are left out | A call of the worker that GameService did not answer within the worker's timeout refuses a join or loses a heartbeat |
| `burst_incomplete_accounts` | 0 accounts of a [burst](#burst-scenarios) that did not end logged in or out within its deadline, after waiting out every `auth_busy`; not applicable without a burst | A busy GameService may make a player wait and try again, never fail |
| `worker_loop_failures` | 0 failed steps of the worker's realtime loop and failures that it isolated to one player or peer, from `loopStepFailures` and `loopIsolatedFailures` of the worker intervals of the run | A failure that the loop survives still ends a player's session or loses a step of the World |
| `outage_notice_mismatches` | 0 bots joined across a [GameService outage](#gameservice-outage-scenario) without exactly one notice of each kind; not applicable without an outage | A player must learn that saving is paused and that it works again |
| `outage_kill_corpses_missing` | 0 kills while saving was paused whose corpse the killer did not see within 10 s; not applicable without an outage | Fighting goes on during an outage, corpses included |
| `outage_return_seconds` | At most 5 s from GameService's readiness to the worker's line that it answers again; not applicable without an outage | The worker retries its heartbeat every 2 s, so a return takes it about one retry |
| `outage_durable_recovery_seconds` | At most 15 s from GameService's readiness to the first durable action that commits; not applicable without an outage | What was paused must work again soon after the return |

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
highest working set from its own intervals, the generator's average CPU, the
payload bytes per bot and second that the worker received and sent, the UDP
datagrams per second that the worker received and sent,
`datagramsInPerSecond` and `datagramsOutPerSecond`, and the bytes per bot and
second that sealing added in both directions,
`sealingOverheadBytesPerBotPerSecond`, all three absent for a worker that does
not count datagrams, the worker's loop failures over the run, `workerLoopStepFailures` and
`workerLoopIsolatedFailures`, the average share of the loop thread's busy time,
`workerLoopBusyAveragePercent`, and its garbage collection pauses,
`workerGcPauseMsPerSecond`, both absent for a worker that does not write them,
so an older baseline still compares, in full-stack mode `authBusyRefusals`, every
`auth_busy` answer of the run, and for a burst its refusals and its median and
longest seconds; the summary also carries the burst itself as `burst`, a
[GameService outage](#gameservice-outage-scenario) as `outage`, and the
worker's `worstInterval` as `workerWorstInterval`, and its messages by kind as
`workerSentByMessage` and `workerReceivedByMessage`, which no key number holds.
`tools/Test-Load.ps1` prints that interval with its three costliest tick phases
before the verdict.

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

These are the checked-in baselines in `tools/load/baselines`, all recorded on
2026-11-16 at commit `e5d50ea4`, after the worker and network phase of the
hardening plan. Every summary says that the tree had changes: they were the
baseline files that the runs before it had recorded, so every run measured
that commit's code. The machine is an AMD Ryzen 7 5800 with 16 logical
processors and 31.9 GiB of memory under Windows, with the generator, the
worker, GameService, PostgreSQL, and Redis on that one machine. Beside the runs
it held about twenty containers of another project, a browser, a game
launcher, and chat programs, which together used 7 to 11 percent of all cores
in the 30 seconds before every run; no game, Unity Editor, or CI job ran. The
worker ran 30 simulation ticks and 15 snapshots per second. Each baseline file
names its commit.

| Scenario | Result | Tick p99 | Input ack p50 / p99 | Updates per second seen, p5 | Slowest operation p95 | Worker CPU, one core | Sent per player |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `smoke` | passed | 0.5 ms | 46 / 84 ms | 15.0 | corpse open 97 ms | 8% | 4.0 KiB/s |
| `worker-only-200` | passed | 8 ms | 48 / 82 ms | 10.6 | none | 25% | 30 KiB/s |
| `worker-only-500` | failed | 24 ms | 59 / 97 ms | **4.2** | none | 63% | 30 KiB/s |
| `mixed-100` | passed | 4 ms | 41 / 73 ms | 15.0 | corpse deposit 101 ms | 19% | 16 KiB/s |
| `hotspot-100` | passed | 2 ms | 39 / 75 ms | 15.0 | item move 67 ms | 10% | 4.9 KiB/s |
| `playing-100` | passed | 8 ms | 43 / 97 ms | 14.9 | equip 168 ms | 15% | 5.6 KiB/s |
| `playing-250` | passed | 16 ms | 45 / 98 ms | 14.9 | equip 181 ms | 38% | 9.8 KiB/s |
| `playing-500` | passed | 24 ms | 60 / 111 ms | 14.9 | equip 164 ms | 82% | 16 KiB/s |
| `mixed-250` | failed | 8 ms | 46 / 84 ms | 15.0 | **corpse deposit 1,388 ms** | 43% | 30 KiB/s |
| `soak-30min` | passed | 16 ms | 47 / 99 ms | 14.9 | rejoin 386 ms | 37% | 9.5 KiB/s |
| `soak-500-30min`, later that day | failed | **50 ms** | 54 / 108 ms | 14.9 | rejoin 451 ms | 82% | 14 KiB/s |
| `soak-500-30min`, 2026-11-17 | passed | 24 ms | 49 / 100 ms | 14.9 | rejoin 392 ms | 72% | 14 KiB/s |
| `login-burst` | passed | 1 ms | 35 / 66 ms | 15.0 | relocation 68 ms | 4% | 5.9 KiB/s |
| `logout-burst` | passed | 1 ms | 40 / 75 ms | 15.0 | relocation 66 ms | 5% | 5.9 KiB/s |
| `outage-60s`, no baseline | passed | 8 ms | 43 / 98 ms | 14.9 | equip 153 ms | 15% | 5.5 KiB/s |

The tick p99 is a bucket upper bound, and bold numbers fail their rule. No
full-stack run had a PostgreSQL deadlock or a lock wait longer than the
deadlock timeout, no call of the worker to GameService failed outside the
outage of `outage-60s`, no service logged an error, and the worker's loop
failed no step. The runs of `playing-100` under the network profiles are in
[Measured Under The Profiles](#measured-under-the-profiles).

What the tiers say about the [Scale Target](PROJECT_ARCHITECTURE.md#scale-target):

- **500 playing players pass a five-minute run** on this machine with every
  rule: the tick p99 is 24 ms of the 33.34 ms budget, the players who see the
  least receive 14.9 updates per second, every operation stays below 170 ms
  at the 95th percentile, and the worker uses 82 percent of a core while its
  loop thread is busy 26 percent of the time. The claim of the target also
  needs a 30-minute soak at 500, as
  [What The Playing Scenarios Are For](#what-the-playing-scenarios-are-for)
  says. On 2026-11-16 `soak-500-30min` held everything for 30 minutes except
  the tail of the tick, which passed its rule at the limit in one run and
  failed it in the next. **On 2026-11-17 it passed every rule with room**,
  after the periodic work moved off the snapshot ticks: one tick of 54,600
  went over its budget, and the tick p99 bucket was 16 ms in 178 of 182
  intervals and 24 ms in the other 4, as
  [The 30 Minute Soak](#the-30-minute-soak) shows. So the Shard holds the
  Scale Target on this machine in one soak; a second soak has not confirmed
  it yet. Entry 1 of [What Limits The Next Tier](#what-limits-the-next-tier)
  says what the tail was made of and what a larger tier would meet first.
- **250 playing players pass a five-minute run and a 30-minute soak** with every
  rule, at 37 to 38 percent of a core for the worker. 100 players in one place
  pass too.
- **A crowd in one place lowers its own update rate, by design.** 200 bots in
  one place receive 10.6 updates per second at the fifth percentile, just above
  the rule's 10, because each player's snapshot budget of 32 KiB per second
  holds back 30 percent of their records; 500 in one place receive 4.2.
- **250 players on one shared corpse fail** only the operation time of that
  corpse, the deliberate worst case of `mixed-250`: see entry 6 of
  [What Limits The Next Tier](#what-limits-the-next-tier).
- **A burst of logins waits and completes.** `login-burst` sent 400 logins
  within 39 ms: the password hashing lane refused 164 of them once with
  `auth_busy`, each went again by itself, and every account was logged in after
  16.7 seconds, half of them after 7.8. `logout-burst` logged 500 accounts out
  within 0.33 seconds.

Against the baselines of 2026-11-15, before the worker and network phase, on
the same machine, and for the worker-only scenarios against those of
2026-11-14:

- **Bytes per player fell four to thirteen times.** The worker sends
  `playing-100` 5.6 instead of 27 KiB per second and player, `playing-250` 9.8
  instead of 63, `playing-500` 16 instead of 77, the soak 9.5 instead of 60,
  `hotspot-100` 4.9 instead of 62, and `mixed-250` 30 instead of 148. A crowd
  in one place now stops at each player's budget: 30 KiB per second in both
  worker-only scenarios, against 126 and 74.
- **`playing-500` passes where it failed.** Its tick p99 fell from 50 to 24 ms,
  its update rate at the fifth percentile rose from 6.5 to 14.9, the worker's
  CPU fell from 123 to 82 percent of a core, and its highest working set from
  367 to 244 MiB. Its loop thread was busy about a third of the time before,
  26 percent now, with 2.5 ms of garbage collection pauses per second; the
  older baselines do not record either number.
- **The worker's CPU fell in every tier but `logout-burst`**, which went from 4
  to 5 percent of a core: from 54 to 38 percent in
  `playing-250`, 19 to 15 in `playing-100`, 53 to 37 in the soak, 78 to 43 in
  `mixed-250`, and 48 and 87 to 25 and 63 in the worker-only scenarios. The
  tick p99 of `playing-250` and the soak fell from 24 to 16 ms.
- **The input acknowledgement p99 fell by up to 20 ms** in most tiers, 102 to
  84 ms in `mixed-250` and 115 to 97 in `worker-only-500`, and stayed within a
  millisecond in `hotspot-100` and `logout-burst`.
- **What got worse is the dense crowd of 200:** its update rate at the fifth
  percentile fell from 15.0 to 10.6, because the per-player budget now holds
  back what the crowd's 126 KiB per second used to carry. `worker-only-500`
  rose from 3.5 to 4.2, and its tick p99 from 16 to 24 ms.
- **The operations did not move beyond the noise** but on the shared corpse:
  the slowest kind of the playing tiers is still the equip at 164 to 181 ms,
  and the soak's rejoin went from 372 to 386 ms. The shared corpse of
  `mixed-250` answered faster, a deposit in 1,388 instead of 2,087 ms and a
  partial loot in 1,262 instead of 2,246 at the 95th percentile, although its
  path did not change.

Two runs on two days do not separate the changes from the day's noise, so read
these as direction.

In `playing-100` to `playing-500` every gameplay operation succeeded: no
unexpected rejection and no timeout in 281, 727, and 1,648 kills, their loots,
the gathering, and the vendor trades, and 0.6 to 0.7 deaths per fighter and
ten minutes.

### The 30 Minute Soak

`soak-30min` ran 250 playing bots for 1,800 seconds with ten percent of them
leaving and returning per minute, and passed every rule, as the run of
2026-11-15 did.

- **Churn**: 717 leaves, 359 of them with an account logout, 367 returns to a
  standing body and 350 from the saved place; 2 leaves were skipped because no
  safe moment came within 30 seconds. Every return kept its place and health;
  five bodies died while their players were away and came back at the spawn,
  as designed.
- **Memory**: the working set levels off. Between the second and the last fifth
  of the run the worker grew 10.6 MiB, to 199 MiB, all of it before the third
  fifth (188.3, 197.5, 199.0, and 198.9 MiB from the second fifth on), and
  GameService 4.2 MiB, to 248 MiB. The worker's managed heap rose from 83 to
  92 MiB by the third fifth and stayed there, with 8 generation 2 collections
  per hour and 3.5 MiB allocated per second, against 31 MiB before the worker
  and network phase. The generator itself shrank 24 MiB.
- **Drift**: no session without a bot, no playing bot without a session, no
  session within ten seconds of its lease expiry (the least headroom was 140.1
  of 150 seconds), no pending item operation, at most 2 of 64 pooled
  connections in use.
- **Retention**: a soak sets the retention windows to four minutes, so the run
  shows whether the deletes keep up. Between 270 seconds into the steady
  interval and its end `item_operations` grew 6.1 percent and
  `item_operation_changes` 3.8 percent, against the 25 percent the rule
  allows, and no row was more than 14.8 seconds past its window, against 60.
  With those windows the whole database grew 34 MiB per hour.
- **Logs**: no error or critical line. The one warning was the start notice of
  the development admin tools.

`soak-500-30min`, the same soak at the Scale Target's 500 players, ran twice
on the afternoon of 2026-11-16, after health regeneration travelled as a rule
and with the message counts of
[What Limits The Next Tier](#what-limits-the-next-tier), while another
project's containers used 17 to 24 percent of the machine. It does not pass
yet, and the tick's tail is the reason:

- **What held**, in the second run: 500 of 500 bots and none failed; 1,432
  leaves, 729 returns to a standing body and 703 from the saved place, every
  one with its place and health, and 8 bodies that died while their players
  were away; no deadlock, no error or critical log line, no session within ten
  seconds of its lease expiry (the least headroom was 140.0 of 150 seconds);
  14.9 remote updates per second for the players who see the least;
  operations of at most 451 ms at the 95th percentile; the worker at 82
  percent of a core with its loop thread busy 28 percent of the time.
- **Memory** levels off here too: the worker grew 8.7 MiB between the second
  and the last fifth, to 222 MiB (196.4, 213.2, 216.6, 219.0, and 221.9 MiB),
  with 10.1 MiB allocated per second, and GameService stayed at about 164 MiB.
- **Retention** kept up: the operation tables grew 9.3 and 8.5 percent after
  the first 270 seconds, and no row was more than 15.0 seconds past its
  window.
- **The tick** went over its budget in 34 of 53,700 ticks, 16 in the first
  run. Its p99 bucket was 24 ms in 167 of the 195 intervals, 33.34 ms in 3,
  and 50 ms in 2, so the rule failed; the first run had 33.34 ms in 2
  intervals and passed at its limit. Among the 150 samples of each interval,
  the snapshot phase had one of 16 ms or more in 178 of the 181 steady
  intervals and one above 12 ms in 87, the corpse presence phase reached 10
  to 26 ms in 72, and movement and the Mobs reached 18 ms at times. One tick
  took 122 ms with two phases long at once, snapshots 110 ms and world actors
  62 ms, which points at the machine and not at one phase.

The first of the two runs also failed three rules that judged the tooling and
not the server, and each is corrected: the retention lag counted the
operation of a player's death from its completion although it becomes due
only when its death event leaves with the closed corpse; the generator kept
the update records of every remote entity it ever saw, which grows with
churn, and its memory is now judged by its managed heap; and a reconnect
whose body had died before GameService released the session was judged as a
body in the wrong place.

`soak-500-30min` ran again on 2026-11-17 on a quiet machine, with the
interest refresh and the appearances on the tick before each snapshot and a
lane for every periodic pass, and **passed every rule**; its summary is the
scenario's baseline now:

- **The tick** went over its budget once in 54,600 ticks, a tick of 59.9 ms.
  Its p99 bucket was 16 ms in 178 of the 182 intervals and 24 ms in 4, where
  the runs of the day before had 24 ms in most intervals and 33.34 and 50 ms
  in some. A tick took 6.4 ms on average, a snapshot tick 6.5 and the tick
  before it 6.3 by the phase timers, and the loop thread did not run for 111
  microseconds of a tick on average.
- **What held**: 500 of 500 bots and none failed; 1,429 leaves, 725 returns to
  a standing body and 704 from the saved place, every one with its place and
  health, and 8 bodies that died while their players were away; no deadlock
  and 19 lock waits longer than the deadlock timeout; no error or critical log
  line; 14.9 remote updates per second for the players who see the least;
  operations of at most 392 ms at the 95th percentile; the worker at 72
  percent of a core with its loop thread busy 24 percent of the time.
- **Memory** levels off: the worker's fifths were 209.6, 228.5, 240.8, 242.2,
  and 243.9 MiB, and GameService's 250.6, 251.5, 252.5, 257.1, and 259.0 MiB.
- **Retention** kept up: the operation tables grew 5.2 and 3.6 percent after
  the first 270 seconds, and no row was more than 15.0 seconds past its
  window.

What the history costs without retention was measured on 2026-11-14, before
retention existed: the database grew 184 MiB per hour, about 750 KiB per player
and hour.

| Table | Rows per hour | Growth per hour | Per player and hour |
| --- | ---: | ---: | ---: |
| `item_operation_changes` | 102,000 | 65 MiB | 267 KiB |
| `item_operations` | 47,000 | 107 MiB | 439 KiB |
| `item_container_slots` | 17,000 | 3.2 MiB | 13 KiB |
| `live_mob_loot_grants` | 10,000 | 2.2 MiB | 9 KiB |
| `character_currency_ledger` | 7,300 | 1.9 MiB | 8 KiB |

The two operation tables were 94 percent of that growth. `playing-500` grew 326
MiB per hour then, which is about 8 GiB per day and Shard at the Scale Target;
entry 7 of [What Limits The Next Tier](#what-limits-the-next-tier) says what 14
days of it come to.

## Replication Benchmark

A load run needs eight minutes and an idle machine. The replication benchmark
measures the worker's replication path, interest and snapshots, in about half a
minute on any machine, so each change of interest, snapshot encoding, or the
byte budgets can be measured on its own. `tools/Measure-Replication.ps1` runs
it, as [Measure Replication](LOCAL_DEVELOPMENT.md#measure-replication) shows.

### What It Runs

`ReplicationBenchmarkTests.MeasureTheReplicationLayouts` in
`Mmorpg.SimulationWorker.Tests`, in the `Benchmark` category that CI and
`tools/Test-Changes.ps1` leave out, builds the worker's replication objects the
way `RealtimeServerComposition` builds them, from the worker's own
`appsettings.json`: the entity registry, the connection bindings, the peer
registry and sender, `EntityPresenceReplicator` with the interest manager,
`SnapshotReplicator` with its `SnapshotRecords`, recipient rotation, and the
per-peer and aggregate byte budgets, and the loop guard. Instead of UDP every
peer is a transport double that counts what it is sent, by delivery method,
copies the records of the remote snapshots as a transport copies a packet, and
discards the rest; the benchmark reads the copied records' entity ids after
the tick, outside the measured phases. Nothing needs GameService or a
database. The real
`SimulationTickLoop` runs three phases: a seeded motion in the place of
movement and the Mob brains, the interest refresh on the tick before each
snapshot tick, and the snapshot broadcast on the snapshot ticks. A
clock that advances one tick per tick drives the byte budgets, so the ticks run
as fast as the machine allows and the budgets still refill as at the tick rate.

Each layout places its players and Mobs from a fixed seed. A fixed share walks
straight legs to random points of its ground at the World's walk speed, a
walking Mob at 1.4 m/s, and the rest stands. After 90 ticks of warm-up, 900
ticks, 30 seconds of the World, are measured.

The benchmark runs every layout twice and reports the second run. The ticks
follow each other without a pause, so during a first run the runtime is still
compiling the optimized code of the paths the layout uses, and the first 30
measured snapshots took up to six times as long, which showed as a 99th
percentile far above what a running worker has. The two runs must give the
same deterministic numbers, or the benchmark fails.

| Layout | Players | Mobs | Walking | Ground |
| --- | ---: | ---: | --- | --- |
| `spread-500` | 500 | 180 | 60 % of the players, 20 % of the Mobs | `playing-500` on Development World 2: 200 fighters over the eight hunting grounds, 100 gatherers over the six node fields, 150 walkers that start on the three roads and walk over the World's 360 m square, and 50 traders in the town; the Mobs in the hunting grounds' wolf areas |
| `crowd-200` | 200 | 0 | 60 % | One circle of 30 m radius |
| `crowd-500` | 500 | 0 | 60 % | One circle of 60 m radius |

The Mobs are the benchmark's own definition shaped like the grey wolf, so no
content change moves the numbers. What it leaves out: the movement simulation,
the Mob brains, combat, world actor state, corpses, items, and looks, whose
reliable messages a playing run also sends, and LiteNetLib's own sending.
Players spawn with the default look.

### What It Reports

Per layout, over the measured ticks:

- Deterministic: the snapshot bytes and all bytes per player and second, the
  packets per second, the entity records per player and snapshot, the owner's
  included, the bytes per record with the packet headers, the share of the
  remote updates the budgets deferred, and the update rates, with the counts
  they come from: bytes, packets, records, deferrals, remote recipients,
  encoded packets, spawns, despawns, dropped snapshots, and the near moving
  sightings and records.
- The update rate is the one players judge: per player, the share of the
  snapshots in which a walking entity within the near radius, 50 m, was in
  the player's records, times the snapshot rate; the average, the fifth
  percentile over players, and the lowest. Beside it, the same rate over all
  players for the walking entities within 16 m and beyond the near exit
  radius, 58 m, which shows how the budget degrades by distance, and the rate
  over every entity the player sees with a state, standing or walking, the
  measure before changed-only replication, for comparison.
- Informative: the average and the exact 99th percentile of the tick, the
  interest refresh, and the snapshot broadcast, the bytes allocated per tick,
  the garbage collections and pause milliseconds per second of the World, the
  average of every tick phase, and the wall time; and the average and the
  exact 99th percentile of every part of the interest and snapshot phases, as
  [Monitoring](MONITORING.md#simulationworker) names them under
  `simulation_worker.simulation.tick_phase_part.duration`, which the script
  prints in a second table.

The deterministic numbers depend only on the code and the layout: one source
state gives the same numbers on every run and machine. `result.json` holds
their SHA-256 fingerprint with the commit and a hash of the uncommitted changes,
and the script fails with exit code 1 when an earlier run in
`artifacts/replication` or the baseline has the same commit and changes but
another fingerprint. The timing numbers depend on the machine and what else
runs on it, so they are compared, never judged.

`ASmallLayoutKeepsItsDeterministicNumbers`, a fast test that runs with every
test run, plays 40 players and 12 Mobs with an aggregate budget so small that
about half of the due records are deferred, and pins every deterministic
number, so the harness and the budget order cannot change unnoticed. After an intended change of replication, take its new
numbers from the test's output.

### Baseline

`tools/load/baselines/replication.json` holds the baseline, recorded on
2026-11-15 with changed-only replication, on the machine of
[Measured Tiers](#measured-tiers) while other programs used about 13 percent of
its cores, so its timings are informative only:

| Layout | Snapshot KiB/s per player | All KiB/s per player | Packets/s | Records per player and snapshot | Deferred | Near walkers avg / p5 | Walkers within 16 m / beyond 56 m | Interest avg / p99 | Broadcast avg / p99 | Allocated per tick |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `spread-500` | 15.7 | 16.1 | 20,707 | 71.8 | 0 % | 15.0 / 14.9 Hz | 15.0 / 5.0 Hz | 1.1 / 2.7 ms | 2.5 / 5.7 ms | 24 KiB |
| `crowd-200` | 26.9 | 26.9 | 9,000 | 125.6 | 0 % | 15.0 / 15.0 Hz | 15.0 / 6.0 Hz | 0.2 / 0.3 ms | 0.6 / 1.1 ms | 0 KiB |
| `crowd-500` | 30.3 | 30.3 | 22,500 | 142.0 | 65.1 % | 9.9 / 7.6 Hz | 14.1 / 1.2 Hz | 1.2 / 2.7 ms | 6.7 / 15.1 ms | 0 KiB |

The baseline was measured with the near radius of 48 m and its exit radius of
56 m, which its walkers beyond 56 m name. With 50 and 58 m, on 2026-11-16, the
same layouts send 16.4, 26.9, and 30.3 KiB per second and player, `spread-500`
0.25 KiB more than with 48 m, and the fingerprint is
`0100da41381abdeb2b6c772cb46094b547653a67dedb334763d79e9568fe8548`.
The benchmark sends no combat, look, or world actor state, so the detail radius
leaves its numbers as they are.

The same layouts through the three states of the replication, deterministic
numbers only:

| Layout | | Every record, 41 bytes | Every record, 14 bytes | Due records only |
| --- | --- | ---: | ---: | ---: |
| `spread-500` | Snapshot KiB/s per player | 72.5 | 45.7 | 15.7 |
| | Packets per second | 47,146 | 36,452 | 20,707 |
| | Deferred updates | 45.3 % | 0 % | 0 % |
| `crowd-200` | Snapshot KiB/s per player | 123.5 | 42.7 | 26.9 |
| `crowd-500` | Snapshot KiB/s per player | 73.7 | 73.8 | 30.3 |
| | Packets per second | 45,000 | 45,000 | 22,500 |

`spread-500` and `crowd-200` stay inside a player's budget of 32 KiB per
second without deferring anything: a walker within 48 m comes in every
snapshot, one beyond 56 m five to six times a second, and whoever stands once
a second. `crowd-500` is the overload case: every player sees all 499 others,
most of them walking within 48 m, so the budget fills in every snapshot and
65 percent of the due records wait. The order of the budget decides who waits:
a walker within 16 m still comes 14 times a second, the near walkers 10 times
on average, and one beyond 56 m about once a second, where the 14 byte record
without the order gave everybody 10.5.

The timings are those of each layout's second run, as the benchmark reports
them. Measured back to back on the same machine, first runs against first
runs, the interest phase takes about half the time it took when every record
went out, and the broadcast, which includes the interest phase, about as long
in `crowd-200` and less in `spread-500`: in three alternating runs each,
`crowd-200` took 0.70 ms against 0.70, and `spread-500` 3.3 to 3.6 ms against
3.9 to 4.3. The interest index moves an entity only when it changes cells, a
refresh decides visibility and tier in one scan, and the scheduler's state
follows each visible list, so planning looks an entity up only when its view
or its record moved. Every snapshot packet is written into a reusable buffer,
so the two crowds allocate nothing per tick; what `spread-500` allocates is
the spawns and despawns of entities that walk in and out of view.

`crowd-500` takes about twice as long as when every record went out, because
every recipient now chooses about 142 of about 400 due records. Of its
broadcast, the planning takes about two thirds: deciding which of the 499
visible entities are due about 3.3 ms, weighing the waiting near changes
0.45 ms, and choosing the nearest 1.1 ms per snapshot, measured in a first run
on a busy machine; the interest phase takes 1.4 ms and writing, sending, and
completing the chunks 0.8 ms. The load runs before the compact record sent 115
records of 41.96 bytes per player and snapshot in `playing-500`, 70.8 KiB of
snapshots per player and second.

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

`playing-100` with `-DurationSeconds 180` under each profile on 2026-11-16 at
commit `e5d50ea4`, on the idle reference machine of
[Measured Tiers](#measured-tiers), with `-UpdateBaseline`, so they are the
baselines `playing-100.normal.json`, `playing-100.poor.json`, and
`playing-100.terrible.json`. Direct is the 300-second `playing-100` run of
Measured Tiers, which explains its larger kill count. The proxy used about 0.8
of a core in every run.

| | Direct | `normal` | `poor` | `terrible` |
| --- | --- | --- | --- | --- |
| Result | passed | passed | failed | failed |
| Failed rules | none | none | `gameplay_operation_p95_ms` | `input_acknowledgement_p99_ms`, `gameplay_operation_p95_ms` |
| Input acknowledgement p50 / p99 | 43 / 97 ms | 124 / 176 ms | 252 / 376 ms | 452 / 3,015 ms |
| Operations: p50 range; slowest p95 | 11 to 61 ms; equip 168 ms | 94 to 145 ms; item move 329 ms | 191 to 267 ms; loot close 1,330 ms | 415 to 483 ms; gather close 3,790 ms |
| Remote updates per second, p5 | 14.9 | 14.7 | 13.9 | 11.5 |
| Bots that failed | none | none | none | none |
| Join p95 | 277 ms | 482 ms | 677 ms | 1,332 ms |
| First hit after an attack, p95 | 1.1 s | 1.2 s | 1.6 s | 3.4 s |
| Kills, bot deaths | 281, 29 | 151, 21 | 153, 23 | 117, 44 |
| Worker tick p99, CPU of one core | 8 ms, 15 % | 8 ms, 19 % | 8 ms, 18 % | 8 ms, 16 % |
| Worker payload in / out per player | 0.87 / 5.6 KiB/s | 1.62 / 5.9 KiB/s | 1.58 / 5.7 KiB/s | 1.12 / 6.2 KiB/s |
| Dropped by the proxy, to the worker / to the bots | none | 1.0 / 1.0 % | 5.0 / 5.0 % | 12.9 / 13.9 % |
| Proxy lateness p99, its CPU of one core | none | 0.51 ms, 82 % | 0.53 ms, 83 % | 0.54 ms, 81 % |

What the profiles show:

- **`normal` holds every rule.** Every reliable operation answers about one
  round trip later than directly, with its p95 below 330 ms, and the input
  acknowledgement p99 rises by 79 ms, within its allowance of 95 ms. No leave
  failed, where the runs of 2026-11-14 lost 2 of 100.
- **The worker receives about 85 percent more payload per player under
  `normal` and `poor`.** A movement batch repeats every unacknowledged input up
  to four, and with a longer round trip more of them are unacknowledged when a
  batch leaves. It sends 11 percent more payload under `terrible`, which these
  runs do not explain.
- **Reliable operations slow down with loss far beyond the round trip.** Under
  `poor` the operations' p50 is about one round trip, 190 to 270 ms, but their
  p95 is 0.4 to 1.3 seconds, so `gameplay_operation_p95_ms` fails; under
  `terrible` the p50 is one round trip too, 415 to 483 ms, and the p95 0.6 to
  3.8 seconds. On 2026-11-14 the p50 under `terrible` was about 1.1 seconds and
  the p95 2 to 7 seconds. One cause was that every control message shared one
  reliable ordered channel, so one lost packet held back every later message
  until LiteNetLib resent it; under
  `terrible` only the operations at the start of a session, the equip and the
  teleport, before combat traffic filled the channel, kept a p50 near one round
  trip. Since the intents and their answers travel on the owner channel apart
  from the world state, as [Channels](REALTIME_PROTOCOL.md#channels) describes,
  `playing-100` with `-DurationSeconds 180` answered as follows, before on
  2026-11-16 at 00:30 and 00:36 with a game using about 1.3 cores beside it, after
  at 01:48 and 01:53 on the otherwise idle machine, p50 / p95 in ms:

  | Operation | `poor` before | `poor` after | `terrible` before | `terrible` after |
  | --- | --- | --- | --- | --- |
  | attack | 225 / 1,085 | 219 / 672 | 943 / 2,958 | 436 / 2,143 |
  | attack_clear | 234 / 1,032 | 220 / 710 | 1,054 / 3,151 | 422 / 1,748 |
  | equip | 280 / 381 | 257 / 363 | 466 / 2,320 | 443 / 1,346 |
  | gather_open | 229 / 1,054 | 218 / 757 | 1,066 / 2,886 | 434 / 2,039 |
  | gather_start | 221 / 811 | 219 / 685 | 852 / 2,574 | 427 / 1,811 |
  | item_move | 264 / 784 | 249 / 1,133 | 1,247 / 3,728 | 475 / 2,214 |
  | loot_all | 281 / 1,272 | 267 / 754 | 1,240 / 3,881 | 481 / 1,908 |
  | loot_open | 239 / 849 | 225 / 738 | 1,272 / 3,438 | 447 / 1,864 |
  | teleport | 219 / 678 | 215 / 300 | 461 / 2,086 | 423 / 1,368 |
  | vendor_open | 229 / 1,146 | 222 / 715 | 1,070 / 3,037 | 420 / 1,987 |
  | vendor_sale | 271 / 1,133 | 250 / 698 | 1,166 / 5,750 | 471 / 2,739 |
  | input acknowledgement | 254 / 307 | 253 / 306 | 453 / 829 | 453 / 853 |

  Under `terrible` every p50 is now about one round trip. The p95 shrank for
  most kinds, but `gameplay_operation_p95_ms` still fails under both profiles,
  led by the kinds with 9 to 16 samples, whose p95 is their largest sample.
  What remains is LiteNetLib's resend delay: a packet that is lost goes again
  only after 25 ms plus 2.1 times the measured round trip, about 0.45 seconds
  under `poor` and 0.9 under `terrible`, so an answer whose own intent or
  answer was lost once takes about 0.7 and 1.4 seconds; that matches most p95
  values after the split.
- **A graceful leave failed as a lost connection on a lossy network.** Fixed.
  2 of 100 bots in one of two `normal` runs, 25 under `poor`, and 58 under
  `terrible` failed with `connection_closed` at their leave after the steady
  interval, and none before it: the worker disconnected a peer a quarter of a
  second after it sent `LeaveAccepted`, and when that reliable message was
  lost or held back behind a lost one, the disconnect arrived first. Since
  the worker disconnects once the client acknowledged the answer, at most 2
  seconds later, and the disconnect carries the answer too, as
  [Realtime UDP Transport](SERVICE_FEATURES.md#realtime-udp-transport)
  describes, `playing-100` with `-DurationSeconds 180` lost no leave: on
  2026-11-15, before the change, 29 of 100 bots failed under `poor` and 57
  under `terrible`, and on 2026-11-16, after it, 0 and 0, with a game using
  about one and a half cores beside the runs both times. The runs of the table
  above, on the idle machine, lost no leave under any profile either. The same
  holds for every `ServerDisconnect`, protocol rejection, and the stop notice.
- **An outage costs the inputs it spans.** Under `terrible` the input
  acknowledgement p99 is 3 seconds, the outage itself, so the rule fails as
  the profile predicts; the remote update rate, which the profile takes out,
  stays at 11.5 per second at the fifth percentile.
- **A bot walks on through its outage.** Since the worker walks a silent
  player's path on, as entry 11 of
  [What Limits The Next Tier](#what-limits-the-next-tier) describes, a bot's
  character no longer stands through its outage: a bot keeps naming its path
  while no owner snapshot tells it that the walk ended, and it does not
  predict, so it has no correction to show. `playing-100` with
  `-DurationSeconds 180` under `terrible` on 2026-11-16, once before at 02:43
  and twice after at 03:17 and 03:25, with about an eighth of the machine
  busy: the input acknowledgement p99 stayed at 3,032 against 3,019 and 3,012
  ms, no bot failed, the route plans went from 1,518 to 1,610 and 1,538 with 1
  failed walk before and none after, and the kills from 104 to 78 and 89,
  which three runs do not separate from chance. The second run after had two
  `gather_open` answers `world_interaction_out_of_range`, as two runs of the
  same day before the change had. The run of the table above, on the idle
  machine, measured 3,015 ms with 1,507 route plans, no failed walk, and 117
  kills.

## What Limits The Next Tier

Each entry is a measured limit or defect with its evidence. None of them is
tuned away in the load tooling; the run folders of 2026-11-14, 2026-11-15, and
2026-11-16 hold the reports.

1. **The tick tail and the transport, now that replication fits 500.**
   Replication set the limit at 500 spread players until the interest tiers,
   the byte budget per recipient, and the broadcast planned over arrays: on
   2026-11-15 the tick p99 of `playing-500` reached 50 ms and the players who
   see the least received 6.5 updates per second, with 115 records of 42 bytes
   per player and snapshot, 36 percent of the updates deferred, 77 KiB per
   second and player, and a snapshot broadcast of 9.9 ms on average. On
   2026-11-16 `playing-500` passed every rule, and its performance file says
   what a tier above 500 meets first:
   - The tick grows faster than the population. Its average went from 2.9 ms
     at 250 players to 7.2 ms at 500, and its p99 bucket from 16 to 24 ms of
     the 33.34 ms budget, with 2 ticks over budget, the longest 37.6 ms, in 29
     intervals. The costliest tick phases of the worst interval were the
     snapshots, 76 ms per second with a p99 of 16 ms, movement, 55 ms per
     second, and combat, 52 ms per second. On that trend a tier of 750 would
     reach the budget, so these three phases are the first limit above 500.
     Under churn for 30 minutes the tail already decides the verdict at 500:
     `soak-500-30min` passed the tick rule at its limit in one run and failed
     it in the next on 2026-11-16, as
     [The 30 Minute Soak](#the-30-minute-soak) shows, with the long samples of
     the snapshot phase and of the corpse presence phase as the first things
     to look at; it passed with room on 2026-11-17, after the two changes
     below.
   - The periodic work is off the snapshot ticks since 2026-11-17: the
     interest refresh and the appearances run on the tick before each
     snapshot, and every periodic pass has a lane of its own, as
     `docs/PROJECT_ARCHITECTURE.md` describes for the tick loop. `playing-500`
     on a quiet machine then had 1 tick over budget of 11,400 in the steady
     interval, where it had 12 on 2026-11-16 and 7
     after the corpse presence pass was cut, and a tick p99 bucket of 16 ms in
     36 of 38 intervals, where all 30 had been at 24 ms. By the phase timers a
     snapshot tick takes 5.8 ms on average and the tick before it 5.6 ms, the
     snapshots 36 ms per second and the interest refresh 24. The loop thread
     did not run for 157 microseconds of a tick on average and for 17.3 ms of
     the one tick over budget, which took 36.5 ms, so half of that tick was
     the machine. The worker used 69 percent of a core. The loop thread's
     priority stays `Normal`; whether a higher one helps under contention is
     not measured.
   - The transport sends most messages for other things than snapshots, and
     the count per message kind says which. On 2026-11-16 the worker handed
     65,300 messages per second to the transport at 500 players: 15,200
     snapshots and 50,100 others of 65 bytes on average, which grew with about
     the square of the players, 2,500 per second at 100 players and 13,700 at
     250, and carried 40 percent of the bytes a player receives at 500. The
     largest kind was `CombatantState`, 13.0 per second and player at 100
     players, and half of it was regeneration: every health step of every
     wounded player, once a second, to every peer that sees the player. A
     combatant state now names its regeneration and viewers follow it by
     rule, as [Target-Based Combat](SERVICE_FEATURES.md#target-based-combat)
     describes, which took `CombatantState` to 6.3 per second and player at
     100 players. At 500 players, on a machine that other work kept 27 percent
     busy, the worker then handed 46,900 messages per second to the
     transport, 15,200 snapshots and 31,600 others, 63 per second and player
     with 33 percent of the bytes: 23.6 `CombatantState`, 13.4
     `CombatEventBatch`, 12.0 `EntityAppearanceChanged`, 7.0
     `WorldActorState`, 6.0 spawns and despawns of players and world actors,
     and under 1 of every other kind. The process used 74 percent of a core
     where it had used 82, with its loop thread busy 24 percent of the time,
     so most of its CPU is still in LiteNetLib's threads. What is left are
     real changes, a hit, a new target, a tool taken in hand, a Mob that
     stops, each sent reliably to every peer that sees the entity within the
     interest radius of 128 m, where 48 m was near then: by the areas of the
     two circles about six of seven such messages go to a viewer beyond the
     near radius, an estimate that no run measured. Decided on 2026-11-16: a
     viewer beyond the detail radius of 70 m gets the newest state once a
     second and no attack or hit, as
     [Detail Radius](SERVICE_FEATURES.md#detail-radius) describes. In
     `playing-100` with a 120 second steady interval this took the four kinds
     per second and player from 6.27 to 5.38 `CombatantState`, 3.45 to 1.30
     `CombatEventBatch`, 1.14 to 1.07 `EntityAppearanceChanged`, and 3.33 to
     2.42 `WorldActorState`, and all messages from 47.7 to 43.5. On
     2026-11-17 `playing-500` ran on a quiet machine without and then with the
     detail radius, the second run also with the near radius of 50 m: per
     second and player 24.1 to 22.3 `CombatantState`, 13.7 to 6.4
     `CombatEventBatch`, 11.8 to 12.0 `EntityAppearanceChanged`, 7.2 to 5.6
     `WorldActorState`, and all messages 95.2 to 85.0; a player received 14.9
     and then 14.4 KiB per second, the worker used 69 and then 62 percent of a
     core, and the tick took 5.7 and then 5.0 ms on average, with 1 and then 0
     ticks over budget of 11,400 and a tick p99 bucket of 16 ms or less in
     every interval. The interest refresh, which now also decides the detail,
     took 24.5 ms per second against 20.9, and the far states 5.4. The events
     halved, and the states fell little: the fighters of a hunting ground
     stand within 70 m of each other, so most changes of a fight still go to
     viewers that see it in detail, and what fell away went to the viewers
     between 70 m and the interest radius.
   - Replication itself has room: the broadcast takes 4.7 ms on average, of
     which interest takes 1.9, a player receives 42.9 records per snapshot,
     the owner's included, of 15.4 bytes with their share of the packet
     header, the budgets defer 0.003 percent of the remote updates, and the
     worker sends 16 KiB per second and player. It allocates 9.6 MiB per
     second, against 78 before, with 0.6 generation 0 and 0.09 generation 1
     collections and 2.5 ms of pauses per second.

   At 250 the worker uses 38 percent of a core with its loop thread busy 11
   percent of the time. The [Replication Benchmark](#replication-benchmark)
   measures the bytes, records, and deferrals of 500 spread players in a
   minute.
2. **Bandwidth per player.** Moved: the worker sends 5.6, 9.8, and 16 KiB per
   second to each of 100, 250, and 500 spread players, against 27 to 77 KiB on
   2026-11-15, and only the due records go out, at about 15 bytes each. A crowd
   in one place now stops at each player's snapshot budget of 32 KiB per
   second: 200 players in one place receive 10.6 updates per second at the
   fifth percentile, with 30 percent of their records deferred, and 500 receive
   4.2, with 72 percent deferred (`worker-only-500`), where the aggregate
   budget capped them at 3.5 before. That is the dense case, whose cadence
   falls by design.
3. **A deadlock between a player death and the worker's registry heartbeat.**
   Fixed. The death's transaction held the Shard's row in
   `simulation_assignments` and needed a key-share lock on the Shard's row in
   `shards` for its insert into `corpses`, while the registry heartbeat held
   that row and asked for the assignment row: two in five minutes at 500
   players and one in thirty minutes at 250 on 2026-11-14. The
   [Lock Order](GAME_SERVICE_MODULES.md#lock-order) now fixes which statement
   takes the Shard's row in which mode, and a heartbeat renewal only shares
   it. On 2026-11-15 `playing-500` and `soak-30min` ran without a deadlock and
   without a lock wait longer than the deadlock timeout, and on 2026-11-16 so
   did every full-stack run of [Measured Tiers](#measured-tiers).
4. **A registration or login burst waits in the password hashing lane.**
   Every password hash runs in the
   [password hashing lane](SERVICE_FEATURES.md#password-hashing-lane), at most
   eight at once on this machine, on threads of its own below normal priority,
   so a burst no longer takes GameService's thread pool or more than half the
   cores. On the idle machine on 2026-11-16 `login-burst` sent 400 logins
   within 39 ms: the lane refused 164 of them once with `auth_busy`, each went
   again after its `Retry-After`, and every account was logged in after 16.7
   seconds, half of them after 7.8, as on 2026-11-15 with 147 refusals, 16.4,
   and 7.9 seconds. `mixed-250`, which registers and logs in its accounts 64 at
   a time during the ramp, completed with 250 of 250 bots and without one
   refusal, and no registry heartbeat and no call of the worker to GameService
   failed in either. At its first refusal the lane held 242 waiting logins
   beside the 8 that hashed and took a hash for about 336 ms, so the queue
   holds about 240 logins before a new one would wait longer than 10 seconds.
   Not measured yet: how long a login waits once the lane admitted it, which
   no report states by itself; on a machine that a game shared, that wait
   reached 14 seconds, beyond the 10 seconds the lane aims at.
5. **A logout burst no longer overruns the Redis session cache.** The six
   `session_cache_invalidation_unavailable` of an earlier `mixed-250` were
   answers that Redis sent in time and GameService read too late:
   StackExchange.Redis timed out a tombstone's `SETEX` after 1,343 ms with the
   answer already on its socket (`qs: 0`, `in: 42`, `rs: DequeueResult`) while
   21 thread pool workers were busy against a minimum of 16, during a
   registration burst whose BCrypt ran on the thread pool, and the client's
   timeout was the readiness probe's one second. The password hashing lane
   took BCrypt off the thread pool, the Redis client has its own
   `Redis:CommandTimeoutMilliseconds`, the tombstones of one revocation go to
   Redis together, and a transient failure is tried once more within
   `RedisAcceleration:SessionRevocationDeadlineMilliseconds`. On the idle
   machine on 2026-11-16 `logout-burst` logged 500 accounts out at once within
   0.33 seconds, 0.42 on 2026-11-15, `login-burst` replaced 400 sessions, and
   `mixed-250` logged 250 accounts out, without one
   `session_cache_invalidation_unavailable` or Redis timeout. Left to
   measure: a logout burst at the Scale Target's 500 players beside a full
   World.
6. **One corpse is one queue.** Still the limit of `mixed-250`. With 250
   clients on its shared corpse, the 95th percentile of a deposit is 1.4
   seconds and of a partial loot 1.3 seconds on the idle machine on
   2026-11-16, against the 1 second the rule allows and the 2.1 and 2.2
   seconds of 2026-11-15; it is the only rule the scenario fails. That is the
   deliberate worst case of the mixed workload, not a spread of corpses:
   `mixed-100` has 101 ms on its shared corpse. The worker lets one mutation
   of a corpse through at a time, so the queue is as long as each operation's
   database
   round trips: every statement of an item transaction was one, and a deposit
   took 4.6 seconds on 2026-11-14 on a machine that a game shared. The kernel
   now sends the statements that nothing decides between together, as
   [Round Trips](GAME_SERVICE_MODULES.md#round-trips) describes, with the same
   statements in the same order: a corpse deposit takes 25 to 27 round trips
   instead of 35 to 38, a loot 24 to 26 instead of 34 to 37, and a worker's
   relocation 23 instead of 33. On loopback a round trip costs a fraction of a
   millisecond, so the gain shows mostly with the database on another host.
   Left to measure: the operation times with PostgreSQL on another host.
7. **Storage growth.** [The 30 Minute Soak](#the-30-minute-soak) measured the
   history without retention: 184 MiB per hour at 250 players, 94 percent of it
   in the two operation tables, about 8 GiB per day at 500. GameService now
   deletes its history after `Retention:ItemHistory` and
   `Retention:SessionHistory`, 14 days each, as
   [Retention](GAME_SERVICE_MODULES.md#retention) describes, so the database
   levels off at about 14 days of history, roughly 110 GiB per Shard at the
   Scale Target by those numbers. `soak-30min` passed with retention on the
   idle machine on 2026-11-15 and again on 2026-11-16: with windows of four
   minutes the operation tables grew 4 to 6 percent between the fifth minute
   and the end, and no row was more than 15 seconds past its window. Left to
   measure: what a pass costs at a table that holds 14 days. The gold ledger
   is kept on purpose and grows with trades.
8. **The generator needs several poll threads** for 200 and more bots. With one
   thread `worker-only-500` and `mixed-250` could not be measured, and the
   input acknowledgement p99 that earlier runs reported at 500 bots, 140 to 170
   ms, was partly the generator's own lateness: with four threads it is 111 ms
   in `playing-500`, whose poll rounds were late by at most 7.8 ms at the 99th
   percentile.
9. **A graceful leave ended as a lost connection on a lossy line.** Fixed. The
   worker disconnected the peer a quarter of a second after `LeaveAccepted`,
   so a lost or held back answer let the disconnect arrive first: 2, 25, and 58
   of 100 bots under `normal`, `poor`, and `terrible` on 2026-11-14. The worker
   now waits for the client's acknowledgement, at most 2 seconds, and puts the
   answer into the disconnect too; on 2026-11-16 no bot failed its leave under
   `poor` or `terrible`, also in the runs on the idle machine, where `normal`
   lost none either, and the Unity client's leaves ended cleanly 100 of 100
   times under both, as
   [Measured Under The Profiles](#measured-under-the-profiles) and
   [The Client On A Bad Network](LOCAL_DEVELOPMENT.md#the-client-on-a-bad-network)
   show. Every disconnect with a reason and the stop notice work the same way.
10. **Reliable intents get a long tail as soon as packets are lost.** Under
    `poor` the median answer takes about one round trip, but one answer in
    twenty took 0.4 to 1.6 seconds, and 2 to 7 seconds under `terrible`. Part
    of it was the one reliable ordered channel that every message shared: with
    the intents on their own channel, on 2026-11-16 the p95 of most kinds fell
    to 0.3 to 0.9 seconds under `poor` and to 1.3 to 2.7 under `terrible`,
    where every p50 fell from about 1.1 seconds to one round trip. Still the
    limit on a lossy line: the runs on the idle machine had a p95 of 0.39 to
    1.33 seconds under `poor` and 0.61 to 3.79 seconds under `terrible`; the
    longest were the loot close and the item move under `poor` and the gather
    close and the vendor open under `terrible`, kinds with 13 to 74 samples.
    The rest is LiteNetLib's resend delay of 25 ms plus 2.1 round trips per
    lost packet, the next thing to measure, as
    [Measured Under The Profiles](#measured-under-the-profiles) shows.
11. **An outage of the line snapped a walking character back.** Fixed for a
    character that keeps its path. The worker filled at most 15 lost inputs;
    after a longer gap the character had stood still on the worker, and the
    client was corrected by what it walked meanwhile, about 9 m after 3
    seconds. The worker now walks the path on through a silence of up to
    `Movement:PathContinuationSeconds`, 3 seconds, as
    [Server-Authoritative Movement](SERVICE_FEATURES.md#server-authoritative-movement)
    describes, so the inputs after the outage confirm the prediction. A turn
    during the outage still corrects the client, by what it walked on the new
    path, because the worker learns that path only with the newest inputs
    after the outage and starts it where it is; walking it on retroactively
    would let a player stand still for others and then cover the distance at
    once. The live stack test's walk turns about every two seconds, so its
    largest correction under `terrible` went from 9.1 and 9.7 m to 5.72 to
    5.78 m on 2026-11-16, as
    [The Client On A Bad Network](LOCAL_DEVELOPMENT.md#the-client-on-a-bad-network)
    shows. The load runs cannot show a correction, because the bots do not
    predict; under `terrible` on the idle machine no walk failed.
12. **Other players stood still when their snapshots ran out**, for up to half
    a second under `poor`, and for up to half a second right after every join
    under `normal` and worse, because their presentation stopped at the newest
    state when the client was measured on 2026-11-14. A remote entity now
    walks on along its velocity for a quarter of a second and then eases to a
    stop, as [Remote Players](UNITY_CLIENT_ARCHITECTURE.md#remote-players)
    describes; the client has not been measured under the profiles since. The
    load runs measure only how often the state arrives, not how the client
    shows it: on the idle machine the players who see the least received
    14.7, 13.9, and 11.5 updates per second under `normal`, `poor`, and
    `terrible`.
13. **A durable Mob corpse gave part of a stack.** Fixed: GameService now
    refuses everything but a whole-item loot for a durable creature corpse, as
    the worker does for the live ones, and the shared corpse of the loot
    workloads became a player's corpse. No run of 2026-11-16 had an unexpected
    loot rejection.
14. **With 400 players in view, the others starved although the frame held.**
    Moved. On 2026-11-14 the built client drew 400 dressed bots at 13.1 ms per
    frame at the median, but its remote players had no new state to show 18.7
    percent of the time. On 2026-11-16, with the interest tiers, the byte
    budget per recipient, and a presentation that walks on past the newest
    state, they starve 1.4 percent of the time at 400 in view, and 2.7 percent
    at 200, where each recipient's budget now defers records too; the frame is
    unchanged. What the client itself still lacks, the frames of a first
    crowd's arrival and memory that comes back, is in
    [Crowd Performance](UNITY_CLIENT_ARCHITECTURE.md#crowd-performance).

Entries 9 to 12 are measured in
[Measured Under The Profiles](#measured-under-the-profiles) and
[The Client On A Bad Network](LOCAL_DEVELOPMENT.md#the-client-on-a-bad-network).

Smaller things the bots met are in
[What A Playing Bot Sees And Sends](#what-a-playing-bot-sees-and-sends) and
[Churn](#churn).

### What Held

The same measurements also say what does not need work now:

- **Movement prediction** was never corrected at up to 200 ms round trip and
  5 percent loss: every owner snapshot confirmed it.
- **The shared simulation and the protocol** give the pinned golden results
  inside an IL2CPP player, with the development and the release settings, and
  the client runs the live scenario there without a `link.xml`.
- **Place and health** survived all 717 leaves and returns of the soak.
- **Memory** of the worker and GameService levels off within 30 minutes.
- **Leases and sessions** never drifted: no session without a player, none
  near its expiry.
- **Operations** stay below 200 ms at the 95th percentile up to 500 players on
  loopback, where the tick and the update rate now pass too.
- **The database under play**: no deadlock and no long lock wait in any tier,
  a 30-minute soak with retention keeping up, and bursts of 400 logins and 500
  logouts that complete.
- **The built client's frame** stays below 8 ms at the median with 200 dressed
  players in view and below 14 ms with 400, and no frame took longer than
  36 ms once the whole crowd was shown.

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
| `--retention-item-history-seconds`, `--retention-interval-seconds` | none | Full-stack: GameService's `Retention:ItemHistory` and `Retention:IntervalSeconds`, which the retention sample, the retention lag, and the two retention rules judge against |
| `--network-profile` | none | The network profile file the bots' traffic goes through, by the proxy that `tools/Test-Load.ps1` starts; the report names it and the verdict applies its allowances |
| `--login-burst-accounts`, `--logout-burst-accounts` | `0` | Full-stack: the accounts of a [login or logout burst](#burst-scenarios), registered before the ramp; at most one of the two |
| `--burst-at-seconds` | `10` | How far into the steady interval the burst starts |
| `--burst-deadline-seconds` | `60` | How long every burst account has to end logged in or out; the burst and its deadline fit the steady interval |
| `--outage-at-seconds`, `--outage-seconds`, `--outage-folder` | none | Full-stack `playing` without churn: when in the steady interval the generator asks `tools/Test-Load.ps1` for a [GameService outage](#gameservice-outage-scenario), how long it lasts, 10 to 100 seconds, and the folder of the request and the script's window; all three or none |

`STACK_STRESS_FIXTURE_SECRET` supplies the fixture secret of the gameplay
workloads; it is intentionally not accepted as a command-line argument. The
same seed and bot indices reproduce movement patterns, while tickets, account
session tokens, and generated passwords stay random for every run.

Exit codes: 0 when every requested bot joined and left cleanly, every admitted
account logged out, no gameplay operation failed unexpectedly, the item
invariants held and every check of the economy audit ran, and the run passed
the quality boundary when one was given; 1
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

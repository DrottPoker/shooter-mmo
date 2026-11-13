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
| `playing` | `full-stack` | Yes | Planned walks over the World, like players | Play like players in roles: fight Mobs, loot them, die and recover, and walk, as [Playing Workload](#playing-workload) describes |

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
   mode, so it never meets a stack it did not start.
3. Builds SimulationWorker, and GameService in full-stack mode, in Release.
4. In full-stack mode, recreates the disposable stack with
   `docker compose -f docker-compose.stack-stress.yml down` and
   `up -d --wait`, starts GameService and waits for `/health/ready`, then
   starts the worker and waits until it listens on UDP and its Shard is online.
   In worker-only mode it starts the generator first, because the generator is
   the worker's authority, and then the worker; no container and no
   GameService run.
5. Runs the generator and shows its output, judges the run, and writes the
   verdict.
6. Always stops what it started, also when the run fails or the script is
   interrupted with `Ctrl+C`: the generator, the worker, and GameService with
   `Ctrl+C`, so the worker saves its characters, releases their sessions, and
   goes offline as on a normal shutdown, and then the containers with
   `docker compose ... down`. In worker-only mode the generator keeps its
   in-memory authority up after it wrote the summary, and the script stops the
   worker first and the generator after it, so the worker's shutdown reaches
   its authority.
7. Prints a table of every rule with its limit, the measured value, and pass or
   fail, the difference to the scenario's checked-in baseline when there is
   one, and the result line.

It starts the apphost executables, such as `Mmorpg.SimulationWorker.exe`, so
the generator's process sampler finds the worker and GameService by name.

| Parameter | Meaning |
| --- | --- |
| `-Scenario <name>` | The file `tools/load/scenarios/<name>.json` |
| `-Bots <count>` | Overrides the scenario's bot count; the ramp step shrinks to it when it is larger |
| `-DurationSeconds <seconds>` | Overrides the scenario's steady interval |
| `-UpdateBaseline` | Copies the run's `summary.json` to `tools/load/baselines/<scenario>.json` |
| `-Plan` | Prints the services, their settings, and the generator arguments, and runs nothing |
| `-ReadyTimeoutSeconds <seconds>` | How long the services may take to become ready, 180 by default |

Exit codes: 0 the run passed the quality boundary, 1 it ran and failed the
boundary or the generator's own checks, 2 it could not run, for example
because of a busy port, an invalid scenario, a failed build, a World variant
whose area has no walkable ground, or a service that ended. A run in which the
generator itself fell behind, which the
[validity rules](#validity) judge, measured nothing: the script prints
`could not measure: the generator fell behind` and also exits with 2.

Every run writes `artifacts/load/<yyyyMMdd-HHmmss>-<scenario>/`, which Git
ignores:

| File | Content |
| --- | --- |
| `generator-report.json` | The generator's full JSON report, with the folded worker numbers and the verdict |
| `summary.json` | The small summary described in [Baselines](#baselines) |
| `worker-performance.jsonl` | The worker's performance file, one JSON object per metrics interval |
| `generator.log`, `game-service.log`, `simulation-worker.log` | The output of each process, with `.err.log` files for standard error |
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
| `playing` | The roles and home grounds of the `playing` workload, which needs it, as [Playing Scenarios](#playing-scenarios) describes |
| `worldVariant` | Extra Mob areas and node groups of a [World Variant](#world-variant), full-stack only |

| Scenario | Mode and workload | Shape |
| --- | --- | --- |
| `smoke` | full-stack `mixed-gameplay` | 10 bots in one step, 40 s steady; a tooling check |
| `worker-only-200` | worker-only `lifecycle` | 200 bots in steps of 50 five seconds apart, 60 s steady |
| `worker-only-500` | worker-only `lifecycle` | 500 bots in steps of 100 five seconds apart, 60 s steady |
| `mixed-100` | full-stack `mixed-gameplay` | 100 bots, 32-way admission, steps of 25 ten seconds apart, 120 s steady |
| `mixed-250` | full-stack `mixed-gameplay` | 250 bots, 64-way admission, steps of 50 five seconds apart, 60 s steady |
| `session-cache-on`, `session-cache-off` | full-stack `lifecycle` | The [Redis Session-cache A/B Baseline](#redis-session-cache-ab-baseline) |
| `playing-smoke` | full-stack `playing` | 20 bots on Development World 2, 12 fighters, a quarter of them with bows, in two hunting grounds and 8 walkers; a variant with 10 Feral Wolves and 10 Grey Wolves more; steps of 10 five seconds apart, 90 s steady; a tooling check of the playing workload |

The tier scenarios use the arguments of the
[Current Worker-only Baseline](#current-worker-only-baseline) and the
[Current Full-stack Baseline](#current-full-stack-baseline), with seed `1337`,
three-second inventory and four-second corpse operations, and a ten-second
metrics interval. All of them keep the product's defaults otherwise, including
the 20-second lingering body.

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
  authentication limit of 1,000 requests per minute instead of the normal five,
  which covers the registration and login of every bot. Gameplay workloads add
  the Development environment, `StackStressFixtures:Enabled`, and the fixture
  secret to GameService, and the scenarios with a shared corpse set a
  one-second durable corpse reconciliation, so the stress corpse becomes
  visible quickly. The generator
  gets the connection string in `STACK_STRESS_POSTGRES` and the fixture secret
  in `STACK_STRESS_FIXTURE_SECRET`, never as arguments.
- A `playing` scenario with `teleportToGrounds`: the worker gets
  `DOTNET_ENVIRONMENT=Development`, `DevelopmentAdmin__Enabled=true`, and
  `DevelopmentItemInteractions__GlobalBankAndRecoveryAccess=false`, as
  [The Teleport To The Home Ground](#the-teleport-to-the-home-ground)
  explains, and the generator gets `--scenario-file` with the scenario.
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
| Walker | `walking` | None | Starts on its ground, then walks to random reachable destinations over the whole World with pauses, as the [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) do |

The gatherer and the trader of the reference scenario follow in a later step;
a scenario that gives them a share is refused until they exist.

A role is a small state machine that thinks at human speed: between two
decisions it waits 250 to 650 ms, drawn per bot from the seed, and it sends at
most one intent at a time. A rejected intent, expected or not, is followed by a
pause of two to four seconds, so no bot repeats an intent in a tight loop.
Before it plays, a bot equips its kit over UDP like a player, one item at a
time, and is teleported once to its home ground. Tests drive every role on a
fake body and a fake clock.

### Fighter

A fighter picks the nearest living Mob of its ground, up to 12 m outside the
rectangle, that no other player's attack owns or busies, as the combatant state
tells it, walks into range of its weapon, stands, and attacks. A Mob that
attacks the fighter is fought first. It attacks until the Mob dies, walks back
into range when the Mob moved away, and below 30 percent health stops attacking,
walks 25 m away, and rests. When it stands in range and the worker reports that
cover blocks its line of sight, it waits three seconds for the Mob to move and
then gives that Mob up for 30 seconds and takes another one, because walking
into range again would leave it standing where it is. When the Mob died and its
corpse is lootable for the fighter, it walks to the corpse, opens it, loots all,
and closes it; a corpse that another player's attack owns is left and counted.
Below 60 percent health it rests until 90 percent, at most 40 seconds.

After a death the fighter waits for the respawn at the World spawn, walks back
to its death spot in legs of at most one planned route, opens its own corpse,
loots it all, which puts the kit back into the equipment, and hunts on. When
the corpse is not there within five seconds of arriving, the walk makes no
progress, or four minutes pass, the bot goes on as a walker, and the report
counts the lost corpse. A fighter whose weapon or arrows are gone goes on as a
walker too.

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
  `OwnerItemStateChanged` and `OwnerItemQuantityChanged`. When a committed
  change reached the bot only as a newer carry state, such as a death, or a
  result reports a revision its items do not have, the bot reads the full item
  state over HTTP, as the Unity client does; it never polls.

It sends only what a Unity client can send: movement inputs with planned paths,
`ItemOperationIntent` equip, `CombatIntent`, `CorpseInteractionIntent`, and the
one `AdminIntent` teleport. Routes come from the same planner and collision as
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

The bot reads the equipment slot of every kit item and the range of its weapon
from the compiled item catalog, as the client does, and equips the items itself.
Wolves drop arrows, and a loot all tops up the equipped stack, so archers keep
shooting in long runs.

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

`teleport` has no expected conflict.

### Playing Scenarios

The `playing` section of a scenario:

| Property | Meaning |
| --- | --- |
| `roles` | The percent of every role, `fighter`, `gatherer`, `walker`, and `trader`, adding up to 100 |
| `fighterBowPercent` | The percent of the fighters that carry a bow, 0 by default |
| `teleportToGrounds` | Whether every bot is teleported to its home ground once after its join |
| `grounds` | The home grounds: a `name`, a `kind` of `hunting`, `nodes`, `town`, or `walking`, and a rectangle of `minimumX`, `minimumZ`, `maximumX`, and `maximumZ`. Fighters need a hunting ground |

A run fails before the first bot joins when a ground has no ground under its
center, and a bot fails its teleport when the worker finds no walkable ground
there.

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
workloads that the target names, which no run has done yet.

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
- The remote update rate, how often the bots received the state of the remote
  players they saw during the steady interval, as
  [Remote Update Rate](#remote-update-rate) describes: the average, the fifth
  percentile, and the minimum over bots, the fifth percentile over every pair
  of bot and remote player, and the records of entities without a player spawn.
- The steady interval's start and end.
- Reliable spawn and despawn counts.
- SimulationWorker and generator CPU, working set, private memory, and thread
  summaries, including steady-state memory trend windows.
- `generatorTiming`: how late the latest bot's poll ran against the 3 ms poll
  schedule in every poll round of the steady interval, and, for the `playing`
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
  `kills`, `mobDamageTaken`, `botDeaths`, `corpsesRecovered`, `corpsesLost`,
  `lootRefusedOwned`, `nodesFoundEmpty`, `routePlans`, `routePlansBusy`,
  `routePlansWithoutPath`, `walksFailed`, `itemStateReloads`, and
  `rejectionPauses`; the timings `attack_first_hit`, from an attack intent to
  the bot's first hit on its target, and `route_plan` and `route_queue` of the
  steady interval; and the table of [Expected Conflicts](#expected-conflicts).
  Its gameplay operations are `equip`, `teleport`, `attack`, `attack_clear`,
  `loot_open`, `loot_all`, and `loot_close`, each with its latency from intent
  to result and its result codes.

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
- `levels`: players, peers, entities, CPU, working set, backlogs, and the
  managed heap, each as its average and highest value.
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

### Validity

A run in which the generator itself fell behind measures nothing: its bots sent
inputs late and stood when they meant to walk, whatever the stack did. Two
validity rules judge the generator, not the stack:

| Rule | Limit | Why |
| --- | --- | --- |
| `generator_poll_lateness_p99_ms` | p99 <= 16.67 ms over the poll rounds of the steady interval | Within half a 30 Hz tick, every bot sends its input in the tick it is due. Four `playing-smoke` runs on the reference machine measured a p99 between 3.1 and 4.6 ms, with single rounds of up to about 100 ms |
| `generator_route_queue_p99_ms` | p99 <= 1,000 ms over the route plans of the steady interval | A bot that waits longer than a second for its route stands instead of walking. Four `playing-smoke` runs measured a p99 below 0.5 ms with 4 planner threads; a workload without routes skips it |

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
workload, bots, and steady seconds, the verdict entries, the result, and
`keyNumbers`: join p95, input acknowledgement p50, p95, and p99, the remote
update rate average and fifth percentile, the p95 of every gameplay operation
kind, the worst steady tick p99, the worker's average single-core CPU and
highest working set from its own intervals, the generator's average CPU, and
the payload bytes per bot and second that the worker received and sent.

`-UpdateBaseline` copies the summary to `tools/load/baselines/<scenario>.json`,
which is checked in. Without it, the script prints the difference of every key
number to the checked-in baseline. The difference is informative and never
part of the exit code, because machines and their load differ. Record a
baseline only from a run on an idle machine, and name what else ran in the
pull request that checks it in. The tables below were recorded by hand before
the baseline files existed.

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
  15 Hz. The budget arithmetic gives about 5.6 Hz at 400 mutually visible
  players; `worker-only-500` measures the real rate, as
  [Remote Update Rate](#remote-update-rate) describes. Raising the budget is not the default fix: larger Worlds
  should reduce the visible set, and future protocol work may add measured
  quantization or delta compression.
- Single runs on a shared machine vary; one 200-bot run measured an input
  acknowledgement p99 of 188 ms. All of these are short runs, and a 30 to 60
  minute soak is still required before a tier counts as supported.

## Current Full-stack Baseline

These are the latest accepted full-stack numbers, from the `mixed-gameplay`
workload on development `1eeebca` on the same machine. The full-stack
`loot-hotspot` and `mixed-gameplay` workloads could not run from `6ed319a1`
(2026-10-21) until the stack stress fixture created its corpse with the Mob
corpse presentation key that durable Mob corpses require, so no later
full-stack loot baseline exists from that time. They used the normal
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

The `playing` workload adds combat with Mobs, Mob loot, deaths and corpse
recovery, equipment, the admin teleport, and walks over a World. Gathering,
trading, insurance claims, quest item policies, Bank, Recovery Storage, and Bag
nesting are not generated yet. Add those as explicit workload profiles or roles
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
`--bots`, `--duration-seconds`, `--quality-boundary`, `--source-commit`, and
`--source-tree-dirty`, validates the scenario, the generator arguments it
results in, and the quality boundary file, and exits with 2 and the reason when
one is invalid.

`StackStressGenerator world-variant --scenario <file> --run-folder <folder>`
writes the scenario's [World Variant](#world-variant) into
`<folder>/world-variant`, with the compilers' output in
`<folder>/world-variant.log`, and prints whether it keeps the checked-in
collision revision. It runs from the repository root, refuses a folder that is
not empty, and exits with 2 and the reason when the scenario has no variant or
an added actor has no walkable ground.

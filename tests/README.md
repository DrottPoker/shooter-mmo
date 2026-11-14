# Backend Tests

This directory holds the .NET backend tests: one test project per production
module, one project for flows that cross modules or processes, and shared test
infrastructure. Unity tests live inside the Unity project under
`src/Client/Assets/Tests`, as
[Unity Client Architecture](../docs/UNITY_CLIENT_ARCHITECTURE.md) describes.
The full quality gate is listed in
[Repository Quality Checks](../docs/LOCAL_DEVELOPMENT.md#repository-quality-checks),
and the check before every push in
[Checks For A Change](../docs/LOCAL_DEVELOPMENT.md#checks-for-a-change).

## Layout

| Project | Path | Covers |
| --- | --- | --- |
| `Mmorpg.Testing` | `Testing/Mmorpg.Testing` | Library: repository paths, the PostgreSQL and Redis fact attributes, per-test databases copied from templates, golden files, trait names, and the metric names that the files in `observability` reference |
| `Mmorpg.Testing.Content` | `Testing/Mmorpg.Testing.Content` | Library: item catalog, world actor, quest catalog, and collision world loaders that use the content tools |
| `Mmorpg.GameService.Testing` | `Testing/Mmorpg.GameService.Testing` | Library, the GameService test kit: the PostgreSQL test context, the test composition and its offline variant, the API test hosts, services that issue policy and quest item commands as the system actor, the item scenario world, the traced item transaction service with its SQL trace recorder, and the GameService source map and assembly list |
| `Mmorpg.Shared.Tests` | `Shared/Mmorpg.Shared.Tests` | GameProtocol, GameSimulation, WorldData, and the item catalog, world actor, and quest catalog compilers; links the Unity EditMode movement scenario, realtime protocol golden cases, and content hash coverage helper |
| `Mmorpg.Server.Common.Tests` | `Server/Mmorpg.Server.Common.Tests` | `Mmorpg.Server.Common` |
| `Mmorpg.Server.Telemetry.Tests` | `Server/Mmorpg.Server.Telemetry.Tests` | `Mmorpg.Server.Telemetry`: export options, metric views, resource, sampling, and provider registration |
| `Mmorpg.GameService.Platform.Tests` | `Server/Mmorpg.GameService.Platform.Tests` | GameService Platform |
| `Mmorpg.GameService.Accounts.Tests` | `Server/Mmorpg.GameService.Accounts.Tests` | Accounts, including PostgreSQL and Redis tests |
| `Mmorpg.GameService.Characters.Tests` | `Server/Mmorpg.GameService.Characters.Tests` | Characters, including PostgreSQL tests of character looks and saved character states |
| `Mmorpg.GameService.Placement.Tests` | `Server/Mmorpg.GameService.Placement.Tests` | Placement, including PostgreSQL tests of the character states that the simulation session API reads and saves, and of what the end of an account session does to its simulation session |
| `Mmorpg.GameService.Items.Tests` | `Server/Mmorpg.GameService.Items.Tests` | Items unit and PostgreSQL tests; the item transaction kernel tests, their goldens, and the kernel and corpse area boundary tests under `ItemTransactions`; the worker item route and its operation handler SPI |
| `Mmorpg.GameService.Quests.Tests` | `Server/Mmorpg.GameService.Quests.Tests` | Quests: PostgreSQL tests of the quest log, the quest catalog mirror, the owner's and the worker's quest log reads, and the revision that placement compares; the quest commands with their worker routes, quest drops, goldens, routing, catalog, and HTTP statuses |
| `Mmorpg.GameService.Economy.Tests` | `Server/Mmorpg.GameService.Economy.Tests` | Economy: the vendor and gathering commands with their goldens, routing, catalog, HTTP statuses, and quest credit, and every answer of the worker item route to a vendor or gathering request, pinned through its JSON |
| `Mmorpg.GameService.Content.Tests` | `Server/Mmorpg.GameService.Content.Tests` | Content: the content file loader and its startup refusals, the manifest and file routes with their cache headers and rate limit on an in-process host, and the PostgreSQL test of the startup revision check |
| `Mmorpg.GameService.Tests` | `Server/Mmorpg.GameService.Tests` | The GameService host configuration and the module architecture guard tests under `Architecture` |
| `Mmorpg.SimulationWorker.Tests` | `Server/Mmorpg.SimulationWorker.Tests` | SimulationWorker; socket and in-memory tests of `Mmorpg.SimulationWorker.Realtime` and their shared harness under `Realtime` |
| `Mmorpg.Integration.Tests` | `Integration/Mmorpg.Integration.Tests` | Flows across GameService modules or across GameService and SimulationWorker, the test output guard, and the test database isolation guard |
| `Mmorpg.Tools.Tests` | `Tools/Mmorpg.Tools.Tests` | The tools in `tools`, through their public types |

A project is a test project exactly when its name ends in `.Tests`. The folder
name equals the project name, and the root namespace equals the assembly name.
Files sit at the project root, except the `Architecture`, `ItemTransactions`,
and `Realtime` folders named above, which add their folder name to the
namespace.

## Where A New Test Goes

- A test of one production project goes into that project's test project, even
  when it needs PostgreSQL or Redis. Items tests may arrange state through the
  Accounts, Characters, and Placement services of the test kit, Quests tests
  also through Items, and Economy tests also through Quests.
- A test that needs several GameService modules to cooperate, or GameService and
  SimulationWorker together, goes into `Mmorpg.Integration.Tests`.
- Shared runtime code that also compiles into Unity is tested in
  `Mmorpg.Shared.Tests`.
- Helpers used by one project stay in that project. Move a helper into a
  `Testing` library only when a second project needs it: generic helpers into
  `Mmorpg.Testing`, content loaders into `Mmorpg.Testing.Content`, and helpers
  that compose GameService into the kit.
- A new test project references only what it tests, `Mmorpg.Testing`, and the
  libraries it needs, gets an `AssemblyInfo.cs` like its siblings, joins
  `Mmorpg.slnx` in the matching folder, and commits its `packages.lock.json`.

## Running The Tests

Run the commands from the repository root. While changing code, run only the
tests that cover it:

```powershell
dotnet test tests/Server/Mmorpg.GameService.Items.Tests --configuration Release
dotnet test tests/Server/Mmorpg.GameService.Items.Tests --configuration Release `
  --filter "FullyQualifiedName~<Class>"
dotnet test Mmorpg.slnx --configuration Release --filter "Category!=Integration"
```

The first command runs one test project, the second one test class, and the
last every test that needs neither PostgreSQL nor Redis. A test that needs
them is skipped while its connection variable is unset, as
[Traits](#traits) describes.

### Integration Tests With The Test Containers

`tools/Test-Changes.ps1` does all of this by itself. When a selected test
project has integration tests and `MMORPG_TEST_POSTGRES` or `MMORPG_TEST_REDIS`
is not set, it reads them from `.env`, in a worktree from the `.env` of the main
checkout, starts the `docker-compose.test.yml` containers when they do not run,
and stops the containers it started when it ends. It also warns when
`MMORPG_TEST_POSTGRES` uses `Host=localhost`. Use the steps below to run
integration tests by hand.

`docker-compose.test.yml` runs PostgreSQL 16 on `127.0.0.1:15432` and Redis 7
on `127.0.0.1:16379`. Both ports are below `49152`, where the dynamic port
range of Windows starts: Windows may reserve any part of that range after a
restart, and a container whose port is reserved fails to start with `ports are
not available`. Its Compose project name, `shooter-mmo-tests`, is fixed,
so every checkout and worktree shares the same containers. Both keep their data
in memory, PostgreSQL on a tmpfs and Redis without persistence, so `down`
removes every test database. Compose takes the PostgreSQL credentials from
`TEST_POSTGRES_DB`, `TEST_POSTGRES_USER`, and `TEST_POSTGRES_PASSWORD` in
`.env`, and `MMORPG_TEST_POSTGRES` and `MMORPG_TEST_REDIS` in `.env` point the
tests at the containers, as `.env.example` shows. Never point
`MMORPG_TEST_POSTGRES` at a development, staging, or production database.

1. Start the containers and set both variables from the `.env` of the main
   checkout without printing them. The first two lines find that file, also
   from a worktree, which has no `.env` of its own:

   ```powershell
   $commonDirectory = git rev-parse --path-format=absolute --git-common-dir
   $envFile = Join-Path (Split-Path -Parent $commonDirectory) ".env"
   docker compose --env-file $envFile -f docker-compose.test.yml up -d --wait
   foreach ($name in "MMORPG_TEST_POSTGRES", "MMORPG_TEST_REDIS") {
       $line = Get-Content $envFile | Where-Object { $_ -like "$name=*" } |
           Select-Object -First 1
       Set-Item "Env:$name" $line.Split("=", 2)[1]
   }
   ```

2. Run the tests. Add `--filter "Category=Integration"` to run only the
   PostgreSQL and Redis tests:

   ```powershell
   dotnet test Mmorpg.slnx --configuration Release
   ```

3. Stop the containers and clear the variables:

   ```powershell
   docker compose --env-file $envFile -f docker-compose.test.yml down
   Remove-Item Env:MMORPG_TEST_POSTGRES, Env:MMORPG_TEST_REDIS
   ```

Expected result: every test passes and none is skipped. The run takes about a
minute, and `Mmorpg.GameService.Items.Tests` is the slowest project. The
PostgreSQL server holds the databases of every test project with PostgreSQL
tests, named as [Databases And Redis](#databases-and-redis) describes, until
`down`. A second run of the same test project against the same server, started
while the first still runs, fails at once with a message that names the
databases.

## Traits

| Trait | Set by | Meaning |
| --- | --- | --- |
| `Category=Integration` | `[PostgresIntegrationFact]` and `[RedisIntegrationFact]` | The test needs PostgreSQL or Redis |
| `Infrastructure=Postgres` or `Infrastructure=Redis` | The same attributes | Which service the test needs |
| `Category=Load` | `[Trait("Category", "Load")]` | Deterministic scalability workloads with a timing budget that is sensitive to machine load |
| Unity `Graphics` | `[Category("Graphics")]` on a Unity test | The test draws with a camera, so it needs a graphics device |
| Unity `LiveStack` | `[Category("LiveStack")]` and `[Explicit]` on a Unity test | The test needs the running local stack; only `tools/Test-LiveStack.ps1` runs it, as [Live Stack Test](#live-stack-test) describes |
| Unity `ClientCrowd` | `[Category("ClientCrowd")]` and `[Explicit]` on a Unity test | The test needs the stress arena with ActiveSimulationBots; only `tools/Measure-ClientCrowd.ps1` runs it, as [Live Stack Test](#live-stack-test) describes |
| Unity `Player` | `[Category("Player")]` on a PlayMode test | The test must also pass in the runtime of a built player: `tools/Run-UnityTests.ps1 -Platform StandaloneWindows64` builds an IL2CPP test player and runs only this category in it, as [Run The Unity Tests](../docs/LOCAL_DEVELOPMENT.md#run-the-unity-tests) describes. The Editor's PlayMode suite runs it on Mono too. It holds the shared simulation and protocol goldens; a new test belongs here only when it needs no scene, no stack, and no Editor API |

Use `--filter "Category!=Integration"` for a fast loop without containers and
`--filter "Category=Integration"` for only the infrastructure tests. The
attributes skip their test while `MMORPG_TEST_POSTGRES` or `MMORPG_TEST_REDIS`
is unset, so a full run must show no skipped test.

The `Category=Load` tests are `RealtimeScalabilityTests` in
`Mmorpg.SimulationWorker.Tests`. They run with every test run; run them alone
when you change interest selection, snapshot encoding, or quota code:

```powershell
dotnet test tests/Server/Mmorpg.SimulationWorker.Tests --configuration Release `
  --filter "Category=Load"
```

Expected result: every test passes inside its five-second regression budget,
among them spatial selection and snapshot encoding and decoding for 25,000
entities and 1,000 observers.

## Databases And Redis

Every PostgreSQL test gets its own database. `PostgresTestDatabase` keeps the
databases next to the database in `MMORPG_TEST_POSTGRES` and names them after
the test assembly, `<base>_<project>`: the assembly name without the `Mmorpg.`
prefix and the `.Tests` suffix, lowercased, with dots mapped to underscores.
`Mmorpg.GameService.Items.Tests` uses `<base>_gameservice_items`.

| Database | Holds |
| --- | --- |
| `<base>` | Nothing; it only serves the `CREATE DATABASE` and `DROP DATABASE` commands |
| `<base>_<project>` | Only the run lock |
| `<base>_<project>_tpl_<name>` | One template per `PostgresTestTemplate`, rebuilt once by every test run |
| `<base>_<project>_<n>` | One test's database, recreated from a template for every lease |

- `PostgresIntegrationTestContext.CreateAsync()` copies the `initialized`
  template: the migrations, the startup seeds, and the simulation topology,
  applied through the same service graph and configuration as every test. The
  worker heartbeat still runs per test, because it records the current time.
  `CreateAsync(initializeDatabase: false)` copies the `empty` template, an empty
  `public` schema, for tests that run the initializer themselves.
- A copy takes a few tens of milliseconds, while migrating a fresh schema takes
  most of a second. Every template starts from an empty `public` schema that the
  test role recreated, then runs its initializer.
- Disposing the context ends its lease, and a later test reuses the number
  `<n>`. A run therefore needs only as many test databases as tests ran at the
  same time. A reused database is dropped with any connections a previous test
  left open and copied again. Databases are created on demand and never dropped
  at the end of a run. `PostgresTestDatabaseIsolationTests` in
  `Mmorpg.Integration.Tests` guard these rules.
- The test role needs `CREATEDB`, which the test container's role has, and the
  server must be PostgreSQL 13 or later, for `DROP DATABASE ... WITH (FORCE)`.
- The base and every derived database name must contain `test`; anything else is
  refused before a connection opens.
- A test run holds a session advisory lock on `<base>_<project>`. A second run
  of the same test project against the same server fails at once with a message
  that names the databases, so run one test run per project and server at a
  time. Different test projects run in parallel against the same server.
- Tests that build their own data source use `context.ConnectionString`, never
  the raw `MMORPG_TEST_POSTGRES` value.
- Use `127.0.0.1`, not `localhost`, as the host, as `.env.example` does. The
  test container publishes its port on IPv4 loopback only, and on Windows every
  new connection to `localhost` first waits about two seconds for `::1` to fail.
  Each test opens new connections, so `localhost` makes the PostgreSQL tests
  several times slower. If your `.env` still says `Host=localhost`, change it.

Redis tests must write only below a unique key prefix, such as
`shooter-mmo-test-<guid>`, remove their keys afterwards, and never call
`FLUSHDB` or `FLUSHALL`. The PostgreSQL test contexts disable Redis
acceleration, so they never touch Redis.

## Test Selection

`tools/Test-Changes.ps1` runs only the test projects that a change can affect,
as [Checks For A Change](../docs/LOCAL_DEVELOPMENT.md#checks-for-a-change)
describes. It follows project references: a change to a project selects every
test project that references it, directly or through other projects. A test
that reads a repository file outside its own project and the projects it
references therefore needs a rule in the `$rules` table of that script, as
`RealtimeMetricNamesTests` has for `docs/MONITORING.md`. Content, test
fixtures, generated World data, `docker-compose.test.yml`, and the monitoring
configuration already select every test project, and a configuration file of a
production or tool project, such as an `appsettings.json`, does too. The same
holds for a Unity test that reads a file outside `src/Client` and `src/Shared`:
`art/` and `tools/art/blender/` select the Unity suites because the Editor reads
the art manifests and `PlayerBodyAssetTests` reads the clip speeds in
`tools/art/blender/human_base/animate.py`.

## Shared Build Settings

`tests/Directory.Build.props` applies to every project below `tests`. It sets the
target framework, nullable and implicit usings, the global `Xunit` using, and
for test projects the test packages, a results folder per project
(`TestResults/<Project>`), the `Mmorpg.TestProjectDirectory` assembly metadata,
and the [test output placeholders](#test-output-placeholders). Test projects
reference `xunit.v3.mtp-off`, the xunit.v3 variant without Microsoft Testing
Platform: `dotnet test` runs them in VSTest mode with
`xunit.runner.visualstudio` and `coverlet.collector`, and the platform's v2
rejects that mode on the .NET 10 SDK. The test infrastructure libraries get
`xunit.v3.extensibility.core` and `xunit.v3.assert` instead. Every `xunit.v3`
package must use one version; `tools/Verify-DependencyPolicy.ps1` checks it.

Test projects run in parallel with each other. `Mmorpg.GameService.Items.Tests`
also runs its test classes in parallel, on at most four threads, which bounds
CPU contention and PostgreSQL connections; tests inside one class still run one
at a time. Its classes that observe process-wide state, such as `MeterListener`
tests, join `SerialTestCollection`, which runs alone after the parallel classes.
Put a new Items test class there when it reads process-wide state. Every other
test project disables parallel test execution inside its assembly with
`[assembly: Parallelization(Mode = ParallelMode.None)]` in `AssemblyInfo.cs`,
because its meter listener tests, static stores, and timing
budgets assume it; those projects finish before the Items tests anyway.

## Continuous Integration

`.github/workflows/ci.yml` runs the checks of
[Repository Quality Checks](../docs/LOCAL_DEVELOPMENT.md#repository-quality-checks)
as parallel jobs for every pull request, every push to `development` and
`main`, and manual runs:

| Job | Runs |
| --- | --- |
| `Backend checks and unit tests` | Dependency policy, art assets, the art pipeline's Python tests, the test selection rules of `tools/Test-Changes.ps1`, locked restore, format, build, the content and protocol verifiers, and every test with `Category!=Integration` |
| `Integration tests (items)` | `Category=Integration` in `Mmorpg.GameService.Items.Tests` |
| `Integration tests (other)` | `Category=Integration` in every other test project |
| `Observability configuration` | `tools/Verify-Observability.ps1`: the collector and Prometheus configuration, the promtool alert rule tests, and the Grafana dashboard JSON, with the images pinned in `docker-compose.observability.yml` |
| `Unity quality gate` | `tools/Run-UnityTests.ps1`: the Unity EditMode and PlayMode suites, on the self-hosted Windows runner |

The two integration jobs start their own PostgreSQL, with its data in memory,
and Redis service containers. NuGet packages are cached by `actions/setup-dotnet`
from the `packages.lock.json` files. Every test project writes one TRX file to
`TestResults/<Project>/test-results.trx`, and `tools/Verify-TestResults.ps1`
fails the job when any test in them did not run or pass. Each job uploads its
`TestResults` folder as an artifact. The observability job needs no .NET and
runs the pinned Collector and Prometheus images through the runner's Docker.

The Unity job runs Unity without a graphics device (`-nographics`), where a
camera cannot draw, so `tools/Run-UnityTests.ps1` leaves out the Unity tests of
the `Graphics` category, such as
`WardrobePreviewTests.ABodyDrawsAfterEveryChangeOfItsOutfit`, which renders
every body after each change of its outfit. `tools/Run-UnityEditorTests.ps1`
runs them in the Editor, and `tools/Test-Changes.ps1` uses it before a push when
an Editor has the project open.
The Unity job needs an installed and licensed Unity Editor, so it runs on a
self-hosted runner on the development machine and skips pull requests from
forks. It fails when a Unity test fails, is skipped or inconclusive, or when a
suite has no tests, and uploads the NUnit XML files and Unity logs as
`unity-test-results`. The runner, its security rules, and its setup are
described in [Unity CI Runner](../docs/LOCAL_DEVELOPMENT.md#unity-ci-runner).

The workflow `.github/workflows/unity-player.yml` runs once a week and on a
manual run, never for a pull request, on the same runner: it builds the `local`
development player with its start check and runs the `Player` category in an
IL2CPP test player, as [Unity CI Runner](../docs/LOCAL_DEVELOPMENT.md#unity-ci-runner)
describes. No ruleset requires it.

The ruleset `development CI gate` in `.github/rulesets/development-ci-gate.json`
requires all five jobs to pass before a commit reaches `development`; a skipped
job counts as passed. Rename a job only together with that ruleset.

## Live Stack Test

`LiveStackPlayModeTests` in `src/Client/Assets/Tests/PlayMode/LiveStack` is the
one client test that runs against the real local stack: the client's own
`ApiClient` and `RealtimeSimulationClient` on the real LiteNetLib transport, a
GameService, and the default SimulationWorker. `tools/Test-LiveStack.ps1` starts
the stack, runs it, and stops the stack again, as
[Run The Live Stack Test](../docs/LOCAL_DEVELOPMENT.md#run-the-live-stack-test)
describes. Its one scenario asserts, step by step:

1. A fresh account with a random name and a new character join `local-shard-1`
   over UDP: the ticket and the join are no reconnect, the session is joined,
   and an owner snapshot, the spawns of the world actors around the spawn, and
   the owner's combat state arrive.
2. One movement input per tick along a route about six meters long moves the
   character: the owner snapshots confirm the route and show it standing at
   least 2.5 meters from the spawn.
3. A leave is answered with `LeaveAccepted` and `BodySeconds` 20 and ends the
   session with one `SessionEnded` and no `UnexpectedlyDisconnected`.
4. A join right after it, with a new ticket for the same character, is a
   reconnect to the same entity, at the place where the character stood.
5. A line dropped without a leave, so the worker still holds the connection,
   and a join a second later reconnect to the same entity too.
6. The client's own reconnect, composed on the test's hub as the bootstrap
   composes it, restores a line that drops while the client is in the World:
   one `UnexpectedlyDisconnected`, one `SessionEnded`, and, without a manual
   join, a fresh ticket and one join that reconnects to the same entity where it
   stood, which the client's World follows.
7. After a leave and the body's 20 seconds, the join is no reconnect: a new
   entity at the saved place, with the saved health and mana.
8. A last leave is answered like the first.

`LiveStackNetworkPlayModeTests` in the same folder is the network part, in the
categories `LiveStack` and `LiveStackNetwork`. It plays the running client
itself: the bootstrap's hub, the World scene of Development World 2 with its
local player and presentation, `WorldExit`, and the client measurements, which
it records with. It walks a route with turns around the spawn for at least 30
seconds, stops, stands 2 seconds, walks back, opens and closes Mira five times
through `WorldInteractionClientController`, and leaves through `WorldExit` and
comes back `MMORPG_LIVE_STACK_LEAVES` times. Meanwhile `LiveStackWalker`, a
second hub with its own account and character, joins on the worker's own port,
`MMORPG_LIVE_STACK_DIRECT_UDP_PORT`, past the proxy, and walks back and forth
over ten meters beside the spawn, so the client always watches a remote player
that moves. It asserts only what must hold on any network:

1. The client throws no exception, and the other player walks until the end.
2. After standing still for 2 seconds the drawn player stands within 0.5 m of
   the worker's position.
3. Under `good`, `normal`, `poor`, and on loopback the client never loses its
   World by itself; under `terrible` a lost line is restored within the
   reconnect window.
4. Every leave ends at CharacterSelect or LoginMenu without the World, cleanly
   or not; how many ended cleanly is measured.
5. The hard snaps per minute, the p95 correction, and the largest unexplained
   jump of the play stay within the bounds of
   `tools/load/network-client-bounds.json` for the profile.
6. Before the main recording, a recording of its own sees a development
   teleport of 8.5 m, which the presentation shows as a snap, as a jump of at
   least 8 m in one frame, so the jump detector reads what is drawn.

The play and the leaves are recorded apart, so the moments after each join do
not mix with the play. Everything else is measured and written to
`live-network-<profile>.json` in the run's log folder, and the test prints it
as `[LIVE]` lines.

`tools/Test-LiveStack.ps1 -Platform Player` runs both tests in an IL2CPP test
player instead of the headless Editor, which proves that the client works
under IL2CPP and stripping in real use, as
[The Live Stack Test In A Player](../docs/LOCAL_DEVELOPMENT.md#the-live-stack-test-in-a-player)
describes.

`LiveStackCrowdPlayModeTests`, in the category `ClientCrowd` only, measures
the client with a crowd of ActiveSimulationBots in the stress arena,
`local-shard-2`: it enters the arena, waits up to two minutes until every bot
the client knows has a body and the client knows as many bots as
`MMORPG_CROWD_BOTS` names, or, without it, as many as stayed the same for three
seconds, records the client measurements for `MMORPG_CROWD_SECONDS`, and writes
`crowd-<label>.json` below `MMORPG_CROWD_RESULTS`. In a player it first sets a
1920 by 1080 window. It asserts only that the crowd appears and the client
stays in the arena; no rule judges the numbers.
`tools/Measure-ClientCrowd.ps1` runs it, as
[Measure The Client With A Crowd](../docs/LOCAL_DEVELOPMENT.md#measure-the-client-with-a-crowd)
describes.

No default run selects the test;
[Run The Unity Tests](../docs/LOCAL_DEVELOPMENT.md#run-the-unity-tests) says
how each runner leaves it out. CI does not run it because the self-hosted runner
has no stack of its own and the scenario takes a body's 20 seconds; a run
against no stack fails at once with a message that names the script. The
character takes no damage, so
step 7 compares full health and mana; the manual check
[Place And Vitals Across A Logout](../docs/MANUAL_TESTING.md#place-and-vitals-across-a-logout)
covers lost health.

## Item Catalog Fixture

Item rule tests never name game content. The GameService test kit, the shared
item tests, and the Unity EditMode inventory tests load the test-owned catalog
in `Fixtures/ItemCatalog`, whose `fixture.*` definitions keep fixed weights and
stack sizes, and its development item packages. `RepositoryPaths` and
`ItemCatalogTestData` expose both. Only content tests, which check that the
checked-in game content compiles, round-trips, and fits its development
packages, read the real catalog. How to change and recompile the fixture is in
[Item Catalog Authoring](../content/items/README.md#test-fixture-catalog).

## Quest Catalog Fixture

GameService quest tests never name game quests either. The test kit runs the
Quests module on the test-owned quest catalog in `Fixtures/Quests`: four
`quest.fixture.*` quests that use the fixture items and the actors of the
fixture World layout. `quest.fixture.wolves` kills two Feral Wolves and offers a
reward choice, `quest.fixture.errand` requires it and has a gather, deliver, and
talk-to step before it collects its start item, `quest.fixture.ore_run` is a
repeatable collect quest, and `quest.fixture.trophies` collects two
`fixture.trophy` quest drops that Feral Wolves drop at 50 %. A quest item
belongs to one quest, so the errand's `fixture.letter` and the trophies'
`fixture.trophy` are separate items. `RepositoryPaths.QuestCatalogFixtureContent` and
`QuestCatalogFixtureRuntimePath` name the authoring and the compiled catalog,
and `QuestCatalogTestData.CompileFixture` compiles it.

The compiled fixture is checked in, so the services load it like the real one.
`CompiledFixtureQuestCatalogMatchesItsAuthoring` in `Mmorpg.Tools.Tests` fails
when it is stale; after a fixture change, run that test with
`MMORPG_UPDATE_GOLDENS=1` to rewrite it, and review the diff.

## World Layout Fixtures

Simulation tests never depend on the layout of a development World, so the
Worlds can be redesigned without rewriting them:

- `Fixtures/WorldActors/actor-spawns.json` is the test-owned actor layout: one
  service NPC at `(2.5, 0, -1)`, two guards, one Iron Vein, and two Feral Wolves
  with a patrol, under the `development-world-1` identity that the realtime test
  harness joins. `WorldActorTestData.LoadSpawns` and `Compile` read it, and
  `RepositoryPaths.WorldActorFixtureSpawnsPath` names it. It uses the real actor
  catalog: `Compile` returns a `WorldActorRuntime`, the compiled catalog with
  the fixture's compiled spawns, and `CompileCatalog` the catalog alone.
- `CollisionTestWorldFactory` builds a flat 30 by 30 meter collision world for
  the .NET tests, and the Unity `FixtureCollisionWorld` builds the same ground
  with a wall, cover, and a ramp for the Unity movement tests.
- The Unity `FixtureWorldActorContent` copies the actor content to a temporary
  folder with the fixture spawns as Development World 1's spawns, for the actor
  editor view tests.
- The Unity `FixtureQuestContent` copies the checked-in quests, every World, and
  the compiled items, actors, and quest catalog to a temporary folder, so the
  Quest Studio tests save and verify there and never touch the repository.

Content tests name a World instead. `WorldActorTestData.LoadWorldSpawns`,
`CompileWorld`, `SpawnRuntimeJsonFor`, and `CheckedInWorldIds` read the
checked-in spawns and generated spawns of every World, and `CatalogRuntimeJson`
the generated actor catalog; the committed compiled files are the goldens of
`WorldActorCompilerTests`. `DevelopmentWorldLayoutTests` and the Unity
`AuthoredGameplayAssetTests` check the baked development Worlds themselves.

## Golden Files

`GoldenFiles.ForProject(assembly, "Golden", "ItemTransactions")` resolves a
folder below the test project from the `Mmorpg.TestProjectDirectory` assembly
metadata, so golden files move with their project and no compile-time path is
needed. The item transaction traces live in
`Server/Mmorpg.GameService.Items.Tests/Golden/ItemTransactions`, and those of
the quest commands in `Server/Mmorpg.GameService.Quests.Tests/Golden/ItemTransactions`,
and those of the vendor and gathering commands in
`Server/Mmorpg.GameService.Economy.Tests/Golden/ItemTransactions`;
the kit's `TracedItemTransactions` takes the test assembly whose folder it
compares with. Run the tests
with `MMORPG_UPDATE_GOLDENS=1` to rewrite the files after an intended change,
and review every golden diff before committing it.

### Realtime Protocol Goldens

The realtime protocol golden cases live in
`src/Client/Assets/Tests/Shared/RealtimeProtocolGolden*.cs`. Like the
movement scenario, they compile into both `Mmorpg.Shared.Tests` and the Unity
test assembly `Mmorpg.SharedTests`, which the EditMode tests and the `Player`
category of the PlayMode tests run, so .NET, Mono, and IL2CPP must produce and
accept the same bytes. They
cover four groups:

- Vectors: every message type and variant encodes to the payload pinned in
  `RealtimeProtocolGoldenVectors.cs`, and decoding then encoding it again gives
  the same bytes. Payloads above 256 bytes are pinned by length and SHA-256.
  Every message type has at least one vector.
- Structural rejections: every vector is rejected when truncated at any byte,
  extended by one byte, or given another magic, version, or message type.
- Decode cases: packets built byte by byte, without the protocol code, that
  probe every field rule. They are accepted canonically, accepted and
  normalized to a pinned canonical form, accepted although they cannot be
  encoded again, or rejected.
- Encode rejections: invalid values throw `ArgumentException`, and packets
  above 1,200 bytes throw `InvalidOperationException`.

Error texts are not pinned, only the accept or reject outcome. The vectors
change only together with an intended protocol change and a protocol version
bump, as [Versioning](../docs/REALTIME_PROTOCOL.md#versioning) describes. Never
update them to make a failing refactor pass.

`RealtimeProtocolGolden.EncodeVectors()` returns the packet of every vector by
name for tests of a receiver. `Mmorpg.Tools.Tests` links the golden files and
feeds every server-to-client vector through the bot's `SimulationBotSession`, so
a message type the bots do not handle fails there.

### Content Manifest Samples

`Fixtures/ContentManifest` holds the manifest that GameService writes for a
fixed set of four content files, as `manifest.json` with relative URLs and
`manifest-base-url.json` with `Content:BaseUrl` set. Like the item and quest
fixtures, it is one checked-in file that both sides read:
`ContentManifestSampleTests` in `Mmorpg.GameService.Content.Tests` requires
the bytes `ContentFileStore` writes to equal a sample, without its final line
feed, and the Unity `ClientContentManifestSampleTests` requires
`ClientContentManifest.TryParse` to read every field of every entry from it.
`RepositoryPaths.ContentManifestFixtureDirectory` names the folder. Change a
sample only together with both sides of the manifest format.

The shared movement scenario and when its pinned fingerprint and revision
change are described under Movement determinism in
[Test Architecture](../docs/UNITY_CLIENT_ARCHITECTURE.md#test-architecture).

### Content Hash Coverage

`ContentHashCoverageTests` and the Unity `ItemPresentationHashCoverageTests`
check that the hashes of every compiled document cover all its serialized
fields, and pin the structural field sets of items and quests. Their rule is in
[Changing A Content Format](../content/README.md#changing-a-content-format).
Both use `HashCoverage` in `src/Client/Assets/Tests/Shared`, which
`Mmorpg.Shared.Tests` links like the movement scenario.

## Internal Access

Test access to `internal` types follows the production dependency direction.

- Every GameService module grants `InternalsVisibleTo` to its own test project,
  the test projects of the modules that build on it, the kit
  (`Mmorpg.GameService.Testing`), `Mmorpg.GameService.Tests`, and
  `Mmorpg.Integration.Tests`. Platform grants Accounts, Characters, Placement,
  Items, Quests, Economy, and Content tests; Accounts grants Characters,
  Placement, Items, Quests, Economy, and Content tests; Characters grants
  Placement, Items, Quests, Economy, and Content tests; Placement grants Items,
  Quests, Economy, and Content tests; Items grants Quests, Economy, and Content
  tests; Quests grants Economy and Content tests; Economy grants Content tests.
- The GameService host grants the kit, `Mmorpg.GameService.Tests`, and
  `Mmorpg.Integration.Tests`.
- SimulationWorker grants `Mmorpg.SimulationWorker.Tests`, and
  `Mmorpg.Server.Telemetry` grants `Mmorpg.Server.Telemetry.Tests`.
- The kit keeps its types `internal` and grants all nine GameService test
  projects and `Mmorpg.Integration.Tests`.
- No grant goes to a lower module or to a production assembly.

A test project can call a kit member only when it can also see every type in
that member's signature. Module test projects therefore use the kit overloads
that take only a data source, such as `AddGameServiceForTests(dataSource)` and
`GameServiceTestComposition.BuildServiceProvider(dataSource)`, and never name
host configuration types.

## Test Output Placeholders

GameService, SimulationWorker, and ActiveSimulationBots ship appsettings files
at the same output paths. Tests read configuration and content from repository
paths, never from their output folder. Every test project sets
`CopyConflictingTransitiveContent` to `false` and links the empty `{}`
placeholders in `tests/TestOutput` to `appsettings.json`,
`Config/appsettings.json`, and `Config/appsettings.Development.json`.
`TestBuildOutputTests` in `Mmorpg.Integration.Tests`, which references both
services, fails if a service copy ever replaces a placeholder.

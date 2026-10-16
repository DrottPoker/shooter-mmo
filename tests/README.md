# Backend Tests

Last updated: 2026-10-16

This directory holds the .NET backend tests: one test project per production
module, one project for flows that cross modules or processes, and shared test
infrastructure. Unity tests live inside the Unity project under
`src/Client/Assets/Tests`. Commands for running tests are in
[Standard Quality Checks](../README.md#standard-quality-checks) and
[Local Development](../docs/LOCAL_DEVELOPMENT.md).

## Layout

| Project | Path | Covers |
| --- | --- | --- |
| `Mmorpg.Testing` | `Testing/Mmorpg.Testing` | Library: repository paths, the PostgreSQL and Redis fact attributes, per-test databases copied from templates, golden files, trait names, and the metric names that the files in `observability` reference |
| `Mmorpg.Testing.Content` | `Testing/Mmorpg.Testing.Content` | Library: item catalog, world actor, and collision world loaders that use the content tools |
| `Mmorpg.GameService.Testing` | `Testing/Mmorpg.GameService.Testing` | Library, the GameService test kit: the PostgreSQL test context, the test composition, the API test hosts, and the GameService source map and assembly list |
| `Mmorpg.Shared.Tests` | `Shared/Mmorpg.Shared.Tests` | GameProtocol, GameSimulation, WorldData, and the item catalog and world actor compilers; links the Unity EditMode movement scenario and realtime protocol golden cases |
| `Mmorpg.Server.Common.Tests` | `Server/Mmorpg.Server.Common.Tests` | `Mmorpg.Server.Common` |
| `Mmorpg.Server.Telemetry.Tests` | `Server/Mmorpg.Server.Telemetry.Tests` | `Mmorpg.Server.Telemetry`: export options, metric views, resource, sampling, and provider registration |
| `Mmorpg.GameService.Platform.Tests` | `Server/Mmorpg.GameService.Platform.Tests` | GameService Platform |
| `Mmorpg.GameService.Accounts.Tests` | `Server/Mmorpg.GameService.Accounts.Tests` | Accounts, including one PostgreSQL and one Redis test |
| `Mmorpg.GameService.Characters.Tests` | `Server/Mmorpg.GameService.Characters.Tests` | Characters |
| `Mmorpg.GameService.Placement.Tests` | `Server/Mmorpg.GameService.Placement.Tests` | Placement |
| `Mmorpg.GameService.Items.Tests` | `Server/Mmorpg.GameService.Items.Tests` | Items unit and PostgreSQL tests; the item transaction kernel tests, their goldens, and the kernel boundary tests under `ItemTransactions` |
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
  Accounts, Characters, and Placement services of the test kit.
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

## Shared Build Settings

`tests/Directory.Build.props` applies to every project below `tests`. It sets the
target framework, nullable and implicit usings, the global `Xunit` using, and
for test projects the test packages, a results folder per project
(`TestResults/<Project>`), and the `Mmorpg.TestProjectDirectory` assembly
metadata. Test projects reference `xunit.v3.mtp-off`, the xunit.v3 variant
without Microsoft Testing Platform: `dotnet test` runs them in VSTest mode with
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

## Traits

| Trait | Set by | Meaning |
| --- | --- | --- |
| `Category=Integration` | `[PostgresIntegrationFact]` and `[RedisIntegrationFact]` | The test needs PostgreSQL or Redis |
| `Infrastructure=Postgres` or `Infrastructure=Redis` | The same attributes | Which service the test needs |
| `Category=Load` | `[Trait("Category", "Load")]` | Deterministic scalability workloads with a timing budget |

Use `--filter "Category!=Integration"` for a fast loop without containers and
`--filter "Category=Integration"` for only the infrastructure tests. The
attributes skip their test while `MMORPG_TEST_POSTGRES` or `MMORPG_TEST_REDIS`
is unset, so a full run must show no skipped test.

## Continuous Integration

`.github/workflows/ci.yml` runs every check of
[Standard Quality Checks](../README.md#standard-quality-checks) as parallel jobs
for every pull request, every push to `development` and `main`, and manual runs:

| Job | Runs |
| --- | --- |
| `Backend checks and unit tests` | Dependency policy, locked restore, format, build, the three content verifiers, and every test with `Category!=Integration` |
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

The Unity job needs an installed and licensed Unity Editor, so it runs on a
self-hosted runner on the development machine and skips pull requests from
forks. It fails when a Unity test fails, is skipped or inconclusive, or when a
suite has no tests, and uploads the NUnit XML files and Unity logs as
`unity-test-results`. The runner, its security rules, and its setup are
described in [Unity CI Runner](../docs/LOCAL_DEVELOPMENT.md#unity-ci-runner).

The ruleset `development CI gate` in `.github/rulesets/development-ci-gate.json`
requires all five jobs to pass before a commit reaches `development`; a skipped
job counts as passed. Rename a job only together with that ruleset.

## Databases And Redis

Every PostgreSQL test gets its own database. `PostgresTestDatabase` keeps the
databases next to the database in `MMORPG_TEST_POSTGRES` and names them after
the test assembly, `<base>_<project>`: the assembly name without the `Mmorpg.`
prefix and the `.Tests` suffix, lowercased, with dots mapped to underscores.
`Mmorpg.GameService.Items.Tests` uses `<base>_gameservice_items`.

| Database | Holds |
| --- | --- |
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
  time.
- Tests that build their own data source use `context.ConnectionString`, never
  the raw `MMORPG_TEST_POSTGRES` value.
- Use `127.0.0.1`, not `localhost`, as the host. The test container publishes
  its port on IPv4 loopback only, and on Windows every new connection to
  `localhost` first waits about two seconds for `::1` to fail. Each test opens
  new connections, so `localhost` makes the PostgreSQL tests several times
  slower.

Redis tests must write only below a unique key prefix, such as
`shooter-mmo-test-<guid>`, remove their keys afterwards, and never call
`FLUSHDB` or `FLUSHALL`. The PostgreSQL test contexts disable Redis
acceleration, so they never touch Redis.

## Internal Access

Test access to `internal` types follows the production dependency direction.

- Every GameService module grants `InternalsVisibleTo` to its own test project,
  the test projects of the modules that build on it, the kit
  (`Mmorpg.GameService.Testing`), `Mmorpg.GameService.Tests`, and
  `Mmorpg.Integration.Tests`. Platform grants Accounts, Characters, Placement,
  and Items tests; Accounts grants Characters, Placement, and Items tests;
  Characters grants Placement and Items tests; Placement grants Items tests.
- The GameService host grants the kit, `Mmorpg.GameService.Tests`, and
  `Mmorpg.Integration.Tests`.
- SimulationWorker grants `Mmorpg.SimulationWorker.Tests`.
- The kit keeps its types `internal` and grants all six GameService test
  projects and `Mmorpg.Integration.Tests`.
- No grant goes to a lower module or to a production assembly.

A test project can call a kit member only when it can also see every type in
that member's signature. Module test projects therefore use the kit overloads
that take only a data source, such as `AddGameServiceForTests(dataSource)` and
`GameServiceTestComposition.BuildServiceProvider(dataSource)`, and never name
host configuration types.

## Golden Files

`GoldenFiles.ForProject(assembly, "Golden", "ItemTransactions")` resolves a
folder below the test project from the `Mmorpg.TestProjectDirectory` assembly
metadata, so golden files move with their project and no compile-time path is
needed. The item transaction traces live in
`Server/Mmorpg.GameService.Items.Tests/Golden/ItemTransactions`. Run the tests
with `MMORPG_UPDATE_GOLDENS=1` to rewrite the files after an intended change,
and review every golden diff before committing it.

### Realtime Protocol Goldens

The realtime protocol golden cases live in
`src/Client/Assets/Tests/EditMode/Shared/RealtimeProtocolGolden*.cs`. Like the
movement scenario, they compile into both `Mmorpg.Shared.Tests` and the Unity
EditMode tests, so .NET and Unity must produce and accept the same bytes. They
cover four groups:

- Vectors: every message type and variant encodes to the payload pinned in
  `RealtimeProtocolGoldenVectors.cs`, and decoding then encoding it again gives
  the same bytes. Payloads above 256 bytes are pinned by length and SHA-256.
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
bump. Never update them to make a failing refactor pass.

## Test Output Placeholders

GameService, SimulationWorker, and ActiveSimulationBots ship appsettings files
at the same output paths. Tests read configuration and content from repository
paths, never from their output folder. Every test project sets
`CopyConflictingTransitiveContent` to `false` and links the empty `{}`
placeholders in `tests/TestOutput` to `appsettings.json`,
`Config/appsettings.json`, and `Config/appsettings.Development.json`.
`TestBuildOutputTests` in `Mmorpg.Integration.Tests`, which references both
services, fails if a service copy ever replaces a placeholder.

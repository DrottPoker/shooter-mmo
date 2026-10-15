# Backend Tests

Last updated: 2026-10-15

This directory holds the .NET backend tests: one test project per production
module, one project for flows that cross modules or processes, and shared test
infrastructure. Unity tests live inside the Unity project under
`src/Client/Assets/Tests`. Commands for running tests are in
[Standard Quality Checks](../README.md#standard-quality-checks) and
[Local Development](../docs/LOCAL_DEVELOPMENT.md).

## Layout

| Project | Path | Covers |
| --- | --- | --- |
| `Mmorpg.Testing` | `Testing/Mmorpg.Testing` | Library: repository paths, the PostgreSQL and Redis fact attributes, per-assembly test databases, golden files, trait names |
| `Mmorpg.Testing.Content` | `Testing/Mmorpg.Testing.Content` | Library: item catalog, world actor, and collision world loaders that use the content tools |
| `Mmorpg.GameService.Testing` | `Testing/Mmorpg.GameService.Testing` | Library, the GameService test kit: the PostgreSQL test context, the test composition, the API test hosts, and the GameService source map and assembly list |
| `Mmorpg.Shared.Tests` | `Shared/Mmorpg.Shared.Tests` | GameProtocol, GameSimulation, WorldData, and the item catalog and world actor compilers; links the Unity EditMode movement scenario |
| `Mmorpg.Server.Common.Tests` | `Server/Mmorpg.Server.Common.Tests` | `Mmorpg.Server.Common` |
| `Mmorpg.GameService.Platform.Tests` | `Server/Mmorpg.GameService.Platform.Tests` | GameService Platform |
| `Mmorpg.GameService.Accounts.Tests` | `Server/Mmorpg.GameService.Accounts.Tests` | Accounts, including one PostgreSQL and one Redis test |
| `Mmorpg.GameService.Characters.Tests` | `Server/Mmorpg.GameService.Characters.Tests` | Characters |
| `Mmorpg.GameService.Placement.Tests` | `Server/Mmorpg.GameService.Placement.Tests` | Placement |
| `Mmorpg.GameService.Items.Tests` | `Server/Mmorpg.GameService.Items.Tests` | Items unit and PostgreSQL tests; the item transaction kernel tests, their goldens, and the kernel boundary tests under `ItemTransactions` |
| `Mmorpg.GameService.Tests` | `Server/Mmorpg.GameService.Tests` | The GameService host configuration and the module architecture guard tests under `Architecture` |
| `Mmorpg.SimulationWorker.Tests` | `Server/Mmorpg.SimulationWorker.Tests` | SimulationWorker; socket and in-memory tests of `Mmorpg.SimulationWorker.Realtime` and their shared harness under `Realtime` |
| `Mmorpg.Integration.Tests` | `Integration/Mmorpg.Integration.Tests` | Flows across GameService modules or across GameService and SimulationWorker, and the test output guard |
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
metadata. The test infrastructure libraries get `xunit.v3.extensibility.core`
and `xunit.v3.assert` instead. Every `xunit.v3` package must use one version;
`tools/Verify-DependencyPolicy.ps1` checks it.

Each test project disables parallel test execution inside its assembly in
`AssemblyInfo.cs`. Meter listener tests, static stores, the shared per-assembly
database, and timing budgets assume that. Test projects still run in parallel
with each other.

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

## Databases And Redis

Every test assembly gets its own PostgreSQL database next to the database in
`MMORPG_TEST_POSTGRES`. `PostgresTestDatabase` names it `<base>_<project>`: the
assembly name without the `Mmorpg.` prefix and the `.Tests` suffix, lowercased,
with dots mapped to underscores. `Mmorpg.GameService.Items.Tests` uses
`<base>_gameservice_items`.

- The database is created on first use and never dropped. The test role needs
  `CREATEDB`, which the test container's role has.
- The base and the derived database name must contain `test`; anything else is
  refused before a connection opens.
- Every `PostgresIntegrationTestContext` resets the `public` schema of its
  assembly's database before the test.
- A test run holds a session advisory lock on its database. A second run of the
  same test project against the same server fails at once with a message that
  names the database, so run one test run per project and server at a time.
- Tests that build their own data source use `context.ConnectionString`, never
  the raw `MMORPG_TEST_POSTGRES` value.

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

## Test Output Placeholders

GameService, SimulationWorker, and ActiveSimulationBots ship appsettings files
at the same output paths. Tests read configuration and content from repository
paths, never from their output folder. Every test project sets
`CopyConflictingTransitiveContent` to `false` and links the empty `{}`
placeholders in `tests/TestOutput` to `appsettings.json`,
`Config/appsettings.json`, and `Config/appsettings.Development.json`.
`TestBuildOutputTests` in `Mmorpg.Integration.Tests`, which references both
services, fails if a service copy ever replaces a placeholder.

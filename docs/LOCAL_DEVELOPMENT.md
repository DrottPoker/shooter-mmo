# Local Development

How to set up a development machine, run the backend services and the Unity
client, check a change, and carry it through a pull request. How to try each
feature by hand is in [Manual Testing](MANUAL_TESTING.md).

## Requirements

- .NET SDK 10.0.301 or newer within .NET 10. `global.json` pins `10.0.301` with
  `rollForward` set to `latestFeature`, so the newest installed .NET 10 SDK of
  the 10.0.3xx feature band or a later one is used, never a 10.0.1xx or
  10.0.2xx SDK and never .NET 11.
- Docker Desktop with Linux containers.
- Unity Editor 6000.6.7f1, installed with Unity Hub in its default path. It is
  the version that `src/Client/ProjectSettings/ProjectVersion.txt` pins.
- Python 3 for the art pipeline tests in the quality gate. They use only the
  standard library and need no Blender.
- The GitHub CLI, signed in, for the
  [pull request workflow](#pull-request-workflow).
- Optional: the Unity CLI for Editor automation and coding agents, described in
  [Unity Editor Automation With Unity CLI](#unity-editor-automation-with-unity-cli).
- Optional, for art work: Blender 5.2 and Git LFS, set up as
  [Art Pipeline](ART_PIPELINE.md#set-up-blender) describes.

## Local Environment File

GameService, SimulationWorker, and Docker Compose read local credentials and
connection strings from the ignored `.env` file in the repository root. Create
it once and replace every `replace-with-...` placeholder:

```powershell
Copy-Item .env.example .env
```

`.env.example` documents every key: the PostgreSQL database, user, and password
with the matching `ConnectionStrings__Postgres`, `ConnectionStrings__Redis`, the
`SIMULATION_WORKER_SERVICE_SECRET` that the workers share with GameService, the
development bot settings, `Telemetry__OtlpEndpoint` for the optional monitoring
stack, and the credentials and connection strings of the test containers. Keep
each password the same in both of its entries. Use `127.0.0.1`, not
`localhost`, as the host: Docker publishes the ports on IPv4 loopback only, and
on Windows every new connection to `localhost` first waits about two seconds for
`::1` to fail.

`.env` selects no topology. The worker, fleet, node, shard, and World of a
worker live together in `src/Server/SimulationWorker/Config/appsettings.json`,
and GameService owns the fleet, node, and shard topology in
`src/Server/GameService/Config/appsettings.json`. The non-secret defaults of
both services live in their `Config` folders;
[Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults) describes
the options.

How the services read `.env`:

- Each service looks for `.env` in its working directory and each parent up to
  the repository root, then in its output directory and each parent, and uses
  the first file it finds. A service started from its `bin` folder therefore
  still finds the root file.
- A line may start with `export `, a value may be quoted, and an unquoted value
  ends at a ` #` comment. A malformed line stops the service with its line
  number, never with its content.
- Process environment variables and command-line values override `.env`, and
  `.env` overrides the JSON files, as
  [Configuration Ownership](PROJECT_ARCHITECTURE.md#configuration-ownership)
  describes. Configuration is read once at startup, so restart a service after
  a change.

Never commit `.env` and never print its values.

### Change The PostgreSQL Password

Once the PostgreSQL volume exists, the password lives inside it, so changing
`POSTGRES_PASSWORD` in `.env` alone does not change it. Either keep the current
password in both `.env` entries, or change the password of the role in `psql`
and then update both entries:

```powershell
docker exec -it shooter_mmo_postgres psql -U shooter_mmo -d shooter_mmo
```

Run `\password shooter_mmo` inside `psql`. Recreating the volume, as
[Reset The Local Development Database](#reset-the-local-development-database)
describes, also applies the new password, but it deletes all local data.

## Local Infrastructure

Start PostgreSQL and Redis from the repository root:

```powershell
docker compose up -d --wait
```

Expected result: the containers `shooter_mmo_postgres` and `shooter_mmo_redis`
run and report healthy. PostgreSQL keeps its data in a Compose volume named
after the checkout folder; Redis keeps no data.

The local services use these addresses:

| Address | Service |
| --- | --- |
| `127.0.0.1:5432` | PostgreSQL |
| `127.0.0.1:6379` | Redis |
| `http://localhost:5000` | GameService |
| `0.0.0.0:27015/udp` | The default SimulationWorker of `local-shard-1`; clients connect to `127.0.0.1:27015` |
| `0.0.0.0:27016/udp` | The stress arena SimulationWorker of `local-shard-2`; clients connect to `127.0.0.1:27016` |

`SIMULATION_WORKER_ADVERTISED_HOST` and `SIMULATION_WORKER_ADVERTISED_UDP_PORT`
change the endpoint that GameService sends to clients without changing the
port the worker binds. Two other Compose files run beside this stack on ports
of their own: the PostgreSQL and Redis test containers of
`docker-compose.test.yml`, which only the integration tests use, as
[Backend Tests](../tests/README.md#integration-tests-with-the-test-containers) describes, and the
optional monitoring stack, described in [Monitoring](MONITORING.md).

## Backend Services

Run each process in its own terminal from the repository root. `dotnet run`
builds the project and starts it in the Development environment through its
launch profile, which the development tools and the Admin menu need.

### Run GameService

```powershell
dotnet run --project src/Server/GameService
```

Expected result: GameService listens on `http://localhost:5000`, applies
pending database migrations, seeds the topology, the World definitions, and the
item catalog mirror, and reports ready, as the [health checks](#health-checks)
show.

### Run The Default Worker

```powershell
dotnet run --project src/Server/SimulationWorker
```

SimulationWorker is a headless .NET Generic Host without HTTP routes. Its
configuration names worker `local-simulation-worker-1`, fleet `local-fleet`,
node `local-node-1`, shard `local-shard-1`, and World `development-world-2` on
UDP port `27015`.

Expected result:

- The worker logs `Starting simulation worker local-simulation-worker-1 for
  fleet local-fleet, node local-node-1, shard local-shard-1, and world
  development-world-2 on UDP port 27015` with its runtime id, simulation
  revision, collision revision, and loaded collision chunks. Then it logs the
  world actor revision, the realtime transport with its protocol version, 30
  ticks and 15 snapshots per second, and `Activated <n> world actors`.
- In the Development environment it warns that the development Bank and
  Recovery Storage access and the development admin tools are enabled, as
  [Development Worker Settings](MANUAL_TESTING.md#development-worker-settings)
  describes.
- `GET http://localhost:5000/api/shards` reports `local-shard-1` online.
- Every 30 seconds the worker logs its network counters and a status line,
  which [Monitoring](MONITORING.md#logs) describes.

### Run The Stress Arena Shard

GameService seeds two local shards: `local-shard-1` (`Local Shard 1`), which the
default worker binds to Development World 2, and `local-shard-2` (`Local Stress
Arena`), which runs Development World 1, the stress arena. To run both, start a
second worker in another terminal with the `StressArena` launch profile:

```powershell
dotnet run --project src/Server/SimulationWorker --launch-profile StressArena
```

The profile sets worker `local-simulation-worker-2`, shard `local-shard-2`,
World `development-world-1`, and UDP port `27016`, so both workers run side by
side against the same GameService, PostgreSQL, and Redis.

Expected result: the second worker logs shard `local-shard-2`, World
`development-world-1`, and UDP port `27016`. `GET /api/shards` lists both
shards online, and CharacterSelect lists `Local Stress Arena` next to `Local
Shard 1`. Joining it loads `DevelopmentWorld1` with the character on the spawn
pad in the middle of the arena. ActiveSimulationBots and StackStressGenerator
target this shard by default; see
[Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) and
[Stack Stress Testing](STACK_STRESS_TESTING.md).

A shard binds to the World that its worker names on the worker's first
heartbeat. Moving a shard to another World works only while the shard is
offline and drained, as
[Rebind An Offline Shard To Another World](#rebind-an-offline-shard-to-another-world)
describes.

### Content The Services Load

- Both services read the content they need from their build output, where the
  build copies it from `content` and `src/Shared/WorldData/Generated`: the World
  manifests, the World actors and collision chunks, the compiled item catalog,
  the combat rules, the character appearance content, and the development item
  packages. [Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults)
  describes the options that point a deployment at other copies.
- Startup validates all of it and stops before any player is admitted when
  something is wrong.
- After a content change, compile it as
  [Game Content](../content/README.md#compile-and-verify) describes, then
  restart the services that read it; `dotnet run` builds them first. Restart
  both after an item catalog change: GameService places characters only on a
  worker whose catalog revision equals the catalog it mirrors, and otherwise a
  join fails with `simulation_worker_item_catalog_mismatch`. A catalog change
  that would invalidate live item state stops GameService; see
  [Reset The Local Development Database](#reset-the-local-development-database).
- A worker stops with `shard_rule_set_unknown` when the rule set that
  GameService names for its shard is not in `content/combat/combat-rules.json`.

### Health Checks

Check GameService:

```powershell
Invoke-RestMethod http://localhost:5000/health/live
Invoke-RestMethod http://localhost:5000/health/ready
```

Expected result: liveness reports status `live`, and readiness reports `ready`,
or answers HTTP 503 while PostgreSQL or Redis does not answer, as
[Health And Operations](PROJECT_ARCHITECTURE.md#health-and-operations)
describes.

SimulationWorker has no HTTP surface. Its health check is a one-time run that
loads and validates its configuration and content as a normal start does, then
checks GameService readiness, Redis, and its UDP port, and exits. Run it while
the normal worker is stopped, because it needs the port:

```powershell
dotnet run --project src/Server/SimulationWorker -- --health-check-only
$LASTEXITCODE
```

Expected result: one `... ready at ...` line per dependency and exit code `0`.
With Redis or GameService stopped, a corrupted collision chunk, or UDP port
27015 taken, the run exits with a nonzero code.

While a worker runs, check that it owns its UDP socket:

```powershell
Get-NetUDPEndpoint -LocalPort 27015
```

Expected result: an IPv4 and possibly an IPv6 listener on port 27015.

### Stop The Services

Press Ctrl+C in each service terminal. A worker stopped this way marks its
shard offline at once; a worker that ends without it leaves the shard online
until the worker heartbeat timeout passes. `docker compose stop` stops
PostgreSQL and Redis and keeps the data.

## Unity Client

Open `src/Client` in the Unity Editor of the [requirements](#requirements),
with Unity Hub or with `unity open src/Client`, and wait until the import and
script compilation finish. With GameService and a worker running, open
`Assets/Scenes/LoginMenu.unity` and enter Play Mode;
[Manual Testing](MANUAL_TESTING.md#shared-setup) walks through the flow.

- The client reads the GameService address and its timeouts from
  `Assets/Content/Config/ClientConfig.asset`, which points to
  `http://localhost:5000`. Change the asset before building a client for
  another environment; the login panel cannot change it.
  [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md#configuration)
  describes the configuration.
- After a change to the realtime protocol, the shared simulation, or the
  transport, exit Play Mode, restart SimulationWorker, and rebuild every
  standalone client, so that every process runs the same code. A worker
  rejects a client of another protocol version at the join.
- Standalone player builds go to the ignored `Build`, `Builds`, or
  `ClientBuilds` folders below `src/Client` and are never committed.
- Run the Unity tests as [Run The Unity Tests](#run-the-unity-tests) describes.

## Repository Quality Checks

CI runs the full quality gate for every pull request, split into parallel jobs
that [Backend Tests](../tests/README.md#continuous-integration) describes: the
backend checks and unit tests, the PostgreSQL and Redis integration tests, the
monitoring configuration, and both Unity suites. Run the whole gate locally in
one command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Changes.ps1 -All
```

It starts the test containers when they do not run and stops them afterwards,
fails on any test that was skipped, and also checks the PowerShell syntax of
every script and that no Unity test yields `WaitForEndOfFrame`. These are the
commands of the gate, in its order, for running one step by hand from the
repository root:

```powershell
& .\tools\Test-Changes.ps1 -VerifyRules
& .\tools\Verify-DependencyPolicy.ps1
& .\tools\art\Verify-ArtAssets.ps1
python -m unittest discover -s tools/art/blender/tests
& .\tools\Verify-Observability.ps1
dotnet restore Mmorpg.slnx --locked-mode
dotnet format Mmorpg.slnx --verify-no-changes --no-restore
dotnet build Mmorpg.slnx --configuration Release --no-restore
dotnet run --project tools/ItemCatalogCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldCollisionCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldActorCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/ProtocolGenerator --configuration Release --no-build -- --verify
dotnet test Mmorpg.slnx --configuration Release --no-build --logger "trx;LogFileName=test-results.trx"
& .\tools\Verify-TestResults.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

Expected result:

- The test selection rules find a rule for every tracked file; see
  [Checks For A Change](#checks-for-a-change).
- `Dependency policy verification passed.`: exact Unity and NuGet versions in
  project, props, and targets files, one `xunit.v3` version across the test
  projects, local package paths, Unity lock coverage, one LiteNetLib version as
  [Dependency Updates](#dependency-updates) requires, and no tracked client
  builds.
- `Verified <n> art asset(s).`: every asset below `art/` is approved for its
  current `.blend` file, and every model below `src/Client/Assets/Art/Models` is
  the recorded export of one of them. It needs no Blender and reads the `.blend`
  hashes from Git LFS pointers when the files are not downloaded;
  [Art Pipeline](ART_PIPELINE.md#review-stages) describes the approvals.
- The art pipeline's Python tests pass.
- `Observability verification passed: collector, Prometheus, alert tests, and
  <n> dashboards.` It needs Docker and leaves nothing running.
- Restore accepts every committed `packages.lock.json`, format reports no file
  to change, and the build ends with zero warnings and zero errors.
- Each content verifier and the protocol verifier finds the checked-in
  generated files current, as [Game Content](../content/README.md#compile-and-verify)
  and [Realtime Protocol](REALTIME_PROTOCOL.md#schema-and-generator) describe.
- Every test passes, and `Verify-TestResults.ps1` reports `All <n> tests in <m>
  test result files passed.` The PostgreSQL and Redis integration tests run only
  when `MMORPG_TEST_POSTGRES` and `MMORPG_TEST_REDIS` point to the test
  containers, as [Backend Tests](../tests/README.md#integration-tests-with-the-test-containers)
  describes. Without them those tests are skipped and the verification fails,
  as in CI.
- Both Unity suites pass. With the project open in an Editor, run them there as
  [Run The Unity Tests](#run-the-unity-tests) describes, because the headless
  run cannot start then.

The project files that Unity generates below `src/Client`, and their NuGet lock
files, are ignored by Git, and `src/Client/Directory.Build.props` keeps the
repository build settings away from them.

### Checks For A Change

CI runs the full gate for every pull request, so a task does not run it after
every change. Check at three levels instead:

1. While changing code, run the tests that cover it: one test project, narrowed
   with `--filter "FullyQualifiedName~<Class>"` when that is faster, or one
   Unity fixture with `tools/Run-UnityEditorTests.ps1 -Mode EditMode -Filter
   <Fixture>`, as [Run The Unity Tests](#run-the-unity-tests) describes.
2. Before every push, run the checks that the changes of the branch can affect:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Changes.ps1
   ```

3. CI runs the full gate. `tools/Test-Changes.ps1 -All` runs the same checks
   locally in one command.

`Test-Changes.ps1` compares the checkout with its merge base with
`origin/development`: committed and uncommitted changes and untracked files.
It prints every changed file with the checks it selects and why, runs them, and
ends with a summary:

- A .NET file selects the projects that contain it, through the project folder,
  a linked item, or an imported MSBuild file, and every project that references
  them. Those projects are restored and built through a temporary solution
  filter, their test projects run, and `dotnet format` checks the changed C#
  files. A project file or lock file adds the dependency policy, and a
  configuration file of a production or tool project, such as an
  `appsettings.json`, runs every test project, because tests read the
  configuration of other projects.
- Other files follow the `$rules` table at the top of the script. Content, test
  fixtures, and generated World data run every test project, both Unity suites,
  and the content verifiers. The realtime protocol schema runs the protocol
  verifier, the tool tests, and both Unity suites. Unity client and shared
  package files run both Unity suites, and their linked .NET code the projects
  that compile it; a Unity package change adds the dependency policy. Art
  sources and the Blender scripts in `tools/art/blender` run
  `tools/art/Verify-ArtAssets.ps1`, the art pipeline's Python tests in
  `tools/art/blender/tests`, and both Unity suites, because the Editor reads the
  art manifests and a test reads the clip speeds in the animation script.
  Exported art runs the art verification and both Unity suites, and the art
  pipeline scripts in `tools/art` run the art verification. Monitoring
  configuration runs `tools/Verify-Observability.ps1` and every test project,
  and the test containers' Compose file runs every test project. Documentation
  runs nothing, except `docs/MONITORING.md`, which a test reads. Shared build,
  format, and test settings and the script itself run the full gate, and so
  does a file that no rule matches. A content compiler's verifier runs whenever
  its tool project is selected, and every changed PowerShell script gets a
  syntax check.
- Integration tests run when a selected test project has any. When
  `MMORPG_TEST_POSTGRES` and `MMORPG_TEST_REDIS` are not set, the script reads
  them from `.env`, in a worktree from the `.env` of the main checkout, starts the
  `docker-compose.test.yml` containers when they do not run, and stops the
  containers it started when it ends. A skipped test fails the check, as in CI.
- The Unity suites run through `tools/Run-UnityEditorTests.ps1` when an Editor
  has this checkout's project open, and otherwise headless through
  `tools/Run-UnityTests.ps1`. CI runs them headless, where Unity never evokes
  `WaitForEndOfFrame`, so whenever the Unity suites are selected, the check
  `Unity batchmode rules` fails for a test that yields it, even with
  `-SkipUnity`.

Options:

| Option | Effect |
| --- | --- |
| `-Plan` | Prints the changed files and the selected checks, and runs nothing |
| `-Path <paths>` | Checks these repository paths instead of the changes of the branch; with `-Plan` it shows what a change to them would run |
| `-All` | Runs the full gate |
| `-Base <ref>` | Compares with another branch than `origin/development` |
| `-SkipIntegration` | Runs no integration test, so no containers are needed |
| `-SkipUnity` | Leaves the Unity suites to CI |
| `-VerifyRules` | Fails when a tracked file matches no rule, a .NET folder file belongs to no project, or a rule names an unknown project; CI runs it |

Expected result: the script lists each changed file with its checks, then runs
them, and ends with `Result: every one of <n> selected checks passed` and exit
code 0. A failed check prints its output and gives exit code 1; exit code 2
means the changes or the project files could not be read. A change to one test
file takes about half a minute, and a Unity client change about two minutes.

Keep the rules right: a test that reads a repository file outside its project
and the projects it references needs a rule, as
[Backend Tests](../tests/README.md#test-selection) describes. When CI fails on a
check that the script did not select, fix the failure and add the missing rule
in the same task.

## Dependency Updates

Dependabot proposes NuGet and GitHub Actions updates against `development`
once a month. Unity Package Manager updates stay manual, because Dependabot does
not own `Packages/manifest.json`: update a Unity package in a focused branch,
let Unity rewrite `packages-lock.json`, and run both Unity suites and the
backend gate before merging. The experimental Unity Pipeline package has its
own steps in
[Unity Editor Automation With Unity CLI](#unity-editor-automation-with-unity-cli).

LiteNetLib must have the same version in
`src/Server/SimulationWorker/Mmorpg.SimulationWorker.csproj`,
`tools/SimulationBotClient/Mmorpg.Tools.SimulationBotClient.csproj`, and the
Unity package `com.revenantx.litenetlib`. `Verify-DependencyPolicy.ps1` fails
when they differ, and Dependabot ignores LiteNetLib, so all three are upgraded
together by hand.

A Dependabot NuGet pull request rewrites only the `packages.lock.json` of the
projects that reference the package directly. Every project that reaches the
package through a `ProjectReference`, such as the test projects, keeps its old
lock file, so CI fails at the locked restore with `NU1004`. Refresh all lock
files on the pull request branch before review, then run the backend gate:

```powershell
gh pr checkout <number>
dotnet restore Mmorpg.slnx --force-evaluate
dotnet restore Mmorpg.slnx --locked-mode
git add -- '*packages.lock.json'
git commit -m "Refresh NuGet lock files"
git push
```

Expected result: the locked restore succeeds and CI turns green on the pushed
commit. Dependabot stops rebasing a pull request once it carries a commit of
its own, so comment `@dependabot recreate` and refresh again if the pull request
falls behind `development`. Dependabot refreshes an open pull request only in
the directories it was created for, so after a project moves it cannot update
older pull requests. Close them and let the next monthly run, or a manual check
under `Insights > Dependency graph > Dependabot`, propose them again.

## Pull Request Workflow

Every task reaches `development` through a pull request from its own branch,
as `AGENTS.md` describes under Branches and Task Workflow. Two scripts carry a
pull request from opened to merged and cleaned up without anyone watching it.
Both need the GitHub CLI signed in and run in Windows PowerShell 5.1.

Wait until the pull request is ready or needs work, from the checkout that has
its branch checked out:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Wait-PullRequest.ps1 -Number <number>
```

The script polls GitHub every 30 seconds and returns as soon as there is
something to act on. Ready means that GitHub reports the expected commit as the
pull request head (the local `HEAD` when the branch is checked out, or
`-HeadCommit`), every check passed, including every check that
`.github/rulesets/development-ci-gate.json` requires, the Codex review bot
(`chatgpt-codex-connector[bot]`) finished a review of the head commit, no
review thread is unresolved, and GitHub reports the pull request mergeable. The
Codex state is read from the bot's summary comment. The bot reviews by itself
only when a pull request opens, so for a later head the script posts an
`@codex review` comment, marked with the head commit in an HTML comment. It
gives the bot 20 minutes (`-CodexWaitMinutes`) after the pull request opens and
after each request, and asks at most twice per head. The bot's
`has_unresolved_findings` flag stays true after its threads are resolved, so
the review threads decide. `-NoWait` evaluates once and never posts.

| Exit code | Meaning | Next step |
| --- | --- | --- |
| 0 | Ready to merge | Run `Complete-PullRequest.ps1` |
| 1 | A check failed; the report links each failed run | Read the log with `gh run view <run-id> --log-failed`, fix, push, wait again |
| 2 | Unresolved review threads; the report links each one | Fix every valid finding, reply, resolve the thread, push, wait again |
| 3 | Merge conflict with `development` | Merge `origin/development` into the branch, resolve, push, wait again |
| 4 | The pull request is not open | Nothing to wait for |
| 5 | Timed out after 120 minutes (`-TimeoutMinutes`) | Check the self-hosted Unity runner, then wait again |
| 6 | Not ready yet (only with `-NoWait`) | Wait without `-NoWait` |
| 7 | GitHub could not be read five times in a row | Check `gh auth status` and the network |
| 8 | Everything else is ready, but the Codex bot did not review the head after two requests | Merge with `-AllowMissingCodexReview` and say in the task summary that the head has no Codex review |

Merge the ready pull request and clean up:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Complete-PullRequest.ps1 -Number <number> -Subject "Merge the <what the change does>"
```

The script reads the pull request head once, checks readiness for exactly that
commit, and merges into `development` with a merge commit only while the head
is still that commit, so a push in between is never merged unchecked. The body
defaults to `<pull request title> (#<number>).`. `-AllowMissingCodexReview`
accepts exit code 8 of the readiness check. After the merge the script deletes
the branch on GitHub, but only while it still points to the merged head, and
locally: when the current checkout has the branch checked out and no
uncommitted changes, the main checkout switches to `development` and a task
worktree detaches first. It then deletes every other local branch that is fully
merged into `origin/development` and checked out in no worktree, fast-forwards
the local `development` branch wherever it is checked out, prunes stale
worktree records, and lists the remaining worktrees. For a pull request that is
already merged, the script skips the merge and only runs the cleanup, so
running it again finishes a cleanup that failed halfway, including returning a
detached main checkout to `development`.

Exit code 0 means merged and cleaned up, 1 means nothing was merged, and 2
means merged but the cleanup is incomplete: the remote branch moved after the
merge, the main checkout could not return to `development` and was left
detached, the task branch had to stay (uncommitted changes in the current
checkout, another worktree has it checked out, or it has commits that are not
in `development`), or the local `development` does not match
`origin/development` afterwards, usually because uncommitted changes in the
main checkout block the fast-forward or local commits sit on `development`.
The report says which. Never discard those changes to get past exit code 2.
Because the cleanup deletes the head branch on `origin`, the script refuses
pull requests whose head is `main` or `development`, comes from a fork, or has
a branch name with characters other than letters, digits, `.`, `_`, `/`, and
`-`.

Expected result: the pull request shows as merged with a merge commit,
`git branch -a` lists only `main`, `development`, and branches that other work
still has checked out, and `development` in the main checkout matches
`origin/development`.

## Unity Editor Automation With Unity CLI

The Unity project includes the Editor automation package `com.unity.pipeline`,
pinned to the experimental version `0.8.0-exp.1`. Together with the Unity CLI it
lets local tools and coding agents such as Claude Code drive a running Editor:
read and change scenes, GameObjects, components, and assets, read the console
and compiler state, evaluate C# in the Editor, enter Play Mode, and run tests.
It replaces the deprecated MCP server in the Unity AI Assistant package and
does not need a Unity AI subscription.

Security rules:

- The Editor server binds only to `127.0.0.1`. Every request needs the bearer
  token from a per-user descriptor file in the ignored `Library/` folder, and
  requests with an `Origin` header are refused, so browser pages cannot reach
  it.
- `eval` runs arbitrary C# with full Editor access, and some Editor changes
  cannot be undone. Commit or stash work before an agent makes larger Editor
  changes.
- The package runtime assembly compiles only in the Editor and in builds that
  define `ENABLE_PROFILER` (development builds) or `ENABLE_RUNTIME_PIPELINE`.
  Its Player server stays off because the Pipeline Runtime setting
  `enableInBuilds` is `false`. Do not enable `enableInBuilds`,
  `Allow Browser Clients`, or `ENABLE_RUNTIME_PIPELINE` without a reviewed
  reason.

One-time setup on a new machine:

1. Install the Unity CLI with `winget install Unity.CLI` and open a new
   terminal. Unity Hub may already have installed it in
   `%LOCALAPPDATA%\Unity\bin`.
2. Sign in with `unity auth login`.
3. Open the project with `unity open src/Client`, or through Unity Hub, and wait
   for package import and script compilation to finish. If the Editor was
   already open when the manifest changed, focus the Editor window so that
   Package Manager resolves the change. If Unity Hub lists a project path that
   no longer exists, remove that entry with `Remove from list` and add the
   project with `Add > Add project from disk`, or run
   `unity projects add src/Client` from the repository root.

No other manual Editor work is needed. The package is already in the manifest,
so `unity pipeline install` is only needed for a project that does not have it
yet.

Verify the connection from `src/Client`:

```powershell
unity status
unity pipeline list
unity command editor_status
```

Expected result:

- `unity status` lists the project as `ready` on a loopback port such as `7800`.
  It sends every running Editor a command that the Pipeline package does not
  know, and the error that logs fails any Unity test running in that Editor,
  including the [Unity CI Runner](#unity-ci-runner)'s on this machine. Use it
  only for this setup check; afterwards ask one Editor with
  `unity command editor_status --project-path <path>`.
- `unity pipeline list` reports Pipeline `true`, version `0.8.0-exp.1`, and
  Server Reachable `true`.
- `editor_status` returns `"status":"ready"`.

Useful commands, run from `src/Client` or with `--project-path`:

- `unity list` lists every command the connected Editor exposes.
- `unity command console_status` reports the compiler failure state and the
  console counts. `unity command console --level warning` returns the entries.
- `unity command run_tests --mode editor --async_tests` starts the EditMode
  suite, `--mode playmode` the PlayMode suite, and `unity command test_status`
  reports the run. `tools/Run-UnityEditorTests.ps1` wraps them; use the script
  instead of polling by hand.

Coding agents:

- Unity's official Claude Code plugin, `unity@unity-agent-plugin`, adds Unity
  skills that call the CLI. In the Claude desktop app, open `Plugins > Browse`
  and search for Unity. In a Claude Code terminal, run
  `/plugin marketplace add Unity-Technologies/unity-agent-plugin` and then
  `/plugin install unity@unity-agent-plugin`.
- Agents that can run shell commands should call `unity` directly. It is faster
  and uses fewer tokens than MCP.
- MCP clients that cannot run shell commands can use the CLI MCP server through
  `unity mcp configure <client>`. `unity mcp configure --list` shows the
  supported clients. This repository configures no MCP client.

The package is experimental, so review its changelog before an update, update
it in a focused branch with `unity pipeline install --package-version
<version>`, let Unity rewrite `packages-lock.json`, and run both Unity suites
and the backend gate.

### Run The Unity Tests

In the running Editor:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Run-UnityEditorTests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Run-UnityEditorTests.ps1 -Mode EditMode -Filter InventoryStoragePredictionTests
```

The script talks to the Editor that has its own checkout's `src/Client` open,
so a worktree's script never reaches the Editor of the main checkout;
`-ProjectPath` chooses another project. It leaves Play Mode, refreshes assets so
that changed files compile, and stops at once with the compiler errors when the
scripts do not compile. Then it starts each run asynchronously and polls its
status every two seconds, across the domain reloads of Play Mode, until the run
completes. Every step has a limit of 15 minutes (`-TimeoutMinutes`), after which
the script reports the last Editor state and cancels a running test run, so it
never waits without a report. `-Filter` takes substrings of test full names,
such as fixture names, separated by commas, and each filter runs as its own
run; `-FilterType Category` or `Assembly` matches an exact category or part of
an assembly name instead. A filtered run takes a few seconds once the scripts
are compiled, the whole EditMode suite about 70 seconds, and the PlayMode suite
about 30 seconds.

| Exit code | Meaning |
| --- | --- |
| 0 | Every selected test passed |
| 1 | A test failed, was skipped, or was inconclusive, or a suite has no tests; the report lists each test with its message |
| 2 | The scripts do not compile; the report lists the compiler errors |
| 3 | No running Editor has the project open; open it with `unity open <path>`, or run `tools/Run-UnityTests.ps1` headless |
| 4 | The Editor did not respond or a run timed out, usually because a modal dialog blocks the Editor |
| 5 | A filter selected no test in any selected mode |

Headless, as CI runs them, while no Editor has the project open:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1
```

The script runs the Editor version that `ProjectVersion.txt` pins. An explicit
`-UnityEditorPath` or `UNITY_EDITOR_PATH` runs that Editor. The Unity CLI runs
the tests only when `-UnityCliPath` or `UNITY_CLI_PATH` is set, or when neither
path is set and the CLI exists at `%LOCALAPPDATA%\Unity\bin\unity.exe`;
otherwise the script starts the Editor from the default Unity Hub path. Unity
runs without a graphics device there, so the tests of the `Graphics` category,
which draw, run only in the Editor.

Expected result: fresh EditMode and PlayMode result files and Unity logs below
`TestResults/Unity`, and both suites reported as passed. As in the backend
gate, a skipped or inconclusive test, or a suite without tests, fails the run.
`Window > General > Test Runner` in the Editor runs the same suites by hand.

## Upgrade The Unity Editor

`src/Client/ProjectSettings/ProjectVersion.txt` pins one Editor version for the
project, the CI runner, and the documentation. Upgrade it in a focused branch:

1. Install the new Editor with Unity Hub in the default path on every machine
   that opens the project, including the CI runner machine.
2. In a worktree of the branch, let the new Editor upgrade the project once in
   batch mode, so no dialog stops it. Replace the version and the paths:

   ```powershell
   & "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" -batchmode -projectPath <worktree>\src\Client -quit -logFile <scratch>\upgrade.log
   ```

   Unity rewrites `ProjectVersion.txt`, `manifest.json`, `packages-lock.json`,
   and the import settings and assets whose serialized format changed. Commit
   these files as Unity wrote them.
3. Read the log for `error CS`, `warning`, and import errors such as
   `Rig Error`. Project code compiles with warnings as errors, as
   [Assemblies](UNITY_CLIENT_ARCHITECTURE.md#assemblies) describes, so an API
   that the new version deprecates fails as `error CS0618`; fix every one in the
   same branch. Never print the head of a Unity log: its command line holds the
   Hub access token.
4. Open the worktree with `unity open <worktree>\src\Client` and run both suites
   with `tools/Run-UnityEditorTests.ps1`.
5. Replace the old version everywhere it is written. `git grep -n
   "<old version>"` lists `README.md`, this document,
   `docs/UNITY_CLIENT_ARCHITECTURE.md`, and the runner label in
   `.github/workflows/ci.yml`. Add the new label to the runner as
   [Operate The Runner](#operate-the-runner) describes.
6. After the merge, close the old Editor before pulling `development` into a
   checkout, because an old Editor that sees the new packages fails to compile
   them. Then open `src/Client` in the new Editor, which imports the `Library`
   folder again once.

Expected result: the Unity quality gate passes on the new label, and every
checkout opens in the new Editor without compile errors or warnings.

## Unity CI Runner

The `Unity quality gate` job in `.github/workflows/ci.yml` runs both Unity
suites through `tools/Run-UnityTests.ps1` on a self-hosted Windows runner.
GitHub-hosted runners cannot activate a Unity Personal license, so the runner
runs on the development machine under the developer's own Windows account. It
reuses the Unity installation, the Unity CLI sign-in, and the Personal license
that a local headless run uses, so the Unity gate runs only while that account
is signed in. A locked screen is fine. A queued job waits until the runner is
online, and GitHub cancels it after 24 hours.

The scheduled task `GitHub Actions Unity runner`, registered by
`tools/ci/Register-UnityRunnerTask.ps1`, keeps the runner running:

- It starts `C:\actions-runner\run.cmd` at sign-in, after the machine wakes
  from sleep, and every five minutes when the runner is not running. A runner
  that exits for any reason, such as a crash, a killed process, or a network
  failure that ends the listener, comes back on its own within a few minutes.
- Only one runner runs at a time. It has no window that could be closed by
  mistake or paused by a click, and it runs at normal priority instead of the
  Task Scheduler default below normal.
- The Task Scheduler service starts it, so it belongs to no terminal or app. A
  runner started from a terminal of an app, for example a Claude Code session,
  stops when that app closes or updates, and the Unity gate then waits. Never
  start `run.cmd` by hand; use the task.

The job runs for pull requests, pushes to `development` and `main`, and manual
runs from `Actions > CI > Run workflow`. Pull requests from forks skip it.
While the repository variable `UNITY_CI_ENABLED` is not `true`, the job shows
as skipped. If Unity is not installed in the default Unity Hub path, the
repository variable `UNITY_EDITOR_PATH` can name the full path to `Unity.exe`.

The runner checks the repository out into its own work folder,
`C:\actions-runner\_work\shooter-mmo\shooter-mmo`. That is a separate Unity
project with its own `Library`, so a job can run while an Editor has the main
checkout or a worktree open. Every job starts from a clean checkout and imports
the project again; both suites together take about four minutes.

Security rules, because the repository is public:

- The runner's job-started hook, `tools/ci/Assert-TrustedRunnerJob.ps1`, fails
  every job before its first step unless it is a push, a manual run, or a pull
  request from a branch of `DrottPoker/shooter-mmo`. A condition in the
  workflow cannot protect the runner on its own, because a pull request from a
  fork runs its own copy of the workflow file. Never widen the hook to other
  events such as `pull_request_target`.
- `Settings > Actions > General` requires approval for all external
  contributors, so no workflow of a fork pull request runs before a maintainer
  approves it. Read the workflow changes of such a pull request before
  approving it.
- Jobs run with the rights of the Windows account that runs the runner. Only
  code from branches of this repository reaches it.
- Keep automatic runner updates on. GitHub stops sending jobs to a runner that
  falls more than 30 days behind the latest runner release.

The ruleset `development CI gate`, stored in
`.github/rulesets/development-ci-gate.json`, requires the jobs `Backend checks
and unit tests`, `Integration tests (items)`, `Integration tests (other)`,
`Observability configuration`, and `Unity quality gate` to pass before a commit
reaches `development`. A skipped job counts as passed. When a job name changes,
update the JSON file and the live ruleset together:

```powershell
$rulesetId = ((gh api repos/DrottPoker/shooter-mmo/rulesets | Out-String | ConvertFrom-Json) | Where-Object { $_.name -eq "development CI gate" }).id
gh api -X PUT "repos/DrottPoker/shooter-mmo/rulesets/$rulesetId" --input .github/rulesets/development-ci-gate.json
```

When a merge cannot wait for the runner, a repository admin can set the
ruleset to `Disabled` under `Settings > Rules > Rulesets` and back to `Active`
afterwards.

### Set Up The Runner

Requirements on the machine:

- Unity `6000.6.7f1` in the default Unity Hub path, and the Unity CLI signed
  in: `unity license status` reports `License: active` and `Signed in: yes`.
- Git for Windows and the GitHub CLI, signed in with admin access to the
  repository.
- An execution policy that runs local scripts: `Get-ExecutionPolicy` reports
  `RemoteSigned` or `Unrestricted`. Windows PowerShell 5.1 is enough; the job
  does not need PowerShell 7.

Run these commands once from the repository root in a normal PowerShell window,
not as administrator. Replace the version with the latest release from
`https://github.com/actions/runner/releases`.

```powershell
# Download the runner and check it against the checksum in its release notes.
$version = "2.338.0"
$zip = "C:\actions-runner\actions-runner-win-x64-$version.zip"
New-Item -ItemType Directory -Force C:\actions-runner, C:\actions-runner-hooks | Out-Null
gh release download "v$version" --repo actions/runner --pattern "actions-runner-win-x64-$version.zip" --dir C:\actions-runner --clobber
$notes = gh release view "v$version" --repo actions/runner --json body --jq .body
$expected = [regex]::Match($notes, '<!-- BEGIN SHA win-x64 -->([0-9a-f]{64})<!-- END SHA win-x64 -->').Groups[1].Value
if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected) { throw "The runner download does not match its checksum." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, "C:\actions-runner")
Remove-Item $zip

# Register the runner. The registration token is valid for one hour and is never printed.
$token = gh api -X POST repos/DrottPoker/shooter-mmo/actions/runners/registration-token --jq .token
& C:\actions-runner\config.cmd --unattended --replace --url https://github.com/DrottPoker/shooter-mmo --token $token --name "$env:COMPUTERNAME-unity" --labels unity-6000.6.7f1 --work _work
Remove-Variable token

# Install the job-started hook outside the runner folder.
Copy-Item tools\ci\Assert-TrustedRunnerJob.ps1 C:\actions-runner-hooks\ -Force
Set-Content C:\actions-runner\.env "ACTIONS_RUNNER_HOOK_JOB_STARTED=C:\actions-runner-hooks\Assert-TrustedRunnerJob.ps1" -Encoding Ascii

# Register the scheduled task that keeps the runner running, and start it.
& .\tools\ci\Register-UnityRunnerTask.ps1

# Enable the job, require approval for fork workflows, and require green CI for development.
gh variable set UNITY_CI_ENABLED --body true --repo DrottPoker/shooter-mmo
gh api -X PUT repos/DrottPoker/shooter-mmo/actions/permissions/fork-pr-contributor-approval -f approval_policy=all_external_contributors
gh api -X POST repos/DrottPoker/shooter-mmo/rulesets --input .github/rulesets/development-ci-gate.json
```

Verify the runner:

```powershell
Get-ScheduledTask -TaskName "GitHub Actions Unity runner"
gh api repos/DrottPoker/shooter-mmo/actions/runners --jq '.runners[] | {id, name, status, labels: [.labels[].name]}'
gh workflow run ci.yml --repo DrottPoker/shooter-mmo --ref development
```

Expected result:

- The task state is `Running`.
- The runner is listed with name `<COMPUTERNAME>-unity`, status `online`, and
  labels `self-hosted`, `Windows`, `X64`, and `unity-6000.6.7f1`.
- The manual CI run passes the `Unity quality gate` job. Its `Set up runner`
  step reports `This self-hosted runner accepted the workflow_dispatch job`,
  the job summary lists both suites as passed, and the run uploads the XML
  results and Unity logs as `unity-test-results`.

To check that the runner recovers on its own, stop its listener while no job
runs, as a crash would:

```powershell
Stop-Process -Name Runner.Listener -Force
```

Expected result: the task state changes to `Ready`. At the next five-minute
check it is `Running` again. Within a few more minutes the newest
`Runner_*.log` in `C:\actions-runner\_diag` contains `Listening for Jobs`, and
GitHub lists the runner as `online`. The whole recovery takes at most about ten
minutes.

### Operate The Runner

- Status: `Settings > Actions > Runners` on GitHub, or the `gh api` command
  above. On the machine,
  `Get-ScheduledTask -TaskName "GitHub Actions Unity runner"` reports
  `Running` while the runner runs, and `Get-ScheduledTaskInfo` on the same task
  shows the last start. Runner logs are in `C:\actions-runner\_diag`, one
  `Runner_*.log` per start.
- Start it now instead of waiting for the next check:
  `Start-ScheduledTask -TaskName "GitHub Actions Unity runner"`.
- Restart it, for example after a change to `C:\actions-runner\.env`, with
  `Stop-ScheduledTask` and then `Start-ScheduledTask` on the same task.
  Stopping cancels a running job. After a hard stop GitHub keeps the old
  session for a few minutes, so the new listener logs
  `A session for this runner already exists` and retries until it connects.
- Keep it stopped with `Disable-ScheduledTask` and then `Stop-ScheduledTask`
  on the task. `Enable-ScheduledTask` and `Start-ScheduledTask` undo that.
- After a change to `tools/ci/Register-UnityRunnerTask.ps1`, run it again. It
  updates the task and leaves a running runner alone; the change applies at the
  next start.
- A job that fails with Unity exit code `198`, `Access token is unavailable`,
  or `No valid Unity Editor license found` lost the Unity sign-in. Fix it as
  [Unity License Errors](#unity-license-errors) describes and rerun the job.
- After a change to `tools/ci/Assert-TrustedRunnerJob.ps1`, copy it to
  `C:\actions-runner-hooks` again. The runner runs that copy, not the
  repository file.
- After a Unity Editor upgrade, install the new Editor on the runner machine
  and change the label in the workflow in the upgrade pull request. Before
  that pull request runs CI, add the new label to the runner, so that it takes
  jobs for both versions while other branches still name the old one. After
  the merge, keep only the new label. The runner id is in the runner listing
  above, and the steps for the project itself are in
  [Upgrade The Unity Editor](#upgrade-the-unity-editor):

  ```powershell
  # Before the upgrade pull request runs CI: add the new label.
  gh api -X POST "repos/DrottPoker/shooter-mmo/actions/runners/<id>/labels" -f "labels[]=unity-<version>"
  # After the merge: keep only the new label.
  gh api -X PUT "repos/DrottPoker/shooter-mmo/actions/runners/<id>/labels" -f "labels[]=unity-<version>"
  ```

- To remove the runner, set `UNITY_CI_ENABLED` to `false`, stop and unregister
  the task, unregister the runner, and delete its folders:

  ```powershell
  gh variable set UNITY_CI_ENABLED --body false --repo DrottPoker/shooter-mmo
  Stop-ScheduledTask -TaskName "GitHub Actions Unity runner"
  Unregister-ScheduledTask -TaskName "GitHub Actions Unity runner" -Confirm:$false
  & C:\actions-runner\config.cmd remove --token (gh api -X POST repos/DrottPoker/shooter-mmo/actions/runners/remove-token --jq .token)
  ```

  Then delete `C:\actions-runner` and `C:\actions-runner-hooks`.

## Operations

### Reset The Local Development Database

GameService refuses to start when a catalog change would invalidate live item
state, and the error names the affected definition, equipment slot, tier, or
character setting. On a local development database the alternative to an
explicit data migration is a reset. It deletes every local account, character,
item, corpse, and Recovery delivery.

1. Stop GameService and every SimulationWorker.
2. From the main repository checkout, not a worktree, remove the PostgreSQL
   volume and start the containers again. Compose names the volume after the
   checkout folder, so a worktree would not reach it. Redis keeps no data.

   ```powershell
   docker compose down -v
   docker compose up -d --wait
   ```

3. Start GameService.

Expected result: GameService applies every migration, seeds the current item
catalog, and reports ready. Create a new account and character in the client.

### Rebind An Offline Shard To Another World

A shard can change its World only between worker runtimes, never while it
runs. Prepare all target World content before changing a shard:

- Add `content/worlds/<WorldId>/world.json` with the stable identity, client
  scene, bounds, and spawn. Item services such as Bank and Recovery Storage
  come from NPC capabilities in the actor spawns.
- Provide `content/worlds/<WorldId>/collision.json` and its compiled collision
  data below `src/Shared/WorldData/Generated/WorldCollision/<WorldId>`.
- Provide `content/worlds/<WorldId>/actor-spawns.json` with at least one spawn
  and its compiled
  `src/Shared/WorldData/Generated/Worlds/<WorldId>/world-actors.json`. The
  SimulationWorker loads runtime actor data for every World, and both World
  compilers reject a World folder that lacks `world.json`,
  `actor-spawns.json`, or `collision.json`. Shared actor definitions stay below
  `content/actors`.
- Map the authored Unity scene's `WorldId` in
  `Assets/Content/Worlds/world-scene-catalog.json`, then run `MMORPG > Tools >
  Addressables > Apply Client Layout` so the scene becomes an Addressables
  World scene. Do not add it to Build Profiles.

Perform the rebind in this order:

1. Stop the shard's SimulationWorker cleanly with Ctrl+C and wait for its
   graceful offline registration to complete.
2. Confirm that players have left, and let all open durable corpses of that
   shard expire or close through their normal lifecycle. Do not rewrite
   World-local coordinates in PostgreSQL by hand.
3. Change only `SimulationWorker:WorldId` in
   `src/Server/SimulationWorker/Config/appsettings.json`, and verify that the
   selected World folder contains its manifest, collision, and actor runtime
   data.
4. Start SimulationWorker. Its first authenticated heartbeat proposes the
   configured World. GameService locks the shard, verifies that the World
   exists in the database, checks the offline rebind blockers, and persists the
   new binding with the worker assignment in one transaction.
5. If the worker stops after GameService returns `shard_world_rebind_blocked`,
   leave it stopped. The error lists active assignments, pending join tickets,
   active simulation sessions, and open corpses. Drain the reported state and
   start the worker again.
6. If GameService returns `simulation_world_not_found`, correct the World
   manifest or the configured `WorldId`, restart GameService so it reconciles
   the World manifests, and then start the worker again.
7. Join through CharacterSelect. Unity resolves the accepted `WorldId` through
   its client catalog before it requests a ticket, checks the exact World that
   placement returns, and loads the mapped scene only after the realtime join
   succeeds.

Expected result: no player can enter during the rebind, stale worker content
cannot claim the shard, and the next worker runtime and the Unity client use the
same World identity. Both development Worlds have complete manifest, scene,
collision, actor, and client catalog baselines, and both scenes are
Addressables World scenes; [Development Worlds](DEVELOPMENT_WORLDS.md)
describes them and the checks to repeat after changing one.

## Troubleshooting

### LiteNetLib Package Signature Warning

Unity Package Manager shows a yellow missing-signature warning for LiteNetLib
because the package comes from the scoped OpenUPM registry and is not signed by
Unity. It is not a compilation error, and no Unity Editor action is needed. The
project pins the exact version in `Packages/manifest.json` and
`Packages/packages-lock.json`. Do not select an offered package update without
reviewing the upstream changelog and source changes, and keep LiteNetLib
aligned as [Dependency Updates](#dependency-updates) requires. Before a
production release, review the resolved package source and license, and
consider vendoring the reviewed source or pinning an immutable reviewed
upstream revision if the release threat model needs stronger supply-chain
control.

### Unity License Errors

A headless Unity run needs an active Unity Editor entitlement for the exact
installed Editor version. When `tools/Run-UnityTests.ps1` exits with code
`198`, or reports `Access token is unavailable` or `No valid Unity Editor
license found`, the license check failed before any test started, so it is not
a test failure. Restore the license:

1. Exit every Unity Editor process and quit Unity Hub from its notification-area
   icon.
2. Start Unity Hub, sign in, and confirm that a valid Personal or organization
   license is active under `Preferences > Licenses`. Where the Unity CLI is
   used, `unity auth login` signs it in again and `unity license status`
   reports `License: active`.
3. Open this project with the pinned Editor once and wait for package import
   and script compilation to finish without a licensing error.
4. Close the Editor, keep the signed-in Hub session, and run
   `tools/Run-UnityTests.ps1` again.

Expected result: the script writes fresh EditMode and PlayMode result files
below `TestResults/Unity` and reports both suites as passed.

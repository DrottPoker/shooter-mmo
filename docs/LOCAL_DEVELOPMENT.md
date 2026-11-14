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
  actor catalog revision and the World's spawn revision, the realtime transport
  with its protocol version, 30
  ticks and 15 snapshots per second, and `Activated <n> world actors`.
- In the Development environment it warns that the development Bank and
  Recovery Storage access and the development admin tools are enabled, as
  [Development Worker Settings](MANUAL_TESTING.md#development-worker-settings)
  describes.
- `GET http://localhost:5000/api/shards` reports `local-shard-1` online.
- Every 30 seconds the worker logs its network counters and a status line,
  which [Monitoring](MONITORING.md#logs) describes.

A character whose player leaves or loses the connection stays in the world for
`SimulationWorker:LingeringBodySeconds`, 20 seconds by default, as
[Lingering Bodies](SERVICE_FEATURES.md#lingering-bodies) describes. Until it is
gone, the account can enter only that character again. To watch a body longer,
or to wait less while testing, pass another time to one run; the next run
without the option uses the default again:

```powershell
dotnet run --project src/Server/SimulationWorker -- --SimulationWorker:LingeringBodySeconds=60
```

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
  manifests, the actor catalog, the World spawns and collision chunks, the
  compiled item catalog,
  the combat rules, the character appearance content, and the development item
  packages. [Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults)
  describes the options that point a deployment at other copies.
- Startup validates all of it and stops before any player is admitted when
  something is wrong.
- After a content change, compile it as
  [Game Content](../content/README.md#compile-and-verify) describes, then
  restart the services that read it; `dotnet run` builds them first. Restart
  both after an item or quest catalog change: GameService places characters
  only on a worker whose item and quest catalog revisions equal the catalogs it
  mirrors, and otherwise a join fails with
  `simulation_worker_item_catalog_mismatch` or
  `simulation_worker_quest_catalog_mismatch`. A worker that kept running across
  a quest catalog change ends each session at its next quest action with
  `simulation_worker_quest_catalog_mismatch`. A catalog change
  that would invalidate live item state stops GameService; see
  [Reset The Local Development Database](#reset-the-local-development-database).
- A worker stops with `shard_rule_set_unknown` when the rule set that
  GameService names for its shard is not in `content/combat/combat-rules.json`.
- GameService also serves the compiled item catalog, quest catalog, and the
  text tables of `src/Shared/WorldData/Generated/Text/en` from its output at
  `/api/content`, as [Content Files](SERVICE_FEATURES.md#content-files)
  describes. After a text change, compile the text tables and restart
  GameService to serve them; it refuses to start when a table is invalid.
- A Unity client fetches the item catalog, the quest catalog, and the text
  tables from GameService at its next start, so a change of them needs no
  client build: compile, restart the services, restart the client. A client
  that runs across the change reports `item_catalog_update_required` or
  `quest_catalog_update_required` and asks for a restart. What still needs a
  client build is in [What A Change Needs](../content/README.md#what-a-change-needs).
  In the Editor the bundled files equal the served ones, so Play Mode takes
  them from the build; `MMORPG > Tools > Ignore Bundled Content` makes it fetch
  every file, as
  [Content Resolution At Start](UNITY_CLIENT_ARCHITECTURE.md#content-resolution-at-start)
  describes.

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

- The Editor always talks to the GameService of the `local` environment,
  `src/Client/Environments/local.json`, which points to
  `http://localhost:5000`; a player build talks to the environment it was built
  for, as [Build The Client](#build-the-client) describes. The login panel
  cannot change it. The timeouts and how long the client tries to restore a
  lost connection come from `Assets/Content/Config/ClientConfig.asset`;
  [Configuration](UNITY_CLIENT_ARCHITECTURE.md#configuration) describes both.
- After a change to the realtime protocol, the shared simulation, or the
  transport, exit Play Mode, restart SimulationWorker, and rebuild every
  player, so that every process runs the same code. A worker rejects a client
  of another protocol version at the join.
- Player builds go to the ignored `Builds` folder below `src/Client`, as
  [Build The Client](#build-the-client) describes, and are never committed.
- Run the Unity tests as [Run The Unity Tests](#run-the-unity-tests) describes.

### Build The Client

`tools/Build-Client.ps1` builds the Windows x64 player headless with IL2CPP,
for one environment, through the Editor build method
`Mmorpg.Client.Editor.ClientPlayerBuild.BuildFromCommandLine`. Close the
Editor of the checkout first: Unity opens a project only once, and the script
refuses to run while the project is open.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Build-Client.ps1 -Environment local -Development -StartCheck
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Build-Client.ps1 -Environment local -StartCheck
```

| Parameter | Meaning |
| --- | --- |
| `-Environment <name>` | The environment file `src/Client/Environments/<name>.json` that the build bakes in |
| `-Development` | A development build; without it the build is a release build |
| `-StartCheck` | Starts the built player once after the build, as [The Start Check](#the-start-check) describes |
| `-OverrideAddress <url>` | With `-StartCheck`: starts the player with `-gameServiceBaseUrl <url>` and checks that a development build uses that address and a release build ignores it |
| `-Plan` | Prints the environment, the build kind with its settings, the output, and the checks, and runs nothing |
| `-TimeoutMinutes <n>` | Ends Unity after this long, 60 by default |

The player goes to `src/Client/Builds/<environment>-<development|release>/`,
which the script empties first, with `MMORPG.exe`. Beside that folder it
writes `<build>.build-summary.txt`, the summary it prints: the result, the
environment, the build kind, the commit and whether the working tree had
changes, the build time, the scripting backend, the stripping level, the
IL2CPP code generation and C++ compiler configuration, the time, the shipped
size and the size of the debug data that IL2CPP and Burst write beside the
player, and the build errors and warnings and the C++ warnings of IL2CPP by
count. `<build>.build-report.json` and `<build>.unity-build.log` keep the
details. Never share or print the head of the Unity log.

A Unity run rewrites some tracked files of its own, such as the player
settings that a development build changes for its duration and a hash in the
Addressables settings. The script puts back every such file below
`src/Client/ProjectSettings`, `Packages`, `Assets/AddressableAssetsData`, and
`Assets/Settings` and names it, and it fails with exit code 3 when `git status`
is not what it was before the build; it never touches any other file. A file
there that had local changes before the run gets those changes back, and one
that was deleted before the run stays deleted. `tools/ci/Test-UnityCommon.ps1`
checks each of these cases in a temporary Git repository.

| Exit code | Meaning |
| --- | --- |
| 0 | The player was built and, with `-StartCheck`, passed the start check |
| 1 | The build or the start check failed; the summary and the end of the Unity log say why |
| 2 | The build could not run: an unknown environment, no Unity, or an open project |
| 3 | The build left the working tree changed |

Expected result: the summary with `Result: Succeeded`, `Scripting backend:
IL2CPP`, `Build errors: 0`, and `C++ errors: 0`, then `Built <build> into
...`, exit code 0, and `git status` as before. The first IL2CPP build of a
Library takes the longest; the measured times and sizes are in
[Build Kinds](#build-kinds).

#### Environments

An environment is a checked-in file in `src/Client/Environments`, named after
the environment, and owns the GameService address of a client:

```json
{
  "name": "local",
  "gameServiceBaseUrl": "http://localhost:5000"
}
```

`name` is lowercase letters, digits, and hyphens and equals the file name, and
`gameServiceBaseUrl` is an absolute http or https URL. `local` is the stack of
[Backend Services](#backend-services) on this machine. To add an environment,
add its file and build with `-Environment <name>`; `ClientEnvironmentTests`
checks every file. The Editor always runs `local`, without a build step; a
player runs the one environment its build baked in, with the build stamp, as
[Environment And Build Stamp](UNITY_CLIENT_ARCHITECTURE.md#environment-and-build-stamp)
describes. A build that this script does not start, from `File > Build
Profiles` or a test player, bakes `local`.

#### Another GameService For Development

A development player and the Editor can talk to another GameService than their
environment's, such as one on another machine; a release build ignores every
such override:

- Development player: start it with `-gameServiceBaseUrl <url>`, for example
  `src\Client\Builds\local-development\MMORPG.exe -gameServiceBaseUrl http://192.168.1.20:5000`.
- Editor: `MMORPG > Tools > GameService Address Override` takes the address,
  which Play Mode uses from its next start; `Use The Local Environment` ends
  the override.

Expected result: the login screen's footer says that the address was
overridden at start, the connection line shows the new address, and the client
log has the line `The GameService address is overridden at start: <url>.` A
release build started with the argument logs `A release build ignores the
GameService override <url>.` and keeps its environment's address.

#### Build Kinds

The project settings hold the release kind's values, so a build from `File >
Build Profiles` and a test player of
[Run The Unity Tests](#run-the-unity-tests) are compiled like a release build.
A development build applies its own values for the build, and the script puts
the project settings back afterwards. Why each value was chosen is in
`ClientBuildKindSettings`.

| | Development | Release |
| --- | --- | --- |
| Scripting backend | IL2CPP | IL2CPP |
| Managed stripping | Low | Low |
| IL2CPP code generation | Faster (smaller) builds, `OptimizeSize` | Faster runtime, `OptimizeSpeed` |
| C++ compiler configuration | Release | Master |
| Development build | Yes | No |
| Admin menu, address override, prefilled login | Yes | No |
| Time of a build | 5 min the first time, 1 min the second | 15 min, one build measured |
| Shipped size | 199 MB | 160 MB |
| IL2CPP and Burst debug data beside the player, not shipped | 484 MB | 1,071 MB |

Measured on 2026-11-14 on the reference machine of
[Measured Tiers](STACK_STRESS_TESTING.md#measured-tiers), in a Library that
had imported the project and built the test player of
[Run The Unity Tests](#run-the-unity-tests) once. The release build spent 10 of
its 15 minutes in the C++ link with the whole-program optimization of the
Master configuration. A Library
without any IL2CPP build takes longer the first time. What a release build
leaves out is in
[What A Release Build Leaves Out](UNITY_CLIENT_ARCHITECTURE.md#what-a-release-build-leaves-out).

#### The Start Check

`-StartCheck` starts the built player once in a 1280 by 720 window with
`-logFile <build>.start-check.log`, waits up to two minutes until the log shows
`The content check resolved` and `Login screen shown`, closes it, and fails on
any exception in the log. It prints the client's log lines, among them the
environment, the build stamp, and the login footer.

It proves that the player starts under IL2CPP and stripping: the bootstrap,
Addressables with the configuration, the bundled content and the baked
environment, the text tables through `JsonUtility`, the content check against
the environment's GameService, and the login screen with its TextMesh Pro
texts. It does not prove that the client can log in, join a World, or move:
no GameService needs to run, so the content check falls back to the bundled
content, and nothing reaches a World scene, the realtime protocol, or
LiteNetLib. It does not judge what the screen looks like.

## Run On A Bad Network

Everything else runs on loopback, without delay, loss, or jitter.
`tools/NetworkImpairmentProxy` puts a bad network between the clients and a
worker: a UDP proxy that listens on a port of its own, opens one upstream
socket per client address, so the worker still sees every client apart, and
forwards both directions after the delay of a network profile, or drops or
duplicates a packet. It never reads or changes a payload, so neither the client
nor the worker knows about it: the worker advertises the proxy's port with
`SimulationWorker:AdvertisedUdpPort` and keeps listening on its own, and every
join ticket sends the client to the proxy. The load runner starts it with
`-Network`, as
[Load Runs Under A Network Profile](STACK_STRESS_TESTING.md#load-runs-under-a-network-profile)
describes, and [Play On A Bad Network](MANUAL_TESTING.md#play-on-a-bad-network)
uses it by hand.

### Network Profiles

A profile is a file `tools/load/network-profiles/<name>.json`. The four profiles
split their round trip and its jitter evenly over both directions:

| Profile | Round trip | Jitter | Loss per direction | Duplicated | Line down | Stands for |
| --- | ---: | ---: | --- | ---: | --- | --- |
| `good` | 30 ms | 5 ms | none | none | never | Wired fibre or cable to a server in the same country |
| `normal` | 80 ms | 15 ms | 1 % | none | never | Cable or DSL over Wi-Fi to a server in the same region |
| `poor` | 200 ms | 40 ms | 5 %, a fifth of it in bursts of 4 packets on average | 0.5 % | never | A congested mobile network or a server on another continent |
| `terrible` | 400 ms | 100 ms | 10 %, three tenths of it in bursts of 6 packets on average | 1 % | 3 s every 60 s, the first time 60 s after the client's first packet | A weak mobile signal on the move or a satellite link |

`none` means no proxy, and no profile file may take that name. A profile has a
`description` of the connection it stands for, the objects `clientToServer` and
`serverToClient`, and the `verdict` section that
[Load Runs Under A Network Profile](STACK_STRESS_TESTING.md#load-runs-under-a-network-profile)
describes. The proxy and the load runner read it strictly: an unknown property,
a missing value, or a value out of its range is an error. Each direction has:

| Property | Meaning |
| --- | --- |
| `delayMs` | The one-way delay of every packet, 0 to 10,000 |
| `jitterMs` | Every packet waits the delay plus a jitter drawn evenly from minus to plus this, at most the delay. Packets that are closer together than the jitter's range can overtake each other, and the proxy lets them |
| `lossPercent` | The share of all packets of the direction that is lost, 0 to 99 |
| `duplicatePercent` | The share of the forwarded packets that are sent twice, each copy with its own jitter |
| `burstLoss` | Optional: `sharePercent` of the loss comes in bursts of `averageLengthPackets` on average, the rest falls on single packets |
| `outages` | Optional: everything is dropped for `durationSeconds` every `everySeconds`, the first time `firstAfterSeconds` after the client's first packet |

A burst starts on a packet outside a burst with the probability that makes
bursts drop their share of all packets, and it ends after each of its packets
with the probability one over its average length; the rest of the loss falls on
the packets outside bursts at random. An outage counts from each client's own
first packet, so a client that reconnects with a new socket starts its schedule
again, and in a load run the outages of the bots fall at different times. The
proxy is deterministic for a seed as far as the arrival order allows: each
direction of each client draws from its own random stream, seeded by the seed,
the client's number in the order the proxy first heard from it, and the
direction, and every packet draws the same six numbers whatever its fate, so
the fate of the n-th packet depends only on the seed and n, and on its arrival
time only for an outage.

### Start The Proxy By Hand

Start the stack as [Backend Services](#backend-services) describes, but start
the default worker with the proxy's port as its advertised port:

```powershell
dotnet run --project src/Server/SimulationWorker -- --SimulationWorker:AdvertisedUdpPort=27115
```

Then start the proxy in front of it in another terminal, with a profile name or
the path of a profile file:

```powershell
dotnet run --project tools/NetworkImpairmentProxy --configuration Release -- --profile poor --listen 127.0.0.1:27115 --forward 127.0.0.1:27015
```

Expected result: the proxy prints `Network impairment proxy listening on UDP
127.0.0.1:27115, forwarding to 127.0.0.1:27015 with profile poor (seed 1)` with
the profile's description and what it does to each direction, then a progress
line every 10 seconds. `Ctrl+C` stops it and prints its counts. To change the
profile, stop the proxy and start it again with another one; the worker keeps
running, and a client that loses its line for longer than its 10-second realtime
timeout reconnects. Without the proxy, a client of a worker that advertises
27115 cannot join.

| Option | Default | Meaning |
| --- | --- | --- |
| `--profile <name or file>` | required | A profile of `tools/load/network-profiles`, which the build copies below the proxy, or a profile file |
| `--listen <ip:port>` | required | Where the clients send to |
| `--forward <ip:port>` | required | The worker's own UDP endpoint |
| `--seed <number>` | `1` | The seed of every random decision |
| `--counts <file>` | none | Writes the counts as JSON when the proxy stops |
| `--idle-timeout-seconds <s>` | `30` | Forgets a client that sent and got nothing for this long and closes its upstream socket, so the worker sees it no more |
| `--report-interval-seconds <s>` | `10` | How often the progress line is printed; 0 never |
| `--max-clients <count>` | `2048` | Refuses more clients at once |
| `--allow-non-loopback` | off | Allows a listen or forward address off this machine |

Like the load generator, the proxy refuses a listen or forward address that is
not a loopback address unless `--allow-non-loopback` is given, and it takes IP
addresses only, with `localhost` for `127.0.0.1`. Exit codes: 0 after `Ctrl+C`
or a passed self-test, 1 a failure, 2 invalid options or an invalid profile.

### What The Proxy Counts

For each direction the proxy counts the packets and bytes it received and
forwarded, the duplicated packets, the packets it dropped at random, in bursts,
in outages, and because it fell behind, its send failures, the packets it
discarded when it stopped, the delay it applied, from a packet's arrival to its
send, and its lateness against each packet's due time; and the clients it saw,
at most at once, timed out, and refused, and the processor time it used. It prints them when it stops and
writes them as JSON with `--counts`. A direction keeps at most 200,000 waiting
packets and drops beyond them as overflow, which only a proxy that fell far
behind reaches.

### Delay Accuracy

A proxy that adds its own timer noise measures itself instead of the profile:
an ordinary wait on Windows rounds up to the 15.6 ms system timer tick. Each
direction has its own scheduler thread at the highest thread priority, which
sleeps on a high-resolution waitable timer, the approach of the worker's loop,
until 0.3 ms before the next due time and spins the rest; a new packet wakes it,
because jitter can make it due before the packet it waits for. A spin of 1 ms
gave no better p99 and cost a core more at 6,000 packets per second and
direction, because the thread then spun through the gaps between the packets;
`self-test --spin-ms` measures another spin. Packets due at
the same time leave in the order they arrived. The self-test proves the
accuracy:

```powershell
dotnet run --project tools/NetworkImpairmentProxy --configuration Release -- self-test
```

It runs the proxy in its own process with a constant delay of 40 ms each way
and no jitter or loss in front of an echo server, sends timestamped packets
from 8 client sockets at 100 packets per second each for 20 seconds, and
measures with one clock how much later than the delay each packet arrived at
the server and back at the client. `--delay-ms`, `--seconds`, `--clients`,
`--rate`, `--size`, and `--output <file>` change it. It passes when every packet
arrived and the p99 of the error stays below 2 ms in both directions, and
exits with 0, else with 1. Measured on the reference machine of
[Measured Tiers](STACK_STRESS_TESTING.md#measured-tiers):

| Run | Packets per second and direction | Error p50 / p99 / max, client to server | Error p50 / p99 / max, server to client | Proxy lateness p99 |
| --- | ---: | --- | --- | --- |
| Default, 8 clients at 100 per second | 800 | 0.13 / 0.75 / 7.7 ms | 0.10 / 0.69 / 4.8 ms | 0.66 and 0.58 ms |
| `--clients 100 --rate 60` | 6,000 | 0.12 / 0.56 / 6.7 ms | 0.09 / 0.67 / 14.6 ms | 0.32 and 0.31 ms |

The error includes the two loopback socket hops and the test's own receive
threads; a single packet can still be late by several milliseconds when Windows
schedules another thread, which the p99 stays clear of. Every load run under a
profile checks the proxy again with the validity rule
`network_proxy_lateness_p99_ms`; the `playing-100` runs of
[Measured Under The Profiles](STACK_STRESS_TESTING.md#measured-under-the-profiles)
measured a lateness p99 of 0.52 to 0.54 ms at 3,000 to 3,700 packets per second
and direction, with the proxy at about one core.

### The Client On A Bad Network

The network part of [the live stack test](#run-the-live-stack-test) plays the
running client under a profile and records with the
[client measurements](UNITY_CLIENT_ARCHITECTURE.md#client-measurements): a
walk with a turn about every two seconds, 30 seconds long and 78 under
`terrible`, so that it spans the first outage, then 2 seconds of standing, the
walk back, and Mira's window opened and closed five times, while another
player walks back and forth beside it; then the leaves, each with a join after
it, in a recording of their own. Measured on 2026-11-14 on the reference
machine of [Measured Tiers](STACK_STRESS_TESTING.md#measured-tiers), headless,
with `-Leaves 20` under `good` and `normal`, `-Leaves 100` under `poor` and
`terrible`, and the plain run's 3 on loopback; every run passed:

| | Loopback | `good` | `normal` | `poor` | `terrible` |
| --- | --- | --- | --- | --- | --- |
| Round trip p50 / p95 | below 1 ms | 30 / 31 ms | 80 / 155 ms | 208 / 427 ms | 525 / 829 ms |
| Owner snapshots lost | none | none | 1.2 % | 6.1 % | 20.6 % |
| Silences of a second and more, longest | none | none | none | none | 4, 3.2 s |
| Corrections, p95 / largest | 0.000 / 0.000 m | 0.000 / 0.000 m | 0.000 / 0.000 m | 0.000 / 0.000 m | 0.000 / 8.93 m |
| Hard snaps | none | none | none | none | 1, 0.65 per minute |
| Largest jump of the drawn player | 0.007 m | 0.006 m | 0.005 m | 0.003 m | 9.01 m |
| Drawn player after 2 s of standing, from the worker's | 0.000 m | 0.000 m | 0.000 m | 0.000 m | 0.000 m |
| The other player out of snapshots in the play: share, stalls, longest | none | none | 0.3 %, 6, 0.07 s | 2.4 %, 25, 0.53 s | 13 %, 194, 3.0 s |
| The other player out of snapshots right after the joins: share, longest | none | none | 30 %, 0.41 s | 55 %, 0.49 s | 82 %, 7.8 s (1) |
| Mira's open and close, p50 / p95 | 19 / 41 ms | 46 / 62 ms | 92 / 109 ms | 204 / 1,111 ms | 422 / 720 ms |
| Leaves that ended cleanly | 3 of 3 | 20 of 20 | 20 of 20 | 100 of 100 | 100 of 100 |
| Lost connections, exceptions | none | none | none | none | none |
| Unity time of the run | 104 s | 99 s | 102 s | 276 s | 399 s |

(1) The first leave met the second outage and took 7.9 seconds, cleanly,
while the other player stood; the other 99 leaves took about 0.6 seconds each.

What the profiles show:

- **The prediction holds while the line holds.** Outside the outage every
  owner snapshot of the play confirmed the predicted position to the
  millimeter on every profile, but for one correction of 0.17 m in one
  `terrible` run, so no correction moved the player visibly and the drawn
  player stood exactly where the worker had it: the worker fills a gap of up
  to 15 lost inputs, half a second, with the path before it
  (`AuthoritativePlayerMovement.MaximumFilledInputGap`), which covers every
  loss of the profiles outside an outage.
- **An outage snaps the walking player back by the walk it missed.** Under
  `terrible` the client walks on through the 3-second outage, but none of the
  inputs of those seconds reach the worker, and a gap that long is not filled:
  the worker skips ahead to the first input after it, so the character stood
  through the outage. The first owner snapshot after the outage then corrects
  the prediction by the distance walked, 8.9 to 9.3 m in three runs, a hard
  snap that the drawn player shows as a jump of the same size in one frame. A
  player sees the character jump back to about where the line went down. This
  is a finding about how the worker treats a long gap of inputs, not fixed
  here.
- **Other players freeze when their snapshots run out.** The presentation
  holds a remote entity at its newest state until the next one arrives, with a
  render delay of about 133 ms. A burst of lost snapshots longer than that
  stops the other player for a moment: up to 0.07 s under `normal`, 0.53 s
  under `poor`, and about a second under `terrible` besides the 3 seconds of
  the outage. Right after a join it stands still too until the render clock
  has states to play, for up to half a second under `normal` and worse.
- **Reliable intents wait for resends.** Mira's window answers about one round
  trip after the click, but one lost packet holds it back until LiteNetLib
  resends it, so over three runs the p95 rose to 1.0 to 1.7 seconds under
  `poor` and 0.7 to 2.6 seconds under `terrible`, as for the bots of
  [Measured Under The Profiles](STACK_STRESS_TESTING.md#measured-under-the-profiles).
- **The client's leave meets the leave finding only under `terrible`.** Over
  every run of the day 60 of 60 leaves ended cleanly under `good` and
  `normal`, 220 of 220 under `poor`, and 217 of 220 under `terrible`. The 3
  that failed, all in one run, ended within 0.8 seconds with `Realtime
  operation failed: The realtime connection closed: RemoteConnectionClose.
  [connection_lost]` and went to Character Select: the worker's disconnect
  overtook a lost `LeaveAccepted`, the finding of
  [Measured Under The Profiles](STACK_STRESS_TESTING.md#measured-under-the-profiles)
  that fails 25 and 58 percent of the load bots' leaves under `poor` and
  `terrible`. Every leave but the first came about a second after a join, with
  the reliable channel otherwise quiet, and each join opens a new socket, so
  the proxy's outage count starts again and only the first leave can meet an
  outage. A leave after a longer play, with more in flight, can fail more
  often, as the load bots show.
- **The round trip reads above the profile under loss.** Its p95 lies 75 to
  430 ms above the profile's round trip under `normal` and worse, more than
  the jitter explains; these runs do not show why.

The test asserts the bounds of `tools/load/network-client-bounds.json` for the
play of each profile and loopback, with the reason of each in the file:

| | Loopback, `good`, `normal`, `poor` | `terrible` |
| --- | --- | --- |
| Hard snaps per minute in the World | 0 | 1.5: one per outage, which comes once a minute, with margin |
| Corrections p95 | 0.05 m | 0.05 m |
| Largest jump of the drawn player | 0.25 m, the size the measurements count as a visible jump | 18 m: the walk over the outage and a round trip, 5 m/s for 3.5 s |

A client that misses a bound under `good` or `normal` has a defect to find,
not a bound to raise.

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
every script, that no Unity test yields `WaitForEndOfFrame`, and that client
code makes a `RenderTexture` only after checking for a graphics device. These are the
commands of the gate, in its order, for running one step by hand from the
repository root:

```powershell
& .\tools\Test-Changes.ps1 -VerifyRules
& .\tools\ci\Test-UnityCommon.ps1
& .\tools\Verify-DependencyPolicy.ps1
& .\tools\art\Verify-ArtAssets.ps1
python -m unittest discover -s tools/art/blender/tests
& .\tools\Verify-Observability.ps1
dotnet restore Mmorpg.slnx --locked-mode
dotnet format Mmorpg.slnx --verify-no-changes --no-restore
dotnet build Mmorpg.slnx --configuration Release --no-restore
dotnet run --project tools/ItemCatalogCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldActorCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/WorldCollisionCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/QuestCatalogCompiler --configuration Release --no-build -- --verify
dotnet run --project tools/TextTableCompiler --configuration Release --no-build -- --verify
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
  `WaitForEndOfFrame` and has no graphics device, so whenever the Unity suites
  are selected, the check `Unity batchmode rules` fails for a test that yields
  it and for client code that makes a `RenderTexture` without checking
  `SystemInfo.graphicsDeviceType` against `GraphicsDeviceType.Null`, even with
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
accepts exit code 8 of the readiness check. After the merge the script moves
every open pull request stacked on the branch to `development`, since GitHub
closes a pull request whose base branch is deleted, then deletes the branch on
GitHub, but only while it still points to the merged head and no stacked pull
request failed to move, and
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
merge or a stacked pull request could not move off it, the main checkout could
not return to `development` and was left
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
scripts do not compile. It also stops before the first run when an open scene
has unsaved changes, because the Test Runner would wait on its save dialog;
save or discard them in the Editor first. Then it starts each run
asynchronously and polls its status every two seconds, across the domain reloads
of Play Mode, until the run completes. Every step has a limit of 15 minutes
(`-TimeoutMinutes`), after which
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
| 4 | The Editor did not respond or a run timed out, usually because a modal dialog blocks the Editor, or an open scene has unsaved changes |
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
which draw, run only in the Editor. The script also leaves out the `LiveStack`
category, which needs the local stack and runs through
[Run The Live Stack Test](#run-the-live-stack-test); the CI Unity gate and
`tools/Test-Changes.ps1` run this script, so they leave it out too. That test
is also explicit, so `tools/Run-UnityEditorTests.ps1` and the Editor's Test
Runner do not select it. `-Platform PlayMode` runs
one suite, `-TestCategory` replaces the default category filter
`!Graphics;!LiveStack`, and `-Preflight` only checks that Unity is installed and
the project is free. Unity opens a project only once, so the script stops before
it starts Unity while an Editor or another Unity process has the project open.

Expected result: fresh EditMode and PlayMode result files and Unity logs below
`TestResults/Unity`, and both suites reported as passed. As in the backend
gate, a skipped or inconclusive test, or a suite without tests, fails the run.
`Window > General > Test Runner` in the Editor runs the same suites by hand.
An interrupted run ends the Unity process it started and removes the
`InitTestScene` that Unity's PlayMode runner made in `Assets` and deletes only
at the end of a run. After every run the script puts back the Unity-owned
tracked files that Unity rewrote, as [Build The Client](#build-the-client)
describes, and names them.

The `Player` category runs in a built player as well: the pinned goldens of
the shared movement simulation and the realtime protocol, which must give the
same results under IL2CPP as on Mono and .NET. `-Platform StandaloneWindows64`
builds a Windows test player with the committed IL2CPP settings of a release
build, starts it in a window, and runs the `Player` category in it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Run-UnityTests.ps1 -Platform StandaloneWindows64
```

The Unity CLI cannot run a player test, so this run always starts the Editor
of `-UnityEditorPath`, `UNITY_EDITOR_PATH`, or the Unity Hub's default path.
Expected result: `StandaloneWindows64-results.xml` below `TestResults/Unity`
with every test of the category passed, each with a `[PLAYER] IL2CPP` line of
the value it computed, and `Unity StandaloneWindows64 tests passed`. The test
player's build is an IL2CPP build with the C++ configuration of a release
build, measured on the machine of [Build Kinds](#build-kinds) at 21 minutes the
first time and 17 minutes after a small change of the client, most of it in
the C++ link. The test player differs from a shipped release build in what the
Unity Test Framework adds: it is a development build with the test assemblies
and IL2CPP's checked code variant. The Editor's PlayMode suite runs the same
category on Mono.

### Run A Load Scenario

`tools/Test-Load.ps1 -Scenario <name>` starts a disposable stack, runs one load
scenario against it, judges it against the quality boundary, and stops what it
started. It needs Docker for the full-stack scenarios and refuses to run while
another process uses its ports, so it can run beside the normal local stack.
The scenarios, the run folder, and the verdict are described in
[Run A Scenario](STACK_STRESS_TESTING.md#run-a-scenario).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Load.ps1 -Scenario smoke
```

Expected result: a table in which every rule passes, `Load scenario smoke
passed`, and exit code 0, in about one and a half minutes.

### Run The Live Stack Test

Every other client test drives the network layer through a fake transport or a
scripted GameService. The live stack test, `LiveStackPlayModeTests` in the
PlayMode assembly, plays one character through the real client code against the
real local stack instead: `ApiClient` registers a fresh account, creates a
character, lists the shards, and asks for the join tickets, and
`RealtimeSimulationClient` joins the default worker over UDP through
`LiteNetLibRealtimeTransport`. It walks the character, leaves, reconnects at once
to the body, drops the line without a leave and reconnects again, drops it once
more and lets the client's own reconnect restore it, leaves and
waits until the body is gone, joins as a new entity at the saved place and
vitals, and leaves again; [Live Stack Test](../tests/README.md#live-stack-test)
lists what each step asserts. Run it after a change to the client's network
layer, the realtime protocol, joins and leaves, lingering bodies, or the saved
place and vitals, and before a playtest. It does not replace the manual checks
in [Manual Testing](MANUAL_TESTING.md), which judge what a person sees.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-LiveStack.ps1
```

The script needs Docker, the `.env` file, the Unity Editor of the
[requirements](#requirements), and a free project: it refuses to run while an
Editor has `src/Client` open, or while a GameService or worker already uses port
5000 or UDP port 27015, so it never meets a stack it did not start. It starts
PostgreSQL and Redis with `docker compose up -d --wait` when they do not run,
builds GameService and SimulationWorker, starts them in the Development
environment as background processes, each in its own hidden console, waits until
GameService answers `/health/ready` and the worker listens and has registered
`local-shard-1`, and runs only the `LiveStack` category headless through
`tools/Run-UnityTests.ps1`. The test checks that every join ticket names the
worker runtime that the script started. When the test ends, passed or failed,
and when the script is interrupted with `Ctrl+C`, the script stops the worker and
GameService with `Ctrl+C`, so the worker saves its characters and marks itself
offline, ends a service that does not stop within 30 seconds, and stops the
containers that it started; containers that ran before keep running.

Expected result: the lines of the steps `a.` to `h.` with what each observed,
such as the entity ids, the places, `IsReconnect`, and `BodySeconds 20`, the
lines of the network part, then `Live stack test under the network profile
none passed`, exit code 0, and the stack as it was before. A run takes about
two and a half minutes: up to one minute to start the containers, build, and
start the services, and about 100 seconds of Unity, of which the scenario takes
30 seconds, most of them the 20 seconds that the body stays, and the network
part about 50.

| Exit code | Meaning |
| --- | --- |
| 0 | The scenario passed |
| 1 | The test failed; the step lines and the Unity log say where |
| 2 | The stack or Unity could not run the test, for example without Docker, with an open Editor, or with a port in use |

The run also plays the network part, `LiveStackNetworkPlayModeTests`, which
[Live Stack Test](../tests/README.md#live-stack-test) describes, on loopback.
`-Network <profile>` runs the network part alone on a bad network: the script
also builds and starts the network impairment proxy with the profile of
`tools/load/network-profiles`, as [Run On A Bad Network](#run-on-a-bad-network)
describes, on UDP 27115 in front of the worker, which advertises that port, and
stops it last:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-LiveStack.ps1 -Network poor -Leaves 20
```

| Parameter | Meaning |
| --- | --- |
| `-Network <profile>` | The profile between the client and the worker, `none` by default |
| `-Leaves <count>` | How many times the network part leaves and comes back, 3 by default. A leave and a join take one to three seconds, so 100 give a rate of failed leaves in a few minutes |

The walk lasts 30 seconds, and under a profile with outages until 15 seconds
after the first outage ended, so the measurements see the correction after it.
The log folder keeps the proxy's `network-proxy.json`, the network part's
`live-network-<profile>.json`, and the path of its measurement file, and the
script prints the `[LIVE]` lines of the network part with the proxy's counts.
What the profiles measured is in
[The Client On A Bad Network](#the-client-on-a-bad-network).

Each run keeps its logs in a new folder below
`%TEMP%\shooter-mmo-live-stack`, which the script prints: the build output, the
output of both services, the Compose output, and the Unity log and result file.
Each run also leaves accounts with one character each in the local database:
`live_<hex>` of the scenario, and `net_<hex>` and `walker_<hex>` of the network
part. A script that is killed, instead of interrupted with `Ctrl+C`,
cannot clean up: stop the two processes whose ids it printed, and run
`docker compose stop` if it had started the containers.

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
  `src/Shared/WorldData/Generated/Worlds/<WorldId>/world-actor-spawns.json`. The
  SimulationWorker loads the World's compiled spawns with the shared actor
  catalog, and both World
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
   selected World folder contains its manifest, collision, and compiled actor
   spawns.
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

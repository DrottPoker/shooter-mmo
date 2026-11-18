# Deployment

This document owns how the server is deployed: what a deployment consists of, the two
images, the production-like stack that runs them on one machine, every setting that must be set,
the ports, TLS, how to update, back up, watch, and size a deployment, and what does not exist
yet. It is written for someone who operates the system and did not build it. The meaning of every
setting is in [Service Features](SERVICE_FEATURES.md#configuration-defaults), and the commands of
the stack are in [Local Development](LOCAL_DEVELOPMENT.md#run-the-production-like-stack).

Nothing is deployed to a real server. The deploy shape is ready to deploy: images, a Compose file
that runs them in the Production environment behind a TLS proxy, a smoke test that plays through
that stack, and this runbook. Everything that needs a real host is listed under
[Not There Yet](#not-there-yet).

## What A Deployment Consists Of

| Part | What it is | How many | Faces the internet |
| --- | --- | --- | --- |
| Proxy | Ends TLS, forwards the HTTP API to GameService, sets the forwarded headers. Caddy in the stack | One per public name | TCP 443 |
| GameService | The HTTP API: accounts, characters, placement, items, and the routes of the workers | One here; replicas share PostgreSQL and Redis | Through the proxy only |
| SimulationWorker | The realtime simulation of one Shard over UDP | One per Shard | One UDP port |
| PostgreSQL | The durable authority for everything GameService owns | One | No |
| Redis | Session acceleration and distributed rate limits; no game state | One | No |
| Migration job | `migrate`, which applies the migrations and seeds and ends | Once per update | No |

```text
player ---TLS---> proxy ---HTTP---> GameService ---> PostgreSQL
player ---sealed UDP---> SimulationWorker ---HTTP + worker secret---> GameService
                         SimulationWorker and GameService ---> Redis
```

A player talks to the proxy for everything but the game itself, and to the worker's UDP port for
the game. The worker never talks to a player over HTTP and never reaches PostgreSQL: it asks
GameService for everything durable, as [Project Architecture](PROJECT_ARCHITECTURE.md) describes.
GameService also audits its own database, at start and every hour, as
[The Economy Audit](#the-economy-audit) describes.

## The Images

Both images are two targets of `deploy/Dockerfile`, built with the repository root as the build
context:

```powershell
docker build -f deploy/Dockerfile --target game-service -t shooter-mmo/game-service:local .
docker build -f deploy/Dockerfile --target simulation-worker -t shooter-mmo/simulation-worker:local .
```

- The build stage restores both projects with `--locked-mode`, so the lock files decide every
  package, and publishes them in Release as framework-dependent applications. A layer holds only
  the project, lock, and props files for the restore, so it stays cached while only code changes.
- The runtime stages are the official chiseled .NET images of the version the projects target,
  `aspnet` for GameService and `runtime` for the worker. A chiseled image is Ubuntu without a
  shell, a package manager, or ICU, so globalization is invariant, and it runs as the user `app`,
  never as root. Both services write nothing outside `/tmp`, so a read-only root file system works.
- Each image holds what its service reads at run time and nothing else. GameService: its `Config`
  folder, the World manifests, the compiled item and quest catalogs, the text tables, and the
  character appearance. The worker: its `Config` folder, the World manifests, spawns and collision
  data, the actor, item, and quest catalogs, the combat rules, and the character appearance. The
  Development settings and the development item packages are removed, so an image cannot run
  Development with its wide-open settings. The Unity client, the art, the tools, and the tests
  never enter the build context, as `.dockerignore` keeps them out.
- With no shell, a health check cannot be `curl`. Each service answers its own, as
  [Health Checks](#health-checks) describes.
- Every image tag is pinned to a version, and Dependabot watches the Dockerfile and the Compose
  files in `deploy` and proposes the newer patch of each tag. Update the .NET runtime image by
  changing both runtime stages and the SDK stage together to the newest patch.
- CI builds both images for every pull request without pushing them anywhere, with layer caching.

On the reference machine a build with nothing cached, including the restore, takes about 45
seconds, and the images are about 60 MB for GameService and 47 MB for the worker by
`docker image inspect`.

## The Production-Like Stack

`deploy/docker-compose.production-like.yml` runs the two images with PostgreSQL, Redis, and a
Caddy proxy in the Production environment, under the Compose project `shooter-mmo-deploy`. It
shares no port, container, volume, or network with the development stack, the monitoring stack, or
the load stack, and it assumes no real host. The scripts in `tools` start, test, and stop it, as
[Local Development](LOCAL_DEVELOPMENT.md#run-the-production-like-stack) describes.

| Service | Image | Does | Health check |
| --- | --- | --- | --- |
| `postgres` | `postgres:16.16-alpine` | The database, with its data in the volume `postgres_data` | `pg_isready` |
| `redis` | `redis:7.4.12-alpine` | Redis with a password and no persistence | `redis-cli ping` |
| `migrate` | the GameService image | `migrate`, once; GameService starts only after it ended with exit code 0 | none, it ends |
| `game-service` | `shooter-mmo/game-service:local` | The HTTP API on port 8080 inside the stack | `probe http` of `/health/ready` |
| `simulation-worker` | `shooter-mmo/simulation-worker:local` | One worker for `local-shard-1`, UDP port 27015 inside | `probe file` of its health file |
| `proxy` | `caddy:2.11.9-alpine` | TLS on port 8443 inside, to GameService | Caddy's admin API on its loopback |

Three networks keep the parts apart:

| Network | Holds | Reaches outside |
| --- | --- | --- |
| `edge` | The proxy and GameService, with the fixed range `EDGE_SUBNET` | No |
| `backend` | GameService, the migration job, the worker, PostgreSQL, and Redis | No |
| `public` | The proxy and the worker, where their published ports live | Yes |

`Http:TrustedProxies` of GameService is the `edge` range, which only the proxy and GameService
join, so GameService trusts `X-Forwarded-For` from exactly the proxy and from nothing else. Every
service restarts unless it was stopped, writes its logs through Docker's `json-file` driver with
rotation, and has a read-only root file system with a `tmpfs` for what it writes. The three .NET
containers and the proxy also drop every capability and set `no-new-privileges`. The
`stop_grace_period` of GameService and the worker is 45 seconds, longer than the 30-second
shutdown timeout of their hosts, as [Orderly Shutdown](SERVICE_FEATURES.md#orderly-shutdown)
requires.

### The Secrets

`deploy/stack.env` holds the three secrets of the stack: `POSTGRES_PASSWORD`, `REDIS_PASSWORD`,
and `WORKER_1_SECRET`, which is the secret of the worker `deploy-worker-1`. `tools/New-DeploySecrets.ps1`
makes the file with random hexadecimal values when it does not exist, and
`tools/Start-DeployStack.ps1` calls it. Git ignores the file, the Compose file requires each of
the three and has no default for any of them, and no script prints a secret. The file is the only
place the stack's secrets live; a container sees them as environment variables, which anyone who
can run `docker inspect` on the host can read. A real deployment keeps its secrets in the secret
store of its platform instead.

### The Variables

These have defaults that are no secret and are set in the shell or in `deploy/stack.env`. A value in
the shell wins over the file, as Compose reads them, and the stack's scripts resolve them the same
way, so the port checks, the printed addresses, and the smoke test follow the file too:

| Variable | Default | Sets |
| --- | --- | --- |
| `STACK_BIND_ADDRESS` | `127.0.0.1` | The address that the two published ports bind; `0.0.0.0` makes the stack reachable from other machines |
| `PROXY_HTTPS_PORT` | `8443` | The TCP port of the proxy on the host |
| `WORKER_UDP_PORT` | `28015` | The UDP port of the worker on the host, and the port it advertises |
| `WORKER_ADVERTISED_HOST` | `127.0.0.1` | The address that GameService hands to players as the worker's endpoint |
| `EDGE_SUBNET` | `10.213.77.0/28` | The range of the `edge` network and the trusted proxies; change it when it collides with another Docker network |

To play from another machine, set `STACK_BIND_ADDRESS=0.0.0.0` and `WORKER_ADVERTISED_HOST` to an
address of this machine that the player can reach, and trust the certificate authority there too.

### The Topology

The stack uses the checked-in topology: the Fleet `local-fleet`, the Node `local-node-1`, and the
Shard `local-shard-1`, which the worker binds to Development World 2. There is no production World
yet, so the stack runs the development one. A real deployment names its own Fleet, Node, Shards,
and Worlds as [Topology Bootstrap](SERVICE_FEATURES.md#topology-bootstrap) describes.

### Telemetry

`deploy/docker-compose.telemetry.yml` is an overlay that points GameService and the worker at the
monitoring stack. A Compose profile selects services and cannot change the settings of one, so the
export is an overlay file, started by `tools/Start-DeployStack.ps1 -Telemetry` after the
monitoring stack of [Monitoring](MONITORING.md#run-the-monitoring-stack) runs. It sets
`Telemetry__OtlpEndpoint` to the collector on the monitoring network and joins that network, which
is `shooter-mmo-observability_default` unless `OBSERVABILITY_NETWORK` names another.

## Settings That Must Be Set

Configuration comes from the `Config` files in the image, then the environment, then the command
line. An environment variable names a setting with `__` for `:`, so `Http:TrustedProxies` is
`Http__TrustedProxies`. Outside Development the services refuse to start, with one message that
lists every problem, when a required setting is missing or still local.

GameService, set in the Compose file for the stack:

| Setting | Value in the stack | Means |
| --- | --- | --- |
| `DOTNET_ENVIRONMENT` | `Production` | Selects the rules of a deployment; `Development` has local settings the services refuse elsewhere |
| `ConnectionStrings:Postgres` | `Host=postgres;...;Password=<secret>` | The database; the password must be real |
| `ConnectionStrings:Redis` | `redis:6379,password=<secret>` | Redis; the password must be real |
| `AllowedHosts` | `localhost;game-service` | The host names that requests may name: the public name that the proxy passes on and the name that the worker and the health check use; never `*` |
| `Http:TrustedProxies` | the `edge` range | The proxies whose `X-Forwarded-For` counts, so rate limits see the player and not the proxy |
| `ServiceAuthentication:SimulationWorkers:<workerId>` | the worker's secret | One secret per worker; GameService refuses the shared `SIMULATION_WORKER_SERVICE_SECRET` outside Development |

`Audit:Enabled` needs no setting: it is on outside Development, so GameService audits the economy
at start and every hour, as [The Economy Audit](#the-economy-audit) describes.
`Database:RunMigrationsOnStartup` is false outside Development, so the `migrate` job brings the
database up to date before GameService starts, and a start refuses a database that is not current.
`Http:ShutdownDrainSeconds`, 10 by default, must be longer than the proxy's health check interval
times the failures it needs, and `Http:ShutdownTimeoutSeconds`, 30, must be shorter than the
runtime's stop grace period.

SimulationWorker:

| Setting | Value in the stack | Means |
| --- | --- | --- |
| `DOTNET_ENVIRONMENT` | `Production` | As above |
| `SimulationWorker:SimulationWorkerId` | `deploy-worker-1` | The worker's identity; the key of its secret in GameService |
| `SimulationWorker:FleetId`, `NodeId`, `ShardId`, `WorldId` | the checked-in defaults | The Shard it runs and the World that Shard is bound to |
| `SimulationWorker:GameServiceBaseUrl` | `http://game-service:8080` | Where it calls GameService; see [The Call Between Worker And GameService](#the-call-between-worker-and-gameservice) |
| `SimulationWorker:GameServiceSecret` | the worker's secret | The same value as GameService holds for this worker id; at least 32 characters and never a placeholder |
| `SimulationWorker:AdvertisedHost`, `AdvertisedUdpPort` | `127.0.0.1`, `28015` | The endpoint that placement hands to players, which must be reachable from the player's side, so never a wildcard address |
| `ConnectionStrings:Redis` | as GameService's | Redis for its health check; the password must be real |
| `SimulationWorker:HealthFilePath` | `/tmp/worker-ready` | The file that its health check reads |

The worker also refuses, outside Development, the shared secret variable, a secret or Redis
password that is a placeholder or that the repository publishes, and plain HTTP to a public IP
address, as
[Hosts, Secrets, And Passwords Outside Development](SERVICE_FEATURES.md#hosts-secrets-and-passwords-outside-development)
describes. The other settings of both services have defaults that need no change to start; the
tables in [Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults) list them.

## Ports And What Faces The Internet

| Port | Protocol | Service | Faces the internet |
| --- | --- | --- | --- |
| 443, `8443` in the stack | TCP | The proxy | Yes: every HTTP client |
| One per worker, `28015` in the stack | UDP | The worker, `27015` inside its container | Yes: every player |
| 8080 | TCP | GameService | No: the `edge` network for the proxy, the `backend` network for the worker |
| 5432 | TCP | PostgreSQL | No |
| 6379 | TCP | Redis | No |
| 2019 | TCP | The proxy's admin API | No: the loopback of its container |

The stack publishes only the first two, on `127.0.0.1` unless `STACK_BIND_ADDRESS` says
otherwise, and none of them is a port of the development stack (5000, 27015, 27016, 5432, 6379),
the monitoring stack, or the load stack. A firewall in front of a real host allows exactly
those two and nothing else inbound.

The worker admits a player by its UDP address. Behind Docker's userland proxy, as on Docker
Desktop, every player appears to come from one address, so the per-address admission limits of
[UDP Resilience And Quotas](SERVICE_FEATURES.md#udp-resilience-and-quotas) count them together.
On a Linux host Docker forwards the port with its own NAT rules and keeps the player's address.

## TLS

The proxy ends TLS, and GameService speaks plain HTTP on the private `edge` network, as
[Behind A Proxy](SERVICE_FEATURES.md#behind-a-proxy) describes.

In the stack, Caddy makes a certificate for `localhost` from its own local certificate authority,
with no manual step. The authority lives in the volume `proxy_data`, so it stays the same across
restarts and changes only when the volumes are removed. `tools/Start-DeployStack.ps1` copies its
root certificate to `artifacts/deploy-stack/root-ca.crt`, and a client that trusts that file
reaches the stack. The smoke test trusts exactly that root and nothing else, and refuses any
other certificate. A client on this machine trusts it through the operating system's trust store,
for example by importing the file into the current user's trusted roots on Windows; remove it
again when the stack is gone.

For a real host name and certificate, change the site address in `deploy/Caddyfile` from
`localhost:8443` to the public name, remove `tls internal`, `local_certs`, and
`skip_install_trust`, publish ports 80 and 443, and add the name to `AllowedHosts`. Caddy then
obtains and renews a certificate from a public authority by itself, which needs the name to point
at the host and port 80 to be reachable. With another proxy or a load balancer in front of Caddy,
add its address to `Http:TrustedProxies` and set `Http:ForwardLimit` to the number of proxies in
the chain.

TLS covers the HTTP API only, and the realtime traffic needs none of its own. Everything that a
player and the worker exchange over UDP is encrypted and authenticated by keys that never travel
over UDP, in one mode that no setting turns off, as
[Transport Security](REALTIME_PROTOCOL.md#transport-security) describes. GameService makes a
transport key with every join ticket and hands it out only in HTTPS answers: to the player in the
answer of `POST /api/shards/{shardId}/join`, which is why that answer must travel over TLS, and
to the worker in the answer of its call that consumes the ticket, as
[Join Tickets And Simulation Sessions](SERVICE_FEATURES.md#join-tickets-and-simulation-sessions)
describes. The player proves that it holds the key before GameService lets a ticket be used, so
someone who sees the ticket id on the wire can neither join with it nor spend it. A join answer
through a proxy without TLS would give the key away, and so would a worker call that is not
private, as [The Call Between Worker And GameService](#the-call-between-worker-and-gameservice)
says. The worker's UDP port therefore needs a firewall rule and nothing else: no certificate, no
proxy, and no TLS in front of it.

## The Call Between Worker And GameService

The worker calls GameService for everything durable: its registration, its session heartbeats, its
checkpoints, the join tickets it consumes, and every item, vendor, quest, and death operation.
Each call carries the worker's id and secret in the headers `X-Simulation-Worker-ID` and
`X-Simulation-Worker-Secret`, and the secret is the only thing that stands between a caller and
the authority of a Shard: whoever has it can consume join tickets, release sessions, and commit
operations as the worker. The answer to the call that consumes a join ticket carries the ticket's
transport key, from which the player's sealed realtime traffic is opened, so whoever reads that
answer can read what the player's connection says.

So the call must stay where neither the secret nor the key can be read. In the stack it travels
over the `backend`
network, which has no route out and holds nothing but the stack's own containers. Anywhere else it
must stay on a private network, or go over TLS: point `SimulationWorker:GameServiceBaseUrl` at an
`https` name, through the proxy, which then also forwards the worker routes. The worker refuses
plain HTTP to a public IP address, but it cannot know whether a host name is private, so keeping
the call off the internet is the operator's rule.

The proxy forwards every route, the worker routes too, and GameService authenticates them. A
request without the right secret gets `401` and the code `invalid_service_credentials`, which the
smoke test checks through the proxy. A deployment whose workers share a private network with
GameService can also refuse those routes at the proxy, with a Caddy matcher on
`/api/simulation-*` that answers `404` for requests from outside that network.

Every worker has its own secret, so one leak names one worker. To rotate a secret, set the new
value in GameService's `ServiceAuthentication:SimulationWorkers:<workerId>` and in the worker's
`SimulationWorker:GameServiceSecret`, and restart both; until both run with it, the worker is
refused.

## Health Checks

| Service | Check | Healthy when |
| --- | --- | --- |
| GameService | `dotnet Mmorpg.GameService.dll probe http http://localhost:8080/health/ready` | `/health/ready` answers 2xx: PostgreSQL and Redis answer, and GameService is not stopping |
| SimulationWorker | `dotnet Mmorpg.SimulationWorker.dll probe file /tmp/worker-ready 20` | The worker wrote its health file within 20 seconds: its UDP port is bound, its registration lease is valid, and its realtime loop completed a tick in the last 5 seconds |
| Proxy | `wget` of Caddy's admin API on its loopback | Caddy runs and holds its configuration |
| PostgreSQL | `pg_isready` | It accepts connections |
| Redis | `redis-cli ping` | It answers `PONG` with its password |

The images have no shell, `curl`, or `wget`, so each .NET service answers its own check:
`probe` runs before the host and the configuration exist, prints one line, and exits with 0 or 1,
as [Container Health Checks](SERVICE_FEATURES.md#container-health-checks) describes. The worker
has no HTTP surface, so while it is ready it writes a file every 5 seconds and deletes it when it
is not ready and when it stops, and the check reads the file's age.

What the worker's health means: the worker is bound, registered, and playing. The first two do not
prove the third. A realtime loop that hangs, in a deadlock or an endless loop in a handler, leaves
the UDP port bound, and the registration lease is renewed by another service, so the worker would
look alive while nobody could play. The loop therefore stamps the time of every completed tick, and
the health file is written only while the last tick is at most 5 seconds old, 150 ticks, far longer
than any pause the loop takes in normal operation, a garbage collection of milliseconds or a catch-up of
five ticks. When the bound is passed the file is deleted, the check turns unhealthy within about 30 seconds, and the
worker logs one warning for the episode that names how long the loop has been silent; it logs
nothing more until the loop ticks again, and the file then returns by itself. A worker that is
stopping is not judged: the loop stops ticking in the orderly stop, which takes seconds, and the
file is removed at the stop signal anyway.

What it does not mean: unhealthy is a label, not an action. Compose's `restart: unless-stopped`
restarts a container that exits and does nothing for one that is only unhealthy, so in the stack a
hung worker stays unhealthy, and so stays out of `up --wait`, until someone restarts it. An
orchestrator that restarts or replaces unhealthy containers, which a real deployment needs, turns
the label into a recovery. Until then a hung worker goes on heartbeating, because its heartbeat is
another service, so GameService keeps its Shard online and keeps placing players on it while its
connected players receive no snapshots.

`--health-check-only` of the worker does not fit a running container. It loads and validates all
configuration and content, which takes seconds, and it needs the UDP port that the running worker
holds, so it always fails there. It is the check of a worker that is not running, such as the last
step before a start.

The proxy asks GameService for `/health/ready` every 3 seconds and sends it no request while it
answers anything but 2xx. GameService answers 503 from the stop signal on, so the proxy takes it
out of rotation within one interval, long before its 10-second drain ends.

## Update A Deployment

An update replaces the images, applies the migrations, and starts the services again. The order
matters:

1. Build the new images. Nothing changes while they build.
2. Stop the workers. A worker saves the place and vitals of every player in one last checkpoint,
   goes offline, and tells each player `server_shutdown`; a player returns to character select
   and joins again when the worker is back.
3. Stop GameService. Readiness turns to 503 at once, the proxy takes it out of rotation, and
   GameService serves what is in flight for the drain, then stops.
4. Run `migrate --plan` to see what the new version would change, then `migrate`. A migration
   that waits too long for a lock fails and applies nothing; run it again. The schema never rolls
   back: what an older version can and cannot do against a database that a newer one migrated
   is in [Rolling Back A Release](GAME_SERVICE_MODULES.md#rolling-back-a-release).
5. Start GameService, then the workers. GameService starts only against a current database, and a
   worker registers when GameService answers.
6. Run `audit` once the workers are back, as [The Economy Audit](#the-economy-audit) describes,
   so an update that left the economy inconsistent shows at once and not at the next hour.

In the stack, from the repository root:

```powershell
$compose = "compose", "-p", "shooter-mmo-deploy", "-f", "deploy/docker-compose.production-like.yml", "--env-file", "deploy/stack.env"
docker @compose build
docker @compose stop simulation-worker
docker @compose stop game-service
docker @compose run --rm migrate migrate --plan
docker @compose up -d --wait
```

`up` runs `migrate` again as GameService's dependency, which does nothing when the plan was
applied already, recreates the services whose image changed, and waits until they are healthy.
Then `docker @compose run --rm migrate audit` checks the economy. Run the smoke test afterwards with
`-NoBuild`, as [Local Development](LOCAL_DEVELOPMENT.md#run-the-production-like-stack) describes.

What players see: a player on a worker that stops returns to character select with the text of
`server_shutdown`, and the character keeps its place and vitals. While GameService is stopped, the
proxy answers requests for it with 503, no new login or join succeeds, and a worker that stays up
plays on for its outage tolerance, 120 seconds by default, with everything durable paused, as
[GameService Outage](SERVICE_FEATURES.md#gameservice-outage) describes. A restart of GameService
alone, inside that tolerance, therefore costs the players a saving-paused notice and nothing
else, while a change of the item or quest catalog needs both restarted: GameService places
players only on a worker whose catalog revisions equal its own.

### Players Who Have Not Updated

Versions are exact. A player's client plays only on a worker that runs the realtime protocol
version, the simulation revision, and the collision data of the World that the client was built
with, as [Versioning](REALTIME_PROTOCOL.md#versioning) describes, and one worker owns one Shard.
So an update that changes any of the three takes the Shard away from every client that was not
updated, and there is no overlap, no window of versions, and no update channel that fetches a new
client: a player gets a new client build from whoever distributes it. Whichever side goes first is
refused until the other follows, so the gap is shortest when the new client builds are ready when
the new workers start.

What players see at each moment of an update that changes a version:

- While the workers are stopped, a connected player returns to character select with the text of
  `server_shutdown`.
- While an old worker still runs, an updated client that joins is refused with `shard_not_updated`,
  "This shard has not been updated to your version of the game yet. Try again in a few minutes."
- Once the new worker runs, a client that was not updated is refused with `incompatible_protocol`,
  "Your game is out of date. Update it to play." A change of the simulation revision alone gives
  `incompatible_simulation`, and a change of a World's collision data alone `incompatible_world_data`;
  both texts tell the player to update the game, or to try again in a few minutes if it is up to
  date.
- A player whose connection dropped during the update meets the same refusal when the client
  reconnects, and returns to character select with its text.
- A rollback of the servers to an older version puts the updated clients behind, so they get
  `shard_not_updated`.

Every refusal happens at the join request, before a ticket exists, and counts in
`game_service.placement.join.refusals` by Shard and reason, as [Monitoring](MONITORING.md#gameservice)
describes: after an update, a rising `incompatible_protocol` is the number of players who have not
updated yet.

## Back Up PostgreSQL

PostgreSQL holds every account, character, item, and corpse; Redis holds nothing that cannot be
rebuilt, and the proxy's authority and the images are rebuilt from the volumes and the sources. Back
up the database on a schedule and before every update. In the stack, a custom-format dump goes to
stdout, which cmd on Windows and any POSIX shell write to a file without changing a byte:

```powershell
cmd /c "docker compose -p shooter-mmo-deploy -f deploy/docker-compose.production-like.yml --env-file deploy/stack.env exec -T postgres pg_dump -U shooter_mmo -d shooter_mmo -Fc > backup.dump"
```

Restore into an empty database of the same server, here a scratch one to check the backup, and
drop it after:

```powershell
docker @compose exec -T postgres createdb -U shooter_mmo restore_check
cmd /c "docker compose -p shooter-mmo-deploy -f deploy/docker-compose.production-like.yml --env-file deploy/stack.env exec -T postgres pg_restore -U shooter_mmo -d restore_check --no-owner < backup.dump"
docker @compose exec -T postgres dropdb -U shooter_mmo restore_check
```

To restore the real database, stop the workers and GameService, restore into an empty
`shooter_mmo`, and start them again in the order above. A backup that was never restored is not a
backup: restore each kind of backup once to a scratch database and compare its tables. The dump
holds personal data, so keep it as the database itself is kept. Nothing runs these commands on a
schedule yet.

## The Economy Audit

GameService audits its own database: whether every item is in exactly one place, whether every
slot, stack, and definition is valid, whether every character's gold equals its ledger, and what
the retained history says about conservation. It reads in one snapshot and repairs nothing. The
checks and what they cannot answer are in
[Economy Audit](GAME_SERVICE_MODULES.md#economy-audit), and the settings are the `Audit:*` rows of
[Configuration Defaults](SERVICE_FEATURES.md#gameservice).

- **It runs by itself.** `Audit:Enabled` is on outside Development, so GameService in the stack runs
  the audit at start and then every `Audit:IntervalSeconds`, 3600, which is hourly. Nothing in the
  Compose file sets it. Each run logs one line at `Information`, an `Error` line for every check
  that found a violation with examples by id, and a `Warning` for a check that could not run.
- **It is measured.** The meters `game_service.audit.runs` by outcome, `last_run_timestamp`,
  `last_run_duration`, and `violations` by check go to the monitoring stack with the
  `-Telemetry` overlay, and two alerts watch them: `EconomyAuditViolations`, critical, when a check
  found a violation in its latest run, and `EconomyAuditFailed`, warning, when a run could not run
  every check in two hours. [Monitoring](MONITORING.md#gameservice) lists the meters and
  [the alerts](MONITORING.md#alerts) what each means.
- **An operator runs it by hand** from the GameService image, like `migrate`:

  ```powershell
  docker @compose run --rm migrate audit
  docker @compose run --rm migrate audit --json
  ```

  The exit code is 0 when the economy is consistent, 1 when a check found a violation, and 2 when
  the audit could not run, also when `migrate` would still change the database. It reads the
  database only, so it can run beside a live GameService, and the report names every check with
  what it examined, as
  [Audit The Economy](LOCAL_DEVELOPMENT.md#audit-the-economy) describes. Run it before and after
  an update and after a restore, and keep the report of a violation: the examples name ids and
  never a name.

## What To Watch

| What | Where | Healthy |
| --- | --- | --- |
| GameService is ready | `GET /health/ready` through the proxy, and the container health | `200` with the status `ready`, and both dependencies reachable |
| The Shard is online | `GET /api/shards` | `isOnline` is true with free capacity; false means no worker with a valid registration holds it |
| Every container is healthy | `docker compose ps` | `healthy`, and `migrate` exited with 0 |
| No service logs an error | The console logs, level `fail` and `crit` for the .NET services | None |
| The economy audit | The `Error` lines of the scheduled audit in GameService's log, `docker compose run --rm migrate audit`, and the alerts `EconomyAuditViolations` and `EconomyAuditFailed` | The audit ends `clean` and finds no violation |
| The service meters and the alerts | The monitoring stack of [Monitoring](MONITORING.md), with the `-Telemetry` overlay | No alert fires |

[Monitoring](MONITORING.md) owns the metrics, the logs, the dashboards, and the alerts, including
the tick time of the worker, the Shard gauges, the database and Redis counters, and what each
alert means. The alerts have no notification target yet, as
[Not There Yet](#not-there-yet) says, so someone has to look at the dashboards.

## Sizing

[Stack Stress Testing](STACK_STRESS_TESTING.md#measured-tiers) owns the measurements, taken on one
machine with 16 logical processors where the generator, the worker, GameService, PostgreSQL, and
Redis shared the cores. Read the numbers from there; these are the ones that size a deployment:

| Players on one Shard | Worker CPU | Worker sends per player | Worker memory |
| --- | --- | --- | --- |
| 100 | 15 percent of one core | 5.6 KiB/s | below 250 MiB |
| 250 | 38 percent of one core | 9.8 KiB/s | below 250 MiB |
| 500, the scale target | 82 percent of one core | 16 KiB/s | below 250 MiB |

- **Worker.** One Shard is one worker, and the worker's loop thread is one core: give it a core
  of its own with room. The upstream bandwidth is about 16 KiB/s per player, 8 MB/s or about 64
  Mbit/s for 500, plus the players' much smaller inputs. The worker keeps 30 ticks per second
  inside a 33.34 ms budget, and a host that cannot keep it shows in the tick time of
  [Monitoring](MONITORING.md). Two cores and 1 GiB for 500 players is a sensible start, not a
  measured figure.
- **GameService.** It held about 250 MiB in the 30-minute soak at 500 players. Two cores and 1 GiB
  is a start here too. Its pool of PostgreSQL connections is at most `Database:MaximumPoolSize`, 64.
- **PostgreSQL.** The database work grew with the logins, the joins, and the item operations of
  the tiers, with no deadlock and no lock wait above the timeout. Give it a disk with room for the
  history that [Persistence And Maintenance](SERVICE_FEATURES.md#persistence-and-maintenance)
  keeps for 14 days, and memory for its working set.
- **Redis.** It holds sessions and counters only; a small instance is enough.
- **The proxy.** It forwards small JSON requests, and the content files once per client start.

A tier holds only when the host that runs it has at least the cores the measurement had left for
it, so a smaller host needs its own measurement before the number is believed.

## The Smoke Test

`tools/Test-DeployStack.ps1` starts the stack, plays through it with no Development switch, stops
it in order, and checks the stop. Exit code 0 means every check held:

- Through the TLS proxy, trusting the stack's own root certificate and nothing else, it registers
  accounts, logs in, creates characters, lists the Shards, joins, moves for half a minute over UDP,
  leaves, and logs out.
- It checks `/health/ready`, that the Development routes are absent, that a request that forges
  `X-Forwarded-For` is limited as its real client, and that the worker routes refuse a missing
  secret, a wrong secret, another worker's id, and a player's session.
- After the play, the `audit` command of the GameService image reads the whole database and must
  find the economy that the players left consistent, with exit code 0.
- A player stays in the world through the stop. The worker stops first and writes its last
  checkpoint, goes offline, and tells that player `server_shutdown`. GameService stops next, with
  a watcher asking the proxy for readiness: it must see `ready`, then `503 stopping` while
  GameService still serves, then the proxy taking it out of rotation. Everything else stops, every
  container ends with exit code 0, and no service logged an error.

The player path is `tools/DeploySmokeTest`, built on the bot client library, so it speaks the
client's side of the protocol as the other bots do and changes with them. CI builds the images for
every pull request, and `.github/workflows/deploy-smoke.yml` runs the whole test once a week and
on request on a GitHub-hosted Linux runner, keeping the logs of every service for two weeks.

## Not There Yet

- A real host: a server, its operating system, its firewall, and its monitoring of the host itself.
- A domain name and a certificate for it; the stack uses a local authority for `localhost`.
- An image registry. The images are built where they run, and nothing pushes them anywhere.
- Automated deployment: the update steps above are by hand, and nothing rolls back.
- A secret store: the stack keeps its secrets in one environment file, and `docker inspect` shows
  them to anyone with Docker access.
- Scheduled backups and a tested restore routine for PostgreSQL, and any high availability for it.
- More than one GameService instance, one worker per Shard beyond the first, and a production
  topology with its own Fleets, Nodes, Shards, and Worlds.
- Alert notifications to a chat or a pager, and a monitoring stack that runs beside a deployment.
- Account recovery: a player who loses a password has no way back in, as the open items of
  accounts in [Project Overview](PROJECT_OVERVIEW.md#maturity-by-system) say.

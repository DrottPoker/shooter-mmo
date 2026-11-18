# Service Features

This document owns the implemented server behavior as seen from outside the
processes: the GameService HTTP API and its authentication, the error codes,
the timings, limits, defaults, and configuration options, persistence and
maintenance, the implemented behavior of each server area, and what is not
implemented yet.

The topology and the boundaries behind this behavior are in
[Project Architecture](PROJECT_ARCHITECTURE.md), the GameService module
internals in [GameService Modules](GAME_SERVICE_MODULES.md), the wire format in
[Realtime Protocol](REALTIME_PROTOCOL.md), and the meters and logs in
[Monitoring](MONITORING.md). The game rules are in [Game Design](GAME_DESIGN.md),
the item rules in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), the
world actor model in [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md),
and what a player sees in [Game Features](GAME_FEATURES.md).

## GameService HTTP Surface

| Route | Authority | Behavior |
| --- | --- | --- |
| `GET /` | Public, rate limited | Redirect to `/health/ready` |
| `POST /api/accounts/register` | Public, rate limited | Create an account and its login session |
| `POST /api/accounts/login` | Public, rate limited | Replace the active login session |
| `GET /api/accounts/me` | Account session, rate limited | Return the account profile |
| `GET /api/accounts/session` | Account session, rate limited | Validate the current session |
| `POST /api/accounts/logout` | Account session, rate limited | Revoke the current session and its dependent access |
| `DELETE /api/accounts/sessions/{sessionId}` | Account session, rate limited | Revoke an owned session |
| `GET /api/content` | Public, rate limited | Return the content manifest: every served content file with its revision and URL, as [Content Files](#content-files) describes |
| `GET /api/content/{kind}/{id}/{revision}` | Public, rate limited | Return the exact bytes of the current item or quest catalog as an immutable file |
| `GET /api/content/text-table/{locale}/{id}/{revision}` | Public, rate limited | Return the exact bytes of the current text table of a locale as an immutable file |
| `GET /api/characters` | Account session, rate limited | List owned characters with their body models |
| `POST /api/characters` | Account session, rate limited | Create a character with its body model and look, checked against `content/characters/appearance.json`, or with the default look of the default body model when the request names none |
| `GET /api/character-appearance` | Account session, rate limited | Return what character creation offers: the kinds of a look with their rules and every body model with its options per kind and slot |
| `GET /api/characters/{characterId}/item-state` | Owning account session, rate limited | Return one coherent no-store character item-state snapshot, including the currency balance |
| `GET /api/characters/{characterId}/bank` | Owning account session, rate limited | Return focused no-store bank state |
| `GET /api/characters/{characterId}/recovery` | Owning account session, rate limited | Return focused no-store unclaimed Recovery Storage deliveries |
| `GET /api/characters/{characterId}/quests` | Owning account session, rate limited | Return the no-store quest log: active quests with their current step and its progress, and completed quests |
| `POST /api/characters/{characterId}/item-operations/relocate` | Owning offline account session, rate limited | Relocate an item through the durable transaction kernel. Bank and Recovery Storage are never reachable offline |
| `POST /api/characters/{characterId}/item-operations/split` | Owning offline account session, rate limited | Split a stack into an exact destination slot |
| `POST /api/characters/{characterId}/item-operations/merge` | Owning offline account session, rate limited | Merge compatible stacks |
| `POST /api/characters/{characterId}/item-operations/destroy` | Owning offline account session, rate limited | Perform allowed player destruction with a server-owned reason |
| `GET /api/shards` | Public, rate limited | List logical shards, status, players, and capacity |
| `POST /api/shards/{shardId}/join` | Account session, rate limited | Place an owned character and issue a ticket: its public id and its transport key |
| `POST /api/simulation-workers/{workerId}/heartbeat` | Worker service policy | Register or renew the exact worker runtime and return the lease and the Shard's rule set |
| `POST /api/simulation-workers/{workerId}/offline` | Worker service policy | Release the exact worker runtime's authority |
| `POST /api/simulation-workers/{workerId}/session-heartbeat` | Worker service policy | Renew the exact simulation leases of the worker runtime's sessions in one batch, and answer each session on its own: its lease with the end of its account session and its carry state, or its refusal |
| `POST /api/simulation-workers/{workerId}/checkpoint` | Worker service policy | Save the place and vitals of the worker runtime's characters whose state changed, in one batch |
| `POST /api/simulation-join-tickets/consume` | Worker service policy | Consume an exact-runtime join ticket that the client's proof proves, and return the session, the ticket's transport key, and the character's saved place and vitals |
| `POST /api/simulation-sessions/{id}/release` | Worker service policy | Release an exact simulation lease, with the character's final place and vitals when the worker has them |
| `POST /api/simulation-sessions/{id}/appearance` | Worker service policy | Lower or raise the exact-session character's headwear, or in the Development environment only store another body model and look |
| `POST /api/simulation-sessions/{id}/item-operations` | Worker service policy | Execute one exact-session item mutation, Recovery claim, insurance or quest item action, vendor trade, or gathering yield through the durable kernel; Items runs its own kinds and the Economy module the vendor and gathering kinds |
| `POST /api/simulation-sessions/{id}/quest-log` | Worker service policy | Read the exact-session character's quest log when it joins the worker's Shard |
| `POST /api/simulation-sessions/{id}/quest-operations` | Worker service policy | Run one quest action of the session's character through the durable kernel: accept, abandon, grant start items again, talk, deliver, turn in, count a credited kill, or, in Development only, a quest tool of the Admin menu |
| `POST /api/simulation-sessions/{id}/quest-drops` | Worker service policy | Read how much of the quest drops that a dying Mob rolled for the session's character the character's quests still need, before the worker lays out a live corpse |
| `POST /api/simulation-sessions/{id}/ammunition-consumptions` | Worker service policy | Commit one idempotent batch of shots from the exact-session character's equipped ammunition stack |
| `POST /api/simulation-sessions/{id}/player-deaths` | Worker service policy | Partition one exact-session authoritative player death into durable corpse and Recovery custody |
| `GET /api/simulation-workers/{workerId}/corpses` | Exact worker service policy | Restore open unexpired corpses for the worker's fresh runtime and active Shard assignment |
| `POST /api/simulation-workers/{workerId}/mob-corpses/durable` | Exact worker service policy | Create one durable Mob corpse for the worker's runtime and Shard, idempotent by the operation id the worker derives from the death event |
| `POST /api/simulation-sessions/{id}/corpses/{corpseId}/open` | Worker service policy | Read one exact-session, unexpired, same-Shard corpse snapshot after worker proximity validation |
| `POST /api/simulation-sessions/{id}/corpses/{corpseId}/item-operations` | Worker service policy | Commit bidirectional full or partial corpse transfers, whole-item loot into an equipment slot, a corpse Bag into an empty Bag slot, Loot all, and atomic ordinary, equipment, or Bag aggregate swaps through the durable kernel |
| `POST /api/simulation-sessions/{id}/mob-loot-grants` | Worker service policy | Grant one live Mob loot entry into the slot or stack the client named, idempotent by its grant id |
| `POST /api/simulation-sessions/{id}/mob-loot-grants/loot-all` | Worker service policy | Grant every live Mob loot entry that fits, whole, in one transaction, and name every grant that left the corpse |
| `POST /api/development/simulation-bots/join-tickets` | Development loopback secret | Issue an in-memory exact-runtime bot ticket when explicitly enabled |
| `POST /api/development/stack-stress/characters/{characterId}/inventory-fixture` | Account session and Development loopback secret | Seed the starting items of a stack stress inventory workload for an owned empty character when explicitly enabled |
| `POST /api/development/stack-stress/characters/{characterId}/kit` | Account session and Development loopback secret | Grant an owned empty character the configured kit of its stack stress role, such as a weapon and its arrows, when explicitly enabled |
| `POST /api/development/stack-stress/loot-hotspot` | Account session and Development loopback secret | Create the shared loot corpse of a stack stress workload when explicitly enabled |
| `GET /health/live` | Public, rate limited | Report process liveness |
| `GET /health/ready` | Public, rate limited | Verify obligatory dependencies; `503` with the status `stopping` from the stop signal on, as [Orderly Shutdown](#orderly-shutdown) describes |

Every route's limit is in [Rate Limits](#rate-limits). Route parameters named
`characterId`, `sessionId`, `simulationSessionId`, and `corpseId` must be GUIDs. The public shard list never exposes a
SimulationWorker host, port, or runtime id. That endpoint appears only in a
successful short-lived placement response.

The development bot route is absent unless GameService runs in `Development`
with `DevelopmentSimulationBots:Enabled=true`. It also requires a loopback
caller, judged by [the client's address](#the-clients-address), and the
separate `ACTIVE_SIMULATION_BOTS_SECRET`. Bot identities,
tickets, and sessions remain in memory and are never durable account or
character data. SimulationWorker uses the same worker service routes for real
and bot credentials; GameService routes recognized bot credentials to the
in-memory authority and every other request to the PostgreSQL services. The
guards, the ticket rules, and the connections kept for real players are
described in
[Architecture And Safety Boundary](ACTIVE_SIMULATION_BOTS.md#architecture-and-safety-boundary).

The stack stress fixture routes are absent unless GameService runs in
`Development` with `StackStressFixtures:Enabled=true`. They also require a
loopback caller, judged by [the client's address](#the-clients-address), the
`X-Stack-Stress-Fixture-Key` header with
`STACK_STRESS_FIXTURE_SECRET`, a loopback PostgreSQL host, and a database name
that contains `stress` or `test`; startup fails when an enabled fixture lacks
any of these. They grant only the item definitions that configuration names,
through the item transaction kernel. [Stack Stress Testing](STACK_STRESS_TESTING.md)
uses them.

## Rate Limits

Every route that a client can reach without worker credentials has a
fixed-window limit. A rejected request gets `429` with `rate_limit_exceeded` in
Problem Details and a `Retry-After` in whole seconds that counts down to the end
of the current window, and the next window starts with its full permits. Limits
per client address use [the client's address](#the-clients-address), with one
window per IPv4 address and per IPv6 `/64` network, because one IPv6 host
usually holds a whole `/64`. Limits per account use the account of the session,
so many accounts behind one address, such as the 500 bots of a load run, never
share a window.

| Routes | Caller | How often a legitimate client calls | Limit |
| --- | --- | --- | --- |
| `POST /api/accounts/register`, `POST /api/accounts/login` | Anonymous | Once per client start, and again after a mistyped password | `register` and `login`: `RateLimiting:Authentication`, 5 per 60 s per client address, each route in a window of its own, also in Redis across replicas as [Redis Account-Session Acceleration](#redis-account-session-acceleration) describes |
| `GET /api/content` and the two content file routes | Anonymous | The manifest and about seven files at each client start | `content`: `RateLimiting:Content`, 120 per 60 s per client address, one window for the three routes |
| `GET /api/shards`, `GET /`, `GET /health/live`, `GET /health/ready` | Anonymous: the client's Shard list, and the health checks of a proxy or an orchestrator | The Shard list when character select opens or refreshes; a health check every few seconds | `public`: `RateLimiting:Public`, 300 per 60 s per client address, one window for the four routes |
| `POST /api/shards/{shardId}/join`, `POST /api/characters`, `POST /api/accounts/logout`, `DELETE /api/accounts/sessions/{sessionId}`, and the four offline `item-operations` routes | Account session | Once per join, new character, logout, revoke, or item move, with a few retries | `account-action`: `RateLimiting:AccountActions`, 20 per 60 s per account, each route in a window of its own |
| `GET /api/accounts/me`, `GET /api/accounts/session`, `GET /api/characters`, `GET /api/character-appearance`, and the character's `item-state`, `bank`, `recovery`, and `quests` | Account session | A few at login and character select, and when a window opens | `account-read`: `RateLimiting:AccountReads`, 300 per 60 s per account, one window for these routes |
| The worker routes | SimulationWorker with its service credentials | Every heartbeat, checkpoint, and player action of a Shard | None: they need worker credentials, and the worker bounds its own requests |
| The development bot ticket and stack stress fixture routes | Development tools with their own secret | Hundreds at once for a bot population | None: they exist only in Development and answer only loopback callers |

The account limits run after authentication and authorization, so a request
without a valid session is refused with `401` before it counts. There is no
route that deletes a character.

## Behind A Proxy

GameService speaks plain HTTP. In a deployment a reverse proxy ends TLS in front
of it, so GameService has no HTTPS redirection and no HSTS, and no CORS either,
because the client is a native program and never a browser page. Every response
carries `X-Content-Type-Options: nosniff`, so a browser that opens an API URL
never guesses a content type for its body.

### The Client's Address

One place decides the client's address, and the rate limits, the
`ClientAddress` value of the log scope, and the loopback checks of the
development routes all use it:

- Without `Http:TrustedProxies`, the default and the local stack, the address
  of the connection is the client's, and `X-Forwarded-For` and
  `X-Forwarded-Proto` are ignored entirely.
- With trusted proxies, a request whose connection comes from one of them
  takes its client address from `X-Forwarded-For` and its scheme from
  `X-Forwarded-Proto`, counting at most `Http:ForwardLimit` entries from the
  right. A proxy appends the address it saw, so with one proxy only the entry
  it added counts, and whatever the client wrote before it is ignored.
- A request that names a forwarded address but does not come from a trusted
  proxy keeps the address of its connection and is served normally.
- A client behind a proxy on the same host is therefore not loopback for the
  development routes, although its connection is.

Behind a proxy, `Http:TrustedProxies` must name the proxy, or every client
shares the proxy's address and its per-address limits. At startup GameService
logs whether it trusts proxies and which.

### Request Limits

Kestrel serves HTTP/1.1 with these limits from the `Http` section, listed in
[Configuration Defaults](#gameservice): request headers of at most 16 KiB
together and 64 in number, a request line of at most 4 KiB, 15 seconds to
receive the headers, and keep-alive connections that close after 130 seconds
idle, longer than the idle timeout of a proxy's upstream connections, so the
proxy closes them first. A request body may have at most
`Items:Operations:MaximumHttpRequestBodyBytes`. Kestrel refuses a request over
a limit with `431`, `414`, `413`, or `408` before any route runs. The number of
connections is not limited, so 500 load bots from one machine and a proxy's
connection pool both fit.

### Hosts, Secrets, And Passwords Outside Development

Outside the Development environment GameService refuses to start, with one
message that lists every problem, when:

- `AllowedHosts` is missing or contains `*`, `0.0.0.0`, or `[::]`, each of
  which ASP.NET Core reads as any host. It names the host names that
  requests reach GameService with, separated by `;`: the public name that the
  proxy passes on and the names that health checks and SimulationWorker use. A
  request for another host gets `400` before any route runs. Development keeps
  `*` in `appsettings.Development.json`.
- `SIMULATION_WORKER_SERVICE_SECRET` is set, or the secret of a worker is one
  that the repository publishes, such as the one of the load tooling. Every
  worker has its own secret in
  `ServiceAuthentication:SimulationWorkers:<workerId>`, and sets the same value
  as its own `SimulationWorker:GameServiceSecret`. Development keeps the shared
  secret, so the local stack and the tools work as before.
- The password of `ConnectionStrings:Postgres` or `ConnectionStrings:Redis` is
  empty or a placeholder: a value that starts with `replace-with-`, as in
  `.env.example`, or one that the repository's Compose files or CI workflow
  publish. Redis takes its password as `password=` in the connection string,
  and the readiness probes of GameService and SimulationWorker authenticate with
  it.

SimulationWorker applies the same rules to its own settings and refuses to
start, with one message that lists every problem, when outside Development:

- `SIMULATION_WORKER_SERVICE_SECRET` is set. The worker's secret is
  `SimulationWorker:GameServiceSecret`, the one that GameService holds for its
  worker id.
- `SimulationWorker:GameServiceSecret` is a placeholder or a secret that the
  repository publishes, or `ConnectionStrings:Redis` has an empty, placeholder,
  or published password.
- `SimulationWorker:AdvertisedHost` is a wildcard address, `0.0.0.0` or `::`,
  which no player can connect to.
- `SimulationWorker:GameServiceBaseUrl` is plain `http` to an IP address that
  is neither private, loopback, nor link-local, which would send the worker's
  secret in the clear over a public address. A host name is not judged, so
  keeping that call on a private network or behind TLS stays the operator's
  rule, as [Deployment](DEPLOYMENT.md#the-call-between-worker-and-gameservice)
  describes.

### Orderly Shutdown

On the stop signal, Ctrl+C or `SIGTERM`, GameService stops in this order:

1. `/health/ready` answers `503` with the status `stopping` at once, without
   checking its dependencies, so a proxy takes GameService out of rotation.
2. For `Http:ShutdownDrainSeconds`, 10 by default and 0 in Development,
   GameService keeps accepting and serving requests, so the proxy has time to
   notice. Set it longer than the proxy's health check interval times the
   failures it needs.
3. Kestrel stops accepting connections and lets the requests in flight finish.
4. The background services stop: the retention host, the Recovery and corpse
   expiry, and the metric reporters. Each ends at its next wait on the stop
   token. A batch or item operation that the stop interrupts rolls back its
   transaction, and the next pass or start runs it again. Queued password
   hashes are refused with `auth_busy`.

All of it must fit `Http:ShutdownTimeoutSeconds`, 30 by default, the host's
shutdown timeout, which must exceed the drain; whatever still runs then is
abandoned. A container runtime must wait at least that long before it kills the
process.

### Container Health Checks

The service images have no shell, `curl`, or `wget`, so the .NET services answer
their own container health checks. The first argument `probe` runs before any
host or configuration exists, prints one line that says why, and exits with `0`
when healthy and `1` otherwise:

- `probe http <url> [timeoutSeconds]` is healthy when the URL answers with a
  2xx status within the timeout, 3 seconds by default and at most 60.
  GameService's check is `probe http http://localhost:8080/health/ready`, so it
  turns unhealthy at the stop signal and while PostgreSQL or Redis does not
  answer. The line names the host, port, and path of the URL and never its query.
- `probe file <path> <maxAgeSeconds>` is healthy when the file was written
  within the age. SimulationWorker has no HTTP surface, so while it is ready,
  with its UDP port bound, a valid registration lease, and a realtime loop that
  completed a tick within the last 5 seconds, it writes the file of
  `SimulationWorker:HealthFilePath` every 5 seconds, deletes it when it is not
  ready and when it stops, and its check is `probe file <path> 20`. A worker
  without the setting writes no file.

The loop condition is the one that a hung worker fails: its port stays bound
and the registry heartbeat, a service of its own, keeps renewing the lease, so
without it a worker whose loop thread deadlocks would look ready while nobody
can play. The loop writes the time of every completed tick with one volatile
write, the file service reads it, and a silence longer than 5 seconds, 150
ticks, removes the file and logs one warning for the episode that names how
long the loop was silent. The warning does not repeat until the loop ticked
again, which also returns the file; a worker that is stopping is not judged,
and a worker whose loop never ticked logs its own line. The check only labels
the container: a runtime that restarts containers on exit does not act on an
unhealthy one, as [Health Checks](DEPLOYMENT.md#health-checks) describes.

`--health-check-only` of the worker is the check of a worker that does not run:
it loads all configuration and content and needs the UDP port that a running
worker holds, so it cannot be the health check of a container.

## Accounts And Authentication

- Email and username are normalized and unique. An email must be a plain
  address; display-name forms such as `Name <a@b.c>` are rejected.
- Passwords are hashed with BCrypt at work factor 12. A password must be 8 to
  128 characters and at most 72 UTF-8 bytes, because BCrypt ignores later bytes.
- Login releases its database connection before the password check, and an
  unknown login is checked against a fixed hash, so response time does not show
  whether an account exists. Registration still reports whether an email or
  username is taken until an email verification flow exists; its rate limit
  bounds enumeration.
- Every password hash and check goes through the password hashing lane, as
  [Password Hashing Lane](#password-hashing-lane) describes, so a burst of
  logins and registrations waits or is refused with `auth_busy` instead of
  taking every core and thread of GameService.
- Account tokens are opaque and only their SHA-256 hashes are stored.
- PostgreSQL enforces one unrevoked account session per account. Its lifetime
  is described in [Account Session Lifetime](#account-session-lifetime).
- A successful login locks the account, revokes the older session with the
  stored reason `session_replaced` and the client error code
  `account_session_replaced`, consumes its pending tickets, marks its active
  simulation sessions as [Account Session End](#account-session-end)
  describes, and creates the replacement session transactionally.
- Logout and manual revoke use explicit revocation reasons and dependent cleanup.
  They lock the account first, like login and join, and clean up only sessions
  that belong to the caller. Revoking another account's session id succeeds
  without effect, so foreign session ids are not revealed.

Login and registration use independent fixed-window limits per
[client address](#the-clients-address),
`RateLimiting:Authentication:PermitLimit` requests per
`RateLimiting:Authentication:WindowSeconds`, rejected as every limit is, as
[Rate Limits](#rate-limits) describes. The policy is taken from the matched
endpoint, so a different path casing such as `/api/accounts/LOGIN` cannot skip
the distributed limit.

### Account Session Lifetime

An account session ends at the earlier of its last activity plus
`Auth:SessionIdleLifetimeDays`, 14 by default, and its login plus
`Auth:SessionMaximumLifetimeDays`, 90 by default. `account_sessions.expires_at`
holds that end and `created_at` the login:

- A login or registration starts the session with the end of the idle lifetime,
  and its answer's `expiresAt` is that end. Activity moves it later, so a
  client treats it as the end at the time of the answer, not as a deadline, and
  takes a `401` as the only sign that the session ended.
- Two things count as activity. An authenticated request whose token is
  resolved from PostgreSQL, which happens when the
  [session cache](#redis-account-session-acceleration) has no entry for it,
  about once a minute for a client in use. And a live simulation session bound
  to the account session, through the
  [session heartbeat](#session-heartbeat), because a player in the World makes no
  requests of its own.
- Activity moves the end to the idle lifetime from now, never past the maximum
  lifetime, and writes it only when that moves it by more than an hour. The
  condition is part of the statement that writes, so a session in use costs at
  most one write an hour, and a check that moves nothing writes nothing.
- A session is active while it is not revoked and either its end has not passed
  or a live simulation session holds it: one that is neither released nor past
  its lease, also while the character's body stays after a leave. The clock
  therefore never ends the session of a player in the World. Once the player's
  simulation session is released, a session past its end ended at once, and the
  next request answers `401` `invalid_session_token`, as for any expired session.
- A logout, a revoke, and a new login end a session at once, also while its
  player is in the World, as [Account Session End](#account-session-end)
  describes. A session that ended is never extended again.

### Password Hashing Lane

BCrypt costs about a quarter of a second of one core per hash at work factor
12, so GameService runs every hash and check of a password in one lane of its
own, apart from the threads that serve requests:

- At most `Auth:PasswordHashing:MaxConcurrency` hashes run at once, half of the
  logical processors by default, on the lane's own threads below normal
  priority, so the rest of GameService keeps its thread pool and the other
  cores.
- A registration or login that finds every lane thread busy waits in a
  first-in first-out queue. The lane measures how long a hash takes, and a
  request that would wait longer than `Auth:PasswordHashing:MaxWaitSeconds`,
  10 by default, behind the requests ahead of it is refused at once, so a
  refused request never waited: the queue holds as many requests as the lane
  threads hash within that wait. An admitted request keeps its place until it
  is hashed or its caller gives up, so when hashes slow down after it was
  admitted, it can wait longer than `MaxWaitSeconds`; a caller that
  disconnects leaves the queue.
- A refused request gets `503` with the code `auth_busy` in Problem Details and
  a `Retry-After` in whole seconds, the time the queue ahead needs to drain,
  at least 1 and at most `MaxWaitSeconds`. Nothing of the request is written,
  and the client tries again after `Retry-After`, as
  [LoginMenu](UNITY_CLIENT_ARCHITECTURE.md#loginmenu) describes.
- A login of an unknown account goes through the same queue and the same
  check against the fixed hash as a known one, so it costs the same and is
  refused the same way.
- On shutdown the queued requests are refused with `auth_busy`, and nothing
  waits for them.

The lane's meter and its warning are in [Monitoring](MONITORING.md#gameservice).

## Character Access

- Character names are validated and normalized.
- The character list and a newly created character name each character's id,
  name, balance, creation time, body model, and level, which the client reads
  for quest availability.
- Character ownership is checked on every protected operation.
- The configured maximum character count is enforced transactionally.
- Soft-deleted characters are excluded from active queries.
- Simulation placement locks both account and character rows.

PostgreSQL permits only one active simulation session per character and one per
account. A copied account token therefore cannot run two different characters
at the same time.

## Character Currency

- `characters.currency` is the one balance of a character; the currency rules
  are in
  [Character Currency](INVENTORY_AND_DEATH_LOOT_DESIGN.md#character-currency).
  Every change goes through the Characters wallet in the caller's transaction
  and writes one ledger row, as
  [Character Wallet](GAME_SERVICE_MODULES.md#character-wallet) describes.
- A debit that the balance does not cover is rejected with
  `currency_insufficient`, and a credit above the maximum balance with
  `currency_limit_exceeded`. Neither changes the balance.
- New characters receive the catalog's starting balance with a
  `starting_balance` ledger row in the character creation transaction.
- The item-state snapshot carries the balance from the same repeatable-read
  snapshot as the items, and every in-world currency change runs inside an item
  transaction, so it advances the item-state revision that clients refresh on.
- A player death never reads or writes currency.
- The [economy audit](GAME_SERVICE_MODULES.md#economy-audit) checks that every
  balance equals the sum of its ledger and that every ledger row has its
  reason.
- `--dev-items-currency` credits an offline character for local testing, as
  [Development Item Commands](#development-item-commands) describes.

## Topology Bootstrap

`src/Server/GameService/Config/appsettings.json` owns the checked-in topology
bootstrap in `Simulation:Topology`: Fleets with display names and region codes,
Nodes assigned to Fleets, and Shards assigned to a Fleet with a display name
and a rule set. World definitions come from the World manifests below
`WorldData:WorldsPath`, not from the topology, and no Shard names a World.

Startup validation rejects empty collections, invalid identifiers, duplicates,
unknown references, invalid display names, invalid region codes, and missing
rule sets. After migrations, `SimulationTopologySeeder` idempotently mirrors the
World definitions and upserts the configured records. It does not manufacture
online workers. A seeded shard remains offline until a valid heartbeat and
active assignment exist. The checked-in topology seeds two shards in
`local-fleet` with the rule set `mvp-open-risk`: `local-shard-1`
(`Local Shard 1`), which the default worker binds to Development World 2, and
`local-shard-2` (`Local Stress Arena`), which the SimulationWorker `StressArena`
launch profile binds to Development World 1.

A worker heartbeat binds a shard to the worker's configured `WorldId`, and a
different `WorldId` rebinds it only under the rules in
[Topology Model](PROJECT_ARCHITECTURE.md#topology-model). An unknown World or a
blocked rebind is a definitive worker authority failure:
`simulation_world_not_found` or `shard_world_rebind_blocked`. Before retrying a
rebind, stop the worker cleanly and drain the World-local state, as
[Rebind An Offline Shard](LOCAL_DEVELOPMENT.md#rebind-an-offline-shard-to-another-world)
describes. The next worker runtime must report the rebound World identity, so
stale content cannot silently simulate the shard. A migration placeholder
assignment with no worker runtime is not simulation authority and therefore
does not block a rebind.

## Worker Registration And Assignment

Each SimulationWorker has:

- A stable `SimulationWorkerId`.
- A generated `RuntimeId` for the current process generation.
- Fleet, Node, Shard, and World configuration.
- An advertised UDP endpoint.
- Maximum and active connection counts.
- Protocol, simulation, collision, item catalog, and quest catalog revisions.
- A process start timestamp.

GameService validates identifiers, topology consistency, endpoint metadata,
capacity, revisions, and service identity. A heartbeat of the runtime that
already owns the Shard, with the World the Shard is bound to, only renews the
lease and waits for no player operation, join, or corpse; the locks of both
paths are in [Lock Order](GAME_SERVICE_MODULES.md#lock-order). Every other
heartbeat is a registration, which locks the target shard and applies the rules
in
[Runtime Identity](PROJECT_ARCHITECTURE.md#runtime-identity-and-assignment-safety):

- A different healthy assigned worker causes `shard_assignment_conflict`.
- A timed-out or explicitly offline owner can be replaced.
- Replacement invalidates old-runtime tickets and simulation sessions even when
  the logical worker moves to another shard.
- Worker secrets are matched to the exact, case-sensitive worker id.
- The older runtime is fenced by `worker_runtime_changed`.
- A stale process cannot mark a newer runtime offline.
- Active assignment indexes prevent two worker or shard owners.

A worker is healthy while its heartbeats arrive within
`Simulation:WorkerHeartbeatTimeoutSeconds`. It heartbeats every
`SimulationWorker:RegistryHeartbeatSeconds`, and after a heartbeat that timed
out, could not reach GameService, or got a server error, every
`SimulationWorker:RegistryHeartbeatRetryMilliseconds` until one succeeds. A new
runtime keeps retrying for `SimulationWorker:RegistryAcquireTimeoutSeconds`
while a previous runtime's lease is still live. A heartbeat of the same runtime
renews its registration however long GameService did not hear from it, as long
as no other runtime took the worker id over.

Every heartbeat response also names the rule set of the Shard. SimulationWorker
applies the rule set with that id from its combat rules and stops with
`shard_rule_set_unknown` when its rules do not define it.

SimulationWorker stores the database-issued lease duration locally, measured
from the moment the heartbeat request was sent. It rejects new UDP joins without
a valid registration lease. The end of that lease does not stop the worker: it
plays on without GameService for the outage tolerance, as
[GameService Outage](#gameservice-outage) describes. A definitive topology,
assignment, runtime, or service credential rejection stops the process at once.

## GameService Outage

The worker plays on for up to `SimulationWorker:GameServiceOutageToleranceSeconds`,
120 by default, without GameService. Walking, fighting, Mob brains, and
replication go on; everything durable is paused.

- `GameServiceAvailability` is the one place that knows whether GameService
  answers. The registry heartbeat is the probe: one that times out, cannot reach
  GameService, or gets a server error makes it unavailable, and the next
  successful one available again. Any other GameService call that fails that
  way while GameService still counts as available asks for the next registry
  heartbeat at once, no sooner than the retry delay after the last one, so the
  worker learns of an outage within about one timeout. The change to
  unavailable writes one warning, the return one information line with the
  length of the outage.
- While GameService is unavailable, a client action that GameService must
  commit is refused at once with the action's normal rejection and the code
  `game_service_unavailable`, without a GameService call and without entering
  the player's lane: item operations, corpse views and loot, live Mob loot,
  vendor trades, insurance, quest actions at an NPC, quest intents, the start
  of gathering, and look changes. Such an action that waits in a lane when
  GameService stops answering is refused the same way right then, also behind
  work that keeps the lane until GameService returns, such as an arrow batch,
  a gathering that runs stops with that code at its next cycle, and opening or
  closing an interaction or a corpse view still works. An action that was already at GameService when the
  outage began fails as before, with `auth_service_timeout` or
  `auth_service_unavailable`.
- The players learn it: the `game service availability` tick phase sends every
  joined peer a `GameServiceAvailabilityChanged` once per change of the
  availability, and a peer that joins or reconnects while GameService does not
  answer gets one right after its `JoinAccepted`, as
  [Session Messages](REALTIME_PROTOCOL.md#session-messages) describes. The
  Unity client shows one line in the World while saving is paused and forgets
  every shown `game_service_unavailable` refusal once GameService answers
  again, as [Saving Paused Notice](UNITY_CLIENT_ARCHITECTURE.md#saving-paused-notice)
  describes; the bots read the message and do nothing with it.
- The work that must complete waits without spending a timeout and goes as soon
  as GameService answers again, with the operation id it had: a player death,
  a quest kill credit, an arrow batch, the creation of a Mob corpse that needs
  GameService, a durable one or a live one with quest drops, the release of a
  session, and the session heartbeat, whose rounds pause and one of which goes
  right after the outage when one waited for it. The checkpoint pauses too, and
  one goes right after the outage. A dead player stays dead and a dead Mob in
  view until its death committed or its corpse exists.
- A session whose body went while its spent arrows or kill credits had not
  committed is released by the combat work once they did. The orphan sweep,
  which releases a session that neither a peer nor a body has owned for twice
  `SimulationWorker:JoinHandshakeTimeoutSeconds`, leaves such a session: it
  counts the session's time only while GameService answers and releases it
  only once that work stayed pending for the retry horizon below, when the
  work itself is given up.
- A death, a quest kill credit, or a Mob corpse whose outcome stays uncertain
  for `SimulationWorker:UncertainOperationRetryHorizonSeconds`, 600 by default,
  counted only while GameService answers, is given up with one error line that
  names the operation id and kind and a count in
  `simulation_worker.game_service.abandoned_operations`, as
  [Monitoring](MONITORING.md#simulationworker) lists. A kill credit and a Mob
  corpse are dropped. A player death ends that player's session with
  `death_not_saved`, without a lingering body, and releases it without a final
  state, so the character keeps what GameService committed last and joins
  again from its last checkpoint. An outage never uses up the horizon.
- Joins need the local registration lease, which ends about 30 seconds after
  the last successful heartbeat, so a worker that GameService does not answer
  refuses connection requests from then on; before that a join fails with
  `auth_service_timeout` or `auth_service_unavailable`, because its ticket
  cannot be consumed.
- When the last successful registry heartbeat is older than the tolerance,
  `GameServiceOutageMonitor` stops the worker in order: every connected peer
  gets `ServerDisconnect` with `game_service_outage`, the realtime
  shutdown sends nothing that would only spend its timeout, neither arrows,
  credits, nor the last checkpoint, the worker tries once to go offline, and
  the sessions are not released; their leases expire, or a runtime that
  replaces this one releases them. Players lose their place and vitals since
  the last checkpoint. A normal stop and a lost authority tell the peers
  `server_shutdown` instead.
- The session leases outlast the tolerance: `Simulation:SessionLeaseLifetimeSeconds`
  must exceed the worker's outage tolerance plus one session heartbeat interval
  plus one retry, so GameService still holds every session when the worker
  comes back within the tolerance. GameService does not know the worker's
  tolerance, so the worker checks it when a join grants a lease: a lease
  shorter than `GameServiceOutageToleranceSeconds` plus
  `SimulationSessionHeartbeatSeconds` is refused with `join_failed` and one
  error line that names both settings, and the session is released.

## Shard Discovery And Placement

`GET /api/shards` derives status from fresh, online, actively assigned workers.
It aggregates active players and maximum capacity. A shard is online only when
at least one assigned worker is fresh and has free capacity. The unique
assignment constraint means that aggregate contains at most one worker.

Join placement:

1. Validates account session and character ownership.
2. Locks the account and character.
3. Releases expired account simulation sessions.
4. Rejects another active character or cross-shard active session with
   `account_character_already_active` or `character_already_active`.
5. Invalidates older pending tickets for the account, so a retried join is not
   blocked by its own earlier ticket.
6. Selects the assigned worker only when its heartbeat is fresh and it has room,
   or rejects with `shard_unavailable`; a reconnect whose owning worker is
   unavailable gets `simulation_session_worker_unavailable`. Capacity uses the
   greater of worker-reported connections and authoritative unexpired simulation
   sessions, then adds pending tickets in PostgreSQL. A reconnect ticket for an
   already counted session is not added again. In Development, the in-memory
   sessions and pending tickets of synthetic bots are joined into the same
   query, so players and bots never reserve the same connections.
7. Re-reads the assignment after locking the worker, so a worker that moved to
   another Shard in the meantime is never placed.
8. Rejects the join, and a reconnect to the owning worker, with `409`
   `simulation_worker_item_catalog_mismatch` when the worker's last heartbeat
   reported another item catalog revision than the one GameService mirrors.
   Combat data and Mob loot come from the worker's catalog and item custody
   from GameService's, so both must be the same catalog. In the same way it
   rejects them with `409` `simulation_worker_quest_catalog_mismatch` when the
   worker reported another quest catalog revision, because the worker counts
   quest progress that GameService commits with its own catalog.
9. Stores a new ticket bound to shard, worker, runtime, character, and account
   session: a public ticket id and a transport key of 32 random bytes.
10. Computes expiry with `clock_timestamp()` after placement locks are acquired.
    Ticket consumption and session leases use the same clock, so time spent
    waiting for a lock never shortens or extends a ticket or lease.
11. Returns the shard, the exact endpoint metadata, `joinTicketId`, and
    `transportKey` in base64, the only time the key reaches the client.

Selection serializes reservations on the target shard, then locks the selected
worker. A waiting request recalculates session and ticket usage from a fresh
database snapshot before reserving capacity. Different shards remain parallel.

## Join Tickets And Simulation Sessions

Join tickets live `Simulation:JoinTicketLifetimeSeconds` and are used once. A
ticket is a public id, which the client sends over UDP in clear, and a transport
key, which only the join answer and the consume answer carry, as
[Transport Security](REALTIME_PROTOCOL.md#transport-security) describes.
GameService stores the key with the ticket for its lifetime; the consume clears
it, and so does every other end of a pending ticket: a newer ticket of the
account, a logout, a revoke, and the release of the worker runtime.

The consume call takes the ticket id, the client's 16-byte nonce and 32-byte
proof (base64 in JSON), the protocol version of the join request, and the
worker's own worker id, runtime id, and shard id. GameService checks the proof
against the stored key in constant time before it takes a lock or changes
anything:

- A request without a ticket id, a nonce and a proof of their sizes, or a
  protocol version from 1 to 65,535 is refused with `400` `invalid_ticket`.
- A proof that the ticket's key did not make, of an unknown, consumed, or
  expired ticket, or of a ticket without a key, such as one issued before keys
  existed, is refused with `401` `invalid_ticket`, and the ticket stays as it
  was. Tickets live 30 seconds, so a ticket without a key is gone soon after an
  upgrade.
- A proved ticket is consumed, and the answer carries `transportKey`, from
  which the worker derives the keys of the peer's datagrams. The call must
  reach GameService over TLS or a private network, because it carries the key.

Development bot tickets in GameService memory follow the same rules, as
[Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) describes.

Consumption locks the account, character, and character item-state row,
validates account session status, checks exact placement binding, and creates or
rotates the simulation-session token in one transaction. The response includes
the fenced item-state revision, carried weight, capacity, and equipment: every
occupied equipment slot with its item id, definition id, quantity, and item
revision at that item-state revision. It also carries the character's look and
its saved place and vitals, as [Character Place And Vitals](#character-place-and-vitals)
describes. The session heartbeat updates the leases and then reads the current
committed carry tuple and equipment, so a worker can advance active movement and
combat state monotonically. Reconnect reads the tuple again under the admission
lock.

Simulation session properties:

- Global account and character uniqueness.
- Exact shard, worker, and worker runtime ownership.
- Opaque token with only a stored hash.
- Database-generated expiry after `Simulation:SessionLeaseLifetimeSeconds`.
- One session heartbeat per worker every
  `SimulationWorker:SimulationSessionHeartbeatSeconds`, as
  [Session Heartbeat](#session-heartbeat) describes, so the requests do not grow
  with the players.
- Idempotent exact-generation release.
- Reconnect token rotation.
- Safe stale-token rejection after reconnect.
- A session stays active while its character's body stays in the world after
  the connection ended, as [Lingering Bodies](#lingering-bodies) describes: the
  worker keeps renewing its lease and releases it only when the body goes. The
  end of its account session does not end it either, as
  [Account Session End](#account-session-end) describes.

The worker also enforces cached lease expiry locally so a GameService outage
cannot leave a connected player or a body active forever. The local deadline is the
request send time plus the lease duration that GameService returns, so it does
not depend on the database clock. The lease outlasts the worker's outage
tolerance, as [GameService Outage](#gameservice-outage) describes.

SimulationWorker keeps each character's item state by exact simulation-session
identity and applies only increasing item-state revisions. A same-revision
conflict invalidates the local lease instead of accepting contradictory weight,
capacity, or equipment. The worker updates the item state only from committed
GameService results and heartbeats, and sends a newer carry tuple reliably
before it affects movement.

### Session Heartbeat

`POST /api/simulation-workers/{workerId}/session-heartbeat` renews the leases
of one worker runtime's sessions. It takes the worker's `runtimeId` and
`sessions`, each with its `simulationSessionId` and its `sessionToken`, and
answers `sessions` in the request's order, one entry per session with its
`simulationSessionId` and either `lease` or `error`:

- `lease` is the renewed lease as a release answers it: the session, character,
  Shard, worker, and runtime, the new expiry and `leaseDurationMilliseconds`, the
  carry tuple and equipment read after the renewal, and `accountSessionEnded`,
  as [Account Session End](#account-session-end) describes.
- `error` is `invalid_simulation_session` for a session that is released,
  expired, not the worker runtime's, or proved by another token, and for a
  renewed session whose character has no item state. The others of the request
  are renewed all the same.

The statements are the same for any number of sessions. A short transaction
locks the valid sessions in one statement and, in the next, renews their leases,
marks the sessions whose account session ended, and reads how each ended, so the
answer names a new login that the lock waited for as `account_session_replaced`.
After it committed, one statement moves the end of the account sessions of the
renewed sessions, as [Account Session Lifetime](#account-session-lifetime)
describes, and one more reads the carry states of the renewed characters. The
sessions of development bots are renewed in memory in the same request. A request is refused with `400` as a whole when it is
malformed: `invalid_simulation_worker_identity` for a missing runtime id,
`invalid_simulation_session_heartbeat` for no session, more than 500, or a
session named twice, `invalid_simulation_session_id` for an entry without a
session, and `invalid_simulation_session_token` for one without a token.

SimulationWorker sends every active session once per
`SimulationWorker:SimulationSessionHeartbeatSeconds`, in requests of at most
`SimulationSessionHeartbeatBatchSize` sessions,
`SimulationSessionHeartbeatMaxConcurrency` requests at a time. It applies every
entry on its own: a lease moves the session's local deadline to the request's
send time plus the lease duration and carries the account session end and the
carry state, and an error ends the session as an expired lease does; while a
join is in flight, the error first waits for it, as
[Reconnect Token Rotation](#reconnect-token-rotation) describes. A request
that times out, cannot reach GameService, or gets a server error goes again
after `SimulationSessionHeartbeatRetryMilliseconds` with those of its sessions
that are still active, as long as the retry starts within the heartbeat
interval and GameService still answers the registry heartbeat. Any other failed
request goes no more in that round. Neither ends a session: a lease that is not
renewed ends by the session's local deadline. While GameService does not answer
the registry heartbeat, no round goes, and a round that came due meanwhile goes
as soon as it answers again, as [GameService Outage](#gameservice-outage)
describes.

### Account Session End

When an account session ends, by a logout, a manual revoke, or a new login that
replaces it, the simulation session of the account goes on, so the character's
body stays in the world its full time, as [Lingering Bodies](#lingering-bodies)
describes. The clock never ends the account session of a live simulation
session, as [Account Session Lifetime](#account-session-lifetime) describes:

- A logout, a revoke, and a new login mark the account's unreleased simulation
  sessions with `account_session_ended_at` in their own transaction instead of
  releasing them, and consume the account's pending join tickets as before.
  The session heartbeat marks a session whose account session ended without
  such a transaction, and every check counts such an end at once, also before
  the heartbeat marked it.
- The session heartbeat keeps renewing a marked session's lease and answers
  `accountSessionEnded` in the session's lease: `account_session_replaced` when
  a new login replaced the account session, `account_session_ended` for any
  other end, and `null` while it lasts. A session's heartbeat is refused only
  when the session is released, expired, or not the worker runtime's, with
  `invalid_simulation_session`.
- On a marked session GameService runs only the body's own work: player
  deaths, spent arrows, the kill credit of a quest, the quest log and quest drop
  reads that these need, checkpoints, the release, and the heartbeat. An item
  command runs only when its registration allows it, as
  [Operation Kinds](GAME_SERVICE_MODULES.md#operation-kinds) describes, so every
  other command, also one added later, is refused. Every operation that a
  client asks for, items, vendors, gathering, live Mob loot, corpse views and
  loot, quest actions, and look changes, is refused with `401` and the code of
  the end, `account_session_replaced` or `account_session_ended`.
- SimulationWorker treats a heartbeat that names the end, and such a refusal,
  alike: it ends the connection of a peer that still plays the session with
  the disconnect it sent before for that case, `account_session_replaced` after
  a new login and `session_revoked` otherwise, and the entity stays as a
  lingering body. Neither removes the entity nor invalidates the session, and
  an answer that arrives when the peer is already gone changes nothing more.
- Nothing that holds only the old account session gets a join ticket: its
  token no longer authenticates, its pending tickets are consumed, and a ticket
  is consumed only while its own account session is valid.
- A ticket of the new account session for the same character is the reconnect
  of the marked session on the worker that holds it. Consuming it binds the
  session to the new account session and clears the mark. A ticket for another
  character of the account is refused with `account_character_already_active`
  until the session is released or its lease expired.
- The worker releases a marked session when its body goes, with its final
  state, as any session. When the worker died, nothing renews the lease, and it
  expires as any lease does.

### Character Place And Vitals

GameService stores one state per character: the World, that World's collision
revision, the position and yaw as the simulation's floats, health, and mana,
with the simulation session that wrote it and a sequence within that session.
Three places of the session API carry it:

- The join ticket consume returns it as `savedState`, or `null` when the
  character has none. A development bot never has one.
- The release may carry the final state as `finalState` with its `sequence`,
  `positionX`, `positionY`, `positionZ`, `yawDegrees`, `health`, and `mana`.
  GameService saves it in the transaction of the release, and the response's
  `finalStateSaved` says whether it did.
- `POST /api/simulation-workers/{workerId}/checkpoint` takes the worker's
  `runtimeId` and `states`, each with its `simulationSessionId` and the same
  fields as a final state, and answers how many states it saved as `accepted`
  and how many it skipped as `skipped`. One statement saves the whole batch.

GameService takes the World from the session's Shard and the collision revision
from the worker runtime's registration, never from the request. A state is
saved only for the character's session on the worker and runtime that sent it,
while that session is unreleased, and for a checkpoint also unexpired; a release
saves its final state only when it is the release that ended the session. The
state must also be newer than the stored one: a stored state of the same session
with an equal or higher sequence stays, and a state of another session replaces
it. So a late or repeated state never overwrites a newer one, and a session that
ended never writes again. An entry that is not saved is skipped and counted; it
does not fail the batch, and a final state that is not saved does not fail the
release.

A request is refused with `400` when it is malformed: `invalid_character_state`
names a state with a sequence below 1, a position that is not finite or more
than 1,000,000 meters from the origin, a yaw that is not finite or outside -360
to 360 degrees, health outside 1 to 1,000,000, or mana outside 0 to 1,000,000,
and `invalid_character_checkpoint` names a checkpoint without states, with more
than 500, with an entry without a session, or with a session twice. An invalid
worker or runtime id is `invalid_simulation_worker_identity`.

SimulationWorker restores the state when it creates a joining character's
entity. The position and yaw count only when the saved World is the Shard's
World and the saved collision revision is the one the worker runs; otherwise the
character starts at the World spawn. Health and mana count in any World, within
1 to the maximum health and 0 to the maximum mana of the combat rules. A
reconnect to an entity that still exists, such as a lingering body, keeps that
entity as it is.

The worker saves the state of every player that a person plays; synthetic bots
have none. Every `SimulationWorker:CharacterCheckpointSeconds` it collects the
players whose position, yaw, health, or mana differs from what GameService
last confirmed and sends them in one checkpoint call, in requests of at most 500
states. At most one call is in flight; a failed call is logged, and its players
go again with the next checkpoint; a lingering body counts like a connected
player. While GameService does not answer, no checkpoint goes, and one goes
right after it answers again. When the entity of a session the worker still
holds is removed, such as a body whose time is up or whose death committed, and
at shutdown, the worker
captures the final state, and the release of that session carries it, also when
the release runs later or again. A release whose token GameService refused
keeps the state for the reconnect that rotated the token, whose new entity is
placed from it, as [Reconnect Token Rotation](#reconnect-token-rotation)
describes. At shutdown the worker
first saves every remaining player in one last checkpoint while the sessions
still hold, because going offline releases them. A dead player is saved as its
respawn leaves it: at the World spawn with full health and mana. A worker crash
or a GameService outage loses at most the time since the last saved state.

### Lingering Bodies

When a joined player's connection ends while its simulation session is still
valid, the character's entity stays in the world as a lingering body for
`SimulationWorker:LingeringBodySeconds`, 20 by default:

- A leave is answered at once with `LeaveAccepted`, whose `BodySeconds` says how
  long the body stays, and the peer is disconnected after the answer, as
  [Realtime UDP Transport](#realtime-udp-transport) describes. A lost or
  timed-out connection and a connection that the worker ends with a
  `ServerDisconnect`, such as `udp_rate_limited`, `invalid_packet`, or
  `internal_error`, leave a body the same way. A leave of a session that ended
  already answers `BodySeconds` 0.
- The body's movement stops and its target, an attack in its windup, its
  gathering, its corpse view, its world interaction, and its waiting operations
  end. It stays in the interest index and in the remote snapshots of others,
  keeps its look and its combat state, can be targeted and hit, and
  regenerates as usual.
- An item, corpse, world interaction, gathering, or quest operation that was
  already running at GameService when the connection ended still commits, and
  what it committed reaches the body: its item and carry state, its equipment
  and so its look, its quest log, and the corpse it looted. The body's spent
  arrows and death then build on that revision. Only the operation's result
  needs the peer that sent it; a player who came back in the meantime gets the
  item and quest log changes and the carry state. An answer that ends the
  session invalidates the body's session, so the body goes without a release,
  except that a refusal of the body's token waits for the joins in flight, as
  [Reconnect Token Rotation](#reconnect-token-rotation) describes; an answer
  that says the account session ended changes nothing for a body.
- The time is counted in simulation ticks from the end of the connection. Every
  hit that the body takes moves its end to the full time after the hit when
  that is later, without an upper limit. A body whose time is up or whose death
  committed waits for the operation that still runs for it before it goes.
- When the time is up, the worker despawns the body with reason `left_shard`,
  saves its final state with the release of the session, and releases it as a
  disconnect did before. A body whose death committed goes with reason `died`
  and is saved as its respawn would leave it; it never respawns. The release
  waits for the spent arrows, the death, and the quest credits that the body
  still commits.
- The end of the account session, by a logout or a new login, does not end the
  simulation session, and the clock does not end the account session of a body
  either, as
  [Account Session End](#account-session-end) describes: a connected player
  becomes a body, and a body stays its full time.
- A body whose session ended goes at once without a release, with the
  invalidation code as despawn reason, such as `session_revoked` for a session
  that GameService no longer renews, or `session_expired`. A worker that stops
  saves its bodies in the last checkpoint and takes them out with every other
  player.
- A join ticket of the same character is a reconnect of the body's session: the
  join resumes the body's entity where it stands, with its vitals, and ends its
  time. A body that died and has not gone yet is resumed dead, and respawns at
  the World spawn once its death committed and the respawn delay passed, as any
  character does. When the body already went, because its death committed or
  its time ended, but GameService had not released the session yet, the ticket
  is still a reconnect, and the join gives the player a new entity, placed from
  the state that the session captured when the body went: for a body that died,
  the World spawn with full health and mana, and otherwise where the body
  stood, with its vitals. This holds also when GameService refused the body's
  release, its death, or its heartbeat because the reconnect had rotated the
  session's token, as
  [Reconnect Token Rotation](#reconnect-token-rotation) describes. A ticket for
  another character of the account is refused with
  `account_character_already_active`, and the client tells the player that the
  previous character is still in the world for a short while.
- The orphan sweep never releases a body's session, and leaves the session of
  a body that went with uncommitted work, as
  [GameService Outage](#gameservice-outage) describes.
- Development bots leave bodies too; while its body stands, a bot's ticket
  request is refused with `development_bot_already_active`.

### Reconnect Token Rotation

GameService rotates a session's token when it consumes the ticket of a
reconnect, before the reconnect's join reaches the worker and registers the new
token there. Until then GameService refuses what the worker sends with the old
token: the heartbeat with `invalid_simulation_session`, the release with `401`,
and an item transaction, such as a body's death, its spent arrows, or a kill
credit, with `simulation_session_invalid`. Only a join of the worker that holds
the session consumes such a ticket, so the worker tells a rotated token from an
ended session by its own joins:

- A join counts as in flight from before it consumes its ticket until it
  registered its session or failed. A refusal of a session's credentials by
  the heartbeat, by an operation of the player or of its lingering body, or by
  the session's combat work that comes while a join is in flight waits until
  every join that was in flight then has ended. When one of them registered a
  newer generation of the session, the session goes on and the refusal ends
  nothing; otherwise the session ends as the refusal says. A refusal of a
  token that a newer generation already replaced ends nothing. A join that
  fails unexpectedly after it consumed its ticket releases that session, with
  the local state it registered.
- A reconnect from another client takes over the session of a connected
  player the same way. When an operation of the first client is refused while
  the takeover's join is in flight, the first client gets its
  `ServerDisconnect` at once, but its entity stays as a lingering body until
  the refusal is decided, so the join resumes the entity where it stands with
  its vitals instead of placing a new one from the last checkpoint.
- The death, the spent arrows, and the kill credits of a session belong to the
  session, not to one token of it. Each attempt remembers the generation it
  went with. When GameService refuses its credentials and the session goes on,
  or may go on because a join is in flight, the attempt keeps its place and
  goes again after its backoff, with the reconnect's token once the reconnect's
  join attached the session. So a death commits exactly once, with its corpse,
  whichever token it commits with, and it neither disconnects the returning
  player nor ends the session it goes on with.
- A body that went before the reconnect's join arrived, because its death
  committed or its time ended, left its final state, which its release carries.
  When GameService refused that release because the reconnect had rotated the
  token, the release saved nothing, so the worker keeps the state: the
  reconnect's join places its new entity from it, instead of from the older
  state that GameService returned with the ticket, the new entity takes it
  over, and the next checkpoint saves it. A join that cannot go on after it
  consumed its ticket releases the session with that state, so GameService
  keeps it instead of an older one. A kept state whose reconnect never
  completes, such as one whose consume answer was lost, stays as long as the
  session's lease can last without this worker renewing it, the outage
  tolerance plus one session heartbeat interval, and the next release after
  that forgets it.

So a reconnect ends in one of the outcomes that
[Lingering Bodies](#lingering-bodies) names, whatever the order of the killing
hit, the death's commit, the body's end, the release, the heartbeat, and the
reconnect's join: the join resumes the body's entity; or the body died and went,
and the join gives a new entity at the World spawn with full health and mana
while the corpse lies where the body fell; or the body's time ended, and the
join gives a new entity where the body stood, with its vitals.

## Realtime UDP Transport

SimulationWorker uses LiteNetLib and the versioned `GameProtocol` package.

- Connection key validation happens before application messages. Without a
  valid registration lease, or with every one of the `MaxConnections` slots
  taken, a connection request is refused silently.
- Every datagram passes a LiteNetLib packet layer that seals and opens it, as
  [Transport Security](REALTIME_PROTOCOL.md#transport-security) describes. The
  worker installs a peer's keys when GameService consumed its ticket, before it
  answers the join, and they go when the peer disconnects or the worker stops.
  Before its keys a peer reaches the worker only with LiteNetLib's connection
  control and the session channel; anything else is dropped. Every
  `NetManager` sets `MtuOverride` to 1,024, so the 25 bytes of the seal come on
  top of LiteNetLib's packets. A worker whose platform has no ChaCha20-Poly1305
  logs one warning at its start and seals with the managed implementation.
- The worker counts every datagram it sends and receives, their bytes, the
  bytes that the seal adds in both directions, and the received datagrams it
  drops by reason: `malformed`, `unauthenticated`, `replayed`,
  `clear_after_sealing`, and `clear_not_allowed`. A dropped datagram is never
  answered. The counts are in the metrics and the performance file, as
  [Monitoring](MONITORING.md#simulationworker) lists.
- New peers must send one reliable ordered join request within the join
  handshake timeout, which is also LiteNetLib's disconnect timeout.
- Join ticket consumption is asynchronous and does not block the UDP poll loop.
- The realtime loop runs on its own thread and sleeps until the next tick or the
  next transport poll, `NetworkPollIntervalMilliseconds`, whichever comes first.
  On Windows it waits with a high-resolution waitable timer, because ordinary
  waits round up to the 15.6 ms timer tick; ticks start within about a
  millisecond of their schedule.
- At the end of every loop iteration the worker wakes LiteNetLib's update thread,
  so the queued packets leave at once instead of on the transport's own 15 ms
  update.
- LiteNetLib's packet pool holds one packet per allowed connection plus one per
  512 bytes of the aggregate snapshot budget of one broadcast, between 1,000 and
  65,536, about 6,200 with the default settings, because one snapshot broadcast
  queues an owner packet for every connection and the remote chunks the budget
  allows before they are sent.
- Reliable messages travel ordered on three channels, the session, world, and
  owner channels, so a lost packet holds back only its own stream, as
  [Channels](REALTIME_PROTOCOL.md#channels) describes; movement input uses
  sequenced delivery, and simulation snapshots use unreliable delivery. The
  worker sends every message on the channel its descriptor names.
- Packet magic, version, type, bounds, lengths, and finite numeric values are
  validated. Protocol violations receive a stable error where possible and are
  then disconnected.
- A connection that the worker ends gets the message that says why, a
  `LeaveAccepted`, `ServerDisconnect`, or `JoinRejected`, on the reliable
  session channel and again as the data of the disconnect, as
  [The Reason In The Disconnect](REALTIME_PROTOCOL.md#the-reason-in-the-disconnect)
  describes. The worker disconnects once the client acknowledged that reliable
  message, which LiteNetLib reports with a delivery event, no sooner than the
  next loop iteration and no later than `GracefulDisconnectMaximumMilliseconds`,
  2,000 by default, after it sent the message. A peer without keys would drop
  the reliable copy, so the worker sends it the message only as the data of the
  disconnect, at the next loop iteration. A working line thus gets the
  reason on the reliable channel about one round trip before the disconnect,
  and a line that lost it gets it with the disconnect. Packets that such a peer
  sends meanwhile are counted and dropped.
- `SimulationWorker:Admission` limits remote peers that have not joined yet: at
  most `MaxUnauthenticatedPeers` in total and `MaxUnauthenticatedPeersPerAddress`
  per address, and new connections per address are rate limited by
  `ConnectionAttemptsPerAddressPerSecond` with a `ConnectionAttemptBurstPerAddress`
  burst. Loopback peers, such as local bots and stress tools, are exempt.
  Refused connections are counted by the
  `simulation_worker.realtime.connections.rejected` metric.
- A peer with more than `MaxReliableQueuedPackets` reliable control packets
  waiting to be sent is disconnected, so a client that never acknowledges cannot
  grow server memory without bound. LiteNetLib sends at most 64 unacknowledged
  reliable packets per channel and queues the rest, and the limit counts those
  queues of every reliable channel together. The worker writes a warning with the
  queued count and the limit and counts the disconnect in
  `simulation_worker.realtime.peers.disconnected` under the reason
  `reliable_queue_full`. A crowd cannot reach it: entities enter a view only while
  the queue has room for them, as [Interest Management](#interest-management)
  describes.
- An unexpected exception costs as little of the Shard as possible. A packet,
  a completed operation, or a connection, disconnection, or connection request
  that fails costs only its own peer. A player whose own movement step or look
  fails leaves alone: the client gets `internal_error`, and its character
  leaves the world at once, without a lingering body. The worker then releases
  its session without a final state, because that state may be broken, so the
  character is free at once and resumes from its last checkpoint. A peer whose
  snapshot or look packets fail
  to build or send is disconnected with `internal_error`, and its character
  stays as a lingering body. Any other
  failed tick phase or loop step is skipped for that run while the rest of the
  tick goes on, so every player feels at most a hitch. A tick phase or loop
  step that fails `TickPhaseFailureLimit` runs in a row, 30 by default, stops
  the worker in order, as a graceful stop does; for a phase that works only on
  some ticks, such as the snapshots, only the ticks on which it was due count
  as runs. The remaining players are
  saved in one last checkpoint, and the worker goes offline and releases its
  sessions, so nobody stays in a world that no longer moves. Every failure
  writes an error line and counts in `simulation_worker.loop.failures`, as
  [Monitoring](MONITORING.md#simulationworker) lists.

Join acceptance carries Shard and World identity, the Shard rules, and the
initial carry tuple. A standalone client must be rebuilt when the protocol
version changes, and a mismatched client receives the explicit protocol
rejection path, as [Versioning](REALTIME_PROTOCOL.md#versioning) describes.

## UDP Resilience And Quotas

Per-peer token buckets enforce `SimulationWorker:UdpQuotas` packet and byte
rates with burst allowance. Sustained inbound abuse is rejected with
`udp_rate_limited`. Excess unreliable snapshot output may be dropped without
delaying reliable lifecycle messages.

An additional worker-wide snapshot byte bucket bounds aggregate unreliable
output. Snapshot admission occurs for a complete chunk batch, so a peer receives
either all chunks for one snapshot sequence or none. The recipient start rotates
past the admitted group after each broadcast, distributing overload gaps across
peers instead of starving the same tail of the connection list. Reliable
control, spawn, despawn, and leave traffic does not consume this snapshot
budget.

HTTP calls to GameService map timeout, connection, invalid-response,
authentication, and domain failures to structured results:

- `auth_service_timeout` and `auth_service_unavailable` are uncertain outcomes,
  because GameService may have committed the operation before the response was
  lost. Item, corpse, and live Mob loot results with those codes always ask the
  client to refresh, and operations that the worker retries reuse their
  operation id. Such a failure, or a server error, asks for the next registry
  heartbeat at once, which decides whether GameService is unavailable, as
  [GameService Outage](#gameservice-outage) describes.
- `game_service_unavailable` is the worker's own refusal while GameService does
  not answer: the action was never sent, so nothing needs a refresh.
- `worker_runtime_changed`, `simulation_session_invalid`,
  `auth_service_authentication_failed`, and `invalid_auth_response` end the
  player's session, and so does `wrong_simulation_worker` for an item or NPC
  lifecycle operation. For a corpse operation `wrong_simulation_worker` means a
  corpse of another Shard and closes only the corpse view.
- Live Mob loot claims send the current committed item-state revision from the
  item state store, not the revision from the time of the join.

## Entity Registry And Replication

- `SimulationEntityRegistry` owns nonzero network entity ids: a counter from 1
  that stops with an error at 4,294,967,295, the largest id that a remote
  snapshot record carries. An id is never given out twice. The worker reports
  the ids used, an alert warns at half of them, and a worker that used all of
  them logs a critical line and needs a restart, as
  [Entity Ids](MONITORING.md#entity-ids) describes.
- Persistent character id and transient network entity id are separate.
- `ConnectionEntityBindingRegistry` enforces one peer per entity and one entity
  per peer.
- Same-character reconnect preserves the entity and movement state while
  replacing the peer binding, also for a lingering body without a binding.
- Entity spawn and despawn use reliable ordered control messages, and Unity
  receives a reliable baseline before relying on snapshots.
- Each joined peer receives a dedicated fixed-rate owner snapshot for input
  acknowledgement and reconciliation.
- Remote snapshots carry the players and Mobs that a peer sees, a lingering
  body too, but never the peer's own entity, which its owner snapshot carries;
  only a bound entity has an owner snapshot.
- Every entity's remote record is quantized once per snapshot into a flat
  buffer that keeps it from one snapshot to the next, with a version that
  changes only when the record's bytes change. Each recipient remembers which
  version of every entity in its view it got, and a record goes to it only
  when it is due, in this order:
  1. **New in view**: an entity that entered the view, in the next snapshot.
  2. **Overdue**: a due record of an entity that the recipient got nothing
     of for longer than the refresh interval, because the budget deferred it.
  3. **Near changed**: a changed entity in the near tier, every snapshot.
  4. **Stop repeat**: the unchanged record again in the next
     `SnapshotReplication:StopRepeats` snapshots, 3 by default, of the
     entity's tier after its last change, so one lost packet cannot leave an
     entity walking on in the client.
  5. **Far changed**: a changed entity beyond the near tier, every
     `FarIntervalSnapshots` snapshots, 3 by default, staggered by entity id,
     and at once in the two snapshots of a start: the snapshot where its
     quantized position changes after it did not change in the snapshot
     before, and the next. The client starts a walk from the stand one
     snapshot interval before its first moving record, so that record must
     not wait up to two more snapshots for the far slot, or the client shows
     the walk up to three times too fast for a moment. The second record
     gives the walking speed of a walk that started between two snapshots,
     and so starts slower, before the client would continue that slow start
     and then catch up. A turn in place moves nothing, as the client judges
     standing by the position alone. Mobs travel in the same records and
     follow the same rule.
  6. **Refresh**: an unchanged entity once every `RefreshIntervalSnapshots`
     snapshots, 15 by default, one second, staggered by entity id.
- The near tier is the visible entities within `InterestManagement:NearRadius`,
  50 m by default; an entity leaves it beyond `NearExitRadius`, 58 m. The tier
  is decided at each refresh of the connection's visibility.
- Each recipient gets at most `SnapshotReplication:PlayerBytesPerSecond`, 32
  KiB by default, owner snapshot included, or its share of the aggregate budget
  when that is smaller. The due records fill it in the order above, so the far
  and unchanged entities degrade first. Within one class the records that the
  last snapshot deferred go first and the rest rotate. The near changes go by
  a priority instead: every snapshot that a near change waits adds the inverse
  of its distance, and a sent record starts again from zero. When the near
  changes do not all fit, the nearest therefore go in every snapshot and the
  others in turn, less often the farther they are, and none waits for ever. A
  deferred record is due in the next snapshot, whatever its tier, and the
  overdue class gives every entity at least about one record per second. A
  recipient with nothing due gets no remote snapshot.
- Each recipient's state lists its visible entities in the order of its
  visible set and remembers where each entity's record is, so planning reads
  arrays and looks an entity up only when the visible set or the record moved.
- The owner snapshot and each recipient's MTU-safe chunks are written into
  reusable packet buffers that the transport copies, so the broadcast
  allocates nothing per recipient or packet. The worker-wide byte bucket drops
  complete frames only after the budgets above.
- `SnapshotReplication:OverloadTargetUtilizationBasisPoints` reserves aggregate
  burst headroom. Deferred remote entity updates remain visible through the
  `simulation_worker.simulation.snapshot.entity_updates_deferred` counter.
- A Mob travels in the remote snapshots like a remote player and by the same
  rules, as [Mob Brains](#mob-brains) describes.
- The snapshot sequence and the server tick are 32 bits and wrap, after 3.3 years
  of snapshots at 15 a second and 4.5 years of ticks at 30, and every comparison
  of them is modular, so nothing stops at the wrap. The slots that stagger the
  records, their refresh and far slots, and the far states by entity id are the
  sequence or the tick modulo an interval that 2^32 is no multiple of, so the wrap
  moves them once: with the default intervals a record comes at most one snapshot
  later than its interval, and a state that a viewer missed waits at most two
  seconds instead of one. A snapshot sequence is committed before the broadcast
  reports under it, so a broadcast that fails never leaves its sequence to the
  next one.

## Interest Management

`SimulationInterestManager` maintains a spatial hash from authoritative entity
positions. The tick before every snapshot tick brings it to the players and
world actors as they are: an entity that stays in its cell only gets its new
position, one that changed cells moves, a new one is added, and one that is
gone leaves. Joins add one entity at once and update only the nearby
observers. Each peer has a visibility set:

- The enter radius, 128 m by default, adds entities.
- A larger exit radius, 144 m by default, prevents boundary flapping.
- Visibility changes emit reliable spawn or despawn. The refresh runs on the
  tick before each snapshot tick, so its spawns leave one tick before the
  snapshot that carries the first record of their entities, and each spawn
  carries that tick and the entity's pose at it. A despawn follows the last
  record of its entity, and no snapshot carries the entity after it.
- Snapshots contain only visible entity ids.
- Each connection keeps its visible entities ascending by id, each with its
  tier and its place in the index; the set changes only by what entered and
  left.
- Each connection's visibility set is recomputed every
  `RefreshIntervalSnapshots` snapshots, 3 by default, the connections in turn,
  so a 15 Hz snapshot rate refreshes a third of them per snapshot interval. The band
  between the enter and exit radius keeps an entity visible while its viewer
  waits for its turn, and joins and leaves still update visibility at once.
- A connection gets at most the smaller of two budgets of entities into its view
  between two of its refreshes, the refresh itself included. Each entity that
  enters costs a reliable spawn and, for a combatant, a reliable combatant state:
  2 packets on the world channel.
  - A burst, `SimulationInterestManager.MaximumEnteredPerRefresh`, 128 entities,
    256 reliable packets, all of them in the queue when the peer supervisor looks
    right after the tick: a quarter of the `Admission:MaxReliableQueuedPackets`
    that disconnects a peer.
  - The room of the connection's reliable queue: the packets that wait for its
    peer, the count that the supervisor judges, are read once at the refresh, and
    the room is the packets that fit below a high-water mark, half of
    `MaxReliableQueuedPackets`, 512 by default, at 2 packets an entity. A queue at
    or over the mark lets nothing in but the connection's own entity, which
    always enters. The other half of the limit is for everything else that is
    reliable: the join's own messages, the combat events and far states of the
    same tick, and the answers to what the player does.
  - The second budget is what keeps a slow line connected. LiteNetLib delivers a
    window of 64 packets per round trip, so a line of 200 ms takes 320 packets a
    second, while a crowd that keeps entering adds a burst at every refresh, up to
    1,280 a second. A fixed burst would queue 1,400 packets for a hub of 700
    entities in about a second, drain 350 of them, and disconnect the player, who
    would reconnect into the same crowd. With the room, the crowd enters as fast
    as the client acknowledges and the queue stays at the mark. A client that
    acknowledges nothing stops getting entries at the mark, and the queue limit
    stays a safety net for traffic that no budget bounds.
  - The nearest entities within the enter radius enter first, and their spawns are
    sent in that order, the connection's own entity before any other, and the rest
    wait for the connection's next refresh. On a line that keeps up, a crowd of 500
    around a player is in its view after four refreshes: the first is the join's,
    with its baseline, and the other three follow every 3 snapshots, so the whole
    crowd is in view between 0.4 and 0.6 s after the join at the default rates; the
    128 nearest entities come with the join itself. On a slow line the line bounds
    the time: 500 entities are 1,000 packets, 3.1 s at 200 ms and 6.3 s at 400 ms,
    and a crowd of 701, which `EntryBurstTests` plays, is delivered after 4.8 s and
    9.6 s, in 17 and 32 batches, with the queue at the mark of 512 and never at the
    limit.
  - An entity that waits is not in the view at all, as if it were beyond the enter
    radius: it has no viewer pair, so no spawn, state, look, snapshot record, or
    combat event for its viewers is sent about it, no list of the entity's viewers
    names the connection, and nothing counts it as shown. No despawn is sent when
    it leaves before it entered. Leaving a view is never delayed: an entity that
    leaves goes with its despawn in the refresh that finds it gone, whatever the
    budget is. A combat event still reaches whoever controls its source or target.
  - A join or a respawn enters the connections that see its place at once while
    their entries since their last refresh are within what that refresh allowed,
    the smaller budget, and at their next refresh otherwise.
  - `simulation_worker.realtime.interest.entries_deferred` counts the entities
    that waited, by cause: `burst` for those beyond one burst, which a crowd that
    forms or a burst of joins raises, and `queue` for those that the connection's
    reliable queue had no room for, which a slow client raises. A quiet world
    raises neither.
- A spawn that cannot be built for a connection, such as one whose character name
  the packet cannot carry, costs that entity for that connection only. The worker
  writes an error with the entity and the peer, counts it in
  `simulation_worker.realtime.spawn.failures`, takes the entity out of the
  connection's view, and keeps it out while it stays within the connection's
  enter radius, so it is not built and failed again at every refresh. The refresh
  goes on with the other entities and connections. A join whose own entity's
  spawn cannot be built fails.
- The same refresh decides which visible entities are near: within
  `NearRadius`, 50 m by default, or still within `NearExitRadius`, 58 m, when
  they were near before. An entity that becomes visible between two refreshes
  is near when it is within the near radius.
- The same refresh also decides which visible entities the connection sees in
  detail: within `DetailRadius`, 70 m by default, or still within
  `DetailExitRadius`, 78 m, when they were in detail before. An entity that
  becomes visible between two refreshes is in detail when it is within the
  detail radius. A connection also sees its own entity, its target, and
  whoever targets its entity in detail at any distance. What detail decides is
  in [Detail Radius](#detail-radius).
- A refresh scans the cells around the observer once and decides visibility,
  tier, and detail where it computes each distance. The positions lie in flat
  arrays that the cells point into, so the scan looks nothing up per
  candidate. The tier and the detail of the visible entities lie in lists
  parallel to them, which a refresh reads and writes in order.
- Every connection that sees an entity is one viewer pair, and the pairs of an
  entity form a list, so a state change or a combat event walks the viewers of
  its entity instead of asking every connection. A pair holds a copy of the
  detail, which a refresh writes only when it changes, and the states the
  connection missed. At 500 players the spread layout of the
  [replication benchmark](STACK_STRESS_TESTING.md#replication-benchmark) has
  about 119,000 pairs and the crowd of 500 in one place 250,000, at 37 bytes
  each, a pair of 32 bytes and 5 bytes in the connection's lists: about 4.5
  and 9.5 MB.
- The maximum unreliable packet is `1023` bytes, so a remote snapshot chunk
  contains at most 71 current entity records of 14 bytes, as
  [Remote Entity Record](REALTIME_PROTOCOL.md#remote-entity-record) describes.
- Cell size, radii, and the refresh interval are worker-owned configuration. The
  budget of entries is not: it follows from LiteNetLib's window and from the
  queue limit. The worker refuses to start with a `MaxReliableQueuedPackets`
  below 512, because the half of it that entries may fill has to hold one burst
  of 256 packets.

This is process-local interest management for one complete shard. Cross-worker
zone interest is deferred until zones exist.

### Detail Radius

A viewer sees what happens around an entity in full only within the detail
radius; beyond it, up to the exit radius, it sees the entity move in the
snapshots as before, with its newest look and health once a second. The
movement tiers of the snapshots do not depend on it.

- A change of a combatant's `CombatantState`, a player's
  `EntityAppearanceChanged`, or a world actor's `WorldActorState` goes at once
  to every viewer that sees the entity in detail, and to its owner as before.
- A viewer that sees the entity out of detail missed the change. Every entity
  has a slot in the second, its id modulo the snapshot rate, and on the tick
  before the snapshot of its slot every viewer that missed one of these states
  gets the newest one, encoded once for all of them. The far states phase sends
  them, after the interest refresh, on the same ticks.
- When an entity comes into detail for a viewer at a refresh, the viewer gets
  the newest of every state it missed of it at once, so a figure that walks
  within 70 m never shows an old look or health there. The same holds when the
  viewer's entity takes the entity as its target or the entity takes the
  viewer's entity as its target, at the end of that tick's combat phase.
- A change that must never be late reaches every viewer at once, in or out of
  detail: a combatant whose life state changes between alive, dead, and
  evading, and a world actor that becomes active or inactive, such as a
  depleted resource node or one that returns. Spawns and despawns are not
  affected, so a Mob that dies, leaves, or returns does so at once for every
  viewer.
- A spawn carries the entity's newest look and world actor state, and the
  combatant's state follows it, so a viewer missed nothing of an entity it was
  just sent.
- A Mob's world actor state is held back from a viewer out of detail while the
  Mob walks, because a client takes every world actor state of a Mob as the
  pose it comes to rest at; the viewer gets it on a later slot once the Mob
  stands.
- Combat events: `AttackStarted`, `AttackReleased`, `AttackCancelled`, and
  `Hit` reach the viewers that see the source or the target in detail and
  whoever controls the source or the target. `Died` reaches every viewer of
  either. A viewer that comes into detail while an attack is under way sees it
  from its next event on.
- The worker remembers what a viewer missed as one bit per state kind on its
  viewer pair, which says that a newer revision of that kind exists than the
  one the viewer got, and which a send of the newest state clears.

## Server-Authoritative Movement

- Clients send input sequences and click-to-move paths, never accepted
  positions. Each input names the followed path by id, and a batch carries the
  waypoints of the newest input's path until the owner snapshot reports it.
- SimulationWorker runs the shared movement code at a fixed 30 Hz by default.
- It accepts only newer input sequences. A new path has at most 32 waypoints,
  must keep them in the World bounds, and must stay within 256 meters from the
  authoritative position through every waypoint; a path that breaks the limits
  is adopted as already ended, so client and worker agree that the character
  stands. An input that names a path the worker never received is a protocol
  error, `unknown_movement_path`.
- The worker steps the character exactly once per input, in sequence order, as
  the client prediction does. Inputs wait in a short queue that absorbs uneven
  packet timing; a tick applies the oldest one, and a second one while more than
  one still waits.
- When the next input is late, the tick still steps along the current path, at
  most three times in a row, about 100 ms. The late input then takes over that
  step, or the steps are taken again with it when it names another path. A
  standing character then pauses on the worker until the inputs arrive, and the
  queue then catches up at two steps per tick.
- Path continuation: when no input arrived for longer than those three steps, a
  character that follows a path keeps walking it ahead of its inputs,
  `Movement:PathContinuationSeconds` long in all, 3 seconds by default, and
  then stops; 0 keeps the pause. The client goes on predicting the same path
  through an outage of its line, so the inputs after the outage take over these
  steps one by one, however many were lost, and the owner sees no correction.
  An input that names another path takes again at most the newest three of the
  steps with it; the steps before those stand as taken along the old path. The
  other path so starts where the worker is, the client is corrected by the
  difference, and a client that holds its inputs back cannot return its
  character to where it stopped sending. Once inputs arrive again, no step is
  added until they took over all but three, so a client that stalled or whose
  clock runs slow never falls further behind. A death, a lingering body, a
  teleport, a respawn, and a reconnect that takes the entity over end the
  continuation at once, and its steps wait where the collision chunks still
  load, as every step does.
- Inputs lost from every batch first take over the steps taken ahead for them;
  up to 15 more are filled with the path before them, and a longer gap skips
  to the newest input from where the character stands.
- A step budget keeps the average at one step per tick, with 1/64 of slack for a
  client clock that runs slightly fast, so a client cannot move faster by
  sending more inputs. A client may have at most 16 inputs waiting; a client
  that floods inputs loses the oldest.
- A path that collision blocks for half a second ends.
- New planar control is ignored while airborne.
- Collision, slopes, steps, ground support, and bounds are server-owned.
- Each authoritative entity owns one reusable collision-query workspace, so
  fixed ticks reuse broadphase list and stable-id set capacity instead of
  allocating them again for every movement step.
- Owner snapshots are sent at 15 Hz by default with input acknowledgement,
  authoritative reconciliation state, and path progress. They carry the state
  after the last processed input, without the steps taken ahead of late inputs,
  so the client replays exactly the inputs after the acknowledged one. Remote
  snapshots, combat, and interest use the current state.
- Remote snapshots use the rest of each player's byte budget. Their cadence
  degrades by distance under dense overload instead of starving owner
  reconciliation, as [Entity Registry And Replication](#entity-registry-and-replication)
  describes.
- The carry state slows walking through the shared encumbrance multiplier, as
  [Carry Weight And Encumbrance](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carry-weight-and-encumbrance)
  defines.

## Collision Data And Streaming

- Each `content/worlds/<WorldId>/collision.json` contains neutral collision
  source for that World.
- `tools/WorldCollisionCompiler` emits versioned binary chunks and a manifest
  below `src/Shared/WorldData/Generated/WorldCollision/<WorldId>`.
- Each chunk has a SHA-256 checksum, and the manifest has a deterministic
  complete collision revision.
- SimulationWorker validates format, World id, checksums, coordinates, and
  revision before binding UDP. It recomputes the revision from the manifest
  chunk list and rejects a manifest whose stored revision differs. Startup fails
  when no collision chunk is available around the World spawn.
- Unity validates and loads the same data, with the same revision check, before
  prediction.
- Collision data stays within plus or minus 1,000,000 world units, chunk sizes
  stay between 1 and 4096, and one shape may span at most 65,536 chunks. Chunk
  coordinates are clamped to plus or minus 2^24, so .NET and Unity map every
  position to the same chunk, and a query wider than the loaded set scans the
  loaded chunks instead of every cell in its range.
- The character motor derives its substep count from the full displacement
  length, so no substep, diagonal or not, exceeds the configured limit.
- Static shapes are assigned to every intersected chunk and deduplicated by
  stable id during queries. A chunk with more than 32 shapes indexes them in a
  planar grid of about 2 m cells, so terrain chunks with thousands of triangles
  stay cheap to query.
- Collision loads within `CollisionStreaming:LoadRadiusChunks` chunks, 2 by
  default, of every streaming anchor and unloads outside `UnloadRadiusChunks`,
  3 by default, which gives streaming hysteresis. The anchors are the players,
  the World spawn, walking Mobs, and the places that a join or an admin
  teleport waits for.
- The chunks around the World spawn load before the realtime loop starts.
  After that the worker never reads, hashes, or decodes a chunk on the loop:
  the streaming store's loader does that on one background task, in request
  order, and the collision streaming phase only starts the loads of chunks
  that became required, takes the finished chunks in, and unloads what left
  the unload radius. It plans the required and retained chunks again only when
  an anchor entered another chunk, appeared, or left. A chunk whose file is
  missing or whose hash does not match fails the phase on every tick while it
  is required, with the same log line as before, and its load is tried again
  each tick, so the failure limit stops a worker whose World directory is
  damaged.
- A player or Mob steps only where its own chunk and its eight neighbours are
  loaded or hold no collision; one whose chunks still load stands for that
  tick, its inputs wait, and the step counts in
  `simulation_worker.simulation.collision_streaming.pending_steps`. With 32 m
  chunks and the load radius of 2, a chunk becomes required at least one whole
  chunk before a mover can need it, so in play this only happens when the
  loader falls behind.
- A join whose new entity starts where the chunks are not loaded, at a saved
  place far from every player, waits: its join is finalized once the chunks
  around the place are in, and the player is placed on its ground then. An
  admin teleport to such a place waits the same way and answers once it moved
  the player. The place keeps its chunks loaded, like a player standing
  there, from the moment the wait begins until the completion queue has
  passed the released join or teleport, so no tick in between unloads them.
- A composite collision world combines static chunks and mutable dynamic shapes.

The collision format holds oriented boxes, one-sided triangles, and water
surfaces. The bake turns non-convex MeshColliders into triangles,
TerrainColliders into heightfields, and the meshes of water bodies into water
surfaces. The shared compiler triangulates heightfields, two triangles per cell
except for holes, so Unity and .NET produce identical chunks. A triangle
collides only from its front, so a tunnel can run below walkable terrain. The
compilers also add one movement-layer box per placed resource node, as
[Collision Bounds](../content/README.md#collision-bounds) describes, and the
deep water edges: one-sided walls along the line where water gets deeper than
the block depth, which stop movement from the shallow side, as
[Player Movement](GAME_FEATURES.md#player-movement) describes. Water surfaces
lie on their own layer, which only depth queries read, so they never block
movement, sight, or arrows. Heightfield triangles also carry a terrain layer,
because terrain is solid below its surface: water drawn under terrain or inside
a box is dry land for the deep water edges and the depth query alike. Replicated dynamic transforms are not implemented.
Collision authoring is described in [Development Worlds](DEVELOPMENT_WORLDS.md).

## World Actors And Interaction

SimulationWorker loads its World content before binding UDP:

- The compiled actor catalog from `SimulationWorker:ActorCatalogPath` and the
  compiled spawns of its World, `world-actor-spawns.json` in its output World
  directory. It rejects malformed content, revision drift in either file, wrong
  World identity, spawns compiled against another catalog id or placing an
  actor the catalog lacks, unsupported values, invalid references, or unsafe
  collection bounds, and content that the realtime protocol limits cannot
  carry. It logs both revisions.
- The compiled item catalog from `SimulationWorker:ItemCatalogPath`, verified
  strictly. Every item reference of the actor catalog must resolve against it, and the worker
  reports its revision in each heartbeat.
- The compiled quest catalog from `SimulationWorker:QuestCatalogPath`, verified
  strictly like GameService's copy, whose revision the worker also reports in
  each heartbeat.
- The combat rules and the character appearance content, as
  [Target-Based Combat](#target-based-combat) and
  [Character Looks](#character-looks) describe.

Assignment activation reconstructs ordinary actors from compiled WorldData with
fresh runtime and network entity ids. The actor model, the capability kinds, and
the interaction rules are in
[NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md). The worker implements
them as follows:

- Every current NPC and resource node is authoritatively invulnerable.
- NPCs and resource nodes are event-driven. Mobs occupy bounded central dormant
  or active schedule buckets selected by their activity profile. Delayed actor
  work, the respawn of a depleted resource node or a dead Mob, is scheduled in
  the central `WorldActorTimerQueue`, a min-heap by due tick that
  `WorldActorRuntimeDriver` drains every tick.
- Actor presence reuses reliable interest enter and exit messages, while actor
  state changes use bounded reliable updates, at once to the viewers that see
  the actor in detail and once a second to the others, as
  [Detail Radius](#detail-radius) describes. Assignment-local despawn
  tombstones retain runtime identity until every interest exit can encode the
  actor-specific despawn contract. Each interest pass drops a tombstone once no
  connection still has the actor in its interest set, so they stay bounded by
  current visibility.
- Insurance, quests, bank, Recovery Storage, vendor, and gathering have
  handlers; every other capability kind answers with
  `world_interaction_capability_deferred` until its business authority exists.
- Bank, Recovery Storage, and opened quests or vendor only confirm the
  capability; their item access follows the open interaction. Insurance apply
  or removal, quest actions, and vendor purchase or sale reuse the interaction
  session, and a started gathering activity runs in the central gathering pass
  while the session stays open.
- One world-interaction authority validates the exact simulation session,
  worker runtime, assignment, Shard, target identity and revision, active
  state, closest-point bounds distance, static and dynamic line of sight from
  the player's eye, 1.6 m above the root, to the target's bounds center with the
  same ray as combat, player-specific capability availability and revision,
  rate limits, and the one-active-interaction lease.
- Start range is `3.0` metres and maintain range is `3.5` metres. Open sessions
  are revalidated at 5 Hz and close on target invalidation, range, line of
  sight, disconnect, or assignment lifecycle changes.
- World interaction intents and quest intents share a per-peer budget of 8 per
  second with a burst of 12; more return `world_interaction_rate_limited`.
- Capability summaries are returned only after a successful interaction open
  and are independently revisioned for each player. Multiple players may
  interact with the same actor, but one player cannot overlap an actor
  interaction and a corpse view.

The insurance actions bridge the validated interaction to the worker item
route. Insurance applies or removes a one-death policy and charges
`Items:NpcLifecycle:InsurancePrice` through the wallet with an `insurance`
ledger row, and insurance removal keeps the item instance. The quest actions
use the worker quest route of [Quests](#quests).

## Vendor Trades

The vendor rules are in
[Vendor Trades](INVENTORY_AND_DEATH_LOOT_DESIGN.md#vendor-trades), and the
vendor profiles are item catalog content, described in
[Vendor Profiles](../content/items/README.md#vendor-profiles). The shipped
`vendor.city_general_store` is the profile of Mira the Quartermaster's vendor
capability.

- Opening the vendor capability only confirms it, so the client can show the
  offers from its bundled catalog. A `CapabilityAction` `VendorPurchase` or
  `VendorSale` goes through the same lease, range, line of sight, and capability
  revision checks as every capability, then through the NPC item lifecycle path
  to GameService. The worker sends the capability's vendor profile as the
  vendor access of the operation, never a client value, and answers
  "Purchased." or "Sold." on success.
- GameService resolves the vendor and its offers from the active mirrored
  catalog and prices them with the shared `VendorPricing` rule. The request
  carries the total the client showed, and any difference is rejected with
  `vendor_price_changed`.
- A vendor command without the live vendor access of an active vendor is
  rejected with `vendor_access_required`, an unknown or inactive vendor with
  `vendor_unavailable`, an offer it does not make with
  `vendor_offer_unavailable`, an item it does not buy, or a sale worth nothing,
  with `vendor_will_not_buy`, and a purchase that does not fit with
  `inventory_full`.
- Purchases and sales are idempotent by operation id like every other item
  mutation, so a retry never charges or pays twice. Synthetic development bots
  cannot trade.

## Resource Nodes And Gathering

A resource node and its resource profile are actor content, described in
[Resource Nodes And Gathering](NPC_AND_MOB_SYSTEM_DESIGN.md#resource-nodes-and-gathering)
and [Resource Nodes And Item References](../content/README.md#resource-nodes-and-item-references).
SimulationWorker runs gathering as follows:

- Charges are live Shard state in `WorldActorStore`, shared by every gatherer.
  Each node keeps remaining and reserved charges and a generation that advances
  on every depletion, so a stale completion or respawn never touches a newer
  cycle. A worker restart restores every node with full charges.
- A `CapabilityAction` `StartGathering` passes the usual interaction checks, and
  the gathering handler rejects a depleted node with `resource_node_depleted`.
  It then reads the `tool` slot from the equipment GameService last committed
  for the session and rejects an item without the profile's tool kind, or an
  empty slot, with `gathering_tool_required` and the kind's name, such as
  `Woodcutting Axe required in the Tool slot.`, in the interaction result, so
  gathering never starts; without loaded equipment it rejects with
  `gathering_unavailable`. The realtime loop then registers one gathering
  activity per player and sends `GatheringState` `Started` with the cycle time.
- The gathering pass runs every tick after the lease revalidation. A due cycle
  reserves one charge and queues a `gather_resource` call to GameService behind
  the player's other authority work. When every remaining charge is reserved by
  other gatherers, the cycle waits for the next tick. GameService checks that an
  item of the required tool kind is equipped in the `tool` slot again, since the
  tool may have been taken off since the start, otherwise
  `gathering_tool_required` with the same message, which the worker passes on in
  `GatheringState` `Stopped`. It places the yield through carried acquisition.
  When the call names the node's actor definition in `resourceActorId`, the
  yield also counts for the gather objectives of the character's current quest
  steps in the same transaction, and the response names the changed quests in
  `questLog`, as the quest route does.
- A committed yield consumes the charge, applies the committed carry state, and
  sends `GatheringState` `Yielded` with the item-state revision. The next cycle
  starts one cycle time later. The last charge makes the node inactive for every
  viewer, closes its interaction leases at their next revalidation, stops
  everyone gathering there with `resource_node_depleted`, and schedules its
  respawn in `WorldActorTimerQueue`. The respawn restores the charges and makes
  the node active again.
- Moving more than 0.1 m from where gathering started stops it with
  `gathering_interrupted`, and a closed interaction with
  `world_interaction_closed`. A rejected yield releases the charge and stops
  with the rejection code, such as `inventory_full`. An uncertain GameService
  outcome retries up to three times with the same operation id, so a lost answer
  never grants twice, and a rejection that ends the item session disconnects
  the player.
- While a player gathers, the worker shows the tool of its `tool` slot in its
  look, as [Character Looks](#character-looks) describes.
- Synthetic development bots cannot gather.

## Target-Based Combat

SimulationWorker loads the combat rules from `SimulationWorker:CombatRulesPath`
before binding UDP. The default is `WorldData/Combat/combat-rules.json` in the
worker output, which the build copies from `content/combat/combat-rules.json`.
The loader rejects unknown and duplicate properties, another format version,
values out of range, and rule set ids that are not sorted, lowercase, and
unique. The rules, described in [Combat Rules](../content/README.md#combat-rules),
hold the player maximum health and mana, the respawn delay, the combat timeout,
health and mana regeneration, the equipment lockout, the arrow batch interval,
the kill credit range of Mobs, and the Shard rule sets. Weapon values, weapon family windups and projectile
speeds, and ammunition bonuses come from the item catalog, and Mob values from
the actor catalog. The combat model is in
[Game Design](GAME_DESIGN.md#combat-model).

- The Shard rule set `mvp-open-risk` allows attacks on other characters.
  `JoinAccepted` carries the rule as `ShardRules`.
- A `CombatIntent` `AttackTarget` selects a target that the peer sees, that is a
  combatant, alive, and not the attacker itself, and that is a character only
  when the rule set allows it. `ClearTarget` clears the target. The target stays
  selected until it dies or leaves, or the rule set no longer allows it. Combat
  intents share a per-peer budget of 8 per second with a burst of 12; more
  return `combat_intent_rate_limited`.
- On every tick, a character with a target attacks on its weapon's cooldown when
  it stands still, the target's bounds are within the weapon range, the line of
  sight from eye height reaches the target's center, a weapon is equipped in
  `main_hand`, the equipped arrows belong to the weapon's ammunition family, and
  for a magic weapon the character has the weapon's mana cost. Otherwise the
  owner sees the attack status that holds the attack; `NotEnoughMana` keeps the
  target and the attack resumes once the mana is back. Line of sight costs a
  raycast, so it is checked when an attack is due to start or to release and
  five times per second otherwise.
- An attack starts on the cooldown with an `AttackStarted` event that names the
  tick at which it releases, and releases its blow, arrow, or bolt when the
  windup of its weapon family ends, the family's `windupSeconds`, timed so the
  body's attack clip strikes or looses then. The interval runs from the start,
  so the windup leaves the damage per second as it was. Whatever would keep a
  new attack from starting, such as a step, a lost target, a lockout, or the
  target leaving sight, cancels an attack in its windup with an
  `AttackCancelled` event: nothing is spent, and the next attack may start as
  soon as the character stands ready again. In its windup an attack keeps a
  target that walks on within a quarter more than the weapon's range,
  `CombatSystem.WindupRangeLeeway`, so a patrolling wolf does not cancel a
  shot, and is cancelled only beyond that. A Mob's bite releases at once.
- An attack rolls damage between the weapon's minimum and maximum and adds the
  arrow's bonus percent, rounded half up. An arrow or bolt flies at its weapon
  family's projectile speed and hits at its impact tick, at least one tick after
  the release, when the target is still alive. An arrow is spent when it looses,
  and a magic attack spends the weapon's mana cost when its bolt leaves, whether
  or not it hits. Every `AttackStarted` and `AttackReleased` event names the
  attack's combat style, so clients draw an arrow or a bolt.
- A different item in any equipment slot holds attacks for the equipment
  lockout, and an item or corpse operation that may change equipment holds them
  while it runs. A changed arrow count keeps the same item and does not count.
- A shot counts against the committed arrow stack at once, so the owner sees the
  live count in `OwnerCombatState`. The spent arrows commit as one batch through
  `ammunition-consumptions` once the arrow batch interval has passed since the
  first uncommitted shot, and also before any other item or corpse operation of
  the character, before a leave, and before a disconnected session is
  released, all through the character's authority lane. A committed batch sends
  the owner `OwnerItemQuantityChanged`. An uncertain batch retries with the same
  operation id up to 8 attempts, with a backoff from 0.5 seconds that doubles up
  to 10 seconds; after that its arrows stay with the player. While GameService
  does not answer, a batch makes no attempt, as
  [GameService Outage](#gameservice-outage) describes. A rejected batch
  loses its shots, and a rejection that ends the item session disconnects the
  player. An item intent or an NPC or vendor action whose expected revision only
  arrow batches advanced is sent with the newest revision instead. Corpse
  operations carry no expected character revision.
- A hit that brings health to 0 kills at once, so a later arrow of the same tick
  neither hits nor takes the kill, and a character killed by an arrow does not
  shoot in that tick any more. The killed character stops moving, its world
  interaction and corpse view close with `combat_character_dead`, its waiting
  operations are rejected with that code, and so are its new item, corpse,
  world, and combat intents. The death commits through `player-deaths` with a
  death event id that the worker creates once, together with the shots that are
  not committed yet and the killer. It retries with the same id and a backoff
  from 0.5 seconds that doubles up to 10 seconds until it commits or reaches the
  [retry horizon](#gameservice-outage), and drops the
  uncommitted shots when the arrow stack changed underneath, so they cannot
  block the death. A refusal of the session's token that a reconnect caused
  retries too, with the reconnect's token, as
  [Reconnect Token Rotation](#reconnect-token-rotation) describes. After the
  respawn delay, and once the death committed, the character respawns at the
  World spawn with full health and mana, and every viewer receives its respawn.
- A combatant is in combat until the combat timeout after its last attack or
  hit. A living character out of combat regenerates health and mana every
  second up to its maximum. Mana is owner state: only `OwnerCombatState`
  carries it. A respawn starts with full health and mana. A joining character
  starts with the health and mana it was saved with, or with full health and
  mana when it has no saved state, as
  [Character Place And Vitals](#character-place-and-vitals) describes.
- Every damageable Mob with a combat profile is a combatant, and a Mob with a
  brain and a locomotion profile also thinks and walks, as
  [Mob Brains](#mob-brains) describes. At 0 health a Mob can no longer be
  targeted, and every attacker's target on it clears. Its death event id is
  `ScopedGuid("mob-death", runtimeActorId)`; each Mob life has its own runtime
  id, so the id is stable for retries and unique per death. The Mob rolls its
  loot profile in entry order, where each entry drops with its chance in basis
  points and a quantity between its bounds, and the corpse's owner rolls the
  Mob's [quest drops](#quests) after it. A non-empty roll becomes the Mob's
  corpse, owned by the player the Mob credits as the next item describes: a
  live corpse in worker memory, or for a Mob whose content selects durable
  custody a durable corpse in GameService. A creation that fails retries with
  the same corpse id and a backoff from 0.5 seconds that doubles up to 10
  seconds until it succeeds or reaches the
  [retry horizon](#gameservice-outage), while the dead Mob stays in view. When
  the corpse
  can never be created, because its source, authority, persistence mode, or
  content is invalid, such as a quest drop that is not a protected quest item,
  or when GameService answers that its owner needs none of the quest drops that
  were its only loot, the Mob leaves without one. The Mob
  then leaves every view with the
  despawn reason `died`, a Mob without a corpse at the end of its death tick,
  after its death reached the viewers, and its spawn instance returns after its
  respawn profile's delay with a new runtime and entity id. Respawn timers reset
  on a worker restart.
- A Mob whose content gives `tag` kill credit, every Mob today, is tagged by the
  first character whose hit damages it; a later hit by someone else never takes
  the tag over. `CombatantState.TaggedByEntityId` names the tagger to every
  viewer, and a tagger that rejoins keeps the tag under its new entity. The tag
  clears when the Mob gives up the fight and evades. When the Mob dies, it
  credits its tagger only while the tagger is in the world within the combat
  rules' kill credit range, 100 m, of the Mob, dead or alive; otherwise it
  credits no one. A Mob with `contributors` kill credit, the Feral Alpha, is
  never tagged: it remembers every character whose hit damaged it in the fight,
  forgets them when it evades, and credits each one in the world within the same
  range. The corpse of a tagged Mob belongs to the credited character. A Mob with
  contributor credit rolls its loot once for every credited character, in
  character id order, and leaves each its own corpse, whose id derives from the
  death and the character. The corpses of one death share a loot group, and a
  Mob that credits no one leaves one corpse that belongs to everyone. The Mob
  despawns once every corpse exists or can never be created.
- A disconnect does not cancel combat work. The session stays until the death
  and the last arrow batch committed and is released afterwards, and a leave
  waits for the same work. A worker that stops commits the uncommitted shots it
  can still name exactly before it releases its sessions and marks itself
  offline, as a best effort, including the shots behind a batch
  whose answer arrived while it stopped; a batch without a committed answer is
  left alone, and its arrows stay with the player. A worker crash loses at most
  the uncommitted shots of one batch interval, which stay with the player, and a
  death between the lethal hit and its commit, after which the character keeps
  its items.
- `CombatantState` follows every spawn of a combatant, and every changed
  combatant reaches the viewers that see it in detail once at the end of the
  tick, every viewer when its life state changed, and the others once a second.
  Combat events reach whoever controls the source or the target and the peers
  that see either in detail, and a death every peer that sees either, as
  [Detail Radius](#detail-radius) describes. `OwnerCombatState` reaches the
  owner.
- Health regeneration travels as a rule, not as a state per step. The state of
  a living player out of combat below its maximum health names the
  regeneration amount of the combat rules and the server tick of the next
  step, which is the current tick when that step is already due, and every
  viewer computes the health between states with `RealtimeCombatHealth`, as
  [Combat](REALTIME_PROTOCOL.md#combat) describes. A step on the tick that the
  state named changes only the owner state, so the owner still receives its
  health and mana every second and other viewers receive nothing. Everything
  else that changes a player's health or its regeneration is a new state for
  the viewers: a hit, a death, a respawn, entering and leaving combat, vitals
  restored at a join, and the admin's full health. A step after its named
  tick, such as the first one after restored vitals or after a combat phase
  that failed, is a new state too, because it starts another rhythm. Mobs do
  not regenerate; a Mob that arrives home gets its full health as a new state.
- Synthetic development bots are no combatants, because they have no durable
  character that could die. An attack on one returns
  `combat_target_not_attackable`.

## Mob Brains

A Mob with a brain profile and a locomotion profile runs a brain in
SimulationWorker, in the Mob phase of every tick before combat. Nothing gets its
own task, timer, or thread. The values below are those of the shipped profiles
in `content/actors/catalog.json`:

- The activity tiers decide who works. A dormant Mob neither thinks nor moves; a
  coarse check at the activity profile's dormant rate, every 2 seconds for
  `mob.standard`, makes it active when a player comes within the activation
  range, 48 m, and dormant again when none is within the dormancy range, 64 m. A
  Mob that fights, walks home, or died stays active whoever is near. One grid of
  player positions per tick answers these checks and detection, instead of
  comparing every Mob with every player.
- An active Mob thinks at the activity profile's active rate, 10 per second,
  staggered by entity id, and a moving Mob steps every tick with the player motor
  and collision at its locomotion profile's capsule: at its patrol speed while
  it patrols, 1.4 m/s for the wolves, and at its walk speed while it chases or
  walks home, 4.5 m/s. Mobs do not collide with players or with each other.
- The brain is a state machine: `Idle` stands at the spawn, `Patrol` walks the
  spawn's patrol path with its waits, `Chase` walks toward the target, `Attack`
  stands still and bites on the combat profile's cooldown, `Evade` walks home,
  and `Dead` waits for the corpse and despawn.
- An aggressive brain detects the nearest living player within its detection
  range, 8 m for the Feral Wolves and 10 m for the Feral Alpha, with line of
  sight. The defensive brain `mob.defensive` of the Grey Wolf detects no one.
  Damage starts a fight for every brain. Every spawn of a Mob carries its
  brain's aggression, so clients can color its nameplate. When the target dies or becomes
  invalid, the Mob takes the attacker with the most damage that is still alive,
  then the nearest detected player, and evades otherwise.
- A chasing Mob walks to half its attack range from the target and plans again
  when the target moved more than 1.5 m from the planned goal. In range with
  line of sight from its eye, at 80 percent of its capsule height, it stands,
  faces the target, and combat lets it bite: the bite lands at once, rolls the
  combat profile's damage, and may kill the character with the Mob as the
  recorded killer.
- The leash and the give-up time end a fight. A Mob that is farther than its
  leash range, 30 m, from where the fight started, the spawn of an idle Mob or
  the patrol position of a patrolling one, or that neither bit nor was hit for
  its give-up time, 10 seconds, evades: it clears its target, cannot be damaged,
  walks back, and on arrival has full health and returns to its routine. A Mob
  that cannot walk home after three routes is placed there.
- Routes come from the shared `MovementPathPlanner`, one planner per locomotion
  profile, through one queue that plans at most 8 routes per tick; each route
  expands at most 4,096 grid cells and ends at the closest reachable point when
  the goal cannot be reached. A Mob has at most one waiting request. Spawn
  points and patrol points are projected onto the ground under them, searched
  20 m above and below their authored height, and a Mob starts to move only once
  the collision under it is loaded; walking Mobs keep their collision loaded
  like players.
- Every living Mob that can walk travels in the unreliable `SimulationSnapshot`
  like a remote player and by the same rules, so a standing one costs only its
  refresh, and the snapshots change neither the state nor the interaction
  revision. The first tick it stands still sends one reliable `WorldActorState`
  with its rest pose, which advances the state revision but never the
  interaction revision, to the viewers that see it in detail; the others get
  it on the Mob's slot once it stands, as [Detail Radius](#detail-radius)
  describes.
- A Mob's `WorldActorSpawn` names the server tick of its pose and whether the
  Mob walks then: whether its pose changed in its last tick, so the pose is not
  yet the rest pose that the worker sent reliably. The moving Mobs gauge counts
  the same Mobs. An NPC, a resource node, and a hidden Mob never walk.

## Character Looks

How looks work is in
[The Look On The Server](EQUIPMENT_VISUALS_DESIGN.md#the-look-on-the-server).
The servers implement it as follows:

- GameService stores each character's body model, look choices, and lowered
  headwear in `character_appearances` and checks every stored look against
  `content/characters/appearance.json`. Character creation rejects a look that
  the body model does not offer with `invalid_character_appearance`.
- SimulationWorker loads the same appearance content from
  `SimulationWorker:CharacterAppearancePath` and sends each player's look, the
  items of its visible equipment slots, its headwear, and the tool it uses while
  it gathers, as [Character Appearance](REALTIME_PROTOCOL.md#character-appearance)
  describes, at once to the viewers that see the player in detail and once a
  second to the others, as [Detail Radius](#detail-radius) describes.
  Development bots get a look and an outfit that the worker picks from their
  character id.
- An `AppearanceIntent` lowers or raises the headwear, or in the Development
  environment stores another body model and look. The worker forwards it to the
  appearance route, which refuses a malformed change with
  `invalid_appearance_change`, a development look outside the Development
  environment with `development_look_disabled`, and a look that the appearance
  content does not offer with `invalid_character_appearance`. One change per
  peer runs at a time, and a second one while it runs is answered with
  `appearance_change_in_flight`. A development bot cannot change its look.

## Development Admin Tools

A SimulationWorker with `DevelopmentAdmin:Enabled` lets every player, but never
a synthetic bot, use the tools of the Unity Admin menu (F3). The option fails
startup outside the Development environment, is on in the worker's
`appsettings.Development.json`, and the worker logs a warning when it is on.

- After every join, a reconnect too, the worker sends the owner
  `AdminStateChanged` with its walk speed percent and invulnerability. A client
  that receives none knows the tools are off.
- `AdminIntent` asks for one tool and `AdminIntentResult` answers it; a worker
  without the option answers `admin_tools_disabled`.
  - `SetMovementSpeed` walks the character at 100 to 1000 percent of the World's
    walk speed: `AuthoritativePlayerMovement` steps with
    `MovementSimulationSettings.WithWalkSpeedPercent`, the shared method the
    client predicts with. 1000 percent, 50 m/s, stays far inside the motor's
    substep budget, and the step budget still allows one step per input.
  - `Teleport` moves a living character onto walkable ground at a point inside
    the World bounds, standing still, and despawns and spawns it again for
    everyone who sees it, as a respawn does, with the despawn reason
    `teleported`. When the collision chunks around the destination are not
    loaded, it waits for them and answers on a later tick, once the player
    stands there; meanwhile the player plays on where it was. A teleport that
    moves the character, or begins to wait, while an older one still waits
    replaces the older one, which answers `admin_teleport_superseded` once its
    chunks are in, so the character ends where the player asked last. A
    dead character gets `admin_character_dead`, a point outside the World
    `admin_teleport_outside_world`, and a point with no walkable ground near it
    `admin_teleport_no_ground`.
  - `RestoreHealth` fills a living character's health and mana.
  - `SetInvulnerable` makes hits leave the character's health as it is, as an
    evading Mob's are. Without combat it answers `admin_combat_unavailable`.
- The walk speed and invulnerability belong to the character's entity, so they
  last until the session ends, and a reconnect of the same session keeps them.
  Nothing is stored in PostgreSQL.
- The quest tools are `QuestIntent` kinds, which run on the character's quest
  route like abandoning, as [Quests](#quests) describes; a worker without the
  option answers them with `admin_tools_disabled`. GameService runs them only
  in the Development environment and answers every other environment with
  `403` and `development_tools_disabled`.

## Content Files

GameService serves the content files that a client needs to run the current
content, so a change of rules data or of a text needs no new client build, as
[Client And Server Agreement](PROJECT_ARCHITECTURE.md#client-and-server-agreement)
sums up. The Content module serves exactly the files the other modules run with:

| Kind | Id | File |
| --- | --- | --- |
| `item-catalog` | `core`, the catalog id | The file at `Items:CatalogPath`, which Items mirrors |
| `quest-catalog` | `core`, the catalog id | The file at `Quests:CatalogPath`, which Quests runs and mirrors |
| `text-table` | the table id, such as `items` or `ui` | Every `*.text-table.json` file in `Content:TextTablesPath`, one locale folder |

At startup the module reads every file once and keeps its bytes in memory. It
checks the catalogs with the same loaders that Items, Quests, and
SimulationWorker use and requires their headers to state the revision those
loaders compute, and it checks every text table with `TextTable.Validate`.
GameService does not read the texts; it serves the tables as they are. The
start fails with `GameService cannot serve its content files:` and a list of
every problem when a file or the text table folder is missing, the folder holds
no text table, a file is invalid, or two text tables have the same locale and
id. Once the database is ready, the hosted `ContentRevisionCheck` reads the
catalog revisions that Items and Quests mirrored and refuses to start with
`GameService would serve content that its modules do not run with:` when either
served catalog differs. When the database cannot be read at that moment, the
check logs a warning and is skipped, and readiness reports the database.

`GET /api/content` returns the manifest:

```json
{
  "formatVersion": 1,
  "files": [
    {
      "kind": "item-catalog",
      "id": "core",
      "formatVersion": 9,
      "revision": "cc4929be...",
      "sizeBytes": 23779,
      "url": "/api/content/item-catalog/core/cc4929be..."
    },
    {
      "kind": "text-table",
      "id": "ui",
      "locale": "en",
      "formatVersion": 1,
      "revision": "616a915e...",
      "sizeBytes": 45938,
      "url": "/api/content/text-table/en/ui/616a915e..."
    }
  ]
}
```

- The files are listed in a fixed order: the item catalog, the quest catalog,
  then the text tables by locale and id. `locale` is present for text tables
  only and left out for the catalogs. `formatVersion` and `revision` are the
  file's own header fields, and `sizeBytes` is the length of the served bytes.
- `url` is the path of the file route. When `Content:BaseUrl` is set, it is that
  base plus the same relative path, for example
  `https://cdn.example.com/content/item-catalog/core/<revision>`, so a static
  file host that mirrors the relative paths can serve the files later without a
  client change.
- The manifest is sent with `Cache-Control: no-cache` and a strong ETag, the
  SHA-256 of its bytes, and answers `If-None-Match` with `304 Not Modified`.

`GET /api/content/{kind}/{id}/{revision}` serves a catalog, and
`GET /api/content/text-table/{locale}/{id}/{revision}` a text table, whose
locale is a path segment of its own. Both return the file's exact bytes with
`Content-Type: application/json`,
`Cache-Control: public, max-age=31536000, immutable`, and the revision as a
strong ETag, and answer a matching `If-None-Match` with `304`. Only the current
revision of each file exists. The route values are checked before any lookup:
`kind` must be `item-catalog` or `quest-catalog` without a locale, or
`text-table` with one, `id` a lowercase id of at most 64 letters, digits, `-`,
`_`, or `.` that starts with a letter, `locale` a lowercase locale such as `en`
or `pt-br`, and `revision` 64 lowercase hexadecimal digits. Every miss, also a
known file at an older revision, is `404` with the code `content_not_found`.

The routes are anonymous, because the files hold nothing secret, they ship
inside every client, and a client fetches them before login. They share the
`content` rate limit: a fixed window per
[client address](#the-clients-address) of
`RateLimiting:Content:PermitLimit` requests per
`RateLimiting:Content:WindowSeconds`, 120 a minute by default. A client start
needs the manifest and about seven files, so the default leaves room for many
starts behind one address while it bounds anonymous traffic. A rejected request
gets `429` with `Retry-After` and the code `rate_limit_exceeded`. Unlike login
and registration, the content limit is process-local and has no Redis window.

The Unity client still uses the catalogs and text tables baked into its build;
resolving them from these routes at start, before login, with a disk cache keyed
by revision is the next step.

## Items And Corpses

The item rules are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), the
durable item boundary in
[Durable Item Boundary](PROJECT_ARCHITECTURE.md#durable-item-boundary), and the
kernel and its operation kinds in
[Items Transaction Kernel](GAME_SERVICE_MODULES.md#items-transaction-kernel).
This section describes the implemented behavior around them.

### Item Catalog

- GameService bundles the checked-in runtime gameplay catalog from
  `Items:CatalogPath`, revalidates its revision and structural fingerprints, and
  mirrors the full relational definition structure in one advisory-locked
  PostgreSQL transaction before accepting traffic. It refuses to start when the
  new catalog would invalidate live state, as
  [Revisions And Structural Changes](../content/items/README.md#revisions-and-structural-changes)
  describes; such a change needs an explicit data migration.
- GameService serves the compiled catalog file itself, byte for byte, by its
  revision, as [Content Files](#content-files) describes. The catalog holds ids
  and rules and no names: GameService stores no text of the catalog, and a
  client reads names from its item text table.
- SimulationWorker runs on the same compiled catalog, and placement refuses a
  worker whose catalog revision differs, as
  [Shard Discovery And Placement](#shard-discovery-and-placement) describes.
- Server snapshots must match the gameplay revision the client runs, or the
  Unity client reports `item_catalog_update_required` and refuses stale item
  state.

### Item State Reads

- The item-state, bank, and Recovery routes first verify exact account and
  active-character ownership, then read permanent inventory, equipment, equipped
  Bag contents, bank, Recovery deliveries, revisions,
  encumbrance state, and the currency balance from one repeatable-read
  snapshot. An owning account may inspect its bank and Recovery Storage from
  anywhere; that read authorizes no mutation.
- Recovery reads return only unclaimed deliveries.
- Item instance DTOs contain only instance id, stable definition id, quantity,
  revision, and active policy kind and status. Policy sources, recovery source
  event ids, operation payloads, structural fingerprints, credentials, icons,
  and other client presentation data are excluded.
- Weight and capacity remain unitless integers. Load ratio and movement
  multiplier are returned as deterministic basis points.
- Character-specific reads and writes return `Cache-Control: no-store` and
  `Pragma: no-cache`. GameService never returns icon bytes, Unity references, or
  client presentation entries.

### Item Transactions

Every durable item mutation commits or rejects as a whole through the kernel.
Seen from outside:

- Every mutation carries an operation id. Replaying the same id and canonical
  payload returns the stored result; reusing it with another payload returns
  `item_operation_conflict`. A retry of a committed SimulationWorker operation
  replays its stored result even when the live session has ended since, so a
  lost response never turns a committed change into a reported failure.
- Player-chosen operation ids, from account routes and from SimulationWorker
  requests other than the retried system operations, must be random version 4
  UUIDs or the command returns `item_operation_id_invalid` without claiming the
  id. Every derived system id is a version 5 UUID, so a player cannot claim an
  id that a system operation needs.
- Two kinds of rejection are returned without being stored, so the same id can
  run again: `item_transaction_timeout`, which also covers lock timeouts,
  statement timeouts, deadlocks, and serialization failures, and every
  rejection of a retried system operation: player death, durable Mob corpse
  creation, single live Mob loot grants, corpse expiry, and Recovery expiry. A
  live Mob loot grant therefore stays claimable after a rejected attempt until
  one claim commits.
- A canonical command above `Items:Operations:MaximumCommandPayloadBytes` is
  rejected with `item_command_payload_too_large` before any database access,
  and an HTTP body above `MaximumHttpRequestBodyBytes` never reaches it.
  Statements and lock waits are bounded by `TransactionTimeoutMilliseconds` and
  `LockTimeoutMilliseconds`.
- Player mutations carry the expected character item-state revision, and
  targeted item, Bag, and corpse mutations carry the revisions of the rows they
  target, so an unrelated change in the same container does not reject a
  targeted loot request. A stale revision returns `item_state_conflict`,
  `item_quantity_changed`, `bag_state_changed`, or `corpse_state_changed`.
- Voluntary mutations enforce the exact 140 percent hard cap with
  `carry_weight_limit_exceeded`. The kernel is the only hard-cap authority; no
  row-level check exists, so an authoritative death never fails on carry
  weight, and a character above the cap, such as one from older data, commits
  only non-worsening remediation.
- Equipping into a slot that an equipped item blocks, or with an item that would
  block an occupied slot, is rejected with `equipment_slot_blocked`, and below
  the item's required character level with
  `equipment_level_requirement_not_met`, on every path into an equipment slot,
  including corpse Bag swaps. An equip request for a stack of the equipped
  definition is rejected with `equipment_slot_occupied`, because it tops up the
  equipped stack through a merge instead.
- `grant_mob_loot` grants into an empty container slot or, with an equipment
  slot instead, an empty equipment slot under a derived item id, or tops up the
  stack the worker names in that slot when the two merge within the slot limit.
  It never swaps, because the worker cannot take live Mob loot back, so a named
  stack of another item is rejected with `item_slot_occupied` or
  `equipment_slot_occupied`.
- A quest drop is live Mob loot or a durable Mob corpse entry that names
  `questId`: a protected quest item within its stack limit that the quest's
  current step collects with the Mob as a drop source, as
  [Quest Drops](QUEST_SYSTEM_DESIGN.md#quest-drops) describes. It is granted with
  the quest's grant lineage, never into an equipment slot, and only up to the
  quantity the character still needs, which is the objective's count minus the
  items with the lineage it owns anywhere; a drop that its quest no longer needs
  is rejected with `quest_item_not_needed`. Ordinary Mob loot stays policy-free,
  and loot that names no quest is never a quest item. Loot all leaves a quest
  drop that is not needed in the corpse, and drops of one quest item share what
  the quest still needs. A durable Mob corpse names up to 24 entries of the
  Mob's own loot and up to 64 quest drops, needs an owner for its quest drops,
  keeps only what that owner needs in the entries that its own loot leaves of
  24, in request order, and is not created, with `mob_corpse_empty`, when no
  loot is left. Its response lists the quest drops it kept, in corpse order, as
  `questDrops`, also when the worker asks again with the same operation. A
  quest drop leaves a durable Mob
  corpse only into its owner's carried storage, and only while the owner's quest
  needs all of what moves; the corpse's expiry destroys it like any other loot.
- Every worker mutation response, from item operations, ammunition batches,
  corpse operations, live Mob loot grants, and player deaths, carries
  `characterState`: the acting character's item-state revision, carry tuple,
  and equipment, read right after the commit. A later commit can make it newer
  than the operation's own revision but never older, and SimulationWorker
  rejects a response whose state is older, belongs to another character, or has
  a duplicate equipment slot or item. Stored operation results stay unchanged,
  so a replay returns the same result with the current state.

### Offline Account Item Routes

- Account mutation requests derive authority from the authenticated principal
  and use an offline-required actor. Character and item-state rows are locked
  before checking active simulation sessions, using the same character lock as
  simulation admission. An active simulation session returns
  `item_offline_access_required` without partial mutation.
- Bank and Recovery Storage sources and destinations are never reachable
  offline, so Recovery deliveries are claimed only in the world.

### Live Item Operations

- SimulationWorker accepts item intents only from an exact joined player on the
  owner channel, runs at most one per peer at a time in the character's
  authority lane, and bounds each pending queue to eight operations; more return
  `item_operation_queue_full`. Synthetic development bots cannot mutate durable
  items.
- Bank and Recovery Storage custody require the matching access flag. The worker
  sets it when the operation starts while the player has an open interaction
  with a world actor whose `bank` or `recovery_storage` capability is available;
  otherwise GameService rejects with `bank_access_required` or
  `recovery_access_required`. The interaction closes when the player leaves its
  range or loses line of sight, which ends the access. Insurance and quest
  actions pass their flags only
  through their own NPC capability, and vendor trades pass the vendor profile of
  the validated `vendor` capability.
- In the Development environment, `DevelopmentItemInteractions:GlobalBankAndRecoveryAccess`
  grants Bank and Recovery access from any position for local item testing; the
  worker's `appsettings.Development.json` turns it on. Startup rejects it in
  every other environment, and it never includes insurance access.
- The worker service-authenticated endpoint carries the exact account,
  character, simulation session token, worker id, runtime id, and Shard. The
  authenticated worker id must match the body, or the route answers
  `wrong_simulation_worker`. GameService locks and validates the character, item
  state, active simulation session, active account session, online worker
  runtime, and current assignment inside the same transaction that runs the
  command.
- A committed operation returns its item changes, which the worker sends to the
  owner as `OwnerItemStateChanged` when one packet can describe them, as
  [Owner Item State Changes](REALTIME_PROTOCOL.md#owner-item-state-changes)
  describes; the client refreshes its inventory otherwise. A committed result
  without committed character state, or whose revisions contradict the active
  simulation state, ends the session with `item_state_diverged`.

### Player Death And Durable Corpses

- A player-death request carries the expected character revision, the
  transform, and the presentation key `corpse.remains`. It may carry
  `ammunitionConsumption`, the shots that were not committed yet, which the
  death transaction consumes before it partitions the remaining stack into the
  corpse, and `killer`, either `mob` with an actor definition id or `character`
  with a character id. `death_events` stores the killer, with a character id in
  canonical form. Both fields are optional, and a death without them keeps the
  request fingerprint it had without them.
- Death-event ids add a second idempotency fence above the operation journal. A
  replay with the same authoritative character, Shard, transform, and
  presentation data returns the original corpse, absolute expiry, Recovery
  deliveries, and committed character revision without repartitioning. Reusing
  the event id with different data returns `death_event_conflict`. The event row
  is removed together with its closed corpse once `Retention:ItemHistory` passed
  after the corpse closed. The partition always advances the character
  revision, so a replay that outlives the event row is rejected as a revision
  conflict instead of partitioning twice.
- The database constrains a player corpse's expiry to its database creation time
  plus exactly five minutes, and its transform to a bounded position and a
  normalized quaternion. Snapshot rows contain no item-instance identity.
  Durable Mob corpses use the lifetime their content selects.
- `CorpseExpiryHostedService` scans database-time deadlines in bounded batches.
  Each corpse owns one durable expiry operation id. Cleanup locks the corpse,
  records one `item_destructions` and item-operation audit entry per remaining
  instance, deletes children before Bag roots, closes remaining section
  containers, and marks the corpse expired. Empty player corpses are not closed
  early. A request that arrives after expiry receives `corpse_expired`.
- After obtaining a current registration lease, SimulationWorker restores only
  open, unexpired corpses for its exact runtime and Shard assignment, and checks
  again every `ItemInteraction:DurableCorpseRefreshSeconds` while GameService
  answers its registry heartbeat. GameService returns
  database time with the snapshot. The worker advances that time with a
  monotonic clock and keeps the presentation state without owning or copying
  item custody. A worker restart restores each corpse at its persisted
  transform with the unchanged deadline.

### Corpse Interaction

- SimulationWorker exposes corpse presence within
  `ItemInteraction:CorpseDiscoveryRadius` of the player, twice per second, and
  sends a player its presence again only when it changed. Each pass puts the
  active corpses into cells of the discovery radius, so a player is tested
  only against the corpses around it, and a changed presence is cut into
  chunks by the length of each corpse's entry, which it encodes once. One
  player can inspect one corpse at a time, within
  `ItemInteraction:CorpseInteractionRadius`, while any number of players may
  inspect the same corpse. The worker revalidates its authoritative
  three-dimensional position and cached absolute lifetime before every open,
  refresh, or mutation, and rejects with `corpse_out_of_range`,
  `corpse_view_not_open`, or `corpse_interaction_active`. GameService repeats
  exact-session, Shard, and lifetime authority inside the transaction boundary.
- The corpse of a Mob that credited a character belongs to that character, as
  [Target-Based Combat](#target-based-combat) describes, and so does a durable
  boss corpse, whose `corpses.owner_character_id` names its owner and
  `corpses.loot_group_id` the loot group of its death. Of the corpses of one
  loot group, a recipient's presence holds one: its own, or else the one with
  the smallest id. Each corpse presence tells its recipient in `IsLootable`
  whether it may loot the corpse.
  Every open and mutation of another character's corpse is rejected with
  `corpse_not_lootable`, which closes the view, first by the worker and again
  by GameService when it opens a durable corpse and when it locks the corpse in
  any corpse transaction; only expiry, as the system, empties an owned corpse.
- A creature's corpse gives whole items only. The worker refuses everything but
  a whole-item loot for the live corpses it keeps, with
  `live_mob_corpse_operation_unsupported`. GameService refuses the same for a
  durable creature corpse, one whose `source_type` is `persistent_npc`, with
  `corpse_whole_items_only`: a part of a stack, to the inventory or to an
  equipment slot, a deposit, a move inside the corpse, a Bag swap, and a loot
  that would swap with the item in its destination slot or equipment slot,
  which would put that item into the corpse. `Loot
  all` leaves a stack of such a corpse that does not fit whole, as the client
  predicts it.
- Corpse reads use short read-only snapshots. Full and partial transfers,
  internal corpse moves, ordinary occupied-slot swaps, and atomic Bag aggregate
  swaps reuse the kernel with targeted item, container, and Bag-root revisions.
  Internal moves can split, merge, or swap across the three corpse sections
  without advancing character item-state or carry. Unrelated corpse changes may
  commit concurrently, competing custody has one stable winner, and no
  PostgreSQL transaction remains open across a client network wait.
- Loot all locks the looter, the corpse at the revision the client saw, and
  every corpse and carried item, plans the steps with `CorpseLootAllPlanner`
  from the locked state, and commits each step under the checks of the single
  loot it stands for. A corpse that changed answers `item_state_conflict`, and a
  Loot all that takes nothing answers `inventory_full`. A live Mob Loot all
  plans the worker's entries the same way with whole items only.
- Every live Mob grant, single or Loot all, records its grant id in
  `live_mob_loot_grants`, so a grant committed once is never committed again. A
  single loot of a recorded grant is rejected as `item_already_looted`, and the
  worker then takes the entry out of the corpse.
- The worker broadcasts each committed targeted delta or replacement to every
  current viewer. An open or refresh always gives its requester a complete
  snapshot, while the other viewers receive the delta from the newer revision.
  Its runtime store accepts only monotonic corpse revisions. A completion that
  arrives after its peer disconnected never releases the corpse reservation of
  a new peer that reuses the LiteNetLib peer id; disconnect cleanup releases
  every reservation of the old peer first.
- Expiry, invalidation, and missing-corpse results close affected views with
  stable codes, while stale item or quantity conflicts trigger a refresh. Taking
  the last item of a live Mob corpse closes every view with `corpse_emptied`
  instead of `corpse_invalidated`, also when the presence broadcast notices the
  emptied corpse first. The emptied corpse stays inactive in worker memory until
  its deadline, so a replayed creation cannot restore its loot. The closure
  codes are listed in [Realtime Codes](#realtime-codes).
- GameService accepts `corpse.remains` as the presentation key of a player
  death, and only `corpse.mob` and the keys below it for a durable Mob corpse,
  as [Corpse Presence Facing](REALTIME_PROTOCOL.md#corpse-presence-facing)
  describes. Every Mob corpse gives whole items only.

### Development Item Commands

The Development item tool is a set of process-local GameService commands, not
HTTP routes, so no development grant route exists. Each runs in the Development
environment only, against a loopback PostgreSQL host whose database name does
not contain `prod`, applies every change through the item transaction kernel,
and requires an offline target character. The Unity window
`MMORPG > Tools > Inventory Item Grants` drives them, and
[Development Item Commands](MANUAL_TESTING.md#development-item-commands) shows
how to run them.

| Command | Behavior |
| --- | --- |
| `--dev-items-list` | Report every local character with its balance, the active definitions, and the development packages |
| `--dev-items-grant <characterId> <definitionId> <quantity> <permanent\|bank>` | Grant one stack into the permanent inventory or the bank |
| `--dev-items-package <characterId> <packageId>` | Apply one package from `content/development/item-packages.json` to a new empty character. It refuses a replay once an item or Recovery delivery exists, and rejects a package that misses its exact expected load |
| `--dev-items-corpse <characterId> <shardId> <x> <y> <z>` | Apply the corpse seed package to an empty source when necessary, then run the normal durable death transaction at that Shard position. The corpse keeps its database deadline across a worker restart |
| `--dev-items-level <characterId> <level>` | Set the character's level, because no experience exists yet |
| `--dev-items-currency <characterId> <amount>` | Credit a positive whole amount through the wallet with a `development` ledger row |

## Quests

[Quest System Design](QUEST_SYSTEM_DESIGN.md) owns the quest rules; the Quests
module and its tables are in [GameService Modules](GAME_SERVICE_MODULES.md).

- GameService loads the compiled quest catalog from `Quests:CatalogPath`,
  revalidates its structure, values, structural fingerprints, and revision,
  checks every item it names against the item catalog, and mirrors the revision,
  every quest id with its fingerprint, and every retired id into PostgreSQL
  before it accepts traffic. It refuses to start when a quest left the catalog without
  being listed as retired, a retired id returns, or a quest that characters have
  active was retired or changed its steps or objectives; such a change needs an
  explicit data migration. An unchanged revision is neither checked nor
  written. When the revision changed, the same transaction moves
  every active quest whose current step, not the last, the changed counts
  complete on to its next step and raises its quest log revision.
- `GET /api/characters/{characterId}/quests` verifies exact account and
  active-character ownership and returns, from one repeatable-read snapshot,
  the quest catalog revision, the quest log revision, every active quest with its
  current step, acceptance time, and the progress of the step's counted
  objectives, and every completed quest with its completion count and last
  completion time. Collect objectives count carried items, so they carry no
  stored progress. A character without quests has quest log revision 0. Quest
  texts and rules are not part of the response; the client reads them from the
  quest catalog it resolved at start.
- `POST /api/simulation-sessions/{id}/quest-log` returns the same quest log to
  the worker that hosts the character's live simulation session, for the quest
  state the worker keeps while the character plays. The account, character,
  Shard, worker, runtime, and session token must name that session, with the
  checks and codes of the worker's corpse view: `400`
  `invalid_simulation_session_request` for an incomplete request, `401`
  `simulation_session_invalid` for a released, expired, or other session, `409`
  `worker_runtime_changed` for another runtime or a stale registry lease, and
  `403` `wrong_simulation_worker` for another authenticated worker or a lost
  Shard assignment.
- Every worker quest route, this read, the quest operations, and the quest drops
  below, takes `questCatalogRevision`, the revision of the quest catalog the
  worker runs, and refuses one other than GameService's with `409`
  `simulation_worker_quest_catalog_mismatch` before anything else happens.
- `POST /api/simulation-sessions/{id}/quest-operations` runs one quest action
  of the session's character as an item transaction, with the operation id and
  replay rules of [Item Transactions](#item-transactions). `operationKind` names
  it, `questId` the quest, and `questNpcActorId` the NPC whose quest capability
  the worker validated:
  - `accept_quest` at the quest's giver checks the order of
    [Requirements And Availability](QUEST_SYSTEM_DESIGN.md#requirements-and-availability)
    and grants the start items into carried storage with the quest's lineage.
    It rejects with `quest_already_active`, `quest_already_completed`,
    `quest_requirements_not_met`, `quest_level_too_low`, `quest_log_full`, or
    `inventory_full` and changes nothing.
  - `abandon_quest`, anywhere, ends the quest without a completion and removes
    every item with its lineage, also from the Bank and Recovery Storage.
  - `regrant_quest_items` at the giver grants the start items that the quest
    still needs and the character no longer owns, and grants nothing when it
    owns them.
  - `talk_to_quest_npc` and `deliver_quest_items` at the objective's NPC
    complete the talk-to or deliver objective `objectiveId` of the current step;
    a delivery takes what the objective still lacks from carried storage, its
    count less its stored progress, or rejects with `quest_items_missing`. A complete current step moves the quest on. An
    objective outside the current step, or of another kind, rejects with
    `quest_objective_invalid`, and a complete one with
    `quest_objective_complete`.
  - `turn_in_quest` at the turn-in NPC completes a ready quest: it takes the
    collected items, removes what is left with the quest's lineage, places the
    reward items with the one that `rewardChoiceDefinitionId` names, and credits
    the Gold reward with a `quest_reward` ledger row. It rejects with
    `quest_not_ready`, `quest_reward_choice_required`,
    `quest_reward_choice_invalid`, or `inventory_full` and changes nothing.
  - `credit_quest_kill` counts one credited kill of the Mob that `mobActorId`
    and at most 32 `mobTags` describe for every matching kill objective of the
    character's current steps; a kill that no step asks for changes nothing,
    and an active quest that the catalog does not hold gets no credit without
    holding back the others.
  - The Admin menu's quest tools, which only a Development GameService runs:
    `admin_accept_quest` accepts any quest that is not active, and not
    completed unless it repeats, around its giver, level, and required quests,
    with its start items. `admin_complete_quest_objective` completes the
    objective `objectiveId` of the current step, `admin_complete_quest_step`
    every objective of the current step, which moves the quest on, and
    `admin_make_quest_ready` every step up to the last: a counted objective
    reaches its count, and a collect objective gets the items it lacks into
    carried storage, a quest item with the quest's lineage. `admin_reset_quest`
    forgets a quest with its completions, `admin_reset_quest_chain` also every
    quest that requires it, directly or through another, and
    `admin_reset_quests`, without a quest, every quest; each removes the items
    of the forgotten quests' lineage with the `quest_reset` destruction reason.
    A tool that leaves the quest log as it was, such as filling a collect
    objective, reports no quest change.
  - A quest that the catalog does not hold returns `404` with `quest_not_found`,
    an action at another NPC `403` with `quest_npc_access_required`, and a quest
    that is not active `409` with `quest_not_active`.

  The response is the worker item route's response. Its `questLog` carries the
  quest log revision and, for each changed quest, its current step or none once
  it is no longer active, the progress of its counted objectives, and its
  completion count. An action that moves no item, such as a talk, names no
  character revision; granting start items again changes no quest and names no
  `questLog`.
- `POST /api/simulation-sessions/{id}/quest-drops` answers how much of the quest
  drops that a dying Mob rolled for the session's character, named by
  `sourceActorDefinitionId` and up to 64 `drops` of `questId`, `definitionId`,
  and `quantity`, the character's quests still need, by the rules of a quest
  drop in [Item Transactions](#item-transactions): the needed drops in request
  order, each at most what its quest still needs after the drops before it, and
  without the drops that no quest needs. It only reads, from one repeatable-read
  snapshot, with the session checks and codes of the quest log read, and `400`
  `item_simulation_operation_invalid` for an incomplete request. The grant
  checks the need again.

SimulationWorker keeps the quest log of every player in its sessions and runs
the quest actions of the realtime protocol through these routes:

- After it consumed a join ticket, it reads the character's quest log with the
  worker route. When the read fails, or GameService answers with another quest
  catalog revision than the worker runs, which is
  `simulation_worker_quest_catalog_mismatch`, it releases the session and
  rejects the join. A reconnect of a session that the worker still hosts keeps
  the quest log it holds instead when the read fails for a moment, with a
  timeout, an unreachable GameService, or a 5xx answer, because releasing the
  session would lose the death or the kill credits it may still have to
  commit; the log turns stale, so the next quest action reads it first. A read
  older than the log that the same session already holds never takes the log
  back, and a session released before its peer took it over drops its log. A
  development bot never takes part in quests.
- The `quests` capability opens without GameService. Its actions,
  `AcceptQuest`, `RegrantQuestItems`, `TalkToQuestNpc`, `DeliverQuestItems`, and
  `TurnInQuest`, run through the open interaction, so range, line of sight, and
  the interaction lease apply, and name the NPC's actor definition as
  `questNpcActorId`, which GameService checks against the quest.
- A `QuestIntent` abandons a quest from anywhere. It queues behind the player's
  other authority work, like every quest action, so the worker applies the
  committed changes in order, and a dead character cannot send one. Quest
  intents share the peer's interaction rate limit with the NPC actions, and an
  intent beyond it fails with `world_interaction_rate_limited`.
- A quest action that the worker's own catalog and quest log already refuse
  fails with GameService's code without a GameService call: a quest that the
  catalog does not hold with `quest_not_found`, an action on a quest that the
  log does not have active with `quest_not_active`, an objective of another
  quest or kind with `quest_objective_invalid`, and a reward that the quest
  does not offer with `quest_reward_choice_invalid`. When the action first read
  a stale quest log again and the read moved it on, the refusal still comes
  after a `QuestLogChanged` with `ReloadRequired`, because the client may show
  a change that committed behind lost answers, such as an abandon.
- After every committed action the worker applies the carry state and the
  quest log change, sends the item change and then a `QuestLogChanged` from
  the revision it held, and then the result. A rejection keeps the session
  unless its code ends it, as `simulation_session_invalid` and
  `simulation_worker_quest_catalog_mismatch` do, also when a read of a stale
  quest log answers with one.
- An answer that may have been lost after the commit, a timeout, an unreachable
  GameService, or a 5xx answer, and `item_transaction_timeout`, a transaction
  that concurrent database work rolled back, ask once more with the same
  operation id, which replays a committed result. When that answer is uncertain
  too, the worker reads the quest log again, and a `QuestLogChanged` with
  `ReloadRequired` tells the client to read its own when it changed. A log that
  cannot be read stays stale: the next quest action reads it first and fails
  with `quest_log_unavailable` while GameService cannot answer, so no change
  builds on a log that misses one.
- A Mob's death credits its kill to the players that
  [Mobs And Encounters](GAME_DESIGN.md#mobs-and-encounters) names. When a quest
  of the catalog asks for kills of the Mob, every credited player gets a kill
  credit with a random operation id, which takes its place in the player's lane
  at the kill, so an operation that the player sends after the kill, such as
  abandoning and accepting the quest again, never overtakes it. When its turn
  comes, the worker sends it as a `credit_quest_kill` if a current step holds an
  unfinished kill objective for the Mob by the quest log that every operation
  before it left, or if that log is stale, so a kill during an accept counts for
  the accepted quest; a kill that no quest asks for costs no GameService call.
  The changed quests reach the player as a `QuestLogChanged` without an
  operation id. An uncertain answer, or a transaction that concurrent database
  work rolled back, keeps the player's lane and asks again with the same
  operation id, after 0.5 s doubling up to 10 s, until the credit commits, like
  a death, or until the [retry horizon](#gameservice-outage) drops it; a
  rejection drops the credit with a warning, and a code that ends the
  session ends it. A leave or a disconnect waits for the pending credits, a
  player who started to leave before the kill gets none, and a stopping worker
  gives every credit that has not committed one more attempt while GameService
  answers.
- A Mob's death rolls its quest drops for the owner of each corpse it leaves,
  with the corpse's loot roll: once for every collect objective of a current
  step that names the Mob as a drop source, with the source's chance and
  quantity, by the quest log the worker keeps, or for every quest that the Mob
  drops for while that log is stale, up to 64 drops and only while the Mob's
  own loot leaves room in the corpse. A development bot rolls none. Before it
  lays out a live corpse, the worker asks the quest drop route through the
  owner's current session, which a reconnect keeps up to date, how much of them
  the owner still needs. The needed drops then follow the Mob's own loot and
  fill the room that it leaves in the corpse's 24 entries, so a drop that is
  not needed never takes the place of one that is, and a corpse whose only loot
  is quest drops that the owner no longer needs is not created. A timeout, an
  unreachable GameService, or a 5xx answer retries the corpse; an owner without
  a session on the worker, or any other answer, such as a session that ended,
  lays it out without the quest drops. A durable corpse sends them all with its
  creation, as [Item Transactions](#item-transactions) describes. Once the
  corpse exists, the worker sends its connected owner a `QuestDropInCorpse` for
  every quest drop the corpse holds. Looting a quest drop, alone or with Loot
  all, names its quest, so GameService grants it with the quest's lineage.
- A gathering yield names the node's actor definition as `resourceActorId`
  when a current step holds an unfinished gather objective for it, or the quest
  log is stale, and a retry after an uncertain answer sends the same request.
  The changed quests follow the yield's item change as a `QuestLogChanged`
  without an operation id. An answer that may have been lost after such a yield
  committed makes the log stale, and the worker reads it again; every committed
  yield also reads a stale log again, so a player who only gathers still learns
  the progress the lost answer held.

## Error Codes

Error codes are stable contracts for programs; messages are for people and may
change. GameService returns its codes in RFC Problem Details as the `code`
extension, SimulationWorker returns realtime codes in the `Error` of protocol
results and closures, and Unity adds `item_catalog_update_required`.

### HTTP Error Responses

- A malformed request body returns `400` with `invalid_request`, a transient
  PostgreSQL connection failure returns `503` with `database_unavailable`, and
  any other unhandled failure returns `500` with `internal_server_error`.
  Registration and login return `503` with `auth_busy` and a `Retry-After`
  when the [password hashing lane](#password-hashing-lane) is full; the caller
  sends the same request again after `Retry-After`. A
  body that does not read as the route's request type, such as a field of the
  wrong type, gets `invalid_request` only in Development; elsewhere the route's
  JSON binding answers the bare `400`, which the status code pages complete
  with `http_error`. A
  request that the client aborted is logged at debug level and answered with
  `499`, not reported as a server error.
- Item rejections map to statuses by class on the account, worker item, and
  corpse routes: `401` for an invalid simulation session and for a client's
  operation on a session whose account session ended,
  `account_session_replaced` or `account_session_ended`, `403` for missing
  authority or service access, such as `item_authority_required`,
  `wrong_simulation_worker`, `bank_access_required`, `recovery_access_required`,
  `vendor_access_required`, and `quest_npc_access_required`, `404` for a missing
  item, policy, Recovery delivery, corpse, or quest, `409` for stale state and
  races, such as
  `item_state_conflict`, `item_operation_conflict`, `item_transaction_timeout`,
  `item_slot_occupied`, `equipment_slot_occupied`, `item_quantity_changed`,
  `bag_state_changed`, `worker_runtime_changed`, `vendor_price_changed`,
  `inventory_full`, `quest_not_active`, `quest_already_active`,
  `quest_objective_complete`, `quest_item_not_needed`, the corpse lifecycle codes,
  and on account routes
  `item_offline_access_required`, and `422` for every other domain rejection.
  An incomplete request returns `400` with `item_operation_id_required` or
  `item_simulation_operation_invalid`.

### GameService Codes

| Area | Codes |
| --- | --- |
| Requests and platform | `invalid_request`, `internal_server_error`, `database_unavailable`, `rate_limit_exceeded`, `session_cache_invalidation_unavailable` |
| Accounts and sessions | `invalid_email`, `invalid_username`, `invalid_password`, `invalid_login`, `invalid_credentials`, `email_already_used`, `username_already_used`, `account_already_exists`, `account_not_found`, `invalid_session_token`, `account_session_replaced`, `auth_busy` |
| Characters | `invalid_character_name`, `character_name_taken`, `character_limit_reached`, `invalid_character_appearance`, `character_not_found` |
| Placement and join tickets | `invalid_character_id`, `shard_not_found`, `shard_unavailable`, `simulation_session_worker_unavailable`, `account_character_already_active`, `character_already_active`, `simulation_worker_item_catalog_mismatch`, `simulation_worker_quest_catalog_mismatch`, `invalid_ticket`, `invalid_simulation_target`, `simulation_session_changed`, `wrong_simulation_assignment` |
| Worker authentication and registration | `invalid_service_credentials`, `service_worker_mismatch`, `invalid_simulation_worker_identity`, `invalid_worker_runtime`, `invalid_worker_started_at`, `invalid_worker_host`, `invalid_worker_port`, `invalid_worker_capacity`, `invalid_worker_revision`, `invalid_protocol_version`, `simulation_topology_mismatch`, `simulation_world_not_found`, `simulation_assignment_target_not_found`, `simulation_worker_not_found`, `shard_assignment_conflict`, `shard_world_rebind_blocked`, `worker_runtime_changed` |
| Simulation sessions and looks | `invalid_simulation_session_id`, `invalid_simulation_session_token`, `invalid_simulation_session`, `invalid_simulation_session_heartbeat`, `invalid_simulation_session_request`, `invalid_character_state`, `invalid_character_checkpoint`, `invalid_appearance_change`, `development_look_disabled` |
| Content files | `content_not_found` |
| Item reads and requests | `character_inventory_not_found`, `character_bank_not_found`, `character_recovery_not_found`, `item_operation_id_required` |
| Quest reads | `character_quests_not_found` |
| Quest commands | `quest_not_found`, `quest_not_active`, `quest_already_active`, `quest_already_completed`, `quest_requirements_not_met`, `quest_level_too_low`, `quest_log_full`, `quest_npc_access_required`, `quest_objective_invalid`, `quest_objective_complete`, `quest_items_missing`, `quest_not_ready`, `quest_reward_choice_required`, `quest_reward_choice_invalid`, `quest_item_not_needed` |
| Item authority and session | `item_authority_required`, `item_offline_access_required`, `item_simulation_operation_invalid`, `wrong_simulation_worker`, `simulation_session_invalid`, `account_session_replaced`, `account_session_ended` |
| Item operations | `item_operation_id_invalid`, `item_operation_conflict`, `item_transaction_timeout`, `item_command_payload_too_large` |
| Item state | `item_not_found`, `item_not_owned`, `item_state_conflict`, `item_quantity_changed`, `bag_state_changed` |
| Item placement | `item_slot_occupied`, `item_slot_incompatible`, `equipment_slot_occupied`, `equipment_slot_incompatible`, `equipment_slot_blocked`, `equipment_level_requirement_not_met`, `bag_not_empty`, `carry_weight_limit_exceeded` |
| Stacks and ammunition | `item_stack_incompatible`, `item_stack_limit_exceeded`, `ammunition_not_equipped` |
| Policy, quest, and currency | `item_policy_restricted`, `item_policy_not_found`, `item_destroy_forbidden`, `quest_grant_invalid`, `currency_insufficient`, `currency_limit_exceeded` |
| Vendors and acquisition | `vendor_access_required`, `vendor_unavailable`, `vendor_offer_unavailable`, `vendor_price_changed`, `vendor_will_not_buy`, `inventory_full` |
| Gathering | `gathering_tool_required`, `gathering_yield_invalid` |
| Bank and Recovery | `bank_access_required`, `recovery_access_required`, `recovery_delivery_not_found`, `recovery_delivery_not_expired` |
| Death and corpses | `death_event_conflict`, `mob_corpse_invalid`, `mob_corpse_empty`, `mob_loot_grant_invalid`, `corpse_not_found`, `corpse_not_expired`, `corpse_state_changed`, `corpse_expired`, `corpse_invalidated`, `corpse_not_lootable`, `corpse_whole_items_only`, `item_already_looted` |
| Development tools | `development_simulation_bots_disabled`, `development_bot_loopback_required`, `invalid_development_bot_authority`, `invalid_development_bot_index`, `invalid_development_bot_instance_id`, `development_bot_already_active`, `development_bot_identity_changed`, `development_bot_placement_unavailable`, `development_bot_worker_capacity_reached`, `invalid_shard_id`, `invalid_join_ticket`, `invalid_stack_stress_fixture_authority`, `stack_stress_fixture_loopback_required`, `stack_stress_fixture_invalid`, `stack_stress_fixture_not_empty` |

### Realtime Codes

| Area | Codes |
| --- | --- |
| Packets and dispatch | `invalid_packet`, `udp_rate_limited`, `join_required`, `unexpected_message`, `internal_error`, and per family an `invalid_*_delivery` code for a message on the wrong channel or delivery and an `invalid_*` code for a malformed intent, such as `invalid_join_request`, `invalid_movement_input`, `invalid_item_operation`, and `invalid_world_interaction` |
| Join and session | `missing_join_ticket`, `join_timeout`, `join_failed`, `invalid_leave_request`, `worker_registry_lease_unavailable`, `session_missing`, `session_expired`, `session_revoked`, `session_reconnected`, `account_session_replaced`, `simulation_session_changed`, `wrong_simulation_assignment`, `character_already_active`, `carry_state_conflict`, `carry_state_missing`, `death_not_saved` when a death was given up at the retry horizon |
| Worker stop | In the `ServerDisconnect` that every connected peer gets when the worker stops: `server_shutdown` for a normal stop and a lost authority, and `game_service_outage` when the outage tolerance ran out |
| GameService calls | `auth_service_timeout`, `auth_service_unavailable`, `auth_service_rejected`, `auth_service_authentication_failed`, `invalid_auth_response`, and `game_service_unavailable` for a durable action that the worker refused because GameService does not answer |
| Movement | `movement_unavailable`, `unknown_movement_path` |
| Items | `item_operation_queue_full`, `item_operation_failed`, `item_operation_unavailable`, `item_state_diverged` |
| Corpses | `corpse_out_of_range`, `corpse_view_not_open`, `corpse_interaction_active`, `corpse_interaction_invalid`, `corpse_interaction_unavailable`, `corpse_operation_failed`, `live_mob_corpse_operation_unsupported` |
| Corpse view closures | `corpse_view_closed` when the viewer closed the view, `corpse_emptied` when the last item of a live Mob corpse was taken, `corpse_expired` at the deadline, `corpse_invalidated` when the corpse disappeared or its state can no longer be trusted, `corpse_not_found` when the worker or GameService no longer knows it, and `corpse_not_lootable` when the corpse belongs to the player who tagged its Mob; a view also closes with `combat_character_dead` when its viewer dies, and with the code of a rejection that ends it, such as `wrong_simulation_worker` |
| World interaction | `world_actor_not_found`, `world_actor_unavailable`, `world_interaction_invalid`, `world_interaction_active`, `world_interaction_out_of_range`, `world_interaction_line_of_sight_blocked`, `world_interaction_target_changed`, `world_interaction_capability_unavailable`, `world_interaction_capability_deferred`, `world_interaction_session_invalid`, `world_interaction_closed`, `world_interaction_failed`, `world_interaction_unavailable`, `world_interaction_queue_full`, `world_interaction_rate_limited`, `npc_lifecycle_action_invalid`, `npc_lifecycle_unavailable`, `quest_action_invalid` |
| Quests | `quests_unavailable`, `quest_intent_unavailable`, `invalid_quest_intent`, `quest_intent_queue_full`, `quest_intent_failed`, `quest_log_unavailable`, `simulation_worker_quest_catalog_mismatch`, `world_interaction_rate_limited` for a quest intent beyond the shared rate limit, `admin_tools_disabled` for a quest tool on a worker without the admin tools, and the GameService rejection of a quest action, such as `quest_not_active` or `development_tools_disabled`, which the worker also answers itself for what its catalog and quest log already refuse |
| World interaction notices | For a committed operation, in `WorldInteractionResult.NoticeCode`: `interaction_opened` with the capability's name as the argument `capability`, `gathering_started` with the same argument, `insurance_applied`, `insurance_removed`, `vendor_purchased`, `vendor_sold`, `quest_accepted`, `quest_abandoned`, `quest_items_regranted`, `quest_objective_completed`, `quest_items_delivered`, `quest_completed`, `quest_kill_counted` |
| Gathering | `resource_node_depleted`, `gathering_interrupted`, `gathering_action_invalid`, `gathering_unavailable`, `gathering_failed`, and the GameService rejection of a yield, such as `gathering_tool_required`, which carries the node's tool kind as the argument `toolKind` both when the worker refuses the start and when GameService refuses a yield |
| Combat | `combat_target_not_found`, `combat_target_not_attackable`, `combat_target_dead`, `combat_target_self`, `combat_player_versus_player_disabled`, `combat_character_dead`, `combat_intent_invalid`, `combat_intent_rate_limited`, `combat_unavailable` |
| Admin tools | `admin_tools_disabled`, `admin_character_dead`, `admin_teleport_outside_world`, `admin_teleport_no_ground`, `admin_teleport_superseded`, `admin_combat_unavailable`, `admin_unavailable`, `invalid_admin_intent` |
| Looks | `appearance_unavailable`, `appearance_change_in_flight`, `appearance_change_failed`, `invalid_appearance_intent` |
| Unity client | `item_catalog_update_required` |

Conditions that change every tick, such as being out of range or out of mana,
are attack statuses in `OwnerCombatState`, not error codes. How a code travels
on the wire is in [Realtime Protocol](REALTIME_PROTOCOL.md).

## HTTP Security And Error Handling

- Account routes use the `AccountSession` authentication handler and policy.
- Worker routes use the `SimulationWorker` handler and policy.
- Worker credentials use `X-Simulation-Worker-ID` and
  `X-Simulation-Worker-Secret`.
- Local shared-secret mode, which only the Development environment accepts,
  authenticates any valid worker identifier and leaves topology authorization to
  the registry. When a per-worker credential map is configured in
  `ServiceAuthentication:SimulationWorkers`, it becomes an exact allowlist and
  shared fallback is disabled, as
  [Hosts, Secrets, And Passwords Outside Development](#hosts-secrets-and-passwords-outside-development)
  describes.
- Secrets are compared in fixed time. Every worker secret must have at least 32
  characters, and startup rejects the `replace-with-` placeholders of
  `.env.example`.
- Central middleware converts unhandled failures into RFC Problem Details.
- Responses carry `X-Correlation-ID`, error responses too, as
  [Correlation Ids](MONITORING.md#correlation-ids) describes.
- Token-bearing responses use `Cache-Control: no-store` and `Pragma: no-cache`.
- Every response carries `X-Content-Type-Options: nosniff`; TLS, and with it
  HSTS, belongs to the proxy, as [Behind A Proxy](#behind-a-proxy) describes.
- No debug HTTP endpoint is registered in the current service surface.

### Redis Account-Session Acceleration

GameService uses Redis as a bounded cache-aside accelerator for successful
account-session validation. Token hashes, never bearer tokens, identify cache
entries below the `RedisAcceleration:KeyPrefix`. Every entry carries the end
that the session had when it was cached and expires no later than both
`SessionCacheTtlSeconds` and that end, so it never outlives the session's end,
and a later end that activity wrote shows at the next resolve that reaches
PostgreSQL. A session that is past its end but held by a live simulation
session is never cached. A cache miss, corrupt value, tombstone, or Redis read
failure falls back to PostgreSQL. `RedisAcceleration:SessionCacheEnabled=false` measures the
PostgreSQL validation path without disabling the distributed rate limit, and
`RedisAcceleration:Enabled=false` stops every Redis use except the readiness
check.

Logout, manual revocation, and login replacement write a session-id tombstone
before committing the PostgreSQL revocation, then delete the cached token key.
The tombstones of one revocation go to Redis together, in one round trip, and
the token keys after them. A write that fails for a transient reason, a
timeout or a lost connection, is sent once more, and both attempts together
keep to `RedisAcceleration:SessionRevocationDeadlineMilliseconds`; a tombstone
write is idempotent, so the second attempt is safe. The tombstone lives
`SessionRevocationTombstoneSeconds`, at least as long as any token cache
entry. If GameService cannot store the required tombstone, it returns
`503 session_cache_invalidation_unavailable` and does not commit the durable
revocation. This prevents a successfully revoked session from remaining usable
through stale cache state.

Login and registration also use a Redis fixed-window rate limit keyed by a hash
of the [client address](#the-clients-address) and policy name. The process-local ASP.NET rate limit
remains active as a fallback if Redis is unavailable. Disconnected Redis
commands fail fast instead of accumulating in a client backlog. The Redis client
waits `Redis:CommandTimeoutMilliseconds` for a connection or an answer, its own
timeout apart from the readiness probe's `HealthChecks:TimeoutMilliseconds`.
The Redis counters and logs are in [Monitoring](MONITORING.md#gameservice).

## Configuration Defaults

Checked-in non-secret defaults live in each service's `Config` folder, and the
ignored root `.env` holds local credentials and connection strings, not worker
topology or World selection. Every setting fails fast at startup; the
precedence and reload rules are in
[Configuration Ownership](PROJECT_ARCHITECTURE.md#configuration-ownership). The
telemetry settings are in [Monitoring](MONITORING.md#configuration).

### GameService

| Setting | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings:Postgres`, `ConnectionStrings:Redis` | from `.env` | Required. The PostgreSQL connection must name host, database, username, and password; GameService connects with the application name `Mmorpg.GameService`. Outside Development neither password may be empty or a placeholder, as [Hosts, Secrets, And Passwords Outside Development](#hosts-secrets-and-passwords-outside-development) describes |
| `AllowedHosts` | `*` in `appsettings.Development.json`, none otherwise | The host names that requests may name, separated by `;`; outside Development it is required and must not contain `*`, `0.0.0.0`, or `[::]`, which allow any host |
| `Http:TrustedProxies` | empty | The proxies whose `X-Forwarded-For` and `X-Forwarded-Proto` count, IP addresses and networks such as `10.0.0.0/8` separated by `;` or `,`; empty ignores both headers, as [The Client's Address](#the-clients-address) describes |
| `Http:ForwardLimit` | `1` | How many `X-Forwarded-For` entries from the right count, one per trusted proxy in the chain, 1 to 10 |
| `Http:MaxRequestHeadersTotalSizeBytes`, `MaxRequestHeaderCount`, `MaxRequestLineSizeBytes` | `16384`, `64`, `4096` | Kestrel's limits of the request headers together, their number, and the request line; 4,096 to 1,048,576, 16 to 1,000, and 1,024 to 65,536 |
| `Http:RequestHeadersTimeoutSeconds`, `KeepAliveTimeoutSeconds` | `15`, `130` | How long Kestrel waits for the request headers, 1 to 300, and keeps an idle connection, 1 to 3,600 |
| `Http:ShutdownDrainSeconds` | `10`, and `0` in `appsettings.Development.json` | How long GameService still serves after its readiness turned to `stopping`, 0 to 300, as [Orderly Shutdown](#orderly-shutdown) describes |
| `Http:ShutdownTimeoutSeconds` | `30` | The host's shutdown timeout, which the drain, the requests in flight, and the background services share; longer than the drain and at most 3,600 |
| `Database:RunMigrationsOnStartup` | `true` in the Development environment, `false` otherwise | Apply the migrations, the seeds, and the topology at startup. When false, the `migrate` command does it, and a start refuses a database with a pending migration or seed change; a database that a newer release migrated, with nothing else pending, only logs a warning, as [Running Migrations](GAME_SERVICE_MODULES.md#running-migrations) describes |
| `Database:MigrationLockTimeoutMilliseconds` | `5000` | How long a statement of a migration in a transaction waits for a table lock that other sessions hold before the migration fails and applies nothing, 100 to 600,000. It does not apply to the wait for the migration lock, which never times out, nor to a statement that has its lock |
| `Database:MigrationCommandTimeoutSeconds` | `300` | How long the SQL of a migration in a transaction may run before it is cancelled and the migration applies nothing, 1 to 86,400. The connection's own command timeout, 30 seconds by default, does not apply to a migration |
| `Database:MaximumPoolSize` | `64` | Npgsql pool size of one process, at most 1,000 |
| `HealthChecks:TimeoutMilliseconds` | `1000` | Timeout of each readiness probe |
| `Redis:CommandTimeoutMilliseconds` | `2000` | How long the Redis client waits for a connection or a command's answer, 100 to 60,000 |
| `Retention:IntervalSeconds` | `60` | How often the retention host runs a pass, 1 to 3,600 seconds |
| `Retention:BatchSize` | `1000` | Rows one retention batch deletes at most, 1 to 10,000 |
| `Retention:PassBudgetPercent` | `80` | The share of the interval that one pass may use, 1 to 100 |
| `Retention:ItemHistory` | `14.00:00:00` | How long item operations with their journal, item destructions, claimed Recovery deliveries, closed corpses with their death events, and live Mob loot grants stay after they ended, a time span from one day to 3,650 days, and from three minutes in the Development environment, for a soak or a test |
| `Retention:SessionHistory` | `14.00:00:00` | How long account sessions, simulation sessions, join tickets, and released SimulationAssignments stay after they ended, a time span from one day to 3,650 days, and from three minutes in the Development environment |
| `Audit:Enabled` | `false` in the Development environment, `true` otherwise | Run the [economy audit](GAME_SERVICE_MODULES.md#economy-audit) at start and then on a schedule. The `audit` command runs it whatever this says |
| `Audit:IntervalSeconds` | `3600` | How often the scheduled audit runs, 60 to 86,400 seconds |
| `Audit:StatementTimeoutSeconds` | `60` | The statement timeout of every check; a check that hits it is reported as not run, and the next one still runs, 1 to 3,600 seconds |
| `Audit:ExampleLimit` | `5` | How many violating rows a check reports as examples with their ids, 0 to 100 |
| `Audit:PendingOperationSeconds` | `300` | How long an item operation may stay pending before `item_operation_pending_too_long` reports it, 0 to 86,400 seconds |
| `Auth:SessionIdleLifetimeDays` | `14` | How long an account session lasts without activity, 1 to 365 days, as [Account Session Lifetime](#account-session-lifetime) describes |
| `Auth:SessionMaximumLifetimeDays` | `90` | How long an account session lasts after its login at most, outside the World, from the idle lifetime to 3,650 days. A start that still sets the removed `Auth:SessionLifetimeHours` fails and names these two settings |
| `Auth:PasswordHashing:MaxConcurrency` | half of the logical processors, at least `1` | Password hashes that run at once, 1 to 256, as [Password Hashing Lane](#password-hashing-lane) describes |
| `Auth:PasswordHashing:MaxWaitSeconds` | `10` | The longest a registration or login waits for its hash before it is refused with `auth_busy`, 1 to 10 seconds: a client allows a queued login 10 seconds on top of its request timeout, so the server may not queue longer |
| `RateLimiting:Authentication:PermitLimit`, `WindowSeconds` | `5`, `60` | Login and registration requests per client address and window, each limited separately |
| `RateLimiting:Public:PermitLimit`, `WindowSeconds` | `300`, `60` | Requests per client address and window to the Shard list, the health routes, and `/` together; 1 to 100,000 requests and 1 to 3,600 seconds |
| `RateLimiting:AccountActions:PermitLimit`, `WindowSeconds` | `20`, `60` | Requests per account and window to each route that creates work or state, as [Rate Limits](#rate-limits) lists them; 1 to 100,000 and 1 to 3,600 |
| `RateLimiting:AccountReads:PermitLimit`, `WindowSeconds` | `300`, `60` | Requests per account and window to the account's read routes together; 1 to 100,000 and 1 to 3,600 |
| `RedisAcceleration:Enabled`, `SessionCacheEnabled`, `AuthenticationRateLimitingEnabled` | `true` | Redis session cache and distributed rate limit |
| `RedisAcceleration:KeyPrefix` | `shooter-mmo` | Prefix of every Redis key, 1 to 64 letters, digits, `-`, `_`, or `.` |
| `RedisAcceleration:SessionCacheTtlSeconds` | `60` | Lifetime of a cached session, 1 to 300 seconds |
| `RedisAcceleration:SessionRevocationTombstoneSeconds` | `120` | Lifetime of a revocation tombstone, at least the cache lifetime and at most 3,600 seconds |
| `RedisAcceleration:SessionRevocationDeadlineMilliseconds` | `5000` | How long storing the tombstones of one revocation may take, a retry of a transient failure included, 100 to 30,000 |
| `RedisAcceleration:MetricsIntervalSeconds` | `30` | Interval of the Redis counter log |
| `SIMULATION_WORKER_SERVICE_SECRET` or `ServiceAuthentication:SimulationWorkers:<workerId>` | from `.env` | The shared worker secret, which only the Development environment accepts, or one secret per worker id |
| `Game:MaxCharactersPerAccount` | `5` | Characters an account may own |
| `Characters:AppearancePath` | `WorldData/Characters/appearance.json` in the output | The character appearance content |
| `Simulation:JoinTicketLifetimeSeconds` | `30` | Join ticket lifetime |
| `Simulation:SessionLeaseLifetimeSeconds` | `150` | Simulation session lease that each heartbeat renews. It must exceed every worker's `GameServiceOutageToleranceSeconds` plus one session heartbeat interval plus one retry, so a worker that comes back within its tolerance still holds its sessions; a worker refuses a join whose lease is shorter, as [GameService Outage](#gameservice-outage) describes |
| `Simulation:WorkerHeartbeatTimeoutSeconds` | `30` | Time after the last heartbeat until a worker counts as gone and another runtime may take its worker id over |
| `Simulation:MetricsIntervalSeconds` | `15` | Refresh interval of the Shard gauges, 1 to 3,600 seconds |
| `Simulation:Topology` | `local-fleet`, `local-node-1`, `local-shard-1`, `local-shard-2` | Fleets, Nodes, and Shards, as [Topology Bootstrap](#topology-bootstrap) describes |
| `WorldData:WorldsPath` | `WorldData/Worlds` | The World manifests that seed the World definitions |
| `Items:CatalogPath` | `WorldData/Items/core.item-catalog.json` | The compiled item catalog that GameService mirrors |
| `Quests:CatalogPath` | `WorldData/Quests/core.quest-catalog.json` | The compiled quest catalog that GameService runs and mirrors |
| `Content:TextTablesPath` | `WorldData/Text/en` | The folder of the text tables that GameService serves, one locale |
| `Content:BaseUrl` | empty | When set, an absolute `http` or `https` URL without query or fragment that the content manifest puts before each file's relative path instead of `/api/content` |
| `RateLimiting:Content:PermitLimit`, `WindowSeconds` | `120`, `60` | Content requests per client address and window, shared by the manifest and file routes; 1 to 10,000 requests and 1 to 3,600 seconds |
| `Items:Operations:TransactionTimeoutMilliseconds` | `10000` | Statement timeout of an item transaction, 100 to 120,000 |
| `Items:Operations:LockTimeoutMilliseconds` | `2000` | Lock timeout of an item transaction, 10 to 120,000 and at most the transaction timeout |
| `Items:Operations:MaximumCommandPayloadBytes` | `65536` | Largest canonical command, 1,024 to 1,048,576 |
| `Items:Operations:MaximumHttpRequestBodyBytes` | `262144` | Kestrel's request body limit, 4,096 to 4,194,304 |
| `Items:Operations:MetricsIntervalSeconds`, `MaintenanceIntervalSeconds` | `30`, `60` | Item summary log and Recovery expiry intervals, 1 to 3,600 seconds |
| `Items:Operations:CleanupBatchSize` | `64` | Recovery deliveries that one Recovery expiry run expires at most, 1 to 1,000 |
| `Items:NpcLifecycle:InsurancePrice` | `100` | Server-owned insurance price |
| `Items:Development:ItemPackagesPath` | `Content/Development/item-packages.json` in the output | The Development item packages |
| `DevelopmentSimulationBots:*` and `ACTIVE_SIMULATION_BOTS_SECRET` | off | Development only: `Enabled`, a ticket lifetime of `30` seconds and a session lease of `150` seconds, which must outlast the worker's outage tolerance as `Simulation:SessionLeaseLifetimeSeconds` does, the `CharacterNamePrefix` `Active Bot`, and `ReservedPlayerConnections` `8` |
| `StackStressFixtures:*` and `STACK_STRESS_FIXTURE_SECRET` | off | Development only: `Enabled`, the inventory and loot definitions that the fixtures grant, and the `Kits` of the playing load bots |

### SimulationWorker

The worker settings live in the `SimulationWorker` section. Environment
variables such as `SIMULATION_WORKER_ID`, `SIMULATION_SHARD_ID`,
`SIMULATION_WORLD_ID`, and `SIMULATION_WORKER_UDP_PORT` override the identity
and endpoint settings, as the `StressArena` launch profile does.

| Setting | Default | Meaning |
| --- | --- | --- |
| `SimulationWorkerId`, `FleetId`, `NodeId`, `ShardId`, `WorldId` | `local-simulation-worker-1`, `local-fleet`, `local-node-1`, `local-shard-1`, `development-world-2` | Worker identity and desired World |
| `WorldDataPath` | `WorldData/Worlds` | The World directories in the output |
| `UdpPort`, `AdvertisedHost`, `AdvertisedUdpPort` | `27015`, `127.0.0.1`, `27015` | UDP endpoint and the endpoint that placement hands out |
| `MaxConnections` | `1000` | Connection slots, at most 10,000 |
| `JoinHandshakeTimeoutSeconds` | `10` | Time for a new peer to send its join request; also LiteNetLib's disconnect timeout |
| `NetworkPollIntervalMilliseconds` | `15` | Longest wait between transport polls, at most 1,000 |
| `GracefulDisconnectMaximumMilliseconds` | `2000` | Longest wait for the client to acknowledge the message that says why the worker ends its connection, before the disconnect, from 1 to the `JoinHandshakeTimeoutSeconds` that is also LiteNetLib's disconnect timeout, as [Realtime UDP Transport](#realtime-udp-transport) describes |
| `GameServiceBaseUrl`, `GameServiceTimeoutSeconds` | `http://localhost:5000`, `5` | GameService address and HTTP timeout |
| `GameServiceSecret` | from `SIMULATION_WORKER_SERVICE_SECRET` | Worker service secret, at least 32 characters; outside Development the secret that GameService holds for this worker id in `ServiceAuthentication:SimulationWorkers:<workerId>`, never a placeholder or a published one |
| `SimulationSessionHeartbeatSeconds`, `SimulationSessionHeartbeatMaxConcurrency` | `10`, `8` | Session heartbeat interval, and the session heartbeat requests, and the releases at shutdown, that run at once, at most 128 |
| `SimulationSessionHeartbeatBatchSize` | `250` | Sessions that one session heartbeat request names at most, 1 to 500 |
| `SimulationSessionHeartbeatRetryMilliseconds` | `2000` | How long a session heartbeat request that timed out, could not reach GameService, or got a server error waits before it goes again, shorter than the heartbeat interval, as [Session Heartbeat](#session-heartbeat) describes |
| `RegistryHeartbeatSeconds` | `10` | Worker heartbeat interval |
| `RegistryHeartbeatRetryMilliseconds` | `2000` | How soon a worker heartbeat that timed out, could not reach GameService, or got a server error goes again, until one succeeds; shorter than the heartbeat interval |
| `RegistryAcquireTimeoutSeconds` | `120` | How long a new runtime waits for a previous runtime's lease, longer than the heartbeat interval and at most 3,600 seconds |
| `GameServiceOutageToleranceSeconds` | `120` | How long the worker plays on after its last successful worker heartbeat before it stops in order, 10 to 600 seconds. GameService's `Simulation:SessionLeaseLifetimeSeconds` must exceed it plus one session heartbeat interval plus one retry, as [GameService Outage](#gameservice-outage) describes |
| `UncertainOperationRetryHorizonSeconds` | `600` | How long a death, a quest kill credit, or a Mob corpse whose outcome stays uncertain is retried, counted only while GameService answers, at least `GameServiceOutageToleranceSeconds` plus 60 and at most 3,600 seconds. GameService's `Retention:ItemHistory` must stay far above it |
| `NetworkMetricsLogSeconds` | `30` | Interval of the network, status, and performance logs |
| `PerformanceReportPath` | none | File that the worker appends every metrics interval to as one JSON line, for the load tooling, as [Monitoring](MONITORING.md#periodic-and-routine-logs) describes; a relative path starts at the working directory |
| `HealthFilePath` | none | File that the worker writes every 5 seconds while it is ready, with its port bound, a valid lease, and a loop that ticked in the last 5 seconds, and deletes when it is not, for the container health check, as [Container Health Checks](#container-health-checks) describes; a relative path starts at the working directory |
| `CharacterCheckpointSeconds` | `30` | Interval of the checkpoint that saves the changed place and vitals of the players, 1 to 3,600 seconds |
| `LingeringBodySeconds` | `20` | How long a character's body stays in the world after its connection ended and after every hit it takes, 1 to 3,600 seconds, as [Lingering Bodies](#lingering-bodies) describes |
| `TickPhaseFailureLimit` | `30` | How many runs in a row one tick phase or loop step may fail before the worker stops itself, at least 1, as [Realtime UDP Transport](#realtime-udp-transport) describes |
| `Realtime:LoopThreadPriority` | `Normal` | The scheduling priority of the realtime loop thread, `Normal`, `AboveNormal`, or `Highest`; any other value stops the start. A higher one lets other work on a busy machine preempt the ticks less often; the performance file shows per tick how long the thread did not run, as [Monitoring](MONITORING.md#periodic-and-routine-logs) describes |
| `UdpQuotas:InboundPacketsPerSecond`, `InboundPacketBurst` | `120`, `240` | Inbound packets per peer |
| `UdpQuotas:InboundBytesPerSecond`, `InboundByteBurst` | 128 KiB, 256 KiB | Inbound bytes per peer |
| `UdpQuotas:SnapshotBytesPerSecond`, `SnapshotByteBurst` | 256 KiB, 512 KiB | Unreliable snapshot bytes per peer |
| `UdpQuotas:AggregateSnapshotBytesPerSecond`, `AggregateSnapshotByteBurst` | 38 MiB, 4 MiB | Unreliable snapshot bytes of the whole worker, with fair recipient rotation |
| `Admission:MaxUnauthenticatedPeers`, `MaxUnauthenticatedPeersPerAddress` | `256`, `8` | Remote peers that have not joined yet |
| `Admission:ConnectionAttemptsPerAddressPerSecond`, `ConnectionAttemptBurstPerAddress` | `5`, `20` | New connections per remote address |
| `Admission:MaxReliableQueuedPackets` | `1024` | Reliable packets that wait behind the 64 unacknowledged ones LiteNetLib sends per channel, of every reliable channel together, before a disconnect; at least 512, since entities enter a view only while the queue holds less than half of it, as [Interest Management](#interest-management) describes |
| `InterestManagement:CellSize`, `EnterRadius`, `ExitRadius` | `64`, `128`, `144` m | Spatial hash and visibility hysteresis; a positive cell size of at most 4,096 m, a positive enter radius, an exit radius from the enter radius to 10,000 m, and at most 64 cells per axis direction |
| `InterestManagement:RefreshIntervalSnapshots` | `3` | Snapshots between two refreshes of one connection's visibility, at most one second of snapshots |
| `InterestManagement:NearRadius`, `NearExitRadius` | `50`, `58` m | The near tier of a viewer's visible entities and its hysteresis; the exit radius at least the near radius and at most the enter radius |
| `InterestManagement:DetailRadius`, `DetailExitRadius` | `70`, `78` m | The detail of a viewer's visible entities and its hysteresis, as [Detail Radius](#detail-radius) describes; the detail radius at least the near radius, and the exit radius at least the detail radius and at most the interest exit radius |
| `SnapshotReplication:OverloadTargetUtilizationBasisPoints` | `9500` | Share in basis points, 1 to 10,000, of a player's and of the aggregate snapshot budget that snapshots may use before remote records are deferred |
| `SnapshotReplication:PlayerBytesPerSecond` | 32 KiB | Snapshot bytes per player and second, owner snapshot included, at least two seconds of owner snapshots and at most `UdpQuotas:SnapshotBytesPerSecond` |
| `SnapshotReplication:FarIntervalSnapshots`, `StopRepeats`, `RefreshIntervalSnapshots` | `3`, `3`, `15` | Snapshots between two changes of a far entity, repeats of a record after its last change, and snapshots between two refreshes of an unchanged entity; the first two at most one second of snapshots, the refresh between the far interval and ten seconds of snapshots |
| `CollisionStreaming:LoadRadiusChunks`, `UnloadRadiusChunks` | `2`, `3` | Collision streaming radii around each anchor, the unload radius at most 16 |
| `ItemInteraction:CorpseInteractionRadius` | `3` m | Range for opening and using a corpse, at most 20 m |
| `ItemInteraction:CorpseDiscoveryRadius` | `32` m | Range of corpse presence, at least the interaction radius and at most 1,000 m |
| `ItemInteraction:DurableCorpseRefreshSeconds` | `30` | Interval of durable corpse reconciliation, at most 300 seconds |
| `Movement:TickRateHz`, `SnapshotRateHz` | `30`, `15` | Fixed tick and snapshot rates; the snapshot rate must divide the tick rate |
| `Movement:WalkSpeed`, `RotationSpeedDegrees` | `5` m/s, `720` | Walk speed and turn rate |
| `Movement:Gravity`, `MaximumFallSpeed`, `GroundedVerticalVelocity` | `-24`, `55`, `-2` | Falling |
| `Movement:CharacterRadius`, `CharacterHeight`, `StepHeight`, `MaximumSlopeDegrees`, `GroundSnapDistance` | `0.35`, `2`, `0.35`, `45`, `0.4` | Character capsule and ground handling |
| `Movement:MaximumSubstepDistance`, `MaximumPenetrationIterations` | `0.1`, `6` | Motor substep length and collision resolution |
| `Movement:PathContinuationSeconds` | `3` | How long in all a player whose inputs stop arriving keeps walking its path, a whole number from 0 to 5 seconds, well inside the 256 inputs the client keeps for replay; 0 pauses it after three steps, as [Server-Authoritative Movement](#server-authoritative-movement) describes |
| `ActorCatalogPath`, `ItemCatalogPath`, `QuestCatalogPath`, `CombatRulesPath`, `CharacterAppearancePath` | `WorldData/Actors/core.actor-catalog.json`, `WorldData/Items/core.item-catalog.json`, `WorldData/Quests/core.quest-catalog.json`, `WorldData/Combat/combat-rules.json`, `WorldData/Characters/appearance.json` | Content files in the output; the World's actor spawns and collision come from the World directory |
| `ConnectionStrings:Redis`, `HealthChecks:TimeoutMilliseconds` | `127.0.0.1:6379`, `1000` | Redis for the health check and its timeout |
| `DevelopmentItemInteractions:GlobalBankAndRecoveryAccess` | `true` in `appsettings.Development.json` | Development only: Bank and Recovery access from anywhere |
| `DevelopmentAdmin:Enabled` | `true` in `appsettings.Development.json` | Development only: the admin tools |

SimulationWorker requires one valid `WorldData/Worlds/<WorldId>/world.json` in
its output folder for its configured `WorldId`, built from
`content/worlds/<WorldId>/world.json`. The manifest supplies the client scene,
movement bounds, ground height, and authoritative spawn, and the actor and
collision data resolve below the same World directory. This prevents a shard
restart on another World from silently retaining coordinates from the previous
map. Missing manifests, invalid bounds, out-of-bounds spawns, or invalid runtime
content fail before UDP admission.

## Persistence And Maintenance

- Character creation calls the PostgreSQL bootstrap function before it commits,
  so a character cannot commit without its item state: the catalog's character
  settings, base carry capacity, permanent inventory and bank slot counts, plus
  one unbounded Recovery Storage identity.
- When the item catalog revision differs from the one the last complete carry
  recompute ran for, the seed recomputes the carry state of every character in
  batches of 500 rows that it locks first, so a concurrent item transaction is
  never overwritten with a stale weight, advances a character's item-state
  revision only when the weight or capacity changed, and then records the
  revision. A start with an unchanged catalog does no recompute, so its work
  does not grow with the number of characters, as
  [Data Steps](GAME_SERVICE_MODULES.md#data-steps) describes.
- A bounded hosted maintenance service runs every
  `Items:Operations:MaintenanceIntervalSeconds` and uses deterministic
  system-authority operations to destroy expired Recovery items with durable
  audit.
- The retention host deletes history once it is older than its window:
  `Retention:ItemHistory` for item operations with their journal, item
  destructions, claimed Recovery deliveries, empty closed corpses with their
  death events, and live Mob loot grants, and `Retention:SessionHistory` for
  account sessions, simulation sessions, join tickets, and released
  SimulationAssignments, each counted from the moment it ended. An item
  operation goes only after the death event that names it left with its
  corpse; a destruction no longer holds its operation. After its row went, a
  retried operation id runs again, which every deterministic system operation
  id survives, as [Retention](GAME_SERVICE_MODULES.md#retention) describes with
  every rule of the tasks. The gold ledger is never trimmed. A task that fails
  or falls behind shows in [Monitoring](MONITORING.md#gameservice): its failed
  batches are counted by task and reason, its lag series is dropped while its
  last pass failed, and the alerts `RetentionTaskFailing` and
  `RetentionLagHigh` fire for a task that keeps failing and a task whose
  oldest due row waited more than 15 minutes.
- The economy audit checks item custody, slots, stacks, definitions, the gold
  ledger, and the retained history in one read-only snapshot: at start and
  every `Audit:IntervalSeconds` when `Audit:Enabled` is on, which is the default
  outside the Development environment, and once with the `audit` command. It
  never writes. The command prints a readable report, or JSON with `--json`,
  and exits with 0 when the economy is consistent, 1 when a check found a
  violation, and 2 when the audit could not run, also when `migrate` would
  still change the database or a newer release migrated it. The checks and what
  they cannot answer are in
  [Economy Audit](GAME_SERVICE_MODULES.md#economy-audit), how to run the
  command in [Audit The Economy](LOCAL_DEVELOPMENT.md#audit-the-economy), and
  its logs and metrics in [Monitoring](MONITORING.md#gameservice).
- Scheduled work runs in every GameService process, with no leader, so several
  instances do the same work at once, and each kind stays safe in its own way.
  Every retention batch has a statement timeout, a lock timeout, and
  `skip locked` row selection, so two instances never delete the same row or
  wait for each other. Recovery expiry and durable corpse expiry select their due rows
  without locks, because each expiry runs as an item transaction under a
  deterministic operation id: two instances that pick the same row run the same
  id, and the item transaction's locks and its stored operation let one of them
  change the row and the other find it done. The audit only reads, in one
  snapshot.
- The item metrics and logs are in [Monitoring](MONITORING.md#logs).

## Not Yet Implemented

- HTTP item grant routes. Development grants use the guarded process-local
  commands, and gameplay grants come only from NPC quest, Mob loot, vendor
  purchase, and gathering paths.
- Consume and use intents for potions, food, and other consumables.
- Profession experience and levels, gathering success chances, other
  professions, durable resource node state, limited vendor stock, dynamic
  prices, buyback, and player trading.
- The dialogue, crafting, and trainer capability business operations.
- A threat table, Mob abilities beyond one attack, Mob groups that assist each
  other, armor, defense, resistances, and the combat triangle.
- Durable unique actor state.
- A production scheduler or fleet autoscaler.
- Alert notifications to a chat or paging service, and a shared or staging
  deployment of the monitoring stack.
- Rigid-body collision and replicated dynamic collision transforms.
- Body times per gameplay rule area and an instant logout in a safe city; a
  body stays `LingeringBodySeconds` everywhere.

The planned and open item rules are in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md#planned),
the deferred actor work in
[Deferred Work](NPC_AND_MOB_SYSTEM_DESIGN.md#deferred-work),
and the deferred game features in
[Deferred Features](GAME_DESIGN.md#deferred-features).

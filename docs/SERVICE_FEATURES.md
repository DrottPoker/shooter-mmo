# Service Features

Last updated: 2026-07-15

## Purpose

This document records implemented service, networking, persistence, and
operational behavior. Product intent that is not implemented belongs in the MVP
specification. Player-facing behavior belongs in [Game Features](GAME_FEATURES.md).

## Service Topology

The implemented topology is:

```text
Global AuthService and PostgreSQL
  Fleet
    Node
      SimulationWorker logical id
        Worker runtime generation
        SimulationAssignment -> Shard
          Shard -> World content definition
```

There are no realms. Account and character records are global. Fleets identify
compute placement and region, while shards are the player-selectable simulation
copies. World identifies shared content.

The current runtime permits one active worker per shard and one active shard per
worker. Zone and layer partitioning are not implemented.

## AuthService HTTP Surface

| Route | Authority | Behavior |
| --- | --- | --- |
| `POST /api/accounts/register` | Public, rate limited | Create account and login session |
| `POST /api/accounts/login` | Public, rate limited | Replace the active login session |
| `GET /api/accounts/me` | Account session | Return account profile |
| `GET /api/accounts/session` | Account session | Validate the current session |
| `POST /api/accounts/logout` | Account session | Revoke current session and dependent access |
| `DELETE /api/accounts/sessions/{sessionId}` | Account session | Revoke an owned session |
| `GET /api/characters` | Account session | List owned characters |
| `POST /api/characters` | Account session | Create a character |
| `GET /api/shards` | Public | List logical shards, status, players, and capacity |
| `POST /api/shards/{shardId}/join` | Account session | Place an owned character and issue a ticket |
| `POST /api/simulation-workers/{workerId}/heartbeat` | Worker service policy | Register or renew exact worker runtime |
| `POST /api/simulation-workers/{workerId}/offline` | Worker service policy | Release exact worker runtime authority |
| `POST /api/simulation-join-tickets/consume` | Worker service policy | Consume an exact-runtime join ticket |
| `POST /api/simulation-sessions/{id}/heartbeat` | Worker service policy | Renew an exact simulation lease |
| `POST /api/simulation-sessions/{id}/release` | Worker service policy | Release an exact simulation lease |
| `POST /api/development/simulation-bots/join-tickets` | Development loopback secret | Issue an in-memory exact-runtime bot ticket when explicitly enabled |
| `GET /health/live` | Public | Report process liveness |
| `GET /health/ready` | Public | Verify obligatory dependencies |

The public shard list never exposes a SimulationWorker host, port, or runtime id.
That endpoint appears only in a successful short-lived placement response.

The development bot route is absent unless AuthService runs in `Development`
with `DevelopmentSimulationBots:Enabled=true`. It also requires loopback and a
separate secret. Its identities, tickets, and sessions remain in memory and are
never durable account or character data. SimulationWorker uses the same worker
service routes for real and bot credentials; AuthService routes recognized bot
credentials to the in-memory authority and every other request to the existing
PostgreSQL services.

## Accounts And Authentication

- Email and username are normalized and unique.
- Passwords use ASP.NET Core `PasswordHasher`.
- Account tokens are opaque and only their SHA-256 hashes are stored.
- PostgreSQL enforces one unrevoked account session per account.
- A successful login locks the account, revokes the older session with
  `account_session_replaced`, consumes its pending tickets, releases its active
  simulation sessions, and creates the replacement session transactionally.
- Logout and manual revoke use explicit revocation reasons and dependent cleanup.
- The Unity client periodically validates its account token and handles
  replacement as a controlled disconnect.

Login and registration use independent fixed-window limits keyed by remote IP.
Rejected requests return HTTP 429 with structured Problem Details and
`Retry-After`.

## Character Access

- Character names are validated and normalized.
- Character ownership is checked on every protected operation.
- The configured maximum character count is enforced transactionally.
- Soft-deleted characters are excluded from active queries.
- Simulation placement locks both account and character rows.

PostgreSQL permits only one active simulation session per character and one per
account. A copied account token therefore cannot run two different characters
at the same time.

## Topology Bootstrap

`AuthService/Config/appsettings.json` owns the checked-in topology bootstrap:

- World definitions.
- Fleets with display names and region codes.
- Nodes assigned to fleets.
- Shards assigned to a World and Fleet with a rule set.

Startup validation rejects empty collections, invalid identifiers, duplicates,
unknown references, invalid display names, and invalid region codes. After
migrations, `SimulationTopologySeeder` idempotently upserts configured records.
It does not manufacture online workers. A seeded shard remains offline until a
valid heartbeat and active assignment exist.

## Worker Registration And Assignment

Each SimulationWorker has:

- A stable `SimulationWorkerId`.
- A generated `RuntimeId` for the current process generation.
- Fleet, Node, Shard, and World configuration.
- An advertised UDP endpoint.
- Maximum and active connection counts.
- Protocol, simulation, and collision revisions.
- A process start timestamp.

AuthService validates identifiers, topology consistency, endpoint metadata,
capacity, revisions, and service identity. The heartbeat transaction locks the
target shard and applies these rules:

- A different healthy assigned worker causes `shard_assignment_conflict`.
- A timed-out or explicitly offline owner can be replaced.
- Replacement invalidates old-runtime tickets and simulation sessions even when
  the logical worker moves to another shard.
- A newer runtime can replace an older runtime for the same worker id.
- The older runtime is fenced by `worker_runtime_changed`.
- A stale process cannot mark a newer runtime offline.
- Active assignment indexes prevent two worker or shard owners.

SimulationWorker stores the database-issued lease duration locally. It rejects
new UDP joins without a valid registration lease. A one-second monitor stops the
process after lease expiry. Definitive topology, assignment, runtime, or service
credential rejection also stops the process.

## Shard Discovery And Placement

`GET /api/shards` derives status from fresh, online, actively assigned workers.
It aggregates active players and maximum capacity. A shard is online only when
at least one assigned worker is fresh and has free capacity. The current unique
assignment constraint means that aggregate contains at most one worker.

Join placement:

1. Validates account session and character ownership.
2. Locks the account and character.
3. Releases expired account simulation sessions.
4. Rejects another active character or cross-shard active session.
5. Selects the assigned worker only when its heartbeat is fresh and it has room.
   Capacity uses the greater of worker-reported connections and authoritative
   unexpired simulation sessions, then adds pending tickets in PostgreSQL.
6. Invalidates older pending tickets for the account.
7. Stores a hash of a new ticket bound to shard, worker, runtime, character, and
   account session.
8. Computes expiry after placement locks are acquired.
9. Returns the shard and exact endpoint metadata.

Selection serializes reservations on the target shard, then locks the selected
worker. A waiting request recalculates session and ticket usage from a fresh
database snapshot before reserving capacity. Different shards remain parallel.

## Join Tickets And Simulation Sessions

Join tickets are short lived, one use, and stored only as hashes. A worker must
present the ticket together with its own worker id, runtime id, and shard id.

Consumption locks the account and character, validates account session status,
checks exact placement binding, and creates or rotates the simulation-session
token in one transaction.

Simulation session properties:

- Global account and character uniqueness.
- Exact shard, worker, and worker runtime ownership.
- Opaque token with only a stored hash.
- Database-generated expiry.
- Periodic worker heartbeat.
- Idempotent exact-generation release.
- Reconnect token rotation.
- Safe stale-token rejection after reconnect.
- Graceful leave remains owned by its in-flight release operation after the
  local lease is removed, preventing concurrent leaves from being misreported as
  revoked sessions.

The worker also enforces cached lease expiry locally so an AuthService outage
cannot leave a connected player active forever.

## Realtime UDP Transport

SimulationWorker uses LiteNetLib and the versioned `GameProtocol` package.

- Connection key validation happens before application messages.
- New peers must send one reliable ordered join request before the handshake
  timeout.
- Join ticket consumption is asynchronous and does not block the UDP poll loop.
- Control messages use reliable ordered delivery.
- Movement input uses sequenced delivery.
- Simulation snapshots use unreliable delivery.
- Packet magic, version, type, bounds, lengths, and finite numeric values are
  validated.
- Protocol violations receive a stable error where possible and are then
  disconnected.

Protocol version 6 carries both Shard and World identity. A standalone client
must be rebuilt when the protocol version changes.

## Entity Registry And Replication

- `SimulationEntityRegistry` owns nonzero network entity ids.
- Persistent character id and transient network entity id are separate.
- `ConnectionEntityBindingRegistry` enforces one peer per entity and one entity
  per peer.
- Same-character reconnect preserves the entity and movement state while
  replacing the peer binding.
- Entity spawn and despawn use reliable ordered control messages.
- Unity receives a reliable baseline before relying on snapshots.
- Remote presentation lives under a separate Unity presentation root.

## Interest Management

`SimulationInterestManager` maintains a spatial hash from authoritative entity
positions. Snapshot refreshes synchronize moving entities, while joins add one
entity incrementally and update only existing nearby observers. A join no longer
rebuilds and refreshes every peer visibility set. Each peer has a visibility
set:

- Enter radius adds entities.
- A larger exit radius prevents boundary flapping.
- Visibility changes emit reliable spawn or despawn.
- Snapshots contain only visible entity ids.
- Ordered visibility remains cached until the set changes.
- Equal ordered visibility sets share one encoded snapshot packet batch per
  broadcast.
- Cell size and radii are worker-owned config values.

This is process-local interest management for one complete shard. Cross-worker
zone interest is deferred until zones exist.

## Server-Authoritative Movement

- Clients send input sequences, camera yaw, and action buttons, never accepted
  positions.
- SimulationWorker runs the shared movement code at a fixed 30 Hz by default.
- It accepts only newer input sequences and neutralizes stale input.
- Aiming blocks sprint and jump.
- New planar control is ignored while airborne.
- Collision, slopes, steps, ground support, and bounds are server-owned.
- Each authoritative entity owns one reusable collision-query workspace, so
  fixed ticks reuse broadphase list and stable-id set capacity instead of
  allocating them again for every movement step.
- Snapshots are sent at 15 Hz by default with input acknowledgement.
- Unity predicts with the same source and reconciles to authoritative snapshots.

## Collision Data And Streaming

- `WorldData/Authoring` contains neutral JSON source.
- `Tools/WorldCollisionCompiler` emits versioned binary chunks and a manifest.
- Each chunk has a SHA-256 checksum.
- The manifest has a deterministic complete collision revision.
- SimulationWorker validates format, World id, checksums, coordinates, and
  revision before binding UDP.
- Unity validates and loads the same data before prediction.
- Static shapes are assigned to every intersected chunk and deduplicated by
  stable id during queries.
- Position-driven load and larger unload radii provide streaming hysteresis.
- A composite collision world combines static chunks and mutable dynamic shapes.

The current test World uses oriented boxes for ground, boundaries, a camera
wall, ramp, steps, and cover. Triangle terrain and replicated dynamic transforms
are not implemented.

## UDP Resilience And Quotas

Per-peer token buckets enforce configured packet and byte rates with burst
allowance. Sustained inbound abuse is rejected. Excess unreliable snapshot
output may be dropped without delaying reliable lifecycle messages.

An additional worker-wide snapshot byte bucket bounds aggregate unreliable
output. The checked-in local limit is 38 MiB per second with a 4 MiB burst.
Snapshot admission occurs for a complete chunk batch, so a peer receives either
all chunks for one snapshot sequence or none. The recipient start rotates past
the admitted group after each broadcast, distributing overload gaps across
peers instead of starving the same tail of the connection list. Reliable
control, spawn, despawn, and leave traffic does not consume this snapshot
budget.

Session heartbeats use bounded concurrency so one worker cannot create an
unbounded AuthService request fan-out. HTTP calls map timeout, connection,
invalid-response, authentication, and domain failures to structured results.

## Metrics And Logs

The meter `ShooterMmo.SimulationWorker.Realtime` exposes:

- Active peers and entities.
- Joined real players, joined synthetic bots, and unauthenticated peers as
  separate gauges.
- Sent and received packets and bytes.
- Quota rejections, total dropped snapshots, and the subset dropped by
  aggregate snapshot backpressure.
- Accepted and rejected joins.
- Spawn and despawn packet counts.
- Snapshot entity record counts.

Periodic structured logs expose the same totals. A worker status line also
reports real players, synthetic bots, unauthenticated peers, process CPU
normalized to total logical-core capacity, single-core-equivalent CPU, working
set, packet and payload rates, snapshot
drops, quota rejections, and the sample window. Metrics deliberately avoid
account, character, session, entity, and peer identifiers as labels.

The performance meter `ShooterMmo.SimulationWorker.Performance` records network
poll, completed-operation, join-queue-delay, join-finalization, simulation-tick,
tick-lag, collision-streaming, movement, interest, and snapshot-broadcast
durations plus fixed-tick resynchronizations. The periodic metrics log reports
interval averages, approximate p95 and p99 upper bounds, and maximum durations.
It also reports the current and interval-maximum completed-operation backlog so
admission congestion is distinguishable from UDP loss. Snapshot broadcast is
the complete snapshot pipeline and includes the separately reported interest
phase. The same interval log reports process allocation, GC collection counts,
managed heap size and fragmentation, live managed memory, distinct visibility
groups, encoded snapshot packet count, and sent snapshot packet count.

These operational values remain server-side. They are not added to the realtime
gameplay protocol and are not broadcast to Unity clients. The Unity F2 panel
derives its values only from local rendering, prediction, and received traffic.

SimulationWorker filters routine successful `AuthServiceClient` HTTP pipeline
messages below `Warning`. Domain failures and HTTP warnings remain visible, but
high-frequency bot heartbeat and release requests do not drown out worker
status, performance, lifecycle, or error logs.

Routine UDP connect and disconnect events plus successful synthetic bot joins
and leaves are logged at `Debug`. Successful real-player lifecycle events remain
at `Information`, and all admission failures remain visible. Synthetic bot
population is reported by the aggregate worker status line.

Auth, client, and simulation logs use the categories `[AUTH]`, `[CLIENT]`, and
`[SIMULATION]` in the Unity console.

## HTTP Security And Error Handling

- Account routes use the `AccountSession` authentication handler and policy.
- Worker routes use the `SimulationWorker` handler and policy.
- Worker credentials use `X-Simulation-Worker-ID` and
  `X-Simulation-Worker-Secret`.
- Secrets are compared in fixed time.
- Central middleware converts unhandled failures into RFC Problem Details.
- Responses carry `X-Correlation-ID`.
- Token-bearing responses use `Cache-Control: no-store` and `Pragma: no-cache`.
- No debug HTTP endpoint is registered in the current service surface.

## Health And Configuration

AuthService liveness confirms the process loop. Readiness runs PostgreSQL
`select 1` and Redis `PING` within configured timeouts and returns HTTP 503 if an
obligatory dependency fails.

SimulationWorker `--health-check-only` checks AuthService readiness, Redis, and
UDP port availability and exits 0 only when all checks pass.

All application settings fail fast. Checked-in non-secret defaults live in each
service's `Config` folder. The ignored root `.env` contains local credentials
and overrides. Environment variables and command-line options have higher
precedence.

## Persistence And Migrations

Migrations run under a PostgreSQL advisory transaction lock. Each migration id
is inserted only after its SQL succeeds. The topology migration preserves older
data while separating World content from Shard and SimulationWorker runtime
metadata. A later migration enforces one active simulation session per account.
Previously shipped migration ids and their source-schema references retain their
historical names because changing an applied migration would break upgrade
compatibility. The resulting current schema uses only the canonical topology
names listed above.

Integration tests reset only a database whose name contains `test`. Never point
the test connection variable at development or production data.

## Quality Coverage

- Configuration validation tests.
- Authentication, token, and session replacement tests.
- Protocol codec and invalid-packet tests.
- Entity lifecycle, interest, quota, metrics, and movement tests.
- Runtime heartbeat and exact-runtime fencing tests.
- Isolated PostgreSQL tests for migrations, placement, reconnect, global
  session constraints, worker failover, and transactional races.
- Unity EditMode tests for contracts, state, input, movement, and authored assets.
- Unity PlayMode bootstrap smoke tests.
- Socket-level realtime and load-test coverage.
- External headless stress authority and bot coverage for real UDP admission,
  movement, snapshots, graceful leave, process resources, and phase timing.

## Not Yet Implemented

- Item definitions, item instances, stacks, or slot inventory.
- Equipment, physical Bag items, per-character bank, Secure Container, or
  Recovery Storage.
- Carry weight, the planned 140 percent encumbrance curve, or inventory-driven
  movement restrictions.
- Protected-on-death policy, one-death insurance, death partition, persistent
  player corpses, concurrent corpse looting, or configurable NPC corpse
  persistence.
- Zones, cross-zone handoff, or layers.
- Multiple workers cooperating on one shard.
- Production scheduler or fleet autoscaler.
- Metric exporter, dashboards, and alerting.
- Persistent NPC or combat simulation.
- General terrain mesh and rigid-body collision.

The locked product design and implementation phases for the unimplemented item
foundation are documented in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) and
[Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md).

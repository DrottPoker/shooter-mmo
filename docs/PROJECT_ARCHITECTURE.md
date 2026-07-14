# Project Architecture

Last updated: 2026-07-14

## Purpose

This document is the source of truth for project-wide architecture, naming,
ownership, trust boundaries, and runtime flows. Detailed Unity internals belong
in [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md). Implemented
behavior belongs in the two feature documents.

## Canonical Terminology

The following terms are architectural contracts. Do not use `WorldServer`,
`realm`, or `world instance` as aliases for them.

| Term | Definition |
| --- | --- |
| Global Services | Durable services shared by every region and shard |
| World | Shared game content and world data, not a server or process |
| Fleet | A regional or operational compute group, such as EU or US |
| Node | One machine or container host inside a fleet |
| SimulationWorker | One headless process that runs authoritative simulation |
| Worker runtime | One process generation identified by a unique runtime id |
| SimulationAssignment | The authoritative worker-to-shard mapping |
| Shard | A player-selectable copy of the shared world simulation |
| Zone | A future authoritative spatial partition inside a shard |
| Layer | A future population copy of a zone or area inside one shard |

There is no Realm layer. Accounts, characters, progression, and the future
economy are global. Fleets are compute placement boundaries, not data silos.
Players will be able to use the same character in different regions and shards,
subject to the one-active-session rules.

## Topology Model

```text
Global Services
  AuthService
  PostgreSQL
  Redis
  future economy, social, inventory, and persistence services

World: local-world-1
  shared map, collision, and future content definitions

Fleet: local-fleet
  Node: local-node-1
    SimulationWorker: local-simulation-worker-1
      Worker runtime: generated opaque id per process start
      SimulationAssignment: worker -> local-shard-1

Shard: local-shard-1
  World: local-world-1
  Fleet: local-fleet
```

A shard belongs to one fleet and references one World definition. A node also
belongs to one fleet. AuthService permits an assignment only when the worker's
node and target shard belong to the same fleet.

The current safe scale unit is one active SimulationWorker per shard and one
active shard per SimulationWorker. PostgreSQL partial unique indexes enforce
both sides. Zones and layers will later extend the assignment model when one
process can no longer simulate a complete shard. No placeholder zone or layer
objects exist today.

## System Context

```text
                      HTTPS or local HTTP
Unity Client ------------------------------------> AuthService
     |                                                  |
     | LiteNetLib UDP                                   | durable authority
     v                                                  v
SimulationWorker --------------------------------> PostgreSQL
     |
     | service-authenticated HTTP
     +--------------------------------------------> AuthService
     |
     +--------------------------------------------> Redis readiness

AuthService --------------------------------------> Redis readiness
```

Unity uses AuthService for identity, characters, shard discovery, and placement.
AuthService returns a SimulationWorker endpoint only with a short-lived join
ticket. Realtime gameplay packets travel directly between Unity and the assigned
worker. AuthService never proxies realtime traffic.

## Repository Boundaries

### AuthService

`AuthService` is the only ASP.NET Core application. It owns:

- Accounts, password hashes, and revocable account sessions.
- Characters and ownership.
- World, Fleet, Node, Shard, SimulationWorker, and SimulationAssignment records.
- Worker heartbeats, online status, capacity, runtime fencing, and failover.
- Capacity-aware shard placement and short-lived join tickets.
- Global simulation-session leases.
- HTTP authentication, policies, rate limiting, Problem Details, correlation
  ids, sensitive response caching rules, and health routes.
- PostgreSQL schema migrations and idempotent topology bootstrap.

Feature folders remain inside the service that owns them. Configuration lives
under `AuthService/Config`.

### SimulationWorker

`SimulationWorker` is a .NET Generic Host console application. It does not host
ASP.NET. LiteNetLib owns its UDP endpoint. It owns:

- UDP admission, protocol validation, and join and leave handshakes.
- Exact worker runtime identity and its AuthService registration lease.
- Active simulation sessions and connection-to-entity bindings.
- Server-assigned network entity ids and reliable spawn and despawn.
- Fixed-rate authoritative movement and collision queries.
- Input sequence processing and periodic snapshots.
- Spatial interest management with enter and exit hysteresis.
- Snapshot encoding reuse for peers with equal visibility.
- Per-peer UDP quotas, fair aggregate snapshot backpressure, and
  low-cardinality realtime metrics.
- Bounded session heartbeat fan-out.
- Position-driven world collision chunk streaming.
- Fail-fast configuration and collision validation.

Configuration lives under `SimulationWorker/Config`. A process receives its
Fleet, Node, Shard, and World identity through validated configuration.

### Shared

`Shared` contains backend helpers that have more than one backend consumer:

- Optional root `.env` loading.
- Connection-string parsing.
- Redis protocol health checks.
- Common health response models.

It must not become a dumping ground for feature logic.

### GameProtocol

`GameProtocol/Runtime` is a local Unity package and the single source for the
binary realtime contract. `Shared/DotNet/GameProtocol` compiles the same files
for backend processes and tests.

Protocol version 6 includes:

- Join, leave, rejection, and structured disconnect messages.
- Shard and World identity in join acceptance.
- Server-assigned network entity ids.
- Reliable ordered entity spawn and despawn.
- Bounded, sequenced movement input batches.
- Chunked simulation snapshots with server tick, snapshot sequence, and input
  acknowledgement.
- Movement, simulation revision, and collision revision metadata.
- Packet magic, version, type, size, and bounded-field validation.

Channel 0 is reliable ordered control. Channel 1 is sequenced movement input.
Snapshots use unchanneled unreliable delivery and application-level tick and
chunk metadata.

### GameSimulation

`GameSimulation/Runtime` is the shared fixed-step simulation source. Unity uses
it directly as a local package and `Shared/DotNet/GameSimulation` compiles it
for SimulationWorker.

It owns movement integration, action restrictions, gravity, facing, capsule
collision, steps, slopes, bounds, and collision-data codecs. It has no Unity,
transport, database, or presentation dependency.

### WorldData

`WorldData` is shared content, so it correctly remains at repository root. It
contains neutral collision authoring and compiled, checksummed chunks keyed by
World id. A shard references a World and its worker loads that World's data.

`Tools/WorldCollisionCompiler` builds and verifies the same data consumed by
SimulationWorker and Unity. Static collision is deterministic and shared.
Dynamic collision uses a mutable spatial hash behind the same collision-query
interface, leaving room for doors, lifts, and other server-owned objects.

### Unity Client

`shooter-mmorpg-unity-client` owns presentation, input, prediction,
reconciliation, interpolation, scene transitions, HTTP serialization, and the
persistent UDP client. It never owns authoritative gameplay state. See
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md).

### Tests And Tools

`Tests/ShooterMmo.Backend.Tests` contains unit, socket-level, and isolated
PostgreSQL integration tests. Unity tests live inside the Unity project. `Tools`
contains repository-wide verification, content build, and external stress
tools. `SimulationStressGenerator` hosts an in-memory loopback authority and
manually polled headless UDP clients without adding per-bot transport threads or
a stress admission path to production services. Its bounded latency reservoirs
and process samplers make longer local soak tests safe to run without the tool
itself accumulating every acknowledgement sample.

## Durable Data Model

| Table | Responsibility |
| --- | --- |
| `accounts` | Global account identity and password hash |
| `account_sessions` | One active account login session and revocation reason |
| `characters` | Global characters owned by accounts |
| `world_definitions` | Shared World content identities |
| `fleets` | Regional or operational compute groups |
| `simulation_nodes` | Machines or hosts assigned to fleets |
| `shards` | Player-selectable simulations tied to a World and Fleet |
| `simulation_workers` | Logical workers and current runtime metadata |
| `simulation_assignments` | Historical and active worker-to-shard mappings |
| `simulation_join_tickets` | Short-lived exact-runtime admission credentials |
| `character_simulation_sessions` | Global active simulation leases |
| `schema_migrations` | Applied migration history |

Active account login sessions are unique by account. Active simulation sessions
are unique by both character and account. This prevents one account token from
running multiple characters simultaneously, even if the token is copied to a
second client.

## Runtime Identity And Assignment Safety

`SimulationWorkerId` is a stable logical process slot. Every process start also
creates a random `RuntimeId`. Tickets and simulation sessions bind to both.

Heartbeat processing is transactional:

1. AuthService locks the target shard row.
2. It validates World, Fleet, Node, and Shard topology.
3. A healthy existing assignment fences a different worker.
4. A timed-out or explicitly offline owner is released atomically.
5. Pending tickets and active sessions for every superseded runtime are
   invalidated, including when the logical worker moves to another shard.
6. A newer runtime may replace an older runtime for the same worker id.
7. The exact active SimulationAssignment is created or retained.

SimulationWorker renews a local registration lease from successful heartbeat
responses. UDP joins are rejected without a valid lease. If the lease expires,
or AuthService reports that the runtime lost authority, the process stops. This
limits split-brain behavior during network partitions and allows another worker
to take over after the authoritative heartbeat timeout.

## Trust Boundaries

### Player Authentication

Registration and login issue opaque account tokens. PostgreSQL stores only a
SHA-256 hash. Protected routes use the `AccountSession` ASP.NET authentication
scheme and policy.

A later login replaces the prior account session in one transaction, consumes
its tickets, and releases its active simulation sessions. Logout and manual
revocation perform the same dependent cleanup with distinct reason codes.

### Service Authentication

SimulationWorker sends `X-Simulation-Worker-ID` and
`X-Simulation-Worker-Secret`. AuthService validates the configured secret with a
fixed-time comparison and adds the worker identity claim. Endpoint policies and
request payload checks prevent a worker from acting for another worker id or
runtime.

### Realtime Admission

The LiteNetLib connection key filters unrelated traffic but is not player
authentication. The join ticket is the player credential. It is bound to an
account session, character, shard, worker, and runtime, then consumed exactly
once through the authenticated service channel.

The client validates placement protocol and simulation revisions before opening
UDP. Join acceptance must match the placed character, shard, World, simulation
revision, and collision revision.

## Core Runtime Flows

### Topology Bootstrap

1. AuthService validates `Simulation:Topology` at startup.
2. Database migrations create or upgrade the durable topology schema.
3. The seeder idempotently ensures configured Worlds, Fleets, Nodes, and Shards.
4. Seeded shards remain offline because topology alone does not create a healthy
   worker assignment.

### Worker Registration

1. SimulationWorker validates config and loads collision near the spawn point.
2. It binds UDP and reports transport readiness.
3. It sends worker, runtime, topology, endpoint, capacity, protocol, simulation,
   and collision metadata to AuthService.
4. AuthService validates or creates the assignment and returns a database-timed
   online lease.
5. The worker renews its local registration lease and continues heartbeats.

### Shard Discovery And Placement

1. Unity calls `GET /api/shards`.
2. AuthService lists logical shards and reconciles worker-reported connections
   with authoritative unexpired simulation sessions while aggregating fresh
   assigned-worker capacity.
3. Worker host and UDP port are not exposed in this public list.
4. Unity requests `POST /api/shards/{shardId}/join` for an owned character.
5. AuthService locks the account and character, applies global active-session
   rules, reserves authoritative session and pending-ticket capacity under a
   worker row lock, and creates a ticket.
6. The response contains the selected shard plus an exact worker runtime
   endpoint and compatibility metadata.

### Join And Reconnect

1. Unity connects to the placement endpoint and sends the ticket reliably.
2. SimulationWorker consumes it through AuthService with its worker and runtime
   identity.
3. Ticket consumption and session claim occur in one PostgreSQL transaction.
4. SimulationWorker creates or reconnects the entity and binds it to the peer.
5. Join acceptance returns topology, compatibility, movement, entity, and
   non-secret session metadata.
6. Reliable spawn baselines establish presentation state before snapshots.

A reconnect is valid only for the exact active shard, worker, and runtime. It
preserves session and entity state, rotates the secret simulation-session token,
and disconnects the older peer generation.

### Leave, Disconnect, Expiry, And Failover

1. Unity requests leave with the exact simulation-session id.
2. SimulationWorker releases the exact session token generation through
   AuthService.
3. The worker removes the peer binding and entity and sends reliable despawn.
4. Unexpected disconnect uses the same cleanup path.
5. If release is unavailable, the database lease expires and local expiry
   enforcement removes the session.
6. Worker shutdown or authoritative failover releases its assignment, pending
   tickets, and bound sessions.

### Authoritative Movement

1. SimulationWorker sends validated movement settings and shared-data revisions
   on join.
2. Unity samples Input Actions at the provided fixed rate, predicts locally,
   and sends redundant batches of recent unacknowledged inputs.
3. SimulationWorker processes only newer sequences at 30 Hz. Clients send input,
   never accepted positions.
4. Static and dynamic collision queries run through the shared simulation.
5. Interest management determines which entities each connection can observe.
6. SimulationWorker sends visible authoritative states at 15 Hz.
7. The local client acknowledges, rewinds, and replays prediction. Remote clients
   interpolate behind the latest server tick.

## Configuration Ownership

- `AuthService/Config` owns AuthService defaults and topology bootstrap.
- `SimulationWorker/Config` owns worker identity, endpoint, networking,
  simulation, collision streaming, quotas, and interest settings.
- `shooter-mmorpg-unity-client/Assets/Resources/Config` owns runtime Unity
  endpoint and timeout configuration.
- The ignored root `.env` owns local secrets and machine overrides.
- `.env.example` is the committed environment contract.
- Root Compose, solution, SDK, and build files are repository-wide concerns.

Both backend processes load owned JSON, optional root `.env`, real environment
variables, and command-line values in increasing precedence. Invalid application
configuration fails before work is accepted.

## Health And Operations

AuthService exposes `/health/live` and `/health/ready`. Readiness performs a real
PostgreSQL query and Redis `PING` and returns HTTP 503 when an obligatory
dependency fails.

SimulationWorker uses `--health-check-only` because it has no HTTP server. It
checks AuthService readiness, Redis, and UDP port availability and returns exit
code 1 on failure.

Local Compose ports bind to `127.0.0.1`. Realtime metrics avoid account,
character, session, entity, or peer ids as metric labels.

## Future Scaling Contract

The next topology extension is not another naming rewrite. It will add:

- Zone definitions and authoritative zone ownership inside a shard.
- Cross-zone handoff with one owner at every simulation tick.
- Layer definitions for selected high-population areas inside a shard.
- Placement policies that keep groups together and prevent content exploits.
- Durable shared event policies for bosses, resources, and economy where layers
  must not create duplicate rewards.

Until that work exists, a shard is never presented as partially distributed.
Only UI may remain explicitly temporary. All other boundaries in this document
are production-oriented foundations.

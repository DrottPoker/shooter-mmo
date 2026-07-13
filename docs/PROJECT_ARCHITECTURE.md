# Project Architecture

Last updated: 2026-07-13

## Purpose

This document is the source of truth for project-wide architecture. Detailed
Unity internals belong in [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md).
Implemented backend and gameplay behavior belongs in the feature documents.

## System Context

```text
                         HTTPS or local HTTP
Unity Client ------------------------------------> AuthService
     |                                                  |
     | LiteNetLib reliable UDP                          | PostgreSQL authority
     v                                                  v
WorldServer --------------------------------------> AuthService ------> PostgreSQL
     |                        service-authenticated HTTP
     |
     +-----------------------------------------------------------> Redis

AuthService -----------------------------------------------------> Redis
```

The client uses AuthService for accounts, characters, world discovery, and join
tickets. It connects directly to the selected WorldServer over UDP after ticket
creation. AuthService never carries realtime gameplay packets.

## Repository Components

### AuthService

`AuthService` is the only ASP.NET Core application. It is the public application
API and the authority for persistent identity and world-access data.

AuthService owns:

- Accounts and password hashes.
- Revocable account sessions.
- Characters and ownership.
- World registry records and heartbeat timestamps.
- Short-lived world join tickets.
- Global single-world leases for characters.
- HTTP authentication, authorization, rate limiting, Problem Details, and
  health routes.

### WorldServer

`WorldServer` is a headless .NET Generic Host console application. It does not
host ASP.NET routes. LiteNetLib owns its realtime UDP transport on port `27015`
by default.

WorldServer owns:

- UDP connection admission and versioned packet validation.
- Join-ticket handshakes and local connected-player state.
- Fixed-rate authoritative player movement simulation.
- Fail-fast loading and checksum validation of baked world collision chunks.
- Static and dynamic collision queries through one simulation interface.
- Input sequence processing and periodic world snapshots.
- Reconnect replacement for an older connection of the same character.
- Exact-session leave and disconnect cleanup.
- Periodic world registry and active-session heartbeats.
- Startup configuration and dependency validation.

### Shared Core

`Shared/ShooterMmo.Shared.csproj` contains framework-neutral .NET helpers:

- Optional repository-root `.env` loading.
- Connection-string parsing and validation.
- Redis protocol health checks.
- Common dependency and service health models.

`Shared.Http/ShooterMmo.Shared.Http.csproj` contains ASP.NET-specific Problem
Details, correlation-id, and API pipeline behavior. Only AuthService references
this project.

### Realtime Game Protocol

`GameProtocol/Runtime` is a local Unity package containing the versioned binary
realtime contract. `GameProtocol.DotNet` compiles the same source for WorldServer
and backend tests without placing .NET build output inside the Unity package.

Protocol version 4 currently defines:

- Join request, accepted, and rejected messages.
- Leave request, accepted, and rejected messages.
- Structured server disconnect reasons.
- Bounded batches of sequenced player input commands.
- Chunked world snapshots with server tick, snapshot sequence, acknowledged
  input sequence, and authoritative player state.
- Explicit movement-simulation and collision-data revisions plus character
  capsule settings in join acceptance.
- Packet magic, version, size, and bounded-string validation.

Control messages use reliable ordered channel 0. Movement inputs use sequenced
channel 1. World snapshots use LiteNetLib's unchanneled `Unreliable` delivery
and carry application-level tick and chunk metadata so multiple chunks from one
snapshot can arrive without LiteNetLib discarding an earlier chunk. LiteNetLib
reports unchanneled packets with receive channel 0, so snapshot validation uses
message type and delivery method rather than a fictional third channel.

### Shared Game Simulation

`GameSimulation/Runtime` contains the fixed-step movement rules used by both
WorldServer and Unity. `GameSimulation.DotNet` compiles that exact source for
the backend while Unity consumes it as the local
`com.shootermmo.game-simulation` package.

The shared simulation owns movement integration, aim restrictions on sprint and
jump, sprint state, gravity, facing, world bounds, the kinematic character
capsule, collision queries, step handling, slope limits, and collision-data
compilation. It contains no Unity
dependencies and does not own network transport or presentation. This prevents
the server and client prediction paths from drifting into separate movement or
collision implementations.

### Shared World Collision Data

`WorldData` is a local Unity package and the canonical repository for baked
world collision. Each world has a versioned manifest and binary chunks. Every
chunk records oriented boxes with stable ids and collision-layer masks. The
manifest records a SHA-256 checksum per chunk and a deterministic revision for
the complete collision set.

`Tools/WorldCollisionCompiler` turns neutral JSON authoring data into the same
binary chunks used by WorldServer and Unity. WorldServer copies the package data
into its build output and validates the format, world id, checksums, chunk
coordinates, and revision before opening its UDP socket. Unity loads the same
resources before enabling prediction. A revision mismatch rejects the join on
the client instead of simulating against different geometry.

Static boxes are assigned to every intersected chunk and deduplicated by stable
id during queries. Movement contacts are sorted by stable id before resolution
so server and client do not depend on dictionary or spatial-hash iteration
order. Static chunks can be loaded and unloaded without changing the
movement query contract, which leaves a clean boundary for later client and
server streaming. Dynamic colliders use a mutable spatial hash behind the same
`ICollisionWorld` interface. This allows doors, lifts, and other server-owned
kinematic objects to be registered without replacing the static format. A later
mesh or rigid-body backend can implement the same query boundary without moving
authority into Unity physics.

### Unity Client

`shooter-mmorpg-unity-client` is the Unity 6 player client. It owns local
presentation, input sampling, prediction, reconciliation, remote interpolation,
scene transitions, HTTP serialization, and the persistent UDP client. See
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md).

### Tests

`Tests/ShooterMmo.Backend.Tests` contains unit, socket-level realtime, and
isolated PostgreSQL integration tests. Unity EditMode and PlayMode tests remain
inside the Unity project.

## Data Ownership

| Data | Authority | Current storage |
| --- | --- | --- |
| Account identity and credentials | AuthService | PostgreSQL |
| Account sessions | AuthService | PostgreSQL |
| Characters | AuthService | PostgreSQL |
| World registry and online status | AuthService | PostgreSQL |
| Join tickets | AuthService | PostgreSQL |
| Character world-session lease | AuthService | PostgreSQL |
| Connected UDP peers and local sessions | WorldServer | Process memory |
| Live player movement state | WorldServer | Process memory |
| Movement simulation configuration | WorldServer | Validated configuration |
| Predicted local movement and remote interpolation buffers | Unity client | Process memory |
| Client selection and active session view | Unity client | Process memory |

Redis is required by operational readiness but does not yet own domain state.

## Trust Boundaries

### Player Authentication

Registration and login issue an opaque account session token. Only its SHA-256
hash is stored. Protected routes use the `AccountSession` ASP.NET authentication
scheme and policy. PostgreSQL permits only one unrevoked session per account.
A successful login locks the account, revokes the previous session with the
stable `account_session_replaced` reason, consumes its pending join tickets,
releases every active world lease for the account, and creates the replacement
session in one transaction. Logout and manual revocation also invalidate related
tickets and leases.

### Service Authentication

WorldServer sends `X-World-Server-ID` and `X-World-Server-Secret` to protected
AuthService routes. The `WorldServer` scheme binds the credential to a world. A
server cannot consume, heartbeat, or release another world's resources.

### Realtime Admission

A LiteNetLib connection key rejects unrelated traffic before application
handshake processing. It is not player authentication. The short-lived join
ticket is the credential that authenticates the player and character. WorldServer
consumes it through its authenticated AuthService channel before accepting the
peer as joined.

Every packet validates protocol magic, version, type, size, and bounded fields.
Peers that do not complete one join handshake before the configured timeout are
disconnected.

## Core Runtime Flows

### Account And Character Flow

1. Unity registers or logs in through AuthService.
2. AuthService returns an opaque bearer token and account-session metadata.
3. Unity uses the token for protected account and character operations.
4. AuthService validates account ownership for every character operation.
5. The persistent client periodically validates the account session. An HTTP 401
   with `account_session_replaced` clears local state and returns the older
   client to LoginMenu.

An older client already connected to a world is also removed through the world
lease heartbeat path. AuthService reports the same replacement reason,
WorldServer invalidates the exact local session, and the realtime control
channel sends the reason before disconnecting the peer.

### World Discovery

1. WorldServer binds its UDP transport before it registers as online.
2. WorldServer authenticates and heartbeats its advertised host, UDP port,
   process instance id, protocol version, simulation revision, and collision
   revision.
3. AuthService stores the advertised endpoint, build metadata, and a
   database-generated heartbeat time.
4. World listing derives `isOnline` from that time and the configured timeout.
5. A seeded or stale world remains offline until a fresh heartbeat exists.
6. Graceful shutdown marks the matching process instance offline immediately.
   A stale process cannot mark a newer instance offline.

### World Join And Reconnect

1. Unity requests a join ticket for an owned character and online world.
2. AuthService locks the character and invalidates older unconsumed tickets.
3. Unity opens a LiteNetLib connection to the registry-provided host and UDP
   port.
4. Unity sends the join ticket in a versioned reliable ordered message.
5. WorldServer consumes the ticket through its service-authenticated AuthService
   client.
6. Ticket consumption and the authoritative lease claim occur in one PostgreSQL
   transaction.
7. WorldServer stores the accepted session and returns non-secret session
   metadata over UDP.
8. A same-world reconnect preserves the world-session id, rotates the secret
   session token, and disconnects the older peer.

PostgreSQL locks and constraints prevent concurrent tickets or cross-world
active leases for the same character.

### Leave, Disconnect, And Expiry

1. Unity sends the exact world-session id over its authenticated peer.
2. WorldServer validates it against the peer's local session.
3. WorldServer releases the exact lease generation through AuthService.
4. Unity receives leave acceptance, closes UDP, clears local session state, and
   changes scene.
5. An unexpected peer disconnect triggers the same release service.
6. If release cannot reach AuthService, the lease becomes inactive after
   heartbeat expiry.

WorldServer also enforces the cached database lease expiry locally. A missed
heartbeat cannot leave a peer active indefinitely while AuthService is
unreachable.

A delayed disconnect from an older reconnect generation carries the older secret
session token and cannot release the newer lease.

### Authoritative Movement And Replication

1. WorldServer includes validated movement settings, the initial player state,
   the movement-simulation revision, and the authoritative collision revision
   in the accepted join response. Unity validates both revisions before
   movement begins.
2. Unity samples Player Input Actions at the server-provided fixed rate, assigns
   a monotonically increasing input sequence, predicts the input locally, and
   sends a redundant batch containing up to the four newest unacknowledged
   inputs.
3. WorldServer accepts only newer sequences and simulates every connected player
   on its fixed 30 Hz clock using the shared capsule motor and composite
   collision world. Clients send input, never accepted positions. If no newer
   input arrives for the configured timeout, WorldServer neutralizes movement
   and action buttons instead of replaying stale input indefinitely.
4. WorldServer sends authoritative snapshots at 15 Hz. Each player entry
   includes the latest processed input sequence.
5. The owning client replaces its predicted base with the authoritative state,
   removes acknowledged inputs, and replays remaining inputs. Small visual
   corrections are smoothed and large corrections are applied immediately. A
   local presentation state interpolates predicted fixed-tick poses at the
   render frame rate, including the camera target, without changing simulation
   authority or the commands sent to WorldServer.
6. Other players advance through a snapshot buffer on an adaptive monotonic
   render clock approximately 100 ms behind the latest server tick. It makes
   bounded speed corrections and restores its target delay after a network
   stall instead of keeping permanent extra latency.

The current baked collision set contains the test map ground, four boundaries,
camera wall, ramp, three steps, and two cover objects. The same oriented-box
queries and capsule motor run in WorldServer and local prediction. WorldServer
snapshots remain authoritative and reconciliation corrects any float drift,
packet loss, stale input, or untrusted client behavior.

Walkable ground queries convert the authored surface height and normal into the
exact vertical support height required by the collision capsule. This prevents a
ground penetration correction from adding a downhill component while the player
is idle. Unity removes the capsule's slope support offset from the interpolated
render pose without overwriting its height with a discrete surface sample.
Grounded steps and small drops receive a bounded 100 ms vertical presentation
blend, while ramps, airborne movement, teleports, and large corrections remain
direct. None of this changes the authoritative simulation position. Surfaces
above the configured slope limit are excluded from ground support and remain
collision obstacles.

## HTTP And Realtime Conventions

AuthService HTTP uses:

- RFC Problem Details with stable application error codes.
- `X-Correlation-ID` on responses and error bodies.
- `Cache-Control: no-store` and `Pragma: no-cache` for token responses.
- Separate `/health/live` and `/health/ready` routes.
- Fail-fast validation for application-owned configuration.

WorldServer maps AuthService timeouts, connection failures, invalid payloads,
and service authentication failures into structured realtime errors. Its health
surface is the one-shot `--health-check-only` command because the process does
not host an HTTP server.

## Operational Architecture

Local PostgreSQL and Redis run through Docker Compose and bind only to
`127.0.0.1`. Secrets live in the ignored root `.env`; `.env.example` documents
required keys. Local Redis uses `127.0.0.1` explicitly so the one-second health
probe does not wait for an unavailable IPv6 localhost listener.

AuthService readiness executes PostgreSQL `select 1` and Redis `PING`.
WorldServer startup validates collision data, then readiness checks Redis,
AuthService readiness, and UDP port availability. `--health-check-only` exits
with code 0 when all are ready and 1 otherwise.

## Architectural Direction

The collision format currently supports oriented boxes, which covers the
authored test map. Triangle-mesh chunks for terrain and caves, dynamic-object
replication, durable world simulation, interest management, bandwidth budgets,
and combat remain later slices. Those additions extend the current chunk and
query boundaries rather than replacing movement authority.

Only UI may be intentionally temporary. Service boundaries, state ownership,
networking contracts, gameplay systems, persistence, and tooling must remain
maintainable and testable from their first implementation.

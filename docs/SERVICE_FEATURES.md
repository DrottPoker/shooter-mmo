# Service Features

Last updated: 2026-07-13

## Purpose

This document records implemented backend, networking, persistence, security,
and operational behavior. It does not describe planned gameplay.

## Accounts And Authentication

Status: Implemented

- Register with email, username, and password.
- Login with username or email.
- BCrypt password hashing.
- Opaque database-backed session tokens with only SHA-256 hashes stored.
- Exactly one active session per account, enforced by a partial unique database
  index and an account-locked login transaction.
- A successful later login revokes the previous session with the stable
  `account_session_replaced` code and invalidates all pending tickets and active
  world leases owned by that account.
- Authenticated current-account lookup.
- Current-session logout and account-owned targeted session revocation.
- Five login or registration attempts per client IP per 60-second fixed window.
- No-store headers wherever a session or access token is returned.

Revocation also invalidates unconsumed join tickets and releases character
world-session leases created by the revoked account session.

## AuthService HTTP Surface

AuthService is the only ASP.NET application.

| Method and route | Caller | Purpose |
| --- | --- | --- |
| `POST /api/accounts/register` | Public client | Create account and session |
| `POST /api/accounts/login` | Public client | Create account session |
| `GET /api/accounts/session` | Authenticated player | Validate current session |
| `GET /api/accounts/me` | Authenticated player | Read current account |
| `POST /api/accounts/logout` | Authenticated player | Revoke current session |
| `DELETE /api/accounts/sessions/{sessionId}` | Authenticated player | Revoke an owned session |
| `GET /api/characters` | Authenticated player | List owned characters |
| `POST /api/characters` | Authenticated player | Create character |
| `GET /api/worlds` | Public client | List worlds and online state |
| `POST /api/worlds/{worldId}/join` | Authenticated player | Create join ticket |
| `POST /api/worlds/{worldId}/heartbeat` | Authenticated WorldServer | Refresh registry entry |
| `POST /api/worlds/{worldId}/offline` | Authenticated WorldServer | Unregister exact process instance |
| `POST /api/world-join-tickets/consume` | Authenticated WorldServer | Consume ticket and claim lease |
| `POST /api/world-sessions/{id}/heartbeat` | Authenticated WorldServer | Extend exact lease generation |
| `POST /api/world-sessions/{id}/release` | Authenticated WorldServer | Release exact lease generation |
| `GET /health/live` | Operator | Check process liveness |
| `GET /health/ready` | Operator | Check mandatory dependencies |

WorldServer has no HTTP routes and no debug join surface.

## Characters

Status: Implemented

- Create and list characters owned by the authenticated account.
- Maximum of five characters per account.
- Globally unique names between 3 and 24 characters.
- Names support letters, numbers, spaces, hyphens, and underscores.

Character deletion and restoration are not implemented.

## World Registry

Status: Implemented

- Public world listing with host, UDP port, ruleset, and heartbeat metadata.
- Service-authenticated WorldServer heartbeat.
- Registration begins only after the UDP transport has bound successfully.
- Each heartbeat publishes the advertised host and UDP port, process instance
  id, protocol version, simulation revision, and collision revision.
- Database-generated heartbeat timestamps.
- Timeout-derived online status.
- Seeded and stale worlds remain offline until a fresh heartbeat exists.
- Default 10-second heartbeat interval and 30-second online timeout.
- Graceful shutdown marks the matching instance offline immediately. Instance
  matching prevents an older process from unregistering its replacement.

## World Join Tickets

Status: Implemented

- Authenticated players request a short-lived ticket for an owned character.
- Ticket creation locks the character and invalidates older unconsumed tickets.
- Only one active unconsumed ticket can exist per character.
- WorldServer consumes tickets through a service-authenticated endpoint.
- Wrong-world consumption is rejected without consuming the ticket.
- Ticket consumption and world-session claim share one PostgreSQL transaction.
- Current ticket lifetime is 30 seconds.

## Character World Sessions

Status: Implemented

- One active world-session lease per character across every world.
- Same-world reconnect preserves the world-session id and rotates the secret
  session token.
- WorldServer heartbeat extends the lease from the database clock.
- Explicit release is idempotent.
- Expired leases can be replaced.
- Exact world-session identity and token generation protect newer reconnects
  from delayed leave or disconnect work.
- Account-session revocation releases related leases.
- A replaced account session fails world heartbeat with
  `account_session_replaced`; WorldServer forwards that reason to the displaced
  UDP client before disconnecting it.
- WorldServer disconnects a local peer with `session_expired` when its cached
  database lease reaches its expiry before a successful renewal.

PostgreSQL constraints, transactions, character locks, and advisory migration
locks enforce consistency.

## Realtime UDP Transport

Status: Session and authoritative movement foundation implemented

- WorldServer runs as a headless .NET Generic Host console application.
- LiteNetLib 2.1.4 provides reliable UDP connection management.
- Default UDP port is `27015`.
- Maximum peer count, join timeout, and network poll interval are validated at
  startup.
- A connection key rejects unrelated traffic before application admission.
- Join tickets are sent through a bounded versioned binary protocol.
- Join authentication runs asynchronously so the UDP poll loop is not blocked
  by AuthService requests.
- Every peer must complete one join handshake before its timeout.
- Accepted peers are bound to an exact local session generation.
- Same-character reconnect disconnects the older peer.
- Normal leave, unexpected disconnect, revoked lease, and process shutdown all
  converge on exact-session cleanup.
- Structured join, leave, and server-disconnect errors are returned to Unity.
- Movement input is isolated from control traffic on a sequenced channel.
- World snapshots use unchanneled unreliable delivery with bounded packet size
  and application-level tick and chunk metadata.

Protocol version 4 reserves reliable ordered channel 0 for control, sequenced
channel 1 for player input, and LiteNetLib's unchanneled `Unreliable` delivery
for world snapshots. Unreliable receive callbacks report channel 0, so snapshot
validation relies on the protocol message type and delivery method.

## Server-Authoritative Movement

Status: Fixed-tick movement and authored collision implemented

- WorldServer owns the live movement state for every joined character.
- The simulation runs at a validated fixed 30 Hz by default and limits catch-up
  work to five ticks per poll cycle.
- World snapshots are emitted at a validated 15 Hz by default.
- Each input command carries an unsigned sequence, client tick, normalized move
  vector, camera yaw, and bounded button flags.
- Aim is an authoritative movement state modifier. It cancels sprint and causes
  WorldServer to ignore sprint and jump flags until Aim is released.
- Clients send up to four current unacknowledged inputs per batch. WorldServer
  ignores duplicate and older sequences and preserves a jump edge until the next
  simulation tick.
- The default 500 ms input-silence timeout neutralizes stale movement, sprint,
  aim, and jump state until a newer sequence arrives.
- Each snapshot player record includes the authoritative movement state and the
  newest processed input sequence required for reconciliation.
- Snapshot chunks contain at most 20 players and remain below the protocol's
  1200-byte packet limit.
- Same-character reconnect preserves the current in-memory movement state while
  the older peer is replaced.
- Tick rate, snapshot rate, speeds, rotation, gravity, terminal fall speed,
  jump, bounds, spawn, capsule dimensions, slope limit, step height, ground
  snap, movement substep, and penetration iteration budget are fail-fast
  configuration values.
- WorldServer accepts client input commands only. It never accepts a client
  position as authority.
- The server simulates a vertical capsule against the composite static and
  dynamic collision world. Movement is subdivided to prevent normal sprint and
  fall speeds from tunneling through thin authored objects.
- Walkable rotated ramps, configured steps, walls, cover, ground, ceilings, and
  map boundaries are resolved by the shared kinematic motor.
- Walkable ramps use slope-aware capsule support heights, preventing stationary
  characters from being pushed downhill by penetration resolution. Surfaces
  above the configured slope limit are not accepted as ground support.
- Authoritative snapshots correct client prediction through acknowledged-input
  replay and reconciliation.

`GameSimulation/Runtime` is compiled unchanged into WorldServer through
`GameSimulation.DotNet` and into Unity through a local package. It contains the
neutral collision format, chunk codec, oriented-box queries, spatial indexes,
and capsule motor in addition to movement integration.

## World Collision Data

Status: Static test-map pipeline and dynamic registry foundation implemented

- `WorldData` stores neutral authoring JSON, a versioned manifest, and binary
  chunks shared with Unity builds.
- `Tools/WorldCollisionCompiler` bakes and verifies the data outside Unity.
- The current `local-world-1` revision contains 12 authored BoxColliders in four
  32-meter chunks.
- Every chunk has a SHA-256 checksum. The manifest revision is derived from the
  world id, format, chunk size, chunk coordinates, and checksums.
- WorldServer validates all collision data before starting realtime transport.
- Join acceptance includes the collision revision. A client with different map
  data cannot enable prediction for that session.
- Static objects are queried only from intersected chunks and deduplicated by
  stable id.
- Static chunks expose explicit load and unload operations for later interest
  and streaming systems.
- `DynamicCollisionWorld` supports thread-safe upsert and removal through a
  spatial hash. It is registered in the server's composite collision world for
  future doors, lifts, platforms, and server-owned kinematic objects.
- The current binary format supports oriented boxes. Terrain and cave triangle
  meshes require a later format extension behind the existing query interface.

## Operational Diagnostics

Status: Implemented

- AuthService logs credential validation without recording login input,
  passwords, email addresses, bearer tokens, or session-token values.
- Successful registration, login, logout, and session revocation logs contain
  stable account and public session identifiers.
- WorldServer logs UDP admission, authenticated account and character joins,
  graceful leaves, disconnects, protocol rejections, and cleanup failures.
- Domain events use `[AUTH]` and `[WORLDSERVER]` prefixes so local service
  consoles can be filtered independently from framework logs.
- Join-ticket values, service credentials, and secret world-session tokens are
  never logged.

## Shared Realtime Protocol

Status: Implemented

The local `com.shootermmo.game-protocol` Unity package and the
`GameProtocol.DotNet` build project compile the same source contract. Packet
decoding validates:

- Magic value and protocol version.
- Known message type.
- Maximum packet size of 1200 bytes.
- Bounded UTF-8 strings.
- Complete payloads with no trailing data.
- Finite normalized movement input and known movement flags.
- Valid snapshot chunk metadata and finite player state.
- The accepted join includes an explicit movement-simulation revision so an
  incompatible client fails before prediction starts.

Keeping Unity package contents separate from .NET `bin` and `obj` output avoids
duplicate Unity assembly imports.

## API Security And Error Handling

Status: Implemented

- Separate ASP.NET schemes and policies for account sessions and WorldServer
  identities.
- World identity binding on service operations.
- Central RFC Problem Details responses.
- Stable application error codes.
- `X-Correlation-ID` propagation.
- Explicit WorldServer mapping for AuthService timeout, network, invalid JSON,
  invalid payload, and service-authentication failures.

## Health And Configuration

Status: Implemented

- AuthService exposes separate `/health/live` and `/health/ready` routes.
- AuthService readiness performs PostgreSQL `select 1` and Redis `PING`.
- AuthService readiness returns HTTP 503 when a dependency is unavailable.
- WorldServer `--health-check-only` checks Redis, AuthService readiness, and UDP
  port availability without starting the long-running host.
- WorldServer loads and validates the configured collision manifest and chunks
  before either normal startup or the health-only path can continue.
- The WorldServer health command exits with code 0 on success and 1 on failure.
- Application-owned configuration fails fast at startup.
- Optional root `.env` loading supports local development.
- Compose exposes PostgreSQL and Redis only on `127.0.0.1`.

## Persistence And Migrations

Status: Implemented

AuthService owns ordered migrations for accounts, account sessions, characters,
worlds, join tickets, character world sessions, and migration metadata. Startup
migration execution is configurable and protected against concurrent service
startup. The advisory lock is acquired before the migration metadata table is
created, so two completely fresh service instances cannot race during database
bootstrap.

## Quality Coverage

Status: Implemented

- Unit tests cover validation, configuration, authentication handlers, API
  resilience, protocol encoding, malformed packet rejection, and session stores.
- A socket-level test starts the real LiteNetLib WorldServer transport and proves
  join, authoritative movement snapshot acknowledgement, and leave against a
  controlled AuthService response.
- Isolated PostgreSQL integration tests cover migration concurrency, auth and
  character flow, ticket concurrency, wrong-world protection, reconnect,
  heartbeat, cross-world exclusion, revocation, and idempotent release.
- Unity tests cover client state, input, authored assets, and runtime bootstrap.
- Collision tests cover deterministic baking, binary round trips, static and
  dynamic spatial queries, wall blocking, ramp traversal, and configured steps.

## Not Yet Implemented

- Interest management and per-client snapshot visibility.
- Triangle-mesh collision chunks for terrain, caves, and complex rock meshes.
- Replication of dynamic collision transforms to client prediction.
- General rigid-body simulation for physics-driven world objects.
- Durable WorldServer simulation state.
- Redis-backed domain state.
- Character deletion.
- Production deployment, telemetry, dashboards, and alerting.

See [Local Development](LOCAL_DEVELOPMENT.md) for commands and manual tests.

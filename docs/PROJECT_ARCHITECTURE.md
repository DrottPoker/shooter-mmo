# Project Architecture

Last updated: 2026-07-12

## Purpose

This document is the source of truth for the current project-wide architecture.
It describes component boundaries, data ownership, trust boundaries, and the
main runtime flows. Detailed Unity internals belong in
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md), while implemented
behavior belongs in the feature documents.

## System Context

```text
Unity Client
  | player bearer session
  v
AuthService --------------------> PostgreSQL
  ^                                   ^
  | service-authenticated HTTP        | authoritative leases and data
  |                                   |
WorldServer --------------------------+
  |
  +------------------------------> Redis

AuthService ---------------------> Redis
```

The Unity client treats AuthService as the public application API. WorldServer
is currently contacted through Development-only HTTP integration endpoints to
validate the end-to-end join flow. Future gameplay transport will replace this
development surface without changing AuthService ownership of identity and
access.

## Repository Components

### AuthService

`AuthService` is an ASP.NET Core minimal API and the authority for persistent
identity and world-access data. Its feature folders group account, character,
world registry, join ticket, and world-session logic. It applies ordered database
migrations when configured to do so.

AuthService owns:

- Accounts and password hashes.
- Revocable account sessions.
- Characters and character ownership.
- World registry records and heartbeat timestamps.
- Short-lived world join tickets.
- Global single-world leases for characters.

### WorldServer

`WorldServer` is an ASP.NET Core host for the local world process. It authenticates
to AuthService with a world identity and shared secret. It currently keeps its
local active-player simulation list in memory while the authoritative lease is
stored in PostgreSQL through AuthService.

WorldServer owns:

- The local process view of connected players.
- Periodic world registry heartbeat execution.
- Periodic active world-session heartbeat execution.
- Development-only join, list, and leave integration endpoints.
- WorldServer readiness and startup health checks.

### Shared

`Shared/ShooterMmo.Shared.csproj` contains infrastructure helpers shared by both
.NET services:

- Optional repository-root `.env` loading.
- Connection-string parsing and validation.
- Redis protocol health checks.
- Common dependency and service health models.
- Correlation id, Problem Details, and API pipeline helpers.

Shared does not own domain state or feature-specific business rules.

### Unity Client

`shooter-mmorpg-unity-client` is the player-facing Unity 6 project. It owns local
presentation, input, scene transitions, client session state, API serialization,
and the current local gameplay preview. See
[Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md).

### Tests

`Tests/ShooterMmo.Backend.Tests` references all three backend projects and
contains unit and isolated PostgreSQL integration tests. Unity tests are kept in
separate EditMode and PlayMode assemblies under `Assets/Tests`.

## Data Ownership

| Data | Authority | Current storage |
| --- | --- | --- |
| Account identity and credentials | AuthService | PostgreSQL |
| Account sessions | AuthService | PostgreSQL |
| Characters | AuthService | PostgreSQL |
| World registry and online status | AuthService | PostgreSQL |
| Join tickets | AuthService | PostgreSQL |
| Character world-session lease | AuthService | PostgreSQL |
| Locally connected players | WorldServer | Process memory |
| Unity selection and active session view | Unity client | Process memory |

Redis is required by current operational readiness but does not yet own domain
data.

## Trust Boundaries

### Player Authentication

Registration and login issue an opaque session token. Only a SHA-256 token hash
is stored. Protected player routes use the `AccountSession` ASP.NET authentication
scheme and authorization policy. Logout and revocation invalidate related join
tickets and leases.

### Service Authentication

WorldServer sends `X-World-Server-ID` and `X-World-Server-Secret` to protected
AuthService routes. The `WorldServer` authentication scheme validates both the
credential and the world identity. A server cannot consume, heartbeat, or release
resources belonging to another world.

### Development Surface

WorldServer `/debug/*` endpoints exist only in the Development environment. They
are isolated integration tooling and are not a production gameplay protocol or
a substitute for the future authoritative gameplay transport.

## Core Runtime Flows

### Account And Character Flow

1. Unity registers or logs in through AuthService.
2. AuthService returns a one-time opaque token and account-session metadata.
3. Unity sends the token as a bearer credential for protected operations.
4. Character operations validate the authenticated account and ownership.

### World Discovery

1. WorldServer authenticates and heartbeats its registry entry on startup and at
   a configured interval.
2. AuthService stores the database-generated heartbeat time.
3. World listing derives `isOnline` from the stored state, database clock, and
   configured heartbeat timeout.
4. A seeded or stale world remains offline until a fresh heartbeat exists.

### World Join And Reconnect

1. Unity requests a join ticket for an owned character and online world.
2. AuthService locks the character and invalidates any older unconsumed ticket.
3. Unity submits the short-lived ticket to its selected WorldServer.
4. WorldServer authenticates to AuthService and consumes the ticket for its own
   world id.
5. Ticket consumption and the authoritative lease claim occur in one PostgreSQL
   transaction.
6. A new join creates one character lease. A same-world reconnect keeps the
   world-session id and rotates the world-session token.
7. WorldServer stores the accepted player locally and heartbeats the exact lease
   generation.

PostgreSQL locks and constraints prevent concurrent tickets or cross-world active
leases for the same character.

### Leave And Expiry

1. Unity sends the exact character id and world-session id to WorldServer.
2. WorldServer verifies that the requested session still matches its local
   generation.
3. WorldServer releases the authoritative lease through AuthService and removes
   the local session.
4. Repeated release is safe. A delayed leave cannot remove a newer reconnect
   generation.
5. If graceful release is impossible, the lease becomes inactive after its
   heartbeat expires.

## HTTP Conventions

Both services use:

- RFC Problem Details for errors.
- Stable application error codes.
- `X-Correlation-ID` on every response and in error bodies.
- `Cache-Control: no-store` and `Pragma: no-cache` for token-bearing responses.
- Separate `/health/live` and `/health/ready` endpoints.
- Fail-fast validation for application-owned configuration.

WorldServer maps AuthService connection, timeout, invalid-response, and service
authentication failures into explicit dependency errors.

## Operational Architecture

Local PostgreSQL and Redis run through Docker Compose with host bindings limited
to `127.0.0.1`. Secrets live in the ignored repository-root `.env` file, with
required keys documented in `.env.example`.

AuthService readiness executes a real PostgreSQL query and Redis `PING`.
WorldServer readiness checks Redis and AuthService readiness. A missing mandatory
dependency produces HTTP 503. Liveness only confirms that the process can serve
requests.

See [Local Development](LOCAL_DEVELOPMENT.md) for commands and expected results.

## Architectural Direction

The current foundation deliberately keeps durable identity and access authority
in PostgreSQL. Future gameplay networking, simulation, persistence, and scaling
must preserve these boundaries unless an explicit architecture change updates
this document. Redis can later be introduced behind an abstraction when a real
state or coordination use case requires it.

Only the UI layer may be intentionally temporary. Every other architectural
component must be maintainable, testable, and suitable for extension from its
first implementation. A simplified first version is acceptable, but knowingly
disposable service, client, gameplay, persistence, or networking architecture is
not the project default.

# Service Features

Last updated: 2026-07-12

## Purpose

This document records implemented backend, service, persistence, security, and
operational features. It describes current behavior, not planned gameplay.

## Accounts And Authentication

Status: Implemented

- Register with email, username, and password.
- Login with username or email.
- BCrypt password hashing.
- Opaque database-backed session tokens with only SHA-256 token hashes stored.
- Authenticated current-account lookup.
- Current-session logout.
- Account-owned targeted session revocation.
- Five login or registration attempts per client IP per 60-second fixed window.
- No-store response headers wherever a session or access token is returned.

Revoking an account session also invalidates its unconsumed join tickets and
releases its active character world-session leases.

## Current HTTP Surface

| Method and route | Caller | Purpose |
| --- | --- | --- |
| `POST /api/accounts/register` | Public player client | Create account and session |
| `POST /api/accounts/login` | Public player client | Create account session |
| `GET /api/accounts/me` | Authenticated player | Read current account |
| `POST /api/accounts/logout` | Authenticated player | Revoke current session |
| `DELETE /api/accounts/sessions/{sessionId}` | Authenticated player | Revoke an owned session |
| `GET /api/characters` | Authenticated player | List owned characters |
| `POST /api/characters` | Authenticated player | Create character |
| `GET /api/worlds` | Public player client | List worlds and online state |
| `POST /api/worlds/{worldId}/join` | Authenticated player | Create join ticket |
| `POST /api/worlds/{worldId}/heartbeat` | Authenticated WorldServer | Refresh registry entry |
| `POST /api/world-join-tickets/consume` | Authenticated WorldServer | Consume ticket and claim lease |
| `POST /api/world-sessions/{id}/heartbeat` | Authenticated WorldServer | Extend exact lease generation |
| `POST /api/world-sessions/{id}/release` | Authenticated WorldServer | Release exact lease generation |
| `GET /health/live` | Operator or orchestrator | Check process liveness |
| `GET /health/ready` | Operator or orchestrator | Check mandatory dependencies |

WorldServer additionally exposes `POST /debug/join`, `GET /debug/sessions`, and
`DELETE /debug/sessions/{characterId}?worldSessionId={worldSessionId}` in
Development only.

## Characters

Status: Implemented

- Create and list characters owned by the authenticated account.
- Maximum of five characters per account.
- Globally unique names between 3 and 24 characters.
- Names support letters, numbers, spaces, hyphens, and underscores.

Character deletion and restoration are not implemented.

## World Registry

Status: Implemented

- Public world listing.
- Authenticated WorldServer registry heartbeat.
- Database-generated heartbeat timestamps.
- Timeout-derived online status.
- Seeded and stale worlds remain offline until a fresh heartbeat exists.
- Default 10-second heartbeat interval and 30-second online timeout.

World responses include connection metadata, ruleset, `lastHeartbeatAt`, and
`onlineUntil`.

## World Join Tickets

Status: Implemented

- Authenticated players request a short-lived ticket for an owned character.
- Ticket creation locks the character and invalidates older unconsumed tickets.
- Only one active unconsumed ticket can exist per character.
- WorldServer consumes a ticket through a service-authenticated endpoint.
- Wrong-world consumption is rejected without consuming the ticket.
- Ticket consumption and world-session claim share one PostgreSQL transaction.
- Current ticket lifetime is 30 seconds.

## Character World Sessions

Status: Implemented

- One active world-session lease per character across every world.
- Same-world reconnect preserves the world-session id and rotates the session
  token.
- WorldServer heartbeat extends the lease from the database clock.
- Explicit release is idempotent.
- Expired leases can be replaced.
- Exact world-session identity protects newer reconnect generations from delayed
  leave requests.
- Account-session revocation releases leases created by that session.

PostgreSQL constraints, transactions, character locks, and advisory migration
locks enforce the current consistency rules.

## WorldServer Local Sessions

Status: Implemented for development

- Successful ticket consumption creates an in-memory active-player session.
- A background service heartbeats each authoritative database lease.
- Graceful shutdown attempts to release active leases.
- Development-only endpoints allow local join, session listing, and exact-session
  leave testing.

The in-memory list is not persistent simulation state and is not a gameplay
networking implementation.

## API Security And Error Handling

Status: Implemented

- Separate ASP.NET authentication schemes and policies for player sessions and
  WorldServer identities.
- World identity binding on protected service operations.
- Development-only registration of `/debug/*` endpoints.
- Central RFC Problem Details responses.
- Stable application error codes.
- `X-Correlation-ID` propagation on every response.
- Explicit AuthService dependency mapping for timeout, network, invalid payload,
  malformed JSON, and service-authentication failures in WorldServer.

## Health And Configuration

Status: Implemented

- Separate `/health/live` and `/health/ready` routes in both services.
- AuthService readiness performs PostgreSQL `select 1` and Redis `PING`.
- WorldServer readiness checks Redis and AuthService readiness.
- Mandatory dependency failure returns HTTP 503 while liveness stays available.
- `--health-check-only` returns process exit code 0 on success and 1 on failure.
- Application-owned configuration is validated at startup and fails fast.
- Optional root `.env` loading supports local development.
- Compose exposes PostgreSQL and Redis only on `127.0.0.1`.

## Persistence And Migrations

Status: Implemented

AuthService owns ordered migrations for:

- `accounts`
- `account_sessions`
- `characters`
- `worlds`
- `world_join_tickets`
- `character_world_sessions`
- `schema_migrations`

Startup migration execution is configurable. Migration application is protected
against concurrent service startup.

## Quality Coverage

Status: Implemented

- Unit tests cover validation, token generation, configuration, connection
  parsing, authentication handlers, AuthService client failures, and local
  session behavior.
- Isolated PostgreSQL integration tests cover migration concurrency, auth and
  character flow, ticket concurrency, wrong-world protection, reconnect,
  heartbeat, cross-world exclusion, revocation, and idempotent release.
- GitHub Actions performs locked restore, formatting verification, Release build,
  PostgreSQL integration tests, and coverage collection.

See [Local Development](LOCAL_DEVELOPMENT.md) for commands and manual tests.

## Not Yet Implemented

- Production gameplay transport.
- Durable WorldServer simulation state.
- Redis-backed domain state.
- Character deletion.
- Production deployment and orchestration.
- Metrics, distributed tracing export, dashboards, and alerting.

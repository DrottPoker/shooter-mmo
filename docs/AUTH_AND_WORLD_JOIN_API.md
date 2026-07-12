# Auth And World Join API

Last updated: 2026-07-12

## Scope

This document describes the current MVP API flow for accounts, characters, and
world join tickets.

The current implementation uses database-backed session tokens instead of JWTs.
Sessions support logout and account-owned targeted revocation.
Join tickets and authoritative character world-session leases are also stored in
PostgreSQL. Ticket consumption and world-session claiming therefore share one
transaction. They can move behind a Redis-backed implementation later when scale
requires it.

## Database Startup

AuthService applies its first database migration on startup when this setting is
enabled:

```json
{
  "Database": {
    "RunMigrationsOnStartup": true
  }
}
```

The migration creates:

- `accounts`
- `account_sessions`
- `characters`
- `worlds`
- `world_join_tickets`
- `character_world_sessions`
- `schema_migrations`

It also seeds `local-world-1`.

The phase 2 migration links new join tickets and world-session leases to the
account session that issued them. Revoking that session invalidates its
unconsumed join tickets and releases its active world-session leases.

## Authentication

Register and login return a session token.

Authenticated endpoints require:

```text
Authorization: Bearer <sessionToken>
```

Session tokens are only returned once. AuthService stores a SHA-256 hash of the
token in PostgreSQL.

Protected account routes use the `AccountSession` ASP.NET authentication scheme
and policy. Protected service routes use the separate `WorldServer` scheme and
policy. Endpoint handlers consume authenticated claims instead of parsing bearer
headers manually.

Login and registration each allow five requests per client IP in a 60-second
fixed window. Exceeding the limit returns `429 Too Many Requests` with a
`Retry-After` header.

All token-bearing responses include:

```text
Cache-Control: no-store
Pragma: no-cache
```

## Endpoints

### Register Account

```http
POST /api/accounts/register
```

Request:

```json
{
  "email": "player@example.com",
  "username": "player_one",
  "password": "TestPass123!"
}
```

Response:

```json
{
  "accountId": "00000000-0000-0000-0000-000000000000",
  "username": "player_one",
  "sessionId": "00000000-0000-0000-0000-000000000000",
  "sessionToken": "token",
  "expiresAt": "2026-07-08T13:00:00Z"
}
```

### Login

```http
POST /api/accounts/login
```

Request:

```json
{
  "login": "player_one",
  "password": "TestPass123!"
}
```

`login` accepts either username or email.

### Current Account

```http
GET /api/accounts/me
```

Requires a bearer session token.

### Logout Current Session

```http
POST /api/accounts/logout
```

Requires a bearer session token and returns `204 No Content`. The current session
is revoked, its unconsumed join tickets are invalidated, and its active
world-session leases are released.

The phase 3 migration adds `worlds.last_heartbeat_at` and marks every seeded or
existing world offline. Online status is true only while the registry heartbeat
is newer than `WorldRegistry:HeartbeatTimeoutSeconds`.

### Revoke Account Session

```http
DELETE /api/accounts/sessions/{sessionId}
```

Requires a bearer session token. The target session is revoked only when it
belongs to the authenticated account. The operation is idempotent and returns
`204 No Content` without revealing whether another account owns the supplied id.

### List Characters

```http
GET /api/characters
```

Requires a bearer session token.

### Create Character

```http
POST /api/characters
```

Requires a bearer session token.

Request:

```json
{
  "name": "Hero One"
}
```

Rules:

- Maximum 5 characters per account.
- Character names are globally unique.
- Names must be 3 to 24 characters.
- Names can contain letters, numbers, spaces, hyphens, and underscores.

### List Worlds

```http
GET /api/worlds
```

This is public for now.

Each world response includes `lastHeartbeatAt` and `onlineUntil`. Both are null
before the first heartbeat. `isOnline` is computed from the database clock, the
stored heartbeat timestamp, the configured timeout, and the stored `is_online`
flag.

### Heartbeat World Registry Entry

```http
POST /api/worlds/{worldId}/heartbeat
```

Requires WorldServer service authentication. The authenticated world id must
match the route. A successful response contains the database-generated
`lastHeartbeatAt` and `onlineUntil` timestamps. WorldServer sends this heartbeat
immediately on startup and then every 10 seconds. The default online timeout is
30 seconds.

### Create World Join Ticket

```http
POST /api/worlds/{worldId}/join
```

Requires a bearer session token.

Request:

```json
{
  "characterId": "00000000-0000-0000-0000-000000000000"
}
```

Response:

```json
{
  "world": {
    "id": "local-world-1",
    "displayName": "Local World 1",
    "host": "127.0.0.1",
    "udpPort": 27015,
    "ruleSet": "mvp-open-risk",
    "isOnline": true,
    "lastHeartbeatAt": "2026-07-12T13:00:00Z",
    "onlineUntil": "2026-07-12T13:00:30Z"
  },
  "characterId": "00000000-0000-0000-0000-000000000000",
  "joinTicket": "ticket",
  "expiresAt": "2026-07-07T13:00:30Z",
  "isReconnect": false
}
```

The current ticket lifetime is 30 seconds.

Ticket creation locks the character row. Creating a new ticket invalidates any
older unconsumed ticket for that character. If the character already has an
active lease on the requested world, the response is a reconnect ticket. A join
request for a different world is rejected until the active lease is released or
expires.

### Consume World Join Ticket

```http
POST /api/world-join-tickets/consume
```

Request:

```json
{
  "ticket": "ticket",
  "worldId": "local-world-1"
}
```

Response:

```json
{
  "accountId": "00000000-0000-0000-0000-000000000000",
  "characterId": "00000000-0000-0000-0000-000000000000",
  "characterName": "Hero One",
  "worldId": "local-world-1",
  "worldSessionId": "00000000-0000-0000-0000-000000000000",
  "worldSessionToken": "token",
  "sessionExpiresAt": "2026-07-07T13:01:00Z",
  "isReconnect": false
}
```

AuthService validates `worldId` before consuming the ticket. Sending a ticket to
the wrong WorldServer returns a conflict without invalidating the ticket. A valid
consume atomically marks the ticket as consumed and creates or refreshes the one
active world-session lease for the character.

This endpoint requires WorldServer service authentication. WorldServer sends:

```text
X-World-Server-ID: local-world-1
X-World-Server-Secret: <shared secret>
```

The authenticated world id must match the request `worldId`. The same
authentication and world binding apply to heartbeat and release.

### Heartbeat World Session

```http
POST /api/world-sessions/{worldSessionId}/heartbeat
```

Request:

```json
{
  "worldId": "local-world-1",
  "sessionToken": "token"
}
```

WorldServer sends a heartbeat every 10 seconds. A successful heartbeat extends
the lease to 30 seconds from the database clock. Invalid, released, or expired
credentials are rejected.

### Release World Session

```http
POST /api/world-sessions/{worldSessionId}/release
```

The request uses the same credentials as heartbeat. Release is idempotent, so a
retry with the same valid credentials returns success after the first release.

## WorldServer Debug Endpoints

WorldServer exposes temporary HTTP debug endpoints on `http://localhost:5100`.
They are registered only when `ASPNETCORE_ENVIRONMENT=Development`. They return
`404 Not Found` in Production and other environments.

The Unity temporary client currently uses these endpoints to test the full
account, character, join ticket, and WorldServer validation flow from Play Mode.

### Service Liveness

```http
GET /health/live
```

Liveness checks only that the HTTP process can serve requests. It does not call
dependencies and returns HTTP `200` with status `live`.

### Service Readiness

```http
GET /health/ready
```

AuthService readiness executes `select 1` against PostgreSQL and sends the Redis
RESP `PING` command, requiring `PONG`. WorldServer readiness sends its own Redis
PING and requires AuthService readiness. Readiness returns HTTP `503` with status
`not_ready` when any mandatory dependency fails.

### Debug Join

```http
POST /debug/join
```

Request:

```json
{
  "joinTicket": "ticket"
}
```

WorldServer will:

- Call AuthService `POST /api/world-join-tickets/consume`.
- Reject expired, invalid, or already consumed tickets.
- Reject tickets for a different world without consuming them.
- Claim or reconnect the global PostgreSQL world-session lease.
- Store the successful session locally and heartbeat it while WorldServer runs.

### List Debug Sessions

```http
GET /debug/sessions
```

Returns the active in-memory sessions inside the local WorldServer.

### Remove Debug Session

```http
DELETE /debug/sessions/{characterId}?worldSessionId={worldSessionId}
```

Releases the authoritative PostgreSQL lease and removes the matching local
WorldServer session. The required world-session id prevents a delayed leave from
removing a newer reconnect generation. Repeating the delete after the local
session is gone returns success.

## Current Limitations

- Character deletion is not implemented.
- JWT auth is not implemented.
- Redis is running locally but is not used for auth or join tickets yet.
- WorldServer simulation state is still in-memory. The authoritative lease expires
  if a stopped WorldServer can no longer heartbeat it.

## Error Contract And Correlation

Both services return RFC Problem Details for application errors, authorization
failures, malformed requests, unhandled failures, and unmapped HTTP status codes.
Application errors include a stable `code` extension. Every response includes an
`X-Correlation-ID` header, and error bodies include the same value as
`correlationId`.

Example:

```json
{
  "type": "about:blank",
  "title": "Unauthorized",
  "status": 401,
  "detail": "A valid bearer session token is required.",
  "code": "invalid_session_token",
  "correlationId": "9aaad83514034f9c936f1c820d2df826"
}
```

WorldServer maps AuthService dependency failures as follows:

- Connection failure: `503 auth_service_unavailable`
- Timeout: `504 auth_service_timeout`
- Malformed JSON or invalid success payload: `502 invalid_auth_response`
- Invalid service credentials: `502 auth_service_authentication_failed`

## Unity Client Handling

The temporary Unity client reads endpoints and its request timeout from
`Assets/Resources/ShooterMmoClientConfig.asset`. API failures are represented by
a structured client error containing failure kind, HTTP status, stable API code,
message, and correlation id.

HTTP 401 clears account, character, world, and active world-session state before
loading `LoginMenu`. Character selection operations run sequentially. World join
creates the join ticket and validates it through WorldServer inside one coroutine.
Both WorldScene navigation buttons release the WorldServer session before loading
`CharacterSelect`.

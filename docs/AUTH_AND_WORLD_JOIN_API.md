# Auth And World Join API

Last updated: 2026-07-09

## Scope

This document describes the current MVP API flow for accounts, characters, and
world join tickets.

The current implementation uses database-backed session tokens instead of JWTs.
This keeps early development simple and makes sessions easy to revoke later.
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

## Authentication

Register and login return a session token.

Authenticated endpoints require:

```text
Authorization: Bearer <sessionToken>
```

Session tokens are only returned once. AuthService stores a SHA-256 hash of the
token in PostgreSQL.

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
    "isOnline": true
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

This endpoint is used by WorldServer validation. It has no service-to-service
authentication yet because there is only one local WorldServer in the MVP.

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
These endpoints are for local development until the real Unity and UDP join
handshake exists.

The Unity temporary client currently uses these endpoints to test the full
account, character, join ticket, and WorldServer validation flow from Play Mode.

### WorldServer Health

```http
GET /health
```

Checks Redis and AuthService reachability.

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
DELETE /debug/sessions/{characterId}
```

Releases the authoritative PostgreSQL lease and removes the local WorldServer
session. Repeating the delete after the local session is gone returns success.

## Current Limitations

- Character deletion is not implemented.
- JWT auth is not implemented.
- Service-to-service authentication is not implemented yet.
- Redis is running locally but is not used for auth or join tickets yet.
- WorldServer simulation state is still in-memory. The authoritative lease expires
  if a stopped WorldServer can no longer heartbeat it.

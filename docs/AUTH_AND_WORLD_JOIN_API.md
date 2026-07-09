# Auth And World Join API

Last updated: 2026-07-07

## Scope

This document describes the current MVP API flow for accounts, characters, and
world join tickets.

The current implementation uses database-backed session tokens instead of JWTs.
This keeps early development simple and makes sessions easy to revoke later.
Join tickets are also stored in PostgreSQL for now. They can move to Redis when
multiple live WorldServers need faster short-lived coordination.

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
  "expiresAt": "2026-07-07T13:00:30Z"
}
```

The current ticket lifetime is 30 seconds.

### Consume World Join Ticket

```http
POST /api/world-join-tickets/consume
```

Request:

```json
{
  "ticket": "ticket"
}
```

This endpoint is used by WorldServer validation. It has no service-to-service
authentication yet because there is only one local WorldServer in the MVP.

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
- Reject tickets for a different world.
- Store a successful join as an in-memory active player session.

### List Debug Sessions

```http
GET /debug/sessions
```

Returns the active in-memory sessions inside the local WorldServer.

### Remove Debug Session

```http
DELETE /debug/sessions/{characterId}
```

Removes one in-memory session from WorldServer. This exists only to make local
debugging easier.

## Current Limitations

- Character deletion is not implemented.
- JWT auth is not implemented.
- Active online character leases are not implemented yet.
- Redis is running locally but is not used for auth or join tickets yet.
- WorldServer active sessions are in-memory and are lost when WorldServer stops.

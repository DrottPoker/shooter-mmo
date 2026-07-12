# Local Development

Last updated: 2026-07-12

## Requirements

- .NET SDK 10.0 or newer.
- Docker Desktop.
- Unity Editor for the client project.

## Local Environment File

Create the ignored local environment file before running Compose or either
backend service:

```powershell
Copy-Item .env.example .env
```

Replace every `replace-with-...` placeholder in `.env`. AuthService and WorldServer
share `WORLD_SERVER_ID` and `WORLD_SERVER_SERVICE_SECRET`. Both services search
their content root and parent directories for `.env`. Process environment
variables and command-line values take precedence.

If the PostgreSQL Docker volume already exists, changing `POSTGRES_PASSWORD` does
not change the password stored inside PostgreSQL. Either keep the current local
password in both `.env` entries or update the database role interactively:

```powershell
docker exec -it shooter_mmo_postgres psql -U shooter_mmo -d shooter_mmo
```

Then run `\password shooter_mmo` inside `psql`. Deleting the Compose volume also
recreates the credentials, but permanently removes local database data.

## Repository Quality Checks

Restore locked dependencies and run the standard backend quality gate:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet test ShooterMmo.slnx --configuration Release --no-build
```

Expected result:

- Restore accepts every committed `packages.lock.json` file.
- Format reports no files that need changes.
- Build completes with zero warnings and zero errors.
- Unit tests pass.
- The PostgreSQL integration test is skipped unless its dedicated connection is
  configured.

## Isolated PostgreSQL Integration Tests

The integration test resets the target database's `public` schema. Always use the
isolated test Compose file and never point the test variable at a development,
staging, or production database.

Start the test database:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
```

Set the dedicated connection and run all backend tests:

```powershell
$testConnectionLine = Get-Content .env |
  Where-Object { $_ -like "SHOOTER_MMO_TEST_POSTGRES=*" } |
  Select-Object -First 1
$env:SHOOTER_MMO_TEST_POSTGRES = $testConnectionLine.Split("=", 2)[1]
dotnet test ShooterMmo.slnx --configuration Release
```

Expected result:

- All unit tests pass.
- PostgreSQL migration concurrency, ticket concurrency, wrong-world protection,
  reconnect, heartbeat, cross-world exclusion, and idempotent release tests pass
  instead of being skipped.

Clean up the isolated environment:

```powershell
docker compose -f docker-compose.test.yml down
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
```

## Local Infrastructure

Start PostgreSQL and Redis locally:

```powershell
docker compose up -d
```

The local services use these ports:

- PostgreSQL: `localhost:5432`
- Redis: `localhost:6379`
- AuthService: `http://localhost:5000`
- WorldServer HTTP debug: `http://localhost:5100`
- WorldServer UDP: `27015`

Local PostgreSQL credentials and WorldServer service credentials live only in
the ignored `.env` file. `.env.example` documents every required key without
placing active credentials in application settings or Compose YAML.

## Backend Services

Run AuthService:

```powershell
dotnet run --project AuthService
```

Check AuthService liveness and readiness:

```powershell
Invoke-RestMethod http://localhost:5000/health/live
Invoke-RestMethod http://localhost:5000/health/ready
```

Expected result: liveness reports `live`. Readiness reports `ready` only after a
real PostgreSQL `select 1` query and Redis `PING` both succeed.

Register a test account:

```powershell
$body = @{
  email = "player@example.com"
  username = "player_one"
  password = "TestPass123!"
} | ConvertTo-Json

$auth = Invoke-RestMethod http://localhost:5000/api/accounts/register `
  -Method Post `
  -Body $body `
  -ContentType "application/json"
```

Use the returned session token:

```powershell
$headers = @{ Authorization = "Bearer $($auth.sessionToken)" }
```

The response also contains `sessionId`. Token-bearing responses include
`Cache-Control: no-store` and `Pragma: no-cache`.

Logout the current session:

```powershell
Invoke-RestMethod http://localhost:5000/api/accounts/logout `
  -Method Post `
  -Headers $headers
```

Expected result: the endpoint returns `204 No Content`, and the same bearer token
returns `401 Unauthorized` on the next authenticated request.

An authenticated session can revoke another session owned by the same account:

```powershell
Invoke-RestMethod "http://localhost:5000/api/accounts/sessions/$sessionId" `
  -Method Delete `
  -Headers $headers
```

Expected result: the endpoint returns `204 No Content`. Any unconsumed join ticket
issued by the revoked session is invalidated, and any world-session lease owned by
that account session is released.

Create a character:

```powershell
$characterBody = @{ name = "Hero One" } | ConvertTo-Json

$character = Invoke-RestMethod http://localhost:5000/api/characters `
  -Method Post `
  -Headers $headers `
  -Body $characterBody `
  -ContentType "application/json"
```

Create a local world join ticket:

```powershell
$joinBody = @{ characterId = $character.id } | ConvertTo-Json

Invoke-RestMethod http://localhost:5000/api/worlds/local-world-1/join `
  -Method Post `
  -Headers $headers `
  -Body $joinBody `
  -ContentType "application/json"
```

Run WorldServer:

```powershell
dotnet run --project WorldServer
```

`dotnet run` uses the Development launch profile. The temporary `/debug/*`
endpoints are not registered in Production, Staging, or any other environment.

Run a one-time WorldServer startup health check:

```powershell
dotnet run --project WorldServer -- --health-check-only
```

Expected result: PostgreSQL is verified through AuthService readiness, Redis is
verified directly with `PING`, and the process exits with code `0`. Stop Redis or
AuthService and repeat to verify exit code `1`:

```powershell
$LASTEXITCODE
```

Check WorldServer liveness and readiness while both services are running:

```powershell
Invoke-RestMethod http://localhost:5100/health/live
Invoke-RestMethod http://localhost:5100/health/ready
```

Expected result: both return HTTP `200`. Readiness returns HTTP `503` if Redis or
AuthService readiness is unavailable, while liveness remains HTTP `200` as long
as the WorldServer process can serve requests.

World registry test:

1. Start AuthService without WorldServer and call `GET /api/worlds`.
2. Start WorldServer and call the endpoint again.
3. Stop WorldServer, wait longer than the configured 30-second timeout, and call
   the endpoint again.

Expected result: `local-world-1` is offline before the first heartbeat, online
while fresh heartbeats arrive, and offline after the heartbeat timeout.

Create a join ticket through AuthService, then validate it through WorldServer:

```powershell
$joinBody = @{ characterId = $character.id } | ConvertTo-Json

$join = Invoke-RestMethod http://localhost:5000/api/worlds/local-world-1/join `
  -Method Post `
  -Headers $headers `
  -Body $joinBody `
  -ContentType "application/json"

$debugJoinBody = @{ joinTicket = $join.joinTicket } | ConvertTo-Json

Invoke-RestMethod http://localhost:5100/debug/join `
  -Method Post `
  -Body $debugJoinBody `
  -ContentType "application/json"
```

List active debug sessions in WorldServer:

```powershell
Invoke-RestMethod http://localhost:5100/debug/sessions
```

The returned session includes `worldSessionId`, `sessionExpiresAt`, and
`isReconnect`. Wait at least 40 seconds and list the sessions again to verify that
WorldServer heartbeat keeps the 30-second database lease alive.

Reconnect test:

1. Join `local-world-1` from `CharacterSelect`.
2. Use `Back To Character Select` without leaving the world.
3. Join the same character and world again.

Expected result:

- The second join succeeds as a reconnect.
- `worldSessionId` remains unchanged.
- `isReconnect` is `true` after the second join.
- Only one active session exists for the character.

## Unity Temporary Client Flow

The Unity project includes temporary runtime UI for the current backend flow.
The UI is created automatically by a bootstrap script when each scene starts.

The Unity project also contains separate EditMode and PlayMode test assemblies.
Open `Window > General > Test Runner` and run both suites before delivering Unity
changes.

Expected result:

- EditMode validates API array parsing and client session cleanup.
- PlayMode validates that loading `LoginMenu` creates the persistent client
  bootstrap and runtime login panel.

Before using the Unity client, start these services:

```powershell
docker compose up -d
dotnet run --project AuthService
dotnet run --project WorldServer
```

Then open `shooter-mmorpg-unity-client` in Unity and start from:

```text
Assets/Scenes/LoginMenu.unity
```

Scene flow:

- `LoginMenu`
- `CharacterSelect`
- `WorldScene`

Expected result in `LoginMenu`:

- A `Login Menu` panel appears in the Game view.
- `Auth Service` is set to `http://localhost:5000`.
- `World Server` is set to `http://localhost:5100`.

Manual Unity test flow:

1. Change `Email` and `Username` to unique values.
2. Click `Register`.
3. Unity loads `CharacterSelect`.
4. Create a character if none exists.
5. Select a character.
6. Select `Local World 1`.
7. Click `Join Selected World`.
8. Unity loads `WorldScene`.

Expected result:

- `WorldScene` shows the selected character on `local-world-1`.
- A local placeholder player spawns in a simple safe city area.
- You can move with `WASD`, sprint with `Shift`, jump with `Space`, rotate the
  camera by holding right mouse button, and zoom with the mouse wheel.
- `Invoke-RestMethod http://localhost:5100/debug/sessions` shows the active
  session from WorldServer.
- `Leave World` removes the debug session and returns to `CharacterSelect`.

Direct movement-only test:

1. Open `Assets/Scenes/WorldScene.unity`.
2. Press Play.
3. Move the local placeholder player around the city and through the front gate.

Expected result:

- A safe city placeholder area, boundary marker, camera, and local player are
  created automatically.
- Movement works without starting the backend, but server session data only
  appears after the full login and world join flow.

## Current Foundation Scope

The current backend foundation includes:

- A root .NET solution.
- `AuthService` as a minimal ASP.NET Core service.
- `WorldServer` as a minimal ASP.NET Core debug host for world join validation.
- `ShooterMmo.Shared` for shared foundation helpers.
- Local PostgreSQL and Redis through Docker Compose.
- Protocol-level PostgreSQL query and Redis PING readiness checks without a Redis
  client dependency.
- Auth persistence through Npgsql and Dapper.
- Password hashing through BCrypt.
- Database-backed account registration and login.
- Database-backed session tokens.
- ASP.NET authentication handlers and authorization policies for account sessions
  and WorldServer service identities.
- Login and registration rate limiting, logout, and targeted session revocation.
- Problem Details errors, `X-Correlation-ID`, and no-store token responses.
- Resilient WorldServer handling of AuthService timeout, network, and invalid
  response failures.
- Heartbeat-driven world registry status, split liveness/readiness, fail-fast
  configuration, and complete command-line health checks.
- Character creation and listing.
- Local world listing and join tickets.
- Character-locked ticket creation with one active ticket per character.
- Transactional ticket consumption and PostgreSQL world-session claims.
- Global single-world enforcement per character.
- WorldServer reconnect, heartbeat, graceful release, and lease expiry handling.
- In-memory WorldServer simulation sessions backed by authoritative database
  leases.
- Unity temporary UI for login, character selection, world ticket creation, and
  WorldServer debug join.
- Unity local WorldScene gameplay preview with runtime environment creation,
  placeholder player spawn, local movement, jump, sprint, and third-person
  camera control.
- Backend unit tests and an isolated PostgreSQL integration test.
- Unity EditMode and PlayMode smoke tests in separate test assemblies.
- A GitHub Actions backend quality gate with locked restore, format, build,
  PostgreSQL integration testing, and coverage collection.

Persistent gameplay systems, inventory, combat, and Unity networking are not
implemented yet.

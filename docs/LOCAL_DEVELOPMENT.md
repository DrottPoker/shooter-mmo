# Local Development

Last updated: 2026-07-12

## Requirements

- .NET SDK 10.0 or newer.
- Docker Desktop.
- Unity Editor for the client project.

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
$env:SHOOTER_MMO_TEST_POSTGRES = "Host=localhost;Port=55432;Database=shooter_mmo_tests;Username=shooter_mmo_tests;Password=shooter_mmo_tests_password"
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

The local PostgreSQL database is configured as:

- Database: `shooter_mmo`
- User: `shooter_mmo`
- Password: `shooter_mmo_dev_password`

These credentials are for local development only.

The local WorldServer service identity also uses a committed development-only
secret. AuthService reads it from
`ServiceAuthentication:WorldServers:local-world-1`, and WorldServer reads it from
`WorldServer:AuthServiceSecret`. In any non-local environment, override both with
the same strong secret:

```powershell
$env:ServiceAuthentication__WorldServers__local-world-1 = "replace-with-a-long-random-secret"
$env:WORLD_SERVER_SERVICE_SECRET = "replace-with-a-long-random-secret"
```

## Backend Services

Run AuthService:

```powershell
dotnet run --project AuthService
```

Check AuthService health:

```powershell
Invoke-RestMethod http://localhost:5000/health
```

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

Check WorldServer health while AuthService and WorldServer are both running:

```powershell
Invoke-RestMethod http://localhost:5100/health
```

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
- TCP-based dependency health checks without external NuGet packages.
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

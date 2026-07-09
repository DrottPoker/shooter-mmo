# Local Development

Last updated: 2026-07-08

## Requirements

- .NET SDK 10.0 or newer.
- Docker Desktop.
- Unity Editor for the client project.

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

## Unity Temporary Client Flow

The Unity project includes temporary runtime UI for the current backend flow.
The UI is created automatically by a bootstrap script when each scene starts.

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
- Character creation and listing.
- Local world listing and join tickets.
- WorldServer join ticket validation through AuthService.
- In-memory active player sessions inside WorldServer.
- Unity temporary UI for login, character selection, world ticket creation, and
  WorldServer debug join.
- Unity local WorldScene gameplay preview with runtime environment creation,
  placeholder player spawn, local movement, jump, sprint, and third-person
  camera control.

Persistent gameplay systems, inventory, combat, and Unity networking are not
implemented yet.

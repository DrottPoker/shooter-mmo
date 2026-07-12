# Shooter MMO

Shooter MMO is an early classless, profession-driven open-world MMORPG with a
modern third-person shooter direction. The current repository contains the
account, character, world join, local WorldServer, and Unity client
foundation for the first playable MVP.

## Current Foundation

- ASP.NET Core AuthService backed by PostgreSQL.
- .NET WorldServer with Development-only HTTP join validation.
- Transactional PostgreSQL world-session leases with reconnect, heartbeat, and
  idempotent release.
- Revocable account sessions, authentication rate limits, and service-authenticated
  WorldServer calls.
- RFC Problem Details responses with correlation identifiers and dependency failure
  mapping.
- Heartbeat-driven world registry status with timeout-based offline detection.
- Split liveness and protocol-level dependency readiness checks.
- Shared .NET networking and health helpers.
- Unity 6 client scenes for login, character selection, and a local world preview.
- Timeout-aware Unity API handling, serialized UI operations, and automatic 401
  recovery.
- Input Action movement and collision-safe third-person camera controls.
- PostgreSQL and Redis development infrastructure through Docker Compose.

Inventory, combat, persistent world simulation, and UDP gameplay networking are
intentionally deferred until the foundation is stable.

## Requirements

- .NET SDK 10.0.301 or a compatible .NET 10 feature band.
- Docker Desktop with Linux containers.
- Unity Editor 6000.5.2f1.

## Quick Start

Create the ignored local environment file and replace its placeholder secrets:

```powershell
Copy-Item .env.example .env
```

Start local infrastructure:

```powershell
docker compose up -d --wait
```

Run the backend services in separate terminals:

```powershell
dotnet run --project AuthService
dotnet run --project WorldServer
```

AuthService and WorldServer load the repository-root `.env` file for local
development. Real environment variables and command-line configuration override
the file. Never commit `.env`.

Open `shooter-mmorpg-unity-client` in Unity and enter Play Mode from
`Assets/Scenes/LoginMenu.unity`.

See [Project Overview](docs/PROJECT_OVERVIEW.md) for a short orientation and
[Local Development](docs/LOCAL_DEVELOPMENT.md) for the complete manual flow.

## Quality Checks

Run the standard backend checks from the repository root:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
dotnet test ShooterMmo.slnx --configuration Release --no-build
```

The PostgreSQL integration test is skipped unless a dedicated test connection is
configured. Start the isolated test database and run all tests with:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:SHOOTER_MMO_TEST_POSTGRES = (Get-Content .env | Where-Object { $_ -like "SHOOTER_MMO_TEST_POSTGRES=*" }).Split("=", 2)[1]
dotnet test ShooterMmo.slnx --configuration Release
docker compose -f docker-compose.test.yml down
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
```

The integration test resets the `public` schema. Never point
`SHOOTER_MMO_TEST_POSTGRES` at a development, staging, or production database.

Run Unity tests from `Window > General > Test Runner`:

- EditMode validates client serialization and session state.
- PlayMode validates that the login scene receives its runtime UI bootstrap.

## Repository Layout

- `AuthService`: account, session, character, world, and join ticket API.
- `WorldServer`: Development-only world join validation and active local sessions.
- `Shared`: shared backend health and networking helpers.
- `Tests`: backend unit and PostgreSQL integration tests.
- `shooter-mmorpg-unity-client`: Unity client project and Unity tests.
- `docs`: project overview, architecture, features, product scope, and local
  development documentation.

The complete documentation map is available in
[docs/README.md](docs/README.md).

## Development Rules

Repository-specific contribution rules are defined in [AGENTS.md](AGENTS.md).
Behavior, configuration, architecture, and user-facing workflow changes must be
documented and manually testable.

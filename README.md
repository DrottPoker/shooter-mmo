# Shooter MMO

Shooter MMO is an early classless, profession-driven open-world MMORPG with a
modern third-person shooter direction. The current repository contains the
account, character, world join, local WorldServer, and temporary Unity client
foundation for the first playable MVP.

## Current Foundation

- ASP.NET Core AuthService backed by PostgreSQL.
- .NET WorldServer with temporary HTTP join validation.
- Transactional PostgreSQL world-session leases with reconnect, heartbeat, and
  idempotent release.
- Shared .NET networking and health helpers.
- Unity 6 client scenes for login, character selection, and a local world preview.
- PostgreSQL and Redis development infrastructure through Docker Compose.

Inventory, combat, persistent world simulation, and UDP gameplay networking are
intentionally deferred until the foundation is stable.

## Requirements

- .NET SDK 10.0.301 or a compatible .NET 10 feature band.
- Docker Desktop with Linux containers.
- Unity Editor 6000.5.2f1.

## Quick Start

Start local infrastructure:

```powershell
docker compose up -d --wait
```

Run the backend services in separate terminals:

```powershell
dotnet run --project AuthService
dotnet run --project WorldServer
```

Open `shooter-mmorpg-unity-client` in Unity and enter Play Mode from
`Assets/Scenes/LoginMenu.unity`.

See [Local Development](docs/LOCAL_DEVELOPMENT.md) for the complete manual flow.

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
$env:SHOOTER_MMO_TEST_POSTGRES = "Host=localhost;Port=55432;Database=shooter_mmo_tests;Username=shooter_mmo_tests;Password=shooter_mmo_tests_password"
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
- `WorldServer`: temporary world join validation and active local sessions.
- `Shared`: shared backend health and networking helpers.
- `Tests`: backend unit and PostgreSQL integration tests.
- `shooter-mmorpg-unity-client`: Unity client project and Unity tests.
- `docs`: MVP, architecture, API, and local development documentation.

## Development Rules

Repository-specific contribution rules are defined in [AGENTS.md](AGENTS.md).
Behavior, configuration, architecture, and user-facing workflow changes must be
documented and manually testable.

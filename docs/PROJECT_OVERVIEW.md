# Project Overview

Last updated: 2026-07-12

## What Shooter MMO Is

Shooter MMO is an early classless, profession-driven open-world MMORPG with a
modern third-person shooter direction. The project is currently establishing a
stable account, character, world access, local service, and Unity client
foundation before persistent gameplay systems are expanded.

The current playable path is intentionally small:

1. A player registers or logs in.
2. The player creates and selects a character.
3. The client lists available worlds and requests a short-lived join ticket.
4. WorldServer validates the ticket and claims the character's world session.
5. Unity loads a local world preview with basic movement and camera controls.
6. Leaving the world releases the server session before returning to character
   selection.

## Main Components

- **Unity client** provides temporary menus, structured cross-scene session state,
  API access, scene flow, local player controls, and world preview.
- **AuthService** owns accounts, authentication sessions, characters, the world
  registry, join tickets, and authoritative character world-session leases.
- **WorldServer** validates joins, maintains active local simulation sessions,
  heartbeats authoritative leases, and exposes Development-only HTTP
  debug endpoints.
- **PostgreSQL** is the durable source of truth for account, character, ticket,
  registry, and world-session data.
- **Redis** is currently an operational dependency used by readiness checks. It
  does not yet own gameplay or authentication state.
- **Shared** contains reusable .NET configuration, networking, HTTP pipeline, and
  health-check helpers.

## Current State

The foundation currently supports:

- Database-backed registration, login, logout, and session revocation.
- Character creation and listing.
- Heartbeat-based world discovery and online status.
- Secure, transactional world join tickets and single-world character leases.
- Reconnect, heartbeat, expiry, and safe release behavior.
- Split liveness and readiness health checks.
- A timeout-aware Unity API client with structured errors and 401 recovery.
- A three-scene client flow with temporary UI and a local third-person movement
  foundation.
- Backend unit and PostgreSQL integration tests plus Unity EditMode and PlayMode
  smoke tests.

## Intentionally Deferred

The following areas are not implemented yet:

- Real gameplay networking and authoritative movement simulation.
- Combat, weapons, abilities, damage, death, and respawning.
- Inventory, equipment, loot, crafting, gathering, professions, and economy.
- Persistent world simulation, NPCs, quests, social systems, and guilds.
- Production deployment, horizontal scaling, telemetry, and live operations.

## Engineering Standard

Only the current UI is intentionally temporary while the custom interface is
being designed. Service boundaries, client state, networking contracts, scene
flow, gameplay code, input, camera systems, persistence, and tooling must be
built as maintainable foundations from the start.

Early content can be visually simple, but its implementation must still have a
clear owner, testable behavior, and a safe extension path. Disposable shortcuts
outside the UI require an explicit decision and documentation before they are
introduced.

## Where To Read Next

- [Project Architecture](PROJECT_ARCHITECTURE.md) explains the complete system.
- [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md) explains the client
  in detail.
- [Service Features](SERVICE_FEATURES.md) records implemented server behavior.
- [Game Features](GAME_FEATURES.md) records implemented gameplay behavior.
- [Local Development](LOCAL_DEVELOPMENT.md) explains how to run and test it.
- [MVP Specification](MVP_SPEC.md) defines the current product scope.

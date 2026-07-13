# Project Overview

Last updated: 2026-07-13

## What Shooter MMO Is

Shooter MMO is an early classless, profession-driven open-world MMORPG with a
modern third-person shooter direction. The project is currently establishing a
stable account, character, world access, local service, and Unity client
foundation before persistent gameplay systems are expanded.

The current playable path is intentionally small:

1. A player registers or logs in.
2. The player creates and selects a character.
3. The client lists available worlds and requests a short-lived join ticket.
4. Unity connects to WorldServer over LiteNetLib UDP and sends the ticket.
5. WorldServer validates the ticket and claims the character's world session.
6. Unity loads WorldScene and predicts movement while WorldServer owns the
   authoritative player state.
7. Leaving the world releases the server session before returning to character
   selection.

## Main Components

- **Unity client** provides temporary menus, structured cross-scene session state,
  API access, scene flow, local player controls, and world preview.
- **AuthService** owns accounts, authentication sessions, characters, the world
  registry, join tickets, and authoritative character world-session leases.
- **WorldServer** validates joins, maintains active local simulation sessions,
  heartbeats authoritative leases, and owns the headless realtime UDP transport.
- **PostgreSQL** is the durable source of truth for account, character, ticket,
  registry, and world-session data.
- **Redis** is currently an operational dependency used by readiness checks. It
  does not yet own gameplay or authentication state.
- **Shared** contains framework-neutral .NET configuration and health helpers.
- **Shared.Http** contains AuthService-only ASP.NET pipeline behavior.
- **GameProtocol** is the versioned binary contract shared by Unity and
  WorldServer.
- **GameSimulation** is the fixed-step movement implementation compiled from the
  same source for WorldServer and Unity prediction.
- **WorldData** contains the neutral collision authoring and checksummed chunks
  consumed by WorldServer and Unity.

## Current State

The foundation currently supports:

- Database-backed registration, login, logout, and single-active-account-session
  enforcement. A later login replaces the earlier client session.
- Character creation and listing.
- Readiness-gated world registration with advertised UDP endpoints,
  compatibility metadata, timeout-based status, and graceful offline updates.
- Secure, transactional world join tickets and single-world character leases.
- Reconnect, heartbeat, expiry, and safe release behavior.
- A persistent LiteNetLib UDP client and headless WorldServer join and leave
  handshake.
- A server-owned world entity registry with nonzero network entity ids,
  one-to-one connection ownership, and reliable spawn and despawn lifecycle.
- Sequenced movement input, a fixed 30 Hz authoritative server simulation, 15 Hz
  world snapshots, stale-input neutralization, local reconciliation, and
  stall-recovering remote interpolation.
- A Unity-side entity cache that survives scene loading and creates remote views
  only under a dedicated presentation root.
- Versioned, chunked test-map collision shared by WorldServer and Unity
  prediction, with authoritative capsule movement across walls, ramps, steps,
  and cover.
- Split liveness and readiness health checks.
- A timeout-aware Unity API client with structured errors and 401 recovery.
- A three-scene client flow with temporary UI and a server-authoritative
  third-person movement replication foundation.
- Backend unit and PostgreSQL integration tests plus Unity EditMode and PlayMode
  smoke tests.

## Intentionally Deferred

The following areas are not implemented yet:

- Terrain and cave triangle-mesh collision beyond the current oriented-box test
  map format.
- Dynamic collision transform replication and general rigid-body simulation.
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

# Game Features

Last updated: 2026-07-12

## Purpose

This document records implemented player-facing game features and how they work.
It is deliberately separate from service capabilities and the broader product
design. Features are added here only when working code exists.

## Account Entry Flow

Status: UI prototype on implemented account flow

The temporary LoginMenu UI allows a player to register or log in. A successful
request stores the account session in the persistent Unity client state and loads
CharacterSelect. Duplicate submissions are blocked while a request is active.

This is functional development UI, not final art or UX.

## Character Selection

Status: UI prototype on implemented character flow

The temporary CharacterSelect UI supports:

- Loading the account's characters.
- Creating a character.
- Selecting one character for world entry.
- Loading and selecting an online world.
- Requesting and validating a world join in one sequential operation.
- Logging out before returning to LoginMenu.

Character appearance, customization, deletion, progression, and statistics are
not implemented.

## World Entry And Exit

Status: Foundation implemented with temporary UI

A selected character can enter an online local world through the authenticated
join-ticket flow. WorldScene displays the selected character, world, and current
session metadata.

Both WorldScene exit actions use the same leave flow. The active server session
is released before returning to CharacterSelect. HTTP 401 clears stale client
state and returns the player to LoginMenu.

## Local World Preview

Status: Initial content on implemented gameplay foundation

WorldScene creates an initial code-driven safe-city test area with a local player,
spawn point, boundary marker, front gate, camera target, and third-person camera.
It exists to test scene flow and controls while the real world is still being
designed.

The preview has no persistent world simulation and no remote players.

## Player Movement

Status: Local foundation implemented

- Move with WASD or arrow keys.
- Sprint with Shift.
- Jump with Space.
- Gamepad supports left stick, left-stick press, and south button.
- Movement uses Unity Input Actions.

Movement is client-local and is not validated or replicated by WorldServer.

## Third-Person Camera

Status: Local foundation implemented

- Hold the right mouse button to orbit.
- Use the mouse wheel to zoom.
- Camera focus follows a single player CameraTarget height.
- Spherecast collision moves the camera in front of walls and restores its
  desired distance after the obstruction clears.
- The collision filter ignores the local player hierarchy.

## Planned Feature Categories

These categories are defined by the project direction but are not implemented.
They remain in the MVP specification until working behavior is available:

- Authoritative multiplayer movement and replication.
- Shooter combat, weapons, damage, death, and respawning.
- Inventory, equipment, item stats, and loot.
- Gathering, crafting, professions, and player economy.
- NPCs, enemies, quests, events, and world activities.
- Character progression and long-term persistence.
- Social, grouping, guild, chat, and trading systems.
- Final UI, audio, visual effects, animation, and accessibility.

## Feature Documentation Template

When a game feature is implemented, document it here with:

1. A clear status such as Foundation, MVP, or Production-ready. Use Prototype
   only for temporary UI presentation.
2. The player-visible behavior.
3. Inputs, rules, and important edge cases.
4. Whether the behavior is local, server-authoritative, or persistent.
5. Known limitations.
6. A link to manual test steps in [Local Development](LOCAL_DEVELOPMENT.md).

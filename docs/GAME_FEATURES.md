# Game Features

Last updated: 2026-07-13

## Purpose

This document records implemented player-facing game features and how they work.
It is deliberately separate from service capabilities and the broader product
design. Features are added here only when working code exists.

## Account Entry Flow

Status: UI prototype on implemented account flow

The temporary LoginMenu UI allows a player to register or log in. A successful
request stores the account session in the persistent Unity client state and loads
CharacterSelect. Duplicate submissions are blocked while a request is active.
Only one client session may remain active for an account. A successful login on
a second client replaces the first client, disconnects its active world peer if
needed, clears its local account state, and returns it to LoginMenu with a
temporary Unity Console explanation.

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
join-ticket flow. The client connects to the selected host and UDP port with
LiteNetLib, sends the ticket through the shared versioned protocol, and loads
WorldScene only after WorldServer accepts the session. WorldScene displays the
selected character, world, session metadata, and current UDP connection state.

Leave sends the exact world-session id over the active peer and waits for server
acknowledgement before returning to CharacterSelect. Unexpected disconnects
clear world state and return an authenticated player to CharacterSelect. HTTP
401 from AuthService still clears account state and returns the player to
LoginMenu.

## World Preview

Status: Initial content on implemented network gameplay foundation

WorldScene contains a 30 by 30 meter scene-authored test map, a LocalPlayer
prefab instance, an explicit spawn point, and a configured third-person camera.
Runtime code validates and connects these authored objects but does not generate
them. The map includes static boundaries, a slope, three step heights, cover, and
a dedicated camera-collision wall.

The world has no persistent simulation yet. During authenticated play, other
connected characters are represented by an authored RemotePlayer prefab and
rendered from interpolated WorldServer snapshots. WorldServer assigns each live
player a network entity id. Reliable spawn and despawn messages decide when a
remote player exists, while unreliable snapshots update only its movement.
Remote instances are kept under WorldScene's separate
`EntityPresentationRoot`, outside the authored LocalPlayer hierarchy.

## Player Movement

Status: Server-authoritative movement and test-map collision implemented

- Move with WASD.
- Sprint with Shift.
- Jump with Space.
- Sprint can only begin while grounded. A sprint that began on the ground can
  continue through a jump while Shift remains held.
- Releasing Shift in the air ends sprint, and pressing it again cannot restart
  sprint until the player reaches the ground.
- Camera-relative movement follows the current mouse-controlled view.
- While aiming, the player faces the camera direction so lateral movement
  behaves as shooter strafing.
- Aim cancels sprint and blocks both sprint and jump until Aim is released. The
  shared simulation enforces this rule for prediction and WorldServer authority.
- Movement uses a PlayerInput-owned Unity Input Actions asset.
- The local player predicts each fixed input tick immediately. A separate local
  presentation state interpolates those predictions at the render frame rate so
  the player and camera do not move in 30 Hz steps.
- WorldServer owns the accepted position, velocity, facing, grounded state, and
  sprint state.
- WorldServer resolves the player capsule against the baked ground, boundaries,
  ramp, steps, cover, and camera test wall.
- Walkable slopes up to the configured 45 degree limit hold a grounded character
  in place when movement input stops. Steeper surfaces are not treated as ground.
- The simulation capsule uses a slope-aware support height to avoid penetration
  correction along the slope. Presentation removes only that geometric offset,
  preserving render-frame interpolation instead of replacing it with the latest
  surface height.
- Grounded transitions over configured steps and small drops use an additional
  100 ms vertical presentation blend. Jumping, airborne movement, steep surfaces,
  teleports, and large corrections bypass this blend.
- Authoritative snapshots acknowledge processed input sequences. The client
  replays remaining input and smooths small corrections.
- Remote players advance on an adaptive frame-rate render clock through a
  snapshot buffer approximately 100 ms behind server time. Bounded clock
  correction restores that delay after a network stall instead of accumulating
  permanent latency.
- WorldServer neutralizes movement and action state after 500 ms without a
  newer input sequence, then resumes from the next valid input.

Gamepad bindings and player-configurable rebinding are not implemented yet.

Movement speed, gravity, terminal fall speed, jump, facing, capsule dimensions,
slope and step rules, authored collision, and world bounds are validated and
simulated by WorldServer.
The active UDP connection carries sequenced input and periodic movement
snapshots in addition to session control.
The character root is the shared logical ground point for players and NPCs.
`CharacterBody` configures the CharacterController contact envelope around that
point, including skin width, without moving the presentation hierarchy. Visual
assets use a feet-at-zero convention below PlayerVisual, and spawn placement
aligns the character root directly to the spawn point. Direct scene preview uses
normal gravity and CharacterController collision without forced per-frame ground
snapping. Authenticated play keeps simulation at the shared fixed tick rate and
renders an interpolated presentation state at the same root convention.

The current test map is baked into versioned collision chunks shared by the
server and client prediction. Authenticated movement therefore collides with all
12 current BoxColliders. WorldServer sends the authoritative collision revision
when the character joins, and the client refuses to predict against a different
revision. Direct WorldScene preview still uses Unity CharacterController for a
fast offline authoring check, but it is not the network authority.

Complex terrain meshes, caves, moving platforms, dynamic doors, and rigid-body
objects are not gameplay features yet. Dynamic server collision already has a
spatial registry, while transform replication and non-box collision formats are
later slices.

## Third-Person Camera

Status: Local foundation implemented

- Mouse movement controls the camera continuously while gameplay input is
  captured.
- Hold the right mouse button to enter the current aim input state.
- F1 releases the cursor for temporary debug UI interaction. F1 captures it again.
- Normal framing uses a right-shoulder offset so the player does not block the
  center of the view.
- The LocalPlayer prefab owns `LocalPlayerCamera`, so other scene cameras can be
  added without becoming the gameplay camera by accident.
- Normal framing uses an explicit vertical offset and a closer follow distance
  to keep the character lower and left of center in a modern shooter composition.
- Aim smoothly tightens the right-shoulder framing and changes camera FOV from
  60 to 50 degrees.
- Camera distance is fixed. Mouse-wheel zoom is not available.
- Camera focus follows a single player CameraTarget height.
- Vertical look supports a wider range from 50 degrees upward to 75 degrees
  downward.
- Spherecast collision moves the camera in front of walls and restores its
  desired distance after the obstruction clears.
- The collision filter ignores the local player hierarchy.

## Crosshair And World Debug HUD

Status: Gameplay contract implemented with temporary UI presentation

- An unequipped player sees a small white dot at screen center.
- Crosshair definitions support dot and cross shapes, size, thickness, gap,
  color, and runtime spread.
- Future weapon equipment can replace the active definition without changing
  camera or input code.
- The temporary World Debug panel is compact and anchored to the bottom-left
  corner.
- F2 hides or restores the World Debug panel.
- During an authenticated world session, World Debug shows WorldServer as the
  movement authority, the latest server tick, and the configured tick and
  snapshot rates.
- The crosshair is hidden while F1 has released the cursor.

## Planned Feature Categories

These categories are defined by the project direction but are not implemented.
They remain in the MVP specification until working behavior is available:

- Interest management and dynamic collision transform replication.
- Terrain and cave collision beyond the current oriented-box format.
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

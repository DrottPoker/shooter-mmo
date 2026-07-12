# Unity Client Architecture

Last updated: 2026-07-12

## Purpose

This document is the source of truth for the current Unity client architecture.
It describes runtime responsibilities, dependencies, state, scene flow, API
handling, and local gameplay controls.

## Project Boundary

The Unity project lives in `shooter-mmorpg-unity-client` and targets Unity
6000.5.2f1. Runtime code is compiled into `ShooterMmo.Runtime`. EditMode and
PlayMode tests use separate assemblies.

The current runtime UI is intentionally temporary while the custom UI is being
designed. The client bootstrap, state ownership, API layer, scene lifecycle,
input, camera, and gameplay foundations are long-term systems and must be built
to production-quality structural standards from the start.

The first world environment uses simple code-driven content so scene flow and
controls remain testable. Simple content does not make its supporting gameplay
architecture disposable.

## Runtime Structure

```text
ShooterMmoClientBootstrap
  +-- ShooterMmoClientSession
  +-- ShooterMmoApiClient
  +-- ShooterMmoClientConfig
  +-- scene lifecycle recovery

Scene panel
  +-- ClientOperationState
  +-- API coroutine flow
  +-- ClientSessionRecovery

WorldSceneGameplayBootstrap
  +-- LocalPlayerController
  +-- ThirdPersonCameraController
  +-- initial test environment
```

## Persistent Client Bootstrap

`ShooterMmoClientBootstrap` creates the persistent runtime root and survives scene
changes. It initializes the session and API client from configuration, observes
scene transitions, and performs fallback release when an active WorldScene is
left outside the normal panel flow.

The fallback release is bound to the exact world-session identity. Completion of
an older request cannot clear a newer local reconnect state.

## Configuration

`ShooterMmoClientConfig` is a ScriptableObject loaded from
`Assets/Resources/ShooterMmoClientConfig.asset`. It currently contains:

- AuthService base URL.
- WorldServer base URL.
- Request timeout in seconds.

The runtime login panel displays these values but does not edit them. Different
environments should use build-specific configuration assets or a future build
configuration pipeline.

## Client Session State

`ShooterMmoClientSession` stores the current in-memory client view:

- AuthService and WorldServer endpoints.
- Account and bearer session details.
- Selected character and world.
- Active world-session id and lease metadata.

This state is a client cache, not an authority. AuthService and WorldServer remain
authoritative. HTTP 401 clears all local session state before loading LoginMenu.

## API Layer

`ShooterMmoApiClient` wraps UnityWebRequest in coroutines. It is responsible for:

- Request serialization and response deserialization.
- Bearer authentication headers.
- Configured request timeouts.
- Validation of expected response payloads.
- Structured mapping of HTTP, timeout, network, and invalid-response failures.

`ShooterMmoApiError` carries the failure kind, HTTP status, stable server code,
message, and correlation id. Panels display a safe message rather than raw JSON.
Array responses are validated through `JsonArrayUtility` before they reach UI
state.

## Scene And UI Flow

The build flow is:

```text
LoginMenu -> CharacterSelect -> WorldScene
     ^              ^               |
     +--------------+---------------+
```

### LoginMenu

`LoginMenuPanel` creates the temporary login UI, runs registration or login, and
loads CharacterSelect after successful authentication.

### CharacterSelect

`CharacterSelectPanel` loads characters and worlds sequentially. It supports
character creation, selection, refresh, logout, and world join. Join ticket
creation and WorldServer validation run inside one coroutine so the operation
cannot be partially overlapped by another click.

### WorldScene

`WorldScenePanel` displays active session information. Both navigation actions
use the same leave coroutine, which releases the exact active WorldServer session
before loading CharacterSelect. Unauthorized responses clear the session and
load LoginMenu.

## Operation Serialization

Each panel uses `ClientOperationState` instead of a boolean busy flag. Only one
named operation can own a panel at a time. Relevant controls are disabled until
the operation completes, preventing duplicate clicks and overlapping refresh,
join, leave, or authentication requests.

## Unauthorized Recovery

`ClientSessionRecovery` centralizes HTTP 401 handling:

1. Clear account, selection, and active world-session state.
2. Load LoginMenu.
3. Stop the failed panel flow from continuing with stale data.

Logout is different from recovery. A normal Back to Login action first asks
AuthService to revoke the active account session, then clears local state.

## Local Gameplay Preview

`WorldSceneGameplayBootstrap` creates the initial safe-city test environment, local
player, camera target, and third-person camera when the scene starts.

`LocalPlayerController` uses programmatically defined Input Actions for movement,
sprint, and jump. Current bindings support keyboard, mouse, and gamepad.

`ThirdPersonCameraController` uses Input Actions for orbit and zoom. Camera focus
uses the single CameraTarget position. A non-allocating spherecast moves the
camera in front of obstructions while filtering the local player hierarchy.

This preview is entirely local. It does not send movement to WorldServer and is
not authoritative multiplayer gameplay.

## Test Architecture

EditMode tests cover client session cleanup, operation serialization, structured
Problem Details parsing, configuration loading, Input Action lifecycle, and
invalid array rejection.

PlayMode tests verify that loading LoginMenu creates the persistent client
bootstrap and runtime login panel.

Manual flows and expected results are documented in
[Local Development](LOCAL_DEVELOPMENT.md).

## Extension Rules

- Keep HTTP serialization and failure mapping inside the API layer.
- Keep persistent cross-scene state in the client session, not scene panels.
- Keep one operation owner per panel until a more explicit navigation state
  machine replaces it.
- Route every WorldScene exit through exact-session leave or fallback release.
- Add player-facing behavior to [Game Features](GAME_FEATURES.md) when it becomes
  implemented.
- Update this document when client ownership, scene flow, networking, input, or
  persistent runtime structure changes.
- Temporary implementation shortcuts are permitted only inside the replaceable
  UI presentation layer. UI-independent state and behavior must not be embedded
  in temporary panels.
- Every delivered Unity change must explicitly state whether manual Editor work
  is required. Required Inspector, scene, asset, package, input, or build-setting
  steps must be listed with expected results.

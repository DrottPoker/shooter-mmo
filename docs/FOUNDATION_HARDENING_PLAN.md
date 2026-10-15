# Foundation Hardening Plan

Last updated: 2026-10-15

Status: Active. This document tracks defects found in a full audit of the
existing foundation before new gameplay work starts. It is a work plan, not a
description of implemented behavior. Each item moves to `Fixed` only together
with a regression test or a documented manual verification.

## Purpose

The repository was built in many phases by several coding agents. Before the
combat and movement direction in
[Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md) is implemented, the
foundation must be verified to work as documented. This plan records the
verified baseline, every audit finding, the decisions that need the product
owner, and the order in which defects are fixed.

## Baseline Verification

Run on 2026-10-14 with .NET SDK 10.0.302, Docker 29.6.1, and Unity 6000.5.4f1.

| Check | Result |
| --- | --- |
| Locked restore and dependency policy | Passed |
| `dotnet format --verify-no-changes` | Passed |
| Release build | Passed, 0 warnings, 0 errors |
| Item catalog, collision, and world actor verification | Passed for both Development Worlds |
| Backend tests with PostgreSQL and Redis integration | 458 of 458 passed |
| Unity EditMode tests | 103 of 103 passed |
| Unity PlayMode tests | 2 of 2 passed |
| End-to-end login, character select, shard join, and spawn in Development World 2 | Passed |

Setup fixes made during the baseline:

- The Redis integration test was always skipped because no test Redis existed
  locally or in CI and `SHOOTER_MMO_TEST_REDIS` was undocumented. A Redis
  service now runs in `docker-compose.test.yml` on `127.0.0.1:56380` and in the
  CI backend job.
- Documentation and the CI Unity runner label named Unity `6000.5.2f1` while
  the project uses `6000.5.4f1`. They now match the project.
- `AGENTS.md` had been emptied in commit `d73fafe` while the README still
  pointed to it. Its previous rules were restored, and `CLAUDE.md` now imports
  them.

All tests passing does not mean the foundation is correct. Most findings below
are race conditions, failure paths, and cross-component contracts that the
current tests do not exercise.

## Progress

As of 2026-10-15 every finding, F01 to F74, and decisions D1 to D3 are done.
F74 was found by the F73 movement golden test during the P2 pass and fixed in
its own commit. The final verification after the P2 pass passed: the local
mirror of the CI backend gate, with dependency policy, locked restore, format,
Release build, item catalog, collision, and actor verification, and 526 of 526
backend tests in Release including PostgreSQL and Redis integration; the shared
simulation tests on Linux in the .NET SDK container; 111 Unity EditMode and 2
PlayMode tests; and an end-to-end login, join, graceful leave, and rejoin in
Development World 2 on URP with every actor presented by its registry prefab
and no console warnings or errors.

## Priority Levels

- **P0:** data loss, permanent lockout of a player or entity, a crash of a
  shared process, or a broken security boundary. Fixed first.
- **P1:** a realistic correctness, scalability, or security problem with a
  narrower trigger, or tooling that can corrupt content.
- **P2:** low-impact defects, latent issues, and documentation drift.

Confidence is `confirmed` when the failure path was traced in the code and
`plausible` when it depends on database or runtime behavior that still needs a
reproduction.

## P0 Findings

| Id | Area | Defect | Location | Confidence | Status |
| --- | --- | --- | --- | --- | --- |
| F01 | AuthService sessions | Revoking a session id that belongs to another account still releases that account's simulation sessions and consumes its join tickets. | `AuthService/Auth/SessionService.cs` `RevokeAsync` | confirmed | Fixed |
| F02 | SimulationWorker sessions | A session whose release fails after a disconnect, or whose join finalization is rejected, stays in the local store and the heartbeat service renews its lease forever. The account is locked out of other characters and shards and capacity leaks. Contradicts the documented lease expiry fallback. | `SimulationWorker/Realtime/RealtimeSimulationService.cs` `ReleaseDisconnectedSessionAsync`, `SimulationWorker/Sessions/SimulationSessionHeartbeatService.cs` | confirmed | Fixed |
| F03 | SimulationWorker reconnect | A same-session reconnect rotates the token before the join completes. Cleanup of the old peer then deletes the shared entity and carry state, which are keyed only by session id. | `RealtimeSimulationService.cs`, `SimulationWorker/Entities/SimulationEntityRegistry.cs`, `SimulationWorker/Items/CarryStateStore.cs` | confirmed | Fixed |
| F04 | SimulationWorker loop | Any exception on the simulation loop stops the whole host. AuthService error codes and messages are forwarded unchecked into length-limited protocol fields, so one malformed error response disconnects every player. | `RealtimeSimulationService.cs` `ExecuteAsync`, `SimulationWorker/Auth/AuthServiceClient.cs` | confirmed | Fixed |
| F05 | Shared carry state | The design allows retained weight above the 140 percent hard cap after death, and AuthService persists it, but `PlayerCarryState`, protocol validation, and the worker auth client reject that state. The character can never join again. | `GameSimulation/Runtime/PlayerCarryState.cs`, `GameProtocol/Runtime/RealtimeProtocol.cs` `IsValidCarryState`, `AuthServiceClient.cs` | confirmed | Fixed |
| F06 | Item operations | Rejections are stored durably under deterministic system operation ids. One rejected live Mob loot claim makes that loot unclaimable for everyone. One lock timeout during corpse or Recovery expiry makes it never expire, and 64 such entries block the whole expiry batch. | `AuthService/Items/ItemTransactionService.cs`, `ItemTransactionService.MobCorpse.cs`, `CorpseService.cs`, `ItemOperationsMaintenanceService.cs` | confirmed | Fixed |
| F07 | Item operations | Client-chosen and server-derived operation ids share one global key. A player can pre-claim a derived expiry id and block that corpse or Recovery delivery from ever expiring. | `ItemTransactionService.cs` `ClaimOperationAsync` | confirmed | Fixed |
| F08 | Unity realtime client | In the `Leaving` state, in-flight reliable messages such as spawns, actor state, and operation results are treated as protocol failures, so a graceful leave becomes a disconnect. It happens every time a corpse view is open. | `shooter-mmorpg-unity-client/Assets/Scripts/Networking/RealtimeSimulationClient.cs` | confirmed | Fixed |
| F09 | Unity client state | The inventory operation journal, corpse state, and world interaction state are not reset on a graceful leave, `Abort`, or a 401. After a rejoin, inventory, NPC, and corpse actions stay blocked, and corpse presence is stale after a worker restart. | `Assets/Scripts/Items/InventoryClientController.cs`, `CorpseClientController.cs`, `CorpseClientModels.cs`, `Assets/Scripts/WorldActors/WorldInteractionClientController.cs` | confirmed | Fixed |
| F10 | Unity prediction | The client input sequence restarts at zero on a reconnect while the server keeps the last received sequence for the reused entity, so all new input is ignored and the player is frozen. | `Assets/Scripts/Gameplay/LocalPlayerController.cs`, `SimulationEntityRegistry.cs` | plausible | Fixed |

## P1 Findings

| Id | Area | Defect | Location | Confidence | Status |
| --- | --- | --- | --- | --- | --- |
| F11 | Worker registry | The local registration lease starts when the heartbeat response arrives, so it outlives the database lease by the request latency and lock wait. This widens the split-brain window. | `SimulationWorker/Registry/SimulationWorkerHeartbeatService.cs`, `SimulationWorkerRegistrationLease.cs` | confirmed | Fixed |
| F12 | Worker sessions | Session lease expiry is an absolute database timestamp compared with the worker wall clock. Clock skew kicks every player or keeps an expired session simulated. | `SimulationWorker/Sessions/ActiveSimulationSessionStore.cs` | confirmed | Fixed |
| F13 | Worker registry | Runtime supersession is ordered by the worker-reported `started_at`. A replacement host with a slower clock is fenced and stops itself on every restart. Review follow-up: a runtime that never owned the worker id now waits for the previous lease to expire instead of stopping. | `AuthService/Simulation/SimulationWorkerRegistryService.cs` | confirmed | Fixed |
| F14 | Live Mob loot | Loot claims send the join-time item-state revision, so every claim after the first item change fails. Latent until Mob deaths are produced. | `SimulationWorker/Corpses/LiveMobCorpseInteractionService.cs` | confirmed | Fixed |
| F15 | Auth rate limiting | The distributed limiter matches request paths case-sensitively while routing is case-insensitive, so `/api/accounts/LOGIN` bypasses it. | `AuthService/Redis/RedisAuthenticationRateLimitMiddleware.cs` | confirmed | Fixed |
| F16 | Auth login | Unknown logins skip BCrypt and answer faster, and registration returns distinct email and username conflict codes, which allows account enumeration. Login timing is fixed. Registration conflicts stay explicit until an email verification flow exists, bounded by its rate limit. | `AuthService/Auth/AccountService.cs` | confirmed | Fixed |
| F17 | Auth registration | Email validation accepts display-name forms such as `x <victim@example.com>`, which bypasses email uniqueness. | `AuthService/Auth/AccountValidation.cs` | confirmed | Fixed |
| F18 | Auth login | Login keeps a pooled database connection open during the BCrypt verify, so a login burst exhausts the pool for every other request. | `AccountService.cs` | confirmed | Fixed |
| F19 | Item bootstrap | Startup takes one advisory lock per account inside one transaction, which fails with "out of shared memory" at several thousand accounts. | `AuthService/Items/CharacterItemStateBootstrapper.cs` | confirmed | Fixed |
| F20 | Item performance | The carried-weight recompute and the death source query filter with OR across joined tables and there is no index on `equipped_character_id`, so each commit likely scans all items. | `AuthService/Items/ItemTransactionContext.cs`, `ItemTransactionService.Death.cs` | plausible | Fixed |
| F21 | Worker admission | Admission checks only the connection key and global peer count. One host can fill every slot with peers that never join, and reconnects reset per-peer quotas. | `RealtimeSimulationService.cs` | plausible | Fixed |
| F22 | Worker transport | Reliable control sends have no per-peer queue limit, so a client that never acknowledges grows the outgoing queue without bound. | `RealtimeSimulationService.cs` | plausible | Fixed |
| F23 | Auth locking | Logout and revoke lock the session row without the account row, unlike login, join, and consume. The inconsistent lock order can deadlock. | `SessionService.cs` | plausible | Fixed |
| F24 | Item locking | The insurance charge upgrades the character row lock to `for update`, which conflicts with foreign key checks of concurrent operations. The resulting deadlock error is unmapped and returns 500. | `ItemTransactionService.cs` | confirmed | Fixed |
| F25 | Item idempotency | Live-session checks run before the idempotency lookup, so a retry of a committed worker operation is reported as failed after the session ends. | `ItemTransactionService.cs` | confirmed | Fixed |
| F26 | Worker item results | AuthService timeouts are reported to the client as definite rejections without a refresh, although the operation may have committed. | `SimulationWorker/Items/SimulationItemInteractionService.cs` | confirmed | Fixed |
| F27 | Unity Editor tools | The Development World 2 environment rebuild opens the scene without asking to save, which silently discards unsaved scene edits. | `Assets/Editor/DevelopmentWorld2EnvironmentBuilder.cs` | confirmed | Fixed |
| F28 | Unity Editor tools | The collision bake writes the authoring file, manifest, and chunks in place without temporary files or rollback. A partial write leaves checksums that fail every join. | `Assets/Editor/WorldCollisionBaker.cs` | confirmed | Fixed |
| F29 | Unity realtime client | `FailCurrentOperation` lets later packets overwrite the first error, so the player sees a misleading join failure reason. | `RealtimeSimulationClient.cs` | confirmed | Fixed |
| F30 | Build configuration | The root `Directory.Build.props` also applies to the Unity-generated project files, and their shared NuGet lock file is tracked. | `Directory.Build.props`, `shooter-mmorpg-unity-client/packages.lock.json` | confirmed | Fixed |
| F31 | Dependencies | Dependabot can bump LiteNetLib for the server and bot tools while the Unity package stays, and nothing verifies that the versions match. | `.github/dependabot.yml`, `Tools/Verify-DependencyPolicy.ps1` | confirmed | Fixed |
| F32 | Unity test script | `Run-UnityTests.ps1` always prefers the Unity CLI when it exists and ignores an explicit editor path or `UNITY_EDITOR_PATH`. | `Tools/Run-UnityTests.ps1` | confirmed | Fixed |
| F33 | Configuration | `.env` is searched from the working directory and three parents, not from the content root as documented, so starting a service from its output folder fails. | `Shared/Configuration/DotEnvConfigurationExtensions.cs` | confirmed | Fixed |
| F34 | Configuration | The `.env.example` placeholder worker secret passes validation, unlike the bot and fixture secrets. | `AuthService/Config/AuthServiceConfig.cs`, `SimulationWorker/Config/SimulationWorkerConfig.cs` | confirmed | Fixed |
| F35 | Configuration | Configuration reloads at runtime bypass startup validation. A zero rate-limit window divides by zero, and a very large session lifetime overflows. | `AuthService/Program.cs`, `SessionService.cs`, `RedisAuthenticationRateLimiter.cs` | confirmed | Fixed |

## P2 Findings

| Id | Area | Defect | Location | Status |
| --- | --- | --- | --- | --- |
| F36 | Auth | Passwords longer than 72 UTF-8 bytes are accepted, but BCrypt ignores everything after 72 bytes. | `AccountValidation.cs` | Fixed |
| F37 | Auth | `X-Correlation-ID` is removed from exception-mapped error responses. | `AuthService/Http/ApiPipelineExtensions.cs` | Fixed |
| F38 | Configuration | A malformed `.env` line is copied into the startup exception, `export` prefixes become part of the key, and inline comments become part of the value. | `DotEnvConfigurationExtensions.cs` | Fixed |
| F39 | Auth | `Retry-After` is inaccurate in both rate limiters. | `RedisAuthenticationRateLimiter.cs`, `AuthService/Program.cs` | Fixed |
| F40 | Auth | A client abort is logged as an unhandled error and mapped to 500. | `ApiPipelineExtensions.cs` | Fixed |
| F41 | Placement | Capacity counts the account's own superseded ticket and double-counts reconnect tickets. | `AuthService/Simulation/ShardService.cs` | Fixed |
| F42 | Placement | Ticket and lease expiry use the transaction start time instead of the time after placement locks, which contradicts the documentation. | `ShardService.cs`, `SimulationWorkerRegistryService.cs` | Fixed |
| F43 | World rebind | A timed-out owner's tickets and sessions are released before the rebind blockers are counted, which contradicts the architecture document. The behavior is intended and is now documented. | `SimulationWorkerRegistryService.cs` | Fixed |
| F44 | Worker auth | Worker secrets are looked up case-insensitively, so case variants of one worker id authenticate as separate workers. | `AuthService/Auth/SimulationWorkerAuthenticationHandler.cs` | Fixed |
| F45 | Placement | Placement locks the worker row but not the assignment row, which can bind a ticket to a stale assignment during a worker restart. | `ShardService.cs` | Fixed |
| F46 | Development bots | Player placement ignores in-memory bot reservations, so bots and players can over-reserve a worker. Review follow-up: player placement now counts in-memory bot sessions and tickets. | `ShardService.cs`, `DevelopmentSimulationBotAuthority.cs` | Fixed |
| F47 | Recovery | Recovery reads return every claimed delivery forever. | `AuthService/Items/ItemQueryService.cs` | Fixed |
| F48 | Quest items | Abandoning quest items held in Recovery leaves an empty, unclaimable delivery. | `ItemTransactionService.cs` | Fixed |
| F49 | Corpses | An internal corpse move can place the corpse Bag in a general slot, which breaks the Bag invariants. | `ItemTransactionService.CorpseLoot.cs` | Fixed |
| F50 | Item bootstrap | The startup weight recompute can write a stale weight during a rolling deploy. | `CharacterItemStateBootstrapper.cs` | Fixed |
| F51 | Secure Container | Any account can set its own Secure Container tier. Latent until a second tier exists. Account actors may now only select the base tier. | `AuthService/Items/ItemEndpoints.cs`, `ItemTransactionService.cs` | Fixed |
| F52 | Death events | Death idempotency records are deleted with closed corpses after 30 days, which allows a late replay to partition items again. Every death request now requires the expected character revision, which the partition always advances. | `ItemOperationsMaintenanceService.cs`, `ItemTransactionService.Death.cs` | Fixed |
| F53 | Movement | The motor derives substeps from the largest axis, which allows tunneling with configurations the validator accepts. The shipped configuration is safe. | `GameSimulation/Runtime/Collision/KinematicCharacterMotor.cs` | Fixed |
| F54 | Content limits | Item catalog slot ids and tags allow 128 characters while the protocol allows 64, and there is no load-time compatibility check. | `WorldData/Runtime/ItemDomain/ItemCatalogCompiler.cs` | Fixed |
| F55 | Collision | Very large chunk coordinates overflow differently in .NET and Unity, which can hang the compiler or server. | `GameSimulation/Runtime/Collision/CollisionWorld.cs` | Fixed |
| F56 | Worker corpses | A stale completion cleans up by peer id, which LiteNetLib reuses, so it can close a new peer's corpse view. Disconnect now releases every reservation of the peer, and a stale completion only touches a context that still owns its id. No deterministic socket reproduction exists, because it depends on LiteNetLib id reuse timing. | `RealtimeSimulationService.cs` | Fixed |
| F57 | Worker corpses | Opening a corpse can send a delta instead of a snapshot to the new viewer, which contradicts the documentation. | `RealtimeSimulationService.cs` | Fixed |
| F58 | Worker sessions | Session invalidation entries for detached sessions are never removed. | `ActiveSimulationSessionStore.cs` | Fixed |
| F59 | World actors | Despawn tombstones are never pruned although the documentation calls them bounded. | `SimulationWorker/WorldActors/WorldActorStore.cs` | Fixed |
| F60 | Actor presentation | The fallback tint is also applied to authored prefabs. | `Assets/Scripts/WorldActors/WorldActorPresentationController.cs` | Fixed |
| F61 | Actor presentation | The presentation registry asset has never been created, so every actor uses the fallback and the warning wrongly says the archetype is not mapped. The registry now maps every authored archetype to a placeholder prefab. | `WorldActorPresentationController.cs` | Fixed |
| F62 | Item grant window | Cancelling a grant kills only the `dotnet run` host, not the AuthService child process. | `Assets/Editor/InventoryItemGrantWindow.cs` | Fixed |
| F63 | Unity API client | Web requests in coroutines are not disposed when the owning object is destroyed. | `Assets/Scripts/Api/ShooterMmoApiClient.cs` | Fixed |
| F64 | Unity static state | Static session and scene catalog caches have no reset for disabled domain reload. Harmless while domain reload stays on. | `Assets/Scripts/ShooterMmoClientSession.cs` | Fixed |
| F65 | Collision bake | The bake requires one World per scene although the scene catalog allows shared scenes. | `WorldCollisionBaker.cs` | Fixed |
| F66 | Repository | A Unity crash-recovery scene is tracked in `Assets/_Recovery`. | `shooter-mmorpg-unity-client/Assets/_Recovery/` | Fixed |
| F67 | Unity settings | The project-wide Input System actions setting points to a missing asset. | `ProjectSettings/EditorBuildSettings.asset` | Fixed |
| F68 | Unity packages | Template packages without any use are installed: AI Navigation, Collab Proxy, Multiplayer Center, Timeline, and Visual Scripting. | `Packages/manifest.json` | Fixed |
| F69 | Test build | AuthService and SimulationWorker copy conflicting `appsettings.json` files into the test output. Latent: tests read configuration from repository paths. Fixed with the public `CopyConflictingTransitiveContent` property and empty placeholders that the test project owns. | `Tests/ShooterMmo.Backend.Tests/ShooterMmo.Backend.Tests.csproj` | Fixed |
| F70 | Build | The .NET GameProtocol adapter lists source files explicitly, so a new protocol file is silently missing from the server. | `Shared/DotNet/GameProtocol/ShooterMmo.GameProtocol.csproj` | Fixed |
| F71 | Repository | `.gitattributes` does not mark collision chunk `.bytes` files as binary. | `.gitattributes` | Fixed |
| F72 | Documentation | Drift: protocol version `11` and `13` instead of `14`, the .NET SDK requirement, the missing Development Worlds index entry, the outdated one-time scene authoring steps, the PowerShell 7 requirement, the password hasher and revocation reason, the topology and rebind description, and the release fallback description. | `docs/` | Fixed |
| F73 | Test coverage | Missing cross-runtime movement determinism test, hand-maintained simulation revision, collision revision not recomputed on load, and no check that catalog content fits protocol limits. A golden test now pins the revision with a movement fingerprint, loaders recompute the collision revision, and a test keeps catalog limits within protocol limits. The same scenario now runs in Unity EditMode with the same pinned fingerprint. | `GameSimulation/`, `WorldData/` | Fixed |
| F74 | Movement determinism | Unity's Mono runtime evaluates compound float expressions in double precision, while the worker on .NET rounds every operation to float. The shared movement scenario diverges by one ULP at its first diagonal input, so Unity prediction, including Mono Standalone builds, drifts from the authoritative worker and relies on reconciliation. Found by the F73 golden test. Every float intermediate is now rounded with an explicit cast, which is a no-op on .NET, and Unity reproduces the worker fingerprint. | `GameSimulation/Runtime/` | Fixed |

## Decisions

Decided by the product owner on 2026-10-14.

| Id | Question | Decision | Context |
| --- | --- | --- | --- |
| D1 | May offline account routes move items into and out of the bank and Recovery Storage from anywhere? | No. Bank and Recovery Storage stay city services as the inventory design says. Offline account routes must reject bank and Recovery sources and destinations. Implemented: the offline Recovery claim route was removed. | AuthService currently allows offline moves from any location, so a player can log out in the field, take bank items, and log back in. |
| D2 | Should the client use URP or the Built-in Render Pipeline? | URP. Commit URP pipeline assets and convert the materials. Implemented with PC and Mobile pipeline assets; all 14 materials use URP/Lit. | Graphics and quality settings reference URP assets that were never committed, so the project silently renders with Built-in and all materials were switched to the Standard shader. Built-in is being phased out in Unity 6. |
| D3 | May the unused template packages be removed? | Yes. See F68. Implemented. | They can be added again when a feature needs them. |

## Fix Order

1. P0 findings, each with a regression test that fails before the fix.
2. Decisions D1 to D3 and their findings.
3. P1 findings, grouped by component so each group is verified once.
4. P2 findings and documentation drift.

After each group the full backend quality gate, the integration suite, both
Unity suites, and an end-to-end join run.

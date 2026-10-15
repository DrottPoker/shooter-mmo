# GameService Modules

Last updated: 2026-10-15

## Status

Accepted. GameService is being turned into a compiler-enforced modular monolith
in small steps that each keep every test green. The guard tests described below
are in place. The modules still share one project until the extraction steps
land; [Implementation Status](#implementation-status) lists what is done.

## Context

GameService is one ASP.NET Core process that owns accounts, characters, Shard
placement, and durable items. Its code grew as one project of about 26,000
lines. Folders looked like modules, but nothing stopped one folder from reading,
writing, or locking another folder's tables, and several folders depended on
each other in cycles:

| Id | Cycle | Fix |
| --- | --- | --- |
| C1 | Auth and Redis use each other | Both join the Accounts module. Only the Redis connection moves to Platform. |
| C2 | Characters and Items | Characters publishes a character-created SPI that Items implements. |
| C3 | Placement and Items | Placement publishes session-start, carry-state, and world-rebind SPIs that Items implements. Items consumes Placement authority contracts. |
| C4 | Accounts and Placement | Accounts publishes a session lifecycle SPI that Placement implements. |
| C5 | Database initialization calls Items seeding | The migration runner moves to Platform. Seeding goes through `IDatabaseSeeder`. The host decides the order. |
| C6 | One shared configuration object | Each module validates and owns its own options. The host aggregates them. |

## Decision

GameService stays one process and one deployable, with the same assembly and
process name `Mmorpg.GameService`. Its code is split into six projects whose
references enforce one dependency order:

```text
Platform <- Accounts <- Characters <- Placement <- Items <- host
```

| Project | Owns |
| --- | --- |
| `Mmorpg.GameService.Platform` | Problem Details results, the HTTP pipeline, token hashing, the transaction scope, the migration runner, the Redis connection, process-wide options, and the module contract. No domain model. |
| `Mmorpg.GameService.Accounts` | Accounts, account sessions, account and worker authentication, authorization policies, login and registration rate limits, and Redis session acceleration. |
| `Mmorpg.GameService.Characters` | Characters, ownership, and character currency. |
| `Mmorpg.GameService.Placement` | World definitions, Fleets, Nodes, Shards, the worker registry, SimulationAssignments, join tickets, simulation session leases, and Development simulation bots. It decides where a character plays right now. |
| `Mmorpg.GameService.Items` | The item catalog, item state, item transactions, corpses, death partition, Recovery, policies, item operations maintenance, and the item development tools. |
| `Mmorpg.GameService` (host) | `Program.cs`, the configuration loader, database composition, the frozen baseline migrations, and health routes. |

"Placement" avoids collisions with World content (`Mmorpg.WorldData`) and with
the simulation process names. "Platform" holds infrastructure plumbing only, so
it never becomes a shared domain kernel.

A module exposes only its module class, its options, and its `Contracts`
namespace. Everything else is internal. A module that is lower in the order
publishes an SPI when a higher module must react inside the same transaction.

## Table Ownership

| Owner | Tables and objects |
| --- | --- |
| Platform | `schema_migrations`; advisory lock `7104202607071600` |
| Accounts | `accounts`, `account_sessions` |
| Characters | `characters`, including `currency` |
| Placement | `world_definitions`, `fleets`, `simulation_nodes`, `shards`, `simulation_workers`, `simulation_assignments`, `simulation_join_tickets`, `character_simulation_sessions` |
| Items | every item, container, catalog, Recovery, operation, corpse, and death table, the `validate_character_item_state_containers` trigger function, `bootstrap_character_item_state`, the catalog advisory lock `7104202607151230`, and the per-account Secure Container entitlement advisory lock |

Foreign keys between modules follow the dependency order only: Characters to
Accounts, Placement to Accounts and Characters, and Items to Accounts,
Characters, and Placement (`corpses.shard_id`). Cascading deletes therefore
cross module boundaries, but no delete path exists for accounts or characters
today. A future delete flow must go through the owners' contracts.

## Access Rules

- **R1.** A module never writes another module's table directly. It calls a
  method on the owner's contract, which runs in the caller's
  `PostgresTransactionScope`.
- **R2.** A module takes row locks (`FOR UPDATE`, `FOR NO KEY UPDATE`,
  `FOR SHARE`) on another module's rows only through owner contract methods.
- **R3.** A module reads another module's tables inside its own SQL only through
  SQL fragments that the owner publishes in its `*Sql` contract class. Fragment
  arguments are trusted SQL expressions, never user values, and fragments use
  unique internal aliases. This keeps single-statement atomicity and hot-path
  round trips while the owner keeps control of its SQL text.
- **R4.** The frozen migration history is exempt: the nine baseline migrations,
  the module migrations, and the `bootstrap_character_item_state` function that
  reads `characters`.
- **R5.** Foreign keys only point down the dependency order.

Contract implementations always use the caller's connection and transaction.
They never commit, roll back, or open their own connection. That keeps
RepeatableRead snapshots and the transaction-local `statement_timeout` and
`lock_timeout` that item transactions set.

## Lock Order

Every transaction that spans modules takes locks in this order:

```text
accounts -> account_sessions -> characters (ascending id)
  -> Placement (shards -> join tickets -> simulation sessions -> workers -> assignments)
  -> Items (entitlement advisory lock -> character_item_states ascending -> containers and items)
```

Ticket consume locks `character_item_states` before
`character_simulation_sessions`. That is safe because it first locks the
character `FOR UPDATE`, which conflicts with the `FOR NO KEY UPDATE` lock that
every item transaction takes on the same character.

## Migrations

Migration ids, SQL text, the `schema_migrations` table, and the migration
advisory lock never change. Each migration runs in its own transaction under the
advisory lock and records its id. The runner stores no checksum, so
`MigrationHistoryTests` pins the id, order, and SHA-256 of every historic
migration's SQL. A new migration appends its id and hash to that list.

| # | Id | Owner |
| --- | --- | --- |
| 1 | `202607071600_auth_character_world_join` | host baseline |
| 2 | `202607101200_character_world_sessions` | host baseline |
| 3 | `202607121200_session_ticket_ownership` | host baseline |
| 4 | `202607121500_world_session_ownership` | host baseline |
| 5 | `202607131000_world_registry_heartbeat` | host baseline |
| 6 | `202607131900_single_active_account_session` | host baseline |
| 7 | `202607132100_world_runtime_registration` | host baseline |
| 8 | `202607141200_simulation_topology` | host baseline |
| 9 | `202607141300_single_active_account_simulation_session` | host baseline |
| 10 | `202607151200_item_persistence_foundation` | Items |
| 11 | `202607171200_player_corpse_persistence` | Items |
| 12 | `202607171210_player_death_carry_overflow` | Items |
| 13 | `202607181800_item_operations_hardening` | Items |
| 14 | `202607182100_development_world_identity` | Placement |
| 15 | `202607191200_worker_selected_world_binding` | Placement |
| 16 | `202610141800_release_retryable_operation_claims` | Items |
| 17 | `202610151000_equipped_item_lookup_index` | Items |

The nine baseline migrations stay together in the host because #1 and #6 touch
several modules' tables. The merged list must have unique ids, be strictly
ascending by ordinal id, start with exactly the frozen history, and sort every
newer id after `202610151000_equipped_item_lookup_index`.

## Configuration

Configuration keys did not change. Each module validates and owns the keys it
reads, and services receive immutable validated option records instead of
reading `IConfiguration`:

| Keys | Owner |
| --- | --- |
| `ConnectionStrings:Postgres`, `ConnectionStrings:Redis`, `Database:*`, `HealthChecks:TimeoutMilliseconds` | `PlatformOptions` |
| `Auth:SessionLifetimeHours`, `RateLimiting:Authentication:*`, `ServiceAuthentication:SimulationWorkers:*`, `SIMULATION_WORKER_SERVICE_SECRET`, `RedisAcceleration:*` | `AccountsModuleOptions` |
| `Game:MaxCharactersPerAccount` | `CharactersModuleOptions` |
| `Simulation:*`, `WorldData:WorldsPath`, `DevelopmentSimulationBots:*`, `ACTIVE_SIMULATION_BOTS_SECRET` | `PlacementModuleOptions` |
| `Items:*`, `StackStressFixtures:*`, `STACK_STRESS_FIXTURE_SECRET` | `ItemsModuleOptions` |

The host loader `GameServiceConfiguration.Load` first validates every required
key of every module together and throws one `GameService configuration is
invalid:` error that lists every problem. It then reads the self-contained
option sets in their original order, each with its own error message. Worker
credentials keep the original key casing, so only an exact worker id matches,
and the shared worker secret applies only when no per-worker secret exists.
Items reads the worker heartbeat timeout through Placement's
`SimulationWorkerLeasePolicy`.

## Cross-Module Statements

These are every SQL statement that crossed a future module boundary when the
split started. Acc is Accounts, Chr is Characters, Plc is Placement, and Itm is
Items. R is read, W is write, and L is lock.

| # | Statement | Foreign access | Fix |
| --- | --- | --- | --- |
| S1 | Login session replacement releases simulation sessions | Plc W | `IAccountSessionLifecycleHandler.OnSessionsReplacedAsync`, same SQL and position |
| S2 | Login session replacement consumes join tickets | Plc W | same handler, after S1 |
| S3 | Session revoke releases simulation sessions | Plc W | `IAccountSessionLifecycleHandler.OnSessionRevokedAsync` |
| S4 | Session revoke consumes join tickets | Plc W | same handler, after S3 |
| S5 | Character create locks the account | Acc L | `IAccountLocks` |
| S6 | Character create bootstraps item state | Itm W | `ICharacterLifecycleHandler`, implemented by Items |
| S7 | Join and ticket consume lock the account | Acc L | `IAccountLocks` |
| S8 | Join locks the owned active character | Chr L | `ICharacterLocks.LockOwnedActiveForUpdateAsync` |
| S9 | Ticket consume locks the character | Chr L | `ICharacterLocks.LockForUpdateAsync` |
| S10 | Ticket consume reads character name and account session | Chr R, Acc R | `CharactersSql` and `AccountsSql` fragments in one statement |
| S11 | Ticket consume locks carry state | Itm L | `ISimulationCharacterStateProvider.LockForSessionStartAsync` |
| S12 | Session heartbeat checks the account session and reads carry state | Acc R, Itm R | account fragment, then `ISimulationCharacterStateProvider.GetAsync` |
| S13 | Session heartbeat reads the revocation reason | Acc R | `AccountsSql.RevocationReasonOf` |
| S14 | Session release reads carry state | Itm R | `ISimulationCharacterStateProvider.GetAsync` |
| S15 | World rebind counts open corpses | Itm R | `IShardWorldLocalStateProbe` |
| S16 | Item state backfill lists active characters | Chr R | `CharactersSql.ActiveCharacters` |
| S17 | Carry recompute batch filters active characters | Chr R | `CharactersSql.ActiveCharacters` |
| S18 | Item state bootstrap reads the account for its advisory lock | Chr R | account id from `CharacterCreated` or the backfill row |
| S19 | `bootstrap_character_item_state` reads `characters` | Chr R | R4, frozen |
| S20 | Corpse restoration checks the worker lease | Plc R | `ISimulationWorkerAuthority.GetShardLeaseAsync` |
| S21 | Corpse view checks session, worker, and assignment | Plc R, Acc R | `ISimulationSessionAuthority.GetReadAuthorityAsync` |
| S22 | Live item authority locks the character | Chr L | `ICharacterLocks.LockActiveForItemMutationAsync` |
| S23 | Live item authority locks the simulation session | Plc L, Acc R | `ISimulationSessionAuthority.LockForItemMutationAsync` |
| S24 | Live item authority reads the worker runtime | Plc R | `ISimulationWorkerAuthority.GetRuntimeAsync` |
| S25 | Live item authority shares the assignment | Plc L | `ISimulationWorkerAuthority.LockAssignmentForShareAsync` |
| S26 | Item mutations lock characters with their item state | Chr L | `ICharacterLocks`, then the item state lock |
| S27 | Offline item mutations check for an active session | Plc R | `ISimulationPresence.FindActiveCharacterAsync` |
| S28 | Death and corpse loot lock one character with its item state | Chr L | as S26 |
| S29 | Mob corpse creation locks the worker lease and assignment | Plc L | `ISimulationWorkerAuthority` lock methods |
| S30 | Insurance charges character currency | Chr L, W | `ICharacterWallet.DebitAsync` |
| S31 | Secure Container tier change lists account characters | Chr R | `CharactersSql` fragment |
| S32 | Operation claims reference the actor account and character | Acc R, Chr R | `AccountsSql.ExistingIdOrNull`, `CharactersSql.ExistingIdOrNull` |
| S33 | Death reads the character name and Shard | Chr R, Plc R | `CharactersSql` and `PlacementSql` fragments |
| S34 | Corpse snapshot reads the account entitlement through the character | Chr R | uses the locked item state's account id |
| S35 | Recovery expiry scan skips deleted characters | Chr R | `CharactersSql.ActiveCharacters` |
| S36 | Inventory reads check character ownership | Chr R | `CharactersSql.ActiveCharacters` |
| S37 | Development item tool lists characters | Chr R, Acc R, Plc R | fragments |
| S38 | Development item tool loads a target | Chr R, Plc R | fragments |
| S39 | Phase 9 fixture loads and validates a target | Chr R, Plc R | fragments |
| S40 | Baseline migrations #1 and #6 | mixed DDL | R4, frozen |

`TableOwnershipTests` scans the GameService sources for `from`, `join`, `into`,
and `update` on owned tables. Foreign access that has not moved behind a
contract yet is listed in the test, and every listed entry must still exist, so
the list only shrinks.

## Guard Tests

| Test | What it protects |
| --- | --- |
| `MigrationHistoryTests` | Migration ids, order, SQL hashes, and the ids recorded by a fresh database |
| `MeterInstrumentNamesTests` | The `Mmorpg.GameService.Redis` and `Mmorpg.GameService.Items` meters and every `auth_service.*` instrument |
| `TableOwnershipTests` | Rules R1 to R4 for SQL in source files |
| `NamespaceBoundaryTests` | Namespace references only point down the dependency order |
| `RouteTableSnapshotTests` | Every route with its methods, authorization policy, rate-limit policy, and no-store metadata |

## Accepted Behavior Changes

The split keeps routes, request and response shapes, error codes and messages,
policies, transaction boundaries, isolation levels, statement order inside
transactions, and every persisted or observable identifier. These are the only
intended differences:

- **Hosted service start order.** Modules register in dependency order, so the
  Redis acceleration metrics reporter now starts before the corpse expiry, item
  metrics, and item maintenance services instead of third of four. None of them
  depends on another.
- **Endpoint registration order.** Routes are registered module by module, with
  the health routes first. ASP.NET Core matches routes by template precedence
  and HTTP method, never by registration order, so routing is unchanged.
  `RouteTableSnapshotTests` pins the complete table.

## Implementation Status

- Guard rails: done.
- Composition per module: done. `GameServiceComposition` registers the platform
  services, each module, and the host services, applies the middleware, and maps
  the routes. `Program.cs` and the tests use it.
- Test composition through dependency injection: done.
  `GameServiceTestComposition` builds the production graph with a test-owned
  data source, Redis acceleration disabled, and no module background services.
- Options split: done.
- Platform project: done. `Mmorpg.GameService.Platform` holds the Problem
  Details pipeline, token hashing, the Redis connection, the transaction scope,
  the migration runner, platform options, and `IGameServiceModule`. Each module
  implements `IGameServiceModule`, and the host composes them in dependency
  order.
- Accounts untangled: done. Placement implements `IAccountSessionLifecycleHandler`
  (S1 to S4), character creation and joins lock accounts through `IAccountLocks`
  (S5, S7), and Placement and Items read account sessions through `AccountsSql`
  (S10, S12, S13, S21, S23). The composition requires exactly one
  implementation of every SPI.
- Accounts extracted: `src/Server/GameService.Accounts` builds
  `Mmorpg.GameService.Accounts`, which references only Platform. Only
  `AccountsModule`, its options, and `Accounts.Contracts` are public.
- Characters untangled: done. Items implements `ICharacterLifecycleHandler`
  (S6, S18), Placement and Items lock characters through `ICharacterLocks` (S8,
  S9, S22, S26, S28), insurance debits currency through `ICharacterWallet`
  (S30), foreign character and account reads use `CharactersSql` and
  `AccountsSql` (S10, S16, S17, S31 to S33, S35 to S39), and corpse snapshots
  read the entitlement through the locked account (S34).
- Characters extracted: `src/Server/GameService.Characters` builds
  `Mmorpg.GameService.Characters`, which references Platform and Accounts.
  Only `CharactersModule`, its options, and `Characters.Contracts` are public.
- Placement SPIs: done. The Simulation folder became the Placement namespace.
  Items implements `ISimulationCharacterStateProvider` (S11, S12, S14) and
  `IShardWorldLocalStateProbe` (S15), and reads Placement state through
  `PlacementSql` (S33, S37 to S39).
- Placement authority contracts: done. Items judges simulation sessions,
  worker leases, assignments, and presence through `ISimulationSessionAuthority`,
  `ISimulationWorkerAuthority`, and `ISimulationPresence` (S20 to S25, S27,
  S29). No GameService source reads, writes, or locks another module's table
  outside the frozen migrations.
- Placement migrations: done. `PlacementModule.Migrations` owns #14 and #15,
  and `GameServiceSchema` merges the host list with every module's list by
  ordinal id and fails startup when the result does not start with the shipped
  history or repeats an id.
- Placement extracted: `src/Server/GameService.Placement` builds
  `Mmorpg.GameService.Placement`, which references Platform, Accounts,
  Characters, `Mmorpg.Server.Common`, and `Mmorpg.WorldData`. Only
  `PlacementModule`, its options with the topology, World catalog, and
  Development bot options, and `Placement.Contracts` are public. The host
  seeds the topology through `PlacementModule.SeedTopologyAsync`.
- The Items extraction: in progress.

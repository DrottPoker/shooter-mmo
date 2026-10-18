# GameService Modules

Last updated: 2026-10-16

## Status

Accepted and implemented. GameService is a compiler-enforced modular monolith:
one process and one deployable whose code is split into a host and five module
projects. Each module was first untangled inside the original project and then
extracted into its own project, in dependency order, with every test green at
every step.

## Context

GameService is one ASP.NET Core process that owns accounts, characters, Shard
placement, and durable items. Its code grew as one project of about 26,000
lines. Folders looked like modules, but nothing stopped one folder from reading,
writing, or locking another folder's tables, and several folders depended on
each other in cycles:

| Id | Cycle | Fix |
| --- | --- | --- |
| C1 | Auth and Redis use each other | Both joined the Accounts module. Only the Redis connection moved to Platform. |
| C2 | Characters and Items | Characters publishes a character-created SPI that Items implements. |
| C3 | Placement and Items | Placement publishes session-start carry-state and World-rebind SPIs that Items implements. Items consumes Placement authority contracts. |
| C4 | Accounts and Placement | Accounts publishes a session lifecycle SPI that Placement implements. |
| C5 | Database initialization calls Items seeding | The migration runner moved to Platform. Seeding goes through `IDatabaseSeeder`. The host decides the order. |
| C6 | One shared configuration object | Each module validates and owns its own options. The host aggregates them. |

## Decision

GameService keeps its process and assembly name `Mmorpg.GameService`. Its code
is split into six projects whose references enforce one dependency order:

```text
Platform <- Accounts <- Characters <- Placement <- Items <- host
```

| Project | Path | References | Owns |
| --- | --- | --- | --- |
| `Mmorpg.GameService.Platform` | `src/Server/GameService.Platform` | `Mmorpg.Server.Common` | Problem Details results and the HTTP pipeline, token hashing, the Redis connection, `PostgresTransactionScope`, the migration runner, `IDatabaseSeeder`, platform options, and `IGameServiceModule`. No domain model. |
| `Mmorpg.GameService.Accounts` | `src/Server/GameService.Accounts` | Platform | Accounts, account sessions, account and worker authentication, authorization policies, login and registration rate limits, and Redis session acceleration |
| `Mmorpg.GameService.Characters` | `src/Server/GameService.Characters` | Platform, Accounts | Characters, ownership, character currency, and character level |
| `Mmorpg.GameService.Placement` | `src/Server/GameService.Placement` | Platform, Accounts, Characters, `Mmorpg.Server.Common`, `Mmorpg.WorldData` | World definitions, Fleets, Nodes, Shards, the worker registry, SimulationAssignments, join tickets, simulation session leases, and Development simulation bots. It decides where a character plays right now. |
| `Mmorpg.GameService.Items` | `src/Server/GameService.Items` | Platform, Accounts, Characters, Placement, `Mmorpg.WorldData` | The item catalog, item state, item transactions, corpses, death partition, Recovery, policies, item operations maintenance, and the Development item tools |
| `Mmorpg.GameService` (host) | `src/Server/GameService` | every module | `Program.cs`, the configuration loader, database startup, the frozen baseline migrations, health routes, and the shipped configuration and content files |

"Placement" avoids collisions with World content (`Mmorpg.WorldData`) and with
the simulation process names. "Platform" holds infrastructure plumbing only, so
it never becomes a shared domain kernel.

Module projects import `src/Server/GameService.Module.props`, which gives them
the ASP.NET Core shared framework and the same global usings that the Web SDK
gives the host.

### Public Surface

A module publishes only its module class, its options with their nested option
records, and its `Contracts` namespace. Items also publishes `ItemsCommandLine`
for the Development item commands. Everything else is internal. Test projects
reach internals through `InternalsVisibleTo` along the dependency direction: a
module grants its own test project, the test projects of the modules that build
on it, the GameService test kit, `Mmorpg.GameService.Tests`, and
`Mmorpg.Integration.Tests`, as listed in
[Backend Tests](../tests/README.md#internal-access). A module never grants
another module or any production assembly access to its internals.

| Module | Public types outside `Contracts` |
| --- | --- |
| Accounts | `AccountsModule`, `AccountsModuleOptions`, `SimulationWorkerCredentials`, `RedisAccelerationOptions` |
| Characters | `CharactersModule`, `CharactersModuleOptions` |
| Placement | `PlacementModule`, `PlacementModuleOptions`, `SimulationTopologyConfig`, `FleetBootstrapConfig`, `SimulationNodeBootstrapConfig`, `ShardBootstrapConfig`, `WorldManifestCatalog`, `DevelopmentSimulationBotOptions` |
| Items | `ItemsModule`, `ItemsModuleOptions`, `ItemCatalogSource`, `ItemOperationsOptions`, `NpcItemLifecycleOptions`, `StackStress.StackStressFixtureOptions`, `Development.DevelopmentItemPackageSource`, `ItemsCommandLine` |

Platform is public except its middleware and exception handler.

### Composition

Each module implements `IGameServiceModule`: a name, the migrations it owns,
the meters it creates, its service registrations, and its routes. The host
exports exactly the declared meters when telemetry export is enabled, as
described in [Monitoring](MONITORING.md). `GameServiceComposition` in the host
builds the modules in dependency order and is shared by `Program.cs` and the
tests. Startup runs in this order:

1. Load configuration sources, then validate every option set
   (`GameServiceConfiguration.Load`).
2. Set the Kestrel request body limit from the item operations options.
3. Register Platform, the host services, and each module, then check that every
   SPI has exactly one implementation.
4. Build the pipeline: the API pipeline (correlation id, exception handler,
   status pages, no-store responses), the distributed authentication rate
   limiter, the in-process rate limiter, authentication, and authorization.
   Routing stays implicit, so it runs first.
5. When `Database:RunMigrationsOnStartup` is true, apply the merged migration
   history, run every `IDatabaseSeeder` (the item catalog, then the item state
   backfill), and seed the topology through `PlacementModule.SeedTopologyAsync`.
6. Run a `--dev-items-*` command through
   `ItemsCommandLine` and exit, if one is given. Hosted services never start in
   that mode.
7. Map the health routes, then each module's routes, and run.

## Table Ownership

| Owner | Tables and objects |
| --- | --- |
| Platform | `schema_migrations`; advisory lock `7104202607071600` |
| Accounts | `accounts`, `account_sessions` |
| Characters | `characters`, including `currency` and `level` |
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
  their own aliases. This keeps single-statement atomicity and hot-path round
  trips while the owner keeps control of its SQL text.
- **R4.** The frozen migration history is exempt: the nine baseline migrations,
  the module migrations, and the `bootstrap_character_item_state` function that
  reads `characters`.
- **R5.** Foreign keys only point down the dependency order.

Contract implementations always use the caller's connection and transaction.
They never commit, roll back, or open their own connection. That keeps
RepeatableRead snapshots and the transaction-local `statement_timeout` and
`lock_timeout` that item transactions set.

## Contracts

Every method that takes a `PostgresTransactionScope` runs in the caller's
transaction. Authority contracts return stored facts, not verdicts, so the
caller keeps its own check order, error codes, and messages.

| Contract | Owner | Used by | Purpose |
| --- | --- | --- | --- |
| `IAccountLocks` | Accounts | Characters, Placement | Lock an account `FOR UPDATE` |
| `IAccountSessionLifecycleHandler` | Accounts (SPI) | implemented by Placement | Release simulation sessions and join tickets when sessions are replaced or revoked |
| `AccountsSql` | Accounts | Placement, Items | Active account session, revocation reason, existing account id, and username fragments |
| `ICharacterLocks` | Characters | Placement, Items | Placement `FOR UPDATE` locks and item mutation `FOR NO KEY UPDATE` locks in ascending id order |
| `ICharacterWallet` | Characters | Items | Debit character currency for insurance |
| `ICharacterProgression` | Characters | Items | Set a character's level for the Development item tool |
| `ICharacterLifecycleHandler` | Characters (SPI) | implemented by Items | Create item state for a new character |
| `CharactersSql` | Characters | Placement, Items | The active character derived table, the existing character id fragment, and the character level fragment |
| `SimulationWorkerLeasePolicy` | Placement | Items | The worker heartbeat timeout |
| `ISimulationSessionAuthority` | Placement | Items | Lock a simulation session for an item mutation, or read it with its worker lease and assignment |
| `ISimulationWorkerAuthority` | Placement | Items | Read or lock a worker's runtime, lease, and Shard assignment |
| `ISimulationPresence` | Placement | Items | Find a character that is in the world |
| `PlacementSql` | Placement | Items | Shard existence and active simulation session fragments |
| `ISimulationCharacterStateProvider` | Placement (SPI) | implemented by Items | Lock or read a character's carry state for its simulation session |
| `IShardWorldLocalStateProbe` | Placement (SPI) | implemented by Items | Count open durable corpses that block a World rebind |
| `IDatabaseSeeder` | Platform | implemented by Items | Seed the item catalog and back-fill character item state after migrations |

Every SPI and contract has exactly one implementation, registered as a
stateless singleton. The host composition fails at startup when an SPI has no
implementation or more than one.

No module publishes asynchronous events. Every flow above needs synchronous
atomicity: a revoked session must lose its simulation access in the same
transaction, a character must never exist without item state, and a World
rebind needs exact blocker counts. An outbox becomes necessary only if a module
later becomes its own service.

## Items Transaction Kernel

Every durable item mutation runs through one item transaction kernel in
`src/Server/GameService.Items/Transactions`:

| Folder | Namespace | Contents |
| --- | --- | --- |
| `Transactions/` | `Mmorpg.GameService.Items` | `ItemTransactionService`, the facade with one `ExecuteAsync` overload per command; the command, actor, and result contracts; the error codes and `ItemTransactionRejection` |
| `Kernel/` | `Mmorpg.GameService.Items.Transactions` | `ItemTransactionKernel`, `IItemCommandHandler<TCommand>`, admission, the canonical request, the operation ledger, outcomes, telemetry, and the persisted JSON options |
| `Context/` | `Mmorpg.GameService.Items.Transactions` | `ItemTransactionContext` with its session and facets, the kernel-owned finalizer, the simulation authority, and the lock types |
| `Domain/` | `Mmorpg.GameService.Items.Transactions.Domain` | Rules and writes shared by several handlers: policy states, Bag aggregate guards, Recovery deliveries, durable corpse records, corpse custody and transfers, and transform and Mob loot rules |
| `Handlers/` | `Mmorpg.GameService.Items.Transactions.Handlers` | One stateless handler class per command family; `PlayerDeath/` holds the death handler with its corpse layout writer, custody transfers, and partition models |

`Context/CarryStateCalculator` is the one carried-weight rule, shared with the
startup recompute in `CharacterItemStateBootstrapper`, and
`Context/ItemOwnershipSql` is the one definition of the character that owns an
item. Outside the kernel, `Development/` holds the process-local Development item
tool and its packages, and `StackStress/` holds the stack stress fixtures, which
register only when `StackStressFixtures:Enabled` is true.

Each overload routes its command with the persisted operation kind and the
actor character id to the kernel and one handler. The facade composes the
kernel and the handlers in its constructor; only `ItemTransactionService` is
registered in dependency injection.

`ItemTransactionKernel` owns the whole transaction. In this order it rejects an
invalid operation id or an oversized canonical payload without database access,
opens one connection with one `READ COMMITTED` transaction, sets the
transaction-local `statement_timeout` and `lock_timeout`, validates the actor and
the live simulation session, claims the operation in `item_operations` or replays
its stored result, sets the `item_mutation` savepoint, runs the handler,
finalizes, completes the operation, and commits. A rejection rolls back to the
savepoint and stores the rejected result, except for a timeout or a retried
system operation, which roll back completely and release the claim. A
transaction whose commit or rollback was attempted is never rolled back again,
so a constraint that fails at COMMIT is returned as an unstored rejection or
propagates with its original error.

`ItemTransactionContext` is the handler-facing API. It carries the
`ItemTransactionSession` (connection, transaction, operation, actor, and the
`PostgresTransactionScope` for other modules' contracts) and five facets:

| Facet | Type | Responsibility |
| --- | --- | --- |
| `Authority` | `ItemActorAuthority` | System, account, NPC service, and live container access |
| `Locks` | `ItemTransactionLocks` | Character state locks (`CharacterItemStateLocks`) and the mutation scope (`ItemMutationScopeLocks`) |
| `State` | `ItemStateReader` | Item, container, definition, and policy loads with their ownership and revision checks |
| `Placement` | `ItemSlotPlacement` | Container slot and equipment slot rules |
| `Journal` | `ItemChangeJournal` | Audit changes, touched characters and containers, result items, and Recovery deliveries |

`ItemTransactionFinalizer` turns the journal into container revisions, carry
weight with the hard cap, character revisions, result item revisions, and audit
rows. Handlers never commit, roll back, or open a connection, and they reject
only through `ItemTransactionContext.Reject`.

`ItemTransactionKernelBoundaryTests` pins the boundary: only `Kernel/` begins,
commits, or rolls back a transaction, uses the savepoint or `set_config`, or
touches `item_operations`; handlers hold only readonly injected state; no
transaction code runs database commands in parallel; handlers take locks in the
order below; only `ItemTransactionRejection` throws a rejection; the kernel never
uses HTTP API error codes; and only the types named in
[Cross-Module Statements](#cross-module-statements) use another module's
contracts or SQL fragments.

## Items Tables

| Table | Responsibility |
| --- | --- |
| `item_catalog_revisions` | Applied deterministic catalog revisions and the current one |
| `item_system_settings` | Base carry capacity and the inventory and bank slot counts from the catalog's character settings |
| `item_categories`, `item_tags`, `equipment_slots` | Catalog vocabulary with display order, slot maximum quantity, and active state |
| `item_definitions` | Stable id, display metadata, category, unit weight, stack maximum, destruction capability, required character level, structural fingerprint, and active state |
| `item_definition_tags`, `item_definition_default_policies` | Many-to-many definition tags and default policies |
| `item_definition_equipment_slots`, `item_definition_blocked_equipment_slots` | Compatible equipment slots and the slots an equipped definition blocks |
| `item_definition_location_rules` | Secure Container and other explicit location eligibility |
| `bag_definitions`, `bag_definition_slots`, `bag_definition_slot_tags` | Bag carry bonus and its stable general and specialized slots |
| `secure_container_tiers`, `account_secure_container_entitlements` | Secure Container tiers and the account's selected tier |
| `character_item_states` | Aggregate revision, carried weight, carry capacity, and the owned top-level container ids |
| `item_containers`, `item_container_slots`, `item_container_slot_tags` | Typed containers with owner binding, slot kinds, accepted tags, revision, and lifecycle |
| `item_instances` | Durable identity, definition, quantity, revision, and exactly one location: a container slot or an equipment slot |
| `item_instance_policies` | Protected-on-death and one-death insurance lifecycle records |
| `recovery_deliveries`, `recovery_delivery_items` | System deliveries with source, availability, expiry, and their items |
| `item_operations`, `item_operation_changes`, `item_destructions` | Operation ids with canonical request hash and replayable result, the append-only audit, and destruction records |
| `corpses`, `corpse_sections`, `corpse_snapshots`, `death_events` | Durable player and persistent Mob corpses, their three sections, non-interactive presentation snapshots, and idempotent death events |

CHECK constraints enforce the location union of `item_instances`, and partial
unique indexes allow one item per container slot and per equipment slot. A
retired catalog row stays with `is_active = false`, so audit records and old
corpses keep their references. Normal non-persistent Mob corpses stay
SimulationWorker state.

Every mutation carries an operation id. Replaying the same id and canonical
payload returns the stored result; reusing it with another payload is a stable
conflict. Player mutations also carry the expected character item-state
revision, and targeted item, Bag, and corpse mutations carry the revisions of
the rows they target. An unrelated change in the same container does not reject
a targeted loot request.

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

An item mutation locks all its characters in ascending id order first and then
their item states in ascending order. Before the split it locked each character
together with its item state. Both orders take the first character first, and
the only transaction that locks item states without characters is the startup
backfill, which never waits on a character lock, so the change cannot deadlock,
even while old and new versions run side by side.

Inside Items, a handler takes its locks in this order: the advisory key (the
account's Secure Container entitlement key, or the death event or Mob corpse
key), then its characters and their item states in ascending id order, then the
corpse row for corpse interactions, then the mutation scope. The mutation scope
locks Bag aggregate roots, then containers, then ordinary items and Bag
children, then item policies, each in ascending id order.

## Migrations

Migration ids, SQL text, the `schema_migrations` table, and the migration
advisory lock never change. Each migration runs in its own transaction under the
advisory lock and records its id. The runner stores no checksum, so
`MigrationHistoryTests` pins the id, order, and SHA-256 of every historic
migration's SQL.

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
| 18 | `202610171200_item_catalog_alignment` | Items |
| 19 | `202610181200_character_level` | Characters |
| 20 | `202610181210_item_level_requirement` | Items |

The nine baseline migrations stay together in the host's
`LegacyBaselineMigrations` because #1 and #6 touch several modules' tables.
Each module lists its own migrations in `IGameServiceModule.Migrations`.
`GameServiceSchema` merges the host list with every module's list, sorted by
ordinal id, which reproduces the original application order exactly. Startup
fails when an id repeats or when the merged list does not start with the shipped
history, so every new migration must sort after
`202610181210_item_level_requirement`. A new migration also appends its id
to `GameServiceSchema.FrozenHistory` and its id and SQL hash to
`MigrationHistoryTests`.

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

The host loader `GameServiceConfiguration.Load` first validates the required
keys of every module together and throws one `GameService configuration is
invalid:` error that lists every problem. It then reads the self-contained
option sets in their original order, each with its own error message: the World
catalog, Development simulation bots, stack stress fixtures, NPC item
lifecycle, item operations, and Redis acceleration. Worker credentials keep the
original key casing, so only an exact worker id matches, and the shared worker
secret applies only when no per-worker secret exists. Items reads the worker
heartbeat timeout through Placement's `SimulationWorkerLeasePolicy` and still
writes it into the persisted Mob corpse command, so idempotent replays are
unchanged.

## Cross-Module Statements

These are every SQL statement that crossed a module boundary when the split
started, and how each one crosses now, followed by the statements added since
then from S41 on. Acc is Accounts, Chr is Characters, Plc is Placement, and Itm
is Items. R is read, W is write, and L is lock.

| # | Statement | Foreign access | Now |
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
| S22 | Live item authority locks the character | Chr L | `ICharacterLocks.LockActiveForItemMutationAsync` in `ItemSimulationAuthority` |
| S23 | Live item authority locks the simulation session | Plc L, Acc R | `ISimulationSessionAuthority.LockForItemMutationAsync` in `ItemSimulationAuthority` |
| S24 | Live item authority reads the worker runtime | Plc R | `ISimulationWorkerAuthority.GetRuntimeAsync` in `ItemSimulationAuthority` |
| S25 | Live item authority shares the assignment | Plc L | `ISimulationWorkerAuthority.LockAssignmentForShareAsync` in `ItemSimulationAuthority` |
| S26 | Item mutations lock characters with their item state | Chr L | `ICharacterLocks`, then the item state lock, in `CharacterItemStateLocks` |
| S27 | Offline item mutations check for an active session | Plc R | `ISimulationPresence.FindActiveCharacterAsync` in `CharacterItemStateLocks` |
| S28 | Death and corpse loot lock one character with its item state | Chr L | as S26, in `CharacterItemStateLocks` |
| S29 | Mob corpse creation locks the worker lease and assignment | Plc L | `ISimulationWorkerAuthority.LockLeaseAsync`, then `LockAssignmentAsync`, in `ItemSimulationAuthority` |
| S30 | Insurance charges character currency | Chr L, W | `ICharacterWallet.DebitAsync` in `ItemPolicyCommandHandler` |
| S31 | Secure Container tier change lists account characters | Chr R | `CharactersSql.ActiveCharacters` in `SecureContainerTierCommandHandler` |
| S32 | Operation claims reference the actor account and character | Acc R, Chr R | `AccountsSql.ExistingIdOrNull`, `CharactersSql.ExistingIdOrNull` in `ItemOperationLedger` |
| S33 | Death reads the character name and Shard | Chr R, Plc R | `CharactersSql.ActiveCharacters` and `PlacementSql.ShardExists` in `PlayerDeathCommandHandler` |
| S34 | Corpse snapshot reads the account entitlement through the character | Chr R | the locked item state's account id in `PlayerCorpseLayoutWriter` |
| S35 | Recovery expiry scan skips deleted characters | Chr R | `CharactersSql.ActiveCharacters` |
| S36 | Inventory reads check character ownership | Chr R | `CharactersSql.ActiveCharacters` |
| S37 | Development item tool lists characters | Chr R, Acc R, Plc R | `CharactersSql.ActiveCharacters`, `AccountsSql.UsernameOf`, `PlacementSql.HasActiveSimulationSession` |
| S38 | Development item tool loads a target | Chr R, Plc R | `CharactersSql.ActiveCharacters`, `PlacementSql.HasActiveSimulationSession` |
| S39 | The Development item tool loads and validates a target | Chr R, Plc R | `CharactersSql.ActiveCharacters`, `PlacementSql.HasActiveSimulationSession` |
| S40 | Baseline migrations #1 and #6 | mixed DDL | R4, frozen |
| S41 | Equipment checks the level requirement against the character level | Chr R | `CharactersSql.LevelOf` in `ItemSlotPlacement` |
| S42 | The Development item tool sets a character's level | Chr L, W | `ICharacterProgression.SetLevelAsync` in `DevelopmentItemToolService` |

`TableOwnershipTests` scans the GameService sources for `from`, `join`, `into`,
and `update` on owned tables and fails on any foreign table outside the frozen
migrations.

## Guard Tests

The guard tests live in `tests/Server/Mmorpg.GameService.Tests/Architecture`,
except `ItemTransactionKernelBoundaryTests`, which lives with the kernel tests in
`tests/Server/Mmorpg.GameService.Items.Tests/ItemTransactions`.

| Test | What it protects |
| --- | --- |
| `MigrationHistoryTests` | Migration ids, order, SQL hashes, the merge rules, and the ids recorded by a fresh database |
| `MeterInstrumentNamesTests` | The `Mmorpg.GameService.Redis`, `Mmorpg.GameService.Items`, and `Mmorpg.GameService.Placement` meters and every `game_service.*` instrument that the dashboards and alerts read |
| `TableOwnershipTests` | Rules R1 to R4 for SQL in source files |
| `NamespaceBoundaryTests` | A module references other modules only through lower modules' `Contracts` namespaces and Platform |
| `RouteTableSnapshotTests` | Every route with its methods, authorization policy, rate-limit policy, and no-store metadata |
| `ModuleDependencyGraphTests` | The compiled references between the GameService assemblies and the host assembly name |
| `ModulePublicSurfaceTests` | Each module's exported types and Platform's internal middleware |
| `ModuleCompositionTests` | Exactly one implementation of every SPI and contract, from its owner module, and startup failure for a missing or duplicate SPI |
| `GameServiceTelemetryTests` | Every class that owns a `Meter` publishes `MeterName` and is declared by its module's `MeterNames`, and the dashboards and alert rules reference only published `game_service_*` metrics. It lives in the test project root. |
| `ItemTransactionKernelBoundaryTests` | The item transaction kernel boundary described in [Items Transaction Kernel](#items-transaction-kernel) |

## Accepted Behavior Changes

The split keeps routes, request and response shapes, error codes and messages,
policies, transaction boundaries, isolation levels, statement order inside
transactions, and every persisted or observable identifier: database, table,
and column names, migration ids and SQL, advisory lock ids, the Redis key
prefix and client name, meter and instrument names, `ApplicationName`, the
`Mmorpg.GameService` assembly and process name, configuration keys, and the
idempotency payloads of item operations. These are the only intended
differences:

- **Character and item state locks are two statements (S26, S28).** An item
  mutation used to lock each character and its item state in one joined
  statement. It now locks the active characters in ascending id order through
  `ICharacterLocks`, then their item states in ascending order. The lock order
  changes from character 1, state 1, character 2, state 2 to character 1,
  character 2, state 1, state 2. Both orders take character 1 first, and the
  only transaction that locks item states without characters is the startup
  backfill, which never waits on a character lock, so no deadlock is possible,
  including during a rolling deploy with both versions. Item states of deleted
  characters are never locked, as before. The lock-wait metric still covers
  both statements. This adds one round trip per item transaction.
- **Session heartbeat and release read carry state separately (S12, S14).** The
  lease update no longer joins `character_item_states`. Placement reads the
  carry state through `ISimulationCharacterStateProvider` on the same
  connection right after the lease update commits, so the worker may see an
  item revision that committed in between, which is newer, never older. A
  missing item state still returns the same `401` and the lease update still
  commits, as before. This adds one round trip per heartbeat and release.
- **A World rebind counts open corpses separately (S15).** Placement counts its
  own blockers in one statement and open durable corpses through
  `IShardWorldLocalStateProbe` in a second statement in the same transaction.
  The transaction holds the Shard row lock, which blocks the foreign key lock
  that a new corpse needs, so the count stays exact. The conflict message is
  unchanged. This adds one round trip, only when a worker changes a Shard's
  World.
- **Item state bootstrap takes the account from its caller (S18).** The Secure
  Container entitlement advisory lock key is built from the account id that
  character creation or the backfill supplies, instead of reading
  `characters`. The key text is identical, because a character's account
  never changes.
- **Corpse snapshots read the entitlement through the locked account (S34).**
  The account id comes from the locked character instead of a join on
  `characters`; the row is locked and its account never changes.
- **Foreign reads use owner fragments (R3).** Statements that read another
  module's table now embed the owner's fragment, for example the active
  character derived table or an `exists` check, instead of naming the table.
  The results are identical; PostgreSQL flattens the derived table.
- **Logger categories follow the new namespaces.** `ILogger<T>` categories move
  from `Mmorpg.GameService.Auth`, `.Redis`, `.Simulation`, and `.Database` to
  `Mmorpg.GameService.Accounts`, `.Accounts.Redis`, `.Placement`, and
  `.Platform.Database`. No configuration filters on them. The explicit
  `Mmorpg.GameService.Auth` logout category is unchanged.
- **Hosted service start order.** Modules register in dependency order, so the
  Redis acceleration metrics reporter now starts before the corpse expiry, item
  metrics, and item maintenance services instead of third of four. None of them
  depends on another.
- **Endpoint registration order.** Routes are registered module by module, with
  the health routes first. ASP.NET Core matches routes by template precedence
  and HTTP method, never by registration order, so routing is unchanged.
  `RouteTableSnapshotTests` pins the complete table.
- **New startup checks.** Startup now fails when an SPI has no implementation or
  more than one, and when the merged migration list does not start with the
  shipped history or repeats an id. A correctly built GameService never hits
  either check.

## Testing And Next Steps

`GameServiceTestComposition` in the GameService test kit,
`tests/Testing/Mmorpg.GameService.Testing`, builds the production service graph
for tests with a test-owned data source, Redis acceleration disabled, and no
module background services. Integration tests, the API test hosts, and unit
tests that need a real service instance use it, so constructor changes stay
inside the modules.

Each module has its own test project under `tests/Server`, the host and the
guard tests live in `Mmorpg.GameService.Tests`, and flows that span modules or
processes live in `tests/Integration/Mmorpg.Integration.Tests`. Every
PostgreSQL test gets its own database, which `PostgresTestDatabase` copies from a
template database next to the configured base database; the templates are built
once per test run and named after the test project, `<base>_<project>_*`.
Separate schemas would not work, because the migrations create everything in
the `public` schema and `bootstrap_character_item_state` pins `search_path` to
`public`. Separate databases also keep the migration and catalog advisory locks
of tests that run in parallel apart. See [Backend Tests](../tests/README.md).

The item transaction kernel has golden tests in
`tests/Server/Mmorpg.GameService.Items.Tests/Golden/ItemTransactions`. Each golden file holds the
statements and transaction boundaries that Npgsql logs for one command or kernel
path, the result, the stored operation, and its audit journal.
`ItemTransactionTraceGoldenTests` compares them byte for byte, so any change to
item transaction SQL, its order, or its journal shows up as a reviewed golden
diff. Run the tests with `MMORPG_UPDATE_GOLDENS=1` to rewrite the files after an
intended change.

Measure the extra round trips of the item state lock split and the session
heartbeat with the full-stack stack stress run before raising load targets.

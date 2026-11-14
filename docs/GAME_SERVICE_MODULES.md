# GameService Modules

GameService is one ASP.NET Core process and one deployable whose code is a
compiler-enforced modular monolith: a host and eight module projects. This
document owns the module projects, their public surface and composition, table
ownership, the access rules, the contracts between modules, the items
transaction kernel, the lock order, the migrations, the module configuration,
the cross-module statements, and the guard tests.

The process boundary is in
[Project Architecture](PROJECT_ARCHITECTURE.md#gameservice), the durable data
model in [Durable Data Model](PROJECT_ARCHITECTURE.md#durable-data-model), the
HTTP routes and their behavior in [Service Features](SERVICE_FEATURES.md), and
the item rules that the kernel enforces in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).

## Modules

GameService keeps its process and assembly name `Mmorpg.GameService`. Its code
is split into nine projects whose references enforce one dependency order:

```text
Platform <- Accounts <- Characters <- Placement <- Items <- Quests <- Economy <- Content <- host
```

| Project | Path | References | Owns |
| --- | --- | --- | --- |
| `Mmorpg.GameService.Platform` | `src/Server/GameService.Platform` | `Mmorpg.Server.Common` | Problem Details results and the HTTP pipeline, token hashing, the Redis connection, `PostgresTransactionScope`, the migration runner, `IDatabaseSeeder`, platform options, and `IGameServiceModule`. No domain model. |
| `Mmorpg.GameService.Accounts` | `src/Server/GameService.Accounts` | Platform | Accounts, account sessions, account and worker authentication, authorization policies, login and registration rate limits, and Redis session acceleration |
| `Mmorpg.GameService.Characters` | `src/Server/GameService.Characters` | Platform, Accounts | Characters, ownership, character currency with its ledger, character level, character looks, and every character's saved place and vitals |
| `Mmorpg.GameService.Placement` | `src/Server/GameService.Placement` | Platform, Accounts, Characters, `Mmorpg.Server.Common`, `Mmorpg.WorldData` | World definitions, Fleets, Nodes, Shards, the worker registry, SimulationAssignments, join tickets, simulation session leases, the worker appearance route, and Development simulation bots. It decides where a character plays right now. |
| `Mmorpg.GameService.Items` | `src/Server/GameService.Items` | Platform, Accounts, Characters, Placement, `Mmorpg.WorldData` | The item catalog with item values, tool kinds, and vendor profiles, item state, the item transaction kernel with its item commands including ammunition batches, the worker item route with its operation kinds, corpses, the death partition, Recovery, policies, item operations maintenance, the stack stress fixtures, and the Development item tools |
| `Mmorpg.GameService.Quests` | `src/Server/GameService.Quests` | Platform, Accounts, Characters, Placement, Items, `Mmorpg.Server.Common`, `Mmorpg.WorldData` | The quest catalog that GameService runs, its mirrored revision and quest identities, every character's quest log with its owner and worker reads, and the quest commands with their worker routes, which run in Items' item transaction kernel |
| `Mmorpg.GameService.Economy` | `src/Server/GameService.Economy` | Platform, Accounts, Characters, Placement, Items, Quests, `Mmorpg.Server.Common`, `Mmorpg.WorldData` | NPC vendor purchases and sales and gathering yields: their item commands, which run in Items' item transaction kernel, and their operation kinds on Items' worker item route. It owns no table, migration, meter, route, or setting. |
| `Mmorpg.GameService.Content` | `src/Server/GameService.Content` | Platform, Placement, `Mmorpg.Server.Common`, `Mmorpg.WorldData` | Serving the compiled item catalog, quest catalog, and text tables by revision, exactly the files the other modules load, with the content rate limit. It owns no table and reads no text. |
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
reach internals through `InternalsVisibleTo` along the dependency direction, as
[Backend Tests](../tests/README.md#internal-access) lists. A module never grants
another module or any production assembly access to its internals.

| Module | Public types outside `Contracts` |
| --- | --- |
| Accounts | `AccountsModule`, `AccountsModuleOptions`, `SimulationWorkerCredentials`, `RedisAccelerationOptions` |
| Characters | `CharactersModule`, `CharactersModuleOptions` |
| Placement | `PlacementModule`, `PlacementModuleOptions`, `SimulationTopologyConfig`, `FleetBootstrapConfig`, `SimulationNodeBootstrapConfig`, `ShardBootstrapConfig`, `WorldManifestCatalog`, `DevelopmentSimulationBotOptions` |
| Items | `ItemsModule`, `ItemsModuleOptions`, `ItemCatalogSource`, `ItemOperationsOptions`, `NpcItemLifecycleOptions`, `StackStress.StackStressFixtureOptions`, `Development.DevelopmentItemPackageSource`, `ItemsCommandLine` |
| Quests | `QuestsModule`, `QuestsModuleOptions`, `QuestCatalogSource` |
| Economy | `EconomyModule` |
| Content | `ContentModule`, `ContentModuleOptions` |

Platform is public except its middleware and exception handler.

Items' `Contracts` namespace is the handler API of the
[Items Transaction Kernel](#items-transaction-kernel) for the commands of the
modules above it, and the SPI of its [worker item route](#worker-item-operations).
It holds only what the commands and operation handlers of those modules use:

| Part | Public types |
| --- | --- |
| Entry | `IItemTransactions`, `ItemTransactionRequest<TCommand>`, `ItemTransactionActor`, `ItemTransactionAuthority`, `SimulationItemTransactionAuthority`, `ItemTransactionLiveAccess` |
| Result | `ItemTransactionResult`, `ItemTransactionError`, `ItemTransactionCharacterRevision`, `ItemTransactionContainerRevision`, `ItemTransactionItemRevision`, `ItemTransactionItemChange`, `ItemPolicySummaryResponse` |
| Handler side | `IItemCommandHandler<TCommand>`, `IItemCommandSource`, `ItemCommandRegistration` and `ItemCommandRegistration<TCommand>`, `ItemTransactionContext` with its facets `ItemActorAuthority`, `ItemTransactionLocks`, `ItemStateReader`, `ItemSlotPlacement`, and `ItemChangeJournal` |
| Models | `CharacterLockRequest`, `LockedCharacterState`, `LockedItem`, `LockedItemPolicy`, `ResolvedItemDefinition` |
| Item primitives | `CarriedAcquisition`, `CarriedStacks`, `ItemLineage`, `LineageItems`, `LineageNeedPlan`, `ItemQuantityConsumption`, `ItemPolicyStates`, `ItemPolicySourceKinds` |
| Error codes | `ItemTransactionErrorCodes`, Items' own codes, and `SimulationItemRejections`, their HTTP statuses |
| Worker responses | `SimulationItemOperationResponse`, `SimulationCharacterStateResponse`, `SimulationCharacterStates` |
| SPIs | `IQuestDropDemand` and `IItemCatalogReferences`, which Quests implements, and `ISimulationItemOperationHandler` with `SimulationItemOperation`, which Economy implements |

A member of a published class that only Items uses stays internal: the
context's connection, transaction, and session, the facets' other loads, slot
rules, and item audits, the registration's handler, actor character, retry
flag, and canonical form, every property of `LockedItemPolicy`, and every
property of the other locked models except `LockedCharacterState.CharacterId`
and what a vendor sale reads of `LockedItem`: its id, definition, quantity,
container, equipment slot, and source container type and lifecycle. The
context's actor, the item and policy loads, the vendor profile load, the Bag
contents check, and the mutation scope lock are public for the economy
commands. The constructors of the context, its facets, the journal, and
`SimulationItemOperation` are internal, because only Items builds them. A
command above Items therefore runs no SQL against item tables: it changes items
only through the facets and the item primitives.

### Composition

Each module implements `IGameServiceModule`: a name, the migrations it owns,
the meters it creates, its service registrations, and its routes. The host
exports exactly the declared meters when telemetry export is enabled, as
described in [Monitoring](MONITORING.md). `GameServiceComposition` in the host
builds the modules in dependency order and is shared by `Program.cs` and the
tests, so services register, hosted services start, and routes map in module
order. Startup runs in this order:

1. Load configuration sources, then validate every option set
   (`GameServiceConfiguration.Load`), and configure telemetry export.
2. Set the Kestrel request body limit from the item operations options.
3. Register Platform, the host services, and each module, then check that every
   single-implementation SPI has exactly one implementation. Content reads every
   file it serves here and refuses to start on a missing or invalid file, as
   [Content Files](SERVICE_FEATURES.md#content-files) describes.
4. Build the pipeline: the API pipeline (correlation id, exception handler,
   status pages, no-store responses), the distributed authentication rate
   limiter, the in-process rate limiter, authentication, and authorization.
   Routing stays implicit, so it runs first and the endpoint is known to the
   rate limiter and the sensitive response middleware.
5. When `Database:RunMigrationsOnStartup` is true, apply the merged migration
   history, run every `IDatabaseSeeder` in module order, and seed the topology
   through `PlacementModule.SeedTopologyAsync`. Characters' seeder,
   `CharacterAppearanceVerifier`, refuses to start when
   `content/characters/appearance.json` no longer offers what a stored look
   names, so such a content change needs a data migration first. Items' seeder
   has Quests check the item references of the quest catalog through
   `IItemCatalogReferences`, which needs only the quest catalog that Quests
   loaded at registration, then mirrors the item catalog and back-fills
   character item state. Quests' seeder, `QuestCatalogSeeder`, then mirrors the
   quest catalog's revision and quest ids and refuses to start when a quest left
   the catalog without being retired, a retired id returns, or a quest that
   characters have active was retired or changed its steps or objectives. It
   reads only quest tables, so it needs nothing that Items' seeder mirrors.
6. Run a `--dev-items-*` command through `ItemsCommandLine` and exit, if one is
   given. Hosted services never start in that mode.
7. Map the health routes, then each module's routes, and run. Items' worker
   item route first builds its handlers of the other operation kinds and
   refuses to start when two handlers share a kind or one takes a kind that the
   route runs itself, as [Worker Item Operations](#worker-item-operations)
   describes. Hosted services start in module order; Content's `ContentRevisionCheck` refuses to start when
   the item or quest catalog it serves is not the revision that Items and Quests
   mirrored, and is skipped with a warning when the database cannot be read.

`GameServiceTestComposition` in the GameService test kit,
`tests/Testing/Mmorpg.GameService.Testing`, builds the same production service
graph for tests with a test-owned data source, Redis acceleration disabled, and
no module background services. Integration tests, the API test hosts, and unit
tests that need a real service instance use it, so constructor changes stay
inside the modules.

## Table Ownership

| Owner | Tables and objects |
| --- | --- |
| Platform | `schema_migrations`; advisory lock `7104202607071600` |
| Accounts | `accounts`, `account_sessions` |
| Characters | `characters`, including `currency` and `level`, `character_currency_ledger`, `character_appearances`, and `character_simulation_states` |
| Placement | `world_definitions`, `fleets`, `simulation_nodes`, `shards`, `simulation_workers`, `simulation_assignments`, `simulation_join_tickets`, `character_simulation_sessions` |
| Quests | `quest_catalogs`, `quest_definitions`, `character_quest_logs`, `character_active_quests`, `character_quest_progress`, and `character_completed_quests`; the quest catalog advisory lock `7104202611042200` |
| Economy | none; the vendor profiles and tool kinds that it reads are Items' catalog tables |
| Content | none |
| Items | every item, container, catalog, Recovery, operation, corpse, and death table, including the catalog's `item_tool_kinds`, `item_definition_tool_kinds`, `vendor_profiles`, `vendor_offers`, and `vendor_sale_categories`, and `live_mob_loot_grants`; the `validate_character_item_state_containers` trigger function, `bootstrap_character_item_state`, and the catalog advisory lock `7104202607151230` |

Foreign keys between modules follow the dependency order only: Characters to
Accounts, Placement to Accounts and Characters, Quests to Characters
(`character_quest_logs.character_id`), and Items to Accounts, Characters, and
Placement (`corpses.shard_id`). For the same reason
`character_currency_ledger.item_operation_id` names the item operation of a
change without a foreign key to `item_operations`. Cascading deletes therefore
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
| `IAccountSessionLifecycleHandler` | Accounts (SPI) | implemented by Placement | Mark the simulation sessions and consume the join tickets of sessions that are replaced or revoked |
| `AccountsSql` | Accounts | Placement, Items | Active account session, revocation reason, existing account id, and username fragments |
| `ICharacterLocks` | Characters | Placement, Items | Placement `FOR UPDATE` locks and item mutation `FOR NO KEY UPDATE` locks in ascending id order |
| `ICharacterWallet` | Characters | Items, Quests, Economy | Debit or credit character currency with a `CurrencyChangeSource` and one ledger row, for insurance, vendor trades, the starting balance, quest rewards, and the Development item tool |
| `ICharacterProgression` | Characters | Items | Set a character's level for the Development item tool |
| `ICharacterAppearances` | Characters | Placement, Items | Read a character's body model, look, and headwear, also of deleted characters for their corpses, lower or raise the headwear, and store a development look, each checked against the appearance content |
| `ICharacterSimulationStates` | Characters | Placement | Read a character's saved place and vitals, lock characters `FOR KEY SHARE` in ascending id order before a save, and save many states in one statement, as [Character Simulation States](#character-simulation-states) describes |
| `ICharacterLifecycleHandler` | Characters (SPI) | implemented by Items | Create item state and credit the starting balance for a new character |
| `CharactersSql` | Characters | Placement, Items | The active character derived table, the existing character id fragment, the character level fragment, and the currency balance fragment |
| `SimulationWorkerLeasePolicy` | Placement | Items | The worker heartbeat timeout |
| `ISimulationSessionAuthority` | Placement | Quests, Items | Lock a simulation session for an item mutation, or read it with its worker lease and assignment; both name whether the lease is live and how its account session ended, if it did |
| `ISimulationWorkerAuthority` | Placement | Items | Read or lock a worker's runtime, lease, and Shard assignment |
| `ISimulationPresence` | Placement | Items | Find a character that is in the world |
| `PlacementSql` | Placement | Items | Shard existence and active simulation session fragments |
| `ISimulationCharacterStateProvider` | Placement (SPI) | implemented by Items | Lock or read a character's carry state and equipment for its simulation session |
| `IQuestDefinitions` | Quests | Quests' own quest commands and routes | The loaded quest catalog: its id, revision, active quest limit, and every quest definition by id |
| `ICharacterQuestLog` | Quests | Quests' own quest commands | Read a character's quest log, or accept, progress, advance, complete, or abandon one of its quests, each raising the log revision |
| `IItemTransactions` and the handler API | Items | Quests, Economy | Run an item command through the item transaction kernel, register commands with an `IItemCommandSource`, and change items in a handler through `ItemTransactionContext` and the item primitives, as [Public Surface](#public-surface) lists |
| `SimulationItemRejections` and `SimulationCharacterStates` | Items | Quests, Economy | The HTTP status of a rejected item transaction by its code, and the acting character's state after a committed worker operation |
| `ISimulationItemOperationHandler` | Items (SPI) | implemented by Economy | Run an operation kind of the worker item route that Items does not run itself, as [Worker Item Operations](#worker-item-operations) describes |
| `IQuestDropDemand` | Items (SPI) | implemented by Quests | What a character's quest asks for from a quest drop, for the corpse rules of quest drops |
| `IItemCatalogReferences` | Items (SPI) | implemented by Quests | Check the item references of the quest catalog against the item catalog before Items' seeder mirrors it |
| `IQuestGatherCredit` | Quests | Economy | Count a gathering yield for the gather objectives of current steps in the yield's item transaction, and name the changed quests in its result |
| `IItemCatalogRevisionProvider` | Placement (SPI) | implemented by Items, also used by Content | Read the mirrored item catalog revision that a placed worker must run and that Content must serve |
| `IQuestCatalogRevisionProvider` | Placement (SPI) | implemented by Quests, also used by Content | Read the mirrored quest catalog revision that a placed worker must run and that Content must serve |
| `IShardWorldLocalStateProbe` | Placement (SPI) | implemented by Items | Count open durable corpses that block a World rebind |
| `IDatabaseSeeder` | Platform | implemented by Characters, Items, and Quests | Verify or seed module data after the migrations: Characters checks the stored looks against the appearance content, Items has the quest catalog's item references checked, then mirrors the item catalog and back-fills character item state, and Quests mirrors the quest catalog |

Every contract and every SPI except `IDatabaseSeeder` and
`ISimulationItemOperationHandler` has exactly one implementation, registered as
a stateless singleton, from the module that owns the data. The host composition
fails at startup when one of the single-implementation SPIs has no
implementation or more than one, because a missing one would silently skip
required work. `IDatabaseSeeder` has one implementation per module that
verifies or seeds data, and `ISimulationItemOperationHandler` one per operation
kind that a module adds to the worker item route.

No module publishes asynchronous events. Every flow above needs synchronous
atomicity: a revoked session must lose its simulation access in the same
transaction, a character must never exist without item state, and a World
rebind needs exact blocker counts. An outbox becomes necessary only if a module
later becomes its own service.

### Character Wallet

`ICharacterWallet.DebitAsync` and `CreditAsync` take a positive amount and a
`CurrencyChangeSource(Kind, SourceId, ItemOperationId)`. They lock the character
`FOR NO KEY UPDATE`, change `characters.currency`, and append one row to
`character_currency_ledger` with the source kind, source id, item operation id
when there is one, signed delta, balance after, and time, all in the caller's
transaction. A change that would take the balance below zero or above
`CurrencyRules.MaximumBalance` (1,000,000,000,000) returns a failed
`CurrencyChange` and leaves the balance unchanged; the caller chooses its error
code. The source kinds in `CurrencySourceKinds` are `starting_balance`,
`vendor_purchase`, `vendor_sale`, `insurance`, `development`, and
`quest_reward`. The ledger
also accepts `opening_balance`, which only the ledger migration wrote, once for
each balance that existed before the ledger. A database check keeps the balance
and every `balance_after` within the same range, so each character's ledger
deltas sum to its balance. `CharactersSql.CurrencyOf` lets Items read the
balance in its own statements, such as the item-state snapshot and the
Development item tool's character list.

`CharacterItemStateLifecycleHandler`, the Items implementation of
`ICharacterLifecycleHandler`, bootstraps the new character's item state and
then credits `item_system_settings.starting_currency_balance` with a
`starting_balance` ledger row in the character creation transaction. A zero
starting balance writes nothing. Existing characters keep their balance.

### Character Simulation States

`character_simulation_states` holds one row per character with its last saved
place and vitals: the World id and that World's collision revision when the
state was saved, the position and yaw as `real`, so the simulation's floats come
back bit for bit, health and mana, the simulation session that wrote the row,
the sequence of the state within that session, and the time it was saved. The
row's foreign key to `characters` cascades. The World id and the collision
revision name rows of no other module, so they have no foreign key. Checks keep
the position within 1,000,000 meters of the origin, the yaw within -360 to 360
degrees, health within 1 to 1,000,000, mana within 0 to 1,000,000, and the
sequence above 0, which also refuses a NaN or infinite real.

`ICharacterSimulationStates.ReadAsync` returns a character's row or null.
`SaveManyAsync` upserts a batch in one statement from parallel arrays: a state
replaces the stored row unless the stored row comes from the same simulation
session with an equal or higher sequence, and the method returns the characters
it saved. A batch names each character once. Characters checks no session; the
caller decides which session may write. `LockForSaveAsync` takes the
`FOR KEY SHARE` lock that the foreign key of a first save takes on the
character anyway, in ascending id order, so Placement takes it before it locks
the sessions, as the [lock order](#lock-order) requires.

Placement is the only caller, in `SimulationSessionService`: the join ticket
consume in `ShardService` reads the row (S67), a release with a final state
saves it in the release's transaction only when that release ended the session
(S68), and a checkpoint saves a batch for the sessions that are their
characters' active, unreleased, and unexpired sessions on the worker and
runtime that sent it (S69).
[Character Place And Vitals](SERVICE_FEATURES.md#character-place-and-vitals)
describes the routes.

## Items Transaction Kernel

Every durable item mutation runs through one item transaction kernel in
`src/Server/GameService.Items/Transactions`. The types of the handler API that
[Public Surface](#public-surface) lists are in the `Mmorpg.GameService.Items.Contracts`
namespace wherever their file lives; the request, result, error code, model,
worker response, and SPI types that need no kernel code are in
`src/Server/GameService.Items/Contracts`.

| Folder | Namespace | Contents |
| --- | --- | --- |
| `Transactions/` | `Mmorpg.GameService.Items` | `ItemTransactionService`, whose one generic `ExecuteAsync` runs every command and implements `IItemTransactions`; the commands of the core and corpse areas with their operation kinds in `ItemTransactionContracts.cs`, and `ItemTransactionRejection` |
| `Kernel/` | `Mmorpg.GameService.Items.Transactions` | `ItemTransactionKernel`, `IItemCommandHandler<TCommand>`, `ItemCommandRegistration<TCommand>`, `IItemCommandSource`, `ItemCommandCatalog`, admission, the canonical request, the operation ledger, outcomes, telemetry, and the persisted JSON options |
| `Context/` | `Mmorpg.GameService.Items.Transactions` | `ItemTransactionContext` with its session and facets, the kernel-owned finalizer, the simulation authority, and the lock types |
| `Domain/` | `Mmorpg.GameService.Items.Transactions.Domain` | Rules and writes shared by several handlers: policy states, Bag aggregate guards, Recovery deliveries, durable corpse records, corpse custody and transfers, the corpse rules of quest drops, the Loot all state, transform and Mob loot rules and writes, new item instances, quantity consumption, item destructions, carried acquisition, and the item primitives of [Areas](#areas) |
| `Handlers/` | `Mmorpg.GameService.Items.Transactions.Handlers` | One stateless handler class per command family and one command source per area; `PlayerDeath/` holds the death handler with its corpse layout writer, custody transfers, and partition models |

A module above Items keeps its item commands in its own `Transactions` folder
with the same layout. Quests has its commands and kinds in
`Transactions/QuestTransactionContracts.cs`, its error codes in
`Transactions/QuestTransactionErrorCodes.cs`, its handlers and command source in
`Transactions/Handlers/`, and its quest rules on the item primitives in
`Transactions/Domain/`, as [Quest Commands](#quest-commands) describes.
Economy has its commands, kinds, and `VendorAccess` in
`Transactions/EconomyTransactionContracts.cs`, its error codes in
`Transactions/EconomyTransactionErrorCodes.cs`, and its handlers and command
source in `Transactions/Handlers/`, as
[Vendor, Gathering, And Ammunition Commands](#vendor-gathering-and-ammunition-commands)
describes.

`Context/CarryStateCalculator` is the one carried-weight rule, shared with the
startup recompute in `CharacterItemStateBootstrapper`. It finds the character's
carried containers and equipped Bag with indexed lookups, as the death
partition does for its source items; a partial index on
`item_instances(equipped_character_id, equipment_slot_id)` supports both.
`Context/ItemOwnershipSql` is the one definition of the character that owns an
item. Outside the kernel, `Development/` holds the process-local Development
item tool and its packages, and `StackStress/` holds the stack stress fixtures,
which register only when `StackStressFixtures:Enabled` is true.

A command reaches the kernel through its registration.
`ItemCommandRegistration<TCommand>` holds everything the kernel needs to know
about one command kind: the persisted operation kind, the actor character that
the live authority check and the operation claim use (none for corpse expiry,
durable Mob corpse creation, and Recovery expiry), the handler, the retried
system operation, operationally significant, policy action, and
`MayRunAfterAccountSessionEnd` flags, and an optional canonical form of the
command for the idempotency payload. Each area has one `IItemCommandSource` in
its module's `Transactions/Handlers/`, next to its
handlers, registered as a singleton by the module that owns the area. A source
builds its own handlers from the services they need and never a handler of
another area:

| Source | Module | Commands |
| --- | --- | --- |
| `CoreItemCommandSource` | Items | Grant, relocate, swap container items, equip, unequip, split, merge, consume quantity, consume equipped ammunition, destroy, and swap Bag aggregates |
| `RecoveryAndPolicyCommandSource` | Items | Add, claim, and expire Recovery deliveries, apply an item policy, and remove insurance |
| `CorpseCommandSource` | Items | Player death, corpse expiry, the player corpse loot, deposit, move, Bag swap, and Loot all, durable Mob corpses, and the live Mob loot grants. It also builds the `CorpseQuestDrops` rules that five of its handlers take, on `IQuestDropDemand`. |
| `QuestCommandSource` | Quests | The quest commands and the Admin menu's quest tools of `QuestCommandHandler`, and `AbandonQuestItemsCommand` |
| `EconomyCommandSource` | Economy | Vendor purchases and sales, and gathering yields, which credit quests through Quests' `IQuestGatherCredit` |

`ItemCommandCatalog` collects the registrations of every source of every module
and refuses to build, naming both registrations, when two share a command type
or an operation kind. `ItemTransactionService.ExecuteAsync<TCommand>` looks up
the registration of the command's type and runs the kernel with it; a command
type without a registration throws `InvalidOperationException`. The modules
above Items inject it as `IItemTransactions`. The kernel, its ledger and
telemetry, `ItemSimulationAuthority`, `CharacterItemStateLocks`, the sources,
and the catalog are stateless singletons in dependency injection, and
`ItemTransactionService` is scoped.

A new command kind needs a command record in its area's contracts file, an
operation kind constant in its module's kinds class, `ItemOperationKinds` in
Items, `QuestOperationKinds` in Quests, or `EconomyOperationKinds` in Economy, a
handler in its module's `Transactions/Handlers/`, and one registration in its
area's source.
`ItemOperationsMetrics` labels a transaction with its registration's operation
kind, and counts it as a policy action when the registration's `PolicyAction`
flag is set, so a new kind needs no change to the metrics.
`ItemTransactionRoutingTests`, or `QuestTransactionRoutingTests` and
`EconomyTransactionRoutingTests` for a quest or economy kind, needs a route for
it. `ItemCommandCatalogTests`, `QuestCommandCatalogTests`, and
`EconomyCommandCatalogTests` fail when a kind constant of their module has no
registration or a registration of their module names no constant, and pin the
kinds that carry each flag; `EconomyCommandCatalogTests` also requires the
whole catalog to hold exactly the kinds of Items, Quests, and Economy.

`ItemTransactionKernel` owns the whole transaction. In this order it rejects an
invalid operation id or an oversized canonical payload without database access,
opens one connection with one `READ COMMITTED` transaction, sets the
transaction-local `statement_timeout` and `lock_timeout`, validates the actor and
the live simulation session, refuses a command whose registration does not allow
it once the session's account session ended, claims the operation in
`item_operations` or replays its stored result, sets the `item_mutation`
savepoint, runs the handler, finalizes, completes the operation, and commits. A
rejection rolls back to the savepoint and stores the rejected result, except
for a timeout or a retried system operation, which roll back completely and
release the claim. A
transaction whose commit or rollback was attempted is never rolled back again,
so a constraint that fails at COMMIT is returned as an unstored rejection or
propagates with its original error.

`ItemTransactionContext` is the handler-facing API. It carries the
`ItemTransactionSession` (connection, transaction, operation, actor, and the
`PostgresTransactionScope` for other modules' contracts) and five facets. The
handlers of the modules above Items see the scope, the operation id, the
actor, the facets, and `Reject`, and the facet members that
[Public Surface](#public-surface) publishes; the connection, the transaction,
and every other member stay Items' own:

| Facet | Type | Responsibility |
| --- | --- | --- |
| `Authority` | `ItemActorAuthority` | System, account, NPC service, and live container access, and the capability claims of a live operation |
| `Locks` | `ItemTransactionLocks` | Character state locks (`CharacterItemStateLocks`) and the mutation scope (`ItemMutationScopeLocks`) |
| `State` | `ItemStateReader` | Item, container, definition, policy, equipped tool kind, and vendor profile loads with their ownership and revision checks |
| `Placement` | `ItemSlotPlacement` | Container slot and equipment slot rules, and the character level |
| `Journal` | `ItemChangeJournal` | Audit changes, touched characters and containers, result items, Recovery deliveries, and the named result sections |

The context resolves current definitions and slot data from the mirrored
catalog but delegates stack, equipment, Bag, policy capability, and
integer-weight decisions to the shared WorldData rules. The
kernel does not depend on HTTP, Unity, or SimulationWorker runtime state.
Account, system, and simulation-worker actors are explicit command inputs, and
offline access and exact live-session access are actor requirements that the
kernel enforces while the durable character lock is held.

`ItemTransactionFinalizer` turns the journal into container revisions, carry
weight with the hard cap, character revisions with each touched character's
balance, result item revisions, item changes, and audit rows. The item changes
are the final state of every item an audit change recorded: its definition,
quantity, revision, container slot or equipment slot, and active policies with
their protection source, or `Removed` for an item that no longer exists. A Bag
in the Bag slot also names its contents container and revision, and every item
inside it is reported too, so the client sees the contents of a Bag it just
equipped. A transaction that changed more than 32 items reports none. The
worker turns the item changes into `OwnerItemStateChanged`, described in
[Realtime Protocol](REALTIME_PROTOCOL.md#owner-item-state-changes), so the
client patches its inventory instead of reloading it. Every item write must
therefore record an audit change, which
`ItemTransactionChangeReportIntegrationTests` checks against the inventory
snapshot for every move. A stored result without item changes, as older stored
results are, carries no change and no balance, and the worker then sends no
change. Handlers never commit, roll back, or open a connection, and they reject
only through `ItemTransactionContext.Reject`.

The finalizer also copies the journal's named result sections into
`ItemTransactionResult.Sections`. A command above the kernel sets a section
with `ItemChangeJournal.SetSection(name, value)`, and the value is serialized
with the persisted JSON options when the result is built. Each section is a
top-level key of the stored result JSON, so the journal refuses the name of a
result property, compared without regard to case as stored results are read.
The kernel stores and replays sections without reading them; the code that
writes a section reads it with `ItemTransactionResult.Section(name)`, which
treats a stored `null` as no section, or with
`ItemTransactionResult.ReadSection<TValue>(name)`, which reads it back with the
persisted JSON options. Two sections exist: `questLog`, the quests that a quest
command or a counted gathering yield changed, which Quests' `QuestLogSection`
writes and reads, and `questDrops`, the quest drops that a durable Mob corpse
kept, which `QuestDropSection` writes and reads. Results stored before the
sections hold the same keys with the same shapes, and every other stored result
holds `"questLog": null`, so all of them replay their quest parts. A result
written now has no key for a section it does not hold.

`SimulationItemOperationResponse`, the worker's item, quest, and live Mob loot
response, passes every section that holds a value through as a top-level key
after its own properties, as written, so `questLog` keeps its place and shape.
It writes no key for a section that the result does not hold or holds as `null`,
and never one named like one of its own properties. The worker reads a missing
`questLog` as no quest log change and ignores a section it does not read.

`ItemTransactionKernelBoundaryTests` pins the boundary: only `Kernel/` begins,
commits, or rolls back a transaction, uses the savepoint or `set_config`, or
touches `item_operations`; handlers hold only readonly injected state; no
transaction code runs database commands in parallel; handlers take locks in the
order below; only `ItemTransactionRejection` throws a rejection; the kernel never
uses HTTP API error codes; and only the types named in
[Cross-Module Statements](#cross-module-statements), and the command sources
that pass contracts to the handlers they build, use another module's contracts
or SQL fragments. The handler rules, readonly state, the lock order, sequential
commands, rejection only through the context, and no HTTP API error codes, hold
for the item command handlers of every module, each of which lives in its
module's `Transactions/Handlers/` folder.

### Operation Kinds

Every command persists its operation kind in `item_operations`, and the item
metrics label transactions with it, as [Monitoring](MONITORING.md#gameservice)
describes. The callers are the routes in
[GameService HTTP Surface](SERVICE_FEATURES.md#gameservice-http-surface) and the
system services.

| Operation kind | Command | Handler | Caller |
| --- | --- | --- | --- |
| `grant` | `GrantItemCommand` | `ItemGrantCommandHandler` | Quest grants on the worker route, the Development item tool and its packages, and the stack stress fixtures |
| `relocate` | `RelocateItemCommand` | `ItemRelocationCommandHandler` | Account and worker routes |
| `swap_container_items` | `SwapContainerItemsCommand` | `ItemRelocationCommandHandler` | Worker route |
| `equip`, `unequip` | `EquipItemCommand`, whose optional quantity equips part of a stack into an empty slot, and `UnequipItemCommand` | `ItemEquipmentCommandHandler` | Worker route; Development item packages also equip |
| `split_stack`, `merge_stacks` | `SplitItemStackCommand`, `MergeItemStacksCommand`, whose optional quantity merges part of the source | `ItemStackCommandHandler` | Account and worker routes |
| `consume_quantity` | `ConsumeItemQuantityCommand` | `ItemStackCommandHandler` | No caller outside tests yet |
| `consume_equipped_ammunition` | `ConsumeEquippedAmmunitionCommand` | `AmmunitionConsumptionCommandHandler` | Worker ammunition route |
| `destroy` | `DestroyItemCommand` | `ItemDestructionCommandHandler` | Account and worker routes |
| `swap_bag_aggregates` | `SwapBagAggregatesCommand` | `BagAggregateCommandHandler` | No caller outside tests yet; it swaps complete Bag aggregates, also between characters |
| `add_recovery_delivery`, `claim_recovery_delivery` | `AddRecoveryDeliveryCommand`, `ClaimRecoveryDeliveryCommand` | `RecoveryDeliveryCommandHandler` | Development item packages add deliveries; the worker route claims them |
| `apply_item_policy`, `remove_insurance_policy` | `ApplyItemPolicyCommand`, `RemoveInsurancePolicyCommand` | `ItemPolicyCommandHandler` | Worker route, for the insurance capability |
| `abandon_quest_items` | `AbandonQuestItemsCommand` | `QuestItemCommandHandler` in Quests | System test tools |
| `accept_quest`, `abandon_quest`, `regrant_quest_items` | `AcceptQuestCommand`, `AbandonQuestCommand`, `RegrantQuestItemsCommand` | `QuestCommandHandler` in Quests | Worker quest route |
| `talk_to_quest_npc`, `deliver_quest_items`, `turn_in_quest` | `TalkToQuestNpcCommand`, `DeliverQuestItemsCommand`, `TurnInQuestCommand` | `QuestCommandHandler` in Quests | Worker quest route |
| `credit_quest_kill` | `CreditQuestKillCommand` | `QuestCommandHandler` in Quests | Worker quest route, for every character a Mob's death credits |
| `process_player_death` | `ProcessPlayerDeathCommand` | `PlayerDeathCommandHandler` | Worker player death route and the Development corpse fixture |
| `expire_corpse` | `ExpireCorpseCommand` | `CorpseExpiryCommandHandler` | `CorpseExpiryHostedService` |
| `loot_corpse_item`, `loot_corpse_partial_stack`, `loot_corpse_item_to_equipment` | `LootCorpseItemCommand`, `LootCorpsePartialStackCommand`, `LootCorpseItemToEquipmentCommand` | `CorpseLootCommandHandler` | Worker corpse route |
| `deposit_corpse_item`, `deposit_corpse_partial_stack` | `DepositCorpseItemCommand`, `DepositCorpsePartialStackCommand` | `CorpseDepositCommandHandler` | Worker corpse route |
| `move_corpse_item`, `move_corpse_partial_stack` | `MoveCorpseItemCommand`, `MoveCorpsePartialStackCommand` | `CorpseInternalMoveCommandHandler` | Worker corpse route |
| `swap_corpse_bag` | `SwapCorpseBagCommand` | `CorpseBagSwapCommandHandler` | Worker corpse route |
| `loot_all_corpse_items` | `LootAllCorpseItemsCommand` | `CorpseLootAllCommandHandler` | Worker corpse route |
| `create_persistent_mob_corpse` | `CreatePersistentMobCorpseCommand` | `MobCorpseCommandHandler` | Worker durable Mob corpse route and the stack stress loot fixture |
| `grant_mob_loot`, `grant_mob_loot_all` | `GrantMobLootCommand`, `GrantMobLootAllCommand` | `MobLootGrantCommandHandler`, `MobLootAllGrantCommandHandler` | Worker live Mob loot routes |
| `expire_recovery_delivery` | `ExpireRecoveryDeliveryCommand` | `RecoveryExpiryCommandHandler` | Item operations maintenance |
| `vendor_purchase`, `vendor_sale` | `PurchaseFromVendorCommand`, `SellToVendorCommand` | `VendorCommandHandler` in Economy | Worker route, through Economy's operation handlers, for the vendor capability |
| `gather_resource` | `GatherResourceCommand` | `GatherResourceCommandHandler` in Economy | Worker route, through Economy's operation handler, for a resource node |

The registrations of `process_player_death`, `create_persistent_mob_corpse`,
`grant_mob_loot`, `expire_corpse`, and `expire_recovery_delivery` mark them as
retried system operations, whose ids need not be random and whose rejections
roll back completely instead of being stored. Those of `process_player_death`,
`create_persistent_mob_corpse`, `expire_corpse`, `apply_item_policy`,
`remove_insurance_policy`, and `abandon_quest_items` mark them as operationally
significant, so their commits are logged at `Information` instead of `Debug`.
Those of `apply_item_policy`, `remove_insurance_policy`, `abandon_quest_items`,
and `process_player_death` mark them as policy actions: each of their
transactions, committed or rejected, counts in the policy actions metric.
Those of `process_player_death`, `consume_equipped_ammunition`, and
`credit_quest_kill` set `MayRunAfterAccountSessionEnd`: they are the work of a
character's body that stays in the world after its account session ended, as
[Account Session End](SERVICE_FEATURES.md#account-session-end) describes, so
they run for a simulation session whose account session ended. For such a
session `ItemSimulationAuthority` refuses every other command, after every
check that ends the session, with `account_session_replaced` or
`account_session_ended`; a new kind is refused unless its registration sets
the flag, and the catalog tests pin the three kinds.
The `claim_recovery_delivery` registration sorts the claimed items by id and
revision for the idempotency payload. `ItemOperationAdmission` checks the
operation id rule and the payload limit before any database access.
[Item Transactions](SERVICE_FEATURES.md#item-transactions) describes both rules
as clients see them.

### Areas

The quest and economy code live in the Quests and Economy modules, above Items,
so the module boundaries keep them apart from the kernel: Items references
neither, and their commands reach the kernel only through Items' `Contracts`.
Corpses, death, and Mob loot stay in Items as their own area, because a corpse
is an item container and their code writes item tables directly:

| Area | Code |
| --- | --- |
| Corpse | The corpse handlers, `PlayerDeath/`, `MobCorpseCommandHandler`, the Mob loot grant handlers, `CorpseCommandSource`, the corpse, durable corpse, Loot all, and Mob loot code in `Domain/` with `CorpseQuestDrops`, `CorpseService`, and the corpse contracts and routes |

Two rules hold inside Items, and `ItemAreaBoundaryTests` enforces both from the
sources:

- The corpse area learns what a quest asks for only through `IQuestDropDemand`,
  which no Items code outside the corpse area uses, and it uses no other SPI
  that the quest code implements.
- The kernel names no quest, vendor, or gathering feature. Its one vendor name
  is `ItemStateReader.LoadVendorAsync`, because the vendor profiles are part of
  the mirrored item catalog.

The item primitives that the quest and economy commands use name no area;
those that they call are public in Items' `Contracts`:

| Primitive | What it does |
| --- | --- |
| `CarriedAcquisition` | Places new items into carried storage by the carried acquisition rule, also stack-sized parts of a larger quantity, and new protected items with a lineage that never merge into an existing stack |
| `ItemLineage` and `LineageItems` | An item's lineage is the source kind and id of its active `protected_on_death` policy, such as `quest_grant` and a quest. `LineageItems` counts the items of a definition with a lineage that a character owns anywhere, computes the part of an offer that a character still lacks toward a wanted total, and removes every lineage item of the quest item category with a destruction reason |
| `LineageNeedPlan` | Shares what a character still lacks between the offers of one transaction, so two drops of one item never take more than is wanted |
| `CarriedStacks` | Counts and takes the carried stacks of a definition that count for a lineage: stacks without an active policy, or with the lineage for an item of the quest item category |
| `ItemQuantityConsumption.RemoveForReasonAsync` | Removes a quantity of an item; a whole item leaves an `item_destructions` row with the reason, which `ItemDestructions` writes |
| `ItemPolicyStates.ToPolicyStates` | Turns an item's policies, which `ItemStateReader.LoadPoliciesAsync` loads, into the inputs of the shared policy rules |
| `ItemStateReader.HasEquippedToolKindAsync` | Whether the character has an item of an active tool kind in the `tool` slot |
| `ItemSlotPlacement.LoadCharacterLevelAsync` | The character's level |
| `ItemActorAuthority.EnsureCapability` | Requires a live operation whose capability claim of a kind names exactly an id, with the caller's error code and message |

Live access carries the city services as flags and every other capability as
a claim: `ItemTransactionLiveAccess.Capabilities` maps a capability kind to
the id that the worker validated. The code that receives a worker operation
sets its own claims: Economy's operation handlers claim the `vendor` kind with
the vendor profile through `VendorAccess`, and the quest route claims the
`quest_npc` kind with the NPC's actor definition through `QuestNpcAccess`. Each
also checks its claim through `EnsureCapability` with its own error code.

Items owns two SPIs that Quests implements, each with exactly one
implementation, which the host's SPI check requires at startup and
`ModuleCompositionTests` pins. `IQuestDropDemand` returns the quantity that the
collect objective of a quest's current step asks for in total, when the drop's
source is one of its drop sources, or 0, and `IItemCatalogReferences` lets the
quest catalog check its item references before Items' seeder mirrors the item
catalog. `CorpseQuestDrops` builds the corpse rules of quest drops on
`IQuestDropDemand` and the lineage primitives: what a quest drop item is, its
lineage policies, which corpse a drop may leave, the need of one drop, the
shared plan, and the Loot all plan. Economy's `GatherResourceCommandHandler`
credits quests through Quests' contract `IQuestGatherCredit`, which
`QuestGatherCredit` implements.

### Worker Item Operations

The worker item route, `POST /api/simulation-sessions/{id}/item-operations`,
belongs to Items: `SimulationItemMutationService` checks the authenticated
worker and the request's core fields, builds the simulation actor with the city
service access that the worker validated, and runs the kinds of its own
commands: `relocate`, `equip`, `unequip`, `split_stack`, `merge_stacks`,
`swap_container_items`, `destroy`, `claim_recovery_delivery`,
`apply_item_policy`, and `remove_insurance_policy`. It hands every other kind to
the `ISimulationItemOperationHandler` of that kind, and answers a kind without a
handler with `400` and `item_simulation_operation_invalid`.

A module adds an operation kind without an edit in Items: it registers a
stateless singleton that implements `ISimulationItemOperationHandler`, whose
`OperationKind` names the kind and whose `ExecuteAsync` receives a
`SimulationItemOperation`. That holds what the route established: the
authenticated worker and simulation session, the request's core fields, the
simulation actor, and the request fields that Items does not know, which
`SimulationItemOperationRequest` and its access keep as JSON extension data.
The handler names and checks its own fields:

- `ReadFields<TFields>()` and `ReadAccessFields<TFields>()` read the unknown
  fields of the request and of its access into the handler's own record with
  the route's JSON options, so names match without regard to case and numbers
  may come as strings, as on the route. A field of the wrong type refuses the
  request exactly as the route's JSON binding refuses a body: in Development
  with the binding's `BadHttpRequestException`, which the API pipeline answers
  with `400` and `invalid_request`, and otherwise with the bare `400` and the
  binding's Debug entry, which the status code pages complete with `http_error`.
- `HasItem` and `HasInteraction` apply the route's own checks of an item with
  its expected revision and of the validated interaction.
- `Unsupported()` gives the route's answer to a request that its kind cannot
  run.
- `RunAsync` runs the handler's command under the operation's id with the
  actor that the handler passes, after it added the capability claims of its
  kind, and answers like the route: a committed result with the character's
  state afterwards, a rejection as the handler's mapping gives it, which falls
  back to `SimulationItemRejections`.

`SimulationItemOperationHandlers` collects the handlers. Two handlers of one
kind, a handler of a kind that the route runs itself, or a handler without a
kind fail its construction with a message that names both sides, and the route
builds it when its endpoints are mapped, so the conflict stops the start.
`SimulationItemOperationHandlersTests` pins the route's own kinds and these
failures. Economy's `VendorPurchaseOperationHandler`,
`VendorSaleOperationHandler`, and `GatherResourceOperationHandler` are the
handlers today.

### Quest Commands

The quest commands belong to the Quests module, in
`src/Server/GameService.Quests/Transactions`. `QuestsModule` registers
`QuestCommandSource` with Items' item command catalog, and
`SimulationQuestMutationService` runs them through `IItemTransactions`, so each
is an item transaction of the kernel with its operation id, replay, and audit.
They change items only through `ItemTransactionContext` and the item
primitives, change the quest log through `ICharacterQuestLog` in the item
transaction, and take their quest definitions from `IQuestDefinitions`:

- An action at a quest NPC requires the quest access of the live session: its
  `quest_npc` capability claim must name the NPC that the quest needs, which
  `QuestNpcAccess` checks: the giver for `AcceptQuestCommand` and
  `RegrantQuestItemsCommand`, the objective's NPC for `TalkToQuestNpcCommand`
  and `DeliverQuestItemsCommand`, and the turn-in NPC for `TurnInQuestCommand`.
  The worker sets it from the quest capability it validated, never from a
  client value. `AbandonQuestCommand` and `CreditQuestKillCommand` need only the
  live session.
- Quest commands lock the character with
  `LockCharacterStateForIdempotentEventAsync`, without an expected character
  revision, because they only fill free carried slots or take items with the
  quest's lineage.
- `AcceptQuestCommand` checks availability with the shared `QuestRules`, the
  character level, and the quest log, and grants every start item as a new
  protected stack with the quest's lineage, the `quest_grant` policy source with
  the quest id, through `CarriedAcquisition.AcquireWithLineageAsync`, which never
  merges into an existing stack. Missing room rejects the acceptance with
  `inventory_full`.
- `AbandonQuestCommand` removes every item with the quest's lineage wherever the
  character owns it, with the `quest_abandoned` destruction reason, through
  `QuestLineage` on the lineage primitive, which `AbandonQuestItemsCommand`
  shares.
- `RegrantQuestItemsCommand` grants the start items that the quest still needs
  and the character no longer owns: the start quantity less what completed
  deliveries took, less what the character owns with the lineage.
- `QuestProgression` counts progress for the counted objectives of the current
  steps, up to each objective's count, and moves a quest whose current step is
  complete to its next step with zero progress. A complete last step waits for
  the turn-in. `CreditQuestKillCommand` counts one kill for every kill objective
  that names the Mob's actor definition or one of its tags, as the shared
  `QuestRules.CountsKill` decides, and `GatherResourceCommand` counts the yield
  for every gather objective of the node's actor definition,
  `QuestRules.CountsGather`, in the gathering transaction when the worker names
  the node's `ResourceActorId`, through Quests' `IQuestGatherCredit`, which
  Economy's gathering command calls and `QuestGatherCredit` implements. A credit
  that no current step asks for changes nothing.
- `TalkToQuestNpcCommand` completes a talk-to objective of the current step, and
  `DeliverQuestItemsCommand` takes the objective's count from carried storage
  and completes a deliver objective. `QuestLineage` counts and takes carried
  stacks of the permanent inventory and the equipped Bag through the
  `CarriedStacks` primitive: ordinary items without an active policy, and quest
  items with the quest's lineage.
- `TurnInQuestCommand` requires the last step to be current with its counted
  objectives complete and its collect objectives covered by carried items, and
  one of the reward choices when the quest offers them. It takes the collected
  items, removes what is left with the quest's lineage with the
  `quest_completed` destruction reason, places the rewards through carried
  acquisition, credits the Gold reward with a `quest_reward` ledger row, and
  counts the completion. Missing room for a reward rejects the whole turn-in
  with `inventory_full`.
- The Admin menu's quest tools are `QuestCommandHandler` commands too, in
  `QuestCommandHandler.Admin.cs`: `AdminAcceptQuestCommand`,
  `AdminCompleteQuestObjectiveCommand`, `AdminCompleteQuestStepCommand`,
  `AdminMakeQuestReadyCommand`, `AdminResetQuestCommand`,
  `AdminResetQuestChainCommand`, and `AdminResetQuestsCommand`. They require
  the simulation worker authority, fill collect objectives through carried
  acquisition, ordinary items in stack-sized parts with
  `CarriedAcquisition.AcquireStacksAsync`, and reset through
  `ICharacterQuestLog.ForgetAsync` and
  `QuestLineage` with the `quest_reset` destruction reason.
  `SimulationQuestMutationService` routes them only while
  `QuestsModuleOptions.DevelopmentQuestTools` is on, which only the Development
  environment sets.
- The result names the changed quests in its `questLog` section, which
  `QuestLogSection` writes and reads: the quest log revision afterwards and each
  quest's step, the progress of its counted objectives, and its completion
  count. A stored result replays it, and the worker item response passes it
  through as its `questLog`.
- The quest commands reject with Items' codes and their own,
  `QuestTransactionErrorCodes`. Codes that the item commands also use, such as
  `quest_grant_invalid`, `quest_item_not_needed`, and `inventory_full`, are
  Items' codes. `SimulationQuestRejections` gives the quest codes their HTTP
  status and every other code the status of Items' `SimulationItemRejections`,
  so the worker quest route answers an item code exactly as the worker item
  route does.

### Vendor, Gathering, And Ammunition Commands

The vendor and gathering commands belong to the Economy module, in
`src/Server/GameService.Economy/Transactions`. `EconomyModule` registers
`EconomyCommandSource` with Items' item command catalog and its three operation
handlers with the worker item route. Every economy handler reads all the economy
fields, `definitionId`, `expectedPrice`, `resourceProfileId`, `resourceNodeId`,
`requiredToolKind`, `resourceActorId`, and the access's `vendorProfileId`, so a
field of the wrong type refuses any economy request. The handlers answer a
missing or invalid field with the route's unsupported answer and run the
commands through the route. The commands reject with Items' codes and their
own, `EconomyTransactionErrorCodes`: `vendor_access_required`,
`vendor_offer_unavailable`, `vendor_price_changed`, `vendor_will_not_buy`,
`gathering_tool_required`, and `gathering_yield_invalid`. Codes that other
commands also use, such as `vendor_unavailable` from the vendor profile load,
`currency_insufficient`, and `inventory_full`, are Items' codes.
`SimulationEconomyRejections` gives the economy codes their HTTP status and every
other code the status of Items' `SimulationItemRejections`.
`EconomyOperationValidationTests` pins, through the route's JSON, every answer
that a vendor or gathering request gets before or in its command's own checks.

Both vendor commands require the vendor access of the live session: its
`vendor` capability claim must equal the command's vendor, which `VendorAccess`
checks. The worker sends it from the vendor capability it validated, never from
a client value, and GameService loads that profile with its offers and sale
categories from the active mirrored catalog through
`ItemStateReader.LoadVendorAsync`. `VendorPricing` from
`Mmorpg.WorldData` computes the total, rounded up for a purchase and down for a
sale, so GameService and Unity charge and show the same price.

- `PurchaseFromVendorCommand` checks the expected character revision, the
  vendor offer, a quantity within one stack, and the expected total price,
  debits the wallet with a `vendor_purchase` ledger row, and places the items
  through carried acquisition.
- `SellToVendorCommand` checks the expected character and item revisions, takes
  all or part of one carried, unequipped stack through
  `ItemQuantityConsumption.RemoveForReasonAsync`, which deletes a whole stack
  with an `item_destructions` row with reason `vendor_sale` or reduces a partial
  one, and credits the wallet with a `vendor_sale` ledger row when the price is
  positive.
- `GatherResourceCommand` requires the SimulationWorker actor, checks with
  `ItemStateReader.HasEquippedToolKindAsync` that an item with the required
  active tool kind is equipped in the `tool` slot, refuses a Bag or quest item
  as yield, and places the yield through carried acquisition. It takes no
  expected character revision, because gathering must not fail when the player
  moves an item meanwhile; the live session fence and the worker's operation
  id, which it reuses when it retries an uncertain outcome, replace it.
- `ConsumeEquippedAmmunitionCommand` checks the expected item revision, and for
  a batch the expected character revision, that the item is ammunition in the
  character's `ammunition` slot, and that it holds at least the quantity, and
  deletes the stack when the batch spends all of it. `ProcessPlayerDeathCommand`
  accepts the same consumption for the shots that were not committed yet and
  applies it before it partitions the items.

`CarriedAcquisition` in `Domain/` implements the carried acquisition rule of
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md#carried-acquisition)
in one all-or-nothing pass and rejects with `inventory_full` when the rest does
not fit; the finalizer still enforces the 140 percent hard cap.

The audit journal records `vendor_purchase` and `vendor_sale` with the vendor,
definition, quantity, price, and balance before and after, and
`resource_gathered` with the resource profile, node, Shard, worker runtime,
definition, and quantity. Acquired items are recorded as
`vendor_purchase_stack_increased`, `vendor_purchase_item_created`,
`resource_gathered_stack_increased`, and `resource_gathered_item_created`, and
sold items as `item_sold_to_vendor` or `item_quantity_sold_to_vendor`. The error
codes and their HTTP statuses are in
[Error Codes](SERVICE_FEATURES.md#error-codes).

## Lock Order

Every transaction that spans modules takes locks in this order:

```text
accounts -> account_sessions -> characters (ascending id)
  -> Placement (shards -> join tickets -> simulation sessions -> workers -> assignments)
  -> Items (entitlement advisory lock -> character_item_states ascending -> containers and items)
```

The Shard row is the one row that every join, session, corpse, and worker
heartbeat of a Shard touches, so its lock modes are fixed:

| Lock on the `shards` row | Who takes it | Waits for |
| --- | --- | --- |
| `FOR KEY SHARE` | A join ticket, the renewal of a worker heartbeat, and every foreign key insert of a row of the Shard: a ticket, a session, a corpse | Only a World rebind |
| `FOR NO KEY UPDATE` | A worker registration and a worker going offline, which change who owns the Shard | Each other and a World rebind |
| `FOR UPDATE` | Only a World rebind, so no row of the Shard can appear while it counts what blocks it | Everything above |

No normal traffic takes more than `FOR KEY SHARE`, so joins, ticket redeems,
deaths, and heartbeats of one Shard never queue on its row. A worker heartbeat
has two paths. The renewal, every heartbeat that changes nothing but the lease,
updates the worker row and then holds the assignment `FOR SHARE`, the lock of
every item operation, in the lock order. The registration, a first heartbeat, a
new runtime, a takeover, or another World, locks the assignment `FOR UPDATE`
before the workers it changes, against the lock order, and a World rebind then
raises its Shard lock to `FOR UPDATE` before it changes anything else. A
registration runs once per worker start; when it deadlocks with an operation of
the runtime it replaces, PostgreSQL ends one of them and both sides try again.
A join locks the worker row `FOR NO KEY UPDATE` in a statement of its own and
counts the worker's capacity in the next one, because a statement that waits
for a row lock still counts with the snapshot from before the wait. The lock
serializes the joins of one worker until their ticket is committed and lets the
foreign key insert of a session through. A join reads the catalog revisions
before it, so it holds the lock, which the worker's heartbeat waits for, only
for the count and the ticket insert. `ShardRowLockIntegrationTests` holds each of these locks
and checks who waits.

`ICharacterQuestLog` takes no lock of its own. Its writes run only in
transactions that already hold the character's lock, and the revision upsert
that starts every write locks the quest log row of that character, so a quest
change can wait only on another transaction of the same character, which holds
the character lock first.

A session release that carries a final state and a checkpoint read the
sessions' characters without a lock, lock those characters `FOR KEY SHARE` in
ascending id order through `ICharacterSimulationStates.LockForSaveAsync`, and
only then lock the sessions, the release `FOR NO KEY UPDATE` and the checkpoint
`FOR SHARE`. A first save of a character's state takes that key share lock
through its foreign key anyway, so taking it first keeps the order of a ticket
consume, which locks the character `FOR UPDATE` before it changes the session.

Ticket consume locks `character_item_states` before
`character_simulation_sessions`. That is safe because it first locks the
character `FOR UPDATE`, which conflicts with the `FOR NO KEY UPDATE` lock that
every item transaction takes on the same character.

An item mutation locks all its characters in ascending id order first, through
`ICharacterLocks`, and then their item states in ascending order. The only
transaction that locks item states without characters is the startup backfill,
which never waits on a character lock, so this order cannot deadlock. Item
states of deleted characters are never locked.

Inside Items, a handler takes its locks in this order: the advisory key (the
death event or Mob corpse key), then its characters and their item states in ascending id order, then the
corpse row for corpse interactions, then the mutation scope. The mutation scope
locks Bag aggregate roots, then containers, then ordinary items and Bag
children, then item policies, each in ascending id order. Every operation that
touches a Bag or one of its children therefore takes the Bag aggregate root
lock first.

## Migrations

Migration ids, SQL text, the `schema_migrations` table, and the migration
advisory lock never change. Each migration runs in its own transaction under the
advisory lock and records its id only after its SQL succeeded. The runner
stores no checksum, so `MigrationHistoryTests` pins the id, order, and SHA-256 of
every historic migration's SQL. Shipped migration ids and the identifiers
inside their SQL keep their historical names, such as `auth` and `world` for
what are now GameService and Shard concepts, because changing an applied
migration would break upgrades; the current schema uses only the canonical
topology names.

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
| 21 | `202610181220_death_event_killer` | Items |
| 22 | `202610181230_worker_item_catalog_revision` | Placement |
| 23 | `202610181240_character_currency_ledger` | Characters |
| 24 | `202610181250_item_economy` | Items |
| 25 | `202610251200_character_appearance` | Characters |
| 26 | `202610261800_live_mob_loot_grants` | Items |
| 27 | `202611011200_remove_secure_container` | Items |
| 28 | `202611021200_remove_cloak_equipment` | Items |
| 29 | `202611021300_expand_permanent_inventory` | Items |
| 30 | `202611021700_reduce_permanent_inventory` | Items |
| 31 | `202611041800_mob_corpse_ownership` | Items |
| 32 | `202611042200_quest_log` | Quests |
| 33 | `202611050900_quest_reward_currency` | Characters |
| 34 | `202611051200_worker_quest_catalog_revision` | Placement |
| 35 | `202611051300_remove_placeholder_quest_items` | Items |
| 36 | `202611101200_corpse_source_character_name` | Items |
| 37 | `202611101300_item_catalog_names_removal` | Items |
| 38 | `202611121200_character_simulation_states` | Characters |
| 39 | `202611131200_simulation_session_account_end` | Placement |

The nine baseline migrations stay together in the host's
`LegacyBaselineMigrations` because #1 and #6 touch several modules' tables.
Each module lists its own migrations in `IGameServiceModule.Migrations`, from
its `CharactersSchemaMigrations`, `PlacementSchemaMigrations`,
`QuestsSchemaMigrations`, or `ItemsSchemaMigrations` list. `GameServiceSchema` merges the host list with
every module's list, sorted by ordinal id. Startup fails when an id repeats or
when the merged list does not start with the shipped history, so every new
migration must sort after the last shipped one. A new migration also appends
its id to `GameServiceSchema.FrozenHistory` and its id and SQL hash to
`MigrationHistoryTests`.

The migrations create every object in the `public` schema, and
`bootstrap_character_item_state` pins its `search_path` to `public`. A
GameService database therefore needs a database of its own rather than a
schema, which is why every PostgreSQL test gets its own database, as
[Backend Tests](../tests/README.md#databases-and-redis) describes.

## Configuration

Each module validates and owns the keys it reads, and services receive
immutable validated option records instead of reading `IConfiguration`:

| Keys | Owner |
| --- | --- |
| `ConnectionStrings:Postgres`, `ConnectionStrings:Redis`, `Database:*`, `HealthChecks:TimeoutMilliseconds` | `PlatformOptions` |
| `Auth:SessionLifetimeHours`, `RateLimiting:Authentication:*`, `ServiceAuthentication:SimulationWorkers:*`, `SIMULATION_WORKER_SERVICE_SECRET`, `RedisAcceleration:*` | `AccountsModuleOptions` |
| `Game:MaxCharactersPerAccount`, `Characters:AppearancePath` | `CharactersModuleOptions` |
| `Simulation:*`, `WorldData:WorldsPath`, `DevelopmentSimulationBots:*`, `ACTIVE_SIMULATION_BOTS_SECRET` | `PlacementModuleOptions` |
| `Quests:CatalogPath` | `QuestsModuleOptions` |
| `Items:*`, `StackStressFixtures:*`, `STACK_STRESS_FIXTURE_SECRET` | `ItemsModuleOptions` |
| `Content:*`, `RateLimiting:Content:*` | `ContentModuleOptions`, which also receives the item and quest catalog paths from the host |
| `Telemetry:*` | The host, through `TelemetryOptions` of `Mmorpg.Server.Telemetry` |

The host loader `GameServiceConfiguration.Load` first validates the required
keys of every module together and throws one `GameService configuration is
invalid:` error that lists every problem. It then reads the self-contained
option sets in a fixed order, each with its own error message: the World
catalog, Development simulation bots, stack stress fixtures, NPC item
lifecycle, item operations, Redis acceleration, the item catalog source, the
quest catalog source, telemetry, the content options, and the Development item
packages. Worker credentials keep the
original key casing, so only an exact worker id matches, and the shared worker
secret applies only when no per-worker secret exists. Items reads the worker
heartbeat timeout through Placement's `SimulationWorkerLeasePolicy` and writes
it into the persisted durable Mob corpse command, so it is part of that
command's idempotency payload. The keys and their defaults are in
[Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults).

## Cross-Module Statements

These are the SQL statements and contract calls that cross a module boundary,
and how each one crosses. Acc is Accounts, Chr is Characters, Plc is Placement,
Qst is Quests, and Itm is Items. R is read, W is write, and L is lock. Code comments refer to
the statement ids.

| # | Statement | Foreign access | How it crosses |
| --- | --- | --- | --- |
| S1 | Login session replacement marks simulation sessions | Plc W | `IAccountSessionLifecycleHandler.OnSessionsReplacedAsync` |
| S2 | Login session replacement consumes join tickets | Plc W | same handler, after S1 |
| S3 | Session revoke marks simulation sessions | Plc W | `IAccountSessionLifecycleHandler.OnSessionRevokedAsync` |
| S4 | Session revoke consumes join tickets | Plc W | same handler, after S3 |
| S5 | Character create locks the account | Acc L | `IAccountLocks` |
| S6 | Character create bootstraps item state | Itm W | `ICharacterLifecycleHandler`, implemented by Items |
| S7 | Join and ticket consume lock the account | Acc L | `IAccountLocks` |
| S8 | Join locks the owned active character | Chr L | `ICharacterLocks.LockOwnedActiveForUpdateAsync` |
| S9 | Ticket consume locks the character | Chr L | `ICharacterLocks.LockForUpdateAsync` |
| S10 | Ticket consume reads character name and account session | Chr R, Acc R | `CharactersSql` and `AccountsSql` fragments in one statement |
| S11 | Ticket consume locks carry state | Itm L | `ISimulationCharacterStateProvider.LockForSessionStartAsync` |
| S12 | Session heartbeat marks a session whose account session expired and reads carry state | Acc R, Itm R | account fragment, then `ISimulationCharacterStateProvider.GetAsync` |
| S13 | Session heartbeat, the session authority, and the look change read the revocation reason for how the account session ended | Acc R | `AccountsSql.IsActiveOrUnboundSession` and `AccountsSql.RevocationReasonOf` in `SimulationAccountSessionEndSql` |
| S14 | Session release reads carry state | Itm R | `ISimulationCharacterStateProvider.GetAsync` |
| S15 | World rebind counts open corpses | Itm R | `IShardWorldLocalStateProbe` |
| S16 | Item state backfill lists active characters | Chr R | `CharactersSql.ActiveCharacters` |
| S17 | Carry recompute batch filters active characters | Chr R | `CharactersSql.ActiveCharacters` |
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
| S30 | Insurance charges character currency | Chr L, W | `ICharacterWallet.DebitAsync` with an `insurance` ledger row in `ItemPolicyCommandHandler` |
| S32 | Operation claims reference the actor account and character | Acc R, Chr R | `AccountsSql.ExistingIdOrNull`, `CharactersSql.ExistingIdOrNull` in `ItemOperationLedger` |
| S33 | Death reads the character name and Shard | Chr R, Plc R | `CharactersSql.ActiveCharacters` and `PlacementSql.ShardExists` in `PlayerDeathCommandHandler` |
| S35 | Recovery expiry scan skips deleted characters | Chr R | `CharactersSql.ActiveCharacters` |
| S36 | Inventory reads check character ownership | Chr R | `CharactersSql.ActiveCharacters` |
| S37 | Development item tool lists characters | Chr R, Acc R, Plc R | `CharactersSql.ActiveCharacters`, `AccountsSql.UsernameOf`, `PlacementSql.HasActiveSimulationSession` |
| S38 | Development item tool loads a target | Chr R, Plc R | `CharactersSql.ActiveCharacters`, `PlacementSql.HasActiveSimulationSession` |
| S39 | The Development item tool loads and validates a target | Chr R, Plc R | `CharactersSql.ActiveCharacters`, `PlacementSql.HasActiveSimulationSession` |
| S40 | Baseline migrations #1 and #6 | mixed DDL | R4, frozen |
| S41 | Equipment checks the level requirement against the character level | Chr R | `CharactersSql.LevelOf` in `ItemSlotPlacement` |
| S42 | The Development item tool sets a character's level | Chr L, W | `ICharacterProgression.SetLevelAsync` in `DevelopmentItemToolService` |
| S43 | Join and reconnect compare the worker's item catalog revision with the mirrored one | Itm R | `IItemCatalogRevisionProvider.GetCurrentRevisionAsync` in `ShardService` |
| S44 | Ticket consume, session heartbeat, and session release read the equipment with the carry state | Itm R | the same `ISimulationCharacterStateProvider` statement as S11, S12, and S14 |
| S45 | A vendor purchase debits character currency | Chr L, W | `ICharacterWallet.DebitAsync` in Economy's `VendorCommandHandler` |
| S46 | A vendor sale credits character currency | Chr L, W | `ICharacterWallet.CreditAsync` in Economy's `VendorCommandHandler` |
| S47 | Character create credits the starting balance | Chr L, W | `ICharacterWallet.CreditAsync` in `CharacterItemStateLifecycleHandler` |
| S48 | The item-state snapshot reads the balance | Chr R | `CharactersSql.CurrencyOf` in `ItemQueryService` |
| S49 | The Development item tool grants currency | Chr L, W | `ICharacterWallet.CreditAsync` in `DevelopmentItemToolService` |
| S50 | The Development item tool lists each character's balance | Chr R | `CharactersSql.CurrencyOf` in `DevelopmentItemToolService` |
| S51 | A committed item transaction reports each touched character's balance | Chr R | `CharactersSql.CurrencyOf` in `ItemTransactionFinalizer` |
| S52 | Ticket consume reads the character's look | Chr R | `ICharacterAppearances.ReadAsync` in `ShardService` |
| S53 | The worker appearance route lowers or raises the headwear, or stores a development look | Chr L, W | `ICharacterAppearances.SetHeadwearLoweredAsync` and `SetDevelopmentLookAsync` in `SimulationAppearanceService` |
| S54 | Durable corpse responses read the looks of their source characters, deleted ones too | Chr R | `ICharacterAppearances.ReadManyAsync` in `CorpseService` |
| S55 | The quest log read checks character ownership | Chr R | `CharactersSql.ActiveCharacters` in `QuestLogQueryService` |
| S56 | Quest commands, also the Admin menu's quest tools, run in the item transaction kernel: they change items, read the character level, and report the acting character's state | Itm R, W; Chr R | `QuestCommandSource` registers them through `IItemCommandSource`; `SimulationQuestMutationService` runs them through `IItemTransactions` and reads the state through `SimulationCharacterStates`; `QuestCommandHandler`, `QuestItemCommandHandler`, and `QuestLineage` use `ItemTransactionContext` and the item primitives; the level through `ItemSlotPlacement.LoadCharacterLevelAsync`, the `CharactersSql.LevelOf` statement of S41 |
| S57 | A quest command's result reports the quests it changed | Itm W | `ItemChangeJournal.SetSection` through `QuestLogSection` in `QuestLogChanges`; the kernel stores the section with the result |
| S58 | A gathering yield counts for the gather objectives of current steps | Qst R, W | Quests' `IQuestGatherCredit` in Economy's `GatherResourceCommandHandler`, implemented by `QuestGatherCredit` |
| S59 | A quest turn-in credits its Gold reward | Chr L, W | `ICharacterWallet.CreditAsync` with a `quest_reward` ledger row in `QuestCommandHandler` |
| S60 | Join and reconnect compare the worker's quest catalog revision with the mirrored one | Qst R | `IQuestCatalogRevisionProvider.GetCurrentRevisionAsync` in `ShardService` |
| S61 | The worker quest log read checks session, worker, and assignment | Plc R, Acc R | `ISimulationSessionAuthority.GetReadAuthorityAsync` in `SimulationQuestLogService` |
| S62 | A corpse's quest drop asks what its quest asks for | Qst R | `IQuestDropDemand` in `CorpseQuestDrops`, implemented by Quests' `QuestDropDemand` with `IQuestDefinitions` and `ICharacterQuestLog.ReadAsync` |
| S63 | The worker quest drop read checks session, worker, and assignment, and reads what the quests still need and what the owner already has | Plc R, Acc R, Itm R | `ISimulationSessionAuthority.GetReadAuthorityAsync`, then `QuestDropDemand` and Items' `LineageNeedPlan`, in `SimulationQuestDropService` |
| S64 | Content's startup check compares the served catalogs with the mirrored revisions | Itm R, Qst R | `IItemCatalogRevisionProvider` and `IQuestCatalogRevisionProvider` in one read transaction in `ContentRevisionCheck` |
| S65 | Items' seeder has the quest catalog's item references checked before it mirrors the item catalog | Qst R | `IItemCatalogReferences` in `ItemsDatabaseSeeder`, implemented by Quests' `QuestItemCatalogReferences` on the loaded quest catalog |
| S66 | Vendor trades and gathering yields run in the item transaction kernel: they change items, read vendor profiles and tool kinds of the mirrored catalog, and report the acting character's state | Itm R, W | `EconomyCommandSource` registers them through `IItemCommandSource`; Items' worker item route hands their kinds to Economy's `ISimulationItemOperationHandler` implementations, which run them through `SimulationItemOperation.RunAsync`; `VendorCommandHandler` and `GatherResourceCommandHandler` use `ItemTransactionContext` and the item primitives |
| S67 | Ticket consume reads the character's saved place and vitals | Chr R | `ICharacterSimulationStates.ReadAsync` in `ShardService` |
| S68 | A session release with a final state locks the character and saves the state in the release's transaction | Chr L, W | `ICharacterSimulationStates.LockForSaveAsync` and `SaveManyAsync` in `SimulationSessionService` |
| S69 | A checkpoint locks the characters of the worker runtime's active sessions and saves their states in one statement | Chr L, W | `ICharacterSimulationStates.LockForSaveAsync` and `SaveManyAsync` in `SimulationSessionService` |

Notes on specific statements:

- The wallet's `FOR NO KEY UPDATE` lock in S30, S45, S46, and S59 falls on a character
  row that the item transaction already locked through `ICharacterLocks`, so it
  adds no lock and keeps the lock order.
- S12 and S14 read the carry state through `ISimulationCharacterStateProvider`
  on the same connection right after the lease update commits, so the worker may
  see an item revision that committed in between, which is newer, never older. A
  missing item state still returns `401` and the lease update still commits.
- S15 counts open durable corpses in a second statement in the same transaction
  as Placement's own blocker count. The transaction holds the Shard row
  `FOR UPDATE`, which blocks the foreign key lock that a new corpse needs, so
  the count stays exact.

`TableOwnershipTests` scans the GameService sources for `from`, `join`, `into`,
and `update` on owned tables and fails on any foreign table outside the frozen
migrations.

## Guard Tests

The guard tests live in `tests/Server/Mmorpg.GameService.Tests/Architecture`,
except `GameServiceTelemetryTests` in the root of that project and
`ItemTransactionKernelBoundaryTests` and `ItemAreaBoundaryTests`, which live
with the kernel tests in
`tests/Server/Mmorpg.GameService.Items.Tests/ItemTransactions`. The kernel
boundary test checks the item command handlers of every module.

| Test | What it protects |
| --- | --- |
| `MigrationHistoryTests` | Migration ids, order, SQL hashes, the merge rules, and the ids recorded by a fresh database |
| `MeterInstrumentNamesTests` | The `Mmorpg.GameService.Redis`, `Mmorpg.GameService.Items`, and `Mmorpg.GameService.Placement` meters and every `game_service.*` instrument that the dashboards and alerts read |
| `TableOwnershipTests` | Rules R1 to R4 for SQL in source files |
| `NamespaceBoundaryTests` | A module references other modules only through lower modules' `Contracts` namespaces and Platform |
| `RouteTableSnapshotTests` | Every route with its methods, authorization policy, rate-limit policy, and no-store metadata. ASP.NET Core matches routes by template precedence and HTTP method, never by registration order, so the snapshot is the routing contract |
| `ModuleDependencyGraphTests` | The compiled references between the GameService assemblies and the host assembly name |
| `ModulePublicSurfaceTests` | Each module's exported types and Platform's internal middleware |
| `ModuleCompositionTests` | Exactly one implementation of every single-implementation SPI and contract, from the module that implements it, including Items' `IQuestDropDemand` and `IItemCatalogReferences` and Quests' `IQuestGatherCredit`, all from Quests, and startup failure for a missing or duplicate SPI |
| `GameServiceTelemetryTests` | Every class that owns a `Meter` publishes `MeterName` and is declared by its module's `MeterNames`, and the dashboards and alert rules reference only published `game_service_*` metrics |
| `ItemTransactionKernelBoundaryTests` | The item transaction kernel boundary described in [Items Transaction Kernel](#items-transaction-kernel), with the handler rules for the item command handlers of every module |
| `ItemAreaBoundaryTests` | The corpse area and kernel rules of [Areas](#areas), from the corpse area's file list |

The item transaction kernel also has golden tests:
`ItemTransactionTraceGoldenTests` compares, byte for byte, the statements and
transaction boundaries that Npgsql logs for one command or kernel path, the
result, the stored operation, and its audit journal, and
`QuestTransactionTraceGoldenTests` in `Mmorpg.GameService.Quests.Tests` and
`EconomyTransactionTraceGoldenTests` in `Mmorpg.GameService.Economy.Tests` do
the same for the quest commands they cover and for the vendor and gathering
commands. Any change to item
transaction SQL, its order, or its journal therefore shows up as a reviewed
golden diff, as [Golden Files](../tests/README.md#golden-files) describes.

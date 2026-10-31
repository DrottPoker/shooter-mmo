# GameService Modules

GameService is one ASP.NET Core process and one deployable whose code is a
compiler-enforced modular monolith: a host and five module projects. This
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
is split into six projects whose references enforce one dependency order:

```text
Platform <- Accounts <- Characters <- Placement <- Items <- host
```

| Project | Path | References | Owns |
| --- | --- | --- | --- |
| `Mmorpg.GameService.Platform` | `src/Server/GameService.Platform` | `Mmorpg.Server.Common` | Problem Details results and the HTTP pipeline, token hashing, the Redis connection, `PostgresTransactionScope`, the migration runner, `IDatabaseSeeder`, platform options, and `IGameServiceModule`. No domain model. |
| `Mmorpg.GameService.Accounts` | `src/Server/GameService.Accounts` | Platform | Accounts, account sessions, account and worker authentication, authorization policies, login and registration rate limits, and Redis session acceleration |
| `Mmorpg.GameService.Characters` | `src/Server/GameService.Characters` | Platform, Accounts | Characters, ownership, character currency with its ledger, character level, and character looks |
| `Mmorpg.GameService.Placement` | `src/Server/GameService.Placement` | Platform, Accounts, Characters, `Mmorpg.Server.Common`, `Mmorpg.WorldData` | World definitions, Fleets, Nodes, Shards, the worker registry, SimulationAssignments, join tickets, simulation session leases, the worker appearance route, and Development simulation bots. It decides where a character plays right now. |
| `Mmorpg.GameService.Items` | `src/Server/GameService.Items` | Platform, Accounts, Characters, Placement, `Mmorpg.WorldData` | The item catalog with item values, tool kinds, and vendor profiles, item state, item transactions including vendor trades, gathering yields, and ammunition batches, corpses, the death partition, Recovery, policies, item operations maintenance, the stack stress fixtures, and the Development item tools |
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

Platform is public except its middleware and exception handler.

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
   single-implementation SPI has exactly one implementation.
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
   mirrors the item catalog and then back-fills character item state.
6. Run a `--dev-items-*` command through `ItemsCommandLine` and exit, if one is
   given. Hosted services never start in that mode.
7. Map the health routes, then each module's routes, and run.

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
| Characters | `characters`, including `currency` and `level`, `character_currency_ledger`, and `character_appearances` |
| Placement | `world_definitions`, `fleets`, `simulation_nodes`, `shards`, `simulation_workers`, `simulation_assignments`, `simulation_join_tickets`, `character_simulation_sessions` |
| Items | every item, container, catalog, Recovery, operation, corpse, and death table, including the catalog's `item_tool_kinds`, `item_definition_tool_kinds`, `vendor_profiles`, `vendor_offers`, and `vendor_sale_categories`, and `live_mob_loot_grants`; the `validate_character_item_state_containers` trigger function, `bootstrap_character_item_state`, the catalog advisory lock `7104202607151230`, and the per-account Secure Container entitlement advisory lock |

Foreign keys between modules follow the dependency order only: Characters to
Accounts, Placement to Accounts and Characters, and Items to Accounts,
Characters, and Placement (`corpses.shard_id`). For the same reason
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
| `IAccountSessionLifecycleHandler` | Accounts (SPI) | implemented by Placement | Release simulation sessions and join tickets when sessions are replaced or revoked |
| `AccountsSql` | Accounts | Placement, Items | Active account session, revocation reason, existing account id, and username fragments |
| `ICharacterLocks` | Characters | Placement, Items | Placement `FOR UPDATE` locks and item mutation `FOR NO KEY UPDATE` locks in ascending id order |
| `ICharacterWallet` | Characters | Items | Debit or credit character currency with a `CurrencyChangeSource` and one ledger row, for insurance, vendor trades, the starting balance, and the Development item tool |
| `ICharacterProgression` | Characters | Items | Set a character's level for the Development item tool |
| `ICharacterAppearances` | Characters | Placement, Items | Read a character's body model, look, and headwear, also of deleted characters for their corpses, lower or raise the headwear, and store a development look, each checked against the appearance content |
| `ICharacterLifecycleHandler` | Characters (SPI) | implemented by Items | Create item state and credit the starting balance for a new character |
| `CharactersSql` | Characters | Placement, Items | The active character derived table, the existing character id fragment, the character level fragment, and the currency balance fragment |
| `SimulationWorkerLeasePolicy` | Placement | Items | The worker heartbeat timeout |
| `ISimulationSessionAuthority` | Placement | Items | Lock a simulation session for an item mutation, or read it with its worker lease and assignment |
| `ISimulationWorkerAuthority` | Placement | Items | Read or lock a worker's runtime, lease, and Shard assignment |
| `ISimulationPresence` | Placement | Items | Find a character that is in the world |
| `PlacementSql` | Placement | Items | Shard existence and active simulation session fragments |
| `ISimulationCharacterStateProvider` | Placement (SPI) | implemented by Items | Lock or read a character's carry state and equipment for its simulation session |
| `IItemCatalogRevisionProvider` | Placement (SPI) | implemented by Items | Read the mirrored item catalog revision that a placed worker must run |
| `IShardWorldLocalStateProbe` | Placement (SPI) | implemented by Items | Count open durable corpses that block a World rebind |
| `IDatabaseSeeder` | Platform | implemented by Characters and Items | Verify or seed module data after the migrations: Characters checks the stored looks against the appearance content, and Items mirrors the item catalog and back-fills character item state |

Every contract and every SPI except `IDatabaseSeeder` has exactly one
implementation, registered as a stateless singleton, from the module that owns
the data. The host composition fails at startup when one of the
single-implementation SPIs has no implementation or more than one, because a
missing one would silently skip required work. `IDatabaseSeeder` has one
implementation per module that verifies or seeds data.

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
`vendor_purchase`, `vendor_sale`, `insurance`, and `development`. The ledger
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

## Items Transaction Kernel

Every durable item mutation runs through one item transaction kernel in
`src/Server/GameService.Items/Transactions`:

| Folder | Namespace | Contents |
| --- | --- | --- |
| `Transactions/` | `Mmorpg.GameService.Items` | `ItemTransactionService`, the facade with one `ExecuteAsync` overload per command; the command, actor, and result contracts; the error codes and `ItemTransactionRejection` |
| `Kernel/` | `Mmorpg.GameService.Items.Transactions` | `ItemTransactionKernel`, `IItemCommandHandler<TCommand>`, admission, the canonical request, the operation ledger, outcomes, telemetry, and the persisted JSON options |
| `Context/` | `Mmorpg.GameService.Items.Transactions` | `ItemTransactionContext` with its session and facets, the kernel-owned finalizer, the simulation authority, and the lock types |
| `Domain/` | `Mmorpg.GameService.Items.Transactions.Domain` | Rules and writes shared by several handlers: policy states, Bag aggregate guards, Recovery deliveries, durable corpse records, corpse custody and transfers, the Loot all state, transform and Mob loot rules and writes, new item instances, quantity consumption, and carried acquisition |
| `Handlers/` | `Mmorpg.GameService.Items.Transactions.Handlers` | One stateless handler class per command family; `PlayerDeath/` holds the death handler with its corpse layout writer, custody transfers, and partition models |

`Context/CarryStateCalculator` is the one carried-weight rule, shared with the
startup recompute in `CharacterItemStateBootstrapper`. It finds the character's
carried containers and equipped Bag with indexed lookups, as the death
partition does for its source items; a partial index on
`item_instances(equipped_character_id, equipment_slot_id)` supports both.
`Context/ItemOwnershipSql` is the one definition of the character that owns an
item. Outside the kernel, `Development/` holds the process-local Development
item tool and its packages, and `StackStress/` holds the stack stress fixtures,
which register only when `StackStressFixtures:Enabled` is true.

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

The context resolves current definitions and slot data from the mirrored
catalog but delegates stack, equipment, Bag, Secure Container, policy
capability, and integer-weight decisions to the shared WorldData rules. The
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

`ItemTransactionKernelBoundaryTests` pins the boundary: only `Kernel/` begins,
commits, or rolls back a transaction, uses the savepoint or `set_config`, or
touches `item_operations`; handlers hold only readonly injected state; no
transaction code runs database commands in parallel; handlers take locks in the
order below; only `ItemTransactionRejection` throws a rejection; the kernel never
uses HTTP API error codes; and only the types named in
[Cross-Module Statements](#cross-module-statements) use another module's
contracts or SQL fragments.

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
| `equip`, `unequip` | `EquipItemCommand`, `UnequipItemCommand` | `ItemEquipmentCommandHandler` | Worker route; Development item packages also equip |
| `split_stack`, `merge_stacks` | `SplitItemStackCommand`, `MergeItemStacksCommand` | `ItemStackCommandHandler` | Account and worker routes |
| `consume_quantity` | `ConsumeItemQuantityCommand` | `ItemStackCommandHandler` | No caller outside tests yet |
| `consume_equipped_ammunition` | `ConsumeEquippedAmmunitionCommand` | `AmmunitionConsumptionCommandHandler` | Worker ammunition route |
| `destroy` | `DestroyItemCommand` | `ItemDestructionCommandHandler` | Account and worker routes |
| `swap_bag_aggregates` | `SwapBagAggregatesCommand` | `BagAggregateCommandHandler` | No caller outside tests yet; it swaps complete Bag aggregates, also between characters |
| `add_recovery_delivery`, `claim_recovery_delivery` | `AddRecoveryDeliveryCommand`, `ClaimRecoveryDeliveryCommand` | `RecoveryDeliveryCommandHandler` | Development item packages add deliveries; the worker route claims them |
| `change_secure_container_tier` | `ChangeSecureContainerTierCommand` | `SecureContainerTierCommandHandler` | Account route |
| `apply_item_policy`, `remove_insurance_policy` | `ApplyItemPolicyCommand`, `RemoveInsurancePolicyCommand` | `ItemPolicyCommandHandler` | Worker route, for the insurance capability |
| `abandon_quest_items` | `AbandonQuestItemsCommand` | `QuestItemCommandHandler` | Worker route, for the quest capability |
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
| `vendor_purchase`, `vendor_sale` | `PurchaseFromVendorCommand`, `SellToVendorCommand` | `VendorCommandHandler` | Worker route, for the vendor capability |
| `gather_resource` | `GatherResourceCommand` | `GatherResourceCommandHandler` | Worker route, for a resource node |

`ItemOperationKinds.IsRetriedSystemOperation` names the retried system
operations, whose rejections roll back completely instead of being stored, and
`ItemOperationAdmission` checks the operation id rule and the payload limit
before any database access. [Item Transactions](SERVICE_FEATURES.md#item-transactions)
describes both rules as clients see them.

### Vendor, Gathering, And Ammunition Commands

Both vendor commands require the vendor access of the live session:
`ItemTransactionLiveAccess.VendorProfileId` must equal the command's vendor. The
worker sets it from the vendor capability it validated, never from a client
value, and GameService loads that profile with its offers and sale categories
from the active mirrored catalog. `VendorPricing` from `Mmorpg.WorldData`
computes the total, rounded up for a purchase and down for a sale, so GameService
and Unity charge and show the same price.

- `PurchaseFromVendorCommand` checks the expected character revision, the
  vendor offer, a quantity within one stack, and the expected total price,
  debits the wallet with a `vendor_purchase` ledger row, and places the items
  through carried acquisition.
- `SellToVendorCommand` checks the expected character and item revisions, takes
  all or part of one carried, unequipped stack, deletes a whole stack with an
  `item_destructions` row with reason `vendor_sale` or reduces a partial one,
  and credits the wallet with a `vendor_sale` ledger row when the price is
  positive.
- `GatherResourceCommand` requires the SimulationWorker actor, checks that an
  item with the required active tool kind is equipped in the `tool` slot,
  refuses a Bag or quest item as yield, and places the yield through carried
  acquisition. It takes no expected character revision, because gathering must
  not fail when the player moves an item meanwhile; the live session fence and
  the worker's operation id, which it reuses when it retries an uncertain
  outcome, replace it.
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
account's Secure Container entitlement key, or the death event or Mob corpse
key), then its characters and their item states in ascending id order, then the
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

The nine baseline migrations stay together in the host's
`LegacyBaselineMigrations` because #1 and #6 touch several modules' tables.
Each module lists its own migrations in `IGameServiceModule.Migrations`, from
its `CharactersSchemaMigrations`, `PlacementSchemaMigrations`, or
`ItemsSchemaMigrations` list. `GameServiceSchema` merges the host list with
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
| `Items:*`, `StackStressFixtures:*`, `STACK_STRESS_FIXTURE_SECRET` | `ItemsModuleOptions` |
| `Telemetry:*` | The host, through `TelemetryOptions` of `Mmorpg.Server.Telemetry` |

The host loader `GameServiceConfiguration.Load` first validates the required
keys of every module together and throws one `GameService configuration is
invalid:` error that lists every problem. It then reads the self-contained
option sets in a fixed order, each with its own error message: the World
catalog, Development simulation bots, stack stress fixtures, NPC item
lifecycle, item operations, Redis acceleration, the item catalog source,
telemetry, and the Development item packages. Worker credentials keep the
original key casing, so only an exact worker id matches, and the shared worker
secret applies only when no per-worker secret exists. Items reads the worker
heartbeat timeout through Placement's `SimulationWorkerLeasePolicy` and writes
it into the persisted durable Mob corpse command, so it is part of that
command's idempotency payload. The keys and their defaults are in
[Configuration Defaults](SERVICE_FEATURES.md#configuration-defaults).

## Cross-Module Statements

These are the SQL statements and contract calls that cross a module boundary,
and how each one crosses. Acc is Accounts, Chr is Characters, Plc is Placement,
and Itm is Items. R is read, W is write, and L is lock. Code comments refer to
the statement ids.

| # | Statement | Foreign access | How it crosses |
| --- | --- | --- | --- |
| S1 | Login session replacement releases simulation sessions | Plc W | `IAccountSessionLifecycleHandler.OnSessionsReplacedAsync` |
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
| S30 | Insurance charges character currency | Chr L, W | `ICharacterWallet.DebitAsync` with an `insurance` ledger row in `ItemPolicyCommandHandler` |
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
| S43 | Join and reconnect compare the worker's item catalog revision with the mirrored one | Itm R | `IItemCatalogRevisionProvider.GetCurrentRevisionAsync` in `ShardService` |
| S44 | Ticket consume, session heartbeat, and session release read the equipment with the carry state | Itm R | the same `ISimulationCharacterStateProvider` statement as S11, S12, and S14 |
| S45 | A vendor purchase debits character currency | Chr L, W | `ICharacterWallet.DebitAsync` in `VendorCommandHandler` |
| S46 | A vendor sale credits character currency | Chr L, W | `ICharacterWallet.CreditAsync` in `VendorCommandHandler` |
| S47 | Character create credits the starting balance | Chr L, W | `ICharacterWallet.CreditAsync` in `CharacterItemStateLifecycleHandler` |
| S48 | The item-state snapshot reads the balance | Chr R | `CharactersSql.CurrencyOf` in `ItemQueryService` |
| S49 | The Development item tool grants currency | Chr L, W | `ICharacterWallet.CreditAsync` in `DevelopmentItemToolService` |
| S50 | The Development item tool lists each character's balance | Chr R | `CharactersSql.CurrencyOf` in `DevelopmentItemToolService` |
| S51 | A committed item transaction reports each touched character's balance | Chr R | `CharactersSql.CurrencyOf` in `ItemTransactionFinalizer` |
| S52 | Ticket consume reads the character's look | Chr R | `ICharacterAppearances.ReadAsync` in `ShardService` |
| S53 | The worker appearance route lowers or raises the headwear, or stores a development look | Chr L, W | `ICharacterAppearances.SetHeadwearLoweredAsync` and `SetDevelopmentLookAsync` in `SimulationAppearanceService` |
| S54 | Durable corpse responses read the looks of their source characters, deleted ones too | Chr R | `ICharacterAppearances.ReadManyAsync` in `CorpseService` |

Notes on specific statements:

- The wallet's `FOR NO KEY UPDATE` lock in S30, S45, and S46 falls on a character
  row that the item transaction already locked through `ICharacterLocks`, so it
  adds no lock and keeps the lock order.
- S12 and S14 read the carry state through `ISimulationCharacterStateProvider`
  on the same connection right after the lease update commits, so the worker may
  see an item revision that committed in between, which is newer, never older. A
  missing item state still returns `401` and the lease update still commits.
- S15 counts open durable corpses in a second statement in the same transaction
  as Placement's own blocker count. The transaction holds the Shard row lock,
  which blocks the foreign key lock that a new corpse needs, so the count stays
  exact.
- S18 builds the Secure Container entitlement advisory key from the account id
  that character creation or the backfill supplies. A character's account never
  changes, so the key is the same as one read from `characters`.

`TableOwnershipTests` scans the GameService sources for `from`, `join`, `into`,
and `update` on owned tables and fails on any foreign table outside the frozen
migrations.

## Guard Tests

The guard tests live in `tests/Server/Mmorpg.GameService.Tests/Architecture`,
except `GameServiceTelemetryTests` in the root of that project and
`ItemTransactionKernelBoundaryTests`, which lives with the kernel tests in
`tests/Server/Mmorpg.GameService.Items.Tests/ItemTransactions`.

| Test | What it protects |
| --- | --- |
| `MigrationHistoryTests` | Migration ids, order, SQL hashes, the merge rules, and the ids recorded by a fresh database |
| `MeterInstrumentNamesTests` | The `Mmorpg.GameService.Redis`, `Mmorpg.GameService.Items`, and `Mmorpg.GameService.Placement` meters and every `game_service.*` instrument that the dashboards and alerts read |
| `TableOwnershipTests` | Rules R1 to R4 for SQL in source files |
| `NamespaceBoundaryTests` | A module references other modules only through lower modules' `Contracts` namespaces and Platform |
| `RouteTableSnapshotTests` | Every route with its methods, authorization policy, rate-limit policy, and no-store metadata. ASP.NET Core matches routes by template precedence and HTTP method, never by registration order, so the snapshot is the routing contract |
| `ModuleDependencyGraphTests` | The compiled references between the GameService assemblies and the host assembly name |
| `ModulePublicSurfaceTests` | Each module's exported types and Platform's internal middleware |
| `ModuleCompositionTests` | Exactly one implementation of every single-implementation SPI and contract, from its owner module, and startup failure for a missing or duplicate SPI |
| `GameServiceTelemetryTests` | Every class that owns a `Meter` publishes `MeterName` and is declared by its module's `MeterNames`, and the dashboards and alert rules reference only published `game_service_*` metrics |
| `ItemTransactionKernelBoundaryTests` | The item transaction kernel boundary described in [Items Transaction Kernel](#items-transaction-kernel) |

The item transaction kernel also has golden tests:
`ItemTransactionTraceGoldenTests` compares, byte for byte, the statements and
transaction boundaries that Npgsql logs for one command or kernel path, the
result, the stored operation, and its audit journal. Any change to item
transaction SQL, its order, or its journal therefore shows up as a reviewed
golden diff, as [Golden Files](../tests/README.md#golden-files) describes.

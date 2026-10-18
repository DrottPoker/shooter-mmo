# Vendor And Gathering Plan

Last updated: 2026-10-18

Status: Implemented. The product owner accepted every recommendation, V1 to V10,
on 2026-10-18, and the slice was implemented on one branch the same day. The
design below is kept as the record of the decisions; [Implementation
Notes](#implementation-notes) lists where the implementation differs from it.

## Purpose

This plan prepares the MVP slice "Vendor and gathering" from
[MVP Specification](MVP_SPEC.md#mvp-slices), together with its
[Economy MVP](MVP_SPEC.md#economy-mvp) and
[Gathering MVP](MVP_SPEC.md#gathering-mvp). It reads the current foundation,
proposes the architecture, lists the product decisions with a recommendation
and alternatives, and orders the implementation by dependency.

The slice adds the part of the core loop where a player gathers or loots a
resource, sells it to an NPC vendor, and buys basic supplies:

- One NPC vendor capability that sells basic supplies and buys loot and
  resources.
- Persistent character currency that stays with the character on death.
- One resource node type, an Iron Vein, mined with `tool.starter_pickaxe`
  equipped in the `tool` slot, that yields `material.iron_ore`.
- Gathering through the existing item transaction kernel.

## Out Of Scope

- Profession experience and levels, gathering success chances, and other
  professions (decision V10).
- Gameplay rule areas. Nodes and the vendor work anywhere until the
  [rule area slice](MVP_SPEC.md#gameplay-rule-areas) exists.
- Player trading, auction house, dynamic prices, limited vendor stock,
  reputation discounts, and buyback (decision V6).
- Crafting and the use of iron ore in recipes.
- Showing other players' gathering animation. The player's own progress is
  shown; replicated character actions belong to the combat presentation work.
- Moving the insurance price from service configuration into content. It can
  reuse the item values from this slice later.

## Current Foundation

| Area | What exists | What is missing |
| --- | --- | --- |
| Currency | `characters.currency bigint not null default 0`, owned by the Characters module. `ICharacterWallet.DebitAsync` locks the row and debits in the caller's transaction; insurance is its only caller and audits `item_insurance_currency_charged`. The death partition never reads or writes currency, and `DeathLootIntegrationTests` pins that. The client sees the balance only in the character list. | A credit operation, a currency ledger, a database check that the balance stays non-negative, the balance in the in-world inventory snapshot, a display name, and any way for a new character to get currency. Local development sets currency with direct SQL. |
| Vendor capability | Mira the Quartermaster (`npc.city_services`) offers `services.vendor` ("Trade"). The protocol has `WorldActorCapabilityKind.Vendor = 2`. `ItemPolicyCapabilities.CanSellToVendor` is computed and false for protected and insured items. | Prices, offers, a vendor handler (the worker answers `world_interaction_capability_deferred`), and vendor item commands. |
| Item content | Ten fantasy definitions in catalog format 3 with weight, stack size, slots, tags, and policies. | Any value or price field. |
| Item creation | `GrantItemCommand` creates one new instance in one named container; slot selection takes the first empty compatible slot and never merges into an existing stack. `MobLootGrantCommandHandler` grants worker-originated loot with a deterministic grant id into a client-chosen slot. | A grant that tops up existing stacks and chooses a carried destination by itself, which gathering and buying need. |
| World actors | Kinds `npc` and `mob`. A capability is `{id, kind, displayName}`. `IsActive = false` makes an actor unavailable, advances its revisions, closes every lease on it, and replicates through `WorldActorState`. | A third kind for interactable objects, a capability profile reference (the design names one, the code does not have it), any respawn execution (respawn profiles are compiled but never read, and `SetActive(true)` has no production caller), and any per-instance gameplay state. |
| Worker boundary | `POST /api/simulation-sessions/{id}/item-operations` with exact session, worker, runtime, Shard, and token fencing. Live access flags `Bank`, `RecoveryStorage`, `InsuranceNpc`, and `QuestNpc` come from the worker's validated interaction lease. Insurance reads its price from `Items:NpcLifecycle:InsurancePrice`. | Vendor and gathering operation kinds and access. |
| SimulationWorker | Owns interaction leases, range, line of sight, and capability dispatch. It knows each character's carry state but not its equipment, and it does not load the item catalog. | Resource node runtime state, a central timer queue, and a gathering loop. |
| Realtime protocol | Version 16 with 27 messages. Inventory contents travel over GameService HTTP and refresh by item-state revision. NPC services use `WorldInteractionIntent` `CapabilityAction` with `NpcLifecycleActionKind` and fixed item fields. | Offer, quantity, and price fields, a resource node actor kind, and gathering progress. |
| Unity client | `TemporaryWorldInteractionPanel` draws Vendor as a generic button. `TemporaryInventoryPanel` has Bank, Recovery Storage, and corpse contexts. `WorldActorPresentationRegistry` maps presentation archetypes to Addressable prefabs. | A vendor context, a currency display, a resource node view, and gathering feedback. |

One unrelated drift was found while reading: migration
`202610171200_item_catalog_alignment` is in `ItemsSchemaMigrations` and
`MigrationHistoryTests` but missing from `GameServiceSchema.FrozenHistory` and
from the migration table in [GameService Modules](GAME_SERVICE_MODULES.md#migrations).
Step 3 adds the next migration and fixes both.

## Architecture

### Flows

```text
Buy or sell
  Unity Trade context (offers from the bundled catalog, prices from the shared rule)
    -> WorldInteractionIntent CapabilityAction VendorPurchase or VendorSale
    -> SimulationWorker: lease, 3.5 m range, line of sight, capability revision
    -> GameService item-operations: vendor_purchase or vendor_sale with the vendor profile id
    -> kernel: price from the catalog, wallet debit or credit with ledger row,
       item create or destroy, 140 percent hard cap, audit
    -> WorldInteractionResult with the item-state revision
    -> Unity refreshes inventory and balance over HTTP

Gather
  Unity left click on an Iron Vein -> walk into range -> Open -> CapabilityAction StartGathering
    -> SimulationWorker gathering activity, one cycle every 3 s from the central tick
    -> reserve one node charge -> GameService gather_resource
    -> kernel: tool tag in the tool slot, carried acquisition, hard cap, audit
    -> worker consumes the charge, sends GatheringState Yielded
    -> last charge: node inactive, every gatherer stops, central timer queue respawns it
```

### Currency

Currency stays a balance on the character, not an item. It has no weight, never
enters a corpse, cannot be looted, and is global across every Shard, as the MVP
and the death design already require.

- **Storage.** `characters.currency` stays the single balance and stays owned by
  Characters. A Characters migration adds `check (currency >= 0)` and a check
  against `CurrencyRules.MaximumBalance` (`1,000,000,000,000`), so an overflow
  is a stable `currency_limit_exceeded` rejection instead of an arithmetic
  error.
- **Ledger.** A new Characters table `character_currency_ledger` records every
  change: id, character id, source kind, source id, item operation id when
  there is one, signed delta, balance after, and time. It is append-only. A
  conservation test checks that each character's ledger deltas sum to its
  balance. Source kinds are `starting_balance`, `vendor_purchase`,
  `vendor_sale`, `insurance`, and `development`.
- **Contract.** `ICharacterWallet` gains `CreditAsync`, and both methods take a
  `CurrencyChangeSource(kind, sourceId, operationId)` and write the ledger row in
  the caller's transaction. They keep the existing rules: the caller's
  connection, `FOR NO KEY UPDATE` on the character, no commit. Insurance moves to
  the new signature and keeps its existing audit.
- **Revisions.** Every in-world currency change runs inside an item
  transaction, so it advances the character's item-state revision. The client
  already refreshes on that revision.
- **Read model.** `GET /api/characters/{id}/item-state` gains `currency`, read
  through a new `CharactersSql` fragment in the same repeatable-read snapshot, so
  the displayed balance always matches the displayed items.
- **Content.** The item catalog gains an `economy` block with the currency
  display name and the starting balance (decisions V2 and V3). Items applies the
  starting balance in its `ICharacterLifecycleHandler` through
  `ICharacterWallet.CreditAsync` in the character creation transaction.
  Existing characters keep their balance.
- **Development.** The Development item tool gains a currency grant
  (`--dev-items-currency <characterId> <amount>`, and a field in the Unity
  Inventory Item Grants window) that writes a `development` ledger row. It
  replaces the direct SQL in [Local Development](LOCAL_DEVELOPMENT.md), which
  would bypass the ledger.

### Item Value And Vendor Content

Prices are content, and GameService owns them.

- **Item value.** Catalog format 5 adds a required integer `value` (zero or
  more) to every item definition. Zero means no vendor buys the item. Value is
  not structural: it changes the catalog revision but not a definition's
  structural fingerprint, so a price change never needs an item migration.
- **Vendor profiles.** One file per vendor in
  `content/items/vendors/<vendor-id>.json`, compiled by `tools/ItemCatalogCompiler`
  into the item catalog, because offers and prices reference item definitions:

  ```json
  {
    "id": "vendor.city_general_store",
    "displayName": "General Store",
    "offers": [
      { "definitionId": "ammunition.bronze_arrow" },
      { "definitionId": "potion.minor_healing" },
      { "definitionId": "food.cooked_trout" },
      { "definitionId": "tool.starter_pickaxe" }
    ],
    "purchasePriceBasisPoints": 10000,
    "salePriceBasisPoints": 4000,
    "saleCategories": [ "ammunition", "armor", "bag", "food", "jewelry", "material", "potion", "tool", "weapon" ]
  }
  ```

  Prices are named from the player's side: the player purchases at
  `purchasePriceBasisPoints` of value and sells at `salePriceBasisPoints`.
- **One pricing rule.** `VendorPricing` in the shared `Mmorpg.WorldData` item
  domain computes the total purchase price (rounded up) and the total sale price
  (rounded down) with checked 64-bit integer arithmetic. Unity uses it to show
  prices and GameService uses it to charge them.
- **Compiler rules.** An offer names an active definition with a positive value,
  no default policy, and a category other than `quest_item`. A vendor has at most
  32 offers. Sale categories exist and exclude `quest_item`. Across every
  vendor, the highest sale rate is lower than the lowest purchase rate, so no
  buy and sell loop can create currency.
- **Actor reference.** An actor capability gains an optional `profileId`. A
  `vendor` capability must name a vendor profile, and every other NPC capability
  leaves it empty. `tools/WorldActorCompiler` reads the compiled item catalog to
  validate the reference, so the item catalog compiles first, as CI already
  orders it. Mira's `services.vendor` references `vendor.city_general_store`.
- **Client listing.** The opened capability summary carries the profile id. The
  client renders the offers from its bundled item catalog, which must already
  match the server's catalog revision. No listing message is needed for static
  offers. Every purchase and sale carries the total price the client showed, and
  GameService rejects a difference with `vendor_price_changed`, so a client can
  never be charged a price it did not see. Dynamic stock or reputation pricing
  can later add a capability-state message without changing the commands.

### Vendor Transactions

`VendorCommandHandler` handles two new kernel commands. Both require the
SimulationWorker actor with live vendor access for exactly the requested vendor
profile, which the worker sets only while the player's lease holds an available
`vendor` capability with that profile.

`PurchaseFromVendorCommand` (kind `vendor_purchase`):

1. Lock the character state with the expected character revision.
2. Resolve the vendor and offer from the active catalog, or reject
   `vendor_offer_unavailable`. Quantity is between 1 and the definition's stack
   size.
3. Compute the total with `VendorPricing` and compare it with the expected
   total, or reject `vendor_price_changed`.
4. Debit the wallet with a `vendor_purchase` ledger row, or reject
   `currency_insufficient`.
5. Place the items with the carried acquisition below, or reject
   `inventory_full`. The finalizer enforces the 140 percent hard cap.
6. Audit `vendor_purchase` with vendor, definition, quantity, price, and catalog
   revision.

`SellToVendorCommand` (kind `vendor_sale`):

1. Lock the character state and the item with their expected revisions.
2. The item must be in carried storage: permanent inventory, equipped Bag
   contents, or Secure Container. Equipped items, Bank items, and non-empty Bags
   are rejected.
3. `ItemPolicyRules.Evaluate(...).CanSellToVendor` must be true, which rejects
   protected and insured items with `item_policy_restricted`.
4. The category must be in the vendor's `saleCategories` and the value must be
   positive, or reject `vendor_will_not_buy`.
5. Quantity is between 1 and the stack quantity, so part of a stack can be sold.
   Compare the total with the expected total, or reject `vendor_price_changed`.
6. Reduce or delete the stack and write `item_destructions` with reason
   `vendor_sale`. Credit the wallet with a `vendor_sale` ledger row, or reject
   `currency_limit_exceeded`.
7. Audit `vendor_sale`.

Both commands are idempotent by operation id like every other item mutation, so
a retried request can never charge or pay twice.

### Carried Acquisition

Buying and gathering create items without a client-chosen slot. A new domain
rule, `CarriedAcquisition`, places a quantity into carried storage in one
deterministic, all-or-nothing pass:

1. Top up existing stacks of the same definition and identical policy state:
   permanent inventory slots by index, then equipped Bag slots by index.
2. Put the rest into empty compatible slots: equipped Bag specialized slots
   that accept the item's tags first, then permanent inventory, then equipped
   Bag general slots.
3. The Secure Container is never filled automatically.

If the whole quantity does not fit, the transaction rejects with
`inventory_full` and changes nothing. Weight is enforced once by the finalizer.

### Resource Nodes

A resource node is a world actor of a new third kind, `resource_node`. It is
neither an NPC, which is a social or service actor, nor a Mob, which is a combat
actor. Content, spawn authoring, runtime identity, interest replication,
interaction authority, and leases all reuse the world actor foundation
unchanged.

- **Definition.** A resource node is invulnerable, has no activity profile, no
  respawn profile reference, and no corpse, and uses a new neutral `world`
  faction. It has exactly one capability of the new kind `gathering`, whose
  `profileId` names a resource profile. NPCs and Mobs cannot have a `gathering`
  capability.
- **Resource profile.** The actor catalog gains `resourceProfiles`:

  ```json
  {
    "id": "resource.iron_vein",
    "requiredToolKind": "pickaxe",
    "yieldDefinitionId": "material.iron_ore",
    "yieldQuantity": 1,
    "cycleSeconds": 3.0,
    "charges": 5,
    "respawnSeconds": 30.0
  }
  ```

  `tools/WorldActorCompiler` validates it against the compiled item catalog: the
  yield is an active, non-quest definition without default policies, the yield
  quantity fits its stack, and at least one definition provides the tool kind.
- **Tool kind.** The item catalog gains the `toolKinds` vocabulary with
  `pickaxe`, and `tool.starter_pickaxe` lists it in its own `toolKinds`, so
  better pickaxes later need content only. Only definitions that fit the `tool`
  slot may have tool kinds, and tool kinds are not structural.
- **Content.** A new `resource.iron_vein` actor, presentation archetype, and
  spawns. Development World 1 gets one vein near the spawn. Development World 2
  gets a cluster of four on the southeast expansion pad, away from the service
  plaza. The [Development Worlds](DEVELOPMENT_WORLDS.md) layout table is updated.

### Gathering Authority

The worker owns node state and timing. GameService owns the tool check and the
item grant, because only GameService knows the equipment, and it checks it
atomically with the grant.

- **Node state.** `WorldActorRuntimeState` gains bounded resource state:
  remaining charges and a depletion generation. When the last charge is consumed
  the worker calls `SetActive(false)`. That already advances the revisions,
  closes every lease on the node, and replicates `WorldActorState` with
  `IsActive = false`, so every client shows the depleted model.
- **Respawn.** A central `WorldActorTimerQueue`, a min-heap ordered by due tick
  and drained by the tick loop, schedules the respawn. Respawn restores the
  charges and calls `SetActive(true)`. No node owns a task or timer, as
  invariant 11 of [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md#locked-design-invariants)
  requires. Mob respawn can reuse the same queue.
- **Start.** The client opens the node and sends `CapabilityAction`
  `StartGathering`. The existing authority validates lease, maintain range, line
  of sight, and capability revision. The gathering handler registers one
  activity per player and sends `GatheringState` `Started` with the cycle time.
- **Cycle.** A central gathering pass in the tick loop handles activities in
  due-tick order. For each due activity whose player has no authority operation
  in flight, it reserves one charge and calls GameService `gather_resource` with
  a random operation id that it reuses for retries of an uncertain outcome.
  - Committed: the charge is consumed, the worker applies the committed carry
    state and sends `GatheringState` `Yielded`, and the next cycle is scheduled.
    The last charge depletes the node.
  - Rejected: the reservation is released and the activity stops with the
    rejection code.
- **Stop.** An activity stops when the player sends a movement path, closes the
  interaction, opens another target, loses the lease through range, line of
  sight, or revision, disconnects, or the node depletes, or when GameService
  rejects. The worker sends `GatheringState` `Stopped` with a stable code.
- **Grant.** `GatherResourceCommand` (kind `gather_resource`) requires the
  SimulationWorker actor and its fenced live session. It takes no expected
  character revision, because gathering must not fail when the player moves an
  item meanwhile; the session fence replaces it. It checks that the character's
  `tool` slot holds an item with the required tag, or rejects
  `gathering_tool_required`. It validates the yield definition and quantity like
  Mob loot, places the yield with the carried acquisition, and audits
  `resource_gathered` with resource profile, spawn id, Shard, and worker runtime.
  The worker resolves the yield from its resource profile, as it resolves Mob
  loot seeds today.
- **Restart.** Node state is live Shard state (decision V7). A worker restart
  reconstructs every node with full charges.

### Realtime Protocol Version 17

One version bump covers the whole slice:

| Change | Detail |
| --- | --- |
| `WorldActorKind` | Add `ResourceNode = 3` |
| `WorldActorCapabilityKind` | Add `Gathering = 10` |
| `NpcLifecycleActionKind` | Rename to `CapabilityActionKind` (names are not fingerprinted) and add `VendorPurchase = 5`, `VendorSale = 6`, `StartGathering = 7` |
| `WorldActorCapability` | Add `ProfileId`, a bounded content id that is empty without a profile |
| `WorldInteractionIntent` `CapabilityAction` | Add `OfferDefinitionId`, `Quantity`, and `ExpectedPrice`; `SatisfiesRules` requires exactly the fields of each action |
| `GatheringState` (new, id 28) | Server to client, reliable: interaction session, target entity and runtime id, kind `Started`, `Yielded`, or `Stopped`, cycle milliseconds, yield definition and quantity, item-state revision, and a stop code and message |

The node's available or depleted state reuses `IsActive` in `WorldActorSpawn` and
`WorldActorState`, so it needs no new field. The protocol document, the
descriptor table, the goldens, and the expected generator output are updated in
the same change.

### Unity Client

Permanent state stays separate from the temporary uGUI, as in the existing
inventory and interaction foundation.

- **Permanent.** `InventoryClientState` holds the balance from the snapshot. A
  vendor offer model resolves the profile from the bundled catalog and prices
  with `VendorPricing`. `WorldInteractionClientController` gains purchase, sale,
  and start-gathering intents with operation correlation. A
  `GatheringClientController` tracks `GatheringState` per interaction.
- **Temporary.** A Vendor context in the upper-right area of
  `TemporaryInventoryPanel` lists offers with price, quantity, and a Buy button.
  Dropping an item from character storage on the vendor area sells it after a
  confirmation with the total. The character storage module shows the balance.
  A small progress bar shows gathering.
- **Presentation.** `ResourceNodeView` joins `NpcView` and `MobView` and switches
  between available and depleted models on `IsActive`. A placeholder Iron Vein
  prefab joins `WorldActorPresentationRegistry`. Resource node views register as
  interaction targets, and a left click walks into range, opens, and starts
  gathering.
- **Content tools.** The Item Catalog window edits `value` and vendor profiles
  with live validation. Actor Studio gains the `resource_node` template, resource
  profiles, and profile dropdowns for `vendor` and `gathering` capabilities.
  Spawn Authoring places resource nodes; patrol paths stay Mob-only.

### Monitoring

New low-cardinality instruments, pinned by `MeterInstrumentNamesTests` and shown
on the existing dashboards. Only the SimulationWorker instruments and the item
operation kinds are implemented; see [Implementation
Notes](#implementation-notes).

- `game_service.economy.currency_changed`: currency created and destroyed, by
  source kind and direction, to watch inflation.
- `game_service.items.vendor_transactions`: purchases and sales by outcome.
- `game_service.items.resources_gathered`: gathered quantity by resource
  profile.
- `simulation_worker.gathering.cycles` by outcome, and gauges for active
  gatherers and depleted nodes.

### Security And Abuse

- Prices, cycle time, charges, and yield are server-owned. The client's expected
  price only protects the player from a stale display.
- Purchase and sale are each one idempotent transaction, so an item and its
  currency always move together.
- The compiler rejects vendor content that would allow buy and sell arbitrage.
- The balance cannot go negative or overflow.
- The existing per-peer rate limit and one in-flight authority operation bound
  gathering and vendor traffic. Synthetic bot sessions still cannot mutate
  durable state.

## Product Decisions

The product owner chose the recommendation for every decision on 2026-10-18.

| Id | Decision | Recommendation | Alternatives |
| --- | --- | --- | --- |
| V1 | Which slice is implemented first | Vendor and gathering first, as one branch and one PR. The backend starts at once; the Unity vendor context waits until the inventory placeholder UI PR has merged, because it builds on those panels. Combat planning continues meanwhile, and combat takes protocol version 18. | Combat first, and this slice takes version 18. Both in parallel, where the second to merge rebases and bumps the version. |
| V2 | Currency name and denomination | One integer unit called Gold, shown as `1,250 gold`. The name is catalog content, so a rename is content only. | Coins. Gold, silver, and copper shown from one integer (1 gold = 100 silver = 10,000 copper). Splitting a unit later needs a one-time balance and price migration. |
| V3 | Starting balance | New characters start with 100 gold, set as content. That buys the starter pickaxe and some arrows. Existing characters keep their balance. | A starter kit with a pickaxe instead of gold. No starting balance, so the loop starts with the Development tools. |
| V4 | Price model | Every item has a base value. A vendor sells at 100 percent of value and buys at 40 percent, rounded down on the total. | 25 percent, like World of Warcraft, which drains more currency. 50 percent. Explicit price lists per vendor without item values. RuneScape-style prices that change with stock. |
| V5 | What Mira sells, and the item values | Mira sells Bronze Arrow (1), Minor Healing Potion (25), Cooked Trout (10), and Starter Pickaxe (50). The other values are Iron Ore 10, Training Shortbow 100, Leather Jerkin 80, Copper Ring 40, Traveler's Pack 200, and Sealed Letter 0. | Also sell the Training Shortbow and the Traveler's Pack. Other values. |
| V6 | What the vendor buys, stock, and buyback | Mira buys every item with a value from carried storage, including part of a stack. Protected, insured, quest, equipped, banked, and non-empty Bag items are refused. Stock is unlimited, prices are fixed, and there is no buyback; sold items are destroyed with an audit record. | A buyback list of the last sales. Limited stock that restocks. Sold items appear in the vendor's stock, as in RuneScape. |
| V7 | Resource node state (MVP open question) | Live Shard state in SimulationWorker. A worker restart restores every node to full. A durable opt-in can come later, as for boss corpses. | Persist respawn timestamps in PostgreSQL, with one row per node, a write per depletion, and a drain before a World rebind. |
| V8 | Node sharing, charges, and respawn | Nodes are shared. An Iron Vein gives 5 ore, then stays depleted for 30 seconds; when it depletes, everyone mining it stops. | One private node state per player. One ore per vein with a fast respawn, as in RuneScape. |
| V9 | How gathering is performed | One click mines continuously: one guaranteed ore every 3 seconds until the vein depletes, the inventory is full, the weight cap is reached, the tool is unequipped, or the player moves or closes. | One click gives one ore after 3 seconds. A success chance per cycle, which needs Mining levels. |
| V10 | Mining progression | No Mining experience or level in this slice. Profession progression becomes its own slice. | Add durable Mining experience and level now. |

Engineering decisions that follow the existing design and need no product call:

- Currency stays a character balance in the Characters module, with a ledger and
  database checks, and not an item.
- Vendor profiles and item values live in the item catalog and are priced by one
  shared rule; the client renders static offers from its bundled catalog, and
  GameService validates the expected price.
- A resource node is a third world actor kind with one `gathering` capability
  and a resource profile in the actor catalog.
- GameService checks the tool in the grant transaction; the worker owns node
  state, cycle timing, depletion, and respawn.
- One carried acquisition rule serves buying and gathering, and tops up existing
  stacks.
- Respawn uses a central timer queue, never a timer per actor.

## Implementation Plan

Every step lands with its tests and documentation. The steps are commits on one
branch from the latest `development`.

| Step | Work | Depends on |
| --- | --- | --- |
| 0 | Coordinate: rebase on the latest `development`, confirm protocol version 17 with the combat session, and wait for the inventory placeholder UI PR and PR #29 before the Unity parts that touch their files. | Decisions |
| 1 | Shared content model and compilers: item catalog format 5 with `value`, the `economy` block, vendor profiles, the `pickaxe` tool kind, and `VendorPricing`; actor catalog format 3 with `resource_node`, `gathering`, capability `profileId`, resource profiles, the `world` faction, and cross-validation against the compiled item catalog; regenerated data; compiler, pricing, and content tests. | 0 |
| 2 | Unity content tools: `value` and vendor profiles in the Item Catalog window, resource nodes and profiles in Actor Studio, resource node placement in Spawn Authoring, with EditMode tests. | 1 |
| 3 | Characters: `CreditAsync`, `CurrencyChangeSource`, `character_currency_ledger`, the balance checks, and the first Characters migration; append the missing `202610171200_item_catalog_alignment` and the new id to `FrozenHistory` and the migration table. | 0 |
| 4 | Items: the item value in the catalog mirror if a query needs it, the starting balance at character creation, `CarriedAcquisition`, the purchase, sale, and gather commands and handlers, currency in the item-state snapshot, the Development currency grant, golden traces, routing and boundary tests, and metrics. | 1, 3 |
| 5 | GameService worker boundary: `vendor_purchase`, `vendor_sale`, and `gather_resource` on the item-operations route with vendor and gathering access fields; the route snapshot and the worker client validators. | 4 |
| 6 | Realtime protocol version 17: schema, generator output, lock, `SatisfiesRules`, goldens, and the protocol document. | 1 |
| 7 | SimulationWorker: profile ids in the capability summary, the vendor handler and live vendor access, resource node state, `WorldActorTimerQueue`, the gathering handler and pass, cancellation on movement, metrics, and the startup protocol compatibility check. | 5, 6 |
| 8 | Unity runtime: protocol handling, the vendor context and balance, `ResourceNodeView` with the placeholder prefab and registry entry, the gathering flow, and EditMode and PlayMode tests. | 2, 6, 7 |
| 9 | Content placement: Mira's vendor profile reference and the Iron Vein spawns in both Worlds, plus a Development item package for gathering tests. | 1, 2 |
| 10 | Documentation, dashboards, the manual test in [Local Development](LOCAL_DEVELOPMENT.md), the full quality gate, the Unity gate, and the PR. | All |

### Tests

- Shared: format 4 and actor format 2 compilation, missing and invalid values,
  vendor and resource profile references, the arbitrage rule, pricing rounding
  and overflow, and protocol round trips and goldens for every new field and
  message.
- GameService: wallet credit, debit, limits, and ledger conservation; the
  starting balance; purchase and sale success and every rejection; partial
  stack sales; concurrent sales of the same stack; idempotent replays; carried
  acquisition order and `inventory_full`; the hard cap; gathering with and
  without the tool; currency surviving death; and the architecture guard tests.
- SimulationWorker: vendor access only while the lease holds the capability;
  gathering start, cycles, cancellation on movement, depletion stopping every
  gatherer, respawn through the timer queue, restart reconstruction, and charge
  release after a rejection.
- Integration: one full-stack flow that mines ore, sells it, and buys a potion.
- Unity: vendor context state, pricing parity with the server, the gathering
  state machine, and the depleted presentation.

### Manual Acceptance

Once implemented, with the local stack running and a Unity client joined:

1. Create a new character and see 100 gold in the inventory.
2. Click Mira, choose Trade, buy a Starter Pickaxe, and see 50 gold left.
3. Equip the pickaxe, walk to an Iron Vein, and click it. The character walks
   into range and mines one Iron Ore every 3 seconds.
4. After 5 ore the vein turns depleted for every client and mining stops. After
   30 seconds it returns.
5. Click the ground while mining and see mining stop.
6. Return to Mira and sell the ore. The balance rises by 4 gold per ore.
7. Die with gold and see the balance unchanged after respawn.

## Implementation Notes

Every step above is implemented. Where the implementation differs from the
design:

- **Formats and migrations.** The combat slice merged first, with item catalog
  format 4, actor catalog format 2, and migrations `202610181200` to
  `202610181230`. This slice therefore uses item catalog format 5, actor catalog
  and spawn format 3, and the migrations `202610181240_character_currency_ledger`
  (Characters) and `202610181250_item_economy` (Items). Characters now lists its
  migrations in `CharactersSchemaMigrations`. Protocol version 17 stayed with
  this slice; combat takes 18.
- **Tool kinds instead of a tag.** A tool requirement is a separate `toolKinds`
  vocabulary rather than an item tag, so tags stay about storage compatibility
  and a tool kind never changes a structural fingerprint.
- **Item references.** One `WorldActorItemReferences.Validate` check, shared with
  combat, validates Mob loot, vendor capability profile ids, and resource
  profiles against the compiled item catalog in the generated root.
- **Wolf pelt.** Combat added `material.wolf_pelt`, which this slice priced at 15.
- **Monitoring.** The SimulationWorker reports
  `simulation_worker.world_actors.resource_node`,
  `simulation_worker.resource_nodes.depleted`,
  `simulation_worker.gathering.active`, and `simulation_worker.gathering.cycles`
  by outcome, and the item operation metrics count `vendor_purchase`,
  `vendor_sale`, and `gather_resource` like every other operation kind. The
  playtest dashboard shows the gathering instruments in a Gathering panel of
  its World row. The GameService economy instruments are a follow-up.
- **Client advisory checks.** Opening a resource node starts gathering only when
  an item with a tool kind is equipped in the Tool slot; GameService still checks
  the required kind on every yield.
- **Development currency.** `--dev-items-list` also reports each character's
  balance, and the Inventory Item Grants window shows it beside the Give
  Currency section.

## Coordination

- **Combat planning session.** Both slices change the realtime protocol. This
  slice claims version 17, message id 28, `WorldActorKind` value 3,
  `WorldActorCapabilityKind` value 10, and action values 5 to 7; whichever slice
  merges second rebases and takes the next numbers. Combat can reuse
  `WorldActorTimerQueue` for Mob respawn, and gathering does not depend on the
  worker knowing equipment.
- **Inventory placeholder UI session.** The vendor context and the balance go
  into `TemporaryInventoryPanel` after that work merges.
- **PR #29.** The Development currency grant field goes into the Inventory Item
  Grants window after that PR merges.

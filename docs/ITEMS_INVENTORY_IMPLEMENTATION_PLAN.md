# Items And Inventory Implementation Plan

Last updated: 2026-07-17

Status: Approved delivery baseline, Phases 1 through 11 completed

## Purpose

This document defines the complete dependency-ordered implementation plan for
the design in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).

It covers the long-term backend foundation, realtime authority boundary, Unity
contracts, persistent player corpses, configurable NPC corpses, and verification
needed to prevent item duplication or loss. It does not change the canonical
runtime hierarchy and does not introduce Realms, Zones, or Layers.

## Delivery Strategy

Implementation should proceed through four milestones:

| Milestone | Outcome |
| --- | --- |
| A | Unity item-catalog authoring, durable item, slot inventory, equipment, Bag, bank, Secure Container, Recovery Storage, policy, and transaction foundation |
| B | Carry weight, encumbrance, in-world mutation authority, and Unity inventory flow |
| C | Idempotent death partition, durable player corpses, concurrent looting, Bag swaps, and one-death insurance |
| D | NPC corpse variants, client polish, load testing, documentation, and release hardening |

Each phase must leave the repository buildable and documented. PostgreSQL
integration tests accompany the phase that introduces each invariant rather than
being postponed until the end.

## Architectural Decisions

### Service Ownership

- Keep the feature inside AuthService while it owns the current global durable
  data boundary.
- Add focused feature folders rather than a generic repository framework.
- Continue using Dapper, Npgsql, explicit transactions, and the existing
  advisory-lock migration runner.
- Keep domain validation independent from HTTP so player routes, worker routes,
  quest grants, vendor transactions, and tests use the same transaction kernel.
- SimulationWorker never references Npgsql for inventory work.
- Unity never mutates durable state without server validation.

### Shared Content

- Store neutral item catalog authoring under `WorldData/Authoring/Items`.
- Compile or validate deterministic runtime catalog data under
  `WorldData/Runtime/Items`.
- Provide an Editor-only Unity authoring window over the canonical JSON without
  making Unity assets a second source of truth.
- Use the same framework-neutral compiler for Unity baking, command-line
  verification, tests, and CI.
- Give the catalog a deterministic revision.
- Seed or reconcile the PostgreSQL definition mirror after schema migration and
  before item traffic is accepted.
- Keep definition ids globally stable and independent of Shard identity.

### Transaction Kernel

All mutation entrypoints call one internal transaction service. It owns:

- Idempotency.
- Stable lock ordering.
- Authorization context.
- Expected revisions.
- Definition and policy resolution.
- Slot compatibility.
- Bag aggregate rules.
- Prospective weight and capacity.
- Audit records.
- Atomic state and derived-state revision updates.

Do not duplicate mutation SQL across HTTP endpoints, SimulationWorker service
routes, quest services, or future economy services.

## Proposed Feature Structure

The exact names can follow local conventions, but the responsibilities should
remain separated:

```text
AuthService/
  Items/
    ItemCatalogContracts.cs
    ItemCatalogService.cs
    ItemCatalogSeeder.cs
    ItemContracts.cs
    ItemEndpoints.cs
    ItemQueryService.cs
    ItemTransactionService.cs
    ItemTransactionContext.cs
    ItemTransactionErrors.cs
    ItemRules.cs
    SlotRules.cs
    BagRules.cs
    WeightRules.cs
    PolicyRules.cs
    CorpseContracts.cs
    CorpseService.cs
    RecoveryService.cs

SimulationWorker/
  Items/
    InventoryInteractionService.cs
    CorpseInteractionService.cs
    CarryStateStore.cs

WorldData/
  Authoring/Items/
  Editor/Items/
  Runtime/Items/

Tests/ShooterMmo.Backend.Tests/
  Integration/ItemPersistenceIntegrationTests.cs
  Integration/ItemConcurrencyIntegrationTests.cs
  Integration/DeathLootIntegrationTests.cs
  Unit/ItemRulesTests.cs
  Unit/WeightRulesTests.cs
```

Avoid a catch-all addition to `Shared`. Put code there only when two backend
processes genuinely compile and use the same framework-neutral contract or rule.

## Proposed Durable Data Model

Names remain proposals until the migration phase, but the responsibilities and
constraints are required.

### Catalog Tables

| Table | Responsibility |
| --- | --- |
| `item_catalog_revisions` | Applied deterministic catalog revisions |
| `item_definitions` | Stable id, display metadata, category, unit weight, max stack, active state, destruction capability |
| `item_definition_tags` | Many-to-many definition tags |
| `equipment_slots` | Canonical equipment slot ids |
| `item_definition_equipment_slots` | Definition-to-equipment compatibility |
| `item_definition_location_rules` | Secure Container and other explicit location eligibility |
| `bag_definitions` | Bag general-slot count and carry bonus |
| `bag_definition_slots` | Stable Bag slot indices and general or specialized slot kind |
| `bag_definition_slot_tags` | Accepted tags for specialized Bag slots |
| `secure_container_tiers` | Account-selected tier id, name, and character slot capacity |

Structural catalog fields become immutable while referenced. A changed layout,
weight, stack maximum, or compatibility rule that invalidates live instances
requires an explicit data migration.

### Character And Container Tables

| Table | Responsibility |
| --- | --- |
| `character_item_states` | Aggregate revision, carried weight, carry capacity, and owned top-level container ids |
| `account_secure_container_entitlements` | Account-level selected Secure Container tier and entitlement revision |
| `item_containers` | Typed container identity, owner binding, slot count, revision, and lifecycle |
| `item_container_slots` | Stable per-container slot indices and slot acceptance kind |
| `item_instances` | Durable identity, definition, quantity, revision, and exactly one current location assignment |
| `item_instance_policies` | Protected-on-death and one-death insurance lifecycle records |
| `recovery_deliveries` | System delivery source, event id, availability, expiry, and claim metadata |

`item_instances` should represent current custody through one mutually exclusive
location union:

- Container id plus slot index, or
- Equipped character id plus equipment slot.

Database CHECK constraints enforce the shape. Partial unique indexes enforce one
item per container slot and one item per equipment slot. A deferrable invariant
or equivalent schema design must ensure every extant item has one current
assignment at transaction commit.

Bag content containers bind one-to-one to their Bag item instance. Every Bag
operation locks this aggregate root before any child row.

### Operation And Audit Tables

| Table | Responsibility |
| --- | --- |
| `item_operations` | Global operation id, actor, kind, canonical request hash, status, and replayable result |
| `item_operation_changes` | Append-only before and after custody, quantity, policy, and revision audit |
| `item_destructions` | Destruction reason and source operation for removed instances |

JSON may store an immutable audit or replay payload, but never replaces
relational current-state columns.

### Corpse Tables

| Table | Responsibility |
| --- | --- |
| `corpses` | Source type, character or NPC reference, Shard, transform, created time, absolute expiry, persistence mode, revision, and closed state |
| `corpse_sections` | General inventory, equipment, and Bag section container bindings |
| `corpse_snapshots` | Non-interactive Secure Container, insured item, and protected or insured Bag presentation metadata |
| `death_events` | Idempotent death event id and committed partition result |

Normal non-persistent NPC corpses remain SimulationWorker state. Only durable
player and configured persistent NPC or boss corpses use these tables.

## Locking And Concurrency Contract

Use `READ COMMITTED` with explicit row locks and database uniqueness constraints.
Do not hold a database transaction across a Unity or worker network round trip.

Required lock order:

1. Idempotency operation row or operation key.
2. Character item-state rows sorted by character id.
3. Corpse rows sorted by corpse id.
4. Container and Bag aggregate rows sorted by id.
5. Item instance rows sorted by id.
6. Policy and delivery rows belonging to the locked items.

The same order applies to loot, Bag swap, death, expiry, bank, Secure Container,
recovery, quest cleanup, and future trade or vendor transactions.

### Optimistic Revisions

- Every mutation request includes an operation id.
- Player-owned mutations include expected character item-state revision.
- Targeted item mutations include expected item id and revision.
- Bag swaps include expected Bag aggregate revisions.
- Corpse mutations include corpse id and relevant item revision.
- Container revision is useful for refresh and snapshots, but an unrelated item
  change should not automatically reject a targeted loot request when the target
  itself remains unchanged.

### Idempotency

- Replaying the same operation id and canonical payload returns the stored
  result without another mutation.
- Reusing an operation id with a different payload returns a stable conflict.
- Death events, loot claims, NPC grants, insurance changes, quest cleanup, tier
  changes, and expiry cleanup all require idempotency keys.

## Phase 0: Documentation And Contract Freeze

Status: Completed by the planning change that introduced this document

Deliverables:

- Canonical slot inventory and Bag terminology.
- Secure Container ownership and account-tier rule.
- Weight and 140 percent encumbrance curve.
- Policy behavior for protected-on-death and insured items.
- Player and NPC corpse persistence policy.
- AuthService and SimulationWorker authority boundary.
- Updated MVP and architecture references.

Exit gate:

- No old grid or rotation rule remains active in current design documentation.
- Planned behavior is not described as implemented.

## Phase 1: Item Catalog And Pure Rules

Status: Completed 2026-07-15

### Work

- Define the neutral authoring format for item definitions, tags, slot
  compatibility, location eligibility, Bag layouts, unitless integer weight,
  stack limits, and secure tiers.
- Add representative development definitions:
  - Stackable material.
  - Stackable medical item.
  - Stackable ammunition.
  - Non-stackable weapon.
  - Armor.
  - Tool.
  - Ring.
  - Empty Bag with general and specialized slots.
  - Protected quest item.
- Produce a deterministic catalog revision.
- Implement pure rules for:
  - Stack compatibility.
  - Slot eligibility.
  - Equipment compatibility.
  - Secure Container eligibility.
  - Empty and non-empty Bag location rules.
  - Integer weight arithmetic.
  - Linear encumbrance multiplier.
- Reject malformed, duplicate, structurally impossible, or cyclic content.

### Tests

- Duplicate ids and slots fail validation.
- Negative or decimal weights and invalid stack limits fail validation.
- Specialized slots accept configured tags and reject unrelated definitions.
- Weapons are rejected from Secure Container.
- An empty Bag can use general slots and a non-empty Bag cannot.
- Encumbrance reference points exactly match 100, 105, 110, 120, 130, and 140
  percent.
- Integer comparisons reject values above 140 percent without floating-point
  rounding.
- The canonical base character capacity is `200`, with weight `280` as its exact
  140 percent hard cap.

### Exit Gate

The catalog and rules are deterministic, framework-neutral where sharing is
needed, and independent of HTTP, Unity, and PostgreSQL.

Implementation result:

- Strict neutral authoring lives in
  `WorldData/Authoring/Items/core.item-catalog.json`.
- Deterministic runtime content lives in
  `WorldData/Runtime/Items/core.item-catalog.json` with one catalog revision and
  per-definition and per-tier structural fingerprints.
- Framework-neutral contracts, compiler validation, and pure rules live under
  `WorldData/Runtime/ItemDomain` and compile for .NET through
  `Shared/DotNet/WorldData`.
- Weight uses a unitless integer scale with ammunition `1`, pistol `10`, base
  character capacity `200`, and no physical-unit fields.
- `Tools/ItemCatalogCompiler` compiles and verifies checked-in runtime content.
- Backend unit tests cover every Phase 1 test case plus malformed JSON,
  deterministic ordering, structural change detection, stack compatibility,
  equipment compatibility, integer stack weight, and Bag cycle rejection.
- No schema, migration, item instance, HTTP route, SimulationWorker item state,
  or Unity inventory behavior was introduced.

The Phase 1 exit gate is satisfied. The local and CI quality gates verify that
the runtime catalog still matches authoring.

## Phase 2: Unity Item Catalog Authoring And Bake Tool

Status: Completed 2026-07-15

This phase is intentionally placed before PostgreSQL catalog mirroring. It
turns the completed Phase 1 catalog contract into a practical content workflow
before persistent item definitions depend on it.

### Work

- Add an Editor-only assembly under `WorldData/Editor/Items` with no
  `UnityEditor` reference from runtime assemblies.
- Add `Shooter MMO > Tools > Item Catalog` as the canonical authoring window.
- Load and present `WorldData/Authoring/Items/core.item-catalog.json` through:
  - Searchable and filterable definition list.
  - Create and duplicate actions.
  - Stable id and display-name fields.
  - Category and tag selectors.
  - Unitless integer weight and stack-limit fields.
  - Equipment-slot compatibility.
  - Player-destruction and Secure Container eligibility.
  - Default policies.
  - Bag carry-capacity bonus, general slots, specialized slots, accepted tags,
    and stable slot indices.
- Add a client-only item presentation catalog under
  `shooter-mmorpg-unity-client/Assets/Resources/Items/Presentation` keyed by the
  same stable definition ids. Presentation entries may contain:
  - Icon Sprite.
  - Localization key and fallback display text.
  - Optional UI or world-prefab presentation key.
  - Other non-authoritative visual metadata.
- Present gameplay fields and client presentation fields in the same Editor
  window while saving them to their separate authoritative locations.
- Store the source gameplay catalog revision and a separate deterministic
  presentation revision in the baked presentation catalog. Unity rejects a
  mismatched gameplay pair, while icon-only changes advance only the
  presentation revision.
- Bundle the MVP presentation catalog with the Unity client. Icons remain
  optional local presentation assets and may be added as approved art becomes
  available. Do not add remote Addressables delivery in this phase, but keep
  stable presentation keys so it can be added later without changing item ids
  or server contracts.
- Keep categories, tags, equipment slots, policies, location eligibility, and
  Bag slot acceptance visually and structurally separate.
- Lock the id of every definition already present in the baked runtime catalog.
  A new definition id may be edited until its first successful bake.
- Do not expose destructive deletion for an already baked definition in the
  first version. Retirement requires an explicit inactive-definition or
  tombstone design before persistent state exists, and ids are never reused.
- Use the existing framework-neutral catalog compiler as the only validation
  and structural-fingerprint authority. Do not copy item rules into Editor UI
  code.
- Provide `Validate`, `Save`, and `Save And Bake` actions.
- Write candidate authoring and runtime output safely so a validation or bake
  failure leaves both checked-in files unchanged.
- Show all validation errors with the relevant definition or field.
- Show the previous and candidate catalog revisions and classify each changed
  definition as display-only, structural, added, or removed.
- Require explicit confirmation for structural changes and explain that they
  require migration review whenever Phase 3 persistent state already depends on
  the affected structure.
- Refresh Unity assets after a successful bake without changing runtime item
  authority.
- Keep command-line compilation and `--verify` fully supported so CI and
  backend development never require an open Unity Editor.
- Document the Editor workflow, command-line fallback, and recovery from a
  failed bake.

ScriptableObjects may own the client-only presentation mapping and direct Unity
asset references. They must not duplicate authoritative gameplay fields or
become required input for backend and CI builds. Canonical JSON remains the sole
gameplay catalog authority.

### Automated Tests

- Unity EditMode tests load the checked-in authoring catalog into the Editor
  model and round-trip it without semantic changes.
- An Editor-created item with valid category, weight, stack, equipment, policy,
  location, and Bag data passes the shared compiler.
- Duplicate ids, invalid references, decimal or negative weight, invalid stack
  limits, and malformed Bag slots block save and bake with actionable errors.
- An already baked definition id cannot be changed or reused through the
  Editor.
- Display-only edits preserve the definition structural fingerprint.
- Structural edits change the definition structural fingerprint and require
  confirmation.
- Repeated baking of identical content produces byte-identical runtime JSON and
  the same catalog revision.
- The command-line compiler verifies output produced by the Unity Editor.
- A failed validation or bake leaves authoring and runtime files unchanged.
- Runtime WorldData assemblies have no `UnityEditor` dependency.
- Every gameplay definition has exactly one client presentation entry, and
  duplicate or unknown presentation definition ids fail validation.
- The baked presentation catalog records the exact source gameplay revision and
  its own deterministic presentation revision.
- Changing an icon or other client-only presentation does not change an item
  structural fingerprint or gameplay catalog revision, but it changes the
  presentation revision.

### Manual Test Gate

1. Open `Shooter MMO > Tools > Item Catalog` in Unity.
2. Select `weapon.training_rifle`, use `Duplicate`, assign the temporary draft
   id `weapon.catalog_validation_draft`, and confirm `Validate` accepts its
   integer weight and equipment compatibility without requiring an icon.
3. Remove the unbaked draft and confirm the checked-in catalog still contains
   the original nine stable definitions.
4. Change only the training rifle display name, run `Save And Bake`, and confirm
   the structural fingerprint remains unchanged while the gameplay and
   presentation revisions remain correctly paired. Restore the intended display
   name with a second bake.
5. Change weight and confirm the tool reports a structural change, then use
   `Reload` to discard that edit without saving it.
6. Create an invalid draft with a duplicate id or invalid Bag slot and confirm
   save and bake are blocked without changing either checked-in JSON file.
7. Discard the invalid draft and confirm command-line `--verify` succeeds.

### Exit Gate

A content author can create, duplicate, edit, validate, and bake item definitions
and select their client presentation without hand-editing JSON. Unity, the
command-line compiler, tests, and CI all produce or verify the same deterministic
runtime catalog. Canonical JSON remains the sole gameplay content authority,
client presentation remains local, and no persistence work has started.

### Implementation Result

- `ShooterMmo.WorldData.Editor` provides the canonical searchable and
  category-filtered item window, full definition and Bag editing, draft id
  management, strict validation, safe save and bake actions, change
  classification, and structural-change confirmation.
- `ShooterMmo.WorldData.Client` owns deterministic presentation validation,
  exact gameplay-revision pairing, Resources-based icon lookup, and cached
  catalog access without becoming an item-rule authority.
- The checked-in client presentation catalog covers all nine gameplay
  definitions. Gameplay and presentation revisions are independent, so
  icon-only changes cannot alter gameplay fingerprints.
- The manual workflow uses an unbaked disposable draft and display-only edits
  to validate the Editor without requiring a new canonical item identity or an
  approved icon asset.
- EditMode coverage exercises Editor round-tripping, valid and invalid content,
  locked identities, deterministic baking and command-line verification,
  rollback, assembly dependency direction, presentation coverage, revision
  pairing, and client caching.
- No migration, item-instance, PostgreSQL inventory, AuthService route,
  SimulationWorker inventory state, or player inventory UI was introduced.

The Phase 2 exit gate is satisfied. Phase 3 is now also complete as documented
below.

## Phase 3: Schema, Migrations, And Character Bootstrap

Status: Completed 2026-07-15

### Work

- Add catalog, item, container, slot, policy, operation, audit, recovery, and
  character item-state tables.
- Add CHECK, foreign-key, unique, partial unique, and revision constraints.
- Add stable indexes for character snapshots, item lookup, container slots,
  policy lookup, operation replay, and Recovery Storage.
- Seed canonical equipment slots.
- Mirror the checked-in item catalog transactionally.
- Backfill every existing active character with:
  - Character item-state row.
  - Base carry capacity `200` in the neutral integer weight scale.
  - Permanent inventory container and initial slots.
  - Bank container and initial slots.
  - Secure Container and base-tier slots.
  - Recovery Storage identity.
- Change character creation so all required item state is created in the same
  PostgreSQL transaction as the character.
- Preserve the existing advisory migration lock and immutable migration ids.

### PostgreSQL Tests

- Two concurrent database initializers apply each migration once.
- Migration backfill is complete and idempotent.
- Every character receives exactly one top-level container of each required
  type.
- Duplicate equipment assignment, slot occupancy, Bag binding, or operation id
  is rejected.
- Invalid location-union rows are rejected.
- Account and character deletion behavior follows explicit foreign-key rules.
- Catalog structural changes that would invalidate live data fail startup or an
  explicit migration check.

### Exit Gate

The schema can represent all planned custody without a live HTTP endpoint and
cannot represent the common duplicate-location states.

### Implementation Result

- AuthService migration `202607151200_item_persistence_foundation` adds the
  catalog, definition relation, Bag layout, Secure Container tier, item,
  container, slot, character state, policy, recovery, operation, audit, and
  destruction tables without modifying an older migration id.
- PostgreSQL CHECK, foreign-key, deferrable unique occupancy, partial unique
  aggregate, revision, lifecycle, and exact location-union constraints reject
  duplicate slot, equipment, Bag, and operation assignments plus unassigned or
  multiply assigned items.
- Stable indexes cover catalog revision lookup, character snapshots, definition
  and container item lookup, slots, active policies, operation replay, and the
  Recovery Storage queue.
- AuthService bundles the checked-in deterministic WorldData runtime catalog,
  revalidates its revision and structural fingerprints, and mirrors every
  definition relation transactionally under a dedicated advisory lock.
- Startup rejects definition changes with live item instances and Secure
  Container tier changes with live entitlements. Display-only catalog changes
  remain safe, while identity removal requires an explicit migration.
- One data-driven PostgreSQL bootstrap function creates base carry capacity
  `200`, 20 permanent inventory slots, 40 bank slots, the entitled Secure
  Container tier and slots, and the unbounded Recovery Storage identity.
  Database startup backfills active characters idempotently, and
  `CharacterService` calls the same function inside the character transaction.
- Isolated PostgreSQL coverage verifies concurrent initialization, complete and
  idempotent backfill, catalog reconciliation, atomic character creation,
  required uniqueness failures, location and revision checks, deletion rules,
  planned custody representation, and structural compatibility fencing.
- No item HTTP endpoint, query snapshot, mutation transaction kernel,
  SimulationWorker inventory state, or Unity inventory behavior was added.

The Phase 3 exit gate is satisfied. Phase 4 is now also complete as documented
below.

## Phase 4: Read Model And Development Fixtures

Status: Completed 2026-07-15

### Work

- Implement item catalog queries and character inventory snapshots.
- Return:
  - Catalog revision.
  - Character item-state revision.
  - Permanent inventory slots.
  - Equipment slots.
  - Equipped Bag definition and contents.
  - Bank summary or full bank when authorized.
  - Secure Container tier and contents.
  - Recovery deliveries when authorized.
  - Carried weight, capacity, load ratio, sprint eligibility, and movement
    multiplier.
- Return definition ids in item-state rows instead of repeating complete item
  definitions or presentation data for every instance.
- Add test-only fixture helpers inside the test project. Do not expose a public
  grant endpoint.
- Ensure character and policy ownership is enforced in every query.

### Tests

- Another account cannot read character item state.
- A new character returns empty but complete state.
- Empty slots remain stable and ordered.
- Definition ids and policy summaries resolve through the catalog revision.
- Repeated instances of one definition do not duplicate its catalog metadata in
  the item-state payload.
- Secrets and internal operation metadata never appear in player DTOs.

### Exit Gate

The backend exposes a coherent authoritative snapshot without permitting item
mutation.

### Implementation Result

- AuthService exposes account-session-protected `GET /api/items/catalog` and
  `GET /api/characters/{characterId}/inventory` routes. No item write, grant,
  or development route was added.
- `ItemCatalogQueryService` reads the current mirrored PostgreSQL catalog with
  categories, tags, equipment slots, definitions, Bag layouts, default
  policies, location eligibility, and Secure Container tiers in one coherent
  `REPEATABLE READ` read-only transaction.
- `ItemQueryService` requires exact account and active-character ownership
  before reading any character container or policy. The same transaction
  returns permanent inventory, equipment, equipped Bag contents, full owned
  bank, Secure Container tier and contents, Recovery deliveries, revisions,
  and fixed-point encumbrance state.
- Item-state rows contain stable definition ids, quantity, revision, and active
  policy kind and status. Complete definitions, presentation metadata, policy
  source ids, recovery source event ids, operation payloads, credentials, and
  structural fingerprints are not present in the snapshot DTO.
- Load ratio and movement multiplier use deterministic basis points. Weight and
  capacity remain unitless integer values.
- Item creation helpers exist only in
  `Tests/ShooterMmo.Backend.Tests/Integration/ItemReadModelTestFixture.cs` and
  cannot be reached through AuthService HTTP.
- Isolated PostgreSQL tests verify the current catalog graph, cross-account
  denial, complete empty state, stable slot ordering, every authorized snapshot
  section, definition and policy resolution, metadata deduplication, secret
  exclusion, and read-only behavior.

The Phase 4 exit gate is satisfied. Phase 5 is now also complete as documented
below.

## Phase 5: Core Item Transaction Kernel

Status: Completed 2026-07-15

### Work

Implement internal commands for:

- Grant item into a specific or deterministic compatible slot.
- Relocate item between containers.
- Equip and unequip.
- Split and merge stacks.
- Consume quantity.
- Destroy an allowed item.
- Move an empty Bag as an ordinary item.
- Swap complete Bag aggregates between Bag slots.
- Add and claim Recovery Storage deliveries.
- Apply Secure Container tier changes.

Every command must:

- Open exactly one connection and transaction.
- Claim idempotency.
- Lock in canonical order.
- Validate authoritative current state.
- Compute prospective capacity and weight.
- Apply all mutations.
- Update derived carried state and revisions.
- Append audit entries.
- Commit or roll back the complete operation.

### PostgreSQL Race Tests

- Two moves of the same item result in one committed location.
- Two items targeting one slot result in one winner.
- Two items targeting one equipment slot result in one winner.
- Concurrent split and consume preserve total quantity.
- Repeated operation id returns one result and one audit operation.
- Reused operation id with a different request fails.
- A Bag cannot move while a child-item operation owns the Bag lock.
- A child item cannot move while a Bag swap owns the Bag lock.
- Failed swaps leave both complete Bag aggregates unchanged.
- A Secure Container tier reduction moves the correct descending slot set to
  Recovery Storage without loss.

### Exit Gate

All durable item mutations use one tested transaction kernel and adversarial
concurrency cannot duplicate or lose quantity.

### Implementation Result

- `ItemTransactionService` is the single internal AuthService mutation entry
  point. Its typed commands cover grant, container relocation, equip, unequip,
  split, merge, consume, destruction, empty Bag movement, complete Bag aggregate
  swap, Recovery Storage delivery add and claim, and account-wide Secure
  Container tier changes.
- Every command opens one Npgsql connection and one `READ COMMITTED` transaction,
  claims the global operation id before domain locks, stores a SHA-256 canonical
  request hash, and replays the stored committed or rejected result without a
  second mutation. Reusing an operation id for another canonical request returns
  `item_operation_conflict`.
- A transaction savepoint preserves the claimed operation while any rejected
  domain mutation, prospective weight calculation, or mapped occupancy conflict
  rolls back completely. Unexpected database failures roll back the operation
  claim as well and remain visible to the caller.
- Character item-state rows lock by character id, Bag aggregate roots and
  containers lock by stable id, item rows lock by stable id, and policies or
  Recovery delivery rows lock last. Child-item operations and Bag swaps use the
  same Bag root lock. Secure Container tier changes also coordinate with
  character bootstrap through one account-entitlement advisory key.
- Definition, tag, slot, equipment, Secure Container, Bag, stack, and default
  policy state resolve from the mirrored catalog. Stack splits copy effective
  policy lineage, and merges require the same definition and effective policy
  fingerprint.
- Successful operations advance touched item, container, Bag aggregate, and
  character revisions, recompute unitless carried weight and equipped Bag
  capacity, enforce the exact 140 percent hard cap, append before and after audit
  rows, and return replayable character, container, item, Recovery delivery, and
  entitlement revisions.
- Bag content containers are active only while their Bag is equipped. Empty Bags
  can move through compatible general storage, while a non-empty Bag is rejected
  from ordinary storage and can move only through an aggregate swap.
- Recovery Storage remains system-write-only. Delivery add preserves item
  identity and policy state, claim is atomic to permanent inventory or bank, and
  neither custody contributes to carried weight.
- Secure Container tier changes apply to every active character on the account.
  A reduction moves items from removed slots in descending slot order into
  `secure_capacity_reduction` Recovery deliveries before deleting any slot.
- Isolated PostgreSQL integration coverage exercises every command, account
  authorization, effective policy lineage, specialized and Secure Container
  eligibility, hard-cap rollback, canonical replay, all required contention
  races, shared Bag locks, failed aggregate swaps, and multi-character tier
  reduction without item or quantity loss.
- No account or worker mutation route, SimulationWorker inventory state,
  PostgreSQL access outside AuthService, Unity inventory state, policy lifecycle
  API, corpse table, or Phase 6 behavior was added.

The Phase 5 exit gate is satisfied. Phase 6 is now also complete as documented
below.

## Phase 6: Policies, Bank, Secure Container, And Recovery APIs

Status: Completed 2026-07-15

### Work

- Add protected-on-death and one-death insurance policy records.
- Add policy capability evaluation for trade, auction, vendor sale, player
  destruction, death disposition, and stacking.
- Implement quest-owned item cleanup for quest abandonment.
- Implement per-character bank access.
- Implement Secure Container access and account-tier changes.
- Implement global per-character Recovery Storage claims.
- Add account-authenticated read and offline-safe mutation endpoints.
- Add stable Problem Details codes.
- Mark character-specific responses `no-store`.
- Make the neutral item-catalog response conditionally cacheable by catalog
  revision and ETag. An unchanged request returns `304 Not Modified`.
- Never return icon bytes, Unity asset references, or other client presentation
  assets from AuthService.

Proposed account HTTP surface:

```text
GET  /api/item-catalog
GET  /api/characters/{characterId}/item-state
GET  /api/characters/{characterId}/bank
GET  /api/characters/{characterId}/recovery
POST /api/characters/{characterId}/item-operations/relocate
POST /api/characters/{characterId}/item-operations/split
POST /api/characters/{characterId}/item-operations/merge
POST /api/characters/{characterId}/item-operations/destroy
POST /api/characters/{characterId}/recovery/{deliveryId}/claim
```

Exact routes may be adjusted to local endpoint conventions. The transaction
service contract, authority checks, and error codes are the stable boundary.

### Tests

- Protected and insured items cannot be traded, auctioned, or vendor sold.
- Insurance removal through the insurance service restores normal capability.
- Protected quest items reject direct player destruction.
- Quest abandonment removes only items bound to that quest grant.
- Reaccepting can grant required quest items idempotently.
- Secure Container accepts eligible definitions, rejects weapons, and includes
  contents in carried weight.
- Bank and Recovery Storage remain excluded from carried weight.
- Recovery Storage rejects player deposits and permits system deliveries.
- Claims respect destination slots and the 140 percent hard cap.

### Exit Gate

The complete durable out-of-world item foundation is usable and policy safe.

### Implementation Result

- WorldData now evaluates policy capabilities independently from HTTP,
  PostgreSQL, Unity, SimulationWorker, and runtime session state. Active
  protected or insured policies block trade, auction, and vendor sale.
  Definition destroyability and protected quest lineage control direct player
  destruction, protected recovery takes death priority over insured recovery,
  and insurance is limited to non-stackable equipment-compatible definitions.
- `ItemPolicyService` applies protected-on-death and one-death insurance through
  the existing idempotent transaction kernel and explicitly removes active
  insurance without replacing the item. Policy changes advance item and
  character revisions and append relational audit state.
- A complete Bag aggregate swap locks the Bag roots, containers, child items,
  and policy rows in canonical order. Active protected or insured policy state
  on either Bag or any child rejects a cross-character swap atomically.
- `QuestItemService` grants required protected items with exact quest-grant
  source ids. A second active grant with the same lineage is a successful
  idempotent no-op. Abandon removes only protected quest items bound to that
  source id and records each destruction.
- AuthService exposes both `GET /api/item-catalog` and the existing
  `GET /api/items/catalog`. The neutral response uses its deterministic catalog
  revision as a strong ETag, is conditionally cacheable, and returns
  `304 Not Modified` when unchanged. It contains no icon bytes, Unity asset
  references, or client presentation records.
- Owned `item-state`, `inventory`, `bank`, and `recovery` reads are available to
  an account session. Bank and Recovery routes use focused repeatable-read
  queries. All character-specific responses use `Cache-Control: no-store` and
  `Pragma: no-cache`.
- Account-session routes now expose offline relocation, split, merge, allowed
  destruction, Recovery claim, and account Secure Container tier changes. The
  authenticated account is the only actor source, every request carries an
  operation id and expected revisions, and domain failures return RFC Problem
  Details with stable codes.
- Offline account actors lock both the character row and character item-state
  row before checking `character_simulation_sessions`. Simulation admission
  locks the same character row, so session acquisition and an offline item
  mutation cannot both pass. Active ownership returns
  `item_offline_access_required` with no partial mutation.
- Recovery Storage remains system-write-only. Player deposits are rejected,
  system delivery creation remains internal, and account claims preserve item
  identity and policy lineage while enforcing destination slots and the exact
  140 percent hard cap. Bank and Recovery custody remain excluded from carried
  weight, while Secure Container custody remains included.
- Phase 6 required no migration because Phase 3 already created the constrained
  policy, Recovery, entitlement, operation, and audit tables.
- Pure unit tests cover transfer, destruction, death-disposition, stacking, and
  insurance eligibility. Isolated PostgreSQL and real HTTP-host tests cover
  protected and insured Bag roots and children during aggregate swaps, policy
  removal, exact quest cleanup and reaccept, ETag `304`, no-store headers, owner
  scoping, tier access, stable Problem Details, offline fencing, Recovery deposit
  rejection, system delivery, successful claim, and hard-cap rollback.
- No SimulationWorker inventory state, worker mutation endpoint, GameProtocol
  item message, Unity inventory UI, corpse schema, death partition, Zone, Layer,
  Realm, or Phase 7 behavior was added during Phase 6.

The Phase 6 exit gate is satisfied. Phase 7 is now also complete as documented
below.

## Phase 7: Carry State And Shared Encumbrance

Status: Completed 2026-07-15

### Work

- Connect the authoritative carried weight, capacity, and monotonic character
  item-state revision maintained by the Phase 5 transaction kernel to the shared
  simulation boundary.
- Preserve base capacity `200` plus the equipped Bag bonus as the cross-process
  carry-capacity contract.
- Deliver each committed carry-state revision to the active simulation and
  client state path.
- Add encumbrance state to the shared simulation rules used by
  SimulationWorker and Unity prediction.
- Disable sprint above 100 percent.
- Apply the linear movement multiplier down to 0.20 at 140 percent.
- Reject all weight-increasing operations above the hard cap.
- Include carry state in simulation join or an immediately fenced post-join
  fetch.
- Re-run the movement revision and protocol compatibility process when shared
  simulation behavior changes.

### Tests

- Permanent inventory, equipped Bag contents, carried empty Bags, and Secure
  Container all contribute exactly once. Equipment-slot item roots contribute
  zero.
- Bank, Recovery Storage, and corpse custody contribute zero.
- Equipping a Bag removes its root weight and changes capacity atomically while
  its active contents continue to contribute.
- Swapping to a lower-capacity Bag is rejected when the resulting load exceeds
  140 percent.
- Sprint is enabled at exactly 100 percent and disabled above it.
- Server and Unity shared simulation produce identical movement multipliers.
- Reconnect restores the authoritative carry revision.

### Exit Gate

Movement and item state cannot disagree about encumbrance after join, reconnect,
or mutation.

### Implementation Result

- Simulation admission now locks the character item-state row in the same
  transaction that consumes the exact-runtime join ticket and creates the
  simulation lease. The accepted lease therefore contains one fenced
  `itemStateRevision`, `carriedWeight`, and `carryCapacity` snapshot.
- Session heartbeat reads the committed carry tuple with the updated lease.
  SimulationWorker applies only newer revisions to a session-bound
  `CarryStateStore`; a same-revision conflict invalidates the local lease rather
  than allowing movement and item state to diverge.
- GameSimulation now owns the immutable `PlayerCarryState` and deterministic
  `PlayerEncumbranceRules` used by both SimulationWorker and Unity prediction.
  Its carry wrapper delegates weight thresholds and multiplier arithmetic to the
  canonical pure WorldData rules. The shared rules retain base capacity `200`,
  add the equipped Bag bonus, allow sprint at exactly 100 percent, disable it
  above 100 percent, and apply the fixed-point linear movement multiplier down
  to `0.20` at 140 percent.
- The durable transaction kernel remains the only hard-cap authority for item
  mutations. Phase 7 cross-checks its exact integer 140 percent admission rule
  against the shared simulation rule, including lower-capacity Bag rejection.
- Realtime protocol version `7` includes the initial carry tuple in join
  acceptance and a reliable ordered carry-state update for later committed
  revisions. Movement simulation revision `movement-simulation-v3` fences the
  shared behavior change for server, tools, and Unity.
- Reconnect restores the authoritative committed tuple through admission.
  Active-session heartbeat restores any newer committed tuple before movement
  continues, and the client accepts only monotonic carry revisions.
- Backend unit, socket, and isolated PostgreSQL tests cover custody contribution,
  atomic Bag capacity changes, exact sprint and multiplier boundaries, hard-cap
  rejection, monotonic session propagation, and reconnect restoration. Unity
  EditMode tests execute the same shared encumbrance reference points and client
  revision handling.
- No item intent, item result, worker item-interaction service,
  service-authenticated mutation endpoint, Unity inventory state or UI, corpse
  behavior, Zone, Layer, or Realm was added.

The Phase 7 exit gate is satisfied.

## Phase 8: In-World Mutation Boundary

Status: Completed 2026-07-16

### Work

- Add versioned GameProtocol intents and results for inventory interaction,
  equipment, Secure Container, bank, Recovery Storage, and later corpse actions.
- Add a SimulationWorker item-interaction service.
- Add a service-authenticated AuthService mutation endpoint.
- Bind every request to exact character, simulation session, worker id, runtime
  id, and Shard.
- Validate live proximity and service access before bank, recovery, insurance
  NPC, or corpse operations.
- Permit Secure Container operations in the world without a city requirement.
- Reject direct account mutation while an active simulation session owns the
  character when the operation could change carried state.
- Update the worker carry-state store only from committed AuthService results.
- Disconnect or refresh safely if revisions diverge.

### Tests

- A wrong worker, runtime, Shard, session, account, or character is rejected.
- A stale worker cannot mutate items after assignment loss.
- Duplicate realtime intents remain idempotent.
- Account API and worker API cannot race into two committed locations.
- Bank and Recovery Storage reject requests outside validated city access.
- Secure Container mutation updates encumbrance while the character is active.
- Reconnect retrieves the same committed item and carry revision.

### Implemented Outcome

- GameProtocol version `8` adds bounded, typed, reliable ordered item-operation
  intents and committed or rejected results for relocation, equip, unequip,
  stack split, stack merge, allowed destruction, and complete Recovery Storage
  claims. Corpse mutation remains reserved for its later phase.
- SimulationWorker accepts intents only from the exact joined player session,
  serializes a bounded per-peer queue, evaluates configured bank, Recovery
  Storage, and insurance NPC service points from the authoritative player
  position, and sends no client-provided identity or access claim to authority.
- The worker calls one service-authenticated AuthService endpoint. AuthService
  validates account, character, simulation session and token, active account
  session, worker id, worker runtime id, Shard, and current assignment inside the
  same PostgreSQL transaction that invokes `ItemTransactionService`.
- Bank and Recovery Storage custody require worker-validated live access. Secure
  Container custody remains available without city access. The insurance NPC
  access capability is fenced now for the later insurance operation, but no
  insurance purchase behavior is introduced.
- Account-session mutations still require offline ownership. Character-row
  locking linearizes account admission, account mutations, and worker mutations
  so one item cannot commit to two locations through competing authorities.
- SimulationWorker changes authoritative carry state only after a committed
  AuthService result. New revisions are delivered before the next movement
  step. Same-revision disagreement, stale worker authority, and invalid session
  fencing trigger a safe refresh or disconnect instead of accepting divergent
  state.
- Unity exposes only the versioned transport send method, committed-result event,
  and monotonic carry-state application required by this boundary. Persistent
  inventory models, catalog caches, state refresh orchestration, and UI remain
  Phase 9 work.
- Backend protocol, HTTP client, worker socket, configuration, and isolated
  PostgreSQL tests cover every Phase 8 authority mismatch, stale assignment,
  duplicate intent replay, account-versus-worker race, live service access,
  Secure Container carry updates, and reconnect restoration. Unity EditMode
  tests cover the shared intent and result wire contract.

### Exit Gate

All active-character mutations have one live authority and one durable authority
without direct worker database access.

The Phase 8 exit gate is satisfied. Phase 9 is completed below.

## Phase 9: Unity Inventory Foundation

Status: Completed 2026-07-16

### Work

- Add persistent client models for catalog, item state, revisions, operation ids,
  and structured errors.
- Load the bundled gameplay catalog and client presentation catalog once into a
  scene-independent, definition-id-indexed client cache.
- Compare the local gameplay and presentation source revisions before showing
  item UI.
- Compare the local catalog revision with the authoritative server revision.
  The MVP reports a stable update-required error on mismatch instead of using
  stale item data.
- Resolve icons, localized labels, and optional presentation prefabs locally.
  Inventory snapshots and mutation results carry only definition ids and
  instance state.
- Implement temporary but complete slot UI for:
  - Permanent inventory.
  - Equipment.
  - Equipped Bag general and specialized slots.
  - Secure Container.
  - Bank.
  - Recovery Storage.
- Use the canonical inventory layout:
  - Character equipment on the left.
  - Contextual containers such as bank, corpse, Recovery Storage, or world loot
    in the upper-right area.
  - Character inventory, equipped Bag contents, and Secure Container in the
    lower-right area.
  - Keep the lower-right character inventory visible while a contextual
    container is open.
- Show carried weight, capacity, load percentage, movement multiplier, and sprint
  restriction.
- Disable invalid local targets for usability while still sending every action
  to server authority.
- Refresh targeted state after stale or concurrency errors.
- Never optimistically duplicate or destroy a local item instance.
- Do not reload the catalog or icon assets for each inventory refresh, scene
  change, slot update, or container operation.

Only the UI presentation may be temporary. Client state, networking,
idempotency, revisions, and authority handling are long-term code.

### Implemented Outcome

- `ShooterMmoClientBootstrap` now owns one scene-independent
  `InventoryClientController`. It loads the bundled gameplay catalog and the
  existing presentation catalog into definition-id indexes once, preserves the
  presentation icon cache across scene changes, and automatically restores a
  full authoritative snapshot on initial join and reconnect.
- The client has typed immutable models for complete and focused item snapshots,
  item, container, equipment, Bag, Secure Container, Bank, Recovery delivery,
  carry, revision, operation, context, and structured-error state. Focused Bank
  and Recovery reads can advance observed revisions, but mutations remain
  disabled until a coherent full snapshot reaches the newest known revision.
- The gameplay and presentation source revisions are validated before item UI
  becomes available. Every server item-state response must match the bundled
  gameplay revision. A mismatch clears renderable item state and reports the
  stable `item_catalog_update_required` error instead of using stale data.
- Item revision `0` remains valid for newly granted and split instances. The
  mapper normalizes Unity `JsonUtility` all-default placeholders only in optional
  empty item and equipped-Bag fields, while required or partially malformed item
  payloads remain rejected.
- Every UI mutation creates a new versioned realtime operation id and sends a typed
  intent through the joined `RealtimeSimulationClient`. One pending operation is
  journaled at a time. The client never changes item custody or quantity
  optimistically, ignores a duplicate completion for the last finalized
  operation, and applies state only after an authoritative HTTP refresh reaches
  the committed revision.
- Stale or concurrency rejections refresh the smallest relevant Bank, Recovery,
  or full slice first. A newer focused result forces a full coherence refresh
  before another mutation. Disconnect during an operation marks the result
  uncertain and reconnect replaces local state from authority without creating
  or deleting a local item instance.
- The temporary uGUI panel uses three explicit views. `B` toggles character
  storage only, `C` toggles equipment plus character storage, and `I` toggles the
  complete Development view with Bank and Recovery context. `Escape` closes any
  view, and shooter cursor capture is released while one is open. Equipment,
  Context, and Character Inventory are independent fixed panel roots. View
  changes toggle roots without moving or resizing another module.
- A reusable typed uGUI drag coordinator, item source, and destination target now
  drive relocation, equip, unequip, stack split and merge, atomic ordinary
  occupied-slot swap, and complete Recovery claim intents. Valid targets
  highlight green, rejected targets highlight red,
  and the target is revalidated on drop before an intent is sent. Item clicks
  only select the split and destruction action controls. Recovery items carry a
  delivery payload, so dropping any item from a delivery onto Permanent inventory
  or Bank still claims the complete delivery atomically.
- The panel displays weight, capacity, load, movement multiplier, sprint
  eligibility, and item-state revision. A shared target advisor disables
  obviously invalid equipment, specialized Bag, Secure Container, non-empty Bag,
  stack, destruction, and 140 percent hard-cap targets for usability while
  AuthService remains the final authority.
- Bank and Recovery Storage inspection uses owning-account HTTP reads everywhere.
  Any mutation still travels through the active SimulationWorker, which supplies
  its authoritative service-point access to AuthService. Local Development loads
  `DevelopmentItemInteractions:GlobalBankAndRecoveryAccess=true`, which grants
  Bank and Recovery access from any authoritative player position. The option is
  rejected outside the Development environment, never grants insurance access,
  and does not bypass the active session, worker, assignment, transaction, or
  capacity checks.
- Recovery delivery snapshots expose their existing durable revision to Unity
  as an additive HTTP field. Realtime protocol version `8` remains unchanged.
  Ordinary swap is an additive operation kind that reuses the existing two-item
  id and revision packet shape, leaving earlier operation values and framing
  compatible.
- `Shooter MMO > Tools > Inventory Item Grants` is the primary local item setup
  workflow. The Editor window lists initialized characters and canonical active
  item definitions, grants an individual stack to Permanent inventory, Bank, or
  Secure Container, and provides deterministic packages for the complete Phase
  9 fixture, equipment, stack operations, 100 and 140 percent encumbrance,
  Secure Container, and Recovery delivery testing.
- The Editor window starts machine-readable Development commands in AuthService.
  Those commands use `ItemTransactionService`, require an offline character and
  loopback non-production-like PostgreSQL, and expose no gameplay HTTP route.
  The offline guard prevents a direct Editor mutation from bypassing the active
  SimulationWorker's carry and revision state. A future online grant must use a
  service-authenticated route through the owning worker.
  Individual grants are repeatable. Packages require a character with no items
  or Recovery deliveries so their result stays deterministic. The original
  one-shot `--seed-phase9-items <characterId>` command remains compatible as a
  terminal fallback for the full Phase 9 fixture.
- The fixture starts at weight `122 / 250` because the equipped Bag root has
  zero carried weight. It includes equipment candidates,
  one non-empty equipped Bag, empty Bags in Permanent inventory and Bank,
  medical, material, and ammunition specialized-slot items, a protected Secure
  Container item, merge and split stacks, exact 140 percent weight steps, and one
  Recovery delivery.
- `InventoryContextKind` reserves typed corpse and world-loot adapters, and the
  UI keeps the upper-right layout boundary ready for them. Phase 9 does not fake
  corpse snapshots, proximity, custody, or mutations. Atomic non-empty Bag swaps
  stay on the existing durable command foundation until a later phase exposes
  the required live source and destination contract.
- Backend coverage now includes fixture and Editor-command argument guardrails,
  development list and repeatable custom-grant behavior, exact 140 percent
  package weight, authoritative hard-cap rejection, a real PostgreSQL fixture
  transaction, readback, Recovery delivery revision, and one-shot rejection.
  Unity EditMode coverage verifies the shared `Shooter MMO/Tools` menu root,
  machine-readable development responses, catalog and icon cache reuse,
  catalog mismatch, snapshot validation, monotonic and divergent revisions,
  operation correlation, specialized slots, Secure Container eligibility,
  non-empty Bag rules, split quantities, occupied-slot swap compatibility,
  equipped-weight exclusion, the hard cap, and typed drag acceptance and
  rejection. PlayMode coverage verifies the persistent controller, all three
  panel modes, and runtime uGUI lifecycle.

### Manual Test Gate

- Use `B`, `C`, and `I` to verify the three maintained panel modes.
- Drag items to move, split, merge, equip, unequip, and atomically swap compatible
  occupied ordinary slots. Swap Bags when the authoritative operation is
  available.
- Put an empty Bag in permanent inventory, another Bag, and bank.
- Verify a non-empty Bag is rejected from those locations.
- Use medical, material, and ammunition specialized slots.
- Verify weapons cannot enter Secure Container.
- Open bank, corpse, and Recovery Storage views and verify each uses the
  upper-right area while equipment and lower-right character inventory remain
  visible.
- Open and refresh multiple containers and verify each repeated definition uses
  the same cached icon and catalog entry without another catalog load.
- Force a catalog-revision mismatch and verify the client reports that an update
  is required rather than rendering stale item data.
- Cross 100 percent weight and observe sprint disable and gradual slowdown.
- Reach exactly 140 percent and verify further weight gain is rejected.
- Disconnect and reconnect without losing or duplicating state.

### Exit Gate

The first player-facing inventory loop is usable through the real authoritative
path.

The Phase 9 exit gate is satisfied for every operation exposed by the Phase 8
live authority. The corpse-view and atomic non-empty Bag-swap manual cases remain
explicit prerequisite-gated rather than locally simulated. Their stable client
context and state boundaries are prepared for the later authoritative phases.

## Phase 10: Death Partition And Durable Player Corpses

Status: Completed 2026-07-17

This phase depends on a server-authoritative death event producer. The durable
service and integration tests may be built before combat, but live activation
waits for authoritative death.

### Work

- Add idempotent death-event processing.
- Partition currency, Secure Container, protected items, insured items,
  equipment, permanent inventory, Bag, and Bag contents in one transaction.
- Consume one-death insurance only when it protects an otherwise lootable item.
- Create Recovery Storage deliveries.
- Create durable corpse and section containers.
- Create non-interactive Secure Container, insured equipment, and protected or
  insured Bag snapshots.
- Persist Shard, transform, created time, and database-timed five-minute expiry.
- Restore unexpired corpses after SimulationWorker restart, using a generic loot
  crate presentation when necessary.
- Keep empty player corpses alive until expiry.
- Add idempotent expiry cleanup that destroys remaining loot with audit entries.

### PostgreSQL Tests

- Repeated death event partitions once.
- Every pre-death item appears in exactly one post-death custody or destruction
  record.
- Secure Container contents remain in place and keep weight ownership.
- Losing an equipped Bag capacity bonus cannot block death when retained Secure
  Container weight creates an involuntary over-cap state. While that state
  remains, remediation may not increase weight or worsen the load ratio, and a
  further weight increase is rejected.
- Protected items move to Recovery Storage without visible item snapshots.
- Insured equipment moves to Recovery Storage, consumes policy, and creates a
  non-interactive corpse snapshot.
- A protected or insured Bag reaches Recovery Storage empty while normal child
  items remain in the corpse Bag section.
- Currency remains unchanged.
- Corpse expiry does not reset after service restart.
- Empty player corpses persist until the same absolute expiry.
- Loot and expiry races produce one final owner or destruction result.

### Implemented Outcome

- `ItemTransactionService` now processes one unique death event through the same
  operation journal, exact live-session authority, stable aggregate locking,
  carried-state recomputation, revision, and relational audit foundation as the
  existing item commands.
- One PostgreSQL transaction leaves currency, bank, Recovery Storage, and Secure
  Container custody untouched, routes protected and effective insured items to
  source-correlated Recovery deliveries, and moves remaining permanent,
  equipment, Bag-root, and Bag-child custody into three durable corpse sections.
- Death may remove an equipped Bag capacity bonus while retained Secure Container
  contents keep their carried weight. That involuntary transition commits even
  above 140 percent. The durable kernel then permits only non-worsening
  remediation until the character is within the hard cap again, and continues
  to reject every weight-increasing or ratio-worsening operation.
- Insurance is consumed only for an otherwise lootable item. Protected priority
  does not consume an additional active insurance policy. Every protected or
  insured Bag root reaches Recovery empty after all children are independently
  partitioned.
- `corpses`, `corpse_sections`, `corpse_snapshots`, and `death_events` persist the
  exact Shard, normalized transform, generic presentation key, revisions,
  database creation time, five-minute absolute player expiry, and non-interactive
  presentation metadata without real protected or Secure item ids.
- SimulationWorker restores only open, unexpired corpses for its exact current
  runtime and Shard assignment. Its local store advances from the returned
  database time with a monotonic clock, so local wall-clock changes cannot extend
  a restored representation.
- AuthService cleanup uses each corpse's durable expiry operation id. It locks the
  corpse before remaining loot, destroys each remaining instance once with a
  destruction and operation audit row, closes the corpse, and retains empty
  corpses until the same absolute expiry.
- The combat death producer, Unity corpse presentation, corpse-open and loot
  intents, proximity checks, viewer deltas, partial-stack loot, and Bag swap stay
  prerequisite-gated to their later phases. No Phase 11 protocol operation was
  added.

### Manual Test Gate

Run the dedicated backend gate from
[Local Development](LOCAL_DEVELOPMENT.md#phase-10-death-and-durable-corpse-verification).
Expected result: all Phase 10 unit and PostgreSQL tests pass, including
idempotency, every final custody, policy precedence, Bag-child ordering,
capacity-loss overflow remediation and rejection, restoration, empty-corpse
lifetime, expiry audit, and the custody-versus-expiry race. No manual Unity
Editor authoring or player death flow is required because the authoritative
combat death producer does not exist yet.

### Exit Gate

Player death cannot duplicate, lose, or expose protected durable items.

The Phase 10 durable service and test exit gate is satisfied. Live combat death
production remains explicitly gated on the future server-authoritative combat
producer. Phase 11 now consumes the durable corpse boundary without fabricating
that combat producer.

## Phase 11: Concurrent Corpse Container Transfers And Bag Swap

Status: Implemented on 2026-07-17

### Work

- Add corpse-open, close, item-loot, partial-stack-loot, item-deposit,
  partial-stack-deposit, and Bag-swap intents.
- Enforce one active loot interaction per player in SimulationWorker.
- Allow multiple players to view and mutate the same corpse.
- Validate proximity and corpse lifetime on every mutation.
- Use targeted item revisions so unrelated corpse changes do not reject every
  concurrent request.
- Lock Bag aggregates before child items.
- Broadcast committed corpse deltas to every current viewer.
- Close views with stable errors after expiry or invalidation.
- Treat corpse sections as bidirectional containers for carried inventory.
- Merge compatible stacks with remaining capacity and atomically swap complete
  items that cannot merge when both original slots accept the opposite item.
- Move, split, merge, and swap items between corpse slots and sections without
  changing character custody or carry state.
- Preserve canonical equipment-slot ids on corpse equipment slots and validate
  every internal or external destination against the item definition.
- Refresh character inventory to at least the committed item-state revision
  after every successful custody-changing corpse mutation.

### Race Tests

- Two players request the same non-stackable item, and one wins.
- Two players request quantities from the same stack without negative or
  duplicated quantity.
- A Bag swap racing child-item loot commits only one compatible outcome.
- Two Bag swaps cannot split either aggregate.
- A looter at 140 percent cannot claim more carried weight.
- The dead player competes under the same rules as another player.
- A player can deposit into an empty corpse slot and merge a partial stack.
- Complete items that cannot merge swap atomically in either drag direction.
- Protected, insured, Bank, Recovery Storage, and ordinary equipped sources are
  rejected without changing custody.
- A committed corpse result refreshes a previously coherent character snapshot
  when its item-state revision advanced.
- Internal corpse moves advance only corpse, container, and item revisions and
  never force a character inventory refresh.
- Corpse equipment labels and server validation use canonical equipment-slot
  ids instead of generic slot numbers.
- Viewers receive committed deltas and stale clients can refresh.
- No database transaction remains open while waiting for a client response.

### Manual Test Gate

- Use two Unity clients on the same shard.
- Open the same corpse from both clients.
- Loot different items concurrently and observe both commits.
- Request the same item simultaneously and observe one stable loser response.
- Start a child-item loot and a Bag swap together and verify the aggregate stays
  complete.
- Drag a carried item into an empty corpse slot and verify both views refresh.
- Drag complete items that cannot merge onto each other in both directions and
  verify their slots swap atomically.
- Split a compatible stack into the corpse and verify total quantity is
  conserved.
- Rearrange items between empty and occupied corpse slots, including typed
  equipment slots, and verify every viewer receives the committed result.
- Restart SimulationWorker before five minutes and verify the corpse returns at
  the recorded location with the original expiry.

### Exit Gate

Bidirectional concurrent corpse interaction is deterministic, refreshable, and
dupe safe.

### Implementation Record

- GameProtocol version `10` adds reliable corpse presence, accepted slot tags,
  open, close, refresh, full-item and partial-stack loot or deposit, atomic
  ordinary slot and Bag-swap, operation-result,
  chunked view-state, and stable view-closure messages. Chunk builders measure
  encoded UTF-8 size and keep every packet within the `1200` byte transport
  limit.
- AuthService exposes exact-session corpse open and item-operation routes.
  Read-only snapshots use short `REPEATABLE READ` transactions. Mutations reuse
  `ItemTransactionService`, lock the character and corpse before canonical Bag
  roots, containers, children, and policies, validate the absolute lifetime,
  and commit targeted item or container expectations without rejecting an
  unrelated corpse change solely because the corpse revision advanced.
- Full item moves, partial stack splits or merges, ordinary occupied-slot swaps,
  and Bag aggregate swaps preserve policy, slot, carried-weight, capacity, and
  140 percent hard-cap rules in either direction. Only Permanent Inventory,
  equipped Bag contents, and Secure Container may deposit into a corpse.
  Protected and insured custody cannot be deposited. The dead character uses
  the same transaction path as every other looter. A committed response is
  loaded only after the mutation transaction closes, so no database transaction
  spans a client wait.
- SimulationWorker keeps only bounded runtime corpse presentation and view
  state. One per-peer authority queue serializes item and corpse operations,
  one peer can view only one corpse, any number of peers can view one corpse,
  and the worker rechecks three-dimensional proximity plus cached absolute
  lifetime before every open, refresh, or mutation. AuthService repeats the
  lifetime and exact-session checks at commit authority.
- Committed snapshots update the runtime store and produce targeted deltas for
  every current viewer. Out-of-order HTTP completions cannot regress a newer
  cached corpse revision. Expired, invalidated, or missing corpses close all
  viewers with stable codes, while stale item or quantity conflicts request an
  authoritative refresh.
- Unity owns an immutable presence and corpse-view state adapter, chunk
  assembly, monotonic snapshot and delta application, operation correlation,
  and refresh-on-stale behavior. It never changes corpse or character custody
  optimistically. A replaceable capsule presentation exposes nearby corpses,
  `E` opens the closest corpse within three metres, and the temporary uGUI uses
  the permanent typed drag-and-drop foundation for bidirectional full or partial
  transfers, compatible merges, ordinary occupied-slot swaps, and occupied
  Bag-slot aggregate swaps. Successful results force a full character refresh
  to at least the committed item-state revision instead of accepting a coherent
  but older snapshot.
- `Shooter MMO > Tools > Inventory Item Grants` can create a durable corpse for
  an offline local character at a selected Shard position. Empty characters are
  first seeded through the Phase 9 fixture, then the normal system-death adapter
  creates the corpse. Restarting SimulationWorker restores it with its original
  database deadline.
- Automated coverage includes same-item and partial-stack races, unrelated
  concurrent commits, empty deposits, partial deposit merges, ordinary swaps in
  both directions, protected-item rejection, hard-cap swap rollback,
  deposit-versus-loot races, Bag versus child and Bag versus Bag races, equal
  rules for the dead player, exact HTTP authority, closed transaction checks,
  worker validation of committed deposit responses, viewer and delta state,
  committed-revision refresh state, stable close errors, protocol MTU behavior
  and slot tags, the Development corpse fixture, Unity chunk assembly, drag
  payloads, and bootstrap presentation.
- Final verification passed locked dependency restore, dependency policy,
  formatter verification, deterministic item-catalog and collision verification,
  and the complete Release build with zero warnings and zero errors.
- The complete backend suite passed `335/335`. Unity `6000.5.2f1` passed
  `80/80` EditMode tests and `2/2` PlayMode tests.

The Phase 11 implementation, automated test, documentation, and deterministic
exit gates are satisfied. The documented two-client flow remains the required
manual player-facing acceptance check. Phase 12 has not started and no insurance
NPC or quest lifecycle behavior is introduced here.

## Phase 12: Insurance And Quest Lifecycle Integration

### Work

- Add an insurance NPC service for granting and explicitly removing one-death
  insurance.
- Validate insurance price, ownership, eligible definitions, and active policy.
- Block vendor sale, trade, and auction while insured.
- Remove insurance only through the NPC transaction.
- Add quest grant ids and abandonment cleanup.
- Ensure reaccepting a quest can regrant required protected items once.
- Add UI labels for protection source, insured state, and recovery delivery
  source.

### Tests

- Insurance cannot be applied twice.
- Removing insurance restores normal transfer rules without changing the item.
- First protecting death consumes insurance exactly once.
- Replayed death cannot consume or deliver twice.
- Quest abandonment removes only the correct grant lineage.
- Protected quest items cannot use the generic destroy action.

### Exit Gate

Policy lifecycle is explicit, auditable, and independent from item category.

## Phase 13: NPC Corpse Variants

### Work

- Add NPC content settings for corpse lifetime and persistence mode.
- Default normal NPC corpses to approximately two minutes and live
  SimulationWorker ownership.
- Allow normal NPC corpses and unclaimed loot to disappear on restart.
- Generate deterministic, idempotent loot grant ids so successful player claims
  cannot duplicate after retry.
- Allow boss or selected NPC definitions to use durable corpse custody and
  restart restoration.
- Reuse player corpse transaction and expiry code for persistent NPC corpses.

### Tests

- Normal NPC corpse disappears on worker restart without persistent cleanup
  requirements.
- A retried NPC loot grant creates one persistent player item.
- Boss corpse survives restart with unchanged expiry and custody.
- Per-definition lifetime overrides apply without operational configuration
  branching.

### Exit Gate

NPC persistence is content-controlled and normal NPC volume does not force every
corpse into PostgreSQL.

## Phase 14: Performance, Operations, And Release Hardening

### Work

- Add low-cardinality metrics for item transaction latency, lock waits,
  conflicts, stale revisions, death partition, corpse count, expiry cleanup,
  recovery backlog, and policy actions.
- Never use item, character, account, operation, corpse, or session ids as metric
  labels.
- Add structured logs with correlation and operation ids while excluding secret
  credentials.
- Bound transaction timeouts and command payload sizes.
- Add cleanup jobs for expired audit retention where policy permits, closed
  corpses, and expired recovery deliveries.
- Add load scenarios for concurrent loot hotspots and repeated Bag swaps.
- Rerun SimulationWorker movement and network baselines after encumbrance and
  corpse interaction are active.
- Update all feature, architecture, setup, and manual-test documentation as each
  phase becomes implemented.

### Verification Gate

Run the standard repository checks:

```powershell
dotnet restore ShooterMmo.slnx --locked-mode
& ./Tools/Verify-DependencyPolicy.ps1
dotnet format ShooterMmo.slnx --verify-no-changes --no-restore
dotnet build ShooterMmo.slnx --configuration Release --no-restore
```

Run isolated PostgreSQL integration tests:

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:SHOOTER_MMO_TEST_POSTGRES = (Get-Content .env | Where-Object { $_ -like "SHOOTER_MMO_TEST_POSTGRES=*" }).Split("=", 2)[1]
dotnet test ShooterMmo.slnx --configuration Release
docker compose -f docker-compose.test.yml down
Remove-Item Env:SHOOTER_MMO_TEST_POSTGRES
```

Run Unity EditMode and PlayMode tests through the documented licensed workflow
after Unity-facing phases.

### Exit Gate

The system has passed unit, isolated PostgreSQL, HTTP contract, realtime,
multi-client manual, restart, expiry, and load verification with no known dupe
or loss path.

## Stable Error Contract

The exact list may grow, but planned stable codes include:

```text
item_not_found
item_not_owned
item_authority_required
item_state_conflict
item_operation_conflict
item_slot_occupied
item_slot_incompatible
item_stack_incompatible
item_stack_limit_exceeded
item_policy_restricted
item_destroy_forbidden
bag_not_empty
bag_state_changed
bag_move_in_progress
carry_weight_limit_exceeded
equipment_slot_occupied
equipment_slot_incompatible
secure_container_item_forbidden
bank_access_required
recovery_access_required
recovery_delivery_not_found
corpse_not_found
corpse_expired
corpse_invalidated
corpse_out_of_range
corpse_view_not_open
corpse_interaction_active
item_already_looted
item_quantity_changed
wrong_simulation_worker
worker_runtime_changed
simulation_session_invalid
```

Messages may be localized later. Codes and status semantics are protocol
contracts.

## Documentation Deliverables During Implementation

Update documentation in the same change whenever behavior becomes real:

- `PROJECT_ARCHITECTURE.md` for ownership, trust, and durable flows.
- `SERVICE_FEATURES.md` for implemented routes, persistence, and operations.
- `GAME_FEATURES.md` for implemented player behavior.
- `MVP_SPEC.md` when scope or product rules change.
- `LOCAL_DEVELOPMENT.md` for commands and manual test procedures.
- `UNITY_CLIENT_ARCHITECTURE.md` for client state and scene integration.
- Root and docs README files when entry points or status change.

Until a phase is delivered, its behavior remains planned and must not be listed
as implemented in feature documents.

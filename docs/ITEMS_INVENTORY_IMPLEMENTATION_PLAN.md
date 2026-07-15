# Items And Inventory Implementation Plan

Last updated: 2026-07-15

Status: Approved delivery baseline, Phases 1 and 2 completed, Phase 3 next

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
- Add `Tools > Shooter MMO > Item Catalog` as the canonical authoring window.
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
- Bundle the MVP presentation catalog and icon assets with the Unity client.
  Do not add remote Addressables delivery in this phase, but keep stable
  presentation keys so it can be added later without changing item ids or
  server contracts.
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
- Require explicit confirmation for structural changes and explain that such
  changes require migration review after Phase 3 introduces persistent item
  state.
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

1. Open `Tools > Shooter MMO > Item Catalog` in Unity.
2. Create the approved development pistol definition with stable id
   `weapon.starter_pistol`, weight `10`, its equipment compatibility, and its
   icon.
3. Run `Validate` and confirm the new item has no errors.
4. Run `Save And Bake` and confirm authoring and runtime gameplay JSON update,
   the client presentation entry is generated, and it records the source
   gameplay revision plus its own presentation revision.
5. Change only the display name, bake, and confirm the structural fingerprint
   remains unchanged. Restore the intended display name through the Editor.
6. Change weight and confirm the tool reports a structural change, then cancel
   that edit without saving it.
7. Create an invalid draft with a duplicate id or invalid Bag slot and confirm
   save and bake are blocked without changing either checked-in JSON file.
8. Discard the invalid draft and confirm command-line `--verify` succeeds.

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
- EditMode coverage exercises Editor round-tripping, valid and invalid content,
  locked identities, deterministic baking and command-line verification,
  rollback, assembly dependency direction, presentation coverage, revision
  pairing, and client caching.
- No migration, item-instance, PostgreSQL inventory, AuthService route,
  SimulationWorker inventory state, or player inventory UI was introduced.

The Phase 2 exit gate is satisfied. Phase 3 remains not started.

## Phase 3: Schema, Migrations, And Character Bootstrap

Status: Not started

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

## Phase 4: Read Model And Development Fixtures

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

## Phase 5: Core Item Transaction Kernel

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

## Phase 6: Policies, Bank, Secure Container, And Recovery APIs

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

## Phase 7: Carry State And Shared Encumbrance

### Work

- Add authoritative carried weight and capacity to character item state.
- Compute base capacity `200` plus the equipped Bag bonus.
- Return a monotonic carry-state revision after every relevant transaction.
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

- Character inventory, equipment, Bag contents, carried empty Bags, and Secure
  Container all contribute exactly once.
- Bank, Recovery Storage, and corpse custody contribute zero.
- Equipping a Bag changes both numerator and capacity atomically.
- Swapping to a lower-capacity Bag is rejected when the resulting load exceeds
  140 percent.
- Sprint is enabled at exactly 100 percent and disabled above it.
- Server and Unity shared simulation produce identical movement multipliers.
- Reconnect restores the authoritative carry revision.

### Exit Gate

Movement and item state cannot disagree about encumbrance after join, reconnect,
or mutation.

## Phase 8: In-World Mutation Boundary

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

### Exit Gate

All active-character mutations have one live authority and one durable authority
without direct worker database access.

## Phase 9: Unity Inventory Foundation

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

### Manual Test Gate

- Move, split, merge, equip, unequip, and swap Bags.
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

## Phase 10: Death Partition And Durable Player Corpses

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
- Protected items move to Recovery Storage without visible item snapshots.
- Insured equipment moves to Recovery Storage, consumes policy, and creates a
  non-interactive corpse snapshot.
- A protected or insured Bag reaches Recovery Storage empty while normal child
  items remain in the corpse Bag section.
- Currency remains unchanged.
- Corpse expiry does not reset after service restart.
- Empty player corpses persist until the same absolute expiry.
- Loot and expiry races produce one final owner or destruction result.

### Exit Gate

Player death cannot duplicate, lose, or expose protected durable items.

## Phase 11: Concurrent Corpse Looting And Bag Swap

### Work

- Add corpse-open, close, item-loot, partial-stack-loot, and Bag-swap intents.
- Enforce one active loot interaction per player in SimulationWorker.
- Allow multiple players to view and mutate the same corpse.
- Validate proximity and corpse lifetime on every mutation.
- Use targeted item revisions so unrelated corpse changes do not reject every
  concurrent request.
- Lock Bag aggregates before child items.
- Broadcast committed corpse deltas to every current viewer.
- Close views with stable errors after expiry or invalidation.

### Race Tests

- Two players request the same non-stackable item, and one wins.
- Two players request quantities from the same stack without negative or
  duplicated quantity.
- A Bag swap racing child-item loot commits only one compatible outcome.
- Two Bag swaps cannot split either aggregate.
- A looter at 140 percent cannot claim more carried weight.
- The dead player competes under the same rules as another player.
- Viewers receive committed deltas and stale clients can refresh.
- No database transaction remains open while waiting for a client response.

### Manual Test Gate

- Use two Unity clients on the same shard.
- Open the same corpse from both clients.
- Loot different items concurrently and observe both commits.
- Request the same item simultaneously and observe one stable loser response.
- Start a child-item loot and a Bag swap together and verify the aggregate stays
  complete.
- Restart SimulationWorker before five minutes and verify the corpse returns at
  the recorded location with the original expiry.

### Exit Gate

Concurrent corpse interaction is deterministic, refreshable, and dupe safe.

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
corpse_out_of_range
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

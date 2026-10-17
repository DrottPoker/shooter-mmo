# Items And Inventory Alignment Plan

Last updated: 2026-10-17

Status: Active. The audit is complete, and the product owner accepted every
recommendation on 2026-10-17. This document is a work plan, not a description
of implemented behavior. Each finding moves to `Fixed` only together with a
regression test or a documented manual verification.

## Purpose

Items and inventory Phases 1 through 15 were built while the project was a
modern third-person shooter. [Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md)
has since replaced that direction with a RuneScape-like high fantasy MMO with
click-to-move, the Melee, Ranged, and Magic combat triangle, Heavy, Light, and
Cloth armor, ammunition families, potions and food, and thirteen professions.

This plan records an audit of the complete item and inventory system against
that design and against the repository rules in `AGENTS.md`: the item catalog
and its tools, GameService.Items with persistence, the transaction kernel,
policies, death loot, corpses, insurance, and Recovery Storage, the
SimulationWorker carry state and corpse interaction, the Unity inventory client
and panels, and the item design documents. It lists the decisions that need the
product owner and the order in which the findings are fixed.

## Baseline Verification

Run on 2026-10-17 on `development` at `5bd5b7b`.

| Check | Result |
| --- | --- |
| Locked restore and dependency policy | Passed |
| `dotnet format --verify-no-changes` | Passed |
| Release build | Passed, 0 warnings, 0 errors |
| Backend tests with PostgreSQL and Redis integration | 958 of 958 passed, none skipped |
| Item catalog, collision, actor, and protocol verification | Passed |
| Observability verification | Passed |

## Summary

The durable foundation fits the fantasy design and stays: one custody
assignment per item, the transaction kernel with idempotency, stable lock order
and audit, Bags, the Secure Container, carry weight and the 140 percent
encumbrance curve, the death partition, durable corpses, insurance, Recovery
Storage, and the exact-session worker boundary. The encumbrance curve is
implemented exactly as designed, and no sprint, jump, or aim remnant affects
movement or carry state.

What does not fit falls into five groups:

1. **Content and vocabulary from the shooter.** The development catalog ships a
   Training Rifle, Training 5.56 Ammunition, a Field Dressing, a Signal
   Transponder, and a Field Pack. The `medical` category and tag are required by
   code, potions and food do not exist, and the equipment slots are a shooter
   loadout without the decided ammunition slot.
2. **Rules the design needs that the code cannot express.** Equipped items must
   have quantity one, so the 1000-unit ammunition slot is impossible. One
   ammunition unit weighs as much as the whole scale allows: 200 arrows fill the
   base carry capacity of 200.
3. **Content can never be retired.** Removing or renaming any catalog id stops
   GameService at startup, and the unused retired state deadlocks. New equipment
   slots get a generated sort order that can collide and that also sizes every
   corpse equipment container.
4. **Code quality.** One confirmed Unity crash path, permanent corpse rules in
   temporary UI, phase-named tools and classes, development code mixed into
   production module roots, content ids and balance values copied into code and
   tests, and duplicated rules across the client, the worker, and GameService.
5. **Documentation.** Shooter examples, stale and contradictory statements, and
   the same Phase 1 to 15 story repeated in nine documents.

## Product Decisions

The product owner chose the recommendation for every decision on 2026-10-17.

| Id | Decision | Recommendation | Alternatives | Status |
| --- | --- | --- | --- | --- |
| D1 | Development item content | Replace the shooter items: `weapon.training_shortbow` Training Shortbow, `ammunition.bronze_arrow` Bronze Arrow, `potion.minor_healing` Minor Healing Potion, a new `food.cooked_trout` Cooked Trout, `quest_item.sealed_letter` Sealed Letter, `bag.travelers_pack` Traveler's Pack, `armor.leather_jerkin` Leather Jerkin, and `jewelry.copper_ring` Copper Ring. Keep `tool.starter_pickaxe` and `material.iron_ore`. Show the base Secure Container tier to players as Warded Pouch while the domain term stays Secure Container. | Other names; keep Secure Container as the player-visible name. | Decided 2026-10-17 |
| D2 | Item vocabulary | Categories `weapon`, `ammunition`, `armor`, `jewelry`, `tool`, `bag`, `material`, `potion`, `food`, and `quest_item`; `medical` is removed. Bag slot tags `potion`, `food`, `material`, and `ammunition`. A supplies slot accepts both `potion` and `food`. Code requires only the ids it gives meaning to; everything else is content. | One `consumable` category with `potion` and `food` tags. | Decided 2026-10-17 |
| D3 | Equipment slots | RuneScape-like set: `head`, `neck`, `body`, `legs`, `hands`, `feet`, `main_hand`, `off_hand`, `ammunition`, `ring_1`, `ring_2`, `tool`, and `bag`. A definition may block other slots while equipped, so a two-handed bow or staff keeps `off_hand` empty. The proposed two-loadout swap becomes a later feature of its own and does not need a second weapon slot. | Keep today's eight slots and only add `ammunition`; decide the rest together with combat. | Decided 2026-10-17 |
| D4 | Ammunition slot | Implement it now as decided: one stack of at most 1000 units, set as content data on the slot, weightless while equipped, with top-up merges and partial unequips. It follows the normal death rules. Corpse loot keeps going into carried storage only. | Add only the slot id now and the stack behavior with combat. | Decided 2026-10-17 |
| D5 | Weight scale | Multiply the scale by ten: one ammunition unit stays `1`, a one-handed weapon weighs about `100`, and base carry capacity becomes `2000`, so 1000 spare arrows use half of it. Base capacity and the inventory and bank slot counts move from a migration seed and a C# constant into catalog content. | Keep `200`; another factor. | Decided 2026-10-17 |
| D6 | Retiring the shooter ids | Add retired-id tombstones to the catalog so a retired id can never be reused, and let GameService mark them retired. The shooter items are retired, not converted. Local development databases are reset once, which deletes local accounts and characters. | Write a one-time conversion that maps every old development item to its replacement. | Decided 2026-10-17 |
| D7 | Combat item data | Defer to the combat phase: base damage, damage type, combat style and weapon family, ammunition family and bonus, armor type and stats, resistances, healing multipliers, level requirements, the equipment-change attack lockout, consume and use intents, and per-shot ammunition consumption. Record them in the design as planned, with worker-batched consumption as the proposed approach, because one PostgreSQL transaction per shot is too heavy. | Add the item fields now without consumers. | Decided 2026-10-17 |

Engineering decisions that follow the existing design and need no product call:

- Bank and Recovery Storage access moves to the NPC interaction capability, as
  [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md) and the inventory
  design already require. The radius service points and the never-used
  `insurance_npc` service point are removed. Development keeps its explicit
  global Bank and Recovery option.
- Tests use a test-owned fixture catalog, so content and balance changes no
  longer break rule tests. A small set of content tests keeps checking the real
  catalog.

## Findings

Priorities follow [Foundation Hardening Plan](FOUNDATION_HARDENING_PLAN.md):
**P1** is a realistic correctness problem, a crash, or tooling that blocks
content work; **P2** is a design mismatch, low-impact defect, or quality and
documentation drift. No P0 finding was found.

### P1 Findings

| Id | Area | Finding | Location | Status |
| --- | --- | --- | --- | --- |
| A01 | Unity inventory | `BeginRefresh` stores the coroutine only after `StartCoroutine` returns. The routine's first step raises `Changed`, the NPC panel calls `EnsureFullState` while the snapshot is missing, and a second refresh starts, without end. The client dies with a stack overflow when an NPC insurance or quest panel shows before the first snapshot. | `InventoryClientController.cs` `BeginRefresh`, `TemporaryWorldInteractionPanel.cs` `DrawInsuranceCapability`, `DrawQuestCapability` | Fixed |
| A02 | Catalog retirement | Any removed category, tag, equipment slot, definition, or tier id stops GameService at startup, also without instances. A retired row must neither be in the catalog nor be missing from it, so retirement deadlocks, and no code ever retires a row. Nothing in content prevents a removed id from being reused. | `ItemCatalogSeeder.cs` `EnsureNoIdentityRemoval`, retired-id check | Fixed |
| A03 | Equipment slots | New slots get sort order `1000 + index`, where the index shifts when slots are added, so later slots can collide on the unique sort order. The sort order also sizes and indexes every corpse equipment container, so a slot with sort order 1000 creates 1001-slot containers. | `ItemCatalogSeeder.cs`, `PlayerCorpseLayoutWriter.cs`, `ItemSlotPlacement.cs` | Fixed |
| A04 | Unity corpses | The nearby-corpse `Open` buttons open a corpse directly, which skips click-to-approach and the one-interaction guard. | `TemporaryInventoryPanel.cs` corpse list | Fixed |
| A05 | Unity corpses | A corpse opened while the `B` or `C` view is active stays invisible because only the Development view shows the context module, while its interaction stays open. | `TemporaryInventoryPanel.cs` `OnCorpseStateChanged` | Fixed |
| A06 | Development fixtures | The full fixture pins nine ids with their exact weight and stack size and refuses to run after any balance change. The exact 100 and 140 percent packages hard-code quantities tuned to today's weights and only check stack sizes, so a weight change silently makes them wrong. | `PhaseNineDevelopmentFixtureSeeder.cs`, `DevelopmentItemTool.cs` packages | Fixed |
| A07 | Tests | About 230 server and 40 client test references use the shipped content ids and balance values, so every content change breaks rule tests. | `Mmorpg.GameService.Items.Tests`, `Mmorpg.Shared.Tests`, Unity EditMode inventory tests | Fixed |
| A08 | Worker validation | The worker accepts a player death only with a lifetime of exactly five minutes and a corpse only with the presentation key `corpse.generic_loot_crate`, which duplicates GameService rules. A live Mob corpse invents a `head` slot so an empty equipment section passes the protocol rule. | `GameServiceClient.cs` death and corpse validators, `LiveMobCorpseStore.cs` | Fixed |

### P2 Findings: Design Alignment

| Id | Area | Finding | Location | Status |
| --- | --- | --- | --- | --- |
| B01 | Content | Shooter definitions and names: Training Rifle, Training 5.56 Ammunition with stack 60, Field Dressing, Signal Transponder, Field Pack, Starter Vest, and a ring in category `armor`. | `content/items`, presentation catalog | Fixed |
| B02 | Vocabulary | The compiler requires all eight categories including `medical`, all three tags, and all eight slots, and the base Secure Container tier must have exactly four slots. Potions and food do not exist. | `ItemCatalogCompiler.cs`, `ItemCatalogContracts.cs` | Fixed |
| B03 | Ammunition slot | No ammunition slot exists, and equipped stacks are blocked by the compiler, the equip handler, and the client. Split and merge require containers. | `ItemCatalogCompiler.cs`, `ItemEquipmentCommandHandler.cs`, `ItemStackCommandHandler.cs`, `InventoryTargetAdvisor.cs` | Fixed |
| B04 | Weight scale | One ammunition unit weighs `1` and base capacity is `200`, so the scale cannot express light arrows. The pistol anchor belongs to the shooter. | content, design documents | Fixed |
| B05 | Character settings | Base capacity and the inventory and bank slot counts live in a migration seed, a C# constant, and repeated `character_default` lookups, not in content. | `ItemPersistenceFoundationMigration.cs`, `ItemRules.cs` `CarryWeightDefaults` | Fixed |
| B06 | Equipment slots | `body_armor`, `primary_weapon`, and `secondary_weapon` are a shooter loadout; armor types need more armor slots, and two-handed weapons need an off-hand rule. | catalog, migration seed, `ItemEquipmentSlotIds` | Fixed |
| B07 | Quest and insurance data | One global development quest item and one flat insurance price live in service configuration with code defaults. Quest protection is keyed on the category id in SQL and C#. | `NpcItemLifecycleOptions.cs`, `appsettings.json`, `QuestItemCommandHandler.cs` | Fixed |
| B08 | Service access | Bank and Recovery use radius service points while the NPC capabilities for them answer as deferred. Insurance uses the NPC capability, so the `insurance_npc` service point is dead configuration. | `ItemInteractionAccessService.cs`, `ItemInteractionConfig.cs`, `DeferredWorldActorCapabilityHandler` | Fixed |
| B09 | Unity flow | Panel texts tell the player to press `E`, which no longer exists. Escape is read directly by the panel and through the Cancel action, so one press sends two corpse closes. The interaction prompt sits at the old crosshair position. | `TemporaryInventoryPanel.cs`, `TemporaryWorldInteractionPanel.cs` | Fixed |
| B10 | Wording | The protocol documents carry weight in grams, the corpse presentation key says loot crate, and problem URIs use `shooter-mmo.local`. | `realtime-protocol.json`, `CorpseTransformRules.cs`, stack stress tools | Fixed |
| B11 | Consumption | No consume or use intent exists, and the kernel's consume command is a full durable transaction. Per-shot ammunition and potion use need a batched design. See D7. | protocol schema, `ItemStackCommandHandler.cs` | Open |

### P2 Findings: Code Quality

| Id | Area | Finding | Location | Status |
| --- | --- | --- | --- | --- |
| C01 | Naming | Phase-named classes, commands, packages, source kinds, and messages: `PhaseNineDevelopmentFixtureSeeder`, `--seed-phase9-items`, `phase9_full`, `development_phase9_fixture`, "Create Phase 11 Corpse", and "Phase 12" and "Phase 13" texts in the worker and actor compiler. | GameService.Items, `InventoryItemGrantWindow.cs`, worker actor handlers, `WorldActorCompiler.cs` | Fixed |
| C02 | Module structure | Development tools, fixtures, and stress fixtures sit in the Items module root and are always registered. The development CLI is one 837-line class. Two services are registered for production but used only by tests. | `ItemsModule.cs`, `DevelopmentItemTool.cs`, `QuestItemService`, `ItemPolicyService` | Fixed |
| C03 | Duplication, server | Three development database guards with different rules, three copies of owned-item SQL, carried weight in SQL and in C#, a copy of the persisted id derivation, and about eleven identifier validators. | GameService.Items | Fixed |
| C04 | Duplication, worker | Committed carry state is applied in five places, and the error-code lists that decide between disconnect and close differ between item and corpse paths. | SimulationWorker Items, Corpses, WorldActors, Sessions | Fixed |
| C05 | Unity rules in UI | About 450 lines of permanent corpse target rules live in the temporary panel instead of a tested advisor. | `TemporaryInventoryPanel.cs` | Fixed |
| C06 | Unity literals | Container types, corpse section kinds, the `bag` slot used for Bag swaps, and slot kinds are string literals; `IsCarriedContainer` exists three times, and an unknown container falls back to Secure Container rules. Destroy is offered for protected quest items. | `InventoryTargetAdvisor.cs`, `CorpseClientController.cs`, `TemporaryInventoryPanel.cs` | Fixed |
| C07 | Unity structure | No controller tests, dead code in the operation journal, a refresh scope that treats every external location as Bank, and a 1405-line models file. | `InventoryClientController.cs`, `InventoryClientModels.cs` | Fixed |
| C08 | Catalog shape | The definition shape is mirrored by hand in the contracts, the compiler, the GameService loader, the Unity client DTO, the editor model, and the database, so every new field needs six edits. Equipment slots share one identity type with categories and tags. | WorldData, GameService.Items, Unity | Fixed |
| C09 | Small items | Dead alias routes, the player corpse lifetime in a CHECK constraint and code, implicit encumbrance literals, a pass-through encumbrance wrapper, and repeated limits. | various | Fixed |

### P2 Findings: Documentation

| Id | Area | Finding | Status |
| --- | --- | --- | --- |
| E01 | Shooter wording | `medical`, the pistol and rifle weight anchor, weapon attachments, penetration, the Field Pack, and shooter pointer capture in manual test steps. | Open |
| E02 | Stale statements | The MVP lists click-to-move as future work and has an outdated implementation note; old protocol versions are presented as current; status headers contradict their content; insurance and quest grants are described as missing. | Open |
| E03 | Contradictions | Offline Recovery claims, Recovery withdrawal granularity, Bank and Recovery access, and the lock order differ between documents. | Open |
| E04 | Structure | The Phase 1 to 15 story is repeated in nine documents, the MVP repeats the inventory rules, Local Development organizes item tests by phase, and the implementation plan mixes 1850 lines of history with the only table map and error contract. | Open |

## Fix Order

The work lands on one branch and pull request, one commit per step.

1. **Defects.** A01, A04, A05, A08, B09, and the Destroy offer in C06.
2. **Catalog foundation.** Retirement tombstones (A02), authored immutable
   equipment slot order (A03), vocabulary that code requires only where it
   gives meaning (B02), character settings in content (B05), equipment slots
   with their own data, and a test-owned fixture catalog (A07).
3. **Fantasy content.** D1, D2, D3, and D5 content, the weight scale, and the
   development fixtures rebuilt from the catalog with neutral names (A06, C01).
4. **Equipment stacks and slot rules.** D4 and the slot blocking rule from D3
   through shared rules, compiler, kernel, protocol, worker, and client.
5. **Service access.** B08, then B07 without code defaults and with quest
   protection as definition data.
6. **Quality.** C02 to C09 and B10.
7. **Documentation.** E01 to E04, with feature-based manual test steps.
8. **Verification.** The backend gate, Unity EditMode and PlayMode tests, and
   an end-to-end manual inventory, corpse, and NPC loop.

## Out Of Scope

- Combat item data, consumption, and the equipment-change lockout (D7).
- Quest definitions and a vendor, which belong to their own feature phases.
- Live Mob corpse partial-stack loot and the corpse packet builder's chunk
  sizing, which wait for a combat loot producer.
